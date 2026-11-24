#!/usr/bin/env bash
set -euo pipefail

# The Windows-native implementation verifies artifacts and applies the pinned patch.
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v pwsh.exe >/dev/null 2>&1; then PS=pwsh.exe
elif command -v powershell.exe >/dev/null 2>&1; then PS=powershell.exe
else echo "Run scripts/vendor-browser-runtime.ps1 on Windows." >&2; exit 1; fi
if command -v wslpath >/dev/null 2>&1; then WIN_ROOT="$(wslpath -w "$ROOT_DIR")"
elif command -v cygpath >/dev/null 2>&1; then WIN_ROOT="$(cygpath -w "$ROOT_DIR")"
else WIN_ROOT="$ROOT_DIR"; fi
exec "$PS" -NoProfile -ExecutionPolicy Bypass -File "$WIN_ROOT/scripts/vendor-browser-runtime.ps1" -RepoPath "$WIN_ROOT" -NodeVersion "${NODE_VERSION:-v24.14.0}" -McpVersion "${MCP_VERSION:-1.12.0}"
