# Build, run and test

All commands are PowerShell 5.1, run from the project root. The app needs no administrator rights, so nothing here
needs an elevated shell.

## Build

```powershell
.\app\build.bat          # writes .\SteamBypass.exe (about 85 KB); prints nothing on success
```

It compiles `SteamBypass.cs`, `MainForm.cs`, `Controls.cs` with `/target:winexe /codepage:65001` and the manifest.
Keep the build warning-free (the one warning about `PreviewMode` "never assigned" is expected: only the preview
tool assigns it).

- **CS0016 "being used by another process"**: the app is running. Stop it first:
  `Get-Process SteamBypass | Stop-Process -Force`, wait until it is gone, build, start it again. Killing leaves the
  PAC in the registry; the next start restores it, or run `.\SteamBypass.exe --off`.
- **The exe vanishes after a build**: Defender quarantined it (see below).
- Source files must be ASCII or UTF-8; non-ASCII characters in strings are written as `·`-style escapes.

Run: `Start-Process .\SteamBypass.exe` (window), `-ArgumentList '--tray'` (hidden), `'--on'` (turn on at start),
`'--on','--tray'` (what "Start with Windows" plus auto-on gives). `.\SteamBypass.exe --off` restores the Windows
proxy setting and exits (also use it after a crash or a kill).

## Look at every UI state without the real screen

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\preview.ps1
```

Compiles the app plus `tools\preview\Preview.cs`, renders the window to `dist\preview\`:
`1-off`, `2-on`, `3-steam-restart-and-fail`, `4-downloads-warning`, `5-error`, `6-testing`, and the notices
`t1-success`, `t2-info`, `t3-warning`, `t4-error-long`, `t5-title-only`. It sets state by reflection (private field
names in `Preview.cs`), uses `MainForm.PreviewMode` so no tray icon, registry, socket, timer or log file is
involved, and it never touches the running app. After a UI change: run it and look at all pictures (read the PNGs),
checking clipped text, overlaps, contrast, the empty/zero/error/warning/testing states, and long text in the notice.
Layout is checked at your display scaling only; to see 125% or 150% change the Windows scale and run it again.

## Check the running app

```powershell
. .\tools\ui-probe.ps1
$p = Get-Process SteamBypass
Get-OwnWindows $p.Id                                   # main window and any notice window
Save-WindowShot $p.MainWindowHandle "$env:TEMP\sb.png" # the window's own pixels
Send-WindowClick $p.MainWindowHandle 661 76            # "Run test": x, y in pixels of that screenshot
```

- Do **not** capture the screen and do **not** drive the real mouse (`SetCursorPos`, `mouse_event`): a real click
  once landed on a different window. `ui-probe.ps1` captures with `PrintWindow` and clicks with `PostMessage` to the
  control itself.
- A hidden window (hide-to-tray) has `MainWindowHandle` 0. Start a fresh copy without `--tray` to get a window.
- Notices are only shown when the window does **not** have focus (or for errors/warnings). To see one, close the
  window to the tray (`PostMessage(handle, WM_SYSCOMMAND 0x0112, SC_CLOSE 0xF060, 0)`), then poll
  `Get-OwnWindows` for a window about 420 px wide; it lives about 3 s (6 s for an error) and `Save-WindowShot` can
  capture it. A click on "Run test" while the window is focused gives no notice by design.
- State on disk: `settings.ini`, `steam-bypass.log` next to the exe; `%APPDATA%\SteamBypass-proxy-state.txt`;
  registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` (`AutoConfigURL`) and `...\Run`
  (`SteamBypass`). Check them with `Get-ItemProperty`.

## Check the network path

Use the commands in `blocking-evidence.md` "Re-measuring", steps 4 and 5. Quick checks with the bypass on:

```powershell
curl.exe -s -o NUL -m 15 -x http://127.0.0.1:18081 -w "%{http_code}`n" https://store.steampowered.com/
curl.exe -s -o NUL -m 20 --resolve cache8-hkg1.steamcontent.com:80:127.0.0.1 -w "%{http_code}`n" http://cache8-hkg1.steamcontent.com/
curl.exe -s -m 15 -x http://127.0.0.1:18081 "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=71&qoslevel=3" -o NUL -w "%{http_code}`n"
```

Several requests on one connection and several different cache hosts on one connection are the cases that broke
before: `curl -o NUL --resolve a:80:127.0.0.1 --resolve b:80:127.0.0.1 http://a/ http://b/ http://a/` must return 200
three times. Watch `(Get-Content .\steam-bypass.log -Tail 20)` for `error` lines while Steam downloads.

## Defender

A build was once removed as `Trojan:Win32/Bearfoos.A!ml` (machine-learning heuristic, the exe is unsigned and
opens sockets, edits proxy settings and can write a `Run` entry). On the author's machine the project folder is an
exclusion, added by the user. Check what happened with:

```powershell
Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 3 InitialDetectionTime, Resources
```

Do not add exclusions or alter code to avoid a detection; tell the user and let them decide. Note that Defender
also deletes the `Run` entry it considers part of the threat, which switches "Start with Windows" off.

## Regression checklist

1. `app\build.bat` is clean, `tools\preview.ps1` renders all states and they look right.
2. Turn on: PAC appears in the registry, the four-row check passes within a few seconds, the log shows "Turned on".
3. Turn off, then on again: `AutoConfigURL` is removed/restored each time; kill the process and run `--off`: restored.
4. Port conflicts: occupy 18081 (power button turns red with a plain sentence) and occupy 80 (app turns on, amber hint, "Game downloads" row fails).
5. Steam closed, then opened *after* the bypass: sign-in works, store loads, a download runs. Steam opened *before*: the Steam row says "Opened before the bypass" after about 45 s and Restart fixes it. (Not yet verified against a live Steam in v1.1.0.)
6. Errors counter stays 0 during a download; `ipv6check-http.steamserver.net` appears only as a quiet note in the log file.
7. Close the window: notice "Still running in the tray"; the bypass keeps working.
8. Things that cannot be scripted reliably, say so and ask the user: tray menu actions, autostart at sign-in, keyboard-only use, screen readers, other Windows versions and ISPs.
