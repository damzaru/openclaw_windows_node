param(
  [string]$RepoPath = "",
  [string]$Branch = "main",
  [string]$Platform = "x64",
  [string]$Configuration = "Debug",
  # IMPORTANT: screen.capture + tray builds require the Windows TFM.
  # If you build net8.0, the node will run but screen APIs will fall back to a 1x1 PNG.
  [string]$TargetFramework = "net8.0-windows",
  # Runtime install location used by the watchdog/service.
  # This is the *actual* folder the node should run from.
  [string]$RuntimeDir = "",
  [switch]$NoPull,
  [switch]$NoBuild,
  [switch]$NoPublish,
  [switch]$StartWatchdog,
  [int]$WaitForNodeSeconds = 20
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
  $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Log { param([string]$m) Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

$repoPathFull = (Resolve-Path $RepoPath).Path
$pausePath = Join-Path $repoPathFull ".node-watchdog.pause"
$watchdogScript = Join-Path $repoPathFull "scripts\node-watchdog.ps1"

$buildExePath = Join-Path $repoPathFull "src\OpenClaw.Node\bin\$Platform\$Configuration\$TargetFramework\OpenClaw.Node.exe"

# Publish is only used when you explicitly provide -RuntimeDir.
# Default workflow: build into bin/.. and let node-watchdog run that output.
$publishEnabled = (-not $NoPublish) -and (-not [string]::IsNullOrWhiteSpace($RuntimeDir))
$runtimeExePath = if ($publishEnabled) { (Join-Path $RuntimeDir "OpenClaw.Node.exe") } else { "" }
$expectedExePath = if ($publishEnabled) { $runtimeExePath } else { $buildExePath }

function Get-WatchdogProcesses {
  return @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match 'powershell' -and $_.CommandLine -like "*node-watchdog.ps1*" -and $_.CommandLine -like "*$repoPathFull*"
  })
}

