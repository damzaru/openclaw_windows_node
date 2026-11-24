#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
project="$repo/src/OpenClaw.Node"
build="$project/bin/x64/Release/net8.0-windows"
host=${SSH_HOST:-}
key=${SSH_KEY:-}
force=0 dry=0
usage() { echo "Usage: bash scripts/deploy-node.sh [--host user@host] [--ssh-key path] [--force] [--dry-run]"; }
while (($#)); do
  case $1 in
    --host|--ssh-key) [[ $# -ge 2 && -n $2 && $2 != -* ]] || { usage >&2; exit 2; }
      if [[ $1 == --host ]]; then host=$2; else key=$2; fi; shift 2 ;;
    --force) force=1; shift ;; --dry-run) dry=1; shift ;;
    -h|--help) usage; exit 0 ;; *) usage >&2; exit 2 ;;
  esac
done
if ((!dry)) && [[ ! $host =~ ^([a-zA-Z0-9._-]+@)?[a-zA-Z0-9][a-zA-Z0-9.-]*$ ]]; then echo "Specify an SSH destination with --host or SSH_HOST." >&2; exit 2; fi
if command -v wslpath >/dev/null; then winbuild=$(wslpath -w "$build")
elif command -v cygpath >/dev/null; then winbuild=$(cygpath -aw "$build")
else echo 'Run this from WSL or Git Bash on the Windows build machine.' >&2; exit 1; fi
work=$(mktemp -d)
trap 'rm -f -- "$work/release.zip"; rmdir -- "$work"' EXIT
# Only package files recorded by this Windows Release build. A recursive zip of bin/
# can leak old profiles, WebView data, local configuration and unrelated archives.
digest=$(python3 - "$build" "$winbuild" "$project/obj/x64/Release/net8.0-windows/OpenClaw.Node.csproj.FileListAbsolute.txt" "$work/release.zip" <<'PY'
import hashlib, json, sys, zipfile
from pathlib import Path, PureWindowsPath
root, windows, filelist, archive = sys.argv[1:]
root, windows = Path(root).resolve(), PureWindowsPath(windows)
if not Path(filelist).is_file():
    sys.exit("Release build manifest missing. Run scripts/update-companion.ps1 first.")
names = set()
for line in Path(filelist).read_text(encoding="utf-8-sig").splitlines():
    try: rel = PureWindowsPath(line).relative_to(windows)
    except ValueError: continue  # MSBuild also records obj and historical output locations
    if any(p in (".", "..") or ":" in p for p in rel.parts): sys.exit("Invalid build manifest path")
    if "managed-profile" in rel.as_posix().lower().split("/") or rel.suffix.lower() in (".log", ".zip"): continue
    names.add(rel.as_posix())
required = {"OpenClaw.Node.exe", "OpenClaw.Node.dll", "OpenClaw.Node.runtimeconfig.json", "OpenClaw.Node.deps.json",
            "runtimes/browser/node/node.exe", "runtimes/browser/chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js",
            "runtimes/browser/companion-screenshot.mjs", "runtimes/browser/versions.json"}
if not required <= names: sys.exit("Incomplete Release manifest: " + ", ".join(sorted(required-names)))
manifest = {}
with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=1) as z:
    for name in sorted(names):
        path = (root / name).resolve(strict=True)
        path.relative_to(root)
        # Hash the very bytes we ship, not a second read of a potentially changing build.
        digest = hashlib.sha256()
        with path.open("rb") as src, z.open(name, "w") as dst:
            while data := src.read(1024 * 1024):
                digest.update(data)
                dst.write(data)
        manifest[name] = digest.hexdigest()
    receipt = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode("utf-8")
    z.writestr(".companion-deploy.json", receipt)
