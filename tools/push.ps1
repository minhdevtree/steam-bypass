# Publishes files to GitHub as ONE commit on main, through the GitHub API (the project folder is not a git repository).
# Needs the GitHub CLI logged in (gh auth status).
#
#   .\tools\push.ps1 -Message "Fix something" -Files README.md,app\MainForm.cs
#   .\tools\push.ps1 -Message "Remove a file" -Delete docs\old.png
#
# Paths are relative to the project root. The commit message gets the Co-Authored-By trailer if it has none.
# Refuses to publish a text file that contains the user's local paths or account names.
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [string[]]$Files = @(),
    [string[]]$Delete = @(),
    [string]$Repo = 'minhdevtree/steam-bypass'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) not found.' }
if ($Files.Count -eq 0 -and $Delete.Count -eq 0) { throw 'Nothing to do: give -Files or -Delete.' }

$trailer = 'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>'
if ($Message -notmatch 'Co-Authored-By:') { $Message = $Message.TrimEnd() + "`n`n" + $trailer + "`n" }

$tmp = Join-Path $env:TEMP ('gh-body-' + [guid]::NewGuid().ToString('N') + '.json')
function Api([string]$method, [string]$path, $body) {
    $args2 = @('api', '-X', $method, "repos/$Repo/$path")
    if ($null -ne $body) {
        [IO.File]::WriteAllText($tmp, ($body | ConvertTo-Json -Depth 10 -Compress), (New-Object Text.UTF8Encoding($false)))
        $args2 += @('--input', $tmp)   # piping JSON into gh fails in Windows PowerShell 5.1
    }
    $out = & gh @args2
    if ($LASTEXITCODE -ne 0) { throw "gh api failed: $method $path" }
    $out
}

try {
    $textExt = '.md', '.cs', '.ps1', '.bat', '.txt', '.manifest', '.json', '.yml', ''
    # Built from pieces so this file does not match its own scan.
    $personal = (('min' + 'hh'), ('C:\\Us' + 'ers\\'), ('gmail' + '\.com'), ('not scan' + ' folder')) -join '|'
    foreach ($f in $Files) {
        $p = Join-Path $root $f
        if (-not (Test-Path $p)) { throw "Missing file: $f" }
        if ($textExt -contains [IO.Path]::GetExtension($p).ToLowerInvariant()) {
            $hit = Select-String -Path $p -Pattern $personal -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { throw "Personal data in ${f}:$($hit.LineNumber): $($hit.Line.Trim())" }
        }
    }

    $parent = (Api GET 'git/ref/heads/main' $null | ConvertFrom-Json).object.sha
    $base = (Api GET "git/commits/$parent" $null | ConvertFrom-Json).tree.sha
    $tree = @()
    foreach ($f in $Files) {
        $blob = Api POST 'git/blobs' @{ content = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $f))); encoding = 'base64' } | ConvertFrom-Json
        $tree += @{ path = ($f -replace '\\', '/'); mode = '100644'; type = 'blob'; sha = $blob.sha }
    }
    foreach ($f in $Delete) { $tree += @{ path = ($f -replace '\\', '/'); mode = '100644'; type = 'blob'; sha = $null } }

    $t = Api POST 'git/trees' @{ base_tree = $base; tree = $tree } | ConvertFrom-Json
    $c = Api POST 'git/commits' @{ message = $Message; tree = $t.sha; parents = @($parent) } | ConvertFrom-Json
    Api PATCH 'git/refs/heads/main' @{ sha = $c.sha } | Out-Null
    Write-Host ("pushed {0} to {1}: {2} file(s) changed, {3} deleted" -f $c.sha.Substring(0, 7), $Repo, $Files.Count, $Delete.Count)
}
finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