try {
  Log "Pausing watchdog + stopping node processes"
  Set-Content -Path $pausePath -Value "paused $(Get-Date -Format o)"

  Get-Process OpenClaw.Node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

  $stopDeadline = (Get-Date).AddSeconds(10)
  do {
    $stillRunning = @(Get-Process OpenClaw.Node -ErrorAction SilentlyContinue)
    if (-not $stillRunning) { break }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $stopDeadline)

  $watchdogs = Get-WatchdogProcesses
  if ($watchdogs.Count -gt 1) {
    Log "Multiple watchdogs detected ($($watchdogs.Count)). Stopping extras."
    $watchdogs | Sort-Object ProcessId | Select-Object -Skip 1 | ForEach-Object {
      Log "Stopping extra watchdog PID=$($_.ProcessId)"
      Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
  }

  Push-Location $repoPathFull

  if (-not $NoPull) {
    Log "Syncing git ($Branch)"
    git fetch origin $Branch
    git checkout $Branch
    git pull --ff-only origin $Branch
  }

  if (-not $NoBuild) {
    Log "Building OpenClaw.Node (TFM=$TargetFramework Platform=$Platform Config=$Configuration)"
    dotnet build src\OpenClaw.Node\OpenClaw.Node.csproj -p:Platform=$Platform -c $Configuration -f $TargetFramework
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
  }

  if (-not (Test-Path $buildExePath)) {
    throw "Expected build exe not found: $buildExePath"
  }

  # Validate that we're not accidentally building a net8.0 (non-windows) target.
  $buildRuntimeConfigPath = [System.IO.Path]::ChangeExtension($buildExePath, ".runtimeconfig.json")
  if (-not (Test-Path $buildRuntimeConfigPath)) {
    throw "Expected build runtimeconfig not found: $buildRuntimeConfigPath"
  }

  function Assert-RuntimeConfigMatchesTarget {
    param(
      [Parameter(Mandatory=$true)][string]$RuntimeConfigPath,
      [Parameter(Mandatory=$true)][string]$TargetFramework
    )

    $rc = Get-Content $RuntimeConfigPath -Raw | ConvertFrom-Json

    # NOTE: For WindowsDesktop apps, runtimeconfig.json typically still reports tfm=net8.0
    # even when the project targets net8.0-windows. The reliable signal is the presence
    # of Microsoft.WindowsDesktop.App in runtimeOptions.frameworks.
    if ($TargetFramework.ToLowerInvariant().EndsWith("-windows")) {
      $fw = @($rc.runtimeOptions.frameworks | ForEach-Object { $_.name })
      if (-not ($fw -contains "Microsoft.WindowsDesktop.App")) {
        throw "WindowsDesktop framework missing. Expected runtimeOptions.frameworks to include Microsoft.WindowsDesktop.App. Found: $($fw -join ', ')"
      }
    }
  }

  try {
    Assert-RuntimeConfigMatchesTarget -RuntimeConfigPath $buildRuntimeConfigPath -TargetFramework $TargetFramework
  }
  catch {
    throw "Failed to validate build runtimeconfig at ${buildRuntimeConfigPath}: $($_.Exception.Message)"
  }

  if ($publishEnabled) {
    Log "Publishing OpenClaw.Node -> $RuntimeDir"
    New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null

    # Use publish so the runtime folder contains the exact bits the watchdog/service executes.
    dotnet publish src\OpenClaw.Node\OpenClaw.Node.csproj -p:Platform=$Platform -c $Configuration -f $TargetFramework -r win-x64 --self-contained false -o $RuntimeDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path $runtimeExePath)) {
      throw "Expected runtime exe not found after publish: $runtimeExePath"
    }

    $runtimeRuntimeConfigPath = [System.IO.Path]::ChangeExtension($runtimeExePath, ".runtimeconfig.json")
    if (-not (Test-Path $runtimeRuntimeConfigPath)) {
      throw "Expected runtime runtimeconfig not found: $runtimeRuntimeConfigPath"
    }

    try {
      Assert-RuntimeConfigMatchesTarget -RuntimeConfigPath $runtimeRuntimeConfigPath -TargetFramework $TargetFramework
    }
    catch {
      throw "Failed to validate runtime runtimeconfig at ${runtimeRuntimeConfigPath}: $($_.Exception.Message)"
    }
  }

  Log "Reload prep complete"
}
finally {
  Pop-Location -ErrorAction SilentlyContinue
  if (Test-Path $pausePath) {
    Remove-Item $pausePath -Force
    Log "Watchdog unpaused"
  }
}

if ($StartWatchdog) {
  if (-not (Test-Path $watchdogScript)) {
    throw "Watchdog script not found: $watchdogScript"
  }

  $already = Get-CimInstance Win32_Process | Where-Object { $_.Name -match "powershell" -and $_.CommandLine -like "*$watchdogScript*" }
  if (-not $already) {
    Log "Starting watchdog"
    $watchdogLogDir = Join-Path $repoPathFull "logs"
    New-Item -ItemType Directory -Force -Path $watchdogLogDir | Out-Null
    $watchdogLogFile = Join-Path $watchdogLogDir "node-watchdog.log"
    Start-Process powershell -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $watchdogScript, "-RepoPath", $repoPathFull, "-Platform", $Platform, "-Configuration", $Configuration, "-LogFile", $watchdogLogFile)
  }
  else {
    Log "Watchdog already running"
  }
}

$deadline = (Get-Date).AddSeconds($WaitForNodeSeconds)
do {
  $running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "OpenClaw.Node.exe" -and (
      ($_.ExecutablePath -and $_.ExecutablePath -eq $expectedExePath) -or
      ($_.CommandLine -and $_.CommandLine -like "*$expectedExePath*")
    )
  })

  if ($running) {
    Log "Node running (PID=$($running[0].ProcessId))"
    exit 0
  }

  Start-Sleep -Milliseconds 800
} while ((Get-Date) -lt $deadline)

Log "Node not detected yet (expected exe: $expectedExePath). If watchdog isn't running, start it with:"
Log "powershell -NoProfile -ExecutionPolicy Bypass -File `"$watchdogScript`""
exit 1
