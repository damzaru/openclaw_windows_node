using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OpenClaw.Node.Protocol
{
    /// <summary>
    /// Per-invocation lifecycle exposed to command handlers. The legacy
    /// OnNodeInvoke callback remains supported, while newer duplex handlers
    /// can consume ordered input, observe cancellation, and emit progress.
    /// </summary>
    public sealed class NodeInvokeContext
    {
        private readonly Func<string, CancellationToken, Task> _reportProgress;

        internal NodeInvokeContext(
            BridgeInvokeRequest request,
            CancellationToken cancellationToken,
            ChannelReader<NodeInvokeInputPayload> inputs,
            Func<string, CancellationToken, Task> reportProgress)
        {
            Request = request;
            CancellationToken = cancellationToken;
            Inputs = inputs;
            _reportProgress = reportProgress;
        }

        public BridgeInvokeRequest Request { get; }
        public CancellationToken CancellationToken { get; }
        public ChannelReader<NodeInvokeInputPayload> Inputs { get; }

        public Task ReportProgressAsync(string chunk, CancellationToken cancellationToken = default)
            => _reportProgress(chunk ?? string.Empty, cancellationToken);
    }
}
