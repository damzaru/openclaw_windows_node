# Scripts

Canonical helper scripts for building, versioning, reloading, and deploying `openclaw_windows_app`.

## Rule of thumb

**Do not invent ad-hoc build/deploy flows when a script here already covers the job.**

In particular:
- Prefer `bump-build.ps1` before any runtime-visible node change you want to verify quickly
- Prefer `node-preflight.ps1` before rebuilding or deploying
- Prefer `node-reload.ps1` for normal local Windows build + reload of `OpenClaw.Node`
- Prefer `deploy-rog17-node.sh` for copying the canonical build to ROG17
- Prefer the watchdog scripts for keeping the node running from the expected repo build output
- Prefer `vendor-browser-runtime.sh` for refreshing the bundled browser runtime payload
- **Do not** build into random alternate output folders unless explicitly requested/approved

---

## Recommended flow for live-node changes

1. bump the runtime-visible build label
2. run preflight
3. run local reload from the canonical output path
4. deploy the canonical build to ROG17
5. verify before testing behavior

### Canonical command sequence

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\bump-build.ps1 -RepoPath .
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-preflight.ps1 -RepoPath .
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-reload.ps1 -RepoPath . -NoPull
```

Then from WSL/bash:

```bash
./scripts/deploy-rog17-node.sh
```

---

## `bump-build.ps1`

**Purpose:** Increment the runtime-visible build label in `src/OpenClaw.Node/BuildInfo.cs`.

### Why this exists
The tray menu and several runtime surfaces use `BuildInfo.BuildVersion` (for example `b0028`), not just assembly/file version metadata.

### Behavior
- reads `src/OpenClaw.Node/BuildInfo.cs`
- increments `bNNNN` by one
- writes the updated source file in place
- prints the new build label

### Example
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\bump-build.ps1 -RepoPath .
```

---

## `node-preflight.ps1`

**Purpose:** Inspect the local repo/runtime state before rebuild or deploy.

### Behavior
Prints JSON with:
- source build label from `BuildInfo.cs`
- canonical build output path
- whether the canonical build exists
- file/product version from the canonical build exe
- SHA256 of the canonical build exe
- running watchdog count/PIDs
- running local node processes, paths, and hashes

### Why this exists
Use this before rebuild/deploy so you know:
- what build label the source says
- what binary is actually sitting in the canonical build output
- what process is actually running locally

### Example
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-preflight.ps1 -RepoPath .
```

---

## `node-reload.ps1`

**Purpose:** Canonical script to stop the running node, optionally sync git, build the Windows target, optionally publish to a runtime directory, unpause the watchdog, and wait for the node to come back.

### Default behavior
- pauses the watchdog via `.node-watchdog.pause`
- stops existing `OpenClaw.Node` processes
- optionally `git fetch / checkout / pull`
- builds **Windows target**:
  - `TargetFramework=net8.0-windows`
  - `Platform=x64`
  - `Configuration=Debug`
- validates the produced runtime config to ensure WindowsDesktop bits are present
- unpauses the watchdog
- waits for the node process to come back from the expected path

### Important note
This is the script to use when you want the repo’s **normal** build/reload flow. It already knows the expected output path and watchdog behavior.

### Key parameters
- `-RepoPath`
- `-Branch` (default `main`)
- `-Platform` (default `x64`)
- `-Configuration` (default `Debug`)
- `-TargetFramework` (default `net8.0-windows`)
- `-RuntimeDir` to enable publish to a specific runtime folder
- `-NoPull`
- `-NoBuild`
- `-NoPublish`
- `-StartWatchdog`
- `-WaitForNodeSeconds`

### Example
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\node-reload.ps1 -RepoPath . -NoPull
```

### When not to use
- do not replace this with manual zip/copy/restart flows unless there is a deliberate reason
- if the normal build path is blocked/locked, **stop and ask** before choosing a new output path

---

## `deploy-rog17-node.sh`

**Purpose:** Canonical ROG17 deploy script from WSL/bash.

### Behavior
- deploys **only** from the canonical build output:
  - `src/OpenClaw.Node/bin/x64/Debug/net8.0-windows/`
- reads the current source build label from `BuildInfo.cs`
- computes local `OpenClaw.Node.exe` SHA256
- checks remote `OpenClaw.Node.exe` SHA256 first
- skips deploy if remote already matches local unless `--force` is used
- uploads canonical build zip to ROG17
- extracts into `C:\Users\David\OpenClawNode`
- restarts `OpenClaw.Node.exe`

### Why this exists
This avoids:
- mystery alternate output folders
- redeploying unchanged binaries
- forgetting which build was copied

### Example
```bash
./scripts/deploy-rog17-node.sh
```

### Optional args
```bash
./scripts/deploy-rog17-node.sh --host david@192.168.1.250 --ssh-key ~/.ssh/id_ed25519_openclaw
./scripts/deploy-rog17-node.sh --force
```

### Guardrail
If the canonical build output is missing, this script fails and tells you to run `node-reload.ps1` first.

---

## `node-watchdog.ps1`

**Purpose:** Keeps `OpenClaw.Node.exe` running from the expected repo build output.

### Behavior
- monitors the expected node exe path
- prefers tray build at:
  - `src\OpenClaw.Node\bin\x64\Debug\net8.0-windows\OpenClaw.Node.exe`
- falls back to headless `net8.0` target if tray build is missing
- honors pause file `.node-watchdog.pause`
- writes logs under `logs/`
- restarts the node if it is not running

---

## `node-watchdog-install-task.ps1`

**Purpose:** Installs/updates a Windows Scheduled Task that starts `node-watchdog.ps1` at logon.

---

## `vendor-browser-runtime.sh`

**Purpose:** Refreshes the bundled browser runtime payload that ships inside `OpenClaw.Node`.

### Behavior
- downloads pinned Windows Node runtime
- fetches pinned `chrome-devtools-mcp` package tarball
- installs production dependencies
- writes `src/OpenClaw.Node/browser-runtime/versions.json`

### Default pinned versions
- Node: `v24.14.0`
- `chrome-devtools-mcp`: `0.20.0`

### Example
```bash
./scripts/vendor-browser-runtime.sh
```

---

## Guardrails

- If the normal build output path is blocked or locked, **do not silently switch to a random output directory**.
- If a different build path is needed, **say the exact path first and ask**.
- If testing on a live node/machine, inspect the currently running build/capabilities before redeploying.
- Bump the runtime-visible build label in `BuildInfo.cs` before relying on tray/build verification.
- Prefer explicit, traceable build identity over mystery binaries.
