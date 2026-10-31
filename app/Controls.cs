using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// Small owner-drawn control set for the dark UI. Every size goes through Theme.S() so the window
// follows the Windows display scaling, and every text colour keeps at least 4.5:1 on the surface it
// sits on (the muted and faint tiers were measured: 6.8:1 and 4.8:1 on the card).

namespace SteamBypass
{
    static class Theme
    {
        // Neutral greys, in line with Windows 11 dark mode. Colour is reserved for state.
        public static readonly Color Bg = Color.FromArgb(23, 23, 23);
        public static readonly Color Card = Color.FromArgb(33, 33, 33);
        public static readonly Color Border = Color.FromArgb(51, 51, 51);
        public static readonly Color Raised = Color.FromArgb(43, 43, 43);
        public static readonly Color RaisedHover = Color.FromArgb(54, 54, 54);
        public static readonly Color Text = Color.FromArgb(243, 243, 243);
        public static readonly Color Muted = Color.FromArgb(168, 168, 168);   // secondary text, 6.8:1 on Card
        public static readonly Color Faint = Color.FromArgb(140, 140, 140);   // meta text, 4.8:1 on Card
        public static readonly Color Stroke = Color.FromArgb(90, 90, 90);     // decoration only, never text
        public static readonly Color Green = Color.FromArgb(52, 192, 110);    // "the bypass is on" and passed checks
        public static readonly Color GreenDeep = Color.FromArgb(31, 157, 85); // fill behind white (3.5:1)
        public static readonly Color Amber = Color.FromArgb(229, 169, 59);
        public static readonly Color Red = Color.FromArgb(240, 96, 90);
        public static readonly Color Accent = Color.FromArgb(47, 111, 235);   // the product mark only

        // Type scale in points: meta, body, title, value, state.
        public const float TMeta = 8.5f, TBody = 9.5f, TTitle = 11f, TValue = 15f, TState = 18f;

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

    // Base class: double buffered, not a Tab stop unless it is something you can operate.
    class Surface : Control
    {
        public Surface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        protected void Interactive(AccessibleRole role)
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = role;
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

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    class Card : Surface
    {
        public Card() { BackColor = Theme.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(12)))
            using (var b = new SolidBrush(BackColor))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
    }

    // The product mark: an open ring (the "gap" in the handshake the bypass slips through) around a dot.
    class Logo : Surface
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            var r = new RectangleF(0, 0, Width, Height);
            using (var p = Theme.RoundRect(r, Theme.S(10)))
            using (var b = new SolidBrush(Theme.Accent))
                g.FillPath(b, p);
            float cx = Width / 2f, cy = Height / 2f, rr = Width * 0.25f;
            using (var pen = new Pen(Color.White, Theme.S(3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, cx - rr, cy - rr, rr * 2, rr * 2, -25, 300);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, cx - Theme.S(2.5f), cy - Theme.S(2.5f), Theme.S(5f), Theme.S(5f));
        }
    }

    // Big round power button. Flat: a filled disc, one ring, a glyph. It eases between states and
    // then stops its timer, so an idle tray app does no redrawing.
    class PowerButton : Surface
    {
        public enum Mode { Off, On, Error }
        Mode mode = Mode.Off;
        float on, onTarget, hover, hoverTarget;
        bool down;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public PowerButton()
        {
            Interactive(AccessibleRole.PushButton);
            timer.Interval = 16;
            timer.Tick += delegate
            {
                bool moving = false;
                if (Math.Abs(onTarget - on) > 0.01f) { on += (onTarget - on) * 0.28f; moving = true; } else on = onTarget;
                if (Math.Abs(hoverTarget - hover) > 0.01f) { hover += (hoverTarget - hover) * 0.35f; moving = true; } else hover = hoverTarget;
                Invalidate();
                if (!moving) timer.Stop();
            };
        }

