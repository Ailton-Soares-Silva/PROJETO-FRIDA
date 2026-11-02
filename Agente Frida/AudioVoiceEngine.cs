// language: C# 5 (.NET Framework 4.8), file: AudioVoiceEngine.cs, target: Windows 10/11 x64
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace FridaAgent
{
    public class MicDeviceInfo
    {
        public uint DeviceId;
        public string Name;
        public double ProbeRms;
        public int RecordedBytes;
        public bool IsVirtualDummy;
    }

    internal static class WinMmNative
    {
        public const int WAVE_FORMAT_PCM = 1;
        public const int CALLBACK_EVENT = 0x00050000;
        public const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveInGetDevCaps(IntPtr uDeviceID, out WAVEINCAPS pwic, uint cbwic);

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInClose(IntPtr hwi);
    }

    public class AudioVoiceEngine : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BufferSamples = 1024; // 64ms per chunk
        private const int BufferBytes = BufferSamples * 2;
        private const int NumBuffers = 8;
        private const int PreRollChunks = 22; // ~1.4 seconds pre-roll

        private readonly FridaSettings _settings;
        private readonly HologramOrbForm _orb;
        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json;

        private SpeechRecognizer _winRtWakeRecognizer;
        private Thread _micThread;
        private AutoResetEvent _waveEvent;
        private volatile bool _running = false;
        private volatile bool _restartMicLoop = false;
        private volatile bool _paused = false;
        private volatile bool _isSpeaking = false;
        private volatile bool _isBusyProcessingTurn = false;
        private volatile bool _cancelSpeechRequested = false;
        private volatile bool _followUpWindowActive = false;
        private volatile bool _verifyingStandbyWake = false;

        private uint _selectedMicId = uint.MaxValue;
        private string _selectedMicName = "Padrao";

        private DateTime _followUpDeadlineUtc = DateTime.MinValue;
        private DateTime _ignoreMicUntilUtc = DateTime.MinValue;
        private static DateTime _networkOutageBackoffUntilUtc = DateTime.MinValue;

        private static bool IsNetworkOutageException(Exception ex)
        {
            if (ex == null) return false;
            if (ex is HttpRequestException) return true;
            string msg = ex.Message ?? "";
            if (msg.IndexOf("enviar a solicitação", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (ex.InnerException != null) return IsNetworkOutageException(ex.InnerException);
            return false;
        }

        private readonly object _recLock = new object();
        private readonly Queue<byte[]> _preRollQueue = new Queue<byte[]>();

        // Explicit foreground command capture (after F4, Tray click, Wake-Only greeting, or Follow-Up)
        private bool _capturingActiveCommand = false;
        private bool _includeWakeStrip = false;
        private bool _heardVoiceInActive = false;
        private DateTime _activeStartedUtc;
        private DateTime _activeLastVoiceUtc;
        private MemoryStream _activeCapturePcm;

        // Always-on background wake-word VAD capture directly on the physical microphone (immune to Steam Streaming Mic hijacking WAVE_MAPPER!)
        private bool _capturingStandbyCandidate = false;
        private int _standbyVoiceFrames = 0;
        private DateTime _standbyStartedUtc;
        private DateTime _standbyLastVoiceUtc;
        private MemoryStream _standbyCandidatePcm;

        private double _noiseFloorRms = 155.0;
        private bool _noiseCalibrated = false;
        private int _calibFrames = 0;
        private double _calibSum = 0.0;
        private int _consecutiveVoiceFrames = 0;

        public event Action<string, bool> OnUserTranscriptReady;
        public event Action OnFollowUpWindowExpired;
        public event Action<string> OnNaturalVoiceUnavailable;

        public bool IsPaused
        {
            get { return _paused; }
            set { _paused = value; }
        }

        public bool IsSpeaking
        {
            get { return _isSpeaking; }
        }

        public bool IsBusyProcessingTurn
        {
            get { return _isBusyProcessingTurn; }
            set
            {
                _isBusyProcessingTurn = value;
                if (value)
                {
                    _followUpWindowActive = false;
                    lock (_recLock)
                    {
                        _preRollQueue.Clear();
                        _consecutiveVoiceFrames = 0;
                    }
                }
            }
        }

        public uint ActiveMicDeviceId
        {
            get { return _selectedMicId; }
        }

        public string ActiveMicName
        {
            get { return _selectedMicName; }
        }

        public AudioVoiceEngine(FridaSettings settings, HologramOrbForm orb, HttpClient sharedHttp)
        {
            _settings = settings;
            _orb = orb;
            _http = sharedHttp;
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 64 * 1024 * 1024;
            _voiceProfile = new SpeakerVoiceProfile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frida_speaker_profile.json"));
        }

        // ------------------------------------------------------------------
        // Filtro de voz do operador
        // ------------------------------------------------------------------
        private readonly SpeakerVoiceProfile _voiceProfile;
        private MemoryStream _enrollPcm;
        private DateTime _enrollUntilUtc;
        private Action<int> _enrollDone;

        public SpeakerVoiceProfile VoiceProfile
        {
            get { return _voiceProfile; }
        }

        public bool IsEnrollingVoice
        {
            get { return _enrollPcm != null; }
        }

        /// <summary>Grava o operador por N segundos e treina o perfil. Callback recebe quantas amostras foram aproveitadas.</summary>
        public void StartVoiceEnrollment(int seconds, Action<int> onDone)
        {
            lock (_recLock)
            {
                _enrollDone = onDone;
                _enrollUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(6, seconds));
                _enrollPcm = new MemoryStream();
                _capturingActiveCommand = false;
                _capturingStandbyCandidate = false;
                _followUpWindowActive = false;
            }
            _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · APRENDENDO SUA VOZ", "Leia o texto em voz alta, no seu tom normal...");
        }

        /// <summary>Guarda os ultimos 30 trechos avaliados (voice_filter_debug) para calibrar o filtro com a voz real.</summary>
        private static void SaveFilterDebugClip(byte[] wav, string where, SpeakerCheckResult r)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice_filter_debug");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string name = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:yyyyMMdd_HHmmss}_{1}_{2}_d{3:0.00}.wav",
                    DateTime.Now, where, r.Accepted ? "ACEITO" : "DESCARTADO", r.Distance);
                File.WriteAllBytes(Path.Combine(dir, name), wav);
                FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.wav");
                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return a.CreationTimeUtc.CompareTo(b.CreationTimeUtc); });
                for (int i = 0; i < files.Length - 30; i++) { try { files[i].Delete(); } catch { } }
            }
            catch { }
        }

        private bool PassesSpeakerLock(byte[] wav, string where)
        {
            if (!_settings.speaker_lock_enabled || _voiceProfile == null || !_voiceProfile.IsReady) return true;
            try
            {
                double tol = _settings.speaker_lock_tolerance;
                // Tolerancia ampliada (2.4x / piso 3.3) garante que a voz do operador nunca seja barrada
                tol = Math.Max(tol * 2.4, 3.3);
                SpeakerCheckResult r = _voiceProfile.Check(wav, tol);
                BootLogger.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "FiltroVoz [{0}] d={1:0.00} limite={2:0.00} quadros={3} -> {4} ({5})",
                    where, r.Distance, r.Threshold, r.VoicedFrames, r.Accepted ? "ACEITO" : "DESCARTADO", r.Reason));
                SaveFilterDebugClip(wav, where, r);
                return r.Accepted;
            }
            catch (Exception ex)
            {
                BootLogger.Log("FiltroVoz erro: " + ex.Message);
                return true;
            }
        }

        public List<MicDeviceInfo> EnumerateAndProbeMicrophones()
        {
            List<MicDeviceInfo> list = new List<MicDeviceInfo>();
            uint count = WinMmNative.waveInGetNumDevs();
            uint capsSize = (uint)Marshal.SizeOf(typeof(WinMmNative.WAVEINCAPS));

            for (uint id = 0; id < count; id++)
            {
                WinMmNative.WAVEINCAPS caps;
                if (WinMmNative.waveInGetDevCaps((IntPtr)(int)id, out caps, capsSize) != 0) continue;

                MicDeviceInfo info = new MicDeviceInfo();
                info.DeviceId = id;
                info.Name = (caps.szPname ?? ("Microfone " + id)).Trim();
                string low = info.Name.ToLowerInvariant();
                info.IsVirtualDummy = low.Contains("steam streaming")
                                   || low.Contains("stereo mix")
                                   || low.Contains("mixagem")
                                   || low.Contains("virtual")
                                   || low.Contains("vb-audio")
                                   || low.Contains("cable output");

                // Quick 220ms live hardware probe to verify the mic actually delivers non-zero acoustic samples
                ProbeSingleMic(id, out info.RecordedBytes, out info.ProbeRms);
                list.Add(info);
            }

            return list;
        }

        private static void ProbeSingleMic(uint deviceId, out int recBytes, out double rms)
        {
            recBytes = 0;
            rms = 0.0;

            WinMmNative.WAVEFORMATEX fmt = new WinMmNative.WAVEFORMATEX();
            fmt.wFormatTag = WinMmNative.WAVE_FORMAT_PCM;
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = SampleRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = 2;
            fmt.nAvgBytesPerSec = SampleRate * 2;

            IntPtr hwi = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            IntPtr pHdr = IntPtr.Zero;
            int hdrSize = Marshal.SizeOf(typeof(WinMmNative.WAVEHDR));

            try
            {
                if (WinMmNative.waveInOpen(out hwi, deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0 || hwi == IntPtr.Zero)
                {
                    return;
                }

                int byteLen = 7040; // ~220ms at 16kHz 16-bit
                buf = Marshal.AllocHGlobal(byteLen);
                pHdr = Marshal.AllocHGlobal(hdrSize);

                WinMmNative.WAVEHDR hdr = new WinMmNative.WAVEHDR();
                hdr.lpData = buf;
                hdr.dwBufferLength = (uint)byteLen;
                Marshal.StructureToPtr(hdr, pHdr, false);

                WinMmNative.waveInPrepareHeader(hwi, pHdr, hdrSize);
                WinMmNative.waveInAddBuffer(hwi, pHdr, hdrSize);
                WinMmNative.waveInStart(hwi);
                Thread.Sleep(240);

                hdr = (WinMmNative.WAVEHDR)Marshal.PtrToStructure(pHdr, typeof(WinMmNative.WAVEHDR));
                recBytes = (int)hdr.dwBytesRecorded;
                if (recBytes >= 64)
                {
                    byte[] raw = new byte[recBytes];
                    Marshal.Copy(buf, raw, 0, recBytes);
                    double sumSq = 0;
                    int samples = recBytes / 2;
                    for (int b = 0; b + 1 < recBytes; b += 2)
                    {
                        short s = (short)(raw[b] | (raw[b + 1] << 8));
                        sumSq += (double)s * s;
                    }
                    rms = Math.Sqrt(sumSq / Math.Max(1, samples));
                }
            }
            catch { }
            finally
            {
                if (hwi != IntPtr.Zero)
                {
                    try { WinMmNative.waveInReset(hwi); } catch { }
                    if (pHdr != IntPtr.Zero)
                    {
                        try { WinMmNative.waveInUnprepareHeader(hwi, pHdr, hdrSize); } catch { }
                    }
                    try { WinMmNative.waveInClose(hwi); } catch { }
                }
                if (pHdr != IntPtr.Zero) Marshal.FreeHGlobal(pHdr);
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        private void AutoSelectBestPhysicalMic()
        {
            uint count = WinMmNative.waveInGetNumDevs();
            if (count == 0) return;

            if (_settings.preferred_mic_device >= 0 && _settings.preferred_mic_device < (int)count)
            {
                int rec; double rms;
                ProbeSingleMic((uint)_settings.preferred_mic_device, out rec, out rms);
                if (rec > 0)
                {
                    _selectedMicId = (uint)_settings.preferred_mic_device;
                    WinMmNative.WAVEINCAPS c;
                    if (WinMmNative.waveInGetDevCaps((IntPtr)_settings.preferred_mic_device, out c, (uint)Marshal.SizeOf(typeof(WinMmNative.WAVEINCAPS))) == 0)
                    {
                        _selectedMicName = (c.szPname ?? "Microfone").Trim();
                    }
                    return;
                }
            }

            List<MicDeviceInfo> mics = EnumerateAndProbeMicrophones();
            MicDeviceInfo best = null;
            double bestScore = -1e9;

            for (int i = 0; i < mics.Count; i++)
            {
                MicDeviceInfo m = mics[i];
                if (m.RecordedBytes <= 0) continue; // Disconnected/powered-off Bluetooth headset

                double score = 0.0;
                if (!m.IsVirtualDummy) score += 10000.0;
                string low = m.Name.ToLowerInvariant();
                if (low.Contains("realtek") || low.Contains("grupo de microfones") || low.Contains("microphone array") || low.Contains("intel smart sound"))
                {
                    score += 35000.0;
                }
                else if (low.Contains("headset") || low.Contains("booms") || low.Contains("bluetooth"))
                {
                    score += 500.0;
                }
                else
                {
                    score += 5000.0;
                }

                if (m.ProbeRms >= 10.0 && m.ProbeRms <= 450.0)
                {
                    score += 4000.0;
                }
                else if (m.ProbeRms > 600.0)
                {
                    score -= 3000.0;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            if (best != null)
            {
                _selectedMicId = best.DeviceId;
                _selectedMicName = best.Name;
                if (best.ProbeRms > 30.0 && best.ProbeRms < 900.0)
                {
                    _noiseFloorRms = best.ProbeRms;
                }
            }
            else
            {
                _selectedMicId = uint.MaxValue;
                _selectedMicName = "Padrao do Sistema";
            }
        }

        public void SwitchMicrophoneDevice(uint deviceId, string deviceName)
        {
            _selectedMicId = deviceId;
            _selectedMicName = deviceName ?? ("Microfone " + deviceId);
            _noiseCalibrated = false;
            _calibFrames = 0;
            _calibSum = 0.0;
            _restartMicLoop = true;
            if (_waveEvent != null)
            {
                try { _waveEvent.Set(); } catch { }
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            if (WinMmNative.waveInGetNumDevs() > 0)
            {
                AutoSelectBestPhysicalMic();
                _waveEvent = new AutoResetEvent(false);
                _micThread = new Thread(MicSupervisorLoop);
                _micThread.IsBackground = true;
                _micThread.Name = "FridaPhysicalMicEngine";
                _micThread.Start();
            }

            StartWinRtWakeWordEngine();
        }

        private void MicSupervisorLoop()
        {
            while (_running)
            {
                _restartMicLoop = false;
                RunSingleDeviceCaptureSession(_selectedMicId);
                if (_running && !_restartMicLoop)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private void StartWinRtWakeWordEngine()
        {
            try
            {
                _winRtWakeRecognizer = new SpeechRecognizer(new Language("pt-BR"));
                string[] phrases = new string[]
                {
                    "Frida", "Oi Frida", "Ei Frida", "Olá Frida", "Ok Frida",
                    "Fala Frida", "Ativar Frida", "Escuta Frida", "Frita", "Querida"
                };

                SpeechRecognitionListConstraint wakeConstraint = new SpeechRecognitionListConstraint(phrases, "frida_wake");
                _winRtWakeRecognizer.Constraints.Add(wakeConstraint);

                _winRtWakeRecognizer.HypothesisGenerated += OnWinRtHypothesis;
                _winRtWakeRecognizer.ContinuousRecognitionSession.ResultGenerated += OnWinRtResult;
                _winRtWakeRecognizer.ContinuousRecognitionSession.Completed += OnWinRtSessionCompleted;

                IAsyncOperation<SpeechRecognitionCompilationResult> compileOp = _winRtWakeRecognizer.CompileConstraintsAsync();
                compileOp.Completed = delegate(IAsyncOperation<SpeechRecognitionCompilationResult> info, AsyncStatus status)
                {
                    if (status == AsyncStatus.Completed && _running)
                    {
                        StartContinuousSessionSafe();
                    }
                };
            }
            catch { }
        }

        private void StartContinuousSessionSafe()
        {
            try
            {
                if (!_running || _winRtWakeRecognizer == null) return;
                IAsyncAction startOp = _winRtWakeRecognizer.ContinuousRecognitionSession.StartAsync();
                startOp.Completed = delegate(IAsyncAction a, AsyncStatus s) { };
            }
            catch { }
        }

        private void OnWinRtHypothesis(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
        {
            try
            {
                string text = args != null && args.Hypothesis != null ? args.Hypothesis.Text : "";
                if (!string.IsNullOrEmpty(text) && ContainsWakeWordPhonetic(text))
                {
                    CheckWakeTriggerFromWinRt();
                }
            }
            catch { }
        }

        private void OnWinRtResult(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            try
            {
                if (args != null && args.Result != null && !string.IsNullOrEmpty(args.Result.Text))
                {
                    if (ContainsWakeWordPhonetic(args.Result.Text))
                    {
                        CheckWakeTriggerFromWinRt();
                    }
                }
            }
            catch { }
        }

        private void OnWinRtSessionCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
        {
            if (!_running) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(600);
                StartContinuousSessionSafe();
            });
        }

        private void CheckWakeTriggerFromWinRt()
        {
            if (!_running || _paused || _isSpeaking || _isBusyProcessingTurn) return;
            if (DateTime.UtcNow < _ignoreMicUntilUtc) return;
            TriggerManualOrWakeListen(true);
        }

        public void TriggerManualOrWakeListen(bool fromWakeWord)
        {
            lock (_recLock)
            {
                if (_capturingActiveCommand) return;
                if (_isSpeaking)
                {
                    StopCurrentSpeech();
                }

                // Cancel any standby candidate and promote directly to active foreground command capture
                if (_standbyCandidatePcm != null)
                {
                    _standbyCandidatePcm.Dispose();
                    _standbyCandidatePcm = null;
                }
                _capturingStandbyCandidate = false;

                _followUpWindowActive = false;
                _capturingActiveCommand = true;
                _includeWakeStrip = fromWakeWord;
                _activeStartedUtc = DateTime.UtcNow;
                _activeLastVoiceUtc = DateTime.UtcNow;
                _heardVoiceInActive = fromWakeWord;
                _activeCapturePcm = new MemoryStream();

                if (fromWakeWord)
                {
                    foreach (byte[] chunk in _preRollQueue)
                    {
                        _activeCapturePcm.Write(chunk, 0, chunk.Length);
                    }
                }
            }

            if (!fromWakeWord)
            {
                PlaySoftWakeToneAsync();
            }

            _orb.SetActivityState(
                OrbActivityState.Listening,
                "FRIDA · ESCUTANDO (" + TruncateMicName(_selectedMicName) + ")",
                "Estou te ouvindo, pode falar...");
        }

        public void EnterFollowUpConversationWindow()
        {
            if (!_running || _paused)
            {
                _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                return;
            }

            _followUpDeadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(5, _settings.followup_listen_seconds));
            _followUpWindowActive = true;
            _consecutiveVoiceFrames = 0;
            _orb.SetActivityState(
                OrbActivityState.FollowUp,
                "FRIDA · CONVERSA ATIVA",
                "Pode responder direto sem repetir \"Frida\"...");
        }

        public void CancelFollowUpAndHide()
        {
            _followUpWindowActive = false;
            lock (_recLock)
            {
                _capturingActiveCommand = false;
                if (_activeCapturePcm != null)
                {
                    _activeCapturePcm.Dispose();
                    _activeCapturePcm = null;
                }
            }
            _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
        }

        public void StopCurrentSpeech()
        {
            _cancelSpeechRequested = true;
        }

        private void RunSingleDeviceCaptureSession(uint deviceId)
        {
            IntPtr hWaveIn = IntPtr.Zero;
            IntPtr[] headerPtrs = new IntPtr[NumBuffers];
            IntPtr[] dataPtrs = new IntPtr[NumBuffers];
            int hdrSize = Marshal.SizeOf(typeof(WinMmNative.WAVEHDR));

            try
            {
                WinMmNative.WAVEFORMATEX fmt = new WinMmNative.WAVEFORMATEX();
                fmt.wFormatTag = WinMmNative.WAVE_FORMAT_PCM;
                fmt.nChannels = 1;
                fmt.nSamplesPerSec = SampleRate;
                fmt.wBitsPerSample = 16;
                fmt.nBlockAlign = 2;
                fmt.nAvgBytesPerSec = SampleRate * 2;
                fmt.cbSize = 0;

                int rc = WinMmNative.waveInOpen(
                    out hWaveIn,
                    deviceId,
                    ref fmt,
                    _waveEvent.SafeWaitHandle.DangerousGetHandle(),
                    IntPtr.Zero,
                    WinMmNative.CALLBACK_EVENT);

                if (rc != 0 || hWaveIn == IntPtr.Zero) return;

                for (int i = 0; i < NumBuffers; i++)
                {
                    dataPtrs[i] = Marshal.AllocHGlobal(BufferBytes);
                    headerPtrs[i] = Marshal.AllocHGlobal(hdrSize);

                    WinMmNative.WAVEHDR hdr = new WinMmNative.WAVEHDR();
                    hdr.lpData = dataPtrs[i];
                    hdr.dwBufferLength = BufferBytes;
                    hdr.dwFlags = 0;

                    Marshal.StructureToPtr(hdr, headerPtrs[i], false);
                    WinMmNative.waveInPrepareHeader(hWaveIn, headerPtrs[i], hdrSize);
                    WinMmNative.waveInAddBuffer(hWaveIn, headerPtrs[i], hdrSize);
                }

                WinMmNative.waveInStart(hWaveIn);

                byte[] chunkBuf = new byte[BufferBytes];
                while (_running && !_restartMicLoop)
                {
                    _waveEvent.WaitOne(75);
                    if (!_running || _restartMicLoop) break;

                    for (int i = 0; i < NumBuffers; i++)
                    {
                        WinMmNative.WAVEHDR hdr = (WinMmNative.WAVEHDR)Marshal.PtrToStructure(headerPtrs[i], typeof(WinMmNative.WAVEHDR));
                        if ((hdr.dwFlags & WinMmNative.WHDR_DONE) != 0)
                        {
                            int recorded = (int)hdr.dwBytesRecorded;
                            if (recorded > 0 && recorded <= BufferBytes)
                            {
                                Marshal.Copy(hdr.lpData, chunkBuf, 0, recorded);
                                byte[] copy = new byte[recorded];
                                Buffer.BlockCopy(chunkBuf, 0, copy, 0, recorded);
                                ProcessMicPcmChunk(copy);
                            }

                            hdr.dwBytesRecorded = 0;
                            hdr.dwFlags &= ~WinMmNative.WHDR_DONE;
                            Marshal.StructureToPtr(hdr, headerPtrs[i], false);
                            if (_running && !_restartMicLoop)
                            {
                                WinMmNative.waveInAddBuffer(hWaveIn, headerPtrs[i], hdrSize);
                            }
                        }
                    }

                    CheckFollowUpTimeout();
                }
            }
            catch { }
            finally
            {
                if (hWaveIn != IntPtr.Zero)
                {
                    try { WinMmNative.waveInReset(hWaveIn); } catch { }
                    for (int i = 0; i < NumBuffers; i++)
                    {
                        if (headerPtrs[i] != IntPtr.Zero)
                        {
                            try { WinMmNative.waveInUnprepareHeader(hWaveIn, headerPtrs[i], hdrSize); } catch { }
                            Marshal.FreeHGlobal(headerPtrs[i]);
                        }
                        if (dataPtrs[i] != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(dataPtrs[i]);
                        }
                    }
                    try { WinMmNative.waveInClose(hWaveIn); } catch { }
                }
            }
        }

        private void ProcessMicPcmChunk(byte[] pcmBytes)
        {
            float rmsRaw;
            int peakSample;
            float[] bands16 = ComputePcmSpectrum16(pcmBytes, 0, pcmBytes.Length, out rmsRaw, out peakSample);

            // Initial fast 0.5s ambient floor calibration on the selected physical microphone
            if (!_noiseCalibrated && !_isSpeaking)
            {
                _calibSum += rmsRaw;
                _calibFrames++;
                if (_calibFrames >= 8)
                {
                    _noiseFloorRms = Math.Max(45.0, Math.Min(650.0, _calibSum / _calibFrames));
                    _noiseCalibrated = true;
                }
            }
            else if (!_capturingActiveCommand && !_capturingStandbyCandidate && !_isSpeaking && rmsRaw < _noiseFloorRms * 1.45)
            {
                _noiseFloorRms = _noiseFloorRms * 0.95 + rmsRaw * 0.05;
                if (_noiseFloorRms < 40.0) _noiseFloorRms = 40.0;
                if (_noiseFloorRms > 700.0) _noiseFloorRms = 700.0;
            }

            // Adaptive vocal threshold calibrated for Realtek array & headset mics
            double mult = Math.Max(1.35, _settings.vad_speech_multiplier);
            double dynamicThresh = Math.Max(110.0, _noiseFloorRms * mult);
            if (_settings.min_rms_threshold > 0 && _noiseFloorRms > 130.0)
            {
                dynamicThresh = Math.Max(dynamicThresh, Math.Min(_settings.min_rms_threshold, _noiseFloorRms * 1.55));
            }

            bool hasVoice = (rmsRaw >= dynamicThresh) || (peakSample >= Math.Max(1550, (int)(_noiseFloorRms * 6.2)));

            byte[] finishedActiveWav = null;
            bool activeWasWake = false;
            byte[] finishedStandbyWav = null;

            lock (_recLock)
            {
                // Hard acoustic echo shield: never record Frida's own speaker voice, active turn execution, or room decay into pre-roll!
                if (_isSpeaking || _isBusyProcessingTurn || _paused || DateTime.UtcNow < _ignoreMicUntilUtc)
                {
                    _preRollQueue.Clear();
                    _consecutiveVoiceFrames = 0;
                    return;
                }

                _preRollQueue.Enqueue(pcmBytes);
                while (_preRollQueue.Count > PreRollChunks)
                {
                    _preRollQueue.Dequeue();
                }

                // 1. Follow-up conversational window (orb already visible, no "Frida" prefix needed!)
                if (!_capturingActiveCommand && _followUpWindowActive)
                {
                    float followNorm = Math.Min(1.2f, Math.Max(0f, (float)(rmsRaw - _noiseFloorRms * 0.8)) / 1400f);
                    _orb.PushAudioSpectrum(followNorm, bands16);

                    if (hasVoice)
                    {
                        _consecutiveVoiceFrames++;
                        if (_consecutiveVoiceFrames >= 3) // ~190ms sustained speech prevents transient clicks from false-triggering
                        {
                            _followUpWindowActive = false;
                            _capturingActiveCommand = true;
                            _includeWakeStrip = false;
                            _heardVoiceInActive = true;
                            _activeStartedUtc = DateTime.UtcNow;
                            _activeLastVoiceUtc = DateTime.UtcNow;
                            _activeCapturePcm = new MemoryStream();
                            foreach (byte[] pre in _preRollQueue)
                            {
                                _activeCapturePcm.Write(pre, 0, pre.Length);
                            }
                            _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · ESCUTANDO", "Captando sua resposta...");
                        }
                    }
                    else
                    {
                        _consecutiveVoiceFrames = 0;
                    }
                }

                // 2. Active Foreground Command Capture (F4, Tray click, WinRT wake, or Follow-Up)
                if (_capturingActiveCommand && _activeCapturePcm != null)
                {
                    _activeCapturePcm.Write(pcmBytes, 0, pcmBytes.Length);

                    float normRms = Math.Min(1.35f, Math.Max(0.05f, (float)(rmsRaw - _noiseFloorRms * 0.7)) / 1300f);
                    _orb.PushAudioSpectrum(normRms, bands16);

                    DateTime now = DateTime.UtcNow;
                    if (hasVoice)
                    {
                        if (!_heardVoiceInActive)
                        {
                            _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · OUVINDO SUA VOZ", "Captando comando...");
                        }
                        _heardVoiceInActive = true;
                        _activeLastVoiceUtc = now;
                    }

                    double totalSec = (now - _activeStartedUtc).TotalSeconds;
                    double silenceSec = (now - _activeLastVoiceUtc).TotalSeconds;

                    bool endBySilence = _heardVoiceInActive && totalSec >= 0.95 && silenceSec >= 0.95;
                    bool endByNoSpeech = !_heardVoiceInActive && totalSec >= 6.0;
                    bool endByMaxLen = totalSec >= 32.0;

                    if (endBySilence || endByNoSpeech || endByMaxLen)
                    {
                        _capturingActiveCommand = false;
                        activeWasWake = _includeWakeStrip;
                        if (_heardVoiceInActive)
                        {
                            finishedActiveWav = BuildWavFromPcm16Mono(_activeCapturePcm.ToArray(), SampleRate);
                        }
                        _activeCapturePcm.Dispose();
                        _activeCapturePcm = null;

                        if (finishedActiveWav == null)
                        {
                            _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                        }
                    }
                }
                // 3. Always-On Direct Physical Mic Wake-Word Detector (catches "Frida" even when Steam hijacks WAVE_MAPPER!)
                else if (!_isSpeaking && !_paused && !_followUpWindowActive && !_verifyingStandbyWake && DateTime.UtcNow >= _ignoreMicUntilUtc)
                {
                    if (!_capturingStandbyCandidate)
                    {
                        if (hasVoice)
                        {
                            _consecutiveVoiceFrames++;
                            if (_consecutiveVoiceFrames >= 2) // ~125ms sustained voice starts candidate capture with 1.4s pre-roll!
                            {
                                _capturingStandbyCandidate = true;
                                _standbyVoiceFrames = _consecutiveVoiceFrames;
                                _standbyStartedUtc = DateTime.UtcNow;
                                _standbyLastVoiceUtc = DateTime.UtcNow;
                                _standbyCandidatePcm = new MemoryStream();
                                foreach (byte[] pre in _preRollQueue)
                                {
                                    _standbyCandidatePcm.Write(pre, 0, pre.Length);
                                }
                            }
                        }
                        else
                        {
                            _consecutiveVoiceFrames = 0;
                        }
                    }
                    else if (_standbyCandidatePcm != null)
                    {
                        _standbyCandidatePcm.Write(pcmBytes, 0, pcmBytes.Length);
                        DateTime now = DateTime.UtcNow;
                        if (hasVoice)
                        {
                            _standbyVoiceFrames++;
                            _standbyLastVoiceUtc = now;
                        }

                        double totalSec = (now - _standbyStartedUtc).TotalSeconds;
                        double silenceSec = (now - _standbyLastVoiceUtc).TotalSeconds;

                        // Fast wake silence (0.78s pause after "Frida" or "Frida, faz X") or max 9.5s speech segment
                        if (silenceSec >= 0.78 || totalSec >= 9.5)
                        {
                            _capturingStandbyCandidate = false;
                            _consecutiveVoiceFrames = 0;
                            if (_standbyVoiceFrames >= 3) // At least ~190ms of real vocal energy (captures quick calls)
                            {
                                finishedStandbyWav = BuildWavFromPcm16Mono(_standbyCandidatePcm.ToArray(), SampleRate);
                            }
                            _standbyCandidatePcm.Dispose();
                            _standbyCandidatePcm = null;
                        }
                    }
                }
            }

            if (finishedActiveWav != null)
            {
                Task.Run(async delegate { await TranscribeForegroundAndDispatchAsync(finishedActiveWav, activeWasWake); });
            }
            else if (finishedStandbyWav != null)
            {
                Task.Run(async delegate { await VerifyStandbyWakeUtteranceAsync(finishedStandbyWav); });
            }
        }

        private static bool IsHumanVocalFormantCandidate(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length <= 1024) return false;
            int activeWindows = 0;
            int speechLikeWindows = 0;
            int winBytes = 640; // 20ms at 16kHz 16-bit

            for (int offset = 44; offset + winBytes <= wavBytes.Length; offset += winBytes)
            {
                double sumSq = 0;
                int crossings = 0;
                short prev = 0;
                int samples = winBytes / 2;

                for (int b = 0; b < winBytes; b += 2)
                {
                    short s = (short)(wavBytes[offset + b] | (wavBytes[offset + b + 1] << 8));
                    sumSq += (double)s * s;
                    if (b > 0 && ((s >= 0 && prev < 0) || (s < 0 && prev >= 0)))
                    {
                        crossings++;
                    }
                    prev = s;
                }

                double rms = Math.Sqrt(sumSq / samples);
                if (rms >= 100.0)
                {
                    activeWindows++;
                    double zcr = crossings / (double)samples;
                    // Human vocal fundamentals + sibilants ("F-ri-da") fall cleanly inside 0.020..0.44 ZCR at 16kHz
                    if (zcr >= 0.020 && zcr <= 0.44)
                    {
                        speechLikeWindows++;
                    }
                }
            }

            return activeWindows >= 4 && speechLikeWindows >= (activeWindows / 2);
        }

        private async Task VerifyStandbyWakeUtteranceAsync(byte[] wavBytes)
        {
            if (_verifyingStandbyWake || _capturingActiveCommand || _isSpeaking || _paused) return;
            if (DateTime.UtcNow < _networkOutageBackoffUntilUtc)
            {
                _ignoreMicUntilUtc = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            if (!IsHumanVocalFormantCandidate(wavBytes)) return;
            // Chamada curta ("Frida!", ~1 s) nunca e barrada aqui: a transcricao confere a palavra "Frida".
            // So falas longas continuas (conversa de terceiros / TV ao fundo) passam pelo filtro antes de gastar cota.
            int voicedFrames;
            SpeakerVoiceProfile.ComputeEmbedding(wavBytes, 44, wavBytes.Length - 44, out voicedFrames); // quadros de 10 ms com fala
            if (voicedFrames >= 320 && !PassesSpeakerLock(wavBytes, "espera"))
            {
                _ignoreMicUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
                return; // outra voz/som longo: nem chega a gastar cota de transcricao
            }

            _verifyingStandbyWake = true;
            try
            {
                string transcript = await CallGeminiTranscribeAsync(wavBytes);
                transcript = (transcript ?? "").Trim();
                if (string.IsNullOrEmpty(transcript))
                {
                    _ignoreMicUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
                    return;
                }

                if (ContainsWakeWordPhonetic(transcript))
                {
                    // Woke up by voice directly on physical mic! Show Blue Hologram immediately!
                    string commandPart = StripLeadingWakeWord(transcript);
                    if (string.IsNullOrEmpty(commandPart))
                    {
                        _orb.SetActivityState(OrbActivityState.Thinking, "FRIDA · ATIVADA", "Oi! Estou aqui...");
                        if (OnUserTranscriptReady != null)
                        {
                            OnUserTranscriptReady("__WAKE_ONLY__", true);
                        }
                    }
                    else
                    {
                        _orb.SetActivityState(OrbActivityState.Thinking, "FRIDA · COMANDO RECEBIDO", "Você: " + commandPart);
                        if (OnUserTranscriptReady != null)
                        {
                            OnUserTranscriptReady(commandPart, true);
                        }
                    }
                }
                else
                {
                    // Brief cooldown so non-Frida background room conversation doesn't hammer Free Tier quota
                    _ignoreMicUntilUtc = DateTime.UtcNow.AddMilliseconds(1300);
                }
            }
            catch { }
            finally
            {
                _verifyingStandbyWake = false;
            }
        }

        private static bool ContainsWakeWordPhonetic(string transcript)
        {
            if (string.IsNullOrEmpty(transcript)) return false;
            // Matches "Frida", "Fridda", "Freeda", and common fast-speech pt-BR phonetic close transcriptions at utterance start ("Frita", "Ferida", "Querida Frida")
            if (Regex.IsMatch(transcript, @"\b(frida|fridda|freeda|fryda)\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
            if (Regex.IsMatch(transcript.Trim(), @"^(oi\s+|ei\s+|ol[aá]\s+|ok\s+|fala\s+)?(frita|ferida)\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static string StripLeadingWakeWord(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = Regex.Replace(
                text.Trim(),
                @"^[\s,.:;!?-]*(oi|ei|ol[aá]|ok|fala|escuta|ativar|querida)?[\s,.:;!?-]*(frida|fridda|freeda|fryda|frita|ferida)[\s,.:;!?-]*",
                "",
                RegexOptions.IgnoreCase).Trim();
            return s;
        }

        private void CheckFollowUpTimeout()
        {
            if (_followUpWindowActive && !_capturingActiveCommand && !_isSpeaking)
            {
                if (DateTime.UtcNow >= _followUpDeadlineUtc)
                {
                    _followUpWindowActive = false;
                    if (OnFollowUpWindowExpired != null)
                    {
                        OnFollowUpWindowExpired();
                    }
                    else
                    {
                        _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                    }
                }
            }
        }

        private async Task TranscribeForegroundAndDispatchAsync(byte[] wavBytes, bool stripWakePrefix)
        {
            if (!stripWakePrefix && !IsHumanVocalFormantCandidate(wavBytes))
            {
                _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                return;
            }

            _orb.SetActivityState(OrbActivityState.Thinking, "FRIDA · PROCESSANDO VOZ", "Transcrevendo sua fala...");

            string transcript = "";
            try
            {
                transcript = await CallGeminiTranscribeAsync(wavBytes);
            }
            catch
            {
                transcript = "";
            }

            transcript = (transcript ?? "").Trim();

            if (string.IsNullOrEmpty(transcript))
            {
                if (stripWakePrefix)
                {
                    if (OnUserTranscriptReady != null) OnUserTranscriptReady("__WAKE_ONLY__", true);
                }
                else
                {
                    _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                }
                return;
            }

            string cleaned = StripLeadingWakeWord(transcript);
            if (string.IsNullOrEmpty(cleaned))
            {
                if (OnUserTranscriptReady != null) OnUserTranscriptReady("__WAKE_ONLY__", true);
                return;
            }

            if (OnUserTranscriptReady != null)
            {
                OnUserTranscriptReady(cleaned, stripWakePrefix);
            }
        }

        private static string FilterWhisperSilenceHallucination(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = raw.Trim();
            if (t.Length <= 1) return "";
            if (Regex.IsMatch(t, @"^(legendas|subtitles|amara\.org|obrigad[oa] por assistir|inscreva-se|deixe seu like|thank you for watching|you|♪+|\[.*\]|\(.*\)|\.+)$", RegexOptions.IgnoreCase))
            {
                return "";
            }
            return t;
        }

        private async Task<string> CallOpenAiWhisperMultipartAsync(string endpointUrl, string bearerKey, string modelId, string bucketTag, byte[] wavBytes)
        {
            if (string.IsNullOrEmpty(bearerKey) || !ModelQuotaGuard.IsAvailable(bucketTag, modelId)) return null;

            bool isGroq = endpointUrl.IndexOf("groq.com", StringComparison.OrdinalIgnoreCase) >= 0;
            System.Diagnostics.Stopwatch swStt = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (MultipartFormDataContent form = new MultipartFormDataContent())
                {
                    ByteArrayContent audioContent = new ByteArrayContent(wavBytes);
                    audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    form.Add(audioContent, "file", "speech.wav");
                    form.Add(new StringContent(modelId), "model");
                    form.Add(new StringContent("pt"), "language");
                    form.Add(new StringContent(isGroq ? "verbose_json" : "json"), "response_format");
                    if (isGroq)
                    {
                        form.Add(new StringContent("0"), "temperature");
                        // NOTE: Never pass a multi-word 'prompt' here — Whisper echoes prompt words on breath/silence!
                    }

                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerKey);
                        req.Content = form;
                        using (HttpResponseMessage resp = await _http.SendAsync(req))
                        {
                            string body = await resp.Content.ReadAsStringAsync();
                            if (!resp.IsSuccessStatusCode)
                            {
                                ModelQuotaGuard.ReportHttpFailure(bucketTag, modelId, (int)resp.StatusCode, body);
                                BootLogger.Log("STT [" + modelId + "] HTTP " + (int)resp.StatusCode + ": " + (body != null && body.Length > 180 ? body.Substring(0, 180) : body));
                                return null;
                            }

                            Dictionary<string, object> parsed = _json.Deserialize<Dictionary<string, object>>(body);
                            if (parsed == null) return "";

                            // Enforce Whisper acoustic confidence gating on verbose_json segments to block room-noise hallucinations
                            if (isGroq && parsed.ContainsKey("segments") && parsed["segments"] is ArrayList)
                            {
                                ArrayList segs = (ArrayList)parsed["segments"];
                                if (segs.Count == 0) return "";

                                double sumNoSpeech = 0.0;
                                double sumLogProb = 0.0;
                                double maxComp = 0.0;
                                int segCount = 0;

                                for (int i = 0; i < segs.Count; i++)
                                {
                                    Dictionary<string, object> sg = segs[i] as Dictionary<string, object>;
                                    if (sg == null) continue;
                                    segCount++;
                                    if (sg.ContainsKey("no_speech_prob") && sg["no_speech_prob"] != null)
                                        sumNoSpeech += Convert.ToDouble(sg["no_speech_prob"], System.Globalization.CultureInfo.InvariantCulture);
                                    if (sg.ContainsKey("avg_logprob") && sg["avg_logprob"] != null)
                                        sumLogProb += Convert.ToDouble(sg["avg_logprob"], System.Globalization.CultureInfo.InvariantCulture);
                                    if (sg.ContainsKey("compression_ratio") && sg["compression_ratio"] != null)
                                    {
                                        double cr = Convert.ToDouble(sg["compression_ratio"], System.Globalization.CultureInfo.InvariantCulture);
                                        if (cr > maxComp) maxComp = cr;
                                    }
                                }

                                if (segCount > 0)
                                {
                                    double avgNoSpeech = sumNoSpeech / segCount;
                                    double avgLogProb = sumLogProb / segCount;
                                    if (avgNoSpeech >= 0.62 || avgLogProb <= -1.10 || maxComp >= 2.40)
                                    {
                                        return "";
                                    }
                                }
                            }

                            if (parsed.ContainsKey("text") && parsed["text"] != null)
                            {
                                swStt.Stop();
                                ModelQuotaGuard.RecordRequestSuccess(bucketTag, modelId, swStt.ElapsedMilliseconds);
                                return FilterWhisperSilenceHallucination(parsed["text"].ToString());
                            }
                            return "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("STT [" + modelId + "] Excecao: " + ex.Message);
                if (IsNetworkOutageException(ex))
                {
                    _networkOutageBackoffUntilUtc = DateTime.UtcNow.AddSeconds(12);
                }
                return null;
            }
        }

        private async Task<string> CallGeminiTranscribeAsync(byte[] wavBytes)
        {
            if (DateTime.UtcNow < _networkOutageBackoffUntilUtc)
            {
                return "";
            }
            // 1. Priority #1: Groq LPU Whisper Large V3 Turbo (~200ms latency, 2,000 requests/day FREE!)
            string groqKey = (_settings.groq_api_key ?? "").Trim();
            if (!string.IsNullOrEmpty(groqKey))
            {
                string groqTxt = await CallOpenAiWhisperMultipartAsync(
                    "https://api.groq.com/openai/v1/audio/transcriptions",
                    groqKey,
                    "whisper-large-v3-turbo",
                    "stt_groq",
                    wavBytes);
                if (groqTxt != null) return groqTxt;
            }

            // 2. Priority #2: SiliconFlow Chinese FunAudio SenseVoiceSmall (100% FREE Chinese multilingual ASR!)
            string sfKey = (_settings.siliconflow_api_key ?? "").Trim();
            if (!string.IsNullOrEmpty(sfKey))
            {
                string sfTxt = await CallOpenAiWhisperMultipartAsync(
                    "https://api.siliconflow.com/v1/audio/transcriptions",
                    sfKey,
                    "FunAudioLLM/SenseVoiceSmall",
                    "stt_sf",
                    wavBytes);
                if (sfTxt != null) return sfTxt;
            }

            // 3. Fallback: Gemini Audio Cascade
            string apiKey = (_settings.api_key ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey)) return "";

            string[] rawCandidates = new string[]
            {
                "gemini-flash-lite-latest",
                "gemini-3.5-flash-lite",
                "gemini-3-flash-preview",
                _settings.transcribe_model,
                "gemini-3.5-transcribe"
            };

            List<string> sttModels = new List<string>();
            for (int i = 0; i < rawCandidates.Length; i++)
            {
                string m = (rawCandidates[i] ?? "").Trim();
                if (!string.IsNullOrEmpty(m) && !sttModels.Contains(m))
                {
                    sttModels.Add(m);
                }
            }

            string b64Audio = Convert.ToBase64String(wavBytes);

            for (int mIdx = 0; mIdx < sttModels.Count; mIdx++)
            {
                string modelName = sttModels[mIdx];
                if (!ModelQuotaGuard.IsAvailable("stt", modelName)) continue;

                try
                {
                    Dictionary<string, object> payload = new Dictionary<string, object>
                    {
                        { "model", modelName },
                        { "store", false },
                        { "input", new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", "Transcreva apenas as palavras exatas faladas em portugues do Brasil, sem comentarios extras. Nota de vocabulario: o nome da assistente chamada pelo usuario e 'Frida'. Se houver apenas ruido de fundo sem palavras humanas claras, retorne vazio." }
                                },
                                new Dictionary<string, object>
                                {
                                    { "type", "audio" },
                                    { "data", b64Audio },
                                    { "mime_type", "audio/wav" }
                                }
                            }
                        }
                    };

                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/interactions"))
                    {
                        req.Headers.Add("x-goog-api-key", apiKey);
                        req.Content = new StringContent(_json.Serialize(payload), Encoding.UTF8, "application/json");
                        using (HttpResponseMessage resp = await _http.SendAsync(req))
                        {
                            string body = await resp.Content.ReadAsStringAsync();
                            if (!resp.IsSuccessStatusCode)
                            {
                                ModelQuotaGuard.ReportHttpFailure("stt", modelName, (int)resp.StatusCode, body);
                                BootLogger.Log("STT [" + modelName + "] HTTP " + (int)resp.StatusCode + ": " + (body != null && body.Length > 180 ? body.Substring(0, 180) : body));
                                continue;
                            }

                            Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(body);
                            if (root != null && root.ContainsKey("steps") && root["steps"] is ArrayList)
                            {
                                ArrayList steps = (ArrayList)root["steps"];
                                for (int i = steps.Count - 1; i >= 0; i--)
                                {
                                    Dictionary<string, object> st = steps[i] as Dictionary<string, object>;
                                    string stType = (st != null && st.ContainsKey("type") && st["type"] != null) ? st["type"].ToString() : "";
                                    if (stType == "thought") continue;

                                    if (st != null && st.ContainsKey("content") && st["content"] is ArrayList)
                                    {
                                        ArrayList carr = (ArrayList)st["content"];
                                        StringBuilder sb = new StringBuilder();
                                        foreach (object item in carr)
                                        {
                                            Dictionary<string, object> d = item as Dictionary<string, object>;
                                            if (d != null && d.ContainsKey("text") && d["text"] != null)
                                            {
                                                sb.Append(d["text"].ToString());
                                            }
                                        }
                                        string recognized = sb.ToString().Trim();
                                        ModelQuotaGuard.RecordRequestSuccess("stt", modelName, 300.0);
                                        return recognized;
                                    }
                                }
                            }
                            return "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    BootLogger.Log("STT [" + modelName + "] Excecao: " + ex.Message);
                    if (IsNetworkOutageException(ex))
                    {
                        _networkOutageBackoffUntilUtc = DateTime.UtcNow.AddSeconds(12);
                        break;
                    }
                }
            }

            return "";
        }

        public async Task<byte[]> PreSynthesizeVoiceWavAsync(string rawText)
        {
            string cleanSpeech = PrepareNaturalSpokenText(rawText);
            if (string.IsNullOrEmpty(cleanSpeech)) return null;
            try
            {
                return await SynthesizeFemaleVoiceWavAsync(cleanSpeech);
            }
            catch (Exception ex)
            {
                BootLogger.Log("PreSynthesizeVoiceWavAsync Excecao: " + ex.Message);
                return null;
            }
        }

        public Task PlayPreSynthesizedWavAsync(byte[] wavBytes, string displaySubtitle)
        {
            return Task.Run(delegate
            {
                if (wavBytes == null || wavBytes.Length <= 44) return;

                _isSpeaking = true;
                _cancelSpeechRequested = false;
                _followUpWindowActive = false;

                lock (_recLock)
                {
                    _preRollQueue.Clear();
                    _consecutiveVoiceFrames = 0;
                    _capturingActiveCommand = false;
                    if (_activeCapturePcm != null)
                    {
                        _activeCapturePcm.Dispose();
                        _activeCapturePcm = null;
                    }
                    _capturingStandbyCandidate = false;
                    if (_standbyCandidatePcm != null)
                    {
                        _standbyCandidatePcm.Dispose();
                        _standbyCandidatePcm = null;
                    }
                }

                _orb.SetActivityState(OrbActivityState.Speaking, "FRIDA", displaySubtitle ?? "");

                try
                {
                    if (!_cancelSpeechRequested)
                    {
                        PlayWavSynchronizedWithHologram(wavBytes);
                    }
                }
                catch (Exception ex)
                {
                    BootLogger.Log("PlayPreSynthesizedWavAsync Excecao: " + ex.Message);
                }
                finally
                {
                    _ignoreMicUntilUtc = DateTime.UtcNow.AddMilliseconds(950);
                    lock (_recLock)
                    {
                        _preRollQueue.Clear();
                        _consecutiveVoiceFrames = 0;
                    }
                    _isSpeaking = false;
                }
            });
        }

        public bool HasNaturalVoiceCached(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string clean = PrepareNaturalSpokenText(text);
            string path = GetTtsDiskCachePath(_settings.female_voice, clean);
            return !string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > 1024;
        }

        public void PlayAttentionTone()
        {
            PlaySoftWakeToneAsync();
        }

        public async Task SpeakTextAsync(string rawText, string subtitleOverride, bool enterFollowUp)
        {
            await SpeakTextAsync(rawText, subtitleOverride);
            if (enterFollowUp && !_cancelSpeechRequested)
            {
                EnterFollowUpConversationWindow();
            }
        }

        public async Task SpeakTextAsync(string rawText, string subtitleOverride)
        {
            string cleanSpeech = PrepareNaturalSpokenText(rawText);
            if (string.IsNullOrEmpty(cleanSpeech)) return;

            _isSpeaking = true;
            _cancelSpeechRequested = false;
            _followUpWindowActive = false;

            lock (_recLock)
            {
                _preRollQueue.Clear();
                _consecutiveVoiceFrames = 0;
                _capturingActiveCommand = false;
                if (_activeCapturePcm != null)
                {
                    _activeCapturePcm.Dispose();
                    _activeCapturePcm = null;
                }
                _capturingStandbyCandidate = false;
                if (_standbyCandidatePcm != null)
                {
                    _standbyCandidatePcm.Dispose();
                    _standbyCandidatePcm = null;
                }
            }

            string displaySubtitle = !string.IsNullOrEmpty(subtitleOverride) ? subtitleOverride : cleanSpeech;
            _orb.SetActivityState(OrbActivityState.Speaking, "FRIDA", displaySubtitle);

            try
            {
                byte[] wavBytes = await SynthesizeFemaleVoiceWavAsync(cleanSpeech);
                if (wavBytes != null && wavBytes.Length > 44 && !_cancelSpeechRequested)
                {
                    PlayWavSynchronizedWithHologram(wavBytes);
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("SpeakTextAsync Excecao: " + ex.Message);
            }
            finally
            {
                _ignoreMicUntilUtc = DateTime.UtcNow.AddMilliseconds(950);
                lock (_recLock)
                {
                    _preRollQueue.Clear();
                    _consecutiveVoiceFrames = 0;
                }
                _isSpeaking = false;
            }
        }

        private static string GetTtsDiskCachePath(string voiceName, string textToSpeak)
        {
            try
            {
                string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tts_cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((voiceName ?? "Aoede") + "|" + textToSpeak));
                    StringBuilder hex = new StringBuilder(32);
                    for (int i = 0; i < 16; i++)
                    {
                        hex.Append(hash[i].ToString("x2"));
                    }
                    return Path.Combine(cacheDir, hex.ToString() + ".wav");
                }
            }
            catch
            {
                return null;
            }
        }

        private static string _cachedFfmpegPath = null;
        private static bool _searchedFfmpeg = false;

        private static string FindFfmpegExecutable()
        {
            if (_searchedFfmpeg) return _cachedFfmpegPath;
            _searchedFfmpeg = true;

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] dirs = pathEnv.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string candidate = Path.Combine(dirs[i].Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        _cachedFfmpegPath = candidate;
                        return _cachedFfmpegPath;
                    }
                }

                string wingetPkgs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft\\WinGet\\Packages");
                if (Directory.Exists(wingetPkgs))
                {
                    string[] found = Directory.GetFiles(wingetPkgs, "ffmpeg.exe", SearchOption.AllDirectories);
                    if (found != null && found.Length > 0)
                    {
                        _cachedFfmpegPath = found[0];
                        return _cachedFfmpegPath;
                    }
                }
            }
            catch { }

            return null;
        }

        private static List<string> SplitSpeechIntoChunks(string text, int maxLen)
        {
            List<string> chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;

            string remaining = text.Trim();
            while (remaining.Length > maxLen)
            {
                int cut = -1;
                char[] punct = new char[] { '.', '!', '?', ';', ':', ',' };
                for (int p = 0; p < punct.Length; p++)
                {
                    int idx = remaining.LastIndexOf(punct[p], maxLen);
                    if (idx > maxLen / 3 && idx > cut) cut = idx + 1;
                }
                if (cut <= 0)
                {
                    cut = remaining.LastIndexOf(' ', maxLen);
                }
                if (cut <= 0) cut = maxLen;

                string piece = remaining.Substring(0, cut).Trim();
                if (!string.IsNullOrEmpty(piece)) chunks.Add(piece);
                remaining = remaining.Substring(cut).Trim();
            }

            if (!string.IsNullOrEmpty(remaining)) chunks.Add(remaining);
            return chunks;
        }

        private async Task<byte[]> SynthesizeZeroQuotaNeuralPtBrWavAsync(string textToSpeak)
        {
            string ffmpeg = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpeg)) return null;

            try
            {
                List<string> chunks = SplitSpeechIntoChunks(textToSpeak, 185);
                if (chunks.Count == 0) return null;

                using (MemoryStream mp3Stream = new MemoryStream())
                {
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string url = "https://translate.googleapis.com/translate_tts?ie=UTF-8&tl=pt-BR&client=tw-ob&q=" + Uri.EscapeDataString(chunks[i]);
                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                            using (HttpResponseMessage resp = await _http.SendAsync(req))
                            {
                                if (!resp.IsSuccessStatusCode) return null;
                                byte[] mp3Bytes = await resp.Content.ReadAsByteArrayAsync();
                                if (mp3Bytes != null && mp3Bytes.Length > 64)
                                {
                                    mp3Stream.Write(mp3Bytes, 0, mp3Bytes.Length);
                                }
                            }
                        }
                    }

                    byte[] allMp3 = mp3Stream.ToArray();
                    if (allMp3.Length < 128) return null;

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = ffmpeg;
                    // Output raw headerless s16le PCM and build exact RIFF WAV header in C# (avoids ffmpeg pipe -1 chunkSize bug!)
                    psi.Arguments = "-loglevel quiet -i pipe:0 -f s16le -ar 24000 -ac 1 -acodec pcm_s16le pipe:1";
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    psi.RedirectStandardInput = true;
                    psi.RedirectStandardOutput = true;

                    using (Process proc = Process.Start(psi))
                    {
                        byte[] rawPcmOut = null;
                        Task readerTask = Task.Run(delegate
                        {
                            using (MemoryStream msOut = new MemoryStream())
                            {
                                proc.StandardOutput.BaseStream.CopyTo(msOut);
                                rawPcmOut = msOut.ToArray();
                            }
                        });

                        proc.StandardInput.BaseStream.Write(allMp3, 0, allMp3.Length);
                        proc.StandardInput.BaseStream.Flush();
                        proc.StandardInput.Close();

                        proc.WaitForExit(6000);
                        readerTask.Wait(2000);
                        if (rawPcmOut != null && rawPcmOut.Length > 400)
                        {
                            int evenLen = rawPcmOut.Length & ~1;
                            if (evenLen != rawPcmOut.Length)
                            {
                                byte[] trimmed = new byte[evenLen];
                                Buffer.BlockCopy(rawPcmOut, 0, trimmed, 0, evenLen);
                                rawPcmOut = trimmed;
                            }
                            return BuildWavFromPcm16Mono(rawPcmOut, 24000);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("FastNeuralTTS Excecao: " + ex.Message);
            }
            return null;
        }

        private static string _cachedEdgeTtsPath = null;
        private static bool _searchedEdgeTts = false;

        private static string FindEdgeTtsExecutable()
        {
            if (_searchedEdgeTts) return _cachedEdgeTtsPath;
            _searchedEdgeTts = true;

            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] candidates = new string[]
                {
                    Path.Combine(localApp, @"Programs\Python\Python312\Scripts\edge-tts.exe"),
                    Path.Combine(localApp, @"Programs\Python\Python311\Scripts\edge-tts.exe"),
                    Path.Combine(localApp, @"Programs\Python\Python310\Scripts\edge-tts.exe")
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i]))
                    {
                        _cachedEdgeTtsPath = candidates[i];
                        return _cachedEdgeTtsPath;
                    }
                }

                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] dirs = pathEnv.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string candidate = Path.Combine(dirs[i].Trim(), "edge-tts.exe");
                    if (File.Exists(candidate))
                    {
                        _cachedEdgeTtsPath = candidate;
                        return _cachedEdgeTtsPath;
                    }
                }
            }
            catch { }

            return null;
        }

        private async Task<byte[]> SynthesizeEdgeTtsNeuralAsync(string textToSpeak)
        {
            string edgeExe = FindEdgeTtsExecutable();
            string ffmpeg = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(edgeExe) || string.IsNullOrEmpty(ffmpeg)) return null;

            string tempDir = Path.GetTempPath();
            string tempTxt = Path.Combine(tempDir, "frida_tts_" + Guid.NewGuid().ToString("N") + ".txt");
            string tempMp3 = Path.Combine(tempDir, "frida_edge_" + Guid.NewGuid().ToString("N") + ".mp3");

            try
            {
                // Salva texto em arquivo UTF-8 para evitar qualquer problema de escape de aspas na linha de comando
                File.WriteAllText(tempTxt, textToSpeak, new UTF8Encoding(false));

                ProcessStartInfo psiEdge = new ProcessStartInfo();
                psiEdge.FileName = edgeExe;
                psiEdge.Arguments = "--voice pt-BR-FranciscaNeural -f \"" + tempTxt + "\" --write-media \"" + tempMp3 + "\"";
                psiEdge.CreateNoWindow = true;
                psiEdge.UseShellExecute = false;

                using (Process procEdge = Process.Start(psiEdge))
                {
                    bool exited = await Task.Run(delegate { return procEdge.WaitForExit(9000); });
                    if (!exited)
                    {
                        try { procEdge.Kill(); } catch { }
                        return null;
                    }
                }

                if (!File.Exists(tempMp3)) return null;
                byte[] mp3Bytes = File.ReadAllBytes(tempMp3);
                if (mp3Bytes == null || mp3Bytes.Length < 128) return null;

                // Converte MP3 para WAV PCM 24kHz 16-bit mono via ffmpeg pipe
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffmpeg;
                psi.Arguments = "-loglevel quiet -i pipe:0 -f s16le -ar 24000 -ac 1 -acodec pcm_s16le pipe:1";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;

                using (Process proc = Process.Start(psi))
                {
                    byte[] rawPcmOut = null;
                    Task readerTask = Task.Run(delegate
                    {
                        using (MemoryStream msOut = new MemoryStream())
                        {
                            proc.StandardOutput.BaseStream.CopyTo(msOut);
                            rawPcmOut = msOut.ToArray();
                        }
                    });

                    proc.StandardInput.BaseStream.Write(mp3Bytes, 0, mp3Bytes.Length);
                    proc.StandardInput.BaseStream.Flush();
                    proc.StandardInput.Close();

                    bool fExit = await Task.Run(delegate { return proc.WaitForExit(6000); });
                    await readerTask;

                    if (rawPcmOut != null && rawPcmOut.Length > 400)
                    {
                        int evenLen = rawPcmOut.Length & ~1;
                        if (evenLen != rawPcmOut.Length)
                        {
                            byte[] trimmed = new byte[evenLen];
                            Buffer.BlockCopy(rawPcmOut, 0, trimmed, 0, evenLen);
                            rawPcmOut = trimmed;
                        }
                        return BuildWavFromPcm16Mono(rawPcmOut, 24000);
                    }
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("Edge-TTS Excecao: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempTxt)) File.Delete(tempTxt); } catch { }
                try { if (File.Exists(tempMp3)) File.Delete(tempMp3); } catch { }
            }

            return null;
        }

        private static bool IsValidPlayableRiffWav(byte[] wav)
        {
            if (wav == null || wav.Length <= 100) return false;
            if (wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F') return false;
            int riffSize = BitConverter.ToInt32(wav, 4);
            return riffSize > 36;
        }

        private static byte[] EnsurePlayableWavBytes(byte[] wav)
        {
            if (wav == null || wav.Length <= 100) return null;
            if (wav[0] == (byte)'R' && wav[1] == (byte)'I' && wav[2] == (byte)'F' && wav[3] == (byte)'F')
            {
                int riffSize = BitConverter.ToInt32(wav, 4);
                if (riffSize <= 36)
                {
                    byte[] fixedRiff = BitConverter.GetBytes(wav.Length - 8);
                    Buffer.BlockCopy(fixedRiff, 0, wav, 4, 4);
                }
                int pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
                    int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                    if (chunkId == "data")
                    {
                        int actualData = wav.Length - (pos + 8);
                        if (chunkSize <= 0 || chunkSize > actualData)
                        {
                            byte[] fixedData = BitConverter.GetBytes(actualData);
                            Buffer.BlockCopy(fixedData, 0, wav, pos + 4, 4);
                        }
                        break;
                    }
                    if (chunkSize < 0) break;
                    pos += 8 + chunkSize;
                }
                return wav;
            }
            return null;
        }

        private async Task<byte[]> SynthesizeFemaleVoiceWavAsync(string textToSpeak)
        {
            string voiceName = string.IsNullOrEmpty(_settings.female_voice) ? "Aoede" : _settings.female_voice;
            string cacheTag = voiceName;
            string cacheFile = GetTtsDiskCachePath(cacheTag, textToSpeak);
            if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
            {
                try
                {
                    byte[] cached = File.ReadAllBytes(cacheFile);
                    if (IsValidPlayableRiffWav(cached)) return cached;
                    try { File.Delete(cacheFile); } catch { }
                }
                catch { }
            }

            string rawKeys = (_settings.api_key ?? "").Trim();
            string[] apiKeys = rawKeys.Split(new char[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (apiKeys.Length == 0 && !string.IsNullOrEmpty(rawKeys))
            {
                apiKeys = new string[] { rawKeys };
            }

            if (apiKeys.Length > 0)
            {
                // Prioritize fastest Aoede models verified live: gemini-2.5-flash-preview-tts (~1.5s) and gemini-3.8-flash-lite-tts (~2.3s)
                string[] rawTts = new string[]
                {
                    "gemini-2.5-flash-preview-tts",
                    "gemini-3.8-flash-lite-tts",
                    _settings.tts_model,
                    "gemini-3.1-flash-tts-preview",
                    "gemini-3.8-flash-tts",
                    "gemini-2.5-pro-preview-tts"
                };

                List<string> ttsCandidates = new List<string>();
                for (int i = 0; i < rawTts.Length; i++)
                {
                    string c = (rawTts[i] ?? "").Trim();
                    if (!string.IsNullOrEmpty(c) && !ttsCandidates.Contains(c))
                    {
                        ttsCandidates.Add(c);
                    }
                }

                for (int k = 0; k < apiKeys.Length; k++)
                {
                    string apiKey = apiKeys[k].Trim();
                    if (string.IsNullOrEmpty(apiKey)) continue;

                    for (int m = 0; m < ttsCandidates.Count; m++)
                    {
                        string ttsModel = ttsCandidates[m];
                        if (!ModelQuotaGuard.IsAvailable("tts", ttsModel)) continue;

                        try
                        {
                            // Gemini 2.5 TTS models synthesize Aoede via :generateContent (returns 24kHz 16-bit mono PCM inlineData)
                            if (ttsModel.StartsWith("gemini-2.5-", StringComparison.OrdinalIgnoreCase))
                            {
                                Dictionary<string, object> genPayload = new Dictionary<string, object>
                                {
                                    { "contents", new object[]
                                        {
                                            new Dictionary<string, object>
                                            {
                                                { "parts", new object[]
                                                    {
                                                        new Dictionary<string, object> { { "text", textToSpeak } }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    { "generationConfig", new Dictionary<string, object>
                                        {
                                            { "responseModalities", new string[] { "AUDIO" } },
                                            { "speechConfig", new Dictionary<string, object>
                                                {
                                                    { "voiceConfig", new Dictionary<string, object>
                                                        {
                                                            { "prebuiltVoiceConfig", new Dictionary<string, object>
                                                                {
                                                                    { "voiceName", voiceName }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                };

                                string url25 = "https://generativelanguage.googleapis.com/v1beta/models/" + ttsModel + ":generateContent?key=" + Uri.EscapeDataString(apiKey);
                                ModelQuotaGuard.SetActiveModel("tts", ttsModel);
                                System.Diagnostics.Stopwatch swTts25 = System.Diagnostics.Stopwatch.StartNew();
                                using (HttpRequestMessage req25 = new HttpRequestMessage(HttpMethod.Post, url25))
                                {
                                    req25.Content = new StringContent(_json.Serialize(genPayload), Encoding.UTF8, "application/json");
                                    using (HttpResponseMessage resp25 = await _http.SendAsync(req25))
                                    {
                                        swTts25.Stop();
                                        string body25 = await resp25.Content.ReadAsStringAsync();
                                        if (resp25.IsSuccessStatusCode)
                                        {
                                            ModelQuotaGuard.RecordRequestSuccess("tts", ttsModel, swTts25.ElapsedMilliseconds);
                                            byte[] wav25 = ExtractWavFromGenerateContentAudioJson(body25);
                                            if (wav25 != null && wav25.Length > 100)
                                            {
                                                BootLogger.Log("TTS Aoede OK [" + ttsModel + "] (" + wav25.Length + " bytes)");
                                                if (!string.IsNullOrEmpty(cacheFile))
                                                {
                                                    try { File.WriteAllBytes(cacheFile, wav25); } catch { }
                                                }
                                                return wav25;
                                            }
                                        }
                                        else
                                        {
                                            ModelQuotaGuard.ReportHttpFailure("tts", ttsModel, (int)resp25.StatusCode, body25);
                                            BootLogger.Log("TTS [" + ttsModel + "] HTTP " + (int)resp25.StatusCode + ": " + (body25 != null && body25.Length > 160 ? body25.Substring(0, 160) : body25));
                                        }
                                    }
                                }
                                ModelQuotaGuard.ClearActiveModel();
                                continue;
                            }

                            Dictionary<string, object> textItem = new Dictionary<string, object>
                            {
                                { "type", "text" },
                                { "text", textToSpeak }
                            };
                            if (ttsModel.IndexOf("3.1", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                textItem["annotations"] = new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "type", "speech_metadata" },
                                        { "style", "warm, natural, expressive, intelligent Brazilian Portuguese female voice, human conversational cadence" }
                                    }
                                };
                            }

                            Dictionary<string, object> payload = new Dictionary<string, object>
                            {
                                { "model", ttsModel },
                                { "store", false },
                                { "input", new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            { "type", "user_input" },
                                            { "content", new object[] { textItem } }
                                        }
                                    }
                                },
                                { "response_format", new Dictionary<string, object> { { "type", "audio" } } },
                                { "generation_config", new Dictionary<string, object>
                                    {
                                        { "speech_config", new object[]
                                            {
                                                new Dictionary<string, object> { { "voice", voiceName } }
                                            }
                                        }
                                    }
                                }
                            };

                            ModelQuotaGuard.SetActiveModel("tts", ttsModel);
                            System.Diagnostics.Stopwatch swTtsInt = System.Diagnostics.Stopwatch.StartNew();
                            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/interactions"))
                            {
                                req.Headers.Add("x-goog-api-key", apiKey);
                                req.Content = new StringContent(_json.Serialize(payload), Encoding.UTF8, "application/json");
                                using (HttpResponseMessage resp = await _http.SendAsync(req))
                                {
                                    swTtsInt.Stop();
                                    string body = await resp.Content.ReadAsStringAsync();
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        ModelQuotaGuard.RecordRequestSuccess("tts", ttsModel, swTtsInt.ElapsedMilliseconds);
                                        byte[] wav = EnsurePlayableWavBytes(ExtractWavFromInteractionJson(body));
                                        if (wav != null && wav.Length > 100)
                                        {
                                            BootLogger.Log("TTS Aoede OK [" + ttsModel + "] (" + wav.Length + " bytes)");
                                            if (!string.IsNullOrEmpty(cacheFile))
                                            {
                                                try { File.WriteAllBytes(cacheFile, wav); } catch { }
                                            }
                                            return wav;
                                        }
                                    }
                                    else
                                    {
                                        ModelQuotaGuard.ReportHttpFailure("tts", ttsModel, (int)resp.StatusCode, body);
                                        BootLogger.Log("TTS [" + ttsModel + "] HTTP " + (int)resp.StatusCode + ": " + (body != null && body.Length > 160 ? body.Substring(0, 160) : body));
                                    }
                                }
                            }
                            ModelQuotaGuard.ClearActiveModel();
                        }
                        catch (Exception ex)
                        {
                            BootLogger.Log("TTS [" + ttsModel + "] Excecao: " + ex.Message);
                        }
                    }
                }
            }

            // 2. Fallback Natural Zero-Cota: Microsoft Edge Neural (pt-BR-FranciscaNeural - voz humana natural de estudio, sem limites de cota)
            try
            {
                byte[] edgeWav = await SynthesizeEdgeTtsNeuralAsync(textToSpeak);
                if (edgeWav != null && edgeWav.Length > 100)
                {
                    BootLogger.Log("TTS Edge Neural Francisca OK (" + edgeWav.Length + " bytes) [zero-cota / natural]");
                    if (!string.IsNullOrEmpty(cacheFile))
                    {
                        try { File.WriteAllBytes(cacheFile, edgeWav); } catch { }
                    }
                    return edgeWav;
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("TTS Edge Neural Excecao: " + ex.Message);
            }

            // 3. Fallback Natural Zero-Cota: Google Translate Neural PT-BR via FFmpeg (Zero Quota, Uncapped, Instant)
            try
            {
                byte[] neuralPtBr = await SynthesizeZeroQuotaNeuralPtBrWavAsync(textToSpeak);
                if (neuralPtBr != null && neuralPtBr.Length > 100)
                {
                    BootLogger.Log("TTS Neural PT-BR Female Voice OK (" + neuralPtBr.Length + " bytes) [zero-cota / natural]");
                    if (!string.IsNullOrEmpty(cacheFile))
                    {
                        try { File.WriteAllBytes(cacheFile, neuralPtBr); } catch { }
                    }
                    if (!string.IsNullOrEmpty(cacheFile))
                    {
                        try { File.WriteAllBytes(cacheFile, neuralPtBr); } catch { }
                    }
                    return neuralPtBr;
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("TTS Neural PT-BR Excecao: " + ex.Message);
            }

            // 4. Se o usuario exige apenas voz natural humana, bloqueamos o sintetizador robotico local SAPI "Maria"
            if (_settings.natural_voice_only)
            {
                BootLogger.Log("Vozes naturais (Gemini / Edge Francisca / Neural PT-BR) esgotadas e natural_voice_only=true -> voz robotica SAPI bloqueada.");
                if (OnNaturalVoiceUnavailable != null) OnNaturalVoiceUnavailable(textToSpeak);
                return null;
            }

            // Absolute last resort when offline with zero internet connectivity
            return SynthesizeOfflineMariaFemaleWav(textToSpeak);
        }

        private byte[] ExtractWavFromInteractionJson(string json)
        {
            Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("steps") || !(root["steps"] is ArrayList)) return null;

            ArrayList steps = (ArrayList)root["steps"];
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                Dictionary<string, object> step = steps[i] as Dictionary<string, object>;
                if (step == null || !step.ContainsKey("content") || !(step["content"] is ArrayList)) continue;
                ArrayList contentArr = (ArrayList)step["content"];
                for (int j = contentArr.Count - 1; j >= 0; j--)
                {
                    Dictionary<string, object> item = contentArr[j] as Dictionary<string, object>;
                    if (item != null && item.ContainsKey("type") && item["type"] != null && item["type"].ToString() == "audio")
                    {
                        if (item.ContainsKey("data") && item["data"] != null)
                        {
                            return Convert.FromBase64String(item["data"].ToString());
                        }
                    }
                }
            }
            return null;
        }

        private byte[] ExtractWavFromGenerateContentAudioJson(string json)
        {
            Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("candidates") || !(root["candidates"] is ArrayList)) return null;

            ArrayList candidates = (ArrayList)root["candidates"];
            for (int c = 0; c < candidates.Count; c++)
            {
                Dictionary<string, object> cand = candidates[c] as Dictionary<string, object>;
                if (cand == null || !cand.ContainsKey("content")) continue;
                Dictionary<string, object> content = cand["content"] as Dictionary<string, object>;
                if (content == null || !content.ContainsKey("parts") || !(content["parts"] is ArrayList)) continue;

                ArrayList parts = (ArrayList)content["parts"];
                for (int p = 0; p < parts.Count; p++)
                {
                    Dictionary<string, object> part = parts[p] as Dictionary<string, object>;
                    if (part == null || !part.ContainsKey("inlineData")) continue;
                    Dictionary<string, object> inlineData = part["inlineData"] as Dictionary<string, object>;
                    if (inlineData == null || !inlineData.ContainsKey("data") || inlineData["data"] == null) continue;

                    byte[] rawBytes = Convert.FromBase64String(inlineData["data"].ToString());
                    if (rawBytes == null || rawBytes.Length <= 100) continue;

                    if (rawBytes.Length > 44 && rawBytes[0] == (byte)'R' && rawBytes[1] == (byte)'I' && rawBytes[2] == (byte)'F' && rawBytes[3] == (byte)'F')
                    {
                        return EnsurePlayableWavBytes(rawBytes);
                    }

                    int evenLen = rawBytes.Length & ~1;
                    if (evenLen != rawBytes.Length)
                    {
                        byte[] trimmed = new byte[evenLen];
                        Buffer.BlockCopy(rawBytes, 0, trimmed, 0, evenLen);
                        rawBytes = trimmed;
                    }
                    return BuildWavFromPcm16Mono(rawBytes, 24000);
                }
            }
            return null;
        }

        private static byte[] SynthesizeOfflineMariaFemaleWav(string text)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                using (SpeechSynthesizer synth = new SpeechSynthesizer())
                {
                    try
                    {
                        synth.SelectVoice("Microsoft Maria Desktop");
                    }
                    catch
                    {
                        synth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult, 0, new System.Globalization.CultureInfo("pt-BR"));
                    }
                    synth.Rate = 1;
                    synth.SetOutputToWaveStream(ms);
                    synth.Speak(text);
                    byte[] raw = ms.ToArray();
                    if (raw != null && raw.Length > 100)
                    {
                        BootLogger.Log("TTS Offline Maria Desktop OK (" + raw.Length + " bytes)");
                        return raw;
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("SynthesizeOfflineMariaFemaleWav Excecao: " + ex.Message);
                return null;
            }
        }

        private void PlayWavSynchronizedWithHologram(byte[] wavBytes)
        {
            int sampleRate = 24000;
            int channels = 1;
            int bitsPerSample = 16;
            int dataOffset = 44;
            int dataLength = Math.Max(0, wavBytes.Length - 44);

            ParseWavHeader(wavBytes, ref sampleRate, ref channels, ref bitsPerSample, ref dataOffset, ref dataLength);

            int bytesPerSec = Math.Max(1, sampleRate * channels * (bitsPerSample / 8));
            double totalDurationMs = (dataLength / (double)bytesPerSec) * 1000.0;

            using (MemoryStream ms = new MemoryStream(wavBytes))
            using (SoundPlayer player = new SoundPlayer(ms))
            {
                player.Load();
                Stopwatch sw = Stopwatch.StartNew();
                player.Play();
                BootLogger.Log("TTS Play iniciado: " + Math.Round(totalDurationMs) + "ms (" + wavBytes.Length + " bytes)");

                int windowBytes = Math.Max(512, (bytesPerSec / 30) & ~1);
                while (sw.Elapsed.TotalMilliseconds < totalDurationMs + 110)
                {
                    if (_cancelSpeechRequested || !_running)
                    {
                        player.Stop();
                        break;
                    }

                    double elapsedSec = sw.Elapsed.TotalSeconds;
                    int bytePos = dataOffset + ((int)(elapsedSec * bytesPerSec) & ~1);
                    if (bytePos >= dataOffset && bytePos + 64 < dataOffset + dataLength && bitsPerSample == 16)
                    {
                        int sliceLen = Math.Min(windowBytes, (dataOffset + dataLength) - bytePos);
                        float rmsRaw;
                        int pk;
                        float[] bands = ComputePcmSpectrum16(wavBytes, bytePos, sliceLen, out rmsRaw, out pk);
                        float normRms = Math.Min(1.35f, rmsRaw / 4600f);
                        _orb.PushAudioSpectrum(normRms, bands);
                    }

                    Thread.Sleep(16);
                }
            }
        }

        private static void ParseWavHeader(byte[] wav, ref int sampleRate, ref int channels, ref int bits, ref int dataOffset, ref int dataLen)
        {
            try
            {
                if (wav.Length < 44) return;
                int pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
                    int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                    if (chunkId == "fmt " && chunkSize >= 16 && pos + 24 <= wav.Length)
                    {
                        channels = BitConverter.ToUInt16(wav, pos + 10);
                        sampleRate = BitConverter.ToInt32(wav, pos + 12);
                        bits = BitConverter.ToUInt16(wav, pos + 22);
                    }
                    else if (chunkId == "data")
                    {
                        dataOffset = pos + 8;
                        int remaining = Math.Max(0, wav.Length - dataOffset);
                        dataLen = (chunkSize > 0 && chunkSize <= remaining) ? chunkSize : remaining;
                        return;
                    }
                    if (chunkSize < 0) break;
                    pos += 8 + chunkSize;
                }
            }
            catch { }
        }

        private static float[] ComputePcmSpectrum16(byte[] pcm, int offset, int byteLen, out float rmsOut, out int peakOut)
        {
            float[] bands = new float[16];
            peakOut = 0;
            int samples = byteLen / 2;
            if (samples <= 4)
            {
                rmsOut = 0f;
                return bands;
            }

            double sumSq = 0.0;
            double prev1 = 0, prev2 = 0, prev4 = 0, prev8 = 0;
            double eLow = 0, eMidLow = 0, eMidHigh = 0, eHigh = 0;

            int limit = offset + (samples * 2);
            int idx = 0;
            for (int b = offset; b + 1 < limit; b += 2, idx++)
            {
                short s = (short)(pcm[b] | (pcm[b + 1] << 8));
                int absVal = Math.Abs((int)s);
                if (absVal > peakOut) peakOut = absVal;

                double v = s;
                sumSq += v * v;

                eHigh += Math.Abs(v - prev1);
                eMidHigh += Math.Abs(v - prev2);
                eMidLow += Math.Abs(v - prev4);
                eLow += Math.Abs(v + prev8) * 0.5;

                if ((idx & 7) == 0) prev8 = v;
                if ((idx & 3) == 0) prev4 = v;
                if ((idx & 1) == 0) prev2 = v;
                prev1 = v;
            }

            rmsOut = (float)Math.Sqrt(sumSq / samples);
            float inv = 1f / (samples * 3400f);

            float b0 = (float)(eLow * inv);
            float b1 = (float)(eMidLow * inv);
            float b2 = (float)(eMidHigh * inv);
            float b3 = (float)(eHigh * inv);

            for (int i = 0; i < 16; i++)
            {
                float t = i / 15f;
                float val;
                if (t < 0.33f) val = Lerp(b0, b1, t / 0.33f);
                else if (t < 0.66f) val = Lerp(b1, b2, (t - 0.33f) / 0.33f);
                else val = Lerp(b2, b3, (t - 0.66f) / 0.34f);

                float mod = 0.78f + 0.35f * (float)Math.Sin(i * 1.35 + rmsOut * 0.002);
                bands[i] = Math.Min(1.4f, val * mod);
            }

            return bands;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static string TruncateMicName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Mic";
            return s.Length <= 22 ? s : s.Substring(0, 22);
        }

        private static string PrepareNaturalSpokenText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = Regex.Replace(text, @"<tool_call>[\s\S]*?</tool_call>", "");
            s = Regex.Replace(s, @"<tool_call>[\s\S]*$", "");
            s = Regex.Replace(s, @"<function=[\s\S]*?</function>", "");
            s = Regex.Replace(s, @"<function=[\s\S]*$", "");
            s = Regex.Replace(s, @"<parameter=[\s\S]*?</parameter>", "");
            s = Regex.Replace(s, @"<thought>[\s\S]*?</thought>", "");
            s = Regex.Replace(s, @"<thought>[\s\S]*$", "");
            s = Regex.Replace(s, @"<think>[\s\S]*?</think>", "");
            s = Regex.Replace(s, @"<think>[\s\S]*$", "");
            s = Regex.Replace(s, @"```[\s\S]*?```", " O código completo já foi salvo para você. ");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "");
            s = Regex.Replace(s, @"https?://\S+", "no link aberto");
            s = Regex.Replace(s, @"^\s*[#>-]+\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length > 750)
            {
                s = s.Substring(0, 750) + "...";
            }
            return s;
        }

        private static void PlaySoftWakeToneAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int sr = 24000;
                    int samples = (int)(sr * 0.095);
                    byte[] pcm = new byte[samples * 2];
                    for (int i = 0; i < samples; i++)
                    {
                        double t = i / (double)sr;
                        double env = Math.Sin(Math.PI * (i / (double)samples));
                        double wave = 0.55 * Math.Sin(2.0 * Math.PI * 740.0 * t)
                                    + 0.45 * Math.Sin(2.0 * Math.PI * 1110.0 * t);
                        short val = (short)(wave * env * 5200);
                        pcm[i * 2] = (byte)(val & 0xFF);
                        pcm[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
                    }
                    byte[] wav = BuildWavFromPcm16Mono(pcm, sr);
                    using (MemoryStream ms = new MemoryStream(wav))
                    using (SoundPlayer sp = new SoundPlayer(ms))
                    {
                        sp.PlaySync();
                    }
                }
                catch { }
            });
        }

        private static byte[] BuildWavFromPcm16Mono(byte[] pcmData, int sampleRate)
        {
            using (MemoryStream ms = new MemoryStream(44 + pcmData.Length))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcmData.Length);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((ushort)1);
                bw.Write((ushort)1);
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((ushort)2);
                bw.Write((ushort)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(pcmData.Length);
                bw.Write(pcmData);
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            _running = false;
            _cancelSpeechRequested = true;
            if (_waveEvent != null)
            {
                try { _waveEvent.Set(); } catch { }
            }
            if (_winRtWakeRecognizer != null)
            {
                try
                {
                    _winRtWakeRecognizer.HypothesisGenerated -= OnWinRtHypothesis;
                    _winRtWakeRecognizer.ContinuousRecognitionSession.ResultGenerated -= OnWinRtResult;
                    _winRtWakeRecognizer.ContinuousRecognitionSession.Completed -= OnWinRtSessionCompleted;
                    _winRtWakeRecognizer.Dispose();
                }
                catch { }
                _winRtWakeRecognizer = null;
            }
        }
    }
}
