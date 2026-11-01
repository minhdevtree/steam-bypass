# Pushing and releasing

The project folder is **not a git repository**. Source goes to `github.com/minhdevtree/steam-bypass` (branch `main`,
public) through the GitHub API with `tools\push.ps1`; releases use `gh release`. Only do either when the user asks
(or said "push when you finish"). Everything public is in English.

## Prerequisites

- GitHub CLI installed and logged in: `gh auth status` (account `minhdevtree`, scopes include `repo`). Windows PowerShell may not have it on `PATH` in a fresh session; `push.ps1` reloads `PATH` itself, for other commands run
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- `git` is not needed. Do not `git init` this folder without asking: `dist\`, `legacy-goodbyedpi\` and local logs live here.

## Push source

```powershell
.\tools\push.ps1 -Message "Short imperative summary`n`nWhy, in two or three lines." -Files README.md,app\MainForm.cs
.\tools\push.ps1 -Message "Remove an old screenshot" -Delete docs\old.png
```

Call it directly from the PowerShell session (`& .\tools\push.ps1 -Message $msg -Files $list`). Launching it as
`powershell -File .\tools\push.ps1 ... -Files a,b` fails with "A positional parameter cannot be found", because `-File`
does not pass arrays.

One commit on `main` with exactly those files; the `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` trailer is
appended when missing. The script refuses to publish a text file containing the author's local paths or account
names. Output is `pushed <sha> to <repo>`; verify with
`gh api repos/minhdevtree/steam-bypass/commits?per_page=3` (use `ConvertFrom-Json`; `--jq` with `split("\n")` breaks
in PowerShell quoting).

What goes to GitHub: `README.md`, `LICENSE`, `.gitignore`, `steam-domains.txt`, `app\*`, `docs\*.png`, `tools\*`,
`.claude\skills\steam-bypass\**`. What never does: `SteamBypass.exe` (build output, attached to releases instead),
`dist\`, `legacy-goodbyedpi\`, `settings.ini`, `steam-bypass.log` (all in `.gitignore` or simply not listed). Do not push
a file by name that you have not read.

If you change a source file, push every file you changed in the same call; the build in a release must match the
commit it points at. A release's `Built from commit` line in the notes is the sha that `push.ps1` printed.

Known failure: piping JSON into `gh api --input -` from Windows PowerShell 5.1 gives "Problems parsing JSON". The script
writes a temporary UTF-8 (no BOM) file instead.

## Cut a release

1. Make sure the source on GitHub is the source that built the exe: build, then push, and check
   `(Get-Item .\SteamBypass.exe).LastWriteTime` is later than every file under `app\` (it is only valid if no source
   changed after the build).
2. Stage and zip (inside the Defender-excluded project folder, never `%TEMP%`, or Defender may remove the exe):

```powershell
$ver = 'v1.2.0'
$stage = ".\dist\SteamBypass-$ver-win64"
Remove-Item .\dist\SteamBypass-* -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage -Force | Out-Null
'SteamBypass.exe','steam-domains.txt','README.md','LICENSE' | ForEach-Object { Copy-Item ".\$_" $stage }
Compress-Archive -Path "$stage\*" -DestinationPath ".\dist\SteamBypass-$ver-win64.zip" -Force
Get-FileHash "$stage\SteamBypass.exe" -Algorithm SHA256
Get-FileHash ".\dist\SteamBypass-$ver-win64.zip" -Algorithm SHA256
```

3. Write the notes (English) to a file outside the project (scratchpad or `%TEMP%`). Keep this shape, drop sections that
   do not apply, and be exact about what was and was not tested:
   - one-paragraph summary; **What's new** grouped by area;
   - **Install**: unzip, keep the exe next to `steam-domains.txt`, ports 18081 and 80, "downloads work only while the app is on";
   - **Windows Defender may flag this file** (unsigned, sockets, proxy setting, `Run` entry; `!ml` is a heuristic; build it yourself with `app\build.bat`, verify the checksums, add an exclusion if you trust it; Defender also removes the `Run` entry);
   - **Checksums (SHA-256)** for the zip and the exe, as a code block;
   - **Known limits** (what was not verified, DNS dependency, TCP only);
   - `Built from commit [<sha>](https://github.com/minhdevtree/steam-bypass/commit/<sha>)` and a compare link to the previous tag; disclaimer paragraph (lawful personal use, no anonymity, not affiliated with Valve).
4. Create it:

```powershell
gh release create $ver ".\dist\SteamBypass-$ver-win64.zip" --repo minhdevtree/steam-bypass --target main --title "Steam Bypass $ver" --notes-file <notes.md> --latest
```

5. Verify: `gh release view $ver --repo minhdevtree/steam-bypass --json tagName,isDraft,assets`, the tag points at the
   commit you intended (`gh api repos/minhdevtree/steam-bypass/git/ref/tags/$ver`), and download the asset
   (`gh release download $ver --pattern '*.zip' --dir <empty dir inside dist>`) and compare its SHA-256 with the notes.

Past releases: v1.0.0 (first public release: local proxy, DoH, the `127.0.0.1:80` download forwarder), v1.1.0 (redesigned UI, Steam status row, on-screen notices).
README screenshots live in `docs\`; regenerate them with `tools\preview.ps1` (and a real capture of the window with
`tools\ui-probe.ps1`) when the UI changes.
