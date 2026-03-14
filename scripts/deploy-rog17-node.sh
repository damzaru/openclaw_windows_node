#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$REPO_ROOT/src/OpenClaw.Node/bin/x64/Debug/net8.0-windows"
SSH_HOST="${SSH_HOST:-david@192.168.1.250}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/id_ed25519_openclaw}"
REMOTE_ZIP_WIN='C:\Users\David\OpenClawNode-net8.0-windows.zip'
REMOTE_TARGET_WIN='C:\Users\David\OpenClawNode'
REMOTE_ZIP_UNIX='~/OpenClawNode-net8.0-windows.zip'
FORCE=0

usage() {
  cat <<EOF
Usage: $(basename "$0") [--host user@host] [--ssh-key /path/to/key] [--force]

Canonical ROG17 deploy script.

Rules:
- deploys ONLY from the canonical build output:
  $BUILD_DIR
- refuses to build or pick alternate output directories
- compares local vs remote OpenClaw.Node.exe SHA256 and skips deploy when identical unless --force is used
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host)
      SSH_HOST="$2"; shift 2 ;;
    --ssh-key)
      SSH_KEY="$2"; shift 2 ;;
    --force)
      FORCE=1; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      echo "Unknown arg: $1" >&2
      usage >&2
      exit 1 ;;
  esac
done

if [[ ! -d "$BUILD_DIR" ]]; then
  echo "Canonical build directory missing: $BUILD_DIR" >&2
  echo "Run scripts/node-reload.ps1 first. This script will not build into alternate output paths." >&2
  exit 1
fi

LOCAL_EXE="$BUILD_DIR/OpenClaw.Node.exe"
LOCAL_DLL="$BUILD_DIR/OpenClaw.Node.dll"
if [[ ! -f "$LOCAL_EXE" ]]; then
  echo "Canonical build exe missing: $LOCAL_EXE" >&2
  exit 1
fi
if [[ ! -f "$LOCAL_DLL" ]]; then
  echo "Canonical build dll missing: $LOCAL_DLL" >&2
  exit 1
fi

BUILD_LABEL=$(python3 - <<'PY' "$REPO_ROOT/src/OpenClaw.Node/BuildInfo.cs"
import re, sys, pathlib
text = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')
m = re.search(r'BuildVersion\s*=\s*"(b\d{4})"', text)
print(m.group(1) if m else "unknown")
PY
)

LOCAL_DLL_HASH=$(sha256sum "$LOCAL_DLL" | awk '{print $1}')
echo "[deploy-rog17] source build label: $BUILD_LABEL"
echo "[deploy-rog17] local dll: $LOCAL_DLL"
echo "[deploy-rog17] local dll sha256: $LOCAL_DLL_HASH"

REMOTE_DLL_HASH=$(ssh -i "$SSH_KEY" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no "$SSH_HOST" \
  "powershell -NoProfile -Command \"if (Test-Path '$REMOTE_TARGET_WIN\\OpenClaw.Node.dll') { (Get-FileHash -Algorithm SHA256 '$REMOTE_TARGET_WIN\\OpenClaw.Node.dll').Hash.ToLower() }\"" 2>/dev/null | tr -d '\r' | tail -n 1 || true)

if [[ -n "$REMOTE_DLL_HASH" ]]; then
  echo "[deploy-rog17] remote dll sha256: $REMOTE_DLL_HASH"
fi

if [[ $FORCE -ne 1 && -n "$REMOTE_DLL_HASH" && "${REMOTE_DLL_HASH,,}" == "${LOCAL_DLL_HASH,,}" ]]; then
  echo "[deploy-rog17] remote build already matches local canonical build. Skipping deploy."
  exit 0
fi

ZIP_LOCAL="/tmp/OpenClawNode-net8.0-windows.zip"
rm -f "$ZIP_LOCAL"
python3 - <<'PY' "$BUILD_DIR" "$ZIP_LOCAL"
import os, sys, zipfile
src, zip_path = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            arc = os.path.relpath(p, src)
            z.write(p, arc)
print(zip_path)
PY

echo "[deploy-rog17] uploading canonical build zip..."
scp -i "$SSH_KEY" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no "$ZIP_LOCAL" "$SSH_HOST:$REMOTE_ZIP_UNIX"

echo "[deploy-rog17] extracting + restarting on remote host..."
ssh -i "$SSH_KEY" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no "$SSH_HOST" 'powershell -NoProfile -ExecutionPolicy Bypass -Command "
$ErrorActionPreference = ''Stop''
$zip = '''"$REMOTE_ZIP_WIN"'''
$target = '''"$REMOTE_TARGET_WIN"'''

$procs = Get-CimInstance Win32_Process | Where-Object {
  ($_.Name -match ''OpenClaw\.Node(\.exe)?$'') -or
  ($_.Name -eq ''dotnet.exe'' -and $_.CommandLine -match ''OpenClaw\.Node'')
}
$stopped = 0
foreach($p in $procs){
  try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; $stopped++ } catch {}
}
Write-Output (''STOPPED='' + $stopped)

if(-not (Test-Path $target)){ New-Item -ItemType Directory -Path $target -Force | Out-Null }
Get-ChildItem -Path $target -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip -DestinationPath $target -Force
Write-Output ''COPIED=YES''

$exe = Join-Path $target ''OpenClaw.Node.exe''
if(-not (Test-Path $exe)){ throw ''OpenClaw.Node.exe missing after extract'' }

$tn = ''OpenClawNodeElevatedTemp''
$start = (Get-Date).AddMinutes(1).ToString(''HH:mm'')
try {
  schtasks /Create /TN $tn /TR (''"'' + $exe + ''"'') /SC ONCE /ST $start /RL HIGHEST /F | Out-Null
  schtasks /Run /TN $tn | Out-Null
  Start-Sleep -Seconds 3
  schtasks /Delete /TN $tn /F | Out-Null
  Write-Output ''START=ELEVATED_TASK''
} catch {
  Write-Output (''ELEVATED_START_FAILED='' + $_.Exception.Message)
}

$r = Get-CimInstance Win32_Process | Where-Object { $_.Name -match ''OpenClaw\.Node(\.exe)?$'' }
if(-not $r){
  Start-Process -FilePath $exe -WorkingDirectory $target
  Start-Sleep -Seconds 2
  $r = Get-CimInstance Win32_Process | Where-Object { $_.Name -match ''OpenClaw\.Node(\.exe)?$'' }
  Write-Output ''START=FALLBACK_NORMAL''
}

if($r){
  Write-Output ''RUNNING=YES''
  $r | Select-Object -First 3 ProcessId, Name | ForEach-Object { Write-Output (''PID='' + $_.ProcessId + '' NAME='' + $_.Name) }
} else {
  Write-Output ''RUNNING=NO''
}
"'
