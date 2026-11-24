#if WINDOWS
using OpenClaw.Node.Services;
using System.Windows.Forms;
using Velopack;

namespace OpenClaw.Node.Tray;

internal sealed class CompanionUpdateController
{
    private readonly UpdateManager _manager;
    private readonly CancellationToken _lifetime;
    private readonly Action _exit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile UpdateInfo? _ready;
    private CompanionUpdateController(UpdateManager manager, CancellationToken lifetime, Action exit)
        => (_manager, _lifetime, _exit) = (manager, lifetime, exit);

    internal static CompanionUpdateController? ForInstalledApp(CancellationToken lifetime, Action exit)
    {
        var manager = new UpdateManager(new SignedReleaseSource(lifetime));
        if (!manager.IsInstalled) return null;
        Console.WriteLine($"[UPDATE] Installed Windows package {manager.CurrentVersion}");
        return new(manager, lifetime, exit);
    }
    internal string MenuText => _ready == null ? "Check for updates..." : "Restart to update...";

    private async Task PrepareAsync()
    {
        var update = await _manager.CheckForUpdatesAsync();
        if (update == null) return;
        await _manager.DownloadUpdatesAsync(update, cancelToken: _lifetime);
        _ready = update;
        Console.WriteLine($"[UPDATE] Verified package {update.TargetFullRelease.Version} is ready; waiting for user restart.");
    }

    internal async Task PollAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _lifetime);
            while (!_lifetime.IsCancellationRequested)
            {
                if (await _gate.WaitAsync(0, _lifetime))
                {
                    try { await PrepareAsync(); }
                    catch (Exception e) when (!_lifetime.IsCancellationRequested) { Console.Error.WriteLine("[UPDATE] " + e.Message); }
                    finally { _gate.Release(); }
                }
                await Task.Delay(TimeSpan.FromHours(6), _lifetime);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal async void CheckFromTray()
    {
        // The tray owns this callback. Remain on its UI thread for dialogs.
        if (!await _gate.WaitAsync(0))
        {
            MessageBox.Show("An update check is already in progress.", "OpenClaw Companion");
            return;
        }
        try
        {
            if (_ready == null) await PrepareAsync();
            if (_lifetime.IsCancellationRequested) return;
            if (_ready == null) { MessageBox.Show("You have the latest published build.", "OpenClaw Companion"); return; }
            if (MessageBox.Show("The update is ready. Restart now? This will briefly disconnect the node.", "OpenClaw Companion",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true, restartArgs: new[] { "--tray" });
            _exit();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[UPDATE] " + e.Message);
            if (!_lifetime.IsCancellationRequested) MessageBox.Show("Update not applied: " + e.Message, "OpenClaw Companion");
        }
        finally { _gate.Release(); }
    }
}
#endif
