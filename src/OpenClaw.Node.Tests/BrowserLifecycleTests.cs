using System.Text.Json;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class BrowserLifecycleTests
{
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/profiles")]
    [InlineData("GET", "/tabs")]
    [InlineData("DELETE", "/tabs/missing-target")]
    public async Task PassiveAndCloseRoutes_NeverRequestBrowserStartup(string method, string path)
    {
        var backend = new LifecycleBackend();
        var proxy = new BrowserProxyService(backend);
        using var result = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = method, Path = path }));
        Assert.Equal(0, backend.StartupCalls);
        if (method == "DELETE") Assert.True(result.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ExplicitOpen_CanStillRequestStartup()
    {
        var backend = new LifecycleBackend();
        var proxy = new BrowserProxyService(backend);
        await proxy.ProxyAsync(new() { Method = "POST", Path = "/tabs/open", Body = JsonSerializer.SerializeToElement(new { url = "about:blank" }) });
        Assert.Equal(1, backend.StartupCalls);
    }

    [Theory]
    [InlineData("/tabs/focus")]
    [InlineData("/navigate")]
    [InlineData("/act")]
    [InlineData("/snapshot")]
    public async Task TargetedRequests_DoNotStartBrowserForStaleTabs(string path)
    {
        var backend = new LifecycleBackend();
        var proxy = new BrowserProxyService(backend);
        var request = new BrowserProxyRequest
        {
            Method = path == "/snapshot" ? "GET" : "POST", Path = path,
            Query = new() { ["targetId"] = "stale" },
            Body = JsonSerializer.SerializeToElement(new { targetId = "stale", url = "https://example.test/", kind = "press", key = "Escape" }),
        };
        await proxy.ProxyAsync(request);
        Assert.Equal(0, backend.StartupCalls);
    }

    private sealed class LifecycleBackend : IBrowserBackend
    {
        public int StartupCalls;
        public Task EnsureAvailableAsync(string profile, CancellationToken ct) { StartupCalls++; return Task.CompletedTask; }
        public Task<int?> GetPidAsync(string profile, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<BrowserTab>> ListTabsAsync(string profile, CancellationToken ct) => Task.FromResult<IReadOnlyList<BrowserTab>>(Array.Empty<BrowserTab>());
        public Task CloseTabAsync(string profile, string target, CancellationToken ct) => Task.CompletedTask;
        public Task<BrowserTab> OpenTabAsync(string profile, string url, CancellationToken ct) => Task.FromResult(new BrowserTab { TargetId = "created", Url = url });
        public Task FocusTabAsync(string p, string t, CancellationToken ct) => Task.CompletedTask;
        public Task<string> NavigateAsync(string p, string t, string u, CancellationToken ct) => Task.FromResult(u);
        public Task<string> SnapshotAsync(string p, string t, CancellationToken ct) => Task.FromResult("fixture");
        public Task ActAsync(string p, string t, string kind, JsonElement body, CancellationToken ct) => Task.CompletedTask;
        public Task<object?> ListConsoleMessagesAsync(string p, string? t, string? l, bool c, int? n, CancellationToken ct) => throw new NotSupportedException();
        public Task<object?> ListNetworkRequestsAsync(string p, string? t, string? f, bool c, int? n, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Dictionary<string, object?>>> GetRequestMatchesAsync(string p, string t, string? f, int n, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>?> GetLatestRequestAsync(string p, string t, string? f, CancellationToken ct) => throw new NotSupportedException();
        public string? GetLastError(string profile) => null;
    }
}
