# Temporary OpenClaw snapshot identity patch

This package remains `chrome-devtools-mcp@1.12.0`. OpenClaw modifies two
published runtime files to backport the snapshot identity repair proposed in
https://github.com/ChromeDevTools/chrome-devtools-mcp/pull/2788.

The reviewed upstream source is commit
`06c8d4bc44f68bde8bd3fcd97dcc47375df81fe9`, forward-ported onto the
`chrome-devtools-mcp-v1.12.0` source tag at
`af2834411a564d366f9cab63efb046d9084f4dba`.

- `TextSnapshot.js` carries the reviewed snapshot-owner implementation.
- `McpPage.js` supplies the known element frame for DOM-result reverse lookup
  and resolves CSS-style UIDs through the same guarded snapshot handle. CSS
  lookup stays within the captured renderer and fails closed when the embedded
  DevTools target cannot represent a cross-renderer frame.

Version 1.12.0 already exports its bundled `CdpFrame` implementation, so this
patch does not modify third-party bundle code.

The original package is the published npm 1.12.0 tarball with integrity
`sha512-lAGpH0zXT1ZdaPefP1KK/MvxwihOlu7VGQ7p4wbhD9OhN17vUC4KWk/ImUYR9DE4s+7r4r0ySV4xr2OkTa7ZMA==`.
The original `LICENSE` and `build/src/third_party/THIRD_PARTY_NOTICES` remain
unchanged.

Remove this patch and notice after adopting a published upstream release that
contains the repair and passes the same snapshot/stdio regression proof.
