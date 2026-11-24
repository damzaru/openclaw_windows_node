using System.Text.Json;
using System.Text;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class GatewaySeptemberParityTests
{
    [Fact]
    public void EventFrames_AcceptCurrentStateVersionObject()
    {
        var frame = JsonSerializer.Deserialize<EventFrame>("""{"type":"event","event":"presence","stateVersion":{"presence":12,"health":7},"recipientProfileId":"profile-test"}""");
        Assert.Equal(12, frame?.StateVersion?.Presence);
        Assert.Equal(7, frame?.StateVersion?.Health);
        Assert.Equal("profile-test", frame?.RecipientProfileId);
    }
    [Theory]
    [InlineData("PAIRING_REJECTED")]
    [InlineData("PAIRING_EXPIRED")]
    [InlineData("AUTH_IDENTITY_HEADER_REQUIRED")]
    [InlineData("AUTH_VERIFIED_USER_REQUIRED")]
    [InlineData("CONTROL_UI_BUILD_MISMATCH")]
    public void Reconnect_TerminalRequirementsPause(string code)
    {
        var error = new GatewayErrorShape { Code = "UNAUTHORIZED", Details = JsonSerializer.SerializeToElement(new { code }) };
        Assert.True(GatewayConnection.ShouldPauseAfterConnectError(error));
    }

    [Fact]
    public async Task Prepare_OmitsAbsentFileOperandAndRemainsConsumableByCurrentGateway()
    {
        using var executor = new NodeCommandExecutor();
        var result = await executor.ExecuteAsync(new() { Id = "prepare", Command = "system.run.prepare", ParamsJSON = """{"command":["dotnet","--info"]}""" });
        Assert.True(result.Ok, result.Error?.Message);
        using var payload = JsonDocument.Parse(result.PayloadJSON!);
        var plan = payload.RootElement.GetProperty("plan");
        Assert.False(plan.TryGetProperty("mutableFileOperand", out _));
        Assert.Equal("dotnet", plan.GetProperty("argv")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(plan.GetProperty("commandText").GetString()));
    }

    [Fact]
    public void TokenClearing_DoesNotRemoveAReplacementToken()
    {
        var store = new DeviceTokenStore(new SecureStore(Path.Combine(TestEnvironment.Directory, Guid.NewGuid().ToString("N"))));
        var endpoint = new Uri("wss://gateway.example/test");
        store.Save(endpoint, "device", "node", "replacement", Array.Empty<string>());
        store.Clear(endpoint, "device", "node", "old-observed-token");
        Assert.Equal("replacement", store.Load(endpoint, "device", "node")?.Token);
        store.Clear(endpoint, "device", "node", "replacement");
        Assert.Null(store.Load(endpoint, "device", "node"));
    }

    [Theory]
    [InlineData("agentId")]
    [InlineData("sessionKey")]
    [InlineData("cwd")]
    public async Task PreparedExecution_RejectsChangedIdentityOrWorkingDirectory(string field)
    {
        using var executor = new NodeCommandExecutor();
        var plan = new Dictionary<string, object?>
        {
            ["argv"] = new[] { "dotnet", "--info" }, ["commandText"] = "dotnet --info",
            [field] = "prepared-value",
        };
        var parameters = new Dictionary<string, object?>
        {
            ["command"] = new[] { "dotnet", "--info" }, [field] = "changed-value", ["systemRunPlan"] = plan,
        };
        var result = await executor.ExecuteAsync(new() { Id = "changed-plan", Command = "system.run", ParamsJSON = JsonSerializer.Serialize(parameters) });
        Assert.False(result.Ok);
        Assert.Contains("binding changed", result.Error!.Message);
    }

    [Theory]
    [InlineData("mutableFileOperand")]
    [InlineData("policySnapshot")]
    public async Task PreparedExecution_DoesNotIgnoreUnsupportedPortableBindings(string field)
    {
        using var executor = new NodeCommandExecutor();
        var plan = new Dictionary<string, object?>
        {
            ["argv"] = new[] { "dotnet", "--info" }, ["commandText"] = "dotnet --info", [field] = new { },
        };
        var result = await executor.ExecuteAsync(new() { Id = "unsupported-plan", Command = "system.run", ParamsJSON = JsonSerializer.Serialize(new { command = new[] { "dotnet", "--info" }, systemRunPlan = plan }) });
        Assert.False(result.Ok);
        Assert.Contains("not supported", result.Error!.Message);
    }

    [Fact]
    public void Settings_PreserveVncCredentialWhitespace()
    {
        var settings = new CompanionSettings { DesktopVncPassword = " password " };
        settings.Normalize();
        Assert.Equal(" password ", settings.DesktopVncPassword);
    }

    [Fact]
    public void SurfaceUrls_FollowRemoteGatewayTlsWithoutLosingMountOrCapability()
    {
        Assert.Equal("https://gateway.example:18789/mount/__openclaw__/cap/token", GatewayConnection.NormalizeSurfaceUrl(
            "http://127.0.0.1:18789/mount/__openclaw__/cap/token", new Uri("wss://gateway.example:18789/")));
        Assert.Throws<InvalidOperationException>(() => GatewayConnection.NormalizeSurfaceUrl("file:///private", new Uri("ws://localhost/")));
    }

    [Fact]
    public void Manifest_AdvertisesUploadsAndOnlyOptInComputerActions()
    {
        Assert.Contains("terminal.upload", NodeCapabilityRegistry.Build(new()).GatewayCommands);
        Assert.Contains("browser.proxy.upload.v1", NodeCapabilityRegistry.Build(new()).GatewayCommands);
        Assert.DoesNotContain("computer.act", NodeCapabilityRegistry.Build(new()).GatewayCommands);
        var enabled = NodeCapabilityRegistry.Build(new() { EnableComputerControl = true, EnableBrowserProxy = false });
        Assert.Contains("computer.act", enabled.GatewayCommands);
        Assert.Contains("computer", enabled.Capabilities);
        Assert.DoesNotContain("browser.proxy.upload.v1", enabled.GatewayCommands);
        var descriptor = JsonSerializer.SerializeToElement(ComputerControlService.Descriptor("test-generation"));
        Assert.Equal(2, descriptor.GetProperty("contractVersion").GetInt32());
        Assert.Equal(new[] { "screen" }, descriptor.GetProperty("targets").EnumerateArray().Select(v => v.GetString()));
        Assert.DoesNotContain("launch_app", descriptor.GetProperty("actions").EnumerateArray().Select(v => v.GetString()));
        Assert.DoesNotContain("desktop.stream", NodeCapabilityRegistry.Build(new()).GatewayCommands);
        Assert.Contains("desktop.stream", NodeCapabilityRegistry.Build(new() { EnableDesktopSharing = true }).GatewayCommands);
    }

    [Fact]
    public void StreamInvocations_ZeroDeadlineMeansUntilCancellationAndTicketsStayOnGatewayMount()
    {
        Assert.Null(GatewayConnection.ResolveInvokeTimeoutMs(0));
        Assert.Equal(Constants.DefaultRequestTimeoutMs, GatewayConnection.ResolveInvokeTimeoutMs(null));
        var ticket = new string('a', 48);
        var payload = JsonSerializer.Serialize(new { ticket, attachPath = "/node-desktop/attach?ticket=" + ticket });
        Assert.Equal("wss://gateway.example/mount/node-desktop/attach?ticket=" + ticket,
            DesktopStreamService.ResolveAttachUri(payload, new Uri("wss://gateway.example/mount/")).ToString());
        Assert.Throws<InvalidOperationException>(() => DesktopStreamService.ResolveAttachUri(
            JsonSerializer.Serialize(new { ticket, attachPath = "wss://other.example/steal" }), new Uri("wss://gateway.example/")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task DesktopStream_RefusesUnauthenticatedOrUnsupportedRfbSecurity(int security)
    {
        using var fixture = new RfbFixture(Encoding.ASCII.GetBytes("RFB 003.008\n").Concat(new byte[] { 1, (byte)security }).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStreamService.InspectRfbAsync(fixture, CancellationToken.None));
    }

    [Theory]
    [InlineData("aGVsbG8=", true)]
    [InlineData("", true)]
    [InlineData("Zh==", false)]
    [InlineData("Zg", false)]
    [InlineData("Z g==", false)]
    public void Uploads_RequireCanonicalBoundedBase64(string content, bool valid)
    {
        if (valid) Assert.Equal(content, Convert.ToBase64String(UploadStagingService.DecodeCanonicalBase64(content, 100)));
        else Assert.Throws<InvalidOperationException>(() => UploadStagingService.DecodeCanonicalBase64(content, 100));
    }

    [Fact]
    public async Task TerminalUpload_StagesSanitizedBytesThroughTheCommandDispatcher()
    {
        using var executor = new NodeCommandExecutor();
        var result = await executor.ExecuteAsync(new() { Id = "upload", Command = "terminal.upload", ParamsJSON = """{"name":"../../CON.txt","contentBase64":"aGVsbG8="}""" });
        Assert.True(result.Ok, result.Error?.Message);
        using var payload = JsonDocument.Parse(result.PayloadJSON!);
        var path = payload.RootElement.GetProperty("path").GetString()!;
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.StartsWith(Path.Combine(TestEnvironment.Directory, "uploads"), path);
        Assert.Equal(5, payload.RootElement.GetProperty("size").GetInt32());
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("..", Path.GetFileName(path));
        Assert.Equal("_CON.txt", UploadStagingService.SanitizeName("CON.txt"));
    }

    [Fact]
    public async Task BrowserUpload_ReplacesGatewayPathsAndReturnsStructuredFailures()
    {
        var proxy = new RecordingProxy();
        using var executor = new NodeCommandExecutor(browserProxyService: proxy);
        var result = await executor.ExecuteAsync(new() { Id = "browser-upload", Command = "browser.proxy.upload.v1", ParamsJSON = """
            {"method":"POST","path":"/hooks/file-chooser","body":{"targetId":"tab","inputRef":"ref"},"upload":{"envelope":"browser-upload-v1","files":[{"name":"report.txt","contentBase64":"aGVsbG8="}]}}
            """ });
        Assert.True(result.Ok, result.Error?.Message);
        var path = proxy.Last!.Body!.Value.GetProperty("paths")[0].GetString()!;
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
        proxy.Fail = true;
        var failed = await executor.ExecuteAsync(new() { Id = "browser-failure", Command = "browser.proxy", ParamsJSON = """{"path":"/tabs","errorEnvelope":"browser-v1"}""" });
        Assert.True(failed.Ok);
        using var error = JsonDocument.Parse(failed.PayloadJSON!);
        Assert.Equal(503, error.RootElement.GetProperty("error").GetProperty("status").GetInt32());
        var legacy = await executor.ExecuteAsync(new() { Id = "legacy", Command = "browser.proxy", ParamsJSON = """{"path":"/hooks/file-chooser","upload":{}}""" });
        Assert.False(legacy.Ok);
    }

    [Fact]
    public async Task ComputerCoordinates_BindReferenceWidthAndCurrentDisplayLayout()
    {
        IReadOnlyList<DisplayGeometry> displays = new[] { new DisplayGeometry("secondary", -1920, 0, 1920, 1080) };
        var input = new RecordingInput();
        using var service = new ComputerControlService(() => displays, input);
        var frame = service.RegisterFrame(0, 960, 540, service.CurrentTopology());
        var json = JsonSerializer.Serialize(new { action = "left_click", x = 480, y = 270, displayFrameId = frame, refWidth = 960 });
        await service.ActAsync(json, CancellationToken.None);
        Assert.Contains("move:-960,540", input.Events);
        Assert.Contains("mouse:left:True", input.Events);
        input.Events.Clear();
        displays = new[] { new DisplayGeometry("secondary", 0, 0, 1920, 1080) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync(json, CancellationToken.None));
        Assert.StartsWith("COMPUTER_STALE_OBSERVATION", error.Message);
        Assert.Empty(input.Events);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("hover")]
    [InlineData("type")]
    [InlineData("press")]
    [InlineData("drag")]
    public async Task BrowserInputRoutes_KeepExplicitTargetAndSuccessEnvelope(string kind)
    {
        var backend = new RoutingBrowserBackend();
        var proxy = new BrowserProxyService(backend);
        var json = await proxy.ProxyAsync(new() { Method = "POST", Path = "/act", Body = JsonSerializer.SerializeToElement(new { kind, targetId = "tab-A", @ref = "1_2" }) });
        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal("tab-A", backend.Target);
        Assert.Equal(kind, backend.Kind);
    }

    [Fact]
    public async Task BrowserSnapshotAndUpload_ExerciseRealProxyRoutes()
    {
        var backend = new RoutingBrowserBackend();
        var proxy = new BrowserProxyService(backend);
        var snapshot = await proxy.ProxyAsync(new() { Path = "/snapshot", Query = new() { ["targetId"] = "tab-A" } });
        using var result = JsonDocument.Parse(snapshot);
        Assert.Equal("uid=1_2 button Upload", result.RootElement.GetProperty("result").GetProperty("snapshot").GetString());
        using var executor = new NodeCommandExecutor(browserProxyService: proxy);
        var upload = await executor.ExecuteAsync(new() { Id = "routed-upload", Command = "browser.proxy.upload.v1", ParamsJSON = """
            {"method":"POST","path":"/hooks/file-chooser","body":{"targetId":"tab-A","inputRef":"1_2"},"upload":{"envelope":"browser-upload-v1","files":[{"name":"fixture.txt","contentBase64":"aGVsbG8="}]}}
            """ });
        Assert.True(upload.Ok, upload.Error?.Message);
        Assert.Equal("tab-A", backend.Target);
        Assert.Equal("1_2", backend.Uid);
        Assert.Equal("hello", await File.ReadAllTextAsync(Assert.Single(backend.Paths!)));
    }

    [Fact]
    public async Task ComputerCancellation_ReleasesHeldKeysWithoutInjectingFurtherInput()
    {
        using var cancel = new CancellationTokenSource();
        var input = new RecordingInput { OnDown = () => cancel.Cancel() };
        using var service = new ComputerControlService(() => Array.Empty<DisplayGeometry>(), input);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ActAsync("""{"action":"hold_key","keys":"CTRL+A","durationMs":10000}""", cancel.Token));
        Assert.Contains("key:17:False", input.Events);
        Assert.DoesNotContain("key:65:True", input.Events);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync("""{"action":"type","text":"x","deliveryMode":"background"}""", CancellationToken.None));
    }

    [Fact]
    public async Task SplitDrag_CannotBeReleasedByAnotherExecutionAndDisconnectReleasesInput()
    {
        var input = new RecordingInput();
        using var service = new ComputerControlService(() => new[] { new DisplayGeometry("display", 0, 0, 100, 100) }, input);
        const string first = "123e4567-e89b-42d3-a456-426614174000";
        const string other = "123e4567-e89b-42d3-a456-426614174001";
        var frame = service.RegisterFrame(0, 100, 100, service.CurrentTopology());
        await service.ActAsync(JsonSerializer.Serialize(new { action = "left_mouse_down", executionId = first, modifiers = "CTRL" }), CancellationToken.None);
        Assert.Contains("key:17:True", input.Events);
        Assert.DoesNotContain("key:17:False", input.Events);
        input.Events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync(JsonSerializer.Serialize(new { action = "left_mouse_up", executionId = other }), CancellationToken.None));
        Assert.Empty(input.Events);
        await service.ResetAsync();
        Assert.Contains("mouse:left:False", input.Events);
        Assert.Contains("key:17:False", input.Events);
        Assert.True(input.Events.IndexOf("mouse:left:False") < input.Events.IndexOf("key:17:False"));
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync(JsonSerializer.Serialize(new { action = "mouse_move", x = 1, y = 1, displayFrameId = frame, refWidth = 100 }), CancellationToken.None));
        Assert.StartsWith("COMPUTER_STALE_OBSERVATION", stale.Message);
    }

    private sealed class RecordingInput : IComputerInput
    {
        public List<string> Events { get; } = new();
        public Action? OnDown { get; init; }
        public void Move(int x, int y) => Events.Add($"move:{x},{y}");
        public void Mouse(string button, bool down) => Events.Add($"mouse:{button}:{down}");
        public void Scroll(int amount, bool horizontal) => Events.Add($"scroll:{amount}:{horizontal}");
        public void Key(ushort key, bool down) { Events.Add($"key:{key}:{down}"); if (down) OnDown?.Invoke(); }
        public Task TypeAsync(string text, CancellationToken cancellationToken) { Events.Add("type:" + text); return Task.CompletedTask; }
    }

    private sealed class RfbFixture : Stream
    {
        private readonly MemoryStream _read;
        public RfbFixture(byte[] bytes) => _read = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _read.Dispose(); base.Dispose(disposing); }
    }

    private sealed class RoutingBrowserBackend : IBrowserBackend
    {
        public string? Target, Kind, Uid;
        public string[]? Paths;
        public Task EnsureAvailableAsync(string profileName, CancellationToken ct) => Task.CompletedTask;
        public Task<int?> GetPidAsync(string profileName, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<BrowserTab>> ListTabsAsync(string profileName, CancellationToken ct) => Task.FromResult<IReadOnlyList<BrowserTab>>(new[] { new BrowserTab { TargetId = "tab-A", Url = "http://localhost/fixture" } });
        public Task<string> SnapshotAsync(string profileName, string targetId, CancellationToken ct) { Target = targetId; return Task.FromResult("uid=1_2 button Upload"); }
        public Task ActAsync(string profileName, string targetId, string kind, JsonElement body, CancellationToken ct) { Target = targetId; Kind = kind; return Task.CompletedTask; }
        public Task UploadAsync(string profileName, string targetId, string uid, string[] paths, CancellationToken ct) { Target = targetId; Uid = uid; Paths = paths; return Task.CompletedTask; }
        public string? GetLastError(string profileName) => null;
        public Task<BrowserTab> OpenTabAsync(string profileName, string url, CancellationToken ct) => throw new NotSupportedException();
        public Task FocusTabAsync(string profileName, string targetId, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseTabAsync(string profileName, string targetId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> NavigateAsync(string profileName, string targetId, string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<object?> ListConsoleMessagesAsync(string profileName, string? targetId, string? level, bool clear, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<object?> ListNetworkRequestsAsync(string profileName, string? targetId, string? filter, bool clear, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Dictionary<string, object?>>> GetRequestMatchesAsync(string profileName, string targetId, string? filter, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>?> GetLatestRequestAsync(string profileName, string targetId, string? filter, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingProxy : IBrowserProxyService
    {
        public BrowserProxyRequest? Last { get; private set; }
        public bool Fail { get; set; }
        public Task<string> ProxyAsync(BrowserProxyRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            if (Fail) throw new InvalidOperationException("browser unavailable");
            return Task.FromResult("{\"result\":{\"ok\":true}}");
        }
    }
}
