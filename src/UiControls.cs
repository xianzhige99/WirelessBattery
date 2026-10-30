using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WirelessBattery
{
    public static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(243, 245, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Text = Color.FromArgb(16, 24, 40);
        public static readonly Color Muted = Color.FromArgb(102, 112, 133);
        public static readonly Color Line = Color.FromArgb(234, 236, 240);
        public static readonly Color Accent = Color.FromArgb(79, 70, 229);

        public static Font Font(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { return new Font("Microsoft YaHei", size, style); }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }

    public class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }

    /// <summary>
    /// 共享的应用图标。窗体、任务栏和托盘主图标都用它。
    /// </summary>
    public static class AppIcon
    {
        private static Icon _shared;

        public static Icon Shared
        {
            get
            {
                if (_shared == null)
                {
                    try { _shared = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { }
                    if (_shared == null)
                    {
                        try { _shared = (Icon)SystemIcons.Application.Clone(); }
                        catch { _shared = SystemIcons.Application; }
                    }
                }
                return _shared;
            }
        }
    }

    public class RoundButton : Button
    {
        public int Radius = 10;
        public Color FillColor = Ui.Accent;
        public Color HoverColor = Color.FromArgb(67, 56, 202);
        public Color TextColor = Color.White;
        public Color BorderColor = Color.Transparent;
        private bool _hover;

        public RoundButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.White);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Enabled ? (_hover ? HoverColor : FillColor) : Color.FromArgb(208, 213, 221);
            using (GraphicsPath p = Ui.Round(r, Radius))
            using (SolidBrush b = new SolidBrush(fill))
                e.Graphics.FillPath(b, p);
            if (BorderColor != Color.Transparent)
            {
                using (GraphicsPath p = Ui.Round(r, Radius))
                using (Pen pen = new Pen(BorderColor))
                    e.Graphics.DrawPath(pen, p);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? TextColor : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public class CardPanel : Panel
    {
        public int Radius = 14;
        public Color BorderColor = Ui.Line;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try
            {
                using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width, Height), Radius))
                {
                    Region = new Region(p);
                }
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, p);
                using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, p);
            }
        }
    }

    public class BatteryBar : Control
    {
        private int _value;
        public Color BarColor = Ui.Accent;

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (_value != v) { _value = v; Invalidate(); }
            }
        }

        public BatteryBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 10;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 1, Width - 1, Height - 3);
            using (GraphicsPath p = Ui.Round(track, track.Height / 2))
            using (SolidBrush b = new SolidBrush(Ui.Line))
                e.Graphics.FillPath(b, p);
            if (_value <= 0) return;
            int w = (int)Math.Round(track.Width * _value / 100.0);
            if (w < track.Height) w = track.Height;
            Rectangle fill = new Rectangle(track.X, track.Y, w, track.Height);
            using (GraphicsPath p = Ui.Round(fill, fill.Height / 2))
            using (SolidBrush b = new SolidBrush(BarColor))
                e.Graphics.FillPath(b, p);
        }
    }

    public class BatteryIcon : Control
    {
        private int? _level;
        private bool _charging;
        public Color Color = Ui.Accent;

        public int? Level
        {
            get { return _level; }
            set { _level = value; Invalidate(); }
        }

        public bool Charging
        {
            get { return _charging; }
            set { _charging = value; Invalidate(); }
        }

        public BatteryIcon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(56, 30);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width - 10;
            int h = Height - 8;
            if (w < 20) w = 20;
            if (h < 12) h = 12;
            Rectangle body = new Rectangle(2, 4, w, h);
            using (GraphicsPath p = Ui.Round(body, 4))
            using (Pen pen = new Pen(Color.FromArgb(180, 190, 205), 2f))
                e.Graphics.DrawPath(pen, p);
            Rectangle term = new Rectangle(body.Right + 1, body.Y + body.Height / 3, 5, body.Height / 3);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(180, 190, 205)))
                e.Graphics.FillRectangle(b, term);

            if (_level.HasValue)
            {
                int innerW = body.Width - 6;
                int fillW = (int)Math.Round(innerW * Math.Max(0, Math.Min(100, _level.Value)) / 100.0);
                if (fillW > 0)
                {
                    Rectangle fill = new Rectangle(body.X + 3, body.Y + 3, fillW, body.Height - 6);
                    using (GraphicsPath p = Ui.Round(fill, 2))
                    using (SolidBrush b = new SolidBrush(Color))
                        e.Graphics.FillPath(b, p);
                }
            }
            else
            {
                using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Ui.Muted))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    e.Graphics.DrawString("?", f, b, body, sf);
                }
            }

            if (_charging)
            {
                Point[] pts = new Point[] {
                    new Point(body.X + body.Width / 2 + 3, body.Y + 2),
                    new Point(body.X + body.Width / 2 - 5, body.Y + body.Height / 2 + 1),
                    new Point(body.X + body.Width / 2 + 1, body.Y + body.Height / 2 + 1),
                    new Point(body.X + body.Width / 2 - 3, body.Bottom - 1)
                };
                using (SolidBrush b = new SolidBrush(Color.White))
                    e.Graphics.FillPolygon(b, pts);
            }
        }
    }
}
