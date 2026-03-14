param(
  [string]$RepoPath = "",
  [string]$TaskName = "OpenClawNodeWatchdog"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
  $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$scriptPath = Join-Path $RepoPath "scripts\node-watchdog.ps1"
if (-not (Test-Path $scriptPath)) {
  throw "Watchdog script not found: $scriptPath"
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -RepoPath `"$RepoPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Installed/updated scheduled task: $TaskName"
Write-Host "Start now: Start-ScheduledTask -TaskName '$TaskName'"
