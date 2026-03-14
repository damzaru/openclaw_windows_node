param(
  [string]$RepoPath = "",
  [string]$Platform = "x64",
  [string]$Configuration = "Debug",
  [int]$PollMs = 1500,
  [string]$PauseFile = ".node-watchdog.pause",
  [string]$LogFile = "",
  [bool]$EnableTray = $true
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
  $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Write-Log {
  param([string]$Message)
  $line = "[$(Get-Date -Format o)] $Message"
  Write-Host $line
  if (-not [string]::IsNullOrWhiteSpace($LogFile)) {
    Add-Content -Path $LogFile -Value $line
  }
}

function Get-RunningNodeProcesses {
  param([string]$ExePath)

  $target = $ExePath.ToLowerInvariant()
  return @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "OpenClaw.Node.exe" -and (
      ($_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant() -eq $target) -or
      ($_.CommandLine -and $_.CommandLine -like "*$ExePath*")
    )
  })
}

$repoPathFull = (Resolve-Path $RepoPath).Path
$pausePath = Join-Path $repoPathFull $PauseFile
$trayExePath = Join-Path $repoPathFull "src\OpenClaw.Node\bin\$Platform\$Configuration\net8.0-windows\OpenClaw.Node.exe"
$headlessExePath = Join-Path $repoPathFull "src\OpenClaw.Node\bin\$Platform\$Configuration\net8.0\OpenClaw.Node.exe"
$logDir = Join-Path $repoPathFull "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
if ([string]::IsNullOrWhiteSpace($LogFile)) {
  $LogFile = Join-Path $logDir "node-watchdog.log"
}
$childLogPath = Join-Path $logDir ("node-watchdog-child-{0}.log" -f $PID)
$childErrLogPath = Join-Path $logDir ("node-watchdog-child-{0}.err.log" -f $PID)

Write-Log "Watchdog starting"
Write-Log "RepoPath=$repoPathFull"
Write-Log "TrayExePath=$trayExePath"
Write-Log "HeadlessExePath=$headlessExePath"
Write-Log "PauseFile=$pausePath"
Write-Log "EnableTray=$EnableTray"
Write-Log "ChildLog=$childLogPath"
Write-Log "ChildErrLog=$childErrLogPath"

while ($true) {
  try {
    $effectiveEnableTray = $EnableTray
    $exePath = $headlessExePath

    if ($EnableTray -and (Test-Path $trayExePath)) {
      $exePath = $trayExePath
    }
    elseif ($EnableTray -and -not (Test-Path $trayExePath)) {
      Write-Log "Tray build not found yet ($trayExePath). Falling back to headless target."
      $effectiveEnableTray = $false
    }

    if (Test-Path $pausePath) {
      $running = Get-RunningNodeProcesses -ExePath $exePath
      foreach ($proc in $running) {
        Write-Log "Pause active. Stopping PID=$($proc.ProcessId)"
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
      }
      Start-Sleep -Milliseconds $PollMs
      continue
    }

    if (-not (Test-Path $exePath)) {
      Write-Log "Node binary not found yet: $exePath"
      Start-Sleep -Milliseconds $PollMs
      continue
    }

    $running = Get-RunningNodeProcesses -ExePath $exePath
    if ($running) {
      Start-Sleep -Milliseconds $PollMs
      continue
    }

    Write-Log "Starting OpenClaw.Node"

    $args = ''
    if ($effectiveEnableTray) {
      $args += "--tray"
    }

    $proc = Start-Process -FilePath $exePath -ArgumentList $args -WorkingDirectory $repoPathFull -PassThru -RedirectStandardOutput $childLogPath -RedirectStandardError $childErrLogPath
    Write-Log "Started PID=$($proc.Id) Args=$($args -join ' ')"

    Start-Sleep -Milliseconds 400
    $proc.Refresh()
    if ($proc.HasExited) {
      Write-Log "Node exited immediately. ExitCode=$($proc.ExitCode). See child logs: $childLogPath | $childErrLogPath"
    }
  }
  catch {
    Write-Log "Watchdog loop error: $($_.Exception.Message)"
  }

  Start-Sleep -Milliseconds $PollMs
}
