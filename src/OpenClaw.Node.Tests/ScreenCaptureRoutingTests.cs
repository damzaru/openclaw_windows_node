using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public class ScreenCaptureRoutingTests
{
    private sealed class FakeGatewayRpcClient : IGatewayRpcClient
    {
        public string? LastMethod { get; private set; }
        public object? LastParams { get; private set; }

        public Task SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            LastMethod = method;
            LastParams = @params;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScreenProvider : IScreenImageProvider
    {
        private readonly byte[] _pngBytes;
        public FakeScreenProvider(byte[] pngBytes)
        {
            _pngBytes = pngBytes;
        }

        public Task<(byte[] bytes, int width, int height)> CaptureScreenshotBytesAsync(int screenIndex = 0, string format = "png")
            => Task.FromResult((_pngBytes, 1, 1));

        public Task<(byte[] bytes, int width, int height)> CaptureWindowBytesAsync(long handle, string format = "png")
            => Task.FromResult((_pngBytes, 1, 1));
    }

    [Fact]
    public async Task ScreenCapture_ModeDeliver_RoutesToAgentRequest_WithStrictDeliveryTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        var rpc = new FakeGatewayRpcClient();
        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var exec = new NodeCommandExecutor(rpc, new FakeScreenProvider(png));

        var req = new BridgeInvokeRequest
        {
            Id = "deliver-1",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new
            {
                mode = "deliver",
                message = "shot",
                sessionKey = "agent:main:discord:channel:1475224687599554842",
                channel = "discord",
                to = "channel:1475224687599554842"
            })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.True(res.Ok);
        Assert.Equal("node.event", rpc.LastMethod);
        Assert.NotNull(rpc.LastParams);

        var rpcJson = JsonSerializer.Serialize(rpc.LastParams, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var rpcDoc = JsonDocument.Parse(rpcJson);
        Assert.Equal("agent.request", rpcDoc.RootElement.GetProperty("event").GetString());
        var payload = rpcDoc.RootElement.GetProperty("payload");
        Assert.Equal("discord", payload.GetProperty("channel").GetString());
        Assert.Equal("channel:1475224687599554842", payload.GetProperty("to").GetString());
        Assert.True(payload.GetProperty("deliver").GetBoolean());

        Assert.NotNull(res.PayloadJSON);
        using var doc = JsonDocument.Parse(res.PayloadJSON!);
        var root = doc.RootElement;
        Assert.Equal("deliver", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("delivery", out _));
        Assert.False(root.TryGetProperty("routed", out _));
        Assert.False(root.TryGetProperty("preview", out _));
        Assert.False(root.TryGetProperty("full", out _));
    }

    [Fact]
    public async Task ScreenCapture_ModeDeliver_RequiresChannelAndTo()
    {
        if (!OperatingSystem.IsWindows()) return;

        var rpc = new FakeGatewayRpcClient();
        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var exec = new NodeCommandExecutor(rpc, new FakeScreenProvider(png));

        var req = new BridgeInvokeRequest
        {
            Id = "deliver-2",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new { mode = "deliver" })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        Assert.Contains("requires params.channel and params.to", res.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScreenCapture_ModeFile_WritesOutput_AndReturnsMetadataOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var exec = new NodeCommandExecutor(null, new FakeScreenProvider(png));

        var outPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-screen-{Guid.NewGuid():N}.png");

        try
        {
            var req = new BridgeInvokeRequest
            {
                Id = "file-1",
                Command = "screen.capture",
                ParamsJSON = JsonSerializer.Serialize(new { mode = "file", outputPath = outPath, format = "png" })
            };

            var res = await exec.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.True(File.Exists(outPath));
            Assert.True(new FileInfo(outPath).Length > 0);

            Assert.NotNull(res.PayloadJSON);
            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.Equal("file", root.GetProperty("mode").GetString());
            Assert.Equal(outPath, root.GetProperty("file").GetProperty("path").GetString());
            Assert.False(root.TryGetProperty("inline", out _));
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public async Task ScreenCapture_ModeData_ReturnsInline_WhenUnderCap()
    {
        if (!OperatingSystem.IsWindows()) return;

        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var exec = new NodeCommandExecutor(null, new FakeScreenProvider(png));

        var req = new BridgeInvokeRequest
        {
            Id = "data-1",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new { mode = "data", maxInlineBytes = 200000 })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.True(res.Ok);
        Assert.NotNull(res.PayloadJSON);

        using var doc = JsonDocument.Parse(res.PayloadJSON!);
        var root = doc.RootElement;
        Assert.Equal("data", root.GetProperty("mode").GetString());
        var inline = root.GetProperty("inline");
        var base64 = inline.GetProperty("base64").GetString();
        Assert.False(string.IsNullOrWhiteSpace(base64));
    }

    [Fact]
    public async Task ScreenCapture_ModeData_OverCap_ShouldReturnInvalidRequest()
    {
        if (!OperatingSystem.IsWindows()) return;

        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var exec = new NodeCommandExecutor(null, new FakeScreenProvider(png));

        var req = new BridgeInvokeRequest
        {
            Id = "data-2",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new { mode = "data", maxInlineBytes = 1 })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        Assert.Contains("maxInlineBytes", res.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScreenCapture_LegacyParams_ShouldReturnInvalidRequest()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exec = new NodeCommandExecutor();
        var req = new BridgeInvokeRequest
        {
            Id = "legacy-1",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new { sendToAgent = true })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        Assert.Contains("no longer supported", res.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScreenCapture_InvalidMode_ShouldReturnInvalidRequest()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exec = new NodeCommandExecutor();
        var req = new BridgeInvokeRequest
        {
            Id = "invalid-mode-1",
            Command = "screen.capture",
            ParamsJSON = JsonSerializer.Serialize(new { mode = "banana" })
        };

        var res = await exec.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        Assert.Contains("params.mode", res.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageEncoding_EncodesJpegBase64_FromPngBytes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var png = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);
        var encoded = ImageEncoding.EncodeJpegBase64(png, maxWidth: 100, quality: 0.8);
        Assert.False(string.IsNullOrWhiteSpace(encoded.Base64));
        Assert.Equal("image/jpeg", encoded.MimeType);
        Assert.True(encoded.Bytes > 0);
        Assert.True(encoded.Width > 0);
        Assert.True(encoded.Height > 0);
    }
}
