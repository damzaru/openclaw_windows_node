# OpenClaw Windows Companion

A Windows tray companion for the OpenClaw Gateway. App version `2026.11.8` targets Gateway `2026.11.8` and negotiates node protocol versions 3 through 4. The read-only reference checkout inspected on **2026-11-24** is master `92d4728c1f9be71cd96918619d3777368bbda257` (package `2026.11.8`).

The app, connection, device information, tray and Windows product metadata use one Gateway version: `Version: 2026.11.8`. On a Gateway sync, update the version, source package version, exact commit and sync date in `src/OpenClaw.Node/Version.props`. A normal rebuild uses those committed values unchanged; build details stay in diagnostics and logs.

See [PARITY.md](PARITY.md) for this sync's compatibility findings and remaining optional gaps.

The Gateway source tree is a reference only. This repository does not modify it.

## Install or update on Windows x64

Download **OpenClawCompanion-Setup.exe** and its matching Sigstore bundle from this repository's [latest GitHub release](../../releases/latest). Verify Setup using the command below, then run it as the Windows user who will use the companion. Open **OpenClaw Companion** from Start if the tray has not already opened. The installer includes .NET and the pinned browser helper; installing from a release does not require Git, an SDK, SSH, Python, or a repository token.

Use the release installer on each Windows PC, then update from that PC's tray. There is no remote deployment script. A Git checkout is only needed to build or develop the companion.

To migrate an existing node without pairing it again:

1. Exit the old tray and run Setup under the **same Windows account**, keeping any custom state-directory configuration. Leave the existing settings, identity, browser profile, and checkout in place.
2. Open **OpenClaw Companion** from Start. Confirm the node reconnects and **Copy Diagnostics → windowsPackage** shows a package number rather than `source`.
3. Repoint existing shortcuts to the installed launcher. With the default installation, that is `%LOCALAPPDATA%\OpenClawCompanion\OpenClaw Companion.exe`. If **Start with Windows** was enabled, save Settings from the installed tray. Disable any old scheduled launch for the checkout so only one copy starts.

If the installed copy does not reconnect, exit it before restarting the old copy. A change of install location does not require deleting credentials or generating a new identity.

Choose **Check for updates...** from the installed tray. After the download is verified, choose **Yes** to restart and briefly disconnect, or **No** to keep working. Background checks can stage updates but do not restart the app. **Copy Diagnostics → windowsPackage** identifies the installed package without adding another version to the tray. A [development checkout](scripts/README.md) reports `source`.

Each release includes Sigstore signatures for Setup and the update feed; these are not Windows Authenticode signatures. For initial installation, obtain Setup only from this repository's release page and verify its provenance before running it. With Cosign installed, download the matching `OpenClawCompanion-Setup.exe.sigstore.json` and run this in the download folder (substitute the public repository URL, without a trailing slash):

```powershell
$repo = 'https://github.com/OWNER/REPOSITORY'
cosign verify-blob --bundle OpenClawCompanion-Setup.exe.sigstore.json --certificate-oidc-issuer https://token.actions.githubusercontent.com --certificate-identity "$repo/.github/workflows/windows-release.yml@refs/heads/main" OpenClawCompanion-Setup.exe
```

Subsequent tray updates verify the signed feed against that release workflow and check the downloaded full package's size and SHA-256 before applying it. A failed verification leaves the running version in place.

## Current functionality

- Protocol 3–4 node WebSocket sessions with signed device-auth v3 payloads, plus protocol-v4 operator sidecars
- Shared-token enrollment and per-Gateway, per-device, per-role device-token reconnects
- Bounded frames, handshake/request timeouts, tick monitoring, reconnect jitter, and optional TLS SHA-256 pinning
- Concurrent node invokes with timeouts, cancellation, ordered input, progress chunks, idempotency replay, and exactly one terminal result
- Host-native Windows execution approvals with optimistic concurrency and fail-closed defaults
- Device information/status, Windows location, screen snapshots/recording, camera capture, Canvas/A2UI, browser proxy, and Talk push-to-talk
- Gateway-owned terminal/browser uploads, current host/resource and input-idle telemetry
- Opt-in native File Transfer reads/writes, metadata, paginated listings, bounded TAR/GZIP archives, and acknowledged binary streams
- Explicitly enabled, screen-only Computer Use v2 input and authenticated loopback-VNC desktop sharing
- Same-user authenticated Named Pipe compatibility surface for legacy window/input automation
- Tray settings, diagnostics, onboarding, restart/exit, and optional start-at-login
- DPAPI-protected companion settings, device identity, device tokens, and IPC credential

