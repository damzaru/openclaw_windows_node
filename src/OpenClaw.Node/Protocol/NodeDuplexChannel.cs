using System.Text;
using System.IO;
using System.Text.Json;

namespace OpenClaw.Node.Protocol;

/// <summary>Gateway v1 binary framing carried over existing ordered input/progress JSON.</summary>
internal sealed class NodeDuplexChannel(NodeInvokeContext context, CancellationToken cancellationToken = default) : IAsyncDisposable
{
    internal const int FragmentBytes = 8 * 1024;
    internal const int MaximumMessageBytes = 1024 * 1024;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
    private long _outgoing, _incoming;
    private bool _sentReady, _receivedReady;
    private Task? _heartbeat;

    public async Task StartAsync()
    {
        await _sendGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_sentReady) throw new InvalidDataException("Duplicate duplex readiness.");
            _sentReady = true;
            await context.ReportProgressAsync("{\"v\":1,\"kind\":\"ready\"}", _lifetime.Token);
            _heartbeat = HeartbeatAsync();
        }
        finally { _sendGate.Release(); }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message)
    {
        if (message.Length > MaximumMessageBytes) throw new InvalidDataException("Duplex message exceeds 1 MiB.");
        await _sendGate.WaitAsync(_lifetime.Token);
        try
        {
            var id = _outgoing++;
            var count = Math.Max(1, (message.Length + FragmentBytes - 1) / FragmentBytes);
            for (var index = 0; index < count; index++)
            {
                var offset = index * FragmentBytes;
                var data = Convert.ToBase64String(message.Slice(offset, Math.Min(FragmentBytes, message.Length - offset)).Span);
                var frame = JsonSerializer.Serialize(new { v = 1, kind = "data", message = id, index, last = index == count - 1, data });
                await context.ReportProgressAsync(frame, _lifetime.Token);
            }
        }
        finally { _sendGate.Release(); }
    }

    public async Task<byte[]> ReceiveAsync()
    {
        using var message = new MemoryStream();
        var index = 0;
        for (;;)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var input = await context.Inputs.ReadAsync(idle.Token);
            if (string.IsNullOrEmpty(input.PayloadJSON)) continue;
            if (Encoding.UTF8.GetByteCount(input.PayloadJSON) > 16 * 1024) throw new InvalidDataException("Duplex frame exceeds 16 KiB.");
            using var json = JsonDocument.Parse(input.PayloadJSON);
            var frame = json.RootElement;
            if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
                !frame.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid duplex frame version or shape.");
            var names = frame.EnumerateObject().Select(p => p.Name).ToArray();
            if (kind.GetString() == "ready")
            {
                if (names.Length != 2 || names.Distinct().Count() != 2 || _receivedReady || index != 0 || _incoming != 0)
                    throw new InvalidDataException("Duplicate or late duplex readiness.");
                _receivedReady = true; continue;
            }
            if (kind.GetString() != "data" || names.Length != 6 || names.Distinct().Count() != 6 ||
                !frame.TryGetProperty("message", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var sequence) || sequence != _incoming ||
                !frame.TryGetProperty("index", out var fragment) || fragment.ValueKind != JsonValueKind.Number || !fragment.TryGetInt32(out var i) || i != index ||
                !frame.TryGetProperty("last", out var last) || last.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !frame.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid or out-of-order duplex data frame.");
            var encoded = data.GetString()!;
            if (encoded.Length > ((FragmentBytes + 2) / 3) * 4) throw new InvalidDataException("Oversized duplex fragment.");
            var bytes = Convert.FromBase64String(encoded);
            var final = last.GetBoolean();
            if (Convert.ToBase64String(bytes) != encoded || bytes.Length > FragmentBytes || (!final && bytes.Length != FragmentBytes) ||
                (final && bytes.Length == 0 && index > 0) || message.Length + bytes.Length > MaximumMessageBytes)
                throw new InvalidDataException("Invalid canonical base64 or duplex fragment size.");
            message.Write(bytes); index++;
            if (!final) continue;
            _incoming++; return message.ToArray();
        }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await _sendGate.WaitAsync(_lifetime.Token);
                try { await context.ReportProgressAsync("", _lifetime.Token); }
                finally { _sendGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { _lifetime.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_heartbeat != null) await _heartbeat;
        _lifetime.Dispose(); _sendGate.Dispose();
    }
}
