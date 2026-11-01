using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Dev-only: renders MainForm in several states and saves PNGs. No sockets, registry or tray.
// Run it through tools\preview.ps1. It reaches into MainForm's private fields by name (isOn, startError,
// testing, steamOverride, proxy, rows) and calls ApplyUi/Append by reflection, so a rename in MainForm.cs
// needs the same rename here. It is not part of the shipped exe.
namespace SteamBypass
{
    static class Harness
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static string outDir;

        static void Pump(int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(15); }
        }

        static void SetField(object o, string name, object v) { o.GetType().GetField(name, All).SetValue(o, v); }
        static object GetField(object o, string name) { return o.GetType().GetField(name, All).GetValue(o); }
        static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, All).Invoke(o, args); }

        static void Shot(Form f, string name)
        {
            Pump(700);
            RECT r; GetWindowRect(f.Handle, out r);
            using (var bmp = new Bitmap(r.R - r.L, r.B - r.T))
            {
                using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(f.Handle, dc, 2); g.ReleaseHdc(dc); }
                bmp.Save(Path.Combine(outDir, name + ".png"));
            }
        }

        static void Rows(Form f, params object[] spec)
        {
            var rows = (CheckRow[])GetField(f, "rows");
            for (int i = 0; i < rows.Length; i++)
            {
                var st = (CheckRow.St)spec[i * 2];
                rows[i].Set(st, (string)spec[i * 2 + 1]);
            }
        }

        [STAThread]
        static void Main(string[] a)
        {
            outDir = a[0];
            MainForm.PreviewMode = true;
            Application.EnableVisualStyles();
            var f = new MainForm(outDir, false, false);
            f.Show();
            var proxy = GetField(f, "proxy");
            Pump(300);

            // 1. fresh start, bypass off, Steam closed
            SetField(f, "steamOverride", 0);
            Call(f, "ApplyUi");
            Shot(f, "1-off");

            // 2. on, everything passes, Steam connected
            SetField(f, "isOn", true);
            proxy.GetType().GetField("Active").SetValue(proxy, 3);
            proxy.GetType().GetField("Total").SetValue(proxy, 55);
            SetField(f, "steamOverride", 2);
            Call(f, "Append", "Turned on. Windows now sends Steam domains through this app.");
            Call(f, "ApplyUi");
            Rows(f, CheckRow.St.Ok, "HTTP 200 · 587 ms", CheckRow.St.Ok, "HTTP 200 · 600 ms", CheckRow.St.Ok, "HTTP 301 · 317 ms", CheckRow.St.Ok, "HTTP 200 · 205 ms");
            Call(f, "Append", "Test passed: 4 of 4 reachable (through the bypass).");
            Shot(f, "2-on");

            // 3. on, Steam opened before the bypass, one failing check
            SetField(f, "steamOverride", 3);
            proxy.GetType().GetField("Failed").SetValue(proxy, 2);
            Rows(f, CheckRow.St.Ok, "HTTP 200 · 587 ms", CheckRow.St.Ok, "HTTP 200 · 600 ms", CheckRow.St.Fail, "Blocked (reset)", CheckRow.St.Ok, "HTTP 200 · 205 ms");
            Call(f, "Append", "Test finished: 3 of 4 reachable (through the bypass).");
            Call(f, "Append", "Error: cannot connect to cache8-hkg1.steamcontent.com:80");
            Call(f, "ApplyUi");
            Shot(f, "3-steam-restart-and-fail");

            // 4. on, but port 80 is taken (downloads cannot work)
            proxy.GetType().GetField("DownloadsError").SetValue(proxy, "Port 80 is in use by another program, so game downloads will not work.");
            SetField(f, "steamOverride", 1);
            Call(f, "ApplyUi");
            Shot(f, "4-downloads-warning");

            // 5. could not turn on
            proxy.GetType().GetField("DownloadsError").SetValue(proxy, null);
            SetField(f, "isOn", false);
            SetField(f, "startError", "Port 18081 is in use. Close the other program, or another copy of this app, then try again.");
            SetField(f, "steamOverride", 4);
            Rows(f, CheckRow.St.Idle, "", CheckRow.St.Idle, "", CheckRow.St.Idle, "", CheckRow.St.Idle, "");
            Call(f, "Append", "Error: couldn't turn on: Only one usage of each socket address is normally permitted");
            Call(f, "ApplyUi");
            Shot(f, "5-error");

            // 6. test running
            SetField(f, "startError", null);
            SetField(f, "isOn", true);
            SetField(f, "testing", true);
            SetField(f, "steamOverride", 2);
            Rows(f, CheckRow.St.Running, "", CheckRow.St.Running, "", CheckRow.St.Ok, "HTTP 301 · 317 ms", CheckRow.St.Running, "");
            Call(f, "ApplyUi");
            Shot(f, "6-testing");

            // 7. the on-screen notices
            Shot(Toast.Flash("Bypass is on", "Steam traffic now goes through this app.", ToastKind.Success), "t1-success");
            Shot(Toast.Flash("Bypass is off", "Windows proxy settings are back to normal.", ToastKind.Info), "t2-info");
            Shot(Toast.Flash("Steam is already open", "Restart it from the tray menu so it uses the bypass.", ToastKind.Warning), "t3-warning");
            Shot(Toast.Flash("Couldn't turn on", "Port 18081 is in use. Close the other program, or another copy of this app, then try again.", ToastKind.Error), "t4-error-long");
            Shot(Toast.Flash("Still running in the tray", "", ToastKind.Info), "t5-title-only");

            f.Close();
        }
    }
}
