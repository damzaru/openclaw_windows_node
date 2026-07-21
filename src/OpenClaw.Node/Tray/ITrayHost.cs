using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Tray
{
    public interface ITrayHost
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task UpdateAsync(TrayStatusSnapshot snapshot, CancellationToken cancellationToken);
        Task ShowNotificationAsync(string title, string body, CancellationToken cancellationToken);
        Task StopAsync();
    }
}
