// language: C# 5 (.NET Framework 4.8), file: SpeakerVoiceProfile.cs, target: Windows 10/11 x64
// Perfil "so a minha voz" no padrao de mercado (Voice Match / Voice ID):
//   1) Silero VAD separa fala humana de ruido do ambiente;
//   2) WeSpeaker ResNet34-LM gera o embedding neural do locutor;
//   3) similaridade de cosseno contra o centroide de 5 frases de cadastro, com limite calibrado
//      pela consistencia do proprio cadastro (leave-one-out).
// Audio ambiguo ou com mudanca de locutor e recusado inteiro, sem montar comandos parciais.
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
        public byte[] FilteredWav;     // segmentos aceitos, com tempos preservados; nao separa vozes simultaneas
        public string Reason = "";
    }

    public sealed class EnrollmentSample
    {
        public double[] Embedding;
        public double SpeechSeconds;
        public double SignalToNoiseDb;
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
        private const double BaseThreshold = 0.62; // politica conservadora; validar com voz real no ambiente
        private const double MinThreshold = 0.58;
        private const double MinConsistency = 0.65;
        private const double OutlierConsistency = 0.55;

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
                        try { _analyzer = new SileroVoiceActivity(0.60f); }
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
                    if (dim < 64 || d.embeddings.Count < RequiredPhrases || !d.embeddings.All(v => ValidVector(v, dim))) return;
                    var summary = Summarize(d.embeddings);
                    if (summary.LooMean < MinConsistency || summary.WorstIndex >= 0) return;
                    if (double.IsNaN(d.threshold) || double.IsInfinity(d.threshold) || d.threshold > 0.8) d.threshold = BaseThreshold;
                    // Recalibra perfis validos em memoria; preserva o arquivo e as cinco assinaturas originais.
                    d.threshold = Math.Max(summary.Threshold, d.threshold);
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
        public EnrollmentSample CreateEnrollmentSample(byte[] pcmOrWav, byte[] ambient = null)
        {
            if (!EngineAvailable) throw new InvalidOperationException("Verificador de voz indisponivel: " + EngineError);
            int offset, length;
            if (!GetPcmRange(pcmOrWav, out offset, out length)) throw new InvalidDataException("Formato de audio invalido.");
            double clipping = ClippingFraction(pcmOrWav, offset, length);
            if (clipping > 0.01) throw new InvalidDataException("O microfone saturou (voz alta ou perto demais). Afaste-se um pouco e repita esta frase.");
            var timed = Analyzer.ExtractTimedSegments(pcmOrWav, offset, length);
            var segments = timed.Select(x => x.Samples).ToList();
            var speech = Concat(segments);
            double seconds = speech.Length / (double)SampleRate;
            if (seconds < MinEnrollSeconds)
                throw new InvalidDataException(seconds < 0.2 ? "Nenhuma fala detectada. Leia a frase em voz normal, perto do microfone." : "Fala curta demais (" + seconds.ToString("F1") + " s). Leia a frase inteira, sem pausas longas.");
            if (ambient != null && HasHumanSpeech(ambient, 0.15))
                throw new InvalidDataException("Houve fala durante a medicao do ambiente. Fique em silencio ate aparecer LEIA AGORA; desligue TV ou conversas proximas.");
            var quality = MeasureQuality(pcmOrWav, offset, length, timed, ambient);
            string problem = QualityProblem(quality, true);
            if (problem != null) throw new InvalidDataException(problem);
            if (timed.Count > 0 && timed[timed.Count - 1].Start + timed[timed.Count - 1].Samples.Length > length / 2 - SampleRate / 5)
                throw new InvalidDataException("A fala chegou ao fim da gravacao e pode ter sido cortada. Repita deixando uma pausa no final.");
            var embedding = Encoder.Embed(speech);
            if (embedding == null) throw new InvalidDataException("Nao foi possivel extrair sua assinatura de voz desta frase. Repita.");
            var windows = VerificationWindows(timed);
            var localEmbeddings = new List<double[]>();
            foreach (var window in windows)
            {
                if (window.Samples.Length < SampleRate * MinVerifySeconds)
                    throw new InvalidDataException("Trecho de fala isolado e curto demais. Repita a frase de forma continua, sem outra pessoa falando.");
                var local = Encoder.Embed(window.Samples);
                if (local == null || NeuralSpeakerEncoder.Cosine(local, embedding) < 0.60)
                    throw new InvalidDataException("A voz mudou dentro da frase (ou houve interferencia). Grave sozinho e repita.");
                if (localEmbeddings.Any(previous => NeuralSpeakerEncoder.Cosine(previous, local) < 0.55))
                    throw new InvalidDataException("Os trechos da frase nao parecem ser da mesma pessoa. Grave sem outra voz ao fundo e repita.");
                localEmbeddings.Add(local);
            }
            return new EnrollmentSample { Embedding = embedding, SpeechSeconds = seconds, SignalToNoiseDb = quality.SignalToNoiseDb };
        }

        /// <summary>Confere consistencia das frases (leave-one-out) e calcula o limite. Nao salva.</summary>
        public static EnrollmentSummary Summarize(IList<double[]> embeddings)
        {
            var s = new EnrollmentSummary { Count = embeddings == null ? 0 : embeddings.Count };
            if (s.Count < 3) return s;
            int dim = embeddings[0] == null ? 0 : embeddings[0].Length;
            if (dim < 64 || !embeddings.All(v => ValidVector(v, dim))) throw new InvalidDataException("Assinatura de voz invalida; repita o cadastro.");
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
            s.Threshold = Math.Max(MinThreshold, Math.Min(0.72, s.LooMin - 0.14));
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
        { return Check(audio, tolerance, false); }

        // Chamada/parada curta: o silencio ao redor pode conter TTS e nao estima o ruido ambiente.
        public SpeakerCheckResult CheckShortInteraction(byte[] audio, double tolerance)
        { return Check(audio, tolerance, true); }

        private SpeakerCheckResult Check(byte[] audio, double tolerance, bool shortInteraction)
        {
            var r = new SpeakerCheckResult();
            double[] centroid; double threshold;
            lock (_sync) { centroid = _centroid; threshold = _data.threshold; }
            if (shortInteraction) threshold = 0.68; // margem sobre os trechos curtos de TTS medidos (ate 0,627)
            if (!IsReady || centroid == null) { r.Reason = EngineAvailable ? "voz ainda nao cadastrada" : "verificador indisponivel: " + EngineError; return r; }
            int offset, length;
            if (!GetPcmRange(audio, out offset, out length)) { r.Reason = "formato de audio invalido"; return r; }
            double tol = (double.IsNaN(tolerance) || double.IsInfinity(tolerance)) ? 1.0 : Math.Max(0.75, Math.Min(1.0, tolerance));
            var timed = Analyzer.ExtractTimedSegments(audio, offset, length);
            var segments = timed.Select(s => s.Samples).ToList();
            var speech = Concat(segments);
            r.SpeechSeconds = speech.Length / (double)SampleRate;
            r.Threshold = Math.Min(0.75, threshold / tol + (!shortInteraction && r.SpeechSeconds < 1.0 ? 0.04 : 0.0));
            if (r.SpeechSeconds < MinVerifySeconds)
            {
                r.Inconclusive = r.SpeechSeconds >= 0.15;
                r.Reason = r.SpeechSeconds < 0.15 ? "sem fala humana (ruido do ambiente)" : "fala curta demais para identificar";
                return r;
            }
            var quality = MeasureQuality(audio, offset, length, timed, null);
            string qualityProblem = ShortInteractionQualityProblem(quality, shortInteraction);
            if (qualityProblem != null) { r.Reason = qualityProblem; return r; }
            var embedding = Encoder.Embed(speech);
            if (embedding == null) { r.Inconclusive = true; r.Reason = "fala curta demais para identificar"; return r; }
            r.Score = NeuralSpeakerEncoder.Cosine(embedding, centroid);

            if (r.Score < r.Threshold) { r.Reason = "voz diferente da cadastrada"; return r; }
            // Nao executa pedido: apenas abre a escuta ou interrompe. Comandos continuam na analise completa.
            if (shortInteraction) { r.Accepted = true; r.Reason = "identidade confirmada para chamada/parada"; return r; }
            // VAD separa fala de silencio, nao pessoas. Verifica tambem dentro de um segmento continuo.
            bool inconclusive;
            string rejection = VerifySpeechWindows(timed, centroid, threshold / tol, samples => Encoder.Embed(samples), out inconclusive);
            if (rejection != null) { r.Inconclusive = inconclusive; r.Reason = rejection; return r; }
            r.Accepted = true; r.Reason = "sua voz confirmada em todos os trechos";
            // VAD pode omitir consoantes/palavras baixas. Identidade aprovada: transcreve a captura integral.
            r.FilteredWav = null;
            return r;
        }

        /// <summary>Ha fala humana (nao apenas ruido) no audio? Usado quando o filtro de voz esta desligado.</summary>
        public static bool HasHumanSpeech(byte[] audio, double minSeconds)
        { return DetectHumanSpeech(audio, minSeconds) == true; }

        public static bool? DetectHumanSpeech(byte[] audio, double minSeconds)
        {
            int offset, length;
            if (!GetPcmRange(audio, out offset, out length)) return false;
            var analyzer = Analyzer;
            if (analyzer == null) return null;
            return analyzer.ExtractSpeech(audio, offset, length).Length >= SampleRate * minSeconds;
        }

        // ------------------------------------------------------------------
        // Utilitarios
        // ------------------------------------------------------------------

        internal sealed class AudioQuality
        {
            public double SpeechRms, NoiseRms, SignalToNoiseDb, Clipping;
            public bool HasNoiseEstimate;
        }

        internal static AudioQuality MeasureQuality(byte[] audio, int offset, int length,
            IList<TimedVoiceSegment> segments, byte[] ambient)
        {
            var voiced = new List<double>(); var noise = new List<double>();
            const int frameSamples = 320; // 20 ms; mediana evita que um clique defina o piso inteiro.
            int samples = length / 2;
            for (int start = 0; start + frameSamples <= samples; start += frameSamples)
            {
                bool speech = segments.Any(s => start >= s.Start && start + frameSamples <= s.Start + s.Samples.Length);
                bool nearSpeech = segments.Any(s => start + frameSamples > s.Start - 1600 && start < s.Start + s.Samples.Length + 1600);
                double rms = Rms(audio, offset + start * 2, frameSamples * 2);
                if (speech) voiced.Add(rms); else if (!nearSpeech) noise.Add(rms);
            }
            if (ambient != null)
                for (int start = 0; start + frameSamples * 2 <= ambient.Length; start += frameSamples * 2)
                    noise.Add(Rms(ambient, start, frameSamples * 2));
            voiced.Sort(); noise.Sort();
            var q = new AudioQuality { SpeechRms = voiced.Count == 0 ? 0 : voiced[voiced.Count / 2],
                NoiseRms = noise.Count == 0 ? 0 : noise[noise.Count / 2], HasNoiseEstimate = noise.Count >= 10,
                Clipping = ClippingFraction(audio, offset, length) };
            q.SignalToNoiseDb = q.HasNoiseEstimate ? 20 * Math.Log10(Math.Max(1, q.SpeechRms) / Math.Max(1, q.NoiseRms)) : 0;
            return q;
        }

        internal static string QualityProblem(AudioQuality quality, bool enrollment)
        {
            if (quality.Clipping > 0.01) return "Audio saturado. Afaste um pouco o microfone e repita.";
            if (quality.SpeechRms < 100) return "Voz muito baixa ou distante. Aproxime-se do microfone e repita.";
            if (enrollment && !quality.HasNoiseEstimate) return "Nao houve silencio suficiente para medir o ruido. Aguarde a preparacao e deixe uma pausa apos a frase.";
            if (quality.HasNoiseEstimate && quality.SignalToNoiseDb < (enrollment ? 12.0 : 6.0))
                return "Muito ruido em relacao a voz (" + quality.SignalToNoiseDb.ToString("F1") + " dB). Reduza o ruido ou aproxime o microfone e repita.";
            return null;
        }

        internal static string ShortInteractionQualityProblem(AudioQuality quality, bool shortInteraction)
        {
            if (!shortInteraction) return QualityProblem(quality, false);
            if (quality.Clipping > 0.01) return "audio saturado";
            if (quality.SpeechRms < 100) return "voz muito baixa";
            return null; // a identidade neural continua obrigatoria; SNR local nao e confiavel sob TTS.
        }

        internal static List<TimedVoiceSegment> VerificationWindows(IList<TimedVoiceSegment> segments)
        {
            var windows = new List<TimedVoiceSegment>();
            const int windowSize = SampleRate * 16 / 10, hop = SampleRate * 8 / 10;
            foreach (var segment in segments)
            {
                if (segment.Samples.Length <= windowSize) { windows.Add(segment); continue; }
                int start = 0;
                while (true)
                {
                    var part = new float[windowSize]; Array.Copy(segment.Samples, start, part, 0, windowSize);
                    windows.Add(new TimedVoiceSegment { Start = segment.Start + start, Samples = part });
                    if (start + windowSize >= segment.Samples.Length) break;
                    start = Math.Min(start + hop, segment.Samples.Length - windowSize);
                }
            }
            return windows;
        }

        internal static string VerifySpeechWindows(IList<TimedVoiceSegment> segments, double[] centroid,
            double threshold, Func<float[], double[]> embed, out bool inconclusive)
        {
            inconclusive = false;
            if (segments.Count == 0) return "sem fala humana";
            foreach (var window in VerificationWindows(segments))
            {
                double seconds = window.Samples.Length / (double)SampleRate;
                if (seconds < MinVerifySeconds)
                { inconclusive = true; return "trecho curto sem identidade confirmada; repita sozinho"; }
                var local = embed(window.Samples);
                double score = local == null ? -1 : NeuralSpeakerEncoder.Cosine(local, centroid);
                if (double.IsNaN(score) || double.IsInfinity(score) || score < Math.Min(0.80, threshold + (seconds < 1.0 ? 0.04 : 0.0)))
                    return "identidade inconsistente no comando; possivel outra voz ou interferencia; repita sozinho";
            }
            return null;
        }

        private static float[] Concat(List<float[]> segments)
        {
            int total = 0; foreach (var s in segments) total += s.Length;
            var all = new float[total]; int pos = 0;
            foreach (var s in segments) { Array.Copy(s, 0, all, pos, s.Length); pos += s.Length; }
            return all;
        }

        internal static byte[] PreserveAcceptedSegments(byte[] audio, int offset, int length,
            IList<TimedVoiceSegment> segments, bool[] keep, int paddingSamples)
        {
            if (segments.Count != keep.Length || offset < 0 || length < 0 || offset > audio.Length - length)
                throw new ArgumentException("Invalid segment boundaries");
            int samples = length / 2;
            var pcm = new byte[samples * 2];
            // Preserve the original timeline, never glue a number or negation onto another sentence.
            for (int i = 0; i < segments.Count; i++)
            {
                if (!keep[i]) continue;
                var segment = segments[i];
                long start = Math.Max(0, segment.Start - Math.Max(0, paddingSamples));
                long end = Math.Min(samples, segment.Start + segment.Samples.Length + Math.Max(0, paddingSamples));
                for (int j = 0; j < segments.Count; j++)
                {
                    if (keep[j]) continue;
                    long rejectedStart = segments[j].Start, rejectedEnd = rejectedStart + segments[j].Samples.Length;
                    if (rejectedEnd <= segment.Start) start = Math.Max(start, rejectedEnd);
                    else if (rejectedStart >= segment.Start + segment.Samples.Length) end = Math.Min(end, rejectedStart);
                    else { start = end; break; } // overlapping rejected voice: no claim of separation
                }
                if (end > start) Buffer.BlockCopy(audio, offset + (int)start * 2, pcm, (int)start * 2, (int)(end - start) * 2);
            }
            return AudioVoiceEngine.BuildWavFromPcm16Mono(pcm, SampleRate);
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