## Gateway command surface

The companion uses one registry for both its connect manifest and command dispatch. A handler is not Gateway-reachable unless the registry advertises it.

Enabled by default:

- `system.run.prepare`, `system.run`, `system.which`
- `system.execApprovals.get`, `system.execApprovals.set`
- `system.notify`, `fs.listDir`, `terminal.upload`
- `browser.proxy`, `browser.proxy.upload.v1` (both require Browser proxy enabled)
- `screen.snapshot`
- `camera.list`
- `device.info`, `device.status`
- `canvas.present`, `canvas.hide`, `canvas.navigate`, `canvas.eval`, `canvas.snapshot`
- `canvas.a2ui.push`, `canvas.a2ui.pushJSONL`, `canvas.a2ui.reset`

Explicit opt-in in Settings:

- `screen.record`
- `camera.snap`, `camera.clip`
- `location.get`
- `talk.ptt.start`, `talk.ptt.stop`, `talk.ptt.cancel`, `talk.ptt.once`
- `computer.act` (foreground input only)
- `desktop.stream` (requires a separately installed authenticated loopback VNC server)
- `file.stat`, `file.fetch`, `dir.list`, `dir.fetch` (Read files and directories)
- `file.write`, `file.create` (Create and overwrite files)

The companion does not advertise optional MCP, agent CLI, or private worker/runner commands, because it does not ship those runtimes. Gateway command allowlisting is still required for opt-in capabilities; a local setting does not grant Gateway authority.

Legacy commands such as `screen.capture`, `screen.list`, `window.*`, `input.*`, `ui.*`, and `system.describe` are not declared to or accepted from the Gateway. Supported compatibility automation is confined to authenticated `ipc.*` methods on the same-user Named Pipe.

## Requirements

- Windows 10 or 11
- .NET 8 SDK to build
- A reachable OpenClaw Gateway with a valid shared token for first enrollment, or a previously stored device token
- `ffmpeg` on `PATH` for camera video clips
- Windows camera, microphone, and location permissions for the corresponding opt-in features

Builds target x64. The Windows UI/tray artifact uses `net8.0-windows`; `net8.0` exists for protocol and service tests.

## Configuration

On Windows, use the tray menu's **Settings...** entry. Saving settings restarts the companion.

Connection values resolve in this order:

1. `--gateway-url` and `--gateway-token`
2. `OPENCLAW_GATEWAY_URL` and `OPENCLAW_GATEWAY_TOKEN`
3. DPAPI-protected companion settings

CLI and environment tokens are transient and are not copied into the secure settings store. The companion no longer reads, creates, edits, or opens `~/.openclaw/openclaw.json`; the old tray menu entry and legacy fallback have been removed. Existing files are left untouched because the OpenClaw CLI may use them. Use companion Settings for persistent connection values. The Gateway source/config is never edited.

After a successful enrollment, the Gateway-issued device token is endpoint-, identity-, and role-bound. Shared credentials are sent as `auth.token`; cached credentials are sent as `auth.deviceToken`, including the bounded, trusted-endpoint retry requested by the Gateway. Device signatures use the challenge timestamp to avoid local clock-skew failures. A cached token is cleared only on `AUTH_DEVICE_TOKEN_MISMATCH`, and only if it still matches the failed attempt. Pairing, scope, identity, and terminal authentication failures pause reconnects and show the Gateway's remediation hint without erasing a usable token.

For a remote `wss://` Gateway, Settings accepts an optional SHA-256 certificate pin. When configured, the presented certificate must match that pin.

Companion-owned data is stored under:

```text
%LOCALAPPDATA%\OpenClaw Companion\
```

Secrets and identities use Windows DPAPI with `CurrentUser` scope. The native execution policy is a non-secret JSON file in the same directory and is written atomically.

## Logs and connection diagnosis

Every launch, including a double-clicked tray build, writes timestamped startup, connection, and error records under `%LOCALAPPDATA%\OpenClaw Companion\logs\companion-*.log`. The tray's **Open Logs** action opens that folder, and copied diagnostics include its path. `OPENCLAW_WINDOWS_HOME` redirects this folder along with other companion-owned data during tests. The old repository `logs/node-watchdog*` files remain launcher logs, not the canonical application logs.

