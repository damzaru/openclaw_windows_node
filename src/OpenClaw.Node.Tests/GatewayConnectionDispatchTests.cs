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
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class GatewayConnectionDispatchTests
    {
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
            private readonly TaskCompletionSource<NetworkStream> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TcpClient? _client;
            private NetworkStream? _stream;

            public MockGatewayServer(int port)
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
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
                    await SendJsonAsync(new { type = "event", @event = "connect.challenge", payload = new { nonce = "n" } }, CancellationToken.None);
                });
                return Task.CompletedTask;
            }

            public async Task SendJsonAsync(object obj, CancellationToken cancellationToken)
            {
                var stream = await _connected.Task.WaitAsync(cancellationToken);
                var json = JsonSerializer.Serialize(obj);
                var payload = Encoding.UTF8.GetBytes(json);
                using var frame = new MemoryStream();
                frame.WriteByte(0x81);
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
            {
                var stream = await _connected.Task.WaitAsync(cancellationToken);
                var sb = new StringBuilder();
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
                    if (opcode is not (0 or 1)) continue;
                    sb.Append(Encoding.UTF8.GetString(payload));
                    if ((first & 0x80) != 0) return JsonDocument.Parse(sb.ToString());
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
