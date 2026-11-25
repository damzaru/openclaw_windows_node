using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OpenClaw.Node.Services;

// Only the short-lived worker sees native handles. Never put them, or the desktop
// root, in a Gateway request. A hung third-party UIA provider is killed on timeout.
internal sealed record WindowIdentity(long Handle, int Pid, long Started, string Class, string Title);
internal sealed record WindowRow(WindowIdentity Identity, string App, int X, int Y, int Width, int Height);
internal sealed record WindowRead(WindowIdentity? Window, string Query = "", int Depth = 12, int Limit = 200, bool Image = false);
internal sealed record WindowReply(WindowRow[]? Rows = null, JsonElement? Result = null, bool Stale = false);

internal sealed class WindowObservationService
{
    internal static readonly string[] Actions = { "list_windows", "get_accessibility_tree", "get_window_state" };
    private sealed record Entry(string Scope, WindowIdentity Target, long Born);
    private readonly Dictionary<string, Entry> _refs = new();
    private readonly SemaphoreSlim _slot = new(1);
    private readonly Func<WindowRead, CancellationToken, Task<WindowReply>> _worker;
    private long _epoch;
    internal WindowObservationService(Func<WindowRead,CancellationToken,Task<WindowReply>>? worker = null)
        => _worker = worker ?? RunWorkerAsync;

    internal void Forget(string? scope)
    {
        lock (_refs)
        {
            ++_epoch;
            foreach (var key in _refs.Where(x => scope == null || x.Value.Scope == scope).Select(x => x.Key).ToArray()) _refs.Remove(key);
        }
    }

    private static Exception Invalid() => new ArgumentException("INVALID_REQUEST: invalid window observation parameters");
    private static Exception Stale() => new InvalidOperationException("COMPUTER_STALE_OBSERVATION: refresh list_windows");
    internal async Task<object> ReadAsync(string action, JsonElement args, string scope, CancellationToken ct)
    {
        var state = action == "get_window_state";
        if (args.EnumerateObject().Any(p => p.Name is not ("executionId" or "action") &&
            (!state || p.Name is not ("windowRef" or "query" or "depth" or "maxElements" or "includeScreenshot")))) throw Invalid();
        int Number(string name, int fallback, int min, int max) => !args.TryGetProperty(name, out var p) ? fallback :
            p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) && i >= min && i <= max ? i : throw Invalid();
        string Str(string name) => !args.TryGetProperty(name, out var p) ? "" :
            p.ValueKind == JsonValueKind.String && p.GetString()!.Length <= 256 ? p.GetString()! : throw Invalid();
        var reference = Str("windowRef");
        WindowIdentity? target = null;
        long epoch;
        lock (_refs)
        {
            epoch = _epoch;
            foreach (var key in _refs.Where(x => Environment.TickCount64 - x.Value.Born > 120_000).Select(x => x.Key).ToArray()) _refs.Remove(key);
            if (state)
            {
                if (!_refs.TryGetValue(reference, out var entry) || entry.Scope != scope) throw Stale();
                target = entry.Target;
            }
        }
        var image = state && (!args.TryGetProperty("includeScreenshot", out var b) || b.ValueKind switch {
            JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() });
        var request = new WindowRead(target, Str("query"), Number("depth", 12, 0, 64), Number("maxElements", 200, 1, 2000), image);
        if (!await _slot.WaitAsync(0, ct)) throw new InvalidOperationException("RESOURCE_EXHAUSTED: another window observation is in progress");
        try
        {
            var reply = await _worker(request, ct);
            ct.ThrowIfCancellationRequested();
            lock (_refs)
            {
                if (epoch != _epoch || reply.Stale) throw Stale();
                if (state) return reply.Result ?? throw new InvalidOperationException("Window observation unavailable");
                var rows = reply.Rows ?? throw new InvalidOperationException("Window discovery unavailable");
                // Re-listing replaces this execution's window set rather than growing indefinitely.
                foreach (var key in _refs.Where(x => x.Value.Scope == scope).Select(x => x.Key).ToArray()) _refs.Remove(key);
                if (_refs.Count + rows.Length > 1024) throw new InvalidOperationException("RESOURCE_EXHAUSTED: too many active window observations");
                var windows = rows.Take(96).Select(row => {
                    var id = Guid.NewGuid().ToString("N");
                    _refs[id] = new(scope, row.Identity, Environment.TickCount64);
                    return new { windowRef = id, title = row.Identity.Title, appName = row.App,
                        bounds = new { x = row.X, y = row.Y, width = row.Width, height = row.Height } };
                }).ToArray();
                return new { ok = true, details = new { windows, truncatedWindows = Math.Max(0, rows.Length - windows.Length) } };
            }
        }
        finally { _slot.Release(); }
    }

    private static async Task<WindowReply> RunWorkerAsync(WindowRead request, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OpenClaw.Node.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
          StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("--window-observation-worker");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Window reader unavailable");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(6));
        using var stop = limit.Token.Register(() => {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { } // Already exited.
        });
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), limit.Token);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            limit.Token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 && output.Length <= 5_000_000 ? JsonSerializer.Deserialize<WindowReply>(output)
                ?? throw new InvalidOperationException("Window reader returned no observation") : throw new InvalidOperationException("Window observation unavailable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Window accessibility provider timed out"); }
        finally { if (!process.HasExited) process.Kill(); }
    }
}
