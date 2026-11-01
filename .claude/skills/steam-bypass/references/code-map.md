# Code map (method level)

Read this to find out which method does what, who calls it and what it assumes, without opening the source.
`SKILL.md` has the architecture and the recipes. Line numbers are left out on purpose; if a name is not found, grep
for it, the file has moved on. Namespace `SteamBypass`, everything `internal` (no modifier), C# 5.

## Threads and who may touch what

| Thread | What runs there | Rule |
| --- | --- | --- |
| UI thread | all of `MainForm`, every control, `Toast`, dialogs | Never block it: the proxy does its work on the thread pool |
| thread pool (proxy) | `ProxyServer.AcceptLoop`, `AcceptLoopHttp`, `Handle`, `HandleDirectHttp`, `Pump`, `Resolver` | No control access. Report through `Log` / `Verbose`, which `MainForm` points at `Append` / `AppendFile` (`Append` re-dispatches with `BeginInvoke`) |
| `Task.Run` in `MainForm` | `Probe` (connection check), `RestartSteam`'s shutdown wait | Results return to the UI thread through `await` / `TaskScheduler.FromCurrentSynchronizationContext()` |

Counters `Active`, `Total`, `Failed` are plain `int` fields changed with `Interlocked` and read by the UI timer
without a lock (a stale value is harmless). `Resolver`'s cache is guarded by `lock (cache)`. `ProxyServer.seen`
(the de-duplication set for `Note`) is guarded by `lock (seen)`.

## SteamBypass.cs

| Name | What it does and what to know |
| --- | --- |
| `Program.Main` | Sets `SecurityProtocol = Tls12` and `DefaultConnectionLimit = 64` (both required, see SKILL.md gotchas), handles `--off` (restore and exit, before the mutex), takes the single-instance mutex `SteamBypassTrayApp`, runs `new MainForm(dir, --tray, --on)`. `dir` is the exe's folder; `steam-domains.txt`, `settings.ini` and `steam-bypass.log` live there |
| `Settings` | Reads `settings.ini` (`AutoOn=1` is the only key), `Save()` rewrites it. Errors are swallowed |
| `SystemProxy.Apply(pacUrl)` | Opens `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`, remembers the previous `AutoConfigURL` in `%APPDATA%\SteamBypass-proxy-state.txt` (`0|` none, `1|<url>`), writes ours, calls `InternetSetOption` 39 (settings changed) and 37 (refresh) |
| `SystemProxy.Restore()` | Only acts if the current value starts with `http://127.0.0.1:<Port>/`; puts the previous value back or deletes the value; deletes the state file; notifies WinINet. Safe to call any number of times |
| `DomainSet` | Loads `steam-domains.txt` (trim, lower-case, skip blanks and `#`); falls back to a built-in list when the file is missing or empty. `Contains(host)` is a suffix match (`host == d` or ends with `.d`). `BuildPac(port)` returns the PAC text |
| `DnsMissException(host)` | "No IPv4 address". `Expected` is true for names starting with `ipv6check` |
| `Resolver.Resolve(host, useDoh)` | IP literal returns itself; cache hit returns; otherwise DoH (when asked) then system DNS with loopback/any filtered out; throws `DnsMissException` when nothing usable. Cache TTL is the record TTL clamped to 30-300 s (system answers: 60 s) |
| `Resolver.QueryDoh(host)` | Tries four URLs in order: `https://1.1.1.1/dns-query`, `https://8.8.8.8/resolve`, `https://cloudflare-dns.com/dns-query`, `https://dns.google/resolve` (IP-literal URLs first so no DNS is needed to bootstrap). `Accept: application/dns-json`, `Proxy = null`, 5 s timeout. The JSON is read with regexes (type 1 records, `data`, `TTL`) |
| `ProxyServer` | See below |

### ProxyServer

Public surface: `const Port = 18081`, `Active`, `Total`, `Failed`, `Log`, `Verbose`, `DownloadsError`, `PacUrl`
(static), `Start()`, `Stop()`.

