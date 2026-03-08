using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Services
{
    public sealed class IpcPipeServerService : IDisposable
    {
        private readonly string _pipeName;
        private readonly string _version;
        private readonly string? _authToken;
        private readonly ConcurrentDictionary<int, Task> _clients = new();
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private int _clientSeq;
        private const int DefaultRequestTimeoutMs = 15000;
        private const int MinRequestTimeoutMs = 100;
        private const int MaxRequestTimeoutMs = 120000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        public Action<string>? OnLog { get; set; }

        public IpcPipeServerService(string? pipeName = null, string version = "dev", string? authToken = null)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "openclaw.node.ipc" : pipeName;
            _version = version;
            _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
        }

        public string PipeName => _pipeName;

        public void Start(CancellationToken parentToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                OnLog?.Invoke("[IPC] Named pipe server disabled on non-Windows host.");
                return;
            }

            if (_acceptLoop != null) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
            OnLog?.Invoke($"[IPC] Named pipe server started (\\\\.\\pipe\\{_pipeName}).");
        }

        public async Task StopAsync()
        {
            var cts = _cts;
            if (cts == null) return;

            try { cts.Cancel(); } catch { }

            if (_acceptLoop != null)
            {
                try { await _acceptLoop; } catch { }
            }

            await Task.WhenAll(_clients.Values);

            _acceptLoop = null;
            _cts = null;
            cts.Dispose();
            OnLog?.Invoke("[IPC] Named pipe server stopped.");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);

                    var id = Interlocked.Increment(ref _clientSeq);
                    var task = HandleClientAsync(server, id, ct);
                    _clients[id] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        _clients.TryRemove(id, out var _removed);
                    }, TaskScheduler.Default);

                    server = null; // ownership transferred
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    OnLog?.Invoke($"[IPC] Accept error: {ex.Message}");
                    await Task.Delay(150, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, int clientId, CancellationToken ct)
        {
            await using var _ = stream;
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            OnLog?.Invoke($"[IPC] Client {clientId} connected.");

            try
            {
                while (!ct.IsCancellationRequested && stream.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IpcRequest? req = null;
                    try
                    {
                        req = JsonSerializer.Deserialize<IpcRequest>(line, JsonOptions);
                    }
                    catch
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                        {
                            Id = "",
                            Ok = false,
                            Error = new IpcError { Code = "BAD_JSON", Message = "Invalid JSON request" }
                        }, JsonOptions));
                        continue;
                    }

                    if (req == null || string.IsNullOrWhiteSpace(req.Method))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                        {
                            Id = req?.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "Missing method" }
                        }, JsonOptions));
                        continue;
                    }

                    if (!IsAuthorized(req))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "UNAUTHORIZED", Message = "Missing or invalid IPC auth token" }
                        }, JsonOptions));
                        continue;
                    }

                    if (!TryResolveRequestTimeoutMs(req, out var timeoutMs, out var timeoutError))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = timeoutError ?? "Invalid timeoutMs" }
                        }, JsonOptions));
                        continue;
                    }

                    var res = await DispatchWithTimeoutAsync(req, timeoutMs, ct);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(res, JsonOptions));
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[IPC] Client {clientId} error: {ex.Message}");
            }
            finally
            {
                OnLog?.Invoke($"[IPC] Client {clientId} disconnected.");
            }
        }

        private bool IsAuthorized(IpcRequest req)
        {
            if (string.IsNullOrWhiteSpace(_authToken)) return true;
            return string.Equals(req.AuthToken, _authToken, StringComparison.Ordinal);
        }

        private static bool TryResolveRequestTimeoutMs(IpcRequest req, out int timeoutMs, out string? error)
        {
            timeoutMs = DefaultRequestTimeoutMs;
            error = null;

            var p = req.Params ?? default;
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("timeoutMs", out var t))
            {
                return true;
            }

            if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var parsed))
            {
                error = "params.timeoutMs must be an integer";
                return false;
            }

            timeoutMs = Math.Clamp(parsed, MinRequestTimeoutMs, MaxRequestTimeoutMs);
            return true;
        }

        private async Task<IpcResponse> DispatchWithTimeoutAsync(IpcRequest req, int timeoutMs, CancellationToken serverToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            linked.CancelAfter(timeoutMs);

            try
            {
                return await DispatchAsync(req, linked.Token);
            }
            catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
            {
                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = false,
                    Error = new IpcError { Code = "TIMEOUT", Message = $"IPC method timed out after {timeoutMs}ms" }
                };
            }
        }

        private async Task<IpcResponse> DispatchAsync(IpcRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var method = req.Method?.Trim() ?? string.Empty;
            if (string.Equals(method, "ipc.test.sleep", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                var sleepMs = 250;
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("sleepMs", out var s) && s.ValueKind == JsonValueKind.Number)
                {
                    sleepMs = Math.Clamp(s.GetInt32(), 1, 60000);
                }

                await Task.Delay(sleepMs, ct);
                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true, sleptMs = sleepMs }
                };
            }

            if (string.Equals(method, "ipc.ping", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    ok = true,
                    version = _version,
                    nowUtc = DateTimeOffset.UtcNow.ToString("O")
                };
                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = payload,
                };
            }

            if (string.Equals(method, "ipc.window.list", StringComparison.OrdinalIgnoreCase))
            {
                var svc = new AutomationService();
                var windows = await svc.ListWindowsAsync();
                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { windows }
                };
            }

            if (string.Equals(method, "ipc.window.focus", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                long? handle = null;
                string? titleContains = null;

                if (p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number)
                    {
                        if (!h.TryGetInt64(out var parsedHandle))
                        {
                            return new IpcResponse
                            {
                                Id = req.Id ?? string.Empty,
                                Ok = false,
                                Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.window.focus params.handle must be an integer" }
                            };
                        }
                        handle = parsedHandle;
                    }
                    if (p.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        titleContains = t.GetString();
                    }
                }

                if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.window.focus requires handle or titleContains" }
                    };
                }

                var svc = new AutomationService();
                var focused = await svc.FocusWindowAsync(handle, titleContains);
                if (!focused)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Unable to focus requested window" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true }
                };
            }

            if (string.Equals(method, "ipc.window.rect", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                long? handle = null;
                string? titleContains = null;

                if (p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number)
                    {
                        if (!h.TryGetInt64(out var parsedHandle))
                        {
                            return new IpcResponse
                            {
                                Id = req.Id ?? string.Empty,
                                Ok = false,
                                Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.window.rect params.handle must be an integer" }
                            };
                        }
                        handle = parsedHandle;
                    }
                    if (p.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        titleContains = t.GetString();
                    }
                }

                if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.window.rect requires handle or titleContains" }
                    };
                }

                var svc = new AutomationService();
                var rect = await svc.GetWindowRectAsync(handle, titleContains);
                if (rect == null)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Unable to resolve requested window rect" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { rect }
                };
            }

            if (string.Equals(method, "ipc.input.type", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                string? text = null;
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                }

                if (string.IsNullOrEmpty(text))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.type requires text" }
                    };
                }

                var svc = new AutomationService();
                var ok = await svc.TypeTextAsync(text);
                if (!ok)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Typing input failed" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true }
                };
            }

            if (string.Equals(method, "ipc.input.key", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                string? key = null;
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                {
                    key = k.GetString();
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.key requires key" }
                    };
                }

                var svc = new AutomationService();
                var ok = await svc.SendKeyAsync(key);
                if (!ok)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Sending key input failed" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true }
                };
            }

            if (string.Equals(method, "ipc.input.click", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                if (p.ValueKind != JsonValueKind.Object)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click requires params object" }
                    };
                }

                if (!p.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number ||
                    !p.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click requires numeric x and y" }
                    };
                }

                if (!xEl.TryGetInt32(out var x) || !yEl.TryGetInt32(out var y))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click params.x and params.y must be integers" }
                    };
                }
                var button = p.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.String
                    ? (bEl.GetString() ?? "primary")
                    : "primary";
                var doubleClick = p.TryGetProperty("doubleClick", out var dEl) &&
                                  (dEl.ValueKind == JsonValueKind.True || dEl.ValueKind == JsonValueKind.False)
                    ? dEl.GetBoolean()
                    : false;

                var svc = new AutomationService();
                var ok = await svc.ClickAsync(x, y, button, doubleClick);
                if (!ok)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Mouse click failed" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true, x, y, button, doubleClick }
                };
            }

            if (string.Equals(method, "ipc.input.scroll", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                if (p.ValueKind != JsonValueKind.Object ||
                    !p.TryGetProperty("deltaY", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Number)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll requires numeric deltaY" }
                    };
                }

                if (!deltaEl.TryGetInt32(out var deltaY))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll params.deltaY must be an integer" }
                    };
                }

                int? x = null;
                int? y = null;

                if (p.TryGetProperty("x", out var xEl))
                {
                    if (xEl.ValueKind != JsonValueKind.Number)
                    {
                        return new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll x must be numeric" }
                        };
                    }
                    if (!xEl.TryGetInt32(out var parsedX))
                    {
                        return new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll params.x must be an integer" }
                        };
                    }
                    x = parsedX;
                }

                if (p.TryGetProperty("y", out var yEl))
                {
                    if (yEl.ValueKind != JsonValueKind.Number)
                    {
                        return new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll y must be numeric" }
                        };
                    }
                    if (!yEl.TryGetInt32(out var parsedY))
                    {
                        return new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.scroll params.y must be an integer" }
                        };
                    }
                    y = parsedY;
                }

                var svc = new AutomationService();
                var ok = await svc.ScrollAsync(deltaY, x, y);
                if (!ok)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Mouse scroll failed" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true, deltaY, x, y }
                };
            }

            if (string.Equals(method, "ipc.input.click.relative", StringComparison.OrdinalIgnoreCase))
            {
                var p = req.Params ?? default;
                if (p.ValueKind != JsonValueKind.Object)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click.relative requires params object" }
                    };
                }

                long? handle = null;
                string? titleContains = null;
                if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number)
                {
                    if (!h.TryGetInt64(out var parsedHandle))
                    {
                        return new IpcResponse
                        {
                            Id = req.Id ?? string.Empty,
                            Ok = false,
                            Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click.relative params.handle must be an integer" }
                        };
                    }
                    handle = parsedHandle;
                }
                if (p.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    titleContains = t.GetString();
                }

                if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click.relative requires handle or titleContains" }
                    };
                }

                if (!p.TryGetProperty("offsetX", out var oxEl) || oxEl.ValueKind != JsonValueKind.Number ||
                    !p.TryGetProperty("offsetY", out var oyEl) || oyEl.ValueKind != JsonValueKind.Number)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click.relative requires numeric offsetX and offsetY" }
                    };
                }

                if (!oxEl.TryGetInt32(out var offsetX) || !oyEl.TryGetInt32(out var offsetY))
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "BAD_REQUEST", Message = "ipc.input.click.relative params.offsetX and params.offsetY must be integers" }
                    };
                }
                var button = p.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.String
                    ? (bEl.GetString() ?? "primary")
                    : "primary";
                var doubleClick = p.TryGetProperty("doubleClick", out var dEl) &&
                                  (dEl.ValueKind == JsonValueKind.True || dEl.ValueKind == JsonValueKind.False)
                    ? dEl.GetBoolean()
                    : false;

                var svc = new AutomationService();
                var ok = await svc.ClickRelativeToWindowAsync(handle, titleContains, offsetX, offsetY, button, doubleClick);
                if (!ok)
                {
                    return new IpcResponse
                    {
                        Id = req.Id ?? string.Empty,
                        Ok = false,
                        Error = new IpcError { Code = "UNAVAILABLE", Message = "Relative click failed" }
                    };
                }

                return new IpcResponse
                {
                    Id = req.Id ?? string.Empty,
                    Ok = true,
                    Payload = new { ok = true, offsetX, offsetY, button, doubleClick }
                };
            }

            return new IpcResponse
            {
                Id = req.Id ?? string.Empty,
                Ok = false,
                Error = new IpcError
                {
                    Code = "METHOD_NOT_FOUND",
                    Message = $"Unknown IPC method: {method}"
                }
            };
        }

        public void Dispose()
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore dispose path errors
            }
        }

        private sealed class IpcRequest
        {
            public string? Id { get; set; }
            public string? Method { get; set; }
            public string? AuthToken { get; set; }
            public JsonElement? Params { get; set; }
        }

        private sealed class IpcResponse
        {
            public string Id { get; set; } = string.Empty;
            public bool Ok { get; set; }
            public object? Payload { get; set; }
            public IpcError? Error { get; set; }
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var soTask = proc.StandardOutput.ReadToEndAsync();
            var seTask = proc.StandardError.ReadToEndAsync();

            try
            {
                await proc.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // best effort kill on timeout/cancel
                }

                throw;
            }

            var so = await soTask;
            var se = await seTask;

            return (proc.ExitCode, so ?? string.Empty, se ?? string.Empty);
        }

        private static string QuoteForCmd(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private sealed class IpcError
        {
            public string Code { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }
    }
}
