# Bundled browser runtime

MSBuild copies this directory into `runtimes/browser/`. The companion launches only its bundled `node/node.exe` and `chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js`. It does not install packages, use system Node, or download code on app launch.

The MCP package, CLI entry points, and companion screenshot adapter are tracked. The large Windows x64 Node executable is not. The [local updater](../../../scripts/README.md) restores it at build time when needed and verifies its SHA-256 against `versions.json`.

When changing runtime versions, verify the pinned MCP tarball against its npm SHA-512 integrity, apply the corresponding Gateway snapshot patch from `../browser-runtime-patches/` followed by `windows-capture.patch`, and update the vendored package and `versions.json` together. Take the Windows Node executable hash from the pinned release's official `SHASUMS256.txt` (`win-x64/node.exe`). Run the opt-in isolated browser tests before shipping. Never put browser profiles in this source directory.
