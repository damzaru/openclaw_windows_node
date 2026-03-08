using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
                    policy = new { tickIntervalMs = 30000 }
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
                payload = new { policy = new { tickIntervalMs = 30000 } }
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
                payload = new { policy = new { tickIntervalMs = 30000 } }
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
                payload = new { policy = new { tickIntervalMs = 30000 } }
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
            private readonly HttpListener _listener = new();
            private readonly string _prefix;
            private WebSocket? _socket;

            public MockGatewayServer(int port)
            {
                _prefix = $"http://127.0.0.1:{port}/";
                _listener.Prefixes.Add(_prefix);
            }

            public Task StartAsync()
            {
                _listener.Start();
                _ = Task.Run(async () =>
                {
                    var ctx = await _listener.GetContextAsync();
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                        return;
                    }

                    var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                    _socket = wsCtx.WebSocket;

                    // Immediately send connect.challenge like gateway does
                    await SendJsonAsync(new { type = "event", @event = "connect.challenge", payload = new { nonce = "n" } }, CancellationToken.None);
                });
                return Task.CompletedTask;
            }

            public async Task SendJsonAsync(object obj, CancellationToken cancellationToken)
            {
                while (_socket == null)
                {
                    await Task.Delay(10, cancellationToken);
                }

                var json = JsonSerializer.Serialize(obj);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }

            public async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
            {
                while (_socket == null)
                {
                    await Task.Delay(10, cancellationToken);
                }

                var buffer = new byte[32768];
                var sb = new StringBuilder();
                while (true)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new Exception("socket closed before expected frame");
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        return JsonDocument.Parse(sb.ToString());
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (_socket != null && _socket.State == WebSocketState.Open)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    }
                }
                catch { }
                _socket?.Dispose();
                if (_listener.IsListening) _listener.Stop();
                _listener.Close();
            }
        }
    }
}
