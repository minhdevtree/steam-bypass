using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamBypass
{
    class CheckSpec
    {
        public string Title, Subtitle, Url, Host;
        public bool UseProxy;
    }

    class ProbeResult
    {
        public bool Ok;
        public string Detail;
    }

    // Dark colours for the tray menu.
    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color MenuItemBorder { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelected { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.RaisedHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.RaisedHover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.RaisedHover; } }
        public override Color SeparatorDark { get { return Theme.CardBorder; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static readonly CheckSpec[] Specs = {
            new CheckSpec { Title = "Store", Subtitle = "store.steampowered.com", Url = "https://store.steampowered.com/", UseProxy = true },
            new CheckSpec { Title = "Community", Subtitle = "steamcommunity.com", Url = "https://steamcommunity.com/", UseProxy = true },
            new CheckSpec { Title = "Static content", Subtitle = "store.cloudflare.steamstatic.com", Url = "https://store.cloudflare.steamstatic.com/", UseProxy = true },
            new CheckSpec { Title = "Game downloads", Subtitle = "steamcontent.com cache servers", Url = "http://127.0.0.1/", Host = "cache8-hkg1.steamcontent.com", UseProxy = false }
        };

        readonly string dir;
        readonly Settings settings;
        readonly DomainSet domains;
        readonly ProxyServer proxy;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem miToggle = new ToolStripMenuItem();
        Icon iconOn, iconOff;

        PowerButton power;
        Label lblState, lblHint, lblCheckHint;
        StatTile tActive, tTotal, tErrors;
        FlatButton btnTest, btnRestart;
        CheckRow[] rows;
        ToggleSwitch swAuto, swWin;
        RichTextBox log;

        bool isOn, testing, restarting, reallyExit, startHidden, loading = true;

        public MainForm(string dir, bool hidden, bool forceOn)
        {
            this.dir = dir;
            startHidden = hidden;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;

            settings = new Settings(dir);
            domains = new DomainSet(dir);
            proxy = new ProxyServer(domains);
            proxy.Log = Append;

            iconOn = MakeIcon(Theme.Green);
            iconOff = MakeIcon(Theme.Off);

            Text = "Steam Bypass";
            Icon = iconOff;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(9f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            ClientSize = new Size(Theme.S(740), Theme.S(528));

            BuildUi();
            BuildTray();

            poll.Interval = 1000;
            poll.Tick += delegate { UpdateStats(); };
            poll.Start();

            swAuto.SetCheckedSilently(settings.AutoOn);
            swWin.SetCheckedSilently(GetRunAtLogon());
            loading = false;

            var h = Handle; // create the handle now so background threads can BeginInvoke while hidden
            SystemProxy.Restore(); // clean up after a previous crash
            ApplyUi();
            Append("Ready. Click the power button to turn the bypass on.");
            if (settings.AutoOn || forceOn) TurnOn(false);
        }

        // ------------------------------------------------------------------ layout
        static int S(int v) { return Theme.S(v); }

        static Label MakeLabel(Control parent, string text, Font font, Color color, int x, int y, int w, int h, ContentAlignment align)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = color, TextAlign = align, AutoSize = false,
                Bounds = new Rectangle(S(x), S(y), S(w), S(h)), BackColor = parent.BackColor
            };
            parent.Controls.Add(l);
            return l;
        }

        void BuildUi()
        {
            // ---- left column
            const int lx = 20, lw = 300;
            var logo = new Logo { Bounds = new Rectangle(S(lx), S(20), S(40), S(40)) };
            Controls.Add(logo);
            MakeLabel(this, "Steam Bypass", Theme.Semi(15f), Theme.Text, lx + 52, 18, 240, 26, ContentAlignment.MiddleLeft);
            MakeLabel(this, "Reach Steam on restricted networks", Theme.F(8.5f), Theme.Muted, lx + 52, 42, 248, 18, ContentAlignment.MiddleLeft);

            var hero = new Card { Bounds = new Rectangle(S(lx), S(80), S(lw), S(292)) };
            Controls.Add(hero);
            power = new PowerButton { Bounds = new Rectangle((S(lw) - S(150)) / 2, S(16), S(150), S(150)) };
            power.Click += delegate { Toggle(); };
            hero.Controls.Add(power);
            lblState = MakeLabel(hero, "Bypass is OFF", Theme.Semi(18f), Theme.Muted, 0, 176, lw, 36, ContentAlignment.MiddleCenter);
            lblHint = MakeLabel(hero, "", Theme.F(9f), Theme.Muted, 26, 216, lw - 52, 60, ContentAlignment.TopCenter);

            int gap = S(10);
            int tw = (S(lw) - 2 * gap) / 3;
            tActive = new StatTile { Caption = "Active", Bounds = new Rectangle(S(lx), S(384), tw, S(60)) };
            tTotal = new StatTile { Caption = "Connections", Bounds = new Rectangle(S(lx) + tw + gap, S(384), tw, S(60)) };
            tErrors = new StatTile { Caption = "Errors", Bounds = new Rectangle(S(lx) + 2 * (tw + gap), S(384), S(lw) - 2 * (tw + gap), S(60)) };
            Controls.AddRange(new Control[] { tActive, tTotal, tErrors });

            int bw = (S(lw) - gap) / 2;
            btnTest = new FlatButton { Text = "Run test", Bounds = new Rectangle(S(lx), S(456), bw, S(52)) };
            btnTest.Click += delegate { RunTest(); };
            btnRestart = new FlatButton { Text = "Restart Steam", Bounds = new Rectangle(S(lx) + bw + gap, S(456), S(lw) - bw - gap, S(52)) };
            btnRestart.Click += delegate { RestartSteam(); };
            Controls.AddRange(new Control[] { btnTest, btnRestart });

            // ---- right column
            const int rx = 340, rw = 380;
            var check = new Card { Bounds = new Rectangle(S(rx), S(20), S(rw), S(206)) };
            Controls.Add(check);
            MakeLabel(check, "Connection check", Theme.Semi(11f), Theme.Text, 18, 12, 200, 24, ContentAlignment.MiddleLeft);
            lblCheckHint = MakeLabel(check, "Not run yet", Theme.F(8f), Theme.Muted, 190, 12, rw - 208, 24, ContentAlignment.MiddleRight);
            rows = new CheckRow[Specs.Length];
            for (int i = 0; i < Specs.Length; i++)
            {
                rows[i] = new CheckRow { Title = Specs[i].Title, Subtitle = Specs[i].Subtitle, Bounds = new Rectangle(S(18), S(46 + i * 38), S(rw - 36), S(36)) };
                check.Controls.Add(rows[i]);
            }

            var settingsCard = new Card { Bounds = new Rectangle(S(rx), S(238), S(rw), S(116)) };
            Controls.Add(settingsCard);
            MakeLabel(settingsCard, "Turn on when app starts", Theme.F(9.5f), Theme.Text, 18, 10, 270, 22, ContentAlignment.MiddleLeft);
            MakeLabel(settingsCard, "Enable the bypass as soon as this app opens", Theme.F(8f), Theme.Muted, 18, 31, 280, 18, ContentAlignment.MiddleLeft);
            swAuto = new ToggleSwitch { Bounds = new Rectangle(S(rw - 18 - 44), S(18), S(44), S(24)) };
            swAuto.CheckedChanged += delegate { if (!loading) { settings.AutoOn = swAuto.Checked; settings.Save(); } };
            settingsCard.Controls.Add(swAuto);
            settingsCard.Controls.Add(new Panel { Bounds = new Rectangle(S(18), S(58), S(rw - 36), 1), BackColor = Theme.CardBorder });
            MakeLabel(settingsCard, "Start with Windows", Theme.F(9.5f), Theme.Text, 18, 66, 270, 22, ContentAlignment.MiddleLeft);
            MakeLabel(settingsCard, "Launch minimized to the tray when you sign in", Theme.F(8f), Theme.Muted, 18, 87, 290, 18, ContentAlignment.MiddleLeft);
            swWin = new ToggleSwitch { Bounds = new Rectangle(S(rw - 18 - 44), S(74), S(44), S(24)) };
            swWin.CheckedChanged += delegate { if (!loading) SetRunAtLogon(swWin.Checked); };
            settingsCard.Controls.Add(swWin);

            var act = new Card { Bounds = new Rectangle(S(rx), S(366), S(rw), S(142)) };
            Controls.Add(act);
            MakeLabel(act, "Activity", Theme.Semi(11f), Theme.Text, 18, 10, 120, 24, ContentAlignment.MiddleLeft);
            var clear = MakeLabel(act, "Clear", Theme.F(8.5f), Theme.Muted, rw - 18 - 60, 10, 60, 24, ContentAlignment.MiddleRight);
            clear.Cursor = Cursors.Hand;
            clear.MouseEnter += delegate { clear.ForeColor = Theme.Text; };
            clear.MouseLeave += delegate { clear.ForeColor = Theme.Muted; };
            clear.Click += delegate { log.Clear(); };
            log = new RichTextBox
            {
                Bounds = new Rectangle(S(18), S(38), S(rw - 32), S(142 - 38 - 12)),
                BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted,
                ReadOnly = true, DetectUrls = false, TabStop = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Consolas", 8.5f), WordWrap = true
            };
            act.Controls.Add(log);
            SetWindowTheme(log.Handle, "DarkMode_Explorer", null);
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false, Font = Theme.F(9f) };
            miToggle.Click += delegate { Toggle(); };
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add(miToggle);
            menu.Items.Add("Run test", null, delegate { RunTest(); });
            menu.Items.Add("Restart Steam", null, delegate { RestartSteam(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.Text;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = true;
        }

        static Icon MakeIcon(Color c)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var pen = new Pen(Color.White, 3.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    g.DrawArc(pen, 8.5f, 9.5f, 15f, 15f, -55, 290);
                    g.DrawLine(pen, 16f, 6.5f, 16f, 15f);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int one = 1, cap = ColorTranslator.ToWin32(Theme.Bg), txt = ColorTranslator.ToWin32(Theme.Text);
                DwmSetWindowAttribute(Handle, 20, ref one, 4);  // dark title bar
                DwmSetWindowAttribute(Handle, 35, ref cap, 4);  // caption colour (Windows 11)
                DwmSetWindowAttribute(Handle, 36, ref txt, 4);  // caption text colour
            }
            catch { }
        }

        // ------------------------------------------------------------------- state
        void Append(string s)
        {
            if (IsDisposed) return;
            try { lock (dir) File.AppendAllText(Path.Combine(dir, "steam-bypass.log"), DateTime.Now.ToString("s") + "  " + s + "\r\n"); } catch { }
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), s); } catch { } return; }
            if (log == null) return;
            Color c = Color.FromArgb(174, 185, 199);
            string low = s.ToLowerInvariant();
            if (low.StartsWith("error") || low.Contains("warning") || low.Contains("cannot")) c = Theme.Red;
            else if (s.StartsWith("ON ") || s.Contains("restarted") || s.StartsWith("Test passed")) c = Theme.Green;
            else if (s.StartsWith("OFF")) c = Theme.Muted;
            if (log.TextLength > 30000) log.Clear();
            log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = Theme.Off;
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
            log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = c;
            log.AppendText(s + "\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        void UpdateStats()
        {
            tActive.Value = isOn ? proxy.Active.ToString() : "-";
            tTotal.Value = isOn ? proxy.Total.ToString() : "-";
            tErrors.Value = isOn ? proxy.Failed.ToString() : "-";
            tErrors.ValueColor = isOn && proxy.Failed > 0 ? Theme.Red : Theme.Text;
            tActive.ValueColor = isOn && proxy.Active > 0 ? Theme.Green : Theme.Text;
        }

        void ApplyUi()
        {
            power.On = isOn;
            lblState.Text = isOn ? "Bypass is ON" : "Bypass is OFF";
            lblState.ForeColor = isOn ? Theme.Green : Theme.Muted;
            lblHint.Text = isOn
                ? "Steam traffic is routed through 127.0.0.1:" + ProxyServer.Port + ".\nKeep this app running while you use Steam."
                : "Turn it on to reach Steam. Nothing on your PC is changed while it is off.";
            tray.Icon = isOn ? iconOn : iconOff;
            tray.Text = isOn ? "Steam Bypass: ON" : "Steam Bypass: OFF";
            miToggle.Text = isOn ? "Turn OFF" : "Turn ON";
            btnTest.Enabled = !testing;
            btnRestart.Enabled = !restarting;
            btnRestart.Text = restarting ? "Restarting..." : "Restart Steam";
            UpdateStats();
        }

        void Toggle()
        {
            if (isOn) TurnOff(); else TurnOn(true);
        }

        void TurnOn(bool interactive)
        {
            try
            {
                proxy.Start();
                SystemProxy.Apply(ProxyServer.PacUrl);
                isOn = true;
                Append("ON - proxy 127.0.0.1:" + ProxyServer.Port + ", Windows PAC set for Steam domains.");
            }
            catch (Exception ex)
            {
                proxy.Stop();
                isOn = false;
                Append("ERROR: " + ex.Message + (ex is SocketException ? "  (is port " + ProxyServer.Port + " in use?)" : ""));
            }
            ApplyUi();
            if (!isOn) return;

            // steam.exe reads the proxy settings only when it starts, so a Steam that was already open stays offline
            if (SteamRunning())
            {
                Append("Steam is already running. If it shows NO CONNECTION, restart it (Restart Steam).");
                if (interactive &&
                    MessageBox.Show(this, "Steam is already running. It only picks up the bypass when it starts, so if it was opened before this, it will stay offline.\n\nRestart Steam now? (Downloads resume automatically.)",
                        "Steam Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RestartSteam();
                else if (!interactive)
                    tray.ShowBalloonTip(5000, "Steam Bypass", "Steam was already open. Use 'Restart Steam' in the tray menu so it uses the bypass.", ToolTipIcon.Warning);
            }
        }

        void TurnOff()
        {
            SystemProxy.Restore();
            proxy.Stop();
            isOn = false;
            Append("OFF - proxy stopped, Windows proxy settings restored.");
            ApplyUi();
        }

        // ------------------------------------------------------------------- steam
        static bool SteamRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }

        static string FindSteamExe()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var exe = k.GetValue("SteamExe") as string;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
                    var d = k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "steam.exe"))) return Path.Combine(d, "steam.exe");
                }
            }
            catch { }
            return null;
        }

        async void RestartSteam()
        {
            if (restarting) return;
            var exe = FindSteamExe();
            if (exe == null) { Append("Error: could not find the Steam installation."); return; }
            restarting = true;
            ApplyUi();
            Append("Restarting Steam ...");
            string err = await Task.Run(() =>
            {
                try
                {
                    Process.Start(exe, "-shutdown");
                    for (int i = 0; i < 40 && (Process.GetProcessesByName("steam").Length > 0 || Process.GetProcessesByName("steamwebhelper").Length > 0); i++)
                        Thread.Sleep(1000);
                    Process.Start(exe);
                    return (string)null;
                }
                catch (Exception ex) { return ex.Message; }
            });
            restarting = false;
            Append(err == null ? "Steam restarted." : "Error: could not restart Steam: " + err);
            ApplyUi();
        }

        // -------------------------------------------------------------------- test
        static ProbeResult Probe(CheckSpec s, bool viaProxy)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(s.Url);
                req.Proxy = (viaProxy && s.UseProxy) ? new WebProxy("127.0.0.1", ProxyServer.Port) : null;
                if (s.Host != null) req.Host = s.Host;
                req.Timeout = 12000;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.UserAgent = "Mozilla/5.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return new ProbeResult { Ok = true, Detail = "HTTP " + (int)resp.StatusCode + " · " + sw.ElapsedMilliseconds + " ms" };
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null)
                {
                    int code = (int)hr.StatusCode;
                    hr.Close();
                    return new ProbeResult { Ok = true, Detail = "HTTP " + code + " · " + sw.ElapsedMilliseconds + " ms" };
                }
                return new ProbeResult { Ok = false, Detail = Describe(ex) };
            }
            catch (Exception ex)
            {
                return new ProbeResult { Ok = false, Detail = ex.Message };
            }
        }

        static string Describe(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.ConnectFailure: return "connection refused";
                case WebExceptionStatus.Timeout: return "timed out";
                case WebExceptionStatus.NameResolutionFailure: return "DNS failed";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "proxy not reachable";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.UnknownError: return "blocked (reset)";
                default: return ex.Status.ToString();
            }
        }

        async void RunTest()
        {
            if (testing) return;
            testing = true;
            ApplyUi();
            bool viaProxy = isOn;
            lblCheckHint.Text = viaProxy ? "Testing through the bypass..." : "Testing directly (bypass is off)...";
            foreach (var r in rows) r.Set(CheckRow.St.Running, "");
            int ok = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < Specs.Length; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(() => Probe(Specs[idx], viaProxy)).ContinueWith(t =>
                {
                    var res = t.Result;
                    if (res.Ok) Interlocked.Increment(ref ok);
                    rows[idx].Set(res.Ok ? CheckRow.St.Ok : CheckRow.St.Fail, res.Detail);
                }, TaskScheduler.FromCurrentSynchronizationContext()));
            }
            await Task.WhenAll(tasks);
            testing = false;
            lblCheckHint.Text = (viaProxy ? "Via bypass" : "Direct") + " · " + DateTime.Now.ToString("HH:mm:ss");
            Append((ok == Specs.Length ? "Test passed" : "Test result") + ": " + ok + "/" + Specs.Length + " reachable (" + (viaProxy ? "via bypass" : "direct") + ").");
            ApplyUi();
        }

        // ------------------------------------------------------------- housekeeping
        bool GetRunAtLogon()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k.GetValue("SteamBypass") != null; }
            catch { return false; }
        }

        void SetRunAtLogon(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("SteamBypass", "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue("SteamBypass", false);
                }
            }
            catch (Exception ex) { Append("Error: could not change autostart: " + ex.Message); }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(startHidden ? false : value);
        }

        void ShowWindow()
        {
            startHidden = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            if (isOn) TurnOff();
            reallyExit = true;
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1500, "Steam Bypass", "Still running in the tray.", ToolTipIcon.Info);
                return;
            }
            if (isOn) SystemProxy.Restore(); // Windows shutdown / forced close
            base.OnFormClosing(e);
        }
    }
}
