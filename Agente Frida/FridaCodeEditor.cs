using System;
using System.Drawing;
using System.Windows.Forms;

namespace FridaAgent
{
    public sealed class FridaCodeEditor : RichTextBox
    {
        private sealed class NumberMargin : Control
        {
            private readonly FridaCodeEditor _editor;
            public NumberMargin(FridaCodeEditor editor)
            {
                _editor = editor; Dock = DockStyle.Left; Width = FridaTheme.Px(52);
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                AccessibleName = "Números das linhas";
                editor.VScroll += delegate { Invalidate(); };
                editor.TextChanged += delegate { Invalidate(); };
                editor.Resize += delegate { Invalidate(); };
                editor.SelectionChanged += delegate { Invalidate(); };
            }
            // Numbers follow the file's real lines (text between line breaks), so they stay right
            // when long lines wrap onto several visual rows.
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(FridaTheme.Canvas);
                string text = _editor.Text;
                int first = Math.Max(0, Math.Min(text.Length, _editor.GetCharIndexFromPosition(Point.Empty)));
                int index = first == 0 ? 0 : text.LastIndexOf('\n', first - 1) + 1;
                int line = CountBreaks(text, index), current = CountBreaks(text, Math.Min(text.Length, _editor.SelectionStart));
                while (true)
                {
                    Point position;
                    if (index < text.Length || text.Length == 0) position = text.Length == 0 ? Point.Empty : _editor.GetPositionFromCharIndex(index);
                    else { position = _editor.GetPositionFromCharIndex(index - 1); position.Y += _editor.Font.Height; } // empty last line
                    if (position.Y > Height) break;
                    TextRenderer.DrawText(e.Graphics, (line + 1).ToString(), _editor.Font, new Rectangle(0, position.Y, Width - FridaTheme.Px(12), _editor.Font.Height),
                        line == current ? FridaTheme.Ink : Color.FromArgb(100, 130, 160), TextFormatFlags.Right | TextFormatFlags.NoPadding);
                    int next = text.IndexOf('\n', index); if (next < 0) break;
                    index = next + 1; line++;
                }
                using (var pen = new Pen(FridaTheme.Divider)) e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            }
        }
        public FridaCodeEditor()
        {
            Dock = DockStyle.Fill; Font = FridaTheme.Mono(10.5f); WordWrap = false;
            AcceptsTab = true; DetectUrls = false; BorderStyle = BorderStyle.None;
            BackColor = FridaTheme.Canvas; ForeColor = FridaTheme.Ink;
            AccessibleName = "Código do arquivo";
            FridaTheme.DarkScroll(this);
        }
        public Control CreateLineNumbers() { return new NumberMargin(this); }
        private static int CountBreaks(string text, int end)
        {
            int count = 0;
            for (int i = 0; i < end; i++) if (text[i] == '\n') count++;
            return count;
        }
        // Caret position in file lines/columns (independent of word wrap), 1-based.
        public int CaretLine { get { return CountBreaks(Text, Math.Min(TextLength, SelectionStart)) + 1; } }
        public int CaretColumn
        {
            get
            {
                string text = Text; int at = Math.Min(text.Length, SelectionStart);
                return at - (at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1) + 1;
            }
        }
        public void FindText(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int start = SelectionStart + SelectionLength;
            int index = Text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = Text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return;
            Select(index, value.Length); ScrollToCaret(); Focus();
        }
    }
}
