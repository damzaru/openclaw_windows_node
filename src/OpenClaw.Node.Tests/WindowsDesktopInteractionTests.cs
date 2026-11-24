#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class WindowsDesktopInteractionTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [WindowsDesktopFact]
    public async Task NativeGatewayHandlers_CaptureClickTypeAndKeyboardReplaceInDisposableWindow()
    {
        var ready = new TaskCompletionSource<(Form Form, TextBox Editor)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyTrace = new List<string>();
        var clicks = new List<MouseButtons>();
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var form = new Form
                {
                    Text = "OpenClaw disposable desktop interaction check", Width = 650, Height = 250,
                    StartPosition = FormStartPosition.CenterScreen, TopMost = true,
                };
                var label = new Label { Text = "Synthetic test only — no documents or settings are changed", Dock = DockStyle.Top, Height = 42 };
                var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16), Text = "Waiting for native companion input" };
                editor.KeyDown += (_, e) => keyTrace.Add($"down:{e.KeyCode}:ctrl={e.Control}:selection={editor.SelectionLength}");
                editor.KeyPress += (_, e) => keyTrace.Add($"char:{(int)e.KeyChar}:selection={editor.SelectionLength}");
                editor.KeyUp += (_, e) => keyTrace.Add($"up:{e.KeyCode}:ctrl={e.Control}:selection={editor.SelectionLength}");
                editor.MouseDown += (_, e) => clicks.Add(e.Button);
                form.Controls.Add(editor); form.Controls.Add(label);
                form.Shown += (_, _) => { form.Activate(); editor.Focus(); ready.TrySetResult((form, editor)); };
                Application.Run(form);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var (window, textBox) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableComputerControl = true });
        var executionId = Guid.NewGuid().ToString();
        try
        {
            await Task.Delay(200, timeout.Token);
            var handle = (IntPtr)window.Invoke(() => window.Handle);
            // Test runners are not always allowed to steal foreground focus.
            // Let the operator/desktop test coordinator activate this exact fixture.
            var focusDeadline = DateTime.UtcNow.AddSeconds(25);
            while (GetForegroundWindow() != handle && DateTime.UtcNow < focusDeadline)
                await Task.Delay(100, timeout.Token);
            Assert.Equal(handle, GetForegroundWindow());
            await WaitForEditorFocus();
            var point = (Point)window.Invoke(() => textBox.PointToScreen(new Point(textBox.Width / 2, textBox.Height / 2)));
            var displays = await new ScreenCaptureService().ListDisplaysAsync();
            var display = displays.Single(value => new Rectangle(value.X, value.Y, value.Width, value.Height).Contains(point));
            var snapshot = await Invoke("screen.snapshot", new { executionId, screenIndex = display.Index, format = "png", maxWidth = 1200 });
            using var image = JsonDocument.Parse(snapshot.PayloadJSON!);
            var frame = image.RootElement;
            var refWidth = frame.GetProperty("width").GetInt32();
            var refHeight = frame.GetProperty("height").GetInt32();
            Assert.True(Convert.FromBase64String(frame.GetProperty("base64").GetString()!).Length > 100);
            await Invoke("computer.act", new
            {
                action = "left_click", executionId, screenIndex = display.Index, refWidth,
                displayFrameId = frame.GetProperty("displayFrameId").GetString(),
                x = (point.X - display.X) * refWidth / display.Width,
                y = (point.Y - display.Y) * refHeight / display.Height,
            });
            await WaitForEditorFocus();
            await Task.Delay(200, timeout.Token);
            Assert.Equal(MouseButtons.Left, (MouseButtons)window.Invoke(() => clicks.Single()));
            window.Invoke(() => textBox.Select(0, 0));
            await Invoke("computer.act", new { action = "key", executionId, keys = "CTRL+a" });
            await WaitForSelection();
            const string expected = "OpenClaw 2026.10.9 native input verified — café";
            await Invoke("computer.act", new { action = "type", executionId, text = expected });
            await Task.Delay(150, timeout.Token);
            Assert.True(expected == (string)window.Invoke(() => textBox.Text), (string)window.Invoke(() => "Actual=" + textBox.Text + "\n" + string.Join("\n", keyTrace.Take(25))));
            window.Invoke(() => textBox.Select(0, 0));
            await Invoke("computer.act", new { action = "key", executionId, keys = "Control_L+a" });
            await WaitForSelection();
            await Invoke("computer.act", new { action = "type", executionId, text = expected + " ✓" });
            await Task.Delay(150, timeout.Token);
            Assert.Equal(expected + " ✓", (string)window.Invoke(() => textBox.Text));
            var artifact = Path.Combine(Path.GetTempPath(), "OpenClawDesktopInteractionTests", "native-input-result.png");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            window.Invoke(() =>
            {
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(artifact, ImageFormat.Png);
            });
            await Invoke("computer.act", new { action = "__close_execution", executionId, reason = "smoke-test-complete" });
        }
        finally
        {
            await executor.ResetSessionAsync();
            window.BeginInvoke(window.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Disposable test window did not close");
        }

        async Task<BridgeInvokeResponse> Invoke(string command, object parameters)
        {
            // Abort if focus moved rather than injecting into another user's app.
            Assert.Equal((IntPtr)window.Invoke(() => window.Handle), GetForegroundWindow());
            var result = await executor.ExecuteAsync(new() { Id = Guid.NewGuid().ToString(), Command = command, ParamsJSON = JsonSerializer.Serialize(parameters) }, timeout.Token);
            Assert.True(result.Ok, result.Error?.Message);
            return result;
        }

        async Task WaitForEditorFocus()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if ((bool)window.Invoke(() => textBox.Focused)) { await Task.Delay(100, timeout.Token); return; }
                await Task.Delay(20, timeout.Token);
            }
            Assert.Fail("Disposable editor did not acquire keyboard focus");
        }

        async Task WaitForSelection()
        {
            // SendInput acceptance is not application completion. Observe the
            // fixture's selection and released modifier before the next action.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if ((bool)window.Invoke(() => textBox.SelectionLength == textBox.TextLength && (Control.ModifierKeys & Keys.Control) == Keys.None))
                    return;
                await Task.Delay(20, timeout.Token);
            }
            Assert.Fail((string)window.Invoke(() => $"Native Ctrl+A did not complete: selected={textBox.SelectionLength}/{textBox.TextLength}, modifiers={Control.ModifierKeys}\n" + string.Join("\n", keyTrace.TakeLast(25))));
        }
    }
}

public sealed class WindowsDesktopFactAttribute : FactAttribute
{
    public WindowsDesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_NATIVE_DESKTOP_TESTS") != "1")
            Skip = "Set RUN_NATIVE_DESKTOP_TESTS=1 on an unlocked interactive desktop; this test injects input into its disposable window.";
    }
}
#endif
