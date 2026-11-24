using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class BrowserRuntimeIntegrationTests
{
    [BrowserRuntimeFact]
    public async Task PinnedRuntime_ExercisesSnapshotInputUploadAndIdempotentCleanupInIsolatedChrome()
    {
        var runtime = BundledBrowserRuntimeLocator.Locate();
        using (var package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runtime.McpWorkingDirectory, "package.json"))))
            Assert.Equal("1.12.0", package.RootElement.GetProperty("version").GetString());
        Assert.True(File.Exists(Path.Combine(runtime.McpWorkingDirectory, "build", "src", "OPENCLAW_PATCH_NOTICE.md")));
        await using var fixture = await RuntimeFixture.StartAsync();
        Assert.True(BrowserProfileProcessGuard.IsInUse(fixture.ProfileDirectory));
        Assert.False(BrowserProfileProcessGuard.IsInUse(fixture.ProfileDirectory + "-other"));
        var cdp = new BrowserCdpClient(fixture.BrowserEndpoint);
        cdp.RegisterStartupTab(fixture.StartupTab);
        var initialTabs = await cdp.ListTabsAsync(default);
        var startup = Assert.Single(initialTabs);
        Assert.Equal(fixture.StartupTab.Url, startup.Url);
        using var backend = new ChromeMcpBrowserBackend(cdp);
        var proxy = new BrowserProxyService(backend);
        using var executor = new NodeCommandExecutor(browserProxyService: proxy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var first = await Open("one");
        Assert.Equal(startup.TargetId, first.TargetId);
        Assert.Single(await cdp.ListTabsAsync(ct));
        var second = await Open("two");
        Assert.Equal(2, (await cdp.ListTabsAsync(ct)).Count);
        var snapshot = await Snapshot(first.TargetId);
        Assert.Contains("Runtime fixture one", snapshot);
        await Act(first.TargetId, new { kind = "type", @ref = Ref(snapshot, "textbox", "Name"), text = "OpenClaw café" });
        snapshot = await Snapshot(first.TargetId);
        Assert.Contains("OpenClaw café", snapshot);
        await Act(first.TargetId, new { kind = "click", @ref = Ref(snapshot, "button", "Apply") });
        Assert.Contains("Applied: OpenClaw café", await Snapshot(first.TargetId));
        var secondSnapshot = await Snapshot(second.TargetId);
        Assert.DoesNotContain("OpenClaw café", secondSnapshot);
        snapshot = await Snapshot(first.TargetId);
        var uploadUid = Ref(snapshot, "button", "Upload file");
        var uploadParams = new
        {
            method = "POST", path = "/hooks/file-chooser", body = new { targetId = first.TargetId, inputRef = uploadUid },
            upload = new { envelope = "browser-upload-v1", files = new[] { new { name = "runtime-proof.txt", contentBase64 = Convert.ToBase64String("synthetic upload"u8.ToArray()) } } },
        };
        var uploaded = await executor.ExecuteAsync(new() { Id = "runtime-upload", Command = "browser.proxy.upload.v1", ParamsJSON = JsonSerializer.Serialize(uploadParams) }, ct);
        Assert.True(uploaded.Ok, uploaded.Error?.Message);
        Assert.Contains("Uploaded: runtime-proof.txt", await Snapshot(first.TargetId));
        // Durable ownership closes only this browser instance on one CDP socket.
        var ownership = JsonSerializer.SerializeToElement(first.Ownership);
        Assert.Equal("durable", ownership.GetProperty("status").GetString());
        Assert.Equal("closed", JsonSerializer.SerializeToElement(await cdp.CloseOwnedTabAsync("user", ownership, ct)).GetProperty("status").GetString());
        Assert.Equal("missing", JsonSerializer.SerializeToElement(await cdp.CloseOwnedTabAsync("user", ownership, ct)).GetProperty("status").GetString());
        // The legacy cleanup route also treats an actual 404 as already closed.
        await proxy.ProxyAsync(new() { Method = "DELETE", Path = "/tabs/" + first.TargetId }, ct);
        Assert.Contains(await cdp.ListTabsAsync(ct), tab => tab.TargetId == second.TargetId);
        await cdp.CloseTabAsync(second.TargetId, ct);
        await fixture.StopBrowserAsync();
        using var stopped = JsonDocument.Parse(await proxy.ProxyAsync(new() { Path = "/tabs" }, ct));
        Assert.False(stopped.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
        using var alreadyClosed = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = "DELETE", Path = "/tabs/" + first.TargetId }, ct));
        Assert.True(alreadyClosed.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal("missing", JsonSerializer.SerializeToElement(await cdp.CloseOwnedTabAsync("user", ownership, ct)).GetProperty("status").GetString());
        Console.WriteLine("Pinned Chrome MCP runtime: startup-tab reuse, snapshot, exact-target typing/click, staged upload, owned/legacy close and offline close passed.");

        async Task<BrowserTab> Open(string name)
        {
            using var response = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = "POST", Path = "/tabs/open", Body = JsonSerializer.SerializeToElement(new { url = new Uri(fixture.WebEndpoint, name).ToString() }) }, ct));
            var tab = response.RootElement.GetProperty("result");
            return new BrowserTab { TargetId = tab.GetProperty("targetId").GetString()!, Ownership = tab.GetProperty("ownership").Clone() };
        }
        async Task<string> Snapshot(string target)
        {
            using var response = JsonDocument.Parse(await proxy.ProxyAsync(new() { Path = "/snapshot", Query = new() { ["targetId"] = target } }, ct));
            return response.RootElement.GetProperty("result").GetProperty("snapshot").GetString()!;
        }
        async Task Act(string target, object body)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(body))!;
            values["targetId"] = JsonSerializer.SerializeToElement(target);
            await proxy.ProxyAsync(new() { Method = "POST", Path = "/act", Body = JsonSerializer.SerializeToElement(values) }, ct);
        }
    }

    [BrowserRuntimeFact]
    public async Task StartupTab_IsReusedOnceForConcurrentOpensWithoutTakingAnOrdinaryBlankTab()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var cdp = new BrowserCdpClient(fixture.BrowserEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var startup = Assert.Single(await cdp.ListTabsAsync(ct));
        var userBlank = await cdp.OpenTabAsync("user", "about:blank", ct);
        cdp.RegisterStartupTab(fixture.StartupTab);
        var opened = await Task.WhenAll(cdp.OpenTabAsync("user", new Uri(fixture.WebEndpoint, "one").ToString(), ct),
            cdp.OpenTabAsync("user", new Uri(fixture.WebEndpoint, "two").ToString(), ct));
        Assert.Single(opened, tab => tab.TargetId == startup.TargetId);
        var tabs = await cdp.ListTabsAsync(ct);
        Assert.Equal(3, tabs.Count);
        Assert.Contains(tabs, tab => tab.TargetId == userBlank.TargetId && tab.Url == "about:blank");
        Assert.DoesNotContain(tabs, tab => tab.Url == fixture.StartupTab.Url);
    }

    [BrowserRuntimeFact]
    public async Task StartupTab_NavigatedByTheUserIsNotReused()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var cdp = new BrowserCdpClient(fixture.BrowserEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var startup = Assert.Single(await cdp.ListTabsAsync(ct));
        var userUrl = new Uri(fixture.WebEndpoint, "user-page").ToString();
        using (var socket = new System.Net.WebSockets.ClientWebSocket())
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.BrowserEndpoint.Port}/devtools/page/{startup.TargetId}"), ct);
            await ChromeMcpBrowserBackend.SendCdpCommandAsync(socket, 1, "Page.navigate", new { url = userUrl }, ct);
        }
        while ((await cdp.GetTargetAsync(startup.TargetId, ct)).Url != userUrl) await Task.Delay(20, ct);
        cdp.RegisterStartupTab(fixture.StartupTab);
        var created = await cdp.OpenTabAsync("user", new Uri(fixture.WebEndpoint, "new-page").ToString(), ct);
        Assert.NotEqual(startup.TargetId, created.TargetId);
        var tabs = await cdp.ListTabsAsync(ct);
        Assert.Equal(2, tabs.Count);
        Assert.Contains(tabs, tab => tab.TargetId == startup.TargetId && tab.Url == userUrl);
    }

    [BrowserRuntimeFact]
    public async Task ScreenshotsCaptureTheHiddenTargetWithoutFocusingItOrItsSameUrlSibling()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var cdp = new BrowserCdpClient(fixture.BrowserEndpoint);
        cdp.RegisterStartupTab(fixture.StartupTab);
        var url = new Uri(fixture.WebEndpoint, "one").ToString();
        var background = await cdp.OpenTabAsync("user", url, ct);
        var foreground = await cdp.OpenTabAsync("user", url, ct);
        using var backend = new ChromeMcpBrowserBackend(cdp);
        var proxy = new BrowserProxyService(backend);
        using var http = new HttpClient();
        await http.GetStringAsync(new Uri(fixture.BrowserEndpoint, "json/activate/" + foreground.TargetId), ct);
        async Task<JsonElement> Eval(string target, string expression)
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.BrowserEndpoint.Port}/devtools/page/{target}"), ct);
            var result = await ChromeMcpBrowserBackend.SendCdpCommandAsync(ws, 1, "Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);
            return result.GetProperty("result").GetProperty("value");
        }
        async Task<JsonElement> Capture(object body)
        {
            using var doc = JsonDocument.Parse(await proxy.ProxyAsync(new() { Method = "POST", Path = "/screenshot", Body = JsonSerializer.SerializeToElement(body) }, ct));
            return doc.RootElement.Clone();
        }
        static byte[] Bytes(JsonElement response) => Convert.FromBase64String(response.GetProperty("files")[0].GetProperty("base64").GetString()!);
        static int Dimension(byte[] png, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
        var targetId = background.TargetId;
        await Eval(foreground.TargetId, "window.hiddenEvents=0; document.addEventListener('visibilitychange',()=>{if(document.hidden)hiddenEvents++}); true");
        async Task CheckForeground(string step)
        {
            var active = (await Eval(foreground.TargetId, "[document.visibilityState,document.hasFocus(),window.hiddenEvents].join(',')")).GetString();
            var other = (await Eval(targetId, "[document.visibilityState,document.hasFocus(),window.visibleEvents].join(',')")).GetString();
            Assert.True(active == "visible,true,0" && other == "hidden,false,0", $"{step}: foreground {active}, background {other}");
        }
        Assert.Equal("hidden", (await Eval(targetId, """
            document.body.style.minHeight='1600px';
            document.querySelector('button').style.cssText='width:123px;height:47px';
            window.visibleEvents=0; document.addEventListener('visibilitychange',()=>{if(!document.hidden)visibleEvents++});
            document.visibilityState
            """)).GetString());
        var viewport = Bytes(await Capture(new { targetId }));
        await CheckForeground("viewport");
        var full = Bytes(await Capture(new { targetId, fullPage = true }));
        Assert.True(Dimension(full, 20) > Dimension(viewport, 20));
        var snapshot = await backend.SnapshotAsync("user", targetId, ct);
        await CheckForeground("snapshot");
        var reference = Ref(snapshot, "button", "Apply");
        var plain = Bytes(await Capture(new { targetId, @ref = reference }));
        var marked = await Capture(new { targetId, @ref = reference, labels = true });
        await CheckForeground("element");
        Assert.Equal(123, Dimension(plain, 16));
        Assert.Equal(47, Dimension(plain, 20));
        Assert.Equal(123, Dimension(Bytes(marked), 16));
        var imageData = marked.GetProperty("files")[0].GetProperty("base64").GetString();
        // Verify a badge pixel in the returned file; a re-encoded but unlabeled PNG is not enough.
        var badge = await Eval(targetId, $$"""
            (async () => {
                const b = Uint8Array.from(atob('{{imageData}}'), c => c.charCodeAt(0));
                const img = await createImageBitmap(new Blob([b], {type:'image/png'}));
                const ctx = new OffscreenCanvas(1,1).getContext('2d');
                ctx.drawImage(img,-1,-1);
                return Array.from(ctx.getImageData(0,0,1,1).data).join(',');
            })()
            """);
        Assert.Equal("166,44,0,255", badge.GetString());
        Assert.Equal(1, marked.GetProperty("result").GetProperty("labelsCount").GetInt32());
        var page = await Capture(new { targetId, labels = true, type = "jpeg" });
        Assert.True(page.GetProperty("result").GetProperty("labelsCount").GetInt32() > 1);
        Assert.Equal("image/jpeg", page.GetProperty("files")[0].GetProperty("mimeType").GetString());
        Assert.Equal(new byte[] { 0xff, 0xd8 }, Bytes(page)[..2]);
        Assert.Equal(400, (await Assert.ThrowsAsync<BrowserRouteException>(() => Capture(new { targetId, element = "button" }))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<BrowserRouteException>(() => Capture(new { targetId, fullPage = true, @ref = reference }))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Capture(new { targetId, @ref = "missing" }));
        Assert.Equal(0, (await Eval(targetId, "window.visibleEvents")).GetInt32());
        Assert.Equal("visible", (await Eval(foreground.TargetId, "document.visibilityState")).GetString());
    }

    private static string Ref(string snapshot, string role, string name)
    {
        var line = snapshot.Split('\n').SingleOrDefault(value => value.Contains(role + " \"" + name + "\"", StringComparison.Ordinal));
        Assert.True(line != null, $"Missing {role} {name} in snapshot:\n{snapshot}");
        return Regex.Match(line!, @"uid=([^\s]+)").Groups[1].Value;
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenClawRuntimeProof-" + Guid.NewGuid().ToString("N"));
        private readonly HttpListener _server = new();
        private readonly CancellationTokenSource _cts = new();
        private Process? _chrome;
        private Task? _serving;
        public Uri WebEndpoint { get; } = new($"http://127.0.0.1:{FreePort()}/");
        public Uri BrowserEndpoint { get; private set; } = null!;
        public string ProfileDirectory => _directory;
        public BrowserStartupTab StartupTab { get; private set; } = null!;
        private static int FreePort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
        public static async Task<RuntimeFixture> StartAsync()
        {
            var fixture = new RuntimeFixture();
            try
            {
                Directory.CreateDirectory(fixture._directory);
                fixture._server.Prefixes.Add(fixture.WebEndpoint.ToString()); fixture._server.Start();
                fixture._serving = Task.Run(async () =>
                {
                    try
                    {
                        while (!fixture._cts.IsCancellationRequested)
                        {
                            var request = await fixture._server.GetContextAsync().WaitAsync(fixture._cts.Token);
                            request.Response.ContentType = "text/html; charset=utf-8";
                            var name = request.Request.Url!.AbsolutePath.Trim('/');
                            var html = $"""
                                <!doctype html><meta charset="utf-8"><title>Runtime fixture {name}</title>
                                <h1>Runtime fixture {name}</h1><label>Name <input id="name"></label>
                                <button onclick="document.getElementById('result').textContent='Applied: '+document.getElementById('name').value">Apply</button>
                                <input type="file" aria-label="Upload file" onchange="document.getElementById('upload').textContent='Uploaded: '+this.files[0].name">
                                <p id="result">Ready</p><p id="upload"></p>
                                """;
                            await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html), fixture._cts.Token);
                            request.Response.Close();
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
                });
                var chrome = Environment.GetEnvironmentVariable("OPENCLAW_BROWSER_TEST_CHROME") ?? @"C:\Program Files\Google\Chrome\Application\chrome.exe";
                var start = new ProcessStartInfo(chrome) { UseShellExecute = false, CreateNoWindow = true };
                var startupUrl = BrowserStartupTab.CreateUrl();
                foreach (var arg in new[] { "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", "--user-data-dir=" + fixture._directory, "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--new-window", startupUrl }) start.ArgumentList.Add(arg);
                fixture._chrome = Process.Start(start)!;
                var portFile = Path.Combine(fixture._directory, "DevToolsActivePort");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!File.Exists(portFile)) await Task.Delay(100, timeout.Token);
                fixture.BrowserEndpoint = new Uri("http://127.0.0.1:" + (await File.ReadAllLinesAsync(portFile, timeout.Token))[0] + "/");
                using var http = new HttpClient();
                using var version = JsonDocument.Parse(await http.GetStringAsync(new Uri(fixture.BrowserEndpoint, "json/version"), timeout.Token));
                fixture.StartupTab = new(startupUrl, version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
                await ManagedBrowserHost.WaitForStartupPageAsync(fixture.BrowserEndpoint, startupUrl, timeout.Token);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); _server.Close(); if (_serving != null) await _serving;
            await StopBrowserAsync();
            _chrome?.Dispose(); _cts.Dispose();
            var path = Path.GetFullPath(_directory);
            if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch (IOException) { }
            }
        }
        public async Task StopBrowserAsync()
        {
            if (_chrome is { HasExited: false }) { _chrome.Kill(entireProcessTree: true); await _chrome.WaitForExitAsync(); }
        }
    }
}

public sealed class BrowserRuntimeFactAttribute : FactAttribute
{
    public BrowserRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_BROWSER_RUNTIME_TESTS") != "1")
            Skip = "Set RUN_BROWSER_RUNTIME_TESTS=1 to launch a disposable, headless Chrome for the bundled-runtime acceptance check.";
    }
}
