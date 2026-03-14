# OpenClaw Windows Companion Node

A Windows-native companion node for OpenClaw Gateway.

It connects to your local OpenClaw gateway, exposes system/media/automation commands, supports tray-first operation (with onboarding UX).

---

## Table of Contents

- [1) What this project is](#1-what-this-project-is)
- [2) Feature list](#2-feature-list)
- [3) Requirements](#3-requirements)
- [4) Configuration (Gateway + Node)](#4-configuration-gateway--node)
- [5) Build, version, reload, and deploy](#5-build-version-reload-and-deploy)
- [6) Browser runtime and remote browser behavior](#6-browser-runtime-and-remote-browser-behavior)
- [7) Initial setup and pairing](#7-initial-setup-and-pairing)
- [8) Usage examples](#8-usage-examples)
- [9) Project structure and architecture](#9-project-structure-and-architecture)
- [10) Troubleshooting](#10-troubleshooting)
- [11) Security and privacy notes](#11-security-and-privacy-notes)
- [12) Testing](#12-testing)
- [13) Known limitations](#13-known-limitations)

---

## 1) What this project is

Contains the Windows port of the OpenClaw node runtime.

At a high level, it provides:

- **Gateway connection + protocol handling**
- **Node command execution** (`system.run`, media, automation, etc.)
- **Local IPC bridge** via Named Pipes (Windows)
- **Discovery beacons** on LAN
- **Tray UX + onboarding flow** for non-console user experience

This project is intended to run next to OpenClaw Gateway and be controlled by OpenClaw sessions/tools.

---

## 2) Feature list

### Core connectivity

- Gateway WebSocket handshake flow (`connect.challenge` → `connect` → `hello-ok`)
- Signed device identity payload in connect flow
- Exponential reconnect backoff + tick monitor
- Connection rejection handling with tray-visible/auth dialogs

### Gateway method handlers (core)

- `status`
- `health`
- `set-heartbeats`
- `system-event`
- `channels.status`
- `config.get`, `config.set`, `config.patch`, `config.schema`
- Pairing request handlers:
  - `node.pair.list`, `node.pair.approve`, `node.pair.reject`
  - `device.pair.list`, `device.pair.approve`, `device.pair.reject`

### Node invoke commands

- System:
  - `system.run`
  - `system.which`
  - `system.notify`
- Browser:
  - `browser.proxy` (node-owned bundled DevTools MCP backend)
  - Chrome-first managed browser launch with reuse of an already-running DevTools session when present
- Screen/camera:
  - `screen.list`
  - `screen.capture`
  - `screen.record`
  - `camera.list`
  - `camera.snap`
- Window/input automation:
  - `window.list`, `window.focus`, `window.rect`
  - `input.type`, `input.key`, `input.click`, `input.scroll`, `input.click.relative`
  - `ui.find`, `ui.click`, `ui.type`

### IPC server (Windows)

Named Pipe endpoint with auth support:

- `\\.\pipe\openclaw.node.ipc`
- Methods include `ipc.ping` plus window/input methods
- Per-request timeout (`params.timeoutMs`) with explicit `TIMEOUT` errors

### Discovery

- UDP multicast beacon announcements (`openclaw.node.discovery.v1`)
- Periodic + reconnect-triggered announcements with jitter/throttling
- In-memory discovered-node index with stale-entry expiry

### Tray UX (Windows)

- Default mode on Windows (unless `--no-tray`)
- Custom lobster tray icon
- Menu actions:
  - Open Logs
  - Open Config File
  - Copy Diagnostics
  - Restart Node
  - Exit
- Live status section:
  - State
  - Pending pairs
  - Last reconnect duration
  - Onboarding status
- Onboarding and auth dialogs (OK-button MessageBox)

---

## 3) Requirements

### Runtime requirements

- **Windows 10/11** (recommended for tray and automation)
- **.NET SDK 8.0**
- Running **OpenClaw Gateway** with valid token

### Optional but recommended

- `ffmpeg` available (fallback path for some media flows)
- Camera privacy settings enabled for desktop apps (if camera features are used)

---

## 4) Configuration (Gateway + Node)

Node resolves gateway connection values in this order:

1. CLI args
   - `--gateway-url`
   - `--gateway-token`
2. Environment variables
   - `OPENCLAW_GATEWAY_URL`
   - `OPENCLAW_GATEWAY_TOKEN`
3. OpenClaw config file
   - `~/.openclaw/openclaw.json`

### Minimal gateway config example

```json
{
  "gateway": {
    "host": "127.0.0.1",
    "port": 18789,
    "auth": {
      "token": "REPLACE_WITH_REAL_TOKEN"
    }
  }
}
```

### Gateway node command allowlist example (recommended)

Use Gateway-side command policy to explicitly allow only the node commands you want exposed.

```json
{
  "gateway": {
    "port": 18789,
    "auth": {
      "token": "REPLACE_WITH_REAL_TOKEN"
    },
    "nodes": {
      "allowCommands": [
        "system.notify",
        "system.which",
        "system.run",
        "browser.proxy",
        "screen.capture",
        "screen.list",
        "screen.record",
        "camera.list",
        "camera.snap",
        "window.list",
        "window.focus",
        "window.rect",
        "input.type",
        "input.key",
        "input.click",
        "input.scroll",
        "input.click.relative",
        "ui.find",
        "ui.click",
        "ui.type"
      ],
      "denyCommands": [
        "contacts.*",
        "calendar.*",
        "sms.*"
      ]
    }
  }
}
```

> If your gateway version uses a slightly different schema, keep the same intent: explicit allowlist for node commands and explicit denylist for high-risk surfaces.

### Notes on config fields

- `gateway.host`: gateway host/IP used by node (default `127.0.0.1`)
- `gateway.port`: gateway WebSocket port used by node (`ws://<host>:<port>/`)
- `gateway.auth.token`: shared auth token for gateway connect
- `gateway.nodes.allowCommands`: explicit list of node command names the gateway will permit
- `gateway.nodes.denyCommands`: deny patterns for commands you want blocked even if broadly allowed

If token/config are missing/invalid in tray mode, app stays alive and guides recovery (dialog + tray onboarding status + Open Config menu action).

---

## 5) Build, version, reload, and deploy

### Canonical workflow

Use the scripts in `scripts/` instead of inventing ad-hoc build/deploy flows.

Recommended sequence for runtime-visible node changes:

1. bump the runtime-visible build label
2. inspect local preflight state
3. rebuild/reload the canonical Windows output
4. deploy that canonical output to ROG17 only if needed

### Runtime-visible build label

The tray and several runtime surfaces use:

- `src/OpenClaw.Node/BuildInfo.cs`

Specifically:

- `BuildInfo.BuildVersion` (for example `b0029`)

That build label is the first thing to bump when you want an easy runtime verification point.

### Canonical scripts

#### Bump build label

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\bump-build.ps1 -RepoPath .
```

#### Preflight local state

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-preflight.ps1 -RepoPath .
```

#### Reload local Windows node from canonical output

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-reload.ps1 -RepoPath . -NoPull
```

#### Deploy canonical build to ROG17 from WSL/bash

```bash
./scripts/deploy-rog17-node.sh
```

### Build targets

- `net8.0` (cross-platform/dev target)
- `net8.0-windows` (Windows Forms tray target; built on Windows, `WinExe` output so no console window)

### Canonical build output

The canonical Windows build output is:

```text
src/OpenClaw.Node/bin/x64/Debug/net8.0-windows/
```

Do not silently switch to alternate output folders. If the canonical path is blocked/locked, stop and choose the next step deliberately.

### Direct build (when needed)

From `src`:

```bash
cd <repo-root>/src
dotnet build OpenClaw.Node/OpenClaw.Node.csproj -p:Platform=x64 -f net8.0-windows
```

### Run (direct)

```bash
cd <repo-root>/src/OpenClaw.Node
dotnet run -p:Platform=x64 -f net8.0-windows -- --gateway-url ws://{gateway_ip}:18789 --gateway-token <TOKEN>
```

### Tray/headless behavior

- On Windows, tray mode is default.
- Use `--no-tray` for headless behavior.
- Use `--tray` to force tray mode explicitly.

---

## 6) Browser runtime and remote browser behavior

### Browser backend model

The Windows node owns browser automation through:

- `browser.proxy`
- a **bundled** `chrome-devtools-mcp` backend
- a **bundled** Node runtime

The target machine should not need system `node`, `npm`, or `npx` for browser support.

### Bundled browser runtime

Bundled browser runtime files live under:

```text
src/OpenClaw.Node/browser-runtime/
```

They are copied into the built/published node output under:

```text
runtimes/browser/
```

Current packaged pieces include:

- bundled `node.exe`
- bundled `chrome-devtools-mcp`
- `versions.json` for pinned runtime metadata

Refresh the bundle with:

```bash
./scripts/vendor-browser-runtime.sh
```

### Browser launch behavior

Current intent/behavior:

1. probe `http://127.0.0.1:9222/json/version`
2. if a valid DevTools endpoint already exists, attach to it
3. otherwise discover a supported local browser
4. launch a managed browser profile with remote debugging enabled
5. serve browser actions through the bundled MCP backend

### Browser preference

Current discovery order is Chrome-first:

1. Google Chrome
2. Microsoft Edge

The validated target behavior is:

- prefer **Chrome** when launching a new managed browser session from a clean state
- reuse an already-running valid DevTools session when available

### Notes

- Chromium-compatible browsers can expose the same DevTools Protocol, but product intent here is Chrome-first.
- Fixed-port assumptions around `9222` require validation against a **real** DevTools endpoint, not just “something is listening”.

---

## 7) Initial setup and pairing

1. Ensure gateway is running and reachable on the configured `gateway.host` IP/name (default is `127.0.0.1`).
2. Ensure token is available via CLI/env/config.
3. Start node.
4. Confirm node appears connected in OpenClaw node status.
5. Approve pairing requests if required by your gateway policy.

### If token/config is missing

- Tray starts in onboarding state
- Dialog explains what to fix
- Use **Open Config File** in tray menu, save token, then **Restart Node**

---

## 8) Usage examples

### Example A — run node with explicit token

```bash
dotnet run -p:Platform=x64 -- --gateway-url ws://{gateway_ip}:18789 --gateway-token <TOKEN>
```

### Example B — run with config fallback only

```bash
dotnet run -p:Platform=x64
```

(Requires valid `~/.openclaw/openclaw.json`.)

### Example C — headless run

```bash
dotnet run -p:Platform=x64 -- --no-tray
```

---

## 9) Project structure and architecture

## Folder map

```text
src/
├── OpenClaw.sln
├── OpenClaw.Node/
│   ├── Program.cs
│   ├── OpenClaw.Node.csproj
│   ├── Protocol/
│   │   ├── GatewayConnection.cs
│   │   ├── GatewayModels.cs
│   │   └── BridgeModels.cs
│   ├── Services/
│   │   ├── CoreMethodService.cs
│   │   ├── NodeCommandExecutor.cs
│   │   ├── IpcPipeServerService.cs
│   │   ├── DiscoveryService.cs
│   │   ├── DeviceIdentityService.cs
│   │   ├── ScreenCaptureService.cs
│   │   ├── CameraCaptureService.cs
│   │   └── AutomationService.cs
│   └── Tray/
│       ├── WindowsNotifyIconTrayHost.cs
│       ├── TrayStatusBroadcaster.cs
│       ├── OnboardingAdvisor.cs
│       └── Assets/openclaw-claw.ico
└── OpenClaw.Node.Tests/
    ├── *Tests.cs
    └── OpenClaw.Node.Tests.csproj
```

## Architecture (high-level)

1. **Program bootstrap**
   - resolves config/token
   - builds service graph
   - wires tray events + onboarding
2. **GatewayConnection**
   - handles websocket lifecycle + protocol frames
   - dispatches methods/events
   - reconnect/tick resilience
3. **CoreMethodService**
   - handles gateway methods and pairing state
4. **NodeCommandExecutor**
   - executes node invoke commands
   - delegates to media/automation services
5. **IpcPipeServerService**
   - local named-pipe surface for host integration
6. **DiscoveryService**
   - multicast beacon send/listen/index
7. **Tray layer**
   - tray host abstraction and Windows implementation
   - onboarding state/advice and user diagnostics

---

## 10) Troubleshooting

### App exits immediately

- If running without tray (`--no-tray`) and no token is configured, app exits by design.
- In default Windows tray mode, it should stay alive and show setup guidance.

### No tray icon visible

- Ensure Windows target build exists (`net8.0-windows`)
- Rebuild and restart node

### “Authentication failed” dialog

- Token likely invalid/mismatched
- Open Config File from tray
- verify `gateway.auth.token`
- restart node

### Camera snapshot fails

- Check Windows camera privacy permissions
- verify camera device exists (`camera.list`)
- optionally verify ffmpeg availability if fallback expected

### Gateway unreachable

- verify gateway service status
- verify local port and URL
- confirm no firewall/network policy blocks the configured gateway host/IP websocket

---

## 11) Security and privacy notes

- Do **not** commit real tokens, keys, PATs, or personal local paths.
- Keep secrets in local env/config (ignored from source control).
- Use placeholders in docs and scripts where possible.
- Review logs before sharing externally (logs may include environment-specific info).

---

## 12) Testing

Run all tests:

```bash
cd <repo-root>/src
dotnet test OpenClaw.Node.Tests/OpenClaw.Node.Tests.csproj -p:Platform=x64
```

Run real-gateway integration subset (opt-in):

```bash
cd <repo-root>/src
RUN_REAL_GATEWAY_INTEGRATION=1 dotnet test OpenClaw.Node.Tests/OpenClaw.Node.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~RealGatewayIntegrationTests"
```

---

## 13) Known limitations

- Some automation/media behavior is host and permission dependent.
- `net8.0-windows` target is intended for Windows hosts (tray/UI path).
- Discovery currently uses in-memory index (no persisted discovery DB).

---
