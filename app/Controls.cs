using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// Small owner-drawn control set for the dark UI. All sizes go through Theme.S() so the
// window scales with the Windows display scaling instead of clipping text.

namespace SteamBypass
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(15, 20, 27);
        public static readonly Color Card = Color.FromArgb(24, 31, 41);
        public static readonly Color CardBorder = Color.FromArgb(40, 51, 65);
        public static readonly Color Raised = Color.FromArgb(34, 43, 56);
        public static readonly Color RaisedHover = Color.FromArgb(44, 56, 72);
        public static readonly Color Text = Color.FromArgb(232, 237, 243);
        public static readonly Color Muted = Color.FromArgb(138, 151, 166);
        public static readonly Color Green = Color.FromArgb(46, 189, 107);
        public static readonly Color Amber = Color.FromArgb(229, 169, 59);
        public static readonly Color Red = Color.FromArgb(235, 90, 82);
        public static readonly Color Off = Color.FromArgb(91, 103, 118);

        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
        public static float S(float v) { return v * Scale; }

        public static Font F(float pt) { return new Font("Segoe UI", pt, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Semi(float pt) { return new Font("Segoe UI Semibold", pt, FontStyle.Regular, GraphicsUnit.Point); }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Base class: double buffered, paints its parent's colour behind rounded shapes.
    class Surface : Control
    {
        public Surface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        }
        protected static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }
    }

    class Card : Surface
    {
        public Card() { BackColor = Theme.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(14)))
            using (var b = new SolidBrush(BackColor))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
    }

    class Logo : Surface
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            var r = new RectangleF(0, 0, Width, Height);
            using (var p = Theme.RoundRect(r, Theme.S(11)))
            using (var b = new LinearGradientBrush(r, Color.FromArgb(54, 136, 230), Color.FromArgb(28, 78, 160), 60f))
                g.FillPath(b, p);
            using (var f = Theme.Semi(15f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("S", f, Brushes.White, new RectangleF(0, -1, Width, Height), sf);
        }
    }

    // Big round power button; glows and pulses while the bypass is on.
    class PowerButton : Surface
    {
        bool on;
        float hover, hoverTarget, phase;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public PowerButton()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            timer.Interval = 33;
            timer.Tick += delegate
            {
                bool moving = Math.Abs(hoverTarget - hover) > 0.01f;
                if (moving) hover += (hoverTarget - hover) * 0.3f; else hover = hoverTarget;
                if (on) phase += 0.09f;
                if (on || moving) Invalidate();
            };
            timer.Start();
        }

        public bool On { get { return on; } set { if (on == value) return; on = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hoverTarget = 1f; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverTarget = 0f; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            float cx = Width / 2f, cy = Height / 2f;
            float r = Math.Min(Width, Height) / 2f - Theme.S(16);

            if (on)
            {
                float k = 0.65f + 0.35f * (float)Math.Sin(phase);
                for (int i = 0; i < 7; i++)
                {
                    float rr = r + Theme.S(3) + i * Theme.S(4);
                    int a = (int)(36 * (1 - i / 7f) * k);
                    using (var b = new SolidBrush(Color.FromArgb(a, Theme.Green)))
                        g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
                }
            }

            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            if (on)
                using (var b = new LinearGradientBrush(rect, Color.FromArgb(64, 210, 130), Color.FromArgb(30, 148, 83), 90f))
                    g.FillEllipse(b, rect);
            else
                using (var b = new SolidBrush(Theme.Lerp(Theme.Raised, Theme.RaisedHover, hover)))
                    g.FillEllipse(b, rect);

            Color ring = on ? Color.FromArgb(110, 255, 255, 255) : Theme.Lerp(Theme.CardBorder, Theme.Off, hover);
            using (var pen = new Pen(ring, Theme.S(2f))) g.DrawEllipse(pen, rect);

            float gr = r * 0.36f;
            Color gc = on ? Color.White : Theme.Lerp(Theme.Muted, Theme.Text, hover);
            using (var pen = new Pen(gc, Theme.S(5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, cx - gr, cy - gr, gr * 2, gr * 2, -55, 290);
                g.DrawLine(pen, cx, cy - gr * 1.25f, cx, cy - gr * 0.1f);
            }

            if (Focused)
                using (var pen = new Pen(Color.FromArgb(120, Theme.Text), 1f) { DashStyle = DashStyle.Dot })
                {
                    float fr = r + Theme.S(8);
                    g.DrawEllipse(pen, cx - fr, cy - fr, fr * 2, fr * 2);
                }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    class ToggleSwitch : Surface
    {
        bool on;
        float pos;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            timer.Interval = 15;
            timer.Tick += delegate
            {
                float target = on ? 1f : 0f;
                pos += (target - pos) * 0.35f;
                if (Math.Abs(target - pos) < 0.02f) { pos = target; timer.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                timer.Start();
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetCheckedSilently(bool v) { on = v; pos = v ? 1f : 0f; Invalidate(); }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            var tr = new RectangleF(1, 1, Width - 2, Height - 2);
            using (var p = Theme.RoundRect(tr, tr.Height / 2f))
            using (var b = new SolidBrush(Theme.Lerp(Theme.Raised, Theme.Green, pos)))
            using (var pen = new Pen(Theme.Lerp(Theme.CardBorder, Theme.Green, pos)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            float d = Height - Theme.S(8);
            float x = Theme.S(4) + (Width - Theme.S(8) - d) * pos;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, Theme.S(4), d, d);
            if (Focused)
                using (var pen = new Pen(Color.FromArgb(150, Theme.Text), 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    class FlatButton : Surface
    {
        bool hover, down;
        public FlatButton()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
        }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnClick(EventArgs e) { if (Enabled) base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            Color fill = !Enabled ? Theme.Card : down ? Theme.Bg : hover ? Theme.RaisedHover : Theme.Raised;
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(10)))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(Focused ? Theme.Off : Theme.CardBorder))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var f = Theme.Semi(9.5f))
            using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Off))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(Text, f, b, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    class StatTile : Surface
    {
        string value = "-";
        Color valueColor = Theme.Text;
        public string Caption = "";
        public StatTile() { BackColor = Theme.Card; }
        public string Value { get { return value; } set { if (this.value == value) return; this.value = value; Invalidate(); } }
        public Color ValueColor { get { return valueColor; } set { if (valueColor == value) return; valueColor = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(12)))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                using (var f = Theme.Semi(15f))
                using (var b = new SolidBrush(valueColor))
                    g.DrawString(value, f, b, new RectangleF(0, Theme.S(6), Width, Theme.S(32)), sf);
                using (var f = Theme.F(8f))
                using (var b = new SolidBrush(Theme.Muted))
                    g.DrawString(Caption, f, b, new RectangleF(0, Theme.S(36), Width, Theme.S(18)), sf);
            }
        }
    }

    // One line of the connection check: status icon, name, host, result text.
    class CheckRow : Surface
    {
        public enum St { Idle, Running, Ok, Fail }
        St status = St.Idle;
        string detail = "";
        float angle;
        readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer();
        public string Title = "", Subtitle = "";

        public CheckRow()
        {
            spin.Interval = 30;
            spin.Tick += delegate { angle = (angle + 20f) % 360f; Invalidate(); };
        }

        public void Set(St s, string text)
        {
            status = s;
            detail = text ?? "";
            if (s == St.Running) spin.Start(); else spin.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            float d = Theme.S(20), ix = Theme.S(2), iy = (Height - d) / 2f;
            var ir = new RectangleF(ix, iy, d, d);
            switch (status)
            {
                case St.Idle:
                    using (var pen = new Pen(Theme.Off, Theme.S(1.6f))) g.DrawEllipse(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2);
                    break;
                case St.Running:
                    using (var pen = new Pen(Color.FromArgb(60, Theme.Amber), Theme.S(2f))) g.DrawEllipse(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2);
                    using (var pen = new Pen(Theme.Amber, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2, angle, 100);
                    break;
                case St.Ok:
                    using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, ir);
                    using (var pen = new Pen(Color.White, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] {
                            new PointF(ix + d * 0.27f, iy + d * 0.53f), new PointF(ix + d * 0.44f, iy + d * 0.69f), new PointF(ix + d * 0.74f, iy + d * 0.33f) });
                    break;
                case St.Fail:
                    using (var b = new SolidBrush(Theme.Red)) g.FillEllipse(b, ir);
                    using (var pen = new Pen(Color.White, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, ix + d * 0.33f, iy + d * 0.33f, ix + d * 0.67f, iy + d * 0.67f);
                        g.DrawLine(pen, ix + d * 0.67f, iy + d * 0.33f, ix + d * 0.33f, iy + d * 0.67f);
                    }
                    break;
            }

            float tx = Theme.S(34);
            float rightW = Width * 0.42f;
            using (var f = Theme.F(9.5f))
            using (var b = new SolidBrush(Theme.Text))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Title, f, b, new RectangleF(tx, Theme.S(2), Width - tx - rightW, Theme.S(18)), sf);
            using (var f = Theme.F(8f))
            using (var b = new SolidBrush(Theme.Muted))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Subtitle, f, b, new RectangleF(tx, Theme.S(18), Width - tx - rightW, Theme.S(16)), sf);

            Color dc = status == St.Ok ? Theme.Green : status == St.Fail ? Theme.Red : status == St.Running ? Theme.Amber : Theme.Muted;
            string txt = status == St.Idle ? "Not tested" : status == St.Running ? "Testing..." : detail;
            using (var f = Theme.F(8.5f))
            using (var b = new SolidBrush(dc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(txt, f, b, new RectangleF(Width - rightW, 0, rightW, Height), sf);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }
}
