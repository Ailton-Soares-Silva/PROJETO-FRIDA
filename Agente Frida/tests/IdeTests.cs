using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FridaAgent
{
    internal static class IdeTests
    {
        // Each window gets its own workspace: drafts persist per conversation, so a shared test
        // catalog would leak one test's draft into the next test's composer.
        private static readonly System.Collections.Generic.List<string> IsolatedRoots = new System.Collections.Generic.List<string>();
        private static FridaConversationStore IsolatedStore()
        {
            string root = Path.Combine(Path.GetTempPath(), "frida-ide-test-" + Guid.NewGuid().ToString("N"));
            IsolatedRoots.Add(root); return new FridaConversationStore(root);
        }
        public static void Run(Action<string, Action> test)
        {
            try { RunAll(test); }
            finally { foreach (string root in IsolatedRoots) try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void RunAll(Action<string, Action> test)
        {
            WorkspaceTests.Run(test);
            test("IDE context restores ambient state and resolves project paths", () =>
            {
                var project = new FridaIdeProject { Name = "teste", Root = Path.GetTempPath(), SshAlias = "" };
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Silent = true, Project = project }))
                {
                    if (!FridaIdeRuntime.Silent) throw new Exception("Chat não está silencioso.");
                    if (FridaIdeRuntime.ResolvePath("arquivo.py") != Path.Combine(project.Root, "arquivo.py")) throw new Exception("Raiz incorreta.");
                    var inherited = Task.Run(() => FridaIdeRuntime.Context.Project.Root).Result;
                    if (inherited != project.Root) throw new Exception("Contexto perdido no executor.");
                }
                if (FridaIdeRuntime.Context != null) throw new Exception("Contexto vazou para a próxima operação.");
            });
            test("IDE terminal streams output before process termination", () =>
            {
                var firstLine = new ManualResetEventSlim(false);
                Action<string> observer = text => { if (text.Contains("IDE_STREAM_FIRST")) firstLine.Set(); };
                FridaIdeRuntime.Changed += observer;
                try
                {
                    var execution = Task.Run(() => FridaVerifiedTools.RunPowerShell("Write-Output ('IDE_' + 'STREAM_FIRST'); Start-Sleep -Seconds 4; Write-Output 'DONE'", 15000));
                    if (!firstLine.Wait(3500)) throw new Exception("Primeira linha não chegou durante o processo.");
                    if (execution.IsCompleted) throw new Exception("O terminal recebeu somente o resultado final.");
                    if (execution.GetAwaiter().GetResult().Status != FridaResultStatus.ProcessCompleted) throw new Exception("Processo falhou.");
                }
                finally { FridaIdeRuntime.Changed -= observer; }
            });
            test("IDE microphone capture can finish and cancel without dispatching a live command", () =>
            {
                var voice = new AudioVoiceEngine(new FridaSettings(), null, new System.Net.Http.HttpClient());
                voice.BeginIdeRecording();
                var process = typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                process.Invoke(voice, new object[] { new byte[3200] });
                byte[] wav = voice.FinishIdeRecording(false);
                if (wav == null || wav.Length != 3244 || voice.IsIdeRecording) throw new Exception("Captura WAV não foi finalizada.");
                voice.BeginIdeRecording();
                process.Invoke(voice, new object[] { new byte[3200] });
                if (voice.FinishIdeRecording(true) != null || voice.IsIdeRecording) throw new Exception("Cancelamento reteve áudio.");
            });
            test("IDE friendly aliases keep their Windows meaning inside a project", () =>
            {
                var method = typeof(FridaBrain).GetMethod("ResolveFriendlyPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = new FridaIdeProject { Root = Path.GetTempPath(), Name = "temporario" } }))
                {
                    string desktop = (string)method.Invoke(null, new object[] { "Desktop" });
                    if (desktop != Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) throw new Exception("O alias Desktop foi resolvido dentro do projeto.");
                    string relative = (string)method.Invoke(null, new object[] { "script.py" });
                    if (relative != Path.Combine(Path.GetTempPath(), "script.py")) throw new Exception("Arquivo relativo perdeu a raiz do projeto.");
                }
            });
            test("Remote project validation updates current revision using shared executor", () =>
            {
                var project = new FridaIdeProject { Root = "/tmp/projeto", SshAlias = "test-server", Name = "remoto" };
                int commands = 0;
                FridaSshWorkspace.TransportOverride = (p, exe, payload, timeout, stream, cancellation) =>
                {
                    if (payload.Contains("setsid sh")) commands++;
                    return new FridaSshWorkspace.TransportResult { ExitCode = 0, Output = payload.StartsWith("cat ") ? "0" : "Validado", Error = "" };
                };
                try
                {
                    using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = project }))
                    {
                        var state = FridaDevelopmentTaskState.CreateNew("Verificar script", project.Root, true);
                        state.RecordFileModification("codigo.py", "", new string('a', 64), "diff", Guid.NewGuid().ToString("N"));
                        var receipt = FridaCodingTools.RunProjectValidation(project.Root, "python3 -m py_compile codigo.py", "compile", 5, state);
                        if (receipt.Status != FridaResultStatus.Verified || !state.CompilationPassed || state.CompiledRevision != state.ProjectRevision || commands != 1) throw new Exception("Validação remota não foi vinculada à revisão correta.");
                        if (state.TrackedFiles[0].FilePath != "/tmp/projeto/codigo.py") throw new Exception("Caminho Linux virou caminho Windows.");
                    }
                }
                finally { FridaSshWorkspace.TransportOverride = null; }
            });
            test("Remote resume keeps task available when host is offline and reconciles after reconnect", () =>
            {
                var project = new FridaIdeProject { Name = "remoto", Root = "/tmp/projeto", SshAlias = "test-server" };
                FridaDevelopmentTaskState state;
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = project }))
                {
                    state = FridaDevelopmentTaskState.CreateNew("Continuar projeto", project.Root, true);
                    state.RecordFileModification("codigo.py", "", new string('a', 64), "diff", Guid.NewGuid().ToString("N"));
                }
                string revision = state.ProjectRevision;
                try
                {
                    FridaSshWorkspace.TransportOverride = (p, exe, payload, timeout, stream, token) => { throw new IOException("offline"); };
                    if (state.CheckExternalChangesOnResume() || !state.ResumeVerificationPending || state.ProjectRevision != revision) throw new Exception("Queda de conexão foi confundida com alteração de arquivo.");
                    FridaSshWorkspace.TransportOverride = (p, exe, payload, timeout, stream, token) => new FridaSshWorkspace.TransportResult { ExitCode = 0, Output = new string('a', 64), Error = "" };
                    if (state.CheckExternalChangesOnResume() || state.ResumeVerificationPending || state.UncertainOperationsPendingReconciliation.Count != 0) throw new Exception("Reconexão não reconciliou a revisão salva.");
                }
                finally { FridaSshWorkspace.TransportOverride = null; }
            });
            test("Chat references survive a failed send and a reference-only retry", () =>
            {
                string received = null;
                bool fail = true;
                var settings = new FridaSettings();
                var voice = new AudioVoiceEngine(settings, null, new System.Net.Http.HttpClient());
                using (var form = new FridaIdeForm((text, project, audio) =>
                {
                    received = text;
                    if (fail) throw new IOException("offline");
                    return Task.FromResult("ok");
                }, () => {}, text => {}, () => {}, settings, "config.example.json", voice, IsolatedStore()))
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var add = typeof(FridaIdeForm).GetMethod("AddReference", flags);
                    var send = typeof(FridaIdeForm).GetMethod("SendDraft", flags);
                    var refs = (System.Windows.Forms.Control)typeof(FridaIdeForm).GetField("_references", flags).GetValue(form);
                    string path = Path.Combine(Path.GetTempPath(), "revisão da equipe.pdf");
                    add.Invoke(form, new object[] { path });
                    add.Invoke(form, new object[] { path });
                    form.ShowSection("Histórico"); form.ShowSection("Chat");
                    ((Task)send.Invoke(form, null)).GetAwaiter().GetResult();
                    if (refs.Controls.Count != 1 || received != "Arquivos do computador:" + Environment.NewLine + path)
                        throw new Exception("Referência perdida ou repetida após falha de envio.");
                    fail = false;
                    ((Task)send.Invoke(form, null)).GetAwaiter().GetResult();
                    if (refs.Controls.Count != 0) throw new Exception("Referência enviada continuou no rascunho.");
                }
            });
            test("IDE disposal unregisters events and cancels microphone capture", () =>
            {
                var eventField = typeof(FridaIdeRuntime).GetField("Changed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var before = eventField.GetValue(null) as Delegate;
                int initial = before == null ? 0 : before.GetInvocationList().Length;
                var settings = new FridaSettings();
                var voice = new AudioVoiceEngine(settings, null, new System.Net.Http.HttpClient());
                var form = new FridaIdeForm((text, project, audio) => Task.FromResult("teste"), () => {}, text => {}, () => {}, settings, "config.example.json", voice, IsolatedStore());
                voice.BeginIdeRecording();
                form.Dispose();
                var after = eventField.GetValue(null) as Delegate;
                int final = after == null ? 0 : after.GetInvocationList().Length;
                if (initial != final || voice.IsIdeRecording || !form.IsDisposed) throw new Exception("A janela reteve assinaturas ou captura depois do encerramento.");
                FridaIdeRuntime.Publish("Evento posterior ao encerramento da janela");
            });
            test("Read-only artifact verification preserves saved-file evidence", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "frida-proof-" + Guid.NewGuid().ToString("N") + ".ps1");
                try
                {
                    var ledger = new FridaExecutionLedger();
                    var saved = FridaVerifiedTools.WriteFile(path, "Write-Output 'ok'", false); ledger.Add(saved);
                    ledger.Add(FridaVerifiedTools.VerifyArtifact(path, "sha256", FridaVerifiedTools.FileSha256(path)));
                    ledger.Revalidate();
                    if (!ledger.FileSaved || saved.SupersededBy != null || saved.Status != FridaResultStatus.Verified) throw new Exception("Conferir o hash apagou a evidencia de gravacao.");
                    File.WriteAllText(path, "conteudo externo");
                    ledger.Add(FridaVerifiedTools.VerifyArtifact(path, "sha256", FridaVerifiedTools.FileSha256(path)));
                    ledger.Revalidate();
                    if (ledger.FileSaved || saved.Status == FridaResultStatus.Verified) throw new Exception("Mudanca externa nao invalidou a evidencia antiga.");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
            test("Artifact history keeps stable links after file relocation", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "frida-artifacts-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root); string source = Path.Combine(root, "before.ps1"), target = Path.Combine(root, "after.ps1");
                var context = new FridaIdeContext { Project = new FridaIdeProject { Name = "artifact-test", Root = root }, Silent = true };
                FridaArtifactRecord observed = null;
                Action<FridaArtifactRecord> handler = artifact => { if (artifact.Path == source) observed = artifact; };
                FridaIdeRuntime.ArtifactChanged += handler;
                try
                {
                    using (FridaIdeRuntime.Enter(context))
                    {
                        var receipt = FridaVerifiedTools.WriteFile(source, "Write-Output 'ok'", false);
                        if (receipt.Status != FridaResultStatus.Verified || observed == null || string.IsNullOrEmpty(observed.Id)) throw new Exception("Evento de arquivo sem destino navegavel.");
                        string id = observed.Id;
                        File.Move(source, target); FridaIdeRuntime.RelocateArtifacts("Computador", source, target, Path.DirectorySeparatorChar);
                        var linked = FridaIdeRuntime.ArtifactsForRequest(context.RequestId).Single(item => item.Id == id);
                        if (linked.Path != target || linked.ProjectRoot != root) throw new Exception("O historico perdeu o arquivo apos mover.");
                    }
                }
                finally { FridaIdeRuntime.ArtifactChanged -= handler; if (File.Exists(source)) File.Delete(source); if (File.Exists(target)) File.Delete(target); }
            });
            test("Meeting archive preserves request and project before any model reply", () =>
            {
                var context = new FridaIdeContext { Project = new FridaIdeProject { Name = "meeting-test", Root = Path.GetTempPath() }, Silent = true };
                using (FridaIdeRuntime.Enter(context))
                {
                    try
                    {
                        FridaTeamState.BeginMeeting("Pauta de teste isolada", new FridaSettings());
                        var record = FridaIdeRuntime.LoadMeetings().Single(meeting => meeting.RequestId == context.RequestId);
                        if (record.Project == null || record.Project.Name != "meeting-test" || record.FinishedUtc != null) throw new Exception("Reuniao inicial nao foi vinculada ao atendimento.");
                    }
                    finally { FridaTeamState.EndMeeting(); }
                    if (FridaIdeRuntime.LoadMeetings().Single(meeting => meeting.RequestId == context.RequestId).FinishedUtc == null) throw new Exception("Encerramento da reuniao nao foi salvo.");
                }
            });
            test("Malformed local state preserves original bytes and returns usable empty state", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "frida-state-audit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory); string path = Path.Combine(directory, "projects.json");
                File.WriteAllText(path, "{truncated");
                var result = FridaIdeRuntime.LoadState(path, () => new System.Collections.Generic.List<FridaIdeProject>());
                string[] backups = Directory.GetFiles(directory, "projects.json.invalid-*");
                if (result.Count != 0 || backups.Length != 1 || File.ReadAllText(backups[0]) != "{truncated") throw new Exception("Arquivo inválido não foi preservado com recuperação utilizável.");
            });
            test("Discarded pending audio cannot overwrite a typed draft", () =>
            {
                var settings = new FridaSettings(); var voice = new AudioVoiceEngine(settings, null, new System.Net.Http.HttpClient());
                using (var form = new FridaIdeForm((text, project, audio) => Task.FromResult("teste"), () => {}, text => {}, () => {}, settings, "config.example.json", voice, IsolatedStore()))
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form);
                    draft.Text = "rascunho digitado";
                    var completion = new TaskCompletionSource<string>();
                    form.TranscribeRecording = wav => completion.Task;
                    typeof(FridaIdeForm).GetMethod("BeginRecording", flags).Invoke(form, null);
                    typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk", flags).Invoke(voice, new object[] { new byte[3200] });
                    var task = (Task)typeof(FridaIdeForm).GetMethod("FinishRecording", flags).Invoke(form, null);
                    typeof(FridaIdeForm).GetMethod("DiscardRecording", flags).Invoke(form, null);
                    completion.SetResult("transcricao descartada");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!task.IsCompleted && clock.ElapsedMilliseconds < 3000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
                    task.GetAwaiter().GetResult();
                    if (draft.Text != "rascunho digitado" || typeof(FridaIdeForm).GetField("_recordedAudio", flags).GetValue(form) != null) throw new Exception("Transcrição tardia alterou o rascunho ou reteve áudio descartado.");
                }
            });
            test("Audio transcription preserves text entered while it was pending", () =>
            {
                var settings = new FridaSettings(); var voice = new AudioVoiceEngine(settings, null, new System.Net.Http.HttpClient());
                using (var form = new FridaIdeForm((text, project, audio) => Task.FromResult("teste"), () => {}, text => {}, () => {}, settings, "config.example.json", voice, IsolatedStore()))
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form);
                    var completion = new TaskCompletionSource<string>(); form.TranscribeRecording = wav => completion.Task;
                    typeof(FridaIdeForm).GetMethod("BeginRecording", flags).Invoke(form, null);
                    typeof(AudioVoiceEngine).GetMethod("ProcessMicPcmChunk", flags).Invoke(voice, new object[] { new byte[3200] });
                    var task = (Task)typeof(FridaIdeForm).GetMethod("FinishRecording", flags).Invoke(form, null);
                    draft.Text = "texto durante a transcrição"; completion.SetResult("fala reconhecida");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!task.IsCompleted && clock.ElapsedMilliseconds < 3000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
                    task.GetAwaiter().GetResult();
                    if (draft.Text != "texto durante a transcrição" + Environment.NewLine + "fala reconhecida") throw new Exception("A transcrição substituiu o rascunho digitado.");
                }
            });
            test("Local files populate on project selection and tolerate an unavailable directory", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "frida-browser-audit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root); string path = Path.Combine(root, "audit.txt"); File.WriteAllText(path, "teste");
                var settings = new FridaSettings(); var voice = new AudioVoiceEngine(settings, null, new System.Net.Http.HttpClient());
                try
                {
                    using (var form = new FridaIdeForm((text, project, audio) => Task.FromResult("teste"), () => {}, text => {}, () => {}, settings, "config.example.json", voice, IsolatedStore()))
                    {
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var projects = (System.Windows.Forms.ComboBox)typeof(FridaIdeForm).GetField("_projects", flags).GetValue(form);
                        var project = new FridaIdeProject { Name = "audit", Root = root, SshAlias = "" };
                        projects.Items.Add(project); projects.SelectedItem = project;
                        var pages = (System.Collections.Generic.Dictionary<string, System.Windows.Forms.Control>)typeof(FridaIdeForm).GetField("_pages", flags).GetValue(form);
                        var tabs = (System.Windows.Forms.TabControl)pages["Arquivos"];
                        var browser = tabs.TabPages[0].Controls[0].Controls[0];
                        var list = browser.Controls.OfType<System.Windows.Forms.ListBox>().Single();
                        if (!list.Items.Contains(path)) throw new Exception("O navegador abriu vazio até clicar Atualizar.");
                        File.Delete(path); Directory.Delete(root);
                        ((Action)typeof(FridaIdeForm).GetField("_refreshFiles", flags).GetValue(form))();
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root); }
            });
            test("Completed silent chat returns health state to idle", () =>
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var names = new[] { "_clean", "_state", "_stage", "_request" };
                var fields = names.Select(name => typeof(FridaRuntimeHealth).GetField(name, flags)).ToArray();
                var previous = fields.Select(field => field.GetValue(null)).ToArray();
                try
                {
                    typeof(FridaRuntimeHealth).GetField("_clean", flags).SetValue(null, false);
                    FridaRuntimeHealth.BeginRequest(Guid.NewGuid().ToString("N"));
                    FridaRuntimeHealth.State("processando", "Verificar comando nativo");
                    FridaRuntimeHealth.EndRequest();
                    if ((string)typeof(FridaRuntimeHealth).GetField("_state", flags).GetValue(null) != "inativa" || typeof(FridaRuntimeHealth).GetField("_request", flags).GetValue(null) != null) throw new Exception("O heartbeat manteve a Frida processando apos a conclusao.");
                }
                finally { for (int index = 0; index < fields.Length; index++) fields[index].SetValue(null, previous[index]); }
            });
            test("SSH refuses changed remote file before upload", () =>
            {
                int uploads = 0;
                var project = new FridaIdeProject { Name = "VPS teste", SshAlias = "test-server", Root = "/tmp/projeto" };
                FridaSshWorkspace.TransportOverride = (p, exe, payload, timeout, stream, cancellation) =>
                {
                    if (exe == "sftp.exe") uploads++;
                    return new FridaSshWorkspace.TransportResult { ExitCode = 0, Output = new string('b', 64) + "  /tmp/projeto/a.py", Error = "" };
                };
                try
                {
                    bool failed = false;
                    try { FridaSshWorkspace.Write(project, "/tmp/projeto/a.py", "print(1)", new string('a', 64)); } catch (IOException) { failed = true; }
                    if (!failed || uploads != 0) throw new Exception("O conflito remoto sobrescreveu o arquivo.");
                }
                finally { FridaSshWorkspace.TransportOverride = null; }
            });
            test("SSH disconnection remains uncertain and does not replay command", () =>
            {
                int launches = 0;
                var project = new FridaIdeProject { Name = "VPS teste", SshAlias = "test-server", Root = "/tmp/projeto" };
                FridaSshWorkspace.TransportOverride = (p, exe, payload, timeout, stream, cancellation) =>
                {
                    if (payload.Contains("setsid sh")) launches++;
                    return new FridaSshWorkspace.TransportResult { ExitCode = 255, Output = "", Error = "connection lost" };
                };
                try
                {
                    using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = project }))
                    {
                        var receipt = FridaVerifiedTools.RunPowerShell("printf teste", 5000);
                        if (receipt.Status != FridaResultStatus.Unconfirmed || launches != 1) throw new Exception("Execução remota repetida ou indevidamente confirmada.");
                    }
                }
                finally { FridaSshWorkspace.TransportOverride = null; }
            });
            test("SSH project rejects shell option injection in alias", () =>
            {
                foreach (string alias in new[] { "-oProxyCommand=example", "host;touch file", "host\nother" })
                {
                    bool rejected = false;
                    try { FridaSshWorkspace.ValidateProject(new FridaIdeProject { Root = "/tmp/projeto", SshAlias = alias }); } catch (ArgumentException) { rejected = true; }
                    if (!rejected) throw new Exception("Alias inválido aceito.");
                }
            });
            test("IDE redacts bearer and named credentials before publishing", () =>
            {
                string secret = "Bearer abcdefghijkl password='minha senha' access_token=abcdefghi";
                string result = FridaIdeRuntime.Redact(secret);
                if (result.Contains("abcdefghijkl") || result.Contains("minha senha") || result.Contains("abcdefghi")) throw new Exception("Credencial exposta.");
            });
            test("IDE selected project controls PowerShell working directory", () =>
            {
                string directory = Path.GetTempPath();
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = new FridaIdeProject { Root = directory, Name = "temporario" } }))
                {
                    var receipt = FridaVerifiedTools.RunPowerShell("(Get-Location).Path", 10000);
                    if (receipt.Status != FridaResultStatus.ProcessCompleted || !receipt.Output.Trim().Contains(directory.TrimEnd('\\'))) throw new Exception(receipt.Output);
                }
            });
            test("Operational finalizer preserves grounded model analysis alongside tool receipts", () =>
            {
                var intent = new FridaRequestIntent { RequiresTools = true };
                intent.Operations.Add(new FridaExpectedOperation { Kind = "read", Operation = "read_file_range" });
                var ledger = new FridaExecutionLedger();
                var receipt = new FridaToolReceipt("read_code", "read_file_range", "AgentBrainAndTray.cs", false);
                receipt.Check("file_exists", true, "true", "true", "File.Exists");
                receipt.Put("path", "AgentBrainAndTray.cs");
                receipt.Put("total_lines", 100);
                receipt.Finish(FridaResultStatus.Verified, "file", "linhas 1..20 lidas", "AgentBrainAndTray.cs: linhas 1..20 lidas");
                ledger.Add(receipt);
                string reply = FridaOperationalReply.Finalize(intent, ledger, "O método principal inicializa o pipeline de áudio e registra os provedores.");
                if (!reply.StartsWith("O método principal inicializa o pipeline de áudio") || !reply.Contains("AgentBrainAndTray.cs"))
                    throw new Exception("A análise do copiloto foi descartada pelo finalizador operacional: " + reply);
            });
            test("Economy routing keeps full token and round budget for technical work requests", () =>
            {
                var plan = FridaTaskRouting.Decide("analise o código do arquivo AgentBrainAndTray.cs e execute os testes", false, false);
                var decision = new FridaEconomyDecision { Valid = true, Kind = FridaTaskKind.Technical, RequiresAction = true, Source = "test" };
                FridaEconomyRouting.Apply(plan, decision, "analise o código do arquivo AgentBrainAndTray.cs e execute os testes", false);
                if (plan.EconomyMode || plan.MaxRounds < 10 || plan.CompletionTokens < 8192)
                    throw new Exception("O modo econômico restringiu indevidamente uma tarefa técnica: rounds=" + plan.MaxRounds + ", tokens=" + plan.CompletionTokens);
            });
            test("IDE without selected project defaults working directory to MyDocuments", () =>
            {
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = null, Silent = true }))
                {
                    string expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (!string.IsNullOrWhiteSpace(expected) && FridaIdeRuntime.DefaultDirectory != expected)
                        throw new Exception("Diretório padrão sem projeto deveria ser Documentos, mas foi: " + FridaIdeRuntime.DefaultDirectory);
                }
            });
        }
    }
}
