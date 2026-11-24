#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OpenClaw.Node.Services;
using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class CompanionSettingsDialogTests
{
    [Theory]
    [InlineData(680, 690, 1f)]
    [InlineData(580, 360, 1f)]
    [InlineData(680, 420, 1.5f)]
    [InlineData(680, 420, 2f)]
    public void ActionsStayVisibleAndLastSettingRemainsReachable(int width, int height, float scale)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = CompanionSettingsDialog.CreateForm(new CompanionSettings(), out _);
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Scale(new SizeF(scale, scale));
                form.ClientSize = new Size((int)(width * scale), (int)(height * scale));
                form.Show();
                form.PerformLayout();
                Application.DoEvents();
                var viewport = (Panel)form.Controls.Find("settingsScroll", true).Single();
                var fields = (TableLayoutPanel)form.Controls.Find("settingsFields", true).Single();
                var actions = (FlowLayoutPanel)form.Controls.Find("settingsActions", true).Single();
                var save = (Button)form.AcceptButton!;
                var cancel = (Button)form.CancelButton!;
                var lastSetting = form.Controls.Find("Maximum screen record (seconds)", true).Single();

                Assert.Equal(fields.RowCount, fields.RowStyles.Count);
                Assert.All(fields.RowStyles.Cast<RowStyle>(), row => Assert.Equal(SizeType.AutoSize, row.SizeType));
                Assert.Same(actions, save.Parent);
                Assert.Same(actions, cancel.Parent);
                Assert.NotSame(viewport, actions.Parent);
                foreach (var button in new[] { save, cancel })
                {
                    var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Assert.True(form.ClientRectangle.Contains(bounds), $"Clipped action: {button.Text}, {bounds}, client={form.ClientRectangle}");
                }
                Assert.True(viewport.VerticalScroll.Visible);
                viewport.AutoScrollPosition = new Point(0, fields.Height);
                Application.DoEvents();
                var lastBounds = viewport.RectangleToClient(lastSetting.RectangleToScreen(lastSetting.ClientRectangle));
                Assert.True(viewport.ClientRectangle.Contains(lastBounds), $"Last setting unreachable: {lastBounds}, viewport={viewport.ClientRectangle}");
                var lastNote = fields.Controls.OfType<Label>().Last();
                var noteBounds = viewport.RectangleToClient(lastNote.RectangleToScreen(lastNote.ClientRectangle));
                Assert.True(viewport.ClientRectangle.Contains(noteBounds), $"Bottom note unreachable: {noteBounds}, viewport={viewport.ClientRectangle}");
                Assert.False(viewport.HorizontalScroll.Visible);
                var directory = Path.Combine(Path.GetTempPath(), "OpenClawSettingsLayoutTests");
                Directory.CreateDirectory(directory);
                SaveBitmap(form, directory, width, height, scale, "bottom");
                var actionsBounds = actions.Bounds;
                viewport.AutoScrollPosition = Point.Empty;
                Application.DoEvents();
                Assert.Equal(actionsBounds, actions.Bounds);

                // Synthetic settings only; preserve a rendered regression artifact for visual review.
                SaveBitmap(form, directory, width, height, scale, "top");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Settings layout test did not finish");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnablingUnrestrictedPoliciesRequiresExplicitConfirmation(bool confirm)
    {
        RunSta(() =>
        {
            var settings = new CompanionSettings { GatewayUrl = "wss://original.example", GatewayToken = "synthetic-ui-token" };
            var prompted = false;
            using var form = CompanionSettingsDialog.CreateForm(settings, out var apply, () => { prompted = true; return confirm; });
            var toggle = (CheckBox)form.Controls.Find("Allow unrestricted exec policies", true).Single();
            Assert.False(toggle.Checked);
            toggle.Checked = true;
            ((TextBox)form.Controls.Find("Gateway WebSocket URL", true).Single()).Text = "wss://edited.example";
            Assert.Equal(confirm, apply());
            Assert.True(prompted);
            Assert.Equal(confirm, settings.AllowUnrestrictedExecutionPolicy);
            Assert.Equal(confirm ? "wss://edited.example/" : "wss://original.example", settings.GatewayUrl);
        });
    }

    [Fact]
    public void DisablingUnrestrictedPoliciesDoesNotRequireConfirmation()
    {
        RunSta(() =>
        {
            var settings = new CompanionSettings { AllowUnrestrictedExecutionPolicy = true };
            using var form = CompanionSettingsDialog.CreateForm(settings, out var apply, () => throw new Exception("Unexpected confirmation"));
            ((CheckBox)form.Controls.Find("Allow unrestricted exec policies", true).Single()).Checked = false;
            Assert.True(apply());
            Assert.False(settings.AllowUnrestrictedExecutionPolicy);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnablingFileAccessRequiresExplicitLocalConfirmation(bool confirm)
    {
        RunSta(() =>
        {
            var settings = new CompanionSettings();
            var prompted = false;
            using var form = CompanionSettingsDialog.CreateForm(settings, out var apply, confirmFileAccess: () => { prompted = true; return confirm; });
            ((CheckBox)form.Controls.Find("Read files and directories", true).Single()).Checked = true;
            ((CheckBox)form.Controls.Find("Create and overwrite files", true).Single()).Checked = true;
            Assert.Equal(confirm, apply()); Assert.True(prompted);
            Assert.Equal(confirm, settings.EnableFileRead); Assert.Equal(confirm, settings.EnableFileWrite);
            Assert.False(settings.AllowUnrestrictedExecutionPolicy);
        });
    }

    [Fact]
    public void RevokingFileAccessDoesNotPromptOrChangeExecutionPolicy()
    {
        RunSta(() =>
        {
            var settings = new CompanionSettings { EnableFileRead = true, EnableFileWrite = true, AllowUnrestrictedExecutionPolicy = true };
            using var form = CompanionSettingsDialog.CreateForm(settings, out var apply, confirmFileAccess: () => throw new Exception("Unexpected confirmation"));
            ((CheckBox)form.Controls.Find("Read files and directories", true).Single()).Checked = false;
            ((CheckBox)form.Controls.Find("Create and overwrite files", true).Single()).Checked = false;
            Assert.True(apply()); Assert.False(settings.EnableFileRead); Assert.False(settings.EnableFileWrite); Assert.True(settings.AllowUnrestrictedExecutionPolicy);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Settings test did not finish");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SaveBitmap(Form form, string directory, int width, int height, float scale, string position)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, $"settings-{width}-{height}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{position}.png"), ImageFormat.Png);
    }
}
#endif
