using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
#if WINDOWS
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
#endif

namespace OpenClaw.Node.Services
{
    /// <summary>
    /// Hosts Canvas in the evergreen Edge WebView2 runtime and resolves the
    /// Gateway's capability-scoped plugin surface instead of exposing the
    /// shared Gateway token to web content.
    /// </summary>
    internal sealed class CanvasService : IDisposable
    {
        internal readonly record struct Placement(double? X, double? Y, double? Width, double? Height);
        private const int MaximumA2UiBytes = 1024 * 1024;
        private const int MaximumA2UiMessages = 512;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _a2uiGate = new(1, 1);
        private readonly IPluginSurfaceClient? _surfaceClient;
        private readonly IGatewayRequestClient? _operatorClient;
        private readonly string _sessionKey;
        private readonly string _instanceId;
        private bool _disposed;

#if WINDOWS
        private Thread? _thread;
        private SynchronizationContext? _ui;
        private Form? _form;
        private WebView2? _browser;
        private TaskCompletionSource<bool> _ready = NewCompletion();
        private Uri? _trustedA2UiUrl;
#endif

        public CanvasService(
            IScreenImageProvider screen,
            IPluginSurfaceClient? surfaceClient = null,
            IGatewayRequestClient? operatorClient = null,
            string sessionKey = "main",
            string? instanceId = null)
        {
            _ = screen ?? throw new ArgumentNullException(nameof(screen));
            _surfaceClient = surfaceClient;
            _operatorClient = operatorClient;
            _sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "main" : sessionKey.Trim();
            _instanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString().ToLowerInvariant() : instanceId.Trim().ToLowerInvariant();
        }

        public async Task PresentAsync(string? url, Placement? placement, CancellationToken cancellationToken)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var target = string.IsNullOrWhiteSpace(url) ? null : await ResolveTargetAsync(url!, cancellationToken).ConfigureAwait(false);
#if WINDOWS
            await InvokeAsync(() =>
            {
                if (target != null) NavigateUnsafe(target, IsCapabilityScopedA2UiUrl(target));
                else if (_browser!.Source == null) SetDefaultDocumentUnsafe();
                if (placement.HasValue) ApplyPlacementUnsafe(placement.Value);
                _form!.ShowInTaskbar = true;
                _form.Show();
                _form.Activate();
                return true;
            }, cancellationToken).ConfigureAwait(false);
#endif
        }

        public async Task HideAsync(CancellationToken cancellationToken)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
#if WINDOWS
            await InvokeAsync(() => { _form!.Hide(); return true; }, cancellationToken).ConfigureAwait(false);
#endif
        }

        public async Task NavigateAsync(string url, CancellationToken cancellationToken)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var target = await ResolveTargetAsync(url, cancellationToken).ConfigureAwait(false);
#if WINDOWS
            await InvokeAsync(() =>
            {
                NavigateUnsafe(target, IsCapabilityScopedA2UiUrl(target));
                _form!.ShowInTaskbar = true;
                _form.Show();
                return true;
            }, cancellationToken).ConfigureAwait(false);
#endif
        }

        public async Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(javaScript) || javaScript.Length > 128 * 1024)
                throw new InvalidOperationException("INVALID_REQUEST: canvas.eval requires javaScript of at most 128 KiB");
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
#if WINDOWS
            return await ExecuteScriptTextAsync(javaScript, cancellationToken).ConfigureAwait(false);
#else
            return null;
#endif
        }

        public async Task<(byte[] Bytes, int Width, int Height)> SnapshotAsync(string format, CancellationToken cancellationToken)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
#if WINDOWS
            return await InvokeAsync(async () =>
            {
                using var stream = new MemoryStream();
                var imageFormat = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase)
                    ? CoreWebView2CapturePreviewImageFormat.Png
                    : CoreWebView2CapturePreviewImageFormat.Jpeg;
                await _browser!.CoreWebView2.CapturePreviewAsync(imageFormat, stream);
                return (stream.ToArray(), Math.Max(1, _browser.ClientSize.Width), Math.Max(1, _browser.ClientSize.Height));
            }, cancellationToken).ConfigureAwait(false);
