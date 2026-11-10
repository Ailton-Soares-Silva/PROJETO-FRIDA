using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FridaAgent
{
    // Per-installation arrangement of the workspace panels; geometry is stored as ratios so it
    // survives window, monitor and DPI changes.
    public sealed class FridaIdeLayout
    {
        public bool ToolsVisible;
        public bool SidebarVisible = true;
        public double ToolsRatio = 0.6, TerminalRatio = 0.5;
        public string ToolTab = "Arquivos";
        // Sidebar width in 100%-scale pixels, so the same arrangement holds at any DPI.
        public int SidebarWidth = DefaultSidebarWidth;
        public bool EditorWordWrap;
        public const int DefaultSidebarWidth = 280, MinSidebarWidth = 200, MaxSidebarWidth = 560;
    }

    public sealed partial class FridaIdeForm
    {
        private FridaIdeLayout _layout = new FridaIdeLayout();
        private bool _applyingLayout;
        private readonly Timer _searchDelay = new Timer { Interval = 300 };
        private readonly Dictionary<TabPage, string> _editorConversations = new Dictionary<TabPage, string>();
        private Button _trashButton;
        private FridaConversationStore _workspaceStore;
        private FridaConversation _conversation;
        private FridaWorkspaceSection _workspaceSection;
        private readonly FridaConversationTree _conversations = new FridaConversationTree { Dock = DockStyle.Fill, AccessibleName = "Conversas, projetos e reuniões" };
        private readonly TextBox _conversationSearch = new TextBox { AccessibleName = "Pesquisar conversas e mensagens" };
        private readonly TabControl _workspaceCenter = new HeadlessTabControl { Dock = DockStyle.Fill };
        private readonly TabControl _workspaceTools = new HeadlessTabControl { Dock = DockStyle.Fill };
        private readonly TabPage _conversationTab = new TabPage("Conversa"), _codeTab = new TabPage("Editor");
        private SplitContainer _workspaceSplit;
        private Panel _workspaceHost;
        private bool _changingConversation, _showTrash;
        private int _messagePage;
        private readonly RichTextBox _motorTerminal = TerminalArea();
        private SplitContainer _terminalSplit;
        private string _editorExecutionConversationId;
        private readonly ListBox _meetingArtifacts = new ListBox { Dock = DockStyle.Top, AccessibleName = "Planos, fluxos, conclusões e skills" };
        private readonly RichTextBox _artifactPreview = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
        private readonly Dictionary<string, byte[]> _conversationAudio = new Dictionary<string, byte[]>();
        // Sidebar, conversation header and right-panel header.
        private Panel _sidebar, _trashBanner, _chatHeader, _toolsHeader;
        private Splitter _sidebarSplitter;
        // Smallest widths kept while dragging: conversation column and right panel.
        private int MinConversationWidth { get { return Px(380); } }
        private int MinToolsWidth { get { return Px(260); } }
        private SegmentedTabs _filterTabs, _centerTabs, _toolTabs;
        private Label _headerTitle;
        private Chip _sectionChip, _projectChip, _linksChip, _runningChip;
        private PillButton _stopButton, _panelButton, _headerMore;
        private readonly ContextMenuStrip _headerMenu = new ContextMenuStrip(), _newMenu = new ContextMenuStrip(), _linksMenu = new ContextMenuStrip(), _treeMenu = new ContextMenuStrip();
        private readonly HashSet<string> _expandedProjects = new HashSet<string>(), _collapsedGroups = new HashSet<string>();
        private bool _sidebarOverride, _changingFilter;
        // Item the open menu acts on (the menu never selects it), inline rename box, deferred list refresh.
        private TreeNode _menuTarget, _renameNode;
        private InputFrame _inlineRename;
        private bool _treeRefreshPending;
        private readonly Timer _treeRefreshDelay = new Timer { Interval = 500 }, _undoDelay = new Timer { Interval = 10000 };
        private PillButton _undoTrash;
        private string _lastTrashedConversation, _lastTrashedProject;
        public string SelectedConversationId { get { return _conversation == null ? null : _conversation.Id; } }

        private void InitializeWorkspace(FridaConversationStore supplied)
        {
            _workspaceStore = supplied ?? new FridaConversationStore(Path.Combine(FridaIdeRuntime.DataRoot, "Workspace"));
            var migration = supplied == null ? _workspaceStore.ImportLegacy(FridaConversationAudit.DirectoryPath, Path.Combine(FridaIdeRuntime.DataRoot, "projects.json"), Path.Combine(FridaIdeRuntime.DataRoot, "Meetings")) : new FridaMigrationResult();
            if (migration.Warnings.Count > 0) FridaIdeRuntime.StorageWarning = migration.Warnings.Count + " registro(s) antigo(s) não importado(s); originais preservados.";
            foreach (string warning in migration.Warnings) BootLogger.Log("Migração da IDE: " + warning);
            if (_workspaceStore.RootTooDeep)
                FridaIdeRuntime.StorageWarning = "A pasta de dados da Frida está em um caminho muito longo (" + _workspaceStore.Root.Length + " caracteres). Instale em uma pasta mais curta para evitar falhas ao salvar conversas.";
            // The workspace catalog is the single project registry: imported legacy entries and new
            // local/VPS projects share one identity, so every list and editor uses the same objects.
            _projectList.Clear(); while (_projects.Items.Count > 1) _projects.Items.RemoveAt(1);
            foreach (var p in _workspaceStore.Projects()) { _projectList.Add(p); _projects.Items.Add(p); }
            _projects.Visible = false;
            _layout = FridaIdeRuntime.LoadState(Path.Combine(_workspaceStore.Root, "layout.json"), () => new FridaIdeLayout());
            foreach (var menu in new[] { _headerMenu, _newMenu, _linksMenu, _treeMenu }) { FridaTheme.Style(menu); _cleanup.Add(menu.Dispose); }

            _workspaceHost = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            BuildSidebar();
            _workspaceSplit = new SplitContainer { Dock = DockStyle.Fill, Panel2Collapsed = true, SplitterWidth = Px(6), BackColor = FridaTheme.Divider };
            _workspaceSplit.Panel1.BackColor = Canvas; _workspaceSplit.Panel2.BackColor = Canvas;
            // Center: conversation header over a headless Conversa/Editor tab control.
            _conversationTab.Controls.Add(_chatPage); _chatPage.Visible = true; _conversationTab.BackColor = Canvas; _codeTab.BackColor = Canvas;
            _workspaceCenter.TabPages.Add(_conversationTab);
            _codeTab.Controls.Add(_editorPage); _editorPage.Visible = true;
            _cleanup.Add(_codeTab.Dispose);
            BuildConversationHeader();
            _workspaceSplit.Panel1.Controls.Add(_workspaceCenter); _workspaceSplit.Panel1.Controls.Add(_chatHeader);
            // Right panel: segmented Artefatos | Arquivos | Terminais.
            BuildToolsPanel();
            _workspaceSplit.Panel2.Controls.Add(_workspaceTools); _workspaceSplit.Panel2.Controls.Add(_toolsHeader);
            // Draggable divider between the list and the conversation (docked between them).
            _sidebarSplitter = new Splitter { Dock = DockStyle.Left, Width = Px(6), MinSize = Px(FridaIdeLayout.MinSidebarWidth), MinExtra = MinConversationWidth, AccessibleName = "Redimensionar a lista (arraste; duplo clique restaura)" };
            FridaResize.Style(_sidebarSplitter, Surface);
            FridaTheme.Tip(_sidebarSplitter, "Arraste para ajustar a largura da lista · duplo clique restaura");
            _sidebarSplitter.SplitterMoving += (sender, e) => { e.SplitX = Math.Min(e.SplitX, _sidebar.Left + Px(FridaIdeLayout.MaxSidebarWidth)); };
            _sidebarSplitter.SplitterMoved += delegate { if (!_applyingLayout) RememberSidebarWidth(); };
            _sidebarSplitter.DoubleClick += delegate { SetSidebarWidth(FridaIdeLayout.DefaultSidebarWidth); RememberSidebarWidth(); };
            _workspaceHost.Controls.Add(_workspaceSplit); _workspaceHost.Controls.Add(_sidebarSplitter); _workspaceHost.Controls.Add(_sidebar);
            _pages["Chat"] = _workspaceHost; _pages["Código"] = _workspaceHost;
            _body.Controls.Add(_workspaceHost);
            AddNavigationButton("Chat"); AddNavigationButton("Código");

            // Right panel: drag freely between the minimum conversation and panel widths.
            FridaResize.Style(_workspaceSplit); FridaResize.Style(_terminalSplit);
            _workspaceSplit.SplitterMoving += (sender, e) =>
            {
                int width = _workspaceSplit.Width;
                e.SplitX = Math.Max(Math.Min(MinConversationWidth, width / 2), Math.Min(e.SplitX, width - _workspaceSplit.SplitterWidth - Math.Min(MinToolsWidth, width / 3)));
            };
            _workspaceSplit.SplitterMoved += delegate
            {
                if (_applyingLayout || !UserMovedSplitter(_workspaceSplit) || _workspaceSplit.Panel2Collapsed || _workspaceSplit.Width < 180) return;
                RememberToolsSplit();
            };
            _workspaceSplit.DoubleClick += delegate
            {
                if (_workspaceSplit.Panel2Collapsed || !_workspaceSplit.SplitterRectangle.Contains(_workspaceSplit.PointToClient(Cursor.Position))) return;
                _layout.ToolsRatio = new FridaIdeLayout().ToolsRatio; SaveLayout(); ResizeWorkspaceTools();
            };
            _terminalSplit.SplitterMoved += delegate
            {
                if (_applyingLayout || !UserMovedSplitter(_terminalSplit) || _terminalSplit.Height < 120) return;
                _layout.TerminalRatio = Clamp((double)_terminalSplit.SplitterDistance / _terminalSplit.Height, 0.15, 0.85); SaveLayout();
            };
            _terminalSplit.DoubleClick += delegate
            {
                if (!_terminalSplit.SplitterRectangle.Contains(_terminalSplit.PointToClient(Cursor.Position))) return;
                _layout.TerminalRatio = 0.5; SaveLayout(); ApplyTerminalRatio();
            };
            _terminalSplit.Resize += delegate { ApplyTerminalRatio(); };
            _workspaceTools.SelectedIndexChanged += delegate { if (!_applyingLayout && _workspaceTools.SelectedTab != null) { _layout.ToolTab = _workspaceTools.SelectedTab.Text; SaveLayout(); } };
            Action<FridaDiagnosticEvent> diagnostics = e => Post(() => {
                if (_conversation == null || e.ConversationId != _conversation.Id) return;
                var view = e.Kind == "motor" ? _motorTerminal : _terminal;
                if (view.TextLength > 250000) view.Clear(); view.AppendText(e + "\r\n");
            });
            FridaDiagnostics.Published += diagnostics; _cleanup.Add(() => FridaDiagnostics.Published -= diagnostics);
            Action<string> meetingChanged = id => Post(() => { if (_conversation != null && _conversation.Id == id) { RenderConversation(); RefreshWorkspaceArtifacts(); } });
            FridaMeetingWorkspace.Changed += meetingChanged; _cleanup.Add(() => FridaMeetingWorkspace.Changed -= meetingChanged);
            // Content search reads message files; wait for a pause in typing instead of every key.
            _searchDelay.Tick += delegate { _searchDelay.Stop(); RefreshConversationTree(); };
            _cleanup.Add(() => { _searchDelay.Stop(); _searchDelay.Dispose(); });
            _conversationSearch.TextChanged += delegate { _searchDelay.Stop(); _searchDelay.Start(); };
            _conversationSearch.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; _conversations.Focus(); } else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _conversationSearch.Clear(); } };
            BuildTreeMenu();
            _workspaceHost.Resize += delegate { ResizeWorkspaceTools(); };
            Theme(_workspaceHost);
            ApplyLayout();
            RefreshConversationTree();
            var latest = _workspaceStore.List().FirstOrDefault(); if (latest != null) SelectConversation(latest.Id);
            RefreshConversationHeader();
        }

        private static Panel Spacer(int height, Color color) { return new Panel { Dock = DockStyle.Top, Height = FridaTheme.Px(height), BackColor = color }; }

        // Sidebar: primary "Nova conversa" with a split menu, search, segmented filter, the list and
        // a discreet Lixeira entry. Docked top-down; the list fills the remaining height.
        private void BuildSidebar()
        {
            _sidebar = new Panel { Dock = DockStyle.Left, Width = Px(ClampSidebar(_layout.SidebarWidth)), BackColor = Surface, Padding = new Padding(Px(12), Px(12), Px(12), Px(6)) };
            var top = new Panel { Dock = DockStyle.Top, Height = Px(36), BackColor = Surface };
            var newButton = new PillButton("Nova conversa", PillKind.Primary, FridaTheme.GlyphAdd) { AccessibleName = "Nova conversa (Ctrl+N)" };
            FridaTheme.Tip(newButton, "Nova conversa (Ctrl+N)");
            newButton.Click += delegate { WorkspaceAction(() => NewWorkspaceConversation(false)); };
            var newMore = new PillButton("", PillKind.Secondary, FridaTheme.GlyphChevronDown) { AccessibleName = "Mais opções: reunião e projetos" };
            FridaTheme.Tip(newMore, "Nova reunião, abrir ou criar projeto");
            newMore.Click += delegate { _newMenu.Show(newMore, new Point(newMore.Width - _newMenu.Width, newMore.Height)); };
            top.Controls.Add(newButton); top.Controls.Add(newMore);
            top.Resize += delegate
            {
                int h = top.Height, gap = Px(6);
                newMore.SetBounds(top.Width - h, 0, h, h);
                newButton.SetBounds(0, 0, Math.Max(Px(80), top.Width - h - gap), h);
            };
            _newMenu.Items.Add("Nova reunião", null, delegate { WorkspaceAction(() => NewWorkspaceConversation(true)); }).ToolTipText = "Ctrl+Shift+N";
            _newMenu.Items.Add(new ToolStripSeparator());
            _newMenu.Items.Add("Abrir pasta do computador…", null, delegate { WorkspaceAction(() => AddProject(false)); });
            _newMenu.Items.Add("Criar projeto no computador…", null, delegate { WorkspaceAction(() => AddProject(true)); });
            _newMenu.Items.Add("Abrir projeto na VPS…", null, async delegate { await WorkspaceActionAsync(() => AddRemoteProject(false)); });
            _newMenu.Items.Add("Criar projeto na VPS…", null, async delegate { await WorkspaceActionAsync(() => AddRemoteProject(true)); });
            _newMenu.Items.Add(new ToolStripSeparator());
            _newMenu.Items.Add("Conferir identidade SSH…", null, delegate { WorkspaceAction(() => { string alias = Prompt("Alias SSH para conferir a identidade do servidor", ""); if (alias != null) FridaSshWorkspace.OpenIdentityCheck(alias); }); });
            var search = new InputFrame(_conversationSearch, "Pesquisar (Ctrl+K)", true) { Dock = DockStyle.Top };
            _filterTabs = new SegmentedTabs { Dock = DockStyle.Top, AccessibleName = "Filtro da lista" };
            _filterTabs.SetItems(new[] { "Conversas", "Projetos", "Reuniões" });
            _filterTabs.SelectedIndexChanged += delegate { if (!_changingFilter) RefreshConversationTree(); };
            _trashBanner = new Panel { Dock = DockStyle.Top, Height = Px(44), BackColor = Surface, Visible = false };
            var trashLabel = new Label { Text = "Lixeira da IDE · itens restauráveis", Dock = DockStyle.Fill, ForeColor = Coral, Font = FridaTheme.Strong(9), TextAlign = ContentAlignment.MiddleLeft, BackColor = Surface };
            var back = new PillButton("Voltar", PillKind.Secondary, null, true) { Dock = DockStyle.Right };
            back.Click += delegate { ToggleTrash(); };
            _trashBanner.Controls.Add(trashLabel); _trashBanner.Controls.Add(back);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = Px(44), BackColor = Surface, Padding = new Padding(0, Px(6), 0, 0) };
            _trashButton = new PillButton("Lixeira", PillKind.Ghost, FridaTheme.GlyphDelete, true) { Dock = DockStyle.Left };
            FridaTheme.Tip(_trashButton, "Itens excluídos da IDE (restauráveis)");
            _trashButton.Click += delegate { ToggleTrash(); };
            footer.Controls.Add(_trashButton);
            // After a delete, "Desfazer" stays in the footer for ten seconds.
            _undoTrash = new PillButton("Desfazer", PillKind.Secondary, FridaTheme.GlyphHistory, true) { Dock = DockStyle.Right, Visible = false, AccessibleName = "Desfazer a exclusão" };
            FridaTheme.Tip(_undoTrash, "Restaurar o item que acabou de ir para a Lixeira");
            _undoTrash.Click += delegate { UndoTrash(); };
            footer.Controls.Add(_undoTrash);
            _undoDelay.Tick += delegate { HideUndoTrash(); };
            _treeRefreshDelay.Tick += delegate { _treeRefreshDelay.Stop(); RefreshConversationTree(); };
            _cleanup.Add(() => { _undoDelay.Dispose(); _treeRefreshDelay.Dispose(); });
            footer.Paint += (sender, e) => { using (var pen = new Pen(FridaTheme.Divider)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            _conversations.IsRunning = id => _workspaceStore != null && _workspaceStore.IsRunning(id);
            _conversations.MoreRequested += (node, point) => ShowTreeMenu(node, point);
            _conversations.AfterSelect += (sender, args) =>
            {
                if (_changingConversation) return;
                var item = args.Node.Tag as FridaConversation;
                if (item != null && item.DeletedUtc == null) SelectConversation(item.Id);
            };
            _conversations.AfterExpand += (sender, e) => TrackExpansion(e.Node, true);
            _conversations.AfterCollapse += (sender, e) => TrackExpansion(e.Node, false);
            // The list scrolls under an open rename box: keep the box on its row by closing it.
            _conversations.MouseWheel += delegate { if (_inlineRename != null) FinishInlineRename(true); };
            _sidebar.Controls.Add(_conversations);
            _sidebar.Controls.Add(footer);
            _sidebar.Controls.Add(_trashBanner); _sidebar.Controls.Add(Spacer(8, Surface));
            _sidebar.Controls.Add(_filterTabs); _sidebar.Controls.Add(Spacer(10, Surface));
            _sidebar.Controls.Add(search); _sidebar.Controls.Add(Spacer(10, Surface));
            _sidebar.Controls.Add(top);
        }
        private static int ClampSidebar(int logical) { return Math.Max(FridaIdeLayout.MinSidebarWidth, Math.Min(FridaIdeLayout.MaxSidebarWidth, logical)); }
        private void SetSidebarWidth(int logical)
        {
            _applyingLayout = true;
            try { _sidebar.Width = Px(ClampSidebar(logical)); } finally { _applyingLayout = false; }
            ResizeWorkspaceTools(); LayoutConversationHeader();
        }
        private void RememberSidebarWidth()
        {
            _layout.SidebarWidth = ClampSidebar((int)Math.Round(_sidebar.Width / FridaTheme.Factor));
            SaveLayout(); ResizeWorkspaceTools(); LayoutConversationHeader();
        }
        private void TrackExpansion(TreeNode node, bool expanded)
        {
            if (_changingConversation) return;
            var project = node.Tag as FridaIdeProject; var group = node.Tag as FridaTreeGroup;
            if (project != null) { if (expanded) _expandedProjects.Add(project.Id); else _expandedProjects.Remove(project.Id); }
            if (group != null) { if (expanded) _collapsedGroups.Remove(group.Title); else _collapsedGroups.Add(group.Title); }
        }
        private void ToggleTrash()
        {
            _showTrash = !_showTrash;
            _trashBanner.Visible = _showTrash; _trashButton.Visible = !_showTrash;
            RefreshConversationTree();
        }
        // Item menu: opened from "⋯", a right click or Apps/Shift+F10 on the item it acts on, without
        // selecting or opening it. Rename edits the row in place; delete offers "Desfazer".
        private TreeNode TreeTarget { get { return _menuTarget ?? _conversations.SelectedNode; } }
        private bool TreeBusy { get { return _treeMenu.Visible || _inlineRename != null; } }
        private void ShowTreeMenu(TreeNode node, Point point)
        {
            if (node == null) return;
            if (_inlineRename != null) FinishInlineRename(true);
            _menuTarget = node; _conversations.MenuNode = node;
            _treeMenu.Show(_conversations, point);
            if (!_treeMenu.Visible) { _menuTarget = null; _conversations.MenuNode = null; } // nothing to offer
        }
        private void BuildTreeMenu()
        {
            var menu = _treeMenu;
            var newInProject = menu.Items.Add("Nova conversa no projeto", null, delegate { WorkspaceAction(() => NewWorkspaceConversation(false)); });
            var meetingInProject = menu.Items.Add("Nova reunião no projeto", null, delegate { WorkspaceAction(() => NewWorkspaceConversation(true)); });
            // Started after the menu has closed and returned focus, so the box keeps it.
            Action renameSelected = () => { var node = TreeTarget; BeginInvoke((Action)(() => BeginInlineRename(node))); };
            var rename = menu.Items.Add("Renomear", null, delegate { renameSelected(); }); ((ToolStripMenuItem)rename).ShortcutKeyDisplayString = "F2";
            var toChat = menu.Items.Add("Mover para Chat", null, delegate { MoveWorkspaceConversation(FridaWorkspaceSection.Chat); });
            var toCode = menu.Items.Add("Mover para Código", null, delegate { MoveWorkspaceConversation(FridaWorkspaceSection.Code); });
            // Existing conversations can join a project; their history then feeds the project memory.
            var toProject = new ToolStripMenuItem("Mover para projeto");
            menu.Items.Add(toProject);
            var origin = menu.Items.Add("Abrir conversa de origem", null, delegate { var c = SelectedTreeConversation(); if (c != null && c.OriginConversationId != null) WorkspaceAction(() => SelectConversation(c.OriginConversationId)); });
            var probe = menu.Items.Add("Testar conexão SSH", null, async delegate
            {
                var p = SelectedTreeProject(); if (p == null) return;
                await WorkspaceActionAsync(async () => { Progress("Conectando ao destino selecionado…"); string result = await Task.Run(() => FridaSshWorkspace.Probe(p)); Progress(result.Replace("\n", " · ")); });
            });
            var stop = menu.Items.Add("Interromper execução", null, delegate { StopWorkspaceExecution(); });
            var separator = new ToolStripSeparator(); menu.Items.Add(separator);
            Action trashSelected = () => WorkspaceAction(() =>
            {
                var c = SelectedTreeConversation(); var p = SelectedTreeProject();
                if (c != null) _workspaceStore.Trash(c.Id); else if (p != null) _workspaceStore.TrashProject(p.Id); else return;
                if (_conversation != null && _workspaceStore.Get(_conversation.Id, true).DeletedUtc != null) ClearSelectedConversation();
                ShowUndoTrash(c == null ? null : c.Id, p == null ? null : p.Id);
                Progress((c != null ? "Conversa" : "Projeto") + " enviado à Lixeira. Use Desfazer para recuperar agora.");
            });
            var trash = menu.Items.Add("Excluir", null, delegate { trashSelected(); }); ((ToolStripMenuItem)trash).ShortcutKeyDisplayString = "Del";
            var restore = menu.Items.Add("Restaurar", null, delegate { WorkspaceAction(() => { var c = SelectedTreeConversation(); var p = SelectedTreeProject(); if (c != null) _workspaceStore.Restore(c.Id); else if (p != null) _workspaceStore.RestoreProject(p.Id); }); });
            menu.Opening += (sender, args) =>
            {
                var c = SelectedTreeConversation(); var p = SelectedTreeProject();
                if (c == null && p == null) { args.Cancel = true; return; }
                newInProject.Visible = meetingInProject.Visible = p != null && !_showTrash;
                rename.Visible = !_showTrash;
                toChat.Visible = c != null && !_showTrash && c.Section != FridaWorkspaceSection.Chat;
                toCode.Visible = c != null && !_showTrash && c.Section != FridaWorkspaceSection.Code;
                origin.Visible = c != null && !_showTrash && c.OriginConversationId != null;
                toProject.Visible = c != null && !_showTrash;
                toProject.DropDownItems.Clear();
                if (c != null && !_showTrash)
                {
                    foreach (var project in _workspaceStore.Projects().Where(x => x.Id != c.ProjectId).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    { var target = project; toProject.DropDownItems.Add(project.Name, null, delegate { MoveConversationToProject(c, target); }); }
                    if (c.ProjectId != null)
                    {
                        if (toProject.DropDownItems.Count > 0) toProject.DropDownItems.Add(new ToolStripSeparator());
                        toProject.DropDownItems.Add("Remover do projeto", null, delegate { MoveConversationToProject(c, null); });
                    }
                    if (toProject.DropDownItems.Count == 0) toProject.DropDownItems.Add("Nenhum projeto ainda: use a seta de Nova conversa").Enabled = false;
                    toProject.Enabled = !_workspaceStore.IsRunning(c.Id);
                }
                probe.Visible = p != null && !_showTrash && !string.IsNullOrEmpty(p.SshAlias);
                // A running item cannot be trashed; offer the existing interruption instead.
                bool running = c != null && _workspaceStore.IsRunning(c.Id);
                stop.Visible = running && _conversation != null && _conversation.Id == c.Id;
                trash.Visible = !_showTrash; trash.Enabled = !running; separator.Visible = !_showTrash;
                restore.Visible = _showTrash;
            };
            // Item clicks run before this posted reset; a list refresh that waited for the menu runs now.
            menu.Closed += delegate
            {
                BeginInvoke((Action)(() =>
                {
                    _menuTarget = null; _conversations.MenuNode = null;
                    if (_treeRefreshPending && !TreeBusy) RefreshConversationTree();
                }));
            };
            _conversations.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.F2 && !_showTrash) { args.Handled = true; renameSelected(); }
                else if (args.KeyCode == Keys.Delete && !_showTrash) { args.Handled = true; trashSelected(); }
            };
        }

        // Conversation header: editable title, section/project/links/running chips, Conversa|Editor
        // switch (Código) and icon actions. Laid out by priority so nothing overlaps.
        private void BuildConversationHeader()
        {
            _chatHeader = new Panel { Dock = DockStyle.Top, Height = Px(56), BackColor = Canvas };
            _headerTitle = new Label { Font = FridaTheme.Strong(13), ForeColor = Ink, BackColor = Canvas, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, AccessibleName = "Título da conversa (F2 renomeia)" };
            _headerTitle.Click += delegate { RenameCurrentConversation(); };
            FridaTheme.Tip(_headerTitle, "Clique ou F2 para renomear");
            _sectionChip = new Chip("Chat", FridaTheme.GlyphChat);
            _projectChip = new Chip("Sem projeto · Computador", FridaTheme.GlyphFolder);
            _linksChip = new Chip("Vínculos", FridaTheme.GlyphLink) { Clickable = true, Tint = FridaTheme.Highlight };
            _linksChip.Click += delegate { ShowLinksMenu(_linksChip); };
            _runningChip = new Chip("Executando…", FridaTheme.GlyphRefresh) { Tint = Accent };
            _stopButton = new PillButton("Parar", PillKind.Danger, FridaTheme.GlyphStop, true);
            _stopButton.Click += delegate { StopWorkspaceExecution(); };
            _centerTabs = new SegmentedTabs { Stretch = false, AccessibleName = "Conversa ou editor" };
            _centerTabs.Bind(_workspaceCenter);
            _centerTabs.SelectedIndexChanged += delegate { LayoutConversationHeader(); if (_workspaceCenter.SelectedTab == _conversationTab) _draft.Focus(); };
            _panelButton = new PillButton("", PillKind.Icon, FridaTheme.GlyphPaneOpen) { AccessibleName = "Painel direito (Ctrl+J)" };
            FridaTheme.Tip(_panelButton, "Mostrar ou ocultar painel: artefatos, arquivos e terminais (Ctrl+J)");
            _panelButton.Click += delegate { SetToolsVisible(_workspaceSplit.Panel2Collapsed); };
            _headerMore = new PillButton("", PillKind.Icon, FridaTheme.GlyphMore) { AccessibleName = "Mais ações da conversa" };
            FridaTheme.Tip(_headerMore, "Mais ações");
            _headerMore.Click += delegate { _headerMenu.Show(_headerMore, new Point(_headerMore.Width - _headerMenu.Width, _headerMore.Height)); };
            _headerMenu.Items.Add("Nova conversa", null, delegate { WorkspaceAction(() => NewWorkspaceConversation(false)); }).ToolTipText = "Ctrl+N";
            _headerMenu.Items.Add("Nova reunião", null, delegate { WorkspaceAction(() => NewWorkspaceConversation(true)); });
            _headerMenu.Items.Add(new ToolStripSeparator());
            var rename = (ToolStripMenuItem)_headerMenu.Items.Add("Renomear", null, delegate { RenameCurrentConversation(); }); rename.ShortcutKeyDisplayString = "F2";
            var toChat = _headerMenu.Items.Add("Mover para Chat", null, delegate { if (_conversation != null) WorkspaceAction(() => { _workspaceStore.Move(_conversation.Id, FridaWorkspaceSection.Chat); SelectConversation(_conversation.Id); }); });
            var toCode = _headerMenu.Items.Add("Mover para Código", null, delegate { if (_conversation != null) WorkspaceAction(() => { _workspaceStore.Move(_conversation.Id, FridaWorkspaceSection.Code); SelectConversation(_conversation.Id); }); });
            var links = _headerMenu.Items.Add("Vínculos…", null, delegate { ShowLinksMenu(_headerMore); });
            _headerMenu.Items.Add(new ToolStripSeparator());
            var delete = _headerMenu.Items.Add("Excluir conversa", null, delegate
            {
                if (_conversation == null) return;
                string id = _conversation.Id;
                WorkspaceAction(() => { _workspaceStore.Trash(id); ClearSelectedConversation(); Progress("Enviado à Lixeira da IDE. Use Lixeira › Restaurar para recuperar."); });
            });
            _headerMenu.Opening += delegate
            {
                bool has = _conversation != null, running = has && _workspaceStore.IsRunning(_conversation.Id);
                rename.Enabled = links.Enabled = has; delete.Enabled = has && !running;
                toChat.Visible = has && _conversation.Section != FridaWorkspaceSection.Chat;
                toCode.Visible = has && _conversation.Section != FridaWorkspaceSection.Code;
            };
            foreach (Control c in new Control[] { _headerTitle, _sectionChip, _projectChip, _linksChip, _runningChip, _stopButton, _centerTabs, _panelButton, _headerMore }) _chatHeader.Controls.Add(c);
            _chatHeader.Resize += delegate { LayoutConversationHeader(); };
            _chatHeader.Paint += (sender, e) => { using (var pen = new Pen(FridaTheme.Divider)) e.Graphics.DrawLine(pen, 0, _chatHeader.Height - 1, _chatHeader.Width, _chatHeader.Height - 1); };
        }
        private void RenameCurrentConversation()
        {
            if (_conversation == null) return;
            string title = Prompt("Título da conversa", _conversation.Title); if (title == null) return;
            string id = _conversation.Id;
            WorkspaceAction(() => { _workspaceStore.Rename(id, title); _conversation = _workspaceStore.Get(id); RefreshConversationHeader(); });
        }
        private void ShowLinksMenu(Control anchor)
        {
            _linksMenu.Items.Clear();
            if (_conversation != null)
                foreach (var linked in _workspaceStore.Linked(_conversation.Id))
                {
                    string id = linked.Id;
                    string label = (linked.Id == _conversation.OriginConversationId ? "Origem · " : "Continuação · ") + FridaConversationStore.SectionName(linked.Section) + " › " + linked.Title;
                    _linksMenu.Items.Add(label, null, delegate { WorkspaceAction(() => SelectConversation(id)); });
                }
            if (_linksMenu.Items.Count == 0) { Progress("Esta conversa não tem conversas vinculadas."); return; }
            _linksMenu.Show(anchor, new Point(0, anchor.Height));
        }
        private void RefreshConversationHeader()
        {
            if (_chatHeader == null || _workspaceStore == null) return;
            var c = _conversation;
            _headerTitle.Text = c == null ? (_workspaceSection == FridaWorkspaceSection.Code ? "Nova conversa de código" : "Nova conversa") : c.Title;
            _headerTitle.ForeColor = c == null ? Muted : Ink;
            bool code = (c == null ? _workspaceSection : c.Section) == FridaWorkspaceSection.Code;
            _sectionChip.Text = c != null && c.Kind == FridaConversationKind.Meeting ? (code ? "Reunião · Código" : "Reunião") : code ? "Código" : "Chat";
            _sectionChip.Glyph = c != null && c.Kind == FridaConversationKind.Meeting ? FridaTheme.GlyphPeople : code ? FridaTheme.GlyphCode : FridaTheme.GlyphChat;
            var project = ConversationProject(c);
            _projectChip.Text = project == null ? "Sem projeto · Computador" : project.Name + " · " + (string.IsNullOrEmpty(project.SshAlias) ? "Computador" : "VPS " + project.SshAlias);
            _projectChip.Glyph = project != null && !string.IsNullOrEmpty(project.SshAlias) ? FridaTheme.GlyphGlobe : FridaTheme.GlyphFolder;
            FridaTheme.Tip(_projectChip, project == null ? "Pedidos sem projeto usam a pasta Documentos neste computador." : "Destino dos pedidos: " + project.Root);
            int linkCount = c == null ? 0 : _workspaceStore.Linked(c.Id).Count;
            _linksChip.Text = linkCount == 1 ? "1 vínculo" : linkCount + " vínculos"; _linksChip.Tag = linkCount;
            bool running = c != null && _workspaceStore.IsRunning(c.Id);
            _runningChip.Tag = running;
            _centerTabs.Tag = code;
            _panelButton.Kind = _workspaceSplit != null && !_workspaceSplit.Panel2Collapsed ? PillKind.Ghost : PillKind.Icon;
            foreach (var chip in new[] { _sectionChip, _projectChip, _linksChip }) { chip.Fit(); chip.Invalidate(); }
            LayoutConversationHeader();
            _conversations.Invalidate();
        }
        private void LayoutConversationHeader()
        {
            if (_chatHeader == null) return;
            int h = _chatHeader.Height, gap = Px(8), x = Px(20), right = _chatHeader.Width - Px(12);
            Func<Control, int> top = c => (h - c.Height) / 2;
            _headerMore.SetBounds(right - _headerMore.Width, top(_headerMore), _headerMore.Width, _headerMore.Height); right = _headerMore.Left - Px(4);
            _panelButton.SetBounds(right - _panelButton.Width, top(_panelButton), _panelButton.Width, _panelButton.Height); right = _panelButton.Left - gap;
            bool code = _centerTabs.Tag is bool && (bool)_centerTabs.Tag;
            _centerTabs.Visible = code && _workspaceCenter.TabPages.Count > 1;
            if (_centerTabs.Visible) { var s = _centerTabs.GetPreferredSize(Size.Empty); _centerTabs.SetBounds(right - s.Width, (h - s.Height) / 2, s.Width, s.Height); right = _centerTabs.Left - gap * 2; }
            bool running = _runningChip.Tag is bool && (bool)_runningChip.Tag;
            int links = _linksChip.Tag is int ? (int)_linksChip.Tag : 0;
            // Chips in priority order; lower-priority ones drop out before the title shrinks below its minimum.
            var chips = new List<Control>();
            if (running) { chips.Add(_runningChip); chips.Add(_stopButton); }
            chips.Add(_projectChip); chips.Add(_sectionChip);
            if (links > 0) chips.Add(_linksChip);
            int minTitle = Px(140), titleWant = TextRenderer.MeasureText(_headerTitle.Text, _headerTitle.Font).Width + Px(8);
            while (chips.Count > 0 && minTitle + chips.Sum(c => c.Width + gap) > right - x) chips.RemoveAt(chips.Count - 1);
            foreach (var c in new Control[] { _sectionChip, _projectChip, _linksChip, _runningChip, _stopButton }) c.Visible = chips.Contains(c);
            int chipsWidth = chips.Sum(c => c.Width + gap);
            int titleWidth = Math.Max(Px(60), Math.Min(titleWant, right - x - chipsWidth));
            int titleHeight = _headerTitle.Font.Height + Px(6); // one line: long titles end in an ellipsis
            _headerTitle.SetBounds(x, (h - titleHeight) / 2, titleWidth, titleHeight);
            int cx = _headerTitle.Right + gap;
            foreach (var c in new Control[] { _sectionChip, _projectChip, _linksChip, _runningChip, _stopButton }.Where(chips.Contains))
            { c.SetBounds(cx, top(c), c.Width, c.Height); cx += c.Width + gap; }
        }

        // Right panel: header with segmented tabs and a close button over a headless tab control.
        private void BuildToolsPanel()
        {
            _toolsHeader = new Panel { Dock = DockStyle.Top, Height = Px(56), BackColor = Canvas, Padding = new Padding(Px(10), Px(11), Px(8), Px(11)) };
            _toolTabs = new SegmentedTabs { Dock = DockStyle.Fill, AccessibleName = "Painel direito" };
            var close = new PillButton("", PillKind.Icon, FridaTheme.GlyphClose, true) { Dock = DockStyle.Right, AccessibleName = "Fechar painel (Ctrl+J)" };
            FridaTheme.Tip(close, "Fechar painel (Ctrl+J)");
            close.Click += delegate { SetToolsVisible(false); };
            _toolsHeader.Controls.Add(_toolTabs); _toolsHeader.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Px(6), BackColor = Canvas }); _toolsHeader.Controls.Add(close);
            _toolsHeader.Paint += (sender, e) => { using (var pen = new Pen(FridaTheme.Divider)) e.Graphics.DrawLine(pen, 0, _toolsHeader.Height - 1, _toolsHeader.Width, _toolsHeader.Height - 1); };
            // Artefatos: cards with version and state chips over a formatted Markdown preview.
            var artifacts = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var artifactBar = Bar();
            var artifactTitle = new Label { Text = "Planos, fluxos e skills", Font = FridaTheme.Strong(9.5f), ForeColor = Ink, BackColor = Canvas, AutoSize = true };
            artifactBar.Controls.Add(artifactTitle);
            var export = new PillButton("", PillKind.Icon, FridaTheme.GlyphExport, true) { AccessibleName = "Exportar Markdown" };
            FridaTheme.Tip(export, "Exportar a versão selecionada em Markdown");
            export.Click += delegate
            {
                var artifact = _meetingArtifacts.SelectedItem as FridaMeetingArtifact; if (artifact == null) return;
                using (var picker = new SaveFileDialog { Filter = "Markdown|*.md", FileName = artifact.Kind + "-v" + artifact.Version + ".md" })
                    if (picker.ShowDialog(this) == DialogResult.OK) File.WriteAllText(picker.FileName, artifact.Markdown, new System.Text.UTF8Encoding(false));
            };
            artifactBar.AddRight(export);
            StyleArtifactList();
            _meetingArtifacts.Height = Px(220);
            _artifactPreview.BackColor = Canvas; _artifactPreview.ForeColor = Ink; _artifactPreview.Font = FridaTheme.Font(10.5f);
            FridaTheme.DarkScroll(_artifactPreview);
            var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(Px(14), Px(10), Px(10), Px(10)) };
            previewHost.Controls.Add(_artifactPreview);
            var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = FridaTheme.Divider };
            artifacts.Controls.Add(previewHost); artifacts.Controls.Add(divider); artifacts.Controls.Add(_meetingArtifacts); artifacts.Controls.Add(artifactBar);
            _meetingArtifacts.SelectedIndexChanged += delegate { var a = _meetingArtifacts.SelectedItem as FridaMeetingArtifact; if (a == null) _artifactPreview.Clear(); else FridaMarkdown.Render(_artifactPreview, a.Markdown); };
            // A final decision opens as the full report (prints, exports) on double click or Enter.
            Action openDecision = () => { var a = _meetingArtifacts.SelectedItem as FridaMeetingArtifact; if (a != null && a.Kind == "decision") ShowDecisionReport(FridaMeetingWorkspace.DecisionOf(a)); };
            _meetingArtifacts.DoubleClick += delegate { openDecision(); };
            _meetingArtifacts.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; openDecision(); } };
            AddWorkspaceTool("Artefatos", artifacts);
            // Arquivos: segmented Navegar | Usados pela Frida bound to the files TabControl.
            var files = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            var filesTabs = (TabControl)_pages["Arquivos"];
            var filesSwitch = new SegmentedTabs { Dock = DockStyle.Fill, AccessibleName = "Arquivos" };
            filesSwitch.Bind(filesTabs);
            var filesSwitchRow = new Panel { Dock = DockStyle.Top, Height = Px(46), BackColor = Canvas, Padding = new Padding(Px(10), Px(8), Px(10), Px(4)) };
            filesSwitchRow.Controls.Add(filesSwitch);
            files.Controls.Add(filesTabs); files.Controls.Add(filesSwitchRow);
            AddWorkspaceTool("Arquivos", files);
            // Terminais: Motor above Execução, resizable.
            _terminalSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = Px(6), BackColor = FridaTheme.Divider };
            _terminalSplit.Panel1.BackColor = Canvas; _terminalSplit.Panel2.BackColor = Canvas;
            var motor = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
            motor.Controls.Add(_motorTerminal); motor.Controls.Add(TerminalHeader("Motor · classificação, modelos e ferramentas", FridaTheme.GlyphSettings, _motorTerminal));
            _motorTerminal.AccessibleName = "Terminal Motor: classificação, modelos, ferramentas, etapas, duração e erros";
            _terminal.AccessibleName = "Terminal Execução: comandos, diretório, destino, saída e resultado";
            _terminalSplit.Panel1.Controls.Add(motor); _terminalSplit.Panel2.Controls.Add(_pages["Terminal"]); _pages["Terminal"].Visible = true;
            AddWorkspaceTool("Terminais", _terminalSplit);
            _toolTabs.Bind(_workspaceTools);
        }
        private void StyleArtifactList()
        {
            var list = _meetingArtifacts;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = Px(54); list.BorderStyle = BorderStyle.None; list.BackColor = Canvas; list.IntegralHeight = false;
            FridaTheme.DarkScroll(list);
            list.DrawItem += (sender, e) =>
            {
                using (var brush = new SolidBrush(Canvas)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                var a = list.Items[e.Index] as FridaMeetingArtifact; if (a == null) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                var card = new Rectangle(e.Bounds.X + Px(8), e.Bounds.Y + Px(3), e.Bounds.Width - Px(16), e.Bounds.Height - Px(6));
                FridaTheme.FillRound(e.Graphics, card, Px(10), selected ? FridaTheme.Bubble : FridaTheme.Surface, selected ? FridaTheme.Edge : FridaTheme.Divider);
                string glyph = a.Kind == "skill" ? FridaTheme.GlyphStar : a.Kind == "flow" ? FridaTheme.GlyphLink : a.Kind == "decision" ? FridaTheme.GlyphCheck : FridaTheme.GlyphDocument;
                FridaTheme.Text(e.Graphics, glyph, FridaTheme.Icons(11), new Rectangle(card.X + Px(10), card.Y, Px(22), card.Height), selected ? Highlight : Muted, TextFormatFlags.VerticalCenter);
                int x = card.X + Px(40);
                FridaTheme.Text(e.Graphics, a.Title, FridaTheme.Strong(10), new Rectangle(x, card.Y + Px(5), card.Right - x - Px(10), Px(20)), Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                string kind = a.Kind == "plan" ? "Plano" : a.Kind == "flow" ? "Fluxo" : a.Kind == "report" ? "Conclusões" : a.Kind == "skill" ? "Skill" : a.Kind == "decision" ? "Decisão final" : a.Kind;
                var chips = new List<Tuple<string, Color>> { Tuple.Create(kind, Muted), Tuple.Create("v" + a.Version, FridaTheme.Highlight) };
                if (a.SkillState != null) chips.Add(Tuple.Create(a.SkillState, a.SkillState == "verificada" ? FridaTheme.Success : a.SkillState == "salva" ? Accent : Muted));
                int cx = x;
                foreach (var chip in chips)
                {
                    int w = TextRenderer.MeasureText(chip.Item1, FridaTheme.Font(8)).Width + Px(14);
                    if (cx + w > card.Right - Px(8)) break;
                    var r = new Rectangle(cx, card.Y + Px(27), w, Px(18));
                    FridaTheme.FillRound(e.Graphics, r, Px(9), Color.FromArgb(40, chip.Item2), Color.FromArgb(140, chip.Item2));
                    FridaTheme.Text(e.Graphics, chip.Item1, FridaTheme.Font(8), r, chip.Item2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    cx += w + Px(6);
                }
            };
            list.SelectedIndexChanged += delegate { list.Invalidate(); };
        }
        private void AddWorkspaceTool(string title, Control content)
        { var tab = new TabPage(title) { BackColor = Canvas }; content.Dock = DockStyle.Fill; tab.Controls.Add(content); content.Visible = true; _workspaceTools.TabPages.Add(tab); }
        private void ApplyLayout()
        {
            _applyingLayout = true;
            try
            {
                _workspaceSplit.Panel2Collapsed = !_layout.ToolsVisible;
                foreach (TabPage tab in _workspaceTools.TabPages) if (tab.Text == _layout.ToolTab) _workspaceTools.SelectedTab = tab;
            }
            finally { _applyingLayout = false; }
            ApplyEditorWordWrap(_layout.EditorWordWrap);
            ResizeWorkspaceTools(); ApplyTerminalRatio(); ApplyResponsiveLayout();
        }
        private static bool UserMovedSplitter(SplitContainer split) { return Control.MouseButtons == MouseButtons.Left || split.Focused; }
        private void SetToolsVisible(bool visible)
        {
            _applyingLayout = true;
            try { _workspaceSplit.Panel2Collapsed = !visible; } finally { _applyingLayout = false; }
            _layout.ToolsVisible = visible;
            ResizeWorkspaceTools(); SaveLayout(); RefreshConversationHeader();
        }
        // Below 960 px the list collapses (Ctrl+B or the rail button shows it again) so the
        // conversation and the right panel keep usable widths.
        private bool Narrow { get { return ClientSize.Width < Px(960); } }
        private void ToggleSidebar()
        {
            if (_sidebar == null) return;
            if (Narrow) _sidebarOverride = !_sidebar.Visible;
            else { _layout.SidebarVisible = !_layout.SidebarVisible; _sidebarOverride = false; SaveLayout(); }
            ApplyResponsiveLayout();
            if (_sidebar.Visible) _conversations.Focus();
        }
        private void ApplyResponsiveLayout()
        {
            if (_sidebar == null) return;
            bool visible = Narrow ? _sidebarOverride : _layout.SidebarVisible;
            if (_sidebar.Visible != visible) _sidebar.Visible = visible;
            if (_sidebarSplitter != null && _sidebarSplitter.Visible != visible) _sidebarSplitter.Visible = visible;
            ResizeWorkspaceTools(); LayoutConversationHeader();
        }
        private void SaveLayout()
        {
            if (_workspaceStore == null || _applyingLayout) return;
            try
            {
                Directory.CreateDirectory(_workspaceStore.Root);
                FridaRuntimeHealth.AtomicWrite(Path.Combine(_workspaceStore.Root, "layout.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(_layout));
            }
            catch (IOException ex) { BootLogger.Log("Disposição da IDE não salva: " + ex.GetType().Name); }
            catch (UnauthorizedAccessException ex) { BootLogger.Log("Disposição da IDE não salva: " + ex.GetType().Name); }
        }
        private static double Clamp(double value, double minimum, double maximum) { return double.IsNaN(value) ? minimum : Math.Max(minimum, Math.Min(maximum, value)); }
        private void RememberToolsSplit()
        {
            _layout.ToolsRatio = Clamp((double)_workspaceSplit.SplitterDistance / _workspaceSplit.Width, 0.15, 0.85);
            SaveLayout(); LayoutConversationHeader();
        }
        // Honors the user's proportion; only the minimum conversation/panel widths constrain it
        // (the conversation wins when both cannot fit).
        private void ResizeWorkspaceTools()
        {
            if (_workspaceSplit == null || _workspaceSplit.Panel2Collapsed || _workspaceSplit.Width < 180) return;
            int width = _workspaceSplit.Width, splitter = _workspaceSplit.SplitterWidth;
            int distance = (int)(width * Clamp(_layout.ToolsRatio, 0.15, 0.85));
            distance = Math.Min(distance, width - splitter - Math.Min(MinToolsWidth, width / 3));
            distance = Math.Max(distance, Math.Min(MinConversationWidth, width - splitter - Px(160)));
            if (distance < 90 || distance > width - splitter - 20) return;
            _applyingLayout = true;
            try { _workspaceSplit.SplitterDistance = distance; } finally { _applyingLayout = false; }
        }
        private void ApplyTerminalRatio()
        {
            if (_terminalSplit == null || _terminalSplit.Height < 120) return;
            int distance = (int)(_terminalSplit.Height * Clamp(_layout.TerminalRatio, 0.15, 0.85));
            _applyingLayout = true;
            try { _terminalSplit.SplitterDistance = Math.Max(40, Math.Min(_terminalSplit.Height - 40, distance)); } finally { _applyingLayout = false; }
        }
        private void WorkspaceAction(Action action)
        { try { action(); RefreshConversationTree(); } catch (Exception ex) { Progress(ex.Message); } }
        private async Task WorkspaceActionAsync(Func<Task> action)
        { try { await action(); RefreshConversationTree(); } catch (Exception ex) { Progress(ex.Message); } }
        private FridaConversation SelectedTreeConversation() { var node = TreeTarget; return node == null ? null : node.Tag as FridaConversation; }
        private FridaIdeProject SelectedTreeProject() { var node = TreeTarget; return node == null ? null : node.Tag as FridaIdeProject; }

        private void ShowUndoTrash(string conversationId, string projectId)
        {
            _lastTrashedConversation = conversationId; _lastTrashedProject = projectId;
            _trashButton.Visible = false; _undoTrash.Visible = true;
            _undoDelay.Stop(); _undoDelay.Start();
        }
        private void HideUndoTrash()
        {
            _undoDelay.Stop(); _undoTrash.Visible = false; _trashButton.Visible = !_showTrash;
            _lastTrashedConversation = _lastTrashedProject = null;
        }
        private void UndoTrash()
        {
            string c = _lastTrashedConversation, p = _lastTrashedProject;
            HideUndoTrash();
            WorkspaceAction(() =>
            {
                if (c != null) _workspaceStore.Restore(c); else if (p != null) _workspaceStore.RestoreProject(p); else return;
                Progress("Item restaurado da Lixeira.");
            });
        }

        // Rename in place: a box over the row's title. Enter or clicking away saves, Esc cancels;
        // only that row changes (no list rebuild, no reload of the conversation).
        private void BeginInlineRename(TreeNode node)
        {
            if (node == null || node.TreeView != _conversations || _showTrash || !(node.Tag is FridaConversation || node.Tag is FridaIdeProject)) return;
            if (_inlineRename != null) FinishInlineRename(true);
            node.EnsureVisible();
            var c = node.Tag as FridaConversation; var p = node.Tag as FridaIdeProject;
            var box = new TextBox { Text = c != null ? c.Title : p.Name, AccessibleName = c != null ? "Novo título da conversa" : "Novo nome do projeto" };
            var frame = new InputFrame(box);
            int x = Px(36) + node.Level * _conversations.Indent;
            frame.SetBounds(x, node.Bounds.Y + 1, Math.Max(Px(80), _conversations.ClientSize.Width - x - Px(6)), Math.Max(Px(28), node.Bounds.Height - 2));
            _renameNode = node; _inlineRename = frame;
            box.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FinishInlineRename(true); _conversations.Focus(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; FinishInlineRename(false); _conversations.Focus(); }
            };
            box.LostFocus += delegate { if (_inlineRename == frame) BeginInvoke((Action)(() => { if (_inlineRename == frame) FinishInlineRename(true); })); };
            _conversations.Controls.Add(frame);
            frame.BringToFront(); box.Focus(); box.SelectAll();
        }
        private void FinishInlineRename(bool save)
        {
            var frame = _inlineRename; var node = _renameNode;
            if (frame == null) return;
            _inlineRename = null; _renameNode = null;
            string title = frame.Input.Text.Trim();
            _conversations.Controls.Remove(frame); frame.Dispose();
            if (save && title.Length > 0 && node != null)
            {
                var c = node.Tag as FridaConversation; var p = node.Tag as FridaIdeProject;
                try
                {
                    if (c != null && title != c.Title)
                    {
                        _workspaceStore.Rename(c.Id, title); c.Title = title; node.Text = title; node.ToolTipText = TreeTip(node);
                        if (_conversation != null && _conversation.Id == c.Id) { _conversation = _workspaceStore.Get(c.Id); RefreshConversationHeader(); }
                    }
                    else if (p != null && title != p.Name)
                    {
                        _workspaceStore.RenameProject(p.Id, title); p.Name = title; node.Text = title; node.ToolTipText = TreeTip(node);
                        foreach (var listed in _projectList.Where(x => x.Id == p.Id)) listed.Name = title;
                        RefreshConversationHeader();
                    }
                }
                catch (Exception ex) { Progress(ex.Message); }
            }
            if (_treeRefreshPending && !TreeBusy) RefreshConversationTree();
        }
        private FridaIdeProject ConversationProject(FridaConversation c)
        { return c == null || c.ProjectId == null ? null : _projectList.FirstOrDefault(p => p.Id == c.ProjectId); }
        // UI-initiated file operations report to the selected conversation's terminal, like Frida's own.
        private IDisposable EnterUiContext(FridaIdeProject project, string conversationId = null)
        {
            string id = conversationId ?? SelectedConversationId;
            return FridaIdeRuntime.Enter(new FridaIdeContext { ConversationId = id, ProjectId = project == null ? null : project.Id, Project = project, Store = _workspaceStore, Silent = true });
        }
        private string EditorConversation(TabPage tab)
        {
            string id;
            if (tab != null && _editorConversations.TryGetValue(tab, out id) && id != null && _workspaceStore.TryGet(id) != null) return id;
            return SelectedConversationId;
        }
        // Opening a file always lands on Código › Editor.
        private void ShowEditor()
        {
            ShowSection("Código");
            if (!_workspaceCenter.TabPages.Contains(_codeTab)) _workspaceCenter.TabPages.Add(_codeTab);
            _workspaceCenter.SelectedTab = _codeTab; LayoutConversationHeader();
        }
        private bool EditorActive { get { return _currentSection == "Código" && _workspaceCenter.SelectedTab == _codeTab; } }
        // Layout tests render 150/200% sizes larger than the test machine's screen. Form.SetBoundsCore
        // clamps to the screen, so the test resizes through SetWindowPos (bounds then follow WM_WINDOWPOSCHANGED).
        internal static bool AllowOversizeForTests;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        internal void ResizeClientForTests(Size client)
        {
            var outer = SizeFromClientSize(client);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, outer.Width, outer.Height, 0x0002 | 0x0004 | 0x0010); // NOMOVE | NOZORDER | NOACTIVATE
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0024 && AllowOversizeForTests) // WM_GETMINMAXINFO
            {
                var info = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                info.MaxTrackSize = new Point(8000, 6000); info.MaxSize = info.MaxTrackSize;
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, m.LParam, false);
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_workspaceStore != null)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.N: WorkspaceAction(() => NewWorkspaceConversation(false)); return true;
                    case Keys.Control | Keys.Shift | Keys.N: WorkspaceAction(() => NewWorkspaceConversation(true)); return true;
                    case Keys.Control | Keys.K:
                        if (!_sidebar.Visible) ToggleSidebar();
                        _conversationSearch.Focus(); _conversationSearch.SelectAll(); return true;
                    case Keys.Control | Keys.B: ToggleSidebar(); return true;
                    case Keys.Control | Keys.J: SetToolsVisible(_workspaceSplit.Panel2Collapsed); return true;
                    case Keys.Control | Keys.D1: ShowSection("Chat"); return true;
                    case Keys.Control | Keys.D2: ShowSection("Código"); return true;
                    case Keys.Control | Keys.D3: ShowSection("WhatsApp"); return true;
                    case Keys.Control | Keys.D4: ShowSection("Zelda Manager"); return true;
                    case Keys.F2:
                        if (!_conversations.Focused && !(ActiveControl is TextBoxBase)) { RenameCurrentConversation(); return true; }
                        break;
                    case Keys.Control | Keys.S:
                        if (EditorActive) { SaveEditor().ContinueWith(t => { if (t.IsFaulted) Progress(t.Exception.GetBaseException().Message); }, TaskScheduler.FromCurrentSynchronizationContext()); return true; }
                        break;
                    case Keys.F5:
                        if (EditorActive) { RunEditor().ContinueWith(t => { if (t.IsFaulted) Progress(t.Exception.GetBaseException().Message); }, TaskScheduler.FromCurrentSynchronizationContext()); return true; }
                        break;
                    case Keys.Alt | Keys.Z:
                        if (EditorActive) { ToggleEditorWordWrap(); return true; }
                        break;
                    case Keys.Control | Keys.F:
                        if (EditorActive) { var box = _editorPage.Controls.OfType<ActionBar>().SelectMany(b => b.Controls.OfType<InputFrame>()).FirstOrDefault(); if (box != null) { box.Input.Focus(); return true; } }
                        break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        public void OpenLatestConversation()
        {
            string id = null;
            var latest = FridaConversationAudit.LoadLatest();
            if (latest != null)
            {
                id = latest.conversation_id != null && _workspaceStore.TryGet(latest.conversation_id) != null ? latest.conversation_id : _workspaceStore.ConversationForRequest(latest.request_id);
                if (id == null)
                    try { id = _workspaceStore.RecordReport(latest); if (_workspaceStore.TryGet(id) == null) id = null; }
                    catch (Exception ex) { BootLogger.Log("Último atendimento indisponível: " + ex.GetType().Name); }
            }
            if (id == null) { var recent = _workspaceStore.List().FirstOrDefault(); if (recent != null) id = recent.Id; }
            if (id != null) SelectConversation(id); else Progress("Ainda não há conversas registradas.");
        }
        public void OpenLatestMeeting()
        {
            var meeting = _workspaceStore.List().FirstOrDefault(c => c.Kind == FridaConversationKind.Meeting);
            if (meeting != null) { SelectConversation(meeting.Id); return; }
            ShowSection(_workspaceSection == FridaWorkspaceSection.Code ? "Código" : "Chat");
            Progress("Nenhuma reunião registrada. Use Nova conversa › Nova reunião para iniciar uma.");
        }
        private void MoveConversationToProject(FridaConversation c, FridaIdeProject project)
        {
            WorkspaceAction(() =>
            {
                _workspaceStore.AssignProject(c.Id, project == null ? null : project.Id);
                if (project != null)
                {
                    _expandedProjects.Add(project.Id);
                    if (!_projectList.Any(x => x.Id == project.Id)) { _projectList.Add(project); _projects.Items.Add(project); }
                }
                // The open conversation follows: project selector, destination folder and list filter.
                if (_conversation != null && _conversation.Id == c.Id) SelectConversation(c.Id);
                Progress(project == null ? "Conversa removida do projeto." : "Conversa movida para o projeto " + project.Name + ". O histórico dela passa a fazer parte da memória do projeto.");
            });
        }
        // Moving the open conversation follows it to the new section; moving another one only
        // takes it out of this list.
        private void MoveWorkspaceConversation(FridaWorkspaceSection section)
        {
            var c = SelectedTreeConversation(); if (c == null) return;
            bool open = _conversation != null && _conversation.Id == c.Id;
            WorkspaceAction(() =>
            {
                _workspaceStore.Move(c.Id, section);
                if (open) SelectConversation(c.Id);
                else Progress("Conversa movida para " + (section == FridaWorkspaceSection.Code ? "Código" : "Chat") + ".");
            });
        }

        private static string DateBucket(string utc)
        {
            DateTime at;
            if (!DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at)) return "Anteriores";
            var day = at.ToLocalTime().Date; var today = DateTime.Today;
            if (day == today) return "Hoje";
            if (day == today.AddDays(-1)) return "Ontem";
            if (day > today.AddDays(-7)) return "Últimos 7 dias";
            if (day > today.AddDays(-30)) return "Últimos 30 dias";
            return "Anteriores";
        }
        private TreeNode AddGroup(TreeNodeCollection nodes, string title, int count)
        {
            var node = nodes.Add(title); node.Tag = new FridaTreeGroup { Title = title, Count = count };
            return node;
        }
        private void AddByDate(TreeNodeCollection nodes, IEnumerable<FridaConversation> items)
        {
            foreach (var bucket in items.GroupBy(c => DateBucket(c.UpdatedUtc)))
            {
                var group = AddGroup(nodes, bucket.Key, bucket.Count());
                foreach (var c in bucket) group.Nodes.Add(c.Title).Tag = c;
            }
        }
        // Filtered list: Conversas (by date), Projetos (indented conversations) or Reuniões (by date).
        // A search spans the three groups.
        private void ScheduleTreeRefresh() { if (_workspaceStore == null) return; _treeRefreshDelay.Stop(); _treeRefreshDelay.Start(); }
        private static string TreeKey(TreeNode node)
        {
            var c = node.Tag as FridaConversation; if (c != null) return "c:" + c.Id;
            var p = node.Tag as FridaIdeProject; if (p != null) return "p:" + p.Id;
            var g = node.Tag as FridaTreeGroup; return g != null ? "g:" + g.Title : "t:" + node.Text;
        }
        private static void TreeSignature(TreeNodeCollection nodes, System.Text.StringBuilder into)
        {
            foreach (TreeNode node in nodes) { into.Append('[').Append(TreeKey(node)).Append('|').Append(node.Text); TreeSignature(node.Nodes, into); into.Append(']'); }
        }
        private static string TreeSignature(TreeNodeCollection nodes) { var s = new System.Text.StringBuilder(); TreeSignature(nodes, s); return s.ToString(); }
        private static TreeNode FindTreeKey(TreeNodeCollection nodes, string key)
        {
            foreach (TreeNode node in nodes) { if (TreeKey(node) == key) return node; var child = FindTreeKey(node.Nodes, key); if (child != null) return child; }
            return null;
        }
        // Same rows: only the data behind them (times, counts, running state) is updated.
        private static void SyncTree(TreeNodeCollection fresh, TreeNodeCollection shown)
        {
            for (int i = 0; i < fresh.Count; i++) { shown[i].Tag = fresh[i].Tag; shown[i].ToolTipText = fresh[i].ToolTipText; SyncTree(fresh[i].Nodes, shown[i].Nodes); }
        }
        private static string TreeTip(TreeNode node)
        {
            var c = node.Tag as FridaConversation; var p = node.Tag as FridaIdeProject;
            if (c != null) return c.Title + "\nMais ações: ⋯, botão direito ou Shift+F10";
            if (p != null) return p.Name + (string.IsNullOrEmpty(p.Root) ? "" : "\n" + p.Root) + "\nMais ações: ⋯, botão direito ou Shift+F10";
            return null;
        }
        private static void TipTree(TreeNodeCollection nodes) { foreach (TreeNode node in nodes) { node.ToolTipText = TreeTip(node); TipTree(node.Nodes); } }

        // Rebuilt off-screen, then compared: unchanged rows are updated in place (nothing moves or
        // blinks); a changed list replaces the rows keeping the scroll position. While the item menu
        // or a rename box is open the refresh waits for it.
        private void RefreshConversationTree()
        {
            if (_workspaceStore == null) return;
            _treeRefreshDelay.Stop();
            if (TreeBusy) { _treeRefreshPending = true; return; }
            _treeRefreshPending = false;
            var fresh = BuildConversationNodes();
            _changingConversation = true;
            try
            {
                if (TreeSignature(fresh) == TreeSignature(_conversations.Nodes)) { SyncTree(fresh, _conversations.Nodes); _conversations.Invalidate(); }
                else
                {
                    string top = _conversations.TopNode == null ? null : TreeKey(_conversations.TopNode);
                    _conversations.BeginUpdate();
                    try
                    {
                        _conversations.Nodes.Clear();
                        foreach (TreeNode node in fresh.Cast<TreeNode>().ToArray()) { node.Remove(); _conversations.Nodes.Add(node); }
                        if (_conversation != null)
                        {
                            var current = FindConversationNode(_conversations.Nodes, _conversation.Id);
                            if (current != null) { for (var parent = current.Parent; parent != null; parent = parent.Parent) parent.Expand(); _conversations.SelectedNode = current; }
                        }
                        var keep = top == null ? null : FindTreeKey(_conversations.Nodes, top);
                        if (keep != null) _conversations.TopNode = keep;
                    }
                    finally { _conversations.EndUpdate(); }
                }
                // Keep the open conversation highlighted after every refresh.
                if (_conversation != null)
                {
                    var current = FindConversationNode(_conversations.Nodes, _conversation.Id);
                    if (current != null && _conversations.SelectedNode != current) { for (var parent = current.Parent; parent != null; parent = parent.Parent) parent.Expand(); _conversations.SelectedNode = current; }
                }
            }
            finally { _changingConversation = false; }
        }
        private TreeNodeCollection BuildConversationNodes()
        {
            var root = new TreeNode();
            var list = root.Nodes;
            {
                string search = _conversationSearch.Text;
                bool searching = !string.IsNullOrWhiteSpace(search);
                var all = _workspaceStore.List(_workspaceSection, _showTrash, search);
                var projects = _workspaceStore.Projects(_showTrash);
                var projectIds = new HashSet<string>(projects.Select(p => p.Id));
                var counts = all.Where(c => c.ProjectId != null).GroupBy(c => c.ProjectId).ToDictionary(g => g.Key, g => g.Count());
                _conversations.ProjectCount = p => { int n; return counts.TryGetValue(p.Id, out n) ? n : 0; };
                int filter = _filterTabs == null ? 0 : _filterTabs.SelectedIndex;
                Action<TreeNodeCollection, bool> addProjects = (nodes, onlyMatches) =>
                {
                    foreach (var p in projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var children = all.Where(c => c.ProjectId == p.Id).ToList();
                        if (onlyMatches && children.Count == 0 && p.Name.IndexOf(search.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                        var node = nodes.Add(p.Name); node.Tag = p;
                        foreach (var c in children) node.Nodes.Add(c.Title).Tag = c;
                        if (onlyMatches || _expandedProjects.Contains(p.Id) || (_conversation != null && _conversation.ProjectId == p.Id)) node.Expand();
                    }
                };
                if (searching)
                {
                    var chats = all.Where(c => c.Kind != FridaConversationKind.Meeting && (c.ProjectId == null || !projectIds.Contains(c.ProjectId))).ToList();
                    var meetings = all.Where(c => c.Kind == FridaConversationKind.Meeting && (c.ProjectId == null || !projectIds.Contains(c.ProjectId))).ToList();
                    if (chats.Count > 0) { var g = AddGroup(list, "Conversas", chats.Count); foreach (var c in chats) g.Nodes.Add(c.Title).Tag = c; }
                    var pg = AddGroup(list, "Projetos", 0); addProjects(pg.Nodes, true); if (pg.Nodes.Count == 0) pg.Remove(); else ((FridaTreeGroup)pg.Tag).Count = pg.Nodes.Count;
                    if (meetings.Count > 0) { var g = AddGroup(list, "Reuniões", meetings.Count); foreach (var c in meetings) g.Nodes.Add(c.Title).Tag = c; }
                    if (list.Count == 0)
                        list.Add(search.Trim().Length < 3 ? "Nenhum título encontrado. Digite 3 letras ou mais para pesquisar também o conteúdo." : "Nenhum resultado para a pesquisa.");
                }
                else if (filter == 1)
                {
                    addProjects(list, false);
                    if (list.Count == 0) list.Add(_showTrash ? "Nenhum projeto na Lixeira." : "Nenhum projeto ainda. Use a seta ao lado de Nova conversa para abrir uma pasta ou um projeto na VPS.");
                }
                else if (filter == 2)
                {
                    AddByDate(list, all.Where(c => c.Kind == FridaConversationKind.Meeting));
                    if (list.Count == 0) list.Add(_showTrash ? "Nenhuma reunião na Lixeira." : "Nenhuma reunião ainda. Use Nova conversa › Nova reunião.");
                }
                else
                {
                    AddByDate(list, all.Where(c => c.Kind != FridaConversationKind.Meeting && (c.ProjectId == null || !projectIds.Contains(c.ProjectId))));
                    if (list.Count == 0) list.Add(_showTrash ? "Nenhuma conversa na Lixeira." : "Nenhuma conversa ainda. Escreva para a Frida para começar.");
                }
                // Detached nodes remember Expand() and apply it when added to the list.
                foreach (TreeNode node in list)
                {
                    var group = node.Tag as FridaTreeGroup;
                    if (group != null && !_collapsedGroups.Contains(group.Title)) node.Expand();
                }
                TipTree(list);
            }
            return list;
        }
        private static TreeNode FindConversationNode(TreeNodeCollection nodes, string id)
        {
            foreach (TreeNode node in nodes)
            {
                var c = node.Tag as FridaConversation;
                if (c != null && c.Id == id) return node;
                var child = FindConversationNode(node.Nodes, id); if (child != null) return child;
            }
            return null;
        }
        // Switches the sidebar filter to one that lists the conversation (search text is kept).
        private void RevealInFilter(FridaConversation c)
        {
            if (_filterTabs == null || c == null || !string.IsNullOrWhiteSpace(_conversationSearch.Text)) return;
            int filter = _filterTabs.SelectedIndex;
            bool listed = filter == 0 ? c.Kind != FridaConversationKind.Meeting && c.ProjectId == null : filter == 1 ? c.ProjectId != null : c.Kind == FridaConversationKind.Meeting;
            if (listed) return;
            if (c.ProjectId != null) _expandedProjects.Add(c.ProjectId);
            _changingFilter = true;
            try { _filterTabs.SelectedIndex = c.Kind == FridaConversationKind.Meeting && filter != 1 ? 2 : c.ProjectId != null ? 1 : 0; }
            finally { _changingFilter = false; }
        }
        private void SaveWorkspaceDraft()
        {
            _draftSave.Stop();
            if (_workspaceStore == null || _conversation == null || _changingConversation) return;
            try { _workspaceStore.UpdateDraft(_conversation.Id, _draft.Text, _references.Controls.Cast<Control>().Select(c => (string)c.Tag)); }
            catch (Exception ex) { Progress(ex.Message); }
            if (_recordedAudio != null) _conversationAudio[_conversation.Id] = _recordedAudio;
            else _conversationAudio.Remove(_conversation.Id);
        }
        private void ClearSelectedConversation()
        {
            _changingConversation = true;
            try { _conversation = null; FridaIdeRuntime.ActiveConversationId = null; _draft.Clear(); ClearReferences(); _recordedAudio = null; RenderConversation(); }
            finally { _changingConversation = false; }
            RefreshConversationHeader();
        }
        public void SelectConversation(string id)
        {
            if (_voice.IsIdeRecording || _transcribing || _playingDraft) { Progress("Conclua ou descarte a gravação antes de trocar de conversa."); return; }
            SaveWorkspaceDraft(); var c = _workspaceStore.Get(id); _changingConversation = true;
            try
            {
                _conversation = c; _workspaceSection = c.Section; _messagePage = 0;
                FridaIdeRuntime.ActiveConversationId = c.Id;
                var p = ConversationProject(c);
                if (p == null && c.ProjectId != null)
                {
                    p = _workspaceStore.Projects().FirstOrDefault(x => x.Id == c.ProjectId);
                    if (p != null) { _projectList.Add(p); _projects.Items.Add(p); }
                }
                _projects.SelectedItem = p == null ? _projects.Items[0] : p;
                _draft.Text = c.Draft ?? ""; ClearReferences(); foreach (string path in c.Attachments ?? new List<string>()) AddReference(path);
                _conversationAudio.TryGetValue(c.Id, out _recordedAudio);
                RenderConversation(); RefreshWorkspaceArtifacts(); RefreshWorkspaceTerminals(); ShowSection(c.Section == FridaWorkspaceSection.Code ? "Código" : "Chat");
                if (_workspaceCenter.TabPages.Contains(_conversationTab)) _workspaceCenter.SelectedTab = _conversationTab;
            }
            finally { _changingConversation = false; }
            RevealInFilter(c);
            RefreshConversationTree(); RefreshConversationHeader();
        }
        private void NewWorkspaceConversation(bool meeting)
        {
            SaveWorkspaceDraft(); string title = meeting ? Prompt("Pauta da reunião", "Nova reunião") : "Nova conversa";
            if (title == null) return;
            // A project node, or a conversation listed inside a project, scopes the new item to it.
            var node = TreeTarget; var p = node == null ? null : node.Tag as FridaIdeProject;
            if (p == null && node != null && node.Parent != null) p = node.Parent.Tag as FridaIdeProject;
            if (p != null && p.DeletedUtc != null) p = null;
            SelectConversation(_workspaceStore.Create(title, _workspaceSection, meeting ? FridaConversationKind.Meeting : FridaConversationKind.Conversation, p == null ? null : p.Id).Id);
            _draft.Focus();
        }
        // Registers a local or VPS project in the workspace catalog and opens a conversation in it.
        private FridaIdeProject RegisterWorkspaceProject(string name, string root, string sshAlias)
        {
            var stored = _workspaceStore.RegisterProject(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root.TrimEnd('\\', '/')) : name.Trim(), root, string.IsNullOrEmpty(sshAlias) ? null : sshAlias);
            var listed = _projectList.FirstOrDefault(x => x.Id == stored.Id);
            if (listed == null) { listed = stored; _projectList.Add(listed); _projects.Items.Add(listed); }
            _expandedProjects.Add(listed.Id);
            SelectConversation(_workspaceStore.Create("Nova conversa", _workspaceSection, FridaConversationKind.Conversation, listed.Id).Id);
            return listed;
        }
        private const string InterruptedRound = "Rodada interrompida ou sem resposta registrada. Ela não será reexecutada automaticamente; envie novamente se quiser continuar.";
        private string _renderedConversationId;
        private int _renderedPage = -1;
        private readonly List<string> _renderedKeys = new List<string>();

        // Re-renders the open conversation. When the stored page only grew (a new reply, a meeting
        // contribution) the existing bubbles stay and only the new ones are added; otherwise the
        // transcript is rebuilt with painting suspended and laid out once.
        private void RenderConversation()
        {
            var items = new List<KeyValuePair<string, FridaConversationMessage>>();
            bool older = false, newer = false;
            if (_conversation != null)
            {
                int count = _workspaceStore.MessageCount(_conversation.Id);
                int offset = Math.Max(0, count - (_messagePage + 1) * 100);
                var page = _workspaceStore.Messages(_conversation.Id, offset, Math.Min(100, Math.Max(1, count - offset)));
                bool newest = offset + page.Count >= count, running = _workspaceStore.IsRunning(_conversation.Id);
                older = offset > 0; newer = _messagePage > 0;
                var answered = new HashSet<string>(page.Where(m => m.RequestId != null && (m.Role == "assistant" || m.Role == "report" || m.Role == "decision")).Select(m => m.RequestId));
                var lastUser = running && newest ? page.LastOrDefault(m => m.Role == "user") : null;
                if (lastUser != null && lastUser.RequestId != null) answered.Add(lastUser.RequestId); // still being answered
                for (int i = 0; i < page.Count; i++)
                {
                    var m = page[i];
                    items.Add(new KeyValuePair<string, FridaConversationMessage>(m.Id ?? "at:" + (offset + i), m));
                    // A round without a recorded reply stays identified after restart; it is never re-run automatically.
                    bool lastOfOlderPage = i == page.Count - 1 && !newest;
                    if (m.Role == "user" && m.RequestId != null && !answered.Contains(m.RequestId) && !lastOfOlderPage)
                        items.Add(new KeyValuePair<string, FridaConversationMessage>("interrupted:" + m.RequestId, null));
                }
            }
            string id = _conversation == null ? null : _conversation.Id;
            bool append = id != null && id == _renderedConversationId && _messagePage == _renderedPage && older == _loadOlder.Visible && newer == _loadNewer.Visible
                && _renderedKeys.Count > 0 && _messages.Count == _renderedKeys.Count && items.Count >= _renderedKeys.Count
                && _renderedKeys.SequenceEqual(items.Take(_renderedKeys.Count).Select(item => item.Key));
            if (append)
            {
                if (items.Count == _renderedKeys.Count) { RefreshComposer(); return; }
                bool follow = NearLatest(); int first = _messages.Count;
                _bulkMessages = true;
                try { foreach (var item in items.Skip(_renderedKeys.Count)) { AddRenderItem(item); _renderedKeys.Add(item.Key); } }
                finally { _bulkMessages = false; }
                LayoutMessages(first);
                if (follow) ScrollToLatest(); else _jump.Visible = true;
                RefreshComposer();
                return;
            }
            using (FridaTheme.Freeze(_historyView))
            {
                foreach (var m in _messages) m.Card.Dispose(); _messages.Clear(); _renderedKeys.Clear();
                _loadOlder.Visible = older; _loadNewer.Visible = newer;
                _bulkMessages = true;
                try { foreach (var item in items) { AddRenderItem(item); _renderedKeys.Add(item.Key); } }
                finally { _bulkMessages = false; }
                _renderedConversationId = id; _renderedPage = _messagePage;
                _messageWidth = 0;
                LayoutChat(); RefreshComposer();
                ScrollToLatest();
            }
        }
        private void AddRenderItem(KeyValuePair<string, FridaConversationMessage> item)
        {
            if (item.Value != null) AddMessage(item.Value);
            else AddMessage("Atendimento", InterruptedRound);
        }
        private void RefreshWorkspaceTerminals()
        {
            _motorTerminal.Clear(); _terminal.Clear(); _terminalFiles.Items.Clear();
            if (_conversation == null) return;
            foreach (var e in _workspaceStore.Diagnostics(_conversation.Id))
                (e.Kind == "motor" ? _motorTerminal : _terminal).AppendText(e + "\r\n");
            foreach (var file in FridaIdeRuntime.Artifacts().Where(a => a.ConversationId == _conversation.Id)) _terminalFiles.Items.Add(file);
        }
        private void StopWorkspaceExecution()
        {
            if (_workspaceStore == null) { _stop(); return; }
            string id = SelectedConversationId;
            if (id == null || !_workspaceStore.IsRunning(id)) { Progress("Não há execução ativa nesta conversa."); return; }
            if (_executionCancellation != null && _editorExecutionConversationId == id) _executionCancellation.Cancel();
            else _stop();
        }
        private void RefreshWorkspaceArtifacts()
        {
            string selected = _meetingArtifacts.SelectedItem is FridaMeetingArtifact ? ((FridaMeetingArtifact)_meetingArtifacts.SelectedItem).Id : null;
            _meetingArtifacts.Items.Clear();
            if (_conversation != null) foreach (var artifact in _workspaceStore.Artifacts(_conversation.Id)) _meetingArtifacts.Items.Add(artifact);
            if (_meetingArtifacts.Items.Count > 0) {
                _meetingArtifacts.SelectedIndex = 0;
                for (int i = 0; i < _meetingArtifacts.Items.Count; i++) if (((FridaMeetingArtifact)_meetingArtifacts.Items[i]).Id == selected) { _meetingArtifacts.SelectedIndex = i; break; }
            }
            else { _artifactPreview.Clear(); _artifactPreview.SelectionColor = Muted; _artifactPreview.AppendText("Ainda não há planos, fluxos ou skills registrados nesta conversa."); }
        }
        private bool ShowWorkspaceSection(ref string name)
        {
            if (_workspaceHost == null) return false;
            string tool = name == "Terminal" ? "Terminais" : name == "Arquivos" ? "Arquivos" : name == "Planos" ? "Artefatos" : null;
            if (tool != null) { name = _workspaceSection == FridaWorkspaceSection.Code ? "Código" : "Chat"; if (_workspaceSplit.Panel2Collapsed) SetToolsVisible(true); foreach (TabPage tab in _workspaceTools.TabPages) if (tab.Text == tool) _workspaceTools.SelectedTab = tab; }
            if (name == "Histórico" || name == "Projetos" || name == "Reuniões") name = _workspaceSection == FridaWorkspaceSection.Code ? "Código" : "Chat";
            if (name != "Chat" && name != "Código") return false;
            var before = _workspaceSection;
            _workspaceSection = name == "Código" ? FridaWorkspaceSection.Code : FridaWorkspaceSection.Chat;
            if (_workspaceSection == FridaWorkspaceSection.Code && !_workspaceCenter.TabPages.Contains(_codeTab)) _workspaceCenter.TabPages.Add(_codeTab);
            if (_workspaceSection == FridaWorkspaceSection.Chat) _workspaceCenter.TabPages.Remove(_codeTab);
            // The list only changes with the section (opening a conversation refreshes it once itself).
            if (before != _workspaceSection || _conversations.Nodes.Count == 0) RefreshConversationTree();
            ResizeWorkspaceTools(); return true;
        }
    }
}
