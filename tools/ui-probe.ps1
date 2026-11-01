# Helpers to look at and operate the running Steam Bypass window WITHOUT the real screen or mouse.
#
#   . .\tools\ui-probe.ps1
#   $p = Get-Process SteamBypass
#   Get-OwnWindows $p.Id                       # the main window and any notice (Toast) window, with sizes
#   Save-WindowShot $p.MainWindowHandle "$env:TEMP\win.png"
#   Send-WindowClick $p.MainWindowHandle 661 76    # x, y in pixels of the screenshot (window-relative)
#
# Pictures come from PrintWindow (the window's own pixels, never the screen). Clicks are posted as
# WM_LBUTTONDOWN/UP straight to the control under that point, so no other window can be hit.
# A hidden window (hide-to-tray) has MainWindowHandle 0: show it from the tray first.
if (-not ('SbWin' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Runtime.InteropServices;
public static class SbWin {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  static SbWin() { SetProcessDPIAware(); }
  public static List<IntPtr> Windows(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static string Size(IntPtr h) { RECT r; GetWindowRect(h, out r); return (r.R - r.L) + "x" + (r.B - r.T); }
  public static bool Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r); int w = r.R - r.L, ht = r.B - r.T; if (w <= 0 || ht <= 0) return false;
    using (var bmp = new Bitmap(w, ht)) {
      using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      bmp.Save(path);
    }
    return true;
  }
  public static void Click(IntPtr h, int x, int y) {
    RECT r; GetWindowRect(h, out r);
    var screen = new POINT { X = r.L + x, Y = r.T + y };
    POINT cp = screen; ScreenToClient(h, ref cp); IntPtr cur = h;
    for (int i = 0; i < 4; i++) {
      IntPtr c = ChildWindowFromPointEx(cur, cp, 1 | 2 | 4); if (c == IntPtr.Zero || c == cur) break;
      POINT s2 = screen; ScreenToClient(c, ref s2); cur = c; cp = s2;
    }
    IntPtr lp = (IntPtr)((cp.Y << 16) | (cp.X & 0xFFFF));
    PostMessage(cur, 0x201, (IntPtr)1, lp); System.Threading.Thread.Sleep(60); PostMessage(cur, 0x202, IntPtr.Zero, lp);
  }
}
'@
}

function Get-OwnWindows([int]$ProcessId) {
    [SbWin]::Windows([uint32]$ProcessId) | ForEach-Object { [pscustomobject]@{ Handle = $_; Size = [SbWin]::Size($_) } }
}
function Save-WindowShot([IntPtr]$Handle, [string]$Path) { [SbWin]::Shot($Handle, $Path) }
function Send-WindowClick([IntPtr]$Handle, [int]$X, [int]$Y) { [SbWin]::Click($Handle, $X, $Y) }
