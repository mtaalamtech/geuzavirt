using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    // Core Mtaalam Technologies design system (mtaalamtech.com): dark navy + teal + amber.
    static class Theme
    {
        public static readonly Color Bg0 = Color.FromArgb(0x06, 0x0B, 0x14);
        public static readonly Color Bg1 = Color.FromArgb(0x0A, 0x13, 0x22);
        public static readonly Color Bg2 = Color.FromArgb(0x0E, 0x1B, 0x30);
        public static readonly Color Bg3 = Color.FromArgb(0x14, 0x24, 0x3E);
        public static readonly Color Line = Color.FromArgb(0x22, 0x33, 0x4D);
        public static readonly Color Text0 = Color.FromArgb(0xEB, 0xF1, 0xFA);
        public static readonly Color Text1 = Color.FromArgb(0xB6, 0xC3, 0xD7);
        public static readonly Color Text2 = Color.FromArgb(0x7D, 0x8D, 0xA7);
        public static readonly Color Accent = Color.FromArgb(0x2D, 0xD4, 0xBF);
        public static readonly Color Accent2 = Color.FromArgb(0xFF, 0xB6, 0x2E);
        public static readonly Color Blue = Color.FromArgb(0x4E, 0x8C, 0xFF);
        public static readonly Color Purple = Color.FromArgb(0x7A, 0x5C, 0xFF);
        public static readonly Color Red = Color.FromArgb(0xFF, 0x6B, 0x6B);

        static PointF P(RectangleF r, float scale, float x, float y)
        {
            return new PointF(r.X + x * scale, r.Y + y * scale);
        }

        // The "M" mark from /assets/img/logo.svg (48x48 viewBox, transparent).
        public static void DrawLogo(Graphics g, RectangleF r)
        {
            float s = r.Width / 48f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new LinearGradientBrush(P(r, s, 9, 40), P(r, s, 39, 12), Accent, Accent2))
            using (var pen = new Pen(brush, 7 * s))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { P(r, s, 9, 39), P(r, s, 14, 16), P(r, s, 24, 28.5f), P(r, s, 34, 16), P(r, s, 39, 39) });
            }
            using (var dot = new SolidBrush(Accent2))
                g.FillEllipse(dot, r.X + (24 - 3.2f) * s, r.Y + (13 - 3.2f) * s, 6.4f * s, 6.4f * s);
        }

        // The app icon, from /favicon.svg (64x64 viewBox, navy tile).
        public static void DrawAppIcon(Graphics g, int size)
        {
            var r = new RectangleF(0, 0, size, size);
            float s = size / 64f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tile = RoundedRect(r, 14 * s))
            using (var bg = new SolidBrush(Bg1))
                g.FillPath(bg, tile);
            using (var brush = new LinearGradientBrush(P(r, s, 12, 54), P(r, s, 52, 12), Accent, Accent2))
            using (var pen = new Pen(brush, 6 * s))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { P(r, s, 14, 50), P(r, s, 20, 24), P(r, s, 32, 38), P(r, s, 44, 24), P(r, s, 50, 50) });
            }
            using (var dot = new SolidBrush(Accent2))
                g.FillEllipse(dot, (32 - 3.4f) * s, (20 - 3.4f) * s, 6.8f * s, 6.8f * s);
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Button MakeButton(string text, bool primary)
        {
            var b = new ThemedButton
            {
                Primary = primary,
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                BackColor = primary ? Accent : Bg3,
                ForeColor = primary ? Bg0 : Text0,
                UseVisualStyleBackColor = false,
            };
            if (primary) b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            return b;
        }

        public static TextBox MakeTextBox()
        {
            return new TextBox
            {
                BackColor = Bg2,
                ForeColor = Text0,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3),
            };
        }

        public static ComboBox MakeCombo(bool editable)
        {
            return new ComboBox
            {
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Bg2,
                ForeColor = Text0,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3),
            };
        }

        public static string Bytes(long b)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = b;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? b + " B" : v.ToString(v >= 100 ? "0" : "0.0") + " " + units[u];
        }

        // "42 s", "12 min 05 s", "3 h 07 min"; "-" when unknown
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "-";
            long s = (long)Math.Round(seconds);
            if (s < 60) return s + " s";
            if (s < 3600) return s / 60 + " min " + (s % 60).ToString("00") + " s";
            if (s < 4 * 86400) return s / 3600 + " h " + (s % 3600 / 60).ToString("00") + " min";
            return s / 86400 + " d " + s % 86400 / 3600 + " h";
        }
    }

    // Flat button painted by hand: Windows draws the text of a disabled flat button in
    // dark grey, which cannot be read on the dark theme.
    class ThemedButton : Button
    {
        public bool Primary;
        bool hover, down;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color back, border, fore;
            if (!Enabled) { back = Theme.Bg2; border = Theme.Line; fore = Theme.Text2; }
            else if (Primary) { back = down ? Theme.Accent2 : hover ? Color.FromArgb(0x5E, 0xE4, 0xD3) : Theme.Accent; border = back; fore = Theme.Bg0; }
            else { back = down ? Theme.Bg2 : hover ? Theme.Line : Theme.Bg3; border = hover ? Theme.Text2 : Theme.Line; fore = Theme.Text0; }
            var g = e.Graphics;
            g.Clear(back);
            using (var pen = new Pen(border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (Focused && ShowFocusCues && Enabled)
                using (var pen = new Pen(Primary ? Theme.Bg1 : Theme.Accent) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // Thin flat progress bar in the brand gradient.
    class FlatProgress : Control
    {
        double value;

        public FlatProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 8;
            Margin = new Padding(0, 2, 0, 10);
            Dock = DockStyle.Top;
        }

        public double Value
        {
            get { return value; }
            set
            {
                double v = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(v - this.value) < 0.0005) return;
                this.value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            float radius = Math.Min(Height / 2f, 4);
            using (var path = Theme.RoundedRect(track, radius))
            using (var b = new SolidBrush(Theme.Bg3))
                g.FillPath(b, path);
            float w = (float)(track.Width * value);
            if (w < 1) return;
            var fill = new RectangleF(0, 0, Math.Max(w, radius * 2), track.Height);
            using (var path = Theme.RoundedRect(fill, radius))
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), Theme.Accent, Theme.Blue, LinearGradientMode.Horizontal))
                g.FillPath(b, path);
        }
    }

    // Header band: logo, product name, company.
    class HeaderPanel : Control
    {
        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Height = (int)(Font.Height * 4.4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg1);
            int h = Height;
            float logo = h * 0.58f;
            float pad = h * 0.26f;
            Theme.DrawLogo(g, new RectangleF(pad, (h - logo) / 2, logo, logo));

            float x = pad * 2 + logo;
            using (var title = new Font("Segoe UI Semibold", 15f))
            using (var sub = new Font("Segoe UI", 9.5f))
            {
                int th = TextRenderer.MeasureText("M", title).Height;
                int sh = TextRenderer.MeasureText("M", sub).Height;
                int top = (h - th - sh) / 2;
                TextRenderer.DrawText(g, Program.Product, title, new Point((int)x, top), Theme.Text0, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "Windows && Linux machines · VMware ESXi · Proxmox VE   →   Proxmox VE · VMware ESXi", sub,
                    new Point((int)x + 1, top + th), Theme.Text2, TextFormatFlags.NoPadding);

                string brand = "CORE MTAALAM";
                string tech = "Technologies";
                using (var bold = new Font("Segoe UI", 10f, FontStyle.Bold))
                {
                    var bs = TextRenderer.MeasureText(brand, bold);
                    var ts = TextRenderer.MeasureText(tech, sub);
                    int right = Width - (int)pad;
                    TextRenderer.DrawText(g, brand, bold, new Point(right - bs.Width, h / 2 - bs.Height), Theme.Text0);
                    TextRenderer.DrawText(g, tech, sub, new Point(right - ts.Width, h / 2), Theme.Accent);
                }
            }

            using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, 2), Theme.Accent, Theme.Purple, LinearGradientMode.Horizontal))
            {
                var blend = new ColorBlend
                {
                    Colors = new[] { Theme.Accent, Theme.Blue, Theme.Purple },
                    Positions = new[] { 0f, 0.6f, 1f },
                };
                b.InterpolationColors = blend;
                g.FillRectangle(b, 0, h - 2, Width, 2);
            }
        }
    }

    static class IconWriter
    {
        public static void Write(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        Theme.DrawAppIcon(g, size);
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        images.Add(ms.ToArray());
                    }
                }
            }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
