using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Protocol
{
    public interface IGatewayRpcClient
    {
        Task SendRequestAsync(string method, object? @params, CancellationToken cancellationToken);
    }
}
