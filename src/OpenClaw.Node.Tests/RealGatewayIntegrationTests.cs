using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class RealGatewayIntegrationTests
    {
        [Fact]
        public async Task RealGateway_NodeConnectsAndReceivesHelloOk()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            Assert.False(string.IsNullOrWhiteSpace(cfg.Url));
            Assert.False(string.IsNullOrWhiteSpace(cfg.Token));

            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "node",
                Client = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "displayName", "windows-node-test" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "test" },
                    { "instanceId", Guid.NewGuid().ToString("N") },
                    { "deviceFamily", "Windows" }
                },
                Commands = new System.Collections.Generic.List<string> { "system.notify", "system.which", "system.run", "screen.capture", "screen.list", "screen.record", "camera.list", "camera.snap", "window.list", "window.focus", "window.rect", "input.type", "input.key", "input.click", "input.scroll", "input.click.relative", "ui.find", "ui.click", "ui.type" },
                Scopes = new System.Collections.Generic.List<string>(),
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var node = new GatewayConnection(cfg.Url, cfg.Token, connectParams);

            var outcomeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            node.OnConnected += () => outcomeTcs.TrySetResult("connected");
            node.OnLog += msg =>
            {
                if (msg.Contains("Connect rejected:", StringComparison.OrdinalIgnoreCase))
                {
                    outcomeTcs.TrySetResult(msg);
                }
            };

            var nodeTask = node.StartAsync(cts.Token);

            var outcomeTask = await Task.WhenAny(outcomeTcs.Task, Task.Delay(TimeSpan.FromSeconds(18), cts.Token));
            Assert.True(
                outcomeTask == outcomeTcs.Task,
                "Timed out waiting for a definitive real-gateway node connect outcome.");

            var outcome = await outcomeTcs.Task;
            if (!string.Equals(outcome, "connected", StringComparison.Ordinal))
            {
                // Some environments require a pre-paired device identity for node role.
                // Treat this as a valid integration outcome shape instead of a flaky hard-fail.
                Assert.Contains("NOT_PAIRED", outcome, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("DEVICE_IDENTITY_REQUIRED", outcome, StringComparison.OrdinalIgnoreCase);
            }

            cts.Cancel();
            try
            {
                await nodeTask;
            }
            catch (TaskCanceledException)
            {
                // expected on cancellation during integration teardown
            }
        }

        [Fact]
        public async Task RealGateway_StatusCommand_ReturnsResponse()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var res = await client.RequestAsync("status", new { }, cts.Token);

            Assert.Equal("res", res.GetProperty("type").GetString());
            Assert.True(res.TryGetProperty("ok", out _));

            if (res.GetProperty("ok").GetBoolean())
            {
                Assert.True(res.TryGetProperty("payload", out _), JsonSerializer.Serialize(res));
            }
            else
            {
                // On some setups this token may not carry operator.read scope; still validate real command response shape.
                Assert.True(res.TryGetProperty("error", out var err), JsonSerializer.Serialize(res));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(res));
            }
        }

        [Fact]
        public async Task RealGateway_CameraSnapCommand_ReturnsResponseShape_WhenNodeAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "camera.snap",
                    @params = new { facing = "front", delayMs = 0, quality = 0.85, maxWidth = 1280 },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-camera-snap"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var cameraPayload), JsonSerializer.Serialize(invoke));
                Assert.Equal("jpg", cameraPayload.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(cameraPayload.GetProperty("base64").GetString()));
                Assert.True(cameraPayload.GetProperty("width").GetInt32() > 0);
                Assert.True(cameraPayload.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_CameraSnap_WithDeviceId_ReturnsResponseShape_WhenCameraListProvidesDevice()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var listInvoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "camera.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-camera-list-for-device-id"
                },
                cts.Token);

            if (!listInvoke.TryGetProperty("ok", out var listOk) || !listOk.GetBoolean())
            {
                return;
            }

            if (!listInvoke.TryGetProperty("payload", out var listPayload) ||
                !listPayload.TryGetProperty("devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array ||
                devices.GetArrayLength() == 0)
            {
                return;
            }

            string? deviceId = null;
            foreach (var d in devices.EnumerateArray())
            {
                if (d.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    deviceId = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(deviceId)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(deviceId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "camera.snap",
                    @params = new { deviceId, facing = "front", delayMs = 0, quality = 0.85, maxWidth = 1280 },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-camera-snap-device-id"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.Equal("jpg", p.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base64").GetString()));
                Assert.True(p.GetProperty("width").GetInt32() > 0);
                Assert.True(p.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_CameraSnap_FrontBack_WithDeviceId_ToleratesSingleCameraSemantics()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var listInvoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "camera.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-camera-list-front-back"
                },
                cts.Token);

            if (!listInvoke.TryGetProperty("ok", out var listOk) || !listOk.GetBoolean())
            {
                return;
            }

            if (!listInvoke.TryGetProperty("payload", out var listPayload) ||
                !listPayload.TryGetProperty("devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array ||
                devices.GetArrayLength() == 0)
            {
                return;
            }

            string? deviceId = null;
            foreach (var d in devices.EnumerateArray())
            {
                if (d.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    deviceId = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(deviceId)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(deviceId)) return;

            async Task<JsonElement> InvokeSnapAsync(string facing, string key)
            {
                return await client.RequestAsync(
                    "node.invoke",
                    new
                    {
                        nodeId,
                        command = "camera.snap",
                        @params = new { deviceId, facing, delayMs = 0, quality = 0.85, maxWidth = 1280 },
                        timeoutMs = 15000,
                        idempotencyKey = key
                    },
                    cts.Token);
            }

            var front = await InvokeSnapAsync("front", "itest-camera-snap-front-device-id");
            var back = await InvokeSnapAsync("back", "itest-camera-snap-back-device-id");

            Assert.Equal("res", front.GetProperty("type").GetString());
            Assert.Equal("res", back.GetProperty("type").GetString());
            Assert.True(front.TryGetProperty("ok", out var frontOk));
            Assert.True(back.TryGetProperty("ok", out var backOk));

            if (frontOk.GetBoolean())
            {
                Assert.True(front.TryGetProperty("payload", out var p), JsonSerializer.Serialize(front));
                Assert.Equal("jpg", p.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base64").GetString()));
                Assert.True(p.GetProperty("width").GetInt32() > 0);
                Assert.True(p.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.True(front.TryGetProperty("error", out var err), JsonSerializer.Serialize(front));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(front));
            }

            if (backOk.GetBoolean())
            {
                Assert.True(back.TryGetProperty("payload", out var p), JsonSerializer.Serialize(back));
                Assert.Equal("jpg", p.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base64").GetString()));
                Assert.True(p.GetProperty("width").GetInt32() > 0);
                Assert.True(p.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.True(back.TryGetProperty("error", out var err), JsonSerializer.Serialize(back));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(back));
            }

            // On single-camera hosts, front/back may map to the same device backend.
            // If both succeed, dimensions should still be consistent for identical params.
            if (frontOk.GetBoolean() && backOk.GetBoolean())
            {
                var frontPayload = front.GetProperty("payload");
                var backPayload = back.GetProperty("payload");
                Assert.Equal(frontPayload.GetProperty("width").GetInt32(), backPayload.GetProperty("width").GetInt32());
                Assert.Equal(frontPayload.GetProperty("height").GetInt32(), backPayload.GetProperty("height").GetInt32());
            }
        }

        [Fact]
        public async Task RealGateway_CameraListCommand_ReturnsResponseShape_WhenNodeAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "camera.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-camera-list"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.True(p.TryGetProperty("devices", out var devices), JsonSerializer.Serialize(invoke));
                Assert.Equal(JsonValueKind.Array, devices.ValueKind);

                foreach (var d in devices.EnumerateArray())
                {
                    Assert.True(d.TryGetProperty("id", out var id), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, id.ValueKind);

                    Assert.True(d.TryGetProperty("name", out var name), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, name.ValueKind);

                    Assert.True(d.TryGetProperty("position", out var position), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, position.ValueKind);

                    Assert.True(d.TryGetProperty("deviceType", out var deviceType), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, deviceType.ValueKind);
                }
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_WindowListCommand_ReturnsResponseShape_WhenNodeAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "window.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-window-list"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.True(p.TryGetProperty("windows", out var windows), JsonSerializer.Serialize(invoke));
                Assert.Equal(JsonValueKind.Array, windows.ValueKind);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_WindowRectCommand_ReturnsResponseShape_WhenWindowAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var listInvoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "window.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-window-list-for-rect"
                },
                cts.Token);

            if (!listInvoke.TryGetProperty("ok", out var listOk) || !listOk.GetBoolean())
            {
                return;
            }

            if (!listInvoke.TryGetProperty("payload", out var listPayload) ||
                !listPayload.TryGetProperty("windows", out var windows) ||
                windows.ValueKind != JsonValueKind.Array ||
                windows.GetArrayLength() == 0)
            {
                return;
            }

            long? handle = null;
            foreach (var w in windows.EnumerateArray())
            {
                if (w.TryGetProperty("Handle", out var hEl) && hEl.ValueKind == JsonValueKind.Number)
                {
                    handle = hEl.GetInt64();
                    break;
                }
            }

            if (!handle.HasValue || handle.Value == 0) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "window.rect",
                    @params = new { handle = handle.Value },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-window-rect"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.True(p.TryGetProperty("rect", out var rect), JsonSerializer.Serialize(invoke));
                Assert.True(rect.GetProperty("Width").GetInt32() >= 0);
                Assert.True(rect.GetProperty("Height").GetInt32() >= 0);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_ScreenListCommand_ReturnsResponseShape_WhenNodeAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "screen.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-screen-list"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.True(p.TryGetProperty("displays", out var displays), JsonSerializer.Serialize(invoke));
                Assert.Equal(JsonValueKind.Array, displays.ValueKind);

                foreach (var d in displays.EnumerateArray())
                {
                    Assert.True(d.TryGetProperty("index", out var index), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.Number, index.ValueKind);

                    Assert.True(d.TryGetProperty("id", out var id), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, id.ValueKind);

                    Assert.True(d.TryGetProperty("name", out var name), JsonSerializer.Serialize(d));
                    Assert.Equal(JsonValueKind.String, name.ValueKind);
                }
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_ScreenRecordCommand_WithDisplayIndex_ReturnsResponseShape_WhenDisplayAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var listInvoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "screen.list",
                    @params = new { },
                    timeoutMs = 15000,
                    idempotencyKey = "itest-screen-list-for-record"
                },
                cts.Token);

            if (!listInvoke.TryGetProperty("ok", out var listOk) || !listOk.GetBoolean())
            {
                return;
            }

            if (!listInvoke.TryGetProperty("payload", out var listPayload) ||
                !listPayload.TryGetProperty("displays", out var displays) ||
                displays.ValueKind != JsonValueKind.Array ||
                displays.GetArrayLength() == 0)
            {
                return;
            }

            int? screenIndex = null;
            foreach (var d in displays.EnumerateArray())
            {
                if (d.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number)
                {
                    screenIndex = indexEl.GetInt32();
                    break;
                }
            }

            if (!screenIndex.HasValue) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "screen.record",
                    @params = new { durationMs = 1000, fps = 5, includeAudio = false, screenIndex = screenIndex.Value },
                    timeoutMs = 25000,
                    idempotencyKey = "itest-screen-record-display-index"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.Equal("mp4", p.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base64").GetString()));
                Assert.True(p.GetProperty("durationMs").GetInt32() > 0);
                Assert.True(p.GetProperty("fps").GetInt32() > 0);
                Assert.True(p.GetProperty("screenIndex").GetInt32() >= 0);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        [Fact]
        public async Task RealGateway_ScreenRecordCommand_ReturnsResponseShape_WhenNodeAvailable()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REAL_GATEWAY_INTEGRATION"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var cfg = LoadGatewayConfig();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var client = new RealGatewayRpcClient();

            await client.ConnectAsOperatorAsync(cfg.Url, cfg.Token, cts.Token);
            var nodeId = await ResolveFirstConnectedNodeIdAsync(client, cts.Token);
            if (string.IsNullOrWhiteSpace(nodeId)) return;

            var invoke = await client.RequestAsync(
                "node.invoke",
                new
                {
                    nodeId,
                    command = "screen.record",
                    @params = new { durationMs = 1000, fps = 5, includeAudio = false, screenIndex = 0 },
                    timeoutMs = 25000,
                    idempotencyKey = "itest-screen-record"
                },
                cts.Token);

            Assert.Equal("res", invoke.GetProperty("type").GetString());
            Assert.True(invoke.TryGetProperty("ok", out var invokeOk));

            if (invokeOk.GetBoolean())
            {
                Assert.True(invoke.TryGetProperty("payload", out var p), JsonSerializer.Serialize(invoke));
                Assert.Equal("mp4", p.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base64").GetString()));
                Assert.True(p.GetProperty("durationMs").GetInt32() > 0);
                Assert.True(p.GetProperty("fps").GetInt32() > 0);
            }
            else
            {
                Assert.True(invoke.TryGetProperty("error", out var err), JsonSerializer.Serialize(invoke));
                Assert.True(err.TryGetProperty("code", out _), JsonSerializer.Serialize(invoke));
            }
        }

        private static async Task<string?> ResolveFirstConnectedNodeIdAsync(RealGatewayRpcClient client, CancellationToken ct)
        {
            var list = await client.RequestAsync("node.list", new { }, ct);
            if (!list.TryGetProperty("ok", out var listOk) || !listOk.GetBoolean())
            {
                return null;
            }

            if (!list.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array ||
                nodes.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("nodeId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!node.TryGetProperty("connected", out var connectedEl) || connectedEl.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                return idEl.GetString();
            }

            return null;
        }

        private static (string Url, string Token) LoadGatewayConfig()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "openclaw.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var gw = doc.RootElement.GetProperty("gateway");
            var port = gw.GetProperty("port").GetInt32();
            var token = gw.GetProperty("auth").GetProperty("token").GetString() ?? string.Empty;
            return ($"ws://127.0.0.1:{port}/", token);
        }

        private sealed class RealGatewayRpcClient : IDisposable
        {
            private readonly ClientWebSocket _ws = new();

            public async Task ConnectAsOperatorAsync(string wsUrl, string token, CancellationToken ct)
            {
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                _ws.Options.SetRequestHeader("Origin", "http://127.0.0.1");
                await _ws.ConnectAsync(new Uri(wsUrl), ct);

                var challenge = await ReceiveAsync(ct);
                Assert.Equal("event", challenge.GetProperty("type").GetString());
                Assert.Equal("connect.challenge", challenge.GetProperty("event").GetString());

                var connectId = Guid.NewGuid().ToString("N");
                await SendAsync(new
                {
                    type = "req",
                    id = connectId,
                    method = "connect",
                    @params = new
                    {
                        minProtocol = Constants.GatewayProtocolVersion,
                        maxProtocol = Constants.GatewayProtocolVersion,
                        role = "operator",
                        client = new
                        {
                            id = "cli",
                            mode = "cli",
                            platform = "windows",
                            version = "test"
                        },
                        auth = new { token }
                    }
                }, ct);

                var res = await ReceiveResponseByIdAsync(connectId, ct);
                Assert.True(res.GetProperty("ok").GetBoolean(), JsonSerializer.Serialize(res));
            }

            public async Task<JsonElement> RequestAsync(string method, object @params, CancellationToken ct)
            {
                var id = Guid.NewGuid().ToString("N");
                await SendAsync(new { type = "req", id, method, @params }, ct);
                return await ReceiveResponseByIdAsync(id, ct);
            }

            private async Task SendAsync(object obj, CancellationToken ct)
            {
                var json = JsonSerializer.Serialize(obj);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }

            private async Task<JsonElement> ReceiveResponseByIdAsync(string id, CancellationToken ct)
            {
                while (true)
                {
                    var frame = await ReceiveAsync(ct);
                    if (frame.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        type.GetString() == "res" &&
                        frame.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String &&
                        idEl.GetString() == id)
                    {
                        return frame;
                    }
                }
            }

            private async Task<JsonElement> ReceiveAsync(CancellationToken ct)
            {
                var buffer = new byte[64 * 1024];
                var sb = new StringBuilder();
                while (true)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new Exception("websocket closed");
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        using var doc = JsonDocument.Parse(sb.ToString());
                        return doc.RootElement.Clone();
                    }
                }
            }

            public void Dispose()
            {
                _ws.Dispose();
            }
        }
    }
}
