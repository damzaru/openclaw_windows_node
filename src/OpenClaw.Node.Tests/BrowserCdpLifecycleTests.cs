using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class BrowserCdpLifecycleTests
{
    [Fact]
    public async Task EmptyBrowser_StatusAndListStayEmptyWithoutMcpOrNewTabs()
    {
        await using var fixture = new CdpFixture();
        using var backend = new ChromeMcpBrowserBackend(new BrowserCdpClient(fixture.Endpoint));
        var proxy = new BrowserProxyService(backend);
        foreach (var path in new[] { "/", "/tabs", "/profiles" }) await proxy.ProxyAsync(new() { Path = path });
        Assert.All(fixture.Requests, path => Assert.Equal("/json/list", path));
    }

    [Fact]
    public async Task MissingLegacyClose_IsIdempotentAndNeverCreatesAnything()
    {
        await using var fixture = new CdpFixture();
        using var backend = new ChromeMcpBrowserBackend(new BrowserCdpClient(fixture.Endpoint));
        var proxy = new BrowserProxyService(backend);
        for (var i = 0; i < 3; i++)
        {
            using var response = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = "DELETE", Path = "/tabs/stale" }));
            Assert.True(response.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        }
        Assert.All(fixture.Requests, path => Assert.Equal("/json/close/stale", path));
    }

    [Fact]
    public async Task OfflineBrowser_PassiveReadDoesNotStartItAndLegacyCloseSucceeds()
    {
        using var backend = new ChromeMcpBrowserBackend(new BrowserCdpClient(new Uri($"http://127.0.0.1:{CdpFixture.FreePort()}/")));
        var proxy = new BrowserProxyService(backend);
        using var tabs = JsonDocument.Parse(await proxy.ProxyAsync(new() { Path = "/tabs" }));
        Assert.False(tabs.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
        for (var i = 0; i < 3; i++)
        {
            using var closed = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = "DELETE", Path = "/tabs/stale" }));
            Assert.True(closed.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task OfflineBrowser_OwnedCloseReportsMissing()
    {
        var client = new BrowserCdpClient(new Uri($"http://127.0.0.1:{CdpFixture.FreePort()}/"));
        var result = JsonSerializer.SerializeToElement(await client.CloseOwnedTabAsync("user", Ownership(), default));
        Assert.Equal("missing", result.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(408)]
    [InlineData(503)]
    public async Task RealCloseErrors_PreserveHttpStatusThroughNodeDispatcher(int status)
    {
        await using var fixture = new CdpFixture { CloseError = status };
        using var backend = new ChromeMcpBrowserBackend(new BrowserCdpClient(fixture.Endpoint));
        using var executor = new NodeCommandExecutor(browserProxyService: new BrowserProxyService(backend));
        var result = await executor.ExecuteAsync(new() { Id = "error", Command = "browser.proxy", ParamsJSON = """{"method":"DELETE","path":"/tabs/stale","errorEnvelope":"browser-v1"}""" });
        Assert.True(result.Ok);
        using var body = JsonDocument.Parse(result.PayloadJSON!);
        Assert.Equal(status, body.RootElement.GetProperty("error").GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task OwnedClose_EndpointErrorsAreNotReportedAsMissing(int status)
    {
        await using var fixture = new CdpFixture { VersionError = status };
        var client = new BrowserCdpClient(fixture.Endpoint);
        var result = JsonSerializer.SerializeToElement(await client.CloseOwnedTabAsync("user", Ownership(), default));
        Assert.Equal("unavailable", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LegacyClose_CancellationIsNotReportedAsSuccess()
    {
        await using var fixture = new CdpFixture { CloseDelay = TimeSpan.FromSeconds(5) };
        var client = new BrowserCdpClient(fixture.Endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CloseTabAsync("stale", timeout.Token));
    }

    private static JsonElement Ownership() => JsonSerializer.SerializeToElement(new
    {
        status = "durable", nativeTargetId = "stale", profileFingerprint = "sha256:old-profile", browserInstanceFingerprint = "sha256:old-browser",
    });

    [Fact]
    public async Task OwnedClose_AfterRestartRetiresOldOwnershipWithoutClosingNewTargets()
    {
        await using var fixture = new CdpFixture();
        var client = new BrowserCdpClient(fixture.Endpoint);
        var created = await client.OpenTabAsync("user", "https://example.test/fixture", default);
        fixture.BrowserInstance = "replacement-browser";
        var result = JsonSerializer.SerializeToElement(await client.CloseOwnedTabAsync("user", JsonSerializer.SerializeToElement(created.Ownership), default));
        Assert.Equal("ownership-mismatch", result.GetProperty("status").GetString());
        Assert.DoesNotContain(fixture.Requests, path => path.StartsWith("/json/close") || path.StartsWith("/devtools/"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupTab_OnlyMatchesTheAppMarkerInTheSameBrowserInstance(bool replacedBrowser)
    {
        var marker = BrowserStartupTab.CreateUrl();
        await using var fixture = new CdpFixture
        {
            Tabs = new object[] { new { id = "existing", type = "page", url = replacedBrowser ? marker : "about:blank" } },
        };
        var client = new BrowserCdpClient(fixture.Endpoint);
        client.RegisterStartupTab(new(marker, $"ws://127.0.0.1:{fixture.Endpoint.Port}/devtools/browser/{(replacedBrowser ? "old-browser" : fixture.BrowserInstance)}"));
        await client.OpenTabAsync("user", "https://example.test/one", default);
        await client.OpenTabAsync("user", "https://example.test/two", default);
        Assert.Equal(2, fixture.Requests.Count(path => path == "/json/new"));
        Assert.DoesNotContain(fixture.Requests, path => path.StartsWith("/devtools/"));
    }

    private sealed class CdpFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public Uri Endpoint { get; } = new($"http://127.0.0.1:{FreePort()}/");
        public List<string> Requests { get; } = new();
        public string BrowserInstance { get; set; } = "fixture-browser";
        public int CloseError { get; init; }
        public int VersionError { get; init; }
        public TimeSpan CloseDelay { get; init; }
        public object[] Tabs { get; init; } = Array.Empty<object>();
        public CdpFixture()
        {
            _listener.Prefixes.Add(Endpoint.ToString()); _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                        var path = context.Request.Url!.AbsolutePath;
                        Requests.Add(path);
                        object payload;
                        if (path == "/json/list") payload = Tabs;
                        else if (path == "/json/version") { context.Response.StatusCode = VersionError == 0 ? 200 : VersionError; payload = new { webSocketDebuggerUrl = $"ws://127.0.0.1:{Endpoint.Port}/devtools/browser/{BrowserInstance}" }; }
                        else if (path == "/json/new") payload = new { id = "created", type = "page", title = "fixture", url = "https://example.test/fixture" };
                        else { await Task.Delay(CloseDelay, _cts.Token); context.Response.StatusCode = CloseError == 0 ? 404 : CloseError; payload = new { error = "test close response" }; }
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes, _cts.Token); context.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });
        }
        public static int FreePort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
        public async ValueTask DisposeAsync() { _cts.Cancel(); _listener.Close(); await _loop; _cts.Dispose(); }
    }
}