#else
            throw new PlatformNotSupportedException("Canvas is only available on Windows");
#endif
        }

        public async Task<string> PushA2UiAsync(string? messagesJson, string? jsonl, CancellationToken cancellationToken)
        {
            var messages = DecodeMessages(messagesJson, jsonl);
            var serialized = JsonSerializer.Serialize(messages);
            if (Encoding.UTF8.GetByteCount(serialized) > MaximumA2UiBytes)
                throw new InvalidOperationException("INVALID_REQUEST: A2UI messages exceed 1 MiB");

            await _a2uiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureA2UiHostAsync(cancellationToken).ConfigureAwait(false);
                var script = """
                    (() => {
                      try {
                        const host = globalThis.openclawA2UI;
                        if (!host) return JSON.stringify({ok:false,error:"missing openclawA2UI"});
                        const messages = __MESSAGES__;
                        return JSON.stringify(host.applyMessages(messages));
                      } catch (e) {
                        return JSON.stringify({ok:false,error:String(e?.message ?? e)});
                      }
                    })()
                    """.Replace("__MESSAGES__", serialized, StringComparison.Ordinal);
#if WINDOWS
                return NormalizeJsonResult(await ExecuteScriptTextAsync(script, cancellationToken).ConfigureAwait(false));
#else
                throw new PlatformNotSupportedException("Canvas is only available on Windows");
#endif
            }
            finally
            {
                _a2uiGate.Release();
            }
        }

        public async Task<string> ResetA2UiAsync(CancellationToken cancellationToken)
        {
            await _a2uiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureA2UiHostAsync(cancellationToken).ConfigureAwait(false);
#if WINDOWS
                const string script = """
                    (() => {
                      try {
                        const host = globalThis.openclawA2UI;
                        if (!host) return JSON.stringify({ok:false,error:"missing openclawA2UI"});
                        return JSON.stringify(host.reset());
                      } catch (e) {
                        return JSON.stringify({ok:false,error:String(e?.message ?? e)});
                      }
                    })()
                    """;
                return NormalizeJsonResult(await ExecuteScriptTextAsync(script, cancellationToken).ConfigureAwait(false));
#else
                throw new PlatformNotSupportedException("Canvas is only available on Windows");
#endif
            }
            finally
            {
                _a2uiGate.Release();
            }
        }

        private async Task EnsureA2UiHostAsync(CancellationToken cancellationToken)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            if (await IsA2UiReadyAsync(false, cancellationToken).ConfigureAwait(false)) return;

            var url = ResolveA2UiUrl(_surfaceClient?.GetPluginSurfaceUrl("canvas"));
            if (url == null && _surfaceClient?.IsConnected == true)
                url = ResolveA2UiUrl(await _surfaceClient.RefreshPluginSurfaceUrlAsync("canvas", cancellationToken).ConfigureAwait(false));
            if (url == null)
                throw new InvalidOperationException("A2UI_HOST_NOT_CONFIGURED: gateway did not advertise canvas host");

#if WINDOWS
            await InvokeAsync(() =>
            {
                NavigateUnsafe(url, trustedA2UiActions: true);
                _form!.ShowInTaskbar = true;
                _form.Show();
                return true;
            }, cancellationToken).ConfigureAwait(false);
