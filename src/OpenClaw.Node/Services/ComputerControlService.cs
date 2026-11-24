using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenClaw.Node.Services;

internal sealed record DisplayGeometry(string Id, int X, int Y, int Width, int Height);

internal interface IComputerInput
{
    void Move(int x, int y);
    void Mouse(string button, bool down);
    void Scroll(int amount, bool horizontal);
    void Key(ushort key, bool down);
    Task TypeAsync(string text, CancellationToken cancellationToken);
}

/// <summary>The declared screen-only subset of the Gateway's Computer Use v2 contract.</summary>
internal sealed class ComputerControlService : IDisposable
{
    internal static readonly string[] Actions = { "left_click", "right_click", "middle_click", "double_click", "triple_click", "mouse_move", "left_click_drag", "left_mouse_down", "left_mouse_up", "scroll", "type", "key", "hold_key" };
    private sealed record Frame(DisplayGeometry Display, string Topology, int Index, int Width, int Height, string Scope, DateTimeOffset Created);
    private readonly Func<IReadOnlyList<DisplayGeometry>> _displays;
    private readonly IComputerInput _input;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Frame> _frames = new();
    private readonly List<ushort> _heldKeys = new();
    private bool _mouseHeld;
    private string? _mouseScope;
    private ushort[] _mouseModifiers = Array.Empty<ushort>();
    private DateTimeOffset _mouseReleaseAt;
    private readonly System.Threading.Timer _releaseWatchdog;
    private bool _disposed;

