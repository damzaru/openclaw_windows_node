# Bundled browser runtime

This directory is copied to the published node output as:

`runtimes/browser/`

Expected packaged layout on the target machine:

```text
runtimes/browser/
  node/
    node.exe
    LICENSE
    README.md
  chrome-devtools-mcp/
    build/
      src/
        bin/
          chrome-devtools-mcp.js
    package.json
    package-lock.json
    README.md
    LICENSE
  versions.json
```

Rules:
- Do not depend on system `node`, `npm`, or `npx`.
- Do not download runtime dependencies on first launch.
- Launch the browser backend only through bundled absolute paths.
- Keep versions pinned and reproducible.

The Windows node app resolves these files at runtime via `BundledBrowserRuntimeLocator`.

## Refreshing the bundle

From the repo root:

```bash
./scripts/vendor-browser-runtime.sh
```

Optional version overrides:

```bash
NODE_VERSION=v24.14.0 MCP_VERSION=0.20.0 ./scripts/vendor-browser-runtime.sh
```
