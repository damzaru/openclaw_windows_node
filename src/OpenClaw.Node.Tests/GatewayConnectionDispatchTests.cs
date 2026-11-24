using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class GatewayConnectionDispatchTests
    {
        [Fact]
        public async Task GatewayConnection_PreservesNativePermissionRemediationDetails()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", CreateNodeConnectParams());
            connection.OnNodeInvoke += req => Task.FromResult(new BridgeInvokeResponse
            {
                Id = req.Id, Ok = false,
                Error = new OpenClawNodeError
                {
                    Code = OpenClawNodeErrorCode.Unavailable, Message = "Computer control is disabled",
                    Details = JsonSerializer.SerializeToElement(new { capabilities = new[] { "computer" }, state = "disabled-in-openclaw" }),
                },
            });
            var running = connection.StartAsync(cts.Token);
            await CompleteConnectAsync(server, cts.Token);
            await server.SendJsonAsync(new { type = "event", @event = "node.invoke.request", payload = new { id = "permission", nodeId = "node", command = "computer.act", paramsJSON = "{}" } }, cts.Token);
            using var result = await server.ReceiveJsonAsync(cts.Token);
            var details = result.RootElement.GetProperty("params").GetProperty("error").GetProperty("details");
            Assert.Equal("disabled-in-openclaw", details.GetProperty("state").GetString());
            Assert.Equal("computer", details.GetProperty("capabilities")[0].GetString());
            cts.Cancel();
            await running;
        }

        [Fact]
        public async Task GatewayConnection_ShouldHandleConnectAndDispatchStatusRequest()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();

            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "node",
                Client = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "dev" }
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", connectParams);
            connection.OnLog += message => Console.WriteLine(message);

            connection.RegisterMethodHandler("status", _ =>
                Task.FromResult<object?>(new { ok = true, status = "online" }));

            var runTask = connection.StartAsync(cts.Token);

            // 1) server sends connect.challenge and reads connect req
            var connectReq = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("req", connectReq.RootElement.GetProperty("type").GetString());
            Assert.Equal("connect", connectReq.RootElement.GetProperty("method").GetString());
            var connectId = connectReq.RootElement.GetProperty("id").GetString()!;

            var p = connectReq.RootElement.GetProperty("params");
            Assert.True(p.TryGetProperty("device", out var device));
            Assert.Equal("n", device.GetProperty("nonce").GetString());
            Assert.False(string.IsNullOrWhiteSpace(device.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(device.GetProperty("publicKey").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(device.GetProperty("signature").GetString()));

            // 2) server responds hello-ok
            await server.SendJsonAsync(new
            {
                type = "res",
                id = connectId,
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    server = new { version = "2026.11.5" },
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cts.Token);

            // 3) server sends status request and expects status response
            await server.SendJsonAsync(new
            {
                type = "req",
                id = "status-1",
                method = "status"
            }, cts.Token);

            var statusRes = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("res", statusRes.RootElement.GetProperty("type").GetString());
            Assert.Equal("status-1", statusRes.RootElement.GetProperty("id").GetString());
            Assert.True(statusRes.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("2026.11.5", connection.ServerVersion);

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldSendNodeInvokeResultAsRequest()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();

            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "node",
                Client = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "dev" }
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", connectParams);
            connection.OnNodeInvoke += req =>
                Task.FromResult(new BridgeInvokeResponse
                {
                    Id = req.Id,
                    Ok = true,
                    PayloadJSON = "{\"ok\":true}"
                });

            var runTask = connection.StartAsync(cts.Token);

            // connect handshake
            var connectReq = await server.ReceiveJsonAsync(cts.Token);
            var connectId = connectReq.RootElement.GetProperty("id").GetString()!;
            await server.SendJsonAsync(new
            {
                type = "res",
                id = connectId,
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cts.Token);

            // send invoke request event from gateway
            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.request",
                payload = new
                {
                    id = "invoke-1",
                    command = "system.which",
                    paramsJSON = "{\"command\":\"dotnet\"}"
                }
            }, cts.Token);

            var invokeResultReq = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("req", invokeResultReq.RootElement.GetProperty("type").GetString());
            Assert.Equal("node.invoke.result", invokeResultReq.RootElement.GetProperty("method").GetString());
            var p = invokeResultReq.RootElement.GetProperty("params");
            Assert.Equal("invoke-1", p.GetProperty("id").GetString());
            Assert.True(p.GetProperty("ok").GetBoolean());
            Assert.True(p.TryGetProperty("nodeId", out var nodeIdEl));
            Assert.False(string.IsNullOrWhiteSpace(nodeIdEl.GetString()));

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldReplaceOversizedInvokeResultWithTerminalError()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection(
                $"ws://127.0.0.1:{port}/",
                "test-token",
                CreateNodeConnectParams(),
                new GatewayConnectionOptions { MaximumFrameBytes = 64 * 1024 });
            connection.OnNodeInvoke += req => Task.FromResult(new BridgeInvokeResponse
            {
                Id = req.Id,
                Ok = true,
                PayloadJSON = JsonSerializer.Serialize(new { data = new string('x', 100_000) }),
            });

            var runTask = connection.StartAsync(cts.Token);
            await CompleteConnectAsync(server, cts.Token);
            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.request",
                payload = new { id = "oversized-1", nodeId = "node-1", command = "system.which", paramsJSON = "{}" }
            }, cts.Token);

            using var result = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("node.invoke.result", result.RootElement.GetProperty("method").GetString());
            var resultParams = result.RootElement.GetProperty("params");
            Assert.False(resultParams.GetProperty("ok").GetBoolean());
            Assert.Equal("UNAVAILABLE", resultParams.GetProperty("error").GetProperty("code").GetString());
            Assert.False(resultParams.TryGetProperty("payloadJSON", out _));

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldIgnoreInvokeEventsBeforeHelloAdmission()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", CreateNodeConnectParams());
            var invoked = 0;
            connection.OnNodeInvoke += req =>
            {
                Interlocked.Increment(ref invoked);
                return Task.FromResult(new BridgeInvokeResponse { Id = req.Id, Ok = true });
            };

            var runTask = connection.StartAsync(cts.Token);
            using var connect = await server.ReceiveJsonAsync(cts.Token);
            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.request",
                payload = new { id = "pre-auth", nodeId = "node-1", command = "system.which", paramsJSON = "{}" }
            }, cts.Token);
            await Task.Delay(100, cts.Token);
            Assert.Equal(0, Volatile.Read(ref invoked));

            await server.SendJsonAsync(new
            {
                type = "res",
                id = connect.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cts.Token);

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldReturnErrorForUnhandledMethod()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();

            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "node",
                Client = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "dev" }
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", connectParams);

            var runTask = connection.StartAsync(cts.Token);

            var connectReq = await server.ReceiveJsonAsync(cts.Token);
            var connectId = connectReq.RootElement.GetProperty("id").GetString()!;

            await server.SendJsonAsync(new
            {
                type = "res",
                id = connectId,
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cts.Token);

            await server.SendJsonAsync(new
            {
                type = "req",
                id = "unknown-1",
                method = "does.not.exist"
            }, cts.Token);

            var res = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("res", res.RootElement.GetProperty("type").GetString());
            Assert.Equal("unknown-1", res.RootElement.GetProperty("id").GetString());
            Assert.False(res.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("INVALID_REQUEST", res.RootElement.GetProperty("error").GetProperty("code").GetString());

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldReturnUnavailableWhenHandlerThrows()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();

            var connectParams = new ConnectParams
            {
                MinProtocol = Constants.GatewayProtocolVersion,
                MaxProtocol = Constants.GatewayProtocolVersion,
                Role = "node",
                Client = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "dev" }
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", connectParams);
            connection.RegisterMethodHandler("status", _ => throw new InvalidOperationException("boom"));

            var runTask = connection.StartAsync(cts.Token);

            var connectReq = await server.ReceiveJsonAsync(cts.Token);
            var connectId = connectReq.RootElement.GetProperty("id").GetString()!;

            await server.SendJsonAsync(new
            {
                type = "res",
                id = connectId,
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cts.Token);

            await server.SendJsonAsync(new
            {
                type = "req",
                id = "status-err-1",
                method = "status"
            }, cts.Token);

            var res = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("res", res.RootElement.GetProperty("type").GetString());
            Assert.Equal("status-err-1", res.RootElement.GetProperty("id").GetString());
            Assert.False(res.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("UNAVAILABLE", res.RootElement.GetProperty("error").GetProperty("code").GetString());

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldCancelActiveInvokeAndReturnOneTerminalResult()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", CreateNodeConnectParams());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnNodeInvokeContext += async context =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                return new BridgeInvokeResponse { Id = context.Request.Id, Ok = true };
            };
            var runTask = connection.StartAsync(cts.Token);
            await CompleteConnectAsync(server, cts.Token);

            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.request",
                payload = new { id = "cancel-1", nodeId = "node-1", command = "system.which", paramsJSON = "{}", timeoutMs = 5000 }
            }, cts.Token);
            await started.Task.WaitAsync(cts.Token);
            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.cancel",
                payload = new { invokeId = "cancel-1", nodeId = "node-1" }
            }, cts.Token);

            using var result = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("node.invoke.result", result.RootElement.GetProperty("method").GetString());
            var payload = result.RootElement.GetProperty("params");
            Assert.Equal("cancel-1", payload.GetProperty("id").GetString());
            Assert.False(payload.GetProperty("ok").GetBoolean());
            var error = payload.GetProperty("error");
            Assert.Equal("CANCELLED", error.GetProperty("code").GetString());
            Assert.Equal(2, error.EnumerateObject().Count());

            cts.Cancel();
            await runTask;
        }

        [Fact]
        public async Task GatewayConnection_ShouldRouteOrderedInputAndAcknowledgeProgress()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", CreateNodeConnectParams());
            connection.OnNodeInvokeContext += async context =>
            {
                var input = await context.Inputs.ReadAsync(context.CancellationToken);
                await context.ReportProgressAsync(input.PayloadJSON, context.CancellationToken);
                return new BridgeInvokeResponse { Id = context.Request.Id, Ok = true, PayloadJSON = "{\"done\":true}" };
            };
            var runTask = connection.StartAsync(cts.Token);
            await CompleteConnectAsync(server, cts.Token);

            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.request",
                payload = new { id = "duplex-1", nodeId = "node-1", command = "agent.cli.claude.run.v1", paramsJSON = "{}" }
            }, cts.Token);
            await server.SendJsonAsync(new
            {
                type = "event",
                @event = "node.invoke.input",
                payload = new { id = "duplex-1", nodeId = "node-1", seq = 0, payloadJSON = "{\"kind\":\"data\",\"data\":\"hello\"}" }
            }, cts.Token);

            using var progress = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("node.invoke.progress", progress.RootElement.GetProperty("method").GetString());
            var progressParams = progress.RootElement.GetProperty("params");
            Assert.Equal("duplex-1", progressParams.GetProperty("invokeId").GetString());
            Assert.Equal(0, progressParams.GetProperty("seq").GetInt64());
            Assert.Contains("hello", progressParams.GetProperty("chunk").GetString());
            await server.SendJsonAsync(new
            {
                type = "res",
                id = progress.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new { }
            }, cts.Token);

            using var result = await server.ReceiveJsonAsync(cts.Token);
            Assert.Equal("node.invoke.result", result.RootElement.GetProperty("method").GetString());
            Assert.True(result.RootElement.GetProperty("params").GetProperty("ok").GetBoolean());

            cts.Cancel();
            await runTask;
        }

        [Theory]
        [InlineData("file.stat")]
        [InlineData("file.fetch")]
        [InlineData("dir.list")]
        [InlineData("dir.fetch")]
        [InlineData("file.write")]
        [InlineData("file.create")]
        public void GatewayConnection_FilePreflightsMustAlwaysInspectFreshIdentity(string command)
        {
            Assert.True(GatewayConnection.BypassesIdempotencyReceipts(new() { Command = command, ParamsJSON = "{\"preflightOnly\":true}" }));
            Assert.False(GatewayConnection.BypassesIdempotencyReceipts(new() { Command = "system.which", ParamsJSON = "{\"preflightOnly\":true}" }));
            if (command != "file.create")
                Assert.False(GatewayConnection.BypassesIdempotencyReceipts(new() { Command = command, ParamsJSON = "{}" }));
        }

        [Fact]
        public async Task GatewayConnection_ShouldAllowPreflightAndBoundTransferToShareKeyWithoutDisablingMutationReceipts()
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port);
            await server.StartAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "test-token", CreateNodeConnectParams());
            var calls = 0;
            connection.OnNodeInvoke += req => Task.FromResult(new BridgeInvokeResponse
            {
                Id = req.Id, Ok = true, PayloadJSON = JsonSerializer.Serialize(new { calls = Interlocked.Increment(ref calls) })
            });
            var run = connection.StartAsync(cts.Token);
            await CompleteConnectAsync(server, cts.Token);
            async Task<JsonElement> Invoke(string id, string parameters)
            {
                await server.SendJsonAsync(new
                {
                    type = "event", @event = "node.invoke.request",
                    payload = new { id, nodeId = "node-files", command = "file.write", paramsJSON = parameters, idempotencyKey = "same-gateway-operation" }
                }, cts.Token);
                using var response = await server.ReceiveJsonAsync(cts.Token);
                Assert.Equal("node.invoke.result", response.RootElement.GetProperty("method").GetString());
                var result = response.RootElement.GetProperty("params").Clone();
                await server.SendJsonAsync(new { type = "res", id = response.RootElement.GetProperty("id").GetString(), ok = true, payload = new { } }, cts.Token);
                return result;
            }
            const string probe = "{\"path\":\"C:/Transfer/test.txt\",\"preflightOnly\":true}";
            const string bound = "{\"path\":\"C:/Transfer/test.txt\",\"expectedCanonicalPath\":\"C:/Transfer/test.txt\",\"expectedBinding\":{\"kind\":\"write\"}}";
            Assert.True((await Invoke("probe", probe)).GetProperty("ok").GetBoolean());
            Assert.True((await Invoke("transfer", bound)).GetProperty("ok").GetBoolean());
            Assert.Equal(2, calls);
            Assert.True((await Invoke("retry", bound)).GetProperty("ok").GetBoolean());
            Assert.Equal(2, calls);
            var conflict = await Invoke("conflict", "{\"path\":\"C:/Transfer/other.txt\"}");
            Assert.False(conflict.GetProperty("ok").GetBoolean());
            Assert.Equal("INVALID_REQUEST", conflict.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(2, calls);
            Assert.True((await Invoke("fresh-probe", probe)).GetProperty("ok").GetBoolean());
            Assert.Equal(3, calls);
            var cached = await Invoke("retry-after-probe", bound);
            Assert.True(cached.GetProperty("ok").GetBoolean());
            Assert.Equal(2, JsonDocument.Parse(cached.GetProperty("payloadJSON").GetString()!).RootElement.GetProperty("calls").GetInt32());
            Assert.Equal(3, calls);
            cts.Cancel();
            await run;
        }

        [WindowsFileTheory]
        [InlineData("file.fetch")]
        [InlineData("file.create")]
        public async Task GatewayConnection_ShouldCarryFileTransferFramingThroughRealWebSocket(string command)
        {
            var directory = Path.Combine(Path.GetTempPath(), "openclaw-file-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var content = RandomNumberGenerator.GetBytes(30001);
                var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                var path = Path.Combine(directory, "transfer.bin");
                if (command == "file.fetch") File.WriteAllBytes(path, content);
                var p = new Dictionary<string, object> { ["path"] = path, ["preflightOnly"] = true };
                if (command == "file.fetch") { p["transport"] = "binary"; p["maxBytes"] = content.Length; }
                else { p["sizeBytes"] = content.Length; p["expectedSha256"] = digest; }
                var preflight = JsonSerializer.SerializeToElement(await new FileTransferService().ExecuteAsync(command, JsonSerializer.Serialize(p), null, default));
                Assert.True(preflight.GetProperty("ok").GetBoolean(), preflight.ToString());
                p.Remove("preflightOnly"); p["expectedCanonicalPath"] = preflight.GetProperty("path").GetString()!; p["expectedBinding"] = preflight.GetProperty("binding");
                var port = GetFreePort();
                await using var server = new MockGatewayServer(port); await server.StartAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var store = new SecureStore(Path.Combine(directory, "identity"));
                using var connection = new GatewayConnection($"ws://127.0.0.1:{port}/", "synthetic-token", CreateNodeConnectParams(), deviceIdentityService: new DeviceIdentityService(store), deviceTokenStore: new DeviceTokenStore(store));
                using var executor = new NodeCommandExecutor(settings: new() { EnableFileRead = true, EnableFileWrite = true });
                connection.OnNodeInvokeContext += executor.ExecuteContextAsync;
                var run = connection.StartAsync(cts.Token); await CompleteConnectAsync(server, cts.Token);
                await server.SendJsonAsync(new { type = "event", @event = "node.invoke.request", payload = new { id = "file-wire", nodeId = "node-file", command, paramsJSON = JsonSerializer.Serialize(p), timeoutMs = 0 } }, cts.Token);
                long inputSequence = 0, progressSequence = 0;
                async Task SendInput(object frame)
                    => await server.SendJsonAsync(new { type = "event", @event = "node.invoke.input", payload = new { id = "file-wire", nodeId = "node-file", seq = inputSequence++, payloadJSON = JsonSerializer.Serialize(frame) } }, cts.Token);
                async Task<JsonElement> ReadProgress()
                {
                    using var request = await server.ReceiveJsonAsync(cts.Token);
                    Assert.Equal("node.invoke.progress", request.RootElement.GetProperty("method").GetString());
                    var progress = request.RootElement.GetProperty("params"); Assert.Equal(progressSequence++, progress.GetProperty("seq").GetInt64());
                    using var frame = JsonDocument.Parse(progress.GetProperty("chunk").GetString()!);
                    await server.SendJsonAsync(new { type = "res", id = request.RootElement.GetProperty("id").GetString(), ok = true, payload = new { } }, cts.Token);
                    return frame.RootElement.Clone();
                }
                var ready = await ReadProgress(); Assert.Equal("ready", ready.GetProperty("kind").GetString());
                if (command == "file.fetch")
                {
                    await SendInput(new { v = 1, kind = "data", message = 0, index = 0, last = true, data = Convert.ToBase64String(Encoding.UTF8.GetBytes("start")) });
                    using var received = new MemoryStream(); var index = 0;
                    for (;;)
                    {
                        var frame = await ReadProgress(); Assert.Equal("data", frame.GetProperty("kind").GetString()); Assert.Equal(0, frame.GetProperty("message").GetInt32()); Assert.Equal(index++, frame.GetProperty("index").GetInt32());
                        received.Write(Convert.FromBase64String(frame.GetProperty("data").GetString()!));
                        if (frame.GetProperty("last").GetBoolean()) break;
                    }
                    Assert.Equal(content, received.ToArray());
                    await SendInput(new { v = 1, kind = "data", message = 1, index = 0, last = true, data = Convert.ToBase64String(Encoding.UTF8.GetBytes("ack")) });
                }
                else
                {
                    for (var offset = 0; offset < content.Length; offset += NodeDuplexChannel.FragmentBytes)
                        await SendInput(new { v = 1, kind = "data", message = 0, index = offset / NodeDuplexChannel.FragmentBytes, last = offset + NodeDuplexChannel.FragmentBytes >= content.Length, data = Convert.ToBase64String(content, offset, Math.Min(NodeDuplexChannel.FragmentBytes, content.Length - offset)) });
                    var ack = await ReadProgress(); Assert.Equal("ack", Encoding.UTF8.GetString(Convert.FromBase64String(ack.GetProperty("data").GetString()!)));
                    Assert.False(File.Exists(path));
                    await SendInput(new { v = 1, kind = "data", message = 1, index = 0, last = true, data = "" });
                }
                using var result = await server.ReceiveJsonAsync(cts.Token);
                Assert.Equal("node.invoke.result", result.RootElement.GetProperty("method").GetString()); Assert.True(result.RootElement.GetProperty("params").GetProperty("ok").GetBoolean());
                using var receipt = JsonDocument.Parse(result.RootElement.GetProperty("params").GetProperty("payloadJSON").GetString()!);
                Assert.True(receipt.RootElement.GetProperty("ok").GetBoolean(), receipt.RootElement.ToString()); Assert.Equal(digest, receipt.RootElement.GetProperty("sha256").GetString());
                Assert.Equal(content, File.ReadAllBytes(path));
                cts.Cancel(); await run;
            }
            finally { Directory.Delete(directory, recursive: true); }
        }

        [Theory]
        [InlineData("PAIRING_REQUIRED", true)]
        [InlineData("AUTH_SCOPE_MISMATCH", true)]
        [InlineData("AUTH_DEVICE_TOKEN_MISMATCH", false)]
        public async Task StoredAuthentication_UsesDeviceTokenAndClearsOnlyRejectedCredential(string code, bool retained)
        {
            var port = GetFreePort();
            await using var server = new MockGatewayServer(port, challengeTimestamp: 1_800_000_123_456);
            await server.StartAsync();
            var endpoint = new Uri($"ws://127.0.0.1:{port}/");
            var identityService = new DeviceIdentityService();
            var identity = identityService.LoadOrCreate();
            var store = new DeviceTokenStore();
            store.Save(endpoint, identity.DeviceId, "node", "cached-device-credential", Array.Empty<string>());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new GatewayConnection(endpoint.ToString(), "", CreateNodeConnectParams(), deviceIdentityService: identityService, deviceTokenStore: store);
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnConnectRejected += _ => rejected.TrySetResult();
            var running = connection.StartAsync(cts.Token);
            using var connect = await server.ReceiveJsonAsync(cts.Token);
            var parameters = connect.RootElement.GetProperty("params");
            Assert.False(parameters.GetProperty("auth").TryGetProperty("token", out _));
            Assert.Equal("cached-device-credential", parameters.GetProperty("auth").GetProperty("deviceToken").GetString());
            Assert.Equal(1_800_000_123_456, parameters.GetProperty("device").GetProperty("signedAt").GetInt64());
            await server.SendJsonAsync(new { type = "res", id = connect.RootElement.GetProperty("id").GetString(), ok = false,
                error = new { code = "UNAUTHORIZED", message = "test rejection", details = new { code } } }, cts.Token);
            await rejected.Task.WaitAsync(cts.Token);
            Assert.Equal(retained, store.Load(endpoint, identity.DeviceId, "node") != null);
            cts.Cancel();
            await running;
        }

        [Fact]
        public async Task DesktopStream_RelaysInspectedRfbConnectionThroughGatewayTicket()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var localInput = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rfb = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), deadline.Token);
                var reply = new byte[12];
                await stream.ReadExactlyAsync(reply, deadline.Token);
                Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(reply));
                await stream.WriteAsync(new byte[] { 1, 2 }, deadline.Token);
                var input = new byte[3];
                await stream.ReadExactlyAsync(input, deadline.Token);
                localInput.TrySetResult(input);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("screen-data"), deadline.Token);
                await release.Task.WaitAsync(deadline.Token);
            }, deadline.Token);
            try
            {
                var gatewayPort = GetFreePort();
                await using var server = new MockGatewayServer(gatewayPort, sendChallenge: false);
                await server.StartAsync();
                using var gateway = new GatewayConnection($"ws://127.0.0.1:{gatewayPort}/mount/", "", CreateNodeConnectParams());
                var ticket = new string('a', 48);
                var running = DesktopStreamService.RunAsync(JsonSerializer.Serialize(new { ticket, attachPath = "/node-desktop/attach?ticket=" + ticket }), gateway, localPort, "synthetic-vnc-password", deadline.Token);
                using var metadata = await server.ReceiveJsonAsync(deadline.Token);
                Assert.Equal("vnc-password", metadata.RootElement.GetProperty("auth").GetString());
                Assert.Equal("synthetic-vnc-password", metadata.RootElement.GetProperty("vncPassword").GetString());
                var prefix = await server.ReceiveBytesAsync(deadline.Token);
                Assert.Equal(Encoding.ASCII.GetBytes("RFB 003.008\n").Concat(new byte[] { 1, 2 }), prefix);
                await server.SendBytesAsync(Encoding.ASCII.GetBytes("RFB 003.008\n").Concat(new byte[] { 2, 9, 8 }).ToArray(), deadline.Token, binary: true);
                Assert.Equal(new byte[] { 2, 9, 8 }, await localInput.Task.WaitAsync(deadline.Token));
                Assert.Equal("screen-data", Encoding.ASCII.GetString(await server.ReceiveBytesAsync(deadline.Token)));
                release.TrySetResult();
                await running.WaitAsync(deadline.Token);
                await rfb;
            }
            finally { release.TrySetResult(); listener.Stop(); }
        }

        private static ConnectParams CreateNodeConnectParams() => new()
        {
            MinProtocol = Constants.MinimumNodeProtocolVersion,
            MaxProtocol = Constants.MaximumNodeProtocolVersion,
            Role = "node",
            Client = new System.Collections.Generic.Dictionary<string, object>
            {
                { "id", "node-host" },
                { "platform", "windows" },
                { "deviceFamily", "Windows" },
                { "mode", "node" },
                { "version", "dev" }
            }
        };

        private static async Task CompleteConnectAsync(MockGatewayServer server, CancellationToken cancellationToken)
        {
            using var connect = await server.ReceiveJsonAsync(cancellationToken);
            await server.SendJsonAsync(new
            {
                type = "res",
                id = connect.RootElement.GetProperty("id").GetString(),
                ok = true,
                payload = new
                {
                    type = "hello-ok",
                    protocol = Constants.GatewayProtocolVersion,
                    policy = new { maxPayload = 8 * 1024 * 1024, maxBufferedBytes = 8 * 1024 * 1024, tickIntervalMs = 30000 }
                }
            }, cancellationToken);
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class MockGatewayServer : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly long? _challengeTimestamp;
            private readonly bool _sendChallenge;
            private readonly TaskCompletionSource<NetworkStream> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TcpClient? _client;
            private NetworkStream? _stream;

            public MockGatewayServer(int port, long? challengeTimestamp = null, bool sendChallenge = true)
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _challengeTimestamp = challengeTimestamp;
                _sendChallenge = sendChallenge;
            }

            public Task StartAsync()
            {
                _listener.Start();
                _ = Task.Run(async () =>
                {
                    _client = await _listener.AcceptTcpClientAsync();
                    _stream = _client.GetStream();
                    await CompleteWebSocketHandshakeAsync(_stream);
                    _connected.TrySetResult(_stream);
                    if (_sendChallenge)
                        await SendJsonAsync(new { type = "event", @event = "connect.challenge", payload = new { nonce = "n", ts = _challengeTimestamp } }, CancellationToken.None);
                });
                return Task.CompletedTask;
            }

            public async Task SendJsonAsync(object obj, CancellationToken cancellationToken)
            {
                var json = JsonSerializer.Serialize(obj);
                await SendBytesAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
            }

            public async Task SendBytesAsync(byte[] payload, CancellationToken cancellationToken, bool binary = false)
            {
                var stream = await _connected.Task.WaitAsync(cancellationToken);
                using var frame = new MemoryStream();
                frame.WriteByte(binary ? (byte)0x82 : (byte)0x81);
                if (payload.Length < 126)
                {
                    frame.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)(payload.Length >> 8));
                    frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(127);
                    var length = (ulong)payload.LongLength;
                    for (var shift = 56; shift >= 0; shift -= 8) frame.WriteByte((byte)(length >> shift));
                }
                frame.Write(payload);
                await stream.WriteAsync(frame.GetBuffer().AsMemory(0, checked((int)frame.Length)), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            public async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
                => JsonDocument.Parse(await ReceiveBytesAsync(cancellationToken));

            public async Task<byte[]> ReceiveBytesAsync(CancellationToken cancellationToken)
            {
                var stream = await _connected.Task.WaitAsync(cancellationToken);
                using var message = new MemoryStream();
                while (true)
                {
                    var first = await ReadByteAsync(stream, cancellationToken);
                    var second = await ReadByteAsync(stream, cancellationToken);
                    var opcode = first & 0x0f;
                    if (opcode == 8) throw new Exception("socket closed before expected frame");
                    var masked = (second & 0x80) != 0;
                    ulong length = (uint)(second & 0x7f);
                    if (length == 126) length = (ulong)((await ReadByteAsync(stream, cancellationToken) << 8) | await ReadByteAsync(stream, cancellationToken));
                    else if (length == 127)
                    {
                        length = 0;
                        for (var i = 0; i < 8; i++) length = (length << 8) | (uint)await ReadByteAsync(stream, cancellationToken);
                    }
                    if (length > 4 * 1024 * 1024) throw new Exception("unexpected oversized WebSocket test frame");
                    var mask = masked ? await ReadExactlyAsync(stream, 4, cancellationToken) : null;
                    var payload = await ReadExactlyAsync(stream, checked((int)length), cancellationToken);
                    if (mask != null)
                    {
                        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                    }
                    if (opcode == 9)
                    {
                        continue;
                    }
                    if (opcode is not (0 or 1 or 2)) continue;
                    message.Write(payload);
                    if ((first & 0x80) != 0) return message.ToArray();
                }
            }

            private static async Task CompleteWebSocketHandshakeAsync(NetworkStream stream)
            {
                var requestBytes = new List<byte>();
                while (requestBytes.Count < 64 * 1024)
                {
                    requestBytes.Add((byte)await ReadByteAsync(stream, CancellationToken.None));
                    var count = requestBytes.Count;
                    if (count >= 4 && requestBytes[count - 4] == 13 && requestBytes[count - 3] == 10 && requestBytes[count - 2] == 13 && requestBytes[count - 1] == 10) break;
                }
                var request = Encoding.ASCII.GetString(requestBytes.ToArray());
                var keyLine = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
                var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            }

            private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
            {
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read != 1) throw new EndOfStreamException();
                return buffer[0];
            }

            private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
            {
                var buffer = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                return buffer;
            }

            public ValueTask DisposeAsync()
            {
                _stream?.Dispose();
                _client?.Dispose();
                _listener.Stop();
                return ValueTask.CompletedTask;
            }
        }
    }
}
