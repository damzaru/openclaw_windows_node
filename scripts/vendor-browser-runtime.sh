#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET_DIR="$ROOT_DIR/src/OpenClaw.Node/browser-runtime"
NODE_VERSION="${NODE_VERSION:-v24.14.0}"
MCP_VERSION="${MCP_VERSION:-0.20.0}"
NODE_DIST="node-${NODE_VERSION}-win-x64"
NODE_ZIP_URL="https://nodejs.org/dist/${NODE_VERSION}/${NODE_DIST}.zip"
TMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

mkdir -p "$TARGET_DIR"
rm -rf "$TARGET_DIR/node" "$TARGET_DIR/chrome-devtools-mcp"
mkdir -p "$TARGET_DIR/node" "$TARGET_DIR/chrome-devtools-mcp"

echo "[vendor-browser-runtime] target=$TARGET_DIR"
echo "[vendor-browser-runtime] node_version=$NODE_VERSION mcp_version=$MCP_VERSION"

echo "[1/4] Downloading Windows Node runtime..."
curl -fsSL "$NODE_ZIP_URL" -o "$TMP_DIR/node.zip"
python3 - <<'PY' "$TMP_DIR/node.zip" "$TMP_DIR/unzip"
import sys, zipfile, pathlib
zip_path = pathlib.Path(sys.argv[1])
out_dir = pathlib.Path(sys.argv[2])
out_dir.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(zip_path) as zf:
    zf.extractall(out_dir)
PY
cp "$TMP_DIR/unzip/$NODE_DIST/node.exe" "$TARGET_DIR/node/node.exe"
cp "$TMP_DIR/unzip/$NODE_DIST/LICENSE" "$TARGET_DIR/node/LICENSE"
if [ -f "$TMP_DIR/unzip/$NODE_DIST/README.md" ]; then
  cp "$TMP_DIR/unzip/$NODE_DIST/README.md" "$TARGET_DIR/node/README.md"
fi

echo "[2/4] Fetching chrome-devtools-mcp package tarball..."
pushd "$TMP_DIR" >/dev/null
npm pack "chrome-devtools-mcp@${MCP_VERSION}" >/tmp/openclaw_chrome_mcp_pack.txt
TARBALL="$(cat /tmp/openclaw_chrome_mcp_pack.txt)"
tar -xzf "$TARBALL"
popd >/dev/null

PACKAGE_SRC="$TMP_DIR/package"
PACKAGE_DST="$TARGET_DIR/chrome-devtools-mcp"
cp -R "$PACKAGE_SRC/"* "$PACKAGE_DST/"

if [ ! -f "$PACKAGE_DST/package.json" ]; then
  echo "[vendor-browser-runtime] package.json missing after extraction" >&2
  exit 1
fi

echo "[3/4] Installing production dependencies for chrome-devtools-mcp..."
pushd "$PACKAGE_DST" >/dev/null
npm install --omit=dev --ignore-scripts --no-audit --no-fund
popd >/dev/null

echo "[4/4] Writing pinned version manifest..."
cat > "$TARGET_DIR/versions.json" <<JSON
{
  "nodeVersion": "${NODE_VERSION}",
  "chromeDevtoolsMcpVersion": "${MCP_VERSION}"
}
JSON

echo "[vendor-browser-runtime] done"
find "$TARGET_DIR" -maxdepth 3 \( -type f -o -type l \) | sed "s#^$ROOT_DIR/##" | sort
