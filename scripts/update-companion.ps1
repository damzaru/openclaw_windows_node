#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.FullName
$project = Join-Path $repo 'src\OpenClaw.Node'
$release = Join-Path $project 'bin\x64\Release\net8.0-windows'
$exe = Join-Path $release 'OpenClaw.Node.exe'

function Invoke-Git {
    & git -C $repo @args
    if ($LASTEXITCODE -ne 0) { throw "Git failed ($LASTEXITCODE): git $args" }
}

function Get-InstalledProcess([string]$path, [switch]$IncludeUnknown) {
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    foreach ($p in (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        if ($p.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) { continue }
        if ($p.Path -eq $path -or (!$p.Path -and $IncludeUnknown)) { $p }
    }
}

function Stop-InstalledProcess($process) {
    Write-Host "Stopping PID $($process.Id) ($($process.Path))"
    Stop-Process -InputObject $process -Force
    if (!$process.WaitForExit(10000)) { throw "Timed out stopping PID $($process.Id)." }
}

function Stop-ElevatedRelease {
    # Elevate only this fixed stop operation, never Git, MSBuild or the replacement tray.
    $targetLiteral = "'" + $exe.Replace("'", "''") + "'"
    $command = '$target = ' + $targetLiteral + ';' + @'
    $ErrorActionPreference = 'Stop'
    $stopped = $false
    try {
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        foreach ($p in (Get-Process -Name OpenClaw.Node -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)) {
            if (!$p.Path) { throw 'Cannot verify companion path' }
            if ($p.Path -ne $target) { continue }
            Stop-Process -InputObject $p -Force
            if (!$p.WaitForExit(10000)) { throw 'Companion did not stop' }
            $stopped = $true
        }
        $worker = Join-Path (Split-Path -Parent $target) 'runtimes\browser\node\node.exe'
        foreach ($p in (Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.Path -eq $worker })) {
            Stop-Process -InputObject $p -Force
            if (!$p.WaitForExit(10000)) { throw 'Bundled worker did not stop' }
        }
        if ($stopped) { exit 10 }; exit 0
    } catch { exit 1 }
'@
    Write-Host 'Windows needs permission to stop the previous Release. Check the UAC prompt.'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($child.ExitCode -notin 0,10) { throw 'Cannot verify or stop the elevated Release. Exit it from the tray and rerun.' }
    return ($child.ExitCode -eq 10)
}

function Start-Tray {
    $p = Start-Process -FilePath $exe -ArgumentList '--tray' -WorkingDirectory $release -WindowStyle Hidden -PassThru
    if ($p.WaitForExit(2000)) { throw "Companion exited during startup (code $($p.ExitCode)). Check the companion logs." }
    Write-Host "Started $exe (PID $($p.Id))"
}

function Ensure-BrowserRuntime {
    $runtime = Join-Path $project 'browser-runtime'
    $pin = Get-Content -Raw -LiteralPath (Join-Path $runtime 'versions.json') | ConvertFrom-Json
    $node = Join-Path $runtime 'node\node.exe'
    if ($pin.nodeVersion -notmatch '^v\d+\.\d+\.\d+$' -or $pin.nodeWinX64Sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Missing or invalid Windows Node runtime pin in browser-runtime/versions.json.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $runtime 'chrome-devtools-mcp\build\src\bin\chrome-devtools-mcp.js') -PathType Leaf)) {
        throw 'The tracked browser launcher is missing from the checkout.'
    }
    if ((Test-Path -LiteralPath $node) -and (Get-FileHash -LiteralPath $node).Hash -eq $pin.nodeWinX64Sha256) { return }
    $tmp = "$node.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Write-Host "Restoring pinned Node $($pin.nodeVersion) for the build..."
        Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$($pin.nodeVersion)/win-x64/node.exe" -OutFile $tmp
        if ((Get-FileHash -LiteralPath $tmp).Hash -ne $pin.nodeWinX64Sha256) { throw 'Downloaded node.exe failed SHA-256 verification.' }
        Move-Item -LiteralPath $tmp -Destination $node -Force
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp }
    }
}

if (!$PSCmdlet.ShouldProcess($exe, 'Stop companion, fast-forward main from origin, build x64 Release, and start the new tray app')) { return }
Get-Command git,dotnet -CommandType Application -ErrorAction Stop | Out-Null
# Prevent simultaneous invocations racing the checkout or the running tray process.
$mutex = [Threading.Mutex]::new($false, 'Local\OpenClawCompanionReleaseUpdate')
$locked = $false
$restartOnFailure = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Another companion update is already running.' }
    if ((Invoke-Git rev-parse --show-toplevel) -replace '/', '\' -ne $repo) { throw 'Run this script from the companion checkout.' }
    if ((Invoke-Git branch --show-current) -ne 'main') { throw 'Switch to main before updating. No app was stopped.' }
    if (Invoke-Git status --porcelain --untracked-files=normal) { throw 'Commit or stash local changes first. No app was stopped.' }
    # Fetch before downtime, and refuse to build a local-only or diverged main.
    Invoke-Git fetch origin main
    Invoke-Git merge-base --is-ancestor HEAD FETCH_HEAD
    $target = Invoke-Git rev-parse FETCH_HEAD
    $running = @(Get-InstalledProcess $exe -IncludeUnknown)
    if (@($running | Where-Object { !$_.Path }).Count) {
        $restartOnFailure = Stop-ElevatedRelease
    } else {
        foreach ($p in $running) {
            try { Stop-InstalledProcess $p; $restartOnFailure = $true }
            catch {
                $cause = $_.Exception.GetBaseException()
                if ($_.CategoryInfo.Category -ne 'PermissionDenied' -and $cause -isnot [UnauthorizedAccessException] -and
                    !($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 5)) { throw }
                $stopped = Stop-ElevatedRelease
                $restartOnFailure = $stopped -or $restartOnFailure
                break
            }
        }
    }
    # Only the bundled worker can hold this build's runtime files open. Leave Chrome and system Node alone.
    foreach ($p in (Get-InstalledProcess (Join-Path $release 'runtimes\browser\node\node.exe'))) { Stop-InstalledProcess $p }
    Invoke-Git merge --ff-only $target
    Ensure-BrowserRuntime
    $restartOnFailure = $false  # from here, a failed build might have partially overwritten Release
    & dotnet build (Join-Path $project 'OpenClaw.Node.csproj') -c Release -f net8.0-windows -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Release build failed ($LASTEXITCODE). Companion not started; fix the error and rerun." }
    foreach ($name in @('OpenClaw.Node.exe','OpenClaw.Node.dll','OpenClaw.Node.runtimeconfig.json','runtimes\browser\node\node.exe','runtimes\browser\chrome-devtools-mcp\build\src\bin\chrome-devtools-mcp.js')) {
        if (!(Test-Path -LiteralPath (Join-Path $release $name) -PathType Leaf)) { throw "Incomplete Release build: missing $name." }
    }
    Start-Tray
} catch {
    if ($restartOnFailure -and !(Get-InstalledProcess $exe)) { Start-Tray }
    throw
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
