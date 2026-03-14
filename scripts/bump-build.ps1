param(
  [string]$RepoPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
  $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Log { param([string]$m) Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

$repoPathFull = (Resolve-Path $RepoPath).Path
$buildInfoPath = Join-Path $repoPathFull "src\OpenClaw.Node\BuildInfo.cs"

if (-not (Test-Path $buildInfoPath)) {
  throw "BuildInfo.cs not found: $buildInfoPath"
}

$content = Get-Content $buildInfoPath -Raw
$match = [regex]::Match($content, 'BuildVersion\s*=\s*"b(?<num>\d{4})"')
if (-not $match.Success) {
  throw "Could not find BuildVersion pattern in $buildInfoPath"
}

$current = [int]$match.Groups['num'].Value
$next = $current + 1
$nextLabel = ('b{0:D4}' -f $next)
$newContent = [regex]::Replace($content, 'BuildVersion\s*=\s*"b\d{4}"', ('BuildVersion = "' + $nextLabel + '"'), 1)
Set-Content -Path $buildInfoPath -Value $newContent -NoNewline

Log "BuildVersion bumped: b$('{0:D4}' -f $current) -> $nextLabel"
Write-Output $nextLabel
