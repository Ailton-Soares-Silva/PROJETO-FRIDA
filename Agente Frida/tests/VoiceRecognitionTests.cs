using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    internal static class VoiceRecognitionTests
    {
        private sealed class Provider : HttpMessageHandler
        {
            public readonly List<string> Bodies = new List<string>();
            public Func<int, CancellationToken, Task<HttpResponseMessage>> Reply;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                return await Reply(Bodies.Count, token).ConfigureAwait(false);
            }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static string Response(string text, double logprob = -0.2, double noSpeech = 0.01, double compression = 1.1)
        {
            return new JavaScriptSerializer().Serialize(new { text = text,
                segments = new[] { new { text = text, avg_logprob = logprob, no_speech_prob = noSpeech, compression_ratio = compression } } });
        }
        private static Task<HttpResponseMessage> Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        { return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(value) }); }
        private static byte[] Wave() { return AudioVoiceEngine.BuildWavFromPcm16Mono(new byte[32000], 16000); }
        private static FridaSettings Settings()
        { return new FridaSettings { groq_api_key = "test-not-a-credential", voice_metrics_enabled = false }; }
        public static void Run(Action<string, Action> test)
        {
            test("voice playback stop is bounded to a recent authenticated session",()=>{
                Check(AudioVoiceEngine.CanStopAuthenticatedPlayback("pare",FridaControlCommand.Stop,true,30),"Parada bloqueada");
                Check(!AudioVoiceEngine.CanStopAuthenticatedPlayback("para",FridaControlCommand.Stop,true,30),"Preposicao aceita");
                Check(!AudioVoiceEngine.CanStopAuthenticatedPlayback("pare",FridaControlCommand.Stop,false,30),"Ativou fora da resposta");
                Check(!AudioVoiceEngine.CanStopAuthenticatedPlayback("pare",FridaControlCommand.Stop,true,121),"Sessao expirada aceita");
                Check(!AudioVoiceEngine.CanStopAuthenticatedPlayback("frida fique em off",FridaControlCommand.Off,true,30),"Fallback desligou a sessao");
            });
            test("voice post playback guard retains the beginning of the next request",()=>{
                using(var http=new HttpClient()) using(var engine=new AudioVoiceEngine(Settings(),null,http)) {
                    typeof(AudioVoiceEngine).GetField("_ignoreMicUntilUtc",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(engine,DateTime.UtcNow.AddSeconds(2));
                    var pcm=new byte[2048];pcm[0]=123;
                    typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(engine,new object[]{pcm});
                    var queue=(Queue<byte[]>)typeof(AudioVoiceEngine).GetField("_preRollQueue",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine);
                    Check(queue.Count==1 && queue.Peek()[0]==123,"Inicio descartado pela guarda");
                }
            });
            test("voice preserves Hilton Ailton numbers and negation literally", () => {
                string text = "Não envie 3213 para Hilton. Consulte Ailton.";
                var result = FridaTranscriptionQuality.Parse(Response(text));
                Check(result.IsOk && result.Text == text, "A transcricao foi alterada");
                Check(FridaTranscriptionQuality.Parse(Response("Obrigado por assistir")).IsOk, "Frase legitima descartada por blacklist");
            });
            test("voice missing malformed or empty metadata cannot authorize a command", () => {
                foreach (string body in new[] { "{}", "null", "not json", "{\"text\":\"abra\"}",
                    "{\"text\":\"abra\",\"segments\":[]}", "{\"text\":\"abra\",\"segments\":[{\"text\":\"abra\"}]}" })
                    Check(!FridaTranscriptionQuality.Parse(body).IsOk, "Metadados ausentes aceitos: " + body);
            });
            test("voice a bad negation segment retries whole utterance instead of deleting words", () => {
                var body = new JavaScriptSerializer().Serialize(new { text = "não envie 3213", segments = new[] {
                    new { text = "não", avg_logprob = -2.0, no_speech_prob = .1, compression_ratio = 1.0 },
                    new { text = "envie 3213", avg_logprob = -.1, no_speech_prob = .1, compression_ratio = 1.0 } } });
                var result = FridaTranscriptionQuality.Parse(body);
                Check(result.Status == FridaTranscriptionStatus.LowQuality && result.Text == "" && result.SuspectSegments == 1, "Comando parcial liberado");
            });
            test("voice confident speech is not rejected by no_speech_prob alone", () =>
                Check(FridaTranscriptionQuality.Parse(Response("Não envie", -.1, .7)).IsOk, "Ausencia de fala isolada sobrepos fala confiante"));
            test("voice inconsistent segment text cannot lose a negation", () => {
                string body = Response("não envie").Replace("\"text\":\"não envie\",\"segments\"", "\"text\":\"envie\",\"segments\"");
                Check(!FridaTranscriptionQuality.Parse(body).IsOk, "Texto divergente aceito");
            });
            test("voice silence and unavailable VAD make zero HTTP calls including prompt", () => {
                var provider = new Provider { Reply = (n,t) => { throw new Exception("Enviou silencio"); } };
                using (var http = new HttpClient(provider)) {
                    var settings = Settings(); settings.stt_vocabulary_enabled = true;
                    foreach (bool? speech in new bool?[] { false, null }) {
                        var result = new FridaSpeechRecognizer(http, settings, audio => speech, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                        Check(result.Status == (speech.HasValue ? FridaTranscriptionStatus.NoSpeech : FridaTranscriptionStatus.ServiceError), "Estado incorreto");
                        Check(result.Attempts == 0 && provider.Bodies.Count == 0, "Requisicao indevida");
                    }
                }
            });
            test("voice low quality gets exactly one full audio Large v3 retry", () => {
                var provider = new Provider { Reply = (n,t) => Json(Response("Não envie para Hilton", n == 1 ? -2 : -.1)) };
                using (var http = new HttpClient(provider)) {
                    var result = new FridaSpeechRecognizer(http, Settings(), audio => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(result.IsOk && result.Text.Contains("Hilton") && result.Attempts == 2, "Reserva nao recuperou frase");
                    Check(result.History[0].Model == ModelSelection.Transcription && result.History[1].Model == ModelSelection.TranscriptionAccurate, "Modelos incorretos");
                    Check(provider.Bodies.All(b => b.Contains("speech.wav") && b.Contains("verbose_json")), "Formato alterado");
                    Check(!provider.Bodies.Any(b => b.Contains("name=prompt") || b.Contains("name=\"prompt\"")), "Prompt ativo sem configuracao");
                    Check(Math.Abs(result.EstimatedUsd - 10.0 / 3600 * .151) < 1e-8, "Custo minimo nao contabilizado");
                }
            });
            test("voice persistent uncertainty is never dispatched after second attempt", () => {
                var provider = new Provider { Reply = (n,t) => Json(Response("apague", -2)) };
                using (var http = new HttpClient(provider)) {
                    var result = new FridaSpeechRecognizer(http, Settings(), audio => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(result.Status == FridaTranscriptionStatus.LowQuality && result.Text == "" && provider.Bodies.Count == 2, "Tentativas ilimitadas ou comando liberado");
                }
            });
            test("voice primary Large v3 and retry off each send a single request", () => {
                foreach (bool large in new[] { false, true }) {
                    var settings = Settings(); settings.stt_primary_model = large ? ModelSelection.TranscriptionAccurate : ModelSelection.Transcription;
                    settings.stt_quality_retry_enabled = large;
                    var provider = new Provider { Reply = (n,t) => Json(Response("repita", -2)) };
                    using (var http = new HttpClient(provider)) {
                        var r = new FridaSpeechRecognizer(http, settings, a => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                        Check(!r.IsOk && r.Attempts == 1 && provider.Bodies.Count == 1, "Retry duplicado");
                    }
                }
            });
            test("voice HTTP failure is not silence and has no quality retry", () => {
                var provider = new Provider { Reply = (n,t) => Json("{}", HttpStatusCode.ServiceUnavailable) };
                using (var http = new HttpClient(provider)) {
                    var r = new FridaSpeechRecognizer(http, Settings(), a => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(r.Status == FridaTranscriptionStatus.ServiceError && r.Reason == "http_503" && r.Attempts == 1, "Erro confundido");
                }
            });
            test("voice unavailable quality retry stays low quality rather than service fallback", () => {
                var provider = new Provider { Reply = (n,t) => Json(Response("envie", -2)) };
                using (var http = new HttpClient(provider)) {
                    var r = new FridaSpeechRecognizer(http, Settings(), a => true, m => m == ModelSelection.Transcription).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(r.Status == FridaTranscriptionStatus.LowQuality && r.Text == "" && r.Attempts == 1, "Reserva indisponivel virou falha que liberaria outro provedor");
                }
            });
            test("voice invalid audio and pre-cancelled requests never reach provider", () => {
                var provider = new Provider { Reply = (n,t) => { throw new Exception("HTTP indevido"); } };
                using (var http = new HttpClient(provider)) {
                    var recognizer = new FridaSpeechRecognizer(http, Settings(), a => true, m => true);
                    var invalid = recognizer.TranscribeAsync(new byte[32000], CancellationToken.None).Result;
                    var cancelled = recognizer.TranscribeAsync(Wave(), new CancellationToken(true)).Result;
                    Check(invalid.Status == FridaTranscriptionStatus.ServiceError && cancelled.Status == FridaTranscriptionStatus.Cancelled && provider.Bodies.Count == 0, "Entrada invalida processada");
                }
            });
            test("voice timeout becomes service error without quality retry", () => {
                var provider = new Provider { Reply = (n,t) => { throw new TaskCanceledException(); } };
                using (var http = new HttpClient(provider)) {
                    var r = new FridaSpeechRecognizer(http, Settings(), a => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(r.Status == FridaTranscriptionStatus.ServiceError && r.Reason == "timeout" && r.Attempts == 1, "Timeout virou silencio ou cancelamento do usuario");
                }
            });
            test("voice capture diagnostics count clipping and energy veto without text", () => {
                var capture = new FridaCaptureMetrics();
                capture.Observe(new byte[] {255,127,0,0}, true, false);
                capture.Observe(new byte[] {0,0}, null, false);
                Check(capture.Frames == 2 && capture.Samples == 3 && capture.Clipped == 1 && capture.VadEnergyDisagreements == 1 && capture.VadUnavailable == 1, "Diagnostico incorreto");
            });
            test("voice capture limit never dispatches or transcribes a truncated command", () => {
                var provider = new Provider { Reply = (n,t) => { throw new Exception("Transcreveu fala cortada"); } };
                var settings = Settings(); settings.speaker_lock_enabled = false;
                using (var http = new HttpClient(provider))
                using (var engine = new AudioVoiceEngine(settings, null, http)) {
                    Action<string, object> set = (field, value) => typeof(AudioVoiceEngine).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(engine, value);
                    set("_running", true); set("_offRequested", false); set("_capturingActiveCommand", true); set("_heardVoiceInActive", true);
                    set("_activeStartedUtc", DateTime.UtcNow.AddSeconds(-35)); set("_activeLastVoiceUtc", DateTime.UtcNow);
                    set("_activeCapturePcm", new MemoryStream());
                    int dispatched = 0; engine.OnUserTranscriptReady += (text, wake) => dispatched++;
                    byte[] pcm = new byte[2048]; for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 232; pcm[i + 1] = 3; }
                    typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(engine, new object[] { pcm });
                    Check(dispatched == 0 && provider.Bodies.Count == 0, "Comando parcial liberado");
                    Check((bool)typeof(AudioVoiceEngine).GetField("_followUpWindowActive", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine), "Nao abriu repeticao");
                }
            });
            test("voice cancellation during HTTP does not trigger second request", () => {
                using (var cancel = new CancellationTokenSource()) {
                    var provider = new Provider { Reply = async (n,t) => { cancel.Cancel(); await Task.Delay(1000,t).ConfigureAwait(false); return null; } };
                    using (var http = new HttpClient(provider)) {
                        var r = new FridaSpeechRecognizer(http, Settings(), a => true, m => true).TranscribeAsync(Wave(), cancel.Token).Result;
                        Check(r.Status == FridaTranscriptionStatus.Cancelled && r.Text == "" && r.Attempts == 1, "Cancelamento ignorado");
                    }
                }
            });
            test("voice explicit vocabulary is sent only with proven speech", () => {
                var settings = Settings(); settings.stt_vocabulary_enabled = true; settings.stt_vocabulary = "MBX, SolidWorks";
                var provider = new Provider { Reply = (n,t) => Json(Response("Abra SolidWorks")) };
                using (var http = new HttpClient(provider)) {
                    var r = new FridaSpeechRecognizer(http, settings, a => true, m => true).TranscribeAsync(Wave(), CancellationToken.None).Result;
                    Check(r.IsOk && provider.Bodies.Single().Contains("MBX, SolidWorks"), "Vocabulario ausente");
                }
            });
            test("voice invalid settings normalize to finite supported defaults", () => {
                var settings = Settings(); settings.voice_end_silence_seconds = double.NaN; settings.stt_primary_model = "unknown";
                settings.stt_timeout_seconds = -1; settings.stt_vocabulary_enabled = true; settings.stt_vocabulary = new string('á', 200);
                FridaVoiceOptions.Normalize(settings);
                Check(settings.voice_end_silence_seconds == .6 && settings.stt_primary_model == ModelSelection.Transcription
                    && settings.stt_timeout_seconds == 20 && !settings.stt_vocabulary_enabled, "Config invalida aceita");
                Check(ModelSelection.IsAllowed("stt_groq", ModelSelection.TranscriptionAccurate), "Reserva bloqueada pelo gerenciador");
                Check(!FridaVoiceTiming.EndBySilence(true, 3, .7, 1.0) && FridaVoiceTiming.EndBySilence(true, 3, 1.1, 1.0), "Pausa configurada ignorada");
            });
            test("voice retained segments preserve timeline margins and never copy rejected voice", () => {
                var pcm = Enumerable.Repeat((byte)33, 6400).ToArray();
                var wave = AudioVoiceEngine.BuildWavFromPcm16Mono(pcm, 16000);
                var spans = new[] { new TimedVoiceSegment { Start = 100, Samples = new float[600] },
                    new TimedVoiceSegment { Start = 750, Samples = new float[400] }, new TimedVoiceSegment { Start = 1500, Samples = new float[300] } };
                var filtered = SpeakerVoiceProfile.PreserveAcceptedSegments(wave, 44, pcm.Length, spans, new[] { true, false, true }, 100);
                Check(filtered.Length == wave.Length, "Duracao mudou");
                Check(filtered[44] == 33 && filtered[44 + 2 * 749] == 33, "Margem perdida");
                Check(filtered[44 + 2 * 750] == 0 && filtered[44 + 2 * 1149] == 0, "Outra voz recuperada");
                Check(filtered[44 + 2 * 1400] == 33 && filtered[44 + 2 * 1900] == 0, "Posicao dos trechos mudou");
            });
            test("voice stop controls accept informal PT-BR commands and reject negations and prepositions", () => {
                foreach (string stop in new[] { "para", "pare", "Frida, para!", "Frida pare agora", "para de falar", "para tudo", "chega", "basta", "silêncio", "cala a boca", "quieta", "cancela" })
                    Check(FridaSessionControl.Parse(stop) == FridaControlCommand.Stop, "Comando de parada ignorado: " + stop);
                foreach (string off in new[] { "fique em off", "fique em silêncio", "modo off", "desativar" })
                    Check(FridaSessionControl.Parse(off) == FridaControlCommand.Off, "Comando de off ignorado: " + off);
                foreach (string nonStop in new[] { "não pare", "não para", "como parar", "como para", "para parar", "para [unk]", "vou para casa" })
                    Check(FridaSessionControl.Parse(nonStop) == FridaControlCommand.None, "Falso positivo de parada: " + nonStop);
            });
            test("voice local wake detector suppresses mid-playback partials and accepts compound or finalized stops", () => {
                string cleaned; bool isControl;
                foreach(string partial in new[]{"frida fique em off","para","off"})
                    Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis(partial,false,false,out cleaned,out isControl),"Controle parcial aceito: "+partial);
                foreach(string accidental in new[]{"off","fique em off","[unk] frida pare"})
                    Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis(accidental,true,false,out cleaned,out isControl),"Conversa virou controle: "+accidental);
                Check(LocalWakeDetector.ShouldAcceptLocalHypothesis("frida fique em off",true,false,out cleaned,out isControl) && isControl,"Off explicito nao funciona");
                Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis("[unk] para", false, true, out cleaned, out isControl), "Preposicao parcial aceita");
                Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis("[unk] para", true, true, out cleaned, out isControl), "Preposicao final precedida de [unk] aceita");
                Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis("para", false, true, out cleaned, out isControl), "Parcial de 1 palavra cortou reproducao");
                foreach(string stop in new[]{"para","pare","chega","cancela"})
                    Check(LocalWakeDetector.ShouldAcceptLocalHypothesis(stop,true,true,out cleaned,out isControl) && isControl,"Parada curta ignorada: "+stop);
                Check(LocalWakeDetector.ShouldAcceptLocalHypothesis("frida pare",false,true,out cleaned,out isControl) && isControl,"Parada explicita exige esperar fim da reproducao");
                Check(!LocalWakeDetector.ShouldAcceptLocalHypothesis("frida", false, true, out cleaned, out isControl), "Reset prematuro da chamada durante a fala");
                Check(LocalWakeDetector.ShouldAcceptLocalHypothesis("frida pare", true, true, out cleaned, out isControl) && isControl, "Parada composta 'frida pare' bloqueada");
                Check(LocalWakeDetector.ShouldAcceptLocalHypothesis("frida para", true, true, out cleaned, out isControl) && isControl, "Parada composta 'frida para' bloqueada");
                Check(LocalWakeDetector.ShouldAcceptLocalHypothesis("frida para de falar", true, true, out cleaned, out isControl) && isControl, "Parada composta bloqueada");
            });
            test("voice priority controls never bypass rejected mixed speech or echo", () => {
                double eff; string reason;
                foreach (double score in new[] { 0.483, 0.485, 0.520, 0.548, 0.676, 0.90 }) {
                    var mixed = new SpeakerCheckResult { Accepted = false, Inconclusive = false, Score = score, Threshold = 0.54, SpeechSeconds = 0.8, Reason = "voz diferente" };
                    Check(!AudioVoiceEngine.EvaluatePriorityControlSpeaker(mixed, true, out eff, out reason), "Rejeicao de identidade contornada no score " + score);
                }
                var own = new SpeakerCheckResult { Accepted = true, Score = .85, Threshold = .64, SpeechSeconds = .8 };
                Check(AudioVoiceEngine.EvaluatePriorityControlSpeaker(own, true, out eff, out reason), "Voz confirmada nao pode interromper");
                var echo = new SpeakerCheckResult { Accepted = false, Inconclusive = false, Score = 0.31, Threshold = 0.50, SpeechSeconds = 1.2, Reason = "eco" };
                Check(!AudioVoiceEngine.EvaluatePriorityControlSpeaker(echo, true, out eff, out reason), "Eco da propria TTS aceito");
                var tooShort = new SpeakerCheckResult { Accepted = false, Inconclusive = true, Score = 0.0, Threshold = 0.54, SpeechSeconds = 0.25, Reason = "curta" };
                Check(!AudioVoiceEngine.EvaluatePriorityControlSpeaker(tooShort, true, out eff, out reason), "Trecho inconclusivo aceito");
            });
            test("voice continuous speech is inspected in overlapping windows including the final word", () => {
                var speech = new TimedVoiceSegment { Start = 2400, Samples = new float[16000 * 5 + 1] };
                var spans = SpeakerVoiceProfile.VerificationWindows(new[] { speech });
                Check(spans.Count > 3 && spans[0].Start == speech.Start, "Segmento longo nao subdividido");
                Check(spans.Last().Start + spans.Last().Samples.Length == speech.Start + speech.Samples.Length, "Final da frase ignorado");
                Check(spans.Zip(spans.Skip(1), (a,b) => b.Start < a.Start + a.Samples.Length).All(x => x), "Lacuna entre janelas");
                var shortVoice = new TimedVoiceSegment { Start = 96000, Samples = new float[3200] };
                Check(SpeakerVoiceProfile.VerificationWindows(new[] { speech, shortVoice }).Last().Samples.Length == 3200, "Outra voz curta sumiu");
            });
            test("voice enrollment measures actual signal to noise and rejects distant noisy or clipped audio", () => {
                Func<int,int,byte[]> tone = (count,level) => { var pcm = new byte[count*2]; for(int i=0;i<count;i++){short v=(short)(i%2==0?level:-level);pcm[2*i]=(byte)v;pcm[2*i+1]=(byte)(v>>8);} return pcm; };
                byte[] ambient = tone(16000, 100), spoken = tone(32000, 1200);
                var spans = new[] { new TimedVoiceSegment { Start = 0, Samples = new float[32000] } };
                var q = SpeakerVoiceProfile.MeasureQuality(spoken,0,spoken.Length,spans,ambient);
                Check(q.HasNoiseEstimate && q.SignalToNoiseDb > 21 && SpeakerVoiceProfile.QualityProblem(q,true)==null, "Fala limpa recusada");
                q = SpeakerVoiceProfile.MeasureQuality(spoken,0,spoken.Length,spans,tone(16000,900));
                Check(SpeakerVoiceProfile.QualityProblem(q,true)!=null && SpeakerVoiceProfile.QualityProblem(q,false)!=null, "Ruido quase tao alto quanto fala aceito");
                q = SpeakerVoiceProfile.MeasureQuality(tone(32000,50),0,spoken.Length,spans,ambient);
                Check(SpeakerVoiceProfile.QualityProblem(q,true)!=null, "Voz distante aceita");
                q = SpeakerVoiceProfile.MeasureQuality(tone(32000,32760),0,spoken.Length,spans,ambient);
                Check(SpeakerVoiceProfile.QualityProblem(q,true)!=null, "Saturacao aceita");
            });
            test("voice majority owner cannot hide another speaker inside a continuous segment", () => {
                var timed=new[] { new TimedVoiceSegment { Start=0, Samples=new float[16000*5] } };
                bool inconclusive; int calls=0;
                var owner=new double[]{1,0}; var other=new double[]{0,1};
                string reason=SpeakerVoiceProfile.VerifySpeechWindows(timed,owner,.62,samples => ++calls==3?other:owner,out inconclusive);
                Check(reason!=null && calls==3 && !inconclusive,"Voz minoritaria ignorada");
                Check(SpeakerVoiceProfile.VerifySpeechWindows(timed,owner,.62,samples=>owner,out inconclusive)==null,"Voz consistente recusada");
                var shortOther=new TimedVoiceSegment { Start=90000, Samples=new float[3200] };
                Check(SpeakerVoiceProfile.VerifySpeechWindows(timed.Concat(new[]{shortOther}).ToList(),owner,.62,samples=>owner,out inconclusive)!=null && inconclusive,"Trecho curto desconhecido liberado");
                Check(SpeakerVoiceProfile.VerifySpeechWindows(timed,owner,.62,samples=>new double[]{double.NaN,1},out inconclusive)!=null,"Embedding invalido liberado");
            });
            test("voice enrollment calibration does not weaken inconsistent or malformed profiles", () => {
                var owner=new double[256];owner[0]=1;
                var phrases=Enumerable.Range(0,5).Select(i=>(double[])owner.Clone()).ToList();
                var summary=SpeakerVoiceProfile.Summarize(phrases);
                Check(summary.Threshold>=.58 && summary.Threshold<=.72 && summary.WorstIndex<0,"Calibracao inconsistente");
                phrases[3]=new double[256];phrases[3][1]=1;
                Check(SpeakerVoiceProfile.Summarize(phrases).WorstIndex==3,"Frase de outra pessoa passou");
                bool failed=false;phrases[3][0]=double.NaN;
                try { SpeakerVoiceProfile.Summarize(phrases); } catch(InvalidDataException){failed=true;}
                Check(failed,"Assinatura invalida aceita");
            });
            test("voice negative test cannot claim other-speaker rejection from silence or unavailable profile", () => {
                Check(!VoiceEnrollmentSession.IsNegativeTestConclusive(new SpeakerCheckResult { SpeechSeconds=0 }), "Silencio passou por outra pessoa");
                Check(!VoiceEnrollmentSession.IsNegativeTestConclusive(new SpeakerCheckResult { SpeechSeconds=2, Threshold=0 }), "Motor indisponivel passou");
                Check(!VoiceEnrollmentSession.IsNegativeTestConclusive(new SpeakerCheckResult { SpeechSeconds=2, Threshold=.6, Inconclusive=true }), "Inconclusivo passou");
                Check(VoiceEnrollmentSession.IsNegativeTestConclusive(new SpeakerCheckResult { SpeechSeconds=2, Threshold=.6 }), "Outra voz nao testavel");
            });
            test("voice short interaction can verify identity under playback without weakening command quality", () => {
                var q=new SpeakerVoiceProfile.AudioQuality { SpeechRms=1200,NoiseRms=800,HasNoiseEstimate=true,SignalToNoiseDb=3.5 };
                Check(SpeakerVoiceProfile.ShortInteractionQualityProblem(q,true)==null,"TTS impediu identificacao da chamada");
                Check(SpeakerVoiceProfile.ShortInteractionQualityProblem(q,false)!=null,"Comando ruidoso liberado");
                q.Clipping=.05;Check(SpeakerVoiceProfile.ShortInteractionQualityProblem(q,true)!=null,"Saturacao liberada");
                q.Clipping=0;q.SpeechRms=20;Check(SpeakerVoiceProfile.ShortInteractionQualityProblem(q,true)!=null,"Silencio liberado");
            });
            test("voice training silence preparation cancellation and microphone stall are deterministic", () => {
                DateTime now=DateTime.UtcNow;
                var profile=new SpeakerVoiceProfile(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));
                using(var session=new VoiceEnrollmentSession(profile,"mic",VoiceCaptureMode.EnrollPhrase,()=>now,false)) {
                    Check(session.Snapshot().State==VoiceEnrollmentState.Calibrating && session.IsActive, "Nao preparou ambiente");
                    session.Push(new byte[32000]);
                    Check(session.Snapshot().State==VoiceEnrollmentState.Recording && session.Snapshot().ElapsedSeconds==0, "Silencio virou voz cadastrada");
                    session.Push(new byte[6400]);
                    Check(Math.Abs(session.Snapshot().ElapsedSeconds-.2)<.001, "Duracao incorreta");
                    session.Cancel(); session.Push(new byte[32000]);
                    Check(!session.IsActive && session.Completion.Result.State==VoiceEnrollmentState.Cancelled, "Cancelamento nao terminal");
                }
                using(var session=new VoiceEnrollmentSession(profile,"mic",VoiceCaptureMode.EnrollPhrase,()=>now,false)) {
                    now=now.AddSeconds(3); session.Tick();
                    Check(session.Completion.Result.ErrorCode=="microphone_stalled", "Microfone parado nao diagnosticado");
                }
            });
            test("voice training never feeds command audio and ignores stop phrases", () => {
                var profile=new SpeakerVoiceProfile(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));
                var settings=Settings(); settings.speaker_lock_enabled=false;
                using(var http=new HttpClient())
                using(var engine=new AudioVoiceEngine(settings,null,http))
                using(var session=new VoiceEnrollmentSession(profile,"mic",VoiceCaptureMode.EnrollPhrase,automaticTimer:false)) {
                    typeof(AudioVoiceEngine).GetField("_enrollment",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(engine,session);
                    int controls=0; engine.OnPriorityControl += command => controls++;
                    typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(engine,new object[]{new byte[6400]});
                    var queue=(System.Collections.ICollection)typeof(AudioVoiceEngine).GetField("_controlPreRoll",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine);
                    Check(queue.Count==0,"Treinamento vazou para comando");
                    typeof(AudioVoiceEngine).GetMethod("HandleLocalRecognition",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(engine,new object[]{"Frida, pare"});
                    Check(controls==0 && session.IsActive,"Frase cancelou o treinamento");
                }
            });
        }
    }
}