Logs rotate at 4 MiB and retain the ten newest files (active files are not forcibly deleted). Credential fields, registered credentials, URL user-info/query strings, and capability tokens are redacted; browser response-body previews are not logged. Oversized lines are omitted. Logging failures do not stop the application.

For a bounded, non-interactive startup and connection check using the normal saved settings:

```powershell
.\src\OpenClaw.Node\bin\x64\Release\net8.0-windows\OpenClaw.Node.exe --diagnose-connection
```

The check exits after node connection, a transport/authentication failure, or 25 seconds. It does not start local IPC/discovery, show dialogs, or remain running. Exit codes: 0 means node connected; 1 means startup/runtime failure; 2 means missing credentials or Gateway rejection; 3 means transport failure/timeout. A node success does not imply that the optional operator sidecar is paired; check its separately labeled messages for Canvas/Talk authorization. A temporary `--gateway-url` override does not change saved settings.

Transport logs include nested socket/HTTP/TLS errors. If a Gateway has switched to TLS, the old `ws://` URL must be replaced with `wss://`; its hostname must match the certificate unless a separately verified SHA-256 pin is configured. Do not disable certificate validation to work around a name mismatch.

## Execution approvals

`system.run` is deny-by-default. The Windows-native contract returned by `system.execApprovals.get` is:

```json
{
  "enabled": true,
  "hash": "sha256:...",
  "baseHash": "sha256:...",
  "defaultAction": "deny",
  "rules": [],
  "constraints": {
    "baseHashRequired": true,
    "defaultAllowAllowed": false,
    "broadAllowRulesAllowed": false,
    "dangerousAllowRulesAllowed": false
  }
}
```

Updates are full replacements with `defaultAction`, `rules`, and the last observed `baseHash`. Stale writes are rejected. `prompt` fails closed when there is no active local approval UI.

By default, `defaultAction: allow`, wildcard executables, and allow rules for shells/loaders such as `cmd`, PowerShell, `mshta`, or `rundll32` are rejected. To permit these policies, locally enable **Settings → Remote command execution → Allow unrestricted exec policies**, confirm the warning, and save/restart. Reload the node's execution policy in the Gateway, then explicitly set **Allow all** or add the required PowerShell rule. Opting in does not automatically change the current policy or grant Gateway pairing/administrative authority. Commands run with the companion's Windows permissions, including elevation if the companion was launched elevated.

The snapshot's three permission constraints become `true` only while that local opt-in is enabled. Turning it off makes a stored Allow-all default effectively deny and disables broad/shell allow rules without rewriting the stored policy; ordinary maintenance rules remain available. Re-enabling the opt-in reactivates those stored policies, so review them before doing so. The CAS hash includes the local permission ceiling, preventing an old Gateway snapshot from overwriting policy after an opt-in/revocation.

Only a non-empty string array is accepted for `system.run.params.command`. Prepared plans are revalidated before execution, sensitive inherited environment variables are removed, forwarded environment overrides are restricted, stdout/stderr share a 200,000-character cap, and timeout/cancellation kills the process tree.

Prepared plans bind argv, command text, working directory, agent, and session. Absent mutable file operands are omitted, as required by the current Gateway normalizer. Portable file/policy bindings fail closed: this companion uses the separate Windows-native approval provider and does not claim to enforce portable approval snapshots or file pinning.

## Uploads, browser actions, and desktop control

`terminal.upload` accepts `{name, contentBase64}` and returns `{path, size}` for a private local copy. `browser.proxy.upload.v1` accepts the `browser-upload-v1` envelope for `POST /hooks/file-chooser` and supplies newly staged local paths to the browser. Ordinary `browser.proxy` cannot carry uploads. Base64 must be canonical and padded. Limits are 16 MiB for terminal files, 10 MiB per browser file, and 16 MiB aggregate; retained copies expire after 24 hours, with a 256 MiB / 64-upload quota. Filenames are sanitized and filesystem links are not followed. Returned paths are temporary; copy a terminal upload elsewhere if it must be retained.

The optional File Transfer plugin is supported through separate native handlers. The legacy directory picker (`fs.listDir`) and terminal/browser staging commands keep their existing contracts. Gateway authorization preflights always inspect fresh filesystem identity and do not consume the actual operation's idempotency receipt. File-write preflights acknowledge the enforced hard-link rejection required by the current Gateway's workspace-write policy.

