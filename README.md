# OpenClaw Windows Companion Node

A Windows-native companion node for OpenClaw Gateway.

It connects to your local OpenClaw gateway, exposes system/media/automation commands, supports tray-first operation (with onboarding UX), and provides watchdog scripts for reliable day-to-day running.

---

## Table of Contents

- [1) What this project is](#1-what-this-project-is)
- [2) Feature list](#2-feature-list)
- [3) Requirements](#3-requirements)
- [4) Configuration (Gateway + Node)](#4-configuration-gateway--node)
- [5) Build and run](#5-build-and-run)
- [6) Initial setup and pairing](#6-initial-setup-and-pairing)
- [7) Usage examples](#7-usage-examples)
- [8) Scripts and operational workflow](#8-scripts-and-operational-workflow)
- [9) Project structure and architecture](#9-project-structure-and-architecture)
- [10) Troubleshooting](#10-troubleshooting)
- [11) Security and privacy notes](#11-security-and-privacy-notes)
- [12) Testing](#12-testing)
- [13) Known limitations](#13-known-limitations)

---

## 1) What this project is

`apps/windows` contains the Windows port of the OpenClaw node runtime.

At a high level, it provides:

- **Gateway connection + protocol handling**
- **Node command execution** (`system.run`, media, automation, etc.)
- **Local IPC bridge** via Named Pipes (Windows)
- **Discovery beacons** on LAN
- **Tray UX + onboarding flow** for non-console user experience
- **Watchdog scripts** for resilient local operation

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

## 5) Build and run

From `apps/windows`:

```bash
cd <repo-root>/apps/windows
dotnet build OpenClaw.Node/OpenClaw.Node.csproj -p:Platform=x64
```

### Build targets

- `net8.0` (cross-platform/dev target)
- `net8.0-windows` (Windows Forms tray target; built on Windows, `WinExe` output so no console window)

### Run (direct)

```bash
cd <repo-root>/apps/windows/OpenClaw.Node
dotnet run -p:Platform=x64 -- --gateway-url ws://192.168.1.50:18789 --gateway-token <TOKEN>
```

### Tray/headless behavior

- On Windows, tray mode is default.
- Use `--no-tray` for headless behavior.
- Use `--tray` to force tray mode explicitly.

---

## 6) Initial setup and pairing

1. Ensure gateway is running and reachable on the configured `gateway.host` IP/name (default is `127.0.0.1`).
2. Ensure token is available via CLI/env/config.
3. Start node (or watchdog).
4. Confirm node appears connected in OpenClaw node status.
5. Approve pairing requests if required by your gateway policy.

### If token/config is missing

- Tray starts in onboarding state
- Dialog explains what to fix
- Use **Open Config File** in tray menu, save token, then **Restart Node**

---

## 7) Usage examples

### Example A — run node with explicit token

```bash
dotnet run -p:Platform=x64 -- --gateway-url ws://192.168.1.50:18789 --gateway-token <TOKEN>
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

### Example D — watchdog-managed runtime (PowerShell)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-watchdog.ps1
```

---

## 8) Scripts and operational workflow

Located in `apps/windows/scripts/`.

### `node-watchdog.ps1`

Long-running supervisor that:

- starts node if it is not running
- can pause via `.node-watchdog.pause`
- writes child logs:
  - `node-watchdog-child.log`
  - `node-watchdog-child.err.log`
- prefers tray build (`net8.0-windows`) when available and tray enabled

### `node-reload.ps1`

Developer reload helper that:

- pauses watchdog
- stops node
- optionally pulls latest branch
- optionally rebuilds
- unpauses watchdog
- verifies process is running

### `node-watchdog-install-task.ps1`

Installs/updates a Scheduled Task (`OpenClawNodeWatchdog`) for logon startup.

---

## 9) Project structure and architecture

## Folder map

```text
apps/windows/
├── OpenClaw.sln
├── README.md
├── PLAN.md
├── HANDOFF.md
├── RELEASE_NOTES.md
├── scripts/
│   ├── node-watchdog.ps1
│   ├── node-reload.ps1
│   └── node-watchdog-install-task.ps1
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
- Rebuild and restart watchdog/node

### “Authentication failed” dialog

- Token likely invalid/mismatched
- Open Config File from tray
- verify `gateway.auth.token`
- restart node

### Camera snapshot fails

- Check Windows camera privacy permissions
- verify camera device exists (`camera.list`)
- optionally verify ffmpeg availability if fallback expected

### Watchdog keeps relaunching

- inspect:
  - `node-watchdog-child.log`
  - `node-watchdog-child.err.log`
- check token/config path resolution

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
cd <repo-root>/apps/windows
dotnet test OpenClaw.Node.Tests/OpenClaw.Node.Tests.csproj -p:Platform=x64
```

Run real-gateway integration subset (opt-in):

```bash
cd <repo-root>/apps/windows
RUN_REAL_GATEWAY_INTEGRATION=1 dotnet test OpenClaw.Node.Tests/OpenClaw.Node.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~RealGatewayIntegrationTests"
```

---

## 13) Known limitations

- Some automation/media behavior is host and permission dependent.
- `net8.0-windows` target is intended for Windows hosts (tray/UI path).
- Discovery currently uses in-memory index (no persisted discovery DB).

---

If you are maintaining this project, also read:

- `PLAN.md` (implementation roadmap and completion state)
- `HANDOFF.md` (current state and operational notes)
- `RELEASE_NOTES.md` (historical milestones)
