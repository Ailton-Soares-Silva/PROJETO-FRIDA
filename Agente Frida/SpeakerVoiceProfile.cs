// language: C# 5 (.NET Framework 4.8), file: SpeakerVoiceProfile.cs, target: Windows 10/11 x64
// Perfil "so a minha voz" no padrao de mercado (Voice Match / Voice ID):
//   1) Silero VAD separa fala humana de ruido do ambiente;
//   2) WeSpeaker ResNet34-LM gera o embedding neural do locutor;
//   3) similaridade de cosseno contra o centroide de 5 frases de cadastro, com limite calibrado
//      pela consistencia do proprio cadastro (leave-one-out).
// Trechos de outras vozes (TV, pessoas proximas) sao removidos antes da transcricao.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public class VoiceProfileData
    {
        public int version = 0;
        public string model = "";
        public string microphone = "";
        public string created_at = "";
        public List<double[]> embeddings = new List<double[]>();
        public double threshold = 0.5;
        public double loo_mean, loo_min;
    }

    public class SpeakerCheckResult
    {
        public bool Accepted;
        public bool Inconclusive;      // fala curta demais para identificar (< 0,5 s)
        public double Score;           // similaridade de cosseno (-1..1); maior = mais parecido
        public double Threshold;
        public double SpeechSeconds;
        public int RemovedSegments;    // trechos de outra voz retirados antes da transcricao
        public byte[] FilteredWav;     // audio para transcrever (apenas a sua voz quando houve mistura)
        public string Reason = "";
    }

    public sealed class EnrollmentSample
    {
        public double[] Embedding;
        public double SpeechSeconds;
    }

    public sealed class EnrollmentSummary
    {
        public double Threshold, LooMean, LooMin;
        public int Count;
        public int WorstIndex = -1;    // frase que destoou das demais (para regravar)
    }

    public class SpeakerVoiceProfile
    {
        public const int ProfileVersion = 4;
        public const int RequiredPhrases = 5;
        private const int SampleRate = 16000;
        public const double MinVerifySeconds = 0.5;     // menor trecho que o modelo identifica
        public const double MinEnrollSeconds = 1.5;     // fala minima por frase de cadastro
        private const double BaseThreshold = 0.50;      // ponto de operacao padrao (cosseno) para ResNet34-LM
        private const double MinThreshold = 0.40;
        private const double MinConsistency = 0.55;     // media leave-one-out minima do cadastro
        private const double OutlierConsistency = 0.45;

        private readonly string _path;
        private readonly object _sync = new object();
        private VoiceProfileData _data = new VoiceProfileData();
        private double[] _centroid;
        private static readonly object AnalyzerSync = new object();
        private static SileroVoiceActivity _analyzer;
        private static string _analyzerError;

        public SpeakerVoiceProfile(string path)
        {
            _path = path;
            Load();
        }

        public static NeuralSpeakerEncoder Encoder { get { return NeuralSpeakerEncoder.Shared; } }
        public static bool EngineAvailable { get { var e = Encoder; return e != null && e.Ready && Analyzer != null; } }
        public static string EngineError
        {
            get
            {
                if (Encoder == null) return "modelo de locutor nao carregou (" + (SherpaNative.LoadError ?? "ver log") + ")";
                if (Analyzer == null) return "detector de fala nao carregou (" + (_analyzerError ?? "ver log") + ")";
                return "";
            }
        }

        private static SileroVoiceActivity Analyzer
        {
            get
            {
                lock (AnalyzerSync)
                {
                    if (_analyzer == null && _analyzerError == null)
                    {
                        try { _analyzer = new SileroVoiceActivity(); }
                        catch (Exception ex) { _analyzerError = ex.Message; BootLogger.Log("Silero VAD indisponivel: " + ex.Message); }
                    }
                    return _analyzer;
                }
            }
        }

        public bool IsReady
        {
            get { lock (_sync) return _data.version == ProfileVersion && _centroid != null && EngineAvailable; }
        }
        public bool HasRefinedProfile { get { return IsReady; } }
        public bool HasLegacyProfile { get { lock (_sync) return File.Exists(_path) && _data.version != ProfileVersion; } }
        public int EnrollmentCount { get { lock (_sync) return _data.embeddings == null ? 0 : _data.embeddings.Count; } }
        public double Threshold { get { lock (_sync) return _data.threshold; } }
        public string Microphone { get { lock (_sync) return _data.microphone ?? ""; } }
        public string CreatedAt { get { lock (_sync) return _data.created_at ?? ""; } }
        public double Consistency { get { lock (_sync) return _data.loo_mean; } }

        /// <summary>O embedding neural tolera troca de microfone; mantido para informar o usuario.</summary>
        public bool MatchesMicrophone(string name)
        {
            lock (_sync) return IsReady && string.Equals((_data.microphone ?? "").Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidVector(double[] v, int dim)
        {
            return v != null && v.Length == dim && v.All(x => !double.IsNaN(x) && !double.IsInfinity(x)) && v.Sum(x => x * x) > 0.5;
        }

        private void Load()
        {
            lock (_sync)
            {
                _centroid = null;
                try
                {
                    if (!File.Exists(_path)) return;
                    var d = new JavaScriptSerializer().Deserialize<VoiceProfileData>(File.ReadAllText(_path, Encoding.UTF8));
                    if (d == null) return;
                    _data = d;
                    if (d.version != ProfileVersion || d.model != NeuralSpeakerEncoder.ModelFile || d.embeddings == null) return; // perfil antigo: exige novo cadastro
                    int dim = d.embeddings.Count > 0 && d.embeddings[0] != null ? d.embeddings[0].Length : 0;
                    if (dim < 64 || d.embeddings.Count < 3 || !d.embeddings.All(v => ValidVector(v, dim))) return;
                    if (double.IsNaN(d.threshold) || d.threshold < MinThreshold || d.threshold > 0.8) d.threshold = BaseThreshold;
                    _centroid = NeuralSpeakerEncoder.Mean(d.embeddings);
                }
                catch (Exception ex) { BootLogger.Log("Perfil de voz ilegivel: " + ex.GetType().Name); }
            }
        }

        private void SaveLocked(VoiceProfileData data)
        {
            string temp = _path + ".tmp";
            try
            {
                string folder = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".previous");
                else File.Move(temp, _path);
            }
            catch (Exception ex) { throw new IOException("Nao foi possivel salvar o perfil de voz. " + ex.Message, ex); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _data = new VoiceProfileData(); _centroid = null;
                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    if (File.Exists(_path + ".previous")) File.Delete(_path + ".previous");
                }
                catch (Exception ex) { throw new IOException("Nao foi possivel apagar o perfil. " + ex.Message, ex); }
            }
        }

        // ------------------------------------------------------------------
        // Cadastro (5 frases curtas, estilo Voice Match)
        // ------------------------------------------------------------------

        /// <summary>Analisa uma frase de cadastro. Lanca InvalidDataException com orientacao clara se nao servir.</summary>
        public EnrollmentSample CreateEnrollmentSample(byte[] pcmOrWav)
        {
            if (!EngineAvailable) throw new InvalidOperationException("Verificador de voz indisponivel: " + EngineError);
            int offset, length;
            if (!GetPcmRange(pcmOrWav, out offset, out length)) throw new InvalidDataException("Formato de audio invalido.");
            double clipping = ClippingFraction(pcmOrWav, offset, length);
            if (clipping > 0.01) throw new InvalidDataException("O microfone saturou (voz alta ou perto demais). Afaste-se um pouco e repita esta frase.");
            var segments = Analyzer.ExtractSegments(pcmOrWav, offset, length);
            var speech = Concat(segments);
            double seconds = speech.Length / (double)SampleRate;
            if (seconds < MinEnrollSeconds)
                throw new InvalidDataException(seconds < 0.2 ? "Nenhuma fala detectada. Leia a frase em voz normal, perto do microfone." : "Fala curta demais (" + seconds.ToString("F1") + " s). Leia a frase inteira, sem pausas longas.");
            var embedding = Encoder.Embed(speech);
            if (embedding == null) throw new InvalidDataException("Nao foi possivel extrair sua assinatura de voz desta frase. Repita.");
            return new EnrollmentSample { Embedding = embedding, SpeechSeconds = seconds };
        }

        /// <summary>Confere consistencia das frases (leave-one-out) e calcula o limite. Nao salva.</summary>
        public static EnrollmentSummary Summarize(IList<double[]> embeddings)
        {
            var s = new EnrollmentSummary { Count = embeddings == null ? 0 : embeddings.Count };
            if (s.Count < 3) return s;
            var loo = new double[s.Count];
            for (int i = 0; i < s.Count; i++)
            {
                var others = new List<double[]>();
                for (int j = 0; j < s.Count; j++) if (j != i) others.Add(embeddings[j]);
                loo[i] = NeuralSpeakerEncoder.Cosine(embeddings[i], NeuralSpeakerEncoder.Mean(others));
            }
            s.LooMean = loo.Average(); s.LooMin = loo.Min();
            int worst = Array.IndexOf(loo, s.LooMin);
            if (s.LooMin < OutlierConsistency || s.LooMin < s.LooMean - 0.15) s.WorstIndex = worst;
            // Sua voz, em condicoes reais, fica bem acima do limite; vozes de outras pessoas ficam abaixo de ~0,35.
            s.Threshold = Math.Max(MinThreshold, Math.Min(BaseThreshold, s.LooMean - 0.22));
            return s;
        }

        public EnrollmentSummary SaveEnrollment(IList<double[]> embeddings, string microphone)
        {
            if (embeddings == null || embeddings.Count < RequiredPhrases) throw new InvalidDataException("Grave as " + RequiredPhrases + " frases antes de salvar.");
            var s = Summarize(embeddings);
            if (s.LooMean < MinConsistency)
                throw new InvalidDataException("As frases ficaram inconsistentes entre si (consistencia " + s.LooMean.ToString("F2") + "). Grave sozinho, sem TV/conversa ao fundo, e repita.");
            if (s.WorstIndex >= 0)
                throw new InvalidDataException("A frase " + (s.WorstIndex + 1) + " destoou das demais. Regrave somente ela.");
            var data = new VoiceProfileData
            {
                version = ProfileVersion, model = NeuralSpeakerEncoder.ModelFile, microphone = microphone ?? "",
                created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), embeddings = embeddings.Select(x => (double[])x.Clone()).ToList(),
                threshold = s.Threshold, loo_mean = s.LooMean, loo_min = s.LooMin
            };
            lock (_sync)
            {
                SaveLocked(data);
                _data = data; _centroid = NeuralSpeakerEncoder.Mean(data.embeddings);
            }
            BootLogger.Log("Perfil de voz neural salvo: frases=" + s.Count + " consistencia=" + s.LooMean.ToString("F3") + " limite=" + s.Threshold.ToString("F3"));
            return s;
        }

        // ------------------------------------------------------------------
        // Verificacao em uso
        // ------------------------------------------------------------------

        /// <summary>Confere se o audio (WAV 16 kHz mono ou PCM) e a voz cadastrada.
        /// tolerance 0,75..1 (menor = mais rigoroso), como em config.json.</summary>
        public SpeakerCheckResult Check(byte[] audio, double tolerance)
        {
            var r = new SpeakerCheckResult();
            double[] centroid; double threshold;
            lock (_sync) { centroid = _centroid; threshold = _data.threshold; }
            if (!IsReady || centroid == null) { r.Reason = EngineAvailable ? "voz ainda nao cadastrada" : "verificador indisponivel: " + EngineError; return r; }
            int offset, length;
            if (!GetPcmRange(audio, out offset, out length)) { r.Reason = "formato de audio invalido"; return r; }
            double tol = (double.IsNaN(tolerance) || double.IsInfinity(tolerance)) ? 1.0 : Math.Max(0.75, Math.Min(1.0, tolerance));
            var segments = Analyzer.ExtractSegments(audio, offset, length);
            var speech = Concat(segments);
            r.SpeechSeconds = speech.Length / (double)SampleRate;
            r.Threshold = Math.Min(0.75, threshold / tol + (r.SpeechSeconds < 1.0 ? 0.04 : 0.0));
            if (r.SpeechSeconds < MinVerifySeconds)
            {
                r.Inconclusive = r.SpeechSeconds >= 0.15;
                r.Reason = r.SpeechSeconds < 0.15 ? "sem fala humana (ruido do ambiente)" : "fala curta demais para identificar";
                return r;
            }
            var embedding = Encoder.Embed(speech);
            if (embedding == null) { r.Inconclusive = true; r.Reason = "fala curta demais para identificar"; return r; }
            r.Score = NeuralSpeakerEncoder.Cosine(embedding, centroid);

            // Mistura de vozes: avalia cada trecho longo e remove os que claramente sao de outra pessoa.
            var kept = new List<float[]>();
            var scores = new double?[segments.Count];
            double bestSegment = double.MinValue;
            if (segments.Count > 1)
                for (int i = 0; i < segments.Count; i++)
                {
                    if (segments[i].Length < SampleRate * 0.8) continue;
                    var e = Encoder.Embed(segments[i]);
                    if (e == null) continue;
                    scores[i] = NeuralSpeakerEncoder.Cosine(e, centroid);
                    bestSegment = Math.Max(bestSegment, scores[i].Value);
                }
            // Corte: claramente abaixo do limite, ou muito abaixo do seu melhor trecho neste mesmo audio.
            double cut = r.Threshold - 0.10;
            if (bestSegment >= r.Threshold + 0.15) cut = Math.Max(cut, bestSegment - 0.30);
            for (int i = 0; i < segments.Count; i++)
            {
                if (scores[i].HasValue && scores[i].Value < cut) { r.RemovedSegments++; continue; }
                kept.Add(segments[i]);
            }
            // Se a sua voz domina um trecho, nao deixe a voz de fundo derrubar a decisao do audio inteiro.
            if (r.RemovedSegments > 0 && kept.Count > 0)
            {
                var keptSpeech = Concat(kept);
                var keptEmbedding = keptSpeech.Length >= SampleRate * MinVerifySeconds ? Encoder.Embed(keptSpeech) : null;
                if (keptEmbedding != null) r.Score = Math.Max(r.Score, NeuralSpeakerEncoder.Cosine(keptEmbedding, centroid));
                r.FilteredWav = BuildWav(kept);
            }
            r.Accepted = r.Score >= r.Threshold && kept.Count > 0;
            r.Reason = r.Accepted
                ? (r.RemovedSegments > 0 ? "sua voz; " + r.RemovedSegments + " trecho(s) de outra voz removido(s)" : "sua voz")
                : "voz diferente da cadastrada";
            return r; // FilteredWav nulo = transcrever o audio original
        }

        /// <summary>Ha fala humana (nao apenas ruido) no audio? Usado quando o filtro de voz esta desligado.</summary>
        public static bool HasHumanSpeech(byte[] audio, double minSeconds)
        {
            int offset, length;
            if (!GetPcmRange(audio, out offset, out length)) return false;
            var analyzer = Analyzer;
            if (analyzer == null) return true; // sem VAD, nao bloqueia
            return analyzer.ExtractSpeech(audio, offset, length).Length >= SampleRate * minSeconds;
        }

        // ------------------------------------------------------------------
        // Utilitarios
        // ------------------------------------------------------------------

        private static float[] Concat(List<float[]> segments)
        {
            int total = 0; foreach (var s in segments) total += s.Length;
            var all = new float[total]; int pos = 0;
            foreach (var s in segments) { Array.Copy(s, 0, all, pos, s.Length); pos += s.Length; }
            return all;
        }

        private static byte[] BuildWav(List<float[]> segments)
        {
            int gap = SampleRate / 5; // 200 ms de silencio entre trechos
            int samples = 0; foreach (var s in segments) samples += s.Length + gap;
            var wav = new byte[44 + samples * 2];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); BitConverter.GetBytes(36 + samples * 2).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8); BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20); BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(SampleRate).CopyTo(wav, 24); BitConverter.GetBytes(SampleRate * 2).CopyTo(wav, 28);
            BitConverter.GetBytes((short)2).CopyTo(wav, 32); BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36); BitConverter.GetBytes(samples * 2).CopyTo(wav, 40);
            int pos = 44 + gap; // comeca com uma pausa curta
            foreach (var s in segments)
            {
                foreach (float f in s)
                {
                    int v = (int)Math.Round(f * 32767.0); v = Math.Max(-32768, Math.Min(32767, v));
                    wav[pos] = (byte)v; wav[pos + 1] = (byte)(v >> 8); pos += 2;
                    if (pos >= wav.Length) break;
                }
                pos += gap * 2;
                if (pos >= wav.Length) break;
            }
            return wav;
        }

        private static double ClippingFraction(byte[] pcm, int offset, int length)
        {
            int clipped = 0, n = length / 2;
            for (int i = 0; i < n; i++) if (Math.Abs((int)(short)(pcm[offset + 2 * i] | pcm[offset + 2 * i + 1] << 8)) >= 32000) clipped++;
            return clipped / (double)Math.Max(1, n);
        }

        public static double Rms(byte[] pcm, int offset, int length)
        {
            double e = 0; int n = length / 2;
            for (int i = 0; i < n; i++) { double v = (short)(pcm[offset + 2 * i] | pcm[offset + 2 * i + 1] << 8); e += v * v; }
            return Math.Sqrt(e / Math.Max(1, n));
        }

        public static bool GetPcmRange(byte[] audio, out int offset, out int length)
        {
            offset = 0; length = audio == null ? 0 : audio.Length;
            if (audio == null || audio.Length < 2) return false;
            if (audio.Length >= 12 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF")
            {
                if (Encoding.ASCII.GetString(audio, 8, 4) != "WAVE") return false;
                bool format = false; int pos = 12;
                while (pos + 8 <= audio.Length)
                {
                    string id = Encoding.ASCII.GetString(audio, pos, 4); int size = BitConverter.ToInt32(audio, pos + 4);
                    if (size < 0 || size > audio.Length - pos - 8) return false;
                    if (id == "fmt " && size >= 16) format = BitConverter.ToInt16(audio, pos + 8) == 1 && BitConverter.ToInt16(audio, pos + 10) == 1 && BitConverter.ToInt32(audio, pos + 12) == SampleRate && BitConverter.ToInt16(audio, pos + 22) == 16;
                    if (id == "data") { offset = pos + 8; length = size & ~1; return format && size >= 2; }
                    pos += 8 + size + (size & 1);
                }
                return false;
            }
            length = audio.Length & ~1;
            return length >= 2;
        }
    }
}