    internal ComputerControlService(Func<IReadOnlyList<DisplayGeometry>>? displays = null, IComputerInput? input = null)
    {
        _displays = displays ?? ReadDisplays;
        _input = input ?? new WindowsComputerInput();
        _releaseWatchdog = new System.Threading.Timer(_ => _ = ReleaseExpiredMouseAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal static object Descriptor(string generation) => new
    {
        contractVersion = 2,
        provider = new { id = "openclaw-windows", label = "Windows native input", generation },
        // Screenshot is a provider action even though it uses screen.snapshot,
        // not computer.act. Current Gateways derive local wait support from it.
        actions = new[] { "screenshot" }.Concat(Actions).ToArray(), targets = new[] { "screen" }, deliveryModes = new[] { "foreground" },
        observations = new[] { "image" }, features = new { recording = false, agentCursor = false, multiDisplay = true },
    };

    internal static IReadOnlyList<DisplayGeometry> ReadDisplays()
        => new ScreenCaptureService().ListDisplaysAsync().GetAwaiter().GetResult()
            .Select(d => new DisplayGeometry(d.Id, d.X, d.Y, d.Width, d.Height)).ToArray();

    internal string CurrentTopology() => JsonSerializer.Serialize(_displays());

    internal string RegisterFrame(int screenIndex, int width, int height, string capturedTopology, string? executionId = null)
    {
        lock (_frames)
        {
            var displays = _displays();
            if (capturedTopology != JsonSerializer.Serialize(displays) || screenIndex < 0 || screenIndex >= displays.Count)
                throw new InvalidOperationException("COMPUTER_STALE_OBSERVATION: displays changed during capture; take another screenshot");
            if (_frames.Count >= 128) _frames.Clear();
            var id = Guid.NewGuid().ToString();
            _frames[id] = new Frame(displays[screenIndex], capturedTopology, screenIndex, width, height, executionId ?? "session", DateTimeOffset.UtcNow);
            return id;
        }
    }

    internal async Task<object> ActAsync(string? json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json ?? "{}");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("computer.act params must be an object");
        var action = String(root, "action", required: true)!;
        var executionId = ReadExecutionId(root);
        var inputScope = executionId ?? "session";
        if (action == "__close_execution")
        {
            if (executionId == null || root.EnumerateObject().Any(p => p.Name is not ("action" or "executionId" or "reason")))
                throw Invalid("execution cleanup requires executionId and an optional reason");
            var reason = String(root, "reason");
            if (reason?.Length > 256) throw Invalid("execution cleanup reason is too long");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_mouseScope == inputScope) ReleaseHeldInput();
                lock (_frames)
                    foreach (var id in _frames.Where(pair => pair.Value.Scope == inputScope).Select(pair => pair.Key).ToArray())
                        _frames.Remove(id);
                return new { ok = true };
            }
            finally { _gate.Release(); }
        }
        if (!Actions.Contains(action, StringComparer.Ordinal)) throw Invalid("computer action is not supported by this provider");
        if (root.TryGetProperty("deliveryMode", out var mode) && (mode.ValueKind != JsonValueKind.String || mode.GetString() != "foreground"))
            throw Invalid("this provider supports foreground delivery only");
        foreach (var field in new[] { "windowRef", "elementRef", "observationId" })
            if (root.TryGetProperty(field, out _)) throw Invalid("this provider supports screen targets only");
        var permitted = new HashSet<string>(new[] { "action", "executionId", "deliveryMode", "screenIndex", "refWidth" }, StringComparer.Ordinal);
        if (action is "type") permitted.Add("text");
        else if (action is "key" or "hold_key") { permitted.Add("keys"); if (action == "hold_key") permitted.Add("durationMs"); }
        else
        {
            permitted.UnionWith(new[] { "displayFrameId", "x", "y" });
            if (action == "left_click_drag") permitted.UnionWith(new[] { "fromX", "fromY", "durationMs" });
            else permitted.Add("modifiers");
            if (action == "scroll") permitted.UnionWith(new[] { "scrollDirection", "scrollAmount" });
        }
        if (root.EnumerateObject().Any(p => !permitted.Contains(p.Name))) throw Invalid("unsupported computer action fields");
        if (root.TryGetProperty("screenIndex", out _)) Integer(root, "screenIndex", 0, int.MaxValue);
        if (root.TryGetProperty("refWidth", out _)) Integer(root, "refWidth", 1, 8000);
        var text = action == "type" ? String(root, "text", required: true) : null;
        if (text?.Length > 100_000) throw Invalid("text exceeds 100000 characters");
        var keys = action is "key" or "hold_key" ? ParseKeys(String(root, "keys", true)!) : Array.Empty<ushort>();
        var modifiers = String(root, "modifiers") is string chord ? ParseKeys(chord, modifiersOnly: true) : Array.Empty<ushort>();
        var duration = action is "hold_key" or "left_click_drag" ? Integer(root, "durationMs", 0, 10_000, 500) : 0;
        var direction = action == "scroll" ? String(root, "scrollDirection", true) : null;
        if (direction != null && direction is not ("up" or "down" or "left" or "right")) throw Invalid("invalid scrollDirection");
        var amount = action == "scroll" ? Integer(root, "scrollAmount", 1, 100, 3) : 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ComputerControlService));
            cancellationToken.ThrowIfCancellationRequested();
            if (_mouseHeld && (_mouseScope != inputScope || action is not ("mouse_move" or "left_mouse_up")))
                throw Invalid("left button is already held by another action; finish its split drag first");
            if (!_mouseHeld && action == "left_mouse_up") throw Invalid("left button is not held by computer control");
            (int X, int Y)? point = null, from = null;
            var hasCoordinates = root.TryGetProperty("x", out _) || root.TryGetProperty("y", out _) || action == "left_click_drag";
            if (hasCoordinates)
            {
                var frame = ResolveFrame(root, inputScope);
                point = MapPoint(frame, Number(root, "x"), Number(root, "y"));
                if (action == "left_click_drag")
                    from = root.TryGetProperty("fromX", out _) || root.TryGetProperty("fromY", out _)
                        ? MapPoint(frame, Number(root, "fromX"), Number(root, "fromY")) : point;
            }
            else if (action is not ("type" or "key" or "hold_key" or "scroll" or "left_mouse_down" or "left_mouse_up"))
                throw Invalid("x and y are required");
            foreach (var key in modifiers) { cancellationToken.ThrowIfCancellationRequested(); Press(key); }
            try
            {
                if (point.HasValue && action != "left_click_drag") _input.Move(point.Value.X, point.Value.Y);
                switch (action)
                {
                    case "type": await _input.TypeAsync(text!, cancellationToken); break;
                    case "key": case "hold_key":
                        foreach (var key in keys) { cancellationToken.ThrowIfCancellationRequested(); Press(key); }
                        if (action == "hold_key") await Task.Delay(duration, cancellationToken);
                        break;
                    case "scroll": _input.Scroll((direction is "down" or "left" ? -1 : 1) * amount * 120, direction is "left" or "right"); break;
                    case "mouse_move": if (_mouseHeld) _mouseReleaseAt = DateTimeOffset.UtcNow.AddSeconds(30); break;
                    case "left_mouse_down":
                        _mouseHeld = true; _mouseScope = inputScope; _mouseReleaseAt = DateTimeOffset.UtcNow.AddSeconds(30);
                        _mouseModifiers = modifiers;
                        _input.Mouse("left", true); break;
                    case "left_mouse_up": _input.Mouse("left", false); _mouseHeld = false; _mouseScope = null; _mouseModifiers = Array.Empty<ushort>(); break;
                    case "left_click_drag":
                        _input.Move(from!.Value.X, from.Value.Y);
                        _mouseHeld = true; _mouseScope = inputScope; _input.Mouse("left", true);
                        try
                        {
                            var steps = Math.Max(1, duration / 16);
                            for (var i = 1; i <= steps; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                _input.Move(from.Value.X + (point!.Value.X - from.Value.X) * i / steps, from.Value.Y + (point.Value.Y - from.Value.Y) * i / steps);
                                if (duration > 0) await Task.Delay(Math.Max(1, duration / steps), cancellationToken);
                            }
                        }
                        finally { _input.Mouse("left", false); _mouseHeld = false; _mouseScope = null; }
                        break;
                    default:
                        var button = action == "right_click" ? "right" : action == "middle_click" ? "middle" : "left";
                        var count = action == "triple_click" ? 3 : action == "double_click" ? 2 : 1;
                        for (var i = 0; i < count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try { _input.Mouse(button, true); }
                            finally { _input.Mouse(button, false); }
                        }
                        break;
                }
            }
            finally { ReleaseKeys(keepMouseModifiers: _mouseHeld); }
            // SendInput acceptance does not prove the target application acted on it.
            return new { ok = true, effect = "unverifiable" };
        }
        catch { if (!_mouseHeld || _mouseScope == inputScope) ReleaseHeldInput(); throw; }
        finally { _gate.Release(); }
    }

    private Frame ResolveFrame(JsonElement root, string inputScope)
    {
        var id = String(root, "displayFrameId", true)!;
        var width = Integer(root, "refWidth", 1, 8000);
        lock (_frames)
        {
            if (!_frames.TryGetValue(id, out var frame) || frame.Scope != inputScope || frame.Topology != CurrentTopology() || frame.Width != width ||
                DateTimeOffset.UtcNow - frame.Created > TimeSpan.FromMinutes(10) ||
                (root.TryGetProperty("screenIndex", out _) && Integer(root, "screenIndex", 0, int.MaxValue) != frame.Index))
                throw new InvalidOperationException("COMPUTER_STALE_OBSERVATION: screenshot geometry changed; take another screen.snapshot");
            return frame;
        }
    }

    private static (int X, int Y) MapPoint(Frame frame, double x, double y)
    {
        if (x >= frame.Width || y >= frame.Height) throw Invalid("coordinates are outside the reference screenshot");
        return (frame.Display.X + (int)Math.Floor(x * frame.Display.Width / frame.Width), frame.Display.Y + (int)Math.Floor(y * frame.Display.Height / frame.Height));
    }

    private void Press(ushort key)
    {
        if (_heldKeys.Contains(key)) return;
        _heldKeys.Add(key);
        _input.Key(key, true);
    }
    private void ReleaseKeys(bool keepMouseModifiers = false)
    {
        Exception? failure = null;
        foreach (var key in _heldKeys.AsEnumerable().Reverse().Where(key => !keepMouseModifiers || !_mouseModifiers.Contains(key)).ToArray())
        {
            try { _input.Key(key, false); } catch (Exception ex) { failure ??= ex; }
            _heldKeys.Remove(key);
        }
        if (failure != null) throw failure;
    }
    private void ReleaseHeldInput()
    {
        if (_mouseHeld)
        {
            try { _input.Mouse("left", false); } catch { }
            finally { _mouseHeld = false; _mouseScope = null; _mouseModifiers = Array.Empty<ushort>(); }
        }
        try { ReleaseKeys(); } catch { }
    }
    private async Task ReleaseExpiredMouseAsync()
    {
        await _gate.WaitAsync();
        try { if (!_disposed && _mouseHeld && DateTimeOffset.UtcNow >= _mouseReleaseAt) ReleaseHeldInput(); }
        finally { _gate.Release(); }
    }
    internal async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try { ReleaseHeldInput(); lock (_frames) _frames.Clear(); }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        _disposed = true;
        _releaseWatchdog.Dispose();
        ReleaseHeldInput();
        // Reset/watchdog callbacks can already be queued. Keep their semaphore alive.
    }

    private static InvalidOperationException Invalid(string message) => new("INVALID_REQUEST: " + message);
    internal static string? ReadExecutionId(JsonElement root)
    {
        var id = String(root, "executionId");
        if (id != null && !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"))
            throw Invalid("executionId must be a UUID");
        return id;
    }
    private static string? String(JsonElement root, string key, bool required = false)
    {
        if (!root.TryGetProperty(key, out var value)) { if (required) throw Invalid(key + " is required"); return null; }
        if (value.ValueKind != JsonValueKind.String) throw Invalid(key + " must be a string");
        return value.GetString();
    }
    private static double Number(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result) || result < 0)
            throw Invalid(key + " must be a non-negative finite number");
        return result;
    }
    private static int Integer(JsonElement root, string key, int min, int max, int? fallback = null)
    {
        if (!root.TryGetProperty(key, out var value) && fallback.HasValue) return fallback.Value;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < min || result > max) throw Invalid(key + " is outside its integer bounds");
        return result;
    }
    internal static ushort[] ParseKeys(string chord, bool modifiersOnly = false)
    {
        var parts = chord.Replace('+', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 8) throw Invalid("invalid key chord");
        return parts.Select(part =>
        {
            var token = part.ToUpperInvariant();
            ushort key = token switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "CTRL_L" or "CONTROL_L" => 0xA2, "CTRL_R" or "CONTROL_R" => 0xA3,
                "ALT" or "OPTION" => 0x12, "ALT_L" => 0xA4, "ALT_R" => 0xA5,
                "SHIFT" => 0x10, "SHIFT_L" => 0xA0, "SHIFT_R" => 0xA1,
                "WIN" or "WINDOWS" or "SUPER" or "META" or "CMD" or "COMMAND" or "SUPER_L" or "META_L" => 0x5b,
                "SUPER_R" or "META_R" => 0x5c,
                "ENTER" or "RETURN" => 0x0d, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1b,
                "BACKSPACE" or "BACK_SPACE" => 0x08, "DELETE" or "DEL" => 0x2e, "SPACE" => 0x20, "INSERT" or "INS" => 0x2d,
                "PLUS" or "EQUAL" or "=" => 0xBB, "MINUS" or "-" => 0xBD,
                "PRINTSCREEN" or "PRINT_SCREEN" => 0x2C, "CAPSLOCK" or "CAPS_LOCK" => 0x14,
                "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
                "HOME" => 0x24, "END" => 0x23, "PAGEUP" or "PAGE_UP" => 0x21, "PAGEDOWN" or "PAGE_DOWN" => 0x22,
                _ when token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]) => token[0],
                _ when token.StartsWith('F') && int.TryParse(token[1..], out var n) && n is >= 1 and <= 24 => (ushort)(0x70 + n - 1),
                _ => throw Invalid("unsupported key token: " + part),
            };
            if (modifiersOnly && key is not (0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >= 0xA0 and <= 0xA5)) throw Invalid("modifiers must contain modifier keys only");
            return key;
        }).Distinct().ToArray();
    }
}

