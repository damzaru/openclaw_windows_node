using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Node.Protocol;

namespace OpenClaw.Node.Services;

/// <summary>Same-connection RFB inspection and ticket-only, bounded Gateway relay.</summary>
internal static class DesktopStreamService
{
    internal static Uri ResolveAttachUri(string? json, Uri gateway)
    {
        using var document = JsonDocument.Parse(json ?? "{}");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("ticket", out var ticket) || ticket.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(ticket.GetString()!, "^[a-f0-9]{48}$") ||
            !root.TryGetProperty("attachPath", out var path) || path.ValueKind != JsonValueKind.String ||
            path.GetString() != "/node-desktop/attach?ticket=" + ticket.GetString())
            throw new InvalidOperationException("INVALID_REQUEST: desktop stream requires a canonical ticket and attachPath");
        return new UriBuilder(gateway) { Path = gateway.AbsolutePath.TrimEnd('/') + "/node-desktop/attach", Query = "ticket=" + ticket.GetString(), Fragment = "" }.Uri;
    }

    internal sealed record RfbHandshake(byte[] Prefix, byte[] Reply);

    internal static async Task<RfbHandshake> InspectRfbAsync(Stream stream, CancellationToken cancellationToken)
    {
        var banner = new byte[12];
        await stream.ReadExactlyAsync(banner, cancellationToken);
        var match = Regex.Match(Encoding.ASCII.GetString(banner), "^RFB 003\\.(\\d{3})\\n$");
        if (!match.Success) throw new InvalidOperationException("UNAVAILABLE: loopback desktop server is not an RFB server");
        var minor = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var reply = Encoding.ASCII.GetBytes(minor > 7 ? "RFB 003.008\n" : minor == 7 ? "RFB 003.007\n" : "RFB 003.003\n");
        await stream.WriteAsync(reply, cancellationToken);
        byte[] security;
        uint[] offered;
        if (minor < 7)
        {
            security = new byte[4];
            await stream.ReadExactlyAsync(security, cancellationToken);
            offered = new[] { BinaryPrimitives.ReadUInt32BigEndian(security) };
        }
        else
        {
            var count = new byte[1];
            await stream.ReadExactlyAsync(count, cancellationToken);
            security = new byte[count[0] + 1];
            security[0] = count[0];
            await stream.ReadExactlyAsync(security.AsMemory(1), cancellationToken);
            offered = security.Skip(1).Select(v => (uint)v).ToArray();
        }
        // Match the first standard security choice noVNC will select. An earlier
        // None offer must not be hidden by a later VncAuth option.
        var selected = offered.FirstOrDefault(v => v is 1 or 2 or 30);
        if (selected == 1) throw new InvalidOperationException("UNAVAILABLE: refusing an unauthenticated loopback VNC server");
        if (selected != 2) throw new InvalidOperationException("UNAVAILABLE: Windows desktop sharing requires VNC password authentication");
        return new RfbHandshake(banner.Concat(security).ToArray(), reply);
    }

    internal static async Task RunAsync(string? json, IGatewayStreamClient gateway, int port, string? password, CancellationToken cancellationToken)
    {
        var attach = ResolveAttachUri(json, gateway.GatewayUri);
        if (port is < 1 or > 65535) throw new InvalidOperationException("INVALID_REQUEST: invalid loopback VNC port");
        if (password != null && (Encoding.UTF8.GetByteCount(password) > 4096 || password.Contains('\0')))
            throw new InvalidOperationException("INVALID_REQUEST: invalid VNC password");
        using var tcp = new TcpClient();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        try { await tcp.ConnectAsync(IPAddress.Loopback, port, startup.Token); }
        catch (SocketException ex) { throw new InvalidOperationException("UNAVAILABLE: start an authenticated loopback VNC server on the configured desktop-sharing port", ex); }
        using var local = tcp.GetStream();
        var inspected = await InspectRfbAsync(local, startup.Token);
        using var socket = gateway.CreateStreamSocket();
        await socket.ConnectAsync(attach, startup.Token);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(password == null
            ? new Dictionary<string, string> { ["auth"] = "vnc-password" }
            : new Dictionary<string, string> { ["auth"] = "vnc-password", ["vncPassword"] = password });
        try { await socket.SendAsync(metadata, WebSocketMessageType.Binary, true, startup.Token); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadata); }
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outbound = LocalToGatewayAsync(local, socket, inspected.Prefix, relay.Token);
        var inbound = GatewayToLocalAsync(socket, local, inspected.Reply, relay.Token);
        try
        {
            await await Task.WhenAny(outbound, inbound);
        }
        finally
        {
            relay.Cancel();
            tcp.Close();
            socket.Abort();
            try { await Task.WhenAll(outbound, inbound); } catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (relay.IsCancellationRequested) { }
            catch (IOException) when (relay.IsCancellationRequested) { }
            catch (WebSocketException) when (relay.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task LocalToGatewayAsync(Stream local, ClientWebSocket socket, byte[] prefix, CancellationToken cancellationToken)
    {
        await SendBinaryAsync(socket, prefix, cancellationToken);
        var bytes = new byte[64 * 1024];
        int count;
        while ((count = await local.ReadAsync(bytes, cancellationToken)) > 0)
            await SendBinaryAsync(socket, bytes.AsMemory(0, count), cancellationToken);
    }

    private static async Task SendBinaryAsync(ClientWebSocket socket, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, deadline.Token);
    }

    private static async Task GatewayToLocalAsync(ClientWebSocket socket, Stream local, byte[] expectedReply, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var version = new byte[12];
        var versionBytes = 0;
        var messageBytes = 0;
        while (socket.State == WebSocketState.Open)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (frame.MessageType == WebSocketMessageType.Close) return;
            messageBytes += frame.Count;
            if (frame.MessageType != WebSocketMessageType.Binary || messageBytes > 1024 * 1024)
                throw new InvalidOperationException("UNAVAILABLE: invalid desktop relay frame");
            if (frame.EndOfMessage) messageBytes = 0;
            var skip = Math.Min(12 - versionBytes, frame.Count);
            if (skip > 0)
            {
                buffer.AsSpan(0, skip).CopyTo(version.AsSpan(versionBytes));
                versionBytes += skip;
                if (versionBytes == 12 && !version.AsSpan().SequenceEqual(expectedReply))
                    throw new InvalidOperationException("UNAVAILABLE: desktop RFB version negotiation changed after inspection");
            }
            if (frame.Count > skip) await local.WriteAsync(buffer.AsMemory(skip, frame.Count - skip), cancellationToken);
        }
    }
}
