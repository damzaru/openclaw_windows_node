using OpenClaw.Node.Services;

namespace OpenClaw.Node.Tray;

internal static class BrowserCacheDialog
{
    internal static async void Show()
    {
#if WINDOWS
        if (System.Windows.Forms.MessageBox.Show(
            "Clear only the companion browser's disk, code and GPU caches?\n\nLogins, cookies, passwords, browsing history, site storage and extensions will be kept. Close the companion browser completely first.",
            "Clear Browser Cache", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question,
            System.Windows.Forms.MessageBoxDefaultButton.Button2) != System.Windows.Forms.DialogResult.Yes) return;
        try
        {
            var result = await ManagedBrowserHost.MaintainProfileAsync(clear: true);
            System.Windows.Forms.MessageBox.Show($"Cleared {result.RemovedCacheBytes / (1024d * 1024):N1} MiB of browser cache.\nLogins and extensions were preserved.", "Browser Cache");
        }
        catch (Exception error)
        {
            Console.WriteLine("[BROWSER] Cache clearing failed: " + error.Message);
            System.Windows.Forms.MessageBox.Show(error.Message, "Browser Cache", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        }
#else
        await Task.CompletedTask;
#endif
    }
}