#endif
            if (await IsA2UiReadyAsync(true, cancellationToken).ConfigureAwait(false)) return;

            if (_surfaceClient?.IsConnected == true)
            {
                var refreshed = ResolveA2UiUrl(await _surfaceClient.RefreshPluginSurfaceUrlAsync("canvas", cancellationToken).ConfigureAwait(false));
                if (refreshed != null)
                {
#if WINDOWS
                    await InvokeAsync(() => { NavigateUnsafe(refreshed, trustedA2UiActions: true); return true; }, cancellationToken).ConfigureAwait(false);
#endif
                    if (await IsA2UiReadyAsync(true, cancellationToken).ConfigureAwait(false)) return;
                }
            }
            throw new InvalidOperationException("A2UI_HOST_UNAVAILABLE: A2UI host not reachable");
        }

        private async Task<bool> IsA2UiReadyAsync(bool poll, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(poll ? 6 : 0);
            do
            {
                try
                {
#if WINDOWS
                    var value = await ExecuteScriptTextAsync("String(Boolean(globalThis.openclawA2UI))", cancellationToken).ConfigureAwait(false);
                    if (string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return true;
#endif
                }
                catch when (poll && !cancellationToken.IsCancellationRequested) { }
                if (!poll || DateTimeOffset.UtcNow >= deadline) return false;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            } while (true);
        }

        private async Task<Uri> ResolveTargetAsync(string rawTarget, CancellationToken cancellationToken)
        {
            if (IsHostedTarget(rawTarget))
            {
                string? surface = null;
                if (_surfaceClient?.IsConnected == true)
                {
                    try { surface = await _surfaceClient.RefreshPluginSurfaceUrlAsync("canvas", cancellationToken).ConfigureAwait(false); }
                    catch when (!cancellationToken.IsCancellationRequested) { }
                }
                surface ??= _surfaceClient?.GetPluginSurfaceUrl("canvas");
                return ResolveHostedTarget(surface, rawTarget)
                    ?? throw new InvalidOperationException("CANVAS_HOST_NOT_CONFIGURED: gateway did not advertise canvas host");
            }

            if (!Uri.TryCreate(rawTarget.Trim(), UriKind.Absolute, out var uri) || !IsAllowedCanvasScheme(uri))
                throw new InvalidOperationException("INVALID_REQUEST: canvas URL must use http, https, file, or about");
            return uri;
        }

        internal static bool IsHostedTarget(string rawTarget)
        {
            var value = rawTarget?.Trim() ?? string.Empty;
            if (!value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\')) return false;
            if (!Uri.TryCreate("https://openclaw.invalid" + value, UriKind.Absolute, out var parsed)) return false;
            var path = parsed.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            path = "/" + path.TrimStart('/');
            return IsCanonicalPath(path) && (IsPath(path, "/__openclaw__/canvas") || IsPath(path, "/__openclaw__/a2ui"));
        }

        internal static Uri? ResolveHostedTarget(string? rawSurfaceUrl, string rawTarget)
        {
            if (!IsHostedTarget(rawTarget) || !TryParseCapabilitySurface(rawSurfaceUrl, out var surface)) return null;
            var target = new Uri("https://openclaw.invalid" + rawTarget.Trim());
            var basePath = surface!.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
            var targetPath = target.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            var builder = new UriBuilder(surface)
            {
                Path = basePath + "/" + targetPath.TrimStart('/'),
                Query = target.Query.TrimStart('?'),
                Fragment = target.Fragment.TrimStart('#'),
            };
            return builder.Uri;
        }

        internal static Uri? ResolveA2UiUrl(string? rawSurfaceUrl)
            => ResolveHostedTarget(rawSurfaceUrl, "/__openclaw__/a2ui/?platform=windows");

        private static bool TryParseCapabilitySurface(string? raw, out Uri? surface)
        {
            surface = null;
            if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var candidate) ||
                candidate.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(candidate.Host) ||
                !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment)) return false;
            var path = "/" + candidate.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/').TrimEnd('/');
            if (!IsCanonicalPath(path)) return false;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || segments[^3] != "__openclaw__" || segments[^2] != "cap" || string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segments[^1]))) return false;
            surface = candidate;
            return true;
        }

        private static bool IsCanonicalPath(string path)
        {
            var segments = path.Split('/', StringSplitOptions.None);
            if (segments.Length < 2 || segments[0].Length != 0) return false;
            for (var index = 1; index < segments.Length; index++)
            {
                if (segments[index].Length == 0 && index != segments.Length - 1) return false;
                var segment = segments[index];
                for (var pass = 0; pass < 8; pass++)
                {
                    string decoded;
                    try { decoded = Uri.UnescapeDataString(segment); }
                    catch { return false; }
                    if (decoded == segment) break;
                    segment = decoded;
                }
                if (segment is "." or ".." || segment.Contains('/') || segment.Contains('\\')) return false;
            }
            return true;
        }

        private static bool IsPath(string path, string prefix)
            => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

        private static bool IsAllowedCanvasScheme(Uri uri)
            => uri.Scheme is "http" or "https" or "file" or "about";

        private static bool IsCapabilityScopedA2UiUrl(Uri uri)
        {
            if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            var path = "/" + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
            var marker = path.IndexOf("/__openclaw__/cap/", StringComparison.Ordinal);
            if (marker < 0) return false;
            var suffix = path[(marker + "/__openclaw__/cap/".Length)..];
            var slash = suffix.IndexOf('/');
            if (slash <= 0) return false;
            var hosted = suffix[slash..];
            return IsCanonicalPath(hosted) && IsPath(hosted, "/__openclaw__/a2ui");
        }

        private static List<JsonElement> DecodeMessages(string? messagesJson, string? jsonl)
        {
            var result = new List<JsonElement>();
            if (!string.IsNullOrWhiteSpace(messagesJson))
            {
                if (Encoding.UTF8.GetByteCount(messagesJson) > MaximumA2UiBytes)
                    throw new InvalidOperationException("INVALID_REQUEST: A2UI messages exceed 1 MiB");
                using var document = JsonDocument.Parse(messagesJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("INVALID_REQUEST: canvas.a2ui.push messages must be an array");
                result.AddRange(document.RootElement.EnumerateArray().Select(value => value.Clone()));
            }
            else if (!string.IsNullOrWhiteSpace(jsonl))
            {
                if (Encoding.UTF8.GetByteCount(jsonl) > MaximumA2UiBytes)
                    throw new InvalidOperationException("INVALID_REQUEST: A2UI JSONL exceeds 1 MiB");
                foreach (var line in jsonl.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(line);
                    result.Add(document.RootElement.Clone());
                    if (result.Count > MaximumA2UiMessages)
                        throw new InvalidOperationException("INVALID_REQUEST: A2UI message limit exceeded");
                }
            }
            else throw new InvalidOperationException("INVALID_REQUEST: A2UI messages or jsonl required");
            if (result.Count > MaximumA2UiMessages)
                throw new InvalidOperationException("INVALID_REQUEST: A2UI message limit exceeded");
            return result;
        }

        private static string NormalizeJsonResult(string? result)
        {
            var json = string.IsNullOrWhiteSpace(result) ? "null" : result.Trim();
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "A2UI host returned invalid JSON" });
            }
        }

        private async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Canvas is only available on Windows");
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CanvasService));
                if (_thread == null)
                {
                    _thread = new Thread(RunUi) { IsBackground = true, Name = "OpenClaw Canvas" };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
#else
            await Task.CompletedTask;
            throw new PlatformNotSupportedException("Canvas is only available on Windows");
#endif
        }

