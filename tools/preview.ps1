# Renders every UI state of Steam Bypass to PNG files, without showing anything on the real screen,
# opening a socket or touching the registry. Output: dist\preview\*.png (git-ignored).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\preview.ps1
#
# Compiles the app sources together with tools\preview\Preview.cs (entry point SteamBypass.Harness),
# runs the result and lists the pictures. The running app is not touched.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'dist\preview'
New-Item -ItemType Directory $out -Force | Out-Null
Remove-Item (Join-Path $out '*.png') -ErrorAction SilentlyContinue

# Keep this list in step with app\build.bat.
$sources = 'SteamBypass.cs', 'MainForm.cs', 'Controls.cs' | ForEach-Object { Join-Path $root "app\$_" }
& $csc /nologo /codepage:65001 /target:winexe /main:SteamBypass.Harness "/out:$out\preview.exe" `
    "/win32manifest:$root\app\app.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    $sources (Join-Path $root 'tools\preview\Preview.cs')
if ($LASTEXITCODE -ne 0) { throw "Compile failed ($LASTEXITCODE)" }

# Defender may need a moment to scan a new exe (and may remove it: see .claude\skills\steam-bypass\references\testing.md).
Start-Sleep -Seconds 3
if (-not (Test-Path "$out\preview.exe")) { throw 'preview.exe was removed after the build (antivirus?).' }

$p = Start-Process "$out\preview.exe" -ArgumentList "`"$out`"" -PassThru
if (-not $p.WaitForExit(90000)) { $p.Kill(); throw 'preview.exe did not finish in 90 s.' }
if ($p.ExitCode -ne 0) { throw "preview.exe exited with $($p.ExitCode)" }
Get-ChildItem $out -Filter *.png | Select-Object Name, Length | Format-Table -AutoSize
