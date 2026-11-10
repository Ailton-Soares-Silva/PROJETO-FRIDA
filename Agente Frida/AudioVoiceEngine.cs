// language: C# 5 (.NET Framework 4.8), file: AudioVoiceEngine.cs, target: Windows 10/11 x64
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private LocalWakeDetector _localWake;
        private DateTime _lastWakeAcceptedUtc = DateTime.MinValue;
        private DateTime _lastControlUtc=DateTime.MinValue;
        private readonly Queue<byte[]> _controlPreRoll=new Queue<byte[]>();
        private readonly object _controlAudioSync=new object();
        private readonly SemaphoreSlim _speechGate=new SemaphoreSlim(1,1);
        private readonly object _speechSync=new object();
        private CancellationTokenSource _speechCancellation;
        private readonly object _inputSync=new object();
        private CancellationTokenSource _inputCancellation;
        private int _activeCaptureEpoch;
        private SoundPlayer _activePlayer;
        private int _speechEpoch,_inputEpoch;
        private volatile bool _offRequested=true;
        public bool IsOff {get{return _offRequested;}}
        public event Action<FridaControlCommand> OnPriorityControl;

        private DateTime _wakeVoiceGuardUntilUtc = DateTime.MinValue;
        private Thread _micThread;
        private AutoResetEvent _waveEvent;
        private volatile bool _running = false;
        private volatile bool _restartMicLoop = false;
        private volatile bool _paused = false;
        private volatile bool _isSpeaking = false;
        private volatile bool _isBusyProcessingTurn = false;
        private volatile bool _cancelSpeechRequested = false;
        private volatile bool _followUpWindowActive = false;

        private uint _selectedMicId = uint.MaxValue;
        private string _selectedMicName = "Padrao";

        private DateTime _followUpDeadlineUtc = DateTime.MinValue;
        private DateTime _ignoreMicUntilUtc = DateTime.MinValue;
        private DateTime _lastVerifiedUserVoiceUtc = DateTime.MinValue;
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
        private FridaCaptureMetrics _captureMetrics = new FridaCaptureMetrics();
        private bool _heardVoiceInActive = false;
        private bool _heardVoiceAfterWake;
        private int _wakePostVoiceFrames;
        private DateTime _activeStartedUtc;
        private DateTime _activeLastVoiceUtc;
        private MemoryStream _activeCapturePcm;

        // Always-on background wake-word VAD capture directly on the physical microphone (immune to Steam Streaming Mic hijacking WAVE_MAPPER!)

        private double _noiseFloorRms = 155.0;
        private bool _noiseCalibrated = false;
        private int _calibFrames = 0;
        private double _calibSum = 0.0;
        private int _consecutiveVoiceFrames = 0;

        public event Action<string, bool> OnUserTranscriptReady;
        public event Action OnFollowUpWindowExpired;
        public event Action<string> OnAudioUnavailable;
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
        private SileroVoiceActivity _liveVad; // usado so na thread do microfone
        private VoiceEnrollmentSession _enrollment;
        private DateTime _lastMicChunkUtc=DateTime.MinValue;
        private DateTime _lastCaptureErrorUtc=DateTime.MinValue;
        public string LastCaptureError {get;private set;}
        private int _foregroundTranscribing;
        private double _microphoneRms;
        public double MicrophoneRms {get{return _microphoneRms;}}
        public event Action OnVoiceTrainingRequired;
        public VoiceEnrollmentSession Enrollment {get{return _enrollment;}}
        public string VoiceProtectionStatus
        {
            get
            {
                if(!_settings.speaker_lock_enabled)return "Identificacao de voz desligada; o detector de fala ignora ruidos, mas aceita qualquer voz.";
                if(!SpeakerVoiceProfile.EngineAvailable)return "Verificador de voz indisponivel: "+SpeakerVoiceProfile.EngineError;
                if(!_voiceProfile.IsReady)return _voiceProfile.HasLegacyProfile?"O perfil antigo foi desativado. Cadastre sua voz (5 frases curtas) para liberar a escuta.":"Cadastre sua voz (5 frases curtas). A escuta fica bloqueada ate la.";
                return "Voz cadastrada. Os comandos passam pela verificacao de locutor; vozes sobrepostas podem exigir repeticao.";
            }
        }
        private bool HandsFreeProfileReady {get{return !_settings.speaker_lock_enabled || _voiceProfile.IsReady;}}

        public SpeakerVoiceProfile VoiceProfile
        {
            get { return _voiceProfile; }
        }

        public bool IsEnrollingVoice
        {
            get { return _enrollment != null && _enrollment.IsActive; }
        }

        public VoiceEnrollmentSession BeginVoiceCapture(VoiceCaptureMode mode)
        {
            if(!_running || (DateTime.UtcNow-_lastMicChunkUtc).TotalSeconds>2.5)throw new InvalidOperationException("O microfone nao esta enviando audio. Use Reiniciar audio ou selecione um microfone conectado. "+(LastCaptureError ?? ""));
            if(IsEnrollingVoice)throw new InvalidOperationException("Ja existe uma captura de voz em andamento.");
            if(_isSpeaking || _isBusyProcessingTurn || Interlocked.CompareExchange(ref _foregroundTranscribing,0,0)!=0)throw new InvalidOperationException("Aguarde o atendimento ou a fala da Frida terminar antes de gravar.");
            if(!SpeakerVoiceProfile.EngineAvailable)throw new InvalidOperationException("Verificador de voz indisponivel: "+SpeakerVoiceProfile.EngineError);
            if(mode!=VoiceCaptureMode.EnrollPhrase && !_voiceProfile.IsReady)throw new InvalidOperationException("Cadastre sua voz antes de testar.");
            bool validation=mode!=VoiceCaptureMode.EnrollPhrase;
            lock(_recLock)
            {
                _offRequested=true;Interlocked.Increment(ref _inputEpoch);
                _capturingActiveCommand=false;_followUpWindowActive=false;_preRollQueue.Clear();_consecutiveVoiceFrames=0;
                if(_activeCapturePcm!=null){_activeCapturePcm.Dispose();_activeCapturePcm=null;}
                lock(_controlAudioSync)_controlPreRoll.Clear();
                if(_localWake!=null)_localWake.Reset();
                _enrollment=new VoiceEnrollmentSession(_voiceProfile,_selectedMicName,mode,tolerance:_settings.speaker_lock_tolerance);
            }
            var session=_enrollment;
            session.Completion.ContinueWith(t=>
            {
                if(!object.ReferenceEquals(_enrollment,session))return;
                _ignoreMicUntilUtc=DateTime.UtcNow.AddSeconds(1);
                lock(_recLock){_preRollQueue.Clear();_consecutiveVoiceFrames=0;}
                if(_localWake!=null)_localWake.Reset();
                if(_orb!=null)_orb.SetActivityState(OrbActivityState.Hidden,"FRIDA","");
            });
            if(_orb!=null)_orb.SetActivityState(OrbActivityState.Listening,"FRIDA · TESTE LOCAL",validation?"Conferindo sua voz sem transcricao...":"Treinando sua voz sem enviar audio a API...");
            return session;
        }

        public void CancelVoiceEnrollment()
        {
            var session=_enrollment;if(session!=null)session.Cancel();
        }
        public void RestartTrainingMicrophone(){SwitchMicrophoneDevice(_selectedMicId,_selectedMicName);}
        public List<MicDeviceInfo> ListTrainingMicrophones()
        {
            var devices=new List<MicDeviceInfo>();uint size=(uint)Marshal.SizeOf(typeof(WinMmNative.WAVEINCAPS));
            for(uint id=0;id<WinMmNative.waveInGetNumDevs();id++)
            {WinMmNative.WAVEINCAPS caps;if(WinMmNative.waveInGetDevCaps((IntPtr)(int)id,out caps,size)==0)devices.Add(new MicDeviceInfo{DeviceId=id,Name=(caps.szPname ?? ("Microfone "+id)).Trim()});}
            return devices;
        }
        public void SelectTrainingMicrophone(uint id,string name)
        {_settings.preferred_mic_device=(int)id;FridaSettings.Save(_settings,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"config.json"));SwitchMicrophoneDevice(id,name);}

        private bool PassesSpeakerLock(byte[] wav,string where)
        {byte[] ignored;return PassesSpeakerLock(wav,where,false,out ignored);}

        /// <summary>Filtro "so a minha voz". filtered recebe o audio somente com a sua voz quando havia outra voz junto.
        /// Chamadas inconclusivas tambem sao recusadas; usar "Oi Frida" fornece mais fala para identificar.</summary>
        private bool PassesSpeakerLock(byte[] wav,string where,bool allowInconclusive,out byte[] filtered)
        {return PassesSpeakerLock(wav,where,allowInconclusive,0.0,out filtered);}

        /// <summary>extraMargin: folga exigida acima do limiar. Usada enquanto a Frida fala, porque o eco da propria
        /// voz no microfone chega a pontuar 0,51-0,57 contra limiar 0,50 e interrompia as explicacoes.</summary>
        internal static bool EvaluatePriorityControlSpeaker(SpeakerCheckResult result,bool isSpeaking,out double effectiveThreshold,out string reason)
        {
            effectiveThreshold=0.50;
            reason=result!=null ? (result.Reason ?? "") : "resultado ausente";
            if(result==null){return false;}
            // Nao contorna rejeicao de qualidade, mistura de vozes ou identidade durante a TTS.
            effectiveThreshold=Math.Max(0.58,result.Threshold);
            if(result.Inconclusive || result.SpeechSeconds<SpeakerVoiceProfile.MinVerifySeconds)
            {
                reason=string.IsNullOrWhiteSpace(result.Reason)?"fala curta demais para identificar":result.Reason;
                return false;
            }
            if(result.Accepted && result.Score>=effectiveThreshold)
            {
                reason=isSpeaking?"sua voz sobreposta a reproducao":"sua voz";
                return true;
            }
            reason="voz abaixo do limiar de controle ("+effectiveThreshold.ToString("F3")+")";
            return false;
        }

        private bool PassesPriorityControlSpeakerLock(byte[] wav,bool isSpeaking)
        {
            if(!_settings.speaker_lock_enabled)return IsHumanVocalFormantCandidate(wav);
            if(_voiceProfile==null || !_voiceProfile.IsReady)return false;
            try
            {
                var result=_voiceProfile.CheckShortInteraction(wav,_settings.speaker_lock_tolerance);
                double effectiveThreshold;string reason;
                bool accepted=EvaluatePriorityControlSpeaker(result,isSpeaking,out effectiveThreshold,out reason);
                BootLogger.Log("FiltroVoz [controle prioritario]: "+(accepted?"ACEITO":"DESCARTADO")+" ("+reason+"; score="+result.Score.ToString("F3")+"/"+effectiveThreshold.ToString("F3")+"; fala="+result.SpeechSeconds.ToString("F1")+"s)");
                return accepted;
            }
            catch(Exception ex){BootLogger.Log("FiltroVoz bloqueou controle apos falha: "+ex.GetType().Name+": "+ex.Message);return false;}
        }

        private bool PassesSpeakerLock(byte[] wav,string where,bool allowInconclusive,double extraMargin,out byte[] filtered)
        {
            filtered=null;
            if(!_settings.speaker_lock_enabled)return IsHumanVocalFormantCandidate(wav);
            if(_voiceProfile==null || !_voiceProfile.IsReady)return false;
            try
            {
                var result=allowInconclusive ? _voiceProfile.CheckShortInteraction(wav,_settings.speaker_lock_tolerance) : _voiceProfile.Check(wav,_settings.speaker_lock_tolerance);
                bool accepted=result.Accepted && !result.Inconclusive;
                string reason=result.Reason;
                if(accepted && extraMargin>0 && result.Score<result.Threshold+extraMargin)
                {accepted=false;reason="provavel eco da propria fala; exige score >= "+(result.Threshold+extraMargin).ToString("F3")+" durante a reproducao";}
                BootLogger.Log("FiltroVoz ["+where+"]: "+(accepted?"ACEITO":"DESCARTADO")+" ("+reason+"; score="+result.Score.ToString("F3")+"/"+result.Threshold.ToString("F3")+"; fala="+result.SpeechSeconds.ToString("F1")+"s)");
                if(accepted){filtered=result.FilteredWav;if(where=="comando")_lastVerifiedUserVoiceUtc=DateTime.UtcNow;}
                return accepted;
            }
            catch(Exception ex){BootLogger.Log("FiltroVoz bloqueou audio apos falha: "+ex.GetType().Name+": "+ex.Message);return false;}
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
            CancelVoiceEnrollment();
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
            if(_micThread!=null && _micThread.IsAlive)throw new InvalidOperationException("A captura anterior ainda esta encerrando.");
            _running = true;
            try{_liveVad=new SileroVoiceActivity();BootLogger.Log("Detector neural de fala (Silero VAD) ativo no microfone.");}
            catch(Exception ex){_liveVad=null;BootLogger.Log("Silero VAD indisponivel, usando limiar de energia: "+ex.Message);}
            Task.Run(delegate{if(SpeakerVoiceProfile.EngineAvailable)BootLogger.Log("Verificador neural de locutor pronto ("+NeuralSpeakerEncoder.ModelFile+"); perfil "+(_voiceProfile.IsReady?"cadastrado":"pendente")+".");else BootLogger.Log("Verificador neural indisponivel: "+SpeakerVoiceProfile.EngineError);});

            if (WinMmNative.waveInGetNumDevs() > 0)
            {
                AutoSelectBestPhysicalMic();
                _waveEvent = new AutoResetEvent(false);
                _micThread = new Thread(MicSupervisorLoop);
                _micThread.IsBackground = true;
                _micThread.Name = "FridaPhysicalMicEngine";
                _micThread.Start();
            }

            if(WinMmNative.waveInGetNumDevs()==0 && OnAudioUnavailable!=null)OnAudioUnavailable("Nenhum microfone esta disponivel.");
            try
            {
                _localWake = new LocalWakeDetector(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wake"), HandleLocalRecognition);
                BootLogger.Log("Chamada local PT pronta no microfone selecionado.");
            }
            catch (Exception ex)
            {
                BootLogger.Log("Detector local indisponivel: " + ex.GetType().Name + ". Usando reconhecimento local do Windows.");
                StartWinRtWakeWordEngine();
            }
        }

        private void MicSupervisorLoop()
        {
            int failures=0;bool unavailable=false;
            while(_running)
            {
                if(unavailable && !_restartMicLoop){Thread.Sleep(250);continue;}
                if(_restartMicLoop){failures=0;unavailable=false;}
                _restartMicLoop=false;DateTime started=DateTime.UtcNow;
                RunSingleDeviceCaptureSession(_selectedMicId);
                if(!_running)break;
                if(_restartMicLoop)continue;
                if(_lastMicChunkUtc>started)failures=0;
                if(++failures>1)
                {
                    unavailable=true;FridaRuntimeHealth.State("audio indisponivel",LastCaptureError ?? "capture_failed");
                    if(OnAudioUnavailable!=null)OnAudioUnavailable("Falha no microfone apos uma tentativa de reinicializacao. "+LastCaptureError);
                }
                else {BootLogger.Log("Reiniciando captura de audio (tentativa unica).");Thread.Sleep(500);}
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
                    "Fala Frida", "Ativar Frida", "Escuta Frida"
                };

                phrases=phrases.Concat(FridaSessionControl.SpokenControls).Concat(FridaSessionControl.SpokenControls.Select(x=>"Frida "+x)).ToArray();
                SpeechRecognitionListConstraint wakeConstraint = new SpeechRecognitionListConstraint(phrases, "frida_wake");
                _winRtWakeRecognizer.Constraints.Add(wakeConstraint);
                _winRtWakeRecognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(250);

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
                // Hipoteses parciais disparam varias vezes por segundo sobre a propria fala da Frida;
                // durante a reproducao so o resultado final pode acionar "pare"/"pausa".
                if (_isSpeaking) return;
                if (!string.IsNullOrEmpty(text))
                {
                    HandleLocalRecognition(text);
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
                    if (!string.IsNullOrWhiteSpace(args.Result.Text))
                    {
                        HandleLocalRecognition(args.Result.Text);
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

        private void HandleLocalRecognition(string text)
        {
            if (IsEnrollingVoice) return; // "Frida, pare" e uma frase de cadastro, nao uma ordem.
            var command=FridaSessionControl.Parse(text);
            if(command!=FridaControlCommand.None)
            {
                if(command==FridaControlCommand.Off && !FridaSessionControl.IsAddressedControl(text))return;
                if(_settings.speaker_lock_enabled && !_voiceProfile.HasRefinedProfile)return;
                byte[] pcm;lock(_controlAudioSync)pcm=_controlPreRoll.SelectMany(x=>x).ToArray();
                if(pcm.Length<3200 || IsPlaybackEcho(pcm))return;
                byte[] wav=BuildWavFromPcm16Mono(pcm,SampleRate);
                bool accepted=PassesPriorityControlSpeakerLock(wav,_isSpeaking);
                if(!accepted && CanStopAuthenticatedPlayback(text,command,_isSpeaking || _isBusyProcessingTurn,
                    (DateTime.UtcNow-_lastVerifiedUserVoiceUtc).TotalSeconds) && IsHumanVocalFormantCandidate(wav))
                {accepted=true;BootLogger.Log("Parada local: interrompendo sessao autenticada sob mistura com TTS.");}
                if(!accepted || (DateTime.UtcNow-_lastControlUtc).TotalMilliseconds<400)return;
                _lastControlUtc=DateTime.UtcNow;
                BootLogger.Log("Controle local confirmado: "+command+"; hipotese="+text);
                lock(_controlAudioSync)_controlPreRoll.Clear();
                if(_localWake!=null)_localWake.Reset();
                StopCurrentSpeech();
                if(OnPriorityControl!=null)OnPriorityControl(command);
                return;
            }
            if(ContainsWakeWordPhonetic(text))CheckWakeTriggerFromWinRt();
        }

        internal static bool CanStopAuthenticatedPlayback(string text,FridaControlCommand command,bool busy,double ageSeconds)
        {
            if(command!=FridaControlCommand.Stop || !busy || ageSeconds<0 || ageSeconds>120 || double.IsNaN(ageSeconds))return false;
            string t=FridaInteractionPolicy.Normalize(text);
            return t=="pare" || t=="pare de falar" || t=="para de falar" || FridaSessionControl.IsAddressedControl(text);
        }

        public void EnterOff()
        {
            FridaRuntimeHealth.State("inativa","off solicitado pelo operador");
            _offRequested=true;Interlocked.Increment(ref _inputEpoch);
            StopCurrentSpeech();CancelVoiceEnrollment();CancelFollowUpAndHide();
            lock(_controlAudioSync)_controlPreRoll.Clear();
            if(_localWake!=null)_localWake.Reset();
            if(_orb!=null)_orb.SetActivityState(OrbActivityState.Hidden,"FRIDA · OFF","");
        }

        private void CheckWakeTriggerFromWinRt()
        {
            if (!_running || _paused || _isSpeaking || _isBusyProcessingTurn || IsEnrollingVoice || Interlocked.CompareExchange(ref _foregroundTranscribing,0,0)!=0 || !HandsFreeProfileReady) return;
            if (DateTime.UtcNow < _ignoreMicUntilUtc || _capturingActiveCommand || _followUpWindowActive) return;
            lock (_recLock)
            {
                if ((DateTime.UtcNow - _lastWakeAcceptedUtc).TotalMilliseconds < 1400) return;
                _lastWakeAcceptedUtc = DateTime.UtcNow;
            }
            byte[] call;
            lock(_recLock)call=BuildWavFromPcm16Mono(_preRollQueue.SelectMany(x=>x).ToArray(),SampleRate);
            byte[] unusedFiltered;
            if(!PassesSpeakerLock(call,"chamada",true,out unusedFiltered))return;
            TriggerManualOrWakeListen(true);
        }

        public void TriggerManualOrWakeListen(bool fromWakeWord)
        {
            if(IsEnrollingVoice || _isBusyProcessingTurn || Interlocked.CompareExchange(ref _foregroundTranscribing,0,0)!=0)return;
            if(!HandsFreeProfileReady)
            {
                if(OnVoiceTrainingRequired!=null)OnVoiceTrainingRequired();
                if(_orb!=null)_orb.SetActivityState(OrbActivityState.Listening,"FRIDA · TREINE SUA VOZ",VoiceProtectionStatus);
                return;
            }
            lock (_recLock)
            {
                if (_capturingActiveCommand) return;
                if (_isSpeaking)
                {
                    StopCurrentSpeech();
                }

                _offRequested=false;Interlocked.Increment(ref _inputEpoch);
                // Inicia captura somente por chamada explicita (voz, F4 ou clique).



                _followUpWindowActive = false;
                _capturingActiveCommand = true;
                _captureMetrics = new FridaCaptureMetrics();
                _includeWakeStrip = fromWakeWord;
                _activeStartedUtc = DateTime.UtcNow;
                _activeCaptureEpoch=Volatile.Read(ref _inputEpoch);
                _followUpDeadlineUtc=_activeStartedUtc.AddSeconds(FridaSessionControl.IdleSeconds);
                _activeLastVoiceUtc = DateTime.UtcNow;
                _heardVoiceInActive = fromWakeWord;
                _heardVoiceAfterWake = false;
                _wakePostVoiceFrames = 0;
                _wakeVoiceGuardUntilUtc = fromWakeWord ? DateTime.UtcNow.AddMilliseconds(180) : DateTime.MinValue;
                _activeCapturePcm = new MemoryStream();

                if (fromWakeWord)
                {
                    foreach (byte[] chunk in _preRollQueue)
                    {
                        _activeCapturePcm.Write(chunk, 0, chunk.Length);
                    }
                }
            }

            PlaySoftWakeToneAsync();

            if (_orb != null) _orb.SetActivityState(
                OrbActivityState.Listening,
                "FRIDA · ESCUTANDO (" + TruncateMicName(_selectedMicName) + ")",
                "Pode falar.");
        }

        public void WaitForExplicitRequest()
        {
            if (!_running || _paused || IsEnrollingVoice || !HandsFreeProfileReady) return;
            _followUpDeadlineUtc = DateTime.UtcNow.AddSeconds(FridaSessionControl.IdleSeconds);
            _offRequested=false;
            _followUpWindowActive = true;
            if (_orb != null) _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · ATENDIMENTO", "Pode falar.");
        }

        public void WaitForConversationContinuation()
        {
            if (!_running || _paused || IsEnrollingVoice || !HandsFreeProfileReady) { CancelFollowUpAndHide(); return; }
            _followUpDeadlineUtc = DateTime.UtcNow.AddSeconds(FridaSessionControl.IdleSeconds);
            _offRequested=false;
            _followUpWindowActive = true;
            _consecutiveVoiceFrames = 0;
            if (_orb != null) _orb.SetActivityState(OrbActivityState.FollowUp, "FRIDA · CONVERSA", "Pode continuar falando.");
        }

        public void EnterFollowUpConversationWindow()
        {

            if (!_running || _paused)
            {
                if (_orb != null) _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                return;
            }

            _followUpDeadlineUtc = DateTime.UtcNow.AddSeconds(FridaSessionControl.IdleSeconds);
            _offRequested=false;
            _followUpWindowActive = true;
            _consecutiveVoiceFrames = 0;
            if (_orb != null) _orb.SetActivityState(
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
            if (_orb != null) _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
        }

        public void StopCurrentSpeech()
        {
            Interlocked.Increment(ref _speechEpoch);Interlocked.Increment(ref _inputEpoch);
            _cancelSpeechRequested=true;
            lock(_speechSync)
            {
                if(_speechCancellation!=null)try{_speechCancellation.Cancel();}catch(ObjectDisposedException){}
                if(_activePlayer!=null)try{_activePlayer.Stop();}catch{}
            }
            lock(_inputSync)if(_inputCancellation!=null)try{_inputCancellation.Cancel();}catch(ObjectDisposedException){}
        }

        private static void RequireCaptureSuccess(int result,string operation)
        {if(result!=0)throw new IOException(operation+" falhou; codigo="+result);}
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

                if (rc != 0 || hWaveIn == IntPtr.Zero)throw new IOException("waveInOpen falhou; codigo="+rc+"; dispositivo="+deviceId);

                for (int i = 0; i < NumBuffers; i++)
                {
                    dataPtrs[i] = Marshal.AllocHGlobal(BufferBytes);
                    headerPtrs[i] = Marshal.AllocHGlobal(hdrSize);

                    WinMmNative.WAVEHDR hdr = new WinMmNative.WAVEHDR();
                    hdr.lpData = dataPtrs[i];
                    hdr.dwBufferLength = BufferBytes;
                    hdr.dwFlags = 0;

                    Marshal.StructureToPtr(hdr, headerPtrs[i], false);
                    RequireCaptureSuccess(WinMmNative.waveInPrepareHeader(hWaveIn,headerPtrs[i],hdrSize),"waveInPrepareHeader");
                    RequireCaptureSuccess(WinMmNative.waveInAddBuffer(hWaveIn,headerPtrs[i],hdrSize),"waveInAddBuffer");
                }

                RequireCaptureSuccess(WinMmNative.waveInStart(hWaveIn),"waveInStart");

                byte[] chunkBuf = new byte[BufferBytes];int nextBuffer=0;
                while (_running && !_restartMicLoop)
                {
                    _waveEvent.WaitOne(75);
                    if (!_running || _restartMicLoop) break;

                    for (int completed = 0; completed < NumBuffers; completed++)
                    {
                        int i=nextBuffer;
                        WinMmNative.WAVEHDR hdr = (WinMmNative.WAVEHDR)Marshal.PtrToStructure(headerPtrs[i], typeof(WinMmNative.WAVEHDR));
                        if((hdr.dwFlags & WinMmNative.WHDR_DONE)==0)break;
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
                                RequireCaptureSuccess(WinMmNative.waveInAddBuffer(hWaveIn,headerPtrs[i],hdrSize),"waveInAddBuffer");
                            }
                            nextBuffer=(nextBuffer+1)%NumBuffers;
                        }
                    }

                    CheckFollowUpTimeout();
                }
            }
            catch(Exception ex)
            {
                LastCaptureError=ex.GetType().Name+": "+ex.Message;
                if(_running && (DateTime.UtcNow-_lastCaptureErrorUtc).TotalSeconds>=10)
                {_lastCaptureErrorUtc=DateTime.UtcNow;BootLogger.Log("CaptacaoWinMM: "+LastCaptureError);}
            }
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

        private readonly object _ideCaptureLock = new object();
        private MemoryStream _ideCapture;
        private int _idePlayback;
        private TimeSpan _ideFollowUpRemaining;
        private bool _ideWasFollowUp;
        public bool IsIdeRecording { get { lock (_ideCaptureLock) return _ideCapture != null; } }
        public void BeginIdeRecording()
        {
            if (IsEnrollingVoice || _isBusyProcessingTurn || _isSpeaking || _capturingActiveCommand || Volatile.Read(ref _idePlayback) != 0)
                throw new InvalidOperationException("Aguarde o atendimento ou treinamento terminar para gravar.");
            lock (_ideCaptureLock)
            {
                if (_ideCapture != null) throw new InvalidOperationException("Uma gravação já está ativa.");
                _ideWasFollowUp = _followUpWindowActive;
                _ideFollowUpRemaining = _followUpDeadlineUtc - DateTime.UtcNow;
                lock (_recLock) { _preRollQueue.Clear(); _consecutiveVoiceFrames = 0; }
                _ideCapture = new MemoryStream();
            }
        }
        public byte[] FinishIdeRecording(bool cancel)
        {
            lock (_ideCaptureLock)
            {
                if (_ideCapture == null) return null;
                using (var capture = _ideCapture)
                {
                    _ideCapture = null;
                    if (_ideWasFollowUp && _ideFollowUpRemaining > TimeSpan.Zero) _followUpDeadlineUtc = DateTime.UtcNow + _ideFollowUpRemaining;
                    return cancel || capture.Length == 0 ? null : BuildWavFromPcm16Mono(capture.ToArray(), SampleRate);
                }
            }
        }
        public async Task PlayIdeRecordingAsync(byte[] wav)
        {
            if (wav == null || IsIdeRecording || IsEnrollingVoice || _isBusyProcessingTurn || _capturingActiveCommand)
                throw new InvalidOperationException("Aguarde o atendimento ou a gravação terminar para ouvir o áudio.");
            await _speechGate.WaitAsync();
            bool started = false;
            try
            {
                lock (_ideCaptureLock)
                {
                    if (IsIdeRecording || IsEnrollingVoice || _isBusyProcessingTurn || _capturingActiveCommand)
                        throw new InvalidOperationException("A Frida iniciou outro atendimento. Tente ouvir o áudio depois.");
                    Interlocked.Exchange(ref _idePlayback, 1);
                    _isSpeaking = true;
                    started = true;
                }
                using (var memory = new MemoryStream(wav))
                using (var player = new SoundPlayer(memory))
                {
                    lock (_speechSync) _activePlayer = player;
                    await Task.Run(() => player.PlaySync());
                }
            }
            finally
            {
                if (started)
                {
                    lock (_speechSync) _activePlayer = null;
                    _isSpeaking = false;
                    Interlocked.Exchange(ref _idePlayback, 0);
                }
                _speechGate.Release();
            }
        }

        public async Task<string> TranscribeIdeRecordingAsync(byte[] wav)
        {
            if (wav == null || wav.Length < 45) throw new InvalidOperationException("A gravação não contém áudio. Verifique o microfone.");
            var result = await TranscribeAudioAsync(wav, "ide");
            if (result.Status == FridaTranscriptionStatus.Cancelled) throw new OperationCanceledException();
            if (!result.IsOk) throw new InvalidOperationException(result.UserMessage);
            return result.Text;
        }

        private void ProcessMicPcmChunk(byte[] pcmBytes)
        {
            if (Volatile.Read(ref _idePlayback) != 0) { _lastMicChunkUtc = DateTime.UtcNow; return; }
            lock (_ideCaptureLock)
            {
                if (_ideCapture != null)
                {
                    int remaining = 300 * SampleRate * 2 - (int)_ideCapture.Length;
                    if (remaining > 0) _ideCapture.Write(pcmBytes, 0, Math.Min(remaining, pcmBytes.Length));
                    _lastMicChunkUtc = DateTime.UtcNow;
                    return;
                }
            }

            _lastMicChunkUtc=DateTime.UtcNow;
            LastCaptureError=null;
            var session=_enrollment;
            if(session!=null && session.IsActive){session.Push(pcmBytes);return;}
            lock(_controlAudioSync){_controlPreRoll.Enqueue((byte[])pcmBytes.Clone());while(_controlPreRoll.Count>48)_controlPreRoll.Dequeue();}
            if(_localWake!=null && _running && !_paused){_localWake.IsSpeakingPlayback=_isSpeaking;_localWake.Push(pcmBytes);}
            float rmsRaw;
            int peakSample;
            float[] bands16 = ComputePcmSpectrum16(pcmBytes, 0, pcmBytes.Length, out rmsRaw, out peakSample);
            _microphoneRms=rmsRaw;

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
            else if (!_capturingActiveCommand && !_isSpeaking && rmsRaw < _noiseFloorRms * 1.45)
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

            bool hasVoice;
            bool? neuralSpeech = null;
            bool energyDecision = rmsRaw>=Math.Max(70.0,_noiseFloorRms*1.15) || peakSample>=Math.Max(900,(int)(_noiseFloorRms*4.0));
            var liveVad=_liveVad;
            if(liveVad!=null)
            {
                // Silero VAD: so fala humana conta como voz. Ventilador, teclado, musica sem voz e cliques sao ignorados.
                bool speech=false;
                try{speech=liveVad.PushAndDetect(pcmBytes);}catch(Exception ex){_liveVad=null;BootLogger.Log("Silero VAD falhou, voltando ao limiar de energia: "+ex.GetType().Name);}
                neuralSpeech = _liveVad == null ? (bool?)null : speech;
                hasVoice = speech && (!_settings.voice_vad_energy_gate || energyDecision);
            }
            else hasVoice = (rmsRaw >= dynamicThresh) || (peakSample >= Math.Max(1550, (int)(_noiseFloorRms * 6.2)));

            byte[] finishedActiveWav = null;
            bool activeWasWake = false;
            bool finishedLocalWakeOnly = false;
            bool finishedTruncated = false;
            int finishedInputEpoch=Volatile.Read(ref _inputEpoch);

            lock (_recLock)
            {
                // Hard acoustic echo shield: never record Frida's own speaker voice, active turn execution, or room decay into pre-roll!
                if (_isSpeaking || _paused)
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
                // Preserva inicio da resposta durante a guarda pos-TTS/termino de processamento.
                if(_isBusyProcessingTurn || DateTime.UtcNow < _ignoreMicUntilUtc)return;

                // 1. Follow-up conversational window (orb already visible, no "Frida" prefix needed!)
                if (!_capturingActiveCommand && _followUpWindowActive)
                {
                    float followNorm = Math.Min(1.2f, Math.Max(0f, (float)(rmsRaw - _noiseFloorRms * 0.8)) / 1400f);
                    if(_orb!=null)_orb.PushAudioSpectrum(followNorm, bands16);

                    if (hasVoice)
                    {
                        _consecutiveVoiceFrames++;
                        if (_consecutiveVoiceFrames >= 3) // ~190ms sustained speech prevents transient clicks from false-triggering
                        {
                            _followUpWindowActive = false;
                            _capturingActiveCommand = true;
                            _captureMetrics = new FridaCaptureMetrics();
                            _includeWakeStrip = false;
                            _heardVoiceInActive = true;
                            _activeStartedUtc = DateTime.UtcNow;
                            _activeCaptureEpoch=Volatile.Read(ref _inputEpoch);
                            _activeLastVoiceUtc = DateTime.UtcNow;
                            _activeCapturePcm = new MemoryStream();
                            foreach (byte[] pre in _preRollQueue)
                            {
                                _activeCapturePcm.Write(pre, 0, pre.Length);
                            }
                            if (_orb != null) _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · ESCUTANDO", "Captando sua resposta...");
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
                    _captureMetrics.Observe(pcmBytes, neuralSpeech, energyDecision);
                    _activeCapturePcm.Write(pcmBytes, 0, pcmBytes.Length);

                    float normRms = Math.Min(1.35f, Math.Max(0.05f, (float)(rmsRaw - _noiseFloorRms * 0.7)) / 1300f);
                    if(_orb!=null)_orb.PushAudioSpectrum(normRms, bands16);

                    DateTime now = DateTime.UtcNow;
                    if (hasVoice && now >= _wakeVoiceGuardUntilUtc)
                    {
                        if (!_heardVoiceInActive)
                        {
                            _activeStartedUtc=now;
                            _activeCapturePcm.SetLength(0);
                            foreach(byte[] pre in _preRollQueue)_activeCapturePcm.Write(pre,0,pre.Length);
                            if (_orb != null) _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · OUVINDO SUA VOZ", "Captando comando...");
                        }
                        _heardVoiceInActive = true;
                        if(_includeWakeStrip && ++_wakePostVoiceFrames>=2)_heardVoiceAfterWake=true;
                        _activeLastVoiceUtc = now;
                    }
                    else if(!hasVoice)_wakePostVoiceFrames=0;

                    double totalSec = (now - _activeStartedUtc).TotalSeconds;
                    double silenceSec = (now - _activeLastVoiceUtc).TotalSeconds;

                    bool endByWakeOnly=FridaVoiceTiming.WakeOnly(_includeWakeStrip,_heardVoiceAfterWake,totalSec,silenceSec);
                    bool endBySilence = FridaVoiceTiming.EndBySilence(_heardVoiceInActive,totalSec,silenceSec,FridaVoiceOptions.EndSilence(_settings));
                    bool endByNoSpeech = !_heardVoiceInActive && totalSec >= FridaSessionControl.IdleSeconds;
                    bool endByMaxLen = totalSec >= 32.0;

                    if (endByWakeOnly || endBySilence || endByNoSpeech || endByMaxLen)
                    {
                        FridaVoiceMetrics.Capture(_settings, _captureMetrics, endByMaxLen ? "max_length" : endByWakeOnly ? "wake_only" : endBySilence ? "silence" : "no_speech", (int)_selectedMicId);
                        _capturingActiveCommand = false;
                        activeWasWake = _includeWakeStrip;
                        finishedInputEpoch=_activeCaptureEpoch;
                        if(endByMaxLen && !endBySilence)
                        {
                            finishedTruncated = true;
                        }
                        else if(endByWakeOnly)
                        {
                            finishedLocalWakeOnly=true;
                            _followUpWindowActive=true;
                            _followUpDeadlineUtc=now.AddSeconds(FridaSessionControl.IdleSeconds);
                            _consecutiveVoiceFrames=0;
                        }
                        else if (_heardVoiceInActive)
                        {
                            finishedActiveWav = BuildWavFromPcm16Mono(_activeCapturePcm.ToArray(), SampleRate);
                        }
                        _activeCapturePcm.Dispose();
                        _activeCapturePcm = null;

                        if (finishedActiveWav == null && !finishedLocalWakeOnly && !finishedTruncated)
                        {
                            _offRequested=false;
                            FridaRuntimeHealth.Inactivity();FridaRuntimeHealth.State("inativa","sem fala; aguardando nova chamada");
                            if (_orb != null) _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                        }
                    }
                }
            }


            if (finishedTruncated)
            {
                if (finishedInputEpoch != Volatile.Read(ref _inputEpoch) || _offRequested) return;
                var truncated = FridaTranscriptionResult.Create(FridaTranscriptionStatus.LowQuality, "capture_limit");
                FridaVoiceMetrics.Record(_settings, truncated, "command", (int)_selectedMicId);
                EnterFollowUpConversationWindow();
                if (_orb != null) _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · REPITA", "A fala atingiu o limite. Divida o pedido e repita o comando completo.");
                return;
            }
            if(finishedLocalWakeOnly)
            {
                if(finishedInputEpoch!=Volatile.Read(ref _inputEpoch) || _offRequested)return;
                BootLogger.Log("Chamada local reconhecida: atendimento pronto sem consulta de transcricao.");
                if(_orb!=null)_orb.SetActivityState(OrbActivityState.Listening,"FRIDA · ATENDIMENTO","Pode falar.");
                if(OnUserTranscriptReady!=null)OnUserTranscriptReady("__WAKE_ONLY__",true);
                return;
            }
            if (finishedActiveWav != null)
            {
                if(Interlocked.CompareExchange(ref _foregroundTranscribing,1,0)==0)
                    Task.Run(async delegate {try {await TranscribeWithEpochAsync(finishedActiveWav,activeWasWake,finishedInputEpoch);} finally {Interlocked.Exchange(ref _foregroundTranscribing,0);}});
            }

        }

        private static bool IsHumanVocalFormantCandidate(byte[] audio)
        {
            try{return SpeakerVoiceProfile.HasHumanSpeech(audio,0.20);}catch{return false;}
        }

        private static bool ContainsWakeWordPhonetic(string transcript)
        {
            string command;
            return FridaInteractionPolicy.TryStripWake(transcript, out command);
        }

        private static string StripLeadingWakeWord(string text)
        {
            string command;
            return FridaInteractionPolicy.TryStripWake(text, out command) ? command : (text ?? "").Trim();
        }

        private void CheckFollowUpTimeout()
        {
            if (_followUpWindowActive && !_capturingActiveCommand && !_isSpeaking && !_isBusyProcessingTurn)
            {
                if (DateTime.UtcNow >= _followUpDeadlineUtc)
                {
                    _followUpWindowActive = false;_offRequested=false;
                    FridaRuntimeHealth.Inactivity();FridaRuntimeHealth.State("inativa","conversa expirada; aguardando nova chamada");
                    if (OnFollowUpWindowExpired != null)
                    {
                        OnFollowUpWindowExpired();
                    }
                    else
                    {
                        if (_orb != null) _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                    }
                }
            }
        }

        private Task TranscribeForegroundAndDispatchAsync(byte[] wavBytes,bool stripWakePrefix)
        {return TranscribeWithEpochAsync(wavBytes,stripWakePrefix,Volatile.Read(ref _inputEpoch));}

        private async Task TranscribeWithEpochAsync(byte[] wavBytes,bool stripWakePrefix,int inputEpoch)
        {
            CancellationTokenSource input;
            lock(_inputSync)
            {
                if(inputEpoch!=Volatile.Read(ref _inputEpoch))return;
                input=CancellationTokenSource.CreateLinkedTokenSource(FridaWorkContext.Token);_inputCancellation=input;
            }
            using(input)
            using(FridaWorkContext.Enter(input.Token))
            try
            {
            byte[] ownVoiceOnly=null;
            if (!_running || _paused || IsEnrollingVoice) return;
            if (!PassesSpeakerLock(wavBytes, "comando", false, out ownVoiceOnly))
            {
                var rejected = FridaTranscriptionResult.Create(FridaTranscriptionStatus.SpeakerRejected, "speaker_gate_rejected");
                FridaVoiceMetrics.Record(_settings,rejected,"command",(int)_selectedMicId);
                if(_running && !_paused && !IsEnrollingVoice && !_offRequested && HandsFreeProfileReady && DateTime.UtcNow<_followUpDeadlineUtc)
                {_followUpWindowActive=true;if(_orb!=null)_orb.SetActivityState(OrbActivityState.Listening,"FRIDA · ATENDIMENTO",rejected.UserMessage);}
                else if (_orb != null) _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                return;
            }

            if (_orb != null) _orb.SetActivityState(OrbActivityState.Thinking, "FRIDA · PROCESSANDO VOZ", "Transcrevendo sua fala...");

            FridaTranscriptionResult result;
            try
            {
                result = await TranscribeAudioAsync(ownVoiceOnly ?? wavBytes, "command");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                BootLogger.Log("STT comando: " + ex.GetType().Name);
                result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "unexpected_error");
            }

            if(!_running || _paused || IsEnrollingVoice || inputEpoch!=Volatile.Read(ref _inputEpoch))return;
            if (!result.IsOk)
            {
                if (result.Status != FridaTranscriptionStatus.Cancelled && !_offRequested)
                {
                    EnterFollowUpConversationWindow();
                    if (_orb != null) _orb.SetActivityState(OrbActivityState.Listening, "FRIDA · REPITA", result.UserMessage);
                }
                return;
            }

            string cleaned = StripLeadingWakeWord(result.Text);
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
            finally {lock(_inputSync)if(object.ReferenceEquals(_inputCancellation,input))_inputCancellation=null;}
        }

        private async Task<FridaTranscriptionResult> TranscribeAudioAsync(byte[] wavBytes, string source)
        {
            FridaTeamState.BeginWork(TeamRole.Hearing, "Transcrevendo audio", false);
            FridaTranscriptionResult result;
            try
            {
                result = await new FridaSpeechRecognizer(_http, _settings).TranscribeAsync(wavBytes, FridaWorkContext.Token);
                // A quality retry never becomes a third request to an unrelated model.
                bool serviceFailure = result.Reason == "provider_unavailable" || result.Reason == "timeout"
                    || result.Reason == "transport_or_response_error" || result.Reason.StartsWith("http_", StringComparison.Ordinal);
                if (result.Status == FridaTranscriptionStatus.ServiceError && serviceFailure && result.Attempts < 2
                    && _settings.stt_gemini_fallback_enabled && !string.IsNullOrWhiteSpace(_settings.api_key))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var attempt = new FridaTranscriptionAttempt { Model = ModelSelection.GeminiLite };
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(FridaWorkContext.Token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(FridaVoiceOptions.TimeoutSeconds(_settings)));
                            using (FridaWorkContext.Enter(timeout.Token))
                            {
                                string text = await CallGeminiBackupTranscribeAsync(wavBytes);
                                result.Status = text == null ? FridaTranscriptionStatus.ServiceError
                                    : string.IsNullOrWhiteSpace(text) ? FridaTranscriptionStatus.LowQuality : FridaTranscriptionStatus.Ok;
                                result.Text = text ?? "";
                                result.Reason = text == null ? "backup_unavailable" : result.IsOk ? "backup_transcript" : "backup_empty_after_speech";
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        result.Status = FridaWorkContext.Token.IsCancellationRequested ? FridaTranscriptionStatus.Cancelled : FridaTranscriptionStatus.ServiceError;
                        result.Text = ""; result.Reason = result.Status == FridaTranscriptionStatus.Cancelled ? "cancelled" : "backup_timeout";
                    }
                    result.Model = attempt.Model; attempt.Milliseconds = watch.Elapsed.TotalMilliseconds;
                    result.ElapsedMilliseconds += attempt.Milliseconds; attempt.Status = result.Status.ToString(); attempt.Reason = result.Reason;
                    result.History.Add(attempt); result.Attempts = result.History.Count;
                }
                if (FridaWorkContext.Token.IsCancellationRequested)
                { result.Status = FridaTranscriptionStatus.Cancelled; result.Text = ""; result.Reason = "cancelled"; }
                FridaVoiceMetrics.Record(_settings, result, source, (int)_selectedMicId);
                return result;
            }
            finally { FridaTeamState.EndWork(TeamRole.Hearing); }
        }

        private async Task<string> CallGeminiBackupTranscribeAsync(byte[] wavBytes)
        {
            FridaWorkContext.ThrowIfCancelled();
            // Reserva: reutiliza o Gemini Lite ja selecionado.
            string apiKey = (_settings.api_key ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey)) return null;

            string[] rawCandidates = new string[] { ModelSelection.GeminiLite };

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
                        using (HttpResponseMessage resp = await _http.SendAsync(req,FridaWorkContext.Token))
                        {
                            string body = await resp.Content.ReadAsStringAsync();
                            if (!resp.IsSuccessStatusCode)
                            {
                                ModelQuotaGuard.RecordHttpResponse("stt", modelName, 0, resp, body);
                                BootLogger.Log("STT [" + modelName + "] HTTP " + (int)resp.StatusCode);
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
                                        ModelQuotaGuard.RecordHttpResponse("stt", modelName, 300.0, resp, body);
                                        return recognized;
                                    }
                                }
                            }
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FridaWorkContext.ThrowIfCancelled();
                    BootLogger.Log("STT [" + modelName + "] Excecao: " + ex.GetType().Name);
                    if (IsNetworkOutageException(ex))
                    {
                        _networkOutageBackoffUntilUtc = DateTime.UtcNow.AddSeconds(12);
                        break;
                    }
                }
            }

            return null;
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

        public Task PlayPreSynthesizedWavAsync(byte[] wavBytes,string displaySubtitle)
        {return RunSpeechAsync(null,displaySubtitle,wavBytes);}

        private bool _technicalDelivery = true;
        private string VoiceCacheTag()
        {
            // Cache separado por voz: frases salvas em outra voz (Aoede, Francisca) nao se misturam com a voz Edge atual.
            string voice = _settings.prefer_zero_quota_tts ? "edge_" + ModelSelection.EdgeSpeech : ModelSelection.Speech + "_" + _settings.female_voice;
            return voice + (_technicalDelivery ? "_technical" : "_conversation");
        }
        public async Task SpeakResponseAsync(string text, bool technical)
        {
            await SpeakResponseWithResultAsync(text,technical);
        }
        public Task<FridaSpeechResult> SpeakResponseWithResultAsync(string text,bool technical)
        {return RunSpeechWithResultAsync(PrepareNaturalSpokenText(text),null,null,technical);}

        public bool HasNaturalVoiceCached(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string clean = PrepareNaturalSpokenText(text);
            string path = GetTtsDiskCachePath(VoiceCacheTag(), clean);
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

        public Task SpeakTextAsync(string rawText,string subtitleOverride)
        {return RunSpeechAsync(PrepareNaturalSpokenText(rawText),subtitleOverride,null);}

        private async Task RunSpeechAsync(string text,string subtitle,byte[] prepared,bool technical=true)
        {
            await RunSpeechWithResultAsync(text,subtitle,prepared,technical);
        }
        private async Task<FridaSpeechResult> RunSpeechWithResultAsync(string text,string subtitle,byte[] prepared,bool technical=true)
        {
            var result=new FridaSpeechResult();
            if((prepared==null || prepared.Length<=44) && string.IsNullOrWhiteSpace(text)){result.Status="empty";return result;}
            int admitted=Volatile.Read(ref _speechEpoch);
            await _speechGate.WaitAsync();
            CancellationTokenSource cancellation=null;IDisposable scope=null;
            try
            {
                if(admitted!=Volatile.Read(ref _speechEpoch)){result.Status="cancelled";return result;}
                cancellation=CancellationTokenSource.CreateLinkedTokenSource(FridaWorkContext.Token);
                lock(_speechSync)
                {
                    if(admitted!=Volatile.Read(ref _speechEpoch)){result.Status="cancelled";return result;}
                    _speechCancellation=cancellation;_cancelSpeechRequested=false;
                    _technicalDelivery=technical;
                    _isSpeaking=true;_offRequested=false;_followUpWindowActive=false;
                }
                scope=FridaWorkContext.Enter(cancellation.Token);
                FridaTeamState.BeginWork(TeamRole.Voice,"Falando como Frida",false);
                lock(_recLock){_preRollQueue.Clear();_consecutiveVoiceFrames=0;_capturingActiveCommand=false;if(_activeCapturePcm!=null){_activeCapturePcm.Dispose();_activeCapturePcm=null;}}
                lock(_controlAudioSync)_controlPreRoll.Clear();
                if(_localWake!=null){_localWake.IsSpeakingPlayback=true;_localWake.Reset();}
                if(_orb!=null)_orb.SetActivityState(OrbActivityState.Speaking,"FRIDA",subtitle ?? text ?? "");
                if(prepared!=null){cancellation.Token.ThrowIfCancellationRequested();await Task.Run(()=>PlayWavSynchronizedWithHologram(prepared));cancellation.Token.ThrowIfCancellationRequested();result.CompletedChunks++;}
                else
                {
                    // Primeiro trecho curto (fala comeca rapido) e o seguinte e sintetizado enquanto o atual toca:
                    // antes era sequencial e deixava ~26 s de silencio entre trechos longos.
                    List<string> pieces=SplitSpeechForStreaming(text);
                    Task<byte[]> pending=pieces.Count>0?SynthesizeFemaleVoiceWavAsync(pieces[0]):null;
                    try
                    {
                        for(int i=0;i<pieces.Count;i++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            byte[] wav=await pending;pending=null;
                            cancellation.Token.ThrowIfCancellationRequested();
                            if(wav==null || wav.Length<=44)
                            {result.Status="failed";result.ErrorCode="synthesis_unavailable";if(OnNaturalVoiceUnavailable!=null)OnNaturalVoiceUnavailable(text);break;}
                            if(i+1<pieces.Count)pending=SynthesizeFemaleVoiceWavAsync(pieces[i+1]);
                            await Task.Run(()=>PlayWavSynchronizedWithHologram(wav));
                            cancellation.Token.ThrowIfCancellationRequested();result.CompletedChunks++;
                        }
                    }
                    finally
                    {
                        // Sintese antecipada abandonada (cancelamento/falha): observa o resultado para nao vazar excecao.
                        if(pending!=null){var observed=pending.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);}
                    }
                }
                if(result.Status!="failed")result.Status=_running?"playback_completed":"playback_skipped";
            }
            catch(OperationCanceledException){result.Status="cancelled";}
            catch(Exception ex){result.Status="failed";result.ErrorCode=ex.GetType().Name;BootLogger.Log("Falha na fala: "+ex.GetType().Name);if(OnNaturalVoiceUnavailable!=null && text!=null)OnNaturalVoiceUnavailable(text);}
            finally
            {
                if(scope!=null)scope.Dispose();
                lock(_speechSync)
                {
                    if(object.ReferenceEquals(_speechCancellation,cancellation))
                    {_speechCancellation=null;_isSpeaking=false;_activePlayer=null;_playbackReference=null;}
                }
                if(_localWake!=null)_localWake.IsSpeakingPlayback=_isSpeaking;
                if(cancellation!=null)cancellation.Dispose();
                if(admitted==Volatile.Read(ref _speechEpoch))
                {
                    _ignoreMicUntilUtc=DateTime.UtcNow.AddMilliseconds(350);
                    lock(_recLock){_preRollQueue.Clear();_consecutiveVoiceFrames=0;}
                    lock(_controlAudioSync)_controlPreRoll.Clear();
                    if(_localWake!=null)_localWake.Reset();
                    if(!_offRequested && result.Status!="failed")WaitForConversationContinuation();
                }
                _technicalDelivery=true;FridaTeamState.EndWork(TeamRole.Voice);_speechGate.Release();
            }
            return result;
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

        /// <summary>Primeiro trecho de ate ~220 caracteres (inicio rapido); os demais de ate ~450 (cada um sintetiza
        /// em menos tempo do que o anterior leva para tocar).</summary>
        private static List<string> SplitSpeechForStreaming(string text)
        {
            List<string> result = new List<string>();
            List<string> first = SplitSpeechIntoChunks(text, 220);
            if (first.Count == 0) return result;
            result.Add(first[0]);
            string rest = (text ?? "").Trim();
            rest = rest.Length > first[0].Length ? rest.Substring(rest.IndexOf(first[0], StringComparison.Ordinal) + first[0].Length).Trim() : "";
            result.AddRange(SplitSpeechIntoChunks(rest, 450));
            return result;
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
                if(cut<remaining.Length && cut>0 && char.IsHighSurrogate(remaining[cut-1]) && char.IsLowSurrogate(remaining[cut]))cut--;

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
                            using (HttpResponseMessage resp = await _http.SendAsync(req,FridaWorkContext.Token))
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
                    using(FridaWorkContext.Token.Register(()=>FridaVerifiedTools.KillOwnedProcessTree(proc)))
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

                        bool exited = proc.WaitForExit(6000);
                        if (!exited)
                        {
                            FridaVerifiedTools.KillOwnedProcessTree(proc);
                            readerTask.Wait(1000);
                            return null;
                        }
                        if (!readerTask.Wait(2000))
                        {
                            FridaVerifiedTools.KillOwnedProcessTree(proc);
                            return null;
                        }
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

        // Registra cada sintese Edge no consumo (painel Zelda), como os demais provedores.
        private async Task<byte[]> SynthesizeEdgeTtsNeuralAsync(string textToSpeak)
        {
            if (string.IsNullOrEmpty(FindEdgeTtsExecutable()) || string.IsNullOrEmpty(FindFfmpegExecutable())) return null;
            ModelQuotaGuard.SetActiveModel("tts_edge", ModelSelection.EdgeSpeech);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] wav = null;
            bool cancelled = false;
            try { wav = await SynthesizeEdgeTtsNeuralCoreAsync(textToSpeak); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            finally
            {
                sw.Stop();
                if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested) cancelled = true;
                if (wav != null && wav.Length > 100)
                    ModelQuotaGuard.RecordRequestSuccess("tts_edge", ModelSelection.EdgeSpeech, sw.ElapsedMilliseconds, 0, 0, (textToSpeak ?? "").Length + " caracteres");
                else if (!cancelled)
                    ModelQuotaGuard.RecordException("tts_edge", ModelSelection.EdgeSpeech, "Edge-TTS nao gerou audio");
                ModelQuotaGuard.ClearActiveModel("tts_edge", ModelSelection.EdgeSpeech);
            }
            return wav;
        }

        private async Task<byte[]> SynthesizeEdgeTtsNeuralCoreAsync(string textToSpeak)
        {
            string edgeExe = FindEdgeTtsExecutable();
            string ffmpeg = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(edgeExe) || string.IsNullOrEmpty(ffmpeg)) return null;
            if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested) throw new OperationCanceledException();

            string tempDir = Path.GetTempPath();
            string tempTxt = Path.Combine(tempDir, "frida_tts_" + Guid.NewGuid().ToString("N") + ".txt");
            string tempMp3 = Path.Combine(tempDir, "frida_edge_" + Guid.NewGuid().ToString("N") + ".mp3");

            try
            {
                // Salva texto em arquivo UTF-8 para evitar qualquer problema de escape de aspas na linha de comando
                File.WriteAllText(tempTxt, textToSpeak, new UTF8Encoding(false));

                ProcessStartInfo psiEdge = new ProcessStartInfo();
                psiEdge.FileName = edgeExe;
                psiEdge.Arguments = "--voice " + ModelSelection.EdgeSpeech + " -f \"" + tempTxt + "\" --write-media \"" + tempMp3 + "\"";
                psiEdge.CreateNoWindow = true;
                psiEdge.UseShellExecute = false;

                using (Process procEdge = Process.Start(psiEdge))
                using(FridaWorkContext.Token.Register(()=>FridaVerifiedTools.KillOwnedProcessTree(procEdge)))
                {
                    // Explicacoes longas (~40 s de fala) levavam mais de 9 s para sintetizar e caiam na voz paga.
                    // Medido em 03/11: 834 caracteres = 10,9 s. Prazo: 10 s + 1,5 s a cada 100 caracteres, ate 90 s.
                    int edgeTimeoutMs = Math.Min(90000, 10000 + (textToSpeak ?? "").Length * 15);
                    bool exited = await Task.Run(delegate { return procEdge.WaitForExit(edgeTimeoutMs); });
                    if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested)
                    {
                        FridaVerifiedTools.KillOwnedProcessTree(procEdge);
                        throw new OperationCanceledException();
                    }
                    if (!exited)
                    {
                        BootLogger.Log("Edge-TTS excedeu " + edgeTimeoutMs + " ms para " + (textToSpeak ?? "").Length + " caracteres.");
                        try { procEdge.Kill(); } catch { }
                        return null;
                    }
                }

                if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested) throw new OperationCanceledException();
                if (!File.Exists(tempMp3)) return null;
                byte[] mp3Bytes = File.ReadAllBytes(tempMp3);
                if (mp3Bytes == null || mp3Bytes.Length < 128) return null;

                // Converte MP3 para WAV PCM 24kHz 16-bit mono via ffmpeg pipe
                byte[] converted = await ConvertMp3ToWavViaFfmpegAsync(
                    ffmpeg,
                    "-loglevel quiet -i pipe:0 -f s16le -ar 24000 -ac 1 -acodec pcm_s16le pipe:1",
                    mp3Bytes,
                    6000).ConfigureAwait(false);
                if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested) throw new OperationCanceledException();
                return converted;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_cancelSpeechRequested || FridaWorkContext.Token.IsCancellationRequested) throw new OperationCanceledException();
                BootLogger.Log("Edge-TTS Excecao: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempTxt)) File.Delete(tempTxt); } catch { }
                try { if (File.Exists(tempMp3)) File.Delete(tempMp3); } catch { }
            }

            return null;
        }

        internal static async Task<byte[]> ConvertMp3ToWavViaFfmpegAsync(
            string ffmpegFileName,
            string ffmpegArguments,
            byte[] mp3Bytes,
            int timeoutMs = 6000)
        {
            if (string.IsNullOrWhiteSpace(ffmpegFileName) || mp3Bytes == null || mp3Bytes.Length == 0)
                return null;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ffmpegFileName;
            psi.Arguments = ffmpegArguments;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;

            using (Process proc = Process.Start(psi))
            using (FridaWorkContext.Token.Register(() => FridaVerifiedTools.KillOwnedProcessTree(proc)))
            {
                if (proc == null) return null;
                byte[] rawPcmOut = null;
                Task readerTask = Task.Run(delegate
                {
                    try
                    {
                        using (MemoryStream msOut = new MemoryStream())
                        {
                            proc.StandardOutput.BaseStream.CopyTo(msOut);
                            rawPcmOut = msOut.ToArray();
                        }
                    }
                    catch { }
                });

                try
                {
                    proc.StandardInput.BaseStream.Write(mp3Bytes, 0, mp3Bytes.Length);
                    proc.StandardInput.BaseStream.Flush();
                    proc.StandardInput.Close();
                }
                catch { }

                bool fExit = await Task.Run(delegate { return proc.WaitForExit(Math.Max(200, timeoutMs)); }).ConfigureAwait(false);
                if (!fExit)
                {
                    FridaVerifiedTools.KillOwnedProcessTree(proc);
                    await Task.WhenAny(readerTask, Task.Delay(1000)).ConfigureAwait(false);
                    return null;
                }

                Task completedReader = await Task.WhenAny(readerTask, Task.Delay(2000)).ConfigureAwait(false);
                if (completedReader != readerTask)
                {
                    FridaVerifiedTools.KillOwnedProcessTree(proc);
                    return null;
                }

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
                return null;
            }
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
            string cacheTag = VoiceCacheTag();
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

            // Modo economia de voz: Edge Neural (gratuito) primeiro; Gemini TTS pago fica apenas como reserva.
            bool edgeTried = false;
            if (_settings.prefer_zero_quota_tts)
            {
                edgeTried = true;
                FridaWorkContext.ThrowIfCancelled();
                try
                {
                    byte[] edgeFirst = await SynthesizeEdgeTtsNeuralAsync(textToSpeak);
                    if (edgeFirst != null && edgeFirst.Length > 100)
                    {
                        BootLogger.Log("TTS Edge Neural " + ModelSelection.EdgeSpeechName + " OK (" + edgeFirst.Length + " bytes) [voz principal / zero-cota]");
                        if (!string.IsNullOrEmpty(cacheFile))
                        {
                            try { File.WriteAllBytes(cacheFile, edgeFirst); } catch { }
                        }
                        return edgeFirst;
                    }
                    BootLogger.Log("TTS Edge Neural indisponivel -> reserva Gemini TTS.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    BootLogger.Log("TTS Edge Neural (principal) Excecao: " + ex.Message + " -> reserva Gemini TTS.");
                }
            }

            string rawKeys = (_settings.api_key ?? "").Trim();
            string[] apiKeys = rawKeys.Split(new char[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (apiKeys.Length == 0 && !string.IsNullOrEmpty(rawKeys))
            {
                apiKeys = new string[] { rawKeys };
            }

            if (apiKeys.Length > 0)
            {
                // Voz natural essencial: apenas TTS 3.8 Lite, sem previews legados.
                string[] rawTts = new string[] { ModelSelection.Speech };

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
                        FridaWorkContext.ThrowIfCancelled();
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
                                            ModelQuotaGuard.RecordHttpResponse("tts", ttsModel, swTts25.ElapsedMilliseconds, resp25, body25);
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
                                            ModelQuotaGuard.RecordHttpResponse("tts", ttsModel, swTts25.ElapsedMilliseconds, resp25, body25);
                                            BootLogger.Log("TTS [" + ttsModel + "] HTTP " + (int)resp25.StatusCode + ": " + (body25 != null && body25.Length > 160 ? body25.Substring(0, 160) : body25));
                                        }
                                    }
                                }
                                ModelQuotaGuard.ClearActiveModel("tts", ttsModel);
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
                                        { "style", _technicalDelivery
                                            ? "natural Brazilian Portuguese female voice, neutral technical delivery, concise, no theatrical emphasis"
                                            : "natural Brazilian Portuguese female voice, relaxed conversational delivery, warm and spontaneous, no theatrical emphasis" }
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
                                using (HttpResponseMessage resp = await _http.SendAsync(req,FridaWorkContext.Token))
                                {
                                    swTtsInt.Stop();
                                    string body = await resp.Content.ReadAsStringAsync();
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        ModelQuotaGuard.RecordHttpResponse("tts", ttsModel, swTtsInt.ElapsedMilliseconds, resp, body);
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
                                        ModelQuotaGuard.RecordHttpResponse("tts", ttsModel, swTtsInt.ElapsedMilliseconds, resp, body);
                                        BootLogger.Log("TTS [" + ttsModel + "] HTTP " + (int)resp.StatusCode + ": " + (body != null && body.Length > 160 ? body.Substring(0, 160) : body));
                                    }
                                }
                            }
                            ModelQuotaGuard.ClearActiveModel("tts", ttsModel);
                        }
                        catch (Exception ex)
                        {
                            BootLogger.Log("TTS [" + ttsModel + "] Excecao: " + ex.Message);
                        }
                    }
                }
            }

            FridaWorkContext.ThrowIfCancelled();
            // 2. Fallback Natural Zero-Cota: Microsoft Edge Neural (pt-BR-FranciscaNeural - voz humana natural de estudio, sem limites de cota)
            if (!edgeTried) try
            {
                byte[] edgeWav = await SynthesizeEdgeTtsNeuralAsync(textToSpeak);
                if (edgeWav != null && edgeWav.Length > 100)
                {
                    BootLogger.Log("TTS Edge Neural " + ModelSelection.EdgeSpeechName + " OK (" + edgeWav.Length + " bytes) [zero-cota / natural]");
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

            FridaWorkContext.ThrowIfCancelled();
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

        // Interromper a fala exige score >= limiar + margem (0,50 + 0,12 = 0,62). Log de 03/11: eco da Frida 0,25-0,57;
        // sua voz 0,67-0,85.
        private const double BargeInMargin = 0.12;

        private byte[] _playbackReference;
        private DateTime _playbackStartUtc;
        private bool IsPlaybackEcho(byte[] microphone)
        {
            byte[] reference=_playbackReference;if(!_isSpeaking || reference==null || microphone.Length<6400)return false;
            int rate=24000,channels=1,bits=16,offset=44,length=reference.Length-44;
            ParseWavHeader(reference,ref rate,ref channels,ref bits,ref offset,ref length);
            if(bits!=16 || channels!=1)return false;
            int samples=Math.Min(microphone.Length/2,12800),start= microphone.Length/2-samples;
            double elapsed=(DateTime.UtcNow-_playbackStartUtc).TotalSeconds;
            for(double lag=0;lag<=400;lag+=0.5)
            {
                double begin=elapsed-samples/(double)SampleRate-lag/1000.0;
                if(begin<0)continue;
                double dot=0,a2=0,b2=0;
                for(int n=0;n<samples;n+=8)
                {
                    int index=offset+2*(int)((begin+n/(double)SampleRate)*rate);
                    if(index<offset || index+1>=offset+length || index+1>=reference.Length)continue;
                    short a=(short)(microphone[(start+n)*2]|microphone[(start+n)*2+1]<<8);
                    short b=(short)(reference[index]|reference[index+1]<<8);
                    dot+=(double)a*b;a2+=(double)a*a;b2+=(double)b*b;
                }
                if(a2>1 && b2>1 && Math.Abs(dot)/Math.Sqrt(a2*b2)>0.82)return true;
            }
            return false;
        }

        private void PlayWavSynchronizedWithHologram(byte[] wavBytes)
        {
            if(!_running || FridaWorkContext.Token.IsCancellationRequested)return;
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
                FridaWorkContext.ThrowIfCancelled();
                player.Load();
                lock(_speechSync){_activePlayer=player;_playbackReference=wavBytes;_playbackStartUtc=DateTime.UtcNow;}
                Stopwatch sw = Stopwatch.StartNew();
                player.Play();
                BootLogger.Log("TTS Play iniciado: " + Math.Round(totalDurationMs) + "ms (" + wavBytes.Length + " bytes)");

                int windowBytes = Math.Max(512, (bytesPerSec / 30) & ~1);
                while (sw.Elapsed.TotalMilliseconds < totalDurationMs + 110)
                {
                    if (_cancelSpeechRequested || !_running || FridaWorkContext.Token.IsCancellationRequested)
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
                        if(_orb!=null)_orb.PushAudioSpectrum(normRms, bands);
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
            s = Regex.Replace(s, @"```[\s\S]*?```", " Codigo disponivel no texto. ");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "");
            s = Regex.Replace(s, @"https?://\S+", "na referencia indicada");
            s = Regex.Replace(s, @"^\s*[#>-]+\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\s+", " ").Trim();
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

        internal static byte[] BuildWavFromPcm16Mono(byte[] pcmData, int sampleRate)
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
            StopCurrentSpeech();CancelVoiceEnrollment();
            _running = false;
            _cancelSpeechRequested = true;
            if (_waveEvent != null)
            {
                try { _waveEvent.Set(); } catch { }
            }
            if(_micThread!=null && _micThread!=Thread.CurrentThread && _micThread.IsAlive)_micThread.Join(3000);
            if(_micThread==null || !_micThread.IsAlive){_micThread=null;if(_waveEvent!=null){_waveEvent.Close();_waveEvent=null;}}
            if (_localWake != null) { _localWake.Dispose(); _localWake = null; }
            var vad=_liveVad;_liveVad=null;if(vad!=null)vad.Dispose();
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
