using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FridaAgent
{
    internal static class WorkspaceTests
    {
        [STAThread]
        public static int Main()
        {
            // Same text rendering as FridaAgent's Main, so measured layouts match the real app.
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            int failed = 0;
            Run((name, action) => { try { action(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } });
            Console.WriteLine("Workspace failures=" + failed); return failed == 0 ? 0 : 1;
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        // RichEdit does not paint into DrawToBitmap; PrintWindow (full content) captures what the user sees.
        private static void Snapshot(System.Windows.Forms.Control form, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    IntPtr dc = g.GetHdc();
                    try { PrintWindow(form.Handle, dc, 2); } finally { g.ReleaseHdc(dc); }
                }
                bitmap.Save(path);
            }
        }
        private static void InStore(Action<FridaConversationStore> action)
        {
            string root = Path.Combine(Path.GetTempPath(), "frida-workspace-test-" + Guid.NewGuid().ToString("N"));
            try { action(new FridaConversationStore(root)); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        // Layout integrity: interactive siblings never overlap, stay inside their (non-scrolling)
        // parent, keep a 32 px (scaled) target and no visible control falls back to a light background.
        private static bool Interactive(System.Windows.Forms.Control c)
        {
            if (c is System.Windows.Forms.ButtonBase || c is System.Windows.Forms.ComboBox || c is System.Windows.Forms.ListBox || c is SegmentedTabs || c is System.Windows.Forms.TreeView || c is InputFrame) return true;
            var box = c as System.Windows.Forms.TextBoxBase; if (box != null) return c is System.Windows.Forms.TextBox || !box.ReadOnly;
            var chip = c as Chip; return chip != null && chip.Clickable;
        }
        private static string Describe(System.Windows.Forms.Control c)
        {
            string name = !string.IsNullOrEmpty(c.AccessibleName) ? c.AccessibleName : c.Text;
            return c.GetType().Name + "'" + (name.Length > 40 ? name.Substring(0, 40) : name) + "'";
        }
        private static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root)
        {
            foreach (System.Windows.Forms.Control child in root.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        private static void InspectLayout(System.Windows.Forms.Control root, string where, System.Collections.Generic.List<string> problems)
        {
            foreach (var parent in new[] { root }.Concat(Descendants(root)))
            {
                if (!parent.Visible) continue;
                var scrollable = parent as System.Windows.Forms.ScrollableControl;
                bool scrolls = (scrollable != null && scrollable.AutoScroll) || parent is System.Windows.Forms.TabControl;
                var kids = parent.Controls.Cast<System.Windows.Forms.Control>().Where(c => c.Visible && c.Width > 0 && c.Height > 0).ToList();
                foreach (var c in kids)
                {
                    if (!scrolls && Interactive(c))
                    {
                        // Visible region = intersection of every ancestor up to the window (or up to a
                        // scrolling container, whose content may legitimately extend beyond view).
                        var rect = root.RectangleToClient(parent.RectangleToScreen(c.Bounds));
                        var visible = root.ClientRectangle; bool insideScroller = false;
                        for (var p = parent; p != null && p != root; p = p.Parent)
                        {
                            var sc = p as System.Windows.Forms.ScrollableControl;
                            if (sc != null && sc.AutoScroll) { insideScroller = true; break; }
                            visible.Intersect(root.RectangleToClient(p.RectangleToScreen(p.ClientRectangle)));
                        }
                        visible.Inflate(1, 1);
                        if (!insideScroller && !visible.Contains(rect)) problems.Add(where + ": cortado " + Describe(c) + " " + rect + " visível " + visible + " (pai " + Describe(parent) + ")");
                    }
                    if (c is System.Windows.Forms.ButtonBase && (c.Height < FridaTheme.Px(32) - 1 || c.Width < FridaTheme.Px(32) - 1))
                        problems.Add(where + ": alvo pequeno " + Describe(c) + " " + c.Size);
                    if (!(c is System.Windows.Forms.TabControl) && !(c is System.Windows.Forms.PictureBox) && c.BackColor.A == 255 && c.BackColor.GetBrightness() > 0.6f)
                        problems.Add(where + ": fundo claro " + Describe(c) + " " + c.BackColor);
                }
                var interactive = kids.Where(Interactive).ToList();
                for (int i = 0; i < interactive.Count; i++)
                    for (int j = i + 1; j < interactive.Count; j++)
                        if (interactive[i].Bounds.IntersectsWith(interactive[j].Bounds))
                            problems.Add(where + ": sobreposição " + Describe(interactive[i]) + " × " + Describe(interactive[j]) + " em " + Describe(parent));
            }
        }
        private static void Rejected(Action action)
        {
            bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Operação indevida aceita.");
        }
        public static void Run(Action<string, Action> test)
        {
            test("Workspace diagnostics stream to the origin and redact before persistence", () => InStore(store =>
            {
                var a = store.Create("A", FridaWorkspaceSection.Code, FridaConversationKind.Conversation);
                var b = store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var line = new System.Threading.ManualResetEventSlim(false);
                Action<FridaDiagnosticEvent> observer = e => { if (e.ConversationId == a.Id && e.Kind == "execution" && e.Text.Contains("QA_STREAM")) line.Set(); };
                FridaDiagnostics.Published += observer;
                try {
                    using (FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = a.Id, Store = store }))
                    {
                        FridaDiagnostics.Publish("motor", "password=segredo; classificando");
                        var run = Task.Run(() => FridaVerifiedTools.RunPowerShell("Write-Output ('QA_' + 'STREAM'); Start-Sleep -Seconds 3", 10000));
                        Check(line.Wait(2500) && !run.IsCompleted, "Saída não chegou enquanto o processo executava.");
                        run.GetAwaiter().GetResult();
                    }
                    Check(store.Diagnostics(b.Id).Count == 0 && store.Diagnostics(a.Id).Any(e => e.OperationId != null), "Destino ou recibo ausente.");
                    Check(store.Diagnostics(a.Id).All(e => !e.Text.Contains("segredo")), "Segredo persistido.");
                    string path = Path.Combine(store.Root, "Conversations", a.Id, "diagnostics.jsonl"); File.AppendAllText(path, "{partial");
                    Check(new FridaConversationStore(store.Root).Diagnostics(a.Id).Count > 2, "Linha interrompida ocultou histórico.");
                } finally { FridaDiagnostics.Published -= observer; line.Dispose(); }
            }));
            test("Workspace Zelda fills resized bounds and maps clicks to the painted tabs", () =>
            {
                using (var dashboard = new ZeldaExecutiveLlmDashboard(new FridaSettings(), "config.example.json", () => {}))
                {
                    dashboard.EnableEmbeddedLayout();
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    foreach (var size in new[] { new System.Drawing.Size(860,600), new System.Drawing.Size(1380,800), new System.Drawing.Size(1720,1200) })
                    {
                        dashboard.ClientSize = size; dashboard.CreateControl();
                        using (var bitmap = new System.Drawing.Bitmap(size.Width,size.Height))
                        {
                            dashboard.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,size));
                            string render = Environment.GetEnvironmentVariable("FRIDA_QA_RENDER_DIR");
                            if (!string.IsNullOrEmpty(render)) { Directory.CreateDirectory(render); bitmap.Save(Path.Combine(render,"zelda-"+size.Width+"x"+size.Height+".png")); }
                        }
                        float scale = (float)typeof(ZeldaExecutiveLlmDashboard).GetProperty("EmbeddedScale",flags).GetValue(dashboard,null);
                        var side = (System.Drawing.Rectangle)typeof(ZeldaExecutiveLlmDashboard).GetProperty("SidePanelRect",flags).GetValue(dashboard,null);
                        Check(Math.Abs(side.Right * scale - size.Width) < 30 && Math.Abs(side.Bottom * scale - size.Height) < 30, "Faixa vazia à direita ou abaixo.");
                        var tabs = (System.Drawing.Rectangle[])typeof(ZeldaExecutiveLlmDashboard).GetField("_roomTabRects",flags).GetValue(dashboard);
                        var center = tabs[2];
                        typeof(ZeldaExecutiveLlmDashboard).GetMethod("OnDashboardMouseDown",flags).Invoke(dashboard,new object[] { dashboard, new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,(int)((center.X+center.Width/2)*scale),(int)((center.Y+center.Height/2)*scale),0) });
                        Check((DashboardRoom)typeof(ZeldaExecutiveLlmDashboard).GetField("_currentRoom",flags).GetValue(dashboard) == DashboardRoom.Office, "Clique desalinhado.");
                    }
                    Check(dashboard.ContextMenuStrip != null && dashboard.ContextMenuStrip.Items.Count >= 7, "Ações removidas sem alternativa acessível.");
                }
            });
            test("Workspace routing forks development with context but no pending commands", () => InStore(store =>
            {
                var p = store.RegisterProject("P", store.Root);
                var a = store.Route(null, "Pesquise filamento PP", FridaWorkspaceSection.Chat, false, p.Id);
                store.PutMessage(new FridaConversationMessage { ConversationId = a.Id, Role = "user", Text = "contexto anterior" });
                store.SaveBrainState(a.Id, new FridaBrainConversationState { PendingNative = "Desligue", Memory = new System.Collections.Generic.List<FridaMemoryTurn> { new FridaMemoryTurn { Role = "user", Text = "contexto anterior" } } });
                var b = store.Route(a.Id, "Crie um script Python", FridaWorkspaceSection.Chat);
                Check(b.Id != a.Id && b.Section == FridaWorkspaceSection.Code && b.ProjectId == p.Id && b.OriginConversationId == a.Id, "Encaminhamento perdeu origem/projeto.");
                Check(store.LoadBrainState(b.Id).Memory.Single().Text == "contexto anterior" && store.LoadBrainState(b.Id).PendingNative == null, "Contexto ou pendência incorretos.");
                Check(store.Route(b.Id, "continue", FridaWorkspaceSection.Chat).Id == b.Id, "Continuação criou conversa redundante.");
                Check(FridaWorkspaceRouting.Classify("Gere uma planilha com os valores") == FridaWorkspaceSection.Chat, "Planilha foi tratada como dev.");
                store.Move(b.Id, FridaWorkspaceSection.Chat);
                Check(store.Route(b.Id, "Explique o código", FridaWorkspaceSection.Code).Section == FridaWorkspaceSection.Chat, "Correção manual ignorada.");
            }));
            test("Workspace meeting rounds retain all contributions and version artifacts", () => InStore(store =>
            {
                var c = store.Create("Reunião", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                var context = new FridaIdeContext { ConversationId = c.Id, Store = store, Kind = FridaConversationKind.Meeting };
                using (FridaIdeRuntime.Enter(context))
                {
                    try {
                        FridaTeamState.BeginMeeting("Pauta", new FridaSettings());
                        for (int i = 0; i < 82; i++) FridaTeamState.Add(TeamRole.Manager, "modelo-teste", "contribuição " + i, "Equipe", true);
                        FridaMeetingWorkspace.Plan("{\"plan\":\"Primeiro plano\",\"workflow\":[\"etapa A\",\"etapa B\"]}");
                    } finally { FridaTeamState.EndMeeting(); }
                    context.RequestId = Guid.NewGuid().ToString("N");
                    try {
                        FridaTeamState.BeginMeeting("Continuação", new FridaSettings());
                        FridaTeamState.Add(TeamRole.Analyst, "modelo-teste", "Indisponível", "Equipe", false);
                        FridaMeetingWorkspace.Plan("{\"plan\":\"Plano revisado\",\"workflow\":[\"etapa A\",\"etapa C\"]}");
                    } finally { FridaTeamState.EndMeeting(); }
                }
                var reopened = new FridaConversationStore(store.Root);
                Check(reopened.Messages(c.Id, 0, 100).Count == 83, "Reunião truncada entre rodadas.");
                Check(reopened.Messages(c.Id, 0, 100).Last().Status == "unavailable", "Falha de participante oculta.");
                var plans = reopened.Artifacts(c.Id).Where(a => a.Kind == "plan").ToList();
                Check(plans.Count == 2 && plans.Max(a => a.Version) == 2 && reopened.Artifacts(c.Id).Count(a => a.Kind == "flow") == 2, "Versões sobrescritas.");
            }));
            test("Workspace UI keeps late replies in origin and preserves another draft", () => InStore(store =>
            {
                var a = store.Create("A", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var b = store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.UpdateDraft(b.Id, "rascunho B", new[] { "arquivo-b.txt" });
                var completion = new TaskCompletionSource<string>(); int stopped = 0;
                var settings = new FridaSettings();
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new FridaIdeForm((text, project, audio) => completion.Task, () => { stopped++; }, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                {
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var nav = (System.Collections.IDictionary)typeof(FridaIdeForm).GetField("_sectionButtons", flags).GetValue(form);
                    Check(nav.Count == 4 && nav.Contains("Chat") && nav.Contains("Código"), "Navegação antiga permanece.");
                    form.SelectConversation(a.Id);
                    var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form); draft.Text = "pergunta A";
                    var pending = (Task)typeof(FridaIdeForm).GetMethod("SendDraft", flags).Invoke(form, null);
                    form.SelectConversation(b.Id); Check(draft.Text == "rascunho B", "Rascunho B não carregado.");
                    typeof(FridaIdeForm).GetMethod("StopWorkspaceExecution", flags).Invoke(form, null); Check(stopped == 0, "Parou execução de outra conversa.");
                    completion.SetResult("resposta A");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!pending.IsCompleted && clock.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    Check(pending.IsCompleted, "Envio não terminou."); pending.GetAwaiter().GetResult();
                    Check(store.Messages(a.Id).Count == 2 && store.Messages(b.Id).Count == 0, "Resposta desviada.");
                    Check(form.SelectedConversationId == b.Id && draft.Text == "rascunho B", "Resposta apagou o outro rascunho.");
                    string render = Environment.GetEnvironmentVariable("FRIDA_QA_RENDER_DIR");
                    if (!string.IsNullOrEmpty(render)) {
                        Directory.CreateDirectory(render); form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-20000,-20000); form.ShowInTaskbar = false; form.Show(); System.Windows.Forms.Application.DoEvents();
                        using (var bitmap = new System.Drawing.Bitmap(form.Width,form.Height)) {
                            form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height)); bitmap.Save(Path.Combine(render,"workspace-chat.png"));
                        }
                        form.ShowSection("Terminal");
                        using (var bitmap = new System.Drawing.Bitmap(form.Width,form.Height)) {
                            form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height)); bitmap.Save(Path.Combine(render,"workspace-terminal.png"));
                        }
                    }
                }
            }));
            test("Workspace brain isolates memory and pending native choices and restores after restart", () => InStore(store =>
            {
                var a = store.Create("A", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var b = store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                using (var http = new System.Net.Http.HttpClient())
                {
                    var brain = new FridaBrain(new FridaSettings(), null, null, http); brain.ConversationStore = store;
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var pending = typeof(FridaBrain).GetField("_pendingNativeRequest", flags);
                    var choices = typeof(FridaBrain).GetField("_pendingOpenCandidates", flags);
                    var memory = typeof(FridaBrain).GetField("_chatMemory", flags);
                    brain.ActivateBrainConversation(a.Id);
                    pending.SetValue(brain, "Desligue"); choices.SetValue(brain, new[] { "arquivo A" });
                    ((System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>)memory.GetValue(brain)).Add(new System.Collections.Generic.KeyValuePair<string,string>("user", "assunto A"));
                    brain.ActivateBrainConversation(b.Id);
                    Check(pending.GetValue(brain) == null && choices.GetValue(brain) == null && ((System.Collections.ICollection)memory.GetValue(brain)).Count == 0, "Pendência vazou entre conversas.");
                    brain.ActivateBrainConversation(a.Id);
                    Check((string)pending.GetValue(brain) == "Desligue" && ((string[])choices.GetValue(brain)).Single() == "arquivo A", "Retomada perdeu pendência.");
                    var restarted = new FridaBrain(new FridaSettings(), null, null, http); restarted.ConversationStore = store; restarted.ActivateBrainConversation(a.Id);
                    Check((string)pending.GetValue(restarted) == "Desligue" && ((System.Collections.ICollection)memory.GetValue(restarted)).Count == 1, "Reinício perdeu contexto.");
                    store.Trash(a.Id); restarted.ActivateBrainConversation(b.Id);
                    Rejected(() => restarted.ActivateBrainConversation(a.Id));
                }
            }));
            test("Workspace imports available conversation memory without inventing context", () => InStore(store =>
            {
                var c = store.Create("Importada", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.RecordReport(new FridaConversationEntry { conversation_id = c.Id, request_id = Guid.NewGuid().ToString("N"), request = "antes", response = "depois", started_utc = "2026-01-01T00:00:00Z", finished_utc = "2026-01-01T00:00:01Z" });
                var state = store.LoadBrainState(c.Id);
                Check(state.Memory.Count == 2 && state.Memory[0].Role == "user" && state.Memory[1].Role == "model", "Histórico importado não alimenta contexto.");
            }));
            test("Workspace legacy migration is resumable and preserves source and deleted items", () => InStore(store =>
            {
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                string legacy = Path.Combine(store.Root, "legacy"), reports = Path.Combine(legacy, "reports"), meetings = Path.Combine(legacy, "meetings");
                Directory.CreateDirectory(reports); Directory.CreateDirectory(meetings);
                string requestId = Guid.NewGuid().ToString("N");
                var entry = new FridaConversationEntry { request_id = requestId, request = "Converse sobre a pauta", response = "resposta", started_utc = "2026-01-01T12:00:00Z", finished_utc = "2026-01-01T12:00:02Z" };
                string source = json.Serialize(entry), reportPath = Path.Combine(reports, "old.json"); File.WriteAllText(reportPath, source);
                var meeting = new FridaIdeMeeting { Id = Guid.NewGuid().ToString("N"), RequestId = requestId, Topic = "Pauta", StartedUtc = entry.started_utc,
                    Project = new FridaIdeProject { Name = "P", Root = legacy }, Messages = new System.Collections.Generic.List<TeamMessage> {
                        new TeamMessage { Title = "Gestor", Role = TeamRole.Manager, Text = "contribuição", AtUtc = new DateTime(2026,1,1,12,0,1,DateTimeKind.Utc), Success = true } } };
                File.WriteAllText(Path.Combine(meetings, "meeting.json"), json.Serialize(meeting));
                File.WriteAllText(Path.Combine(reports, "invalid.json"), "{");
                var result = store.ImportLegacy(reports, Path.Combine(legacy, "absent.json"), meetings);
                Check(result.Reports == 1 && result.Meetings == 1 && result.Warnings.Count == 1, "Importação não distinguiu registro inválido.");
                var c = store.List().Single(); Check(c.Kind == FridaConversationKind.Meeting && c.ProjectId != null, "Reunião não vinculada ao atendimento/projeto.");
                Check(store.Messages(c.Id).Count == 3, "Mensagem perdida.");
                string missing = Directory.GetFiles(Path.Combine(store.Root, "Conversations", c.Id, "Messages")).First(); File.Delete(missing);
                store.ImportLegacy(reports, "absent", meetings);
                Check(store.List().Count == 1 && store.Messages(c.Id).Count == 3, "Retomada duplicou ou não recuperou mensagem.");
                Check(File.ReadAllText(reportPath) == source, "Original alterado.");
                store.Trash(c.Id); store.ImportLegacy(reports, "absent", meetings);
                Check(store.List().Count == 0 && store.List(null, true).Count == 1, "Importação ressuscitou conversa excluída.");
            }));
            test("Workspace live reports bind repeated updates and meetings to explicit conversation", () => InStore(store =>
            {
                var c = store.Create("Conversa", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var e = new FridaConversationEntry { conversation_id = c.Id, request_id = Guid.NewGuid().ToString("N"), request = "pergunta", started_utc = DateTime.UtcNow.ToString("o") };
                store.RecordReport(e); e.response = "resposta"; store.RecordReport(e); e.status = "complete"; store.RecordReport(e);
                store.RecordMeeting(new FridaIdeMeeting { Id = Guid.NewGuid().ToString("N"), RequestId = e.request_id, Topic = "Pauta" });
                Check(store.List().Count == 1 && store.Messages(c.Id).Count == 2, "Atualização duplicou atendimento/reunião.");
            }));
            test("Workspace persists separate histories and drafts across reopen", () => InStore(store =>
            {
                var a = store.Create("A", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var b = store.Create("B", FridaWorkspaceSection.Code, FridaConversationKind.Conversation, null, a.Id);
                store.UpdateDraft(a.Id, "rascunho A", new[] { "arquivo.txt" });
                store.PutMessage(new FridaConversationMessage { ConversationId = a.Id, Text = "mensagem A" });
                store.PutMessage(new FridaConversationMessage { ConversationId = b.Id, Text = "mensagem B" });
                var reopened = new FridaConversationStore(store.Root);
                Check(reopened.Messages(a.Id).Single().Text == "mensagem A", "Histórico misturado.");
                Check(reopened.Get(a.Id).Draft == "rascunho A" && reopened.Get(a.Id).Attachments.Single() == "arquivo.txt", "Rascunho perdido.");
                Check(reopened.Get(b.Id).OriginConversationId == a.Id && reopened.List(FridaWorkspaceSection.Code).Count == 1, "Vínculo ou seção perdido.");
            }));
            test("Workspace retains over eighty messages and reads only requested page", () => InStore(store =>
            {
                var c = store.Create("Reunião", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                for (int i = 0; i < 125; i++) store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Text = i.ToString(), AtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i).ToString("o") });
                Check(store.Messages(c.Id, 100, 25).Last().Text == "124", "Histórico truncado.");
                var first = store.Messages(c.Id, 0, 1).Single(); first.Text = "atualizada"; store.PutMessage(first);
                Check(store.Messages(c.Id, 0, 500).Count == 125 && store.Messages(c.Id, 0, 1).Single().Text == "atualizada", "Atualização duplicou mensagem.");
                string last = Directory.GetFiles(Path.Combine(store.Root, "Conversations", c.Id, "Messages")).OrderBy(x => x).Last();
                File.WriteAllText(last, "broken");
                Check(store.Messages(c.Id, 0, 10).Count == 10, "Página abriu registros fora do intervalo.");
            }));
            test("Workspace project trash preserves files and independently trashed conversations", () => InStore(store =>
            {
                string folder = Path.Combine(store.Root, "physical-project"); Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "keep.txt"); File.WriteAllText(file, "preservar");
                var p = store.RegisterProject("P", folder);
                Check(store.RegisterProject("Alias", folder + Path.DirectorySeparatorChar).Id == p.Id, "Projeto duplicado.");
                var a = store.Create("A", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation, p.Id);
                var b = store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting, p.Id);
                store.Trash(b.Id); store.TrashProject(p.Id);
                Check(store.List().Count == 0 && store.List(null, true).Count == 2, "Lixeira incompleta.");
                Rejected(() => store.Restore(a.Id));
                store.RestoreProject(p.Id);
                Check(store.List().Single().Id == a.Id && store.List(null, true).Single().Id == b.Id, "Restauração recuperou exclusão independente.");
                Check(File.ReadAllText(file) == "preservar", "Pasta alterada.");
            }));
            test("Workspace protects running requests across store instances", () => InStore(store =>
            {
                var p = store.RegisterProject("P", store.Root);
                var c = store.Create("C", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation, p.Id);
                using (store.BeginExecution(c.Id))
                {
                    var other = new FridaConversationStore(store.Root);
                    Rejected(() => other.Trash(c.Id)); Rejected(() => other.TrashProject(p.Id));
                    Rejected(() => other.Move(c.Id, FridaWorkspaceSection.Code));
                }
                store.Trash(c.Id); Rejected(() => store.BeginExecution(c.Id));
                Rejected(() => store.Messages(c.Id));
                store.Restore(c.Id); Check(store.List().Count == 1, "Não restaurou.");
            }));
            test("Workspace concurrent writers preserve all metadata and messages", () => InStore(store =>
            {
                var c = store.Create("C", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                Parallel.For(0, 30, i => new FridaConversationStore(store.Root).PutMessage(new FridaConversationMessage { ConversationId = c.Id, Text = "item " + i }));
                Check(store.Messages(c.Id).Count == 30, "Mensagem perdida.");
            }));
            test("Workspace search matches message content without accents and skips trash", () => InStore(store =>
            {
                var a = store.Create("Orçamento", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var b = store.Create("Outra", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.PutMessage(new FridaConversationMessage { ConversationId = b.Id, Role = "user", Text = "Precisamos revisar a cotação do filamento PETG" });
                Check(store.List(null, false, "orcamento").Single().Id == a.Id, "Título sem acento não encontrado.");
                Check(store.List(null, false, "cotacao").Single().Id == b.Id, "Conteúdo não pesquisado.");
                Check(store.List(null, false, "pe").Count == 0, "Busca curta varreu conteúdo.");
                new FridaConversationStore(store.Root).PutMessage(new FridaConversationMessage { ConversationId = a.Id, Role = "assistant", Text = "Planilha de vendas" });
                Check(store.List(null, false, "planilha").Single().Id == a.Id, "Cache de busca ignorou mensagem nova.");
                store.Trash(b.Id);
                Check(store.List(null, false, "cotacao").Count == 0 && store.List(null, true, "cotacao").Single().Id == b.Id, "Busca incluiu ou perdeu item da Lixeira.");
            }));
            test("Workspace repeated migration skips imported records without rewriting the catalog", () => InStore(store =>
            {
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                string reports = Path.Combine(store.Root, "legacy-reports"); Directory.CreateDirectory(reports);
                for (int i = 0; i < 130; i++)
                    File.WriteAllText(Path.Combine(reports, i.ToString("D4") + ".json"), json.Serialize(new FridaConversationEntry { request_id = Guid.NewGuid().ToString("N"),
                        request = "pedido " + i, response = "resposta " + i, started_utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i).ToString("o") }));
                var first = store.ImportLegacy(reports, "absent", "absent");
                Check(first.Reports == 130 && first.Warnings.Count == 0 && store.List().Count == 130, "Importação em lote incompleta.");
                Check(store.List().All(c => store.MessageCount(c.Id) == 2), "Mensagens importadas incompletas.");
                string catalog = Path.Combine(store.Root, "workspace.json"); var written = File.GetLastWriteTimeUtc(catalog);
                System.Threading.Thread.Sleep(50);
                var again = new FridaConversationStore(store.Root).ImportLegacy(reports, "absent", "absent");
                Check(again.Reports == 130 && store.List().Count == 130 && File.GetLastWriteTimeUtc(catalog) == written, "Reimportação regravou o catálogo ou duplicou.");
                var victim = store.List().First(); File.Delete(Directory.GetFiles(Path.Combine(store.Root, "Conversations", victim.Id, "Messages")).First());
                store.ImportLegacy(reports, "absent", "absent");
                Check(store.MessageCount(victim.Id) == 2 && store.List().Count == 130, "Mensagem ausente não recuperada.");
            }));
            test("Workspace meeting stays one record when the subject changes and trashed targets are not reused", () => InStore(store =>
            {
                var meeting = store.Create("Reunião", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                store.PutMessage(new FridaConversationMessage { ConversationId = meeting.Id, Role = "user", Text = "pauta" });
                Check(store.Route(meeting.Id, "Crie um script Python para o relatório", FridaWorkspaceSection.Chat).Id == meeting.Id, "Reunião foi dividida.");
                var c = store.Create("C", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.Trash(c.Id);
                var routed = store.Route(c.Id, "Pesquise a previsao do tempo", FridaWorkspaceSection.Chat);
                Check(routed.Id != c.Id && routed.DeletedUtc == null, "Conversa da Lixeira recebeu novo atendimento.");
            }));
            test("Workspace corrupt catalog restores the previous valid copy and keeps the invalid file", () => InStore(store =>
            {
                var a = store.Create("A", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                string catalog = Path.Combine(store.Root, "workspace.json"); File.WriteAllText(catalog, "{broken");
                bool invalid = false; try { store.List(); } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "Catálogo inválido não foi sinalizado como dado inválido.");
                string preserved = store.RestorePreviousCatalog();
                Check(File.ReadAllText(preserved) == "{broken" && store.Get(a.Id).Title == "A", "Recuperação não preservou o inválido ou não restaurou.");
            }));
            test("Workspace resolves an attendance to its conversation and renames projects", () => InStore(store =>
            {
                var p = store.RegisterProject("Antigo", store.Root);
                var c = store.Create("C", FridaWorkspaceSection.Code, FridaConversationKind.Conversation, p.Id);
                string request = Guid.NewGuid().ToString("N");
                store.RecordReport(new FridaConversationEntry { conversation_id = c.Id, request_id = request, request = "pedido", started_utc = DateTime.UtcNow.ToString("o") });
                Check(store.ConversationForRequest(request) == c.Id && store.ConversationForRequest(Guid.NewGuid().ToString("N")) == null, "Atendimento não localizado.");
                store.RenameProject(p.Id, "Novo"); Check(store.Projects().Single().Name == "Novo", "Projeto não renomeado.");
                store.Trash(c.Id); Check(store.ConversationForRequest(request) == null, "Atalho abriu item da Lixeira.");
            }));
            test("Workspace UI registers local and VPS projects once, remembers layout and binds editor saves", () => InStore(store =>
            {
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                string folder = Path.Combine(store.Root, "projeto-local"); Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "nota.txt"); File.WriteAllText(file, "inicial");
                using (var http = new System.Net.Http.HttpClient())
                {
                    using (var form = new FridaIdeForm((text, project, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        var register = typeof(FridaIdeForm).GetMethod("RegisterWorkspaceProject", flags);
                        var local = (FridaIdeProject)register.Invoke(form, new object[] { "Local", folder, null });
                        string a = form.SelectedConversationId;
                        Check(store.Get(a).ProjectId == local.Id, "Conversa não vinculada ao projeto.");
                        Check(((FridaIdeProject)register.Invoke(form, new object[] { "Repetido", folder + "\\", null })).Id == local.Id, "Projeto local duplicado.");
                        var remote = (FridaIdeProject)register.Invoke(form, new object[] { "VPS", "/srv/app", "servidor-teste" });
                        Check(store.Projects().Count == 2 && remote.SshAlias == "servidor-teste" && remote.Id != local.Id, "Projeto VPS não registrado no mesmo cadastro.");
                        form.ClientSize = new System.Drawing.Size(1400, 900); form.CreateControl();
                        typeof(FridaIdeForm).GetMethod("SetToolsVisible", flags).Invoke(form, new object[] { true });
                        var layout = (FridaIdeLayout)typeof(FridaIdeForm).GetField("_layout", flags).GetValue(form);
                        Check(Math.Abs(layout.ToolsRatio - 0.6) < 0.001, "Abrir o painel alterou a proporção salva: " + layout.ToolsRatio);
                        form.SelectConversation(a);
                        ((Task)typeof(FridaIdeForm).GetMethod("OpenEditor", flags).Invoke(form, new object[] { file, local })).GetAwaiter().GetResult();
                        var editors = (System.Windows.Forms.TabControl)typeof(FridaIdeForm).GetField("_editors", flags).GetValue(form);
                        var tab = editors.SelectedTab; tab.Controls.OfType<FridaCodeEditor>().Single().Text = "alterado";
                        var b = store.Create("B", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation); form.SelectConversation(b.Id);
                        Check(((Task<bool>)typeof(FridaIdeForm).GetMethod("SaveEditor", flags).Invoke(form, new object[] { tab })).GetAwaiter().GetResult(), "Editor não salvou.");
                        Check(File.ReadAllText(file) == "alterado", "Arquivo não gravado.");
                        Check(store.Diagnostics(a).Count > 0 && store.Diagnostics(b.Id).Count == 0, "Gravação do editor seguiu a seleção atual em vez da conversa de origem.");
                    }
                    using (var reopened = new FridaIdeForm((text, project, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        var split = (System.Windows.Forms.SplitContainer)typeof(FridaIdeForm).GetField("_workspaceSplit", flags).GetValue(reopened);
                        Check(!split.Panel2Collapsed, "Painel direito não lembrou a disposição.");
                    }
                }
            }));
            test("Workspace skill proposals stay separate from saved and verified skills", () => InStore(store =>
            {
                var c = store.Create("Reunião", FridaWorkspaceSection.Code, FridaConversationKind.Meeting);
                string skillDir = Path.Combine(store.Root, "skill-teste"); Directory.CreateDirectory(skillDir);
                File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Exportar relatório\n\nConteúdo real");
                using (FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = c.Id, Store = store, Kind = FridaConversationKind.Meeting }))
                {
                    FridaMeetingWorkspace.Plan("{\"plan\":\"p\",\"proposed_skills\":[{\"name\":\"Exportar relatório\",\"purpose\":\"Gerar PDF\"}]}");
                    Check(store.Artifacts(c.Id).Single(a => a.Kind == "skill").SkillState == "proposta", "Proposta não identificada.");
                    FridaMeetingWorkspace.Skill(new FridaSkillEntry { Id = "exportar", Name = "Exportar relatório", SkillDirectory = skillDir, Verified = false });
                    FridaMeetingWorkspace.Plan("{\"plan\":\"p2\",\"proposed_skills\":[\"Exportar relatório\"]}");
                    FridaMeetingWorkspace.Skill(new FridaSkillEntry { Id = "exportar", Name = "Exportar relatório", SkillDirectory = skillDir, Verified = true });
                }
                var versions = store.Artifacts(c.Id).Where(a => a.Kind == "skill").OrderBy(a => a.Version).Select(a => a.SkillState).ToArray();
                Check(versions.SequenceEqual(new[] { "proposta", "salva", "verificada" }), "Estados da skill misturados: " + string.Join(",", versions));
                Check(store.Artifacts(c.Id).Single(a => a.Version == 3).Markdown.Contains("Conteúdo real"), "Skill sem conteúdo real do SKILL.md.");
            }));
            test("Workspace links forked conversations both ways and marks interrupted rounds", () => InStore(store =>
            {
                var a = store.Route(null, "Pesquise o preço do filamento PP", FridaWorkspaceSection.Chat);
                store.PutMessage(new FridaConversationMessage { ConversationId = a.Id, Role = "user", Text = "contexto" });
                var b = store.Route(a.Id, "Crie um script Python que calcule o custo", FridaWorkspaceSection.Chat);
                Check(store.Linked(a.Id).Single().Id == b.Id && store.Linked(b.Id).Single().Id == a.Id, "Vínculo não navegável nos dois sentidos.");
                Check(store.Messages(a.Id).Any(m => m.Role == "link" && m.Text.Contains("Código")) && store.Messages(b.Id).Any(m => m.Role == "link" && m.Text.Contains("Chat")), "Histórico sem indicação do vínculo.");
                Check(store.LoadBrainState(a.Id).Memory.All(m => m.Text != null && !m.Text.Contains("continua em")), "Vínculo entrou no contexto do modelo.");
                string interrupted = Guid.NewGuid().ToString("N"), answered = Guid.NewGuid().ToString("N");
                store.RecordReport(new FridaConversationEntry { conversation_id = a.Id, request_id = answered, request = "respondida", response = "ok", started_utc = "2026-01-01T00:00:00Z", finished_utc = "2026-01-01T00:00:01Z" });
                store.RecordReport(new FridaConversationEntry { conversation_id = a.Id, request_id = interrupted, request = "interrompida", started_utc = "2026-01-01T00:00:02Z" });
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new FridaIdeForm((text, project, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                {
                    form.SelectConversation(a.Id);
                    var cards = (System.Collections.IList)typeof(FridaIdeForm).GetField("_messages", flags).GetValue(form);
                    int markers = 0;
                    foreach (object card in cards) { var label = (System.Windows.Forms.Label)card.GetType().GetField("Author", flags).GetValue(card); if (label.Text == "Atendimento") markers++; }
                    Check(markers == 1, "Rodada interrompida não identificada (ou respondida marcada): " + markers);
                }
            }));
            test("Workspace UI moves a changed-subject request to the linked conversation without leaving it as the origin draft", () => InStore(store =>
            {
                var a = store.Create("Compras", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.PutMessage(new FridaConversationMessage { ConversationId = a.Id, Role = "user", Text = "Pesquise filamento PP" });
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                string received = null;
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new FridaIdeForm((text, project, audio) => { received = FridaIdeRuntime.Context.ConversationId; return Task.FromResult("feito"); }, () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                {
                    form.SelectConversation(a.Id);
                    var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form);
                    draft.Text = "Crie um script Python que calcule o custo do filamento";
                    var pending = (Task)typeof(FridaIdeForm).GetMethod("SendDraft", flags).Invoke(form, null);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!pending.IsCompleted && clock.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    pending.GetAwaiter().GetResult();
                    var linked = store.Linked(a.Id).Single();
                    Check(received == linked.Id && form.SelectedConversationId == linked.Id && linked.Section == FridaWorkspaceSection.Code, "Pedido não seguiu para a conversa vinculada.");
                    Check(store.Get(a.Id).Draft == "" && store.Get(linked.Id).Draft == "" && draft.Text == "", "Texto enviado ficou como rascunho.");
                    Check(store.Messages(linked.Id).Count(m => m.Role == "user" && m.Text.Contains("script Python")) == 1, "Pedido não registrado na conversa vinculada.");
                }
            }));
            test("Workspace writes the deepest files within MAX_PATH at the supported root depth", () =>
            {
                string top = Path.Combine(Path.GetTempPath(), "frida-deep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string root = top + "\\" + new string('d', Math.Max(1, FridaConversationStore.MaxSafeRootLength - top.Length - 1));
                try
                {
                    var store = new FridaConversationStore(root);
                    Check(root.Length == FridaConversationStore.MaxSafeRootLength && !store.RootTooDeep && new FridaConversationStore(root + "x").RootTooDeep, "Limite de profundidade incorreto.");
                    var c = store.Create("Profunda", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                    store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "user", Text = "mensagem" });
                    store.SaveArtifact(c.Id, null, "Plano", "plan", "# Plano", "Gestor");
                    store.SaveBrainState(c.Id, new FridaBrainConversationState());
                    Check(store.Messages(c.Id).Count == 1 && store.Artifacts(c.Id).Count == 1, "Gravação falhou no limite suportado.");
                }
                finally { if (Directory.Exists(top)) Directory.Delete(top, true); }
            });
            test("Workspace layout has no overlapping, clipped, undersized or light controls at 100/150/200%", () =>
            {
                var problems = new System.Collections.Generic.List<string>(); int inspected = 0;
                string render = Environment.GetEnvironmentVariable("FRIDA_QA_RENDER_DIR");
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                FridaIdeForm.AllowOversizeForTests = true;
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    FridaTheme.ScaleOverride = scale;
                    try
                    {
                        InStore(store =>
                        {
                            string folder = Path.Combine(store.Root, "projeto"); Directory.CreateDirectory(folder);
                            string file = Path.Combine(folder, "script.py"); File.WriteAllText(file, "print('ok')\n");
                            var project = store.RegisterProject("Projeto de teste com nome longo", folder);
                            var chat = store.Create("Conversa do dia a dia com um título bem comprido para testar reticências", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                            store.RecordReport(new FridaConversationEntry { conversation_id = chat.Id, request_id = Guid.NewGuid().ToString("N"), request = "Qual a previsão do tempo?", response = "Amanhã faz sol com 28 °C.", started_utc = DateTime.UtcNow.AddMinutes(-3).ToString("o"), finished_utc = DateTime.UtcNow.AddMinutes(-2).ToString("o") });
                            var meeting = store.Create("Reunião de planejamento", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting, project.Id);
                            store.PutMessage(new FridaConversationMessage { ConversationId = meeting.Id, Role = "Manager", Author = "Gestor / Cerebro", Model = "deepseek-v4-pro", Channel = "meeting", Text = "Plano proposto para a equipe." });
                            store.PutMessage(new FridaConversationMessage { ConversationId = meeting.Id, Role = "Technician", Author = "Engenheiro / Kimi K3", Model = "kimi-k3", Channel = "meeting", Status = "unavailable", Text = "Indisponível." });
                            store.SaveArtifact(meeting.Id, null, "Plano", "plan", "# Plano\n\n- **Etapa** um\n- Etapa dois", "Gestor");
                            var code = store.Create("Script de custos", FridaWorkspaceSection.Code, FridaConversationKind.Conversation, project.Id);
                            var settings = new FridaSettings();
                            using (var http = new System.Net.Http.HttpClient())
                            using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                            {
                                form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                                var tools = (System.Windows.Forms.TabControl)typeof(FridaIdeForm).GetField("_workspaceTools", flags).GetValue(form);
                                var setTools = typeof(FridaIdeForm).GetMethod("SetToolsVisible", flags);
                                var views = new System.Collections.Generic.List<Tuple<string, Action>>
                                {
                                    Tuple.Create("chat", (Action)(() => { form.SelectConversation(chat.Id); setTools.Invoke(form, new object[] { false }); })),
                                    Tuple.Create("reuniao-artefatos", (Action)(() => { form.SelectConversation(meeting.Id); setTools.Invoke(form, new object[] { true }); tools.SelectedIndex = 0; })),
                                    Tuple.Create("arquivos", (Action)(() => { form.SelectConversation(code.Id); setTools.Invoke(form, new object[] { true }); tools.SelectedIndex = 1; })),
                                    Tuple.Create("terminais", (Action)(() => { tools.SelectedIndex = 2; })),
                                    Tuple.Create("editor", (Action)(() => { ((Task)typeof(FridaIdeForm).GetMethod("OpenEditor", flags).Invoke(form, new object[] { file, null })).GetAwaiter().GetResult(); typeof(FridaIdeForm).GetMethod("ShowEditor", flags).Invoke(form, null); tools.SelectedIndex = 1; })),
                                    Tuple.Create("whatsapp", (Action)(() => form.ShowSection("WhatsApp"))),
                                    Tuple.Create("zelda", (Action)(() => form.ShowSection("Zelda Manager"))),
                                };
                                foreach (var logical in new[] { new System.Drawing.Size(900, 600), new System.Drawing.Size(1040, 720), new System.Drawing.Size(1366, 768), new System.Drawing.Size(1920, 1080) })
                                {
                                    var wanted = new System.Drawing.Size((int)(logical.Width * scale), (int)(logical.Height * scale));
                                    form.ResizeClientForTests(wanted); System.Windows.Forms.Application.DoEvents();
                                    if (form.ClientSize != wanted) { problems.Add("tamanho não aplicado: " + wanted + " → " + form.ClientSize); continue; }
                                    foreach (var view in views)
                                    {
                                        view.Item2(); System.Windows.Forms.Application.DoEvents(); System.Windows.Forms.Application.DoEvents();
                                        string where = (scale * 100) + "% " + logical.Width + "x" + logical.Height + " " + view.Item1;
                                        if (form.ClientSize != wanted || form.Width < wanted.Width) { problems.Add(where + ": janela com " + form.Size + " em vez de " + wanted); continue; }
                                        InspectLayout(form, where, problems); inspected++;
                                        if (!string.IsNullOrEmpty(render) && (logical.Width == 1366 || logical.Width == 1040))
                                        {
                                            Directory.CreateDirectory(render);
                                            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                                            { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(render, "layout-" + (int)(scale * 100) + "-" + logical.Width + "x" + logical.Height + "-" + view.Item1 + ".png")); }
                                        }
                                    }
                                }
                                form.ShowSection("Chat");
                            }
                        });
                    }
                    finally { FridaTheme.ScaleOverride = 0; }
                }
                FridaIdeForm.AllowOversizeForTests = false;
                Check(inspected == 3 * 4 * 7, "Combinações inspecionadas: " + inspected + " de 84");
                Check(problems.Count == 0, problems.Count + " problema(s) de layout:\n" + string.Join("\n", problems.Distinct().Take(30)));
            });
            test("Workspace side panels resize by dragging, are remembered and never scroll sideways", () => InStore(store =>
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var c = store.Create(new string('T', 40) + " título muito longo para caber na lista lateral sem cortar " + new string('x', 60), FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                for (int i = 0; i < 30; i++)
                    store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = i % 2 == 0 ? "user" : "assistant", Text = i == 5 ? new string('w', 600) : "mensagem " + i + " " + string.Join(" ", Enumerable.Repeat("palavra", 40)) });
                string folder = Path.Combine(store.Root, "editor"); Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "longo.py"); File.WriteAllText(file, "x = '" + new string('a', 400) + "'\nprint(x)\n");
                var settings = new FridaSettings();
                FridaIdeForm.AllowOversizeForTests = true;
                try
                {
                    using (var http = new System.Net.Http.HttpClient())
                    {
                        using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                        {
                            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                            form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1920), FridaTheme.Px(1080))); System.Windows.Forms.Application.DoEvents();
                            form.SelectConversation(c.Id); System.Windows.Forms.Application.DoEvents();
                            var sidebar = (System.Windows.Forms.Panel)typeof(FridaIdeForm).GetField("_sidebar", flags).GetValue(form);
                            var splitter = (System.Windows.Forms.Splitter)typeof(FridaIdeForm).GetField("_sidebarSplitter", flags).GetValue(form);
                            var layout = (FridaIdeLayout)typeof(FridaIdeForm).GetField("_layout", flags).GetValue(form);
                            Check(sidebar.Width == FridaTheme.Px(FridaIdeLayout.DefaultSidebarWidth) && splitter.Visible && splitter.Left == sidebar.Right, "Divisória da lista ausente ou fora do lugar.");
                            splitter.SplitPosition = FridaTheme.Px(420); System.Windows.Forms.Application.DoEvents();
                            Check(sidebar.Width == FridaTheme.Px(420) && layout.SidebarWidth == 420, "Arrastar a lista não mudou/salvou a largura: " + sidebar.Width + " / " + layout.SidebarWidth);
                            typeof(FridaIdeForm).GetMethod("SetSidebarWidth", flags).Invoke(form, new object[] { 9999 });
                            Check(sidebar.Width == FridaTheme.Px(FridaIdeLayout.MaxSidebarWidth), "Largura máxima da lista ignorada.");
                            typeof(FridaIdeForm).GetMethod("SetSidebarWidth", flags).Invoke(form, new object[] { 420 });
                            // Right panel: a wide preference is honored (no old 640 px cap) while the conversation keeps its minimum.
                            layout.ToolsRatio = 0.3;
                            typeof(FridaIdeForm).GetMethod("SetToolsVisible", flags).Invoke(form, new object[] { true }); System.Windows.Forms.Application.DoEvents();
                            var split = (System.Windows.Forms.SplitContainer)typeof(FridaIdeForm).GetField("_workspaceSplit", flags).GetValue(form);
                            Check(split.Panel2.Width > FridaTheme.Px(640) && split.Panel1.Width >= FridaTheme.Px(380) - 1, "Painel direito limitado ou conversa espremida: " + split.Panel1.Width + " / " + split.Panel2.Width);
                            // No sideways scrolling in the list or the conversation, even with long titles and unbroken text.
                            var tree = (System.Windows.Forms.TreeView)typeof(FridaIdeForm).GetField("_conversations", flags).GetValue(form);
                            var cp = (System.Windows.Forms.CreateParams)typeof(System.Windows.Forms.Control).GetProperty("CreateParams", flags).GetValue(tree, null);
                            Check((cp.Style & 0x8000) != 0, "Lista ainda permite rolagem horizontal.");
                            var history = (System.Windows.Forms.Panel)typeof(FridaIdeForm).GetField("_historyView", flags).GetValue(form);
                            var thread = (System.Windows.Forms.Panel)typeof(FridaIdeForm).GetField("_thread", flags).GetValue(form);
                            foreach (int width in new[] { 1920, 1366, 1100 })
                            {
                                form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(width), FridaTheme.Px(800))); System.Windows.Forms.Application.DoEvents();
                                Check(!history.HorizontalScroll.Visible && thread.Right <= history.ClientSize.Width, "Rolagem horizontal na conversa em " + width + ": " + thread.Right + " > " + history.ClientSize.Width);
                            }
                            // Editor word wrap toggles for open files and is remembered.
                            ((Task)typeof(FridaIdeForm).GetMethod("OpenEditor", flags).Invoke(form, new object[] { file, null })).GetAwaiter().GetResult();
                            var editors = (System.Windows.Forms.TabControl)typeof(FridaIdeForm).GetField("_editors", flags).GetValue(form);
                            var editor = editors.SelectedTab.Controls.OfType<FridaCodeEditor>().Single();
                            Check(!editor.WordWrap, "Editor começou com quebra de linha.");
                            typeof(FridaIdeForm).GetMethod("ToggleEditorWordWrap", flags).Invoke(form, null);
                            Check(editor.WordWrap && layout.EditorWordWrap, "Quebra de linha não aplicada ao editor aberto.");
                            editor.SelectionStart = editor.TextLength; Check(editor.CaretLine == 3, "Linha do cursor contada pela quebra visual: " + editor.CaretLine);
                        }
                        using (var reopened = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                        {
                            var sidebar = (System.Windows.Forms.Panel)typeof(FridaIdeForm).GetField("_sidebar", flags).GetValue(reopened);
                            Check(sidebar.Width == FridaTheme.Px(420), "Largura da lista não lembrada: " + sidebar.Width);
                            ((Task)typeof(FridaIdeForm).GetMethod("OpenEditor", flags).Invoke(reopened, new object[] { file, null })).GetAwaiter().GetResult();
                            var editors = (System.Windows.Forms.TabControl)typeof(FridaIdeForm).GetField("_editors", flags).GetValue(reopened);
                            Check(editors.SelectedTab.Controls.OfType<FridaCodeEditor>().Single().WordWrap, "Quebra de linha não lembrada.");
                        }
                    }
                }
                finally { FridaIdeForm.AllowOversizeForTests = false; }
            }));
            test("Workspace Markdown renders without raw symbols and keeps underscores inside words", () =>
            {
                string md = "### 3. O que NÃO foi cruzado — e por quê\n1. **Planilhas 2 e 3**: retornaram `auth_required`, com **0 linhas** lidas.\n- **Autorização A** — leitura das _Planilhas_ 2 e 3\n  - item aninhado com snake_case_name\n\n"
                    + "| Chassi | Data | Status | Origem | Lote |\n|---|---|---|---|---|\n| VR005 | 01/11 | confirmado | base | 3 |\n\n```\nGet-Content log.txt | Select -Last 5\n```\n---\nVeja [o painel](https://example.com/painel).";
                string plain = FridaMarkdown.ToPlain(md);
                Check(!plain.Contains("###") && !plain.Contains("**") && !plain.Contains("`") && !plain.Contains("|---"), "Símbolos de Markdown no texto: " + plain);
                Check(plain.Contains("auth_required") && plain.Contains("snake_case_name") && plain.Contains("1. Planilhas 2 e 3"), "Conteúdo perdido: " + plain);
                Check(plain.Contains("Get-Content log.txt | Select -Last 5") && plain.Contains("o painel") && plain.Contains("https://example.com/painel"), "Código ou link perdido: " + plain);
                using (var box = new System.Windows.Forms.RichTextBox())
                {
                    box.Rtf = FridaMarkdown.ToRtf(md, new FridaMarkdownStyle { Width = 400 });
                    string shown = box.Text;
                    Check(!shown.Contains("###") && !shown.Contains("**") && !shown.Contains("`") && !shown.Contains("|---"), "Tela mostra símbolos: " + shown);
                    Check(shown.Contains("auth_required") && shown.Contains("Status: confirmado"), "Tabela larga sem 'coluna: valor': " + shown);
                    box.Select(shown.IndexOf("Planilhas 2 e 3", StringComparison.Ordinal) + 1, 1);
                    Check(box.SelectionFont.Name.Contains("Semibold") || box.SelectionFont.Bold, "Negrito não aplicado: " + box.SelectionFont.Name);
                    box.Select(shown.IndexOf("auth_required", StringComparison.Ordinal) + 2, 1);
                    Check(box.SelectionFont.Name == FridaTheme.MonoFamily, "Código sem fonte monoespaçada: " + box.SelectionFont.Name);
                }
            });
            test("Workspace chat shows bubbles, groups consecutive replies and appends without rebuilding", () => InStore(store =>
            {
                var c = store.Create("Bolhas", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var start = DateTime.UtcNow.AddMinutes(-2);
                Func<int, string> at = s => start.AddSeconds(s).ToString("o");
                store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "user", Text = "Cruze as planilhas com o WhatsApp", AtUtc = at(0) });
                store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "assistant", Author = "Frida", AtUtc = at(1),
                    Text = "### 3. O que NÃO foi cruzado\n1. **Planilhas 2 e 3**: retornaram `auth_required`, com **0 linhas** lidas.\n2. **WhatsApp**: não houve leitura nesta operação.\n\n### 4. Pendências\n- **Autorização A** — acesso de leitura às Planilhas 2 e 3\n- **Autorização B** — leitura do WhatsApp Web (modo ouvinte)" });
                store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "assistant", Author = "Frida", AtUtc = at(2), Text = "Libere **A + B** e eu entrego a matriz de confronto." });
                store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "Analyst", Author = "Pesquisador sênior / DeepSeek R1", Model = "deepseek-reasoner", Channel = "meeting", AtUtc = at(3), Text = "## Relatório de cruzamento\nNenhum par **chassi ↔ WhatsApp** confirmado." });
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Func<object, string, object> get = (o, name) => o.GetType().GetField(name, flags).GetValue(o);
                FridaIdeForm.AllowOversizeForTests = true;
                try
                {
                    using (var http = new System.Net.Http.HttpClient())
                    using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                        form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1366), FridaTheme.Px(768))); System.Windows.Forms.Application.DoEvents();
                        form.SelectConversation(c.Id); System.Windows.Forms.Application.DoEvents();
                        var cards = (System.Collections.IList)typeof(FridaIdeForm).GetField("_messages", flags).GetValue(form);
                        var thread = (System.Windows.Forms.Control)typeof(FridaIdeForm).GetField("_thread", flags).GetValue(form);
                        Check(cards.Count == 4, "Mensagens renderizadas: " + cards.Count);
                        var userCard = (System.Windows.Forms.Control)get(cards[0], "Card");
                        Check(userCard.Right == thread.Width && userCard.Width < thread.Width, "Balão do usuário não está à direita: " + userCard.Bounds + " em " + thread.Width);
                        var reply = (System.Windows.Forms.RichTextBox)get(cards[1], "Body");
                        Check(!reply.Text.Contains("**") && !reply.Text.Contains("###") && !reply.Text.Contains("`") && reply.Text.Contains("auth_required"), "Markdown cru na conversa: " + reply.Text);
                        Check(!reply.TabStop && ((System.Windows.Forms.Control)get(cards[1], "Card")).Left == 0, "Resposta fora do lugar ou na ordem de tabulação.");
                        Check(get(cards[1], "Avatar") != null && (bool)get(cards[2], "Grouped") && get(cards[2], "Avatar") == null && !((System.Windows.Forms.Label)get(cards[2], "Author")).Visible, "Respostas seguidas não agrupadas.");
                        Check((bool)get(cards[3], "Team") && !(bool)get(cards[3], "Grouped") && get(cards[3], "Avatar") != null, "Membro do time sem avatar/barra.");
                        string render = Environment.GetEnvironmentVariable("FRIDA_QA_SNAPSHOT_DIR");
                        if (!string.IsNullOrEmpty(render)) Snapshot(form, Path.Combine(render, "chat-bolhas.png"));
                        // A new stored reply is appended: the bubbles already on screen are kept.
                        var firstCard = (System.Windows.Forms.Control)get(cards[0], "Card");
                        store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "assistant", Author = "Frida", Text = "Concluído." });
                        typeof(FridaIdeForm).GetMethod("RenderConversation", flags).Invoke(form, null);
                        Check(cards.Count == 5 && ReferenceEquals(get(cards[0], "Card"), firstCard) && !firstCard.IsDisposed, "Conversa reconstruída para acrescentar uma resposta.");
                    }
                }
                finally { FridaIdeForm.AllowOversizeForTests = false; }
            }));
            test("Workspace composer shows focus, grows with the text, saves drafts after a pause and turns long pastes into attachments", () => InStore(store =>
            {
                var c = store.Create("Composer", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Func<object, string, object> get = (o, name) => o.GetType().GetField(name, flags).GetValue(o);
                FridaIdeForm.AllowOversizeForTests = true;
                try
                {
                    using (var http = new System.Net.Http.HttpClient())
                    using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                        form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1366), FridaTheme.Px(768))); System.Windows.Forms.Application.DoEvents();
                        form.SelectConversation(c.Id); System.Windows.Forms.Application.DoEvents();
                        var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form);
                        var composer = typeof(FridaIdeForm).GetField("_composer", flags).GetValue(form);
                        var refs = (System.Windows.Forms.Control)typeof(FridaIdeForm).GetField("_references", flags).GetValue(form);
                        // Focus is unmistakable: a 2 px highlight ring with a halo.
                        form.Activate(); draft.Focus(); System.Windows.Forms.Application.DoEvents();
                        Check(draft.Focused && (System.Drawing.Color)get(composer, "Stroke") == FridaTheme.Highlight && (float)get(composer, "StrokeWidth") == 2f && ((System.Drawing.Color)get(composer, "Glow")).A > 0, "Foco da caixa sem destaque.");
                        // Grows from one line, caps at ten and only then shows the scrollbar.
                        draft.Text = ""; System.Windows.Forms.Application.DoEvents(); int one = draft.Height;
                        draft.Text = string.Join("\r\n", Enumerable.Range(1, 5).Select(i => "linha " + i)); System.Windows.Forms.Application.DoEvents(); int five = draft.Height;
                        var box = typeof(FridaIdeForm).GetProperty("Composer", flags).GetValue(form, null);
                        Check(five > one * 3 && !(bool)box.GetType().GetProperty("VerticalBarShown", flags).GetValue(box, null), "Caixa não cresceu com o texto: " + one + " → " + five);
                        draft.Text = string.Join("\r\n", Enumerable.Range(1, 25).Select(i => "linha " + i)); System.Windows.Forms.Application.DoEvents();
                        Check(draft.Height <= draft.Font.Height * 10 + FridaTheme.Px(6) && (bool)box.GetType().GetProperty("VerticalBarShown", flags).GetValue(box, null), "Caixa sem limite ou sem rolagem: " + draft.Height);
                        // Typing does not rewrite the catalog on every key: the draft is saved after a pause.
                        for (int i = 0; i < 5; i++) draft.Text = "rascunho " + i;
                        Check(store.Get(c.Id).Draft != "rascunho 4", "Rascunho gravado a cada tecla.");
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        while (store.Get(c.Id).Draft != "rascunho 4" && clock.ElapsedMilliseconds < 3000) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                        Check(store.Get(c.Id).Draft == "rascunho 4", "Rascunho não gravado após a pausa.");
                        // A long paste becomes a chip (the box keeps its text) and goes inside the message.
                        string pasted = string.Join("\n", Enumerable.Range(1, 120).Select(i => "registro " + i + " chassi VR00" + i));
                        var data = new System.Windows.Forms.DataObject(); data.SetData(System.Windows.Forms.DataFormats.UnicodeText, pasted);
                        typeof(FridaIdeForm).GetField("_clipboardSource", flags).SetValue(form, (Func<System.Windows.Forms.IDataObject>)(() => data));
                        draft.Text = "Analise isto:"; draft.SelectionStart = draft.TextLength; draft.Paste();
                        Check(draft.Text == "Analise isto:" && refs.Controls.Count == 1, "Colagem longa inundou a caixa: " + draft.TextLength + " / " + refs.Controls.Count);
                        string file = (string)refs.Controls[0].Tag;
                        Check(file.StartsWith(store.Root) && File.ReadAllText(file) == pasted, "Texto colado não guardado na conversa.");
                        string request = (string)typeof(FridaIdeForm).GetMethod("ComposeRequest", flags).Invoke(form, null);
                        Check(request.StartsWith("Analise isto:") && request.Contains("[Anexo colado: ") && request.Contains("registro 120 chassi VR00120") && !request.Contains("Arquivos do computador"), "Texto colado não vai na mensagem: " + request.Substring(0, Math.Min(200, request.Length)));
                        string bubble = (string)typeof(FridaIdeForm).GetMethod("CollapsePasted", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { request });
                        Check(bubble.StartsWith("Analise isto:") && bubble.Contains("Texto colado") && !bubble.Contains("registro 120"), "Balão mostra o texto colado inteiro: " + bubble);
                        // Ctrl+Z right after puts the text back where the caret was and drops the file.
                        Check((bool)typeof(FridaIdeForm).GetMethod("UndoPasteAttachment", flags).Invoke(form, null) && draft.Text == "Analise isto:" + pasted && refs.Controls.Count == 0 && !File.Exists(file), "Ctrl+Z não desfez a colagem como anexo.");
                        // A short paste stays in the box.
                        data = new System.Windows.Forms.DataObject(); data.SetData(System.Windows.Forms.DataFormats.UnicodeText, " curto");
                        draft.Text = "x"; draft.SelectionStart = 1; draft.Paste();
                        Check(draft.Text == "x curto" && refs.Controls.Count == 0, "Colagem curta virou anexo: " + draft.Text);
                        string render = Environment.GetEnvironmentVariable("FRIDA_QA_SNAPSHOT_DIR");
                        if (!string.IsNullOrEmpty(render))
                        {
                            typeof(FridaIdeForm).GetField("_clipboardSource", flags).SetValue(form, (Func<System.Windows.Forms.IDataObject>)(() => { var d = new System.Windows.Forms.DataObject(); d.SetData(System.Windows.Forms.DataFormats.UnicodeText, pasted); return d; }));
                            draft.Text = "Cruze esta lista com a base e me diga o que falta"; draft.Paste(); draft.Focus(); draft.SelectionStart = draft.TextLength;
                            form.Refresh(); System.Windows.Forms.Application.DoEvents();
                            Check(draft.Text.StartsWith("Cruze esta lista") && refs.Controls.Count == 1, "Texto digitado perdido ao colar.");
                            Snapshot(form, Path.Combine(render, "composer-foco-anexo.png"));
                        }
                    }
                }
                finally { FridaIdeForm.AllowOversizeForTests = false; }
            }));
            test("Workspace item menu opens without selecting, the list is not rebuilt meanwhile, rename is in place and delete can be undone", () => InStore(store =>
            {
                var a = store.Create("Conversa A aberta", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var b = store.Create("Conversa B da lista", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                FridaIdeForm.AllowOversizeForTests = true;
                try
                {
                    using (var http = new System.Net.Http.HttpClient())
                    using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                        form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1366), FridaTheme.Px(768))); System.Windows.Forms.Application.DoEvents();
                        form.SelectConversation(a.Id); System.Windows.Forms.Application.DoEvents();
                        var tree = (FridaConversationTree)typeof(FridaIdeForm).GetField("_conversations", flags).GetValue(form);
                        var menu = (System.Windows.Forms.ContextMenuStrip)typeof(FridaIdeForm).GetField("_treeMenu", flags).GetValue(form);
                        var refresh = typeof(FridaIdeForm).GetMethod("RefreshConversationTree", flags);
                        Func<string, System.Windows.Forms.TreeNode> find = id => (System.Windows.Forms.TreeNode)typeof(FridaIdeForm).GetMethod("FindConversationNode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { tree.Nodes, id });
                        var nodeB = find(b.Id);
                        // An unchanged list is refreshed in place: the same rows stay.
                        refresh.Invoke(form, null);
                        Check(ReferenceEquals(find(b.Id), nodeB), "Lista reconstruída sem mudança.");
                        // "⋯" on another conversation: the menu opens for it, the open conversation stays.
                        int x = tree.ClientSize.Width - FridaTheme.Px(8) - FridaTheme.Px(13), y = nodeB.Bounds.Y + nodeB.Bounds.Height / 2;
                        SendMessage(tree.Handle, 0x0201, (IntPtr)1, (IntPtr)((y << 16) | (x & 0xFFFF))); System.Windows.Forms.Application.DoEvents();
                        Check(menu.Visible && form.SelectedConversationId == a.Id && tree.MenuNode == nodeB && tree.SelectedNode != nodeB, "O ⋯ abriu/selecionou a conversa: aberta=" + form.SelectedConversationId + " menu=" + menu.Visible);
                        // While the menu is open a store change does not rebuild the list under the mouse.
                        store.Create("Conversa C nova", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                        refresh.Invoke(form, null);
                        Check(ReferenceEquals(find(b.Id), nodeB) && (bool)typeof(FridaIdeForm).GetField("_treeRefreshPending", flags).GetValue(form), "Lista reconstruída com o menu aberto.");
                        menu.Close(); System.Windows.Forms.Application.DoEvents(); System.Windows.Forms.Application.DoEvents();
                        Check(tree.MenuNode == null && tree.Nodes.Cast<System.Windows.Forms.TreeNode>().SelectMany(n => n.Nodes.Cast<System.Windows.Forms.TreeNode>()).Any(n => n.Text == "Conversa C nova"), "Atualização adiada não aplicada ao fechar o menu.");
                        nodeB = find(b.Id);
                        // Right click: same behavior.
                        y = nodeB.Bounds.Y + nodeB.Bounds.Height / 2;
                        SendMessage(tree.Handle, 0x0204, (IntPtr)2, (IntPtr)((y << 16) | 40)); System.Windows.Forms.Application.DoEvents();
                        Check(menu.Visible && form.SelectedConversationId == a.Id && tree.MenuNode == nodeB, "Botão direito abriu/selecionou a conversa.");
                        menu.Close(); System.Windows.Forms.Application.DoEvents(); System.Windows.Forms.Application.DoEvents();
                        // Rename in place: Esc cancels, Enter saves; the open conversation is untouched.
                        var begin = typeof(FridaIdeForm).GetMethod("BeginInlineRename", flags);
                        begin.Invoke(form, new object[] { nodeB });
                        var frame = (InputFrame)typeof(FridaIdeForm).GetField("_inlineRename", flags).GetValue(form);
                        Check(frame != null && frame.Parent == tree && frame.Input.Text == "Conversa B da lista", "Caixa de renomear ausente.");
                        frame.Input.Text = "Não salvar"; typeof(FridaIdeForm).GetMethod("FinishInlineRename", flags).Invoke(form, new object[] { false });
                        Check(store.Get(b.Id).Title == "Conversa B da lista", "Esc salvou o título.");
                        begin.Invoke(form, new object[] { nodeB });
                        frame = (InputFrame)typeof(FridaIdeForm).GetField("_inlineRename", flags).GetValue(form);
                        frame.Input.Text = "Planilhas de chassi"; typeof(FridaIdeForm).GetMethod("FinishInlineRename", flags).Invoke(form, new object[] { true });
                        Check(store.Get(b.Id).Title == "Planilhas de chassi" && nodeB.Text == "Planilhas de chassi" && form.SelectedConversationId == a.Id && tree.Controls.Count == 0, "Renomear no lugar falhou.");
                        // Delete from the menu, then undo from the footer.
                        typeof(FridaIdeForm).GetField("_menuTarget", flags).SetValue(form, nodeB);
                        var trash = menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(i => i.Text == "Excluir"); trash.PerformClick();
                        typeof(FridaIdeForm).GetField("_menuTarget", flags).SetValue(form, null);
                        var undo = (PillButton)typeof(FridaIdeForm).GetField("_undoTrash", flags).GetValue(form);
                        Check(store.Get(b.Id, true).DeletedUtc != null && undo.Visible && form.SelectedConversationId == a.Id, "Excluir sem Desfazer ou fechou a conversa aberta.");
                        undo.Invoke();
                        Check(store.Get(b.Id, true).DeletedUtc == null && !undo.Visible && find(b.Id) != null, "Desfazer não restaurou.");
                    }
                }
                finally { FridaIdeForm.AllowOversizeForTests = false; }
            }));
            test("Workspace secretary publishes the final decision with evidence, prints and skill lessons", () => InStore(store =>
            {
                var c = store.Create("Cruzamento de chassis", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                var initialized = typeof(FridaSkillRegistry).GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                byte[] jpeg;
                using (var bmp = new System.Drawing.Bitmap(64, 36))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.FromArgb(20, 60, 90));
                    using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpeg = ms.ToArray(); }
                }
                try
                {
                    FridaSkillRegistry.Initialize(Path.Combine(store.Root, "skills-base"));
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "cruzar-planilhas", Name = "Cruzar planilhas", Description = "Cruza a base com o WhatsApp." });
                    FridaEvidenceCapture.Capture = () => jpeg;
                    var read = new FridaToolReceipt("read_spreadsheet", "read", "base.xlsx", false); read.Reference = "E1"; read.Finish(FridaResultStatus.Verified, "read", "141 linhas lidas da planilha base.");
                    var screen = new FridaToolReceipt("inspect_screen", "analyze", "tela", false); screen.Reference = "E2"; screen.Finish(FridaResultStatus.Verified, "ok", "Tela do WhatsApp conferida.");
                    string observed = read.ToJson() + "\n\n" + screen.ToJson();
                    var coordinator = new FridaTeamCoordinator(new FridaSettings { max_correction_cycles = 1 }, (role, prompt) =>
                    {
                        if (role == TeamRole.Manager && prompt.Contains("\"veredito\""))
                            return Task.FromResult("{\"veredito\":\"aprovado\",\"feito\":[\"Planilha base lida (141 linhas)\",\"Chassis cruzados com o WhatsApp\"],\"pendente\":[],\"nao_feito\":[],\"justificativa\":\"Recibos de leitura e cruzamento aprovados.\",\"proximos_passos\":[\"Enviar a matriz ao operador\"],\"atualizar_skill\":{\"skill_id\":\"cruzar-planilhas\",\"licoes\":[\"Ler as 3 planilhas antes do WhatsApp\"]}}");
                        if (role == TeamRole.Manager && prompt.Contains("learn_new_skill")) return Task.FromResult("{\"learn_new_skill\": false}");
                        if (role == TeamRole.Manager) return Task.FromResult("{\"plan\":\"Ler as planilhas e cruzar com o WhatsApp\"}");
                        if (role == TeamRole.Analyst) return Task.FromResult("[APROVADO_SEM_PENDENCIAS] Leitura e cruzamento comprovados pelos recibos.");
                        if (role == TeamRole.Secretary) return Task.FromResult("Cruzei as planilhas com o WhatsApp e confirmei 12 chassis.");
                        return Task.FromResult("OK");
                    });
                    string summary;
                    using (FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = c.Id, Store = store, Kind = FridaConversationKind.Meeting }))
                    {
                        FridaEvidenceCapture.Save(jpeg, "Tela analisada: WhatsApp");
                        summary = coordinator.RunAsync("Cruze as planilhas de chassi com o WhatsApp", instruction => Task.FromResult("Planilhas lidas e cruzadas."), () => observed, null, null,
                            new FridaTaskPlan { Kind = FridaTaskKind.ApplicationBuild, NeedsTeam = true }).GetAwaiter().GetResult();
                    }
                    Check(summary.Contains("12 chassis") && FridaTeamState.LastReport == summary, "Resumo da secretária perdido.");
                    var artifact = store.Artifacts(c.Id).Single(a => a.Kind == "decision");
                    var report = FridaMeetingWorkspace.DecisionOf(artifact);
                    Check(report != null && report.Verdict == FridaTeamDecisionReport.Approved && report.Done.Count == 2 && report.NextSteps.Count == 1, "Decisão estruturada ausente: " + (report == null ? "null" : report.Verdict));
                    Check(report.Evidence.Any(e => e.Ref == "E1" && e.Tool == "read_spreadsheet/read" && e.Summary.Contains("141")), "Recibos fora do relatório.");
                    Check(report.Screenshots.Count == 2 && report.Screenshots.All(s => File.Exists(s.Path) && s.Sha256 == FridaVerifiedTools.Sha256(File.ReadAllBytes(s.Path))), "Prints sem arquivo ou SHA-256: " + report.Screenshots.Count);
                    var skill = FridaSkillRegistry.Get("cruzar-planilhas");
                    Check(skill.Version == 2 && skill.Lessons.Count == 1 && File.ReadAllText(Path.Combine(skill.SkillDirectory, "SKILL.md")).Contains("Lições aprendidas") && report.SkillUpdate.Contains("versão 2"), "Skill não atualizada: " + report.SkillUpdate);
                    Check(artifact.Markdown.Contains("## Resumo executivo") && artifact.Markdown.Contains("12 chassis") && artifact.Markdown.Contains("## Evidências") && artifact.Markdown.Contains("Prints da tela") && artifact.Markdown.Contains("## Skill"), "Relatório incompleto.");
                    var card = store.Messages(c.Id).Single(m => m.Role == "decision");
                    Check(card.ArtifactId == artifact.Id + ":" + artifact.Version && card.Text.Contains("Decisão final: Aprovado") && card.Text.Contains("2 feitos"), "Card da decisão incorreto: " + card.Text);
                    // The verdict never claims more than the review supports.
                    var pending = FridaDecisionReports.Build(new FridaDecisionInput { Decision = "{\"veredito\":\"aprovado\",\"feito\":[\"x\"]}", Audit = "ok", CorrectionRequired = true });
                    var unreviewed = FridaDecisionReports.Build(new FridaDecisionInput { Decision = "{\"veredito\":\"aprovado\"}", Audit = "" });
                    Check(pending.Verdict == FridaTeamDecisionReport.ApprovedWithPending && pending.Pending.Count == 1 && unreviewed.Verdict == FridaTeamDecisionReport.NotReviewed, "Veredito acima das evidências.");
                    // In the conversation: the card with the verdict, the prints and the full report button.
                    var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    FridaIdeForm.AllowOversizeForTests = true;
                    try
                    {
                        using (var http = new System.Net.Http.HttpClient())
                        using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                        {
                            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                            form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1366), FridaTheme.Px(768))); System.Windows.Forms.Application.DoEvents();
                            form.SelectConversation(c.Id); System.Windows.Forms.Application.DoEvents();
                            var cards = (System.Collections.IList)typeof(FridaIdeForm).GetField("_messages", flags).GetValue(form);
                            object view = null; foreach (object item in cards) if (item.GetType().GetField("Footer", flags).GetValue(item) != null) view = item;
                            Check(view != null, "Card da decisão não aparece na conversa.");
                            var footer = (System.Windows.Forms.Control)view.GetType().GetField("Footer", flags).GetValue(view);
                            Check(footer.Controls.OfType<System.Windows.Forms.PictureBox>().Count() == 2 && footer.Controls.OfType<PillButton>().Single().Enabled && footer.Controls.OfType<Chip>().Single().Text == FridaTeamDecisionReport.Approved, "Card sem prints, veredito ou botão.");
                            foreach (object item in cards)
                            {
                                string shown = ((System.Windows.Forms.RichTextBox)item.GetType().GetField("Body", flags).GetValue(item)).Text;
                                Check(!shown.Contains("{\"") && !shown.Contains("\":"), "Resposta JSON do time aparece crua: " + shown.Substring(0, Math.Min(80, shown.Length)));
                            }
                            string render = Environment.GetEnvironmentVariable("FRIDA_QA_SNAPSHOT_DIR");
                            if (!string.IsNullOrEmpty(render))
                            {
                                var history = (System.Windows.Forms.Panel)typeof(FridaIdeForm).GetField("_historyView", flags).GetValue(form);
                                history.AutoScrollPosition = new System.Drawing.Point(0, ((System.Windows.Forms.Control)view.GetType().GetField("Card", flags).GetValue(view)).Top - FridaTheme.Px(40));
                                form.Refresh(); System.Windows.Forms.Application.DoEvents();
                                Snapshot(form, Path.Combine(render, "decisao-final-card.png"));
                            }
                        }
                    }
                    finally { FridaIdeForm.AllowOversizeForTests = false; }
                }
                finally { FridaEvidenceCapture.Capture = null; initialized.SetValue(null, false); }
            }));
            test("Skill verification refuses a trivial command or a missing script and accepts a real test", () => InStore(store =>
            {
                var initialized = typeof(FridaSkillRegistry).GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try
                {
                    FridaSkillRegistry.Initialize(Path.Combine(store.Root, "skills-verify"));
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "so-imprime", Name = "Só imprime", ScriptFileName = "run.py", ScriptContent = "print('ok')\n", VerificationCommand = "Write-Output 'OK'" });
                    var trivial = FridaSkillRegistry.VerifySkill("so-imprime");
                    Check(trivial.Status == FridaResultStatus.Failed && trivial.ErrorCode == "trivial_test_command" && !FridaSkillRegistry.Get("so-imprime").Verified, "Comando trivial verificou a skill: " + trivial.Status);
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "sem-script", Name = "Sem script", ScriptFileName = "run.py", ScriptContent = "# Skill sem código\n", VerificationCommand = "python run.py --check" });
                    var missing = FridaSkillRegistry.VerifySkill("sem-script");
                    Check(missing.Status == FridaResultStatus.Failed && missing.ErrorCode == "script_missing" && !FridaSkillRegistry.Get("sem-script").Verified, "Skill sem script foi verificada: " + missing.Status);
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "soma", Name = "Soma", ScriptFileName = "run.py", ScriptContent = "import sys\nsys.exit(0 if sum([2, 2]) == 4 else 1)\n", VerificationCommand = "python run.py --check" });
                    var real = FridaSkillRegistry.VerifySkill("soma");
                    Check(FridaSkillRegistry.Get("soma").Verified, "Teste real não verificou a skill: " + real.Status + " " + real.Summary);
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "quebrada", Name = "Quebrada", ScriptFileName = "run.py", ScriptContent = "import sys\nsys.exit(1)\n", VerificationCommand = "python run.py --check" });
                    FridaSkillRegistry.VerifySkill("quebrada");
                    Check(!FridaSkillRegistry.Get("quebrada").Verified, "Teste com falha verificou a skill.");
                    string catalog = FridaSkillRegistry.GenerateActiveSkillsSystemPrompt();
                    Check(catalog.Contains("`soma`") && catalog.Contains("status: VERIFICADA") && catalog.Contains("`quebrada`, complexidade: Low, status: RASCUNHO"), "Catálogo não separa verificada de rascunho: " + catalog);
                }
                finally { initialized.SetValue(null, false); }
            }));
            test("Skill catalog loads a hand-edited registry and rebuilds a skill from its SKILL.md", () => InStore(store =>
            {
                var initialized = typeof(FridaSkillRegistry).GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try
                {
                    // 1. A registry reformatted by hand: indented, dates without the serializer's backslashes, folder from another machine.
                    string edited = Path.Combine(store.Root, "skills-edited"), editedSkill = Path.Combine(edited, "skills", "placas-pendentes");
                    Directory.CreateDirectory(editedSkill);
                    File.WriteAllText(Path.Combine(editedSkill, "run.py"), "import sys\nsys.exit(0)\n");
                    File.WriteAllText(Path.Combine(edited, "skills", "frida_skills_registry.json"),
                        "[\n  {\n    \"Id\": \"placas-pendentes\",\n    \"Name\": \"Placas pendentes\",\n    \"Description\": \"Consolida placas\",\n    \"Complexity\": 1,\n    \"TriggerPatterns\": [\"placas pendentes\"],\n    \"ExecutionKind\": \"python\",\n    \"ScriptFileName\": \"run.py\",\n    \"VerificationCommand\": \"python run.py --check\",\n    \"Verified\": true,\n    \"SkillDirectory\": \"Z:\\\\outra-maquina\\\\skills\\\\placas-pendentes\",\n    \"CreatedUtc\": \"/Date(1794171674985)/\",\n    \"LastUsedUtc\": \"/Date(1794171674985)/\",\n    \"SuccessCount\": 1\n  }\n]\n");
                    FridaSkillRegistry.Initialize(edited);
                    var loaded = FridaSkillRegistry.Get("placas-pendentes");
                    Check(loaded != null && loaded.Verified && loaded.ScriptFileName == "run.py" && loaded.ExecutionKind == "python" && loaded.SuccessCount == 1 && loaded.TriggerPatterns.Contains("placas pendentes"), "Registro editado à mão não carregou por inteiro.");
                    Check(loaded.CreatedUtc.Year == 2026 && loaded.SkillDirectory == editedSkill && loaded.Version == 1 && loaded.Lessons != null, "Datas, pasta ou padrões da skill incorretos: " + loaded.CreatedUtc.ToString("o") + " " + loaded.SkillDirectory);
                    Check(Directory.GetFiles(Path.Combine(edited, "skills"), "*.ilegivel-*").Length == 0, "Registro legível foi tratado como ilegível.");

                    // 2. No usable registry: the skill comes back from its own folder, with the script it really has.
                    string manual = Path.Combine(store.Root, "skills-manual"), manualSkill = Path.Combine(manual, "skills", "conferir-estoque");
                    Directory.CreateDirectory(manualSkill);
                    string document = "# Conferir estoque\r\n\r\n## Descrição\r\nConfere o estoque contra a planilha.\r\n\r\n## Metadados Operacionais\r\n- **Complexidade**: `Medium`\r\n- **Modo de Execução**: `python` (`run.py`)\r\n- **Status de Verificação**: ✅ Verificada e Homologada\r\n- **Versão**: 3\r\n\r\n## Lições aprendidas\r\n- 09/11/2026 — Ler a planilha antes do WhatsApp\r\n\r\n## Padrões de Ativação\r\n- `conferir estoque`\r\n- `estoque da planilha`\r\n\r\n## Comando de Verificação\r\n```powershell\r\npython run.py --check\r\n```\r\n\r\n## Instruções de Uso\r\nRode `python run.py --search CX-1` para um item.\r\n";
                    File.WriteAllText(Path.Combine(manualSkill, "SKILL.md"), document, new System.Text.UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(manualSkill, "run.py"), "import sys\nsys.exit(0 if sum([2, 2]) == 4 else 1)\n");
                    File.WriteAllText(Path.Combine(manual, "skills", "frida_skills_registry.json"), "[ { \"Id\": \"conferir-estoque\", \"CreatedUtc\": \"ontem\" } ]");
                    FridaSkillRegistry.Initialize(manual);
                    var rebuilt = FridaSkillRegistry.Get("conferir-estoque");
                    Check(rebuilt != null && rebuilt.Name == "Conferir estoque" && rebuilt.ScriptFileName == "run.py" && rebuilt.ExecutionKind == "python" && rebuilt.VerificationCommand == "python run.py --check", "Skill não reconstruída a partir do SKILL.md.");
                    Check(rebuilt.TriggerPatterns.SequenceEqual(new[] { "conferir estoque", "estoque da planilha" }) && rebuilt.Lessons.Count == 1 && rebuilt.Version == 3 && rebuilt.Complexity == FridaSkillComplexity.Medium, "Gatilhos, lições, versão ou complexidade perdidos: " + string.Join("|", rebuilt.TriggerPatterns.ToArray()));
                    Check(!rebuilt.Verified, "O texto do documento marcou a skill como verificada sem recibo.");
                    Check(Directory.GetFiles(Path.Combine(manual, "skills"), "*.ilegivel-*").Length == 1, "Registro ilegível não foi preservado antes de ser reescrito.");
                    // Verifying it writes the receipt and keeps the hand-written instructions.
                    FridaSkillRegistry.VerifySkill("conferir-estoque");
                    Check(FridaSkillRegistry.Get("conferir-estoque").Verified && File.Exists(Path.Combine(manualSkill, "verification.json")), "Skill reconstruída não verificou com teste real.");
                    Check(File.ReadAllText(Path.Combine(manualSkill, "SKILL.md")) == document, "SKILL.md escrito à mão foi sobrescrito.");
                    // With the registry gone again, the receipt (not the document) restores the verified state.
                    File.Delete(Path.Combine(manual, "skills", "frida_skills_registry.json"));
                    FridaSkillRegistry.Initialize(manual);
                    Check(FridaSkillRegistry.Get("conferir-estoque").Verified, "Recibo de verificação ignorado na reconstrução.");
                    // A later failed check removes the receipt, so the skill cannot come back as verified.
                    File.WriteAllText(Path.Combine(manualSkill, "run.py"), "import sys\nsys.exit(1)\n");
                    FridaSkillRegistry.VerifySkill("conferir-estoque");
                    Check(!FridaSkillRegistry.Get("conferir-estoque").Verified && !File.Exists(Path.Combine(manualSkill, "verification.json")), "Recibo antigo sobreviveu a uma verificação reprovada.");
                    // A document Frida generated herself is still refreshed.
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "gerada", Name = "Gerada", Description = "Primeira descrição" });
                    FridaSkillRegistry.RegisterAndSave(new FridaSkillEntry { Id = "gerada", Name = "Gerada", Description = "Segunda descrição" });
                    Check(File.ReadAllText(Path.Combine(manual, "skills", "gerada", "SKILL.md")).Contains("Segunda descrição"), "SKILL.md gerado pela Frida não foi atualizado.");
                }
                finally { initialized.SetValue(null, false); }
            }));
            test("Skill evolution reports a draft when verification fails and verified only after a real test", () => InStore(store =>
            {
                var initialized = typeof(FridaSkillRegistry).GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try
                {
                    FridaSkillRegistry.Initialize(Path.Combine(store.Root, "skills-evolve"));
                    Func<string, string, Func<TeamRole, string, Task<string>>> team = (id, verification) => (role, prompt) =>
                    {
                        if (role == TeamRole.Manager) return Task.FromResult("{\"skill_id\":\"" + id + "\",\"skill_name\":\"Conferir soma\",\"description\":\"Confere uma soma\",\"complexity\":\"Low\",\"implementation_criteria\":\"somar\",\"verification_command\":\"" + verification + "\",\"trigger_keywords\":[\"conferir soma\"]}");
                        if (role == TeamRole.Technician) return Task.FromResult("{\"script_filename\":\"run.py\",\"script_content\":\"import sys\\nsys.exit(0 if sum([2, 2]) == 4 else 1)\\n\"}");
                        return Task.FromResult("OK");
                    };
                    var draft = FridaSkillOrchestrator.EvolveSkillDetailedAsync("Crie uma skill para conferir somas", new FridaSettings(), team("soma-rascunho", "Write-Output 'OK'"), null, null).GetAwaiter().GetResult();
                    Check(!draft.Passed && draft.SkillId == "soma-rascunho" && draft.Summary.Contains("rascunho") && !draft.Summary.Contains("Aprendida e Verificada") && !FridaSkillRegistry.Get("soma-rascunho").Verified, "Rascunho apresentado como verificado: " + draft.Summary);
                    var learned = FridaSkillOrchestrator.EvolveSkillDetailedAsync("Crie uma skill para conferir somas", new FridaSettings(), team("soma-testada", "python run.py --check"), null, null).GetAwaiter().GetResult();
                    Check(learned.Passed && learned.Summary.Contains("Aprendida e Verificada") && FridaSkillRegistry.Get("soma-testada").Verified, "Skill com teste real não foi verificada: " + learned.Summary);
                    // The text-only entry point still returns the same honest summary.
                    string text = FridaSkillOrchestrator.EvolveSkillAsync("Crie uma skill para conferir somas", new FridaSettings(), team("soma-texto", "echo ok"), null, null).GetAwaiter().GetResult();
                    Check(text.Contains("rascunho") && !text.Contains("✅"), "Resumo textual esconde a falha: " + text);
                }
                finally { initialized.SetValue(null, false); }
            }));
            test("Team learns a skill only from approved work that built a new procedure", () => InStore(store =>
            {
                var initialized = typeof(FridaSkillRegistry).GetField("_initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try
                {
                    FridaSkillRegistry.Initialize(Path.Combine(store.Root, "skills-team"));
                    Func<string, bool, int[]> run = (request, approved) =>
                    {
                        // [0] learning prompts seen, [1] 1 when learning came after the manager's verdict
                        var seen = new int[2]; bool decided = false;
                        var c = store.Create("Rodada", FridaWorkspaceSection.Chat, FridaConversationKind.Meeting);
                        var coordinator = new FridaTeamCoordinator(new FridaSettings { max_correction_cycles = 1 }, (role, prompt) =>
                        {
                            if (role == TeamRole.Manager && prompt.Contains("\"veredito\"")) { decided = true; return Task.FromResult("{\"veredito\":\"aprovado\",\"feito\":[\"Tarefa concluída\"],\"pendente\":[],\"nao_feito\":[],\"justificativa\":\"Recibos aprovados.\",\"proximos_passos\":[]}"); }
                            if (role == TeamRole.Manager && prompt.Contains("learn_new_skill")) { seen[0]++; if (decided) seen[1] = 1; return Task.FromResult("{\"learn_new_skill\": false}"); }
                            if (role == TeamRole.Manager) return Task.FromResult("{\"plan\":\"Executar o pedido\"}");
                            if (role == TeamRole.Analyst) return Task.FromResult(approved ? "[APROVADO_SEM_PENDENCIAS] Comprovado pelos recibos." : "[REPROVAR_CORRECAO_NECESSARIA] DEFEITO_ENCONTRADO: o script falha.");
                            if (role == TeamRole.Secretary) return Task.FromResult("Pedido atendido.");
                            return Task.FromResult("OK");
                        });
                        using (FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = c.Id, Store = store, Kind = FridaConversationKind.Meeting }))
                            coordinator.RunAsync(request, instruction => Task.FromResult("Executado."), () => "Sem evidencias de ferramentas.", null, null, new FridaTaskPlan { Kind = FridaTaskKind.ApplicationBuild, NeedsTeam = true }).GetAwaiter().GetResult();
                        return seen;
                    };
                    var routine = run("Organize os relatórios do mês em uma pasta", true);
                    Check(routine[0] == 0, "Rodada rotineira gastou uma consulta de aprendizado de skill.");
                    var procedure = run("Instale o ffmpeg e monte um script de conversão de vídeo", true);
                    Check(procedure[0] == 1 && procedure[1] == 1, "Novo procedimento aprovado não foi avaliado depois do veredito: " + procedure[0] + "/" + procedure[1]);
                    var rejected = run("Instale o ffmpeg e monte um script de conversão de vídeo", false);
                    Check(rejected[0] == 0, "Trabalho reprovado gerou aprendizado de skill.");
                }
                finally { initialized.SetValue(null, false); }
            }));
            test("Workspace composer empties as soon as the message is sent and gets it back if the send fails", () => InStore(store =>
            {
                var c = store.Create("Envio", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                var reply = new TaskCompletionSource<string>();
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new FridaIdeForm((text, p, audio) => reply.Task, () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                {
                    form.SelectConversation(c.Id);
                    var draft = (System.Windows.Forms.TextBox)typeof(FridaIdeForm).GetField("_draft", flags).GetValue(form);
                    var refs = (System.Windows.Forms.Control)typeof(FridaIdeForm).GetField("_references", flags).GetValue(form);
                    string file = Path.Combine(store.Root, "anexo.txt"); File.WriteAllText(file, "x");
                    draft.Text = "Cruze as planilhas";
                    typeof(FridaIdeForm).GetMethod("AddReference", flags).Invoke(form, new object[] { file });
                    var pending = (Task)typeof(FridaIdeForm).GetMethod("SendDraft", flags).Invoke(form, null);
                    Check(!pending.IsCompleted && draft.Text == "" && refs.Controls.Count == 0 && store.Get(c.Id).Draft == "", "A caixa manteve o texto enviado enquanto a Frida responde.");
                    reply.SetException(new IOException("offline"));
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!pending.IsCompleted && clock.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    Check(draft.Text == "Cruze as planilhas" && refs.Controls.Count == 1, "Falha no envio perdeu o texto ou o anexo.");
                }
            }));
            test("Workspace replies keep tool output and long code behind technical details", () =>
            {
                string reply = "As planilhas 2 e 3 foram abertas e confirmadas.\nPreciso da sua autorização para ler as abas.\nProcesso encerrado com codigo zero. O resultado funcional ainda precisa de verificacao.\nCodigo de saida: 0\n\nFullName\n--------\nC:\\Users\\x\\planilha.xlsx\n[STDERR]: ";
                string answer, details; FridaReplyParts.Split(reply, out answer, out details);
                Check(answer.EndsWith("ler as abas.") && !answer.Contains("Codigo de saida") && details.Contains("Codigo de saida: 0") && details.Contains("planilha.xlsx"), "Saída técnica misturada à resposta: " + answer);
                string receipt = "{\"schema_version\":1,\"tool\":\"open_program_or_url\",\"status\":\"Verified\",\"summary\":\"Janela/aba confirmada no desktop: Chrome\"}";
                FridaReplyParts.Split(receipt, out answer, out details);
                Check(answer.StartsWith("Recibo da ferramenta **open_program_or_url** (Verified)") && details.Contains("schema_version"), "Recibo JSON cru na conversa: " + answer);
                string code = "Segue o script:\n```python\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => "print(" + i + ")")) + "\n```\nPronto.";
                FridaReplyParts.Split(code, out answer, out details);
                Check(answer.Contains("print(6)") && !answer.Contains("print(7)") && answer.Contains("mais 24 linhas") && answer.EndsWith("Pronto.") && details.Contains("print(30)"), "Código longo não foi recolhido: " + answer);
                Check(FridaReplyParts.Answer(reply, 500) == "As planilhas 2 e 3 foram abertas e confirmadas. Preciso da sua autorização para ler as abas.", "Memória com saída técnica.");
                // In the conversation: the answer, a details button, and the full text on demand.
                InStore(store =>
                {
                    var c = store.Create("Detalhes", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                    store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Role = "assistant", Author = "Frida", Text = reply });
                    var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    using (var http = new System.Net.Http.HttpClient())
                    using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                    {
                        string render = Environment.GetEnvironmentVariable("FRIDA_QA_SNAPSHOT_DIR");
                        if (!string.IsNullOrEmpty(render))
                        {
                            FridaIdeForm.AllowOversizeForTests = true;
                            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false; form.Show();
                            form.ResizeClientForTests(new System.Drawing.Size(FridaTheme.Px(1366), FridaTheme.Px(768))); System.Windows.Forms.Application.DoEvents();
                        }
                        form.SelectConversation(c.Id);
                        var view = ((System.Collections.IList)typeof(FridaIdeForm).GetField("_messages", flags).GetValue(form))[0];
                        if (!string.IsNullOrEmpty(render)) { form.Refresh(); System.Windows.Forms.Application.DoEvents(); Snapshot(form, Path.Combine(render, "detalhes-recolhidos.png")); FridaIdeForm.AllowOversizeForTests = false; }
                        var body = (System.Windows.Forms.RichTextBox)view.GetType().GetField("Body", flags).GetValue(view);
                        var button = (PillButton)view.GetType().GetField("DetailsButton", flags).GetValue(view);
                        Check(button != null && !body.Text.Contains("Codigo de saida") && body.Text.Contains("autorização"), "Balão mostra a saída técnica: " + body.Text);
                        button.Invoke();
                        Check(body.Text.Contains("Codigo de saida: 0") && button.Text.StartsWith("Ocultar"), "Detalhes técnicos não abriram.");
                        button.Invoke();
                        Check(!body.Text.Contains("Codigo de saida"), "Detalhes técnicos não recolheram.");
                    }
                });
            });
            test("Workspace conversations move into projects and feed the project memory", () => InStore(store =>
            {
                string folder = Path.Combine(store.Root, "kanban"); Directory.CreateDirectory(folder);
                var project = store.RegisterProject("Kanban COPAC", folder);
                var old = store.Create("Cruzamento de chassis", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.RecordReport(new FridaConversationEntry { conversation_id = old.Id, request_id = Guid.NewGuid().ToString("N"), request = "Cruze as planilhas de chassi",
                    response = "Encontrei 12 chassis confirmados no WhatsApp.\nCodigo de saida: 0\n[STDERR]: nada", started_utc = DateTime.UtcNow.AddMinutes(-5).ToString("o"), finished_utc = DateTime.UtcNow.AddMinutes(-4).ToString("o") });
                var current = store.Create("Nova análise", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation, project.Id);
                Check(store.ProjectMemory(project.Id, current.Id) == "", "Memória do projeto antes da conversa entrar.");
                store.AssignProject(old.Id, project.Id);
                string memory = store.ProjectMemory(project.Id, current.Id);
                Check(store.Get(old.Id).ProjectId == project.Id && memory.Contains("Cruzamento de chassis") && memory.Contains("12 chassis confirmados") && !memory.Contains("Codigo de saida"), "Conversa movida não entrou na memória do projeto: " + memory);
                using (FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = current.Id, ProjectId = project.Id, Project = project, Store = store }))
                    Check(FridaIdeRuntime.ProjectInstructions().Contains("[MEMÓRIA DO PROJETO]") && FridaIdeRuntime.ProjectInstructions().Contains("12 chassis"), "Pedido no projeto sem a memória do projeto.");
                bool rejected = false; try { store.AssignProject(old.Id, Guid.NewGuid().ToString("N")); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Projeto inexistente aceito.");
                store.AssignProject(old.Id, null);
                Check(store.Get(old.Id).ProjectId == null && store.ProjectMemory(project.Id, current.Id) == "", "Remover do projeto não tirou a conversa da memória.");
                // From the list menu, on the open conversation: it follows into the project.
                var settings = new FridaSettings(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new FridaIdeForm((text, p, audio) => Task.FromResult("ok"), () => {}, text => {}, () => {}, settings, "config.example.json", new AudioVoiceEngine(settings, null, http), store))
                {
                    form.SelectConversation(old.Id);
                    typeof(FridaIdeForm).GetMethod("MoveConversationToProject", flags).Invoke(form, new object[] { store.Get(old.Id), store.Projects().Single() });
                    var filter = (SegmentedTabs)typeof(FridaIdeForm).GetField("_filterTabs", flags).GetValue(form);
                    Check(store.Get(old.Id).ProjectId == project.Id && form.SelectedConversationId == old.Id && filter.SelectedIndex == 1, "A conversa aberta não acompanhou o projeto.");
                }
            }));
            test("Workspace welcome logo is large and no welcome text is clipped or overlapping at 100/150/200%", () =>
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                string render = Environment.GetEnvironmentVariable("FRIDA_QA_SNAPSHOT_DIR");
                var problems = new System.Collections.Generic.List<string>();
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    FridaTheme.ScaleOverride = scale;
                    try
                    {
                        // Chat area sizes: wide (the operator's screenshot), laptop and the narrowest window.
                        foreach (var logical in new[] { new System.Drawing.Size(1508, 766), new System.Drawing.Size(1000, 560), new System.Drawing.Size(560, 430) })
                        {
                            var size = new System.Drawing.Size((int)(logical.Width * scale), (int)(logical.Height * scale));
                            using (var welcome = new FridaWelcomeView { Size = size })
                            using (var bitmap = new System.Drawing.Bitmap(size.Width, size.Height))
                            {
                                welcome.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
                                if (!string.IsNullOrEmpty(render)) { Directory.CreateDirectory(render); bitmap.Save(Path.Combine(render, "welcome-" + (int)(scale * 100) + "-" + logical.Width + "x" + logical.Height + ".png")); }
                                string where = (scale * 100) + "% " + logical.Width + "x" + logical.Height;
                                float radius = (float)typeof(FridaWelcomeView).GetField("_logoRadius", flags).GetValue(welcome);
                                var center = (System.Drawing.PointF)typeof(FridaWelcomeView).GetField("_logoCenter", flags).GetValue(welcome);
                                var title = (System.Drawing.Rectangle)typeof(FridaWelcomeView).GetField("_titleRect", flags).GetValue(welcome);
                                var subtitle = (System.Drawing.Rectangle)typeof(FridaWelcomeView).GetField("_subtitleRect", flags).GetValue(welcome);
                                var view = new System.Drawing.Rectangle(System.Drawing.Point.Empty, size);
                                int titleNeed = System.Windows.Forms.TextRenderer.MeasureText("Fala comigo chefe", FridaTheme.Strong(24f)).Height;
                                if (title.Height < titleNeed) problems.Add(where + ": título cortado (" + title.Height + " < " + titleNeed + ")");
                                if (!view.Contains(title) || !view.Contains(subtitle)) problems.Add(where + ": texto fora da tela");
                                if (subtitle.Top < title.Bottom) problems.Add(where + ": subtítulo sobre o título");
                                if (title.Top < center.Y + radius * 1.14f) problems.Add(where + ": título sobre o logo");
                                if (center.Y - radius * 1.14f < 0) problems.Add(where + ": logo cortado no topo");
                                var chips = (System.Collections.IEnumerable)typeof(FridaWelcomeView).GetField("_chips", flags).GetValue(welcome);
                                foreach (object chip in chips)
                                {
                                    var bounds = (System.Drawing.Rectangle)chip.GetType().GetField("Bounds").GetValue(chip);
                                    if (bounds.IsEmpty) continue;
                                    if (!view.Contains(bounds) || bounds.Top < subtitle.Bottom) problems.Add(where + ": atalho cortado ou sobre o texto " + bounds);
                                }
                                // Large on the wide view: about 2.3x the former 84 px radius.
                                if (logical.Width == 1508 && radius < 180f * scale) problems.Add(where + ": logo pequeno (" + radius + ")");
                            }
                        }
                    }
                    finally { FridaTheme.ScaleOverride = 0; }
                }
                Check(problems.Count == 0, string.Join("\n", problems));
            });
            test("Workspace welcome screen never disposes the shared theme fonts", () =>
            {
                using (var welcome = new FridaWelcomeView { Size = new System.Drawing.Size(FridaTheme.Px(900), FridaTheme.Px(600)) })
                using (var bitmap = new System.Drawing.Bitmap(welcome.Width, welcome.Height))
                {
                    welcome.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, welcome.Width, welcome.Height));
                    welcome.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, welcome.Width, welcome.Height));
                }
                foreach (var font in new[] { FridaTheme.Strong(22f), FridaTheme.Font(10.5f), FridaTheme.Font(9.5f) })
                {
                    bool alive; try { alive = font.GetHeight() > 0; } catch (ArgumentException) { alive = false; }
                    Check(alive, "Fonte compartilhada descartada pela tela inicial: " + font.Size);
                }
            });
            test("Workspace corrupt metadata is preserved and credentials are redacted", () => InStore(store =>
            {
                var c = store.Create("C", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation);
                store.PutMessage(new FridaConversationMessage { ConversationId = c.Id, Text = "password=segredo" });
                Check(!store.Messages(c.Id).Single().Text.Contains("segredo"), "Credencial persistida.");
                string path = Path.Combine(store.Root, "workspace.json"); File.WriteAllText(path, "{broken");
                bool failed = false;
                try { store.Create("D", FridaWorkspaceSection.Chat, FridaConversationKind.Conversation); } catch { failed = true; }
                Check(failed && File.ReadAllText(path) == "{broken", "Catálogo corrompido sobrescrito.");
            }));
        }
    }
}
