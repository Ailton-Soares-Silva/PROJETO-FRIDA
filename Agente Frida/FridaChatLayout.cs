using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;
using System.Windows.Forms;

namespace FridaAgent
{
    public sealed partial class FridaIdeForm
    {
        private static readonly Color Canvas = FridaTheme.Canvas;
        private static readonly Color Surface = FridaTheme.Surface;
        private static readonly Color Bubble = FridaTheme.Bubble;
        private static readonly Color Edge = FridaTheme.Edge;
        private static readonly Color Ink = FridaTheme.Ink;
        private static readonly Color Muted = FridaTheme.Muted;
        private static readonly Color Accent = FridaTheme.Accent;
        private static readonly Color Highlight = FridaTheme.Highlight;
        private static readonly Color Coral = FridaTheme.Coral;

        // Navigation rail order is also the Ctrl+1..4 order.
        private static readonly Dictionary<string, string> SectionIcons = new Dictionary<string, string>
        {
            { "Chat", FridaTheme.GlyphChat }, { "Código", FridaTheme.GlyphCode }, { "WhatsApp", FridaTheme.GlyphPhone }, { "Zelda Manager", FridaTheme.GlyphChart }
        };

        private readonly ToolTip _tips = new ToolTip();
        private readonly ContextMenuStrip _options = new ContextMenuStrip();
        private string _currentSection = "Chat";
        private readonly Panel _chatPage = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        private readonly Panel _historyView = new HistoryPanel { AutoScroll = true, AccessibleName = "Mensagens da conversa" };
        private readonly Panel _thread = new BufferedPanel();
        private readonly Label _thinking = new Label { Text = "Frida está trabalhando", AutoSize = false, Visible = false, TextAlign = ContentAlignment.MiddleLeft, AccessibleRole = AccessibleRole.StatusBar };
        private readonly Timer _thinkingTimer = new Timer { Interval = 450 };
        private readonly ContextMenuStrip _messageMenu = new ContextMenuStrip();
        private string _busyConversationId;
        private bool _bulkMessages, _thinkingShown;
        private int _thinkingDots;
        private readonly FridaWelcomeView _welcome = new FridaWelcomeView();
        private readonly RoundedPanel _composer = new RoundedPanel { Fill = Surface, Radius = 16, Stroke = Edge, BackColor = Canvas };
        private readonly Label _notice = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AccessibleRole = AccessibleRole.StatusBar, LiveSetting = AutomationLiveSetting.Polite };
        private readonly Label _referenceHint = new Label { ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
        // Attachments as chips that wrap onto at most two visible rows.
        private readonly FlowLayoutPanel _references = new FlowLayoutPanel { AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        // Draft persistence waits for a pause in typing instead of rewriting the catalog on every key.
        private readonly Timer _draftSave = new Timer { Interval = 500 };
        private readonly HashSet<string> _pastedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _composerHover;
        private int _draftLines = -1;
        // Last paste turned into an attachment, undone by Ctrl+Z while the text is unchanged.
        private Control _lastPaste; private string _draftAtPaste, _pasteText; private int _pasteCaret;
        // Pastes above either limit become an attachment chip instead of flooding the box.
        internal const int PasteAttachmentChars = 2000, PasteAttachmentLines = 40;
        private readonly List<MessageView> _messages = new List<MessageView>();
        private IconButton _brand, _settingsButton, _clip, _mic, _sendButton, _listen, _discard, _jump;
        private PillButton _loadOlder, _loadNewer;
        private bool _chatReady, _layingOut, _playingDraft;
        private int _messageWidth;

        private int Px(int value) { return FridaTheme.Px(value); }

        private static GraphicsPath Outline(RectangleF rect, float radius) { return FridaTheme.Round(rect, radius); }

        private class RoundedPanel : Panel
        {
            internal Color Fill, Stroke = Color.Transparent;
            // Optional role color drawn as a 3 px bar along the left edge (team members).
            internal Color Bar = Color.Empty;
            internal int Radius;
            internal bool FocusRing = true;
            // Stroke width in 100%-scale pixels and an optional soft halo outside it (focus state).
            internal float StrokeWidth = 1;
            internal Color Glow = Color.Empty;
            internal RoundedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (Width < 3 || Height < 3) return;
                float scale = FridaTheme.Factor, stroke = Math.Max(1, StrokeWidth * scale), inset = Glow.A > 0 ? 3 * scale : Math.Max(1, stroke / 2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Outline(new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), Radius * scale))
                using (var brush = new SolidBrush(Fill))
                using (var pen = new Pen(FocusRing && ContainsFocus && Stroke != Color.Transparent ? Highlight : Stroke, stroke))
                {
                    if (Glow.A > 0) using (var halo = new Pen(Glow, stroke + 4 * scale)) e.Graphics.DrawPath(halo, path);
                    e.Graphics.FillPath(brush, path);
                    if (Bar.A > 0)
                    {
                        var clip = e.Graphics.Clip; e.Graphics.SetClip(path);
                        using (var bar = new SolidBrush(Bar)) e.Graphics.FillRectangle(bar, 1, 1, Math.Max(2, 3 * scale), Height - 2);
                        e.Graphics.Clip = clip;
                    }
                    e.Graphics.DrawPath(pen, path);
                }
            }
        }