        public Mode State
        {
            get { return mode; }
            set { if (mode == value) return; mode = value; onTarget = value == Mode.On ? 1f : 0f; timer.Start(); Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hoverTarget = 1f; timer.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverTarget = 0f; down = false; timer.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            float cx = Width / 2f, cy = Height / 2f;
            float r = Math.Min(Width, Height) / 2f - Theme.S(20);
            if (down) r -= Theme.S(2);
            bool err = mode == Mode.Error;

            var rest = Theme.Lerp(Theme.Raised, Theme.RaisedHover, hover);
            var disc = Theme.Lerp(rest, Theme.GreenDeep, on);
            Color ringCol = err ? Color.FromArgb(150, Theme.Red) : Theme.Lerp(Theme.Border, Color.FromArgb(140, Theme.Green), on);
            Color glyph = err ? Theme.Red : Theme.Lerp(Theme.Lerp(Theme.Muted, Theme.Text, hover), Color.White, on);

            float rr = r + Theme.S(9);
            using (var pen = new Pen(ringCol, Theme.S(2f))) g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            using (var b = new SolidBrush(disc)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);

            float gr = r * 0.36f;
            using (var pen = new Pen(glyph, Theme.S(5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, cx - gr, cy - gr, gr * 2, gr * 2, -55, 290);
                g.DrawLine(pen, cx, cy - gr * 1.25f, cx, cy - gr * 0.1f);
            }

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Text, 1f) { DashStyle = DashStyle.Dot })
                {
                    float fr = rr + Theme.S(5);
                    g.DrawEllipse(pen, cx - fr, cy - fr, fr * 2, fr * 2);
                }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // Neutral on-state (near-white track, dark knob) so green keeps meaning "the bypass is on".
    class ToggleSwitch : Surface
    {
        bool on;
        float pos;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            Interactive(AccessibleRole.CheckButton);
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
                AccessibleDescription = on ? "On" : "Off";
                timer.Start();
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetCheckedSilently(bool v) { on = v; pos = v ? 1f : 0f; AccessibleDescription = v ? "On" : "Off"; Invalidate(); }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            var tr = new RectangleF(1, 1, Width - 2, Height - 2);
            var onTrack = Color.FromArgb(230, 230, 230);
            using (var p = Theme.RoundRect(tr, tr.Height / 2f))
            using (var b = new SolidBrush(Theme.Lerp(Theme.Raised, onTrack, pos)))
            using (var pen = new Pen(Theme.Lerp(Theme.Stroke, onTrack, pos)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            float d = Height - Theme.S(10);
            float x = Theme.S(5) + (Width - Theme.S(10) - d) * pos;
            using (var b = new SolidBrush(Theme.Lerp(Theme.Muted, Theme.Bg, pos)))
                g.FillEllipse(b, x, Theme.S(5), d, d);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Text, 1f) { DashStyle = DashStyle.Dot })
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
        public bool Compact;
        public FlatButton() { Interactive(AccessibleRole.PushButton); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; Invalidate(); base.OnTextChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnClick(EventArgs e) { if (Enabled) base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            Color fill = !Enabled ? Parent.BackColor : down ? Theme.Bg : hover ? Theme.RaisedHover : Theme.Raised;
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(Compact ? 7 : 9)))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(Focused && ShowFocusCues ? Theme.Muted : Theme.Border))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var f = Theme.Semi(Compact ? Theme.TMeta + 0.5f : Theme.TBody))
            using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Faint))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(Text, f, b, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    // A quiet text action (underlines on hover).
    class TextAction : Surface
    {
        bool hover;
        public TextAction() { Interactive(AccessibleRole.Link); }
        protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; Invalidate(); base.OnTextChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            using (var f = new Font("Segoe UI", Theme.TMeta, hover || (Focused && ShowFocusCues) ? FontStyle.Underline : FontStyle.Regular, GraphicsUnit.Point))
            using (var b = new SolidBrush(hover ? Theme.Text : Theme.Muted))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Text, f, b, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    // Three figures in one container, split by hairlines (no tile per figure).
    class StatStrip : Surface
    {
        public readonly string[] Captions = { "Active", "Total", "Errors" };
        readonly string[] values = { "0", "0", "0" };
        readonly Color[] colors = { Theme.Text, Theme.Text, Theme.Text };

        public StatStrip() { BackColor = Theme.Card; AccessibleRole = AccessibleRole.Grouping; AccessibleName = "Connection statistics"; }

        public void Set(int i, string value, Color color)
        {
            if (values[i] == value && colors[i] == color) return;
            values[i] = value; colors[i] = color;
            AccessibleDescription = Captions[0] + " " + values[0] + ", " + Captions[1] + " " + values[1] + ", " + Captions[2] + " " + values[2];
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            int n = 3;
            float w = Width / (float)n;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var fv = Theme.Semi(Theme.TValue))
            using (var fc = Theme.F(Theme.TMeta))
            using (var bc = new SolidBrush(Theme.Muted))
            using (var pen = new Pen(Theme.Border))
            {
                for (int i = 0; i < n; i++)
                {
                    using (var bv = new SolidBrush(colors[i]))
                        g.DrawString(values[i], fv, bv, new RectangleF(i * w, Theme.S(10), w, Theme.S(26)), sf);
                    g.DrawString(Captions[i], fc, bc, new RectangleF(i * w, Theme.S(36), w, Theme.S(18)), sf);
                    if (i > 0) g.DrawLine(pen, i * w, Theme.S(12), i * w, Height - Theme.S(12));
                }
            }
        }
    }

    // The Steam row: a status dot, the word "Steam" and one line saying how Steam relates to the bypass.
    class SteamStatus : Surface
    {
        string text = "";
        Color color = Theme.Muted;
        public SteamStatus() { BackColor = Theme.Card; AccessibleRole = AccessibleRole.StaticText; }

        public void Set(string status, Color c)
        {
            if (text == status && color == c) return;
            text = status; color = c;
            AccessibleName = "Steam: " + status;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Quality(g);
            float d = Theme.S(8);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, 0, Theme.S(7), d, d);
            using (var f = Theme.Semi(Theme.TBody))
            using (var b = new SolidBrush(Theme.Text))
                g.DrawString("Steam", f, b, new PointF(Theme.S(18), Theme.S(1)));
            using (var f = Theme.F(Theme.TMeta))
            using (var b = new SolidBrush(color))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord })
                g.DrawString(text, f, b, new RectangleF(Theme.S(18), Theme.S(22), Width - Theme.S(18), Height - Theme.S(22)), sf);
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
            AccessibleRole = AccessibleRole.Row;
            spin.Interval = 30;
            spin.Tick += delegate { angle = (angle + 20f) % 360f; Invalidate(); };
        }

        string StatusText()
        {
            return status == St.Idle ? "Not tested" : status == St.Running ? "Testing..." : detail;
        }

        public void Set(St s, string text)
        {
            status = s;
            detail = text ?? "";
            if (s == St.Running) spin.Start(); else spin.Stop();
            AccessibleName = Title + ": " + (s == St.Ok ? "reachable, " : s == St.Fail ? "failed, " : "") + StatusText();
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
                    using (var pen = new Pen(Theme.Stroke, Theme.S(1.6f))) g.DrawEllipse(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2);
                    break;
                case St.Running:
                    using (var pen = new Pen(Color.FromArgb(60, Theme.Amber), Theme.S(2f))) g.DrawEllipse(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2);
                    using (var pen = new Pen(Theme.Amber, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, ir.X + 1, ir.Y + 1, d - 2, d - 2, angle, 100);
                    break;
                case St.Ok:
                    using (var b = new SolidBrush(Theme.GreenDeep)) g.FillEllipse(b, ir);
                    using (var pen = new Pen(Color.White, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] {
                            new PointF(ix + d * 0.27f, iy + d * 0.53f), new PointF(ix + d * 0.44f, iy + d * 0.69f), new PointF(ix + d * 0.74f, iy + d * 0.33f) });
                    break;
                case St.Fail:
                    using (var b = new SolidBrush(Theme.Red)) g.FillEllipse(b, ir);
                    using (var pen = new Pen(Theme.Bg, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, ix + d * 0.33f, iy + d * 0.33f, ix + d * 0.67f, iy + d * 0.67f);
                        g.DrawLine(pen, ix + d * 0.67f, iy + d * 0.33f, ix + d * 0.33f, iy + d * 0.67f);
                    }
                    break;
            }

            float tx = Theme.S(34);
            float rightW = Width * 0.40f;
            using (var f = Theme.F(Theme.TBody))
            using (var b = new SolidBrush(Theme.Text))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Title, f, b, new RectangleF(tx, Theme.S(2), Width - tx - rightW, Theme.S(18)), sf);
            using (var f = Theme.F(Theme.TMeta))
            using (var b = new SolidBrush(Theme.Faint))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(Subtitle, f, b, new RectangleF(tx, Theme.S(19), Width - tx - rightW, Theme.S(16)), sf);

            Color dc = status == St.Ok ? Theme.Green : status == St.Fail ? Theme.Red : status == St.Running ? Theme.Amber : Theme.Faint;
            using (var f = Theme.F(Theme.TMeta))
            using (var b = new SolidBrush(dc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(StatusText(), f, b, new RectangleF(Width - rightW, 0, rightW, Height), sf);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }

    internal enum ToastKind { Info, Success, Warning, Error }

    // On-screen notice, drawn by the app instead of the Windows tray balloon (which Focus Assist
    // swallows and the OS restyles). Borderless, always on top, never takes focus, lets clicks fall
    // through, fades in and out, and sizes itself to its text. Warnings and errors stay longer.
    internal sealed class Toast : Form
    {
        static Toast current;
        const int FadeIn = 140, FadeOut = 220;

        readonly string title, detail;
        readonly ToastKind kind;
        readonly Rectangle titleRect, detailRect;
        readonly int holdMs;
        readonly DateTime started = DateTime.Now;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();

        // Call on the UI thread. Replaces a notice that is still showing.
        public static Toast Flash(string title, string detail, ToastKind kind)
        {
            try
            {
                if (current != null && !current.IsDisposed) current.Close();
                current = new Toast(title, detail ?? "", kind);
                current.Show();
                return current;
            }
            catch { return null; }
        }

        Toast(string title, string detail, ToastKind kind)
        {
            this.title = title;
            this.detail = detail;
            this.kind = kind;
            holdMs = kind == ToastKind.Error ? 6000 : kind == ToastKind.Warning ? 5000 : 2600;

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Raised;
            Opacity = 0;

            int width = Theme.S(420), left = Theme.S(52), textW = width - left - Theme.S(18);
            int detailH = 0;
            if (detail.Length > 0)
                using (var f = Theme.F(Theme.TBody))
                {
                    var m = TextRenderer.MeasureText(detail, f, new Size(textW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    detailH = Math.Min(m.Height, f.Height * 2 + 2);
                }
            int height = Theme.S(14) + Theme.S(22) + (detailH > 0 ? Theme.S(2) + detailH : 0) + Theme.S(14);
            titleRect = new Rectangle(left, Theme.S(14), textW, Theme.S(22));
            detailRect = new Rectangle(left, Theme.S(38), textW, detailH);
            Size = new Size(width, height);

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Theme.S(56));
            using (var p = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Theme.S(12)))
                Region = new Region(p);

            anim.Interval = 16;
            anim.Tick += delegate
            {
                double t = (DateTime.Now - started).TotalMilliseconds;
                if (t < FadeIn) Opacity = t / FadeIn;
                else if (t < FadeIn + holdMs) Opacity = 1;
                else if (t < FadeIn + holdMs + FadeOut) Opacity = 1 - (t - FadeIn - holdMs) / FadeOut;
                else { anim.Stop(); Close(); }
            };
            Shown += delegate { anim.Start(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;   // no-activate, tool window (no Alt-Tab entry), click-through
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Raised);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(12)))
            using (var pen = new Pen(Theme.Stroke))
                g.DrawPath(pen, p);

            float d = Theme.S(22), ix = Theme.S(16), iy = (Height - d) / 2f;
            var ir = new RectangleF(ix, iy, d, d);
            Color fill = kind == ToastKind.Success ? Theme.GreenDeep : kind == ToastKind.Error ? Theme.Red : kind == ToastKind.Warning ? Theme.Amber : Theme.Stroke;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, ir);
            Color mark = kind == ToastKind.Success || kind == ToastKind.Info ? Color.White : Theme.Bg;
            using (var pen = new Pen(mark, Theme.S(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(mark))
            {
                switch (kind)
                {
                    case ToastKind.Success:
                        g.DrawLines(pen, new[] { new PointF(ix + d * 0.27f, iy + d * 0.53f), new PointF(ix + d * 0.44f, iy + d * 0.69f), new PointF(ix + d * 0.74f, iy + d * 0.33f) });
                        break;
                    case ToastKind.Error:
                        g.DrawLine(pen, ix + d * 0.33f, iy + d * 0.33f, ix + d * 0.67f, iy + d * 0.67f);
                        g.DrawLine(pen, ix + d * 0.67f, iy + d * 0.33f, ix + d * 0.33f, iy + d * 0.67f);
                        break;
                    case ToastKind.Warning:
                        g.DrawLine(pen, ix + d * 0.5f, iy + d * 0.25f, ix + d * 0.5f, iy + d * 0.57f);
                        g.FillEllipse(b, ix + d * 0.5f - Theme.S(1.4f), iy + d * 0.72f - Theme.S(1.4f), Theme.S(2.8f), Theme.S(2.8f));
                        break;
                    default:
                        g.DrawLine(pen, ix + d * 0.5f, iy + d * 0.45f, ix + d * 0.5f, iy + d * 0.72f);
                        g.FillEllipse(b, ix + d * 0.5f - Theme.S(1.4f), iy + d * 0.28f - Theme.S(1.4f), Theme.S(2.8f), Theme.S(2.8f));
                        break;
                }
            }

            using (var f = Theme.Semi(Theme.TTitle))
                TextRenderer.DrawText(g, title, f, titleRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (detailRect.Height > 0)
                using (var f = Theme.F(Theme.TBody))
                    TextRenderer.DrawText(g, detail, f, detailRect, Theme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            if (current == this) current = null;
            base.Dispose(disposing);
        }
    }
}
