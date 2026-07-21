# OpenClaw Windows Companion

A Windows tray companion for the OpenClaw Gateway. This implementation is aligned with the Gateway `2026.7.2` protocol baseline in `openclaw_source` and negotiates node protocol versions 3 through 4.

The Gateway source tree is a reference only. This repository does not modify it.

## Current functionality

- Protocol 3–4 node WebSocket sessions with signed device-auth v3 payloads, plus protocol-v4 operator sidecars
- Shared-token enrollment and per-Gateway, per-device, per-role device-token reconnects
- Bounded frames, handshake/request timeouts, tick monitoring, reconnect jitter, and optional TLS SHA-256 pinning
- Concurrent node invokes with timeouts, cancellation, ordered input, progress chunks, idempotency replay, and exactly one terminal result
- Host-native Windows execution approvals with optimistic concurrency and fail-closed defaults
- Device information/status, Windows location, screen snapshots/recording, camera capture, Canvas/A2UI, browser proxy, and Talk push-to-talk
- Same-user authenticated Named Pipe compatibility surface for legacy window/input automation
- Tray settings, diagnostics, onboarding, restart/exit, and optional start-at-login
- DPAPI-protected companion settings, device identity, device tokens, and IPC credential

## Gateway command surface

The companion uses one registry for both its connect manifest and command dispatch. A handler is not Gateway-reachable unless the registry advertises it.

Enabled by default:

- `system.run.prepare`, `system.run`, `system.which`
- `system.execApprovals.get`, `system.execApprovals.set`
- `system.notify`, `fs.listDir`
- `browser.proxy`
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

The companion does not advertise `mcp.tools.call.v1` or `agent.cli.claude.run.v1`, because it does not ship those optional runtimes.

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
4. Read-only legacy import from `~/.openclaw/openclaw.json`

CLI and environment tokens are transient and are not copied into the secure settings store. On first use, a token found only in the legacy config may be imported once. The Gateway source/config is never edited.

After a successful enrollment, the Gateway-issued device token is endpoint-, identity-, and role-bound. The companion can reconnect with it after the shared token is removed; invalid stored tokens are cleared and require fresh enrollment.

For a remote `wss://` Gateway, Settings accepts an optional SHA-256 certificate pin. When configured, the presented certificate must match that pin.

Companion-owned data is stored under:

```text
%LOCALAPPDATA%\OpenClaw Companion\
```

Secrets and identities use Windows DPAPI with `CurrentUser` scope. The native execution policy is a non-secret JSON file in the same directory and is written atomically.

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

Updates are full replacements with `defaultAction`, `rules`, and the last observed `baseHash`. Stale writes are rejected. `prompt` fails closed when there is no active local approval UI. Default allow, wildcard executables, and allow rules for shells/loaders such as `cmd`, PowerShell, `mshta`, or `rundll32` are rejected.

Only a non-empty string array is accepted for `system.run.params.command`. Prepared plans are revalidated before execution, sensitive inherited environment variables are removed, forwarded environment overrides are restricted, stdout/stderr share a 200,000-character cap, and timeout/cancellation kills the process tree.

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

Restore and run the full test suite:

```powershell
dotnet restore .\src\OpenClaw.sln
dotnet test .\src\OpenClaw.Node.Tests\OpenClaw.Node.Tests.csproj --no-restore -p:Platform=x64
```

Build the Windows companion:

```powershell
dotnet build .\src\OpenClaw.Node\OpenClaw.Node.csproj --no-restore -p:Platform=x64 -f net8.0-windows
```

The canonical debug output is:

```text
src\OpenClaw.Node\bin\x64\Debug\net8.0-windows\
```

Run directly:

```powershell
dotnet run --project .\src\OpenClaw.Node\OpenClaw.Node.csproj -p:Platform=x64 -f net8.0-windows -- --gateway-url ws://127.0.0.1:18789 --gateway-token TOKEN
```

Windows starts in tray mode by default. Use `--no-tray` for a headless process or `--tray` to force tray mode.

Repository scripts in `scripts/` remain the canonical workflow for build-label bumps, local reloads, preflight checks, and deployment.

## Architecture

- `Protocol/GatewayConnection.cs`: Gateway session, auth, request correlation, invoke lifecycle
- `Services/NodeCapabilityRegistry.cs`: canonical advertised/dispatchable command surface
- `Services/NodeCommandExecutor.cs`: current Gateway command handlers and execution hardening
- `Services/ExecApprovalsStore.cs`: host-native Windows approval contract
- `Services/SecureStore.cs`: CurrentUser-DPAPI persistence and atomic writes
- `Services/CanvasService.cs`: Canvas/A2UI window and capture
- `Services/TalkPushToTalkService.cs`: Windows speech capture and operator-sidecar `chat.send`
- `Services/IpcPipeServerService.cs`: same-user local compatibility bridge
- `Tray/CompanionSettingsDialog.cs`: settings, sensitive-feature confirmation, start-at-login

## Known limitations

- Canvas uses Microsoft Edge WebView2 and requires the evergreen WebView2 Runtime. A2UI is loaded from the Gateway's capability-scoped `canvas` plugin surface; capability URLs are refreshed through `node.pluginSurface.refresh`, and UI actions are forwarded only from the exact trusted A2UI document.
- Camera clips use `ffmpeg` DirectShow. Requests with `includeAudio: true` capture the first available DirectShow microphone and fail clearly when no microphone is available.
- Talk transcription uses Windows `System.Speech`; recognition quality and language availability depend on installed Windows speech components.
- ARM64 packaging, WSL provisioning, a full Command Center/chat UI, an updater, and local MCP/Claude runtimes are not included.