        // History scroller that never jumps to show a clicked message whole (WinForms scrolls the
        // focused child into view by default), double-buffered so scrolling does not tear.
        private sealed class HistoryPanel : Panel
        {
            internal HistoryPanel() { DoubleBuffered = true; }
            protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }
        }
        private sealed class BufferedPanel : Panel
        {
            internal BufferedPanel() { DoubleBuffered = true; }
        }

        // Composer text box. The placeholder is painted by the box itself, so it never covers the
        // caret (a separate label used to). Every paste path (Ctrl+V, Shift+Insert, context menu)
        // arrives as WM_PASTE and can be turned into an attachment. The vertical scrollbar exists
        // from creation and is only shown or hidden (changing ScrollBars would recreate the window,
        // flashing and losing the undo history).
        private sealed class ComposerTextBox : TextBox
        {
            [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
            [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref RECT rect);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);
            // Replaces the selection keeping Ctrl+Z (TextBox.SelectedText clears the undo history).
            internal void InsertUndoable(string text) { SendMessage(Handle, 0x00C2, (IntPtr)1, text ?? ""); }
            [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
            private string _placeholder = "Escreva para a Frida…";
            private bool _bar;
            internal Func<bool> PasteHandler;
            internal string Placeholder { get { return _placeholder; } set { if (_placeholder == value) return; _placeholder = value; if (TextLength == 0) Invalidate(); } }
            internal bool VerticalBarShown { get { return _bar; } }
            internal void ShowVerticalBar(bool show)
            {
                if (show == _bar) return;
                _bar = show;
                if (IsHandleCreated) ShowScrollBar(Handle, 1, show);
            }
            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ShowScrollBar(Handle, 1, _bar); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0302 && !ReadOnly && PasteHandler != null && PasteHandler()) return; // WM_PASTE handled as attachment
                base.WndProc(ref m);
                if (m.Msg == 0x000F && TextLength == 0 && !string.IsNullOrEmpty(_placeholder)) // WM_PAINT
                {
                    var r = new RECT(); SendMessage(Handle, 0x00B2, IntPtr.Zero, ref r); // EM_GETRECT: where text starts
                    using (var g = CreateGraphics())
                        TextRenderer.DrawText(g, _placeholder, Font, new Rectangle(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Font.Height + 2), Muted, BackColor,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }
            }
        }
        private ComposerTextBox Composer { get { return (ComposerTextBox)_draft; } }

        private sealed class IconButton : Button
        {
            internal string Glyph;
            internal bool Selected, Primary, Hover, HideCaption;
            internal IconButton()
            {
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
                Margin = Padding.Empty; ForeColor = Ink;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = false; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                float scale = FridaTheme.Factor;
                int inset = Math.Max(1, (int)(2 * scale));
                if (Width <= 2 * inset || Height <= 2 * inset) return;
                Color fill = Primary && Enabled ? Accent : Selected ? Bubble : Hover && Enabled ? FridaTheme.Hover : BackColor;
                if (Primary && !Enabled) fill = Edge;
                Color ink = !Enabled ? Muted : Primary ? Canvas : Selected ? Highlight : ForeColor;
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Outline(new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), (Primary ? Math.Min(20, Height / 2f / scale) : 10) * scale))
                using (var brush = new SolidBrush(fill))
                {
                    e.Graphics.FillPath(brush, path);
                    if (Focused && ShowFocusCues)
                        using (var pen = new Pen(Primary ? Canvas : Highlight, 2 * scale)) e.Graphics.DrawPath(pen, path);
                }
                bool caption = !HideCaption && Text.Length > 0;
                int iconWidth = caption ? (int)(42 * scale) : Width;
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(e.Graphics, Glyph, FridaTheme.Icons(12), new Rectangle(0, 0, iconWidth, Height), ink, flags | TextFormatFlags.HorizontalCenter);
                if (caption)
                    TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(iconWidth, 0, Math.Max(1, Width - iconWidth - inset), Height), ink, flags | TextFormatFlags.EndEllipsis);
                if (Selected)
                    using (var pen = new Pen(Highlight, Math.Max(2, 3 * scale))) e.Graphics.DrawLine(pen, inset, Height / 3, inset, Height * 2 / 3);
            }
        }

        private IconButton ActionButton(string name, string glyph, Action action, Color background, bool caption)
        {
            var button = new IconButton { Glyph = glyph, Text = name, HideCaption = !caption, AccessibleName = name, BackColor = background };
            FridaTheme.Tip(button, name);
            button.Click += delegate { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } };
            return button;
        }

        private void BuildShell(Action voiceOff)
        {
            FridaTheme.DarkChrome(this);
            foreach (Control control in new Control[] { _draft, _historyView, _references })
                FridaTheme.DarkScroll(control);
            _navigation.BackColor = Surface;
            // Rail: sidebar toggle on top, sections as icons (tooltips carry names and shortcuts), settings at the bottom.
            _brand = ActionButton("Mostrar ou ocultar a lista (Ctrl+B)", FridaTheme.GlyphMenu, delegate { ToggleSidebar(); }, Surface, false);
            _navigation.Controls.Add(_brand);
            _options.Items.Add("Voz em OFF", null, delegate { voiceOff(); });
            FridaTheme.Style(_options);
            _settingsButton = ActionButton("Opções", FridaTheme.GlyphSettings, delegate { _options.Show(_settingsButton, new Point(_settingsButton.Width, 0)); }, Surface, false);
            _navigation.Controls.Add(_settingsButton);
            var main = new Panel { Dock = DockStyle.Fill };
            main.Controls.Add(_body);
            Controls.Add(main); Controls.Add(_navigation); Controls.Add(_status);
            _status.BackColor = Surface; _status.ForeColor = Muted; _status.Font = FridaTheme.Font(9.5f); _status.Height = Px(28);
            _navigation.ClientSizeChanged += delegate { SizeNavigationButtons(); };
            Resize += delegate { RefreshShell(); };
        }

        private void RefreshShell()
        {
            RefreshNavigation(); LayoutChat();
            ApplyResponsiveLayout();
        }

        private void AddNavigationButton(string title)
        {
            int index = SectionIcons.Keys.ToList().IndexOf(title) + 1;
            var button = ActionButton(title + (index > 0 ? " (Ctrl+" + index + ")" : ""), SectionIcons[title], () => ShowSection(title), Surface, false);
            button.AccessibleName = title;
            _sectionButtons.Add(title, button); _navigation.Controls.Add(button);
        }

        private void RefreshNavigation()
        {
            if (_brand == null) return;
            _navigation.Width = Px(60);
            foreach (var item in _sectionButtons)
            {
                var button = (IconButton)item.Value;
                button.HideCaption = true;
                button.Selected = item.Key == _currentSection;
                button.AccessibleDescription = button.Selected ? "Seção atual" : null;
                button.Invalidate();
            }
            SizeNavigationButtons();
        }

        private void SizeNavigationButtons()
        {
            if (_brand == null) return;
            int width = _navigation.ClientSize.Width, size = Px(44), x = (width - size) / 2, y = Px(10);
            _brand.SetBounds(x, y, size, size); y += size + Px(18);
            foreach (string title in SectionIcons.Keys)
            {
                Button button;
                if (!_sectionButtons.TryGetValue(title, out button)) continue;
                button.SetBounds(x, y, size, size); y += size + Px(6);
            }
            _settingsButton.SetBounds(x, Math.Max(y, _navigation.ClientSize.Height - size - Px(12)), size, size);
        }

        private Panel BuildChat()
        {
            _draft.Font = FridaTheme.Font(11.5f);
            _draft.ForeColor = Ink; _draft.BackColor = Surface; _draft.MaxLength = int.MaxValue;
            Composer.PasteHandler = PasteIntoComposer;
            _draft.KeyDown += (sender, e) => { if (e.KeyData == (Keys.Control | Keys.Z) && UndoPasteAttachment()) e.SuppressKeyPress = true; };
            // The whole frame is the text field: I-beam everywhere and a click anywhere focuses the box.
            _composer.Cursor = Cursors.IBeam; _composer.FocusRing = false; _composer.Stroke = FridaTheme.EdgeStrong;
            _composer.MouseDown += delegate { _draft.Focus(); };
            foreach (Control part in new Control[] { _composer, _draft, _references, _referenceHint })
            {
                part.MouseEnter += delegate { SetComposerHover(true); };
                part.MouseLeave += delegate { SetComposerHover(_composer.ClientRectangle.Contains(_composer.PointToClient(Cursor.Position))); };
            }
            _draftSave.Tick += delegate { _draftSave.Stop(); SaveWorkspaceDraft(); };
            // Closing within the half-second pause still keeps what was typed.
            _cleanup.Add(() => { if (_draftSave.Enabled) { try { SaveWorkspaceDraft(); } catch (Exception ex) { BootLogger.Log("Rascunho não salvo ao fechar: " + ex.GetType().Name); } } _draftSave.Dispose(); });
            _welcome.Font = FridaTheme.Strong(20);
            _notice.Font = FridaTheme.Font(9.5f); _referenceHint.Font = FridaTheme.Font(9);

            _historyView.Controls.Add(_thread);
            // Messages wrap to the column width, so the history only ever scrolls vertically.
            _historyView.HorizontalScroll.Enabled = false;
            _historyView.ClientSizeChanged += delegate { if (!_layingOut) LayoutChat(); };
            _clip = ActionButton("Referenciar arquivo", "", PickReferences, Surface, false);
            _mic = ActionButton("Gravar áudio", "", ToggleRecording, Surface, false);
            _sendButton = ActionButton("Enviar", "", SendOrStop, Surface, false);
            _sendButton.Primary = true;
            _listen = ActionButton("Ouvir áudio", "", PlayDraft, Surface, true);
            _listen.Font = FridaTheme.Font(10);
            _discard = ActionButton("Descartar áudio", "", DiscardRecording, Surface, false);
            _jump = ActionButton("Ir para a última mensagem", "", ScrollToLatest, Surface, true);
            _jump.Font = FridaTheme.Font(10);
            _jump.Visible = false;
            _loadOlder = new PillButton("Carregar mensagens anteriores", PillKind.Secondary, FridaTheme.GlyphHistory, true) { Visible = false };
            _loadOlder.Click += delegate { _messagePage++; RenderConversation(); };
            _loadNewer = new PillButton("Voltar às mensagens recentes", PillKind.Secondary, FridaTheme.GlyphChevronDown, true) { Visible = false };
            _loadNewer.Click += delegate { _messagePage = 0; RenderConversation(); ScrollToLatest(); };
            _thread.Controls.Add(_loadOlder); _thread.Controls.Add(_loadNewer);
            _thinking.Font = FridaTheme.Font(10, FontStyle.Italic); _thinking.ForeColor = Muted; _thinking.BackColor = Canvas;
            _thread.Controls.Add(_thinking);
            _thinkingTimer.Tick += delegate { _thinkingDots = (_thinkingDots + 1) % 4; _thinking.Text = "Frida está trabalhando" + new string('.', _thinkingDots); };
            BuildMessageMenu();
            _cleanup.Add(() => { _thinkingTimer.Stop(); _thinkingTimer.Dispose(); _messageMenu.Dispose(); });
            _composer.Controls.AddRange(new Control[] { _references, _referenceHint, _draft, _clip, _listen, _discard, _mic, _sendButton });
            _references.BackColor = _referenceHint.BackColor = Surface;
            _references.Padding = Padding.Empty;
            _chatPage.Controls.AddRange(new Control[] { _historyView, _welcome, _notice, _composer, _jump });
            _jump.BringToFront();
            _welcome.SendToBack();
            _welcome.PromptSelected += prompt =>
            {
                _draft.Text = prompt;
                _draft.SelectionStart = _draft.TextLength;
                _draft.Focus();
                RefreshComposer();
            };
            _draft.TextChanged += delegate { OnDraftChanged(); };
            _composer.Enter += delegate { UpdateComposerFrame(); };
            _composer.Leave += delegate { UpdateComposerFrame(); };
            _chatPage.Resize += delegate { LayoutChat(); };
            _historyView.Scroll += delegate { if (NearLatest()) _jump.Visible = false; };
            _projects.SelectedIndexChanged += delegate { RefreshComposer(); };
            _status.TextChanged += delegate
            {
                _notice.Text = _status.Text; _notice.AccessibleName = _status.Text;
                FridaTheme.Tip(_notice, _status.Text);
                LayoutChat();
                if (_notice.Visible && _notice.IsHandleCreated && (!_voice.IsIdeRecording || _recordingSeconds == 0))
                    _notice.AccessibilityObject.RaiseLiveRegionChanged();
            };
            _recordingTimer.Tick += async delegate
            {
                _recordingSeconds++;
                _status.Text = "Gravando · " + TimeSpan.FromSeconds(_recordingSeconds).ToString(@"mm\:ss");
                if (_recordingSeconds >= 300) await FinishRecording();
            };
            _chatReady = true;
            RefreshComposer();
            return _chatPage;
        }

        private bool HasDraft { get { return !string.IsNullOrWhiteSpace(_draft.Text) || _references.Controls.Count > 0; } }

        private void SetAction(IconButton button, string name, string glyph)
        {
            if (button.Text == name) return;
            button.Text = button.AccessibleName = name; button.Glyph = glyph;
            FridaTheme.Tip(button, name); button.Invalidate();
        }

        private async void ToggleRecording()
        {
            try { if (_voice.IsIdeRecording) await FinishRecording(); else BeginRecording(); }
            catch (Exception ex) { _status.Text = ex.Message; RefreshComposer(); }
        }

        private async void PlayDraft()
        {
            if (_recordedAudio == null || _playingDraft) return;
            _playingDraft = true; RefreshComposer();
            try { await _voice.PlayIdeRecordingAsync(_recordedAudio); }
            catch (Exception ex) { if (!_disposingIde) _status.Text = ex.Message; }
            finally { _playingDraft = false; if (!_disposingIde) RefreshComposer(); }
        }

        private async void SendOrStop()
        {
            if (_busy) StopWorkspaceExecution(); else await SendDraft();
        }

        private void RefreshComposer()
        {
            if (!_chatReady || _disposingIde) return;
            bool recording = _voice.IsIdeRecording;
            _clip.Enabled = !_busy;
            _references.Enabled = !_busy;
            _draft.ReadOnly = _busy;
            Composer.Placeholder = _busy ? "Frida está respondendo… use ■ para interromper" : recording ? "Gravando… toque no microfone para parar" : "Escreva para a Frida…";
            _discard.Visible = recording || _transcribing || _recordedAudio != null;
            _discard.Enabled = !_busy && !_playingDraft;
            _listen.Visible = _recordedAudio != null && !_transcribing;
            _listen.Enabled = !_busy && !_playingDraft;
            _mic.Enabled = !_busy && !_transcribing && !_playingDraft;
            _mic.ForeColor = recording ? Coral : Ink;
            SetAction(_mic, recording ? "Parar gravação" : _recordedAudio == null ? "Gravar áudio" : "Gravar novamente", recording ? "" : "");
            SetAction(_sendButton, _busy ? "Interromper" : "Enviar", _busy ? "" : "");
            _sendButton.Enabled = _busy || (HasDraft && !recording && !_transcribing && !_playingDraft);
            bool files = _references.Controls.Cast<Control>().Any(c => !IsPasted((string)c.Tag)), pasted = _references.Controls.Count > 0 && !files;
            _referenceHint.Text = pasted ? "O texto colado vai junto com a mensagem"
                : SelectedProject != null && !string.IsNullOrEmpty(SelectedProject.SshAlias) ? "Arquivo do computador · O caminho será incluído na mensagem" : "O caminho será incluído na mensagem";
            RefreshConversationHeader();
            UpdateThinking();
            UpdateComposerFrame();
            LayoutChat();
        }

        // Typing only touches what a keystroke can change: the send button, the height when the
        // line count changes, and a delayed draft save (no catalog write or list repaint per key).
        private void OnDraftChanged()
        {
            if (!_chatReady || _disposingIde) return;
            bool recording = _voice.IsIdeRecording;
            _sendButton.Enabled = _busy || (HasDraft && !recording && !_transcribing && !_playingDraft);
            if (DraftLines() != _draftLines) LayoutChat();
            if (_workspaceStore != null && !_changingConversation) { _draftSave.Stop(); _draftSave.Start(); }
        }
        private int DraftLines() { return _draft.IsHandleCreated ? _draft.GetLineFromCharIndex(_draft.TextLength) + 1 : Math.Max(1, _draft.Lines.Length); }

        private void SetComposerHover(bool hover)
        {
            if (_composerHover == hover) return;
            _composerHover = hover; UpdateComposerFrame();
        }
        // Idle: strong border. Hover: lighter border. Focus: 2 px highlight ring with a soft halo, so
        // it is obvious the box is ready for typing. Busy: muted border. Recording: coral ring.
        private void UpdateComposerFrame()
        {
            if (!_chatReady) return;
            bool focus = _composer.ContainsFocus, recording = _voice.IsIdeRecording;
            Color stroke = recording ? Coral : _busy ? FridaTheme.Divider : focus ? Highlight : _composerHover ? Color.FromArgb(104, 140, 182) : FridaTheme.EdgeStrong;
            float width = recording || focus ? 2 : 1;
            Color glow = focus && !recording && !_busy ? Color.FromArgb(48, Highlight) : Color.Empty;
            if (_composer.Stroke == stroke && _composer.StrokeWidth == width && _composer.Glow == glow) return;
            _composer.Stroke = stroke; _composer.StrokeWidth = width; _composer.Glow = glow; _composer.Invalidate();
        }

        // "Frida está trabalhando…" under the last message while this conversation's request runs.
        private void UpdateThinking()
        {
            bool show = _busy && _conversation != null && _busyConversationId == _conversation.Id;
            if (show == _thinkingShown) return;
            _thinkingShown = show; _thinking.Visible = show; _thinkingDots = 0; _thinking.Text = "Frida está trabalhando";
            if (show) _thinkingTimer.Start(); else _thinkingTimer.Stop();
            bool follow = NearLatest();
            LayoutMessages(Math.Max(0, _messages.Count - 1));
            if (show && follow) ScrollToLatest();
        }

        private void PickReferences()
        {
            using (var dialog = new OpenFileDialog { Title = "Referenciar arquivo do computador", Multiselect = true, CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    foreach (string path in dialog.FileNames) AddReference(path);
            _draft.Focus();
        }

        // Attachment chip: icon, name (or "Texto colado · tamanho") and a remove button; a pasted
        // text chip opens a menu to view it or put it back into the box.
        private sealed class AttachmentChip : Panel
        {
            internal readonly PillButton Remove;
            private readonly string _title, _detail, _glyph;
            internal AttachmentChip(string path, string title, string detail, bool pasted)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                Tag = path; _title = title; _detail = detail; _glyph = pasted ? FridaTheme.GlyphDocument : FridaTheme.GlyphLink;
                BackColor = FridaTheme.SurfaceRaised; Height = FridaTheme.Px(36); Margin = new Padding(0, 0, FridaTheme.Px(6), FridaTheme.Px(6));
                AccessibleName = pasted ? title + " · " + detail : "Arquivo " + title; AccessibleDescription = path; AccessibleRole = AccessibleRole.Grouping;
                Remove = new PillButton("", PillKind.Icon, FridaTheme.GlyphClose, true) { AccessibleName = "Remover " + title };
                FridaTheme.Tip(Remove, "Remover anexo");
                Controls.Add(Remove);
                string text = title + (detail == null ? "" : " · " + detail);
                Width = Math.Min(FridaTheme.Px(360), TextRenderer.MeasureText(text, FridaTheme.Font(9.5f)).Width + FridaTheme.Px(36 + 36 + 4));
                FridaTheme.Tip(this, pasted ? text + " · clique para ver ou inserir no texto" : path);
                Cursor = pasted ? Cursors.Hand : Cursors.Default;
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (Remove == null) return; // the constructor sizes the chip before creating the button
                int s = FridaTheme.Px(32); Remove.SetBounds(Width - s - FridaTheme.Px(2), (Height - s) / 2, s, s);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(FridaTheme.Effective(Parent));
                FridaTheme.FillRound(e.Graphics, new Rectangle(0, 0, Width, Height), Height / 2, FridaTheme.SurfaceRaised, FridaTheme.EdgeStrong);
                int x = FridaTheme.Px(12);
                FridaTheme.Text(e.Graphics, _glyph, FridaTheme.Icons(9.5f), new Rectangle(x, 0, FridaTheme.Px(18), Height), Highlight, TextFormatFlags.VerticalCenter);
                x += FridaTheme.Px(24);
                FridaTheme.Text(e.Graphics, _title + (_detail == null ? "" : " · " + _detail), FridaTheme.Font(9.5f), new Rectangle(x, 0, Math.Max(1, Remove.Left - x - FridaTheme.Px(2)), Height), Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private void AddReference(string path)
        {
            if (_references.Controls.Cast<Control>().Any(c => string.Equals((string)c.Tag, path, StringComparison.OrdinalIgnoreCase))) return;
            bool pasted = IsPasted(path);
            string title = pasted ? "Texto colado" : Path.GetFileName(path), detail = null;
            if (pasted) { try { detail = PasteSize(File.ReadAllText(path)); } catch (IOException) { detail = "arquivo indisponível"; } catch (UnauthorizedAccessException) { detail = "arquivo indisponível"; } }
            var chip = new AttachmentChip(path, title, detail, pasted);
            chip.Remove.Click += delegate { RemoveReference(chip, true); };
            if (pasted) chip.MouseDown += (sender, e) => { if (e.Button == MouseButtons.Left) ShowPasteMenu(chip); };
            _references.Controls.Add(chip); RefreshComposer(); SaveWorkspaceDraft();
        }

        private void RemoveReference(Control chip, bool focus)
        {
            string path = (string)chip.Tag;
            _references.Controls.Remove(chip); chip.Dispose();
            // A discarded paste is not kept on disk; files from the computer are never touched.
            if (IsPasted(path)) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            RefreshComposer(); SaveWorkspaceDraft();
            if (focus) _draft.Focus();
        }

        private void ShowPasteMenu(Control chip)
        {
            string path = (string)chip.Tag;
            var menu = new ContextMenuStrip();
            FridaTheme.Style(menu);
            menu.Items.Add("Ver o texto colado", null, delegate
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Progress("Não foi possível abrir o texto colado: " + ex.Message); }
            });
            menu.Items.Add("Inserir no texto", null, delegate
            {
                string text;
                try { text = File.ReadAllText(path); } catch (Exception ex) { Progress("Não foi possível ler o texto colado: " + ex.Message); return; }
                RemoveReference(chip, true);
                Composer.InsertUndoable(text);
            });
            menu.Items.Add("Remover", null, delegate { RemoveReference(chip, true); });
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            menu.Show(chip, new Point(0, chip.Height));
        }

        private bool IsPasted(string path)
        {
            return _workspaceStore != null && !string.IsNullOrEmpty(path) && Path.GetFileName(path).StartsWith("colado-", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(_workspaceStore.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static string PasteSize(string text)
        {
            var br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            int lines = text.Split('\n').Length;
            string chars = text.Length < 1000 ? text.Length + " caracteres" : (text.Length / 1000.0).ToString("0.#", br) + " mil caracteres";
            return chars + " · " + lines.ToString("N0", br) + (lines == 1 ? " linha" : " linhas");
        }

        // Very long pastes become an attachment chip: the box stays readable and the text still goes
        // with the message. Files copied in Explorer become file references.
        // Tests supply their own data instead of touching the user's clipboard.
        private Func<IDataObject> _clipboardSource = Clipboard.GetDataObject;
        private bool PasteIntoComposer()
        {
            try
            {
                var data = _clipboardSource();
                if (data == null) return false;
                var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null && files.Length > 0)
                {
                    foreach (string file in files) AddReference(file);
                    return true;
                }
                string text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
                if (string.IsNullOrEmpty(text)) return false;
                if (_workspaceStore == null || text.Length <= PasteAttachmentChars && text.Split('\n').Length <= PasteAttachmentLines)
                {
                    Composer.InsertUndoable(text); // an ordinary paste, Ctrl+Z included
                    return true;
                }
                string path = _workspaceStore.SavePastedText(_conversation == null ? null : _conversation.Id, text);
                _draftAtPaste = _draft.Text; _pasteCaret = _draft.SelectionStart; _pasteText = text;
                AddReference(path);
                _lastPaste = _references.Controls.Cast<Control>().LastOrDefault(c => string.Equals((string)c.Tag, path, StringComparison.OrdinalIgnoreCase));
                _status.Text = "Texto longo colado como anexo (" + PasteSize(text) + "). Ctrl+Z desfaz.";
                return true;
            }
            catch (ExternalException) { return false; }
            catch (IOException ex) { _status.Text = "Não foi possível guardar o texto colado: " + ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { _status.Text = "Não foi possível guardar o texto colado: " + ex.Message; return false; }
        }

        // Ctrl+Z right after such a paste puts the text back where the caret was.
        private bool UndoPasteAttachment()
        {
            var chip = _lastPaste; _lastPaste = null;
            if (chip == null || chip.IsDisposed || chip.Parent != _references || _draft.Text != _draftAtPaste) return false;
            RemoveReference(chip, false);
            _draft.SelectionStart = Math.Min(_pasteCaret, _draft.TextLength); _draft.SelectionLength = 0;
            Composer.InsertUndoable(_pasteText); _pasteText = null;
            _status.Text = "Texto colado de volta na caixa.";
            return true;
        }

        private void ClearReferences()
        {
            foreach (Control row in _references.Controls.Cast<Control>().ToArray()) row.Dispose();
            _lastPaste = null;
        }

        // Pasted text travels inside the message (delimited, within a size budget); files from the
        // computer travel as paths.
        private const int PastedInlineLimit = 100000;
        private string ComposeRequest()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_draft.Text)) parts.Add(_draft.Text.Trim());
            var paths = _references.Controls.Cast<Control>().Select(c => (string)c.Tag).ToList();
            foreach (string path in paths.Where(IsPasted))
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException) { text = "[texto colado indisponível em " + path + "]"; }
                catch (UnauthorizedAccessException) { text = "[texto colado indisponível em " + path + "]"; }
                if (text.Length > PastedInlineLimit) text = text.Substring(0, PastedInlineLimit) + Environment.NewLine + "[… conteúdo truncado; texto completo em " + path + "]";
                parts.Add("[Anexo colado: " + Path.GetFileName(path) + "]" + Environment.NewLine + text.TrimEnd() + Environment.NewLine + "[fim do anexo]");
            }
            var files = paths.Where(p => !IsPasted(p)).ToList();
            if (files.Count > 0) parts.Add("Arquivos do computador:" + Environment.NewLine + string.Join(Environment.NewLine, files));
            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }

        // The user's bubble shows a pasted attachment as one line, not its whole content.
        private static readonly System.Text.RegularExpressions.Regex PastedBlock = new System.Text.RegularExpressions.Regex(@"\[Anexo colado: ([^\]\r\n]+)\]\r?\n(.*?)\r?\n\[fim do anexo\]", System.Text.RegularExpressions.RegexOptions.Singleline);
        internal static string CollapsePasted(string text)
        {
            return PastedBlock.Replace(text ?? "", m => "▤ Texto colado · " + PasteSize(m.Groups[2].Value));
        }

        private void LayoutChat()
        {
            if (!_chatReady || _layingOut || _disposingIde || _chatPage.ClientSize.Width < 80) return;
            _layingOut = true;
            try
            {
                int width = Math.Max(1, Math.Min(Px(800), _chatPage.ClientSize.Width - Px(32)));
                int height = _chatPage.ClientSize.Height;
                int left = (_chatPage.ClientSize.Width - width) / 2;
                int padding = Px(16), textWidth = Math.Max(1, width - padding * 2);
                // Attachment chips wrap; at most two rows show (the rest scroll inside the strip).
                int refs = _references.Controls.Count, chipRow = Px(36) + Px(6), rows = 0, rowX = 0;
                int chipSpace = Math.Max(1, textWidth - SystemInformation.VerticalScrollBarWidth);
                foreach (Control chip in _references.Controls)
                {
                    if (chip.Width > chipSpace) chip.Width = chipSpace;
                    if (rows == 0 || rowX + chip.Width > chipSpace) { rows++; rowX = 0; }
                    rowX += chip.Width + Px(6);
                }
                _references.Visible = _referenceHint.Visible = refs > 0;
                _references.SetBounds(padding, Px(10), textWidth, Math.Min(2, rows) * chipRow);
                _referenceHint.SetBounds(padding, _references.Bottom, textWidth, Px(22));
                int draftTop = refs == 0 ? Px(12) : _referenceHint.Bottom + Px(4);
                // One line when empty, growing with the text up to ten lines (or 40% of the page);
                // past that the box scrolls. The scrollbar is shown/hidden, never recreated.
                _draft.Width = textWidth;
                int lines = DraftLines(); _draftLines = lines;
                int maxLines = Math.Max(3, Math.Min(10, (int)(_chatPage.ClientSize.Height * 0.4) / Math.Max(1, _draft.Font.Height)));
                Composer.ShowVerticalBar(lines > maxLines);
                int draftHeight = Math.Min(maxLines, Math.Max(1, lines)) * _draft.Font.Height + Px(6);
                _draft.SetBounds(padding, draftTop, textWidth, draftHeight);
                int actionsTop = _draft.Bottom + Px(4), target = Px(40);
                _clip.SetBounds(Px(8), actionsTop, target, target);
                _sendButton.SetBounds(width - Px(8) - target, actionsTop, target, target);
                _mic.SetBounds(_sendButton.Left - target - Px(4), actionsTop, target, target);
                _discard.SetBounds(_mic.Left - target - Px(4), actionsTop, target, target);
                _listen.SetBounds(Math.Max(_clip.Right + Px(8), _discard.Left - Px(140)), actionsTop, Math.Min(Px(136), Math.Max(1, _discard.Left - _clip.Right - Px(12))), target);
                int composerHeight = actionsTop + target + Px(8);
                _composer.SetBounds(left, height - composerHeight - Px(16), width, composerHeight);
                bool hasNotice = !string.IsNullOrEmpty(_notice.Text) && _notice.Text != "Pronta";
                _notice.Visible = hasNotice;
                _notice.ForeColor = (_voice.IsIdeRecording || _notice.Text.StartsWith("Falha")) ? Coral : Muted;
                _notice.SetBounds(left + padding, _composer.Top - Px(30), textWidth, Px(28));
                // The notice line keeps a fixed slot: a status change never pushes the history up or down.
                int historyHeight = Math.Max(1, _composer.Top - Px(34));
                _welcome.SetBounds(0, 0, _chatPage.ClientSize.Width, historyHeight);
                _historyView.SetBounds(0, 0, _chatPage.ClientSize.Width, historyHeight);
                _historyView.Visible = _messages.Count > 0 || _loadOlder.Visible || _thinkingShown;
                _welcome.Visible = !_historyView.Visible;
                // Always reserve the vertical scrollbar: when it appears after messages grow, the
                // thread must still fit, otherwise a 1 px overflow brings a horizontal scrollbar.
                int usable = Math.Max(1, _historyView.Width - SystemInformation.VerticalScrollBarWidth);
                int threadWidth = Math.Max(1, Math.Min(width, usable - Px(24)));
                if (_messageWidth != threadWidth)
                {
                    _messageWidth = threadWidth;
                    LayoutMessages(0);
                }
                _thread.Left = Math.Max(0, (usable - _messageWidth) / 2) + _historyView.AutoScrollPosition.X;
                _jump.SetBounds(left + (width - Px(230)) / 2, Math.Max(0, historyHeight - Px(48)), Px(230), target);
            }
            finally { _layingOut = false; }
        }

        // RichEdit normally consumes the wheel even when its full text is visible.
        private sealed class MessageText : RichTextBox
        {
            internal Control ScrollHost;
            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x020A && ScrollHost != null && ScrollHost.IsHandleCreated)
                {
                    message.Result = SendMessage(ScrollHost.Handle, message.Msg, message.WParam, message.LParam);
                    return;
                }
                base.WndProc(ref message);
            }
        }

        // Circle with the participant's initials, colored by team role (group-chat style meetings).
        private sealed class Avatar : Control
        {
            internal Color Tint;
            internal Avatar(string name, Color tint)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Tint = tint; AccessibleName = name;
                Text = string.Concat((name ?? "?").Split(new[] { ' ', '/', '·' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
                if (Text.Length == 0) Text = "?";
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                // The avatar sits on the bubble fill (its BackColor), not on the card's outer corners.
                e.Graphics.Clear(BackColor.A == 255 ? BackColor : FridaTheme.Effective(Parent)); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(60, Tint))) e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
                using (var pen = new Pen(Tint, Math.Max(1f, FridaTheme.Factor))) e.Graphics.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);
                FridaTheme.Text(e.Graphics, Text, FridaTheme.Strong(9), ClientRectangle, Tint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        private static Color RoleTint(string role, string author)
        {
            string key = (role ?? "") + " " + (author ?? "");
            if (key.Contains("Manager") || key.Contains("Gestor")) return FridaTheme.Accent;
            if (key.Contains("Technician") || key.Contains("Engenheiro")) return Color.FromArgb(255, 184, 92);
            if (key.Contains("Analyst") || key.Contains("Analista")) return Color.FromArgb(186, 150, 255);
            if (key.Contains("WhatsApp") || key.Contains("Pesquisador")) return Color.FromArgb(255, 128, 196);
            if (key.Contains("Assistant") || key.Contains("Estagi")) return FridaTheme.Success;
            if (key.Contains("Secretary") || key.Contains("Secretaria")) return FridaTheme.Highlight;
            return FridaTheme.Muted;
        }

        private sealed class MessageView
        {
            internal RoundedPanel Card;
            internal Label Author;
            internal Label Meta;
            internal RichTextBox Body;
            internal IconButton Speak, Copy;
            internal Avatar Avatar;
            internal bool Outgoing, Notice;
            internal int ContentHeight;
            // Markdown source (copy/listen), author key and time for grouping, rendered as Markdown?
            internal string Raw, AuthorKey, Key;
            internal DateTime At;
            internal bool Grouped, Rich, Team;
            // Author row shown? (kept as a flag: Control.Visible reads false until the window is shown)
            internal bool Header;
            internal int Natural;
            internal DecisionFooter Footer;
            // Answer shown by default; tool output and long code behind the details button.
            internal string Answer, Details;
            internal bool Expanded;
            internal PillButton DetailsButton;
        }

        private static string DetailsCaption(MessageView message)
        {
            int lines = FridaReplyParts.Lines(message.Details);
            return (message.Expanded ? "Ocultar detalhes técnicos" : "Detalhes técnicos") + " · " + lines + (lines == 1 ? " linha" : " linhas");
        }
        private void ToggleDetails(MessageView message)
        {
            message.Expanded = !message.Expanded;
            message.DetailsButton.Text = DetailsCaption(message);
            message.DetailsButton.Glyph = message.Expanded ? FridaTheme.GlyphChevronDown : FridaTheme.GlyphChevronRight;
            message.DetailsButton.AccessibleName = message.Expanded ? "Ocultar detalhes técnicos" : "Mostrar detalhes técnicos";
            message.Body.Rtf = FridaMarkdown.ToRtf(message.Expanded ? message.Raw : message.Answer, BubbleStyle());
            message.Natural = NaturalWidth(message);
            int index = _messages.IndexOf(message);
            if (index >= 0) LayoutMessages(index);
        }

        // Secretary's final decision: verdict, counts and first items, up to three prints and
        // "Ver relatório completo" (the versioned report with evidence and the skill update).
        private void AddDecisionCard(FridaConversationMessage m, string time, DateTime at)
        {
            string author = m.Author ?? "Secretaria Frida";
            var report = _workspaceStore == null || _conversation == null ? null : FridaMeetingWorkspace.LoadDecision(_workspaceStore, _conversation.Id, m.ArtifactId);
            string meta = string.Join(" · ", new[] { "Decisão final", m.Model, time }.Where(x => !string.IsNullOrEmpty(x)));
            AddMessage(author, m.Text, meta, new Avatar(author, Highlight), false, false, at, m.Id, false);
            var view = _messages[_messages.Count - 1];
            view.AuthorKey = null; // nothing groups under the card
            view.Footer = new DecisionFooter(report, Surface, () => ShowDecisionReport(report));
            view.Card.Controls.Add(view.Footer);
            view.Natural = Math.Max(view.Natural, view.Footer.PreferredWidth);
            if (!_bulkMessages) { LayoutMessages(_messages.Count - 1); if (NearLatest()) ScrollToLatest(); }
        }

        internal static Color VerdictTint(string verdict)
        {
            return verdict == FridaTeamDecisionReport.Approved ? FridaTheme.Success : verdict == FridaTeamDecisionReport.Rejected ? FridaTheme.Coral
                : verdict == FridaTeamDecisionReport.NotReviewed || verdict == null ? FridaTheme.Muted : FridaTheme.Warning;
        }

        private static Image Thumbnail(string path, int width, int height)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            using (var image = Image.FromStream(stream))
            {
                double scale = Math.Min((double)width / image.Width, (double)height / image.Height);
                var thumb = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                using (var g = Graphics.FromImage(thumb)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(image, 0, 0, thumb.Width, thumb.Height); }
                return thumb;
            }
        }
        private static PictureBox EvidenceThumb(FridaDecisionScreenshot shot, int width, int height, Action<string> open)
        {
            var box = new PictureBox { Size = new Size(width, height), SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand, BackColor = FridaTheme.Canvas, AccessibleName = shot.Caption, Tag = shot.Path };
            try { box.Image = Thumbnail(shot.Path, width, height); } catch (Exception) { box.Cursor = Cursors.Default; } // file gone: empty frame
            FridaTheme.Tip(box, shot.Caption + " · clique para abrir");
            box.Click += delegate { open(shot.Path); };
            box.Disposed += delegate { if (box.Image != null) box.Image.Dispose(); };
            return box;
        }

        private sealed class DecisionFooter : Panel
        {
            private readonly Chip _verdict;
            private readonly PillButton _open;
            private readonly List<PictureBox> _thumbs = new List<PictureBox>();
            internal DecisionFooter(FridaTeamDecisionReport report, Color fill, Action open)
            {
                BackColor = fill; TabStop = false;
                _verdict = new Chip(report == null ? "Relatório indisponível" : report.Verdict, FridaTheme.GlyphCheck) { Tint = VerdictTint(report == null ? null : report.Verdict) };
                _open = new PillButton("Ver relatório completo", PillKind.Secondary, FridaTheme.GlyphDocument, true) { Enabled = report != null, AccessibleName = "Ver relatório completo da decisão final" };
                FridaTheme.Tip(_open, "Resumo, decisão, evidências, prints e atualização de skill");
                _open.Click += delegate { open(); };
                Controls.Add(_verdict); Controls.Add(_open);
                if (report != null)
                    foreach (var shot in report.Screenshots.Take(3))
                    {
                        var box = EvidenceThumb(shot, FridaTheme.Px(132), FridaTheme.Px(76), OpenEvidence);
                        Controls.Add(box); _thumbs.Add(box);
                    }
            }
            internal int PreferredWidth { get { return Math.Max(_thumbs.Count * FridaTheme.Px(140), _verdict.Width + FridaTheme.Px(8) + _open.Width); } }
            internal int Arrange(int width)
            {
                int y = 0, x = 0;
                foreach (var box in _thumbs) { box.Location = new Point(x, 0); x += box.Width + FridaTheme.Px(8); y = box.Height + FridaTheme.Px(8); }
                int row = FridaTheme.Px(32);
                _verdict.Location = new Point(0, y + (row - _verdict.Height) / 2);
                int openWidth = _open.GetPreferredSize(Size.Empty).Width;
                if (_verdict.Right + FridaTheme.Px(8) + openWidth <= width) _open.SetBounds(_verdict.Right + FridaTheme.Px(8), y, openWidth, row);
                else { y += row + FridaTheme.Px(6); _open.SetBounds(0, y, Math.Min(openWidth, width), row); }
                return y + row;
            }
        }
        private static void OpenEvidence(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { BootLogger.Log("Evidência não aberta: " + ex.GetType().Name); }
        }

        // Full report: Markdown (Resumo, Decisão, Avaliação, Evidências, Skill) with the prints on top;
        // exports to Markdown (prints copied next to it) or Word, and opens the evidence folder.
        private void ShowDecisionReport(FridaTeamDecisionReport report)
        {
            if (report == null) { Progress("Relatório da decisão indisponível."); return; }
            using (var dialog = new FridaDialog("Relatório da decisão final · " + report.Verdict, 860, 660))
            {
                dialog.FormBorderStyle = FormBorderStyle.Sizable; dialog.MaximizeBox = true;
                dialog.MinimumSize = new Size(Px(560) + dialog.Width - dialog.ClientSize.Width, Px(420) + dialog.Height - dialog.ClientSize.Height);
                var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = FridaTheme.Surface, ForeColor = Ink, DetectUrls = true, AccessibleName = "Relatório da decisão final" };
                EnableHttpLinks(text); FridaTheme.DarkScroll(text);
                dialog.Body.Controls.Add(text);
                if (report.Screenshots.Count > 0)
                {
                    var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Px(106), AutoScroll = true, WrapContents = false, BackColor = FridaTheme.Surface, Padding = new Padding(0, 0, 0, Px(8)), AccessibleName = "Prints das evidências" };
                    foreach (var shot in report.Screenshots) { var box = EvidenceThumb(shot, Px(160), Px(90), OpenEvidence); box.Margin = new Padding(0, 0, Px(8), 0); strip.Controls.Add(box); }
                    dialog.Body.Controls.Add(strip);
                    var folder = Path.GetDirectoryName(report.Screenshots[0].Path);
                    var openFolder = new PillButton("Abrir pasta de evidências", PillKind.Ghost, FridaTheme.GlyphFolder, true);
                    openFolder.Click += delegate { OpenEvidence(folder); };
                    dialog.Footer.Controls.Add(openFolder);
                }
                var exportMarkdown = new PillButton("Exportar Markdown", PillKind.Secondary, FridaTheme.GlyphExport, true);
                exportMarkdown.Click += delegate { ExportDecisionMarkdown(dialog, report); };
                var exportWord = new PillButton("Exportar Word", PillKind.Secondary, FridaTheme.GlyphDocument, true);
                exportWord.Click += delegate { ExportDecisionWord(dialog, report); };
                dialog.Footer.Controls.Add(exportMarkdown); dialog.Footer.Controls.Add(exportWord);
                dialog.AddButton("Fechar", DialogResult.Cancel, PillKind.Primary);
                dialog.Shown += delegate { FridaMarkdown.Render(text, report.ToMarkdown(), new FridaMarkdownStyle { Width = text.ClientSize.Width, CodeBack = Canvas }); };
                dialog.ShowDialog(this);
            }
        }
        private void ExportDecisionMarkdown(IWin32Window owner, FridaTeamDecisionReport report)
        {
            using (var picker = new SaveFileDialog { Filter = "Markdown|*.md", FileName = "decisao-final-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".md" })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK) return;
                string folderName = Path.GetFileNameWithoutExtension(picker.FileName).Replace(' ', '-') + "-evidencias";
                try
                {
                    if (report.Screenshots.Count > 0)
                    {
                        string folder = Path.Combine(Path.GetDirectoryName(picker.FileName), folderName); Directory.CreateDirectory(folder);
                        foreach (var shot in report.Screenshots) if (File.Exists(shot.Path)) File.Copy(shot.Path, Path.Combine(folder, Path.GetFileName(shot.Path)), true);
                    }
                    File.WriteAllText(picker.FileName, report.ToMarkdown(report.Screenshots.Count > 0 ? folderName : null), new System.Text.UTF8Encoding(false));
                    Progress("Relatório exportado: " + picker.FileName);
                }
                catch (Exception ex) { Progress("Falha ao exportar o relatório: " + ex.Message); }
            }
        }
        private void ExportDecisionWord(IWin32Window owner, FridaTeamDecisionReport report)
        {
            using (var picker = new SaveFileDialog { Filter = "Documento do Word|*.docx", FileName = "decisao-final-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".docx" })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK) return;
                try { FridaDocumentIO.WriteDocx(picker.FileName, FridaMarkdown.ToPlain(report.ToMarkdown())); Progress("Relatório exportado: " + picker.FileName); }
                catch (Exception ex) { Progress("Falha ao exportar o relatório: " + ex.Message); }
            }
        }

        private void BuildMessageMenu()
        {
            FridaTheme.Style(_messageMenu);
            Func<MessageView> source = () => _messages.FirstOrDefault(m => m.Body == _messageMenu.SourceControl);
            var selection = (ToolStripMenuItem)_messageMenu.Items.Add("Copiar seleção", null, delegate { var m = source(); if (m != null && m.Body.SelectionLength > 0) m.Body.Copy(); });
            selection.ShortcutKeyDisplayString = "Ctrl+C";
            var whole = _messageMenu.Items.Add("Copiar mensagem", null, delegate { var m = source(); if (m != null) CopyMessage(m); });
            var markdown = _messageMenu.Items.Add("Copiar como Markdown", null, delegate { var m = source(); if (m != null) SetClipboard(() => Clipboard.SetText(m.Raw)); });
            _messageMenu.Items.Add("Selecionar tudo", null, delegate { var m = source(); if (m != null) { m.Body.Focus(); m.Body.SelectAll(); } });
            _messageMenu.Opening += (sender, e) =>
            {
                var m = source(); if (m == null) { e.Cancel = true; return; }
                selection.Enabled = m.Body.SelectionLength > 0;
                markdown.Visible = m.Rich && m.Raw.Length > 0;
                whole.Enabled = m.Raw.Length > 0;
            };
        }

        // Rich replies go to the clipboard twice: formatted (dark text on white, for Word or e-mail)
        // and as clean text without Markdown symbols.
        private void CopyMessage(MessageView message)
        {
            if (string.IsNullOrEmpty(message.Raw)) return;
            SetClipboard(() =>
            {
                if (!message.Rich) { Clipboard.SetText(message.Raw); return; }
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, FridaMarkdown.ToPlain(message.Raw));
                data.SetData(DataFormats.Rtf, FridaMarkdown.ToRtf(message.Raw, FridaMarkdownStyle.Document()));
                Clipboard.SetDataObject(data, true);
            });
            Progress(message.Outgoing ? "Mensagem copiada." : "Resposta copiada.");
        }
        private void SetClipboard(Action write)
        {
            try { write(); }
            catch (ExternalException) { Progress("A área de transferência está em uso por outro programa. Tente de novo."); }
        }

        private FridaMarkdownStyle BubbleStyle()
        {
            return new FridaMarkdownStyle { Size = 11, CodeBack = Canvas, Width = _messageWidth > 0 ? _messageWidth * 17 / 20 - Px(80) : 0 };
        }

        private bool NearLatest()
        {
            return _thread.Height + _historyView.AutoScrollPosition.Y <= _historyView.ClientSize.Height + Px(24);
        }

        private void ScrollToLatest()
        {
            _historyView.AutoScrollPosition = new Point(0, _thread.Height);
            _jump.Visible = false;
        }

        private void AddMessage(string author, string text)
        {
            bool notice = author == "Atendimento" || author == "Vínculo";
            AddMessage(author, text, notice ? null : DateTime.Now.ToString("HH:mm"), null, notice);
        }

        private static DateTime LocalTime(string utc)
        {
            DateTime at;
            return DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at) ? at.ToLocalTime() : DateTime.Now;
        }

        // Stored messages: chat turns, meeting contributions (avatar, model, time) and notices.
        private void AddMessage(FridaConversationMessage m)
        {
            string time = FridaIdeRuntime.DisplayTime(m.AtUtc);
            if (time.Length > 5) time = time.Substring(time.Length - 5);
            var at = LocalTime(m.AtUtc);
            if (m.Role == "link") { AddMessage("Vínculo", m.Text, null, null, true, false, at, m.Id); return; }
            if (m.Role == "user") { AddMessage("Você", m.Text, time, null, false, false, at, m.Id); return; }
            if (m.Role == "decision") { AddDecisionCard(m, time, at); return; }
            if (m.Channel == "meeting" || m.Role == "report")
            {
                string author = m.Author ?? (m.Role == "report" ? "Gestor" : "Equipe");
                string meta = string.Join(" · ", new[] { m.Model, time, m.Status == "unavailable" ? "indisponível" : null }.Where(x => !string.IsNullOrEmpty(x)));
                AddMessage(author, m.Text, meta, new Avatar(author, RoleTint(m.Role, author)), false, m.Status == "unavailable", at, m.Id);
                return;
            }
            AddMessage(m.Author ?? "Frida", m.Text, time, null, false, false, at, m.Id);
        }

        // One bubble per message. "Você" sits right in the accent bubble; Frida and team members sit
        // left in raised bubbles (team members with their role color bar) and render Markdown;
        // notices are centered pills. Consecutive turns of one author within five minutes form a
        // group without repeated header or avatar.
        private void AddMessage(string author, string text, string meta, Avatar avatar, bool notice, bool failed = false, DateTime? at = null, string key = null, bool groupable = true)
        {
            text = text ?? "";
            bool follow = _messages.Count == 0 || NearLatest();
            bool outgoing = !notice && author.StartsWith("Você", StringComparison.Ordinal);
            bool team = avatar != null, rich = !notice && !outgoing;
            // Rich replies: the answer is shown; tool output and long code wait behind "Detalhes técnicos".
            // A JSON answer (plan, decision) reads as labelled items.
            string details = "";
            if (rich) { string answer; FridaReplyParts.Split(text, out answer, out details); text = FridaMarkdown.Readable(answer); }
            var when = at ?? DateTime.Now;
            var previous = _messages.Count > 0 ? _messages[_messages.Count - 1] : null;
            bool grouped = groupable && !notice && previous != null && !previous.Notice && previous.AuthorKey == author && Math.Abs((when - previous.At).TotalMinutes) < 5;
            Color bar = team ? avatar.Tint : Color.Empty;
            if (grouped && avatar != null) { avatar.Dispose(); avatar = null; }
            else if (rich && avatar == null && !grouped) avatar = new Avatar(author, Accent);
            Color fill = notice ? Canvas : outgoing ? Bubble : team ? Surface : FridaTheme.SurfaceRaised;
            var card = new RoundedPanel
            {
                Radius = notice ? 14 : 16, BackColor = Canvas, Fill = fill, FocusRing = false,
                Stroke = failed ? Coral : outgoing ? Color.Transparent : FridaTheme.Divider, Bar = bar
            };
            var body = new MessageText { ScrollHost = _historyView, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true, TabStop = false };
            EnableHttpLinks(body);
            body.Dock = DockStyle.None; body.Font = notice ? FridaTheme.Font(9.5f) : _draft.Font; body.ScrollBars = RichTextBoxScrollBars.None;
            body.BackColor = fill; body.ForeColor = notice ? Muted : Ink; body.AccessibleName = "Mensagem de " + author;
            body.ContextMenuStrip = _messageMenu;
            var label = new Label
            {
                Text = author, Font = notice ? FridaTheme.Strong(8.5f) : FridaTheme.Strong(10), ForeColor = notice ? (author == "Atendimento" ? Coral : Muted) : Ink,
                BackColor = fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Visible = !grouped && !outgoing
            };
            var message = new MessageView { Card = card, Author = label, Body = body, Outgoing = outgoing, Notice = notice, Avatar = avatar, AuthorKey = author, At = when, Grouped = grouped, Rich = rich, Team = team, Key = key, Header = !grouped && !outgoing,
                Answer = text, Details = details, Raw = details.Length == 0 ? text : text + "\n\n" + details };
            card.Controls.Add(label); card.Controls.Add(body);
            if (message.Header) FridaTheme.Tip(label, author);
            if (!string.IsNullOrEmpty(meta) && !grouped)
            {
                message.Meta = new Label { Text = meta, Font = FridaTheme.Font(8.5f), ForeColor = failed ? Coral : Muted, BackColor = fill, TextAlign = outgoing ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft, AutoEllipsis = true };
                FridaTheme.Tip(message.Meta, meta);
                card.Controls.Add(message.Meta);
            }
            if (avatar != null) { avatar.BackColor = fill; card.Controls.Add(avatar); }
            if (rich)
            {
                // Compact actions; listening skips the Markdown symbols, copying keeps the formatting.
                message.Speak = ActionButton("Ouvir resposta", FridaTheme.GlyphSpeak, () => _speak(FridaInteractionPolicy.SpeakableMarkdown(text)), fill, false);
                message.Copy = ActionButton("Copiar resposta", FridaTheme.GlyphCopy, () => CopyMessage(message), fill, false);
                message.Speak.ForeColor = message.Copy.ForeColor = Muted;
                card.Controls.Add(message.Speak); card.Controls.Add(message.Copy);
                if (details.Length > 0)
                {
                    message.DetailsButton = new PillButton(DetailsCaption(message), PillKind.Ghost, FridaTheme.GlyphChevronRight, true) { AccessibleName = "Mostrar detalhes técnicos", Underlay = fill };
                    FridaTheme.Tip(message.DetailsButton, "Saída das ferramentas e código completo desta resposta");
                    message.DetailsButton.Click += delegate { ToggleDetails(message); };
                    card.Controls.Add(message.DetailsButton);
                }
            }
            _thread.Controls.Add(card);
            _messages.Add(message);
            body.ContentsResized += (sender, e) =>
            {
                message.ContentHeight = e.NewRectangle.Height;
                if (!_layingOut && !_bulkMessages && _messages.Count > 0 && !_disposingIde) LayoutMessages(_messages.IndexOf(message));
            };
            if (rich) body.Rtf = FridaMarkdown.ToRtf(text, BubbleStyle()); else body.Text = outgoing ? CollapsePasted(text) : text;
            message.Natural = NaturalWidth(message);
            if (_bulkMessages) return;
            LayoutChat(); LayoutMessages(_messages.Count - 1);
            if (follow) ScrollToLatest(); else _jump.Visible = true;
        }

        // Widest line of the rendered text (headings and code are a little wider than body text).
        private int NaturalWidth(MessageView message)
        {
            string plain = message.Rich ? FridaMarkdown.ToPlain(message.Expanded ? message.Raw : message.Answer) : message.Body.Text;
            int text = TextRenderer.MeasureText(plain.Length > 4000 ? plain.Substring(0, 4000) : plain, message.Body.Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            if (message.Rich) text = text * 11 / 10;
            int header = message.Header ? TextRenderer.MeasureText(message.Author.Text, message.Author.Font).Width + Px(12) : 0;
            if (message.Meta != null) header += TextRenderer.MeasureText(message.Meta.Text, message.Meta.Font).Width + Px(24);
            if (message.Speak != null) header = Math.Max(header, Px(72));
            if (message.DetailsButton != null) header = Math.Max(header, Px(84) + message.DetailsButton.GetPreferredSize(Size.Empty).Width);
            return Math.Max(text, header);
        }

        private void LayoutMessages(int first)
        {
            if (_messageWidth < 1 || first < 0) return;
            bool previousLayout = _layingOut;
            _layingOut = true;
            try
            {
                int top = Px(16);
                if (_loadOlder.Visible)
                {
                    var s = _loadOlder.GetPreferredSize(Size.Empty);
                    _loadOlder.SetBounds(Math.Max(0, (_messageWidth - s.Width) / 2), Px(12), s.Width, s.Height); top = _loadOlder.Bottom + Px(16);
                }
                int y = first == 0 ? top : _messages[first - 1].Card.Bottom;
                for (int index = first; index < _messages.Count; index++)
                {
                    var msg = _messages[index];
                    if (index > 0) y += msg.Grouped ? Px(4) : msg.Notice || _messages[index - 1].Notice ? Px(10) : Px(14);
                    int pad = Px(14), bar = msg.Team ? Px(3) : 0, avatarSize = msg.Team ? Px(32) : Px(28);
                    // Grouped replies keep the avatar column empty so their text lines up with the first one.
                    int contentLeft = pad + bar + (msg.Rich ? avatarSize + Px(10) : 0);
                    int max = msg.Outgoing ? _messageWidth * 3 / 4 : msg.Notice ? _messageWidth : _messageWidth * 17 / 20;
                    int min = Math.Min(max, msg.Outgoing ? Px(96) : msg.Notice ? Px(120) : Px(220));
                    int width = Math.Max(min, Math.Min(max, contentLeft + msg.Natural + pad + Px(4)));
                    int inner = Math.Max(1, width - contentLeft - pad);
                    int headerTop = Px(msg.Notice ? 6 : 10), bodyTop = headerTop;
                    if (msg.Avatar != null) msg.Avatar.SetBounds(pad + bar, headerTop, avatarSize, avatarSize);
                    if (msg.Header)
                    {
                        int h = Px(msg.Notice ? 18 : 20);
                        int authorWidth = msg.Meta == null ? inner : Math.Min(inner, TextRenderer.MeasureText(msg.Author.Text, msg.Author.Font).Width + Px(8));
                        msg.Author.SetBounds(contentLeft, headerTop, authorWidth, h);
                        if (msg.Meta != null) msg.Meta.SetBounds(msg.Author.Right + Px(4), headerTop, Math.Max(1, contentLeft + inner - msg.Author.Right - Px(4)), h);
                        bodyTop = headerTop + h + Px(4);
                    }
                    msg.Body.SetBounds(contentLeft, bodyTop, inner, Math.Max(1, msg.Body.Height));
                    int content = msg.ContentHeight;
                    if (content <= 0)
                        content = TextRenderer.MeasureText(msg.Body.Text, msg.Body.Font, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
                    msg.Body.Height = Math.Max(msg.Body.Font.Height, content) + Px(2);
                    int bottom = msg.Body.Bottom;
                    // The user's own bubble carries only the time, under the text.
                    if (msg.Outgoing && msg.Meta != null) { msg.Meta.SetBounds(contentLeft, bottom + Px(2), inner, Px(16)); bottom = msg.Meta.Bottom; }
                    if (msg.Footer != null) { int footer = msg.Footer.Arrange(inner); msg.Footer.SetBounds(contentLeft, bottom + Px(8), inner, footer); bottom = msg.Footer.Bottom; }
                    if (msg.Speak != null)
                    {
                        int s = Px(32);
                        msg.Speak.SetBounds(contentLeft - Px(6), bottom + Px(2), s, s);
                        msg.Copy.SetBounds(msg.Speak.Right + Px(2), bottom + Px(2), s, s);
                        if (msg.DetailsButton != null)
                        {
                            int want = msg.DetailsButton.GetPreferredSize(Size.Empty).Width;
                            msg.DetailsButton.SetBounds(msg.Copy.Right + Px(8), bottom + Px(2), Math.Max(Px(60), Math.Min(want, contentLeft + inner - msg.Copy.Right - Px(8))), s);
                        }
                        bottom = msg.Speak.Bottom;
                    }
                    bottom += Px(msg.Notice ? 6 : msg.Speak != null ? 4 : 10);
                    if (msg.Avatar != null) bottom = Math.Max(bottom, msg.Avatar.Bottom + Px(10));
                    int x = msg.Outgoing ? _messageWidth - width : msg.Notice ? (_messageWidth - width) / 2 : 0;
                    msg.Card.SetBounds(x, y, width, bottom);
                    y = msg.Card.Bottom;
                }
                y += Px(12);
                if (_thinkingShown) { _thinking.SetBounds(Px(16), y, Math.Max(1, _messageWidth - Px(32)), Px(28)); y = _thinking.Bottom + Px(8); }
                if (_loadNewer.Visible)
                {
                    var s = _loadNewer.GetPreferredSize(Size.Empty);
                    _loadNewer.SetBounds(Math.Max(0, (_messageWidth - s.Width) / 2), y + Px(4), s.Width, s.Height); y = _loadNewer.Bottom + Px(16);
                }
                _thread.Size = new Size(_messageWidth, y);
            }
            finally { _layingOut = previousLayout; }
        }
    }

    internal sealed class FridaWelcomeView : Control
    {
        private struct Particle3D
        {
            public float nx, ny, nz;
            public float shellRadius;
            public float phase;
            public float size;
        }

        private struct ProjectedNode
        {
            public float sx, sy, z, scale;
        }

        private sealed class PromptChip
        {
            public string Icon;
            public string Label;
            public string Prompt;
            public Rectangle Bounds;
            public bool Hovered;
        }

        public event Action<string> PromptSelected;

        private readonly Timer _animTimer;
        private readonly Particle3D[] _particles;
        private readonly ProjectedNode[] _projNodes;
        private readonly PromptChip[] _chips;

        private float _time;
        private float _smoothEnergy;
        private float _targetEnergy;
        private float _tiltX;
        private float _tiltY;
        private float _targetTiltX;
        private float _targetTiltY;

        private PointF _logoCenter;
        private float _logoRadius = 70f;
        private float _rippleRadius;
        private float _rippleAlpha;
        private PointF _rippleOrigin;

        public FridaWelcomeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = FridaTheme.Canvas;
            ForeColor = FridaTheme.Ink;
            AccessibleName = "Tela inicial da Frida";
            AccessibleRole = AccessibleRole.Grouping;
            Text = "Fala comigo chefe";

            _particles = BuildFibonacciSphereParticles(48);
            _projNodes = new ProjectedNode[_particles.Length];

            _chips = new PromptChip[]
            {
                new PromptChip { Icon = "📊", Label = "Consultar planilhas", Prompt = "Consultar as planilhas base do Google Drive" },
                new PromptChip { Icon = "💬", Label = "Ler WhatsApp", Prompt = "Verificar mensagens e grupos recentes no WhatsApp" },
                new PromptChip { Icon = "💻", Label = "Diagnóstico do PC", Prompt = "Fazer diagnóstico completo do computador (CPU, RAM, disco)" },
                new PromptChip { Icon = "🛠️", Label = "Inspecionar código", Prompt = "Inspecionar o código e projetos abertos" }
            };

            _animTimer = new Timer { Interval = 25 };
            _animTimer.Tick += OnAnimationTick;
        }

        private static Particle3D[] BuildFibonacciSphereParticles(int count)
        {
            var arr = new Particle3D[count];
            var rng = new Random(6801);
            float goldenAngle = (float)(Math.PI * (3.0 - Math.Sqrt(5.0)));

            for (int i = 0; i < count; i++)
            {
                float y = 1f - (i / (float)(count - 1)) * 2f;
                float radiusAtY = (float)Math.Sqrt(Math.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;

                arr[i] = new Particle3D
                {
                    nx = (float)Math.Cos(theta) * radiusAtY,
                    ny = y,
                    nz = (float)Math.Sin(theta) * radiusAtY,
                    shellRadius = (i % 3 == 0) ? 0.72f : ((i % 5 == 0) ? 1.16f : 0.94f),
                    phase = (float)(rng.NextDouble() * Math.PI * 2.0),
                    size = 1.6f + (float)(rng.NextDouble() * 1.8)
                };
            }
            return arr;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && Parent != null && !Disposing && !IsDisposed) StartAnimation();
            else StopAnimation();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) StopAnimation();
            else if (Visible && !Disposing && !IsDisposed) StartAnimation();
        }

        private void StartAnimation()
        {
            if (!_animTimer.Enabled && !Disposing && !IsDisposed) _animTimer.Start();
        }

        private void StopAnimation()
        {
            if (_animTimer.Enabled) _animTimer.Stop();
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            if (!Visible || Width < 10 || Height < 10 || Disposing || IsDisposed)
            {
                if (!Visible && _animTimer.Enabled) _animTimer.Stop();
                return;
            }

            _time += 0.035f;
            _smoothEnergy += (_targetEnergy - _smoothEnergy) * 0.12f;
            _tiltX += (_targetTiltX - _tiltX) * 0.10f;
            _tiltY += (_targetTiltY - _tiltY) * 0.10f;

            if (_rippleAlpha > 1f)
            {
                _rippleRadius += 6.5f * FridaTheme.Factor;
                _rippleAlpha = Math.Max(0f, _rippleAlpha - 9.0f);
            }

            Invalidate();
        }

        private void TriggerRipple(float x, float y)
        {
            _rippleOrigin = new PointF(x, y);
            _rippleRadius = 4f * FridaTheme.Factor;
            _rippleAlpha = 245f;
            _targetEnergy = 2.4f;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (_chips != null)
            {
                foreach (var chip in _chips)
                {
                    if (chip.Bounds.Contains(e.Location))
                    {
                        TriggerRipple(e.X, e.Y);
                        if (PromptSelected != null) PromptSelected(chip.Prompt);
                        return;
                    }
                }
            }

            float dx = e.X - _logoCenter.X;
            float dy = e.Y - _logoCenter.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist <= _logoRadius * 1.45f)
            {
                TriggerRipple(_logoCenter.X, _logoCenter.Y);
                _targetEnergy = 2.5f;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool anyHover = false;

            if (_chips != null)
            {
                foreach (var chip in _chips)
                {
                    bool h = chip.Bounds.Contains(e.Location);
                    if (h != chip.Hovered)
                    {
                        chip.Hovered = h;
                        Invalidate(Rectangle.Inflate(chip.Bounds, 2, 2));
                    }
                    if (h) anyHover = true;
                }
            }

            float dx = e.X - _logoCenter.X;
            float dy = e.Y - _logoCenter.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            bool overLogo = dist <= _logoRadius * 1.4f;

            if (overLogo)
            {
                anyHover = true;
                _targetEnergy = 1.0f;
            }
            else
            {
                _targetEnergy = 0.0f;
            }

            if (_logoRadius > 5f)
            {
                _targetTiltX = Math.Max(-0.55f, Math.Min(0.55f, dx / (_logoRadius * 2.4f)));
                _targetTiltY = Math.Max(-0.55f, Math.Min(0.55f, dy / (_logoRadius * 2.4f)));
            }

            Cursor = anyHover ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _targetEnergy = 0.0f;
            _targetTiltX = 0.0f;
            _targetTiltY = 0.0f;
            if (_chips != null)
            {
                foreach (var chip in _chips)
                {
                    if (chip.Hovered)
                    {
                        chip.Hovered = false;
                        Invalidate(Rectangle.Inflate(chip.Bounds, 2, 2));
                    }
                }
            }
            Cursor = Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 20 || Height < 20) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            float scale = FridaTheme.Factor;
            float w = Width;
            float h = Height;

            // Text block measured with the real fonts (a fixed 32 px box clipped the 22 pt title's
            // descenders and pushed it into the subtitle). Long lines wrap on narrow windows.
            // FridaTheme fonts are shared and cached: never dispose them (that once broke every other
            // control drawing with the same size, which then showed the red error cross).
            Font titleFont = FridaTheme.Strong(24f), subFont = FridaTheme.Font(11f);
            int textWidth = Math.Max(1, (int)(w - 48f * scale));
            var wrap = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            int titleH = TextRenderer.MeasureText(WelcomeTitle, titleFont, new Size(textWidth, int.MaxValue), wrap).Height;
            int subH = TextRenderer.MeasureText(WelcomeSubtitle, subFont, new Size(textWidth, int.MaxValue), wrap).Height;
            bool showChips = h >= 360f * scale && _chips != null && _chips.Length > 0;
            float chipsH = showChips ? ChipRowsHeight(w, scale) : 0f;
            float gapLogo = 22f * scale, gapSub = 6f * scale, gapChips = 22f * scale, margin = 24f * scale;
            float textBlock = gapLogo + titleH + gapSub + subH + (showChips ? gapChips + chipsH : 0f);

            // The logo takes the room left by the text: up to 190 px of radius (2.3× the former 84),
            // never wider than ~44% of the view. Its glow and outer shell reach ~1.2 radius.
            float radius = Math.Min(190f * scale, Math.Min(w * 0.22f, (h - textBlock - margin * 2f) / 2.4f));
            _logoRadius = Math.Max(36f * scale, radius);
            float totalH = _logoRadius * 2.4f + textBlock;
            float startY = Math.Max(margin * 0.5f, (h - totalH) * 0.45f);
            _logoCenter = new PointF(w * 0.5f, startY + _logoRadius * 1.2f);

            float titleY = _logoCenter.Y + _logoRadius * 1.2f + gapLogo;
            float subtitleY = titleY + titleH + gapSub;
            float chipsY = subtitleY + subH + gapChips;

            DrawRipple(g);
            // The logo is designed at an 84 px radius; drawing it through a scale transform keeps every
            // proportion (line weights, core, particles, perspective) at any size.
            float design = LogoDesignRadius * scale, zoom = _logoRadius / design;
            var state = g.Save();
            g.TranslateTransform(_logoCenter.X, _logoCenter.Y);
            g.ScaleTransform(zoom, zoom);
            DrawFridaLogo(g, 0f, 0f, design);
            g.Restore(state);

            int left = (int)((w - textWidth) / 2f);
            _titleRect = new Rectangle(left, (int)titleY, textWidth, titleH);
            _subtitleRect = new Rectangle(left, (int)subtitleY, textWidth, subH);
            TextRenderer.DrawText(g, WelcomeTitle, titleFont, _titleRect, FridaTheme.Ink, wrap);
            TextRenderer.DrawText(g, WelcomeSubtitle, subFont, _subtitleRect, FridaTheme.Muted, wrap);

            if (showChips) DrawPromptChips(g, w, chipsY, scale);
        }

        private const string WelcomeTitle = "Fala comigo chefe", WelcomeSubtitle = "Frida pronta para agir · Digite abaixo ou escolha um comando";
        // Last painted text boxes (layout checks in the tests).
        private Rectangle _titleRect, _subtitleRect;
        private const float LogoDesignRadius = 84f;

        // Height of the shortcut chips: one row when they fit, otherwise two.
        private float ChipRowsHeight(float w, float scale)
        {
            Font chipFont = FridaTheme.Font(9.5f);
            int padX = (int)(14 * scale), gap = (int)(10 * scale), chipH = (int)(32 * scale), total = 0;
            for (int i = 0; i < _chips.Length; i++) total += TextRenderer.MeasureText(_chips[i].Icon + "  " + _chips[i].Label, chipFont).Width + padX * 2 + (i > 0 ? gap : 0);
            return total <= w - 32 * scale ? chipH : chipH * 2 + 8 * scale;
        }

        // Click shockwave, in screen coordinates (it can start on a chip, outside the logo).
        private void DrawRipple(Graphics g)
        {
            if (_rippleAlpha <= 1f || _rippleRadius <= 0f) return;
            float rw = _rippleRadius;
            using (Pen rPen = new Pen(Color.FromArgb(ClampByte(_rippleAlpha), 90, 235, 255), 2.2f * FridaTheme.Factor))
                g.DrawEllipse(rPen, _rippleOrigin.X - rw, _rippleOrigin.Y - rw, rw * 2f, rw * 2f);
            if (rw > 14f * FridaTheme.Factor)
            {
                float rw2 = rw * 0.65f;
                using (Pen rPen2 = new Pen(Color.FromArgb(ClampByte(_rippleAlpha * 0.45f), 35, 190, 255), 1.2f * FridaTheme.Factor))
                    g.DrawEllipse(rPen2, _rippleOrigin.X - rw2, _rippleOrigin.Y - rw2, rw2 * 2f, rw2 * 2f);
            }
        }

        private void DrawPromptChips(Graphics g, float w, float startY, float scale)
        {
            Font chipFont = FridaTheme.Font(9.5f); // shared cached font: never dispose
            {
                int chipH = (int)(32 * scale);
                int padX = (int)(14 * scale);
                int gap = (int)(10 * scale);

                int[] widths = new int[_chips.Length];
                int totalW = 0;
                for (int i = 0; i < _chips.Length; i++)
                {
                    string text = _chips[i].Icon + "  " + _chips[i].Label;
                    widths[i] = TextRenderer.MeasureText(text, chipFont).Width + padX * 2;
                    totalW += widths[i];
                    if (i > 0) totalW += gap;
                }

                if (totalW <= w - 32 * scale)
                {
                    int curX = (int)((w - totalW) * 0.5f);
                    for (int i = 0; i < _chips.Length; i++)
                    {
                        var chip = _chips[i];
                        chip.Bounds = new Rectangle(curX, (int)startY, widths[i], chipH);
                        DrawSingleChip(g, chip, chipFont, scale);
                        curX += widths[i] + gap;
                    }
                }
                else
                {
                    int row1W = widths[0] + widths[1] + gap;
                    int row2W = widths[2] + widths[3] + gap;

                    int curX = (int)((w - row1W) * 0.5f);
                    for (int i = 0; i < 2; i++)
                    {
                        var chip = _chips[i];
                        chip.Bounds = new Rectangle(curX, (int)startY, widths[i], chipH);
                        DrawSingleChip(g, chip, chipFont, scale);
                        curX += widths[i] + gap;
                    }

                    int row2Y = (int)(startY + chipH + 8 * scale);
                    curX = (int)((w - row2W) * 0.5f);
                    for (int i = 2; i < 4; i++)
                    {
                        var chip = _chips[i];
                        chip.Bounds = new Rectangle(curX, row2Y, widths[i], chipH);
                        DrawSingleChip(g, chip, chipFont, scale);
                        curX += widths[i] + gap;
                    }
                }
            }
        }

        private void DrawSingleChip(Graphics g, PromptChip chip, Font font, float scale)
        {
            Rectangle r = chip.Bounds;
            using (GraphicsPath path = FridaTheme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 14 * scale))
            {
                Color fill = chip.Hovered ? FridaTheme.Hover : FridaTheme.Surface;
                Color stroke = chip.Hovered ? FridaTheme.Highlight : FridaTheme.Edge;
                Color ink = chip.Hovered ? Color.White : FridaTheme.Ink;

                using (SolidBrush sb = new SolidBrush(fill))
                using (Pen pen = new Pen(stroke, Math.Max(1f, 1.2f * scale)))
                {
                    g.FillPath(sb, path);
                    g.DrawPath(pen, path);
                }

                string text = chip.Icon + "  " + chip.Label;
                TextRenderer.DrawText(g, text, font, r, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawFridaLogo(Graphics g, float cx, float cy, float R)
        {
            float pulse = (float)Math.Sin(_time * 2.4f) * 0.5f + 0.5f;
            float pulseScale = 1.0f + _smoothEnergy * 0.12f + pulse * 0.04f;
            float masterAlpha = 1.0f;

            // 1. Volumetric Atmospheric Glowing Core
            DrawAtmosphericGlow(g, cx, cy, R * 1.45f * pulseScale, masterAlpha, _smoothEnergy, pulse);

            // 2. Click shockwave: drawn by OnPaint in screen coordinates (DrawRipple).

            // 3. 3D Gyroscopic Architectural Wireframe Shells (4 counter-rotating planes)
            float rotScale = 1.0f + _smoothEnergy * 0.45f;
            DrawGyroShell(g, cx, cy, R * 1.14f * pulseScale, _time * 0.75f * rotScale + _tiltY * 0.8f, 0.55f + _tiltX * 0.8f, _time * 0.35f, 44, true, masterAlpha);
            DrawGyroShell(g, cx, cy, R * 0.95f * pulseScale, -_time * 0.95f * rotScale - _tiltY * 0.7f, 1.18f - _tiltX * 0.7f, -_time * 0.50f, 36, false, masterAlpha);
            DrawGyroShell(g, cx, cy, R * 0.75f * pulseScale, _time * 1.25f * rotScale + _tiltY, -0.78f + _tiltX, _time * 0.85f, 32, true, masterAlpha);
            DrawGyroShell(g, cx, cy, R * 0.56f * pulseScale, -_time * 1.65f * rotScale - _tiltY * 1.2f, 0.35f - _tiltX * 1.2f, -_time * 1.10f, 24, false, masterAlpha);

            // 4. Signature JARVIS/FRIDAY Segmented Holographic Ladder-Tracks
            DrawHolographicLadderTrack(g, cx, cy, R * 0.80f * pulseScale, R * 0.98f * pulseScale, _time * 0.62f * rotScale, 0.42f + _tiltX * 0.6f, 0.25f + _tiltY * 0.6f, masterAlpha);
            DrawHolographicLadderTrack(g, cx, cy, R * 0.60f * pulseScale, R * 0.74f * pulseScale, -_time * 0.84f * rotScale, -0.95f - _tiltX * 0.6f, 0.72f - _tiltY * 0.6f, masterAlpha);

            // 5. 3D Swirling Neural Particles & Connecting Synaptic Filaments
            DrawNeuralParticles(g, cx, cy, R, pulseScale, masterAlpha);

            // 6. Inner Rotating Singularity C-Rings & Plasma Core
            DrawInnerSingularityCore(g, cx, cy, pulseScale, masterAlpha);
        }

        private void DrawAtmosphericGlow(Graphics g, float cx, float cy, float radius, float alpha, float energy, float pulse)
        {
            if (radius < 10f) return;
            RectangleF rect = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(rect);
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    int coreA = ClampByte((70f + energy * 80f + pulse * 25f) * alpha);
                    pgb.CenterPoint = new PointF(cx, cy);
                    pgb.CenterColor = Color.FromArgb(coreA, 0, 160, 255);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 12, 20, 36) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        private void DrawGyroShell(Graphics g, float cx, float cy, float radius, float rotX, float rotY, float rotZ, int segments, bool brokenArcs, float alpha)
        {
            PointF prev = PointF.Empty;
            float prevZ = 0f;
            bool hasPrev = false;

            for (int i = 0; i <= segments; i++)
            {
                if (brokenArcs && ((i / 4) % 3 == 1))
                {
                    hasPrev = false;
                    continue;
                }

                float ang = (float)(i * 2.0 * Math.PI / segments);
                float wave = 1.0f + 0.04f * (float)Math.Sin(ang * 3f + _time * 4f);
                float waveR = radius * wave;

                float x = (float)Math.Cos(ang) * waveR;
                float y = (float)Math.Sin(ang) * waveR;
                float z = 0f;

                Rotate3D(ref x, ref y, ref z, rotX, rotY, rotZ);

                float fov = 340f;
                float persp = fov / (fov + z + 160f);
                PointF cur = new PointF(cx + x * persp, cy + y * persp);

                if (hasPrev)
                {
                    float avgZ = (prevZ + z) * 0.5f;
                    bool front = avgZ >= 0f;
                    int a = ClampByte((front ? 195f : 60f) * alpha);
                    Color col = front
                        ? Color.FromArgb(a, 35, 220, 255)
                        : Color.FromArgb(a, 0, 90, 210);
                    float penW = front ? (1.5f * FridaTheme.Factor) : (0.9f * FridaTheme.Factor);
                    using (Pen p = new Pen(col, penW))
                    {
                        g.DrawLine(p, prev, cur);
                    }
                }

                prev = cur;
                prevZ = z;
                hasPrev = true;
            }
        }

        private void DrawHolographicLadderTrack(Graphics g, float cx, float cy, float rInner, float rOuterBase, float rotX, float rotY, float rotZ, float alpha)
        {
            const int rungs = 36;
            PointF prevIn = PointF.Empty;
            PointF prevOut = PointF.Empty;
            bool hasArc = false;

            for (int i = 0; i <= rungs; i++)
            {
                int cluster = (i / 6) % 3;
                if (cluster == 2)
                {
                    hasArc = false;
                    continue;
                }

                float ang = (float)(i * 2.0 * Math.PI / rungs);
                float dynamicOut = rOuterBase + (float)(Math.Sin(_time * 3f + i * 0.4f) * 3f * FridaTheme.Factor);

                float ix = (float)Math.Cos(ang) * rInner;
                float iy = (float)Math.Sin(ang) * rInner;
                float iz = 0f;

                float ox = (float)Math.Cos(ang) * dynamicOut;
                float oy = (float)Math.Sin(ang) * dynamicOut;
                float oz = 0f;

                Rotate3D(ref ix, ref iy, ref iz, rotX, rotY, rotZ);
                Rotate3D(ref ox, ref oy, ref oz, rotX, rotY, rotZ);

                float fov = 340f;
                float pIn = fov / (fov + iz + 160f);
                float pOut = fov / (fov + oz + 160f);

                PointF ptIn = new PointF(cx + ix * pIn, cy + iy * pIn);
                PointF ptOut = new PointF(cx + ox * pOut, cy + oy * pOut);

                bool front = oz >= -10f;
                int rungAlpha = ClampByte((front ? 170f : 45f) * alpha);
                Color rungColor = front
                    ? Color.FromArgb(rungAlpha, 95, 235, 255)
                    : Color.FromArgb(rungAlpha, 0, 105, 225);

                using (Pen rungPen = new Pen(rungColor, (front ? 1.4f : 0.8f) * FridaTheme.Factor))
                {
                    g.DrawLine(rungPen, ptIn, ptOut);
                    if (hasArc)
                    {
                        g.DrawLine(rungPen, prevIn, ptIn);
                        g.DrawLine(rungPen, prevOut, ptOut);
                    }
                }

                prevIn = ptIn;
                prevOut = ptOut;
                hasArc = true;
            }
        }

        private void DrawNeuralParticles(Graphics g, float cx, float cy, float R, float pulseScale, float alpha)
        {
            int n = _particles.Length;

            for (int i = 0; i < n; i++)
            {
                Particle3D p = _particles[i];
                float harmonic = (float)(Math.Sin(_time * 3.8f + p.phase) * Math.Cos(_time * 2.4f - p.ny * 3.5f));
                float baseR = p.shellRadius * R;
                float r = (baseR + (_smoothEnergy * 14f * FridaTheme.Factor) * (0.5f + 0.5f * harmonic)) * pulseScale;

                float x = p.nx * r;
                float y = p.ny * r;
                float z = p.nz * r;

                Rotate3D(ref x, ref y, ref z, _time * 0.45f + _tiltY * 0.5f, _time * 0.82f + _tiltX * 0.5f, _time * 0.28f);

                float fov = 340f;
                float s = fov / (fov + z + 160f);
                _projNodes[i].sx = cx + x * s;
                _projNodes[i].sy = cy + y * s;
                _projNodes[i].z = z;
                _projNodes[i].scale = s;
            }

            float maxDist = 32f * FridaTheme.Factor;
            float maxDistSq = maxDist * maxDist;

            for (int i = 0; i < n; i += 2)
            {
                if (_projNodes[i].z < -20f) continue;
                int next = (i + 5) % n;
                float dx = _projNodes[i].sx - _projNodes[next].sx;
                float dy = _projNodes[i].sy - _projNodes[next].sy;
                float distSq = dx * dx + dy * dy;
                if (distSq < maxDistSq)
                {
                    int lineA = ClampByte((1f - distSq / maxDistSq) * 110f * alpha);
                    using (Pen synPen = new Pen(Color.FromArgb(lineA, 45, 215, 255), 0.9f * FridaTheme.Factor))
                    {
                        g.DrawLine(synPen, _projNodes[i].sx, _projNodes[i].sy, _projNodes[next].sx, _projNodes[next].sy);
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                ProjectedNode pn = _projNodes[i];
                bool front = pn.z >= 0f;
                float sz = Math.Max(1.5f, _particles[i].size * pn.scale * (1f + _smoothEnergy * 0.35f) * FridaTheme.Factor);
                int a = ClampByte((front ? 240f : 75f) * alpha);

                Color c = front
                    ? ((i % 4 == 0) ? Color.FromArgb(a, 220, 253, 255) : Color.FromArgb(a, 40, 225, 255))
                    : Color.FromArgb(a, 0, 110, 240);

                using (SolidBrush sb = new SolidBrush(c))
                {
                    g.FillEllipse(sb, pn.sx - sz * 0.5f, pn.sy - sz * 0.5f, sz, sz);
                }
            }
        }

        private void DrawInnerSingularityCore(Graphics g, float cx, float cy, float pulseScale, float alpha)
        {
            float coreR = (16f + _smoothEnergy * 10f) * pulseScale * FridaTheme.Factor;

            for (int ring = 0; ring < 3; ring++)
            {
                float rr = coreR + (6f + ring * 7f) * FridaTheme.Factor;
                float startAng = (_time * (ring % 2 == 0 ? 150f : -180f) + ring * 65f) % 360f;
                float sweep = 210f;
                int arcA = ClampByte((220 - ring * 45) * alpha);
                using (Pen arcPen = new Pen(Color.FromArgb(arcA, 115, 240, 255), (2.0f - ring * 0.4f) * FridaTheme.Factor))
                {
                    g.DrawArc(arcPen, cx - rr, cy - rr, rr * 2f, rr * 2f, startAng, sweep);
                }
            }

            RectangleF glowRect = new RectangleF(cx - coreR * 1.5f, cy - coreR * 1.5f, coreR * 3f, coreR * 3f);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(glowRect);
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb(ClampByte(245f * alpha), 220, 253, 255);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 195, 255) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        private static void Rotate3D(ref float x, ref float y, ref float z, float rx, float ry, float rz)
        {
            float cosX = (float)Math.Cos(rx), sinX = (float)Math.Sin(rx);
            float y1 = y * cosX - z * sinX;
            float z1 = y * sinX + z * cosX;

            float cosY = (float)Math.Cos(ry), sinY = (float)Math.Sin(ry);
            float x2 = x * cosY + z1 * sinY;
            float z2 = -x * sinY + z1 * cosY;

            float cosZ = (float)Math.Cos(rz), sinZ = (float)Math.Sin(rz);
            float x3 = x2 * cosZ - y1 * sinZ;
            float y3 = x2 * sinZ + y1 * cosZ;

            x = x3; y = y3; z = z2;
        }

        private static int ClampByte(float val)
        {
            if (val < 0f) return 0;
            if (val > 255f) return 255;
            return (int)val;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopAnimation();
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