File Transfer setup:

1. In companion **Settings → File Transfer**, enable **Read files and directories** and/or **Create and overwrite files**, confirm the local warning, and save/restart. Both are off by default and independently gate the command manifest and execution. Allow-all execution policy does not enable file access.
2. Keep the Gateway File Transfer plugin enabled. Add only the selected commands to the Gateway's explicit `gateway.nodes.commands.allow` list: read uses `file.stat`, `file.fetch`, `dir.list`, `dir.fetch`; write uses `file.write`, `file.create`. These plugin commands are dangerous and are not platform defaults.
3. Approve the expanded command/capability surface using **Nodes → pending approvals**, or `openclaw nodes pending` followed by `openclaw nodes approve <nodeRequestId>` on the Gateway. A node-surface request is separate from device/role pairing; `devices list` is not the relevant pending queue.
4. Configure File Transfer policy version 2 for the exact node ID, including selected `allowReadPaths` and/or `allowWritePaths`, approval mode, and byte limits. For a folder such as `C:/Transfer`, authorize the folder itself and `C:/Transfer/**` when descendants are intended. Older positive permissions may require `openclaw file-transfer approvals migrate`. The companion does not edit Gateway configuration or widen these permissions.
5. Use the dedicated `file_fetch`, `file_write`, `dir_list`, or `dir_fetch` tools. Generic node invocation still traverses the Gateway plugin policy; it is not a permission bypass.

Native contracts and boundaries:

| Command | Behavior |
| --- | --- |
| `file.stat` | File/directory type, size, modification time, and an existing-path identity binding |
| `file.fetch` | Unary base64 download (8 MiB default, 16 MiB hard maximum), or binary download with explicit `maxBytes` (50 MiB local ceiling) |
| `dir.list` | Stable paginated names, paths, types, sizes and modification times; 200 entries default, 5000 per page maximum |
| `dir.fetch` | Whole-tree TAR/GZIP with exact relative manifest, hidden files, size and SHA-256; no filtering of denied or unsupported descendants |
| `file.write` | Verified base64 bytes, preflight write binding, create-parents, explicit overwrite, and inode-preserving bounded rollback |
| `file.create` | Create-only acknowledged binary upload; verifies size/hash before atomic publication, returns `created` or `exists`, never overwrites |

Existing paths bind to Windows volume ID and full 128-bit file ID; writes also bind to the existing ancestor and, when present, target identity. Canonical metadata and bytes come from opened handles. Directory ancestors are held against rename/deletion during operations, leaves are opened without following reparse points, and write mutations require the authorized preflight binding. Binary streams implement Gateway v1 readiness, ordered 8 KiB fragments, at most 1 MiB logical chunks, START/ACK controls, heartbeats, cancellation, and a 30-second input-idle timeout. Streaming invocations do not replay a cached terminal receipt without their bytes.

Only fully qualified local drive paths are supported. UNC/device paths, alternate data streams, traversal, reserved names, and trailing-dot/space aliases are refused. Symlink/junction redirects require Gateway canonical-path authorization and `followSymlinks`; archive traversal never follows descendant links. Workspace reads with `rootPath` remain confined to that canonical root and reject final-file symlinks. Overwrites and archives reject hard-linked files, even if the caller requests otherwise. Filesystems lacking stable native identities fail closed. New files use an owner-only protected Windows ACL; overwrites preserve the existing object's identity and permissions.

Directory archives are limited to 5000 descendants, 16 MiB per file, 64 MiB expanded total, 8 MiB compressed by default (16 MiB maximum), 64 nesting levels and 60 seconds. Listing indexes are capped at 100000 names. Streaming creates are capped at 50 MiB. Four filesystem operations may run concurrently, with a ten-minute wall-clock ceiling outside archive operations. Failed/cancelled creates retire private staging and never expose partial destination bytes; newly requested parent directories are created only after content verification. These local ceilings can be stricter than a Gateway policy configured with larger limits.

The browser proxy supports AI snapshots and ref-based click, hover, type, key press, drag, and staged file-chooser uploads through bundled Chrome DevTools MCP **1.12.0**, including the Gateway's pinned snapshot-identity backport. Requests use explicit page IDs; duplicate-URL target ambiguity fails closed. Opting into `errorEnvelope: "browser-v1"` preserves HTTP route errors. MCP usage statistics, CrUX queries and page-supplied third-party tools are disabled. File access is limited to temporary files and the companion's upload-staging root.