internal sealed class WindowsComputerInput : IComputerInput
{
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private static void Send(Input input)
    {
        if (!OperatingSystem.IsWindows() || SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("UNAVAILABLE: Windows input was blocked; check the interactive desktop and application privilege level");
    }
    public void Move(int x, int y)
    {
        if (!OperatingSystem.IsWindows() || !SetCursorPos(x, y)) throw new InvalidOperationException("UNAVAILABLE: cursor could not be moved on the interactive desktop");
    }
    internal static uint MouseFlags(string button, bool down, bool swapped)
    {
        // The wire's left/right actions mean primary/secondary. SendInput flags
        // address physical buttons; Windows then applies the user's handedness.
        var physical = swapped ? button switch { "left" => "right", "right" => "left", _ => button } : button;
        return physical switch { "left" => down ? 0x2u : 0x4u, "right" => down ? 0x8u : 0x10u, _ => down ? 0x20u : 0x40u };
    }
    public void Mouse(string button, bool down) => Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = MouseFlags(button, down, OperatingSystem.IsWindows() && GetSystemMetrics(23) != 0) } } });
    public void Scroll(int amount, bool horizontal) => Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = horizontal ? 0x1000u : 0x800u, Data = unchecked((uint)amount) } } });
    public void Key(ushort key, bool down) => Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput
    {
        Key = key,
        Flags = (down ? 0u : 2u) | (key is >= 0x21 and <= 0x28 or 0x2c or 0x2d or 0x2e or 0x5b or 0x5c or 0xA3 or 0xA5 ? 1u : 0u),
    } } });
    public Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 4 } } }); }
            finally { Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 6 } } }); }
        }
        return Task.CompletedTask;
    }
}
