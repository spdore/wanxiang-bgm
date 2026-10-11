using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal static class UiTheme
    {
        public static readonly Color Background = Color.FromArgb(247, 248, 250);
        public static readonly Color Text = Color.FromArgb(27, 43, 58);
        public static readonly Color Muted = Color.FromArgb(72, 88, 105);
        public static readonly Color Accent = Color.FromArgb(41, 94, 219);
        public static readonly Color Tint = Color.FromArgb(238, 243, 255);
        public static readonly Color Border = Color.FromArgb(223, 231, 239);

        public static Label Label(string text, float size, bool bold)
        {
            return new Label { Text = text, ForeColor = Text, Font = new Font("Microsoft YaHei UI", Math.Max(10F, size), bold ? FontStyle.Bold : FontStyle.Regular), UseCompatibleTextRendering = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0) };
        }

        public static Button Button(string text, bool primary)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Text, Cursor = Cursors.Hand, Height = 36, Width = 116, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), UseCompatibleTextRendering = false, Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(32, 78, 191) : Tint;
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(27, 66, 160) : Color.FromArgb(226, 235, 255);
            return button;
        }

        public static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class UiCard : Panel
    {
        public UiCard()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Padding = new Padding(16);
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            using (GraphicsPath path = UiTheme.Rounded(new RectangleF(scale / 2, scale / 2, Width - scale, Height - scale), 12 * scale))
            using (Pen border = new Pen(UiTheme.Border, scale)) e.Graphics.DrawPath(border, path);
        }
    }

    internal sealed class UiMeter : ProgressBar
    {
        public UiMeter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            RectangleF track = new RectangleF(0, Height / 2F - 4 * scale, Width, 8 * scale);
            using (GraphicsPath path = UiTheme.Rounded(track, 4 * scale))
            using (Brush bg = new SolidBrush(UiTheme.Background)) e.Graphics.FillPath(bg, path);
            float fill = Width * Value / 100F;
            if (fill >= 8 * scale)
                using (GraphicsPath path = UiTheme.Rounded(new RectangleF(0, track.Y, fill, 8 * scale), 4 * scale))
                using (Brush color = new SolidBrush(Value >= 97 ? Color.FromArgb(224, 95, 76) : UiTheme.Accent)) e.Graphics.FillPath(color, path);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x402) Invalidate();
        }
    }
}
