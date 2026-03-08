using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Tray
{
    public sealed class NoOpTrayHost : ITrayHost
    {
        private readonly Action<string>? _log;

        public NoOpTrayHost(Action<string>? log = null)
        {
            _log = log;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _log?.Invoke("[TRAY] Tray shell requested, using no-op host in this build/runtime.");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(TrayStatusSnapshot snapshot, CancellationToken cancellationToken)
        {
            _log?.Invoke($"[TRAY] {snapshot.State}: {snapshot.Message}");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _log?.Invoke("[TRAY] Stopped.");
            return Task.CompletedTask;
        }
    }
}
