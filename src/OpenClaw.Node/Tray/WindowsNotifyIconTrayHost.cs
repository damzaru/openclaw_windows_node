using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Tray
{
    public sealed class WindowsNotifyIconTrayHost : ITrayHost
    {
        private readonly Action<string>? _log;
        private readonly Action? _onOpenLogs;
        private readonly Action? _onOpenConfig;
        private readonly Action? _onRestart;
        private readonly Action? _onExit;
        private readonly Action? _onCopyDiagnostics;

        private readonly TaskCompletionSource<bool> _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Thread? _uiThread;
        private SynchronizationContext? _uiContext;

        private object? _notifyIcon;
        private MethodInfo? _appExitThread;
        private object? _stateItem;
        private object? _pairsItem;
        private object? _reconnectItem;
        private object? _onboardingItem;

        public WindowsNotifyIconTrayHost(Action<string>? log = null, Action? onOpenLogs = null, Action? onOpenConfig = null, Action? onRestart = null, Action? onExit = null, Action? onCopyDiagnostics = null)
        {
            _log = log;
            _onOpenLogs = onOpenLogs;
            _onOpenConfig = onOpenConfig;
            _onRestart = onRestart;
            _onExit = onExit;
            _onCopyDiagnostics = onCopyDiagnostics;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                _log?.Invoke("[TRAY] Windows tray host skipped: non-Windows runtime.");
                return;
            }

            if (_uiThread != null) return;

            _uiThread = new Thread(UiMain)
            {
                IsBackground = true,
                Name = "OpenClawTrayUI"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();

            using var reg = cancellationToken.Register(() => _ = StopAsync());
            await _startedTcs.Task.WaitAsync(cancellationToken);
        }

        public Task UpdateAsync(TrayStatusSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (_notifyIcon == null || _uiContext == null) return Task.CompletedTask;

            _uiContext.Post(_ =>
            {
                try
                {
                    SetProperty(_notifyIcon, "Text", ToNotifyIconText($"OpenClaw: {snapshot.Message}"));
                    if (_stateItem != null) SetProperty(_stateItem, "Text", $"State: {snapshot.State}");
                    if (_pairsItem != null) SetProperty(_pairsItem, "Text", $"Pending pairs: {snapshot.PendingPairs}");
                    if (_reconnectItem != null)
                    {
                        var reconnectText = snapshot.LastReconnectMs.HasValue
                            ? $"Last reconnect: {snapshot.LastReconnectMs.Value}ms"
                            : "Last reconnect: n/a";
                        SetProperty(_reconnectItem, "Text", reconnectText);
                    }
                    if (_onboardingItem != null)
                    {
                        SetProperty(_onboardingItem, "Text", snapshot.OnboardingStatus);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[TRAY] Update failed: {ex.Message}");
                }
            }, null);

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_uiThread == null)
            {
                return;
            }

            try
            {
                if (_uiContext != null)
                {
                    _uiContext.Post(_ =>
                    {
                        try
                        {
                            _appExitThread?.Invoke(null, null);
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[TRAY] ExitThread failed: {ex.Message}");
                        }
                    }, null);
                }
                else
                {
                    _appExitThread?.Invoke(null, null);
                }
            }
            catch
            {
                // no-op
            }

            await _stoppedTcs.Task;
            _uiThread = null;
        }

        private void UiMain()
        {
            try
            {
                var appType = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms")
                    ?? throw new InvalidOperationException("System.Windows.Forms.Application not available");
                var notifyIconType = Type.GetType("System.Windows.Forms.NotifyIcon, System.Windows.Forms")
                    ?? throw new InvalidOperationException("System.Windows.Forms.NotifyIcon not available");
                var menuType = Type.GetType("System.Windows.Forms.ContextMenuStrip, System.Windows.Forms")
                    ?? throw new InvalidOperationException("System.Windows.Forms.ContextMenuStrip not available");
                var menuItemType = Type.GetType("System.Windows.Forms.ToolStripMenuItem, System.Windows.Forms")
                    ?? throw new InvalidOperationException("System.Windows.Forms.ToolStripMenuItem not available");
                var separatorType = Type.GetType("System.Windows.Forms.ToolStripSeparator, System.Windows.Forms");
                var wfSyncType = Type.GetType("System.Windows.Forms.WindowsFormsSynchronizationContext, System.Windows.Forms");

                if (wfSyncType != null)
                {
                    var wfSync = Activator.CreateInstance(wfSyncType) as SynchronizationContext;
                    if (wfSync != null) SynchronizationContext.SetSynchronizationContext(wfSync);
                }

                _uiContext = SynchronizationContext.Current;

                _notifyIcon = Activator.CreateInstance(notifyIconType)
                    ?? throw new InvalidOperationException("Unable to create NotifyIcon");
                var menu = Activator.CreateInstance(menuType)
                    ?? throw new InvalidOperationException("Unable to create ContextMenuStrip");

                _stateItem = Activator.CreateInstance(menuItemType, "State: Starting")
                    ?? throw new InvalidOperationException("Unable to create State item");
                _pairsItem = Activator.CreateInstance(menuItemType, "Pending pairs: 0")
                    ?? throw new InvalidOperationException("Unable to create Pair item");
                _reconnectItem = Activator.CreateInstance(menuItemType, "Last reconnect: n/a")
                    ?? throw new InvalidOperationException("Unable to create Reconnect item");
                _onboardingItem = Activator.CreateInstance(menuItemType, "Onboarding: Ready")
                    ?? throw new InvalidOperationException("Unable to create Onboarding item");
                SetProperty(_stateItem, "Enabled", false);
                SetProperty(_pairsItem, "Enabled", false);
                SetProperty(_reconnectItem, "Enabled", false);
                SetProperty(_onboardingItem, "Enabled", false);

                var openLogsItem = Activator.CreateInstance(menuItemType, "Open Logs")
                    ?? throw new InvalidOperationException("Unable to create Open Logs item");
                var openConfigItem = Activator.CreateInstance(menuItemType, "Open Config File")
                    ?? throw new InvalidOperationException("Unable to create Open Config item");
                var copyDiagItem = Activator.CreateInstance(menuItemType, "Copy Diagnostics")
                    ?? throw new InvalidOperationException("Unable to create Copy Diagnostics item");
                var restartItem = Activator.CreateInstance(menuItemType, "Restart Node")
                    ?? throw new InvalidOperationException("Unable to create Restart item");
                var exitItem = Activator.CreateInstance(menuItemType, "Exit")
                    ?? throw new InvalidOperationException("Unable to create Exit item");

                AddClickHandler(openLogsItem, () =>
                {
                    try { _onOpenLogs?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke($"[TRAY] Open Logs action failed: {ex.Message}"); }
                });
                AddClickHandler(openConfigItem, () =>
                {
                    try { _onOpenConfig?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke($"[TRAY] Open Config action failed: {ex.Message}"); }
                });
                AddClickHandler(copyDiagItem, () =>
                {
                    try { _onCopyDiagnostics?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke($"[TRAY] Copy Diagnostics action failed: {ex.Message}"); }
                });
                AddClickHandler(restartItem, () =>
                {
                    try { _onRestart?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke($"[TRAY] Restart action failed: {ex.Message}"); }
                });
                AddClickHandler(exitItem, () =>
                {
                    try { _onExit?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke($"[TRAY] Exit action failed: {ex.Message}"); }
                });

                AddMenuItem(menu, _stateItem);
                AddMenuItem(menu, _pairsItem);
                AddMenuItem(menu, _reconnectItem);
                AddMenuItem(menu, _onboardingItem);
                if (separatorType != null && Activator.CreateInstance(separatorType) is object separator1) AddMenuItem(menu, separator1);
                AddMenuItem(menu, openLogsItem);
                AddMenuItem(menu, openConfigItem);
                AddMenuItem(menu, copyDiagItem);
                AddMenuItem(menu, restartItem);
                if (separatorType != null && Activator.CreateInstance(separatorType) is object separator2) AddMenuItem(menu, separator2);
                AddMenuItem(menu, exitItem);

                SetProperty(_notifyIcon, "ContextMenuStrip", menu);
                var icon = ResolveApplicationIcon();
                if (icon != null)
                {
                    SetProperty(_notifyIcon, "Icon", icon);
                }
                SetProperty(_notifyIcon, "Text", ToNotifyIconText("OpenClaw: Starting"));
                SetProperty(_notifyIcon, "Visible", true);

                _appExitThread = appType.GetMethod("ExitThread", BindingFlags.Public | BindingFlags.Static);
                var run = appType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, Type.DefaultBinder, Type.EmptyTypes, null)
                    ?? throw new InvalidOperationException("Application.Run() not found");

                _startedTcs.TrySetResult(true);
                _log?.Invoke("[TRAY] Windows NotifyIcon host started.");
                run.Invoke(null, null);
            }
            catch (Exception ex)
            {
                _startedTcs.TrySetException(ex);
                _log?.Invoke($"[TRAY] Failed to start: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (_notifyIcon != null)
                    {
                        SetProperty(_notifyIcon, "Visible", false);
                        ( _notifyIcon as IDisposable )?.Dispose();
                    }
                }
                catch
                {
                    // no-op
                }

                _log?.Invoke("[TRAY] Windows NotifyIcon host stopped.");
                _stoppedTcs.TrySetResult(true);
            }
        }

        private static string ToNotifyIconText(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "OpenClaw" : value.Trim();
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private static void AddClickHandler(object menuItem, Action onClick)
        {
            var clickEvent = menuItem.GetType().GetEvent("Click")
                ?? throw new InvalidOperationException("ToolStripMenuItem.Click not found");
            EventHandler handler = (_, _) => onClick();
            clickEvent.AddEventHandler(menuItem, handler);
        }

        private static void AddMenuItem(object menu, object item)
        {
            var itemsProp = menu.GetType().GetProperty("Items")
                ?? throw new InvalidOperationException("ContextMenuStrip.Items not found");
            var items = itemsProp.GetValue(menu)
                ?? throw new InvalidOperationException("ContextMenuStrip.Items is null");

            MethodInfo? add = null;
            foreach (var m in items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(m.Name, "Add", StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType.IsAssignableFrom(item.GetType()))
                {
                    add = m;
                    break;
                }
            }

            add ??= items.GetType().GetMethod("Add", new[] { item.GetType() });
            if (add == null)
            {
                throw new InvalidOperationException($"ToolStripItemCollection.Add(<{item.GetType().Name}>) not found");
            }

            add.Invoke(items, new[] { item });
        }

        private object? ResolveApplicationIcon()
        {
            var iconType = Type.GetType("System.Drawing.Icon, System.Drawing")
                ?? Type.GetType("System.Drawing.Icon, System.Drawing.Common");

            try
            {
                var asm = typeof(WindowsNotifyIconTrayHost).Assembly;
                const string iconResource = "OpenClaw.Node.Tray.Assets.openclaw-claw.ico";
                using var stream = asm.GetManifestResourceStream(iconResource);
                var streamCtor = iconType?.GetConstructor(new[] { typeof(Stream) });
                if (stream != null && streamCtor != null)
                {
                    _log?.Invoke("[TRAY] Using embedded tray icon resource.");
                    return streamCtor.Invoke(new object[] { stream });
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TRAY] Embedded icon load failed: {ex.Message}");
            }

            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "openclaw-claw.ico");
                if (File.Exists(iconPath))
                {
                    var fileCtor = iconType?.GetConstructor(new[] { typeof(string) });
                    if (fileCtor != null)
                    {
                        _log?.Invoke($"[TRAY] Using custom icon file fallback: {iconPath}");
                        return fileCtor.Invoke(new object[] { iconPath });
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TRAY] Custom icon file load failed: {ex.Message}");
            }

            var iconsType = Type.GetType("System.Drawing.SystemIcons, System.Drawing")
                ?? Type.GetType("System.Drawing.SystemIcons, System.Drawing.Common");
            var appProp = iconsType?.GetProperty("Application", BindingFlags.Public | BindingFlags.Static);
            return appProp?.GetValue(null);
        }

        private static void SetProperty(object target, string name, object? value)
        {
            var prop = target.GetType().GetProperty(name)
                ?? throw new InvalidOperationException($"Property not found: {target.GetType().Name}.{name}");
            prop.SetValue(target, value);
        }
    }
}
