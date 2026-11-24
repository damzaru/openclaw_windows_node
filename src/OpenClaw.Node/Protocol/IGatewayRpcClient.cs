using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;

namespace OpenClaw.Node.Protocol
{
    public interface IGatewayRpcClient
    {
        Task SendRequestAsync(string method, object? @params, CancellationToken cancellationToken);
    }

    public interface IGatewayRequestClient : IGatewayRpcClient
    {
        Task<T?> RequestAsync<T>(string method, object? @params, CancellationToken cancellationToken, int? timeoutMs = null);
        bool IsConnected { get; }
    }

    public interface IPluginSurfaceClient : IGatewayRequestClient
    {
        string? GetPluginSurfaceUrl(string surface);
        Task<string?> RefreshPluginSurfaceUrlAsync(string surface, CancellationToken cancellationToken);
    }

    public interface IGatewayStreamClient
    {
        Uri GatewayUri { get; }
        ClientWebSocket CreateStreamSocket();
    }
}
