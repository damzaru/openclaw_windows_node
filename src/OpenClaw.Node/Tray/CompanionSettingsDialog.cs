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
            using var form = CreateForm(settings, out var tryApplySettings);
            if (form.ShowDialog() != DialogResult.OK || !tryApplySettings()) return false;
            store.Save(settings);
            ApplyStartup(settings.StartWithWindows);
            return true;
        }

        internal static Form CreateForm(CompanionSettings settings, out Func<bool> tryApplySettings, Func<bool>? confirmUnrestrictedExecution = null, Func<bool>? confirmFileAccess = null)
        {
            var form = new Form
            {
                Text = "OpenClaw Companion Settings",
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = false,
                MinimizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(680, 690),
                MinimumSize = new Size(560, 320),
            };
            var layout = new TableLayoutPanel
            {
                Name = "settingsLayout",
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var scroll = new Panel { Name = "settingsScroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            var table = new TableLayoutPanel
            {
                Name = "settingsFields",
                Dock = DockStyle.Top,
                Padding = new Padding(18),
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(table);
            layout.Controls.Add(scroll, 0, 0);
            form.Controls.Add(layout);
            form.Load += (_, _) =>
            {
                var area = Screen.FromControl(form).WorkingArea;
                form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
                form.Location = new Point(Math.Clamp(form.Left, area.Left, area.Right - form.Width), Math.Clamp(form.Top, area.Top, area.Bottom - form.Height));
            };

            var url = AddText(table, "Gateway WebSocket URL", settings.GatewayUrl);
            var token = AddText(table, "Gateway token", settings.GatewayToken ?? string.Empty, secret: true);
            var fingerprint = AddText(table, "TLS certificate SHA-256 pin", settings.TlsCertificateSha256 ?? string.Empty);
            var session = AddText(table, "PTT chat session key", settings.TalkSessionKey);
            var startWithWindows = AddCheck(table, "Start with Windows", settings.StartWithWindows);
            AddSection(table, "Remote command execution");
            var unrestricted = AddCheck(table, "Allow unrestricted exec policies", settings.AllowUnrestrictedExecutionPolicy);
            AddNote(table, "Local opt-in: permits Allow all, wildcard rules, and shells such as PowerShell. Off by default. Does not rewrite the policy; re-enabling reactivates any stored unrestricted policies.");
            AddSection(table, "File Transfer (explicit opt-in)");
            var fileRead = AddCheck(table, "Read files and directories", settings.EnableFileRead);
            var fileWrite = AddCheck(table, "Create and overwrite files", settings.EnableFileWrite);
            AddNote(table, "Requires the Gateway File Transfer plugin, approval of the new node commands, and allowed read/write paths. Local drive paths only. Read/write access is separate from command execution policy.");
            AddSection(table, "Gateway capabilities");
            var browser = AddCheck(table, "Browser proxy", settings.EnableBrowserProxy);
            var diskCache = AddNumber(table, "Browser disk cache (MiB)", settings.BrowserDiskCacheMiB, 16, 2048);
            var codeCache = AddNumber(table, "Browser code cache (MiB)", settings.BrowserCodeCacheMiB, 16, 2048);
            AddNote(table, "Browser data: " + BrowserProfileService.Default.ProfilePath + ". Logins and extensions persist. Disk-cache target applies at browser launch; oversized disk/code/GPU caches are cleared only while the browser is closed (checked every minute). Clear Browser Cache is also available in the tray menu.");
            var canvas = AddCheck(table, "Canvas and A2UI", settings.EnableCanvas);
            var computer = AddCheck(table, "Computer control (foreground)", settings.EnableComputerControl);
            var desktop = AddCheck(table, "Desktop sharing (loopback VNC)", settings.EnableDesktopSharing);
            var vncPort = AddNumber(table, "Loopback VNC port", settings.DesktopVncPort, 1, 65535);
            var vncPassword = AddText(table, "VNC password (optional)", settings.DesktopVncPassword ?? "", secret: true);
            AddNote(table, "Desktop sharing requires a separately installed VNC server on 127.0.0.1 using VNC password authentication. Enter its password here, or in the Control UI when prompted.");
            var location = AddCheck(table, "Windows location", settings.EnableLocation);
            var talk = AddCheck(table, "Push-to-talk microphone", settings.EnableTalkPushToTalk);
            AddSection(table, "Sensitive capture (explicit opt-in)");
            var camera = AddCheck(table, "Camera snapshots", settings.EnableCameraSnapshots);
            var clips = AddCheck(table, "Camera video clips", settings.EnableCameraClips);
            var recording = AddCheck(table, "Screen recording", settings.EnableScreenRecording);
            var maxClip = AddNumber(table, "Maximum camera clip (seconds)", settings.MaximumCameraClipSeconds, 1, 60);
            var maxScreen = AddNumber(table, "Maximum screen record (seconds)", settings.MaximumScreenRecordSeconds, 1, 120);

            AddNote(table, "Changes restart the companion. Execution is managed through OpenClaw exec approvals and remains deny-by-default unless you explicitly allow otherwise.");

            // Actions are outside the scroll viewport, so they remain reachable at every DPI/window height.
            var buttons = new FlowLayoutPanel
            {
                Name = "settingsActions", FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Padding = new Padding(18, 10, 18, 12), Margin = Padding.Empty,
            };
            var save = new Button { Name = "saveSettingsButton", Text = "Save and restart", AutoSize = true, Padding = new Padding(8, 4, 8, 4), DialogResult = DialogResult.OK };
            var cancel = new Button { Name = "cancelSettingsButton", Text = "Cancel", AutoSize = true, Padding = new Padding(8, 4, 8, 4), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 1);
            form.AcceptButton = save;
            form.CancelButton = cancel;

            tryApplySettings = () =>
            {
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
                if (!settings.AllowUnrestrictedExecutionPolicy && unrestricted.Checked)
                {
                    var confirmed = confirmUnrestrictedExecution?.Invoke() ?? MessageBox.Show(
                        "This lets authorized Gateway policy operators enable Allow all or allow arbitrary programs, including PowerShell and other shells. Commands run with the companion's Windows permissions and can access, change, or delete your files.\n\nThis does not rewrite the policy. Re-enabling can immediately reactivate previously stored Allow-all or shell policies. Permit unrestricted execution policies?",
                        "Confirm unrestricted remote execution", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (!confirmed) return false;
                }
                if ((!settings.EnableFileRead && fileRead.Checked) || (!settings.EnableFileWrite && fileWrite.Checked))
                {
                    var confirmed = confirmFileAccess?.Invoke() ?? MessageBox.Show(
                        "Authorized Gateway file-transfer tools will be able to read/download or create/overwrite files with this companion's Windows permissions. The Gateway must separately allow the commands and paths. This does not change execution policy. Enable the selected file access?",
                        "Confirm remote file access", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (!confirmed) return false;
                }
                var enablingSensitive = (!settings.EnableCameraSnapshots && camera.Checked) ||
                                        (!settings.EnableCameraClips && clips.Checked) ||
                                        (!settings.EnableScreenRecording && recording.Checked) ||
                                        (!settings.EnableLocation && location.Checked) ||
                                        (!settings.EnableTalkPushToTalk && talk.Checked);
                enablingSensitive |= !settings.EnableComputerControl && computer.Checked;
                enablingSensitive |= !settings.EnableDesktopSharing && desktop.Checked;
                if (enablingSensitive && MessageBox.Show(
                        "These capabilities can access private capture, location, or microphone data, or control the desktop. They still require Gateway pairing/allowlisting. Enable them?",
                        "Confirm sensitive capabilities",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;

                settings.GatewayUrl = endpoint.ToString();
                settings.GatewayToken = token.Text;
                settings.TlsCertificateSha256 = pin;
                settings.TalkSessionKey = session.Text;
                settings.StartWithWindows = startWithWindows.Checked;
                settings.AllowUnrestrictedExecutionPolicy = unrestricted.Checked;
                settings.EnableFileRead = fileRead.Checked;
                settings.EnableFileWrite = fileWrite.Checked;
                settings.EnableBrowserProxy = browser.Checked;
                settings.BrowserDiskCacheMiB = (int)diskCache.Value;
                settings.BrowserCodeCacheMiB = (int)codeCache.Value;
                settings.EnableCanvas = canvas.Checked;
                settings.EnableComputerControl = computer.Checked;
                settings.EnableDesktopSharing = desktop.Checked;
                settings.DesktopVncPort = (int)vncPort.Value;
                settings.DesktopVncPassword = vncPassword.Text;
                settings.EnableLocation = location.Checked;
                settings.EnableTalkPushToTalk = talk.Checked;
                settings.EnableCameraSnapshots = camera.Checked;
                settings.EnableCameraClips = clips.Checked;
                settings.EnableScreenRecording = recording.Checked;
                settings.MaximumCameraClipSeconds = (int)maxClip.Value;
                settings.MaximumScreenRecordSeconds = (int)maxScreen.Value;
                return true;
            };
            return form;
        }

        private static TextBox AddText(TableLayoutPanel table, string label, string value, bool secret = false)
        {
            AddLabel(table, label);
            var input = new TextBox { Name = label, Text = value, Dock = DockStyle.Fill, UseSystemPasswordChar = secret };
            table.Controls.Add(input, 1, table.RowCount - 1);
            return input;
        }

        private static CheckBox AddCheck(TableLayoutPanel table, string label, bool value)
        {
            AddLabel(table, label);
            var input = new CheckBox { Name = label, Checked = value, AutoSize = true };
            table.Controls.Add(input, 1, table.RowCount - 1);
            return input;
        }

        private static NumericUpDown AddNumber(TableLayoutPanel table, string label, int value, int min, int max)
        {
            AddLabel(table, label);
            var input = new NumericUpDown { Name = label, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 100 };
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
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = text, Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 15, 3, 6) };
            table.Controls.Add(label, 0, table.RowCount);
            table.SetColumnSpan(label, 2);
            table.RowCount++;
        }

        private static void AddNote(TableLayoutPanel table, string text)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = new Label
            {
                Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText,
                MaximumSize = new Size(610, 0), Margin = new Padding(3, 10, 3, 14),
            };
            table.Controls.Add(note, 0, table.RowCount);
            table.SetColumnSpan(note, 2);
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
