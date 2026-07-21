using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenClaw.Node.Services;

namespace OpenClaw.Node.Protocol
{
    public sealed class GatewayConnectionOptions
    {
        public int HandshakeTimeoutMs { get; set; } = Constants.DefaultHandshakeTimeoutMs;
        public int RequestTimeoutMs { get; set; } = Constants.DefaultRequestTimeoutMs;
        public int MaximumFrameBytes { get; set; } = Constants.DefaultMaximumFrameBytes;
        public string? TlsCertificateSha256 { get; set; }
    }

    /// <summary>
    /// Ordered Gateway protocol session with device authentication, correlated
    /// requests, bounded frames, reconnect jitter, and cancellable node invokes.
    /// </summary>
    public sealed class GatewayConnection : IDisposable, IPluginSurfaceClient
    {
        private sealed class ActiveInvoke : IDisposable
        {
            public required BridgeInvokeRequest Request { get; init; }
            public required string NodeId { get; init; }
            public required CancellationTokenSource HandlerCts { get; init; }
            public required CancellationTokenSource TimeoutCts { get; init; }
            public Channel<NodeInvokeInputPayload> Inputs { get; } = Channel.CreateBounded<NodeInvokeInputPayload>(
                new BoundedChannelOptions(256)
                {
                    // TryWrite must report overflow so the invocation can be
                    // cancelled fail-closed instead of silently losing input.
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
            public long NextInputSequence;
            public long ProgressSequence;
            public int Completed;
            public int CancelledByGateway;
            public int SessionEnded;

            public void Dispose()
            {
                Inputs.Writer.TryComplete();
                HandlerCts.Dispose();
                TimeoutCts.Dispose();
            }
        }

        private sealed record IdempotencyReceipt(string Digest, BridgeInvokeResponse Response, DateTimeOffset ExpiresAt);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly Uri _serverUri;
        private readonly string _token;
        private readonly ConnectParams _connectParams;
        private readonly GatewayConnectionOptions _options;
        private readonly DeviceIdentityService _deviceIdentityService;
        private readonly DeviceTokenStore _deviceTokenStore;
        private readonly ConcurrentDictionary<string, Func<RequestFrame, Task<object?>>> _methodHandlers = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseFrame>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, ActiveInvoke> _activeInvokes = new();
        private readonly ConcurrentDictionary<string, IdempotencyReceipt> _idempotencyReceipts = new();
        private readonly ConcurrentDictionary<string, string> _pluginSurfaceUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _socketGate = new();

        private CancellationTokenSource _cts = new();
        private CancellationTokenSource? _activeReceiveCts;
        private ClientWebSocket? _webSocket;
        private DeviceIdentityService.DeviceIdentity? _deviceIdentity;
        private string? _pendingConnectRequestId;
        private DateTimeOffset _handshakeDeadline;
        private DateTimeOffset _lastTickAt;
        private int _tickIntervalMs = 30_000;
        private int _maximumFrameBytes;
        private int _reconnectAttempt;
        private int? _retryAfterMs;
        private int _connected;
        private int _disposed;
        private bool _pendingDeviceTokenRetry;
        private bool _deviceTokenRetryBudgetUsed;
        private bool _attemptedDeviceTokenRetry;
        private bool _usedStoredTokenAsPrimary;
        private bool _storedTokenAvailable;
        private bool _reconnectPausedForAuthFailure;

        public GatewayConnection(
            string serverUrl,
            string token,
            ConnectParams connectParams,
            GatewayConnectionOptions? options = null,
            DeviceIdentityService? deviceIdentityService = null,
            DeviceTokenStore? deviceTokenStore = null)
        {
            _serverUri = new Uri(serverUrl);
            _token = token ?? string.Empty;
            _connectParams = connectParams ?? throw new ArgumentNullException(nameof(connectParams));
            _options = options ?? new GatewayConnectionOptions();
            var tlsFingerprint = NormalizeFingerprint(_options.TlsCertificateSha256);
            _options.TlsCertificateSha256 = tlsFingerprint;
            if (tlsFingerprint.Length > 0 && !string.Equals(_serverUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Gateway TLS certificate pin requires a wss:// URL", nameof(options));
            _deviceIdentityService = deviceIdentityService ?? new DeviceIdentityService();
            _deviceTokenStore = deviceTokenStore ?? new DeviceTokenStore();
            _maximumFrameBytes = Math.Max(64 * 1024, _options.MaximumFrameBytes);
        }

        public event Action<string>? OnLog;
        public event Action<EventFrame>? OnEventReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string>? OnConnectRejected;
        public event Func<BridgeInvokeRequest, Task<BridgeInvokeResponse>>? OnNodeInvoke;
        public event Func<NodeInvokeContext, Task<BridgeInvokeResponse>>? OnNodeInvokeContext;

        public bool IsConnected => Volatile.Read(ref _connected) == 1;

        public string? GetPluginSurfaceUrl(string surface)
        {
            var key = surface?.Trim();
            return !string.IsNullOrWhiteSpace(key) && _pluginSurfaceUrls.TryGetValue(key, out var url) ? url : null;
        }

        public async Task<string?> RefreshPluginSurfaceUrlAsync(string surface, CancellationToken cancellationToken)
        {
            var key = surface?.Trim();
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Plugin surface is required", nameof(surface));
            var result = await RequestAsync<PluginSurfaceRefreshPayload>(
                "node.pluginSurface.refresh",
                new { surface = key },
                cancellationToken,
                timeoutMs: 8_000).ConfigureAwait(false);
            if (result?.PluginSurfaceUrls != null)
            {
                foreach (var item in result.PluginSurfaceUrls)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                        _pluginSurfaceUrls[item.Key] = item.Value.Trim();
                }
            }
            return GetPluginSurfaceUrl(key);
        }

        private sealed class PluginSurfaceRefreshPayload
        {
            [JsonPropertyName("pluginSurfaceUrls")]
            public Dictionary<string, string> PluginSurfaceUrls { get; set; } = new();
        }

        public void RegisterMethodHandler(string method, Func<RequestFrame, Task<object?>> handler)
        {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required", nameof(method));
            _methodHandlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            _cts.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tickTask = TickMonitorLoopAsync(_cts.Token);

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await ConnectAndReceiveLoopAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // The active socket was deliberately interrupted to reconnect.
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[Gateway] Disconnected: {ex.Message}");
                    }

                    if (_cts.IsCancellationRequested) break;
                    if (_reconnectPausedForAuthFailure)
                    {
                        OnLog?.Invoke("[Gateway] Reconnect paused until credentials are changed or the companion is restarted.");
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                        break;
                    }
                    var delayMs = NextReconnectDelayMs();
                    OnLog?.Invoke($"[Gateway] Reconnecting in {delayMs}ms...");
                    try
                    {
                        await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                await DisconnectAsync().ConfigureAwait(false);
                try { await tickTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        public async Task SendEventAsync(string eventName, object? payload, CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new InvalidOperationException("Gateway is not connected");
            var frame = new EventFrame { Event = eventName, Payload = payload };
            await SendRawAsync(JsonSerializer.Serialize(frame, JsonOptions), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Compatibility one-way request. Use RequestAsync when an RPC result is required.
        /// </summary>
        public async Task SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new InvalidOperationException("Gateway is not connected");
            var request = NewRequest(method, @params);
            await SendRawAsync(JsonSerializer.Serialize(request, JsonOptions), cancellationToken).ConfigureAwait(false);
        }

        public async Task<T?> RequestAsync<T>(
            string method,
            object? @params,
            CancellationToken cancellationToken,
            int? timeoutMs = null)
        {
            ThrowIfDisposed();
            if (!IsConnected) throw new InvalidOperationException("Gateway is not connected");
            var request = NewRequest(method, @params);
            var completion = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingRequests.TryAdd(request.Id, completion)) throw new InvalidOperationException("Duplicate request id");

            try
            {
                var sent = await SendRawCoreAsync(JsonSerializer.Serialize(request, JsonOptions), cancellationToken).ConfigureAwait(false);
                if (!sent) throw new InvalidOperationException("Gateway is not connected");
                var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs ?? _options.RequestTimeoutMs, 100, 10 * 60 * 1000));
                var response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!response.Ok) throw new GatewayRpcException(response.Error ?? new GatewayErrorShape());
                if (response.Payload == null) return default;
                if (response.Payload is T already) return already;
                return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response.Payload, JsonOptions), JsonOptions);
            }
            finally
            {
                _pendingRequests.TryRemove(request.Id, out _);
            }
        }

