using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FridaAgent
{
    public sealed partial class FridaIdeForm : Form
    {
        private readonly Func<string, FridaIdeProject, byte[], Task<string>> _send;
        private readonly Action _stop;
        private readonly Action<string> _speak;
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill };
        private readonly Panel _navigation = new Panel { Dock = DockStyle.Left, Width = 60 };

        private readonly TextBox _draft = new ComposerTextBox { Multiline = true, BorderStyle = BorderStyle.None, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
        private readonly RichTextBox _terminal = TerminalArea();
        private readonly ListBox _terminalFiles = new ListBox { Dock = DockStyle.Bottom, AccessibleName = "Arquivos usados nesta conversa" };
        private readonly ComboBox _projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TabControl _editors = new HeadlessTabControl { Dock = DockStyle.Fill };
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 28, Text = "Pronta", Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft };
        private readonly List<FridaIdeProject> _projectList;
        private readonly Dictionary<TabPage, string> _paths = new Dictionary<TabPage, string>();
        private readonly Dictionary<TabPage, FridaIdeProject> _editorProjects = new Dictionary<TabPage, FridaIdeProject>();
        private Control _editorPage;
        private string _requestedDirectory;
        private Action _refreshFiles;
        private readonly Dictionary<TabPage, string> _hashes = new Dictionary<TabPage, string>();
        private readonly Dictionary<string, Button> _sectionButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();

        private readonly FridaSettings _settings;
        private readonly string _configPath;
        private readonly AudioVoiceEngine _voice;
        private byte[] _recordedAudio;
        private int _recordingVersion;
        private bool _transcribing;
        private CancellationTokenSource _transcriptionCancellation;
        internal Func<byte[], Task<string>> TranscribeRecording;
        private readonly System.Windows.Forms.Timer _recordingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private int _recordingSeconds;
        private CancellationTokenSource _executionCancellation;
        private ZeldaExecutiveLlmDashboard _zelda;
        private readonly List<Action> _cleanup = new List<Action>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposingIde;
        private bool _busy;

        private readonly HashSet<string> _displayedRequests = new HashSet<string>();
        public FridaIdeProject SelectedProject { get { return _projects.SelectedItem as FridaIdeProject; } }

        public FridaIdeForm(Func<string, FridaIdeProject, byte[], Task<string>> send, Action stop, Action<string> speak,
            Action voiceOff, FridaSettings settings, string configPath, AudioVoiceEngine voice, FridaConversationStore workspaceStore = null)
        {
            SuspendLayout();
            _voice = voice;
            TranscribeRecording = voice.TranscribeIdeRecordingAsync;
            _draft.AccessibleName = "Mensagem para a Frida";
            _draft.AccessibleDescription = "Enter envia. Shift e Enter criam uma nova linha.";
            _send = send; _stop = stop; _speak = speak; _settings = settings; _configPath = configPath;
            Text = "Frida · Chat IDE";
            // Sizes and fonts follow FridaTheme's single scale factor; WinForms auto-scaling stays
            // off so explicitly scaled sizes are not scaled twice at 150/200%.
            AutoScaleMode = AutoScaleMode.None;
            Font = FridaTheme.Font(10);
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(FridaTheme.Px(1320), area.Width * 9 / 10), Math.Min(FridaTheme.Px(840), area.Height * 9 / 10));
            MinimumSize = new Size(Math.Min(FridaTheme.Px(900), area.Width), Math.Min(FridaTheme.Px(600), area.Height));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas; ForeColor = Ink;
            string icon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frida.ico");
            if (File.Exists(icon)) Icon = new Icon(icon);
            BuildShell(voiceOff);
            var miVoiceOptions = new ToolStripMenuItem("Opções de Voz da Frida");
            var miFrancisca = new ToolStripMenuItem("Francisca (Edge · Grátis)");
            var miAoede = new ToolStripMenuItem("Aoede (Gemini · Natural)");
            var miTestVoice = new ToolStripMenuItem("Ouvir Demonstração da Voz Atual...");

            miFrancisca.Click += delegate
            {
                _settings.prefer_zero_quota_tts = true;
                FridaSettings.Save(_settings, _configPath);
                miFrancisca.Checked = true;
                miAoede.Checked = false;
                Progress("Voz da Frida alterada para Francisca.");
                if (!_voice.IsOff)
                {
                    Task.Run(async () => {
                        try { await _voice.SpeakResponseAsync("Voz alterada para Francisca.", false); } catch { }
                    });
                }
            };
            miAoede.Click += delegate
            {
                _settings.prefer_zero_quota_tts = false;
                _settings.female_voice = "Aoede";
                FridaSettings.Save(_settings, _configPath);
                miFrancisca.Checked = false;
                miAoede.Checked = true;
                Progress("Voz da Frida alterada para Aoede.");
                if (!_voice.IsOff)
                {
                    Task.Run(async () => {
                        try { await _voice.SpeakResponseAsync("Voz alterada para Aoede.", false); } catch { }
                    });
                }
            };
            miTestVoice.Click += async delegate
            {
                string v = _settings.prefer_zero_quota_tts ? "Francisca" : "Aoede";
                await _voice.SpeakResponseAsync("Olá! Esta é a voz " + v + " da Frida.", false);
            };

            miVoiceOptions.DropDownOpening += delegate
            {
                miFrancisca.Checked = _settings.prefer_zero_quota_tts;
                miAoede.Checked = !_settings.prefer_zero_quota_tts;
            };

            miVoiceOptions.DropDownItems.Add(miFrancisca);
            miVoiceOptions.DropDownItems.Add(miAoede);
            miVoiceOptions.DropDownItems.Add(new ToolStripSeparator());
            miVoiceOptions.DropDownItems.Add(miTestVoice);

            _options.Items.Add(new ToolStripSeparator());
            _options.Items.Add(miVoiceOptions);
            _projectList = FridaIdeRuntime.LoadProjects();
            _projects.AccessibleName = "Projeto ativo";
            _projects.Items.Add("Sem projeto · Computador");
            foreach (var project in _projectList) _projects.Items.Add(project);
            _projects.SelectedIndex = 0;
            FridaIdeRuntime.ActiveProject = null;
            _projects.SelectedIndexChanged += delegate { FridaIdeRuntime.ActiveProject = SelectedProject; };
            BuildChat();
            _editorPage = MakeCode();
            // Files and terminals live in the right-hand panel; they are pages without a rail entry.
            _pages.Add("Arquivos", MakeFiles());
            _pages.Add("Terminal", MakeTerminalPage());
            AddPage("WhatsApp", MakeWhatsApp());
            AddPage("Zelda Manager", new Panel { Dock = DockStyle.Fill });
            _draft.KeyDown += async (sender, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendDraft(); } };
            FridaIdeRuntime.Changed += OnActivity;
            FridaIdeRuntime.ArtifactChanged += OnArtifact;
            foreach (var artifact in FridaIdeRuntime.Artifacts().OrderByDescending(item => item.LastUsedUtc).Take(100)) _terminalFiles.Items.Add(artifact);
            FridaConversationAudit.Saved += OnConversationSaved;
            FridaIdeRuntime.PairingRequested += OnPairingRequested;
            _terminal.Text = FridaIdeRuntime.Snapshot();
            FormClosing += (sender, e) => { if (e.CloseReason == CloseReason.UserClosing && !FridaShutdown.IsShuttingDown) { e.Cancel = true; Hide(); } };

            InitializeWorkspace(workspaceStore);
            ShowSection(_workspaceSection == FridaWorkspaceSection.Code ? "Código" : "Chat");
            if (!string.IsNullOrEmpty(FridaIdeRuntime.StorageWarning)) _status.Text = FridaIdeRuntime.StorageWarning;
            ResumeLayout(true);
            RefreshShell();
            Shown += delegate { RefreshShell(); if (_currentSection == "Chat" || _currentSection == "Código") _draft.Focus(); };
        }
        // Execution terminal for the right panel: header with copy/clear/more, output, and the
        // files touched in this conversation.
        private Control MakeTerminalPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            _terminalFiles.Height = Px(120);
            FridaTheme.StyleList(_terminalFiles, item => { var a = item as FridaArtifactRecord; return a == null ? null : new[] { a.Operation == "mkdir" || a.Operation == "list" ? FridaTheme.GlyphFolder : FridaTheme.GlyphDocument, System.IO.Path.GetFileName(a.Path.TrimEnd('\\', '/')), a.Destination + " · " + a.Path }; }, 40);
            _terminalFiles.DoubleClick += async delegate { try { await OpenArtifact(_terminalFiles.SelectedItem as FridaArtifactRecord); } catch (Exception ex) { Progress(ex.Message); } };
            _terminalFiles.KeyDown += async (sender, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; try { await OpenArtifact(_terminalFiles.SelectedItem as FridaArtifactRecord); } catch (Exception ex) { Progress(ex.Message); } } };
            var verify = AsyncButton("Verificar resultado remoto", VerifyRemoteResult);
            var header = TerminalHeader("Execução · comandos e saída", FridaTheme.GlyphTerminal, _terminal, verify);
            var filesHeader = new Label { Text = "ARQUIVOS DESTA CONVERSA", Dock = DockStyle.Bottom, Height = Px(28), Font = FridaTheme.Strong(8.5f), ForeColor = Muted, BackColor = Canvas, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(Px(10), 0, 0, Px(4)) };
            page.Controls.Add(_terminal); page.Controls.Add(filesHeader); page.Controls.Add(_terminalFiles); page.Controls.Add(header);
            return page;
        }
        // Compact header for a terminal block: title, copy, clear and optional overflow actions.
        private ActionBar TerminalHeader(string title, string glyph, RichTextBox box, params Button[] more)
        {
            var bar = new ActionBar(true) { BackColor = Canvas };
            var label = new Label { Text = title, Font = FridaTheme.Strong(9.5f), ForeColor = Ink, AutoSize = true, BackColor = Canvas, AccessibleRole = AccessibleRole.StaticText, AutoEllipsis = true };
            bar.Controls.Add(label);
            var copy = new PillButton("", PillKind.Icon, FridaTheme.GlyphCopy, true) { AccessibleName = "Copiar " + title };
            copy.Click += delegate { if (box.TextLength > 0) Clipboard.SetText(box.Text); Progress("Terminal copiado."); };
            var clear = new PillButton("", PillKind.Icon, FridaTheme.GlyphDelete, true) { AccessibleName = "Limpar a tela de " + title };
            clear.Click += delegate { box.Clear(); };
            FridaTheme.Tip(copy, "Copiar"); FridaTheme.Tip(clear, "Limpar a tela (o registro continua salvo)");
            foreach (var extra in more) bar.Controls.Add(extra);
            bar.AddRight(copy); bar.AddRight(clear);
            return bar;
        }
        private async Task VerifyRemoteResult()
        {
            using (var dialog = new FridaDialog("Execuções e gravações remotas", 620, 190))
            {
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Execução remota" };
                foreach (var execution in FridaSshWorkspace.Executions()) list.Items.Add(execution);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                dialog.Body.Controls.Add(new InputFrame(list) { Dock = DockStyle.Top });
                dialog.AddButton("Cancelar", DialogResult.Cancel, PillKind.Secondary);
                dialog.AddButton("Conferir no servidor", DialogResult.OK, PillKind.Primary);
                if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedItem == null) return;
                var selected = (FridaRemoteExecution)list.SelectedItem;
                string result = await Task.Run(() => FridaSshWorkspace.Reconcile(selected.Id));
                FridaIdeRuntime.Publish(result); Progress(result);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposingIde)
            {
                SaveWorkspaceDraft(); SaveLayout();
                _disposingIde = true;
                _tips.Dispose();
                _options.Dispose();
                _lifetime.Cancel();
                if (_transcriptionCancellation != null) _transcriptionCancellation.Cancel();
                foreach (var cleanup in _cleanup) cleanup();
                _cleanup.Clear();
                FridaIdeRuntime.Changed -= OnActivity;
                FridaIdeRuntime.ArtifactChanged -= OnArtifact;
                FridaConversationAudit.Saved -= OnConversationSaved;
                FridaIdeRuntime.PairingRequested -= OnPairingRequested;
                if (_executionCancellation != null) _executionCancellation.Cancel();
                _recordingTimer.Stop(); _recordingTimer.Dispose();
                _voice.FinishIdeRecording(true);
                foreach (var page in _pages.Values) if (!page.IsDisposed) page.Dispose();
            }
            base.Dispose(disposing);
        }
        private void Post(Action action)
        {
            if (IsDisposed || Disposing || _disposingIde || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing && !_disposingIde) action(); })); }
            catch (InvalidOperationException) { }
        }
        private static RichTextBox TextArea(bool readOnly)
        {
            var area = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = readOnly, BorderStyle = BorderStyle.None, Font = FridaTheme.Font(10.5f), DetectUrls = true, BackColor = Canvas, ForeColor = Ink };
            FridaTheme.DarkScroll(area);
            EnableHttpLinks(area);
            return area;
        }
        private static RichTextBox TerminalArea()
        {
            var area = TextArea(true);
            area.Font = FridaTheme.Mono(9.5f); area.WordWrap = true;
            area.Padding = new Padding(4);
            return area;
        }
        private static void EnableHttpLinks(RichTextBox area)
        {
            area.LinkClicked += (sender, e) =>
            {
                Uri url;
                if (Uri.TryCreate(e.LinkText, UriKind.Absolute, out url) && (url.Scheme == "https" || url.Scheme == "http"))
                    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { FridaDialog.Info(area.FindForm(), "Frida", ex.Message); }
            };
        }
        // Single-row bar that never wraps or overlaps; extra buttons go to its "more" menu.
        private static ActionBar Bar(bool compact = true) { return new ActionBar(compact); }
        private static Button Button(string text, Action action, PillKind kind = PillKind.Secondary, string glyph = null)
        {
            var button = new PillButton(text, kind, glyph, true);
            button.Click += (sender, e) => { try { action(); } catch (Exception ex) { FridaDialog.Info(button.FindForm(), "Frida", ex.Message); } };
            return button;
        }
        private static Button AsyncButton(string text, Func<Task> action, PillKind kind = PillKind.Secondary, string glyph = null)
        {
            var button = new PillButton(text, kind, glyph, true);
            button.Click += async (sender, e) =>
            {
                button.Enabled = false;
                try { await action(); }
                catch (Exception ex) { if (!button.IsDisposed) FridaDialog.Info(button.FindForm(), "Frida", ex.Message); }
                finally { if (!button.IsDisposed) button.Enabled = true; }
            };
            return button;
        }
        private static void Theme(Control control) { FridaTheme.Apply(control); }
        private void AddPage(string title, Control page)
        {
            Theme(page);
            _pages.Add(title, page);
            _body.Controls.Add(page); page.Visible = false; page.BringToFront();
            AddNavigationButton(title);
        }
        public void ShowSection(string name)
        {
            bool workspace = ShowWorkspaceSection(ref name);
            if (workspace && _conversation != null && _conversation.Section != _workspaceSection) { SaveWorkspaceDraft(); ClearSelectedConversation(); }
            Text = "Frida · " + name;
            _currentSection = name;
            RefreshNavigation();
            _status.Visible = name == "WhatsApp";
            // Only the pages that change are toggled: hiding and re-showing the current page made the
            // whole window blink on every conversation switch.
            Control selected = _pages[name];
            if (selected.Parent == null) { _body.Controls.Add(selected); selected.BringToFront(); }
            foreach (Control page in _body.Controls) if (page != selected) page.Visible = false; // no-op when already hidden
            selected.Visible = true;
            if (workspace) { LayoutChat(); RefreshConversationHeader(); if (_workspaceCenter.SelectedTab == _conversationTab) _draft.Focus(); }
            if (name == "Zelda Manager" && selected.Controls.Count == 0) BuildZelda(selected);
        }
        private void BuildZelda(Control page)
        {
            _zelda = new ZeldaExecutiveLlmDashboard(_settings, _configPath, () => FridaDialog.Info(this, "Zelda Manager", "As configurações estão no menu Opções da Frida."));
            _zelda.EnableEmbeddedLayout(); page.Controls.Add(_zelda); _zelda.Show();
        }
        public async Task<string> SendMeetingAsync(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return "Informe uma pauta para a reunião.";
            if (_busy) return "A Frida está atendendo. Aguarde ou interrompa para iniciar a reunião.";
            var meeting = _workspaceStore.Create(topic, FridaWorkspaceRouting.Classify(topic) ?? _workspaceSection, FridaConversationKind.Meeting, _conversation == null ? null : _conversation.ProjectId);
            SelectConversation(meeting.Id); _draft.Text = topic; await SendDraft();
            int count = _workspaceStore.MessageCount(meeting.Id);
            return count == 0 ? "Reunião criada." : _workspaceStore.Messages(meeting.Id, Math.Max(0, count - 1), 1).Last().Text;
        }
        public async void NavigateZelda(DashboardRoom? room, string topic)
        {
            if (!string.IsNullOrWhiteSpace(topic)) { try { await SendMeetingAsync(topic); } catch (Exception ex) { Progress(ex.Message); } return; }
            if (room == DashboardRoom.MeetingRoom) { OpenLatestMeeting(); return; }
            ShowSection("Zelda Manager");
            if (room.HasValue) _zelda.SwitchRoom(room.Value);
        }
        public void OpenSection(string section)
        {
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate();
            // The history shortcut opens the exact conversation of the latest attendance.
            if (section == "Histórico") OpenLatestConversation(); else ShowSection(section);
        }
        public void AppendChat(string label, string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { Post(() => AppendChat(label, text)); return; }
            AddMessage(label, text ?? "");
        }
        private bool _pairingForChat;
        private void OnPairingRequested()
        {
            if (!IsHandleCreated || IsDisposed) return;
            Post(() => { _pairingForChat = true; OpenSection("WhatsApp"); });
        }
        private void OnConversationSaved(FridaConversationEntry entry)
        {
            if (entry == null || IsDisposed) return;
            // The bubbles update at once; the list waits for a pause (meetings save many times in a row).
            Post(() => { if (_conversation != null && entry.conversation_id == _conversation.Id) RenderConversation(); ScheduleTreeRefresh(); });
        }
        private void OnArtifact(FridaArtifactRecord artifact)
        {
            Post(() =>
            {
                if (_workspaceStore != null && (_conversation == null || artifact.ConversationId != _conversation.Id)) return;
                _terminalFiles.Items.Insert(0, artifact);
                if (_terminalFiles.Items.Count > 200) _terminalFiles.Items.RemoveAt(_terminalFiles.Items.Count - 1);
            });
        }
        private async Task OpenArtifact(FridaArtifactRecord artifact)
        {
            if (artifact == null) return;
            var current = string.IsNullOrEmpty(artifact.Id) ? null : FridaIdeRuntime.Artifacts().FirstOrDefault(item => item.Id == artifact.Id);
            artifact = current ?? artifact;
            bool remote = artifact.Destination != "Computador";
            var project = _projectList.FirstOrDefault(item => item.Name == artifact.Project && (remote ? item.SshAlias == artifact.Destination : string.IsNullOrEmpty(item.SshAlias)));
            if (remote && project == null)
            {
                if (string.IsNullOrEmpty(artifact.ProjectRoot)) throw new IOException("Associe o projeto remoto para abrir este item.");
                project = new FridaIdeProject { Name = artifact.Project, SshAlias = artifact.Destination, Root = artifact.ProjectRoot };
            }
            bool directory = remote ? await Task.Run(() => FridaSshWorkspace.IsDirectory(project, artifact.Path)) : Directory.Exists(artifact.Path);
            if (directory)
            {
                if (remote)
                {
                    if (!_projectList.Contains(project)) { _projectList.Add(project); _projects.Items.Add(project); FridaIdeRuntime.SaveProjects(_projectList); }
                    _projects.SelectedItem = project; _requestedDirectory = artifact.Path; _refreshFiles(); ShowSection("Arquivos");
                }
                else Process.Start(new ProcessStartInfo(artifact.Path) { UseShellExecute = true });
            }
            else { await OpenEditor(artifact.Path, project); ShowEditor(); }
        }
        private void OnActivity(string line)
        {
            if (_workspaceStore != null) return;
            if (IsDisposed || !IsHandleCreated) return;
            Post(() => { if (_terminal.TextLength > 250000) _terminal.Clear(); _terminal.AppendText(line + "\r\n"); });
        }
        private void BeginRecording()
        {
            if (_busy || _transcribing) { Progress("Aguarde o atendimento ou descarte a transcrição em andamento."); return; }
            _voice.BeginIdeRecording();
            _recordingVersion++; _recordedAudio = null; _recordingSeconds = 0; _recordingTimer.Start();
            _status.Text = "Gravando · 00:00"; RefreshComposer();
        }
        private void DiscardRecording()
        {
            _recordingVersion++;
            _recordingTimer.Stop(); _voice.FinishIdeRecording(true);
            if (_transcriptionCancellation != null) _transcriptionCancellation.Cancel();
            _transcriptionCancellation = null; _transcribing = false; _recordedAudio = null;
            if (!_disposingIde) { _status.Text = "Gravação descartada"; RefreshComposer(); _draft.Focus(); }
        }
        private async Task FinishRecording()
        {
            _recordingTimer.Stop();
            if (_transcribing || !_voice.IsIdeRecording) return;
            int version = _recordingVersion;
            _recordedAudio = _voice.FinishIdeRecording(false);
            _transcribing = true; _status.Text = "Transcrevendo áudio…"; RefreshComposer();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _transcriptionCancellation = cancellation;
            try
            {
                string transcript;
                using (FridaWorkContext.Enter(cancellation.Token)) transcript = await TranscribeRecording(_recordedAudio);
                if (_disposingIde || version != _recordingVersion || cancellation.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(transcript)) { _status.Text = "Nenhuma fala reconhecida. Grave novamente ou digite a mensagem."; return; }
                string draft = _draft.Text;
                _draft.Text = string.IsNullOrWhiteSpace(draft) ? transcript : draft.TrimEnd() + Environment.NewLine + transcript;
                _status.Text = "Revise a transcrição e envie";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_disposingIde && version == _recordingVersion) _status.Text = ex.Message + " · Tente novamente"; }
            finally
            {
                if (version == _recordingVersion)
                {
                    _transcriptionCancellation = null; _transcribing = false;
                    if (!_disposingIde) { RefreshComposer(); if (_currentSection == "Chat" || _currentSection == "Código") _draft.Focus(); }
                }
                cancellation.Dispose();
            }
        }
        private async Task SendDraft()
        {
            if (_busy || _playingDraft || _transcribing || _voice.IsIdeRecording || !HasDraft) return;
            string request = ComposeRequest(), draft = _draft.Text;
            if (_conversation == null) {
                var created = _workspaceStore.Create(request.Length > 70 ? request.Substring(0,70) : request, _workspaceSection, FridaConversationKind.Conversation);
                _conversation = created; FridaIdeRuntime.ActiveConversationId = created.Id;
            }
            SaveWorkspaceDraft();
            var routed = _workspaceStore.Route(_conversation.Id, request, _workspaceSection, _conversation.Kind == FridaConversationKind.Meeting || FridaTaskRouting.IsExplicitMeetingRequest(request));
            if (routed.Id != _conversation.Id || routed.Section != _workspaceSection) {
                var audio = _recordedAudio; var refs = _references.Controls.Cast<Control>().Select(c => (string)c.Tag).ToArray();
                // The text moves to the linked conversation: empty the composer without saving it back
                // as the origin's draft (SelectConversation saves the current composer first).
                _changingConversation = true;
                try { _draft.Clear(); ClearReferences(); _recordedAudio = null; }
                finally { _changingConversation = false; }
                _workspaceStore.UpdateDraft(_conversation.Id, "", new string[0]); _conversationAudio.Remove(_conversation.Id);
                SelectConversation(routed.Id); _draft.Text = draft; foreach (string path in refs) AddReference(path); _recordedAudio = audio;
            }
            _conversation = routed;
            var origin = _conversation;
            if (origin.Title == "Nova conversa") _workspaceStore.Rename(origin.Id, request.Length > 70 ? request.Substring(0,70) : request);
            // Conversation, project and destination are fixed here; later navigation cannot redirect them.
            var sentReferences = _references.Controls.Cast<Control>().Select(c => (string)c.Tag).ToList();
            var context = new FridaIdeContext { ConversationId = origin.Id, ProjectId = origin.ProjectId, Section = origin.Section, Kind = origin.Kind, Store = _workspaceStore, Project = ConversationProject(origin), Silent = true, Audio = _recordedAudio };
            // The box empties as soon as the message goes (it used to keep the text until the whole reply
            // arrived, which can take minutes). A failed send puts the text, files and audio back.
            var sentAudio = _recordedAudio;
            _changingConversation = true;
            try { _draft.Clear(); ClearReferences(); _recordedAudio = null; }
            finally { _changingConversation = false; }
            try { _workspaceStore.UpdateDraft(origin.Id, "", new string[0]); } catch (Exception ex) { BootLogger.Log("Rascunho enviado não limpo: " + ex.GetType().Name); }
            _conversationAudio.Remove(origin.Id);
            _busyConversationId = origin.Id; _busy = true; RefreshComposer();
            try
            {
                using (_workspaceStore.BeginExecution(origin.Id))
                using (FridaIdeRuntime.Enter(context))
                {
                    _status.Text = "Frida está respondendo…";
                    string reply = await _send(request, context.Project, context.Audio);
                    if (_workspaceStore.MessageCount(origin.Id) == 0 || !_workspaceStore.HasRequest(origin.Id, context.RequestId))
                        _workspaceStore.RecordReport(new FridaConversationEntry { conversation_id = origin.Id, request_id = context.RequestId, request = request, response = reply, started_utc = DateTime.UtcNow.ToString("o"), finished_utc = DateTime.UtcNow.AddTicks(1).ToString("o"), input_channel = context.Audio == null ? "chat" : "chat_audio" });
                }
                // Clear only what was sent: text typed or files added during the reply stay as the draft.
                bool viewingOrigin = !_disposingIde && _conversation != null && _conversation.Id == origin.Id;
                var stored = _workspaceStore.TryGet(origin.Id);
                if (stored != null && !viewingOrigin && (stored.Draft ?? "") == draft && stored.Attachments.SequenceEqual(sentReferences))
                    _workspaceStore.UpdateDraft(origin.Id, "", new string[0]);
                _conversationAudio.Remove(origin.Id);
                if (_disposingIde) return;
                if (viewingOrigin) {
                    _recordedAudio = null; _pairingForChat = false;
                    var current = _references.Controls.Cast<Control>().Select(c => (string)c.Tag).ToList();
                    if (current.SequenceEqual(sentReferences)) ClearReferences();
                    if (_draft.Text == draft) _draft.Clear();
                    SaveWorkspaceDraft();
                    RenderConversation();
                }
                RefreshConversationTree(); _status.Text = "Pronta";
            }
            catch (Exception ex)
            {
                if (!_disposingIde) { _status.Text = "Falha no envio: " + ex.Message; RestoreUnsentDraft(origin, draft, sentReferences, sentAudio); }
            }
            finally { _busy = false; _busyConversationId = null; if (!_disposingIde) RefreshComposer(); }
        }
        // After a failed send the message is not lost: back into the box when that conversation is open
        // and the box is still empty, otherwise kept as that conversation's draft.
        private void RestoreUnsentDraft(FridaConversation origin, string draft, List<string> references, byte[] audio)
        {
            try
            {
                bool viewing = _conversation != null && _conversation.Id == origin.Id;
                if (viewing && _draft.TextLength == 0 && _references.Controls.Count == 0)
                {
                    _draft.Text = draft; foreach (string path in references) AddReference(path);
                    _recordedAudio = audio; SaveWorkspaceDraft();
                    return;
                }
                var stored = _workspaceStore.TryGet(origin.Id);
                if (stored != null && string.IsNullOrEmpty(stored.Draft) && stored.Attachments.Count == 0) _workspaceStore.UpdateDraft(origin.Id, draft, references);
                if (audio != null) _conversationAudio[origin.Id] = audio;
            }
            catch (Exception ex) { BootLogger.Log("Rascunho não restaurado após falha: " + ex.GetType().Name); }
        }
        public void Progress(string text) { Post(() => _status.Text = text); }
        private async Task AddRemoteProject(bool create)
        {
            using (var dialog = new FridaDialog(create ? "Criar projeto na VPS" : "Abrir projeto na VPS", 520, 330))
            {
                var aliases = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Perfil SSH" };
                aliases.Items.AddRange(FridaSshWorkspace.Aliases());
                var name = new TextBox { AccessibleName = "Nome do projeto" };
                var root = new TextBox { AccessibleName = "Diretório absoluto no servidor" };
                // Label above each field, top-down, so nothing competes for the same row.
                foreach (var field in new[] { Tuple.Create("Diretório absoluto (ex.: /srv/app)", (Control)root), Tuple.Create("Nome do projeto (opcional)", (Control)name), Tuple.Create("Perfil SSH", (Control)aliases) })
                {
                    dialog.Body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Px(10) });
                    dialog.Body.Controls.Add(new InputFrame(field.Item2) { Dock = DockStyle.Top });
                    dialog.Body.Controls.Add(new Label { Text = field.Item1, Dock = DockStyle.Top, Height = Px(24), ForeColor = Muted, Font = FridaTheme.Font(9.5f), TextAlign = ContentAlignment.BottomLeft });
                }
                dialog.AddButton("Cancelar", DialogResult.Cancel, PillKind.Secondary);
                dialog.AddButton(create ? "Criar e conectar" : "Conectar", DialogResult.OK, PillKind.Primary);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var project = new FridaIdeProject { Name = name.Text.Trim(), SshAlias = aliases.Text.Trim(), Root = root.Text.Trim().TrimEnd('/') };
                FridaSshWorkspace.ValidateProject(project);
                if (string.IsNullOrWhiteSpace(project.Name)) project.Name = project.Root.Substring(project.Root.LastIndexOf('/') + 1);
                Progress("Conectando ao destino selecionado…");
                if (create) await Task.Run(() => FridaSshWorkspace.CreateProject(project));
                string confirmation = await Task.Run(() => FridaSshWorkspace.Probe(project));
                RegisterWorkspaceProject(project.Name, project.Root, project.SshAlias);
                Progress(confirmation.Replace("\n", " · "));
            }
        }
        private void AddProject(bool create)
        {
            using (var picker = new FolderBrowserDialog())
            {
                picker.Description = create ? "Selecione o diretório pai" : "Selecione a raiz do projeto";
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                string root = picker.SelectedPath;
                if (create)
                {
                    string name = Prompt("Nome do projeto", "NovoProjeto"); if (name == null) return;
                    if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") throw new IOException("Nome de projeto inválido.");
                    root = Path.Combine(root, name); if (Directory.Exists(root)) throw new IOException("A pasta já existe."); Directory.CreateDirectory(root);
                }
                // The selected folder is only registered; deleting the project later never touches it.
                RegisterWorkspaceProject(Path.GetFileName(root.TrimEnd('\\')), root, null);
            }
        }
        private EditorTabStrip _editorStrip;
        private readonly Label _editorStatus = new Label { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Label _editorEmpty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        // Editor tab: compact action bar, own tab strip (dirty dot + close), empty-state guidance and
        // a status footer. Files open from the right panel's Arquivos tab.
        private Control MakeCode()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var bar = Bar();
            bar.Controls.Add(AsyncButton("Novo script", () => OpenEditor(null, SelectedProject), PillKind.Secondary, FridaTheme.GlyphAdd));
            bar.Controls.Add(AsyncButton("Salvar", async () => { await SaveEditor(); }, PillKind.Secondary, FridaTheme.GlyphSave));
            bar.Controls.Add(AsyncButton("Executar", RunEditor, PillKind.Primary, FridaTheme.GlyphPlay));
            bar.Controls.Add(AsyncButton("Comparar", async () => { var tab = _editors.SelectedTab; if (tab != null) await CompareEditor(tab); }, PillKind.Ghost, FridaTheme.GlyphCompare));
            _wrapButton = (PillButton)Button("Quebra de linha", ToggleEditorWordWrap, PillKind.Ghost);
            FridaTheme.Tip(_wrapButton, "Quebrar linhas longas na largura do editor, sem rolagem horizontal (Alt+Z)");
            bar.Controls.Add(_wrapButton);
            FridaTheme.Tip(bar.Controls[bar.Controls.Count - 3], "Salvar (Ctrl+S)"); FridaTheme.Tip(bar.Controls[bar.Controls.Count - 2], "Executar (F5)");
            var search = new TextBox { AccessibleName = "Buscar no arquivo aberto" };
            search.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var editor = ActiveEditor(); if (editor != null) editor.FindText(search.Text); } };
            var searchFrame = new InputFrame(search, "Buscar no arquivo (Enter)", true) { Width = Px(220) };
            bar.AddRight(searchFrame);
            _editorStrip = new EditorTabStrip(this) { Dock = DockStyle.Top };
            _editorStatus.Height = Px(26); _editorStatus.Font = FridaTheme.Font(9); _editorStatus.ForeColor = Muted; _editorStatus.BackColor = Surface; _editorStatus.Padding = new Padding(Px(12), 0, Px(12), 0);
            _editorEmpty.Text = "Nenhum arquivo aberto\n\nAbra um arquivo pelo painel Arquivos (Ctrl+J) ou crie um script em Novo script.\nCtrl+S salva · F5 executa · Ctrl+F busca";
            _editorEmpty.Font = FridaTheme.Font(10.5f); _editorEmpty.ForeColor = Muted; _editorEmpty.BackColor = Canvas;
            var area = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            area.Controls.Add(_editors); area.Controls.Add(_editorEmpty);
            Action refresh = () => { bool any = _editors.TabPages.Count > 0; _editors.Visible = any; _editorEmpty.Visible = !any; _editorStrip.Visible = any; _editorStrip.Invalidate(); UpdateEditorStatus(); };
            _editors.ControlAdded += delegate { refresh(); }; _editors.ControlRemoved += delegate { if (IsHandleCreated) BeginInvoke(refresh); else refresh(); };
            _editors.SelectedIndexChanged += delegate { _editorStrip.Invalidate(); UpdateEditorStatus(); };
            refresh();
            page.Controls.Add(area); page.Controls.Add(_editorStrip); page.Controls.Add(bar); page.Controls.Add(_editorStatus);
            return page;
        }
        private PillButton _wrapButton;
        // Word wrap for every open editor (remembered): long lines fold instead of scrolling sideways.
        private void ToggleEditorWordWrap() { ApplyEditorWordWrap(!_layout.EditorWordWrap); SaveLayout(); }
        private void ApplyEditorWordWrap(bool wrap)
        {
            _layout.EditorWordWrap = wrap;
            foreach (TabPage tab in _editors.TabPages) foreach (var editor in tab.Controls.OfType<FridaCodeEditor>()) { editor.WordWrap = wrap; editor.Invalidate(); foreach (Control margin in tab.Controls) margin.Invalidate(); }
            if (_wrapButton != null) _wrapButton.Kind = wrap ? PillKind.Secondary : PillKind.Ghost;
            UpdateEditorStatus();
        }
        private void UpdateEditorStatus()
        {
            var tab = _editors.SelectedTab; var editor = ActiveEditor();
            if (tab == null || editor == null) { _editorStatus.Text = ""; return; }
            FridaIdeProject project; _editorProjects.TryGetValue(tab, out project);
            string path; _paths.TryGetValue(tab, out path);
            _editorStatus.Text = "Ln " + editor.CaretLine + ", Col " + editor.CaretColumn + "   ·   UTF-8   ·   " + (editor.WordWrap ? "Quebra de linha   ·   " : "") + (project == null ? "Computador" : project.Name + " · " + (string.IsNullOrEmpty(project.SshAlias) ? "Computador" : "VPS " + project.SshAlias)) + (path == null ? "   ·   não salvo" : "   ·   " + path);
        }
        private async Task CloseEditor(TabPage tab)
        {
            if (tab == null) return;
            if (tab.Text.EndsWith(" *"))
            {
                var choice = FridaDialog.Choose(this, "Salvar alterações?", "\"" + tab.Text.TrimEnd(' ', '*') + "\" tem alterações não salvas.", "Salvar", "Descartar");
                if (choice == DialogResult.Cancel) return;
                if (choice == DialogResult.Yes && !await SaveEditor(tab)) return;
            }
            _paths.Remove(tab); _hashes.Remove(tab); _editorProjects.Remove(tab); _editorConversations.Remove(tab);
            _editors.TabPages.Remove(tab); tab.Dispose();
        }
        // Rounded tabs with a dirty dot and close button; selection drives the headless TabControl.
        private sealed class EditorTabStrip : Control
        {
            private readonly FridaIdeForm _owner;
            private int _hover = -1; private bool _hoverClose;
            internal EditorTabStrip(FridaIdeForm owner)
            {
                _owner = owner; Height = FridaTheme.Px(40); BackColor = FridaTheme.Surface; TabStop = false; AccessibleRole = AccessibleRole.PageTabList; AccessibleName = "Arquivos abertos";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }
            private List<Rectangle> Tabs()
            {
                var result = new List<Rectangle>(); var pages = _owner._editors.TabPages; if (pages.Count == 0) return result;
                int pad = FridaTheme.Px(6), gap = FridaTheme.Px(4), x = pad;
                var widths = pages.Cast<TabPage>().Select(p => Math.Min(FridaTheme.Px(220), TextRenderer.MeasureText(p.Text.TrimEnd(' ', '*'), FridaTheme.Font(9.5f)).Width + FridaTheme.Px(56))).ToList();
                int total = widths.Sum() + gap * (widths.Count - 1);
                double scale = total > Width - pad * 2 ? (double)(Width - pad * 2 - gap * (widths.Count - 1)) / widths.Sum() : 1;
                foreach (int w in widths) { int width = Math.Max(FridaTheme.Px(90), (int)(w * scale)); result.Add(new Rectangle(x, pad, width, Height - pad * 2)); x += width + gap; }
                return result;
            }
            private Rectangle CloseBox(Rectangle tab) { int s = FridaTheme.Px(22); return new Rectangle(tab.Right - s - FridaTheme.Px(6), tab.Y + (tab.Height - s) / 2, s, s); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(FridaTheme.Surface); var tabs = Tabs(); var pages = _owner._editors.TabPages;
                for (int i = 0; i < tabs.Count && i < pages.Count; i++)
                {
                    var r = tabs[i]; bool selected = pages[i] == _owner._editors.SelectedTab, dirty = pages[i].Text.EndsWith(" *");
                    if (selected) FridaTheme.FillRound(g, r, FridaTheme.Px(8), FridaTheme.Canvas, FridaTheme.Edge);
                    else if (i == _hover) FridaTheme.FillRound(g, r, FridaTheme.Px(8), FridaTheme.Hover);
                    var text = new Rectangle(r.X + FridaTheme.Px(12), r.Y, r.Width - FridaTheme.Px(44), r.Height);
                    FridaTheme.Text(g, pages[i].Text.TrimEnd(' ', '*'), selected ? FridaTheme.Strong(9.5f) : FridaTheme.Font(9.5f), text, selected ? FridaTheme.Ink : FridaTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    var close = CloseBox(r);
                    if (i == _hover && _hoverClose) FridaTheme.FillRound(g, close, FridaTheme.Px(6), FridaTheme.Bubble);
                    if (dirty && !(i == _hover)) { int d = FridaTheme.Px(8); using (var b = new SolidBrush(FridaTheme.Highlight)) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.FillEllipse(b, close.X + (close.Width - d) / 2, close.Y + (close.Height - d) / 2, d, d); } }
                    else FridaTheme.Text(g, FridaTheme.GlyphClose, FridaTheme.Icons(8), close, FridaTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); var tabs = Tabs(); int hover = tabs.FindIndex(r => r.Contains(e.Location));
                bool close = hover >= 0 && CloseBox(tabs[hover]).Contains(e.Location);
                // The tooltip changes only when another tab is under the mouse (resetting it on every
                // move restarted the tooltip and made it flicker).
                if (hover != _hover) FridaTheme.Tip(this, hover >= 0 ? _owner._editors.TabPages[hover].ToolTipText : "");
                if (hover != _hover || close != _hoverClose) { _hover = hover; _hoverClose = close; Invalidate(); }
                Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
            protected override async void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e); var tabs = Tabs(); int index = tabs.FindIndex(r => r.Contains(e.Location)); if (index < 0) return;
                var page = _owner._editors.TabPages[index];
                if (e.Button == MouseButtons.Middle || CloseBox(tabs[index]).Contains(e.Location)) { try { await _owner.CloseEditor(page); } catch (Exception ex) { _owner.Progress(ex.Message); } }
                else _owner._editors.SelectedTab = page;
                Invalidate();
            }
        }
        private FridaCodeEditor ActiveEditor()
        {
            return _editors.SelectedTab == null ? null : _editors.SelectedTab.Controls.OfType<FridaCodeEditor>().FirstOrDefault();
        }
        private async Task OpenEditor(string path, FridaIdeProject project)
        {
            foreach (var pair in _paths)
                if (path != null && pair.Value == path && _editorProjects[pair.Key] == project) { _editors.SelectedTab = pair.Key; return; }
            bool remote = project != null && !string.IsNullOrEmpty(project.SshAlias);
            string content = "", hash = null;
            if (path != null)
            {
                if (remote)
                {
                    hash = await Task.Run(() => FridaSshWorkspace.Hash(project, path));
                    content = await Task.Run(() => FridaSshWorkspace.Read(project, path));
                    if (hash != await Task.Run(() => FridaSshWorkspace.Hash(project, path))) throw new IOException("O arquivo remoto mudou durante a leitura.");
                }
                else
                {
                    if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("Arquivo excede 2 MB para edição.");
                    hash = FridaVerifiedTools.FileSha256(path); content = File.ReadAllText(path);
                    if (hash != FridaVerifiedTools.FileSha256(path)) throw new IOException("O arquivo mudou durante a leitura.");
                }
            }
            var tab = new TabPage(path == null ? "Novo script" : Path.GetFileName(path));
            var editor = new FridaCodeEditor { Text = content, WordWrap = _layout.EditorWordWrap };
            tab.Controls.Add(editor); tab.Controls.Add(editor.CreateLineNumbers());
            _editors.TabPages.Add(tab); _editors.SelectedTab = tab;
            _paths[tab] = path; _hashes[tab] = hash; _editorProjects[tab] = project; _editorConversations[tab] = SelectedConversationId;
            tab.ToolTipText = (path ?? "Novo script") + "  ·  " + (project == null ? "Computador" : project.ToString() + " · " + project.Root);
            tab.BackColor = Canvas;
            editor.TextChanged += delegate { if (!tab.Text.EndsWith(" *")) { tab.Text += " *"; _editorStrip.Invalidate(); } };
            editor.SelectionChanged += delegate { if (_editors.SelectedTab == tab) UpdateEditorStatus(); };
            _editorStrip.Invalidate(); UpdateEditorStatus();
        }
        private async Task CompareEditor(TabPage tab, string selectedPath = null)
        {
            string path = selectedPath ?? _paths[tab]; if (path == null) return;
            var project = _editorProjects[tab];
            string disk;
            if (project != null && !string.IsNullOrEmpty(project.SshAlias))
                disk = await Task.Run(() => FridaSshWorkspace.Hash(project, path) == null ? "[Arquivo removido do servidor]" : FridaSshWorkspace.Read(project, path));
            else disk = File.Exists(path) ? File.ReadAllText(path) : "[Arquivo removido do disco]";
            var editor = tab.Controls.OfType<FridaCodeEditor>().Single();
            using (var dialog = new FridaDialog("Comparar · " + Path.GetFileName(path), 1000, 620))
            {
                dialog.FormBorderStyle = FormBorderStyle.Sizable; dialog.MinimumSize = new Size(Px(640), Px(400));
                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = Px(8), BackColor = Surface };
                Func<string, string, Control> pane = (title, text) =>
                {
                    var host = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
                    var area = TerminalArea(); area.Text = text; area.WordWrap = false;
                    host.Controls.Add(area);
                    host.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = Px(30), Font = FridaTheme.Strong(9.5f), ForeColor = Muted, Padding = new Padding(Px(8), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft });
                    return host;
                };
                split.Panel1.Controls.Add(pane("Arquivo salvo", disk)); split.Panel2.Controls.Add(pane("Seu rascunho", editor.Text));
                dialog.Body.Controls.Add(split);
                dialog.Shown += delegate { split.SplitterDistance = split.Width / 2; };
                dialog.AddButton("Usar arquivo salvo", DialogResult.Yes, PillKind.Secondary);
                dialog.AddButton("Manter rascunho", DialogResult.Cancel, PillKind.Primary);
                if (dialog.ShowDialog(this) == DialogResult.Yes) editor.Text = disk;
            }
        }
        private async Task<bool> SaveEditor(TabPage tab = null)
        {
            tab = tab ?? _editors.SelectedTab; if (tab == null) return false;
            var editor = tab.Controls.OfType<FridaCodeEditor>().Single();
            var project = _editorProjects[tab];
            bool remote = project != null && !string.IsNullOrEmpty(project.SshAlias);
            string path = _paths[tab];
            if (path == null)
            {
                if (remote)
                {
                    string name = Prompt("Caminho do script dentro do projeto remoto", "script.py");
                    if (name == null) return false;
                    using (EnterUiContext(project, EditorConversation(tab))) path = FridaSshWorkspace.ResolvePath(name);
                }
                else
                {
                    using (var picker = new SaveFileDialog { InitialDirectory = project == null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : project.Root, Filter = "Scripts e texto|*.*" })
                    { if (picker.ShowDialog(this) != DialogResult.OK) return false; path = picker.FileName; }
                }
            }
            string current = remote ? await Task.Run(() => FridaSshWorkspace.Hash(project, path)) : File.Exists(path) ? FridaVerifiedTools.FileSha256(path) : null;
            if (current != _hashes[tab])
            {
                await CompareEditor(tab, path);
                if (!FridaDialog.Confirm(this, "Salvar sobre a versão comparada?", "O arquivo mudou depois que foi aberto. Salvar o seu rascunho substitui a versão que você acabou de comparar.", "Salvar rascunho")) return false;
            }
            string content = editor.Text;
            // The save is attributed to the conversation that opened the file, not to later navigation.
            using (EnterUiContext(project, EditorConversation(tab)))
            {
                if (remote) await Task.Run(() => FridaSshWorkspace.Write(project, path, content, current));
                else
                {
                    if (current != (File.Exists(path) ? FridaVerifiedTools.FileSha256(path) : null)) throw new IOException("O arquivo mudou novamente. Compare antes de salvar.");
                    var receipt = FridaVerifiedTools.WriteFile(path, content, false);
                    if (receipt.Status != FridaResultStatus.Verified) throw new IOException(receipt.Summary);
                }
            }
            _paths[tab] = path;
            _hashes[tab] = remote ? FridaVerifiedTools.Sha256(new UTF8Encoding(false).GetBytes(content)) : FridaVerifiedTools.FileSha256(path);
            tab.Text = Path.GetFileName(path) + (editor.Text == content ? "" : " *");
            tab.ToolTipText = path + "  ·  " + (project == null ? "Computador" : project.ToString() + " · " + project.Root);
            _editorStrip.Invalidate(); UpdateEditorStatus(); return true;
        }
        private async Task RunEditor()
        {
            var tab = _editors.SelectedTab;
            if (tab == null || !await SaveEditor(tab)) return; if (tab == null || _paths[tab] == null || tab.Text.EndsWith(" *")) return;
            var project = _editorProjects[tab];
            bool remote = project != null && !string.IsNullOrEmpty(project.SshAlias);
            string path = _paths[tab], extension = Path.GetExtension(path).ToLowerInvariant();
            string quoted = remote ? FridaSshWorkspace.ShellQuote(path) : "'" + path.Replace("'", "''") + "'";
            string command = extension == ".ps1" ? (remote ? "pwsh -File " : "& ") + quoted : extension == ".py" ? (remote ? "python3 -u " : "python -u ") + quoted : extension == ".js" ? "node " + quoted : extension == ".sh" && remote ? "sh " + quoted : null;
            if (command == null) throw new IOException("Escolha um script PowerShell, Python, JavaScript ou shell compatível com o destino.");
            if (_executionCancellation != null) throw new InvalidOperationException("Já existe uma execução no editor.");
            // Prefer the conversation that opened the script; otherwise one in the script's project.
            string owner = EditorConversation(tab);
            var ownerConversation = owner == null ? null : _workspaceStore.TryGet(owner);
            string projectId = project == null ? null : project.Id ?? _workspaceStore.RegisterProject(project.Name, project.Root, string.IsNullOrEmpty(project.SshAlias) ? null : project.SshAlias).Id;
            if (ownerConversation == null || ownerConversation.ProjectId != projectId)
                owner = _workspaceStore.Create("Executar " + Path.GetFileName(path), FridaWorkspaceSection.Code, FridaConversationKind.Conversation, projectId).Id;
            _editorConversations[tab] = owner;
            if (SelectedConversationId != owner) SelectConversation(owner);
            string executionConversationId = owner; _editorExecutionConversationId = executionConversationId;
            ShowSection("Terminal");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _executionCancellation = cancellation;
            try
            {
                using (_workspaceStore.BeginExecution(executionConversationId))
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Project = project, ProjectId = projectId, ConversationId = executionConversationId, Store = _workspaceStore, Silent = true }))
                using (FridaWorkContext.Enter(cancellation.Token))
                    await Task.Run(() => FridaVerifiedTools.RunPowerShell(command, 120000));
            }
            finally { _executionCancellation = null; _editorExecutionConversationId = null; cancellation.Dispose(); }
        }
        // WhatsApp: a centered status card (state, QR in a rounded frame and pill actions).
        private Control MakeWhatsApp()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var card = new RoundedPanel { Fill = Surface, Stroke = FridaTheme.Divider, Radius = 16, BackColor = Canvas };
            var title = new Label { Text = "WhatsApp da Frida", Font = FridaTheme.Strong(14), ForeColor = Ink, BackColor = Surface, TextAlign = ContentAlignment.MiddleCenter };
            var state = new Label { Text = FridaTheme.GlyphPhone, Font = FridaTheme.Icons(22), ForeColor = Muted, BackColor = Surface, TextAlign = ContentAlignment.MiddleCenter };
            var detail = new Label { Text = "Verifique a sessão do leitor da Frida.", Font = FridaTheme.Font(10.5f), ForeColor = Muted, BackColor = Surface, TextAlign = ContentAlignment.TopCenter, AccessibleRole = AccessibleRole.StatusBar };
            var qrFrame = new RoundedPanel { Fill = Color.White, Stroke = FridaTheme.Edge, Radius = 12, BackColor = Surface, Visible = false };
            var qr = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, AccessibleName = "QR do WhatsApp" };
            qrFrame.Controls.Add(qr);
            var bar = new ActionBar(true) { Dock = DockStyle.None, BackColor = Surface, Centered = true };
            card.Controls.Add(title); card.Controls.Add(state); card.Controls.Add(detail); card.Controls.Add(qrFrame); card.Controls.Add(bar);
            page.Controls.Add(card);
            Action<string, Color> setState = (text, tint) => { detail.Text = text; state.ForeColor = tint; };
            Action layout = () =>
            {
                int width = Math.Min(Px(560), Math.Max(Px(320), page.ClientSize.Width - Px(48)));
                int qrSize = qrFrame.Visible ? Math.Min(Px(280), Math.Max(Px(160), page.ClientSize.Height - Px(320))) : 0;
                int height = Px(64) + Px(48) + Px(56) + (qrFrame.Visible ? qrSize + Px(16) : 0) + Px(64);
                card.SetBounds((page.ClientSize.Width - width) / 2, Math.Max(Px(16), (page.ClientSize.Height - height) / 2), width, height);
                int y = Px(20);
                state.SetBounds(0, y, width, Px(44)); y += Px(44);
                title.SetBounds(Px(16), y, width - Px(32), Px(32)); y += Px(36);
                detail.SetBounds(Px(24), y, width - Px(48), Px(48)); y += Px(52);
                if (qrFrame.Visible) { qrFrame.SetBounds((width - qrSize) / 2, y, qrSize, qrSize); qr.SetBounds(Px(10), Px(10), qrSize - Px(20), qrSize - Px(20)); y += qrSize + Px(16); }
                bar.SetBounds(Px(12), y, width - Px(24), Px(48));
            };
            page.Resize += delegate { layout(); };
            bool running = false;
            CancellationTokenSource cancellation = null;
            Action<string> receiveQr = png => Post(() =>
            {
                if (!running && !_pairingForChat) return;
                try
                {
                    using (var memory = new MemoryStream(Convert.FromBase64String(png)))
                    using (var decoded = Image.FromStream(memory))
                    {
                        var previous = qr.Image; qr.Image = new Bitmap(decoded);
                        if (previous != null) previous.Dispose();
                    }
                    qrFrame.Visible = true; layout();
                    setState("No celular, abra Aparelhos conectados e escaneie este QR.", Highlight);
                }
                catch (Exception ex) { setState("QR indisponível. Tente conectar novamente. " + ex.GetType().Name, Coral); }
            });
            Action<string> finished = status => Post(() =>
            {
                _pairingForChat = false;
                if (qr.Image != null) { qr.Image.Dispose(); qr.Image = null; }
                qrFrame.Visible = false; layout();
                setState(DateTime.Now.ToString("HH:mm") + " · " + status, Muted);
            });
            FridaWhatsAppSource.QrChanged += receiveQr;
            FridaIdeRuntime.PairingFinished += finished;
            _cleanup.Add(() =>
            {
                FridaWhatsAppSource.QrChanged -= receiveQr;
                FridaIdeRuntime.PairingFinished -= finished;
                if (cancellation != null) cancellation.Cancel();
                if (qr.Image != null) { qr.Image.Dispose(); qr.Image = null; }
            });
            Action<bool> run = async pair =>
            {
                if (running) return;
                running = true; cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                setState(pair ? "Preparando QR…" : "Verificando sessão…", Accent);
                try
                {
                    var token = cancellation.Token;
                    var result = await Task.Run(() => { using (FridaWorkContext.Enter(token)) return FridaWhatsAppSource.ObserveConnection(pair); });
                    setState(DateTime.Now.ToString("HH:mm") + " · " + result.Summary, FridaTheme.Success);
                }
                catch (Exception ex) { setState(ex.Message, Coral); }
                finally
                {
                    running = false; cancellation.Dispose(); cancellation = null;
                    if (qr.Image != null) { qr.Image.Dispose(); qr.Image = null; }
                    qrFrame.Visible = false; layout();
                }
            };
            bar.Controls.Add(Button("Verificar conexão", () => run(false), PillKind.Secondary, FridaTheme.GlyphRefresh));
            bar.Controls.Add(Button("Conectar com QR", () => run(true), PillKind.Primary, FridaTheme.GlyphPhone));
            bar.Controls.Add(Button("Cancelar", () => { if (cancellation != null) cancellation.Cancel(); }, PillKind.Ghost));
            return page;
        }
        // Files tool: segmented Navegar / Usados pela Frida over a headless TabControl. The
        // TabControl stays the "Arquivos" page so existing navigation keeps working.
        private Control MakeFiles()
        {
            var container = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var tabs = new HeadlessTabControl { Dock = DockStyle.Fill };
            var browserTab = new TabPage("Navegar") { BackColor = Canvas }; browserTab.Controls.Add(container);
            var recentTab = new TabPage("Usados pela Frida") { BackColor = Canvas }; recentTab.Controls.Add(MakeArtifacts());
            tabs.TabPages.Add(browserTab); tabs.TabPages.Add(recentTab);
            Action select = () =>
            {
                foreach (Control old in container.Controls.Cast<Control>().ToArray()) old.Dispose();
                container.Controls.Clear();
                var project = SelectedProject;
                Control browser = project != null && !string.IsNullOrEmpty(project.SshAlias) ? MakeRemoteFiles(project, _requestedDirectory) : MakeLocalFiles();
                container.Controls.Add(browser); Theme(browser);
            };
            _refreshFiles = select;
            _projects.SelectedIndexChanged += delegate { _requestedDirectory = null; select(); };
            select(); return tabs;
        }
        // Path row: up button + current folder (ellipsis keeps the end of long paths visible).
        private Panel PathRow(Label location, Action up)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = Px(40), BackColor = Canvas, Padding = new Padding(Px(8), Px(4), Px(8), Px(4)) };
            var upButton = new PillButton("", PillKind.Icon, FridaTheme.GlyphUp, true) { AccessibleName = "Subir um nível", Dock = DockStyle.Left };
            FridaTheme.Tip(upButton, "Subir um nível");
            upButton.Click += delegate { try { up(); } catch (Exception ex) { Progress(ex.Message); } };
            location.Dock = DockStyle.Fill; location.AutoEllipsis = true; location.TextAlign = ContentAlignment.MiddleLeft; location.Font = FridaTheme.Font(9.5f);
            location.ForeColor = Muted; location.BackColor = Canvas; location.Padding = new Padding(Px(6), 0, 0, 0);
            location.TextChanged += delegate { FridaTheme.Tip(location, location.Text); };
            row.Controls.Add(location); row.Controls.Add(upButton);
            return row;
        }
        private Control MakeArtifacts()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas }; var bar = Bar();
            var list = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Arquivos e pastas usados pela Frida" };
            FridaTheme.StyleList(list, item => { var a = item as FridaArtifactRecord; return a == null ? null : new[] { a.Favorite ? FridaTheme.GlyphStar : a.Operation == "mkdir" || a.Operation == "list" || a.Operation == "inspect" ? FridaTheme.GlyphFolder : FridaTheme.GlyphDocument, Path.GetFileName(a.Path.TrimEnd('\\', '/')), a.Destination + " · " + a.Path }; }, 44);
            var search = new TextBox { AccessibleName = "Buscar arquivo ou pasta usados" };
            var searchFrame = new InputFrame(search, "Buscar arquivos usados", true) { Dock = DockStyle.Top };
            var searchRow = new Panel { Dock = DockStyle.Top, Height = Px(48), BackColor = Canvas, Padding = new Padding(Px(8), Px(6), Px(8), Px(6)) };
            searchRow.Controls.Add(searchFrame);
            bool favoritesOnly = false;
            Action refresh = () =>
            {
                list.Items.Clear();
                foreach (var item in FridaIdeRuntime.Artifacts().Where(item => (!favoritesOnly || item.Favorite) && item.Path.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(item => item.Favorite).ThenByDescending(item => item.LastUsedUtc)) list.Items.Add(item);
            };
            PillButton favorites = null;
            favorites = (PillButton)Button("Favoritos", () => { favoritesOnly = !favoritesOnly; favorites.Kind = favoritesOnly ? PillKind.Primary : PillKind.Secondary; refresh(); }, PillKind.Secondary, FridaTheme.GlyphStar);
            bar.Controls.Add(AsyncButton("Abrir", async () =>
            {
                var item = list.SelectedItem as FridaArtifactRecord; if (item == null) return;
                if (item.Destination == "Computador")
                {
                    if (Directory.Exists(item.Path)) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                    else { await OpenEditor(item.Path, _projectList.FirstOrDefault(project => project.Name == item.Project && string.IsNullOrEmpty(project.SshAlias))); ShowEditor(); }
                }
                else
                {
                    var project = _projectList.FirstOrDefault(entry => entry.SshAlias == item.Destination && entry.Name == item.Project);
                    if (project == null) throw new IOException("Associe o projeto remoto para abrir este item.");
                    bool directory = await Task.Run(() => FridaSshWorkspace.IsDirectory(project, item.Path));
                    if (directory)
                    {
                        _projects.SelectedItem = project;
                        _requestedDirectory = item.Path; _refreshFiles(); ShowSection("Arquivos");
                    }
                    else { await OpenEditor(item.Path, project); ShowEditor(); }
                }
            }, PillKind.Primary, FridaTheme.GlyphDocument));
            bar.Controls.Add(favorites);
            bar.Controls.Add(Button("Favoritar", () => { var item = list.SelectedItem as FridaArtifactRecord; if (item != null) { FridaIdeRuntime.FavoriteArtifact(item); refresh(); } }));
            bar.Controls.Add(Button("Copiar caminho", () => { var item = list.SelectedItem as FridaArtifactRecord; if (item != null) Clipboard.SetText(item.Path); }));
            bar.Controls.Add(Button("Revelar no Explorer", () => { var item = list.SelectedItem as FridaArtifactRecord; if (item != null && item.Destination == "Computador") Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true }); }));
            bar.Controls.Add(Button("Atualizar", refresh, PillKind.Ghost, FridaTheme.GlyphRefresh));
            list.DoubleClick += delegate { ((PillButton)bar.Controls.Cast<Control>().First(c => c.Text == "Abrir")).Invoke(); };
            search.TextChanged += delegate { refresh(); };
            page.VisibleChanged += delegate { if (page.Visible) refresh(); };
            page.Controls.Add(list); page.Controls.Add(bar); page.Controls.Add(searchRow); return page;
        }
        private sealed class RemoteFile
        {
            public string Path;
            public bool Directory;
            public override string ToString() { return Path.Substring(Path.LastIndexOf('/') + 1); }
        }
        private Control MakeRemoteFiles(FridaIdeProject project, string initialDirectory = null)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var bar = Bar(); var list = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Arquivos do diretório remoto" };
            FridaTheme.StyleList(list, item => { var f = item as RemoteFile; return f == null ? null : new[] { f.Directory ? FridaTheme.GlyphFolder : FridaTheme.GlyphDocument, f.ToString() }; });
            var location = new Label { AccessibleName = "Diretório remoto" };
            string current = initialDirectory ?? project.Root;
            Func<Task> refresh = async () =>
            {
                string folder = current;
                string[] entries = await Task.Run(() => FridaSshWorkspace.List(project, folder));
                if (list.IsDisposed) return;
                list.Items.Clear(); location.Text = project.SshAlias + "  ›  " + folder;
                foreach (string entry in entries.OrderBy(e => e.Length > 0 && e[0] == 'd' ? 0 : 1).ThenBy(e => e, StringComparer.OrdinalIgnoreCase))
                    if (entry.Length >= 3) list.Items.Add(new RemoteFile { Path = entry.Substring(2), Directory = entry[0] == 'd' });
            };
            Func<Task> open = async () =>
            {
                var item = list.SelectedItem as RemoteFile; if (item == null) return;
                if (item.Directory) { current = item.Path; await refresh(); }
                else { await OpenEditor(item.Path, project); ShowEditor(); }
            };
            list.DoubleClick += async delegate { try { await open(); } catch (Exception ex) { Progress(ex.Message); } };
            list.KeyDown += async (sender, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; try { await open(); } catch (Exception ex) { Progress(ex.Message); } } };
            var pathRow = PathRow(location, () => { if (current != project.Root) { current = current.Substring(0, current.LastIndexOf('/')); if (current.Length == 0) current = "/"; var _ = refresh(); } });
            bar.Controls.Add(AsyncButton("Abrir", open, PillKind.Primary, FridaTheme.GlyphDocument));
            bar.Controls.Add(AsyncButton("Nova pasta", async () =>
            {
                string name = Prompt("Nome da pasta remota", "Nova pasta"); if (name == null) return; ValidateName(name);
                using (EnterUiContext(project))
                {
                    var result = await Task.Run(() => FridaSshWorkspace.ExecuteTool("manage_files", new Dictionary<string, object> { { "operation", "mkdir" }, { "path", current.TrimEnd('/') + "/" + name } }));
                    if (result.Status != FridaResultStatus.Verified) throw new IOException(result.Summary);
                }
                await refresh();
            }, PillKind.Secondary, FridaTheme.GlyphNewFolder));
            bar.Controls.Add(Button("Copiar caminho", () => { var item = list.SelectedItem as RemoteFile; if (item != null) Clipboard.SetText(item.Path); }));
            bar.Controls.Add(AsyncButton("Renomear", async () =>
            {
                var item = list.SelectedItem as RemoteFile; if (item == null) return;
                string name = Prompt("Novo nome remoto", item.Path.Substring(item.Path.LastIndexOf('/') + 1)); if (name == null) return; ValidateName(name);
                string destination = item.Path.Substring(0, item.Path.LastIndexOf('/') + 1) + name;
                using (EnterUiContext(project)) await Task.Run(() => FridaSshWorkspace.Move(project, item.Path, destination));
                RelocateEditors(project, item.Path, destination, '/'); await refresh();
            }));
            bar.Controls.Add(AsyncButton("Mover", async () =>
            {
                var item = list.SelectedItem as RemoteFile; if (item == null) return;
                string directory = Prompt("Diretório de destino no servidor", current); if (directory == null) return;
                if (!directory.StartsWith("/")) throw new IOException("Informe um caminho absoluto remoto.");
                string destination = directory.TrimEnd('/') + "/" + item.Path.Substring(item.Path.LastIndexOf('/') + 1);
                using (EnterUiContext(project)) await Task.Run(() => FridaSshWorkspace.Move(project, item.Path, destination));
                RelocateEditors(project, item.Path, destination, '/'); await refresh();
            }));
            bar.Controls.Add(AsyncButton("Mover para recuperação", async () =>
            {
                var item = list.SelectedItem as RemoteFile; if (item == null) return;
                if (!FridaDialog.Confirm(this, "Mover para recuperação?", "\"" + item.Path + "\" vai para a pasta de recuperação do projeto no servidor e pode ser restaurado depois.", "Mover", true)) return;
                using (EnterUiContext(project)) await Task.Run(() => FridaSshWorkspace.MoveToRecovery(project, item.Path));
                await refresh();
            }, PillKind.Danger));
            bar.Controls.Add(AsyncButton("Atualizar", refresh, PillKind.Ghost, FridaTheme.GlyphRefresh));
            page.Controls.Add(list); page.Controls.Add(pathRow); page.Controls.Add(bar);
            page.VisibleChanged += async delegate { if (page.Visible) { try { await refresh(); } catch (Exception ex) { Progress(ex.Message); } } };
            return page;
        }
        private void RelocateEditors(FridaIdeProject project, string source, string destination, char separator)
        {
            FridaIdeRuntime.RelocateArtifacts(project.SshAlias, source, destination, separator);
            foreach (var tab in _paths.Keys.ToList())
            {
                string path = _paths[tab];
                if (_editorProjects[tab] != project || path == null) continue;
                if (path == source || path.StartsWith(source + separator, StringComparison.Ordinal))
                {
                    _paths[tab] = destination + path.Substring(source.Length);
                    tab.Text = Path.GetFileName(_paths[tab]) + (tab.Text.EndsWith(" *") ? " *" : "");
                }
            }
            if (_editorStrip != null) _editorStrip.Invalidate();
        }
        private Control MakeLocalFiles()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var bar = Bar();
            var list = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Arquivos do diretório" };
            FridaTheme.StyleList(list, item => { string p = item as string; return p == null ? null : new[] { Directory.Exists(p) ? FridaTheme.GlyphFolder : FridaTheme.GlyphDocument, Path.GetFileName(p) }; });
            var location = new Label { AccessibleName = "Diretório atual" };
            string current = null;
            Func<string> root = () => SelectedProject != null && !string.IsNullOrWhiteSpace(SelectedProject.Root) ? SelectedProject.Root : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Action refresh = () =>
            {
                list.Items.Clear();
                string top = root();
                if (current == null || !current.StartsWith(top.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) current = top;
                location.Text = current;
                try
                {
                    foreach (string path in Directory.GetDirectories(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) list.Items.Add(path);
                    foreach (string path in Directory.GetFiles(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) list.Items.Add(path);
                }
                catch (IOException ex) { Progress("Diretório indisponível · " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Progress("Não foi possível ler o diretório · " + ex.Message); }
            };
            Action open = () =>
            {
                if (list.SelectedItem == null) return;
                string path = list.SelectedItem.ToString();
                if (Directory.Exists(path)) { current = path; refresh(); }
                else { OpenEditor(path, SelectedProject).ContinueWith(task => { if (task.IsFaulted) Progress(task.Exception.GetBaseException().Message); }); ShowEditor(); }
            };
            list.DoubleClick += delegate { open(); };
            list.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; open(); } };
            var pathRow = PathRow(location, () =>
            {
                if (current == null || string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
                current = Path.GetDirectoryName(current); refresh();
            });
            bar.Controls.Add(Button("Abrir", open, PillKind.Primary, FridaTheme.GlyphDocument));
            bar.Controls.Add(Button("Nova pasta", () =>
            {
                if (current == null) { refresh(); if (current == null) return; }
                string name = Prompt("Nome da pasta", "Nova pasta");
                if (name == null) return;
                ValidateName(name);
                string created = Path.Combine(current, name); Directory.CreateDirectory(created);
                using (EnterUiContext(SelectedProject)) { FridaIdeRuntime.RecordArtifact(created, "mkdir"); FridaIdeRuntime.Publish("Pasta criada · " + created); }
                refresh();
            }, PillKind.Secondary, FridaTheme.GlyphNewFolder));
            bar.Controls.Add(Button("Abrir no Windows", () => { if (list.SelectedItem != null) Process.Start(new ProcessStartInfo(list.SelectedItem.ToString()) { UseShellExecute = true }); }));
            bar.Controls.Add(Button("Copiar caminho", () => { if (list.SelectedItem != null) Clipboard.SetText(list.SelectedItem.ToString()); }));
            bar.Controls.Add(Button("Renomear", () =>
            {
                if (list.SelectedItem == null) return;
                string source = list.SelectedItem.ToString();
                string name = Prompt("Novo nome", Path.GetFileName(source));
                if (name == null) return;
                ValidateName(name);
                MoveEntry(source, Path.Combine(Path.GetDirectoryName(source), name)); refresh();
            }));
            bar.Controls.Add(Button("Mover", () =>
            {
                if (list.SelectedItem == null) return;
                using (var picker = new FolderBrowserDialog())
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    string source = list.SelectedItem.ToString();
                    MoveEntry(source, Path.Combine(picker.SelectedPath, Path.GetFileName(source))); refresh();
                }
            }));
            bar.Controls.Add(Button("Enviar à Lixeira do Windows", () =>
            {
                if (list.SelectedItem == null) return;
                string path = Path.GetFullPath(list.SelectedItem.ToString());
                if (!FridaDialog.Confirm(this, "Enviar à Lixeira?", "\"" + path + "\" vai para a Lixeira do Windows.", "Enviar à Lixeira", true)) return;
                if (Directory.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                using (EnterUiContext(SelectedProject)) FridaIdeRuntime.Publish("Enviado à Lixeira do Windows · " + path);
                refresh();
            }, PillKind.Danger));
            bar.Controls.Add(Button("Atualizar", refresh, PillKind.Ghost, FridaTheme.GlyphRefresh));
            page.Controls.Add(list); page.Controls.Add(pathRow); page.Controls.Add(bar);
            page.VisibleChanged += delegate { if (page.Visible) refresh(); };
            refresh(); return page;
        }
        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") throw new IOException("Nome inválido.");
        }
        private void MoveEntry(string source, string destination)
        {
            source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("O destino já existe.");
            if (Directory.Exists(source) && destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Escolha uma pasta fora da própria origem.");
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
            foreach (var tab in _paths.Keys.ToList())
            {
                string path = _paths[tab];
                if (path == null) continue;
                if (path.Equals(source, StringComparison.OrdinalIgnoreCase) || path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _paths[tab] = destination + path.Substring(source.Length);
                    tab.Text = Path.GetFileName(_paths[tab]) + (tab.Text.EndsWith(" *") ? " *" : "");
                }
            }
            if (_editorStrip != null) _editorStrip.Invalidate();
            FridaIdeRuntime.RelocateArtifacts("Computador", source, destination, Path.DirectorySeparatorChar);
            using (EnterUiContext(SelectedProject)) FridaIdeRuntime.Publish("Movido · " + source + " → " + destination);
        }
        private static string Prompt(string title, string initial) { return FridaDialog.Prompt(Form.ActiveForm, title, initial); }
    }
}
