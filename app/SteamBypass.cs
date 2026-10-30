using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// Steam Bypass: local proxy that defeats SNI / Host based DPI blocking.
//   HTTPS : first TLS record is sent as two TCP segments (DPI cannot see the SNI).
//   HTTP  : "Host:" header is rewritten to "HoSt:" (DPI matches the exact spelling).
//   DNS   : Steam domains are resolved over DoH, because the ISP resolver is poisoned.
// Windows is pointed at the proxy with a PAC file that only lists Steam domains,
// so all other traffic stays direct. No driver, no administrator rights.

namespace SteamBypass
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            // .NET Framework defaults to protocols modern DoH servers reject; the default of 2 connections per host would also throttle DoH lookups
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;
            if (Array.IndexOf(args, "--off") >= 0)
            {
                SystemProxy.Restore();
                return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, "SteamBypassTrayApp", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.Run(new MainForm(dir, Array.IndexOf(args, "--tray") >= 0, Array.IndexOf(args, "--on") >= 0));
            }
            return 0;
        }
    }

    // ---------------------------------------------------------------- settings
    class Settings
    {
        readonly string path;
        public bool AutoOn;
        public Settings(string dir)
        {
            path = Path.Combine(dir, "settings.ini");
            try
            {
                foreach (var line in File.ReadAllLines(path))
                    if (line.Trim() == "AutoOn=1") AutoOn = true;
            }
            catch { }
        }
        public void Save()
        {
            try { File.WriteAllText(path, "AutoOn=" + (AutoOn ? "1" : "0") + "\r\n"); } catch { }
        }
    }

    // ------------------------------------------------------------ system proxy
    static class SystemProxy
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        static readonly string StateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamBypass-proxy-state.txt");

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);

        static void Notify()
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }

        public static void Apply(string pacUrl)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                var cur = k.GetValue("AutoConfigURL") as string;
                if (cur != pacUrl)
                    File.WriteAllText(StateFile, cur == null ? "0|" : "1|" + cur);
                k.SetValue("AutoConfigURL", pacUrl);
            }
            Notify();
        }

        public static void Restore()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    string cur = k.GetValue("AutoConfigURL") as string;
                    // only touch the value if it is still ours
                    if (cur != null && cur.StartsWith("http://127.0.0.1:" + ProxyServer.Port + "/"))
                    {
                        string prev = File.Exists(StateFile) ? File.ReadAllText(StateFile) : "0|";
                        if (prev.StartsWith("1|")) k.SetValue("AutoConfigURL", prev.Substring(2));
                        else k.DeleteValue("AutoConfigURL", false);
                    }
                }
                if (File.Exists(StateFile)) File.Delete(StateFile);
            }
            catch { }
            Notify();
        }
    }

    // ------------------------------------------------------------- domain list
    class DomainSet
    {
        readonly List<string> domains = new List<string>();
        public DomainSet(string dir)
        {
            try
            {
                foreach (var l in File.ReadAllLines(Path.Combine(dir, "steam-domains.txt")))
                {
                    var d = l.Trim().ToLowerInvariant();
                    if (d.Length > 0 && !d.StartsWith("#")) domains.Add(d);
                }
            }
            catch { }
            if (domains.Count == 0)
                domains.AddRange(new[] { "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com",
                    "steamusercontent.com", "steamserver.net", "steamgames.com", "steam-chat.com" });
        }
        public bool Contains(string host)
        {
            host = host.TrimEnd('.').ToLowerInvariant();
            foreach (var d in domains)
                if (host == d || host.EndsWith("." + d)) return true;
            return false;
        }
        public string BuildPac(int port)
        {
            var sb = new StringBuilder();
            sb.Append("function FindProxyForURL(url, host) {\n  host = host.toLowerCase();\n  var d = [");
            for (int i = 0; i < domains.Count; i++) { if (i > 0) sb.Append(","); sb.Append("'" + domains[i] + "'"); }
            sb.Append("];\n  for (var i = 0; i < d.length; i++) {\n");
            sb.Append("    if (host === d[i] || (host.length > d[i].length && host.substring(host.length - d[i].length - 1) === '.' + d[i]))\n");
            sb.Append("      return 'PROXY 127.0.0.1:" + port + "; DIRECT';\n  }\n  return 'DIRECT';\n}\n");
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- resolver
    class Resolver
    {
        class Entry { public IPAddress[] Ips; public DateTime Expires; }
        readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // IP-literal URLs first: they need no DNS at all.
        static readonly string[] DohUrls = {
            "https://1.1.1.1/dns-query?name={0}&type=A",
            "https://8.8.8.8/resolve?name={0}&type=A",
            "https://cloudflare-dns.com/dns-query?name={0}&type=A",
            "https://dns.google/resolve?name={0}&type=A" };

        public async Task<IPAddress[]> Resolve(string host, bool useDoh)
        {
            IPAddress lit;
            if (IPAddress.TryParse(host, out lit)) return new[] { lit };
            lock (cache)
            {
                Entry e;
                if (cache.TryGetValue(host, out e) && e.Expires > DateTime.UtcNow) return e.Ips;
            }
            Entry res = null;
            if (useDoh) res = await Task.Run(() => QueryDoh(host)).ConfigureAwait(false);
            if (res == null)
            {
                var sys = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                var good = new List<IPAddress>();
                foreach (var a in sys)
                    if (!IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)) good.Add(a);
                if (good.Count == 0) throw new Exception("DNS failed for " + host);
                res = new Entry { Ips = good.ToArray(), Expires = DateTime.UtcNow.AddSeconds(60) };
            }
            lock (cache) cache[host] = res;
            return res.Ips;
        }

        static Entry QueryDoh(string host)
        {
            foreach (var fmt in DohUrls)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(string.Format(fmt, Uri.EscapeDataString(host)));
                    req.Proxy = null; // never loop back into our own PAC
                    req.Accept = "application/dns-json";
                    req.Timeout = 5000;
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream()))
                        json = sr.ReadToEnd();
                    var ips = new List<IPAddress>();
                    int ttl = 300;
                    foreach (Match m in Regex.Matches(json, "\\{[^{}]*\"type\"\\s*:\\s*1\\b[^{}]*\\}"))
                    {
                        var d = Regex.Match(m.Value, "\"data\"\\s*:\\s*\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\"");
                        if (!d.Success) continue;
                        ips.Add(IPAddress.Parse(d.Groups[1].Value));
                        var t = Regex.Match(m.Value, "\"TTL\"\\s*:\\s*(\\d+)");
                        if (t.Success) ttl = Math.Min(ttl, int.Parse(t.Groups[1].Value));
                    }
                    if (ips.Count > 0)
                        return new Entry { Ips = ips.ToArray(), Expires = DateTime.UtcNow.AddSeconds(Math.Max(30, Math.Min(ttl, 300))) };
                }
                catch { }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------- proxy
    class ProxyServer
    {
        public const int Port = 18081;
        public int Active, Total, Failed;
        public Action<string> Log = delegate { };

        readonly DomainSet steam;
        readonly Resolver resolver = new Resolver();
        readonly HashSet<string> seen = new HashSet<string>();
        TcpListener listener;
        TcpListener listenerHttp;
        volatile bool running;

        public static string PacUrl { get { return "http://127.0.0.1:" + Port + "/proxy.pac"; } }

        public ProxyServer(DomainSet steam) { this.steam = steam; }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            running = true;
            Task.Run(new Func<Task>(AcceptLoop));
            try
            {
                listenerHttp = new TcpListener(IPAddress.Loopback, 80);
                listenerHttp.Start();
                Task.Run(new Func<Task>(AcceptLoopHttp));
            }
            catch (Exception ex)
            {
                listenerHttp = null;
                Log("WARNING: cannot listen on 127.0.0.1:80 (" + ex.Message + "). Game downloads will not work.");
            }
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            try { if (listenerHttp != null) listenerHttp.Stop(); } catch { }
        }

        async Task AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }
                var ignored = Task.Run(() => Handle(c));
            }
        }

        void Note(string host, string what)
        {
            lock (seen) { if (!seen.Add(host + what)) return; }
            Log(host + "  " + what);
        }

        async Task Handle(TcpClient client)
        {
            Interlocked.Increment(ref Active);
            Interlocked.Increment(ref Total);
            TcpClient upstream = null;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();
                var h = await ReadHead(cs, null).ConfigureAwait(false);
                if (h == null) return;
                byte[] leftover = h.Leftover;
                var lines = h.Text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var first = lines[0].Split(' ');
                if (first.Length < 3) return;
                string method = first[0], target = first[1];

                if (method == "CONNECT")
                {
                    int colon = target.LastIndexOf(':');
                    string host = colon > 0 ? target.Substring(0, colon) : target;
                    int port = colon > 0 ? int.Parse(target.Substring(colon + 1)) : 443;
                    upstream = await Connect(host, port).ConfigureAwait(false);
                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    await cs.WriteAsync(ok, 0, ok.Length).ConfigureAwait(false);
                    var us = upstream.GetStream();

                    var buf = new byte[16384];
                    int n = leftover.Length > 0 ? leftover.Length : await cs.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (leftover.Length > 0) Array.Copy(leftover, buf, n);
                    if (n <= 0) return;
                    if (buf[0] == 0x16 && n > 2)
                    {
                        // TLS handshake: send it as two segments so DPI never sees a full ClientHello
                        await us.WriteAsync(buf, 0, 2).ConfigureAwait(false);
                        await us.FlushAsync().ConfigureAwait(false);
                        await Task.Delay(20).ConfigureAwait(false);
                        await us.WriteAsync(buf, 2, n - 2).ConfigureAwait(false);
                        Note(host, "TLS split");
                    }
                    else
                    {
                        await us.WriteAsync(buf, 0, n).ConfigureAwait(false);
                        Note(host, "tunnel");
                    }
                    await Task.WhenAll(Pump(cs, us, client, upstream), Pump(us, cs, upstream, client)).ConfigureAwait(false);
                }
                else if (target.StartsWith("/"))
                {
                    await ServeLocal(cs, target).ConfigureAwait(false);
                }
                else
                {
                    var uri = new Uri(target);
                    upstream = await Connect(uri.Host, uri.Port).ConfigureAwait(false);
                    var us = upstream.GetStream();
                    var sb = new StringBuilder();
                    sb.Append(method + " " + uri.PathAndQuery + " " + first[2] + "\r\n");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var l = lines[i];
                        if (l.Length == 0) continue;
                        string low = l.ToLowerInvariant();
                        if (low.StartsWith("proxy-connection:") || low.StartsWith("connection:") || low.StartsWith("keep-alive:")) continue;
                        if (low.StartsWith("host:")) l = "HoSt:" + l.Substring(5); // DPI is case-sensitive here
                        sb.Append(l + "\r\n");
                    }
                    sb.Append("Connection: close\r\n\r\n");
                    var req = Encoding.ASCII.GetBytes(sb.ToString());
                    await us.WriteAsync(req, 0, req.Length).ConfigureAwait(false);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, 0, leftover.Length).ConfigureAwait(false);
                    Note(uri.Host, "HTTP host-rewrite");
                    await Task.WhenAll(Pump(cs, us, client, upstream), Pump(us, cs, upstream, client)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Failed);
                Log("error: " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                if (upstream != null) try { upstream.Close(); } catch { }
                Interlocked.Decrement(ref Active);
            }
        }

        async Task<TcpClient> Connect(string host, int port, bool forceDoh = false)
        {
            var ips = await resolver.Resolve(host, forceDoh || steam.Contains(host)).ConfigureAwait(false);
            Exception last = null;
            int tried = 0;
            foreach (var ip in ips)
            {
                if (++tried > 4) break;
                var c = new TcpClient(ip.AddressFamily);
                bool ok = false;
                try
                {
                    var t = c.ConnectAsync(ip, port);
                    var done = await Task.WhenAny(t, Task.Delay(5000)).ConfigureAwait(false);
                    if (done == t) { await t.ConfigureAwait(false); ok = true; }
                }
                catch (Exception e) { last = e; }
                if (ok) { c.NoDelay = true; return c; }
                c.Close();
            }
            throw new Exception("cannot connect to " + host + ":" + port + (last != null ? " (" + last.Message + ")" : ""));
        }

        static async Task Pump(NetworkStream from, NetworkStream to, TcpClient fromC, TcpClient toC)
        {
            var buf = new byte[32768];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                    await to.WriteAsync(buf, 0, n).ConfigureAwait(false);
                try { toC.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            catch
            {
                try { fromC.Close(); } catch { }
                try { toC.Close(); } catch { }
            }
        }

        class Head { public string Text; public byte[] Leftover; }

        // Reads one HTTP head (up to the blank line). `initial` holds bytes already read from the stream.
        static async Task<Head> ReadHead(NetworkStream s, byte[] initial)
        {
            var ms = new MemoryStream();
            var buf = new byte[4096];
            if (initial != null && initial.Length > 0) ms.Write(initial, 0, initial.Length);
            while (ms.Length < 65536)
            {
                var arr = ms.GetBuffer();
                int len = (int)ms.Length;
                for (int i = 3; i < len; i++)
                {
                    if (arr[i] == '\n' && arr[i - 1] == '\r' && arr[i - 2] == '\n' && arr[i - 3] == '\r')
                    {
                        var left = new byte[len - (i + 1)];
                        Array.Copy(arr, i + 1, left, 0, left.Length);
                        return new Head { Text = Encoding.ASCII.GetString(arr, 0, i + 1), Leftover = left };
                    }
                }
                int n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (n <= 0) return null;
                ms.Write(buf, 0, n);
            }
            return null;
        }

        // ---- plain HTTP that arrives on 127.0.0.1:80 --------------------------------------
        // The ISP resolver answers 127.0.0.1 for Steam hosts, so Steam's own downloader (which ignores
        // the system proxy) connects here. We resolve the real address over DoH and forward the
        // requests with the Host header respelled so the DPI box does not recognise it.
        async Task HandleDirectHttp(TcpClient client)
        {
            Interlocked.Increment(ref Active);
            Interlocked.Increment(ref Total);
            TcpClient up = null;
            Task respPump = null;
            var gen = new int[1];
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();
                NetworkStream us = null;
                string upHost = null;
                byte[] pending = null;
                while (true)
                {
                    var h = await ReadHead(cs, pending).ConfigureAwait(false);
                    if (h == null) break;
                    pending = h.Leftover;
                    var lines = h.Text.Split(new[] { "\r\n" }, StringSplitOptions.None);

                    string host = null;
                    long contentLength = 0;
                    bool chunked = false;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string low = lines[i].ToLowerInvariant();
                        if (low.StartsWith("host:")) host = lines[i].Substring(5).Trim();
                        else if (low.StartsWith("content-length:")) long.TryParse(lines[i].Substring(15).Trim(), out contentLength);
                        else if (low.StartsWith("transfer-encoding:") && low.Contains("chunked")) chunked = true;
                    }
                    if (string.IsNullOrEmpty(host)) throw new Exception("request without Host header");
                    int colon = host.LastIndexOf(':');
                    if (colon > 0 && host.IndexOf(']') < colon) host = host.Substring(0, colon);

                    if (up == null || !string.Equals(host, upHost, StringComparison.OrdinalIgnoreCase))
                    {
                        // Steam reuses one loopback connection for many cacheN hosts: switch upstream per request.
                        // It only sends the next request after the previous response is complete, so the old one can go.
                        gen[0]++;
                        if (up != null) try { up.Close(); } catch { }
                        upHost = host;
                        up = await Connect(host, 80, true).ConfigureAwait(false);
                        us = up.GetStream();
                        respPump = DirectResponsePump(up, client, gen, gen[0]);
                        Note(host, "HTTP via loopback (real IP over DoH)");
                    }

                    var sb = new StringBuilder();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string l = lines[i];
                        if (i > 0 && l.ToLowerInvariant().StartsWith("host:")) l = "HoSt:" + l.Substring(5);
                        if (i == lines.Length - 1 && l.Length == 0) continue; // trailing blank pieces are re-added below
                        if (l.Length == 0) continue;
                        sb.Append(l + "\r\n");
                    }
                    sb.Append("\r\n");
                    var head = Encoding.ASCII.GetBytes(sb.ToString());
                    await us.WriteAsync(head, 0, head.Length).ConfigureAwait(false);

                    if (chunked)
                    {
                        // cannot frame a chunked request body here: hand the rest of the stream over untouched
                        if (pending.Length > 0) await us.WriteAsync(pending, 0, pending.Length).ConfigureAwait(false);
                        await Pump(cs, us, client, up).ConfigureAwait(false);
                        break;
                    }
                    long remaining = contentLength;
                    if (remaining > 0 && pending.Length > 0)
                    {
                        int take = (int)Math.Min(remaining, pending.Length);
                        await us.WriteAsync(pending, 0, take).ConfigureAwait(false);
                        remaining -= take;
                        var rest = new byte[pending.Length - take];
                        Array.Copy(pending, take, rest, 0, rest.Length);
                        pending = rest;
                    }
                    var bb = new byte[16384];
                    while (remaining > 0)
                    {
                        int n = await cs.ReadAsync(bb, 0, (int)Math.Min(bb.Length, remaining)).ConfigureAwait(false);
                        if (n <= 0) break;
                        await us.WriteAsync(bb, 0, n).ConfigureAwait(false);
                        remaining -= n;
                    }
                }
                if (up != null) try { up.Client.Shutdown(SocketShutdown.Send); } catch { }
                if (respPump != null) await Task.WhenAny(respPump, Task.Delay(30000)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Failed);
                Log("error (loopback:80): " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                if (up != null) try { up.Close(); } catch { }
                Interlocked.Decrement(ref Active);
            }
        }

        // Copies upstream -> client for the current upstream only; a superseded upstream ends silently.
        static async Task DirectResponsePump(TcpClient upC, TcpClient client, int[] gen, int myGen)
        {
            var buf = new byte[65536];
            try
            {
                var from = upC.GetStream();
                var to = client.GetStream();
                int n;
                while ((n = await from.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    if (gen[0] != myGen) return;
                    await to.WriteAsync(buf, 0, n).ConfigureAwait(false);
                }
                if (gen[0] == myGen) try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            catch
            {
                if (gen[0] == myGen) try { client.Close(); } catch { }
            }
        }

        async Task AcceptLoopHttp()
        {
            while (running)
            {
                TcpClient c;
                try { c = await listenerHttp.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }
                var ignored = Task.Run(() => HandleDirectHttp(c));
            }
        }

        async Task ServeLocal(NetworkStream cs, string path)
        {
            string body, type = "text/plain";
            string status = "200 OK";
            if (path.StartsWith("/proxy.pac")) { body = steam.BuildPac(Port); type = "application/x-ns-proxy-autoconfig"; }
            else if (path == "/") body = "Steam Bypass proxy is running.";
            else { body = "not found"; status = "404 Not Found"; }
            var b = Encoding.UTF8.GetBytes(body);
            var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
            await cs.WriteAsync(b, 0, b.Length).ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------------- UI
    class MainForm : Form
    {
        readonly string dir;
        readonly Settings settings;
        readonly DomainSet domains;
        readonly ProxyServer proxy;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon iconOn, iconOff, iconBusy;
        readonly Label lblState = new Label();
        readonly Label lblStats = new Label();
        readonly Button btnToggle = new Button();
        readonly Button btnTest = new Button();
        readonly CheckBox chkAuto = new CheckBox();
        readonly CheckBox chkWin = new CheckBox();
        readonly TextBox log = new TextBox();
        readonly ToolStripMenuItem miToggle = new ToolStripMenuItem();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        bool isOn, busy, reallyExit, startHidden, loading = true;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public MainForm(string dir, bool hidden, bool forceOn)
        {
            this.dir = dir;
            startHidden = hidden;
            settings = new Settings(dir);
            domains = new DomainSet(dir);
            proxy = new ProxyServer(domains);
            proxy.Log = Append;

            iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
            iconOff = MakeIcon(Color.FromArgb(130, 130, 130));
            iconBusy = MakeIcon(Color.FromArgb(219, 160, 20));

            Text = "Steam Bypass";
            Icon = iconOff;
            ClientSize = new Size(420, 360);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            lblState.SetBounds(16, 12, 388, 32);
            lblState.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            lblStats.SetBounds(16, 44, 388, 18);
            lblStats.ForeColor = Color.Gray;

            btnToggle.SetBounds(16, 70, 200, 40);
            btnToggle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            btnToggle.Click += delegate { Toggle(); };

            btnTest.SetBounds(226, 70, 178, 40);
            btnTest.Text = "Test connection";
            btnTest.Click += delegate { TestConnection(); };

            chkAuto.SetBounds(16, 118, 190, 22);
            chkAuto.Text = "Turn on when app starts";
            chkAuto.Checked = settings.AutoOn;
            chkAuto.CheckedChanged += delegate { if (!loading) { settings.AutoOn = chkAuto.Checked; settings.Save(); } };

            chkWin.SetBounds(216, 118, 190, 22);
            chkWin.Text = "Start with Windows";
            chkWin.Checked = GetRunAtLogon();
            chkWin.CheckedChanged += delegate { if (!loading) SetRunAtLogon(chkWin.Checked); };

            log.SetBounds(16, 148, 388, 196);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Font = new Font("Consolas", 9f);

            Controls.AddRange(new Control[] { lblState, lblStats, btnToggle, btnTest, chkAuto, chkWin, log });

            miToggle.Click += delegate { Toggle(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add(miToggle);
            menu.Items.Add("Test connection", null, delegate { TestConnection(); });
            menu.Items.Add("Restart Steam", null, delegate { RestartSteam(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = true;

            poll.Interval = 1000;
            poll.Tick += delegate { UpdateStats(); };
            poll.Start();

            loading = false;
            var h = Handle; // create the handle now so background threads can BeginInvoke while hidden
            SystemProxy.Restore(); // clean up after a previous crash
            ApplyUi();
            if (settings.AutoOn || forceOn) TurnOn(false);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(startHidden ? false : value);
        }

        static Icon MakeIcon(Color c)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("S", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void Append(string s)
        {
            if (IsDisposed) return;
            try { lock (dir) File.AppendAllText(Path.Combine(dir, "steam-bypass.log"), DateTime.Now.ToString("s") + "  " + s + "\r\n"); } catch { }
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), s); } catch { } return; }
            if (log.TextLength > 20000) log.Clear();
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }

        void UpdateStats()
        {
            lblStats.Text = isOn
                ? string.Format("Active {0}  |  Total {1}  |  Errors {2}", proxy.Active, proxy.Total, proxy.Failed)
                : "Proxy stopped";
        }

        void ApplyUi()
        {
            lblState.Text = isOn ? "Bypass: ON" : "Bypass: OFF";
            lblState.ForeColor = isOn ? Color.FromArgb(46, 160, 67) : Color.Gray;
            tray.Icon = isOn ? iconOn : iconOff;
            tray.Text = isOn ? "Steam Bypass: ON" : "Steam Bypass: OFF";
            btnToggle.Text = isOn ? "Turn OFF" : "Turn ON";
            miToggle.Text = isOn ? "Turn OFF" : "Turn ON";
            btnTest.Enabled = !busy;
            UpdateStats();
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
                Append("Steam is already running. If it shows NO CONNECTION, restart it (tray menu > Restart Steam).");
                if (interactive &&
                    MessageBox.Show(this, "Steam is already running. It only picks up the bypass when it starts, so if it was opened before this, it will stay offline.\n\nRestart Steam now? (Downloads resume automatically.)",
                        "Steam Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RestartSteam();
                else if (!interactive)
                    tray.ShowBalloonTip(5000, "Steam Bypass", "Steam was already open. Use 'Restart Steam' in the tray menu so it uses the bypass.", ToolTipIcon.Warning);
            }
            else Append("Reload the browser tab if it was already open.");
        }

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
                    var dir = k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "steam.exe"))) return Path.Combine(dir, "steam.exe");
                }
            }
            catch { }
            return null;
        }

        async void RestartSteam()
        {
            var exe = FindSteamExe();
            if (exe == null) { Append("Could not find the Steam installation."); return; }
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
            Append(err == null ? "Steam restarted." : "Could not restart Steam: " + err);
        }

        void TurnOff()
        {
            SystemProxy.Restore();
            proxy.Stop();
            isOn = false;
            Append("OFF - proxy stopped, Windows proxy settings restored.");
            ApplyUi();
        }

        void Toggle()
        {
            if (isOn) TurnOff(); else TurnOn(true);
        }

        async void TestConnection()
        {
            if (busy) return;
            busy = true;
            ApplyUi();
            Append(isOn ? "Testing through the proxy ..." : "Testing DIRECT (bypass is off) ...");
            bool viaProxy = isOn;
            var result = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                foreach (var u in new[] {
                    "https://store.steampowered.com/", "https://steamcommunity.com/",
                    "https://store.cloudflare.steamstatic.com/", "http://steamcdn-a.akamaihd.net/" })
                {
                    string r;
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(u);
                        req.Proxy = viaProxy ? new WebProxy("127.0.0.1", ProxyServer.Port) : null;
                        req.Timeout = 12000;
                        req.AllowAutoRedirect = false;
                        req.UserAgent = "Mozilla/5.0";
                        using (var resp = (HttpWebResponse)req.GetResponse()) r = "OK  HTTP " + (int)resp.StatusCode;
                    }
                    catch (WebException ex)
                    {
                        var hr = ex.Response as HttpWebResponse;
                        r = hr != null ? "OK  HTTP " + (int)hr.StatusCode : "BLOCKED  (" + ex.Message + ")";
                    }
                    catch (Exception ex) { r = "ERROR  " + ex.Message; }
                    sb.AppendLine("  " + u + "  ->  " + r);
                }
                return sb.ToString().TrimEnd();
            });
            Append(result);
            busy = false;
            ApplyUi();
        }

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
            catch (Exception ex) { Append("Could not change autostart: " + ex.Message); }
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
