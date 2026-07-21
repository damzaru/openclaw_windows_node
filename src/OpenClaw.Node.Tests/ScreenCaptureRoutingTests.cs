using System;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public class ScreenCaptureRoutingTests
{
    private sealed class FakeScreenProvider : IScreenImageProvider
    {
        private readonly byte[] _bytes = Convert.FromBase64String(ScreenCaptureService.Png1x1FallbackBase64);

        public Task<(byte[] bytes, int width, int height)> CaptureScreenshotBytesAsync(int screenIndex = 0, string format = "png")
            => Task.FromResult((_bytes, 1, 1));

        public Task<(byte[] bytes, int width, int height)> CaptureWindowBytesAsync(long handle, string format = "png")
            => Task.FromResult((_bytes, 1, 1));
    }

    [Fact]
    public async Task ScreenSnapshot_ReturnsCurrentGatewayPayloadShape()
    {
        using var executor = new NodeCommandExecutor(screen: new FakeScreenProvider());
        var response = await executor.ExecuteAsync(new BridgeInvokeRequest
        {
            Id = "snapshot-1",
            Command = "screen.snapshot",
            ParamsJSON = JsonSerializer.Serialize(new { format = "png", screenIndex = 0 })
        });

        Assert.True(response.Ok);
        using var document = JsonDocument.Parse(response.PayloadJSON!);
        var payload = document.RootElement;
        Assert.Equal("png", payload.GetProperty("format").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("base64").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("displayFrameId").GetString()));
        Assert.Equal(1, payload.GetProperty("width").GetInt32());
        Assert.Equal(1, payload.GetProperty("height").GetInt32());
        Assert.Equal(0, payload.GetProperty("screenIndex").GetInt32());
        Assert.True(payload.GetProperty("capturedAtMs").GetInt64() > 0);
    }

    [Fact]
    public async Task ScreenSnapshot_RejectsInvalidFormat()
    {
        using var executor = new NodeCommandExecutor(screen: new FakeScreenProvider());
        var response = await executor.ExecuteAsync(new BridgeInvokeRequest
        {
            Id = "snapshot-bad-format",
            Command = "screen.snapshot",
            ParamsJSON = "{\"format\":\"gif\"}"
        });

        Assert.False(response.Ok);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, response.Error?.Code);
    }

    [Theory]
    [InlineData("screen.capture")]
    [InlineData("screen.list")]
    [InlineData("window.list")]
    [InlineData("input.click")]
    [InlineData("ui.find")]
    public async Task LegacyAutomationCommands_AreNotGatewayInvokable(string command)
    {
        using var executor = new NodeCommandExecutor(screen: new FakeScreenProvider());
        var response = await executor.ExecuteAsync(new BridgeInvokeRequest { Id = "legacy", Command = command, ParamsJSON = "{}" });

        Assert.False(response.Ok);
        Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, response.Error?.Code);
        Assert.Contains("Unsupported or disabled gateway command", response.Error?.Message);
    }
}
