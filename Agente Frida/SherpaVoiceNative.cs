// language: C# 5 (.NET Framework 4.8), file: SherpaVoiceNative.cs, target: Windows 10/11 x64
// Componentes de mercado, 100% locais (k2-fsa/sherpa-onnx, Apache-2.0):
//  - Silero VAD v5: separa fala humana de ruido (TV ligada sem voz, teclado, ventilador, cliques).
//  - WeSpeaker ResNet34-LM (VoxCeleb): embedding neural de locutor para verificacao "so a minha voz".
// Os arquivos ficam em wake\sherpa. Nada e enviado para a rede.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FridaAgent
{
    internal static class SherpaNative
    {
        private const string Dll = "sherpa-onnx-c-api.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        private static readonly object Sync = new object();
        private static bool _loaded;
        private static string _error;
        public static string Folder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wake", "sherpa"); } }
        public static string LoadError { get { return _error; } }

        /// <summary>Carrega as DLLs pela pasta da Frida (evita o onnxruntime.dll antigo do Windows em System32).</summary>
        public static bool EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded) return true;
                try
                {
                    foreach (string name in new[] { "onnxruntime.dll", "sherpa-onnx-c-api.dll" })
                    {
                        string path = Path.Combine(Folder, name);
                        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo ausente: " + path);
                        if (LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                            throw new DllNotFoundException(name + " (erro " + Marshal.GetLastWin32Error() + ")");
                    }
                    _loaded = true;
                    return true;
                }
                catch (Exception ex) { _error = ex.Message; return false; }
            }
        }

        public static IntPtr Utf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((value ?? "") + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SpeakerEmbeddingExtractorConfig
        {
            public IntPtr model;
            public int num_threads;
            public int debug;
            public IntPtr provider;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SileroVadModelConfig
        {
            public IntPtr model;
            public float threshold;
            public float min_silence_duration;
            public float min_speech_duration;
            public int window_size;
            public float max_speech_duration;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VadModelConfig
        {
            public SileroVadModelConfig silero_vad;
            public int sample_rate;
            public int num_threads;
            public IntPtr provider;
            public int debug;
            public SileroVadModelConfig ten_vad; // mesmo layout de SherpaOnnxTenVadModelConfig; model vazio = desativado
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SpeechSegment
        {
            public long start;
            public IntPtr samples;
            public int n;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SherpaOnnxCreateSpeakerEmbeddingExtractor(ref SpeakerEmbeddingExtractorConfig config);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxDestroySpeakerEmbeddingExtractor(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SherpaOnnxSpeakerEmbeddingExtractorDim(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SherpaOnnxSpeakerEmbeddingExtractorCreateStream(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SherpaOnnxSpeakerEmbeddingExtractorIsReady(IntPtr p, IntPtr stream);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(IntPtr p, IntPtr stream);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(IntPtr v);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int sampleRate, float[] samples, int n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxOnlineStreamInputFinished(IntPtr stream);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxDestroyOnlineStream(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SherpaOnnxCreateVoiceActivityDetector(ref VadModelConfig config, float bufferSizeInSeconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxDestroyVoiceActivityDetector(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxVoiceActivityDetectorAcceptWaveform(IntPtr p, float[] samples, int n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SherpaOnnxVoiceActivityDetectorDetected(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SherpaOnnxVoiceActivityDetectorEmpty(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SherpaOnnxVoiceActivityDetectorFront(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxVoiceActivityDetectorPop(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxVoiceActivityDetectorReset(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxVoiceActivityDetectorFlush(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SherpaOnnxDestroySpeechSegment(IntPtr p);

        public static float[] ToFloat(byte[] pcm, int offset, int length)
        {
            int n = length / 2;
            var result = new float[n];
            for (int i = 0; i < n; i++)
                result[i] = (short)(pcm[offset + 2 * i] | (pcm[offset + 2 * i + 1] << 8)) / 32768f;
            return result;
        }
    }

    /// <summary>Silero VAD v5. Uma instancia por thread de uso (o objeto nativo nao e thread-safe).</summary>
    public sealed class TimedVoiceSegment
    {
        public long Start;
        public float[] Samples;
    }

    public sealed class SileroVoiceActivity : IDisposable
    {
        public const int SampleRate = 16000;
        private IntPtr _vad;
        private readonly object _sync = new object();
        public bool Ready { get { return _vad != IntPtr.Zero; } }

        public SileroVoiceActivity(float threshold = 0.5f, float minSilence = 0.25f, float minSpeech = 0.20f, float maxSpeech = 25f)
        {
            if (!SherpaNative.EnsureLoaded()) throw new DllNotFoundException("sherpa-onnx: " + SherpaNative.LoadError);
            string model = Path.Combine(SherpaNative.Folder, "silero_vad_v5.onnx");
            if (!File.Exists(model)) throw new FileNotFoundException("Modelo Silero VAD ausente", model);
            IntPtr modelPtr = SherpaNative.Utf8(model), provider = SherpaNative.Utf8("cpu"), empty = SherpaNative.Utf8("");
            try
            {
                var cfg = new SherpaNative.VadModelConfig();
                cfg.silero_vad.model = modelPtr;
                cfg.silero_vad.threshold = threshold;
                cfg.silero_vad.min_silence_duration = minSilence;
                cfg.silero_vad.min_speech_duration = minSpeech;
                cfg.silero_vad.window_size = 512;
                cfg.silero_vad.max_speech_duration = maxSpeech;
                cfg.ten_vad.model = empty;
                cfg.ten_vad.threshold = 0.5f;
                cfg.ten_vad.min_silence_duration = minSilence;
                cfg.ten_vad.min_speech_duration = minSpeech;
                cfg.ten_vad.window_size = 256;
                cfg.ten_vad.max_speech_duration = maxSpeech;
                cfg.sample_rate = SampleRate;
                cfg.num_threads = 1;
                cfg.provider = provider;
                cfg.debug = 0;
                _vad = SherpaNative.SherpaOnnxCreateVoiceActivityDetector(ref cfg, 60f);
            }
            finally { Marshal.FreeHGlobal(modelPtr); Marshal.FreeHGlobal(provider); Marshal.FreeHGlobal(empty); }
            if (_vad == IntPtr.Zero) throw new InvalidDataException("Falha ao iniciar Silero VAD");
        }

        /// <summary>Uso continuo (thread do microfone): alimenta e informa se ha fala humana agora.</summary>
        public bool PushAndDetect(byte[] pcm)
        {
            lock (_sync)
            {
                if (_vad == IntPtr.Zero || pcm == null || pcm.Length < 2) return false;
                float[] f = SherpaNative.ToFloat(pcm, 0, pcm.Length & ~1);
                SherpaNative.SherpaOnnxVoiceActivityDetectorAcceptWaveform(_vad, f, f.Length);
                bool detected = SherpaNative.SherpaOnnxVoiceActivityDetectorDetected(_vad) != 0;
                DrainLocked(null);
                return detected;
            }
        }

        public void Reset()
        {
            lock (_sync) if (_vad != IntPtr.Zero) SherpaNative.SherpaOnnxVoiceActivityDetectorReset(_vad);
        }

        /// <summary>Retorna apenas os trechos com fala (amostras float) de uma gravacao inteira.</summary>
        public float[] ExtractSpeech(byte[] pcm, int offset, int length)
        {
            var all = new List<float>();
            foreach (var segment in ExtractSegments(pcm, offset, length)) all.AddRange(segment);
            return all.ToArray();
        }

        /// <summary>Segmentos de fala separados por pausas, na ordem em que ocorreram.</summary>
        public List<float[]> ExtractSegments(byte[] pcm, int offset, int length)
        {
            var samples = new List<float[]>();
            foreach (var segment in ExtractTimedSegments(pcm, offset, length)) samples.Add(segment.Samples);
            return samples;
        }

        public List<TimedVoiceSegment> ExtractTimedSegments(byte[] pcm, int offset, int length)
        {
            lock (_sync)
            {
                var segments = new List<TimedVoiceSegment>();
                if (_vad == IntPtr.Zero || pcm == null || length < 2) return segments;
                SherpaNative.SherpaOnnxVoiceActivityDetectorReset(_vad);
                float[] f = SherpaNative.ToFloat(pcm, offset, length & ~1);
                var speech = new SegmentSink(segments);
                // Alimenta em janelas de 512 amostras (32 ms), como no uso continuo, e recolhe cada segmento.
                var window = new float[512];
                for (int pos = 0; pos < f.Length; pos += window.Length)
                {
                    int n = Math.Min(window.Length, f.Length - pos);
                    Array.Copy(f, pos, window, 0, n);
                    if (n < window.Length) Array.Clear(window, n, window.Length - n);
                    SherpaNative.SherpaOnnxVoiceActivityDetectorAcceptWaveform(_vad, window, window.Length);
                    DrainLocked(speech);
                }
                SherpaNative.SherpaOnnxVoiceActivityDetectorFlush(_vad);
                DrainLocked(speech);
                SherpaNative.SherpaOnnxVoiceActivityDetectorReset(_vad);
                return segments;
            }
        }

        private sealed class SegmentSink
        {
            public readonly List<TimedVoiceSegment> Items;
            public SegmentSink(List<TimedVoiceSegment> items) { Items = items; }
        }

        private void DrainLocked(SegmentSink sink)
        {
            while (SherpaNative.SherpaOnnxVoiceActivityDetectorEmpty(_vad) == 0)
            {
                IntPtr seg = SherpaNative.SherpaOnnxVoiceActivityDetectorFront(_vad);
                if (seg != IntPtr.Zero)
                {
                    if (sink != null)
                    {
                        var s = (SherpaNative.SpeechSegment)Marshal.PtrToStructure(seg, typeof(SherpaNative.SpeechSegment));
                        if (s.n > 0 && s.samples != IntPtr.Zero)
                        {
                            var buf = new float[s.n];
                            Marshal.Copy(s.samples, buf, 0, s.n);
                            sink.Items.Add(new TimedVoiceSegment { Start = s.start, Samples = buf });
                        }
                    }
                    SherpaNative.SherpaOnnxDestroySpeechSegment(seg);
                }
                SherpaNative.SherpaOnnxVoiceActivityDetectorPop(_vad);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_vad != IntPtr.Zero) SherpaNative.SherpaOnnxDestroyVoiceActivityDetector(_vad);
                _vad = IntPtr.Zero;
            }
        }
    }

    /// <summary>Embedding neural de locutor (WeSpeaker ResNet34-LM, 256 dimensoes). Thread-safe.</summary>
    public sealed class NeuralSpeakerEncoder : IDisposable
    {
        public const string ModelFile = "wespeaker_en_voxceleb_resnet34_LM.onnx";
        private IntPtr _extractor;
        private readonly object _sync = new object();
        public int Dim { get; private set; }
        public bool Ready { get { return _extractor != IntPtr.Zero; } }

        private static readonly Lazy<NeuralSpeakerEncoder> Instance = new Lazy<NeuralSpeakerEncoder>(() =>
        {
            try { return new NeuralSpeakerEncoder(); }
            catch (Exception ex) { BootLogger.Log("Verificador neural de locutor indisponivel: " + ex.Message); return null; }
        });
        public static NeuralSpeakerEncoder Shared { get { return Instance.Value; } }

        public NeuralSpeakerEncoder()
        {
            if (!SherpaNative.EnsureLoaded()) throw new DllNotFoundException("sherpa-onnx: " + SherpaNative.LoadError);
            string model = Path.Combine(SherpaNative.Folder, ModelFile);
            if (!File.Exists(model)) throw new FileNotFoundException("Modelo de locutor ausente", model);
            IntPtr modelPtr = SherpaNative.Utf8(model), provider = SherpaNative.Utf8("cpu");
            try
            {
                var cfg = new SherpaNative.SpeakerEmbeddingExtractorConfig { model = modelPtr, num_threads = 2, debug = 0, provider = provider };
                _extractor = SherpaNative.SherpaOnnxCreateSpeakerEmbeddingExtractor(ref cfg);
            }
            finally { Marshal.FreeHGlobal(modelPtr); Marshal.FreeHGlobal(provider); }
            if (_extractor == IntPtr.Zero) throw new InvalidDataException("Falha ao carregar o modelo de locutor");
            Dim = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDim(_extractor);
        }

        /// <summary>Embedding L2-normalizado; null se o audio for curto demais.</summary>
        public double[] Embed(float[] samples)
        {
            if (samples == null || samples.Length < SileroVoiceActivity.SampleRate / 2) return null;
            lock (_sync)
            {
                if (_extractor == IntPtr.Zero) return null;
                IntPtr stream = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorCreateStream(_extractor);
                if (stream == IntPtr.Zero) return null;
                try
                {
                    SherpaNative.SherpaOnnxOnlineStreamAcceptWaveform(stream, SileroVoiceActivity.SampleRate, samples, samples.Length);
                    SherpaNative.SherpaOnnxOnlineStreamInputFinished(stream);
                    if (SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorIsReady(_extractor, stream) == 0) return null;
                    IntPtr v = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(_extractor, stream);
                    if (v == IntPtr.Zero) return null;
                    try
                    {
                        var f = new float[Dim];
                        Marshal.Copy(v, f, 0, Dim);
                        var d = new double[Dim];
                        double norm = 0;
                        for (int i = 0; i < Dim; i++) { d[i] = f[i]; norm += d[i] * d[i]; }
                        norm = Math.Sqrt(norm);
                        if (norm < 1e-9 || double.IsNaN(norm)) return null;
                        for (int i = 0; i < Dim; i++) d[i] /= norm;
                        return d;
                    }
                    finally { SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(v); }
                }
                finally { SherpaNative.SherpaOnnxDestroyOnlineStream(stream); }
            }
        }

        public static double Cosine(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            double dot = 0, aa = 0, bb = 0;
            for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
            return aa <= 0 || bb <= 0 ? -1 : dot / Math.Sqrt(aa * bb);
        }

        public static double[] Mean(IList<double[]> vectors)
        {
            if (vectors == null || vectors.Count == 0) return null;
            var m = new double[vectors[0].Length];
            foreach (var v in vectors) for (int i = 0; i < m.Length; i++) m[i] += v[i];
            double norm = 0;
            for (int i = 0; i < m.Length; i++) norm += m[i] * m[i];
            norm = Math.Sqrt(norm);
            if (norm > 0) for (int i = 0; i < m.Length; i++) m[i] /= norm;
            return m;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_extractor != IntPtr.Zero) SherpaNative.SherpaOnnxDestroySpeakerEmbeddingExtractor(_extractor);
                _extractor = IntPtr.Zero;
            }
        }
    }
}
