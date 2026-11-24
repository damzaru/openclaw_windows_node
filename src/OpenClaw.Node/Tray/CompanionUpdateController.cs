using OpenClaw.Node.Services;
#if WINDOWS
using System.Windows.Forms;
#endif
using Velopack;

namespace OpenClaw.Node.Tray;

internal sealed class CompanionUpdateController
{
    private readonly UpdateManager _manager;
    private readonly CancellationToken _lifetime;
    private readonly Action _exit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    internal CompanionUpdateController(UpdateManager manager, CancellationToken lifetime, Action exit)
        => (_manager, _lifetime, _exit) = (manager, lifetime, exit);

    internal static CompanionUpdateController? ForInstalledApp(CancellationToken lifetime, Action exit)
    {
        var manager = new UpdateManager(new SignedReleaseSource(lifetime));
        if (!manager.IsInstalled) return null;
        Console.WriteLine($"[UPDATE] Installed Windows package {manager.CurrentVersion}");
        return new(manager, lifetime, exit);
    }
    internal string MenuText => _ready ? "Restart to update..." : "Check for updates...";

    internal async Task<UpdateInfo?> PrepareAsync()
    {
        var update = await _manager.CheckForUpdatesAsync();
        _ready = false;
        if (update == null) return null;
        await _manager.DownloadUpdatesAsync(update, cancelToken: _lifetime);
        _ready = true;
        Console.WriteLine($"[UPDATE] Verified package {update.TargetFullRelease.Version} is ready; waiting for user restart.");
        return update;
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

#if WINDOWS
    internal async void CheckFromTray() => await CheckFromTrayAsync(
        release => MessageBox.Show(
            $"Update {release.Version} is ready. Restart now? This will briefly disconnect the node.",
            "OpenClaw Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes,
        message => MessageBox.Show(message, "OpenClaw Companion"));
#endif

    internal async Task CheckFromTrayAsync(Func<VelopackAsset, bool> confirmRestart, Action<string> showMessage)
    {
        // Stay on the calling UI thread for dialogs.
        if (!await _gate.WaitAsync(0))
        {
            showMessage("An update check is already in progress.");
            return;
        }
        try
        {
            // A newer full package may have been published since the background download.
            var update = await PrepareAsync();
            if (_lifetime.IsCancellationRequested) return;
            if (update == null) { showMessage("You have the latest published build."); return; }
            if (!confirmRestart(update.TargetFullRelease)) return;
            _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, restartArgs: new[] { "--tray" });
            _exit();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[UPDATE] " + e.Message);
            if (!_lifetime.IsCancellationRequested) showMessage("Update not applied: " + e.Message);
        }
        finally { _gate.Release(); }
    }
}