print(hashlib.sha256(receipt).hexdigest())
print(f"Release: {len(names)} build files, {Path(archive).stat().st_size // (1024 * 1024)} MiB zipped", file=sys.stderr)
PY
)
echo "Payload SHA-256: $digest"
if ((dry)); then echo "Dry run: no SSH connection, upload or restart."; exit 0; fi
id=$(python3 -c 'import uuid; print(uuid.uuid4().hex)')
ziphash=$(sha256sum "$work/release.zip" | cut -d' ' -f1)
opts=(-o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10)
if [[ -n $key ]]; then opts+=(-i "$key" -o IdentitiesOnly=yes); fi
# Stream PowerShell over stdin instead of nesting Bash, SSH server-shell and PowerShell quoting.
boot=$(printf '%s' 'try { & ([scriptblock]::Create([Console]::In.ReadToEnd())); exit 0 } catch { [Console]::Error.WriteLine($_); exit 1 }' | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\r\n')
remote() {
  { printf "\$mode='%s'; \$id='%s'; \$digest='%s'; \$ziphash='%s'; \$force=%s\n" "$1" "$id" "$digest" "$ziphash" "$force"
    cat <<'POWERSHELL'
$ErrorActionPreference = 'Stop'
$target = Join-Path $HOME 'OpenClawNode'
$exe = Join-Path $target 'OpenClaw.Node.exe'
$receipt = '.companion-deploy.json'
$zip = Join-Path $HOME "OpenClawNode-$id.zip"
$stage = Join-Path $HOME ".OpenClawNode-$id"
function Assert-PlainPath($path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing linked path: $path" }
}
function Below($dir, $relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '[:\\]|(^|/)\.{1,2}(/|$)|//') { throw "Invalid payload path: $relative" }
    $full = [IO.Path]::GetFullPath((Join-Path $dir $relative))
    if (!$full.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Outside deployment: $full" }
    for ($p = $full; $p -ne $dir; $p = [IO.Path]::GetDirectoryName($p)) { Assert-PlainPath $p }
    return $full
}
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Installed {
    foreach ($p in (Get-Process -Name OpenClaw.Node -ErrorAction SilentlyContinue)) {
        if (!$p.Path) { throw "Cannot inspect companion PID $($p.Id); check SSH account privileges." }
        if ($p.Path -eq $exe) { $p }
    }
}
function Running { Installed | Where-Object SessionId -gt 0 }
function Start-Tray {
    if (Running) { return }
    # An SSH session is not the user's interactive desktop. Launch there or report failure.
    $task = "OpenClawDeploy-$id"
    $action = New-ScheduledTaskAction -Execute $exe -Argument '--tray' -WorkingDirectory $target
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)) | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        for ($i=0; $i -lt 20 -and !(Running); $i++) { Start-Sleep -Milliseconds 500 }
    } finally { Unregister-ScheduledTask -TaskName $task -Confirm:$false }
    Start-Sleep -Seconds 1
    if (!(Running)) { throw 'Tray did not stay running in an interactive session. Is the SSH user logged in on the destination host?' }
}
function Invoke-Deployment {
Assert-PlainPath $target
$marker = Below $target $receipt
if ($mode -eq 'check') {
    if (!$force -and !(Installed | Where-Object SessionId -eq 0) -and (Test-Path -LiteralPath $marker -PathType Leaf) -and (Hash $marker) -eq $digest) {
        $matches = $true
        foreach ($entry in (Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json).PSObject.Properties) {
            $file = Below $target $entry.Name
            if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Hash $file) -ne $entry.Value) { $matches = $false; break }
        }
        if ($matches) { Start-Tray; Write-Output 'CURRENT'; return }
    }
    Write-Output 'UPLOAD'; return
}
if ($stage -ne (Join-Path ([IO.Path]::GetFullPath($HOME)) ".OpenClawNode-$id") -or (Test-Path -LiteralPath $stage)) { throw 'Unsafe or occupied staging directory' }
New-Item -ItemType Directory -Path $stage | Out-Null
$payload = Join-Path $stage 'payload'
$backup = Join-Path $stage 'previous'
$changed = [Collections.Generic.List[string]]::new()
$wasRunning = $false
$keepBackup = $false
try {
    Assert-PlainPath $zip
    if ((Hash $zip) -ne $ziphash) { throw 'Uploaded ZIP hash mismatch' }
    Expand-Archive -LiteralPath $zip -DestinationPath $payload
    $manifest = Below $payload $receipt
    if ((Hash $manifest) -ne $digest) { throw 'Uploaded manifest hash mismatch' }
    $files = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).PSObject.Properties
    $install = @(
        foreach ($entry in $files) {
            if ((Hash (Below $payload $entry.Name)) -ne $entry.Value) { throw "Bad uploaded file: $($entry.Name)" }
            $dest = Below $target $entry.Name
            if (!(Test-Path -LiteralPath $dest -PathType Leaf) -or (Hash $dest) -ne $entry.Value) { $entry.Name }
        }
        $receipt
    )
    $running = @(Installed)
    $wasRunning = $running.Count -gt 0
    $worker = Below $target 'runtimes/browser/node/node.exe'
    foreach ($p in @($running) + @(Get-Process node -ErrorAction SilentlyContinue | Where-Object Path -eq $worker)) {
        Stop-Process -InputObject $p -Force
        if (!$p.WaitForExit(10000)) { throw "Could not stop PID $($p.Id)" }
    }
    foreach ($name in $install) {
        $src = Below $payload $name
        $dest = Below $target $name
        $old = Below $backup $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
        if (Test-Path -LiteralPath $dest -PathType Leaf) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($old)) | Out-Null
            [IO.File]::Replace($src, $dest, $old)
        } else { [IO.File]::Move($src, $dest) }
        $changed.Add($name)
    }
    Start-Tray
    Write-Output "Running: $exe (PID $((Running).Id))"
} catch {
    $failure = $_
    $keepBackup = $true
    for ($i=$changed.Count-1; $i -ge 0; $i--) {
        $dest = Below $target $changed[$i]; $old = Below $backup $changed[$i]
        if (Test-Path -LiteralPath $old) { [IO.File]::Copy($old, $dest, $true) } else { [IO.File]::Delete($dest) }
    }
    if ($wasRunning -and !(Running)) { Start-Tray }
    $keepBackup = $false
    throw $failure
} finally {
    if (!$keepBackup) { Assert-PlainPath $stage; Remove-Item -LiteralPath $stage -Recurse -Force }
    else { Write-Warning "Rollback incomplete; preserved backup at $backup" }
    Remove-Item -LiteralPath $zip -Force
}
}
# Concurrent SSH sessions must not interleave replacement and rollback on the same install.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$lock = [Threading.Mutex]::new($false, "Global\OpenClawNodeDeploy-$sid")
$held = $false
try {
    try { $held = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (!$held) { throw 'Another deployment is active on the destination host.' }
    Invoke-Deployment
} finally {
    if ($held) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
POWERSHELL
  } | ssh "${opts[@]}" "$host" "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $boot"
}
state=$(remote check | tr -d '\r')
case "$state" in
  CURRENT) echo "Destination already matches and the tray is running."; exit 0 ;;
  UPLOAD) ;;
  *) echo "Unexpected remote preflight response: $state" >&2; exit 1 ;;
esac
scp "${opts[@]}" "$work/release.zip" "$host:OpenClawNode-$id.zip"
remote install
