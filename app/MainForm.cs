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
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelected { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.RaisedHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.RaisedHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.RaisedHover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.RaisedHover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    // Asks Windows whether steam.exe holds a connection to our proxy port. A Steam that was opened
    // before the bypass was turned on never does: it read the proxy settings at startup.
    static class SteamLink
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        public static bool UsesProxy(ICollection<int> pids, int port)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);   // AF_INET, TCP_TABLE_OWNER_PID_ALL
            if (size <= 0) return false;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return false;
                int n = Marshal.ReadInt32(buf);
                IntPtr row = IntPtr.Add(buf, 4);
                for (int i = 0; i < n; i++, row = IntPtr.Add(row, 24))
                {
                    int state = Marshal.ReadInt32(row, 0);
                    int remoteAddr = Marshal.ReadInt32(row, 12);
                    int remotePortNet = Marshal.ReadInt32(row, 16);
                    int pid = Marshal.ReadInt32(row, 20);
                    int remotePort = ((remotePortNet & 0xFF) << 8) | ((remotePortNet >> 8) & 0xFF);
                    if (state == 5 && remoteAddr == 0x0100007F && remotePort == port && pids.Contains(pid)) return true;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return false;
        }
    }

    class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Dev preview (screenshots): no tray icon, no registry, no sockets, no log file.
        internal static bool PreviewMode;

        enum SteamState { NotRunning, Starting, Using, NeedsRestart, RunningOff }

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
        Label lblState, lblHint, lblCheckSub;
        StatStrip stats;
        SteamStatus steamStatus;
        FlatButton btnTest, btnSteam;
        CheckRow[] rows;
        ToggleSwitch swAuto, swWin;
        RichTextBox log;

        bool isOn, testing, restarting, reallyExit, startHidden, loading = true;
        string startError;            // why the last attempt to turn on failed, or null
        DateTime onSince;
        int steamOverride = -1;       // preview only
        SteamState steamState = SteamState.NotRunning;

        public MainForm(string dir, bool hidden, bool forceOn)
        {
            this.dir = dir;
            startHidden = hidden;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;

            settings = new Settings(dir);
            domains = new DomainSet(dir);
            proxy = new ProxyServer(domains);
            proxy.Log = Append;
            proxy.Verbose = AppendFile;

            iconOn = MakeIcon(Theme.GreenDeep);
            iconOff = MakeIcon(Theme.Stroke);

            Text = "Steam Bypass";
            Icon = iconOff;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(Theme.TBody);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            ClientSize = new Size(Theme.S(740), Theme.S(528));

            BuildUi();
            BuildTray();

            if (!PreviewMode)
            {
                poll.Interval = 1000;
                poll.Tick += delegate { if (Visible) { UpdateStats(); UpdateSteam(); } };
                poll.Start();
            }

            swAuto.SetCheckedSilently(settings.AutoOn);
            swWin.SetCheckedSilently(PreviewMode ? true : GetRunAtLogon());
            loading = false;

            var h = Handle; // create the handle now so background threads can BeginInvoke while hidden
            if (!PreviewMode) SystemProxy.Restore(); // clean up after a previous crash
            ApplyUi();
            Append("Ready.");
            if (!PreviewMode && (settings.AutoOn || forceOn)) TurnOn(false);
        }

        // ------------------------------------------------------------------ layout
        static int S(int v) { return Theme.S(v); }

        static Label MakeLabel(Control parent, string text, Font font, Color color, int x, int y, int w, int h, ContentAlignment align)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = color, TextAlign = align, AutoSize = false,
                Bounds = new Rectangle(S(x), S(y), S(w), S(h)), BackColor = parent.BackColor, UseMnemonic = false
            };
            parent.Controls.Add(l);
            return l;
        }

        static void Divider(Control parent, int x, int y, int w)
        {
            parent.Controls.Add(new Panel { Bounds = new Rectangle(S(x), S(y), S(w), 1), BackColor = Theme.Border });
        }

        void BuildUi()
        {
            // ---- left column: one card with the power button, the figures and the Steam row
            const int lx = 20, lw = 300;
            Controls.Add(new Logo { Bounds = new Rectangle(S(lx), S(20), S(40), S(40)), AccessibleName = "Steam Bypass", AccessibleRole = AccessibleRole.Graphic });
            MakeLabel(this, "Steam Bypass", Theme.Semi(Theme.TValue), Theme.Text, lx + 52, 17, 240, 26, ContentAlignment.MiddleLeft);
            MakeLabel(this, "Reach Steam on restricted networks", Theme.F(Theme.TMeta), Theme.Muted, lx + 52, 42, 248, 18, ContentAlignment.MiddleLeft);

            var hero = new Card { Bounds = new Rectangle(S(lx), S(80), S(lw), S(428)) };
            Controls.Add(hero);
            power = new PowerButton { Bounds = new Rectangle((S(lw) - S(176)) / 2, S(8), S(176), S(176)) };
            power.Click += delegate { Toggle(); };
            hero.Controls.Add(power);
            lblState = MakeLabel(hero, "", Theme.Semi(Theme.TState), Theme.Text, 0, 184, lw, 32, ContentAlignment.MiddleCenter);
            lblHint = MakeLabel(hero, "", Theme.F(Theme.TBody), Theme.Muted, 26, 220, lw - 52, 54, ContentAlignment.TopCenter);
            Divider(hero, 16, 282, lw - 32);
            stats = new StatStrip { Bounds = new Rectangle(S(0), S(283), S(lw), S(64)) };
            hero.Controls.Add(stats);
            Divider(hero, 16, 347, lw - 32);
            steamStatus = new SteamStatus { Bounds = new Rectangle(S(20), S(361), S(168), S(56)) };
            hero.Controls.Add(steamStatus);
            btnSteam = new FlatButton { Text = "Restart", Compact = true, Bounds = new Rectangle(S(196), S(372), S(84), S(32)) };
            btnSteam.Click += delegate { SteamAction(); };
            hero.Controls.Add(btnSteam);

            // ---- right column
            const int rx = 340, rw = 380;
            var check = new Card { Bounds = new Rectangle(S(rx), S(20), S(rw), S(220)) };
            Controls.Add(check);
            MakeLabel(check, "Connection check", Theme.Semi(Theme.TTitle), Theme.Text, 18, 12, 220, 22, ContentAlignment.MiddleLeft);
            lblCheckSub = MakeLabel(check, "Not run yet", Theme.F(Theme.TMeta), Theme.Faint, 18, 33, 220, 18, ContentAlignment.MiddleLeft);
            btnTest = new FlatButton { Text = "Run test", Compact = true, Bounds = new Rectangle(S(rw - 18 - 88), S(14), S(88), S(32)) };
            btnTest.Click += delegate { RunTest(); };
            check.Controls.Add(btnTest);
            rows = new CheckRow[Specs.Length];
            for (int i = 0; i < Specs.Length; i++)
            {
                rows[i] = new CheckRow { Title = Specs[i].Title, Subtitle = Specs[i].Subtitle, Bounds = new Rectangle(S(18), S(62 + i * 36), S(rw - 36), S(36)) };
                rows[i].Set(CheckRow.St.Idle, "");
                check.Controls.Add(rows[i]);
            }

            var settingsCard = new Card { Bounds = new Rectangle(S(rx), S(254), S(rw), S(112)) };
            Controls.Add(settingsCard);
            swAuto = AddSetting(settingsCard, rw, 0, "Turn on when the app starts", "Enable the bypass as soon as this app opens");
            Divider(settingsCard, 18, 56, rw - 36);
            swWin = AddSetting(settingsCard, rw, 56, "Start with Windows", "Open in the tray when you sign in");
            swAuto.CheckedChanged += delegate { if (!loading) { settings.AutoOn = swAuto.Checked; settings.Save(); } };
            swWin.CheckedChanged += delegate { if (!loading) SetRunAtLogon(swWin.Checked); };

            var act = new Card { Bounds = new Rectangle(S(rx), S(380), S(rw), S(128)) };
            Controls.Add(act);
            MakeLabel(act, "Activity", Theme.Semi(Theme.TTitle), Theme.Text, 18, 8, 100, 24, ContentAlignment.MiddleLeft);
            var clear = new TextAction { Text = "Clear", Bounds = new Rectangle(S(rw - 18 - 44), S(8), S(44), S(24)), BackColor = act.BackColor };
            clear.Click += delegate { log.Clear(); };
            var open = new TextAction { Text = "Open log file", Bounds = new Rectangle(S(rw - 18 - 44 - 8 - 84), S(8), S(84), S(24)), BackColor = act.BackColor };
            open.Click += delegate { OpenLogFile(); };
            act.Controls.Add(clear);
            act.Controls.Add(open);
            log = new RichTextBox
            {
                Bounds = new Rectangle(S(18), S(34), S(rw - 36), S(128 - 34 - 10)),
                BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted,
                ReadOnly = true, DetectUrls = false, TabStop = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Consolas", 9f), WordWrap = true, AccessibleName = "Activity log"
            };
            act.Controls.Add(log);
            SetWindowTheme(log.Handle, "DarkMode_Explorer", null);
        }

        // A settings row: label and one line of explanation on the left, switch on the right. Clicking
        // the text flips the switch too, so the target is the whole row.
        ToggleSwitch AddSetting(Card card, int cardWidth, int top, string title, string sub)
        {
            var sw = new ToggleSwitch { Bounds = new Rectangle(S(cardWidth - 18 - 44), S(top + 16), S(44), S(24)), AccessibleName = title };
            var t = MakeLabel(card, title, Theme.F(Theme.TBody), Theme.Text, 18, top + 9, cardWidth - 100, 22, ContentAlignment.MiddleLeft);
            var s = MakeLabel(card, sub, Theme.F(Theme.TMeta), Theme.Faint, 18, top + 30, cardWidth - 100, 18, ContentAlignment.MiddleLeft);
            t.Cursor = s.Cursor = Cursors.Hand;
            t.Click += delegate { sw.Checked = !sw.Checked; };
            s.Click += delegate { sw.Checked = !sw.Checked; };
            card.Controls.Add(sw);
            return sw;
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false, Font = Theme.F(Theme.TBody) };
            miToggle.Click += delegate { Toggle(); };
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add(miToggle);
            menu.Items.Add("Run test", null, delegate { RunTest(); });
            menu.Items.Add("Restart Steam", null, delegate { SteamAction(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.Text;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = !PreviewMode;
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
        string LogPath { get { return Path.Combine(dir, "steam-bypass.log"); } }

        // File only: per-host detail that would drown the activity list.
        void AppendFile(string s)
        {
            if (PreviewMode) return;
            try { lock (dir) File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  " + s + "\r\n"); } catch { }
        }

        void Append(string s)
        {
            if (IsDisposed) return;
            AppendFile(s);
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), s); } catch { } return; }
            if (log == null) return;
            Color c = Theme.Text;
            string low = s.ToLowerInvariant();
            if (low.StartsWith("error") || low.Contains("warning") || low.Contains("cannot") || low.Contains("couldn't")) c = Theme.Red;
            else if (s.StartsWith("Turned on") || s.Contains("restarted") || s.StartsWith("Test passed")) c = Theme.Green;
            else if (s.StartsWith("Turned off") || s == "Ready.") c = Theme.Muted;
            if (log.TextLength > 20000) log.Clear();
            log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = Theme.Faint;
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
            log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = c;
            log.AppendText(s + "\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        void OpenLogFile()
        {
            try { if (File.Exists(LogPath)) Process.Start(LogPath); else Append("There is no log file yet."); }
            catch (Exception ex) { Append("Error: could not open the log file: " + ex.Message); }
        }

        void UpdateStats()
        {
            // Counts since the app started; dimmed while the bypass is off, because nothing is moving.
            stats.Set(0, proxy.Active.ToString(), !isOn ? Theme.Faint : proxy.Active > 0 ? Theme.Green : Theme.Text);
            stats.Set(1, proxy.Total.ToString(), isOn ? Theme.Text : Theme.Faint);
            stats.Set(2, proxy.Failed.ToString(), proxy.Failed > 0 ? Theme.Red : (isOn ? Theme.Text : Theme.Faint));
        }

        void UpdateSteam()
        {
            var state = SteamState.NotRunning;
            if (steamOverride >= 0) state = (SteamState)steamOverride;
            else
            {
                Process[] procs = Process.GetProcessesByName("steam");
                if (procs.Length > 0)
                {
                    state = isOn ? SteamState.Starting : SteamState.RunningOff;
                    if (isOn)
                    {
                        var pids = new HashSet<int>();
                        DateTime oldest = DateTime.MaxValue;
                        foreach (var p in procs)
                        {
                            pids.Add(p.Id);
                            try { if (p.StartTime < oldest) oldest = p.StartTime; } catch { }
                        }
                        if (SteamLink.UsesProxy(pids, ProxyServer.Port)) state = SteamState.Using;
                        else if (DateTime.Now - oldest > TimeSpan.FromSeconds(45) && DateTime.UtcNow - onSince > TimeSpan.FromSeconds(45))
                            state = SteamState.NeedsRestart;
                    }
                }
                foreach (var p in procs) p.Dispose();
            }
            steamState = state;

            string text; Color color; string button = "Restart";
            switch (state)
            {
                case SteamState.Using: text = "Using the bypass"; color = Theme.Green; break;
                case SteamState.NeedsRestart: text = "Opened before the bypass. Restart it to connect."; color = Theme.Amber; break;
                case SteamState.Starting: text = "Starting..."; color = Theme.Muted; break;
                case SteamState.RunningOff: text = "Running"; color = Theme.Muted; break;
                default: text = "Not running"; color = Theme.Muted; button = "Open"; break;
            }
            steamStatus.Set(text, color);
            if (btnSteam.Text != button && !restarting) btnSteam.Text = button;
        }

        void ApplyUi()
        {
            power.State = startError != null ? PowerButton.Mode.Error : isOn ? PowerButton.Mode.On : PowerButton.Mode.Off;
            power.AccessibleName = isOn ? "Bypass is on. Press to turn off." : "Bypass is off. Press to turn on.";

            Color hintColor = Theme.Muted;
            if (startError != null)
            {
                lblState.Text = "Couldn't turn on";
                lblState.ForeColor = Theme.Red;
                lblHint.Text = startError;
            }
            else if (isOn)
            {
                lblState.Text = "Bypass is on";
                lblState.ForeColor = Theme.Green;
                if (proxy.DownloadsError != null) { lblHint.Text = proxy.DownloadsError; hintColor = Theme.Amber; }
                else lblHint.Text = "Steam traffic goes through this app. Keep it running while you use Steam.";
            }
            else
            {
                lblState.Text = "Bypass is off";
                lblState.ForeColor = Theme.Text;
                lblHint.Text = "Turn it on to reach Steam. Nothing on your PC changes while it is off.";
            }
            lblHint.ForeColor = hintColor;

            tray.Icon = isOn ? iconOn : iconOff;
            tray.Text = isOn ? "Steam Bypass: on" : "Steam Bypass: off";
            miToggle.Text = isOn ? "Turn off" : "Turn on";
            btnTest.Enabled = !testing;
            btnTest.Text = testing ? "Testing..." : "Run test";
            btnSteam.Enabled = !restarting;
            if (restarting) btnSteam.Text = "Working...";
            UpdateStats();
            UpdateSteam();
        }

        void Toggle()
        {
            if (isOn) TurnOff(); else TurnOn(true);
        }

        static string StartFailure(Exception ex)
        {
            var se = ex as SocketException;
            if (se != null && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return "Port " + ProxyServer.Port + " is in use. Close the other program, or another copy of this app, then try again.";
            return ex.Message;
        }

        void TurnOn(bool interactive)
        {
            try
            {
                proxy.Start();
                SystemProxy.Apply(ProxyServer.PacUrl);
                isOn = true;
                startError = null;
                onSince = DateTime.UtcNow;
                Append("Turned on. Windows now sends Steam domains through this app.");
            }
            catch (Exception ex)
            {
                proxy.Stop();
                isOn = false;
                startError = StartFailure(ex);
                Append("Error: couldn't turn on: " + ex.Message);
            }
            ApplyUi();
            if (!isOn)
            {
                Notify("Couldn't turn on", startError, ToastKind.Error, true);
                return;
            }

            // steam.exe reads the proxy settings only when it starts, so a Steam that was already open stays offline
            if (SteamRunning() && steamState != SteamState.Using)
            {
                if (interactive &&
                    MessageBox.Show(this, "Steam is already running. It only picks up the bypass when it starts, so if it was opened before this, it will stay offline.\n\nRestart Steam now? Downloads resume automatically.",
                        "Steam Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RestartSteam();
                else if (!interactive)
                    Notify("Steam is already open", "Restart it from the tray menu so it uses the bypass.", ToastKind.Warning, true);
            }
            else Notify("Bypass is on", "Steam traffic now goes through this app.", ToastKind.Success, false);
            if (interactive) AutoTest();
        }

        // Shows an on-screen notice. Skipped while this window has focus (it already shows the state)
        // unless the message must not be missed.
        void Notify(string title, string detail, ToastKind kind, bool always)
        {
            if (PreviewMode || IsDisposed || reallyExit) return;
            if (!always && ContainsFocus) return;
            Toast.Flash(title, detail, kind);
        }

        void TurnOff()
        {
            SystemProxy.Restore();
            proxy.Stop();
            isOn = false;
            startError = null;
            Append("Turned off. Windows proxy settings restored.");
            ApplyUi();
            Notify("Bypass is off", "Windows proxy settings are back to normal.", ToastKind.Info, false);
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

        // "Open" when Steam is closed, otherwise "Restart" after a confirmation (a running game would be closed).
        void SteamAction()
        {
            if (restarting) return;
            if (SteamRunning() &&
                MessageBox.Show(this, "Restart Steam?\n\nDownloads resume automatically. Close any game you are playing first.",
                    "Steam Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            RestartSteam();
        }

        async void RestartSteam()
        {
            if (restarting) return;
            var exe = FindSteamExe();
            if (exe == null) { Append("Error: could not find the Steam installation."); return; }
            restarting = true;
            ApplyUi();
            Append(SteamRunning() ? "Restarting Steam..." : "Opening Steam...");
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
            if (err == null) Notify("Steam restarted", "It will now use the bypass.", ToastKind.Success, false);
            else Notify("Couldn't restart Steam", err, ToastKind.Error, true);
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
                case WebExceptionStatus.ConnectFailure: return "Connection refused";
                case WebExceptionStatus.Timeout: return "Timed out";
                case WebExceptionStatus.NameResolutionFailure: return "DNS failed";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "Proxy unreachable";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.UnknownError: return "Blocked (reset)";
                default: return ex.Status.ToString();
            }
        }

        // Right after turning the bypass on: show that it works, without a second click.
        async void AutoTest()
        {
            await Task.Delay(700);
            if (isOn && !testing) RunTest();
        }

        async void RunTest()
        {
            if (testing) return;
            testing = true;
            ApplyUi();
            bool viaProxy = isOn;
            lblCheckSub.Text = viaProxy ? "Through the bypass..." : "Directly (the bypass is off)...";
            foreach (var r in rows) r.Set(CheckRow.St.Running, "");
            int ok = 0;
            var rowsOk = new bool[Specs.Length];
            var tasks = new List<Task>();
            for (int i = 0; i < Specs.Length; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(() => Probe(Specs[idx], viaProxy)).ContinueWith(t =>
                {
                    var res = t.Result;
                    rowsOk[idx] = res.Ok;
                    if (res.Ok) Interlocked.Increment(ref ok);
                    rows[idx].Set(res.Ok ? CheckRow.St.Ok : CheckRow.St.Fail, res.Detail);
                }, TaskScheduler.FromCurrentSynchronizationContext()));
            }
            await Task.WhenAll(tasks);
            testing = false;
            lblCheckSub.Text = (viaProxy ? "Through the bypass" : "Directly") + " · " + DateTime.Now.ToString("HH:mm:ss");
            Append((ok == Specs.Length ? "Test passed" : "Test finished") + ": " + ok + " of " + Specs.Length + " reachable (" + (viaProxy ? "through the bypass" : "directly") + ").");
            ApplyUi();
            if (ok == Specs.Length)
                Notify("Connection check passed", "All " + Specs.Length + " services are reachable" + (viaProxy ? " through the bypass." : "."), ToastKind.Success, false);
            else
            {
                var failed = new List<string>();
                for (int i = 0; i < Specs.Length; i++) if (!rowsOk[i]) failed.Add(Specs[i].Title);
                Notify("Connection check failed", "Not reachable: " + string.Join(", ", failed.ToArray()) + ".", ToastKind.Error, false);
            }
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
            reallyExit = true;
            if (isOn) TurnOff();
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!PreviewMode && !reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                Notify("Still running in the tray", isOn ? "The bypass stays on. Right-click the tray icon to quit." : "Right-click the tray icon to quit.", ToastKind.Info, true);
                return;
            }
            if (isOn && !PreviewMode) SystemProxy.Restore(); // Windows shutdown / forced close
            base.OnFormClosing(e);
        }
    }
}
