using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class NodeCommandExecutorTests
    {
        [Fact]
        public async Task SystemWhich_ShouldReturnPath_ForKnownCommand()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "1",
                Command = "system.which",
                ParamsJSON = JsonSerializer.Serialize(new { command = "dotnet" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);
        }

        [Fact]
        public async Task ScreenList_ShouldReturnDisplaysArray()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-list-1",
                Command = "screen.list"
            };

            var res = await executor.ExecuteAsync(req);

            if (!res.Ok) throw new Exception(res.Error?.Message ?? "(no error)");
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("displays", out var displays));
            Assert.Equal(JsonValueKind.Array, displays.ValueKind);

            foreach (var d in displays.EnumerateArray())
            {
                Assert.True(d.TryGetProperty("index", out var index));
                Assert.Equal(JsonValueKind.Number, index.ValueKind);
                Assert.True(index.GetInt32() >= 0);

                Assert.True(d.TryGetProperty("id", out var id));
                Assert.Equal(JsonValueKind.String, id.ValueKind);

                Assert.True(d.TryGetProperty("name", out var name));
                Assert.Equal(JsonValueKind.String, name.ValueKind);
            }
        }

        [Fact]
        public async Task ScreenRecord_ShouldReturnExpectedResult_ForCurrentPlatform()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-1",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { durationMs = 500, fps = 8, includeAudio = false, screenIndex = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            if (OperatingSystem.IsWindows())
            {
                Assert.True(res.Ok);
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("mp4", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
                Assert.Equal(500, root.GetProperty("durationMs").GetInt32());
                Assert.Equal(8, root.GetProperty("fps").GetInt32());
                Assert.Equal(0, root.GetProperty("screenIndex").GetInt32());
                Assert.False(root.GetProperty("hasAudio").GetBoolean());
                Assert.True(root.TryGetProperty("captureApi", out var captureApi));
                Assert.Equal(JsonValueKind.String, captureApi.ValueKind);
                Assert.True(root.TryGetProperty("hardwareEncoding", out var hw));
                Assert.True(hw.ValueKind == JsonValueKind.True || hw.ValueKind == JsonValueKind.False);
                Assert.True(root.TryGetProperty("lowLatency", out var ll));
                Assert.True(ll.ValueKind == JsonValueKind.True || ll.ValueKind == JsonValueKind.False);
            }
            else
            {
                // Non-Windows dev fallback keeps command path alive.
                Assert.True(res.Ok);
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("mp4", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
            }
        }

        [Fact]
        public async Task CameraList_ShouldReturnDevicesArray()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "camera-list-1",
                Command = "camera.list"
            };

            var res = await executor.ExecuteAsync(req);

            if (!res.Ok) throw new Exception(res.Error?.Message ?? "(no error)");
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("devices", out var devices));
            Assert.Equal(JsonValueKind.Array, devices.ValueKind);

            foreach (var d in devices.EnumerateArray())
            {
                Assert.True(d.TryGetProperty("id", out var id));
                Assert.Equal(JsonValueKind.String, id.ValueKind);

                Assert.True(d.TryGetProperty("name", out var name));
                Assert.Equal(JsonValueKind.String, name.ValueKind);

                Assert.True(d.TryGetProperty("position", out var position));
                Assert.Equal(JsonValueKind.String, position.ValueKind);

                Assert.True(d.TryGetProperty("deviceType", out var deviceType));
                Assert.Equal(JsonValueKind.String, deviceType.ValueKind);
            }
        }

        [Fact]
        public async Task CameraSnap_ShouldReturnExpectedPayloadShape()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "camera-1",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { facing = "front", maxWidth = 1280, quality = 0.9, delayMs = 1 })
            };

            var res = await executor.ExecuteAsync(req);

            if (res.Ok)
            {
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("jpg", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
                Assert.True(root.GetProperty("width").GetInt32() > 0);
                Assert.True(root.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            }
        }

        [Fact]
        public async Task SystemRun_InvalidTimeoutType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "run-invalid-timeout",
                Command = "system.run",
                ParamsJSON = "{\"command\":\"echo hi\",\"timeoutMs\":\"1000\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("timeoutMs", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SystemRun_Timeout_ShouldKillProcessTree_AndReturnTimedOut()
        {
            var executor = new NodeCommandExecutor();

            object command = OperatingSystem.IsWindows()
                ? new[] { "powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 2" }
                : new[] { "bash", "-lc", "sleep 2" };

            var req = new BridgeInvokeRequest
            {
                Id = "run-timeout",
                Command = "system.run",
                ParamsJSON = JsonSerializer.Serialize(new { command, timeoutMs = 100 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            Assert.Contains("timed out", res.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(res.PayloadJSON);
            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("timedOut").GetBoolean());
            Assert.Equal(-1, root.GetProperty("exitCode").GetInt32());
        }

        [Fact]
        public async Task ScreenRecord_InvalidDuration_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-duration",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { durationMs = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidFps_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-fps",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { fps = -1 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidIncludeAudioType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-audio",
                Command = "screen.record",
                ParamsJSON = "{\"includeAudio\":\"yes\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidScreenIndex_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-index",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { screenIndex = -1 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidCaptureApiType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-capture-api",
                Command = "screen.record",
                ParamsJSON = "{\"captureApi\":123}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidLowLatencyType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-low-latency",
                Command = "screen.record",
                ParamsJSON = "{\"lowLatency\":\"yes\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidDurationType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-duration-type",
                Command = "screen.record",
                ParamsJSON = "{\"durationMs\":\"1000\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidFacing_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-facing",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { facing = "side" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidFormat_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-format",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { format = "png" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidQuality_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-quality",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { quality = 1.5 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task WindowList_ShouldReturnWindowsArray()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-list-1", Command = "window.list" };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("windows", out var windows));
            Assert.Equal(JsonValueKind.Array, windows.ValueKind);
        }

        [Fact]
        public async Task WindowFocus_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-focus-1", Command = "window.focus", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task WindowRect_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-rect-1", Command = "window.rect", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputType_MissingText_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-type-1", Command = "input.type", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputKey_MissingKey_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-key-1", Command = "input.key", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_MissingCoordinates_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-click-1", Command = "input.click", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_InvalidButton_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-2",
                Command = "input.click",
                ParamsJSON = JsonSerializer.Serialize(new { x = 10, y = 20, button = "middle" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_NonIntegerCoordinates_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-3",
                Command = "input.click",
                ParamsJSON = "{\"x\":1.5,\"y\":2}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("must be integers", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InputScroll_MissingDelta_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-scroll-1", Command = "input.scroll", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputScroll_ZeroDelta_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-scroll-2",
                Command = "input.scroll",
                ParamsJSON = JsonSerializer.Serialize(new { deltaY = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClickRelative_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-relative-1",
                Command = "input.click.relative",
                ParamsJSON = JsonSerializer.Serialize(new { offsetX = 10, offsetY = 10 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClickRelative_MissingOffsets_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-relative-2",
                Command = "input.click.relative",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

                        [Fact]
        public async Task ScreenCapture_WithoutRouting_ShouldReturnInvalidRequest_OnWindows()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-capture-1",
                Command = "screen.capture"
            };

            var res = await executor.ExecuteAsync(req);

            if (OperatingSystem.IsWindows())
            {
                Assert.False(res.Ok);
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            }
            else
            {
                Assert.False(res.Ok);
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            }
        }

        [Fact]
        public async Task UiFind_MissingParams_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "ui-find-1", Command = "ui.find", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiFind_MissingSelectors_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-find-2",
                Command = "ui.find",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiClick_InvalidButton_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-click-1",
                Command = "ui.click",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit", button = "middle" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiType_MissingText_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-type-1",
                Command = "ui.type",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiFind_OnNonWindows_ShouldReturnUnavailable_WhenSelectorsProvided()
        {
            if (OperatingSystem.IsWindows()) return;

            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-find-3",
                Command = "ui.find",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit", timeoutMs = 1000 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("details", out var details));
            Assert.Equal("not-windows", details.GetProperty("reason").GetString());
        }

        [Fact]
        public async Task UnknownCommand_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "2", Command = "nope.command" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }
    }
}
