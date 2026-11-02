// language: C# 5 (.NET Framework 4.8), file: SpeakerVoiceProfile.cs, target: Windows 10/11 x64
// Perfil de voz do operador: assinatura acustica (MFCC + tom de voz) extraida de gravacoes de treino.
// Cada trecho captado pelo microfone e comparado com o perfil; vozes/sons diferentes sao descartados
// antes de serem enviados para transcricao.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public class VoiceProfileData
    {
        public string created_at = "";
        public List<double[]> samples = new List<double[]>();
        public double self_distance_mean = 1.0;
        public double self_distance_std = 0.3;
    }

    public class SpeakerCheckResult
    {
        public bool Accepted;
        public double Distance;
        public double Threshold;
        public int VoicedFrames;
        public string Reason = "";
    }

    public class SpeakerVoiceProfile
    {
        private const int SampleRate = 16000;
        private const int FrameLen = 400;   // 25 ms
        private const int Hop = 160;        // 10 ms
        private const int FftSize = 512;
        private const int MelBands = 24;
        private const int NumCeps = 12;     // c1..c12
        public const int MinSamplesForLock = 10; // ~2 gravacoes de 20 s: com menos amostras o perfil fica instavel

        private readonly string _path;
        private readonly object _sync = new object();
        private VoiceProfileData _data = new VoiceProfileData();
        private double[] _mu;
        private double[] _sigma;
        private static double[][] _melFilters;

        public SpeakerVoiceProfile(string path)
        {
            _path = path;
            Load();
        }

        public int SampleCount
        {
            get { lock (_sync) { return _data.samples.Count; } }
        }

        public bool IsReady
        {
            get { return SampleCount >= MinSamplesForLock && _mu != null; }
        }

        private void Load()
        {
            lock (_sync)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        VoiceProfileData d = ser.Deserialize<VoiceProfileData>(File.ReadAllText(_path, Encoding.UTF8));
                        if (d != null && d.samples != null) _data = d;
                    }
                }
                catch { }
                RecomputeLocked();
            }
        }

        private void SaveLocked()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(_path, ser.Serialize(_data), new UTF8Encoding(false));
            }
            catch { }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _data = new VoiceProfileData();
                _mu = null;
                _sigma = null;
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }

        /// <summary>Treina o perfil com uma gravacao longa (PCM 16 kHz mono): divide em janelas de ~3 s com fala.</summary>
        public int EnrollFromLongRecording(byte[] pcm16)
        {
            int added = 0;
            // Janelas de 3 s (frases) e de 1,2 s (palavras curtas, como a chamada "Frida!")
            int[] windows = new int[] { SampleRate * 2 * 3, (int)(SampleRate * 2 * 1.2) };
            int[] minVoiced = new int[] { 80, 35 };
            for (int w = 0; w < windows.Length; w++)
            {
                int win = windows[w];
                for (int off = 0; off + win <= pcm16.Length; off += win)
                {
                    int voiced;
                    double[] emb = ComputeEmbedding(pcm16, off, win, out voiced);
                    if (emb != null && voiced >= minVoiced[w])
                    {
                        lock (_sync) { _data.samples.Add(emb); }
                        added++;
                    }
                }
            }
            lock (_sync)
            {
                while (_data.samples.Count > 120) _data.samples.RemoveAt(0);
                if (string.IsNullOrEmpty(_data.created_at)) _data.created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                RecomputeLocked();
                SaveLocked();
            }
            return added;
        }

        /// <summary>Compara um trecho WAV (cabecalho de 44 bytes) com o perfil.</summary>
        public SpeakerCheckResult Check(byte[] wav16, double tolerance)
        {
            SpeakerCheckResult r = new SpeakerCheckResult();
            int voiced;
            int offset = (wav16 != null && wav16.Length > 44 && wav16[0] == 'R') ? 44 : 0;
            double[] emb = ComputeEmbedding(wav16, offset, wav16 == null ? 0 : wav16.Length - offset, out voiced);
            r.VoicedFrames = voiced;
            double[] mu;
            double[] sigma;
            double selfMean, selfStd;
            lock (_sync)
            {
                mu = _mu;
                sigma = _sigma;
                selfMean = _data.self_distance_mean;
                selfStd = _data.self_distance_std;
            }
            if (mu == null)
            {
                r.Accepted = true;
                r.Reason = "perfil nao treinado";
                return r;
            }
            if (emb == null || voiced < 12)
            {
                r.Accepted = false;
                r.Reason = "sem fala humana suficiente";
                return r;
            }
            r.Distance = Distance(emb, mu, sigma);
            // Calibrado em teste: voz treinada ~1,35 (mediana) vs. outra voz feminina parecida ~1,67.
            // Trechos curtos (ex: so "Frida!") variam muito mais: o limite cresce ate 2x abaixo de ~2 s de fala.
            double shortFactor = 1.0 + Math.Max(0.0, 1.0 - voiced / 200.0);
            r.Threshold = Math.Max(1.3, selfMean + 2.2 * selfStd) * shortFactor * Math.Max(0.5, tolerance);
            r.Accepted = r.Distance <= r.Threshold;
            r.Reason = r.Accepted ? "voz do operador" : "voz diferente do perfil";
            return r;
        }

        private void RecomputeLocked()
        {
            int n = _data.samples.Count;
            if (n < MinSamplesForLock)
            {
                _mu = null;
                _sigma = null;
                return;
            }
            int dim = _data.samples[0].Length;
            double[] mu = new double[dim];
            double[] sg = new double[dim];
            for (int i = 0; i < n; i++)
                for (int d = 0; d < dim; d++) mu[d] += _data.samples[i][d] / n;
            for (int i = 0; i < n; i++)
                for (int d = 0; d < dim; d++) sg[d] += Math.Pow(_data.samples[i][d] - mu[d], 2) / n;
            for (int d = 0; d < dim; d++) sg[d] = Math.Max(Math.Sqrt(sg[d]), 0.05 + Math.Abs(mu[d]) * 0.05);
            // Tom de voz: chamar alguem ("Frida!") sobe o tom; tolerancia minima de ~30 Hz
            if (dim > NumCeps * 2) sg[NumCeps * 2] = Math.Max(sg[NumCeps * 2], 0.6);
            _mu = mu;
            _sigma = sg;

            // Calibracao: distancia de cada amostra de treino ao proprio perfil
            double sum = 0, sum2 = 0;
            for (int i = 0; i < n; i++)
            {
                double dd = Distance(_data.samples[i], mu, sg);
                sum += dd;
                sum2 += dd * dd;
            }
            double mean = sum / n;
            _data.self_distance_mean = mean;
            _data.self_distance_std = Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
        }

        // Dimensoes usadas na comparacao: medias dos MFCC c1..c12 + tom de voz (os desvios pioraram a separacao no teste)
        private static readonly int[] CompareDims = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, NumCeps * 2 };

        private static double Distance(double[] x, double[] mu, double[] sigma)
        {
            double acc = 0;
            int used = 0;
            for (int i = 0; i < CompareDims.Length; i++)
            {
                int d = CompareDims[i];
                if (d >= x.Length || d >= mu.Length) continue;
                double z = (x[d] - mu[d]) / sigma[d];
                acc += z * z;
                used++;
            }
            return used == 0 ? 0 : Math.Sqrt(acc / used);
        }

        /// <summary>Aprendizado continuo: comandos iniciados pelo operador (F4/icone) entram no perfil.</summary>
        public bool AddUtterance(byte[] wav16)
        {
            int offset = (wav16 != null && wav16.Length > 44 && wav16[0] == 'R') ? 44 : 0;
            int voiced;
            double[] emb = ComputeEmbedding(wav16, offset, wav16 == null ? 0 : wav16.Length - offset, out voiced);
            if (emb == null || voiced < 80) return false;
            lock (_sync)
            {
                _data.samples.Add(emb);
                while (_data.samples.Count > 120) _data.samples.RemoveAt(0);
                RecomputeLocked();
                SaveLocked();
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Extracao de caracteristicas
        // ------------------------------------------------------------------

        /// <summary>Assinatura: media e desvio dos MFCC c1..c12 nos quadros com voz + tom medio (pitch).</summary>
        public static double[] ComputeEmbedding(byte[] pcm, int offset, int length, out int voicedFrames)
        {
            voicedFrames = 0;
            if (pcm == null || length < FrameLen * 2 * 10) return null;
            int samples = length / 2;
            double[] x = new double[samples];
            for (int i = 0; i < samples; i++)
            {
                x[i] = (short)(pcm[offset + i * 2] | (pcm[offset + i * 2 + 1] << 8));
            }

            int frames = (samples - FrameLen) / Hop;
            if (frames <= 0) return null;

            // Limiar de voz relativo: quadros acima de 35% da energia RMS mais alta (ignora silencio/ruido de fundo)
            double[] frameRms = new double[frames];
            double maxRms = 0;
            for (int f = 0; f < frames; f++)
            {
                double s = 0;
                int st = f * Hop;
                for (int i = 0; i < FrameLen; i++) s += x[st + i] * x[st + i];
                frameRms[f] = Math.Sqrt(s / FrameLen);
                if (frameRms[f] > maxRms) maxRms = frameRms[f];
            }
            if (maxRms < 150) return null;
            double gate = Math.Max(140, maxRms * 0.35);

            double[][] mel = GetMelFilters();
            double[] window = new double[FrameLen];
            for (int i = 0; i < FrameLen; i++) window[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameLen - 1));

            double[] sum = new double[NumCeps];
            double[] sum2 = new double[NumCeps];
            double pitchSum = 0;
            int pitchCount = 0;
            double[] re = new double[FftSize];
            double[] im = new double[FftSize];
            double[] melE = new double[MelBands];

            for (int f = 0; f < frames; f++)
            {
                if (frameRms[f] < gate) continue;
                int st = f * Hop;
                Array.Clear(re, 0, FftSize);
                Array.Clear(im, 0, FftSize);
                double prev = 0;
                for (int i = 0; i < FrameLen; i++)
                {
                    double v = x[st + i];
                    re[i] = (v - 0.97 * prev) * window[i]; // pre-enfase
                    prev = v;
                }
                Fft(re, im);
                for (int b = 0; b < MelBands; b++)
                {
                    double e = 0;
                    double[] filt = mel[b];
                    for (int k = 0; k < filt.Length; k++)
                    {
                        if (filt[k] == 0) continue;
                        e += filt[k] * (re[k] * re[k] + im[k] * im[k]);
                    }
                    melE[b] = Math.Log(e + 1e-3);
                }
                for (int c = 1; c <= NumCeps; c++)
                {
                    double acc = 0;
                    for (int b = 0; b < MelBands; b++) acc += melE[b] * Math.Cos(Math.PI * c * (b + 0.5) / MelBands);
                    sum[c - 1] += acc;
                    sum2[c - 1] += acc * acc;
                }

                double pitch = EstimatePitch(x, st, FrameLen);
                if (pitch > 0)
                {
                    pitchSum += pitch;
                    pitchCount++;
                }
                voicedFrames++;
            }

            if (voicedFrames < 10) return null;
            double[] emb = new double[NumCeps * 2 + 1];
            for (int c = 0; c < NumCeps; c++)
            {
                double m = sum[c] / voicedFrames;
                emb[c] = m;
                emb[NumCeps + c] = Math.Sqrt(Math.Max(0, sum2[c] / voicedFrames - m * m));
            }
            emb[NumCeps * 2] = pitchCount > 5 ? (pitchSum / pitchCount) / 50.0 : 0;
            return emb;
        }

        /// <summary>Tom de voz (Hz) por autocorrelacao entre 70 e 400 Hz; 0 se o quadro nao for vozeado.</summary>
        private static double EstimatePitch(double[] x, int start, int len)
        {
            int minLag = SampleRate / 400;
            int maxLag = SampleRate / 70;
            if (len <= maxLag + 10) len = Math.Min(x.Length - start, maxLag * 2 + 20);
            if (start + len > x.Length) return 0;
            double energy = 0;
            for (int i = 0; i < len; i++) energy += x[start + i] * x[start + i];
            if (energy <= 0) return 0;
            double best = 0;
            int bestLag = 0;
            for (int lag = minLag; lag <= maxLag && lag < len; lag++)
            {
                double acc = 0;
                for (int i = 0; i + lag < len; i++) acc += x[start + i] * x[start + i + lag];
                if (acc > best)
                {
                    best = acc;
                    bestLag = lag;
                }
            }
            if (bestLag == 0 || best / energy < 0.35) return 0;
            return SampleRate / (double)bestLag;
        }

        private static double[][] GetMelFilters()
        {
            if (_melFilters != null) return _melFilters;
            int bins = FftSize / 2 + 1;
            double melLow = HzToMel(90), melHigh = HzToMel(7200);
            double[] pts = new double[MelBands + 2];
            for (int i = 0; i < pts.Length; i++)
            {
                double hz = MelToHz(melLow + (melHigh - melLow) * i / (MelBands + 1));
                pts[i] = Math.Floor((FftSize + 1) * hz / SampleRate);
            }
            double[][] f = new double[MelBands][];
            for (int b = 0; b < MelBands; b++)
            {
                f[b] = new double[bins];
                for (int k = (int)pts[b]; k < (int)pts[b + 1] && k < bins; k++)
                    f[b][k] = (k - pts[b]) / Math.Max(1, pts[b + 1] - pts[b]);
                for (int k = (int)pts[b + 1]; k < (int)pts[b + 2] && k < bins; k++)
                    f[b][k] = (pts[b + 2] - k) / Math.Max(1, pts[b + 2] - pts[b + 1]);
            }
            _melFilters = f;
            return f;
        }

        private static double HzToMel(double hz) { return 2595 * Math.Log10(1 + hz / 700.0); }
        private static double MelToHz(double mel) { return 700 * (Math.Pow(10, mel / 2595.0) - 1); }

        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