Browser lifecycle is explicit: status/profile/tab listing and requests targeting an existing tab never launch Chrome or initialize MCP just to probe availability. Only explicit open, or navigation without an existing target, can start a browser. A managed launch marks its own startup page and waits for it to become ready; the first open reuses that exact page once, instead of leaving an extra blank tab. Existing user tabs, including blank tabs and startup pages the user has navigated, are not reused. Closing an already-missing tab succeeds idempotently. For close requests only, loopback connection refusal also counts as already closed; timeouts and other HTTP/transport failures remain errors and never trigger startup. New tabs include Gateway-compatible durable ownership; owned cleanup verifies the profile/browser instance and reports `closed`, `missing`, `ownership-mismatch` or `unavailable`, without starting a replacement browser. MCP sessions are retired when the browser-instance identity changes.

The browser profile lives in `%LOCALAPPDATA%\OpenClaw Companion\browser\managed-profile` (or under `OPENCLAW_WINDOWS_HOME` for an explicitly isolated installation). Startup atomically relocates a recognized legacy profile from the executable's `runtimes/browser/managed-profile` directory while the browser is closed. It never merges or overwrites an existing nonempty destination. `Local State`, cookies/login databases, passwords, extensions/settings, history and site storage are preserved. An explicitly chosen backup can be migrated with `--maintain-browser-profile --legacy-browser-profile <absolute-path>` while all companion browser processes are closed.

Settings expose disk/code cache targets (defaults **128/64 MiB**, range 16–2048). Managed Chrome receives the disk-cache-size target; on startup and every minute while the browser is closed, oversized disk/code cache containers and GPU/shader cache containers (32 MiB each) are cleared safely. These are cache targets, not a cap on persistent site data or all Chrome components. **Clear Browser Cache...** in the tray asks for confirmation and clears only allowlisted disk/code/GPU caches; it refuses to modify an active browser profile. It does not clear cookies, logins, extensions, history, service-worker storage or other site storage. Cache maintenance refuses symlinks/junctions and unknown process ownership.

The Windows build replaces only vendored `chrome-devtools-mcp`/`node` output directories. User browser data is kept outside build output. See the [browser runtime notes](src/OpenClaw.Node/browser-runtime/README.md) for the pinned inputs.

Computer control is disabled by default. Its v2 descriptor advertises `screenshot` plus the 13 implemented pointer, scroll, text, key, and held-key actions, screen targets, foreground delivery, and image observations. The Gateway derives `wait` from screenshot support. Primary/secondary input respects Windows' swapped-button setting, including drags; OS handedness settings are not changed. The Windows executable initializes Per-Monitor V2 DPI awareness before creating windows. Pointer coordinates bind to `displayFrameId`, `refWidth`, and the requesting execution from a recent `screen.snapshot`; stale display layouts are rejected, including multi-monitor origin changes. Held input is released on cancellation, disconnect, or Gateway `__close_execution` cleanup; cleanup invalidates only that execution's frames. Split drags auto-release after 30 seconds without activity. Window/accessibility/background/browser-specific v2 actions and recording are not advertised. Native input acceptance is reported as `effect: "unverifiable"`, not as proof that an application changed.

Desktop sharing is separately disabled by default. Configure an IPv4 loopback VNC server (port 5900 by default) offering VNC password authentication; no VNC server is bundled. Unauthenticated and unsupported security types are refused. An optional VNC password is DPAPI-protected in Settings. The stream uses a one-use Gateway ticket, the enrolled Gateway mount path and TLS pin, the same inspected RFB connection, bounded binary frames and backpressure, and runs until cancellation when invoked with `timeoutMs: 0`.

Desktop-sharing setup on Windows:

1. Install a separate VNC server (for example, the server component from the [official TightVNC download](https://www.tightvnc.com/download/)). Its [Windows configuration reference](https://www.tightvnc.com/doc/win/TightVNC_2.7_for_Windows_Installing_from_MSI_Packages.pdf) covers loopback and password-authentication options.
2. Allow loopback connections, restrict access to loopback only (`127.0.0.1`), use port 5900 (or match the companion's configured port), and require standard VNC password authentication. Do not expose the VNC port to LAN/Internet clients or disable authentication.
3. Enable **Desktop sharing (loopback VNC)** in companion Settings. Optionally enter the server's VNC password there; otherwise enter it in the Control UI when prompted. The VNC password is separate from the Gateway token and the VNC server's administrative password.
4. Save/restart the companion and reconnect the Desktop panel. If the error says to start an authenticated loopback VNC server, first check `Get-NetTCPConnection -State Listen -LocalPort 5900` on the Windows node.

Settings actions live in a fixed footer outside the scrollable fields, and the dialog can be resized for smaller work areas.

## Remaining parity gaps (Gateway 2026.11.4 reference)

This companion supports the listed contracts, not every optional capability of the TypeScript node host or the macOS/mobile apps. Unimplemented surfaces are deliberately not advertised.

| Area | Still missing or limited |
| --- | --- |
| Workspace services | `workspace.memory` indexing/search and `workspace.skills` installation/provenance/update operations. Basic filesystem handlers alone do not provide a complete Gateway storage split; the current upstream workspace placement contracts also require POSIX remote roots. |
| Plugin/agent runtimes | Generic `mcp.tools.call.v1`, dynamically registered plugin commands/tools, `agent.cli.claude.run.v1`, node-local CLI session catalogs/resume, Codex `codex.exec-server.stdio.v1`, and provider-owned interactive PTY terminals. The bundled browser MCP runtime is not a generic node MCP host. |
| Managed workers | Private `worker.*` bundle installation, run supervision, workspace synchronization/retention, process management and managed desktop/portal ownership. These optional worker runtimes are separate from this companion's foreground desktop control. |
| Computer control | Gateway-native window/accessibility-tree/background targets and richer observation/actions. Only foreground screen-based v2 actions are currently implemented; same-user IPC window helpers are not a replacement Gateway contract. |
| Execution approvals | Interactive local prompt/approval queue, portable approval snapshots/file pinning, and `system.run.execution-context.v1` routing hints. Batch native policy execution works, but `prompt` remains fail-closed. |
| Desktop sharing | The RFB bridge is implemented; a VNC server is not bundled. AnyDesk is not a compatible noVNC/RFB backend. |
| App integration | Embedded Gateway chat/dashboard and the newer native-device-settings/browser web bridges, local Gateway lifecycle/setup, Authenticode signing and ARM64 packaging are not supplied by this x64 tray app. |

Personal-data/mobile commands (contacts, calendar, SMS, photos, health, motion, mobile Accessibility Control) are platform-specific optional capabilities, not requirements for a Windows file-transfer node. The platform allowlist in the Gateway is a ceiling, not proof that another app actually implements those commands.

## Local IPC

The compatibility server listens on:

```text
\\.\pipe\openclaw.node.ipc
```

It uses `PipeOptions.CurrentUserOnly`, a random DPAPI-protected 256-bit credential, fixed-time credential comparison, request timeouts, and bounded method behavior. Production methods are:

- `ipc.ping`
- `ipc.window.list`, `ipc.window.focus`, `ipc.window.rect`
- `ipc.input.type`, `ipc.input.key`, `ipc.input.click`, `ipc.input.scroll`, `ipc.input.click.relative`

These methods are not part of the Gateway command manifest.

Same-user compatibility clients read `%LOCALAPPDATA%\OpenClaw Companion\ipc-credential.dat`, decrypt it with Windows DPAPI `CurrentUser` scope and the `OpenClaw.Windows.Companion.v2` optional entropy, and send the resulting credential as the top-level `authToken` on every request. The path follows `OPENCLAW_WINDOWS_HOME` when that test/development override is set.

## Build and test

Develop and test on `dev` or a feature branch. Merge into `main` only after the build on `dev` has been tested and approved as stable; a push to `main` triggers the release workflow.

From a clean checkout, run `.\scripts\build-dev.ps1` to build and start `dev`, or pass `-Branch feature/my-change` to select another remote branch. Add `-BuildOnly` if the installed companion is running and should stay running. See the [development build guide](scripts/README.md) for details.

### Download a Windows build from CI

You can develop on Linux without cross-compiling the tray. Push to `dev`, or open/update a pull request. **Windows dev build** uses a hosted Windows runner to build self-contained `win-x64`, run the unit tests, and smoke-test the packaged executable. For a push it builds the pushed commit; for a PR it builds the **PR head**, not the temporary merge commit. Live Gateway and interactive desktop checks are separate.

1. While signed in to GitHub, open the commit's **Checks** (or the PR's **Checks** tab) and follow the **Windows dev build** run. You can also find it under the repository's **Actions** tab.
2. Open the successful run's **Summary** and download the ZIP from **Artifacts**. Its name is `companion-win-x64-<full-commit-sha>-<run-id>-<attempt>`; artifacts are retained for 14 days. The build job's summary also links the ZIP and records its SHA-256.
3. Extract the **entire** ZIP into a new folder outside your installation. Check `commit` in `build-info.json` against the commit you intended to test. From the extracted folder, `.\OpenClaw.Node.exe --version` is a smoke check; `.\OpenClaw.Node.exe --tray` starts the test app. No SDK or Git checkout is needed on the test PC.

This is a portable **test build**, not an installer or a tray update. Only run code you trust on a paired Windows account: the test app uses that account's settings and credentials. Exit the installed tray before launching the test tray, leave startup settings alone, then exit the test tray and reopen **OpenClaw Companion** from Start when finished. Use a disposable Windows VM and test credentials for untrusted PRs. The CI job does not create tags or releases, sign updates, or change the installer feed.

### Build and test locally

Restore and run the full test suite:

```powershell
dotnet restore .\src\OpenClaw.sln
dotnet test .\src\OpenClaw.Node.Tests\OpenClaw.Node.Tests.csproj --no-restore -p:Platform=x64
```

Run the Windows-only settings layout regressions (including compact windows and 150%/200% scaling):

```powershell
dotnet test .\src\OpenClaw.Node.Tests\OpenClaw.Node.Tests.csproj -p:Platform=x64 -p:WindowsUiTests=true --filter FullyQualifiedName~CompanionSettingsDialogTests
```

Build without fetching or restarting (with the pinned runtime already present):

```powershell
dotnet build .\src\OpenClaw.Node\OpenClaw.Node.csproj -c Release -p:Platform=x64 -f net8.0-windows
```

The **source checkout's** Release output is separate from the per-user installation:

```text
src\OpenClaw.Node\bin\x64\Release\net8.0-windows\
```

Run directly:

```powershell
dotnet run --project .\src\OpenClaw.Node\OpenClaw.Node.csproj -p:Platform=x64 -f net8.0-windows -- --gateway-url ws://127.0.0.1:18789 --gateway-token TOKEN
```

Windows starts in tray mode by default. Use `--no-tray` for a headless process or `--tray` to force tray mode.

## Architecture

- `Protocol/GatewayConnection.cs`: Gateway session, auth, request correlation, invoke lifecycle
- `Services/NodeCapabilityRegistry.cs`: canonical advertised/dispatchable command surface
- `Services/NodeCommandExecutor.cs`: current Gateway command handlers and execution hardening
- `Services/ExecApprovalsStore.cs`: host-native Windows approval contract
- `Services/UploadStagingService.cs`: bounded private upload copies and expiry
- `Services/ComputerControlService.cs`: declared Computer Use v2 subset and input cleanup
- `Services/DesktopStreamService.cs`: ticket-only RFB desktop relay
- `Services/SecureStore.cs`: CurrentUser-DPAPI persistence and atomic writes
- `Services/CanvasService.cs`: Canvas/A2UI window and capture
- `Services/TalkPushToTalkService.cs`: Windows speech capture and operator-sidecar `chat.send`
- `Services/IpcPipeServerService.cs`: same-user local compatibility bridge
- `Tray/CompanionSettingsDialog.cs`: settings, sensitive-feature confirmation, start-at-login

## Known limitations

- Canvas uses Microsoft Edge WebView2 and requires the evergreen WebView2 Runtime. A2UI is loaded from the Gateway's capability-scoped `canvas` plugin surface; capability URLs are refreshed through `node.pluginSurface.refresh`, and UI actions are forwarded only from the exact trusted A2UI document.
- Camera clips use `ffmpeg` DirectShow. Requests with `includeAudio: true` capture the first available DirectShow microphone and fail clearly when no microphone is available.
- Talk transcription uses Windows `System.Speech`; recognition quality and language availability depend on installed Windows speech components.
- Native Windows desktop input has an opt-in disposable-window smoke test (capture, primary click with swapped buttons, Ctrl+A, Unicode typing and replacement). Real-browser acceptance of the bundled DevTools actions remains separate. Desktop streaming has a real TCP/WebSocket handshake regression test, but this sync does not install or provision a VNC server.
- ARM64 packaging, WSL provisioning, a full Command Center/chat UI, unattended restarts, and local MCP/Claude runtimes are not included.
