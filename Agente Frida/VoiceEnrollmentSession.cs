using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FridaAgent
{
    public enum VoiceEnrollmentState { Recording, Processing, Completed, Failed, Cancelled }
    public enum VoiceCaptureMode { EnrollPhrase, TestOwnVoice, TestOtherVoice }

    public sealed class VoiceEnrollmentResult
    {
        public VoiceEnrollmentState State;
        public VoiceCaptureMode Mode;
        public bool TestPassed;
        public EnrollmentSample Sample;      // cadastro: assinatura desta frase
        public SpeakerCheckResult Check;     // teste: decisao do filtro
        public string Message;
        public string ErrorCode;
        public double AudioSeconds;
    }

    public sealed class VoiceEnrollmentSnapshot
    {
        public VoiceEnrollmentState State;
        public VoiceCaptureMode Mode;
        public double ElapsedSeconds, RemainingSeconds, Rms;
        public string Microphone, Message;
    }

    /// <summary>Uma captura curta (uma frase ou um teste), processada localmente. Nao transcreve nem envia audio.</summary>
    public sealed class VoiceEnrollmentSession : IDisposable
    {
        public const int PhraseSeconds = 5;
        private readonly object _sync = new object();
        private readonly SpeakerVoiceProfile _profile;
        private readonly Func<DateTime> _clock;
        private readonly string _microphone;
        private readonly int _seconds;
        private readonly VoiceCaptureMode _mode;
        private readonly DateTime _started;
        private DateTime _lastAudio;
        private readonly MemoryStream _pcm = new MemoryStream();
        private readonly TaskCompletionSource<VoiceEnrollmentResult> _completion = new TaskCompletionSource<VoiceEnrollmentResult>();
        private Timer _timer;
        private VoiceEnrollmentState _state = VoiceEnrollmentState.Recording;
        private bool _cancelled;
        private double _rms;
        private string _message;

        public Task<VoiceEnrollmentResult> Completion { get { return _completion.Task; } }
        public VoiceCaptureMode Mode { get { return _mode; } }
        public bool IsActive { get { lock (_sync) return _state == VoiceEnrollmentState.Recording || _state == VoiceEnrollmentState.Processing; } }

        public VoiceEnrollmentSession(SpeakerVoiceProfile profile, string microphone, VoiceCaptureMode mode, Func<DateTime> clock = null, bool automaticTimer = true)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            _profile = profile; _microphone = microphone ?? ""; _mode = mode; _seconds = PhraseSeconds;
            _clock = clock ?? (() => DateTime.UtcNow);
            _started = _lastAudio = _clock();
            _message = mode == VoiceCaptureMode.EnrollPhrase ? "Leia a frase em voz normal." : mode == VoiceCaptureMode.TestOwnVoice ? "Fale qualquer comando, como faria com a Frida." : "Deixe a TV, outra pessoa ou o ruido tocar; nao fale.";
            if (automaticTimer) _timer = new Timer(_ => Tick(), null, 100, 100);
        }

        public VoiceEnrollmentSnapshot Snapshot()
        {
            lock (_sync)
            {
                double elapsed = _pcm.Length / 32000.0;
                return new VoiceEnrollmentSnapshot { State = _state, Mode = _mode, ElapsedSeconds = Math.Min(elapsed, _seconds), RemainingSeconds = Math.Max(0, _seconds - elapsed), Rms = _rms, Microphone = _microphone, Message = _message };
            }
        }

        public void Push(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2) return;
            lock (_sync)
            {
                if (_state != VoiceEnrollmentState.Recording) return;
                int count = Math.Min(_seconds * 32000 - (int)_pcm.Length, pcm.Length) & ~1;
                if (count <= 0) return;
                _pcm.Write(pcm, 0, count); _lastAudio = _clock();
                double energy = 0; for (int i = 0; i < count; i += 2) { short s = (short)(pcm[i] | pcm[i + 1] << 8); energy += (double)s * s; }
                _rms = Math.Sqrt(energy / (count / 2));
            }
        }

        public void Tick()
        {
            byte[] recording;
            lock (_sync)
            {
                if (_state != VoiceEnrollmentState.Recording) return;
                DateTime now = _clock();
                if (_pcm.Length < _seconds * 32000)
                {
                    if ((now - _lastAudio).TotalSeconds > 2.5) FinishLocked(VoiceEnrollmentState.Failed, null, null, "O microfone parou de enviar audio. Use Reiniciar audio ou selecione outro microfone.", "microphone_stalled");
                    return;
                }
                recording = _pcm.ToArray(); _state = VoiceEnrollmentState.Processing; _rms = 0; _message = "Analisando localmente...";
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
            Task.Run(() => Process(recording));
        }

        private void Process(byte[] recording)
        {
            try
            {
                if (_mode == VoiceCaptureMode.EnrollPhrase)
                {
                    var sample = _profile.CreateEnrollmentSample(recording);
                    lock (_sync) if (!_cancelled) FinishLocked(VoiceEnrollmentState.Completed, sample, null, "Frase aproveitada (" + sample.SpeechSeconds.ToString("F1") + " s de fala).", null);
                }
                else
                {
                    var check = _profile.Check(recording, 1.0);
                    bool expected = _mode == VoiceCaptureMode.TestOwnVoice;
                    string detail = check.SpeechSeconds < 0.15 ? check.Reason : check.Reason + " — semelhanca " + check.Score.ToString("F2") + " (limite " + check.Threshold.ToString("F2") + ")";
                    string message = expected
                        ? (check.Accepted ? "Aprovado: " + detail + "." : "Sua voz nao foi aprovada: " + detail + ". Fale mais perto do microfone ou refaca o cadastro.")
                        : (check.Accepted ? "Falhou: o audio foi aceito como sua voz (" + detail + "). Refaca o cadastro sozinho, sem ruido." : "Aprovado: audio recusado (" + detail + ").");
                    lock (_sync) if (!_cancelled) FinishLocked(VoiceEnrollmentState.Completed, null, check, message, null);
                }
            }
            catch (OperationCanceledException) { Cancel(); }
            catch (Exception ex)
            {
                lock (_sync) if (!_cancelled) FinishLocked(VoiceEnrollmentState.Failed, null, null, ex.Message, ex is InvalidDataException ? "phrase_not_accepted" : "engine_error");
            }
            finally { Array.Clear(recording, 0, recording.Length); }
        }

        private void FinishLocked(VoiceEnrollmentState state, EnrollmentSample sample, SpeakerCheckResult check, string message, string errorCode)
        {
            if (_completion.Task.IsCompleted) return;
            double seconds = _pcm.Length / 32000.0;
            _state = state; _message = message; _rms = 0;
            Array.Clear(_pcm.GetBuffer(), 0, (int)_pcm.Length); _pcm.SetLength(0);
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            bool passed = state == VoiceEnrollmentState.Completed && (_mode == VoiceCaptureMode.EnrollPhrase ? sample != null : check != null && check.Accepted == (_mode == VoiceCaptureMode.TestOwnVoice));
            var result = new VoiceEnrollmentResult { State = state, Mode = _mode, TestPassed = passed, Sample = sample, Check = check, Message = message, ErrorCode = errorCode, AudioSeconds = seconds };
            if (check != null) result.Check.FilteredWav = null;
            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoiceTrainingReports"); Directory.CreateDirectory(folder);
                var report = new
                {
                    started_utc = _started.ToString("o"), finished_utc = _clock().ToString("o"), microphone = _microphone, mode = _mode.ToString(), status = state.ToString(),
                    error_code = errorCode, message = message, audio_seconds = seconds, passed = passed,
                    speech_seconds = sample != null ? sample.SpeechSeconds : check != null ? check.SpeechSeconds : 0,
                    score = check != null ? check.Score : 0, threshold = check != null ? check.Threshold : 0
                };
                File.WriteAllText(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(report));
            }
            catch (Exception ex) { BootLogger.Log("Falha ao registrar treino: " + ex.GetType().Name); }
            BootLogger.Log("TreinoVoz: " + _mode + " " + state + "; codigo=" + (errorCode ?? "none") + (check != null ? "; score=" + check.Score.ToString("F3") + "/" + check.Threshold.ToString("F3") : ""));
            _completion.TrySetResult(result);
        }

        public void Cancel()
        {
            lock (_sync)
            {
                if (_completion.Task.IsCompleted) return;
                _cancelled = true;
                FinishLocked(VoiceEnrollmentState.Cancelled, null, null, "Captura cancelada. O perfil anterior foi preservado.", "user_cancelled");
            }
        }

        public void Dispose() { Cancel(); }
    }
}
