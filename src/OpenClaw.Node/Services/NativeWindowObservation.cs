#if WINDOWS
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace OpenClaw.Node.Services;

// Invoked before tray/log initialization with a private stdio pair. No root-desktop
// UIA traversal and no process titles, queries or provider errors in persistent logs.
internal static class NativeWindowObservation
{
    private static string Bound(string text, int n = 160) => text.Length > n ? text[..n] : text;
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, out int val, int size);

    internal static int Run()
    {
        try
        {
            // WinExe runs without a console. Changing Console.InputEncoding attempts
            // SetConsoleCP even when both handles are redirected anonymous pipes.
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            var request = JsonSerializer.Deserialize<WindowRead>(input.ReadLine() ?? "");
            if (request == null || request.Depth is < 0 or > 64 || request.Limit is < 1 or > 2000 || request.Query.Length > 256) return 2;
            output.WriteLine(JsonSerializer.Serialize(Read(request)));
            return 0;
        }
        catch (JsonException) { return 3; }
        catch (IOException) { return 4; }
        catch (System.ComponentModel.Win32Exception) { return 5; }
        catch { return 1; }
    }

    private static IEnumerable<WindowRow> Windows()
    {
        var svc = new AutomationService();
        foreach (var w in svc.ListWindowsAsync().GetAwaiter().GetResult())
        {
            var hwnd = (IntPtr)w.Handle;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) != 0 || cloaked != 0) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            WindowRow? row = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                var name = new StringBuilder(256);
                if (GetClassName(hwnd, name, name.Capacity) == 0) continue;
                var rect = svc.GetWindowRectAsync(w.Handle, null).GetAwaiter().GetResult();
                if (rect == null || rect.Width < 1 || rect.Height < 1) continue;
                if (!GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != 0) continue;
                var identity = new WindowIdentity(w.Handle, pid, p.StartTime.ToUniversalTime().Ticks, name.ToString(), Bound(w.Title));
                row = new(identity, Bound(w.Process), rect.Left, rect.Top, rect.Width, rect.Height);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (row != null) yield return row;
        }
    }

    private static WindowReply Read(WindowRead request)
    {
        if (request.Window == null) return new(Rows: Windows().Take(97).ToArray());
        var row = Windows().FirstOrDefault(w => w.Identity == request.Window);
        if (row == null) return new(Stale: true);
        var hwnd = (IntPtr)row.Identity.Handle;
        var observationId = Guid.NewGuid().ToString("N");
        var elements = new List<object>();
        var parents = new Dictionary<string,string>();
        var walked = 0;
        var truncated = false;
        var walker = TreeWalker.ControlViewWalker;
        void Visit(AutomationElement e, int depth, string? parent)
        {
            if (++walked > 2048 || elements.Count >= Math.Min(request.Limit, 512)) { truncated = true; return; }
            // Missing, inaccessible or sensitive controls do not contribute text or descendants.
            var c = e.Current;
            if (c.IsPassword || c.ControlType == ControlType.Edit || c.ControlType == ControlType.Document ||
                (e.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly)) return;
            var role = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var name = Bound(c.Name ?? "");
            var rect = c.BoundingRectangle;
            if (!rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
                && (name.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || role.Contains(request.Query, StringComparison.OrdinalIgnoreCase)))
            {
                var id = observationId + ":" + walked;
                elements.Add(new { elementRef = id, role, label = name, bounds = new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height } });
                if (parent != null) parents[id] = parent;
                parent = id;
            }
            if (depth >= request.Depth) return;
            for (var child = walker.GetFirstChild(e); child != null && !truncated; child = walker.GetNextSibling(child))
                Visit(child, depth + 1, parent);
        }
        Visit(AutomationElement.FromHandle(hwnd), 0, null);
        var observation = new Dictionary<string,object> { ["kind"] = "window", ["observationId"] = observationId, ["elements"] = elements };
        var details = new Dictionary<string,object> { ["parents"] = parents, ["truncated"] = truncated, ["elementBoundsSpace"] = "screen-physical-pixels" };
        if (request.Image) CaptureWindow(row, observation, details);
        var current = Windows().FirstOrDefault(w => w.Identity == row.Identity);
        if (current != row) return new(Stale: true);
        return new(Result: JsonSerializer.SerializeToElement(new { ok = true, observation, details }));
    }

    private static void CaptureWindow(WindowRow row, Dictionary<string,object> observation, Dictionary<string,object> details)
    {
        var hwnd = (IntPtr)row.Identity.Handle;
        // Never sample the screen behind/over the window. If the OS cannot confirm
        // capture policy, the app doesn't render, or the budget is exceeded, keep AX only.
        details["screenshotError"] = "Window image unavailable; no desktop capture substituted.";
        if (IsIconic(hwnd) || !GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != 0 ||
            (long)row.Width * row.Height > 8_000_000) return;
        using var bitmap = new Bitmap(row.Width, row.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var dc = g.GetHdc();
            try { if (!PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */)) return; }
            finally { g.ReleaseHdc(dc); }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        if (stream.Length > 1_000_000 || !GetWindowDisplayAffinity(hwnd, out affinity) || affinity != 0) return;
        observation["format"] = "jpeg";
        observation["width"] = row.Width;
        observation["height"] = row.Height;
        observation["base64"] = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
        details["coordinateSpace"] = "image-pixels";
        details.Remove("screenshotError");
    }
}
#endif