#if WINDOWS
        private void RunUi()
        {
            try
            {
                _form = new Form
                {
                    Text = "OpenClaw Canvas",
                    Width = 1080,
                    Height = 760,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.CenterScreen,
                    AutoScaleMode = AutoScaleMode.Dpi,
                };
                _browser = new WebView2 { Dock = DockStyle.Fill };
                _form.Controls.Add(_browser);
                _ui = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(_ui);
                _form.Shown += InitializeBrowserAsync;
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex.InnerException ?? ex);
            }
        }

        private async void InitializeBrowserAsync(object? sender, EventArgs args)
        {
            try
            {
                var userData = Path.Combine(new SecureStore().BaseDirectory, "WebView2");
                Directory.CreateDirectory(userData);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
                await _browser!.EnsureCoreWebView2Async(environment);
                _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                _browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                _browser.CoreWebView2.NavigationStarting += (_, eventArgs) =>
                {
                    if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) || !IsAllowedCanvasScheme(target))
                    {
                        eventArgs.Cancel = true;
                        return;
                    }
                    if (_trustedA2UiUrl != null && !ExactUrlEquals(target, _trustedA2UiUrl)) _trustedA2UiUrl = null;
                };
                _browser.CoreWebView2.WebMessageReceived += HandleWebMessageReceived;
                await _browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ActionBridgeScript);
                SetDefaultDocumentUnsafe();
                _ready.TrySetResult(true);
                _form!.Hide();
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex.InnerException ?? ex);
                try { _form?.Close(); } catch { }
            }
        }

        private const string ActionBridgeScript = """
            (() => {
              if (globalThis.__openclawWindowsA2UIBridgeInstalled) return;
              globalThis.__openclawWindowsA2UIBridgeInstalled = true;
              globalThis.addEventListener('a2uiaction', (evt) => {
                try {
                  const payload = evt?.detail ?? evt?.payload ?? null;
                  if (!payload || payload.eventType !== 'a2ui.action') return;
                  const action = payload.action ?? null;
                  const name = action?.name ?? action?.action ?? '';
                  if (!name) return;
                  const context = Array.isArray(action?.context) ? action.context : [];
                  globalThis.chrome?.webview?.postMessage({
                    type: 'openclaw.a2ui.action',
                    userAction: {
                      id: action?.id ?? crypto.randomUUID(),
                      name,
                      surfaceId: payload.surfaceId ?? 'main',
                      sourceComponentId: payload.sourceComponentId ?? '',
                      dataContextPath: payload.dataContextPath ?? '',
                      timestamp: new Date().toISOString(),
                      ...(context.length ? {context} : {})
                    }
                  });
                } catch {}
              }, true);
            })();
            """;

        private async void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string? actionId = null;
            try
            {
                if (_trustedA2UiUrl == null || !Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
                    !ExactUrlEquals(source, _trustedA2UiUrl) || _browser?.Source == null || !ExactUrlEquals(_browser.Source, _trustedA2UiUrl)) return;
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                var root = message.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "openclaw.a2ui.action" ||
                    !root.TryGetProperty("userAction", out var action) || action.ValueKind != JsonValueKind.Object) return;
                var name = ReadNonEmptyString(action, "name") ?? ReadNonEmptyString(action, "action");
                if (name == null) return;
                actionId = ReadNonEmptyString(action, "id") ?? Guid.NewGuid().ToString();
                var surfaceId = ReadNonEmptyString(action, "surfaceId") ?? "main";
                var component = ReadNonEmptyString(action, "sourceComponentId") ?? "-";
                var context = action.TryGetProperty("context", out var contextValue) ? " ctx=" + contextValue.GetRawText() : string.Empty;
                var text = $"CANVAS_A2UI action={SanitizeTag(name)} session={SanitizeTag(_sessionKey)} " +
                           $"surface={SanitizeTag(surfaceId)} component={SanitizeTag(component)} " +
                           $"host={SanitizeTag(Environment.MachineName)} instance={SanitizeTag(_instanceId)}{context} default=update_canvas";
                if (_operatorClient?.IsConnected != true) throw new InvalidOperationException("operator connection unavailable");
                await _operatorClient.RequestAsync<JsonElement>("chat.send", new
                {
                    sessionKey = _sessionKey,
                    message = text,
                    deliver = false,
                    suppressCommandInterpretation = false,
                    idempotencyKey = actionId,
                }, CancellationToken.None, 30_000).ConfigureAwait(true);
                await DispatchActionStatusAsync(actionId, true, null).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (actionId != null) await DispatchActionStatusAsync(actionId, false, ex.Message).ConfigureAwait(true);
            }
        }

        private async Task DispatchActionStatusAsync(string actionId, bool ok, string? error)
        {
            var detail = JsonSerializer.Serialize(new { id = actionId, ok, error = error ?? string.Empty });
            var script = $"window.dispatchEvent(new CustomEvent('openclaw:a2ui-action-status', {{detail:{detail}}}))";
            await _browser!.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task<string?> ExecuteScriptTextAsync(string javaScript, CancellationToken cancellationToken)
        {
            var encoded = await InvokeAsync(async () => await _browser!.CoreWebView2.ExecuteScriptAsync(javaScript), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(encoded) || encoded == "null") return null;
            try
            {
                using var document = JsonDocument.Parse(encoded);
                return document.RootElement.ValueKind == JsonValueKind.String
                    ? document.RootElement.GetString()
                    : document.RootElement.GetRawText();
            }
            catch (JsonException) { return encoded; }
        }

        private void NavigateUnsafe(Uri uri, bool trustedA2UiActions)
        {
            if (!IsAllowedCanvasScheme(uri)) throw new InvalidOperationException("INVALID_REQUEST: unsupported canvas URL scheme");
            _trustedA2UiUrl = trustedA2UiActions && IsCapabilityScopedA2UiUrl(uri) ? uri : null;
            _browser!.CoreWebView2.Navigate(uri.AbsoluteUri);
        }

        private void SetDefaultDocumentUnsafe()
        {
            _trustedA2UiUrl = null;
            _browser!.NavigateToString("<!doctype html><html><head><meta charset='utf-8'><style>body{font:16px Segoe UI;background:#111;color:#eee;margin:0;padding:28px}h1{font-size:24px}</style></head><body><h1>OpenClaw Canvas</h1><p>Ready for canvas navigation or A2UI messages.</p></body></html>");
        }

        private void ApplyPlacementUnsafe(Placement placement)
        {
            var bounds = _form!.Bounds;
            var x = placement.X.HasValue ? (int)Math.Round(placement.X.Value) : bounds.X;
            var y = placement.Y.HasValue ? (int)Math.Round(placement.Y.Value) : bounds.Y;
            var width = placement.Width.HasValue ? (int)Math.Round(placement.Width.Value) : bounds.Width;
            var height = placement.Height.HasValue ? (int)Math.Round(placement.Height.Value) : bounds.Height;
            _form.SetBounds(x, y, Math.Max(320, width), Math.Max(240, height));
        }

        private Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui!.Post(_ =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex.InnerException ?? ex); }
            }, null);
            return completion.Task.WaitAsync(cancellationToken);
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui!.Post(async _ =>
            {
                try { completion.TrySetResult(await action().ConfigureAwait(true)); }
                catch (Exception ex) { completion.TrySetException(ex.InnerException ?? ex); }
            }, null);
            return completion.Task.WaitAsync(cancellationToken);
        }

        private static bool ExactUrlEquals(Uri left, Uri right)
            => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port && string.Equals(left.PathAndQuery, right.PathAndQuery, StringComparison.Ordinal);

        private static string? ReadNonEmptyString(JsonElement value, string name)
            => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!.Trim()
                : null;

        private static string SanitizeTag(string value)
        {
            var source = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace(' ', '_');
            return new string(source.Select(character => char.IsAsciiLetterOrDigit(character) || "_-.:".Contains(character) ? character : '_').ToArray());
        }
#endif

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
#if WINDOWS
            try { _ui?.Post(_ => { try { _form?.Close(); } catch { } }, null); } catch { }
#endif
            _a2uiGate.Dispose();
        }

#if WINDOWS
        private static TaskCompletionSource<bool> NewCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
    }
}
