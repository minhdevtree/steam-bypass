# How Steam is blocked: evidence and re-measuring

Everything the design rests on was measured on one network (Vietnam, October 2026, home Wi-Fi, ISP resolver on the
router at `192.168.31.1`). The README has the same tables for readers; this file adds what was tried, what failed
and how to repeat each measurement when Steam stops working again. If a test here gives a different result than
the table, the ISP changed something: update this file and the README, then the code.

## Layers

| # | Layer | Measured behaviour | Evidence |
| --- | --- | --- | --- |
| 1 | DNS poisoning | ISP resolver answers `127.0.0.1` for `store.steampowered.com`, `steamcommunity.com`, `api.steampowered.com`, `*.steamcontent.com`, `*.steamserver.net`. `8.8.8.8` and `208.67.222.222` over UDP are intercepted and answered with a non-Steam address (`118.68.82.144`). `1.1.1.1` over UDP gave real answers for some names but the system resolver still returned `127.0.0.1` | `Resolve-DnsName <name> -Type A [-Server <ip>]` |
| 2 | TLS SNI filter | Same real IP, SNI `example.com` completes the handshake; SNI `store.steampowered.com` gets a TCP reset | `curl --resolve` |
| 3 | HTTP `Host` filter | Port 80 with `Host: store.steampowered.com` is reset; case-variant header names pass | probe program, see below |
| 4 | Client behaviour | `steam.exe`'s downloader ignores the Windows proxy, resolves with the system DNS and therefore connects to `127.0.0.1:80`; the browser engine (`steamwebhelper`) and the Steam UI use the proxy; `steam.exe` reads proxy settings at start only | `<SteamPath>\logs\content_log.txt` lines like `cache8-hkg1.steamcontent.com (127.0.0.1:80 ...): failed to send manifest request`; `Get-NetTCPConnection` showed `SynSent 127.0.0.1:80` |

IP reachability was fine throughout (`Test-NetConnection <real ip> -Port 443` succeeded), so this is not an IP block.

## What gets past the SNI filter

A test program captured a real TLS 1.2 ClientHello for `store.steampowered.com` (an `SslStream` talking to a local
listener, protocol forced to TLS 1.2: the .NET default hello is rejected by the server and gives false negatives)
and replayed it to the real IP in different shapes:

| Variation | Result |
| --- | --- |
| Unmodified | reset |
| Split into two TCP segments at byte 1 or 2 | **passes** (this is what the app does: 2 bytes, 20 ms, rest) |
| Four segments cut at SNI offset -2, +3, +9 | passes |
| Split exactly at the SNI start or 5 bytes into it | reset |
| Every 3 bytes through the SNI | reset |
| 500 ms between pieces | no difference |
| Two TLS records (record fragmentation) | passes (even in one write) |
| UPPERCASE or mixed-case SNI | reset |
| SNI with a trailing dot | passes |

The exact parsing rule of the middlebox was not worked out. The one consistent pattern: cutting inside the first two
bytes (the record header) always got through, and the server just sees a normal TCP stream.

## What gets past the Host filter

Plain HTTP to the real IP, header name varied:

| Header | Result |
| --- | --- |
| `Host:`, `host:` | reset |
| `HoSt:`, `hOST:`, `HOST:`, `hoST:` | passes |
| no space, tab, trailing dot in the value, header split across two packets, split at byte 1 or 3 | reset |
| `HoSt:` first request then plain `Host:` on the same connection | the second is reset: rewrite **every** request |

## What did not work

- Changing the DNS server only (Google, Cloudflare): fixes layer 1 for some names, nothing for layers 2-3.
- **GoodbyeDPI 0.2.2** (WinDivert driver): modes -1 to -6 started but Steam stayed blocked; -7, -8 and -9 exited immediately on Windows 11 build 26300; it also needs administrator rights. The attempt is kept untracked in `legacy-goodbyedpi/`. A user-space proxy that cuts the first two bytes does the job without any of that.
- System proxy alone: fixes the browser and the Steam UI, not game downloads (layer 4).

## Re-measuring (do these in order, stop at the first difference)

```powershell
# 1. DNS: ISP answer vs a public resolver
Resolve-DnsName store.steampowered.com -Type A -DnsOnly
Resolve-DnsName store.steampowered.com -Type A -Server 1.1.1.1 -DnsOnly
Resolve-DnsName cache8-hkg1.steamcontent.com -Type A -DnsOnly      # expect 127.0.0.1 for the downloader path to work

# 2. DoH reaches Cloudflare and Google (the app depends on this)
curl.exe -s -m 8 -H "accept: application/dns-json" "https://1.1.1.1/dns-query?name=store.steampowered.com&type=A"

# 3. Real IP reachable, then SNI filter (use an IP from step 1/2)
Test-NetConnection <ip> -Port 443
curl.exe -sS -o NUL -m 8 --resolve example.com:443:<ip> https://example.com/                      # handshake completes (HTTP 400 is fine)
curl.exe -sS -o NUL -m 8 --resolve store.steampowered.com:443:<ip> https://store.steampowered.com/ # "Connection was reset" while blocked

# 4. Through the app (turn the bypass on first)
curl.exe -s -o NUL -m 15 -x http://127.0.0.1:18081 -w "%{http_code}`n" https://store.steampowered.com/
curl.exe -s -o NUL -m 20 --resolve cache8-hkg1.steamcontent.com:80:127.0.0.1 -w "%{http_code}`n" http://cache8-hkg1.steamcontent.com/

# 5. What Steam itself says
$sp = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
Get-Content "$sp\logs\content_log.txt" -Tail 30
Get-Content "$sp\logs\connection_log.txt" -Tail 30
```

Reading step 4: HTTP 200/301/403 means the path works (the CDN root returns 403 or 301, which is fine); `000` means
the connection died. In `connection_log.txt`, `RecvMsgClientLogOnResponse() : ... 'OK'` is a good login and
`ConnectFailed('Connection Failed':0) (0.0.0.0:0, WebSocket)` repeated means `steam.exe` is not using the proxy
(restart Steam with the bypass on). In `content_log.txt`, `stats: ... Mbps` lines show real download speed and
`(127.0.0.1:80 ...) failed to send manifest request` means the forwarder is not running or port 80 is taken.

To write a new probe: a small C# program (compiled with the same `csc`) that captures a ClientHello and replays it
split different ways, or sends hand-built HTTP requests with a header name varied, against the real IP. Keep it in
`dist\` (git-ignored) and do not commit it unless it becomes part of a test routine.
