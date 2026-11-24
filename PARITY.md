# Gateway compatibility — 2026-11-24

Current reference: Gateway **2026.11.8**, master `92d4728c1f9be71cd96918619d3777368bbda257`. This targeted sync leaves Gateway source/configuration untouched.

- Browser screenshots bind to the native tab ID, including when two tabs have identical URLs. Background captures do not activate tabs. Viewport, full-page, snapshot-ref and labeled captures return bounded PNG/JPEG files. Use a snapshot ref for an element; the existing-session driver does not accept CSS selectors.
- Pairing and permission-upgrade failures direct the user to the Gateway Control UI under **Settings > Devices**. Approval remains on the Gateway.

## 2026-11-17: Profile/cache and version follow-up

- App/product/Gateway-client version is plain **2026.11.5**, with a single `Version: 2026.11.5` tray row. Reference source provenance and internal build details are diagnostics-only. The reference checkout still declares 2026.11.4; no newer commit is claimed or substituted.
- Browser profiles now live under `%LOCALAPPDATA%\OpenClaw Companion\browser\managed-profile`, outside executable/build payloads. Migration is offline, atomic on the same volume and refuses nonempty destination conflicts or unrecognized source directories.
- Migration verification checked that persistent profile files and extensions survived relocation and cache maintenance.
- Configurable 128/64 MiB disk/code cache defaults, a Chrome disk-cache target and offline startup/minutely maintenance prevent unchecked cache growth between browser sessions. GPU/shader containers have a 32 MiB offline limit. Clear Browser Cache is a confirmation-protected tray action restricted to known cache directories. Active/unknown browser ownership and linked filesystem entries fail closed; persistent storage is not part of cache deletion.
- Native settings-layout/permission tests: **10 passed**, including scrolling/action visibility at multiple sizes/scales. Release rebuild remains in the original shortcut target folder with zero warnings/errors.
- Final non-live suite: **316 passed**, including the opt-in isolated real-browser acceptance and the exact read-only checkout's generated validators. Live Gateway accepted the rebuilt app as `2026.11.5` on both node and operator connections. Gateway source/configuration and Windows mouse settings were not changed.

## November lifecycle/runtime fixes

- Passive status, profile and tab-list requests never launch a browser or initialize MCP. Target-specific requests also attach only to an existing target; explicit open/navigation-without-target retains startup support.
- Legacy close treats an actual target 404 or loopback connection refusal as already closed. Owned close reports `missing` when the browser endpoint has gone away. Timeouts, authentication/server errors and other transport failures retain their real error state; no replacement browser is launched. HTTP statuses survive the `browser-v1` error envelope.
- Managed launch uses a uniquely marked startup page bound to that browser instance and waits for startup navigation to finish. The first open consumes that page once, including concurrent opens, without leaving an extra blank tab. Ordinary user blank tabs and startup pages subsequently navigated by the user are preserved.
- Newly opened tabs carry durable profile/browser-instance ownership. The owned-close endpoint verifies identity and performs lookup/close on one browser-level CDP connection; missing targets and replaced browser instances retire safely.
- Browser runtime migrated from Chrome DevTools MCP 0.20.0 to **1.12.0**, with the exact Gateway snapshot-identity patch and recorded npm integrity/patch hash. The supported bundled Node version remains v24.14.0. Updated page-routing flags, upload `filePaths`, scoped filesystem roots, telemetry opt-outs and browser-instance session retirement.
- Vendored output is replaced during builds, independently of source timestamps. Managed browser profiles are never removed by the runtime refresh.
- File-transfer MIME sniffing completes a UTF-8 sequence split by the sampling boundary instead of incorrectly labelling valid text as binary.
- New regressions reproduce the original startup/MIME bugs. Isolated real-browser acceptance exercises two-tab routing, snapshot, fill/click, staged upload, owned/legacy close, missing targets and offline passive/cleanup requests. It uses a disposable headless Chrome, not the user's profile or visible windows.

Other optional gaps listed below remain; this update does not claim full Playwright/operator-panel, generic MCP, worker or workspace-runtime parity.

### November verification

- **304 tests passed**, including the opt-in real-browser runtime/upload acceptance and current Gateway validator reference check. Tests requiring a separate live-Gateway fixture were excluded. New tests cover closed-browser success, preserved HTTP errors/cancellation, one-use concurrent startup-tab reuse and preservation of user tabs.
- The x64 Windows Release build completed with **zero warnings and errors** in the original `src/OpenClaw.Node/bin/x64/Release/net8.0-windows/` folder. Windows product metadata and `--version` report `2026.11.4` with the exact reference commit/date; the follow-up lifecycle fix is companion revision **1** (`FileVersion 2026.11.4.1`).
- Both node and operator-sidecar connections were accepted on protocol 4 after restart.
- Live Gateway browser proxy checks with the browser closed returned `running: false`, success for repeated legacy close requests, and `missing` for owned close. The managed Chrome process count stayed at zero and no MCP helper was launched.
- Gateway source remains clean at the reference commit. Gateway configuration, user browser profiles and Windows mouse settings were not changed.

