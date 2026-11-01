---
name: steam-bypass
description: Working knowledge of the Steam Bypass codebase (a C# 5 / .NET Framework 4 WinForms tray app that restores access to Steam on networks that block it with DNS poisoning plus SNI and HTTP Host-header DPI, through a local proxy, DNS-over-HTTPS and a PAC file; no admin rights, no driver). Use it before reading or changing anything in this repo, when touching the proxy or the 127.0.0.1:80 download forwarder, the UI controls, the on-screen notices (Toast), the Steam status row, the domain list, the build or a release, and when Steam shows NO CONNECTION, downloads stall, Defender removes the exe, or the way Steam is blocked changes. Saves re-deriving the architecture, the measured blocking behaviour and the gotchas below.
---

# Steam Bypass: codebase guide

Small Windows tray app, public repo `minhdevtree/steam-bypass` (MIT, releases v1.0.0 and v1.1.0). Written for
Steam being unreachable in Vietnam since October 2026, tested on one network on Windows 11. The user-facing story is
in `README.md`; this file is for people changing code.

Detail files (read only when needed):
- `references/code-map.md`: method-level index of the three source files, thread rules, data shapes, intended oddities. Read it instead of opening a source file to find out what a method does.
- `references/blocking-evidence.md`: what the ISP blocks, the measurements behind the design, what did not work, and how to re-measure when the blocking changes.
- `references/testing.md`: build, run, the off-screen UI preview, live checks without touching the real mouse, Defender, regression checklist.
- `references/release.md`: zip, checksums, release notes, pushing without a local git, the GitHub release.

## Hard constraints (check before you write code)

- **C# 5, .NET Framework 4 compiler** (`csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). No `?.`, `$""`, `nameof`, `out var`, expression-bodied members. `async`/`await` is used in the proxy and is fine, but not inside `catch`/`finally` and not with `ref`/`out` parameters. Anonymous `delegate` style is used for UI events.
- **No NuGet, no project file.** `app\build.bat` compiles an **explicit list**: `SteamBypass.cs`, `MainForm.cs`, `Controls.cs`. A new `.cs` file is not picked up until it is added to that list (and to `tools\preview.ps1`).
- **Never needs administrator rights.** `app.manifest` is `asInvoker`; only `HKCU` is written; no driver. The user explicitly did not want UAC prompts. A feature that needs elevation is the wrong feature.
- **Listeners are loopback only**: `127.0.0.1:18081` (proxy + PAC) and `127.0.0.1:80` (download forwarder).
- **Only Steam hostnames leave through DoH** (and anything that reaches `127.0.0.1:80`). Nothing else is sent anywhere; no telemetry.
- **Windows settings must always be restorable.** Every path that turns the bypass on has a matching path that restores `AutoConfigURL` (see `SystemProxy`). Do not add state that cannot be undone from `SteamBypass.exe --off`.
- **UI rules** (set by the user after a design review): flat surfaces, no gradients, no glows, no shadows; colour means state (green = bypass on or passed, amber = warning, red = error); every text colour at least 4.5:1 on its surface; sizes through `Theme.S()`; nothing smaller than 11 px; no animation that runs while idle.
- **All strings are English** (there is no localization layer). Replies to the user are in Vietnamese; code, README, release notes and commit messages are English.

## The problem in four lines

The ISP breaks Steam in layers. (1) DNS: the ISP resolver answers `127.0.0.1` for Steam names. (2) HTTPS: a middlebox reads the SNI in the TLS ClientHello and resets the connection. (3) HTTP: it matches the exact header `Host:` / `host:` on port 80, which is how game data is downloaded. (4) Steam's downloader ignores the Windows proxy and does its own DNS, so it connects to the poisoned `127.0.0.1:80`. Details and numbers: `references/blocking-evidence.md`.

## How it works

```
 browser / steam.exe / webhelper ──(Windows PAC: Steam domains only)──► 127.0.0.1:18081  ProxyServer.Handle
        CONNECT host:443  → resolve over DoH → open real socket → send first 2 bytes of the TLS record, 20 ms, the rest
        GET http://host/…  → resolve over DoH → rewrite request line, "Host:" → "HoSt:", "Connection: close"
        GET /proxy.pac     → generated PAC (DomainSet.BuildPac): PROXY 127.0.0.1:18081; DIRECT for Steam domains, DIRECT otherwise

 Steam's own downloader ──(system DNS answers 127.0.0.1)──────────────► 127.0.0.1:80     ProxyServer.HandleDirectHttp
        reads the Host header, resolves the real address over DoH, forwards with "HoSt:", keep-alive, switches upstream per request
```

- **Turning on** (`MainForm.TurnOn`): `ProxyServer.Start()` (18081 required, 80 optional: failure sets `DownloadsError` and a warning in the UI), `SystemProxy.Apply(PacUrl)` writes `HKCU\...\Internet Settings\AutoConfigURL` and notifies WinINet (options 39 and 37). The previous value is saved in `%APPDATA%\SteamBypass-proxy-state.txt` (`0|` = none, `1|url`). If Steam is already open, offer to restart it. If the user clicked, run the connection check by itself 700 ms later.
- **Turning off / exit / crash cleanup**: `SystemProxy.Restore()` puts the old value back, but only if `AutoConfigURL` still starts with our URL. It also runs at every app start (a crashed previous run) and from `SteamBypass.exe --off`.
- **`steam.exe` reads the proxy setting only when it starts.** A Steam opened before the bypass has no connection to port 18081 and shows `NO CONNECTION`. The Steam row detects it (`SteamLink.UsesProxy`, a TCP-table lookup) and offers Restart. This was the root cause of the most confusing bug in the project.
- **The poisoned answer is what makes the `:80` forwarder work.** If the system resolver returned real Steam IPs, Steam would connect directly with a plain `Host:` and be reset. The app depends on the ISP's DNS; README says so under Limitations.

## Source map

| File | What lives there |
| --- | --- |
| `app/SteamBypass.cs` | `Program.Main` (flags `--tray`, `--on`, `--off`; single-instance mutex `SteamBypassTrayApp`; TLS 1.2 and connection limit for DoH), `Settings` (`settings.ini`, one key `AutoOn`), `SystemProxy`, `DomainSet`, `DnsMissException`, `Resolver` (DoH), `ProxyServer` (everything network) |
| `app/MainForm.cs` | `CheckSpec`/`ProbeResult`, `DarkColors` (tray menu), `SteamLink` (TCP table), `MainForm`: layout, tray, state, Steam row, connection check, notices, autostart |
| `app/Controls.cs` | `Theme` and all owner-drawn controls (`Surface`, `Card`, `Logo`, `PowerButton`, `ToggleSwitch`, `FlatButton`, `TextAction`, `StatStrip`, `SteamStatus`, `CheckRow`) and `Toast` |
| `steam-domains.txt` | Domains routed through the proxy (suffix match, one per line, `#` comments). Must sit next to the exe |
| `app/build.bat`, `app/app.manifest` | Build (explicit file list) and the `asInvoker`, DPI-aware manifest |
| `tools/preview.ps1`, `tools/preview/Preview.cs` | Off-screen UI renderer for every state (see `references/testing.md`) |
| `docs/*.png` | README screenshots (`window-on` is a real capture; `window-off` and `notice` come from the preview tool) |
| `legacy-goodbyedpi/` (local only, git-ignored) | The abandoned GoodbyeDPI attempt. Do not revive it (reason in `blocking-evidence.md`) |

## UI in one screen

Window 740x528 logical px, fixed size, dark title bar via DWM. Left: one card with the power button, state text, a stat strip (Active / Total / Errors) and the Steam row. Right: Connection check card (four rows + "Run test"), Settings card (two switches), Activity card (coloured log, "Open log file", "Clear"). States: off, on, error ("Couldn't turn on", red button), on with a warning (port 80 busy, amber hint), testing. Notices are drawn by the app (`Toast`), not by the Windows tray balloon: they appear when the window does not have focus, never take focus, let clicks through, fade in and out, and stay 2.6 s (5 s warning, 6 s error). Per-host detail goes to the log file only (`ProxyServer.Verbose`), the Activity list shows events (`ProxyServer.Log`).

## Recipes

**Add or remove a proxied domain.** Edit `steam-domains.txt`; it feeds both the PAC and `DomainSet.Contains` (which decides DoH). A domain also covers its subdomains. No code change. Hostnames that only ever reach `127.0.0.1:80` (the downloader) are resolved over DoH regardless of the list.

**Add a row to the connection check.** Add a `CheckSpec` to `MainForm.Specs` (title, subtitle, URL, optional `Host`, `UseProxy`) and make the check card tall enough: row pitch 36 px starting at y 62, card height 220 (grow it and move the cards below, `BuildUi`). `RunTest` and the failure toast pick it up from `Specs`. `rowsOk` is indexed by the same position.

**Add a notice.** `Notify(title, detail, ToastKind, always)` in `MainForm`. `always: false` means "skip while this window has focus". `Toast.Flash` replaces a notice that is still showing; call it on the UI thread.

**Add a setting.** `Settings` only parses `AutoOn=1`. Extend `Settings` (field, parse in the constructor, write in `Save`), add a `ToggleSwitch` through `AddSetting(card, width, top, title, sub)`, restore it with `SetCheckedSilently` before `loading = false`, and save in its `CheckedChanged` guarded by `loading`. If the card gets a third row, grow the card and shift the Activity card.

**Add or change a control.** Derive from `Surface` (not `Control`). Call `Interactive(AccessibleRole)` only if it can be operated (that makes it a Tab stop and a hand cursor); static controls stay out of the Tab order. Size everything with `Theme.S()`, fonts with `Theme.F`/`Theme.Semi` and the `TMeta/TBody/TTitle/TValue/TState` sizes, colours from `Theme` only. Draw the focus ring only when `Focused && ShowFocusCues`. Any timer must stop when the animation ends.

**Change the proxy port.** `ProxyServer.Port` is the only definition; `PacUrl`, the PAC, `SystemProxy.Restore`'s ownership test and `SteamLink.UsesProxy` read it. Port 80 is hard-coded in `Start()` and `HandleDirectHttp`'s `Connect(host, 80, true)` because Steam's downloader connects to port 80 of whatever the DNS said.

**The blocking changes (new Steam failure).** Do not guess. Follow `references/blocking-evidence.md` §"Re-measuring": check DNS, then whether the real IP is reachable, then SNI, then Host, then what Steam itself logs (`<SteamPath>\logs\content_log.txt`, `connection_log.txt`).

**Cut a release.** `references/release.md`.

## Gotchas (each cost time once)

- **Steam must be restarted after the bypass is turned on** (or started after it). See above.
- **Windows Defender quarantined a build** as `Trojan:Win32/Bearfoos.A!ml` (a machine-learning heuristic) right after the UI rewrite, deleted the exe and removed the `Run` entry. The user excluded the project folder on their machine; other machines will still see it. Do not add exclusions yourself and do not change code to dodge a detection. If the exe disappears, check `Get-MpThreatDetection`.
- **`SteamBypass.exe` is locked while it runs**, so `build.bat` fails with CS0016. Stop the process first. Killing it leaves the PAC in the registry; the next start (or `--off`) cleans it up, and the Steam client keeps using its cached proxy settings until restarted.
- **`.NET Framework` defaults to old TLS**: `ServicePointManager.SecurityProtocol = Tls12` in `Main` is required or every DoH lookup fails silently and the app falls back to the poisoned system DNS. `DefaultConnectionLimit = 64` stops DoH lookups queueing.
- **DoH requests must bypass our own PAC**: `req.Proxy = null` in `Resolver.QueryDoh`, otherwise a loop.
- **8.8.8.8 and 208.67.222.222 over UDP are intercepted** by the ISP, and the system resolver is poisoned: never resolve Steam names with `Dns.GetHostAddresses` unless DoH failed.
- **`ipv6check-*.steamserver.net` has no A record on purpose.** `DnsMissException.Expected` makes it a quiet note, not an error.
- **Steam multiplexes many `cacheN-*.steamcontent.com` hosts over one `127.0.0.1:80` connection.** `HandleDirectHttp` therefore switches the upstream per request and ends the old response pump silently (`gen[]` counter). A "host changed on a kept-alive connection" error means that logic regressed.
- **The proxy path forces `Connection: close`**; the loopback path keeps connections alive. They are different code on purpose: absolute-URI requests need rewriting per request, the loopback path needs speed (it moved game data at about 570 Mbit/s).
- **Header case matters to the DPI**: `HoSt:` passes, `Host:`, `host:`, a tab, no space, a trailing dot and a header split across packets all fail.
- **Splitting inside the SNI does not work; cutting the first 2 bytes does.** Do not "improve" the split offset without re-measuring.
- **The window handle is created in the constructor** (`var h = Handle;`) so `BeginInvoke` from the proxy threads works while the window is hidden. `SetVisibleCore` keeps the form hidden for `--tray`.
- **`Append` is called from proxy threads**: it re-dispatches with `BeginInvoke`; always check `IsDisposed`.
- **PowerShell 5.1 and `gh api --input -`**: piping JSON fails with "Problems parsing JSON"; write the body to a UTF-8 (no BOM) file and pass `--input file` (see `references/release.md`).
- **Do not screen-capture and do not drive the real mouse.** A real click once landed on another window. Use `PrintWindow` for pictures and `PostMessage` to the control for clicks (`references/testing.md`).

## Verified and not verified (as of v1.1.0)

Verified on the real machine: turning on and off, PAC set and restored, store/community/static content through the proxy, game downloads through `127.0.0.1:80` (about 570 Mbit/s), Steam sign-in after a Steam restart, the four-row connection check, the notice on hide-to-tray, the preview renders of every state, Defender behaviour with the folder excluded.
Not verified: the Steam row against a live Steam (`Using the bypass` / `Opened before the bypass` were only seen in previews; Steam was closed during the live run), 125% and 150% display scaling, Windows 10, any other ISP, tray menu actions and autostart at sign-in by script, keyboard focus visuals and screen-reader output, multi-day stability.

## Working agreements with this user

Replies in Vietnamese unless asked otherwise. Short, direct answers; report what was verified and what was not, with the evidence. The user dislikes: UAC prompts, a basic-looking UI, the Windows default notification, extra confirmation questions for things already decided. Push or release only when asked (or when the user said to push after finishing a task). This folder is not a git repository; pushes go through the GitHub API (`references/release.md`). Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