| Member | Behaviour |
| --- | --- |
| `Start()` | Listens on `127.0.0.1:18081` (throws if taken) and starts `AcceptLoop`. Then tries `127.0.0.1:80`; on failure sets `DownloadsError` and logs a warning, on success clears it and starts `AcceptLoopHttp`. `running` becomes true |
| `Stop()` | `running = false`, stops both listeners |
| `Note(host, what)` | Logs one `Verbose` line per distinct host + kind (file log only) |
| `Handle(client)` | One connection on 18081. Reads the head with `ReadHead`, then by method: **CONNECT** → `Connect(host, port)`, replies `200 Connection Established`, reads the first chunk from the client; if it starts with `0x16` (TLS) and is longer than 2 bytes it writes bytes 0-1, flushes, waits 20 ms, writes the rest (`Note: TLS split`), otherwise writes it unchanged (`tunnel`); then two `Pump`s. **Path starting with `/`** → `ServeLocal` (the PAC). **Absolute URI** → `Connect(uri.Host, uri.Port)`, builds `METHOD path HTTP/x.y`, drops `Proxy-Connection`, `Connection`, `Keep-Alive`, rewrites `Host:` to `HoSt:`, appends `Connection: close`, forwards leftover body bytes, pumps. `DnsMissException` is a note when expected, otherwise an error; any other exception increments `Failed` |
| `Connect(host, port, forceDoh)` | Resolves (DoH when `forceDoh` or the host is in the domain list), then tries up to 4 addresses with a 5 s timeout each; `NoDelay` on success |
| `Pump(from, to, fromC, toC)` | Copies until EOF, then half-closes the other side (`Shutdown(Send)`); on an exception closes both clients |
| `ReadHead(stream, initial)` | Reads until the blank line (max 64 KB), returns `Head { Text, Leftover }`; `initial` carries bytes already read (used for keep-alive loops); `null` on EOF or oversize |
| `HandleDirectHttp(client)` | One connection on `127.0.0.1:80` (Steam's downloader). Loop: `ReadHead` → find `Host` (port stripped), `Content-Length`, `chunked` → if there is no upstream or the host differs from the current one: `gen[0]++`, close the old upstream, `Connect(host, 80, true)`, start `DirectResponsePump` → write the head with `HoSt:` and every other header unchanged (keep-alive stays) → copy `Content-Length` body bytes from `pending` then the stream; a chunked request body switches to a raw `Pump` and ends the loop. On client EOF it half-closes the upstream and waits up to 30 s for the response pump |
| `DirectResponsePump(upC, client, gen, myGen)` | Copies upstream → client. A pump whose `myGen` no longer equals `gen[0]` ends silently (the upstream was replaced); only the current one closes the client on error |
| `ServeLocal(stream, path)` | `/proxy.pac*` → PAC (`application/x-ns-proxy-autoconfig`), `/` → a one-line text, anything else 404 |

## MainForm.cs

Fields worth knowing: `isOn`, `startError` (text of the last failed turn-on, `null` when fine), `onSince`,
`testing`, `restarting`, `reallyExit` (set first in `ExitApp`), `startHidden`, `loading` (true until settings are
loaded into the switches, so change handlers do not save), `steamState` / `steamOverride` (preview only),
`internal static PreviewMode`.

| Method | Role |
| --- | --- |
| constructor `(dir, hidden, forceOn)` | Computes `Theme.Scale`, builds `Settings`, `DomainSet`, `ProxyServer` (wires `Log`/`Verbose`), icons, window, `BuildUi`, `BuildTray`, the 1 s `poll` timer (not in preview), loads switch states, creates the handle, restores a stale PAC (`SystemProxy.Restore()`, not in preview), `ApplyUi()`, "Ready.", and turns on when `AutoOn` or `--on` |
| `BuildUi()` | All layout, in logical px: logo/title at (20,20); hero card (20,80,300,428); check card (340,20,380,220); settings card (340,254,380,112); activity card (340,380,380,128). `AddSetting`, `MakeLabel`, `Divider` are the helpers |
| `BuildTray()` | Dark tray menu: Open, Turn on/off, Run test, Restart Steam, Exit. Left click opens the window. Hidden in preview |
| `OnHandleCreated` | DWM attributes 20 (dark title bar), 35 (caption colour), 36 (caption text) |
| `AppendFile(s)` / `Append(s)` | File log (`steam-bypass.log`, ISO time) / file log + Activity list. `Append` colours the line by its start: contains "error", "warning", "cannot", "couldn't" → red; starts with "Turned on", contains "restarted", starts with "Test passed" → green; "Turned off" and "Ready." → muted. Trims the list at 20,000 characters |
| `UpdateStats()` | Writes the three `StatStrip` figures; dimmed when off; Active is green while connections are open; Errors turns red above 0 |
| `UpdateSteam()` | Computes `SteamState` (`NotRunning`, `Starting`, `Using`, `NeedsRestart`, `RunningOff`). `NeedsRestart` needs the bypass on for more than 45 s, `steam.exe` running for more than 45 s and no connection from any `steam.exe` to `127.0.0.1:18081`. Sets the `SteamStatus` text and the button label (`Open` / `Restart`) |
| `ApplyUi()` | Single place that maps state to the power button, headline, hint, tray icon and text, tray menu label, button enabled states; ends with `UpdateStats()` and `UpdateSteam()` |
| `Toggle`, `TurnOn(interactive)`, `TurnOff` | `TurnOn`: start proxy, apply PAC, set `isOn`, log; on failure stop the proxy, set `startError` (`StartFailure` turns "address in use" into a plain sentence) and show an error notice. If Steam runs and is not using the bypass: interactive → ask to restart, otherwise a warning notice; if Steam is fine, a success notice. `interactive` also triggers `AutoTest()` |
| `Notify(title, detail, kind, always)` | `Toast.Flash`, skipped in preview or when `reallyExit`, and skipped while the window has focus unless `always` |
| `SteamRunning`, `FindSteamExe`, `SteamAction`, `RestartSteam` | Steam is found through `HKCU\Software\Valve\Steam` (`SteamExe`, else `SteamPath\steam.exe`). `SteamAction` confirms before restarting a running Steam. `RestartSteam` runs `steam.exe -shutdown`, waits up to 40 s for `steam` and `steamwebhelper` to exit, starts it again |
| `Probe(spec, viaProxy)` | One HTTP(S) request (12 s timeout, no redirects). Any HTTP response counts as reachable; failures are described by `Describe` ("Connection refused", "Timed out", "DNS failed", "Blocked (reset)") |
| `RunTest()`, `AutoTest()` | Runs every `Specs` entry in parallel, updates each `CheckRow` as it finishes, logs the summary, shows a notice when the window is not in front. `viaProxy` is true only when the bypass is on; the "Game downloads" spec never uses a proxy (it calls `http://127.0.0.1/` with `Host: cache8-hkg1.steamcontent.com`, which exercises the real downloader path) |
| `GetRunAtLogon`, `SetRunAtLogon` | `HKCU\...\Run` value `SteamBypass` = `"<exe>" --tray` |
| `ShowWindow`, `ExitApp`, `OnFormClosing`, `SetVisibleCore` | Closing hides to the tray with a notice; `ExitApp` sets `reallyExit`, turns off, removes the tray icon, exits; `SetVisibleCore` keeps the window hidden for `--tray` |

`SteamLink.UsesProxy(pids, port)`: `GetExtendedTcpTable` (IPv4, `TCP_TABLE_OWNER_PID_ALL`), rows of 24 bytes
(state at 0, remote address at 12, remote port at 16 in network order, pid at 20); true when an established row
from one of the pids goes to `127.0.0.1:<port>`.

`Specs` (connection check): Store, Community, Static content (all three through the proxy) and Game downloads
(loopback `:80`).

## Controls.cs

- `Theme`: colour fields (`Bg, Card, Border, Raised, RaisedHover, Text, Muted, Faint, Stroke, Green, GreenDeep, Amber, Red, Accent`), type sizes in points (`TMeta 8.5, TBody 9.5, TTitle 11, TValue 15, TState 18`), `Scale`, `S(int|float)`, `F(pt)`, `Semi(pt)`, `Lerp`, `RoundRect`. `Muted` is 6.8:1 and `Faint` 4.8:1 on `Card`; `Stroke` is for strokes only, never text.
- `Surface`: base class. Double-buffered, not selectable by default; `Interactive(role)` makes it a Tab stop with a hand cursor and an accessible role; clears with its parent's `BackColor` (so rounded shapes sit on the right colour); redraws on focus changes.
- `Card`: rounded fill + 1 px border. `Logo`: flat accent square with an open ring and a dot.
- `PowerButton`: `State` is `Off`, `On` or `Error`; eases between them (timer only while moving); hover and pressed states; focus ring only with `ShowFocusCues`. Raises `Click`.
- `ToggleSwitch`: `Checked` raises `CheckedChanged`; `SetCheckedSilently` does not. Near-white track when on (green is reserved for the bypass state).
- `FlatButton` (`Compact` for the small ones), `TextAction` (underline on hover, right-aligned text), `StatStrip` (`Set(i, text, colour)`, three cells split by hairlines), `SteamStatus` (`Set(text, colour)`: dot, "Steam", one status line), `CheckRow` (`St` = `Idle`, `Running`, `Ok`, `Fail`; `Set(state, detail)`; spinner timer only while running).
- `Toast` + `ToastKind` (`Info`, `Success`, `Warning`, `Error`): `Toast.Flash(title, detail, kind)` returns the form. Borderless, top-most, `WS_EX_NOACTIVATE | TOOLWINDOW | TRANSPARENT`, rounded `Region`, height from the measured detail text (two lines at most), bottom-centre of the screen that holds the cursor, 140 ms fade in, hold 2.6 s (5 s warning, 6 s error), 220 ms fade out. One at a time: a new one closes the old.

## Things that look wrong but are intended

- Two code paths forward HTTP (`Handle` for proxied requests with `Connection: close`, `HandleDirectHttp` for the downloader with keep-alive). They cannot be merged without losing either the rewriting or the throughput.
- Port 80 failing is not fatal: the app still turns on and shows an amber hint, because browsing works without it.
- `Total` and `Failed` are not reset when the bypass is turned off; the figures are "since the app started" and are dimmed while off.
- `Specs[3]` calls `127.0.0.1` with a fake `Host` header on purpose; it fails with "Connection refused" while the bypass is off.
- `PreviewMode` exists only for `tools/preview.ps1`; it skips the tray icon, registry, sockets, timers and file logging.
- `legacy-goodbyedpi/` is not referenced by anything.