        public Task SendNodeEventAsync(string eventName, object? payload, CancellationToken cancellationToken)
            => SendRequestAsync("node.event", new
            {
                @event = eventName,
                payloadJSON = payload == null ? null : JsonSerializer.Serialize(payload, JsonOptions),
            }, cancellationToken);

        private async Task ConnectAndReceiveLoopAsync(CancellationToken cancellationToken)
        {
            TransitionDisconnected();
            CancelActiveInvokes(sessionEnded: true);
            FailPendingRequests(new OperationCanceledException("Gateway session ended"));

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_socketGate)
            {
                _activeReceiveCts?.Cancel();
                _activeReceiveCts = receiveCts;
            }

            using var socket = CreateSocket();
            lock (_socketGate) _webSocket = socket;
            _maximumFrameBytes = Math.Max(64 * 1024, _options.MaximumFrameBytes);
            _pendingConnectRequestId = null;
            _handshakeDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, _options.HandshakeTimeoutMs));

            OnLog?.Invoke($"[Gateway] Connecting to {_serverUri}...");
            try
            {
                await socket.ConnectAsync(_serverUri, receiveCts.Token).ConfigureAwait(false);
                while (socket.State == WebSocketState.Open && !receiveCts.IsCancellationRequested)
                {
                    using var messageCts = CancellationTokenSource.CreateLinkedTokenSource(receiveCts.Token);
                    if (!IsConnected)
                    {
                        var remaining = _handshakeDeadline - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Gateway handshake timed out");
                        messageCts.CancelAfter(remaining);
                    }

                    string? message;
                    try
                    {
                        message = await ReceiveTextMessageAsync(socket, messageCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!receiveCts.IsCancellationRequested && !IsConnected)
                    {
                        throw new TimeoutException("Gateway handshake timed out");
                    }

                    if (message == null) break;
                    await ProcessMessageAsync(message, receiveCts.Token).ConfigureAwait(false);
                }
            }
            catch (WebSocketException ex) when (!IsConnected && LooksLikeAuthenticationFailure(ex.Message))
            {
                OnConnectRejected?.Invoke($"connect-failed: {ex.Message}");
                throw;
            }
            finally
            {
                lock (_socketGate)
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                    if (ReferenceEquals(_activeReceiveCts, receiveCts)) _activeReceiveCts = null;
                }
                TransitionDisconnected();
                CancelActiveInvokes(sessionEnded: true);
                FailPendingRequests(new OperationCanceledException("Gateway session ended"));
            }
        }

        private ClientWebSocket CreateSocket()
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var expectedFingerprint = NormalizeFingerprint(_options.TlsCertificateSha256);
            if (expectedFingerprint.Length > 0)
            {
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    ValidatePinnedCertificate(certificate, errors, expectedFingerprint);
            }
            return socket;
        }

        private async Task<string?> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnLog?.Invoke($"[Gateway] Socket closed by server. code={result.CloseStatus?.ToString() ?? "n/a"} reason={result.CloseStatusDescription ?? "n/a"}");
                    if (!IsConnected) OnConnectRejected?.Invoke($"pre-connect-close: {result.CloseStatusDescription ?? "no reason"}");
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Gateway sent a non-text frame");
                if (stream.Length + result.Count > Volatile.Read(ref _maximumFrameBytes))
                {
                    throw new InvalidDataException($"Gateway frame exceeded {_maximumFrameBytes} bytes");
                }
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }

        private async Task ProcessMessageAsync(string json, CancellationToken cancellationToken)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
                if (!document.RootElement.TryGetProperty("type", out var typeElement))
                {
                    OnLog?.Invoke("[Gateway] Ignored frame without a type");
                    return;
                }

                switch (typeElement.GetString())
                {
                    case "req":
                        var request = JsonSerializer.Deserialize<RequestFrame>(json, JsonOptions);
                        if (request != null) await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
                        break;
                    case "res":
                        var response = JsonSerializer.Deserialize<ResponseFrame>(json, JsonOptions);
                        if (response != null) HandleResponse(response);
                        break;
                    case "event":
                        var eventFrame = JsonSerializer.Deserialize<EventFrame>(json, JsonOptions);
                        if (eventFrame != null) await HandleEventAsync(eventFrame, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        OnLog?.Invoke($"[Gateway] Ignored unknown frame type '{typeElement.GetString()}'");
                        break;
                }
            }
            catch (JsonException ex)
            {
                OnLog?.Invoke($"[Gateway] Ignored malformed JSON frame: {ex.Message}");
            }
        }

        private async Task HandleEventAsync(EventFrame eventFrame, CancellationToken cancellationToken)
        {
            if (!IsConnected && eventFrame.Event != "connect.challenge")
            {
                OnLog?.Invoke($"[Gateway] Ignored pre-auth event '{eventFrame.Event}'");
                return;
            }
            if (IsConnected && eventFrame.Event == "connect.challenge")
            {
                OnLog?.Invoke("[Gateway] Ignored duplicate connect.challenge after admission");
                return;
            }
            switch (eventFrame.Event)
            {
                case "tick":
                    _lastTickAt = DateTimeOffset.UtcNow;
                    return;
                case "connect.challenge":
                    await HandleConnectChallengeAsync(eventFrame, cancellationToken).ConfigureAwait(false);
                    return;
                case "node.invoke.request":
                    StartNodeInvoke(eventFrame);
                    return;
                case "node.invoke.cancel":
                    HandleNodeInvokeCancel(eventFrame);
                    return;
                case "node.invoke.input":
                    HandleNodeInvokeInput(eventFrame);
                    return;
                case "shutdown":
                    OnLog?.Invoke("[Gateway] Server requested session shutdown");
                    AbortCurrentSocket();
                    return;
                default:
                    OnEventReceived?.Invoke(eventFrame);
                    return;
            }
        }

        private async Task HandleConnectChallengeAsync(EventFrame eventFrame, CancellationToken cancellationToken)
        {
            var nonce = ExtractNonce(eventFrame.Payload);
            if (string.IsNullOrWhiteSpace(nonce))
            {
                OnConnectRejected?.Invoke("connect.challenge missing nonce");
                AbortCurrentSocket();
                return;
            }

            _deviceIdentity ??= _deviceIdentityService.LoadOrCreate();
            var client = _connectParams.GetClientInfo();
            var role = string.IsNullOrWhiteSpace(_connectParams.Role) ? "node" : _connectParams.Role.Trim();
            var cached = _deviceTokenStore.Load(_serverUri, _deviceIdentity.DeviceId, role);
            var explicitToken = string.IsNullOrWhiteSpace(_token) ? null : _token.Trim();
            var scopesExceedStoredToken = RequestedScopesExceedStoredToken(role, _connectParams.Scopes, cached);
            var useDeviceTokenRetry = _pendingDeviceTokenRetry &&
                                      !_deviceTokenRetryBudgetUsed &&
                                      IsTrustedDeviceTokenRetryEndpoint() &&
                                      !scopesExceedStoredToken &&
                                      cached != null &&
                                      explicitToken != null;
            _pendingDeviceTokenRetry = false;
            _attemptedDeviceTokenRetry = useDeviceTokenRetry;
            _usedStoredTokenAsPrimary = explicitToken == null && cached != null;
            _storedTokenAvailable = cached != null;
            if (useDeviceTokenRetry) _deviceTokenRetryBudgetUsed = true;

            var authToken = explicitToken ?? cached?.Token;
            _connectParams.Auth = authToken != null || useDeviceTokenRetry
                ? new GatewayAuthParams
                {
                    Token = authToken,
                    DeviceToken = useDeviceTokenRetry ? cached?.Token : null,
                }
                : null;
            var credential = authToken;

            var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var signaturePayload = _deviceIdentityService.BuildDeviceAuthPayload(
                _deviceIdentity.DeviceId,
                client.Id,
                client.Mode,
                role,
                _connectParams.Scopes.ToArray(),
                signedAt,
                credential,
                nonce,
                client.Platform,
                client.DeviceFamily);
            _connectParams.Device = new GatewayDeviceParams
            {
                Id = _deviceIdentity.DeviceId,
                PublicKey = _deviceIdentity.PublicKeyBase64Url,
                Signature = _deviceIdentityService.SignPayloadBase64Url(_deviceIdentity.PrivateKeyBase64Url, signaturePayload),
                SignedAt = signedAt,
                Nonce = nonce,
            };

            var request = NewRequest("connect", _connectParams);
            _pendingConnectRequestId = request.Id;
            OnLog?.Invoke("[Gateway] Received connect.challenge. Sending signed connect request...");
            await SendRawAsync(JsonSerializer.Serialize(request, JsonOptions), cancellationToken).ConfigureAwait(false);
        }

        private void HandleResponse(ResponseFrame response)
        {
            if (response.Id == _pendingConnectRequestId)
            {
                _pendingConnectRequestId = null;
                if (!response.Ok)
                {
                    _retryAfterMs = response.Error?.RetryAfterMs;
                    var retryWithDeviceToken = !_attemptedDeviceTokenRetry &&
                                               !_usedStoredTokenAsPrimary &&
                                               _storedTokenAvailable &&
                                               !_deviceTokenRetryBudgetUsed &&
                                               IsTrustedDeviceTokenRetryEndpoint() &&
                                               IsDeviceTokenRetryAllowed(response.Error);
                    if (retryWithDeviceToken)
                    {
                        _pendingDeviceTokenRetry = true;
                        _retryAfterMs = 0;
                    }
                    else if ((_attemptedDeviceTokenRetry || _usedStoredTokenAsPrimary) && _deviceIdentity != null)
                    {
                        _deviceTokenStore.Clear(_serverUri, _deviceIdentity.DeviceId, _connectParams.Role);
                    }
                    if (!retryWithDeviceToken && ShouldPauseAfterConnectError(response.Error))
                        _reconnectPausedForAuthFailure = true;
                    var message = response.Error == null
                        ? "Gateway rejected the connection"
                        : $"{response.Error.Code}: {response.Error.Message}";
                    OnLog?.Invoke($"[Gateway] Connect rejected: {message}");
                    OnConnectRejected?.Invoke(message);
                    AbortCurrentSocket();
                    return;
                }

                var hello = DeserializePayload<HelloOkPayload>(response.Payload);
                if (hello == null || !string.Equals(hello.Type, "hello-ok", StringComparison.Ordinal) || hello.Protocol <= 0)
                {
                    _reconnectPausedForAuthFailure = true;
                    OnConnectRejected?.Invoke("Gateway returned an invalid hello payload");
                    AbortCurrentSocket();
                    return;
                }
                if (hello.Protocol < _connectParams.MinProtocol || hello.Protocol > _connectParams.MaxProtocol)
                {
                    _reconnectPausedForAuthFailure = true;
                    OnConnectRejected?.Invoke($"Gateway selected unsupported protocol {hello.Protocol}");
                    AbortCurrentSocket();
                    return;
                }
                ApplyHelloPolicy(hello);
                PersistDeviceTokens(hello);
                _pluginSurfaceUrls.Clear();
                if (hello?.PluginSurfaceUrls != null)
                {
                    foreach (var item in hello.PluginSurfaceUrls)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                            _pluginSurfaceUrls[item.Key] = item.Value.Trim();
                    }
                }
                _reconnectAttempt = 0;
                _retryAfterMs = null;
                _pendingDeviceTokenRetry = false;
                _deviceTokenRetryBudgetUsed = false;
                _reconnectPausedForAuthFailure = false;
                _lastTickAt = DateTimeOffset.UtcNow;
                if (Interlocked.Exchange(ref _connected, 1) == 0)
                {
                    OnLog?.Invoke($"[Gateway] Connect accepted (protocol {(hello?.Protocol > 0 ? hello.Protocol : _connectParams.MaxProtocol)}). Tick interval: {_tickIntervalMs}ms");
                    OnConnected?.Invoke();
                }
                return;
            }

            if (_pendingRequests.TryRemove(response.Id, out var completion)) completion.TrySetResult(response);
        }

        private async Task HandleRequestAsync(RequestFrame request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Id)) return;
            if (!IsConnected)
            {
                OnLog?.Invoke($"[Gateway] Ignored pre-auth request '{request.Method}'");
                return;
            }
            if (!_methodHandlers.TryGetValue(request.Method, out var handler))
            {
                await SendResponseAsync(new ResponseFrame
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new GatewayErrorShape { Code = "INVALID_REQUEST", Message = "Method not found" },
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var result = await handler(request).ConfigureAwait(false);
                await SendResponseAsync(new ResponseFrame { Id = request.Id, Ok = true, Payload = result }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Gateway] Error handling method {request.Method}: {ex.Message}");
                await SendResponseAsync(new ResponseFrame
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new GatewayErrorShape { Code = "UNAVAILABLE", Message = ex.Message },
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private void StartNodeInvoke(EventFrame eventFrame)
        {
            var request = DeserializePayload<BridgeInvokeRequest>(eventFrame.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Command))
            {
                OnLog?.Invoke("[Gateway] Ignored invalid node.invoke.request");
                return;
            }
            request.NodeId = string.IsNullOrWhiteSpace(request.NodeId) ? ResolveNodeId() : request.NodeId.Trim();

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                PruneIdempotencyReceipts();
                var receiptKey = IdempotencyReceiptKey(request.NodeId, request.IdempotencyKey!);
                if (_idempotencyReceipts.TryGetValue(receiptKey, out var receipt))
                {
                    var digest = InvokeDigest(request);
                    var replay = receipt.Digest == digest
                        ? CloneResponse(receipt.Response, request.Id, request.NodeId)
                        : ErrorResponse(request, OpenClawNodeErrorCode.InvalidRequest, "idempotency key reused with different parameters");
                    _ = SendInvokeResultAsync(request, replay, CancellationToken.None);
                    return;
                }
            }

            var timeoutMs = Math.Clamp(request.TimeoutMs ?? Constants.DefaultRequestTimeoutMs, 100, 10 * 60 * 1000);
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            var active = new ActiveInvoke
            {
                Request = request,
                NodeId = request.NodeId,
                HandlerCts = handlerCts,
                TimeoutCts = timeoutCts,
            };
            if (!_activeInvokes.TryAdd(request.Id, active))
            {
                active.Dispose();
                OnLog?.Invoke($"[Gateway] Ignored duplicate active invoke {request.Id}");
                return;
            }

            OnLog?.Invoke($"[Gateway] Executing node.invoke.request id={request.Id} command={request.Command}");
            _ = ExecuteNodeInvokeAsync(active);
        }

        private async Task ExecuteNodeInvokeAsync(ActiveInvoke active)
        {
            BridgeInvokeResponse response;
            try
            {
                if (OnNodeInvokeContext != null)
                {
                    var context = new NodeInvokeContext(
                        active.Request,
                        active.HandlerCts.Token,
                        active.Inputs.Reader,
                        (chunk, cancellationToken) => ReportProgressAsync(active, chunk, cancellationToken));
                    response = await OnNodeInvokeContext(context).WaitAsync(active.HandlerCts.Token).ConfigureAwait(false);
                }
                else if (OnNodeInvoke != null)
                {
                    response = await OnNodeInvoke(active.Request).WaitAsync(active.HandlerCts.Token).ConfigureAwait(false);
                }
                else
                {
                    response = ErrorResponse(active.Request, OpenClawNodeErrorCode.Unavailable, "No node invoke handler is registered");
                }
            }
            catch (OperationCanceledException) when (active.TimeoutCts.IsCancellationRequested)
            {
                response = ErrorResponse(active.Request, OpenClawNodeErrorCode.Timeout, "Node invocation timed out", retryable: true);
            }
            catch (OperationCanceledException)
            {
                response = ErrorResponse(active.Request, OpenClawNodeErrorCode.Cancelled, "Node invocation cancelled", retryable: active.SessionEnded == 1);
            }
            catch (Exception ex)
            {
                response = ErrorResponse(active.Request, OpenClawNodeErrorCode.Unavailable, ex.Message, retryable: true);
            }

            response.Id = active.Request.Id;
            response.NodeId = active.NodeId;
            response = BoundInvokeResultToFrame(active.Request, response);
            if (Interlocked.Exchange(ref active.Completed, 1) != 0) return;
            StoreIdempotencyReceipt(active.Request, response);
            try
            {
                await SendInvokeResultAsync(active.Request, response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The receipt remains available for a Gateway replay after a
                // connection loss; result delivery itself is best-effort.
                OnLog?.Invoke($"[Gateway] Failed to send node invoke result {active.Request.Id}: {ex.Message}");
            }
            finally
            {
                _activeInvokes.TryRemove(active.Request.Id, out _);
                active.Dispose();
            }
        }

        private void HandleNodeInvokeCancel(EventFrame eventFrame)
        {
            var payload = DeserializePayload<NodeInvokeCancelPayload>(eventFrame.Payload);
            if (payload == null || string.IsNullOrWhiteSpace(payload.InvokeId)) return;
            if (_activeInvokes.TryGetValue(payload.InvokeId, out var active) && NodeMatches(payload.NodeId, active.NodeId))
            {
                Interlocked.Exchange(ref active.CancelledByGateway, 1);
                active.HandlerCts.Cancel();
            }
        }

        private void HandleNodeInvokeInput(EventFrame eventFrame)
        {
            var input = DeserializePayload<NodeInvokeInputPayload>(eventFrame.Payload);
            if (input == null || string.IsNullOrWhiteSpace(input.Id)) return;
            if (Encoding.UTF8.GetByteCount(input.PayloadJSON ?? string.Empty) > Constants.MaximumInvokeInputBytes)
            {
                OnLog?.Invoke($"[Gateway] Dropped oversized input for invoke {input.Id}");
                return;
            }
            if (!_activeInvokes.TryGetValue(input.Id, out var active) || !NodeMatches(input.NodeId, active.NodeId)) return;
            var expected = Interlocked.Read(ref active.NextInputSequence);
            if (input.Seq != expected) return;
            if (!active.Inputs.Writer.TryWrite(input))
            {
                OnLog?.Invoke($"[Gateway] Input queue overflow for invoke {input.Id}; cancelling fail-closed");
                active.HandlerCts.Cancel();
                return;
            }
            Interlocked.Increment(ref active.NextInputSequence);
        }

        private async Task ReportProgressAsync(ActiveInvoke active, string text, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref active.Completed) != 0) return;
            if (text.Length == 0)
            {
                await RequestAsync<object>("node.invoke.progress", new NodeInvokeProgressParams
                {
                    InvokeId = active.Request.Id,
                    NodeId = active.NodeId,
                    Seq = Interlocked.Increment(ref active.ProgressSequence) - 1,
                    Chunk = string.Empty,
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var remaining = text;
            while (remaining.Length > 0)
            {
                var length = Utf8PrefixLength(remaining, Constants.MaximumProgressChunkBytes);
                var chunk = remaining[..length];
                remaining = remaining[length..];
                await RequestAsync<object>("node.invoke.progress", new NodeInvokeProgressParams
                {
                    InvokeId = active.Request.Id,
                    NodeId = active.NodeId,
                    Seq = Interlocked.Increment(ref active.ProgressSequence) - 1,
                    Chunk = chunk,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendInvokeResultAsync(BridgeInvokeRequest request, BridgeInvokeResponse response, CancellationToken cancellationToken)
        {
            await RequestAsync<object>(
                "node.invoke.result",
                BuildInvokeResultParams(request, response),
                cancellationToken).ConfigureAwait(false);
        }

        private static object BuildInvokeResultParams(BridgeInvokeRequest request, BridgeInvokeResponse response)
        {
            var error = response.Error == null
                ? null
                : new
                {
                    code = JsonSerializer.SerializeToElement(response.Error.Code, JsonOptions).GetString(),
                    message = response.Error.Message,
                };
            return new
            {
                id = request.Id,
                nodeId = string.IsNullOrWhiteSpace(response.NodeId) ? request.NodeId : response.NodeId,
                ok = response.Ok,
                payload = response.Payload,
                payloadJSON = response.PayloadJSON,
                error,
            };
        }

        private BridgeInvokeResponse BoundInvokeResultToFrame(BridgeInvokeRequest request, BridgeInvokeResponse response)
        {
            var probe = new RequestFrame
            {
                Id = new string('0', 32),
                Method = "node.invoke.result",
                Params = BuildInvokeResultParams(request, response),
            };
            if (JsonSerializer.SerializeToUtf8Bytes(probe, JsonOptions).Length <= Volatile.Read(ref _maximumFrameBytes))
                return response;

            OnLog?.Invoke($"[Gateway] Replaced oversized node invoke result {request.Id} with an error result");
            var fallback = ErrorResponse(
                request,
                OpenClawNodeErrorCode.Unavailable,
                "UNAVAILABLE: node invoke result exceeded the Gateway frame limit");
            fallback.NodeId = response.NodeId;
            return fallback;
        }

        private async Task TickMonitorLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (!IsConnected) continue;
                var toleranceMs = Math.Max(5000, _tickIntervalMs) + 5000;
                var elapsed = (DateTimeOffset.UtcNow - _lastTickAt).TotalMilliseconds;
                if (elapsed <= toleranceMs) continue;
                OnLog?.Invoke($"[Gateway] Tick missed after {(long)elapsed}ms; reconnecting");
                AbortCurrentSocket();
            }
        }

        public async Task SendRawAsync(string message, CancellationToken cancellationToken)
            => _ = await SendRawCoreAsync(message, cancellationToken).ConfigureAwait(false);

        private async Task<bool> SendRawCoreAsync(string message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length > Volatile.Read(ref _maximumFrameBytes)) throw new InvalidDataException("Outbound Gateway frame is too large");
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClientWebSocket? socket;
                lock (_socketGate) socket = _webSocket;
                if (socket?.State != WebSocketState.Open) return false;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task SendResponseAsync(ResponseFrame response, CancellationToken cancellationToken)
            => SendRawAsync(JsonSerializer.Serialize(response, JsonOptions), cancellationToken);

        public Task DisconnectAsync()
        {
            ClientWebSocket? socket;
            CancellationTokenSource? receiveCts;
            lock (_socketGate)
            {
                socket = _webSocket;
                _webSocket = null;
                receiveCts = _activeReceiveCts;
                _activeReceiveCts = null;
            }
            try { receiveCts?.Cancel(); } catch { }
            try { socket?.Abort(); } catch { }
            TransitionDisconnected();
            CancelActiveInvokes(sessionEnded: true);
            FailPendingRequests(new OperationCanceledException("Gateway disconnected"));
            return Task.CompletedTask;
        }

        public void Stop() => _cts.Cancel();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            DisconnectAsync().GetAwaiter().GetResult();
            _cts.Dispose();
            _sendLock.Dispose();
        }

        private void ApplyHelloPolicy(HelloOkPayload? hello)
        {
            if (hello?.Policy?.TickIntervalMs is > 0) _tickIntervalMs = hello.Policy.TickIntervalMs.Value;
            var advertisedMax = hello?.Policy?.MaxPayload ?? hello?.Policy?.MaxBufferedBytes;
            if (advertisedMax is > 0)
            {
                _maximumFrameBytes = Math.Max(64 * 1024, Math.Min(_options.MaximumFrameBytes, advertisedMax.Value));
            }
        }

        private void PersistDeviceTokens(HelloOkPayload? hello)
        {
            if (_deviceIdentity == null || hello?.Auth == null) return;
            if (!string.IsNullOrWhiteSpace(hello.Auth.DeviceToken))
            {
                _deviceTokenStore.Save(
                    _serverUri,
                    _deviceIdentity.DeviceId,
                    hello.Auth.Role ?? _connectParams.Role,
                    hello.Auth.DeviceToken,
                    hello.Auth.Scopes);
            }

            // Additional role tokens are bootstrap handoffs. This companion
            // currently authenticates with shared or stored device tokens, so
            // it must not accept cross-role handoffs from a non-bootstrap hello.
        }

        private int NextReconnectDelayMs()
        {
            var attempt = Math.Min(6, Interlocked.Increment(ref _reconnectAttempt));
            var cap = Math.Min(30_000, 500 * (1 << attempt));
            var jitter = Random.Shared.Next(0, cap + 1);
            return Math.Max(jitter, Math.Clamp(_retryAfterMs ?? 0, 0, 30_000));
        }

        private void AbortCurrentSocket()
        {
            ClientWebSocket? socket;
            lock (_socketGate) socket = _webSocket;
            try { socket?.Abort(); } catch { }
        }

        private void TransitionDisconnected()
        {
            if (Interlocked.Exchange(ref _connected, 0) == 1) OnDisconnected?.Invoke();
        }

        private void CancelActiveInvokes(bool sessionEnded)
        {
            foreach (var active in _activeInvokes.Values)
            {
                if (sessionEnded) Interlocked.Exchange(ref active.SessionEnded, 1);
                try { active.HandlerCts.Cancel(); } catch { }
            }
        }

        private void FailPendingRequests(Exception exception)
        {
            foreach (var pair in _pendingRequests.ToArray())
            {
                if (_pendingRequests.TryRemove(pair.Key, out var completion)) completion.TrySetException(exception);
            }
        }

        private string ResolveNodeId()
            => _connectParams.Device?.Id is { Length: > 0 } id ? id : _deviceIdentity?.DeviceId ?? _connectParams.GetClientInfo().Id;

        private static RequestFrame NewRequest(string method, object? @params) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = method,
            Params = @params,
        };

        private static T? DeserializePayload<T>(object? payload)
        {
            if (payload == null) return default;
            if (payload is T value) return value;
            try { return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload, JsonOptions), JsonOptions); }
            catch (JsonException) { return default; }
        }

        private static string? ExtractNonce(object? payload)
        {
            var element = DeserializePayload<JsonElement>(payload);
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String
                ? nonce.GetString()?.Trim()
                : null;
        }

        private static bool NodeMatches(string? supplied, string expected)
            => string.IsNullOrWhiteSpace(supplied) || string.Equals(supplied, expected, StringComparison.Ordinal);

        private static bool LooksLikeAuthenticationFailure(string message)
        {
            var value = message.ToLowerInvariant();
            return value.Contains("401") || value.Contains("403") || value.Contains("unauthorized") || value.Contains("forbidden") || value.Contains("auth");
        }

        private bool IsTrustedDeviceTokenRetryEndpoint()
            => _serverUri.IsLoopback || string.Equals(_serverUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        internal static bool RequestedScopesExceedStoredToken(
            string role,
            IReadOnlyCollection<string> requestedScopes,
            DeviceTokenStore.Entry? cached)
        {
            if (cached == null || cached.Scopes.Count == 0 || requestedScopes.Count == 0) return false;
            var allowed = cached.Scopes.Select(value => value.Trim()).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            var normalizedRole = role.Trim();
            return requestedScopes
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Any(scope => !ScopeIsSatisfied(normalizedRole, scope, allowed));
        }

        private static bool ScopeIsSatisfied(string role, string scope, IReadOnlySet<string> granted)
        {
            if (!string.Equals(role, "operator", StringComparison.Ordinal))
                return scope.StartsWith(role + ".", StringComparison.Ordinal) && granted.Contains(scope);
            if (!scope.StartsWith("operator.", StringComparison.Ordinal)) return false;
            if (granted.Contains("operator.admin")) return true;
            return scope switch
            {
                "operator.read" => granted.Contains("operator.read") || granted.Contains("operator.write"),
                "operator.write" => granted.Contains("operator.write"),
                _ => granted.Contains(scope),
            };
        }

        private static bool IsDeviceTokenRetryAllowed(GatewayErrorShape? error)
        {
            var detailCode = ReadConnectErrorDetailCode(error);
            if (string.Equals(detailCode, "AUTH_TOKEN_MISMATCH", StringComparison.Ordinal)) return true;
            return error?.Details is JsonElement details &&
                   details.ValueKind == JsonValueKind.Object &&
                   details.TryGetProperty("canRetryWithDeviceToken", out var retry) &&
                   retry.ValueKind == JsonValueKind.True;
        }

        internal static bool ShouldPauseAfterConnectError(GatewayErrorShape? error)
        {
            var detailCode = ReadConnectErrorDetailCode(error);
            if (string.IsNullOrWhiteSpace(detailCode))
                return string.Equals(error?.Code, "UNAUTHORIZED", StringComparison.OrdinalIgnoreCase);

            if (detailCode == "PAIRING_REQUIRED")
            {
                if (error?.Details is JsonElement details && details.ValueKind == JsonValueKind.Object)
                {
                    if (details.TryGetProperty("pauseReconnect", out var pause) && pause.ValueKind == JsonValueKind.False)
                        return false;
                    if (details.TryGetProperty("recommendedNextStep", out var next) &&
                        next.ValueKind == JsonValueKind.String &&
                        string.Equals(next.GetString(), "wait_then_retry", StringComparison.Ordinal))
                        return false;
                }
                return true;
            }

            // AUTH_TOKEN_MISMATCH reaches this point only after the bounded
            // stored-device-token retry policy has declined or exhausted retry.
            return detailCode is
                "AUTH_REQUIRED" or
                "AUTH_UNAUTHORIZED" or
                "AUTH_TOKEN_MISSING" or
                "AUTH_TOKEN_MISMATCH" or
                "AUTH_TOKEN_NOT_CONFIGURED" or
                "AUTH_BOOTSTRAP_TOKEN_INVALID" or
                "AUTH_PASSWORD_MISSING" or
                "AUTH_PASSWORD_MISMATCH" or
                "AUTH_PASSWORD_NOT_CONFIGURED" or
                "AUTH_RATE_LIMITED" or
                "AUTH_DEVICE_TOKEN_MISMATCH" or
                "AUTH_SCOPE_MISMATCH" or
                "CONTROL_UI_DEVICE_IDENTITY_REQUIRED" or
                "DEVICE_IDENTITY_REQUIRED" or
                "PROTOCOL_MISMATCH" or
                "CLIENT_VERSION_MISMATCH";
        }

        private static string? ReadConnectErrorDetailCode(GatewayErrorShape? error)
        {
            if (error?.Details is not JsonElement details || details.ValueKind != JsonValueKind.Object) return null;
            return details.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()?.Trim()
                : null;
        }

        private static string NormalizeFingerprint(string? value)
        {
            var builder = new StringBuilder(64);
            foreach (var character in value?.Trim() ?? string.Empty)
            {
                if (Uri.IsHexDigit(character)) builder.Append(char.ToUpperInvariant(character));
                else if (character is ':' or '-' || char.IsWhiteSpace(character)) continue;
                else throw new ArgumentException("Gateway TLS certificate pin must contain only SHA-256 hex digits");
            }
            if (builder.Length is not (0 or 64)) throw new ArgumentException("Gateway TLS certificate pin must contain exactly 64 hex digits");
            return builder.ToString();
        }

        private static bool ValidatePinnedCertificate(X509Certificate? certificate, SslPolicyErrors errors, string expectedFingerprint)
        {
            if (certificate == null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
            using var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
            var actual = Convert.ToHexString(certificate2.GetCertHash(HashAlgorithmName.SHA256));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedFingerprint));
        }

        private static int Utf8PrefixLength(string value, int maximumBytes)
        {
            var chars = 0;
            var bytes = 0;
            while (chars < value.Length)
            {
                var width = char.IsHighSurrogate(value[chars]) && chars + 1 < value.Length && char.IsLowSurrogate(value[chars + 1]) ? 2 : 1;
                var next = Encoding.UTF8.GetByteCount(value.AsSpan(chars, width));
                if (chars > 0 && bytes + next > maximumBytes) break;
                bytes += next;
                chars += width;
            }
            return Math.Max(1, chars);
        }

        private static BridgeInvokeResponse ErrorResponse(
            BridgeInvokeRequest request,
            OpenClawNodeErrorCode code,
            string message,
            bool? retryable = null) => new()
        {
            Id = request.Id,
            NodeId = request.NodeId,
            Ok = false,
            Error = new OpenClawNodeError { Code = code, Message = message, Retryable = retryable },
        };

        private static BridgeInvokeResponse CloneResponse(BridgeInvokeResponse response, string id, string nodeId) => new()
        {
            Id = id,
            NodeId = nodeId,
            Ok = response.Ok,
            Payload = response.Payload,
            PayloadJSON = response.PayloadJSON,
            Error = response.Error,
        };

        private static string IdempotencyReceiptKey(string nodeId, string key) => nodeId + "\0" + key;

        private static string InvokeDigest(BridgeInvokeRequest request)
        {
            var text = request.Command + "\0" + (request.ParamsJSON ?? string.Empty);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private void StoreIdempotencyReceipt(BridgeInvokeRequest request, BridgeInvokeResponse response)
        {
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return;
            _idempotencyReceipts[IdempotencyReceiptKey(request.NodeId, request.IdempotencyKey!)] =
                new IdempotencyReceipt(InvokeDigest(request), CloneResponse(response, response.Id, response.NodeId ?? request.NodeId), DateTimeOffset.UtcNow.AddMinutes(10));
            PruneIdempotencyReceipts();
        }

        private void PruneIdempotencyReceipts()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _idempotencyReceipts.Where(pair => pair.Value.ExpiresAt <= now).ToArray()) _idempotencyReceipts.TryRemove(pair.Key, out _);
            if (_idempotencyReceipts.Count <= 256) return;
            foreach (var pair in _idempotencyReceipts.OrderBy(pair => pair.Value.ExpiresAt).Take(_idempotencyReceipts.Count - 256).ToArray())
                _idempotencyReceipts.TryRemove(pair.Key, out _);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(GatewayConnection));
        }
    }
}
