using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using OpenClaw.Node.Tray;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Node
{
    class Program
    {
        static void Main(string[] args)
        {
#if WINDOWS
            if (args.SequenceEqual(new[] { "--window-observation-worker" }))
            {
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                Environment.ExitCode = NativeWindowObservation.Run();
                return;
            }
            Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
            // Screen capture and SendInput must share physical-pixel coordinates
            // across displays with different scale factors, before any HWND exists.
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
#endif
            MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task MainAsync(string[] args)
        {
            if (HasArg(args, "--version"))
            {
                Console.WriteLine($"OpenClaw Windows Companion {BuildInfo.BuildVersion}");
                return;
            }
            using var logs = CompanionLog.Start();
            if (HasArg(args, "--maintain-browser-profile"))
            {
                try
                {
                    var settings = new CompanionSettingsStore().Load();
                    var legacy = GetArgValue(args, "--legacy-browser-profile");
                    var result = await Task.Run(() => new BrowserProfileService(legacyDirectory: string.IsNullOrWhiteSpace(legacy) ? null : legacy)
                        .Maintain(settings.BrowserDiskCacheMiB, settings.BrowserCodeCacheMiB, clear: HasArg(args, "--clear-browser-cache")));
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
                catch (Exception error) { Environment.ExitCode = 1; Console.Error.WriteLine("[BROWSER] Profile maintenance failed: " + error.Message); }
                return;
            }
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("[FATAL] Unhandled exception: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => Console.Error.WriteLine("[ERROR] Unobserved background exception: " + e.Exception);
            try { await RunAsync(args); }
            catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine("[FATAL] Startup failure: " + ex); }
            finally { Console.WriteLine($"[INFO] Companion exited. ExitCode={Environment.ExitCode}"); }
        }

        private static async Task RunAsync(string[] args)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            Console.WriteLine($"OpenClaw Node for Windows starting... build={BuildInfo.BuildVersion}");
            Console.WriteLine($"[VERSION] Gateway source={BuildInfo.GatewaySourceCommit} synced={BuildInfo.GatewaySyncDate} revision={BuildInfo.CompanionRevision}");
            Console.WriteLine($"[STARTUP] pid={Environment.ProcessId} runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} baseDirectory={AppContext.BaseDirectory}");
            Console.WriteLine($"[LOG] Persistent logs: {CompanionLog.DirectoryPath}");

            var settingsStore = new CompanionSettingsStore();
            var hasSavedSettings = settingsStore.HasSavedSettings;
            var settings = settingsStore.Load();
            ManagedBrowserHost.ConfigureCache(settings);
            try
            {
                var profile = await ManagedBrowserHost.MaintainProfileAsync();
                Console.WriteLine($"[BROWSER] Profile={profile.ProfilePath} migrated={profile.Migrated} removedCacheBytes={profile.RemovedCacheBytes} browserRunning={profile.BrowserRunning}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { Console.WriteLine("[BROWSER] Profile/cache maintenance deferred: " + error.Message); }
            using var browserMaintenance = new System.Threading.Timer(_ => ManagedBrowserHost.MaintainProfileIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var forceTray = HasArg(args, "--tray");
            var diagnostic = HasArg(args, "--diagnose-connection");
            var disableTray = HasArg(args, "--no-tray") || diagnostic;
            var trayEnabled = !disableTray && (forceTray || OperatingSystem.IsWindows());
            var (url, token) = ResolveConnectionSettings(args, settings, hasSavedSettings);
            CompanionLog.RegisterSecret(token);
            CompanionLog.RegisterSecret(settings.GatewayToken);
            CompanionLog.RegisterSecret(settings.DesktopVncPassword);
            var hasGatewayToken = !string.IsNullOrWhiteSpace(token);
            var hasStoredNodeToken = !hasGatewayToken && HasStoredDeviceToken(url, "node");
            var hasGatewayCredential = hasGatewayToken || hasStoredNodeToken;
            Console.WriteLine($"[STARTUP] Gateway={url} settingsSaved={hasSavedSettings} sharedCredentialPresent={hasGatewayToken} storedNodeCredentialPresent={hasStoredNodeToken} tray={trayEnabled} diagnostic={diagnostic}");
            Console.WriteLine($"[POLICY] Unrestricted execution policies permitted by local setting: {settings.AllowUnrestrictedExecutionPolicy}");
            Console.WriteLine($"[FILES] Local file read opt-in: {settings.EnableFileRead}; local file write opt-in: {settings.EnableFileWrite}");

            if (!hasGatewayCredential && !trayEnabled)
            {
                Console.WriteLine("[FATAL] Missing gateway token. Set OPENCLAW_GATEWAY_TOKEN, pass --gateway-token <token>, or run with --tray and open Settings.");
                Environment.ExitCode = 2;
                return;
            }

            try
            {
            var manifest = NodeCapabilityRegistry.Build(settings);
            var instanceId = Guid.NewGuid().ToString();
            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.MinimumNodeProtocolVersion,
                MaxProtocol = Constants.MaximumNodeProtocolVersion,
                Role = "node",
                Client = new Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "displayName", Environment.MachineName },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", BuildInfo.BuildVersion },
                    { "buildId", BuildInfo.BuildId },
                    { "timeZone", GetLocalIanaTimeZone() },
                    { "instanceId", instanceId },
                    { "deviceFamily", "Windows" }
                },
                Caps = manifest.Capabilities.ToList(),
                Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                UserAgent = Environment.OSVersion.VersionString,
                Scopes = new List<string>(),
                Commands = manifest.GatewayCommands.ToList(),
                ComputerUse = settings.EnableComputerControl || settings.EnableWindowInspection ? ComputerControlService.Descriptor(instanceId, settings.EnableWindowInspection, settings.EnableComputerControl) : null,
                Permissions = new Dictionary<string, bool>()
            };

            var operatorParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "operator",
                Scopes = new List<string> { "operator.read", "operator.write" },
                Client = new Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "displayName", $"{Environment.MachineName} Companion" },
                    { "platform", "windows" },
                    { "mode", "ui" },
                    { "version", BuildInfo.BuildVersion },
                    { "buildId", BuildInfo.BuildId },
                    { "timeZone", GetLocalIanaTimeZone() },
                    { "instanceId", instanceId },
                    { "deviceFamily", "Windows" }
                },
                Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                UserAgent = Environment.OSVersion.VersionString,
            };

            using var cts = new CancellationTokenSource();
            if (diagnostic) { Environment.ExitCode = 3; cts.CancelAfter(TimeSpan.FromSeconds(25)); }
            var restartRequested = false;

            var core = new CoreMethodService(startedAtUtc);
            var ipcCredential = new IpcCredentialService().LoadOrCreate();
            CompanionLog.RegisterSecret(ipcCredential);
            using var ipc = new IpcPipeServerService(version: BuildInfo.BuildVersion, authToken: ipcCredential);
            var gatewayOptions = new GatewayConnectionOptions { TlsCertificateSha256 = settings.TlsCertificateSha256 };
            using var connection = new GatewayConnection(url, token, connectParams, gatewayOptions);
            using var operatorConnection = settings.EnableTalkPushToTalk || settings.EnableCanvas
                ? new GatewayConnection(url, token, operatorParams, gatewayOptions)
                : null;
            var browserProxy = new BrowserProxyService();
            ITrayHost? trayHost = null;
            using var executor = new NodeCommandExecutor(
                connection,
                browserProxyService: browserProxy,
                settings: settings,
                operatorClient: operatorConnection,
                instanceId: instanceId,
                notificationSink: (title, body, cancellationToken) =>
                    trayHost?.ShowNotificationAsync(title, body, cancellationToken)
                    ?? Task.FromException(new InvalidOperationException("Windows notification tray is not active")));
            using var discovery = new DiscoveryService(connectParams, url);
            var trayStatus = new TrayStatusBroadcaster(buildVersion: BuildInfo.BuildVersion);
            var reconnectStartedAtUtc = (DateTimeOffset?)null;
            long? lastReconnectMs = null;
            var authDialogShown = false;
            var onboarding = OnboardingAdvisor.Evaluate(url, token, hasStoredNodeToken);

            void SetTray(NodeRuntimeState state, string message)
            {
                trayStatus.Set(state, message, core.PendingPairCount, lastReconnectMs, onboarding.StatusText, connection.IsConnected ? connection.ServerVersion : null);
            }

            if (trayEnabled)
            {
                Action? checkForUpdates = null;
                Func<string>? updateLabel = null;
#if WINDOWS
                var updates = CompanionUpdateController.ForInstalledApp(cts.Token, () => cts.Cancel());
                if (updates != null)
                {
                    checkForUpdates = updates.CheckFromTray;
                    updateLabel = () => updates.MenuText;
                    _ = updates.PollAsync();
                }
#endif
                trayHost = OperatingSystem.IsWindows()
                    ? new WindowsNotifyIconTrayHost(
                        onCheckForUpdates: checkForUpdates, updateLabel: updateLabel,
                        log: msg => Console.WriteLine(msg),
                        onOpenLogs: () => OpenLogsFolder(),
                        onClearBrowserCache: BrowserCacheDialog.Show,
                        onOpenSettings: () =>
                        {
                            if (!CompanionSettingsDialog.Show(settingsStore)) return;
                            restartRequested = true;
                            cts.Cancel();
                        },
                        onRestart: () => { restartRequested = true; cts.Cancel(); },
                        onExit: () => cts.Cancel(),
                        onCopyDiagnostics: () => CopyDiagnosticsToClipboard(BuildDiagnostics(startedAtUtc, url, trayStatus.Current, core.PendingPairCount, lastReconnectMs)))
                    : new NoOpTrayHost(msg => Console.WriteLine(msg));
            }

            connection.OnLog += msg =>
            {
                Console.WriteLine(msg);

                if (msg.Contains("Reconnecting in", StringComparison.OrdinalIgnoreCase))
                {
                    reconnectStartedAtUtc ??= DateTimeOffset.UtcNow;
                    SetTray(NodeRuntimeState.Reconnecting, msg);
                }
            };
            connection.OnConnected += () =>
            {
                Console.WriteLine("[INFO] Connected to Gateway.");
                Console.WriteLine($"[VERSION] Connected Gateway={connection.ServerVersion ?? "unknown"}; companion={BuildInfo.BuildVersion}; referenceSourcePackage={BuildInfo.GatewaySourceVersion}");
                if (diagnostic) { Environment.ExitCode = 0; cts.Cancel(); return; }
                authDialogShown = false;
                if (reconnectStartedAtUtc.HasValue)
                {
                    lastReconnectMs = (long)(DateTimeOffset.UtcNow - reconnectStartedAtUtc.Value).TotalMilliseconds;
                    reconnectStartedAtUtc = null;
                }
                SetTray(NodeRuntimeState.Connected, "Connected to Gateway");
                _ = discovery.TriggerAnnounceAsync("gateway-connected", CancellationToken.None);
            };
            connection.OnTransportFailure += _ =>
            {
                SetTray(NodeRuntimeState.Reconnecting, "Transport connection failed; see logs");
                if (diagnostic) { Environment.ExitCode = 3; cts.Cancel(); }
            };
            connection.OnDisconnected += () =>
            {
                _ = executor.ResetSessionAsync();
                Console.WriteLine("[INFO] Disconnected from Gateway.");
                reconnectStartedAtUtc = DateTimeOffset.UtcNow;
                SetTray(NodeRuntimeState.Disconnected, "Disconnected from Gateway");
            };
            connection.OnConnectFailure += failure =>
            {
                var details = failure.Error.Details;
                var code = details?.ValueKind == JsonValueKind.Object && details.Value.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? failure.Error.Code : failure.Error.Code;
                var pairing = code.StartsWith("PAIRING_", StringComparison.Ordinal);
                var hint = details?.ValueKind == JsonValueKind.Object && details.Value.TryGetProperty("remediationHint", out var remediation) && remediation.ValueKind == JsonValueKind.String
                    ? remediation.GetString() : null;
                var message = failure.DeviceTokenRetryPending ? "Retrying with stored device credentials" : failure.Error.Message;
                SetTray(failure.ReconnectPaused ? NodeRuntimeState.Disconnected : NodeRuntimeState.Reconnecting, message);
                Console.Error.WriteLine($"[Gateway] Connect failure detail={code} paused={failure.ReconnectPaused} deviceTokenRetryPending={failure.DeviceTokenRetryPending}: {failure.Error.Message}");
                var guidance = OnboardingAdvisor.ConnectionRemediation(code, failure.ReconnectPaused, hint);
                if (pairing || !string.IsNullOrWhiteSpace(hint)) Console.Error.WriteLine("[Gateway] Remediation: " + guidance);
                if (diagnostic && !failure.DeviceTokenRetryPending) { Environment.ExitCode = 2; cts.Cancel(); return; }
                if (!trayEnabled || !failure.ReconnectPaused || authDialogShown) return;
                authDialogShown = true;
                ShowUserWarningDialog(
                    pairing ? "OpenClaw Pairing Required" : "OpenClaw Connection Requires Attention",
                    failure.Error.Message + "\n\n" + guidance);
            };
            connection.OnConnectRejected += errorText => Console.WriteLine($"[connect] {errorText}");
            if (operatorConnection != null)
            {
                operatorConnection.OnLog += message => Console.WriteLine($"[operator sidecar] {message}");
                operatorConnection.OnConnectRejected += message => Console.WriteLine($"[operator sidecar] rejected: {message}");
            }
            ipc.OnLog += msg => Console.WriteLine(msg);
            discovery.OnLog += msg => Console.WriteLine(msg);
            connection.OnEventReceived += evt =>
            {
                if (core.HandleGatewayEvent(evt))
                {
                    Console.WriteLine($"[PAIR] pending request event handled: {evt.Event}");
                    SetTray(trayStatus.Current.State, trayStatus.Current.Message);
                }
            };

            connection.OnNodeInvokeContext += async context =>
            {
                var req = context.Request;
                Console.WriteLine($"[INVOKE] Received bridge command: {req.Command}");
                return await executor.ExecuteContextAsync(context);
            };

            if (trayHost != null)
            {
                trayStatus.OnStatusChanged += snapshot =>
                {
                    _ = trayHost.UpdateAsync(snapshot, CancellationToken.None);
                };
            }

            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("Shutting down...");
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                if (trayHost != null)
                {
                    try
                    {
                        await trayHost.StartAsync(cts.Token);
                        SetTray(NodeRuntimeState.Starting, "Starting node runtime");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TRAY] Startup failed, continuing headless: {ex.Message}");
                        await trayHost.StopAsync();
                        trayHost = null;
                    }
                }

                if (!hasGatewayCredential)
                {
                    SetTray(NodeRuntimeState.Disconnected, "Setup needed: add gateway token");
                    Console.WriteLine("[WARN] Gateway token missing. Tray mode is active; open Settings, enter the token, and restart the companion.");
                    ShowUserWarningDialog(
                        title: "OpenClaw Node Setup Required",
                        message: $"{onboarding.StatusText}.\n\n{onboarding.ActionHint}.\n\nOpen the tray menu → Settings, save your changes, and the companion will restart.");
                    await WaitUntilCanceledAsync(cts.Token);
                    return;
                }

                if (!diagnostic) { discovery.Start(cts.Token); ipc.Start(cts.Token); }
                var runTask = connection.StartAsync(cts.Token);
                var operatorTask = operatorConnection?.StartAsync(cts.Token) ?? Task.CompletedTask;
                var telemetryTask = PublishHostTelemetryAsync(connection, cts.Token);
                await Task.WhenAll(runTask, operatorTask, telemetryTask);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine($"[FATAL] Runtime failure: {ex}");
            }
            finally
            {
                connection.Stop();
                await discovery.StopAsync();
                await ipc.StopAsync();

                if (trayHost != null)
                {
                    SetTray(NodeRuntimeState.Stopped, "Node runtime stopped");
                    await trayHost.StopAsync();
                }

                if (restartRequested)
                {
                    TryScheduleSelfRestart();
                }
            }
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine($"[FATAL] Startup failed before runtime guard: {ex}");
                if (trayEnabled)
                {
                    ShowUserWarningDialog(
                        "OpenClaw Node Setup Error",
                        "Node startup failed due to invalid configuration (for example gateway URL).\n\nOpen the tray menu → Settings, fix the values, and save to restart the companion.");
                }
            }
        }

        private static async Task WaitUntilCanceledAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // expected
            }
        }

        private static async Task PublishHostTelemetryAsync(GatewayConnection connection, CancellationToken cancellationToken)
        {
            var device = new WindowsDeviceService();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try
            {
                do
                {
                    if (!connection.IsConnected) continue;
                    try
                    {
                        await connection.SendNodeEventAsync("node.host.stats", device.GetHostStats(), cancellationToken);
                        if (WindowsDeviceService.GetSystemIdleSeconds() is int idle)
                            await connection.SendNodeEventAsync("node.presence.activity", new { idleSeconds = idle, source = "system" }, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { Console.WriteLine($"[telemetry] {ex.Message}"); }
                } while (await timer.WaitForNextTickAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private static bool HasStoredDeviceToken(string gatewayUrl, string role)
        {
            try
            {
                var endpoint = new Uri(gatewayUrl);
                var identity = new DeviceIdentityService().LoadOrCreate();
                return new DeviceTokenStore().Load(endpoint, identity.DeviceId, role) is { Token.Length: > 0 };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTH] Stored device-token lookup skipped: {ex.Message}");
                return false;
            }
        }

        internal static (string Url, string Token) ResolveConnectionSettings(string[] args, CompanionSettings settings, bool hasSavedSettings)
        {
            static string FirstNonEmpty(string? argument, string? environment, string fallback)
                => !string.IsNullOrWhiteSpace(argument) ? argument : !string.IsNullOrWhiteSpace(environment) ? environment : fallback;
            return (
                FirstNonEmpty(GetArgValue(args, "--gateway-url"), Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_URL"), hasSavedSettings ? settings.GatewayUrl : "ws://127.0.0.1:18789"),
                FirstNonEmpty(GetArgValue(args, "--gateway-token"), Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN"), hasSavedSettings ? settings.GatewayToken ?? string.Empty : string.Empty));
        }

        private static void OpenLogsFolder()
        {
            try
            {
                var dir = CompanionLog.DirectoryPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var start = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = QuoteForCmd(dir),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ChildProcessSecurity.ScrubSensitiveEnvironment(start);
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRAY] Open logs folder failed: {ex.Message}");
            }
        }

        private static void ShowUserWarningDialog(string title, string message)
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                var messageBoxType = Type.GetType("System.Windows.Forms.MessageBox, System.Windows.Forms");
                var show = messageBoxType?.GetMethod("Show", new[] { typeof(string), typeof(string) });
                if (show != null)
                {
                    show.Invoke(null, new object[] { message, title });
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRAY] Warning dialog failed: {ex.Message}");
            }

            Console.WriteLine($"[WARN] {title}: {message}");
        }

        private static string BuildDiagnostics(DateTimeOffset startedAtUtc, string gatewayUrl, TrayStatusSnapshot snapshot, int pendingPairs, long? lastReconnectMs)
        {
            var uptime = (long)(DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds;
            var reconnectText = lastReconnectMs.HasValue ? $"{lastReconnectMs.Value}ms" : "n/a";

            return CompanionLog.Redact(string.Join(Environment.NewLine, new[]
            {
                "OpenClaw Windows Node Diagnostics",
                $"timeUtc: {DateTimeOffset.UtcNow:O}",
                $"gatewayUrl: {gatewayUrl}",
                $"state: {snapshot.State}",
                $"message: {snapshot.Message}",
                $"pendingPairs: {pendingPairs}",
                $"onboarding: {snapshot.OnboardingStatus}",
                $"lastReconnect: {reconnectText}",
                $"uptimeSeconds: {uptime}",
                $"pid: {Environment.ProcessId}",
                $"buildVersion: {BuildInfo.BuildVersion}",
                $"windowsPackage: {(string.IsNullOrEmpty(BuildInfo.WindowsPackageBuild) ? "source" : BuildInfo.WindowsPackageBuild)}",
                $"gatewaySourceVersion: {BuildInfo.GatewaySourceVersion}",
                $"gatewaySourceCommit: {BuildInfo.GatewaySourceCommit}",
                $"gatewaySyncDate: {BuildInfo.GatewaySyncDate}",
                $"companionRevision: {BuildInfo.CompanionRevision}",
                $"connectedGatewayVersion: {snapshot.GatewayVersion ?? "unknown"}",
                $"browserProfile: {BrowserProfileService.Default.ProfilePath}",
                $"logsDirectory: {CompanionLog.DirectoryPath}"
            }));
        }

        private static void CopyDiagnosticsToClipboard(string text)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("[TRAY] Copy diagnostics skipped on non-Windows host.");
                return;
            }

            try
            {
                var escaped = text.Replace("'", "''");
                var start = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Set-Clipboard -Value '{escaped}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ChildProcessSecurity.ScrubSensitiveEnvironment(start);
                Process.Start(start);
                Console.WriteLine("[TRAY] Diagnostics copied to clipboard.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRAY] Copy diagnostics failed: {ex.Message}");
            }
        }

        private static void TryScheduleSelfRestart(int delaySeconds = 1)
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath))
                {
                    Console.WriteLine("[TRAY] Restart requested but process path is unavailable.");
                    return;
                }

                delaySeconds = Math.Clamp(delaySeconds, 1, 120);

                var args = Environment.GetCommandLineArgs();
                var argBuilder = new System.Text.StringBuilder();
                for (var i = 1; i < args.Length; i++)
                {
                    if (argBuilder.Length > 0) argBuilder.Append(' ');
                    argBuilder.Append(QuoteForCmd(args[i]));
                }

                var start = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t {delaySeconds} /nobreak >nul && start \"\" {QuoteForCmd(processPath)} {argBuilder} && taskkill /PID {Environment.ProcessId} /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ChildProcessSecurity.ScrubSensitiveEnvironment(start);
                Process.Start(start);

                Console.WriteLine($"[TRAY] Restart scheduled in {delaySeconds}s.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRAY] Failed to schedule restart: {ex.Message}");
            }
        }

        private static string QuoteForCmd(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static bool HasArg(string[] args, string key)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string? GetArgValue(string[] args, string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static string GetLocalIanaTimeZone()
            => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id;
    }
}
