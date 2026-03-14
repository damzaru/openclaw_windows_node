param(
  [string]$RepoPath = "",
  [string]$Platform = "x64",
  [string]$Configuration = "Debug",
  [string]$TargetFramework = "net8.0-windows"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
  $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$repoPathFull = (Resolve-Path $RepoPath).Path
$buildInfoPath = Join-Path $repoPathFull "src\OpenClaw.Node\BuildInfo.cs"
$buildExePath = Join-Path $repoPathFull "src\OpenClaw.Node\bin\$Platform\$Configuration\$TargetFramework\OpenClaw.Node.exe"
$watchdogScript = Join-Path $repoPathFull "scripts\node-watchdog.ps1"

function Get-BuildLabel {
  param([string]$Path)
  if (-not (Test-Path $Path)) { return $null }
  $content = Get-Content $Path -Raw
  $match = [regex]::Match($content, 'BuildVersion\s*=\s*"(?<label>b\d{4})"')
  if ($match.Success) { return $match.Groups['label'].Value }
  return $null
}

function Get-ProcessHash {
  param([string]$Path)
  if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path $Path)) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
  }
  return $null
}

$localWatchdogs = @(Get-CimInstance Win32_Process | Where-Object {
  $_.Name -match 'powershell' -and $_.CommandLine -like "*$watchdogScript*"
})

$runningNodes = @(Get-CimInstance Win32_Process | Where-Object {
  ($_.Name -eq 'OpenClaw.Node.exe') -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'OpenClaw\.Node')
}) | ForEach-Object {
  [pscustomobject]@{
    processId = $_.ProcessId
    name = $_.Name
    executablePath = $_.ExecutablePath
    commandLine = $_.CommandLine
    exeHash = (Get-ProcessHash -Path $_.ExecutablePath)
  }
}

$buildExists = Test-Path $buildExePath
$buildVersionInfo = $null
$buildHash = $null
if ($buildExists) {
  $buildVersionInfo = (Get-Item $buildExePath).VersionInfo | Select-Object FileVersion, ProductVersion
  $buildHash = (Get-FileHash -Algorithm SHA256 -Path $buildExePath).Hash
}

$result = [pscustomobject]@{
  repoPath = $repoPathFull
  sourceBuildLabel = Get-BuildLabel -Path $buildInfoPath
  buildInfoPath = $buildInfoPath
  canonicalBuildExePath = $buildExePath
  canonicalBuildExists = $buildExists
  canonicalBuildFileVersion = $buildVersionInfo.FileVersion
  canonicalBuildProductVersion = $buildVersionInfo.ProductVersion
  canonicalBuildSha256 = $buildHash
  watchdogRunningCount = $localWatchdogs.Count
  watchdogPids = @($localWatchdogs | Select-Object -ExpandProperty ProcessId)
  runningNodes = $runningNodes
}

$result | ConvertTo-Json -Depth 6
