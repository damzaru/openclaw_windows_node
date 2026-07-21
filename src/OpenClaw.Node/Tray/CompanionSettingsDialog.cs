#if WINDOWS
using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenClaw.Node.Services;

namespace OpenClaw.Node.Tray
{
    internal static class CompanionSettingsDialog
    {
        public static bool Show(CompanionSettingsStore store)
        {
            var settings = store.Load();
            using var form = new Form
            {
                Text = "OpenClaw Companion Settings",
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(680, 690),
            };
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 2,
                AutoScroll = true,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.Controls.Add(table);

            var url = AddText(table, "Gateway WebSocket URL", settings.GatewayUrl);
            var token = AddText(table, "Gateway token", settings.GatewayToken ?? string.Empty, secret: true);
            var fingerprint = AddText(table, "TLS certificate SHA-256 pin", settings.TlsCertificateSha256 ?? string.Empty);
            var session = AddText(table, "PTT chat session key", settings.TalkSessionKey);
            var startWithWindows = AddCheck(table, "Start with Windows", settings.StartWithWindows);
            AddSection(table, "Gateway capabilities");
            var browser = AddCheck(table, "Browser proxy", settings.EnableBrowserProxy);
            var canvas = AddCheck(table, "Canvas and A2UI", settings.EnableCanvas);
            var location = AddCheck(table, "Windows location", settings.EnableLocation);
            var talk = AddCheck(table, "Push-to-talk microphone", settings.EnableTalkPushToTalk);
            AddSection(table, "Sensitive capture (explicit opt-in)");
            var camera = AddCheck(table, "Camera snapshots", settings.EnableCameraSnapshots);
            var clips = AddCheck(table, "Camera video clips", settings.EnableCameraClips);
            var recording = AddCheck(table, "Screen recording", settings.EnableScreenRecording);
            var maxClip = AddNumber(table, "Maximum camera clip (seconds)", settings.MaximumCameraClipSeconds, 1, 60);
            var maxScreen = AddNumber(table, "Maximum screen record (seconds)", settings.MaximumScreenRecordSeconds, 1, 120);

            var note = new Label
            {
                Text = "Changes restart the companion. Execution remains deny-by-default and is managed through OpenClaw exec approvals.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                MaximumSize = new Size(610, 0),
                Margin = new Padding(3, 14, 3, 14),
            };
            table.Controls.Add(note, 0, table.RowCount);
            table.SetColumnSpan(note, 2);
            table.RowCount++;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var save = new Button { Text = "Save and restart", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 0, table.RowCount);
            table.SetColumnSpan(buttons, 2);
            table.RowCount++;
            form.AcceptButton = save;
            form.CancelButton = cancel;

            if (form.ShowDialog() != DialogResult.OK) return false;
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("ws" or "wss"))
            {
                MessageBox.Show("Gateway URL must be an absolute ws:// or wss:// URL.", "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            var pin = NormalizeFingerprint(fingerprint.Text);
            if (pin == null || (pin.Length > 0 && endpoint.Scheme != "wss"))
            {
                MessageBox.Show(
                    pin == null ? "TLS certificate pin must contain exactly 64 hexadecimal digits." : "TLS certificate pin requires a wss:// Gateway URL.",
                    "Invalid settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
            var enablingSensitive = (!settings.EnableCameraSnapshots && camera.Checked) ||
                                    (!settings.EnableCameraClips && clips.Checked) ||
                                    (!settings.EnableScreenRecording && recording.Checked) ||
                                    (!settings.EnableLocation && location.Checked) ||
                                    (!settings.EnableTalkPushToTalk && talk.Checked);
            if (enablingSensitive && MessageBox.Show(
                    "These capabilities can access private capture, location, or microphone data. They still require Gateway pairing/allowlisting. Enable them?",
                    "Confirm sensitive capabilities",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;

            settings.GatewayUrl = endpoint.ToString();
            settings.GatewayToken = token.Text;
            settings.TlsCertificateSha256 = pin;
            settings.TalkSessionKey = session.Text;
            settings.StartWithWindows = startWithWindows.Checked;
            settings.EnableBrowserProxy = browser.Checked;
            settings.EnableCanvas = canvas.Checked;
            settings.EnableLocation = location.Checked;
            settings.EnableTalkPushToTalk = talk.Checked;
            settings.EnableCameraSnapshots = camera.Checked;
            settings.EnableCameraClips = clips.Checked;
            settings.EnableScreenRecording = recording.Checked;
            settings.MaximumCameraClipSeconds = (int)maxClip.Value;
            settings.MaximumScreenRecordSeconds = (int)maxScreen.Value;
            store.Save(settings);
            ApplyStartup(settings.StartWithWindows);
            return true;
        }

        private static TextBox AddText(TableLayoutPanel table, string label, string value, bool secret = false)
        {
            AddLabel(table, label);
            var input = new TextBox { Text = value, Dock = DockStyle.Fill, UseSystemPasswordChar = secret };
            table.Controls.Add(input, 1, table.RowCount - 1);
            return input;
        }

        private static CheckBox AddCheck(TableLayoutPanel table, string label, bool value)
        {
            AddLabel(table, label);
            var input = new CheckBox { Checked = value, AutoSize = true };
            table.Controls.Add(input, 1, table.RowCount - 1);
            return input;
        }

        private static NumericUpDown AddNumber(TableLayoutPanel table, string label, int value, int min, int max)
        {
            AddLabel(table, label);
            var input = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 100 };
            table.Controls.Add(input, 1, table.RowCount - 1);
            return input;
        }

        private static string? NormalizeFingerprint(string value)
        {
            var normalized = new System.Text.StringBuilder(64);
            foreach (var character in value.Trim())
            {
                if (Uri.IsHexDigit(character)) normalized.Append(char.ToUpperInvariant(character));
                else if (character is ':' or '-' || char.IsWhiteSpace(character)) continue;
                else return null;
            }
            return normalized.Length is 0 or 64 ? normalized.ToString() : null;
        }

        private static void AddLabel(TableLayoutPanel table, string text)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 7) }, 0, row);
        }

        private static void AddSection(TableLayoutPanel table, string text)
        {
            var label = new Label { Text = text, Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 15, 3, 6) };
            table.Controls.Add(label, 0, table.RowCount);
            table.SetColumnSpan(label, 2);
            table.RowCount++;
        }

        private static void ApplyStartup(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enabled)
            {
                var executable = Environment.ProcessPath ?? Application.ExecutablePath;
                key?.SetValue("OpenClaw Companion", $"\"{executable}\" --tray", RegistryValueKind.String);
            }
            else key?.DeleteValue("OpenClaw Companion", throwOnMissingValue: false);
        }
    }
}
#else
using OpenClaw.Node.Services;

namespace OpenClaw.Node.Tray
{
    internal static class CompanionSettingsDialog
    {
        public static bool Show(CompanionSettingsStore store) => false;
    }
}
#endif
