using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Services
{
    internal sealed class ManagedBrowserState
    {
        public string LaunchMode { get; init; } = "attach-existing";
        public string BrowserName { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
        public string BrowserUrl { get; init; } = BundledBrowserRuntimeLocator.DefaultBrowserUrl;
        public string UserDataDir { get; init; } = string.Empty;
        public int? ProcessId { get; init; }
        public bool DevToolsReady { get; init; }
        public string BrowserVersion { get; init; } = string.Empty;
    }

    internal static class ManagedBrowserHost
    {
        private static readonly HttpClient DevToolsHttp = new HttpClient();
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static Process? _managedProcess;
        private static ManagedBrowserState? _lastState;

        public static async Task<ManagedBrowserState> EnsureReadyAsync(string profileName, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken);
            try
            {
                var existing = await TryGetDevToolsVersionAsync(cancellationToken);
                if (existing != null)
                {
                    _lastState = new ManagedBrowserState
                    {
                        LaunchMode = (_managedProcess != null && !_managedProcess.HasExited) ? "managed-launch" : "attach-existing",
                        BrowserName = InferBrowserName(existing.Browser),
                        BrowserUrl = BundledBrowserRuntimeLocator.DefaultBrowserUrl,
                        UserDataDir = GetUserDataDirectory(),
                        ProcessId = (_managedProcess != null && !_managedProcess.HasExited) ? _managedProcess.Id : null,
                        DevToolsReady = true,
                        BrowserVersion = existing.Browser ?? string.Empty,
                        ExecutablePath = _lastState?.ExecutablePath ?? string.Empty,
                    };
                    BrowserLog.Info(profileName, $"managed browser ready mode={_lastState.LaunchMode} browser={_lastState.BrowserName} pid={_lastState.ProcessId}");
                    return _lastState;
                }

                if (!OperatingSystem.IsWindows())
                {
                    throw new InvalidOperationException("browser backend available, but no supported browser installation was found on this host");
                }

                if (_managedProcess != null && !_managedProcess.HasExited)
                {
                    var ready = await WaitForDevToolsVersionAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    if (ready != null)
                    {
                        _lastState = new ManagedBrowserState
                        {
                            LaunchMode = "managed-launch",
                            BrowserName = InferBrowserName(ready.Browser),
                            BrowserUrl = BundledBrowserRuntimeLocator.DefaultBrowserUrl,
                            UserDataDir = GetUserDataDirectory(),
                            ProcessId = _managedProcess.Id,
                            DevToolsReady = true,
                            BrowserVersion = ready.Browser ?? string.Empty,
                            ExecutablePath = _lastState?.ExecutablePath ?? string.Empty,
                        };
                        return _lastState;
                    }

                    TryKill(_managedProcess);
                    _managedProcess = null;
                }

                var candidates = DiscoverInstalledBrowsers().ToList();
                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException("browser backend available, but no supported browser installation was found");
                }

                foreach (var candidate in candidates)
                {
                    BrowserLog.Info(profileName, $"trying managed browser launch name={candidate.Name} path={candidate.Path}");
                    var launched = TryLaunch(candidate);
                    if (launched == null)
                    {
                        continue;
                    }

                    _managedProcess = launched;
                    var version = await WaitForDevToolsVersionAsync(TimeSpan.FromSeconds(15), cancellationToken);
                    if (version != null)
                    {
                        _lastState = new ManagedBrowserState
                        {
                            LaunchMode = "managed-launch",
                            BrowserName = candidate.Name,
                            ExecutablePath = candidate.Path,
                            BrowserUrl = BundledBrowserRuntimeLocator.DefaultBrowserUrl,
                            UserDataDir = GetUserDataDirectory(),
                            ProcessId = launched.Id,
                            DevToolsReady = true,
                            BrowserVersion = version.Browser ?? string.Empty,
                        };
                        BrowserLog.Info(profileName, $"managed browser launch ok name={candidate.Name} pid={launched.Id}");
                        return _lastState;
                    }

                    BrowserLog.Error(profileName, $"managed browser launch did not expose DevTools in time name={candidate.Name} path={candidate.Path}");
                    TryKill(launched);
                    _managedProcess = null;
                }

                throw new InvalidOperationException("supported browser found, but DevTools attach endpoint is unavailable after managed launch");
            }
            finally
            {
                Gate.Release();
            }
        }

        public static ManagedBrowserState? GetLastState() => _lastState;

        private static Process? TryLaunch((string Name, string Path) candidate)
        {
            try
            {
                var userDataDir = GetUserDataDirectory();
                Directory.CreateDirectory(userDataDir);

                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate.Path,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };

                startInfo.ArgumentList.Add($"--remote-debugging-port={new Uri(BundledBrowserRuntimeLocator.DefaultBrowserUrl).Port}");
                startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
                startInfo.ArgumentList.Add($"--user-data-dir={userDataDir}");
                startInfo.ArgumentList.Add("--no-first-run");
                startInfo.ArgumentList.Add("--no-default-browser-check");
                startInfo.ArgumentList.Add("--new-window");
                startInfo.ArgumentList.Add("about:blank");

                return Process.Start(startInfo);
            }
            catch
            {
                return null;
            }
        }

        private static string GetUserDataDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, "runtimes", "browser", "managed-profile");
        }

        private static async Task<DevToolsVersionInfo?> TryGetDevToolsVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                return await DevToolsHttp.GetFromJsonAsync<DevToolsVersionInfo>($"{BundledBrowserRuntimeLocator.DefaultBrowserUrl}/json/version", cts.Token);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<DevToolsVersionInfo?> WaitForDevToolsVersionAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var version = await TryGetDevToolsVersionAsync(cancellationToken);
                if (version != null)
                {
                    return version;
                }

                await Task.Delay(500, cancellationToken);
            }

            return null;
        }

        private static IEnumerable<(string Name, string Path)> DiscoverInstalledBrowsers()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in GetBrowserPathCandidates())
            {
                var normalized = candidate.Path.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                yield return (candidate.Name, normalized);
            }
        }

        private static IEnumerable<(string Name, string Path)> GetBrowserPathCandidates()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (var baseDir in new[] { programFiles, programFilesX86, localAppData }.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                yield return ("chrome", Path.Combine(baseDir, "Google", "Chrome", "Application", "chrome.exe"));
                yield return ("edge", Path.Combine(baseDir, "Microsoft", "Edge", "Application", "msedge.exe"));
            }

            foreach (var pathDir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return ("chrome", Path.Combine(pathDir, "chrome.exe"));
                yield return ("edge", Path.Combine(pathDir, "msedge.exe"));
            }
        }

        private static string InferBrowserName(string? browserVersion)
        {
            if (string.IsNullOrWhiteSpace(browserVersion))
            {
                return _lastState?.BrowserName ?? string.Empty;
            }

            if (browserVersion.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            {
                return "edge";
            }

            if (browserVersion.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                return "chrome";
            }

            return browserVersion;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        internal sealed class DevToolsVersionInfo
        {
            public string? Browser { get; set; }
            public string? ProtocolVersion { get; set; }
            public string? UserAgent { get; set; }
            public string? WebSocketDebuggerUrl { get; set; }
        }
    }
}
