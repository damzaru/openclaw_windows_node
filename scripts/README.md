# Companion maintenance

For a release installation, use the installed tray's **Check for updates...** command. The script below is only for a companion running from a Git checkout.

## Update a Windows source checkout

From PowerShell in the companion checkout:

```powershell
.\scripts\update-companion.ps1
```

Requires Git, the .NET 8 SDK, an interactive Windows desktop, and a clean `main` checkout. If the existing tray app is elevated, Windows may prompt for permission to identify and stop it. Only that stop operation is elevated; the build and new tray inherit your PowerShell's privileges. `-WhatIf` previews the operation without stopping, fetching, building or starting anything.

The updater fetches `origin/main` before downtime. It refuses local-only commits or divergence. It then stops this checkout's Release companion and bundled Node worker, fast-forwards to the fetched commit, builds **x64 Release / net8.0-windows**, and starts the new tray app from:

```text
src\OpenClaw.Node\bin\x64\Release\net8.0-windows\OpenClaw.Node.exe
```

Shortcuts stay valid. A missing or outdated bundled `node.exe` is restored at build time from nodejs.org and SHA-256 checked against the repository pin. If the update fails before the build, the updater tries to restart the previously running app. If the build fails, it does **not** start a possibly partial Release; fix the error and rerun. Application logs are under `%LOCALAPPDATA%\OpenClaw Companion\logs`.

You don't need a watchdog. Use **Settings → Start with Windows** for sign-in startup. If another PC still uses the old scheduled task, disable and stop **OpenClawNodeWatchdog** there once, before updating. The updater does not change scheduled tasks or Windows startup settings.
