using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Node.Protocol;

namespace OpenClaw.Node.Services
{
    internal static class BrowserLog
    {
        public static void Info(string profileName, string message)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [browser][{profileName}] {message}");
        }

        public static void Error(string profileName, string message)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [browser][{profileName}][ERROR] {message}");
        }
    }

    public interface IBrowserProxyService
    {
        Task<string> ProxyAsync(BrowserProxyRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class BrowserProxyRequest
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public Dictionary<string, string>? Query { get; set; }
        public JsonElement? Body { get; set; }
        public int TimeoutMs { get; set; } = 20000;
        public string? Profile { get; set; }
    }

    internal sealed class BrowserTab
    {
        public string TargetId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Type { get; set; } = "page";
        public object? Ownership { get; set; }
        public string? ResolvedProfile { get; set; }
    }

    internal sealed class BrowserProfileStatus
    {
        public string Name { get; set; } = "user";
        public string Transport { get; set; } = "chrome-mcp";
        public int? CdpPort { get; set; }
        public string? CdpUrl { get; set; }
        public string Color { get; set; } = "#00AA00";
        public string Driver { get; set; } = "existing-session";
        public bool Running { get; set; }
        public int TabCount { get; set; }
        public bool IsDefault { get; set; } = true;
        public bool IsRemote { get; set; }
    }

    internal sealed record BrowserScreenshot(string Url, string Base64, int? LabelsCount, int? LabelsSkipped, bool? Truncated);

    internal interface IBrowserBackend
    {
        Task EnsureAvailableAsync(string profileName, CancellationToken cancellationToken);
        Task<int?> GetPidAsync(string profileName, CancellationToken cancellationToken);
        Task<IReadOnlyList<BrowserTab>> ListTabsAsync(string profileName, CancellationToken cancellationToken);
        Task<BrowserTab> OpenTabAsync(string profileName, string url, CancellationToken cancellationToken);
        Task FocusTabAsync(string profileName, string targetId, CancellationToken cancellationToken);
        Task CloseTabAsync(string profileName, string targetId, CancellationToken cancellationToken);
        Task<object> CloseOwnedTabAsync(string profileName, JsonElement ownership, CancellationToken cancellationToken)
            => throw new BrowserRouteException(501, "Owned-tab cleanup is not supported by this backend");
        Task<string> NavigateAsync(string profileName, string targetId, string url, CancellationToken cancellationToken);
        Task<object?> ListConsoleMessagesAsync(string profileName, string? targetId, string? level, bool clear, int? limit, CancellationToken cancellationToken);
        Task<object?> ListNetworkRequestsAsync(string profileName, string? targetId, string? filter, bool clear, int? limit, CancellationToken cancellationToken);
        Task<List<Dictionary<string, object?>>> GetRequestMatchesAsync(string profileName, string targetId, string? filter, int limit, CancellationToken cancellationToken);
        Task<Dictionary<string, object?>?> GetLatestRequestAsync(string profileName, string targetId, string? filter, CancellationToken cancellationToken);
        string? GetLastError(string profileName);
        Task<string> SnapshotAsync(string profileName, string targetId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Browser snapshots are not available from this backend");
        Task<BrowserScreenshot> ScreenshotAsync(string profile, string target, string type, bool fullPage, string? reference, bool labels, CancellationToken ct)
            => throw new BrowserRouteException(501, "Screenshots are not available from this backend");
        Task ActAsync(string profileName, string targetId, string kind, JsonElement body, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Browser input is not available from this backend");
        Task UploadAsync(string profileName, string targetId, string uid, string[] paths, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Browser uploads are not available from this backend");
    }

    public sealed class BrowserProxyService : IBrowserProxyService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IBrowserBackend _backend;
        private readonly string _defaultProfile;

        public BrowserProxyService()
            : this(new ChromeMcpBrowserBackend(), "user")
        {
        }

        internal BrowserProxyService(IBrowserBackend backend, string defaultProfile = "user")
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _defaultProfile = string.IsNullOrWhiteSpace(defaultProfile) ? "user" : defaultProfile.Trim();
        }

        public async Task<string> ProxyAsync(BrowserProxyRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var method = NormalizeMethod(request.Method);
            var path = NormalizePath(request.Path);
            var profile = NormalizeProfile(request.Profile);

            BrowserLog.Info(profile, $"proxy start {method} {path} timeoutMs={request.TimeoutMs}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.TimeoutMs > 0 ? request.TimeoutMs : 20000);
            var ct = timeoutCts.Token;

            try
            {
                var proxyResponse = path switch
                {
                    "/profiles" when method == "GET" => await WrapResultAsync(HandleProfilesAsync(profile, ct)),
                    "/" when method == "GET" => await WrapResultAsync(HandleStatusAsync(profile, ct)),
                    "/tabs" when method == "GET" => await WrapResultAsync(HandleTabsAsync(profile, ct)),
                    "/tabs/open" when method == "POST" => await WrapResultAsync(HandleOpenTabAsync(profile, request.Body, ct)),
                    "/tabs/focus" when method == "POST" => await WrapResultAsync(HandleFocusTabAsync(profile, request.Body, ct)),
                    _ when method == "DELETE" && path.StartsWith("/tabs/", StringComparison.OrdinalIgnoreCase) => await WrapResultAsync(HandleCloseTabAsync(profile, path, ct)),
                    "/__openclaw/session-tab/close-owned" when method == "POST" => await WrapResultAsync(HandleCloseOwnedTabAsync(profile, request.Body, ct)),
                    "/navigate" when method == "POST" => await WrapResultAsync(HandleNavigateAsync(profile, request.Body, ct)),
                    "/console" when method == "GET" => await WrapResultAsync(HandleConsoleAsync(profile, request.Query, ct)),
                    "/requests" when method == "GET" => await WrapResultAsync(HandleRequestsAsync(profile, request.Query, ct)),
                    "/requests/matches" when method == "GET" => await WrapResultAsync(HandleRequestMatchesAsync(profile, request.Query, ct)),
                    "/request-body" when method == "GET" => await WrapResultAsync(HandleRequestBodyAsync(profile, request.Query, ct)),
                    "/requests/latest-body" when method == "GET" => await WrapResultAsync(HandleLatestRequestBodyAsync(profile, request.Query, ct)),
                    "/act" when method == "POST" => await WrapResultAsync(HandleActAsync(profile, request.Body, ct)),
                    "/snapshot" when method == "GET" => await WrapResultAsync(HandleSnapshotAsync(profile, request.Query, ct)),
                    "/hooks/file-chooser" when method == "POST" => await WrapResultAsync(HandleFileChooserAsync(profile, request.Body, ct)),
                    "/screenshot" when method == "POST" => await HandleScreenshotAsync(profile, request.Body, ct),
                    _ => throw new BrowserRouteException(501, $"browser.proxy route not supported yet: {method} {path}")
                };

                BrowserLog.Info(profile, $"proxy ok {method} {path}");
                return JsonSerializer.Serialize(proxyResponse, JsonOptions);
            }
            catch (Exception ex)
            {
                BrowserLog.Error(profile, $"proxy failed {method} {path}: {ex.Message}");
                throw;
            }
        }

        private string NormalizeProfile(string? profile)
        {
            var value = string.IsNullOrWhiteSpace(profile) ? _defaultProfile : profile.Trim();
            if (!string.Equals(value, _defaultProfile, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"browser profile not supported on this node: {value}");
            }
            return _defaultProfile;
        }

        private static string NormalizeMethod(string? method)
        {
            var normalized = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
            return normalized switch
            {
                "GET" => "GET",
                "POST" => "POST",
                "DELETE" => "DELETE",
                _ => throw new InvalidOperationException($"browser.proxy method not supported: {method}")
            };
        }

        private static string NormalizePath(string? path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            return normalized.StartsWith("/") ? normalized : "/" + normalized;
        }

        private async Task<object> HandleProfilesAsync(string profile, CancellationToken cancellationToken)
        {
            var status = await BuildProfileStatusAsync(profile, cancellationToken);
            return new { profiles = new[] { status } };
        }

        private async Task<object> HandleStatusAsync(string profile, CancellationToken cancellationToken)
        {
            return await BuildStatusAsync(profile, cancellationToken);
        }

        private async Task<object> HandleTabsAsync(string profile, CancellationToken cancellationToken)
        {
            var tabs = await SafeListTabsAsync(profile, cancellationToken);
            return new
            {
                running = tabs != null,
                tabs = tabs ?? Array.Empty<BrowserTab>(),
                error = tabs == null ? _backend.GetLastError(profile) : null,
            };
        }

        private static async Task<object> WrapResultAsync(Task<object> task)
        {
            var result = await task;
            return new { result };
        }

        private async Task<object> HandleOpenTabAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var url = RequireString(body, "url");
            await _backend.EnsureAvailableAsync(profile, cancellationToken);
            return await _backend.OpenTabAsync(profile, url, cancellationToken);
        }

        private async Task<object> HandleFocusTabAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var targetId = RequireString(body, "targetId");
            await _backend.FocusTabAsync(profile, targetId, cancellationToken);
            return new { ok = true };
        }

        private async Task<object> HandleCloseTabAsync(string profile, string path, CancellationToken cancellationToken)
        {
            var targetId = path.Substring("/tabs/".Length).Trim();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new InvalidOperationException("targetId is required");
            }
            await _backend.CloseTabAsync(profile, targetId, cancellationToken);
            return new { ok = true };
        }

        private async Task<object> HandleCloseOwnedTabAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            if (body is not JsonElement value || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("ownership", out var ownership))
                throw new BrowserRouteException(400, "Valid durable tab ownership is required");
            return await _backend.CloseOwnedTabAsync(profile, ownership, cancellationToken);
        }

        private async Task<object> HandleNavigateAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var url = RequireString(body, "url");
            var targetId = TryGetString(body, "targetId");
            if (string.IsNullOrWhiteSpace(targetId))
            {
                // Only an explicit navigation without a target may create a browser.
                await _backend.EnsureAvailableAsync(profile, cancellationToken);
                var tabs = await _backend.ListTabsAsync(profile, cancellationToken);
                var preferred = tabs.FirstOrDefault(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase))
                                ?? tabs.FirstOrDefault();
                if (preferred == null)
                {
                    preferred = await _backend.OpenTabAsync(profile, "about:blank", cancellationToken);
                }
                targetId = preferred.TargetId;
            }

            var resolvedUrl = await _backend.NavigateAsync(profile, targetId!, url, cancellationToken);
            return new { ok = true, targetId, url = resolvedUrl };
        }

        private async Task<object> HandleConsoleAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var targetId = query != null && query.TryGetValue("targetId", out var qTarget) ? qTarget : null;
            var level = query != null && query.TryGetValue("level", out var qLevel) ? qLevel : null;
            var clear = query != null && query.TryGetValue("clear", out var qClear) && bool.TryParse(qClear, out var parsedClear) ? parsedClear : false;
            var limit = query != null && query.TryGetValue("limit", out var qLimit) && int.TryParse(qLimit, out var parsedLimit) ? parsedLimit : (int?)null;
            var messages = await _backend.ListConsoleMessagesAsync(profile, targetId, level, clear, limit, cancellationToken);
            return new { ok = true, targetId = targetId ?? string.Empty, messages };
        }

        private async Task<object> HandleRequestsAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var targetId = query != null && query.TryGetValue("targetId", out var qTarget) ? qTarget : null;
            var filter = query != null && query.TryGetValue("filter", out var qFilter) ? qFilter : null;
            var clear = query != null && query.TryGetValue("clear", out var qClear) && bool.TryParse(qClear, out var parsedClear) ? parsedClear : false;
            var limit = query != null && query.TryGetValue("limit", out var qLimit) && int.TryParse(qLimit, out var parsedLimit) ? parsedLimit : (int?)null;
            var requests = await _backend.ListNetworkRequestsAsync(profile, targetId, filter, clear, limit, cancellationToken);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    var json = JsonSerializer.Serialize(requests);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("requests", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var rid = item.TryGetProperty("requestId", out var ridEl) ? ridEl.ToString() : string.Empty;
                            var url = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
                            BrowserLog.Info(profile, $"request match filter={filter} requestId={rid} url={url}");
                        }
                    }
                }
                catch { }
            }
            return new { ok = true, targetId = targetId ?? string.Empty, requests };
        }

        private async Task<object> HandleRequestMatchesAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var targetId = query != null && query.TryGetValue("targetId", out var qTarget) ? qTarget : null;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new InvalidOperationException("targetId is required");
            }
            var filter = query != null && query.TryGetValue("filter", out var qFilter) ? qFilter : null;
            var limit = query != null && query.TryGetValue("limit", out var qLimit) && int.TryParse(qLimit, out var parsedLimit) ? parsedLimit : 10;
            var matches = await _backend.GetRequestMatchesAsync(profile, targetId, filter, limit, cancellationToken);
            foreach (var match in matches)
            {
                var rid = match.TryGetValue("requestId", out var ridObj) ? ridObj?.ToString() ?? string.Empty : string.Empty;
                var url = match.TryGetValue("url", out var urlObj) ? urlObj?.ToString() ?? string.Empty : string.Empty;
                BrowserLog.Info(profile, $"request exact-match filter={filter} requestId={rid} url={url}");
            }
            return new { ok = true, targetId, matches };
        }

        private async Task<object> HandleRequestBodyAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var targetId = query != null && query.TryGetValue("targetId", out var qTarget) ? qTarget : null;
            var requestId = query != null && query.TryGetValue("requestId", out var qRequest) ? qRequest : null;
            if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("targetId and requestId are required");
            }
            var target = await ChromeMcpBrowserBackend.GetDevToolsTargetAsync(targetId, cancellationToken);
            var monitor = await ChromeMcpBrowserBackend.DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            var body = await monitor.GetResponseBodyAsync(requestId, cancellationToken);
            return new { ok = true, targetId, requestId, body.base64Encoded, body.body };
        }

        private async Task<object> HandleLatestRequestBodyAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var targetId = query != null && query.TryGetValue("targetId", out var qTarget) ? qTarget : null;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new InvalidOperationException("targetId is required");
            }
            var filter = query != null && query.TryGetValue("filter", out var qFilter) ? qFilter : null;
            var latest = await _backend.GetLatestRequestAsync(profile, targetId, filter, cancellationToken);
            if (latest == null)
            {
                throw new InvalidOperationException("no matching request found");
            }
            var requestId = latest.TryGetValue("requestId", out var ridObj) ? ridObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("matching request has no requestId");
            }
            var target = await ChromeMcpBrowserBackend.GetDevToolsTargetAsync(targetId, cancellationToken);
            var monitor = await ChromeMcpBrowserBackend.DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            var body = await monitor.GetResponseBodyAsync(requestId, cancellationToken);
            BrowserLog.Info(profile, $"latest-body requestId={requestId} base64={body.base64Encoded} len={body.body.Length}");
            var matchedUrl = latest.TryGetValue("url", out var urlObj) ? urlObj?.ToString() ?? string.Empty : string.Empty;
            BrowserLog.Info(profile, $"latest-body matched requestId={requestId} url={matchedUrl}");
            return new { ok = true, targetId, requestId, matchedUrl, request = latest, body.base64Encoded, body.body };
        }

        private async Task<object> HandleActAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var kind = RequireString(body, "kind");
            var targetId = RequireString(body, "targetId");
            if (!string.Equals(kind, "evaluate", StringComparison.OrdinalIgnoreCase))
            {
                await _backend.ActAsync(profile, targetId, kind, body!.Value, cancellationToken);
                return new { ok = true, targetId };
            }
            var fn = RequireString(body, "fn");
            var result = await ChromeMcpBrowserBackend.EvaluateViaDevToolsWebSocketAsync(targetId, fn, cancellationToken);
            var tabs = await _backend.ListTabsAsync(profile, cancellationToken);
            var found = tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.OrdinalIgnoreCase));
            return new
            {
                ok = true,
                targetId,
                url = found?.Url ?? string.Empty,
                result,
            };
        }

        private async Task<object> HandleSnapshotAsync(string profile, Dictionary<string, string>? query, CancellationToken cancellationToken)
        {
            if (query == null || !query.TryGetValue("targetId", out var targetId) || string.IsNullOrWhiteSpace(targetId))
                throw new InvalidOperationException("INVALID_REQUEST: snapshot targetId is required");
            var snapshot = await _backend.SnapshotAsync(profile, targetId, cancellationToken);
            var tabs = await _backend.ListTabsAsync(profile, cancellationToken);
            return new { ok = true, targetId, url = tabs.FirstOrDefault(t => t.TargetId == targetId)?.Url ?? "", format = "ai", snapshot };
        }

        private async Task<object> HandleFileChooserAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var targetId = RequireString(body, "targetId");
            var uid = TryGetString(body, "inputRef") ?? RequireString(body, "ref");
            if (body?.TryGetProperty("paths", out var paths) != true || paths.ValueKind != JsonValueKind.Array ||
                paths.GetArrayLength() is < 1 or > 256 || paths.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || !Path.IsPathFullyQualified(p.GetString()!)))
                throw new InvalidOperationException("INVALID_REQUEST: file chooser paths must be absolute paths");
            await _backend.UploadAsync(profile, targetId, uid, paths.EnumerateArray().Select(p => p.GetString()!).ToArray(), cancellationToken);
            return new { ok = true };
        }

        private async Task<object> HandleScreenshotAsync(string profile, JsonElement? body, CancellationToken cancellationToken)
        {
            var targetId = RequireString(body, "targetId");
            var type = string.Equals(TryGetString(body, "type"), "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
            var fullPage = TryGetBool(body, "fullPage") ?? false;
            var reference = TryGetString(body, "ref");
            if (string.IsNullOrWhiteSpace(reference)) reference = null;
            if (!string.IsNullOrWhiteSpace(TryGetString(body, "element")))
                throw new BrowserRouteException(400, "CSS element selectors are not supported in existing sessions; use a snapshot ref.");
            if (fullPage && reference != null)
                throw new BrowserRouteException(400, "fullPage is not supported for element screenshots.");
            var labels = TryGetBool(body, "labels") ?? false;
            var screenshot = await _backend.ScreenshotAsync(profile, targetId, type, fullPage, reference, labels, cancellationToken);
            var fileName = $"browser-screenshot-{targetId}.{(type == "jpeg" ? "jpg" : "png")}";
            return new
            {
                result = new
                {
                    ok = true,
                    path = fileName,
                    targetId,
                    url = screenshot.Url,
                    labels = labels ? true : (bool?)null,
                    labelsCount = screenshot.LabelsCount,
                    labelsSkipped = screenshot.LabelsSkipped,
                    truncated = screenshot.Truncated,
                },
                files = new[]
                {
                    new
                    {
                        path = fileName,
                        base64 = screenshot.Base64,
                        mimeType = type == "jpeg" ? "image/jpeg" : "image/png",
                    }
                }
            };
        }

        private async Task<BrowserProfileStatus> BuildProfileStatusAsync(string profile, CancellationToken cancellationToken)
        {
            var tabs = await SafeListTabsAsync(profile, cancellationToken);
            return new BrowserProfileStatus
            {
                Name = profile,
                Running = tabs != null,
                TabCount = tabs?.Count(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase)) ?? 0,
                IsDefault = string.Equals(profile, _defaultProfile, StringComparison.OrdinalIgnoreCase),
                IsRemote = false,
            };
        }

        private async Task<object> BuildStatusAsync(string profile, CancellationToken cancellationToken)
        {
            var tabs = await SafeListTabsAsync(profile, cancellationToken);
            var state = ManagedBrowserHost.GetLastState();
            var pid = tabs != null ? await _backend.GetPidAsync(profile, cancellationToken) : state?.ProcessId;
            var detectError = tabs == null ? _backend.GetLastError(profile) : null;
            return new
            {
                enabled = true,
                profile,
                driver = state?.LaunchMode == "managed-launch" ? "managed-browser" : "existing-session",
                transport = "chrome-mcp",
                running = tabs != null,
                cdpReady = tabs != null,
                cdpHttp = tabs != null,
                pid,
                cdpPort = 9222,
                cdpUrl = BundledBrowserRuntimeLocator.DefaultBrowserUrl,
                chosenBrowser = state?.BrowserName,
                detectedBrowser = state?.BrowserVersion,
                detectedExecutablePath = state?.ExecutablePath,
                detectError,
                userDataDir = state?.UserDataDir,
                color = "#00AA00",
                headless = false,
                noSandbox = false,
                executablePath = state?.ExecutablePath,
                attachOnly = false,
            };
        }

        private async Task<IReadOnlyList<BrowserTab>?> SafeListTabsAsync(string profile, CancellationToken cancellationToken)
        {
            try
            {
                return await _backend.ListTabsAsync(profile, cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        private static string RequireString(JsonElement? body, string propertyName)
        {
            var value = TryGetString(body, propertyName);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{propertyName} is required");
            }
            return value!;
        }

        private static string? TryGetString(JsonElement? body, string propertyName)
        {
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return body.Value.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
        }

        private static bool? TryGetBool(JsonElement? body, string propertyName)
        {
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!body.Value.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
    }

    internal sealed class ChromeMcpBrowserBackend : IBrowserBackend, IDisposable
    {
        private readonly BrowserCdpClient _cdp;
        internal ChromeMcpBrowserBackend(BrowserCdpClient? cdp = null) => _cdp = cdp ?? new BrowserCdpClient();
        private static readonly HttpClient DevToolsHttp = new HttpClient();
        private readonly ConcurrentDictionary<string, ChromeMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _lastErrors = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _pageOperationGate = new(1, 1);

        private async Task<CallToolResult> PageToolAsync(string profileName, string targetId, string tool, Dictionary<string, object?> args, CancellationToken cancellationToken)
        {
            await _pageOperationGate.WaitAsync(cancellationToken);
            try
            {
                var native = await _cdp.GetTargetAsync(targetId, cancellationToken);
                var session = await GetSessionAsync(profileName, cancellationToken);
                var pages = ExtractPages(await session.CallToolAsync("list_pages", new(), cancellationToken));
                var page = pages.SingleOrDefault(page => page.TargetId == native.Id)
                    ?? throw new BrowserRouteException(404, "Browser target is no longer attached.");
                args["pageId"] = page.Id;
                return await session.CallToolAsync(tool, args, cancellationToken);
            }
            finally { _pageOperationGate.Release(); }
        }

        public async Task<string> SnapshotAsync(string profileName, string targetId, CancellationToken cancellationToken)
        {
            var result = await PageToolAsync(profileName, targetId, "take_snapshot", new(), cancellationToken);
            return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        }

        public async Task ActAsync(string profileName, string targetId, string kind, JsonElement body, CancellationToken cancellationToken)
        {
            string Required(string key) => body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
                ? value.GetString()! : throw new InvalidOperationException("INVALID_REQUEST: browser action requires " + key);
            var args = new Dictionary<string, object?>();
            var tool = kind switch
            {
                "click" => "click", "hover" => "hover", "type" => "fill", "press" => "press_key", "drag" => "drag",
                _ => throw new InvalidOperationException("INVALID_REQUEST: unsupported browser action kind: " + kind),
            };
            if (kind is "click" or "hover" or "type") args["uid"] = Required("ref");
            if (kind == "click" && body.TryGetProperty("doubleClick", out var twice)) args["dblClick"] = twice.GetBoolean();
            if (kind == "type") args["value"] = body.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() : throw new InvalidOperationException("INVALID_REQUEST: browser action requires text");
            if (kind == "press") args["key"] = Required("key");
            if (kind == "drag") { args["from_uid"] = Required("startRef"); args["to_uid"] = Required("endRef"); }
            await PageToolAsync(profileName, targetId, tool, args, cancellationToken);
            if (kind == "type" && body.TryGetProperty("submit", out var submit) && submit.ValueKind == JsonValueKind.True)
                await PageToolAsync(profileName, targetId, "press_key", new() { ["key"] = "Enter" }, cancellationToken);
        }

        public async Task UploadAsync(string profileName, string targetId, string uid, string[] paths, CancellationToken cancellationToken)
        {
            if (paths.Length is < 1 or > 256) throw new InvalidOperationException("INVALID_REQUEST: upload requires between 1 and 256 staged files");
            await PageToolAsync(profileName, targetId, "upload_file", new() { ["uid"] = uid, ["filePaths"] = paths }, cancellationToken);
        }

        public async Task EnsureAvailableAsync(string profileName, CancellationToken cancellationToken)
        {
            BrowserLog.Info(profileName, "EnsureAvailable start");
            try
            {
                if (_cdp.Endpoint.ToString().TrimEnd('/') == BundledBrowserRuntimeLocator.DefaultBrowserUrl)
                {
                    await ManagedBrowserHost.EnsureReadyAsync(profileName, cancellationToken);
                    var startup = ManagedBrowserHost.TakeStartupTab();
                    if (startup != null) _cdp.RegisterStartupTab(startup);
                }
                else
                    _ = await _cdp.ListTabsAsync(cancellationToken);
                _ = await GetSessionAsync(profileName, cancellationToken);
                ClearLastError(profileName);
                BrowserLog.Info(profileName, "EnsureAvailable ok");
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                BrowserLog.Error(profileName, $"EnsureAvailable failed: {ex.Message}");
                throw;
            }
        }

        public Task<int?> GetPidAsync(string profileName, CancellationToken cancellationToken)
            => Task.FromResult(ManagedBrowserHost.GetRunningProcessId());

        public async Task<IReadOnlyList<BrowserTab>> ListTabsAsync(string profileName, CancellationToken cancellationToken)
        {
            try
            {
                var tabs = await _cdp.ListTabsAsync(cancellationToken);
                ClearLastError(profileName);
                return tabs;
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                throw;
            }
        }

        public async Task<BrowserTab> OpenTabAsync(string profileName, string url, CancellationToken cancellationToken)
        {
            BrowserLog.Info(profileName, $"DevTools HTTP /json/new start url={url}");
            try
            {
                var created = await _cdp.OpenTabAsync(profileName, url, cancellationToken);
                ClearLastError(profileName);
                BrowserLog.Info(profileName, $"DevTools HTTP /json/new ok targetId={created.TargetId}");
                return created;
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                BrowserLog.Error(profileName, $"DevTools HTTP /json/new failed: {ex.Message}");
                throw;
            }
        }

        public async Task FocusTabAsync(string profileName, string targetId, CancellationToken cancellationToken)
        {
            BrowserLog.Info(profileName, $"DevTools HTTP /json/activate start targetId={targetId}");
            try
            {
                await ActivateTabViaDevToolsHttpAsync(targetId);
                ClearLastError(profileName);
                BrowserLog.Info(profileName, $"DevTools HTTP /json/activate ok targetId={targetId}");
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                BrowserLog.Error(profileName, $"DevTools HTTP /json/activate failed: {ex.Message}");
                throw;
            }
        }

        public async Task CloseTabAsync(string profileName, string targetId, CancellationToken cancellationToken)
        {
            BrowserLog.Info(profileName, $"DevTools HTTP /json/close start targetId={targetId}");
            try
            {
                await _cdp.CloseTabAsync(targetId, cancellationToken);
                ClearLastError(profileName);
                BrowserLog.Info(profileName, $"DevTools HTTP /json/close ok targetId={targetId}");
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                BrowserLog.Error(profileName, $"DevTools HTTP /json/close failed: {ex.Message}");
                throw;
            }
        }

        public Task<object> CloseOwnedTabAsync(string profileName, JsonElement ownership, CancellationToken cancellationToken)
            => _cdp.CloseOwnedTabAsync(profileName, ownership, cancellationToken);

        public async Task<string> NavigateAsync(string profileName, string targetId, string url, CancellationToken cancellationToken)
        {
            BrowserLog.Info(profileName, $"DevTools WS navigate start targetId={targetId} url={url}");
            try
            {
                await NavigateViaDevToolsWebSocketAsync(targetId, url, cancellationToken);
                var tabs = await ListTabsViaDevToolsHttpAsync();
                var found = tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.OrdinalIgnoreCase));
                ClearLastError(profileName);
                BrowserLog.Info(profileName, $"DevTools WS navigate ok targetId={targetId}");
                return found?.Url ?? url;
            }
            catch (Exception ex)
            {
                SetLastError(profileName, ex);
                BrowserLog.Error(profileName, $"DevTools WS navigate failed: {ex.Message}");
                throw;
            }
        }

        private async Task<ChromeMcpSession> GetSessionAsync(string profileName, CancellationToken cancellationToken)
        {
            var gate = _sessionGates.GetOrAdd(profileName, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var browserFingerprint = await _cdp.GetBrowserFingerprintAsync(profileName, cancellationToken);
                if (_sessions.TryGetValue(profileName, out var existing))
                {
                    if (!existing.IsFaulted && existing.BrowserFingerprint == browserFingerprint)
                    {
                        BrowserLog.Info(profileName, $"reusing session pid={existing.ProcessId}");
                        return existing;
                    }

                    BrowserLog.Info(profileName, "dropping retired browser session");
                    _sessions.TryRemove(profileName, out _);
                    await existing.DisposeAsync();
                }

                var created = new ChromeMcpSession(profileName, _cdp.Endpoint, browserFingerprint);
                _sessions[profileName] = created;
                try
                {
                    BrowserLog.Info(profileName, "initializing new SDK-backed session");
                    await created.InitializeAsync(cancellationToken);
                    BrowserLog.Info(profileName, "session initialize ok");
                    return created;
                }
                catch (Exception ex)
                {
                    _sessions.TryRemove(profileName, out _);
                    await created.DisposeAsync();
                    SetLastError(profileName, ex);
                    BrowserLog.Error(profileName, $"session initialize failed: {ex.Message}");
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<object?> ListConsoleMessagesAsync(string profileName, string? targetId, string? level, bool clear, int? limit, CancellationToken cancellationToken)
        {
            var target = await GetPreferredTargetAsync(targetId, cancellationToken);
            var monitor = await DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            return monitor.GetConsole(level, clear, limit);
        }

        public async Task<object?> ListNetworkRequestsAsync(string profileName, string? targetId, string? filter, bool clear, int? limit, CancellationToken cancellationToken)
        {
            var target = await GetPreferredTargetAsync(targetId, cancellationToken);
            var monitor = await DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            return monitor.GetRequests(filter, clear, limit);
        }

        public async Task<List<Dictionary<string, object?>>> GetRequestMatchesAsync(string profileName, string targetId, string? filter, int limit, CancellationToken cancellationToken)
        {
            var target = await GetPreferredTargetAsync(targetId, cancellationToken);
            var monitor = await DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            return monitor.GetRequestMatches(filter, limit);
        }

        public async Task<Dictionary<string, object?>?> GetLatestRequestAsync(string profileName, string targetId, string? filter, CancellationToken cancellationToken)
        {
            var target = await GetPreferredTargetAsync(targetId, cancellationToken);
            var monitor = await DevToolsActivityMonitor.EnsureAsync(target, cancellationToken);
            return monitor.GetLatestRequest(filter);
        }

        public string? GetLastError(string profileName)
        {
            return _lastErrors.TryGetValue(profileName, out var error) ? error : null;
        }

        private void ClearLastError(string profileName)
        {
            _lastErrors.TryRemove(profileName, out _);
        }

        private void SetLastError(string profileName, Exception ex)
        {
            var message = ex.Message;
            _lastErrors[profileName] = message;
            BrowserLog.Error(profileName, message);
            Debug.WriteLine($"[browser][{profileName}] {message}");
        }

        private static async Task<IReadOnlyList<BrowserTab>> ListTabsViaDevToolsHttpAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var tabs = await DevToolsHttp.GetFromJsonAsync<List<DevToolsListEntry>>("http://127.0.0.1:9222/json/list", cts.Token)
                       ?? new List<DevToolsListEntry>();
            return tabs
                .Where(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase))
                .Select((t, idx) => MapDevToolsEntryToTab(t, idx))
                .ToList();
        }

        private static async Task ActivateTabViaDevToolsHttpAsync(string targetId)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:9222/json/activate/{targetId}");
            using var response = await DevToolsHttp.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
        }

        private static async Task NavigateViaDevToolsWebSocketAsync(string targetId, string url, CancellationToken cancellationToken)
        {
            var target = await GetDevToolsTargetAsync(targetId, cancellationToken);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl!), cancellationToken);
            await SendCdpCommandAsync(ws, 1, "Page.navigate", new { url }, cancellationToken);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        }

        internal static async Task<object?> EvaluateViaDevToolsWebSocketAsync(string targetId, string fn, CancellationToken cancellationToken)
        {
            var target = await GetDevToolsTargetAsync(targetId, cancellationToken);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl!), cancellationToken);
            var evalResult = await SendCdpCommandAsync(ws, 1, "Runtime.evaluate", new
            {
                expression = $"({fn})()",
                awaitPromise = true,
                returnByValue = true,
            }, cancellationToken);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            if (evalResult.ValueKind == JsonValueKind.Object && evalResult.TryGetProperty("result", out var resultObj))
            {
                if (resultObj.TryGetProperty("value", out var value))
                {
                    return JsonSerializer.Deserialize<object>(value.GetRawText());
                }
                if (resultObj.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    return description.GetString();
                }
            }
            return null;
        }

        public async Task<BrowserScreenshot> ScreenshotAsync(string profile, string target, string type, bool fullPage, string? reference, bool labels, CancellationToken ct)
        {
            var args = new Dictionary<string, object?>
            {
                ["companionTarget"] = target, ["format"] = type, ["labels"] = labels, ["fullPage"] = fullPage,
            };
            if (reference != null) args["uid"] = reference;
            var result = await PageToolAsync(profile, target, "take_screenshot", args, ct);
            var image = result.Content.OfType<ImageContentBlock>().Single();
            if (image.MimeType != "image/" + type) throw new InvalidDataException("Unexpected screenshot format.");
            var message = ExtractStructuredContent(result).GetProperty("message").GetString();
            using var metadata = JsonDocument.Parse(message!);
            var info = metadata.RootElement;
            return new BrowserScreenshot(info.GetProperty("url").GetString()!, Encoding.UTF8.GetString(image.Data.Span),
                labels ? info.GetProperty("labelsCount").GetInt32() : null,
                labels ? info.GetProperty("labelsSkipped").GetInt32() : null,
                labels && info.GetProperty("truncated").GetBoolean() ? true : null);
        }

        internal static async Task<(bool base64Encoded, string body)> GetResponseBodyViaDevToolsWebSocketAsync(string targetId, string requestId, CancellationToken cancellationToken)
        {
            var target = await GetDevToolsTargetAsync(targetId, cancellationToken);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl!), cancellationToken);
            await SendCdpCommandAsync(ws, 1, "Network.enable", new { }, cancellationToken);
            var bodyResult = await SendCdpCommandAsync(ws, 2, "Network.getResponseBody", new { requestId }, cancellationToken);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            if (bodyResult.ValueKind == JsonValueKind.Object)
            {
                var body = bodyResult.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() ?? string.Empty : string.Empty;
                var base64Encoded = bodyResult.TryGetProperty("base64Encoded", out var b64El) && b64El.ValueKind == JsonValueKind.True;
                return (base64Encoded, body);
            }
            throw new InvalidOperationException("Network.getResponseBody returned no body.");
        }


        internal static async Task<DevToolsListEntry> GetDevToolsTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            var targets = await DevToolsHttp.GetFromJsonAsync<List<DevToolsListEntry>>("http://127.0.0.1:9222/json/list", cancellationToken)
                          ?? new List<DevToolsListEntry>();
            var target = targets.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase));
            if (target == null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            {
                throw new InvalidOperationException("tab not found");
            }
            return target;
        }

        private static async Task<int?> ResolvePageIdxAsync(string? targetId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return 0;
            }
            var targets = await DevToolsHttp.GetFromJsonAsync<List<DevToolsListEntry>>("http://127.0.0.1:9222/json/list", cancellationToken)
                          ?? new List<DevToolsListEntry>();
            for (var i = 0; i < targets.Count; i++)
            {
                if (string.Equals(targets[i].Id, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return null;
        }

        private static async Task<DevToolsListEntry> GetPreferredTargetAsync(string? targetId, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                return await GetDevToolsTargetAsync(targetId, cancellationToken);
            }
            var targets = await DevToolsHttp.GetFromJsonAsync<List<DevToolsListEntry>>("http://127.0.0.1:9222/json/list", cancellationToken)
                          ?? new List<DevToolsListEntry>();
            var page = targets.FirstOrDefault(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase));
            if (page == null || string.IsNullOrWhiteSpace(page.WebSocketDebuggerUrl))
            {
                throw new InvalidOperationException("no browser page available");
            }
            return page;
        }

        internal static async Task<JsonElement> SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object @params, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new { id, method, @params });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);

            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("DevTools websocket closed unexpectedly.");
                    }
                    if (ms.Length + result.Count > Constants.DefaultMaximumFrameBytes)
                        throw new InvalidOperationException("DevTools response exceeded the maximum frame size");
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var responseId) && responseId == id)
                {
                    if (root.TryGetProperty("error", out var err))
                    {
                        throw new InvalidOperationException(err.ToString());
                    }
                    return root.TryGetProperty("result", out var resultEl) ? resultEl.Clone() : default;
                }
            }
        }

        private static BrowserTab MapDevToolsEntryToTab(DevToolsListEntry t, int idx)
        {
            return new BrowserTab
            {
                TargetId = !string.IsNullOrWhiteSpace(t.Id) ? t.Id! : (idx + 1).ToString(),
                Title = t.Title ?? string.Empty,
                Url = t.Url ?? string.Empty,
                Type = t.Type ?? "page",
            };
        }

        private static List<ChromeMcpPage> ExtractPages(CallToolResult result)
        {
            var structured = ExtractStructuredContent(result);
            if (structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
            {
                var pages = new List<ChromeMcpPage>();
                foreach (var item in pagesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!TryReadInt(item, out var id))
                    {
                        continue;
                    }

                    pages.Add(new ChromeMcpPage
                    {
                        Id = id,
                        TargetId = TryReadString(item, "targetId"),
                        Title = TryReadString(item, "title"),
                        Url = TryReadString(item, "url"),
                        Selected = TryReadBool(item, "selected"),
                    });
                }
                if (pages.Count > 0)
                {
                    return pages;
                }
            }

            var textBlocks = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                            ?? new List<string>();

            var fallback = new List<ChromeMcpPage>();
            foreach (var block in textBlocks)
            {
                foreach (var line in block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+):\s+(.+?)(?:\s+\[(selected)\])?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id))
                    {
                        continue;
                    }
                    fallback.Add(new ChromeMcpPage
                    {
                        Id = id,
                        Url = match.Groups[2].Value.Trim(),
                        Selected = match.Groups[3].Success,
                    });
                }
            }
            return fallback;
        }

        private static JsonElement ExtractStructuredContent(CallToolResult result)
        {
            if (result.StructuredContent is JsonElement el)
            {
                return el;
            }

            if (result.StructuredContent != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.StructuredContent));
                    return doc.RootElement.Clone();
                }
                catch
                {
                }
            }

            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        private static string? ExtractStringResult(CallToolResult result)
        {
            var structured = ExtractStructuredContent(result);
            if (structured.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "result", "value", "text" })
                {
                    if (structured.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        return el.GetString();
                    }
                }
            }

            var text = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(trimmed);
                }
                catch
                {
                }
            }
            return trimmed;
        }

        private static bool TryReadInt(JsonElement item, out int value)
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out value)) return true;
            if (item.TryGetProperty("pageIdx", out var idxEl) && idxEl.TryGetInt32(out value)) return true;
            value = default;
            return false;
        }

        private static string? TryReadString(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }

        private static bool TryReadBool(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            _sessions.Clear();
            foreach (var gate in _sessionGates.Values)
            {
                gate.Dispose();
            }
            _sessionGates.Clear();
        }

        private sealed class ChromeMcpPage
        {
            public int Id { get; set; }
            public string? TargetId { get; set; }
            public string? Title { get; set; }
            public string? Url { get; set; }
            public bool Selected { get; set; }
        }

        internal sealed class DevToolsListEntry
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("webSocketDebuggerUrl")]
            public string? WebSocketDebuggerUrl { get; set; }
        }

        internal sealed class DevToolsActivityMonitor
        {
            private static readonly ConcurrentDictionary<string, DevToolsActivityMonitor> Monitors = new(StringComparer.OrdinalIgnoreCase);
            private readonly DevToolsListEntry _target;
            private readonly ClientWebSocket _ws = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly object _gate = new();
            private readonly List<object> _console = new();
            private readonly List<object> _requests = new();
            private readonly Dictionary<string, Dictionary<string, object?>> _requestsById = new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
            private int _nextCommandId = 10;
            private bool _started;

            private DevToolsActivityMonitor(DevToolsListEntry target)
            {
                _target = target;
            }

            public static async Task<DevToolsActivityMonitor> EnsureAsync(DevToolsListEntry target, CancellationToken cancellationToken)
            {
                var monitor = Monitors.GetOrAdd(target.Id ?? string.Empty, _ => new DevToolsActivityMonitor(target));
                await monitor.StartAsync(cancellationToken);
                return monitor;
            }

            public async Task<(bool base64Encoded, string body)> GetResponseBodyAsync(string requestId, CancellationToken cancellationToken)
            {
                var result = await SendCommandAsync("Network.getResponseBody", new { requestId }, cancellationToken);
                if (result.ValueKind == JsonValueKind.Object)
                {
                    var body = result.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() ?? string.Empty : string.Empty;
                    var base64Encoded = result.TryGetProperty("base64Encoded", out var b64El) && b64El.ValueKind == JsonValueKind.True;
                    return (base64Encoded, body);
                }
                throw new InvalidOperationException("Network.getResponseBody returned no body.");
            }

            public object GetConsole(string? level, bool clear, int? limit)
            {
                lock (_gate)
                {
                    var items = _console.ToList();
                    if (!string.IsNullOrWhiteSpace(level))
                    {
                        var threshold = ConsolePriority(level);
                        items = items.Where(item =>
                        {
                            try
                            {
                                var json = JsonSerializer.Serialize(item);
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                                {
                                    return ConsolePriority(typeEl.GetString() ?? string.Empty) >= threshold;
                                }
                            }
                            catch { }
                            return true;
                        }).ToList();
                    }
                    if (limit.HasValue && limit.Value > 0 && items.Count > limit.Value)
                    {
                        items = items.Skip(Math.Max(0, items.Count - limit.Value)).ToList();
                    }
                    if (clear)
                    {
                        _console.Clear();
                    }
                    return new { messages = items };
                }
            }

            public object GetRequests(string? filter, bool clear, int? limit)
            {
                lock (_gate)
                {
                    var items = _requests.ToList();
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        items = items.Where(item => JsonSerializer.Serialize(item).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                    if (limit.HasValue && limit.Value > 0 && items.Count > limit.Value)
                    {
                        items = items.Skip(Math.Max(0, items.Count - limit.Value)).ToList();
                    }
                    if (clear)
                    {
                        _requests.Clear();
                        _requestsById.Clear();
                    }
                    return new { requests = items };
                }
            }

            public List<Dictionary<string, object?>> GetRequestMatches(string? filter, int limit)
            {
                lock (_gate)
                {
                    IEnumerable<object> items = _requests;
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        items = items.Where(item => JsonSerializer.Serialize(item).Contains(filter, StringComparison.OrdinalIgnoreCase));
                    }
                    var list = items.TakeLast(Math.Max(1, limit)).Select(item =>
                    {
                        if (item is Dictionary<string, object?> dict)
                        {
                            var slim = new Dictionary<string, object?>();
                            if (dict.TryGetValue("requestId", out var rid)) slim["requestId"] = rid;
                            if (dict.TryGetValue("url", out var url)) slim["url"] = url;
                            if (dict.TryGetValue("status", out var status)) slim["status"] = status;
                            if (dict.TryGetValue("method", out var method)) slim["method"] = method;
                            if (dict.TryGetValue("type", out var type)) slim["type"] = type;
                            return slim;
                        }
                        return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(item)) ?? new Dictionary<string, object?>();
                    }).ToList();
                    return list;
                }
            }

            public Dictionary<string, object?>? GetLatestRequest(string? filter)
            {
                lock (_gate)
                {
                    IEnumerable<object> items = _requests;
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        items = items.Where(item => JsonSerializer.Serialize(item).Contains(filter, StringComparison.OrdinalIgnoreCase));
                    }
                    var latest = items.LastOrDefault();
                    if (latest is Dictionary<string, object?> dict)
                    {
                        return new Dictionary<string, object?>(dict);
                    }
                    if (latest != null)
                    {
                        try
                        {
                            return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(latest));
                        }
                        catch
                        {
                        }
                    }
                    return null;
                }
            }

            private async Task StartAsync(CancellationToken cancellationToken)
            {
                if (_started) return;
                await _ws.ConnectAsync(new Uri(_target.WebSocketDebuggerUrl!), cancellationToken);
                _started = true;
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                await SendCommandAsync("Runtime.enable", new { }, cancellationToken);
                await SendCommandAsync("Log.enable", new { }, cancellationToken);
                await SendCommandAsync("Network.enable", new { }, cancellationToken);
            }

            private async Task ReadLoopAsync(CancellationToken cancellationToken)
            {
                var buffer = new byte[64 * 1024];
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var responseId))
                    {
                        if (_pending.TryRemove(responseId, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var errorEl))
                            {
                                tcs.TrySetException(new InvalidOperationException(errorEl.ToString()));
                            }
                            else if (root.TryGetProperty("result", out var resultEl))
                            {
                                tcs.TrySetResult(resultEl.Clone());
                            }
                            else
                            {
                                tcs.TrySetResult(default);
                            }
                        }
                        continue;
                    }

                    if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String) continue;
                    var method = methodEl.GetString() ?? string.Empty;
                    if (!root.TryGetProperty("params", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object) continue;

                    if (string.Equals(method, "Runtime.consoleAPICalled", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate)
                        {
                            _console.Add(new
                            {
                                type = paramsEl.TryGetProperty("type", out var t) ? t.GetString() ?? "log" : "log",
                                text = paramsEl.TryGetProperty("args", out var args) ? args.ToString() : string.Empty,
                                time = paramsEl.TryGetProperty("timestamp", out var ts) ? ts.ToString() : string.Empty,
                            });
                            Trim(_console, 200);
                        }
                    }
                    else if (string.Equals(method, "Log.entryAdded", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate)
                        {
                            _console.Add(new
                            {
                                type = paramsEl.TryGetProperty("entry", out var e) && e.TryGetProperty("level", out var l) ? l.GetString() ?? "info" : "info",
                                text = paramsEl.TryGetProperty("entry", out var entry) ? entry.ToString() : string.Empty,
                                time = paramsEl.TryGetProperty("entry", out var ee) && ee.TryGetProperty("timestamp", out var ets) ? ets.ToString() : string.Empty,
                            });
                            Trim(_console, 200);
                        }
                    }
                    else if (string.Equals(method, "Network.requestWillBeSent", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate)
                        {
                            var requestId = paramsEl.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(requestId))
                            {
                                continue;
                            }

                            var request = paramsEl.TryGetProperty("request", out var req) ? req : default;
                            var item = new Dictionary<string, object?>
                            {
                                ["requestId"] = requestId,
                                ["url"] = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                                ["method"] = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("method", out var meth) ? meth.GetString() ?? string.Empty : string.Empty,
                                ["type"] = paramsEl.TryGetProperty("type", out var ty) ? ty.GetString() ?? string.Empty : string.Empty,
                                ["headers"] = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("headers", out var hdrs) ? JsonSerializer.Deserialize<object>(hdrs.GetRawText()) : null,
                                ["postData"] = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("postData", out var pd) ? pd.ToString() : null,
                                ["initiator"] = paramsEl.TryGetProperty("initiator", out var initiator) ? JsonSerializer.Deserialize<object>(initiator.GetRawText()) : null,
                                ["documentURL"] = paramsEl.TryGetProperty("documentURL", out var docUrl) ? docUrl.GetString() ?? string.Empty : string.Empty,
                            };
                            _requestsById[requestId] = item;
                            RebuildRequestsFromMap();
                        }
                    }
                    else if (string.Equals(method, "Network.responseReceived", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate)
                        {
                            var requestId = paramsEl.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(requestId) || !_requestsById.TryGetValue(requestId, out var item))
                            {
                                continue;
                            }

                            if (paramsEl.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                            {
                                item["status"] = response.TryGetProperty("status", out var status) ? JsonSerializer.Deserialize<object>(status.GetRawText()) : null;
                                item["statusText"] = response.TryGetProperty("statusText", out var statusText) ? statusText.GetString() ?? string.Empty : string.Empty;
                                item["mimeType"] = response.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? string.Empty : string.Empty;
                                item["remoteIPAddress"] = response.TryGetProperty("remoteIPAddress", out var ip) ? ip.GetString() ?? string.Empty : string.Empty;
                                item["responseHeaders"] = response.TryGetProperty("headers", out var rh) ? JsonSerializer.Deserialize<object>(rh.GetRawText()) : null;
                                item["fromDiskCache"] = response.TryGetProperty("fromDiskCache", out var fdc) && fdc.ValueKind == JsonValueKind.True;
                                item["fromServiceWorker"] = response.TryGetProperty("fromServiceWorker", out var fsw) && fsw.ValueKind == JsonValueKind.True;
                            }
                            RebuildRequestsFromMap();
                        }
                    }
                    else if (string.Equals(method, "Network.loadingFailed", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate)
                        {
                            var requestId = paramsEl.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrWhiteSpace(requestId) && _requestsById.TryGetValue(requestId, out var item))
                            {
                                item["failed"] = true;
                                item["errorText"] = paramsEl.TryGetProperty("errorText", out var err) ? err.GetString() ?? string.Empty : string.Empty;
                                item["canceled"] = paramsEl.TryGetProperty("canceled", out var can) && can.ValueKind == JsonValueKind.True;
                                RebuildRequestsFromMap();
                            }
                        }
                    }
                }
            }

            private async Task<JsonElement> SendCommandAsync(string method, object @params, CancellationToken cancellationToken)
            {
                var id = Interlocked.Increment(ref _nextCommandId);
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
                try
                {
                    var payload = JsonSerializer.Serialize(new { id, method, @params });
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
                    using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                    return await tcs.Task;
                }
                finally
                {
                    _pending.TryRemove(id, out _);
                }
            }

            private void RebuildRequestsFromMap()
            {
                _requests.Clear();
                foreach (var item in _requestsById.Values)
                {
                    _requests.Add(new Dictionary<string, object?>(item));
                }
                Trim(_requests, 400);
                while (_requestsById.Count > 400)
                {
                    var firstKey = _requestsById.Keys.FirstOrDefault();
                    if (firstKey == null) break;
                    _requestsById.Remove(firstKey);
                }
            }

            private static void Trim(List<object> list, int max)
            {
                while (list.Count > max) list.RemoveAt(0);
            }

            private static int ConsolePriority(string level)
            {
                return level switch
                {
                    "error" => 3,
                    "warning" => 2,
                    "warn" => 2,
                    "info" => 1,
                    "log" => 1,
                    "debug" => 0,
                    _ => 1,
                };
            }
        }
    }

    internal sealed class ChromeMcpSession : IAsyncDisposable
    {
        private const int InitializeTimeoutMs = 60000;
        private readonly string _profileName;
        private readonly Uri _browserEndpoint;
        internal string BrowserFingerprint { get; }
        private McpClient? _client;
        private StdioClientTransport? _transport;
        private bool _initialized;
        private readonly ConcurrentQueue<string> _stderrTail = new();

        public ChromeMcpSession(string profileName, Uri browserEndpoint, string browserFingerprint)
        {
            _profileName = profileName;
            _browserEndpoint = browserEndpoint;
            BrowserFingerprint = browserFingerprint;
        }

        public bool IsFaulted => _client == null || _client.Completion.IsCompleted;
        public int? ProcessId => null;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                BrowserLog.Info(_profileName, "InitializeAsync skipped (already initialized)");
                return;
            }

            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            initCts.CancelAfter(InitializeTimeoutMs);
            var initToken = initCts.Token;
            var stopwatch = Stopwatch.StartNew();
            var phase = "start";

            try
            {
                phase = "attach-existing-browser";
                BrowserLog.Info(_profileName, $"initialize phase start timeoutMs={InitializeTimeoutMs}");
                BrowserLog.Info(_profileName, $"attaching MCP to existing browser at {_browserEndpoint}");

                phase = "runtime-locate";
                var runtime = BundledBrowserRuntimeLocator.Locate();
                BrowserLog.Info(_profileName, $"using bundled runtime node={runtime.NodeExecutablePath} mcp={runtime.McpEntrypointPath}");
                Directory.CreateDirectory(runtime.McpWorkingDirectory);

                phase = "transport-create";
                BrowserLog.Info(_profileName, "transport create start");
                _transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = $"OpenClaw Node Browser ({_profileName})",
                    Command = runtime.NodeExecutablePath,
                    Arguments = new[]
                    {
                        runtime.McpEntrypointPath,
                        "--browserUrl",
                        _browserEndpoint.ToString().TrimEnd('/'),
                        "--experimentalStructuredContent",
                        "--pageIdRouting",
                        "--filesystemRoot", Path.GetTempPath(),
                        "--filesystemRoot", UploadStagingService.DefaultRoot,
                        "--usageStatistics=false",
                        "--performanceCrux=false",
                        "--categoryExperimentalThirdParty=false"
                    },
                    EnvironmentVariables = ChildProcessSecurity.SensitiveEnvironmentRemovals(),
                    StandardErrorLines = line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        _stderrTail.Enqueue(line.Trim());
                        while (_stderrTail.Count > 12 && _stderrTail.TryDequeue(out _)) { }
                        BrowserLog.Info(_profileName, $"chrome-devtools-mcp stderr: {line}");
                    }
                });
                BrowserLog.Info(_profileName, $"transport create ok elapsedMs={stopwatch.ElapsedMilliseconds}");

                phase = "mcp-create";
                BrowserLog.Info(_profileName, $"mcp client create start elapsedMs={stopwatch.ElapsedMilliseconds}");
                _client = await McpClient.CreateAsync(_transport, cancellationToken: initToken);
                BrowserLog.Info(_profileName, $"mcp client create ok elapsedMs={stopwatch.ElapsedMilliseconds}");

                phase = "tools-list";
                BrowserLog.Info(_profileName, $"tools/list phase start elapsedMs={stopwatch.ElapsedMilliseconds}");
                var tools = await _client.ListToolsAsync(cancellationToken: initToken);
                BrowserLog.Info(_profileName, $"tools/list phase ok elapsedMs={stopwatch.ElapsedMilliseconds}");
                var toolNames = tools.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n).ToList();
                if (!toolNames.Contains("list_pages", StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Chrome MCP attach failed for profile '{_profileName}'. OpenClaw could not confirm the required browser automation tools after launching or attaching to the local browser.");
                }
                BrowserLog.Info(_profileName, $"tools available count={toolNames.Count} names={string.Join(",", toolNames)} elapsedMs={stopwatch.ElapsedMilliseconds}");

                _initialized = true;
                BrowserLog.Info(_profileName, $"initialize fully ok elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException ex)
            {
                throw BuildException($"Chrome MCP initialize phase timed out after {InitializeTimeoutMs}ms at phase '{phase}' after {stopwatch.ElapsedMilliseconds}ms.", ex);
            }
            catch (Exception ex)
            {
                BrowserLog.Error(_profileName, $"initialize failed phase={phase} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
                throw BuildException($"{ex.Message} (phase '{phase}', elapsed {stopwatch.ElapsedMilliseconds}ms)", ex);
            }
        }

        public async Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object?>? arguments, CancellationToken cancellationToken)
        {
            if (_client == null)
            {
                throw new InvalidOperationException("Chrome MCP client is not initialized.");
            }

            var result = await _client.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
            if (result.IsError == true)
            {
                throw new InvalidOperationException(ExtractToolErrorMessage(result, name));
            }
            return result;
        }

        private Exception BuildException(string message, Exception inner)
        {
            var stderr = GetStderrTail();
            return string.IsNullOrWhiteSpace(stderr)
                ? new InvalidOperationException(message, inner)
                : new InvalidOperationException($"{message} | chrome-devtools-mcp stderr: {stderr}", inner);
        }

        private string GetStderrTail()
        {
            return string.Join(" | ", _stderrTail.ToArray());
        }

        private static string ExtractToolErrorMessage(CallToolResult result, string name)
        {
            var text = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return !string.IsNullOrWhiteSpace(text)
                ? text!
                : $"Chrome MCP tool '{name}' failed.";
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is IAsyncDisposable asyncClient)
            {
                try { await asyncClient.DisposeAsync(); } catch { }
            }
            else if (_client is IDisposable disposableClient)
            {
                try { disposableClient.Dispose(); } catch { }
            }

            _client = null;
            _transport = null;
            _initialized = false;
        }
    }
}
