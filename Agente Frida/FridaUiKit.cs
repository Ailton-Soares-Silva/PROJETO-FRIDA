using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FridaAgent
{
    // Design tokens and helpers shared by every IDE surface. One scale factor (system DPI, or a
    // test override) drives sizes and pixel-unit fonts, so 100/150/200% stay proportional.
    internal static class FridaTheme
    {
        public static readonly Color Canvas = Color.FromArgb(12, 20, 36);
        public static readonly Color Surface = Color.FromArgb(18, 29, 48);
        public static readonly Color SurfaceRaised = Color.FromArgb(25, 40, 64);
        public static readonly Color Hover = Color.FromArgb(33, 54, 84);
        public static readonly Color Bubble = Color.FromArgb(22, 62, 92);
        public static readonly Color Edge = Color.FromArgb(57, 83, 113);
        // Input borders: ≈3.2:1 against Canvas/Surface (WCAG 1.4.11 non-text contrast).
        public static readonly Color EdgeStrong = Color.FromArgb(74, 104, 140);
        public static readonly Color Divider = Color.FromArgb(34, 50, 76);
        public static readonly Color Warning = Color.FromArgb(255, 184, 92);
        public static readonly Color Ink = Color.FromArgb(225, 240, 255);
        public static readonly Color Muted = Color.FromArgb(160, 184, 210);
        public static readonly Color Accent = Color.FromArgb(0, 155, 255);
        public static readonly Color AccentHover = Color.FromArgb(48, 178, 255);
        public static readonly Color AccentPressed = Color.FromArgb(0, 126, 214);
        public static readonly Color Highlight = Color.FromArgb(90, 235, 255);
        public static readonly Color Coral = Color.FromArgb(255, 145, 138);
        public static readonly Color Success = Color.FromArgb(86, 214, 150);

        public const string GlyphMenu = "", GlyphAdd = "", GlyphMore = "", GlyphSettings = "", GlyphClose = "",
            GlyphSearch = "", GlyphChat = "", GlyphCode = "", GlyphPeople = "", GlyphFolder = "", GlyphGlobe = "",
            GlyphLink = "", GlyphEdit = "", GlyphDelete = "", GlyphCopy = "", GlyphSpeak = "", GlyphStop = "",
            GlyphUp = "", GlyphRefresh = "", GlyphSave = "", GlyphPlay = "", GlyphNewFolder = "", GlyphDocument = "",
            GlyphChevronRight = "", GlyphChevronDown = "", GlyphPaneOpen = "", GlyphPhone = "", GlyphChart = "",
            GlyphExport = "", GlyphStar = "", GlyphTerminal = "", GlyphCompare = "", GlyphHistory = "", GlyphCheck = "";

        // Tests render 150%/200% without changing Windows settings.
        public static float ScaleOverride;
        private static float _system;
        public static float Factor
        {
            get
            {
                if (ScaleOverride > 0) return ScaleOverride;
                return SystemFactor;
            }
        }
        // Scale Windows itself applies to point/twip units (RichEdit renders those at system DPI).
        public static float SystemFactor
        {
            get
            {
                if (_system <= 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) _system = Math.Max(1f, g.DpiX / 96f);
                return _system;
            }
        }
        public static int Px(int value) { return (int)Math.Round(value * Factor); }

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        private static string _iconFamily;
        public static string IconFamily
        {
            get
            {
                if (_iconFamily != null) return _iconFamily;
                try { using (new FontFamily("Segoe Fluent Icons")) _iconFamily = "Segoe Fluent Icons"; }
                catch (ArgumentException) { _iconFamily = "Segoe MDL2 Assets"; }
                return _iconFamily;
            }
        }
        private static string _monoFamily;
        public static string MonoFamily
        {
            get
            {
                if (_monoFamily != null) return _monoFamily;
                try { using (new FontFamily("Cascadia Mono")) _monoFamily = "Cascadia Mono"; }
                catch (ArgumentException) { _monoFamily = "Consolas"; }
                return _monoFamily;
            }
        }
        // Pixel-unit fonts follow Factor exactly (points would ignore the test override).
        public static Font Font(float points, FontStyle style = FontStyle.Regular, string family = "Segoe UI")
        {
            float pixels = (float)Math.Round(points * 96f / 72f * Factor, 1);
            string key = family + "|" + pixels + "|" + (int)style;
            lock (Fonts)
            {
                Font font;
                bool valid = false;
                if (Fonts.TryGetValue(key, out font) && font != null)
                {
                    try { var _ = font.Height; valid = true; } catch { valid = false; }
                }
                if (!valid)
                {
                    font = new Font(family, pixels, style, GraphicsUnit.Pixel);
                    Fonts[key] = font;
                }
                return font;
            }
        }
        public static Font Strong(float points) { return Font(points, FontStyle.Regular, "Segoe UI Semibold"); }
        public static Font Icons(float points) { return Font(points, FontStyle.Regular, IconFamily); }
        public static Font Mono(float points) { return Font(points, FontStyle.Regular, MonoFamily); }

        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        public static void FillRound(Graphics g, Rectangle rect, int radius, Color fill, Color stroke = default(Color), float strokeWidth = 1)
        {
            if (rect.Width < 2 || rect.Height < 2) return;
            var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius))
            {
                if (fill.A > 0) using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (stroke.A > 0) using (var pen = new Pen(stroke, strokeWidth)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = mode;
        }
        public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text ?? "", font, bounds, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        public static Color Effective(Control control)
        {
            for (var c = control; c != null; c = c.Parent) if (c.BackColor.A == 255) return c.BackColor;
            return Canvas;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        private static void OnHandle(Control control, Action<IntPtr> action)
        {
            if (control.IsHandleCreated) action(control.Handle);
            control.HandleCreated += delegate { action(control.Handle); };
        }
        // Dark title bar and rounded window corners (Windows 11); ignored where unsupported.
        public static void DarkChrome(Control window)
        {
            OnHandle(window, handle => { int on = 1, round = 2; DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)); DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); });
        }
        public static void DarkScroll(Control control) { OnHandle(control, handle => SetWindowTheme(handle, "DarkMode_Explorer", null)); }
        public static void Cue(TextBox box, string text) { OnHandle(box, handle => SendMessage(handle, 0x1501, (IntPtr)1, text)); }
        public static void DoubleBufferTree(TreeView tree) { OnHandle(tree, handle => SendMessage(handle, 0x112C, (IntPtr)0x0004, (IntPtr)0x0004)); }

        // Suspends painting of a visible control and its children (WM_SETREDRAW) while it is rebuilt,
        // then repaints once. Nested freezes of the same control are no-ops.
        private static readonly HashSet<Control> Frozen = new HashSet<Control>();
        public static IDisposable Freeze(Control control)
        {
            if (control == null || !control.IsHandleCreated || !control.Visible || Frozen.Contains(control)) return new Thaw(null);
            Frozen.Add(control);
            SendMessage(control.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            return new Thaw(control);
        }
        private sealed class Thaw : IDisposable
        {
            private Control _control;
            public Thaw(Control control) { _control = control; }
            public void Dispose()
            {
                var c = _control; _control = null;
                if (c == null) return;
                Frozen.Remove(c);
                if (c.IsDisposed || !c.IsHandleCreated) return;
                SendMessage(c.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
                c.Invalidate(true);
            }
        }

        private static ToolTip _tips;
        public static ToolTip Tips
        {
            get
            {
                if (_tips != null) return _tips;
                _tips = new ToolTip { OwnerDraw = true, InitialDelay = 400, ReshowDelay = 100 };
                _tips.Popup += (sender, e) =>
                {
                    string text = _tips.GetToolTip(e.AssociatedControl);
                    var size = TextRenderer.MeasureText(text, Font(9.5f));
                    e.ToolTipSize = new Size(size.Width + Px(16), size.Height + Px(10));
                };
                _tips.Draw += (sender, e) =>
                {
                    e.Graphics.Clear(SurfaceRaised);
                    using (var pen = new Pen(Edge)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                    Text(e.Graphics, e.ToolTipText, Font(9.5f), e.Bounds, Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
                return _tips;
            }
        }
        public static void Tip(Control control, string text) { Tips.SetToolTip(control, text); }

        public static void Style(ToolStrip strip)
        {
            strip.Renderer = new FridaMenuRenderer();
            strip.BackColor = SurfaceRaised; strip.ForeColor = Ink; strip.Font = Font(10);
            strip.Padding = new Padding(Px(4));
            var dropDown = strip as ToolStripDropDown;
            if (dropDown != null) DarkChrome(dropDown);
            foreach (ToolStripItem item in strip.Items) StyleItem(item);
            strip.ItemAdded += (sender, e) => StyleItem(e.Item);
        }
        private static void StyleItem(ToolStripItem item)
        {
            item.ForeColor = Ink; item.Padding = new Padding(Px(4), Px(5), Px(8), Px(5));
            var menu = item as ToolStripMenuItem;
            if (menu != null && !(menu.DropDown.Renderer is FridaMenuRenderer)) Style(menu.DropDown);
        }
        // Applied to pages and dialogs: only replaces Windows default colors, never deliberate ones.
        public static void Apply(Control control)
        {
            if (IsSystem(control.BackColor)) control.BackColor = control is TextBoxBase || control is ListBox || control is ComboBox ? SurfaceRaised : Canvas;
            if (IsSystem(control.ForeColor)) control.ForeColor = Ink;
            var text = control as TextBox; if (text != null && text.BorderStyle == BorderStyle.Fixed3D) text.BorderStyle = BorderStyle.FixedSingle;
            var list = control as ListBox; if (list != null) { list.BorderStyle = BorderStyle.None; DarkScroll(list); }
            var combo = control as ComboBox; if (combo != null) combo.FlatStyle = FlatStyle.Flat;
            var check = control as CheckBox; if (check != null) check.FlatStyle = FlatStyle.Flat;
            if (control is RichTextBox || control is TreeView) DarkScroll(control);
            if (control.ContextMenuStrip != null && !(control.ContextMenuStrip.Renderer is FridaMenuRenderer)) Style(control.ContextMenuStrip);
            foreach (Control child in control.Controls) Apply(child);
        }
        public static bool IsSystem(Color color)
        {
            return color.IsSystemColor || color == Color.White || color == Color.Empty || color.ToArgb() == SystemColors.Control.ToArgb() || color.ToArgb() == SystemColors.Window.ToArgb();
        }
        // Owner-drawn rows for file and artifact lists (icon, title, optional subtitle).
        public static void StyleList(ListBox list, Func<object, string[]> describe, int rowHeight = 32)
        {
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = Px(rowHeight); list.BorderStyle = BorderStyle.None;
            list.BackColor = Canvas; list.ForeColor = Ink; list.IntegralHeight = false; DarkScroll(list);
            list.DrawItem += (sender, e) =>
            {
                e.Graphics.FillRectangle(new SolidBrush(Canvas), e.Bounds);
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                var row = new Rectangle(e.Bounds.X + Px(4), e.Bounds.Y + Px(1), e.Bounds.Width - Px(8), e.Bounds.Height - Px(2));
                if (selected) FillRound(e.Graphics, row, Px(8), list.Focused ? Bubble : Hover);
                string[] parts = describe(list.Items[e.Index]) ?? new string[0];
                string glyph = parts.Length > 0 ? parts[0] : null, title = parts.Length > 1 ? parts[1] : list.Items[e.Index].ToString(), detail = parts.Length > 2 ? parts[2] : null;
                int x = row.X + Px(8);
                if (!string.IsNullOrEmpty(glyph)) { Text(e.Graphics, glyph, Icons(10.5f), new Rectangle(x, row.Y, Px(20), row.Height), selected ? Highlight : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter); x += Px(28); }
                var titleBounds = new Rectangle(x, row.Y, row.Right - x - Px(8), detail == null ? row.Height : row.Height / 2 + Px(2));
                Text(e.Graphics, title, Font(10), titleBounds, Ink, (detail == null ? TextFormatFlags.VerticalCenter : TextFormatFlags.Bottom) | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (detail != null) Text(e.Graphics, detail, Font(8.5f), new Rectangle(x, titleBounds.Bottom, titleBounds.Width, row.Bottom - titleBounds.Bottom), Muted, TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            };
            list.SelectedIndexChanged += delegate { list.Invalidate(); };
            list.GotFocus += delegate { list.Invalidate(); }; list.LostFocus += delegate { list.Invalidate(); };
        }
        public static string RelativeTime(string utc)
        {
            DateTime at;
            if (!DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at)) return "";
            at = at.ToLocalTime(); var today = DateTime.Today;
            if (at.Date == today) return at.ToString("HH:mm");
            if (at.Date == today.AddDays(-1)) return "ontem";
            if (at.Date > today.AddDays(-7)) return at.ToString("ddd", new System.Globalization.CultureInfo("pt-BR")).TrimEnd('.');
            return at.ToString("dd/MM");
        }
    }

    internal sealed class FridaMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return FridaTheme.SurfaceRaised; } }
        public override Color ImageMarginGradientBegin { get { return FridaTheme.SurfaceRaised; } }
        public override Color ImageMarginGradientMiddle { get { return FridaTheme.SurfaceRaised; } }
        public override Color ImageMarginGradientEnd { get { return FridaTheme.SurfaceRaised; } }
        public override Color MenuBorder { get { return FridaTheme.Edge; } }
        public override Color MenuItemBorder { get { return FridaTheme.Hover; } }
        public override Color MenuItemSelected { get { return FridaTheme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return FridaTheme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return FridaTheme.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return FridaTheme.Bubble; } }
        public override Color MenuItemPressedGradientEnd { get { return FridaTheme.Bubble; } }
        public override Color SeparatorDark { get { return FridaTheme.Divider; } }
        public override Color SeparatorLight { get { return FridaTheme.Divider; } }
        public override Color CheckBackground { get { return FridaTheme.Bubble; } }
        public override Color CheckSelectedBackground { get { return FridaTheme.Bubble; } }
        public override Color CheckPressedBackground { get { return FridaTheme.Bubble; } }
    }

    internal sealed class FridaMenuRenderer : ToolStripProfessionalRenderer
    {
        public FridaMenuRenderer() : base(new FridaMenuColors()) { RoundedEdges = true; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? FridaTheme.Ink : FridaTheme.Muted; e.TextFont = FridaTheme.Font(10);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            FridaTheme.FillRound(e.Graphics, new Rectangle(FridaTheme.Px(2), 0, e.Item.Width - FridaTheme.Px(4), e.Item.Height), FridaTheme.Px(6), FridaTheme.Hover);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = FridaTheme.Muted; base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(FridaTheme.Divider)) e.Graphics.DrawLine(pen, FridaTheme.Px(8), y, e.Item.Width - FridaTheme.Px(8), y);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            FridaTheme.Text(e.Graphics, FridaTheme.GlyphCheck, FridaTheme.Icons(9), e.ImageRectangle, FridaTheme.Highlight, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    internal enum PillKind { Primary, Secondary, Ghost, Danger, Icon }

    // Rounded button with a clear hierarchy (primary, secondary, ghost, danger, icon), sized from
    // its content; 36 px regular and 32 px compact targets at 100%.
    internal sealed class PillButton : Button
    {
        private bool _hover, _pressed;
        private PillKind _kind;
        private string _glyph;
        public bool Compact { get; private set; }
        public PillKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }
        // Background behind the pill when the parent's own color is not what surrounds it (a bubble
        // drawn inside a card with transparent-looking corners). Empty = the parent's color.
        public Color Underlay = Color.Empty;
        public string Glyph { get { return _glyph; } set { _glyph = value; Fit(); Invalidate(); } }
        public PillButton(string text, PillKind kind = PillKind.Secondary, string glyph = null, bool compact = false)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
            _kind = kind; _glyph = glyph; Compact = compact; Margin = Padding.Empty;
            Font = kind == PillKind.Primary ? FridaTheme.Strong(compact ? 9.5f : 10) : FridaTheme.Font(compact ? 9.5f : 10);
            Text = text ?? ""; AccessibleName = Text;
            Fit();
        }
        public void Invoke() { OnClick(EventArgs.Empty); }
        private bool IconOnly { get { return _kind == PillKind.Icon || string.IsNullOrEmpty(Text); } }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int h = FridaTheme.Px(Compact ? 32 : 36);
            if (IconOnly) return new Size(h, h);
            int text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int glyph = string.IsNullOrEmpty(_glyph) ? 0 : FridaTheme.Px(22);
            return new Size(text + glyph + FridaTheme.Px(Compact ? 20 : 28), h);
        }
        private void Fit() { Size = GetPreferredSize(Size.Empty); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text.Length > 0 ? Text : AccessibleName; Fit(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Underlay.A == 255 ? Underlay : FridaTheme.Effective(Parent));
            Color fill, ink, stroke = Color.Empty;
            switch (_kind)
            {
                case PillKind.Primary:
                    fill = !Enabled ? FridaTheme.Edge : _pressed ? FridaTheme.AccentPressed : _hover ? FridaTheme.AccentHover : FridaTheme.Accent;
                    ink = FridaTheme.Canvas; break;
                case PillKind.Secondary:
                    fill = _pressed ? FridaTheme.Bubble : _hover ? FridaTheme.Hover : FridaTheme.SurfaceRaised;
                    stroke = FridaTheme.Edge; ink = FridaTheme.Ink; break;
                case PillKind.Danger:
                    fill = _pressed ? Color.FromArgb(90, 40, 48) : _hover ? Color.FromArgb(70, 34, 44) : Color.Empty;
                    stroke = Color.FromArgb(150, FridaTheme.Coral); ink = FridaTheme.Coral; break;
                default:
                    fill = _pressed ? FridaTheme.Bubble : _hover ? FridaTheme.Hover : Color.Empty;
                    ink = _kind == PillKind.Icon && !_hover ? FridaTheme.Muted : FridaTheme.Ink; break;
            }
            if (!Enabled && _kind != PillKind.Primary) ink = Color.FromArgb(110, 130, 155);
            int radius = FridaTheme.Px(IconOnly ? 8 : 10);
            var box = new Rectangle(0, 0, Width, Height);
            FridaTheme.FillRound(g, box, radius, fill, stroke);
            if (Focused && ShowFocusCues)
                FridaTheme.FillRound(g, Rectangle.Inflate(box, -1, -1), radius, Color.Empty, _kind == PillKind.Primary ? FridaTheme.Ink : FridaTheme.Highlight, Math.Max(1.5f, 2 * FridaTheme.Factor));
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (IconOnly)
            { FridaTheme.Text(g, _glyph ?? "", FridaTheme.Icons(Compact ? 10 : 11), box, ink, flags | TextFormatFlags.HorizontalCenter); return; }
            int glyphWidth = string.IsNullOrEmpty(_glyph) ? 0 : FridaTheme.Px(22);
            int textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int start = Math.Max(FridaTheme.Px(8), (Width - glyphWidth - textWidth) / 2);
            if (glyphWidth > 0) FridaTheme.Text(g, _glyph, FridaTheme.Icons(Compact ? 9.5f : 10), new Rectangle(start, 0, glyphWidth, Height), ink, flags);
            FridaTheme.Text(g, Text, Font, new Rectangle(start + glyphWidth, 0, Math.Max(1, Width - start - glyphWidth - FridaTheme.Px(6)), Height), ink, flags | TextFormatFlags.EndEllipsis);
        }
    }

    // Non-interactive (or lightly interactive) rounded label: section, project, links, state.
    internal sealed class Chip : Control
    {
        private bool _hover;
        public string Glyph;
        public Color Tint = FridaTheme.Muted;
        public bool Clickable;
        public Chip(string text, string glyph = null)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Text = text; Glyph = glyph; Font = FridaTheme.Font(9); Height = FridaTheme.Px(30); TabStop = false; AccessibleName = text;
            Fit();
        }
        public void Fit()
        {
            int text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            // Long project names end in an ellipsis instead of pushing the title out.
            Size = new Size(Math.Min(FridaTheme.Px(260), text + (string.IsNullOrEmpty(Glyph) ? 0 : FridaTheme.Px(18)) + FridaTheme.Px(18)), FridaTheme.Px(30));
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Fit(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = Clickable; Cursor = Clickable ? Cursors.Hand : Cursors.Default; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(FridaTheme.Effective(Parent));
            FridaTheme.FillRound(e.Graphics, new Rectangle(0, 0, Width, Height), Height / 2, _hover ? FridaTheme.Hover : FridaTheme.SurfaceRaised, Color.FromArgb(120, Tint));
            int x = FridaTheme.Px(9);
            if (!string.IsNullOrEmpty(Glyph)) { FridaTheme.Text(e.Graphics, Glyph, FridaTheme.Icons(8.5f), new Rectangle(x, 0, FridaTheme.Px(16), Height), Tint, TextFormatFlags.VerticalCenter); x += FridaTheme.Px(18); }
            FridaTheme.Text(e.Graphics, Text, Font, new Rectangle(x, 0, Math.Max(1, Width - x - FridaTheme.Px(6)), Height), FridaTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    // Single-row toolbar: left and right groups, never wraps and never overlaps. Buttons that do
    // not fit move, in order from the end, to a "more" menu.
    internal sealed class ActionBar : Panel
    {
        private readonly List<Control> _right = new List<Control>();
        private readonly HashSet<Control> _overflowed = new HashSet<Control>();
        private readonly PillButton _more;
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private bool _arranging;
        private static readonly System.Reflection.MethodInfo GetState = typeof(Control).GetMethod("GetState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        public bool Centered;
        public ActionBar(bool compact = false)
        {
            Dock = DockStyle.Top; Height = FridaTheme.Px(compact ? 44 : 52);
            Padding = new Padding(FridaTheme.Px(8), 0, FridaTheme.Px(8), 0);
            _more = new PillButton("", PillKind.Icon, FridaTheme.GlyphMore, true) { Visible = false, AccessibleName = "Mais ações" };
            FridaTheme.Tip(_more, "Mais ações");
            _more.Click += delegate
            {
                _menu.Items.Clear();
                foreach (Control control in Controls.Cast<Control>().Where(c => _overflowed.Contains(c)))
                {
                    var button = control as PillButton; if (button == null) continue;
                    var item = _menu.Items.Add(button.Text.Length > 0 ? button.Text : button.AccessibleName);
                    item.Enabled = button.Enabled; item.Click += delegate { button.Invoke(); };
                }
                _menu.Show(_more, new Point(0, _more.Height));
            };
            FridaTheme.Style(_menu);
            Controls.Add(_more);
            Disposed += delegate { _menu.Dispose(); };
        }
        public void AddRight(Control control) { _right.Add(control); Controls.Add(control); }
        private static bool Wanted(Control c)
        {
            try { return GetState != null ? (bool)GetState.Invoke(c, new object[] { 2 }) : c.Visible; } catch (System.Reflection.TargetInvocationException) { return c.Visible; }
        }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); PerformLayout(); }
        protected override void OnControlRemoved(ControlEventArgs e) { base.OnControlRemoved(e); _right.Remove(e.Control); _overflowed.Remove(e.Control); }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_arranging || _more == null) return; // the constructor sizes the bar before its fields exist
            _arranging = true; SuspendLayout();
            try
            {
                foreach (var c in _overflowed) c.Visible = true;
                _overflowed.Clear(); _more.Visible = false;
                int gap = FridaTheme.Px(6), height = ClientSize.Height;
                int left = Padding.Left, right = ClientSize.Width - Padding.Right;
                Func<Control, Size> size = c => { var b = c as Button; return b != null ? b.GetPreferredSize(Size.Empty) : c.Size; };
                foreach (var c in Enumerable.Reverse(_right).Where(Wanted))
                {
                    var s = size(c); c.SetBounds(right - s.Width, Math.Max(0, (height - s.Height) / 2), s.Width, s.Height); right -= s.Width + gap;
                }
                var items = Controls.Cast<Control>().Where(c => c != _more && !_right.Contains(c) && Wanted(c)).ToList();
                int moreWidth = _more.GetPreferredSize(Size.Empty).Width;
                Func<int> needed = () => items.Where(c => !_overflowed.Contains(c)).Sum(c => size(c).Width + gap) - gap + (_overflowed.Count > 0 ? moreWidth + gap : 0);
                for (int i = items.Count - 1; i >= 0 && needed() > right - left; i--)
                    if (items[i] is Button) _overflowed.Add(items[i]);
                foreach (var c in _overflowed) c.Visible = false;
                var shown = items.Where(c => !_overflowed.Contains(c)).ToList();
                int total = shown.Sum(c => size(c).Width + gap) - gap;
                int x = Centered ? Math.Max(left, (ClientSize.Width - total) / 2) : left;
                foreach (var c in shown)
                {
                    var s = size(c);
                    int width = Math.Max(FridaTheme.Px(60), Math.Min(s.Width, right - x - (_overflowed.Count > 0 ? moreWidth + gap : 0)));
                    c.SetBounds(x, Math.Max(0, (height - s.Height) / 2), width, s.Height); x += width + gap;
                }
                if (_overflowed.Count > 0)
                {
                    _more.Visible = true; var s = _more.GetPreferredSize(Size.Empty);
                    _more.SetBounds(Math.Min(x, right - s.Width), (height - s.Height) / 2, s.Width, s.Height);
                }
            }
            finally { ResumeLayout(false); _arranging = false; }
        }
    }

    // Drag handles for the side panels: a thin divider that lights up on hover and while dragging,
    // with a 6 px (scaled) grab area. Double-click restores the default size.
    internal static class FridaResize
    {
        public static void Style(Splitter splitter, Color rest)
        {
            splitter.BackColor = rest; splitter.Cursor = Cursors.VSplit; splitter.TabStop = false;
            splitter.MouseEnter += delegate { splitter.BackColor = FridaTheme.Edge; };
            splitter.MouseLeave += delegate { if (Control.MouseButtons != MouseButtons.Left) splitter.BackColor = rest; };
            splitter.SplitterMoved += delegate { splitter.BackColor = rest; };
            splitter.Paint += (sender, e) =>
            {
                using (var pen = new Pen(splitter.BackColor == rest ? FridaTheme.Divider : FridaTheme.Highlight))
                    e.Graphics.DrawLine(pen, splitter.Width / 2, 0, splitter.Width / 2, splitter.Height);
            };
        }
        public static void Style(SplitContainer split)
        {
            Color rest = split.BackColor;
            split.MouseMove += (sender, e) =>
            {
                bool over = split.SplitterRectangle.Contains(e.Location);
                var wanted = over || Control.MouseButtons == MouseButtons.Left ? FridaTheme.Edge : rest;
                if (split.BackColor != wanted) split.BackColor = wanted;
            };
            split.MouseLeave += delegate { if (Control.MouseButtons != MouseButtons.Left) split.BackColor = rest; };
            split.SplitterMoved += delegate { split.BackColor = rest; };
        }
    }

    // TabControl whose header strip is hidden: pages fill the control and a SegmentedTabs or
    // custom strip drives selection (keeps TabPage semantics for existing code).
    internal sealed class HeadlessTabControl : TabControl
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x1328 && !DesignMode) { m.Result = (IntPtr)1; return; } // TCM_ADJUSTRECT
            base.WndProc(ref m);
        }
    }

    internal sealed class SegmentedTabs : Control
    {
        private readonly List<string> _items = new List<string>();
        private int _selected = -1, _hover = -1;
        private TabControl _bound;
        public bool Stretch = true;
        public event EventHandler SelectedIndexChanged;
        public SegmentedTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Font = FridaTheme.Font(9.5f); Height = FridaTheme.Px(34); TabStop = true; AccessibleRole = AccessibleRole.PageTabList;
        }
        public IList<string> Items { get { return _items.AsReadOnly(); } }
        public void SetItems(IEnumerable<string> items) { _items.Clear(); _items.AddRange(items); if (_selected >= _items.Count) _selected = _items.Count - 1; if (_selected < 0 && _items.Count > 0) _selected = 0; Width = Stretch ? Width : GetPreferredSize(Size.Empty).Width; Invalidate(); }
        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < -1 || value >= _items.Count || value == _selected) return;
                _selected = value; AccessibleDescription = value >= 0 ? _items[value] : null; Invalidate();
                if (_bound != null && _bound.SelectedIndex != value && value >= 0) _bound.SelectedIndex = value;
                var handler = SelectedIndexChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
        }
        public string SelectedText { get { return _selected >= 0 ? _items[_selected] : null; } }
        public void Bind(TabControl tabs)
        {
            _bound = tabs;
            Action sync = () => { SetItems(tabs.TabPages.Cast<TabPage>().Select(p => p.Text)); _selected = tabs.SelectedIndex; Invalidate(); };
            sync();
            tabs.SelectedIndexChanged += delegate { if (_selected != tabs.SelectedIndex) { _selected = tabs.SelectedIndex; Invalidate(); var h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty); } };
            tabs.ControlAdded += delegate { sync(); }; tabs.ControlRemoved += delegate { BeginInvokeSafe(sync); };
        }
        private void BeginInvokeSafe(Action action) { if (IsHandleCreated) BeginInvoke(action); else action(); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = _items.Sum(i => TextRenderer.MeasureText(i, Font).Width + FridaTheme.Px(24)) + FridaTheme.Px(8);
            return new Size(width, FridaTheme.Px(34));
        }
        private Rectangle Segment(int index)
        {
            int pad = FridaTheme.Px(3), x = pad;
            if (Stretch)
            {
                int width = (Width - pad * 2) / Math.Max(1, _items.Count);
                return new Rectangle(pad + index * width, pad, width, Height - pad * 2);
            }
            for (int i = 0; i < index; i++) x += TextRenderer.MeasureText(_items[i], Font).Width + FridaTheme.Px(24);
            return new Rectangle(x, pad, TextRenderer.MeasureText(_items[index], Font).Width + FridaTheme.Px(24), Height - pad * 2);
        }
        private int Hit(Point p) { for (int i = 0; i < _items.Count; i++) if (Segment(i).Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int h = Hit(e.Location); if (h >= 0) { Focus(); SelectedIndex = h; } }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left && _selected > 0) SelectedIndex = _selected - 1;
            else if (e.KeyCode == Keys.Right && _selected < _items.Count - 1) SelectedIndex = _selected + 1;
            else if (e.KeyCode == Keys.Home && _items.Count > 0) SelectedIndex = 0;
            else if (e.KeyCode == Keys.End && _items.Count > 0) SelectedIndex = _items.Count - 1;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(FridaTheme.Effective(Parent));
            int radius = FridaTheme.Px(10);
            FridaTheme.FillRound(g, new Rectangle(0, 0, Width, Height), radius, FridaTheme.Surface, FridaTheme.Divider);
            for (int i = 0; i < _items.Count; i++)
            {
                var r = Segment(i);
                bool selected = i == _selected;
                if (selected) FridaTheme.FillRound(g, r, FridaTheme.Px(8), FridaTheme.Bubble, Focused ? FridaTheme.Highlight : Color.Empty);
                else if (i == _hover) FridaTheme.FillRound(g, r, FridaTheme.Px(8), FridaTheme.Hover);
                FridaTheme.Text(g, _items[i], selected ? FridaTheme.Strong(9.5f) : Font, r, selected ? FridaTheme.Ink : FridaTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    // Rounded input frame hosting a borderless TextBox (or ComboBox), with optional search glyph
    // and clear button. The hosted control keeps its own field and events.
    internal sealed class InputFrame : Panel
    {
        public readonly Control Input;
        private readonly bool _search;
        private readonly PillButton _clear;
        private readonly Label _placeholder;
        public InputFrame(Control input, string cue = null, bool search = false)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Input = input; _search = search; Height = FridaTheme.Px(36);
            input.BackColor = FridaTheme.SurfaceRaised; input.ForeColor = FridaTheme.Ink; input.Font = FridaTheme.Font(10); input.Dock = DockStyle.None;
            var box = input as TextBox; if (box != null) box.BorderStyle = BorderStyle.None;
            // Drawn placeholder: the native cue banner needs visual styles and hides on focus.
            if (cue != null && box != null)
            {
                _placeholder = new Label { Text = cue, ForeColor = FridaTheme.Muted, BackColor = FridaTheme.SurfaceRaised, Font = input.Font, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.IBeam };
                _placeholder.Click += delegate { input.Focus(); };
                Controls.Add(_placeholder);
                input.TextChanged += delegate { _placeholder.Visible = input.Text.Length == 0; };
                if (string.IsNullOrEmpty(box.AccessibleDescription)) box.AccessibleDescription = cue;
            }
            var combo = input as ComboBox; if (combo != null) combo.FlatStyle = FlatStyle.Flat;
            Controls.Add(input);
            if (search)
            {
                _clear = new PillButton("", PillKind.Icon, FridaTheme.GlyphClose, true) { Visible = false, AccessibleName = "Limpar pesquisa", TabStop = false };
                _clear.Click += delegate { input.Text = ""; input.Focus(); };
                Controls.Add(_clear);
                input.TextChanged += delegate { _clear.Visible = input.Text.Length > 0; PerformLayout(); };
            }
            input.GotFocus += delegate { Invalidate(); }; input.LostFocus += delegate { Invalidate(); };
            Click += delegate { input.Focus(); };
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int left = _search ? FridaTheme.Px(34) : FridaTheme.Px(12), right = FridaTheme.Px(10);
            if (_clear != null && _clear.Visible)
            {
                int s = Math.Min(FridaTheme.Px(32), Height - FridaTheme.Px(2));
                _clear.SetBounds(Width - s - FridaTheme.Px(2), (Height - s) / 2, s, s); right = s + FridaTheme.Px(6);
            }
            int h = Input.PreferredSize.Height;
            Input.SetBounds(left, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - left - right), h);
            if (_placeholder != null) { _placeholder.SetBounds(Input.Left, Input.Top, Input.Width, h); _placeholder.Visible = Input.Text.Length == 0; _placeholder.BringToFront(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(FridaTheme.Effective(Parent));
            // Same focus language as the composer: a 2 px highlight ring; at rest a border with 3:1 contrast.
            bool focused = Input.Focused;
            int inset = focused ? Math.Max(1, FridaTheme.Px(1)) : 0;
            FridaTheme.FillRound(e.Graphics, new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2), FridaTheme.Px(10), FridaTheme.SurfaceRaised,
                focused ? FridaTheme.Highlight : FridaTheme.EdgeStrong, focused ? 2 * FridaTheme.Factor : 1f);
            if (_search) FridaTheme.Text(e.Graphics, FridaTheme.GlyphSearch, FridaTheme.Icons(10), new Rectangle(FridaTheme.Px(10), 0, FridaTheme.Px(20), Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class FridaTreeGroup
    {
        public string Title; public int Count; public string Glyph;
        public override string ToString() { return Title; }
    }

    // Conversation list: owner-drawn TreeView rows with icons, relative time, running dot,
    // hover "more" button and indented project children. Native keyboard/expand behavior kept.
    internal sealed class FridaConversationTree : TreeView
    {
        public Func<string, bool> IsRunning = id => false;
        public Func<FridaIdeProject, int> ProjectCount = p => 0;
        // Item menu request from the "⋯" button, a right click or Apps/Shift+F10. None of them
        // selects (and so opens) the item: opening a menu never reloads the conversation.
        public event Action<TreeNode, Point> MoreRequested;
        private TreeNode _hover, _menuNode;
        // Row whose menu is open: its "⋯" stays drawn while the menu has the mouse.
        public TreeNode MenuNode
        {
            get { return _menuNode; }
            set { if (_menuNode == value) return; var old = _menuNode; _menuNode = value; InvalidateRow(old); InvalidateRow(value); }
        }
        public FridaConversationTree()
        {
            DrawMode = TreeViewDrawMode.OwnerDrawAll; ShowLines = false; ShowPlusMinus = false; ShowRootLines = false; FullRowSelect = true;
            HideSelection = false; BorderStyle = BorderStyle.None; ItemHeight = FridaTheme.Px(34); Indent = FridaTheme.Px(16);
            BackColor = FridaTheme.Surface; ForeColor = FridaTheme.Ink; Font = FridaTheme.Font(10); ShowNodeToolTips = true;
            FridaTheme.DarkScroll(this); FridaTheme.DoubleBufferTree(this);
        }
        private static bool Item(TreeNode node) { return node != null && (node.Tag is FridaConversation || node.Tag is FridaIdeProject); }
        private void InvalidateRow(TreeNode node) { if (node != null && node.TreeView == this && IsHandleCreated) Invalidate(Row(node)); }
        public void RequestMenu(TreeNode node, Point? at = null)
        {
            if (!Item(node)) return;
            var more = MoreArea(Row(node));
            var handler = MoreRequested; if (handler != null) handler(node, at ?? new Point(more.Left, more.Bottom + FridaTheme.Px(2)));
        }
        // The native control selects on button-down (and shows a drop highlight on right click), so
        // menu clicks are taken before it sees them.
        protected override void WndProc(ref Message m)
        {
            const int LeftDown = 0x0201, LeftDouble = 0x0203, RightDown = 0x0204, RightUp = 0x0205, ContextMenu = 0x007B;
            if (m.Msg == LeftDown || m.Msg == LeftDouble || m.Msg == RightDown)
            {
                long l = m.LParam.ToInt64();
                var p = new Point(unchecked((short)(l & 0xFFFF)), unchecked((short)((l >> 16) & 0xFFFF)));
                var node = GetNodeAt(0, p.Y);
                if (Item(node) && (m.Msg == RightDown || MoreArea(Row(node)).Contains(p)))
                {
                    RequestMenu(node, m.Msg == RightDown ? p : (Point?)null);
                    return;
                }
            }
            if (m.Msg == RightUp) return;
            if (m.Msg == ContextMenu)
            {
                if (m.LParam.ToInt64() == -1) RequestMenu(SelectedNode); // keyboard: Apps or Shift+F10
                return;
            }
            base.WndProc(ref m);
        }
        // Rows are drawn with ellipsis to the list width, so the native horizontal scrollbar
        // (computed from the full title text) is never useful: TVS_NOHSCROLL.
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x8000; return cp; } }
        private Rectangle MoreArea(Rectangle row) { int s = FridaTheme.Px(26); return new Rectangle(row.Right - s - FridaTheme.Px(8), row.Y + (row.Height - s) / 2, s, s); }
        private Rectangle Row(TreeNode node) { return new Rectangle(0, node.Bounds.Y, ClientSize.Width, node.Bounds.Height); }
        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || e.Bounds.Height <= 0) return;
            var g = e.Graphics; var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
            using (var brush = new SolidBrush(FridaTheme.Surface)) g.FillRectangle(brush, row);
            bool selected = (e.State & TreeNodeStates.Selected) != 0, hover = e.Node == _hover;
            int x = FridaTheme.Px(10) + e.Node.Level * Indent;
            var group = e.Node.Tag as FridaTreeGroup;
            if (group != null)
            {
                if (selected && Focused) FridaTheme.FillRound(g, Rectangle.Inflate(row, -FridaTheme.Px(4), -FridaTheme.Px(2)), FridaTheme.Px(8), FridaTheme.Hover);
                FridaTheme.Text(g, e.Node.IsExpanded ? FridaTheme.GlyphChevronDown : FridaTheme.GlyphChevronRight, FridaTheme.Icons(7.5f), new Rectangle(x, row.Y, FridaTheme.Px(14), row.Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter);
                FridaTheme.Text(g, group.Title.ToUpperInvariant(), FridaTheme.Strong(8.5f), new Rectangle(x + FridaTheme.Px(18), row.Y, row.Width - x - FridaTheme.Px(60), row.Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (group.Count > 0) FridaTheme.Text(g, group.Count.ToString(), FridaTheme.Font(8.5f), new Rectangle(row.Right - FridaTheme.Px(48), row.Y, FridaTheme.Px(36), row.Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                return;
            }
            if (!(e.Node.Tag is FridaConversation) && !(e.Node.Tag is FridaIdeProject))
            {
                FridaTheme.Text(g, e.Node.Text, FridaTheme.Font(9, FontStyle.Italic), new Rectangle(x, row.Y, row.Width - x - FridaTheme.Px(8), row.Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis);
                return;
            }
            var pill = new Rectangle(FridaTheme.Px(6), row.Y + FridaTheme.Px(2), row.Width - FridaTheme.Px(12), row.Height - FridaTheme.Px(4));
            if (selected) FridaTheme.FillRound(g, pill, FridaTheme.Px(8), FridaTheme.Bubble, Focused ? FridaTheme.Edge : Color.Empty);
            else if (hover) FridaTheme.FillRound(g, pill, FridaTheme.Px(8), FridaTheme.Hover);
            if (selected) FridaTheme.FillRound(g, new Rectangle(pill.X, pill.Y + pill.Height / 4, FridaTheme.Px(3), pill.Height / 2), FridaTheme.Px(2), FridaTheme.Highlight);
            if (e.Node.Nodes.Count > 0)
                FridaTheme.Text(g, e.Node.IsExpanded ? FridaTheme.GlyphChevronDown : FridaTheme.GlyphChevronRight, FridaTheme.Icons(7.5f), new Rectangle(x, row.Y, FridaTheme.Px(14), row.Height), FridaTheme.Muted, TextFormatFlags.VerticalCenter);
            x += FridaTheme.Px(16);
            var conversation = e.Node.Tag as FridaConversation; var project = e.Node.Tag as FridaIdeProject;
            string glyph = project != null ? (string.IsNullOrEmpty(project.SshAlias) ? FridaTheme.GlyphFolder : FridaTheme.GlyphGlobe)
                : conversation.Kind == FridaConversationKind.Meeting ? FridaTheme.GlyphPeople : conversation.Section == FridaWorkspaceSection.Code ? FridaTheme.GlyphCode : FridaTheme.GlyphChat;
            FridaTheme.Text(g, glyph, FridaTheme.Icons(10), new Rectangle(x, row.Y, FridaTheme.Px(20), row.Height), selected ? FridaTheme.Highlight : FridaTheme.Muted, TextFormatFlags.VerticalCenter);
            x += FridaTheme.Px(26);
            string meta = project != null ? (ProjectCount(project) > 0 ? ProjectCount(project).ToString() : "") : FridaTheme.RelativeTime(conversation.UpdatedUtc);
            bool running = conversation != null && IsRunning(conversation.Id);
            // "⋯" on the hovered row, the row whose menu is open and the focused selection (keyboard).
            bool more = hover || e.Node == _menuNode || (selected && Focused);
            int metaWidth = more ? FridaTheme.Px(34) : TextRenderer.MeasureText(meta, FridaTheme.Font(8.5f)).Width + FridaTheme.Px(running ? 30 : 14);
            FridaTheme.Text(g, e.Node.Text, selected ? FridaTheme.Strong(10) : FridaTheme.Font(10), new Rectangle(x, row.Y, Math.Max(10, row.Right - x - metaWidth - FridaTheme.Px(4)), row.Height), FridaTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (more)
            {
                var area = MoreArea(row);
                FridaTheme.FillRound(g, area, FridaTheme.Px(6), e.Node == _menuNode ? FridaTheme.Hover : FridaTheme.Surface, e.Node == _menuNode ? FridaTheme.Edge : Color.Empty);
                FridaTheme.Text(g, FridaTheme.GlyphMore, FridaTheme.Icons(9.5f), area, FridaTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var metaBounds = new Rectangle(row.Right - metaWidth - FridaTheme.Px(4), row.Y, metaWidth - FridaTheme.Px(8), row.Height);
            FridaTheme.Text(g, meta, FridaTheme.Font(8.5f), metaBounds, FridaTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            if (running)
            {
                int d = FridaTheme.Px(7);
                using (var brush = new SolidBrush(FridaTheme.Accent))
                { var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(brush, metaBounds.X, row.Y + (row.Height - d) / 2, d, d); g.SmoothingMode = mode; }
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var node = GetNodeAt(0, e.Y);
            if (node != null && !(node.Tag is FridaConversation) && !(node.Tag is FridaIdeProject)) node = null;
            if (node == _hover) return;
            var old = _hover; _hover = node;
            if (old != null) Invalidate(Row(old)); if (node != null) Invalidate(Row(node));
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { var old = _hover; _hover = null; Invalidate(Row(old)); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var node = GetNodeAt(0, e.Y);
            if (node != null && e.Button == MouseButtons.Left)
            {
                if (node.Tag is FridaTreeGroup) { SelectedNode = node; node.Toggle(); return; }
                if (node.Nodes.Count > 0 && e.X < FridaTheme.Px(26) + node.Level * Indent) { node.Toggle(); return; }
            }
            base.OnMouseDown(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && SelectedNode != null && SelectedNode.Nodes.Count > 0 && !(SelectedNode.Tag is FridaConversation))
            { SelectedNode.Toggle(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); InvalidateRow(SelectedNode); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); InvalidateRow(SelectedNode); }
    }

    // Dark rounded dialog with heading, body and a footer of pill buttons (primary rightmost).
    internal class FridaDialog : Form
    {
        public readonly Panel Body;
        public readonly ActionBar Footer;
        public FridaDialog(string title, int width = 460, int height = 200)
        {
            Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = FridaTheme.Surface; ForeColor = FridaTheme.Ink; Font = FridaTheme.Font(10); KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(FridaTheme.Px(width), FridaTheme.Px(height));
            FridaTheme.DarkChrome(this);
            var heading = new Label { Text = title, Font = FridaTheme.Strong(13), Dock = DockStyle.Top, Height = FridaTheme.Px(48), ForeColor = FridaTheme.Ink,
                Padding = new Padding(FridaTheme.Px(20), FridaTheme.Px(16), FridaTheme.Px(20), 0), AutoEllipsis = true };
            Body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(FridaTheme.Px(20), FridaTheme.Px(4), FridaTheme.Px(20), FridaTheme.Px(4)) };
            Footer = new ActionBar { Dock = DockStyle.Bottom, Height = FridaTheme.Px(64) };
            Footer.Padding = new Padding(FridaTheme.Px(16), 0, FridaTheme.Px(16), 0);
            Controls.Add(Body); Controls.Add(Footer); Controls.Add(heading);
        }
        public PillButton AddButton(string text, DialogResult result, PillKind kind)
        {
            var button = new PillButton(text, kind) { DialogResult = result };
            Footer.AddRight(button);
            if (result == DialogResult.Cancel) CancelButton = button; else if (kind == PillKind.Primary || kind == PillKind.Danger) AcceptButton = button;
            return button;
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); FridaTheme.Apply(this); }
        private static Label Message(string text, int width)
        {
            return new Label { Text = text, Dock = DockStyle.Top, ForeColor = FridaTheme.Muted, Font = FridaTheme.Font(10), AutoSize = false,
                Height = TextRenderer.MeasureText(text ?? "", FridaTheme.Font(10), new Size(FridaTheme.Px(width - 40), int.MaxValue), TextFormatFlags.WordBreak).Height + FridaTheme.Px(8) };
        }
        public static string Prompt(IWin32Window owner, string title, string initial, string label = null)
        {
            using (var dialog = new FridaDialog(title, 460, label == null ? 170 : 200))
            {
                var input = new TextBox { Text = initial ?? "", AccessibleName = label ?? title };
                var frame = new InputFrame(input) { Dock = DockStyle.Top };
                dialog.Body.Controls.Add(frame);
                if (label != null) dialog.Body.Controls.Add(Message(label, 460));
                dialog.AddButton("Cancelar", DialogResult.Cancel, PillKind.Secondary);
                dialog.AddButton("Confirmar", DialogResult.OK, PillKind.Primary);
                dialog.Shown += delegate { input.Focus(); input.SelectAll(); };
                return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
        public static bool Confirm(IWin32Window owner, string title, string message, string confirm = "Confirmar", bool danger = false)
        {
            using (var dialog = new FridaDialog(title, 480, 150))
            {
                var text = Message(message, 480); dialog.Body.Controls.Add(text);
                dialog.ClientSize = new Size(dialog.ClientSize.Width, FridaTheme.Px(120) + text.Height);
                dialog.AddButton("Cancelar", DialogResult.Cancel, PillKind.Secondary);
                dialog.AddButton(confirm, DialogResult.OK, danger ? PillKind.Danger : PillKind.Primary);
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }
        // Save / discard / cancel style choice; returns Yes, No or Cancel.
        public static DialogResult Choose(IWin32Window owner, string title, string message, string yes, string no)
        {
            using (var dialog = new FridaDialog(title, 500, 150))
            {
                var text = Message(message, 500); dialog.Body.Controls.Add(text);
                dialog.ClientSize = new Size(dialog.ClientSize.Width, FridaTheme.Px(120) + text.Height);
                dialog.AddButton("Cancelar", DialogResult.Cancel, PillKind.Ghost);
                dialog.AddButton(no, DialogResult.No, PillKind.Secondary);
                dialog.AddButton(yes, DialogResult.Yes, PillKind.Primary);
                return dialog.ShowDialog(owner);
            }
        }
        public static void Info(IWin32Window owner, string title, string message)
        {
            using (var dialog = new FridaDialog(title, 480, 150))
            {
                var text = Message(message, 480); dialog.Body.Controls.Add(text);
                dialog.ClientSize = new Size(dialog.ClientSize.Width, FridaTheme.Px(120) + text.Height);
                dialog.AddButton("OK", DialogResult.OK, PillKind.Primary);
                dialog.ShowDialog(owner);
            }
        }
    }

    // A Frida reply is a conversational answer that is sometimes followed by the raw evidence the tools
    // returned (exit codes, stderr, file listings, receipts) and long code. The chat shows the answer and
    // keeps that material one click away; speech and project memory use the answer only.
    internal static class FridaReplyParts
    {
        private static readonly Regex Technical = new Regex(@"^\s*(C[oó]digo de sa[ií]da:|\[STDERR\]|Processo (encerrado|falhou) com c[oó]digo|Leitura observada:|Janela/aba confirmada|Retorno observado:|\{""schema_version""|""?untrusted_output""?\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int LongCode = 12, CodePreview = 6;
        public static void Split(string text, out string answer, out string details)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            int cut = -1; bool fence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("```")) fence = !fence;
                if (!fence && Technical.IsMatch(lines[i])) { cut = i; break; }
            }
            var kept = cut < 0 ? lines : lines.Take(cut).ToArray();
            string tail = cut < 0 ? "" : string.Join("\n", lines.Skip(cut).Where(l => !l.TrimStart().StartsWith("```"))).Trim();
            // Long code blocks show their first lines; the whole block goes to the details.
            var shown = new List<string>(); var moved = new StringBuilder();
            for (int i = 0; i < kept.Length; i++)
            {
                if (!kept[i].TrimStart().StartsWith("```")) { shown.Add(kept[i]); continue; }
                int end = i + 1; while (end < kept.Length && !kept[end].TrimStart().StartsWith("```")) end++;
                int body = end - i - 1;
                if (body <= LongCode) { for (int k = i; k <= Math.Min(end, kept.Length - 1); k++) shown.Add(kept[k]); i = end; continue; }
                for (int k = i; k <= i + CodePreview; k++) shown.Add(kept[k]);
                shown.Add("```");
                shown.Add("_… mais " + (body - CodePreview) + " linhas de código em Detalhes técnicos._");
                for (int k = i; k <= Math.Min(end, kept.Length - 1); k++) moved.AppendLine(kept[k]);
                if (end >= kept.Length) moved.AppendLine("```");
                i = end;
            }
            answer = string.Join("\n", shown).Trim();
            var d = new StringBuilder();
            if (moved.Length > 0) d.AppendLine("**Código completo**").AppendLine(moved.ToString().TrimEnd());
            if (tail.Length > 0) { if (d.Length > 0) d.AppendLine(); d.AppendLine("**Retorno das ferramentas**").AppendLine("```").AppendLine(tail).Append("```"); }
            details = d.ToString().Trim();
            if (answer.Length == 0 && tail.Length > 0) answer = Summary(tail);
        }
        // Text only, for memory: the answer without the evidence, on one line.
        public static string Answer(string text, int max)
        {
            string answer, details; Split(text, out answer, out details);
            string plain = Regex.Replace(FridaMarkdown.ToPlain(answer), @"\s+", " ").Trim();
            return plain.Length > max ? plain.Substring(0, max).TrimEnd() + "…" : plain;
        }
        public static int Lines(string details) { return string.IsNullOrEmpty(details) ? 0 : details.Split('\n').Count(l => !l.TrimStart().StartsWith("```") && !l.StartsWith("**")); }
        // A reply made only of evidence (a tool receipt) reads as one line about what the tool did.
        private static string Summary(string tail)
        {
            if (tail.StartsWith("{"))
                try
                {
                    var receipt = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(tail.Split('\n')[0]);
                    object tool, status, summary;
                    receipt.TryGetValue("tool", out tool); receipt.TryGetValue("status", out status); receipt.TryGetValue("summary", out summary);
                    return "Recibo da ferramenta **" + Convert.ToString(tool) + "** (" + Convert.ToString(status) + "): " + Convert.ToString(summary);
                }
                catch (ArgumentException) { } catch (InvalidOperationException) { }
            string first = tail.Split('\n')[0].Trim();
            return "Retorno técnico: " + (first.Length > 160 ? first.Substring(0, 160) + "…" : first);
        }
    }

    // Colors and size of one Markdown surface (chat bubble, artifact preview, clipboard document).
    internal sealed class FridaMarkdownStyle
    {
        public Color Ink = FridaTheme.Ink, Muted = FridaTheme.Muted, Accent = FridaTheme.Highlight, Link = FridaTheme.AccentHover,
            CodeInk = FridaTheme.Highlight, CodeBack = FridaTheme.SurfaceRaised;
        public float Size = 10.5f;
        // Available text width in pixels (0 = unknown): decides whether a table keeps its columns.
        public int Width;
        // Dark text on white, for pasting a reply into Word or an e-mail.
        public static FridaMarkdownStyle Document()
        {
            return new FridaMarkdownStyle { Ink = Color.FromArgb(32, 33, 36), Muted = Color.FromArgb(95, 99, 104), Accent = Color.FromArgb(0, 102, 204),
                Link = Color.FromArgb(0, 102, 204), CodeInk = Color.FromArgb(32, 33, 36), CodeBack = Color.FromArgb(241, 243, 244), Size = 11 };
        }
    }

    // Chat-grade Markdown, parsed once into RTF (a single assignment instead of styling run by run)
    // and into plain text for copying. Headings, bold/italic, inline and fenced code, nested bullet
    // and numbered lists, quotes, links, tables (tab columns, or "coluna: valor" rows when too wide)
    // and rules. The markup characters themselves never reach the screen, and underscores inside
    // words (auth_required) stay text instead of turning into emphasis.
    internal static class FridaMarkdown
    {
        private enum Kind { Paragraph, Heading, Bullet, Number, Quote, Code, Table, Gap }
        private sealed class Block
        {
            public Kind Kind; public int Level; public string Text, Marker;
            public List<string> Lines; public List<string[]> Rows;
        }
        private struct Run { public string Text; public bool Bold, Italic, Code, Link, Url; }

        public static void Render(RichTextBox box, string markdown, FridaMarkdownStyle style = null)
        {
            box.Rtf = ToRtf(markdown, style ?? new FridaMarkdownStyle { Width = box.ClientSize.Width });
            box.SelectionStart = 0; box.SelectionLength = 0;
        }

        private static readonly Regex Html = new Regex(@"</?[A-Za-z][A-Za-z0-9]*(\s[^<>]*)?/?>", RegexOptions.Compiled);
        private static readonly Regex Break = new Regex(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HeadingLine = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex BulletLine = new Regex(@"^(\s*)[-*+•]\s+(\S.*)$", RegexOptions.Compiled);
        private static readonly Regex NumberLine = new Regex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleLine = new Regex(@"^([-*_])(\s*\1){2,}$", RegexOptions.Compiled);
        private static readonly Regex TableRule = new Regex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$", RegexOptions.Compiled);

        private static List<Block> Parse(string markdown)
        {
            var blocks = new List<Block>();
            var indents = new List<int>();
            var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Replace("\t", "    "), trimmed = raw.Trim();
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    string fence = trimmed.Substring(0, 3);
                    var code = new List<string>();
                    for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence); i++) code.Add(lines[i].Replace("\t", "    ").TrimEnd());
                    while (code.Count > 0 && code[code.Count - 1].Length == 0) code.RemoveAt(code.Count - 1);
                    if (code.Count > 0) blocks.Add(new Block { Kind = Kind.Code, Lines = code });
                    indents.Clear(); continue;
                }
                raw = Html.Replace(Break.Replace(raw, " "), ""); trimmed = raw.Trim();
                if (trimmed.Length == 0 || RuleLine.IsMatch(trimmed)) { Gap(blocks); continue; }
                bool table = trimmed.StartsWith("|") && trimmed.IndexOf('|', 1) > 0
                    || trimmed.Contains("|") && i + 1 < lines.Length && lines[i + 1].Contains("|") && TableRule.IsMatch(lines[i + 1].Trim());
                if (table)
                {
                    var rows = new List<string[]>();
                    for (; i < lines.Length; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.Length == 0 || !t.Contains("|")) { i--; break; }
                        if (!TableRule.IsMatch(t)) rows.Add(Cells(t));
                    }
                    if (rows.Count > 0) blocks.Add(new Block { Kind = Kind.Table, Rows = rows });
                    indents.Clear(); continue;
                }
                var m = HeadingLine.Match(trimmed);
                if (m.Success) { blocks.Add(new Block { Kind = Kind.Heading, Level = m.Groups[1].Length, Text = m.Groups[2].Value }); indents.Clear(); continue; }
                if (trimmed.StartsWith(">")) { blocks.Add(new Block { Kind = Kind.Quote, Text = trimmed.TrimStart('>', ' ') }); continue; }
                m = BulletLine.Match(raw);
                if (m.Success) { blocks.Add(new Block { Kind = Kind.Bullet, Level = ListLevel(indents, m.Groups[1].Length), Text = m.Groups[2].Value.Trim() }); continue; }
                m = NumberLine.Match(raw);
                if (m.Success) { blocks.Add(new Block { Kind = Kind.Number, Level = ListLevel(indents, m.Groups[1].Length), Marker = m.Groups[2].Value + ".", Text = m.Groups[3].Value.Trim() }); continue; }
                blocks.Add(new Block { Kind = Kind.Paragraph, Text = trimmed }); indents.Clear();
            }
            while (blocks.Count > 0 && blocks[blocks.Count - 1].Kind == Kind.Gap) blocks.RemoveAt(blocks.Count - 1);
            while (blocks.Count > 0 && blocks[0].Kind == Kind.Gap) blocks.RemoveAt(0);
            return blocks;
        }
        private static void Gap(List<Block> blocks) { if (blocks.Count > 0 && blocks[blocks.Count - 1].Kind != Kind.Gap) blocks.Add(new Block { Kind = Kind.Gap }); }
        // Nesting follows the indentation actually used (2 or 4 spaces), not a fixed width.
        private static int ListLevel(List<int> indents, int indent)
        {
            while (indents.Count > 0 && indents[indents.Count - 1] > indent) indents.RemoveAt(indents.Count - 1);
            if (indents.Count == 0 || indents[indents.Count - 1] < indent) indents.Add(indent);
            return Math.Min(3, indents.Count - 1);
        }
        private static string[] Cells(string row)
        {
            string t = row.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            return Regex.Split(t, @"(?<!\\)\|").Select(c => c.Replace("\\|", "|").Trim()).ToArray();
        }

        private static List<Run> Inline(string text) { var runs = new List<Run>(); Inline(text ?? "", false, false, runs); return runs; }
        private static bool Escapable(char c) { return "\\`*_{}[]()#+-.!|>~".IndexOf(c) >= 0; }
        private static int Count(string s, int i, char c) { int n = 0; while (i + n < s.Length && s[i + n] == c) n++; return n; }
        private static void Inline(string s, bool bold, bool italic, List<Run> runs)
        {
            var text = new StringBuilder();
            Action flush = () => { if (text.Length > 0) { runs.Add(new Run { Text = text.ToString(), Bold = bold, Italic = italic }); text.Clear(); } };
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && Escapable(s[i + 1])) { text.Append(s[i + 1]); i += 2; continue; }
                if (c == '`')
                {
                    int ticks = Count(s, i, '`');
                    int end = s.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                    if (end > i + ticks) { flush(); runs.Add(new Run { Text = s.Substring(i + ticks, end - i - ticks).Trim(), Code = true }); i = end + ticks; continue; }
                    i += ticks; continue; // a lone backtick is markup, not content
                }
                if (c == '[')
                {
                    int close = s.IndexOf("](", i + 1, StringComparison.Ordinal), end = close < 0 ? -1 : s.IndexOf(')', close + 2);
                    string url = end > close && close > i ? s.Substring(close + 2, end - close - 2).Trim() : "";
                    if (url.Length > 0 && url.IndexOf(' ') < 0)
                    {
                        flush();
                        string label = s.Substring(i + 1, close - i - 1).Trim();
                        if (label.Length == 0 || label == url) runs.Add(new Run { Text = url });
                        else
                        {
                            int start = runs.Count; Inline(label, bold, italic, runs);
                            for (int k = start; k < runs.Count; k++) { var r = runs[k]; r.Link = true; runs[k] = r; }
                            runs.Add(new Run { Text = " " + url, Url = true });
                        }
                        i = end + 1; continue;
                    }
                }
                if (c == '~' && Count(s, i, '~') >= 2)
                {
                    int close = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (close > i + 2) { flush(); Inline(s.Substring(i + 2, close - i - 2), bold, italic, runs); i = close + 2; continue; }
                }
                if (c == '*' || c == '_')
                {
                    int n = Math.Min(3, Count(s, i, c));
                    if (CanOpen(s, i, n, c))
                    {
                        int close = FindClose(s, i + n, c, n);
                        if (close > i + n) { flush(); Inline(s.Substring(i + n, close - i - n), bold || n >= 2, italic || n != 2, runs); i = close + n; continue; }
                    }
                    if (c == '*' && n >= 2) { i += Count(s, i, c); continue; } // stray ** is markup, not content
                    text.Append(s, i, n); i += n; continue;
                }
                text.Append(c); i++;
            }
            flush();
        }
        private static bool CanOpen(string s, int i, int n, char c)
        {
            if (i + n >= s.Length || char.IsWhiteSpace(s[i + n])) return false;
            return c != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
        }
        private static int FindClose(string s, int from, char c, int n)
        {
            for (int j = from; j < s.Length; j++)
            {
                if (s[j] == '\\') { j++; continue; }
                if (s[j] == '`')
                {
                    int t = Count(s, j, '`'), e = s.IndexOf(new string('`', t), j + t, StringComparison.Ordinal);
                    if (e > 0) { j = e + t - 1; continue; }
                }
                if (s[j] != c) continue;
                int m = Count(s, j, c);
                if (m == n && !char.IsWhiteSpace(s[j - 1]) && (c != '_' || j + m >= s.Length || !char.IsLetterOrDigit(s[j + m]))) return j;
                j += m - 1;
            }
            return -1;
        }
        private static string Plain(List<Run> runs) { return string.Concat(runs.Select(r => r.Text)); }

        // Twips/half-points for RichEdit, which renders them at the system DPI: sizes follow Factor
        // exactly, including the 150/200% test override.
        private static int Tw(int px) { return (int)Math.Round(px * 15f * FridaTheme.Factor / FridaTheme.SystemFactor); }
        private static int TwScaled(int scaledPx) { return (int)Math.Round(scaledPx * 15f / FridaTheme.SystemFactor); }
        private static int HalfPoints(float points) { return Math.Max(8, (int)Math.Round(FridaTheme.Font(points).SizeInPoints * 2)); }
        private static void Escape(StringBuilder rtf, string text)
        {
            foreach (char ch in text ?? "")
            {
                if (ch == '\\' || ch == '{' || ch == '}') rtf.Append('\\').Append(ch);
                else if (ch == '\t') rtf.Append(@"\tab ");
                else if (ch == '\n') rtf.Append(@"\line ");
                else if (ch == '\r') continue;
                else if (ch < 128) rtf.Append(ch);
                else rtf.Append(@"\u").Append((int)(short)ch).Append('?');
            }
        }

        public static string ToRtf(string markdown, FridaMarkdownStyle style)
        {
            style = style ?? new FridaMarkdownStyle();
            var rtf = new StringBuilder(512 + (markdown ?? "").Length * 2);
            rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}{\f1\fnil\fcharset0 Segoe UI Semibold;}{\f2\fmodern\fcharset0 ").Append(FridaTheme.MonoFamily).Append(";}}");
            // cf1 ink, cf2 muted, cf3 accent, cf4 link, cf5 code ink, cf6 code background
            rtf.Append(@"{\colortbl ;");
            foreach (var c in new[] { style.Ink, style.Muted, style.Accent, style.Link, style.CodeInk, style.CodeBack })
                rtf.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            rtf.Append('}');
            int body = HalfPoints(style.Size), code = HalfPoints(style.Size - 0.5f), small = HalfPoints(style.Size - 1.5f);
            rtf.Append(@"\viewkind4\uc1");
            bool first = true; int before = 0;
            Action<string> open = format =>
            {
                if (!first) rtf.Append("\\par\r\n");
                rtf.Append(@"\pard").Append(format).Append(@"\f0\fs").Append(body).Append(@"\cf1\highlight0 ");
                first = false; before = 0;
            };
            Func<int, int> space = minimum => first ? 0 : Math.Max(before, minimum);
            foreach (var b in Parse(markdown))
            {
                switch (b.Kind)
                {
                    case Kind.Gap: before = Tw(8); break;
                    case Kind.Heading:
                    {
                        int size = HalfPoints(style.Size + (b.Level == 1 ? 4 : b.Level == 2 ? 2.5f : b.Level == 3 ? 1 : 0.5f));
                        open(@"\sb" + space(Tw(b.Level <= 2 ? 10 : 6)) + @"\sa" + Tw(4));
                        rtf.Append(@"{\f1\fs").Append(size).Append(' ');
                        Runs(rtf, Inline(b.Text), code, small, @"\cf1", true);
                        rtf.Append('}');
                        break;
                    }
                    case Kind.Bullet:
                    case Kind.Number:
                    {
                        int hang = Tw(b.Kind == Kind.Number ? 22 : 16), left = Tw(6 + b.Level * 18) + hang;
                        open(@"\sb" + space(0) + @"\sa" + Tw(3) + @"\li" + left + @"\fi-" + hang + @"\tx" + left);
                        string marker = b.Kind == Kind.Number ? b.Marker : b.Level == 0 ? "\u2022" : b.Level == 1 ? "\u25E6" : "\u25AA";
                        rtf.Append(b.Kind == Kind.Number ? @"{\f1\cf3 " : @"{\cf3 "); Escape(rtf, marker); rtf.Append(@"}\tab ");
                        Runs(rtf, Inline(b.Text), code, small, @"\cf1", false);
                        break;
                    }
                    case Kind.Quote:
                        open(@"\sb" + space(0) + @"\sa" + Tw(3) + @"\li" + Tw(14));
                        rtf.Append(@"{\cf3 "); Escape(rtf, "\u258E "); rtf.Append('}');
                        Runs(rtf, Inline(b.Text), code, small, @"\cf2\i", false);
                        break;
                    case Kind.Code:
                    {
                        int width = Math.Min(100, b.Lines.Max(l => l.Length));
                        for (int k = 0; k < b.Lines.Count; k++)
                        {
                            open(@"\sb" + (k == 0 ? space(Tw(4)) : 0) + @"\sa" + (k == b.Lines.Count - 1 ? Tw(6) : 0) + @"\li" + Tw(4));
                            rtf.Append(@"{\f2\fs").Append(code).Append(@"\cf5\highlight6 ");
                            Escape(rtf, "\u00A0" + b.Lines[k].PadRight(width) + "\u00A0");
                            rtf.Append('}');
                        }
                        break;
                    }
                    case Kind.Table: Table(rtf, b.Rows, style, open, space, code, small); break;
                    default:
                        open(@"\sb" + space(0) + @"\sa" + Tw(4));
                        Runs(rtf, Inline(b.Text), code, small, @"\cf1", false);
                        break;
                }
            }
            rtf.Append('}');
            return rtf.ToString();
        }
        private static void Table(StringBuilder rtf, List<string[]> rows, FridaMarkdownStyle style, Action<string> open, Func<int, int> space, int code, int small)
        {
            int cols = rows.Max(r => r.Length);
            var cells = rows.Select(r => r.Select(c => Inline(c)).ToArray()).ToList();
            bool header = rows.Count > 1;
            int[] widths = new int[cols];
            bool columns = cols >= 2 && cols <= 4;
            if (columns)
            {
                for (int r = 0; r < cells.Count; r++)
                    for (int c = 0; c < cells[r].Length; c++)
                        widths[c] = Math.Max(widths[c], TextRenderer.MeasureText(Plain(cells[r][c]), r == 0 && header ? FridaTheme.Strong(style.Size) : FridaTheme.Font(style.Size)).Width + FridaTheme.Px(20));
                columns = widths.Sum() <= (style.Width > 0 ? style.Width : FridaTheme.Px(560));
            }
            if (columns)
            {
                string stops = ""; int x = 0;
                for (int c = 0; c < cols - 1; c++) { x += widths[c]; stops += @"\tx" + TwScaled(x); }
                for (int r = 0; r < cells.Count; r++)
                {
                    bool head = r == 0 && header;
                    open(@"\sb" + (r == 0 ? space(Tw(4)) : 0) + @"\sa" + Tw(head ? 5 : 3) + stops);
                    for (int c = 0; c < cells[r].Length; c++)
                    {
                        if (c > 0) rtf.Append(@"\tab ");
                        Runs(rtf, cells[r][c], code, small, head ? @"\cf2" : @"\cf1", head);
                    }
                }
                return;
            }
            // Too wide for columns: one bullet per row, "coluna: valor" pairs after the first cell.
            var titles = header ? rows[0] : new string[0];
            for (int r = header ? 1 : 0; r < cells.Count; r++)
            {
                int hang = Tw(16), left = Tw(6) + hang;
                open(@"\sb" + (r <= 1 ? space(Tw(4)) : 0) + @"\sa" + Tw(4) + @"\li" + left + @"\fi-" + hang + @"\tx" + left);
                rtf.Append(@"{\cf3 "); Escape(rtf, "\u2022"); rtf.Append(@"}\tab ");
                for (int c = 0; c < cells[r].Length; c++)
                {
                    if (Plain(cells[r][c]).Length == 0) continue;
                    if (c > 0)
                    {
                        rtf.Append(@"{\cf2 "); Escape(rtf, "  \u00B7  " + (c < titles.Length && titles[c].Length > 0 ? Plain(Inline(titles[c])) + ": " : "")); rtf.Append('}');
                    }
                    Runs(rtf, cells[r][c], code, small, @"\cf1", c == 0);
                }
            }
        }
        private static void Runs(StringBuilder rtf, List<Run> runs, int code, int small, string color, bool strong)
        {
            foreach (var r in runs)
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                rtf.Append('{');
                if (r.Code) rtf.Append(@"\f2\fs").Append(code).Append(@"\cf5\highlight6 ");
                else
                {
                    if (r.Bold || strong) rtf.Append(@"\f1");
                    if (r.Italic) rtf.Append(@"\i");
                    if (r.Url) rtf.Append(@"\fs").Append(small).Append(@"\cf2");
                    else if (r.Link) rtf.Append(@"\cf4\ul");
                    else rtf.Append(color);
                    rtf.Append(' ');
                }
                Escape(rtf, r.Code ? "\u00A0" + r.Text + "\u00A0" : r.Text);
                rtf.Append('}');
            }
        }

        private static readonly Dictionary<string, string> JsonLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "plan", "Plano" }, { "missing_requirements", "Requisitos que faltam" }, { "delegated_roles", "Papéis delegados" }, { "acceptance_criteria", "Critérios de aceite" },
            { "research_queries", "Pesquisas" }, { "workflow", "Fluxo" }, { "proposed_skills", "Skills propostas" }, { "veredito", "Veredito" }, { "feito", "Feito" },
            { "pendente", "Pendente" }, { "nao_feito", "Não feito" }, { "justificativa", "Justificativa" }, { "proximos_passos", "Próximos passos" },
            { "atualizar_skill", "Atualizar skill" }, { "licoes", "Lições" }, { "skill_id", "Skill" }, { "learn_new_skill", "Aprender nova skill" },
            { "skill_name", "Nome da skill" }, { "description", "Descrição" }, { "name", "Nome" }, { "purpose", "Finalidade" }
        };
        private static string JsonLabel(string key)
        {
            string label;
            if (JsonLabels.TryGetValue(key, out label)) return label;
            return key.Length == 0 ? key : char.ToUpper(key[0]) + key.Substring(1).Replace('_', ' ');
        }
        // Team members answer some steps in JSON (plan, decision); the chat shows it as labelled Markdown.
        public static string Readable(string text)
        {
            string t = (text ?? "").Trim();
            if (t.StartsWith("```")) { int line = t.IndexOf('\n'), end = t.LastIndexOf("```", StringComparison.Ordinal); if (line > 0 && end > line) t = t.Substring(line + 1, end - line - 1).Trim(); }
            if (!t.StartsWith("{") || !t.EndsWith("}")) return text;
            Dictionary<string, object> json;
            try { json = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(t); }
            catch (ArgumentException) { return text; } catch (InvalidOperationException) { return text; }
            if (json == null || json.Count == 0) return text;
            var md = new StringBuilder(); WriteJson(md, json, 0);
            return md.Length == 0 ? text : md.ToString().TrimEnd();
        }
        private static void WriteJson(StringBuilder md, Dictionary<string, object> json, int depth)
        {
            string indent = new string(' ', depth * 2);
            foreach (var pair in json)
            {
                object value = pair.Value;
                if (value == null || value is string && ((string)value).Trim().Length == 0) continue;
                var dict = value as Dictionary<string, object>;
                var list = value as System.Collections.IEnumerable;
                if (dict != null) { if (dict.Count == 0) continue; md.AppendLine(indent + "- **" + JsonLabel(pair.Key) + ":**"); WriteJson(md, dict, depth + 1); }
                else if (list != null && !(value is string))
                {
                    var items = list.Cast<object>().Where(o => o != null).ToList(); if (items.Count == 0) continue;
                    md.AppendLine(indent + "- **" + JsonLabel(pair.Key) + ":**");
                    foreach (object item in items)
                    {
                        var fields = item as Dictionary<string, object>;
                        md.AppendLine(indent + "  - " + (fields == null ? Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)
                            : string.Join(" · ", fields.Where(f => f.Value != null).Select(f => JsonLabel(f.Key) + ": " + Convert.ToString(f.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray())));
                    }
                }
                else if (value is bool) md.AppendLine(indent + "- **" + JsonLabel(pair.Key) + ":** " + ((bool)value ? "sim" : "não"));
                else md.AppendLine(indent + "- **" + JsonLabel(pair.Key) + ":** " + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Same structure as the rendered text, without markup: what "Copiar" puts on the clipboard.
        public static string ToPlain(string markdown)
        {
            var lines = new List<string>();
            foreach (var b in Parse(markdown))
            {
                string indent = new string(' ', b.Level * 2);
                switch (b.Kind)
                {
                    case Kind.Gap: lines.Add(""); break;
                    case Kind.Bullet: lines.Add(indent + "\u2022 " + Plain(Inline(b.Text))); break;
                    case Kind.Number: lines.Add(indent + b.Marker + " " + Plain(Inline(b.Text))); break;
                    case Kind.Quote: lines.Add("  " + Plain(Inline(b.Text))); break;
                    case Kind.Code: lines.AddRange(b.Lines); break;
                    case Kind.Table: lines.AddRange(b.Rows.Select(r => string.Join("\t", r.Select(c => Plain(Inline(c)))))); break;
                    default: lines.Add(Plain(Inline(b.Text))); break;
                }
            }
            return string.Join("\r\n", lines);
        }
    }
}
