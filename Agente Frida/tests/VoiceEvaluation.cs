// Dedicated runner: no FridaBrain, no dispatch subscriptions, no tools or actions.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class VoiceEvaluationCase
    {
        public string id, wav, reference, condition, split, session;
        public bool operator_voice = true;
        public string[] critical_terms = new string[0];
    }

    internal static class VoiceEvaluation
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        [STAThread]
        public static int Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            Console.OutputEncoding = Encoding.UTF8;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Cancel.Cancel(); };
            try
            {
                if (args.Length == 1 && args[0] == "--preview-training")
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    using (var http = new HttpClient())
                    using (var voice = new AudioVoiceEngine(new FridaSettings(), null, http))
                    using (var form = new VoiceEnrollmentForm(voice)) System.Windows.Forms.Application.Run(form);
                    return 0; // visual preview: microphone was never started
                }
                if (args.Length == 1 && args[0] == "--check-native") return NativeCheck();
                if (args.Length == 2 && args[0] == "--check-speaker-noise") return SpeakerNoiseCheck(args[1]);
                if (args.Length == 1 && args[0] == "--tests")
                {
                    int failures = 0, passed = 0;
                    VoiceRecognitionTests.Run((name, body) => { try { body(); passed++; Console.WriteLine("PASS " + name); }
                        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } });
                    Console.WriteLine("Voice tests: passed=" + passed + " failed=" + failures);
                    return failures == 0 ? 0 : 1;
                }
                if (args.Length == 4 && args[0] == "--record") return Record(args[1], args[2], args[3]);
                if (args.Length == 4 && args[0] == "--evaluate" && args[3] == "--live") return Evaluate(args[1], args[2]);
                Console.WriteLine("--check-native | --tests | --record output_directory prompts.json calibration|test | --evaluate manifest.json output_directory --live");
                Console.WriteLine("--record grava somente apos Enter. --evaluate --live envia os audios a Groq e pode consumir cota. Nenhum comando e executado.");
                return args.Length == 0 ? 0 : 2;
            }
            catch (OperationCanceledException) { Console.WriteLine("Cancelado."); return 130; }
            catch (Exception ex) { Console.WriteLine("Falha: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        private static FridaSettings Settings()
        {
            string path = Path.Combine(Root, "config.json");
            // Read-only: evaluating must not rewrite the operator's config or provider policies.
            var settings = File.Exists(path) ? Json.Deserialize<FridaSettings>(File.ReadAllText(path, Encoding.UTF8)) : new FridaSettings();
            if (settings == null) throw new InvalidDataException("Configuracao invalida");
            if (string.IsNullOrWhiteSpace(settings.groq_api_key)) settings.groq_api_key = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
            FridaVoiceOptions.Normalize(settings); settings.voice_metrics_enabled = false;
            return settings;
        }
        private static int NativeCheck()
        {
            byte[] silent = AudioVoiceEngine.BuildWavFromPcm16Mono(new byte[64000], 16000);
            bool? speech = SpeakerVoiceProfile.DetectHumanSpeech(silent, .20);
            if (speech != false) throw new Exception("Silero indisponivel ou silencio identificado como fala");
            if (!SpeakerVoiceProfile.EngineAvailable) throw new Exception("WeSpeaker indisponivel: " + SpeakerVoiceProfile.EngineError);
            using (var vad = new SileroVoiceActivity())
            {
                var segments = vad.ExtractTimedSegments(silent, 44, silent.Length - 44);
                if (segments.Count != 0) throw new Exception("Silencio gerou segmentos");
            }
            Console.WriteLine("PASS Silero e WeSpeaker carregados; segmentacao de silencio verificada sem API e sem microfone.");
            Console.WriteLine("Este teste nao mede precisao com voz real.");
            return 0;
        }

        // Offline regression only: generated noise and explicitly supplied negative WAV fixtures.
        private static int SpeakerNoiseCheck(string directory)
        {
            var profile = new SpeakerVoiceProfile(Path.Combine(Root, "frida_speaker_profile.json"));
            if (!profile.IsReady) throw new InvalidDataException("Perfil local indisponivel para a auditoria");
            var rows = new List<object>(); int failures = 0;
            Action<string,byte[]> evaluate = (name, audio) => {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = profile.Check(audio, 1.0); watch.Stop();
                var shortResult = profile.CheckShortInteraction(audio, 1.0);
                if(shortResult.Accepted) { failures++; Console.WriteLine("FAIL short interaction accepted "+name); }
                int pcmOffset,pcmLength;
                if(SpeakerVoiceProfile.GetPcmRange(audio,out pcmOffset,out pcmLength))
                    for(int start=0;start+44800<=pcmLength;start+=44800)
                    {
                        var excerpt=new byte[44800];Buffer.BlockCopy(audio,pcmOffset+start,excerpt,0,excerpt.Length);
                        var excerptResult=profile.CheckShortInteraction(excerpt,1.0);
                        if(excerptResult.Accepted)
                        { failures++;Console.WriteLine("FAIL short 1.4s excerpt accepted "+name+" at byte "+start+" score="+excerptResult.Score.ToString("F3")); }
                    }
                if (result.Accepted) failures++;
                Console.WriteLine((result.Accepted ? "FAIL " : "PASS ") + name + " score=" + result.Score.ToString("F3") + "/" + result.Threshold.ToString("F3") + " speech=" + result.SpeechSeconds.ToString("F2") + "s " + result.Reason);
                rows.Add(new { name=name, accepted=result.Accepted, score=result.Score, threshold=result.Threshold,
                    speech_seconds=result.SpeechSeconds, elapsed_ms=watch.ElapsedMilliseconds, reason=result.Reason });
            };
            foreach (string kind in new[] { "silence", "fan", "white-noise", "keyboard", "clipped-tone" })
            {
                var random = new Random(42); var pcm = new byte[16000*2*5];
                for (int i=0; i<pcm.Length/2; i++) {
                    double v=kind=="fan" ? 900*Math.Sin(2*Math.PI*120*i/16000.0)+300*Math.Sin(2*Math.PI*240*i/16000.0) :
                        kind=="white-noise" ? (random.NextDouble()*2-1)*1600 :
                        kind=="keyboard" ? (i%7000<160 ? (random.NextDouble()*2-1)*10000 : 0) :
                        kind=="clipped-tone" ? (i%16<8?32760:-32760) : 0;
                    short value=(short)v;pcm[i*2]=(byte)value;pcm[i*2+1]=(byte)(value>>8);
                }
                evaluate(kind,AudioVoiceEngine.BuildWavFromPcm16Mono(pcm,16000));
                bool rejected=false;
                try { profile.CreateEnrollmentSample(pcm,new byte[32000]); } catch(InvalidDataException){rejected=true;}
                if(!rejected) { failures++; Console.WriteLine("FAIL enrollment accepted "+kind); }
            }
            foreach (string file in Directory.GetFiles(directory,"*.wav").OrderBy(x=>x)) evaluate(Path.GetFileName(file),File.ReadAllBytes(file));
            File.WriteAllText(Path.Combine(directory,"speaker-noise-results.json"),Json.Serialize(new { threshold=profile.Threshold, failures=failures,
                cases=rows, limitation="Negative fixtures only; operator voice and live overlapping speakers still require physical validation." }),Encoding.UTF8);
            Console.WriteLine("Speaker/noise audit failures="+failures+"; no microphone or network used.");
            return failures==0?0:1;
        }
        private static int Record(string directory, string promptFile, string split)
        {
            if (split != "calibration" && split != "test") throw new ArgumentException("Use calibration ou test");
            var prompts = Json.Deserialize<List<VoiceEvaluationCase>>(File.ReadAllText(promptFile, Encoding.UTF8));
            if (prompts == null || prompts.Count == 0) throw new ArgumentException("Lista de frases vazia");
            string session = DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string folder = Path.Combine(Path.GetFullPath(directory), session);
            Directory.CreateDirectory(folder);
            var recorded = new List<VoiceEvaluationCase>();
            var settings = Settings();
            using (var http = new HttpClient())
            using (var engine = new AudioVoiceEngine(settings, null, http))
            {
                // Pause hands-free throughout collection. IDE capture remains available while paused.
                engine.IsPaused = true; engine.Start();
                foreach (var prompt in prompts)
                {
                    Cancel.Token.ThrowIfCancellationRequested();
                    Console.WriteLine("\nCondicao: " + prompt.condition + " | " + (prompt.reference ?? "[silencio]"));
                    Console.WriteLine("Enter para iniciar 8 segundos de gravacao; q + Enter para encerrar.");
                    if (Console.ReadLine() == "q") break;
                    engine.BeginIdeRecording();
                    Console.WriteLine("GRAVANDO...");
                    if (Cancel.Token.WaitHandle.WaitOne(8000)) Cancel.Token.ThrowIfCancellationRequested();
                    byte[] wav = engine.FinishIdeRecording(false);
                    if (wav == null || wav.Length < 32044) throw new IOException("Microfone nao entregou audio: " + engine.LastCaptureError);
                    string id = session + "-" + recorded.Count.ToString("D3");
                    string name = id + ".wav"; File.WriteAllBytes(Path.Combine(folder, name), wav);
                    // Human confirmation of the reference is required; there is no automatic labeling.
                    Console.WriteLine("Se falou diferente, digite a transcricao correta; Enter confirma a frase exibida.");
                    string reference = Console.ReadLine();
                    recorded.Add(new VoiceEvaluationCase { id = id, wav = name, reference = string.IsNullOrEmpty(reference) ? prompt.reference ?? "" : reference,
                        condition = prompt.condition, split = split, session = session, operator_voice = prompt.operator_voice, critical_terms = prompt.critical_terms });
                    File.WriteAllText(Path.Combine(folder, "manifest.json"), Json.Serialize(recorded), Encoding.UTF8);
                    Console.WriteLine("Salvo localmente: " + name);
                }
                engine.FinishIdeRecording(true);
            }
            Console.WriteLine("Manifesto: " + Path.Combine(folder, "manifest.json"));
            return 0;
        }
        private static int Evaluate(string manifestPath, string output)
        {
            string manifest = Path.GetFullPath(manifestPath);
            var cases = Json.Deserialize<List<VoiceEvaluationCase>>(File.ReadAllText(manifest, Encoding.UTF8));
            if (cases == null || cases.Count == 0) throw new ArgumentException("Nenhuma gravacao no manifesto");
            if (cases.Select(c => c.id).Distinct().Count() != cases.Count || cases.Any(c => string.IsNullOrWhiteSpace(c.id)
                || (c.split != "calibration" && c.split != "test") || string.IsNullOrWhiteSpace(c.session) || c.reference == null))
                throw new ArgumentException("IDs, referencia, split e sessao sao obrigatorios e IDs devem ser unicos");
            foreach (var group in cases.GroupBy(c => c.session))
                if (group.Select(c => c.split).Distinct().Count() != 1) throw new ArgumentException("Uma sessao nao pode misturar calibration e test");
            var settings = Settings();
            if (string.IsNullOrWhiteSpace(settings.groq_api_key)) throw new InvalidOperationException("GROQ_API_KEY/config.json sem credencial");
            // Validate every file before the first paid request.
            var audio = new Dictionary<string, byte[]>();
            foreach (var item in cases)
            {
                byte[] wav = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(manifest), item.wav));
                int offset, length;
                if (wav.Length < 44 || Encoding.ASCII.GetString(wav,0,4) != "RIFF" || !SpeakerVoiceProfile.GetPcmRange(wav, out offset, out length))
                    throw new InvalidDataException("Exige WAV PCM16 mono 16kHz: " + item.id);
                audio.Add(item.id, wav);
            }
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            string resultPath = Path.Combine(output, "results-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,6) + ".jsonl");
            var profile = new SpeakerVoiceProfile(Path.Combine(Root, "frida_speaker_profile.json"));
            if (settings.speaker_lock_enabled && !profile.IsReady) throw new InvalidOperationException("Perfil de locutor nao esta pronto");
            int errors = 0;
            using (var http = new HttpClient())
            {
                foreach (var item in cases)
                {
                    byte[] wav = audio[item.id], filtered = wav;
                    bool speakerAccepted = true; double? score = null;
                    if (settings.speaker_lock_enabled)
                    {
                        var speaker = profile.Check(wav, settings.speaker_lock_tolerance);
                        speakerAccepted = speaker.Accepted; score = speaker.Score;
                        filtered = speaker.FilteredWav ?? wav;
                    }
                    foreach (string variant in new[] { "turbo", "turbo_vocabulary", "large", "large_vocabulary" })
                    {
                        Cancel.Token.ThrowIfCancellationRequested();
                        settings.stt_primary_model = variant.StartsWith("large") ? ModelSelection.TranscriptionAccurate : ModelSelection.Transcription;
                        settings.stt_vocabulary_enabled = variant.EndsWith("vocabulary"); settings.stt_quality_retry_enabled = false;
                        // Same original WAV for all ASR candidates, including samples rejected by speaker lock.
                        // This distinguishes STT errors from identity rejection; no action is ever dispatched.
                        var result = new FridaSpeechRecognizer(http, settings).TranscribeAsync(wav, Cancel.Token).GetAwaiter().GetResult();
                        if (result.Status == FridaTranscriptionStatus.ServiceError) errors++;
                        if (result.Status == FridaTranscriptionStatus.Cancelled) Cancel.Token.ThrowIfCancellationRequested();
                        File.AppendAllText(resultPath, Json.Serialize(new { id = item.id, variant = variant, split = item.split,
                            session = item.session, condition = item.condition, reference = item.reference, hypothesis = result.Text,
                            critical_terms = item.critical_terms, operator_voice = item.operator_voice,
                            status = result.Status.ToString(), speaker_accepted = speakerAccepted, speaker_score = score,
                            scope = "original_audio_stt_and_speaker_gate", filtered_audio_differs = !object.ReferenceEquals(wav, filtered),
                            elapsed_ms = result.ElapsedMilliseconds, seconds = result.DurationSeconds, estimated_usd = result.EstimatedUsd,
                            attempts = result.Attempts, reason = result.Reason }) + Environment.NewLine, Encoding.UTF8);
                        Console.WriteLine(item.id + " " + variant + " " + result.Status + " " + result.ElapsedMilliseconds.ToString("F0") + "ms");
                    }
                }
            }
            Console.WriteLine("Resultados: " + resultPath);
            Console.WriteLine("Comparacao STT em audio original; captura, wake-word e audio filtrado exigem homologacao separada.");
            if (errors > 0) Console.WriteLine("Comparacao incompleta: " + errors + " erro(s) de servico; nao selecionar vencedor.");
            return errors == 0 ? 0 : 3;
        }
    }
}