## Previous audit — 2026-10-31

Reference: Gateway **2026.10.9**, master commit `18516599b1d417fee621b2fd2ceda21107bc038a`, compared with the preceding `2026.9.9` reference `80239a7e6b2ab05770f1b42aa4e12637fde1313a`. Gateway source was inspected read-only.

## Compatibility changes implemented

| Area | Finding and resolution |
| --- | --- |
| Computer discovery | Current Gateway filters actions by `computerUse.actions`. Added the implemented `screenshot` action, which also enables Gateway-owned `wait`. Native actions remain a truthful foreground screen-only subset. |
| Left-handed input | Physical left-button injection became a secondary click when Windows mouse buttons are swapped. Read `SM_SWAPBUTTON` and map primary/secondary actions correctly for clicks, multi-clicks, split drags and drag cleanup. Do not modify Windows settings. |
| Coordinate scaling | Initialize Per-Monitor V2 DPI awareness before creating windows so capture geometry and injected coordinates share physical pixels. Display topology/reference-width checks remain enforced. |
| Execution lifecycle | Handle Gateway `__close_execution`, release that execution's held input and invalidate its frames without releasing another execution's drag. Bind captured frame IDs to the requesting execution. |
| Keyboard vocabulary | Accept common Gateway/provider aliases (`cmd`, `Control_L/R`, `Shift_L/R`, `Insert`, `PLUS`, etc.), including extended-key flags for right modifiers. |
| File-write preflight | Current Gateway rejects hard-link-safe workspace writes unless preflight acknowledges `rejectHardlinks: true`. The companion already refuses all hard-linked write targets; it now returns that acknowledgement. This does not add full workspace-service support. |
| Error transport | Preserve the Gateway's additive native-permission remediation `error.details` rather than dropping it from `node.invoke.result`. |
| Build identity | Match Gateway package version in the tray, connect metadata, IPC, device info and Windows product metadata. Store exact upstream commit, sync date and companion revision separately in `Version.props`. Update version/preflight/deployment helpers. |

## Other advertised surfaces reviewed

The current protocol remains v4 (native node minimum v3), and existing device-auth v3 signing and role-scoped token handling remain applicable. File fetch/create START/ACK and framed duplex contracts, directory transfer bindings, desktop-stream ticket/RFB contracts, browser proxy/upload commands, and the existing Canvas/A2UI, execution, camera, device, location and push-to-talk surfaces were checked against their current owners. No additional breaking wire change was identified for the implemented subset. Existing focused regressions remain part of the full suite; this is not a claim that every peripheral or optional route received live acceptance.

## Remaining optional parity gaps

| Area | Not implemented by this companion |
| --- | --- |
| Rich computer providers | Window/application discovery and lifecycle, accessibility trees and element references, background delivery, CUA browser/recording/resource families. The source's optional `cua-computer` provider is not bundled here. |
| Workspace services | `workspace.memory` and `workspace.skills`; generic file transfer is not the complete node-local workspace/session runtime. |
| Dynamic runtimes | Generic MCP/plugin node commands, Claude CLI/session catalogs, Codex exec-server and provider-owned interactive PTY terminals. |
| Managed environments | `worker.*` installation/supervision, environment workspaces, cloud-worker desktops and portal streaming. These are distinct from foreground desktop control of this PC. |
| Approval UX | Local interactive execution-approval queue and portable approval snapshots/file pinning; unsupported approval modes still fail closed. |
| Setup and app bridges | One-paste join-code onboarding, Cloudflare Access edge credentials, newer native-device settings bridges, embedded chat/dashboard, installer/updater and ARM64 packaging. |
| Desktop sharing | Existing ticketed RFB bridge still requires an authenticated loopback VNC server. AnyDesk is not an RFB server. |

These features are deliberately not advertised. Mobile personal-data capabilities are not Windows parity requirements.

## Verification

- Regression tests reproduced the missing screenshot declaration, rejected execution cleanup and key aliases before the fix.
- Opt-in reference check runs the current Gateway's generated computer-use validators against companion descriptor/result payloads, without building or changing Gateway source.
- Disposable native Windows acceptance exercises capture → logical primary click → Ctrl+A → Unicode text entry/replacement with Windows mouse buttons swapped. Input aborts if another application owns foreground focus.
- Release build is kept at `src/OpenClaw.Node/bin/x64/Release/net8.0-windows/` to preserve shortcuts.
- Final non-live suite: **276 passed**, including the opt-in current-Gateway validator check; native disposable-window acceptance: **1 passed**. Release build: zero warnings/errors.
- Live protocol-4 verification covered node and operator-sidecar connections, screenshot advertisement, and a complete `screen.snapshot` / `__close_execution` round trip.
- Real camera/location/microphone operation, every browser route and a complete installed-VNC viewer session are not covered by that native desktop test.
