# Development builds

Installed nodes update from the tray. To add or migrate a Windows PC, follow the [release installer instructions](../README.md#install-or-update-on-windows-x64). The script here is only for development checkouts.

## Build a branch

From PowerShell in the companion checkout:

```powershell
.\scripts\build-dev.ps1                                # Build origin/dev and start its tray
.\scripts\build-dev.ps1 -Branch feature/my-change      # Build and start a different branch
.\scripts\build-dev.ps1 -BuildOnly                     # Build dev without stopping or starting a tray
```

Requires Git, the .NET 8 SDK, and a clean checkout on a local branch. Starting a tray also requires an interactive Windows desktop. Use `-WhatIf` to preview the selected branch and operation without fetching or changing anything.

The script fetches the exact branch from `origin`, switches the checkout to it (creating a tracking branch if needed), and fast-forwards to the fetched commit. It leaves the checkout on that branch. It rejects `main`, missing remote branches, uncommitted changes, and a selected branch with local-only or diverged commits. Nothing is pushed. To test uncommitted work, build directly with `dotnet build` on your development branch.

By default it stops only this checkout's tray and bundled Node worker, builds **x64 Release / net8.0-windows**, and starts the new tray from:

```text
src\OpenClaw.Node\bin\x64\Release\net8.0-windows\OpenClaw.Node.exe
```

If the installed companion is running, use `-BuildOnly` (also works with `-Branch`). This leaves the installed node connected and does not launch a duplicate. Build-only mode refuses to overwrite a running checkout build. A normal rebuild may request permission to stop an elevated source tray, but does not elevate the build or the new tray.

The bundled `node.exe` is checked against the selected branch's SHA-256 pin and restored if necessary. A failed build never starts partial output. The script does not change shortcuts, scheduled tasks or Windows startup settings.
