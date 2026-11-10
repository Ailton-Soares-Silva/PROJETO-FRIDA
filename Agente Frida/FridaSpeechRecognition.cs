// C# 5 / .NET Framework 4.8. No model training; Groq Whisper + existing Silero.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaTranscriptionStatus { Ok, NoSpeech, SpeakerRejected, LowQuality, ServiceError, Cancelled }

    public sealed class FridaTranscriptionResult
    {
        public FridaTranscriptionStatus Status;
        public string Text = "", Reason = "", Model = "";
        public int Attempts, SuspectSegments;
        public double DurationSeconds, ElapsedMilliseconds, EstimatedUsd;
        public readonly List<FridaTranscriptionAttempt> History = new List<FridaTranscriptionAttempt>();
        public bool IsOk { get { return Status == FridaTranscriptionStatus.Ok && !string.IsNullOrWhiteSpace(Text); } }
        public static FridaTranscriptionResult Create(FridaTranscriptionStatus status, string reason)
        { return new FridaTranscriptionResult { Status = status, Reason = reason }; }
        public string UserMessage
        {
            get
            {
                if (Status == FridaTranscriptionStatus.NoSpeech) return "Não detectei fala. Tente novamente.";
                if (Status == FridaTranscriptionStatus.SpeakerRejected) return "Não consegui confirmar sua voz. Repita uma frase um pouco mais longa.";
                if (Status == FridaTranscriptionStatus.ServiceError) return "Não foi possível transcrever agora. Tente novamente.";
                if (Status == FridaTranscriptionStatus.Cancelled) return "Transcrição cancelada.";
                return "Não entendi com clareza. Repita o comando completo.";
            }
        }
    }

    public sealed class FridaTranscriptionAttempt
    {
        public string Model, Status, Reason;
        public double Milliseconds, EstimatedUsd;
        public int SuspectSegments;
    }

    public static class FridaVoiceOptions
    {
        public static double EndSilence(FridaSettings s)
        { return FiniteRange(s.voice_end_silence_seconds, 0.4, 2.0, 0.60); }
        private static double FiniteRange(double n, double min, double max, double fallback)
        { return double.IsNaN(n) || double.IsInfinity(n) || n < min || n > max ? fallback : n; }
        public static string Primary(FridaSettings s)
        { return ModelSelection.IsAllowed("stt_groq", s.stt_primary_model) ? s.stt_primary_model.ToLowerInvariant() : ModelSelection.Transcription; }
        public static int TimeoutSeconds(FridaSettings s)
        { return s.stt_timeout_seconds >= 3 && s.stt_timeout_seconds <= 60 ? s.stt_timeout_seconds : 20; }
        public static string Vocabulary(FridaSettings s)
        {
            // 200 UTF-8 bytes is a conservative bound below the 224-token API limit.
            string value = (s.stt_vocabulary ?? "").Trim();
            return s.stt_vocabulary_enabled && Encoding.UTF8.GetByteCount(value) <= 200 ? value : "";
        }
        public static void Normalize(FridaSettings s)
        {
            s.voice_end_silence_seconds = EndSilence(s);
            s.stt_primary_model = Primary(s);
            s.stt_timeout_seconds = TimeoutSeconds(s);
            if (s.stt_vocabulary == null) s.stt_vocabulary = "";
            if (Encoding.UTF8.GetByteCount(s.stt_vocabulary) > 200) s.stt_vocabulary_enabled = false;
        }
    }

    public static class FridaTranscriptionQuality
    {
        private static string StringValue(IDictionary<string, object> d, string key)
        { object v; return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        private static bool Number(IDictionary<string, object> d, string key, out double n)
        {
            object v; n = 0;
            return d.TryGetValue(key, out v) && v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n);
        }
        public static FridaTranscriptionResult Parse(string json)
        {
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (root == null) return FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "invalid_json");
                string text = StringValue(root, "text").Trim();
                // Called only after local VAD: an empty provider response is not proof of silence.
                if (text.Length == 0) return FridaTranscriptionResult.Create(FridaTranscriptionStatus.LowQuality, "empty_after_speech");
                object raw; var segments = root.TryGetValue("segments", out raw) ? raw as IEnumerable : null;
                if (segments == null || raw is string || raw is IDictionary)
                    return FridaTranscriptionResult.Create(FridaTranscriptionStatus.LowQuality, "missing_segments");
                int count = 0, suspect = 0; var words = new StringBuilder();
                foreach (object item in segments)
                {
                    var segment = item as IDictionary<string, object>;
                    if (segment == null) { suspect++; continue; }
                    string part = StringValue(segment, "text").Trim();
                    if (part.Length == 0) continue;
                    count++; words.Append(' ').Append(part);
                    double logprob, noSpeech, compression;
                    bool valid = Number(segment, "avg_logprob", out logprob);
                    valid = Number(segment, "no_speech_prob", out noSpeech) && valid;
                    valid = Number(segment, "compression_ratio", out compression) && valid;
                    // Whisper's reference decoder does not discard confident text solely on
                    // no_speech_prob (openai/whisper transcribe.py). Local Silero already found speech.
                    // Missing metadata must never become a confident zero. A low-quality segment
                    // triggers one full-audio retry; removing words could remove a negation/number.
                    if (!valid || noSpeech < 0 || noSpeech > 1 || logprob > 0 || compression < 0
                        || logprob <= -1.10 || compression >= 2.40) suspect++;
                }
                if (count == 0 || suspect > 0)
                    return new FridaTranscriptionResult { Status = FridaTranscriptionStatus.LowQuality,
                        Reason = count == 0 ? "missing_segment_text" : "suspect_segments", SuspectSegments = suspect };
                if (Canonical(text) != Canonical(words.ToString()))
                    return FridaTranscriptionResult.Create(FridaTranscriptionStatus.LowQuality, "segment_text_mismatch");
                // Literal transcript. Names, parentheses and legitimate phrases are not rewritten.
                return new FridaTranscriptionResult { Status = FridaTranscriptionStatus.Ok, Text = text, Reason = "accepted" };
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw;
                return FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "invalid_json");
            }
        }
        private static string Canonical(string s)
        { return System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " "); }
    }

    public sealed class FridaSpeechRecognizer
    {
        private readonly HttpClient _http;
        private readonly FridaSettings _settings;
        private readonly Func<byte[], bool?> _speech;
        private readonly Func<string, bool> _available;
        public FridaSpeechRecognizer(HttpClient http, FridaSettings settings,
            Func<byte[], bool?> speech = null, Func<string, bool> available = null)
        {
            _http = http; _settings = settings;
            _speech = speech ?? (audio => SpeakerVoiceProfile.DetectHumanSpeech(audio, 0.20));
            _available = available ?? (model => !Array.Exists(settings.disabled_models ?? new string[0],
                disabled => string.Equals(disabled, "stt_groq:" + model, StringComparison.OrdinalIgnoreCase))
                && ModelQuotaGuard.IsAvailable("stt_groq", model));
        }

        public async Task<FridaTranscriptionResult> TranscribeAsync(byte[] wav, CancellationToken cancellation)
        {
            var watch = Stopwatch.StartNew();
            var history = new List<FridaTranscriptionAttempt>();
            double seconds = 0;
            FridaTranscriptionResult result;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                int offset, length;
                if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
                    || !SpeakerVoiceProfile.GetPcmRange(wav, out offset, out length))
                    result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "invalid_pcm16_wav");
                else
                {
                    seconds = length / 32000.0;
                    bool? hasSpeech = _speech(wav);
                    cancellation.ThrowIfCancellationRequested();
                    if (!hasSpeech.HasValue) result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "vad_unavailable");
                    else if (!hasSpeech.Value) result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.NoSpeech, "local_vad_no_speech");
                    else
                    {
                        string primary = FridaVoiceOptions.Primary(_settings);
                        result = await RequestAsync(wav, primary, seconds, history, cancellation).ConfigureAwait(false);
                        if (result.Status == FridaTranscriptionStatus.LowQuality && _settings.stt_quality_retry_enabled
                            && primary != ModelSelection.TranscriptionAccurate)
                        {
                            var retry = await RequestAsync(wav, ModelSelection.TranscriptionAccurate, seconds, history, cancellation).ConfigureAwait(false);
                            if (retry.Status == FridaTranscriptionStatus.ServiceError)
                                result.Reason = "quality_retry_" + retry.Reason; // unresolved quality must not trigger a third provider
                            else result = retry;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            { result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.Cancelled, "cancelled"); }
            catch (Exception ex)
            {
                BootLogger.Log("STT local: " + ex.GetType().Name);
                result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "local_processing_error");
            }
            result.DurationSeconds = seconds; result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
            result.History.AddRange(history); result.Attempts = history.Count;
            foreach (var item in history) result.EstimatedUsd += item.EstimatedUsd;
            return result;
        }

        private async Task<FridaTranscriptionResult> RequestAsync(byte[] wav, string model, double seconds,
            List<FridaTranscriptionAttempt> history, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_settings.groq_api_key) || !_available(model))
                return FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "provider_unavailable");
            var watch = Stopwatch.StartNew();
            var attempt = new FridaTranscriptionAttempt { Model = model, Status = "ServiceError", Reason = "request_failed" };
            history.Add(attempt);
            FridaTranscriptionResult result;
            ModelQuotaGuard.SetActiveModel("stt_groq", model);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var form = new MultipartFormDataContent())
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions"))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(FridaVoiceOptions.TimeoutSeconds(_settings)));
                    var audio = new ByteArrayContent(wav);
                    audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    form.Add(audio, "file", "speech.wav"); form.Add(new StringContent(model), "model");
                    form.Add(new StringContent("pt"), "language"); form.Add(new StringContent("0"), "temperature");
                    form.Add(new StringContent("verbose_json"), "response_format");
                    string vocabulary = FridaVoiceOptions.Vocabulary(_settings);
                    if (vocabulary.Length > 0) form.Add(new StringContent(vocabulary), "prompt");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.groq_api_key.Trim());
                    request.Content = form;
                    using (var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        ModelQuotaGuard.RecordHttpResponse("stt_groq", model, watch.Elapsed.TotalMilliseconds, response, body);
                        if (!response.IsSuccessStatusCode)
                            result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "http_" + (int)response.StatusCode);
                        else
                        {
                            attempt.EstimatedUsd = Math.Max(seconds, 10) / 3600.0 * (model == ModelSelection.TranscriptionAccurate ? 0.111 : 0.04);
                            result = FridaTranscriptionQuality.Parse(body);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            { result = FridaTranscriptionResult.Create(cancellation.IsCancellationRequested ? FridaTranscriptionStatus.Cancelled : FridaTranscriptionStatus.ServiceError,
                cancellation.IsCancellationRequested ? "cancelled" : "timeout"); }
            catch (Exception ex)
            {
                ModelQuotaGuard.RecordException("stt_groq", model, ex.GetType().Name);
                result = FridaTranscriptionResult.Create(FridaTranscriptionStatus.ServiceError, "transport_or_response_error");
            }
            finally { ModelQuotaGuard.ClearActiveModel("stt_groq", model); }
            result.Model = model; attempt.Status = result.Status.ToString(); attempt.Reason = result.Reason;
            attempt.SuspectSegments = result.SuspectSegments; attempt.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }
    }

    public static class FridaVoiceMetrics
    {
        private static readonly object Sync = new object();
        public static void Capture(FridaSettings settings, FridaCaptureMetrics capture, string reason, int device)
        {
            if (!settings.voice_metrics_enabled) return;
            try
            {
                string row = new JavaScriptSerializer().Serialize(new { utc = DateTime.UtcNow.ToString("o"),
                    device = device, reason = reason, frames = capture.Frames, seconds = capture.Samples / 16000.0,
                    clipped_fraction = capture.Samples == 0 ? 0 : capture.Clipped / (double)capture.Samples,
                    vad_energy_disagreements = capture.VadEnergyDisagreements, vad_unavailable_frames = capture.VadUnavailable,
                    end_silence = FridaVoiceOptions.EndSilence(settings), energy_gate = settings.voice_vad_energy_gate });
                lock (Sync)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice_capture_metrics.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.WriteAllText(path, "", Encoding.UTF8);
                    File.AppendAllText(path, row + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public static void Record(FridaSettings settings, FridaTranscriptionResult result, string source, int device)
        {
            if (!settings.voice_metrics_enabled) return;
            try
            {
                // Metadata only. Never persist transcripts, PCM, embeddings or credentials here.
                string row = new JavaScriptSerializer().Serialize(new { utc = DateTime.UtcNow.ToString("o"),
                    version = typeof(FridaVoiceMetrics).Assembly.GetName().Version.ToString(),
                    id = Guid.NewGuid().ToString("N"), source = source, device = device,
                    status = result.Status.ToString(), reason = result.Reason, model = result.Model,
                    seconds = result.DurationSeconds, elapsed_ms = result.ElapsedMilliseconds,
                    estimated_usd = result.EstimatedUsd, attempts = result.History,
                    end_silence = FridaVoiceOptions.EndSilence(settings), energy_gate = settings.voice_vad_energy_gate,
                    vocabulary = settings.stt_vocabulary_enabled });
                lock (Sync)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice_metrics.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                        File.WriteAllText(path, "", Encoding.UTF8);
                    File.AppendAllText(path, row + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class FridaCaptureMetrics
    {
        public long Frames, Samples, Clipped, VadEnergyDisagreements, VadUnavailable;
        public void Observe(byte[] pcm, bool? speech, bool energy)
        {
            Frames++; Samples += pcm.Length / 2;
            if (!speech.HasValue) VadUnavailable++;
            if (speech == true && !energy) VadEnergyDisagreements++;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
                if (Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)) >= 32000) Clipped++;
        }
    }
}
