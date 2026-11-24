using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Node.Services;

internal sealed class BrowserRouteException(int status, string message) : InvalidOperationException(message)
{
    internal int Status { get; } = status;
}

// A one-use capability for the exact startup page created by our own launch.
internal sealed record BrowserStartupTab(string Url, string BrowserWebSocketUrl)
{
    internal static string CreateUrl() => "data:text/html," + Uri.EscapeDataString("<!--openclaw-startup-" + Guid.NewGuid().ToString("N") + "-->");
}

/// <summary>Non-launching lifecycle operations against the configured loopback CDP endpoint.</summary>
internal sealed class BrowserCdpClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private BrowserStartupTab? _startupTab;
    internal void RegisterStartupTab(BrowserStartupTab tab) => Interlocked.Exchange(ref _startupTab, tab);
    internal Uri Endpoint { get; }
    internal BrowserCdpClient(Uri? endpoint = null)
    {
        Endpoint = endpoint ?? new Uri(BundledBrowserRuntimeLocator.DefaultBrowserUrl);
        if (Endpoint.Scheme != "http" || Endpoint.Host != "127.0.0.1" || Endpoint.UserInfo.Length > 0 || Endpoint.AbsolutePath != "/" || Endpoint.Query.Length > 0)
            throw new ArgumentException("Browser CDP must use an IPv4 loopback HTTP origin");
    }

    internal async Task<IReadOnlyList<BrowserTab>> ListTabsAsync(CancellationToken ct)
    {
        var entries = await Http.GetFromJsonAsync<ChromeMcpBrowserBackend.DevToolsListEntry[]>(new Uri(Endpoint, "json/list"), ct)
            ?? throw new BrowserRouteException(503, "Browser target list was empty or invalid");
        return entries.Where(entry => entry.Type == "page" && !string.IsNullOrEmpty(entry.Id)).Select(entry => new BrowserTab
        {
            TargetId = entry.Id!, Title = entry.Title ?? "", Url = entry.Url ?? "", Type = "page",
        }).ToArray();
    }

    internal async Task<ChromeMcpBrowserBackend.DevToolsListEntry> GetTargetAsync(string targetId, CancellationToken ct)
    {
        var entries = await Http.GetFromJsonAsync<ChromeMcpBrowserBackend.DevToolsListEntry[]>(new Uri(Endpoint, "json/list"), ct);
        return entries?.SingleOrDefault(entry => entry.Id == targetId && entry.Type == "page")
            ?? throw new BrowserRouteException(404, "tab not found");
    }

    internal async Task CloseTabAsync(string targetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new BrowserRouteException(400, "targetId is required");
        try
        {
            using var response = await Http.GetAsync(new Uri(Endpoint, "json/close/" + Uri.EscapeDataString(targetId)), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
        }
        // The loopback browser has already gone away. Closing is idempotent;
        // other transport failures, timeouts and HTTP errors still propagate.
        catch (HttpRequestException ex) when (IsConnectionRefused(ex)) { }
    }

    internal async Task<BrowserTab> OpenTabAsync(string profile, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startup = Interlocked.Exchange(ref _startupTab, null);
        if (startup != null)
        {
            try
            {
                var reused = await TryReuseStartupTabAsync(profile, url, startup, ct);
                if (reused != null) return reused;
            }
            catch
            {
                // A cancelled/failed request may retry, but must not replace a
                // capability registered for a newer managed browser instance.
                Interlocked.CompareExchange(ref _startupTab, startup, null);
                throw;
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Endpoint, "json/new?" + Uri.EscapeDataString(url)));
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var entry = await response.Content.ReadFromJsonAsync<ChromeMcpBrowserBackend.DevToolsListEntry>(ct)
            ?? throw new BrowserRouteException(503, "Browser did not return the created target");
        if (string.IsNullOrEmpty(entry.Id)) throw new BrowserRouteException(503, "Created browser target has no identity");
        var identity = await ReadIdentityAsync(profile, ct);
        return ToTab(entry, profile, url, identity.Value);
    }

    private async Task<BrowserTab?> TryReuseStartupTabAsync(string profile, string url, BrowserStartupTab startup, CancellationToken ct)
    {
        var identity = (await ReadIdentityAsync(profile, ct)).Value;
        if (identity == null || identity.WebSocket.AbsoluteUri != startup.BrowserWebSocketUrl) return null;
        var entries = await Http.GetFromJsonAsync<ChromeMcpBrowserBackend.DevToolsListEntry[]>(new Uri(Endpoint, "json/list"), ct);
        var matches = entries?.Where(entry => entry.Type == "page" && entry.Url == startup.Url && !string.IsNullOrEmpty(entry.Id)).Take(2).ToArray();
        if (matches?.Length != 1) return null;
        var entry = matches[0];
        using var socket = new ClientWebSocket();
        var targetSocket = new Uri($"ws://127.0.0.1:{Endpoint.Port}/devtools/page/{Uri.EscapeDataString(entry.Id!)}");
        await socket.ConnectAsync(targetSocket, ct);
        // Recheck on the same target connection: never take a blank/user tab or
        // a startup page that the user has since navigated somewhere else.
        var history = await ChromeMcpBrowserBackend.SendCdpCommandAsync(socket, 1, "Page.getNavigationHistory", new { }, ct);
        if (!history.TryGetProperty("currentIndex", out var index) || !index.TryGetInt32(out var current) ||
            !history.TryGetProperty("entries", out var navigation) || navigation.ValueKind != JsonValueKind.Array ||
            current < 0 || current >= navigation.GetArrayLength() ||
            !navigation[current].TryGetProperty("url", out var currentUrl) || currentUrl.GetString() != startup.Url) return null;
        var result = await ChromeMcpBrowserBackend.SendCdpCommandAsync(socket, 2, "Page.navigate", new { url }, ct);
        if (result.TryGetProperty("errorText", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(error.GetString()))
            throw new BrowserRouteException(502, "Startup tab navigation failed: " + error.GetString());
        BrowserLog.Info(profile, $"reused managed startup targetId={entry.Id}");
        entry.Url = url;
        entry.Title = string.Empty;
        return ToTab(entry, profile, url, identity);
    }

    private static BrowserTab ToTab(ChromeMcpBrowserBackend.DevToolsListEntry entry, string profile, string url, Identity? identity)
    {
        return new BrowserTab
        {
            TargetId = entry.Id!, Title = entry.Title ?? "", Url = entry.Url ?? url, Type = "page", ResolvedProfile = profile,
            Ownership = identity == null ? (object)new { status = "non-durable", reason = "browser-identity-lookup-failed" }
                : new { status = "durable", nativeTargetId = entry.Id, profileFingerprint = identity.ProfileFingerprint, browserInstanceFingerprint = identity.BrowserFingerprint },
        };
    }

    private sealed record Identity(Uri WebSocket, string ProfileFingerprint, string BrowserFingerprint);
    private sealed record IdentityRead(Identity? Value = null, bool BrowserClosed = false);

    internal async Task<string> GetBrowserFingerprintAsync(string profile, CancellationToken ct)
        => (await ReadIdentityAsync(profile, ct)).Value?.BrowserFingerprint
            ?? throw new BrowserRouteException(503, "Browser identity is unavailable; no browser was started");

    private async Task<IdentityRead> ReadIdentityAsync(string profile, CancellationToken ct)
    {
        try
        {
            using var version = await Http.GetFromJsonAsync<JsonDocument>(new Uri(Endpoint, "json/version"), ct);
            if (version == null || version.RootElement.ValueKind != JsonValueKind.Object || !version.RootElement.TryGetProperty("webSocketDebuggerUrl", out var value) || value.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var ws) || ws.Scheme != "ws" || ws.Host != Endpoint.Host || ws.Port != Endpoint.Port ||
                ws.UserInfo.Length > 0 || ws.Query.Length > 0 || !Regex.IsMatch(ws.AbsolutePath, "^/devtools/browser/[A-Za-z0-9._-]+$")) return new();
            // Same canonical hashes as the Gateway CDP ownership contract.
            var profileFingerprint = Fingerprint(JsonSerializer.Serialize(new[] { profile, $"http://127.0.0.1:{Endpoint.Port}" }));
            var browserFingerprint = Fingerprint($"ws://127.0.0.1:{ws.Port}{ws.AbsolutePath}");
            return new(new Identity(ws, profileFingerprint, browserFingerprint));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException error) when (IsConnectionRefused(error)) { return new(BrowserClosed: true); }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException) { return new(); }
    }

    internal async Task<object> CloseOwnedTabAsync(string profile, JsonElement ownership, CancellationToken ct)
    {
        string Required(string key) => ownership.ValueKind == JsonValueKind.Object && ownership.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim() : throw new BrowserRouteException(400, "Valid durable tab ownership is required");
        if (Required("status") != "durable") throw new BrowserRouteException(400, "Valid durable tab ownership is required");
        var target = Required("nativeTargetId");
        var expectedProfile = Required("profileFingerprint");
        var expectedBrowser = Required("browserInstanceFingerprint");
        var lookup = await ReadIdentityAsync(profile, ct);
        if (lookup.BrowserClosed) return new { status = "missing" };
        var identity = lookup.Value;
        if (identity == null) return new { status = "unavailable", reason = "browser-identity-lookup-failed" };
        if (identity.ProfileFingerprint != expectedProfile || identity.BrowserFingerprint != expectedBrowser)
            return new { status = "ownership-mismatch" };
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(identity.WebSocket, ct);
            var targets = await ChromeMcpBrowserBackend.SendCdpCommandAsync(socket, 1, "Target.getTargets", new { }, ct);
            if (!targets.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
                return new { status = "unavailable", reason = "target-lookup-failed" };
            if (!infos.EnumerateArray().Any(info => info.TryGetProperty("targetId", out var id) && id.GetString() == target))
                return new { status = "missing" };
            try
            {
                var closed = await ChromeMcpBrowserBackend.SendCdpCommandAsync(socket, 2, "Target.closeTarget", new { targetId = target }, ct);
                return closed.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                    ? (object)new { status = "closed" } : new { status = "unavailable", reason = "target-close-failed" };
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No target with given id found", StringComparison.Ordinal))
            { return new { status = "missing" }; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsConnectionRefused(ex)) { return new { status = "missing" }; }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or InvalidOperationException or JsonException)
        { return new { status = "unavailable", reason = "target-lookup-failed" }; }
    }

    private static bool IsConnectionRefused(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }

    private static string Fingerprint(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
