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
        static async Task Main(string[] args)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            Console.WriteLine($"OpenClaw Node for Windows starting... build={BuildInfo.BuildVersion}");

            var configPath = GetOpenClawConfigPath();
            var settingsStore = new CompanionSettingsStore();
            var hasSavedSettings = settingsStore.HasSavedSettings;
            var settings = settingsStore.Load();
            var forceTray = HasArg(args, "--tray");
            var disableTray = HasArg(args, "--no-tray");
            var trayEnabled = !disableTray && (forceTray || OperatingSystem.IsWindows());
            string url = ResolveGatewayUrl(args, out var configReadErrorUrl);
            string token = ResolveGatewayToken(args, out var configReadErrorToken);
            var explicitUrl = HasArg(args, "--gateway-url") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_URL"));
            var explicitToken = HasArg(args, "--gateway-token") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN"));
            if (!explicitUrl && hasSavedSettings) url = settings.GatewayUrl;
            if (!explicitToken && hasSavedSettings) token = settings.GatewayToken ?? string.Empty;
            if (!explicitToken && !hasSavedSettings && !string.IsNullOrWhiteSpace(token))
            {
                // One-time, read-only import from the legacy config into the
                // companion's CurrentUser-DPAPI store.
                settings.GatewayUrl = url;
                settings.GatewayToken = token;
                settingsStore.Save(settings);
            }
            var configReadError = configReadErrorUrl ?? configReadErrorToken;
            var hasGatewayToken = !string.IsNullOrWhiteSpace(token);
            var hasStoredNodeToken = !hasGatewayToken && HasStoredDeviceToken(url, "node");
            var hasGatewayCredential = hasGatewayToken || hasStoredNodeToken;

            if (!hasGatewayCredential && !trayEnabled)
            {
                Console.WriteLine("[FATAL] Missing gateway token. Set OPENCLAW_GATEWAY_TOKEN, pass --gateway-token <token>, or run with --tray and open Settings.");
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
                    { "version", $"dev+{BuildInfo.BuildVersion}" },
                    { "instanceId", instanceId },
                    { "deviceFamily", "Windows" }
                },
                Caps = manifest.Capabilities.ToList(),
                Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                UserAgent = Environment.OSVersion.VersionString,
                Scopes = new List<string>(),
                Commands = manifest.GatewayCommands.ToList(),
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
                    { "version", $"dev+{BuildInfo.BuildVersion}" },
                    { "instanceId", instanceId },
                    { "deviceFamily", "Windows" }
                },
                Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                UserAgent = Environment.OSVersion.VersionString,
            };

            using var cts = new CancellationTokenSource();
            var restartRequested = false;

            var core = new CoreMethodService(startedAtUtc);
            var ipcCredential = new IpcCredentialService().LoadOrCreate();
            using var ipc = new IpcPipeServerService(version: "dev", authToken: ipcCredential);
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
            var onboarding = OnboardingAdvisor.Evaluate(url, token, configPath, configReadError);

            void SetTray(NodeRuntimeState state, string message)
            {
                trayStatus.Set(state, message, core.PendingPairCount, lastReconnectMs, onboarding.StatusText);
            }

            if (trayEnabled)
            {
                trayHost = OperatingSystem.IsWindows()
                    ? new WindowsNotifyIconTrayHost(
                        log: msg => Console.WriteLine(msg),
                        onOpenLogs: () => OpenLogsFolder(),
                        onOpenConfig: () => OpenConfigFile(configPath),
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

                var lowered = msg.ToLowerInvariant();
                var isAuthSignal = lowered.Contains("connect rejected") || lowered.Contains("unauthorized") || lowered.Contains("forbidden") || lowered.Contains("auth") || lowered.Contains("token") || lowered.Contains("pre-connect-close");
                if (isAuthSignal && !authDialogShown && hasGatewayCredential)
                {
                    authDialogShown = true;
                    SetTray(NodeRuntimeState.Disconnected, "Authentication failed (check token)");
                    ShowUserWarningDialog(
                        "OpenClaw Authentication Failed",
                        "The gateway rejected node authentication.\n\nOpen Settings, verify the Gateway token, save, and restart the companion.");
                }

                if (msg.Contains("Reconnecting in", StringComparison.OrdinalIgnoreCase))
                {
                    reconnectStartedAtUtc ??= DateTimeOffset.UtcNow;
                    SetTray(NodeRuntimeState.Reconnecting, msg);
                }
            };
            connection.OnConnected += () =>
            {
                Console.WriteLine("[INFO] Connected to Gateway.");
                authDialogShown = false;
                if (reconnectStartedAtUtc.HasValue)
                {
                    lastReconnectMs = (long)(DateTimeOffset.UtcNow - reconnectStartedAtUtc.Value).TotalMilliseconds;
                    reconnectStartedAtUtc = null;
                }
                SetTray(NodeRuntimeState.Connected, "Connected to Gateway");
                _ = discovery.TriggerAnnounceAsync("gateway-connected", CancellationToken.None);
            };
            connection.OnDisconnected += () =>
            {
                Console.WriteLine("[INFO] Disconnected from Gateway.");
                reconnectStartedAtUtc = DateTimeOffset.UtcNow;
                SetTray(NodeRuntimeState.Disconnected, "Disconnected from Gateway");
            };
            connection.OnConnectRejected += errorText =>
            {
                var lowered = (errorText ?? string.Empty).ToLowerInvariant();
                var isAuthIssue = lowered.Contains("token") || lowered.Contains("auth") || lowered.Contains("unauthorized") || lowered.Contains("forbidden") || lowered.Contains("invalid");
                if (!isAuthIssue || authDialogShown) return;

                authDialogShown = true;
                SetTray(NodeRuntimeState.Disconnected, "Authentication failed (check token)");
                ShowUserWarningDialog(
                    "OpenClaw Authentication Failed",
                    "Gateway rejected this node authentication.\n\nOpen Settings, verify the Gateway token, save, and restart the companion.");
            };
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
                return await executor.ExecuteAsync(req, context.CancellationToken);
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
                    var details = string.IsNullOrWhiteSpace(onboarding.Details) ? string.Empty : $"\n\nDetails: {onboarding.Details}";
                    ShowUserWarningDialog(
                        title: "OpenClaw Node Setup Required",
                        message: $"{onboarding.StatusText}.\n\n{onboarding.ActionHint}.\n\nOpen the tray menu → Settings, save your changes, and the companion will restart.{details}");
                    await WaitUntilCanceledAsync(cts.Token);
                    return;
                }

                discovery.Start(cts.Token);
                ipc.Start(cts.Token);
                var runTask = connection.StartAsync(cts.Token);
                var operatorTask = operatorConnection?.StartAsync(cts.Token) ?? Task.CompletedTask;
                await Task.WhenAll(runTask, operatorTask);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] {ex.Message}");
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
                Console.WriteLine($"[FATAL] Startup failed before runtime guard: {ex.Message}");
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

        private static string ResolveGatewayUrl(string[] args, out string? configReadError)
        {
            configReadError = null;

            var fromArgs = GetArgValue(args, "--gateway-url");
            if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs;

            var fromEnv = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_URL");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

            var fromConfig = TryReadGatewayUrlFromOpenClawConfig(out configReadError);
            if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;

            return "ws://127.0.0.1:18789";
        }

        private static string ResolveGatewayToken(string[] args, out string? configReadError)
        {
            configReadError = null;

            var fromArgs = GetArgValue(args, "--gateway-token");
            if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs;

            var fromEnv = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

            return TryReadGatewayTokenFromOpenClawConfig(out configReadError) ?? string.Empty;
        }

        private static string GetOpenClawConfigPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "openclaw.json");
        }

        private static void OpenLogsFolder()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = Path.Combine(home, ".openclaw");
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

        private static void OpenConfigFile(string configPath)
        {
            try
            {
                var parent = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

                if (!File.Exists(configPath))
                {
                    File.WriteAllText(configPath,
                        "{\n  \"gateway\": {\n    \"host\": \"127.0.0.1\",\n    \"port\": 18789,\n    \"auth\": {\n      \"token\": \"\"\n    }\n  }\n}\n");
                }

                var start = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = QuoteForCmd(configPath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ChildProcessSecurity.ScrubSensitiveEnvironment(start);
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRAY] Open config failed: {ex.Message}");
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

            return string.Join(Environment.NewLine, new[]
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
                $"buildVersion: {BuildInfo.BuildVersion}"
            });
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

        private static string? TryReadGatewayUrlFromOpenClawConfig(out string? error)
        {
            error = null;

            if (!TryReadGatewaySection(out var gateway, out error)) return null;
            return BuildGatewayUrlFromGatewaySection(gateway, out error);
        }

        internal static string? BuildGatewayUrlFromGatewaySection(JsonElement gateway, out string? error)
        {
            error = null;

            var host = "127.0.0.1";
            if (gateway.TryGetProperty("host", out var hostEl))
            {
                if (hostEl.ValueKind != JsonValueKind.String)
                {
                    error = "gateway.host must be a string";
                    return null;
                }

                var configuredHost = hostEl.GetString();
                if (string.IsNullOrWhiteSpace(configuredHost))
                {
                    error = "gateway.host must not be empty";
                    return null;
                }

                host = configuredHost.Trim();
            }

            var port = 18789;
            if (gateway.TryGetProperty("port", out var portEl))
            {
                if (portEl.ValueKind != JsonValueKind.Number)
                {
                    error = "gateway.port must be numeric";
                    return null;
                }

                if (!portEl.TryGetInt32(out var parsedPort))
                {
                    error = "gateway.port must be an integer in range 1..65535";
                    return null;
                }

                if (parsedPort is < 1 or > 65535)
                {
                    error = "gateway.port must be in range 1..65535";
                    return null;
                }

                port = parsedPort;
            }

            var normalizedHost = host.Contains(':') && !(host.StartsWith("[") && host.EndsWith("]"))
                ? $"[{host}]"
                : host;

            return $"ws://{normalizedHost}:{port}";
        }

        private static string? TryReadGatewayTokenFromOpenClawConfig(out string? error)
        {
            error = null;

            if (!TryReadGatewaySection(out var gateway, out error)) return null;
            if (!gateway.TryGetProperty("auth", out var auth))
            {
                error = "gateway.auth section missing";
                return null;
            }

            if (!auth.TryGetProperty("token", out var tokenEl))
            {
                error = "gateway.auth.token missing";
                return null;
            }

            if (tokenEl.ValueKind != JsonValueKind.String)
            {
                error = "gateway.auth.token must be a string";
                return null;
            }

            return tokenEl.GetString();
        }

        private static bool TryReadGatewaySection(out JsonElement gateway, out string? error)
        {
            gateway = default;
            error = null;

            var path = GetOpenClawConfigPath();
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("gateway", out var gw))
                {
                    error = "gateway section missing";
                    return false;
                }

                gateway = gw.Clone();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
