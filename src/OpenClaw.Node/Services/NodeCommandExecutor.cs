using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;

namespace OpenClaw.Node.Services
{
    public class NodeCommandExecutor : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static string ToJson(object? value) => JsonSerializer.Serialize(value, JsonOptions);

        private readonly IGatewayRpcClient? _rpc;
        private readonly IScreenImageProvider _screen;
        private readonly IBrowserProxyService? _browserProxy;
        private readonly CompanionSettings _settings;
        private readonly Func<string, string, CancellationToken, Task>? _notificationSink;
        private readonly WindowsDeviceService _device = new();
        private readonly CameraCaptureService _camera = new();
        private readonly CanvasService _canvas;
        private readonly TalkPushToTalkService _talk;

        public NodeCommandExecutor(
            IGatewayRpcClient? rpc = null,
            IScreenImageProvider? screen = null,
            IBrowserProxyService? browserProxyService = null,
            CompanionSettings? settings = null,
            IGatewayRequestClient? operatorClient = null,
            string? instanceId = null,
            Func<string, string, CancellationToken, Task>? notificationSink = null)
        {
            _rpc = rpc;
            _screen = screen ?? new ScreenCaptureService();
            _browserProxy = browserProxyService;
            _settings = settings ?? new CompanionSettings();
            _notificationSink = notificationSink;
            _settings.Normalize();
            _canvas = new CanvasService(
                _screen,
                rpc as IPluginSurfaceClient,
                operatorClient,
                _settings.TalkSessionKey,
                instanceId);
            _talk = new TalkPushToTalkService(operatorClient, _settings.TalkSessionKey);
        }

        public async Task<BridgeInvokeResponse> ExecuteAsync(BridgeInvokeRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!NodeCapabilityRegistry.IsGatewayCommandEnabled(request.Command, _settings))
                {
                    return Invalid(request.Id, $"Unsupported or disabled gateway command: {request.Command}");
                }
                return request.Command switch
                {
                    "system.notify" => await HandleSystemNotifyAsync(request, cancellationToken),
                    "system.which" => await HandleSystemWhichAsync(request, cancellationToken),
                    "system.execApprovals.get" => HandleSystemExecApprovalsGet(request),
                    "system.execApprovals.set" => HandleSystemExecApprovalsSet(request),
                    "system.run.prepare" => HandleSystemRunPrepare(request),
                    "system.run" => await HandleSystemRunAsync(request, cancellationToken),
                    "fs.listDir" => HandleFsListDir(request),
                    "browser.proxy" => await HandleBrowserProxyAsync(request, cancellationToken),
                    "screen.snapshot" => await HandleScreenSnapshotAsync(request, cancellationToken),
                    "screen.record" => await HandleScreenRecordAsync(request, cancellationToken),
                    "camera.list" => await HandleCameraListAsync(request, cancellationToken),
                    "camera.snap" => await HandleCameraSnapAsync(request, cancellationToken),
                    "camera.clip" => await HandleCameraClipAsync(request, cancellationToken),
                    "location.get" => await HandleLocationGetAsync(request, cancellationToken),
                    "device.info" => Success(request.Id, _device.GetInfo()),
                    "device.status" => Success(request.Id, _device.GetStatus()),
                    "canvas.present" => await HandleCanvasPresentAsync(request, cancellationToken),
                    "canvas.hide" => await HandleCanvasHideAsync(request, cancellationToken),
                    "canvas.navigate" => await HandleCanvasNavigateAsync(request, cancellationToken),
                    "canvas.eval" => await HandleCanvasEvalAsync(request, cancellationToken),
                    "canvas.snapshot" => await HandleCanvasSnapshotAsync(request, cancellationToken),
                    "canvas.a2ui.push" => await HandleCanvasA2UiPushAsync(request, false, cancellationToken),
                    "canvas.a2ui.pushJSONL" => await HandleCanvasA2UiPushAsync(request, true, cancellationToken),
                    "canvas.a2ui.reset" => await HandleCanvasA2UiResetAsync(request, cancellationToken),
                    "talk.ptt.start" => Success(request.Id, await _talk.StartAsync(cancellationToken)),
                    "talk.ptt.stop" => Success(request.Id, await _talk.StopAsync(cancellationToken)),
                    "talk.ptt.cancel" => Success(request.Id, await _talk.CancelAsync()),
                    "talk.ptt.once" => Success(request.Id, await _talk.OnceAsync(cancellationToken)),
                    _ => new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.InvalidRequest,
                            Message = $"Unsupported command: {request.Command}"
                        }
                    }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = MapExceptionCode(ex),
                        Message = ex.Message
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleSystemNotifyAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "INVALID_REQUEST: expected JSON object with title/body");
            }

            if (!root.Value.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String ||
                !root.Value.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "INVALID_REQUEST: expected JSON object with title/body");
            }

            var title = (titleEl.GetString() ?? string.Empty).Trim();
            var body = (bodyEl.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            {
                return Invalid(request.Id, "INVALID_REQUEST: empty notification");
            }

            if (root.Value.TryGetProperty("sound", out var sound) && sound.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return Invalid(request.Id, "INVALID_REQUEST: system.notify sound must be a string");
            if (root.Value.TryGetProperty("priority", out var priority) &&
                (priority.ValueKind != JsonValueKind.String || priority.GetString() is not ("passive" or "active" or "timeSensitive")))
                return Invalid(request.Id, "INVALID_REQUEST: system.notify priority must be passive, active, or timeSensitive");
            if (root.Value.TryGetProperty("delivery", out var delivery) &&
                (delivery.ValueKind != JsonValueKind.String || delivery.GetString() is not ("system" or "overlay" or "auto")))
                return Invalid(request.Id, "INVALID_REQUEST: system.notify delivery must be system, overlay, or auto");
            if (root.Value.TryGetProperty("delivery", out delivery) && delivery.GetString() == "overlay")
                return Unavailable(request.Id, "NOTIFICATION_UNAVAILABLE: overlay delivery is not supported on Windows");

            if (_notificationSink == null)
                return Unavailable(request.Id, "NOTIFICATION_UNAVAILABLE: the Windows notification host is not active");
            await _notificationSink(title, body, cancellationToken).ConfigureAwait(false);
            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(new { ok = true })
            };
        }

        private async Task<BridgeInvokeResponse> HandleSystemWhichAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            var bins = ResolveSystemWhichBins(root);
            if (bins == null || bins.Length == 0)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.InvalidRequest,
                        Message = "INVALID_REQUEST: system.which requires a non-empty string array in params.bins"
                    }
                };
            }

            var whichProgram = OperatingSystem.IsWindows() ? "where" : "which";
            var pathsByBin = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var bin in bins)
            {
                var result = await RunProcessAsync(whichProgram, new[] { bin }, timeoutMs: 10_000, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var path = FirstNonEmptyLine(result.StdOut);
                var found = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(path);
                if (found)
                {
                    pathsByBin[bin] = path!;
                }
            }

            var payload = new { bins = pathsByBin };

            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(payload)
            };
        }

        private BridgeInvokeResponse HandleSystemExecApprovalsGet(BridgeInvokeRequest request)
        {
            try
            {
                var snapshot = ExecApprovalsStore.ReadSnapshot();
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(ExecApprovalsStore.ToPayload(snapshot))
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                            ? OpenClawNodeErrorCode.Unavailable
                            : OpenClawNodeErrorCode.InvalidRequest,
                        Message = ex.Message
                    }
                };
            }
        }

        private BridgeInvokeResponse HandleSystemExecApprovalsSet(BridgeInvokeRequest request)
        {
            try
            {
                var parsed = ExecApprovalsStore.DecodeSetParams(request.ParamsJSON);
                if (parsed.Rules == null)
                {
                    return Invalid(request.Id, "INVALID_REQUEST: exec approvals rules are required");
                }

                var snapshot = ExecApprovalsStore.Save(new ExecApprovalsStore.NativePolicy
                {
                    DefaultAction = parsed.DefaultAction,
                    Rules = parsed.Rules,
                }, parsed.BaseHash);
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(ExecApprovalsStore.ToPayload(snapshot))
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.InvalidRequest,
                        Message = ex.Message
                    }
                };
            }
        }

        private BridgeInvokeResponse HandleSystemRunPrepare(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "system.run.prepare requires params");
            }

            if (!TryResolveSystemRunCommand(root.Value, request.Id, out var fileName, out var args, out var commandText, out var commandPreview, out _, out var invalid))
            {
                return invalid!;
            }

            var cwd = root.Value.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? NormalizeNullableString(cwdEl.GetString())
                : null;
            var agentId = root.Value.TryGetProperty("agentId", out var agentIdEl) && agentIdEl.ValueKind == JsonValueKind.String
                ? NormalizeNullableString(agentIdEl.GetString())
                : null;
            var sessionKey = root.Value.TryGetProperty("sessionKey", out var sessionKeyEl) && sessionKeyEl.ValueKind == JsonValueKind.String
                ? NormalizeNullableString(sessionKeyEl.GetString())
                : null;

            var argv = new[] { fileName! }.Concat(args!).ToArray();
            var approval = ExecApprovalsStore.Evaluate(commandText!, argv);
            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(new
                {
                    plan = new
                    {
                        argv,
                        cwd,
                        commandText,
                        commandPreview,
                        agentId,
                        sessionKey,
                        mutableFileOperand = (object?)null,
                        nativeApproval = new
                        {
                            action = approval.Action,
                            shell = approval.Shell,
                            matchedRule = approval.Rule?.Pattern,
                        },
                    }
                })
            };
        }

        private async Task<BridgeInvokeResponse> HandleSystemRunAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "system.run requires params");
            }

            if (!TryResolveSystemRunCommand(root.Value, request.Id, out var fileName, out var args, out var commandText, out _, out var cwd, out var invalid))
            {
                return invalid!;
            }

            int? timeoutMs = null;
            if (root.Value.TryGetProperty("timeoutMs", out var timeoutEl))
            {
                if (timeoutEl.ValueKind != JsonValueKind.Number || !timeoutEl.TryGetInt32(out var parsedTimeout))
                {
                    return Invalid(request.Id, "system.run params.timeoutMs must be an integer");
                }

                if (parsedTimeout <= 0)
                {
                    return Invalid(request.Id, "system.run params.timeoutMs must be > 0");
                }

                timeoutMs = parsedTimeout;
            }

            var argv = new[] { fileName! }.Concat(args!).ToArray();
            if (!ValidateSystemRunPlan(root.Value, argv, commandText!, cwd, request.Id, out invalid))
            {
                return invalid!;
            }

            if (!TryReadSystemRunEnvironment(root.Value, request.Id, argv, out var environment, out invalid))
            {
                return invalid!;
            }

            var approval = ExecApprovalsStore.Evaluate(commandText!, argv);
            if (!string.Equals(approval.Action, ExecApprovalsStore.Allow, StringComparison.Ordinal))
            {
                var reason = approval.Action == ExecApprovalsStore.Prompt
                    ? "local approval is required but no interactive approval is active"
                    : "Windows execution policy denied the command";
                await SendExecEventBestEffortAsync("exec.denied", root.Value, commandText!, new { reason = "approval-required" }, cancellationToken);
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.SystemRunDenied,
                        Message = $"SYSTEM_RUN_DENIED: {reason}",
                        Retryable = approval.Action == ExecApprovalsStore.Prompt,
                    }
                };
            }

            var result = await RunProcessAsync(fileName!, args!, cwd, timeoutMs, cancellationToken, environment);
            cancellationToken.ThrowIfCancellationRequested();

            var payload = new
            {
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                success = result.ExitCode == 0 && !result.TimedOut,
                stdout = result.StdOut,
                stderr = result.StdErr,
                error = result.Error,
                truncated = result.Truncated,
            };

            await SendExecEventBestEffortAsync("exec.finished", root.Value, commandText!, new
            {
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                success = result.ExitCode == 0 && !result.TimedOut,
                output = string.Join(Environment.NewLine, new[] { result.StdOut, result.StdErr }.Where(value => !string.IsNullOrWhiteSpace(value))),
            }, cancellationToken);

            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(payload)
            };
        }

        private BridgeInvokeResponse HandleFsListDir(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            var requested = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (root.HasValue && root.Value.TryGetProperty("path", out var path))
            {
                if (path.ValueKind != JsonValueKind.String) return Invalid(request.Id, "INVALID_REQUEST: fs.listDir path must be a string");
                requested = path.GetString()?.Trim() ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(requested)) requested = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(requested)) return Invalid(request.Id, "INVALID_REQUEST: fs.listDir path must be absolute");
            var resolved = Path.GetFullPath(requested);
            if (!Directory.Exists(resolved)) return Invalid(request.Id, "INVALID_REQUEST: fs.listDir path is not a readable directory");
            var entries = new List<object>();
            foreach (var directory in Directory.EnumerateDirectories(resolved))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    var hidden = info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden);
                    entries.Add(new { name = info.Name, path = info.FullName, hidden = hidden ? true : (bool?)null });
                }
                catch { }
            }
            var sorted = entries
                .Select(value => JsonSerializer.SerializeToElement(value, JsonOptions))
                .OrderBy(value => value.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                .ThenBy(value => value.GetProperty("name").GetString(), StringComparer.Ordinal)
                .ToArray();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var parent = Directory.GetParent(resolved)?.FullName;
            return Success(request.Id, new { path = resolved, parent, home, entries = sorted });
        }

        private async Task<BridgeInvokeResponse> HandleScreenSnapshotAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ParseParams(request.ParamsJSON);
            var format = "jpeg";
            var screenIndex = 0;
            int? requestedScreenIndex = null;
            int? requestedMaxWidth = null;
            var quality = 0.72;
            if (root.HasValue)
            {
                if (root.Value.TryGetProperty("format", out var formatElement))
                {
                    if (formatElement.ValueKind != JsonValueKind.String) return Invalid(request.Id, "INVALID_REQUEST: screen.snapshot format must be a string");
                    format = (formatElement.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                    if (format == "jpg") format = "jpeg";
                    if (format is not ("jpeg" or "png")) return Invalid(request.Id, "INVALID_REQUEST: screen.snapshot format must be jpeg or png");
                }
                if (root.Value.TryGetProperty("screenIndex", out var index))
                {
                    if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out screenIndex) || screenIndex < 0)
                        return Invalid(request.Id, "INVALID_REQUEST: screen.snapshot screenIndex must be a non-negative integer");
                    requestedScreenIndex = screenIndex;
                }
                if (root.Value.TryGetProperty("maxWidth", out var width))
                {
                    if (width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var parsedMaxWidth) || parsedMaxWidth <= 0 || parsedMaxWidth > 8000)
                        return Invalid(request.Id, "INVALID_REQUEST: screen.snapshot maxWidth must be between 1 and 8000");
                    requestedMaxWidth = parsedMaxWidth;
                }
                if (root.Value.TryGetProperty("quality", out var qualityElement) && (qualityElement.ValueKind != JsonValueKind.Number || !qualityElement.TryGetDouble(out quality) || quality <= 0 || quality > 1))
                    return Invalid(request.Id, "INVALID_REQUEST: screen.snapshot quality must be in (0, 1]");
            }
            var maxWidth = requestedMaxWidth ?? (format == "png" ? 900 : 1600);
            var captured = await _screen.CaptureScreenshotBytesAsync(screenIndex, format == "jpeg" ? "jpg" : "png");
            cancellationToken.ThrowIfCancellationRequested();
            if (captured.bytes.Length == 0) return Unavailable(request.Id, "SCREEN_CAPTURE_UNAVAILABLE: capture returned no image");
            var base64 = Convert.ToBase64String(captured.bytes);
            var widthOut = captured.width;
            var heightOut = captured.height;
            var encoded = format == "jpeg"
                ? ImageEncoding.EncodeJpegBase64(captured.bytes, maxWidth, quality)
                : ImageEncoding.EncodePngBase64(captured.bytes, maxWidth);
            if (!string.IsNullOrWhiteSpace(encoded.Base64))
            {
                base64 = encoded.Base64;
                widthOut = encoded.Width;
                heightOut = encoded.Height;
            }
            return Success(request.Id, new
            {
                format,
                base64,
                displayFrameId = Guid.NewGuid().ToString(),
                width = widthOut,
                height = heightOut,
                screenIndex = requestedScreenIndex,
                capturedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        private async Task<BridgeInvokeResponse> HandleCameraClipAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            var durationMs = 3000;
            var facing = "front";
            string? deviceId = null;
            var includeAudio = true;
            if (root.HasValue)
            {
                if (root.Value.TryGetProperty("durationMs", out var duration) && (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out durationMs)))
                    return Invalid(request.Id, "INVALID_REQUEST: camera.clip durationMs must be an integer");
                if (root.Value.TryGetProperty("facing", out var facingElement))
                {
                    if (facingElement.ValueKind != JsonValueKind.String) return Invalid(request.Id, "INVALID_REQUEST: camera.clip facing must be a string");
                    facing = (facingElement.GetString() ?? "front").ToLowerInvariant();
                }
                if (root.Value.TryGetProperty("deviceId", out var device))
                {
                    if (device.ValueKind != JsonValueKind.String) return Invalid(request.Id, "INVALID_REQUEST: camera.clip deviceId must be a string");
                    deviceId = device.GetString();
                }
                if (root.Value.TryGetProperty("includeAudio", out var audio))
                {
                    if (audio.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid(request.Id, "INVALID_REQUEST: camera.clip includeAudio must be a boolean");
                    includeAudio = audio.GetBoolean();
                }
                if (root.Value.TryGetProperty("format", out var format) && (format.ValueKind != JsonValueKind.String || !string.Equals(format.GetString(), "mp4", StringComparison.OrdinalIgnoreCase)))
                    return Invalid(request.Id, "INVALID_REQUEST: camera.clip format must be mp4");
            }
            if (facing is not ("front" or "back")) return Invalid(request.Id, "INVALID_REQUEST: camera.clip facing must be front or back");
            if (durationMs is < 250 || durationMs > 1000 * _settings.MaximumCameraClipSeconds)
                return Invalid(request.Id, $"INVALID_REQUEST: camera.clip durationMs must be between 250 and {_settings.MaximumCameraClipSeconds * 1000}");
            var result = await _camera.CaptureMp4ClipAsBase64Async(durationMs, facing, deviceId, includeAudio, cancellationToken);
            return Success(request.Id, new { format = "mp4", base64 = result.Base64, durationMs = result.DurationMs, hasAudio = result.HasAudio });
        }

        private async Task<BridgeInvokeResponse> HandleLocationGetAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var json = await _device.GetLocationJsonAsync(request.ParamsJSON, cancellationToken);
            return new BridgeInvokeResponse { Id = request.Id, Ok = true, PayloadJSON = json };
        }

        private async Task<BridgeInvokeResponse> HandleCanvasPresentAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            var url = root.HasValue && root.Value.TryGetProperty("url", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            CanvasService.Placement? placement = null;
            if (root.HasValue && root.Value.TryGetProperty("placement", out var placementElement))
            {
                if (placementElement.ValueKind != JsonValueKind.Object)
                    return Invalid(request.Id, "INVALID_REQUEST: canvas.present placement must be an object");
                if (!TryReadOptionalFiniteDouble(placementElement, "x", out var x) ||
                    !TryReadOptionalFiniteDouble(placementElement, "y", out var y) ||
                    !TryReadOptionalFiniteDouble(placementElement, "width", out var width) ||
                    !TryReadOptionalFiniteDouble(placementElement, "height", out var height))
                    return Invalid(request.Id, "INVALID_REQUEST: canvas.present placement values must be finite numbers");
                if (width is <= 0 or > 8192 || height is <= 0 or > 8192)
                    return Invalid(request.Id, "INVALID_REQUEST: canvas.present placement size must be in (0, 8192]");
                placement = new CanvasService.Placement(x, y, width, height);
            }
            await _canvas.PresentAsync(url, placement, cancellationToken);
            return Success(request.Id, new { ok = true });
        }

        private async Task<BridgeInvokeResponse> HandleCanvasHideAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            await _canvas.HideAsync(cancellationToken);
            return Success(request.Id, new { ok = true });
        }

        private async Task<BridgeInvokeResponse> HandleCanvasNavigateAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            if (!root.HasValue || !root.Value.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString()))
                return Invalid(request.Id, "INVALID_REQUEST: canvas.navigate requires url");
            await _canvas.NavigateAsync(url.GetString()!, cancellationToken);
            return Success(request.Id, new { ok = true });
        }

        private async Task<BridgeInvokeResponse> HandleCanvasEvalAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            if (!root.HasValue || !root.Value.TryGetProperty("javaScript", out var script) || script.ValueKind != JsonValueKind.String)
                return Invalid(request.Id, "INVALID_REQUEST: canvas.eval requires javaScript");
            var result = await _canvas.EvaluateAsync(script.GetString() ?? string.Empty, cancellationToken);
            return Success(request.Id, new { result });
        }

        private async Task<BridgeInvokeResponse> HandleCanvasSnapshotAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            var format = root.HasValue && root.Value.TryGetProperty("format", out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "jpeg").ToLowerInvariant()
                : "jpeg";
            if (format == "jpg") format = "jpeg";
            if (format is not ("jpeg" or "png")) return Invalid(request.Id, "INVALID_REQUEST: canvas.snapshot format must be jpeg or png");
            var maxWidth = format == "png" ? 900 : 1600;
            var quality = 0.9;
            if (root.HasValue && root.Value.TryGetProperty("maxWidth", out var maxWidthElement) &&
                (maxWidthElement.ValueKind != JsonValueKind.Number || !maxWidthElement.TryGetInt32(out maxWidth) || maxWidth <= 0 || maxWidth > 8000))
                return Invalid(request.Id, "INVALID_REQUEST: canvas.snapshot maxWidth must be between 1 and 8000");
            if (root.HasValue && root.Value.TryGetProperty("quality", out var qualityElement) &&
                (qualityElement.ValueKind != JsonValueKind.Number || !qualityElement.TryGetDouble(out quality) || !double.IsFinite(quality) || quality <= 0 || quality > 1))
                return Invalid(request.Id, "INVALID_REQUEST: canvas.snapshot quality must be in (0, 1]");
            var snapshot = await _canvas.SnapshotAsync(format == "jpeg" ? "jpg" : "png", cancellationToken);
            var encoded = format == "jpeg"
                ? ImageEncoding.EncodeJpegBase64(snapshot.Bytes, maxWidth, quality)
                : ImageEncoding.EncodePngBase64(snapshot.Bytes, maxWidth);
            var base64 = string.IsNullOrWhiteSpace(encoded.Base64) ? Convert.ToBase64String(snapshot.Bytes) : encoded.Base64;
            return Success(request.Id, new { format, base64 });
        }

        private async Task<BridgeInvokeResponse> HandleCanvasA2UiPushAsync(BridgeInvokeRequest request, bool preferJsonl, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);
            if (!root.HasValue) return Invalid(request.Id, "INVALID_REQUEST: A2UI payload required");
            var messages = root.Value.TryGetProperty("messages", out var values) ? values.GetRawText() : null;
            var jsonl = root.Value.TryGetProperty("jsonl", out var lines) && lines.ValueKind == JsonValueKind.String ? lines.GetString() : null;
            if (preferJsonl && string.IsNullOrWhiteSpace(jsonl)) return Invalid(request.Id, "INVALID_REQUEST: canvas.a2ui.pushJSONL requires jsonl");
            var result = await _canvas.PushA2UiAsync(messages, jsonl, cancellationToken);
            return new BridgeInvokeResponse { Id = request.Id, Ok = true, PayloadJSON = result };
        }

        private async Task<BridgeInvokeResponse> HandleCanvasA2UiResetAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var result = await _canvas.ResetA2UiAsync(cancellationToken);
            return new BridgeInvokeResponse { Id = request.Id, Ok = true, PayloadJSON = result };
        }

        private async Task<BridgeInvokeResponse> HandleBrowserProxyAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            if (_browserProxy == null)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = "browser.proxy is not configured on this node"
                    }
                };
            }

            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "browser.proxy requires params");
            }

            if (!root.Value.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "browser.proxy requires params.path");
            }

            if (root.Value.TryGetProperty("method", out var methodEl) && methodEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "browser.proxy params.method must be a string");
            }

            if (root.Value.TryGetProperty("profile", out var profileEl) && profileEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "browser.proxy params.profile must be a string");
            }

            if (root.Value.TryGetProperty("timeoutMs", out var timeoutEl))
            {
                if (timeoutEl.ValueKind != JsonValueKind.Number || !timeoutEl.TryGetInt32(out var parsedTimeout))
                {
                    return Invalid(request.Id, "browser.proxy params.timeoutMs must be a 32-bit integer");
                }
                if (parsedTimeout <= 0)
                {
                    return Invalid(request.Id, "browser.proxy params.timeoutMs must be > 0");
                }
            }

            if (root.Value.TryGetProperty("query", out var queryEl) && queryEl.ValueKind != JsonValueKind.Object)
            {
                return Invalid(request.Id, "browser.proxy params.query must be an object");
            }

            var proxyRequest = new BrowserProxyRequest
            {
                Method = root.Value.TryGetProperty("method", out methodEl) ? (methodEl.GetString() ?? "GET") : "GET",
                Path = pathEl.GetString() ?? "/",
                Profile = root.Value.TryGetProperty("profile", out profileEl) ? profileEl.GetString() : null,
                TimeoutMs = root.Value.TryGetProperty("timeoutMs", out timeoutEl) && timeoutEl.TryGetInt32(out var parsed) ? parsed : 20000,
                Query = ReadQuery(root.Value),
                Body = root.Value.TryGetProperty("body", out var bodyEl) ? bodyEl.Clone() : null,
            };

            var payloadJson = await _browserProxy.ProxyAsync(proxyRequest, cancellationToken);
            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = payloadJson,
            };
        }

        private async Task<BridgeInvokeResponse> HandleScreenCaptureAsync(BridgeInvokeRequest request)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = "screen.capture is only available on Windows"
                    }
                };
            }

            var root = ParseParams(request.ParamsJSON);

            // Clean contract: reject deprecated/legacy params.
            var legacyParams = new[] { "path", "handle", "route", "sendToAgent", "deliver" };
            if (root != null)
            {
                foreach (var legacy in legacyParams)
                {
                    if (root.Value.TryGetProperty(legacy, out _))
                    {
                        return Invalid(request.Id, $"screen.capture params.{legacy} is no longer supported");
                    }
                }
            }

            if (root != null && root.Value.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.mode must be a string");
            }
            if (root != null && root.Value.TryGetProperty("screenIndex", out var screenEl) && screenEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.capture params.screenIndex must be a number");
            }
            if (root != null && root.Value.TryGetProperty("windowHandle", out var whEl) && whEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.capture params.windowHandle must be a number");
            }
            if (root != null && root.Value.TryGetProperty("format", out var fmtEl) && fmtEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.format must be a string");
            }
            if (root != null && root.Value.TryGetProperty("message", out var msgEl) && msgEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.message must be a string");
            }
            if (root != null && root.Value.TryGetProperty("sessionKey", out var skEl) && skEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.sessionKey must be a string");
            }
            if (root != null && root.Value.TryGetProperty("channel", out var chEl) && chEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.channel must be a string");
            }
            if (root != null && root.Value.TryGetProperty("to", out var toEl) && toEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.to must be a string");
            }
            if (root != null && root.Value.TryGetProperty("outputPath", out var opEl) && opEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.capture params.outputPath must be a string");
            }
            if (root != null && root.Value.TryGetProperty("maxWidth", out var mwEl) && mwEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.capture params.maxWidth must be a number");
            }
            if (root != null && root.Value.TryGetProperty("quality", out var qEl) && qEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.capture params.quality must be a number");
            }
            if (root != null && root.Value.TryGetProperty("maxInlineBytes", out var mibEl) && mibEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.capture params.maxInlineBytes must be a number");
            }

            var mode = root != null && root.Value.TryGetProperty("mode", out var modeVal) && modeVal.ValueKind == JsonValueKind.String
                ? (modeVal.GetString() ?? "deliver").Trim().ToLowerInvariant()
                : "deliver";

            if (mode != "deliver" && mode != "file" && mode != "data")
            {
                return Invalid(request.Id, "screen.capture params.mode must be one of: deliver, file, data");
            }

            var screenIndex = 0;
            if (root != null && root.Value.TryGetProperty("screenIndex", out var sIdx) && sIdx.ValueKind == JsonValueKind.Number)
            {
                if (!sIdx.TryGetInt32(out screenIndex))
                {
                    return Invalid(request.Id, "screen.capture params.screenIndex must be a 32-bit integer");
                }

                if (screenIndex < 0)
                {
                    return Invalid(request.Id, "screen.capture params.screenIndex must be >= 0");
                }
            }

            var format = root != null && root.Value.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
                ? (fmt.GetString() ?? "png").Trim().ToLowerInvariant()
                : "png";
            if (format != "png" && format != "jpg" && format != "jpeg")
            {
                return Invalid(request.Id, "screen.capture params.format must be one of: png, jpg, jpeg");
            }

            var outputPath = root != null && root.Value.TryGetProperty("outputPath", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                ? (pathEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            long windowHandle = 0;
            if (root != null && root.Value.TryGetProperty("windowHandle", out var wh) && wh.ValueKind == JsonValueKind.Number)
            {
                windowHandle = wh.TryGetInt64(out var v) ? v : (long)wh.GetDouble();
            }

            var sessionKey = root != null && root.Value.TryGetProperty("sessionKey", out var sk) && sk.ValueKind == JsonValueKind.String
                ? (sk.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var message = root != null && root.Value.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? (msg.GetString() ?? "Desktop screenshot")
                : "Desktop screenshot";
            var channel = root != null && root.Value.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                ? (ch.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var to = root != null && root.Value.TryGetProperty("to", out var toVal) && toVal.ValueKind == JsonValueKind.String
                ? (toVal.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var maxWidth = 1600;
            if (root != null && root.Value.TryGetProperty("maxWidth", out var mw) && mw.ValueKind == JsonValueKind.Number)
            {
                if (!mw.TryGetInt32(out maxWidth))
                {
                    return Invalid(request.Id, "screen.capture params.maxWidth must be a 32-bit integer");
                }
            }
            var quality = root != null && root.Value.TryGetProperty("quality", out var qualityEl) && qualityEl.ValueKind == JsonValueKind.Number
                ? qualityEl.GetDouble()
                : 0.85;
            var maxInlineBytes = 1_500_000;
            if (root != null && root.Value.TryGetProperty("maxInlineBytes", out var maxInlineEl) && maxInlineEl.ValueKind == JsonValueKind.Number)
            {
                if (!maxInlineEl.TryGetInt32(out maxInlineBytes))
                {
                    return Invalid(request.Id, "screen.capture params.maxInlineBytes must be a 32-bit integer");
                }
            }

            if (maxWidth <= 0)
            {
                return Invalid(request.Id, "screen.capture params.maxWidth must be > 0");
            }
            if (quality <= 0 || quality > 1)
            {
                return Invalid(request.Id, "screen.capture params.quality must be in range (0, 1]");
            }
            if (maxInlineBytes <= 0)
            {
                return Invalid(request.Id, "screen.capture params.maxInlineBytes must be > 0");
            }

            if (mode == "deliver")
            {
                if (_rpc == null)
                {
                    return Invalid(request.Id, "screen.capture mode=deliver requires gateway RPC client");
                }
                if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(to))
                {
                    return Invalid(request.Id, "screen.capture mode=deliver requires params.channel and params.to");
                }
            }

            if (mode == "file" && string.IsNullOrWhiteSpace(outputPath))
            {
                return Invalid(request.Id, "screen.capture mode=file requires params.outputPath");
            }

            try
            {
                var source = windowHandle != 0 ? "window" : "screen";
                (byte[] bytes, int width, int height) raw;
                if (windowHandle != 0)
                {
                    raw = await _screen.CaptureWindowBytesAsync(windowHandle, format);
                }
                else
                {
                    raw = await _screen.CaptureScreenshotBytesAsync(screenIndex, format);
                }

                if (raw.bytes.Length == 0)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "screen.capture failed"
                        }
                    };
                }

                if (mode == "deliver")
                {
                    var encoded = ImageEncoding.EncodeJpegBase64(raw.bytes, maxWidth, quality);
                    if (string.IsNullOrWhiteSpace(encoded.Base64))
                    {
                        return new BridgeInvokeResponse
                        {
                            Id = request.Id,
                            Ok = false,
                            Error = new OpenClawNodeError
                            {
                                Code = OpenClawNodeErrorCode.Unavailable,
                                Message = "screen.capture encode failed"
                            }
                        };
                    }

                    var agentRequest = new
                    {
                        message,
                        sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey,
                        deliver = true,
                        channel,
                        to,
                        attachments = new[]
                        {
                            new
                            {
                                mimeType = encoded.MimeType,
                                fileName = $"screenshot.{encoded.Format}",
                                content = encoded.Base64
                            }
                        }
                    };

                    await _rpc!.SendRequestAsync(
                        method: "node.event",
                        @params: new { @event = "agent.request", payload = agentRequest },
                        cancellationToken: CancellationToken.None);

                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        PayloadJSON = ToJson(new
                        {
                            ok = true,
                            mode,
                            target = new
                            {
                                source,
                                screenIndex,
                                windowHandle = windowHandle == 0 ? (long?)null : windowHandle
                            },
                            capture = new { format, bytes = raw.bytes.Length, width = raw.width, height = raw.height },
                            attachment = new { format = encoded.Format, mimeType = encoded.MimeType, bytes = encoded.Bytes, width = encoded.Width, height = encoded.Height },
                            delivery = new
                            {
                                @event = "agent.request",
                                channel,
                                to,
                                sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey
                            }
                        })
                    };
                }

                if (mode == "file")
                {
                    var effectivePath = outputPath;
                    var ext = Path.GetExtension(effectivePath);
                    if (string.IsNullOrWhiteSpace(ext))
                    {
                        effectivePath += "." + (format == "jpeg" ? "jpg" : format);
                    }

                    byte[] outputBytes;
                    string outputFormat;
                    string mimeType;
                    int outWidth;
                    int outHeight;

                    if (format == "jpg" || format == "jpeg")
                    {
                        var encoded = ImageEncoding.EncodeJpegBase64(raw.bytes, maxWidth, quality);
                        if (string.IsNullOrWhiteSpace(encoded.Base64))
                        {
                            return new BridgeInvokeResponse
                            {
                                Id = request.Id,
                                Ok = false,
                                Error = new OpenClawNodeError
                                {
                                    Code = OpenClawNodeErrorCode.Unavailable,
                                    Message = "screen.capture encode failed"
                                }
                            };
                        }

                        outputBytes = Convert.FromBase64String(encoded.Base64);
                        outputFormat = encoded.Format;
                        mimeType = encoded.MimeType;
                        outWidth = encoded.Width;
                        outHeight = encoded.Height;
                    }
                    else
                    {
                        outputBytes = raw.bytes;
                        outputFormat = "png";
                        mimeType = "image/png";
                        outWidth = raw.width;
                        outHeight = raw.height;
                    }

                    var parent = Path.GetDirectoryName(effectivePath);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    await File.WriteAllBytesAsync(effectivePath, outputBytes);

                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        PayloadJSON = ToJson(new
                        {
                            ok = true,
                            mode,
                            target = new
                            {
                                source,
                                screenIndex,
                                windowHandle = windowHandle == 0 ? (long?)null : windowHandle
                            },
                            capture = new { format, bytes = raw.bytes.Length, width = raw.width, height = raw.height },
                            file = new { path = effectivePath, format = outputFormat, mimeType, bytes = outputBytes.Length, width = outWidth, height = outHeight }
                        })
                    };
                }

                {
                    var encoded = ImageEncoding.EncodeJpegBase64(raw.bytes, maxWidth, quality);
                    if (string.IsNullOrWhiteSpace(encoded.Base64))
                    {
                        return new BridgeInvokeResponse
                        {
                            Id = request.Id,
                            Ok = false,
                            Error = new OpenClawNodeError
                            {
                                Code = OpenClawNodeErrorCode.Unavailable,
                                Message = "screen.capture encode failed"
                            }
                        };
                    }

                    if (encoded.Bytes > maxInlineBytes)
                    {
                        return Invalid(request.Id, "screen.capture mode=data exceeds maxInlineBytes; use mode=deliver or mode=file");
                    }

                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        PayloadJSON = ToJson(new
                        {
                            ok = true,
                            mode,
                            target = new
                            {
                                source,
                                screenIndex,
                                windowHandle = windowHandle == 0 ? (long?)null : windowHandle
                            },
                            capture = new { format, bytes = raw.bytes.Length, width = raw.width, height = raw.height },
                            inline = new { format = encoded.Format, mimeType = encoded.MimeType, bytes = encoded.Bytes, width = encoded.Width, height = encoded.Height, base64 = encoded.Base64 }
                        })
                    };
                }
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"screen.capture failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleDevScreenshotAsync(BridgeInvokeRequest request)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = "screen.capture is only available on Windows"
                    }
                };
            }

            var root = ParseParams(request.ParamsJSON);
            var outPath = root != null && root.Value.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String
                ? (pEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(outPath))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                outPath = Path.Combine(home, "Pictures", "OpenClaw", "dev-screenshot-latest.jpg");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory());
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.InvalidRequest,
                        Message = $"Invalid screenshot path: {ex.Message}"
                    }
                };
            }

            var ps = "$ErrorActionPreference='Stop'; " +
                     "Add-Type -AssemblyName System.Windows.Forms; " +
                     "Add-Type -AssemblyName System.Drawing; " +
                     "$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
                     "$bmp=New-Object System.Drawing.Bitmap($b.Width,$b.Height); " +
                     "$g=[System.Drawing.Graphics]::FromImage($bmp); " +
                     "$g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); " +
                     "$bmp.Save('" + outPath.Replace("'", "''") + "',[System.Drawing.Imaging.ImageFormat]::Jpeg); " +
                     "$g.Dispose(); $bmp.Dispose(); " +
                     "Write-Output 'OK'";

            var capture = await RunProcessAsync("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps });
            if (capture.ExitCode != 0 || !File.Exists(outPath))
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = "screen.capture failed"
                    },
                    PayloadJSON = ToJson(new
                    {
                        ok = false,
                        path = outPath,
                        exitCode = capture.ExitCode,
                        stdout = capture.StdOut,
                        stderr = capture.StdErr
                    })
                };
            }

            var automation = new AutomationService();
            var windows = await automation.ListWindowsAsync();
            var focused = windows.FirstOrDefault(w => w.IsFocused);

            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(new
                {
                    ok = true,
                    path = outPath,
                    focusedTitle = focused?.Title,
                    focusedProcess = focused?.Process
                })
            };
        }

        
        private async Task<BridgeInvokeResponse> HandleScreenListAsync(BridgeInvokeRequest request)
        {
            ScreenCaptureService.ScreenDisplayInfo[] displays;
            try
            {
                var svc = new ScreenCaptureService();
                displays = await svc.ListDisplaysAsync();
            }
            catch
            {
                // Keep command resilient in mixed environments; expose empty list instead of hard error.
                displays = Array.Empty<ScreenCaptureService.ScreenDisplayInfo>();
            }

            var payload = new
            {
                displays
            };

            return new BridgeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                PayloadJSON = ToJson(payload)
            };
        }

        private async Task<BridgeInvokeResponse> HandleScreenRecordAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);

            if (root != null && root.Value.TryGetProperty("durationMs", out var durationEl) && durationEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.record params.durationMs must be a number");
            }

            if (root != null && root.Value.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.record params.fps must be a number");
            }

            if (root != null && root.Value.TryGetProperty("format", out var formatEl) &&
                (formatEl.ValueKind != JsonValueKind.String || !string.Equals(formatEl.GetString(), "mp4", StringComparison.OrdinalIgnoreCase)))
            {
                return Invalid(request.Id, "INVALID_REQUEST: screen.record format must be mp4");
            }

            if (root != null && root.Value.TryGetProperty("includeAudio", out var audioEl) &&
                audioEl.ValueKind != JsonValueKind.True && audioEl.ValueKind != JsonValueKind.False)
            {
                return Invalid(request.Id, "screen.record params.includeAudio must be a boolean");
            }

            if (root != null && root.Value.TryGetProperty("screenIndex", out var screenEl) && screenEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "screen.record params.screenIndex must be a number");
            }

            if (root != null && root.Value.TryGetProperty("captureApi", out var apiEl) && apiEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "screen.record params.captureApi must be a string");
            }

            if (root != null && root.Value.TryGetProperty("lowLatency", out var lowEl) &&
                lowEl.ValueKind != JsonValueKind.True && lowEl.ValueKind != JsonValueKind.False)
            {
                return Invalid(request.Id, "screen.record params.lowLatency must be a boolean");
            }

            var durationMs = 10000;
            if (root != null && root.Value.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number)
            {
                if (!d.TryGetInt32(out durationMs))
                {
                    return Invalid(request.Id, "screen.record params.durationMs must be a 32-bit integer");
                }

                if (durationMs <= 0)
                {
                    return Invalid(request.Id, "screen.record params.durationMs must be > 0");
                }

                if (durationMs > _settings.MaximumScreenRecordSeconds * 1000)
                {
                    return Invalid(request.Id, $"screen.record duration exceeds configured maximum of {_settings.MaximumScreenRecordSeconds} seconds");
                }
            }

            var requestedFps = 10d;
            if (root != null && root.Value.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number)
            {
                if (!f.TryGetDouble(out requestedFps) || !double.IsFinite(requestedFps))
                {
                    return Invalid(request.Id, "screen.record params.fps must be a finite number");
                }

                if (requestedFps <= 0 || requestedFps > 60)
                {
                    return Invalid(request.Id, "screen.record params.fps must be in (0, 60]");
                }
            }
            var captureFps = Math.Clamp((int)Math.Round(requestedFps, MidpointRounding.AwayFromZero), 1, 60);

            var includeAudio = root != null && root.Value.TryGetProperty("includeAudio", out var a) &&
                               (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)
                ? a.GetBoolean()
                : true;

            var screenIndex = 0;
            if (root != null && root.Value.TryGetProperty("screenIndex", out var sIdx) && sIdx.ValueKind == JsonValueKind.Number)
            {
                if (!sIdx.TryGetInt32(out screenIndex))
                {
                    return Invalid(request.Id, "screen.record params.screenIndex must be a 32-bit integer");
                }

                if (screenIndex < 0)
                {
                    return Invalid(request.Id, "screen.record params.screenIndex must be >= 0");
                }
            }

            var captureApi = root != null && root.Value.TryGetProperty("captureApi", out var api) && api.ValueKind == JsonValueKind.String
                ? (api.GetString() ?? "auto")
                : "auto";

            var lowLatency = root != null && root.Value.TryGetProperty("lowLatency", out var ll) &&
                             (ll.ValueKind == JsonValueKind.True || ll.ValueKind == JsonValueKind.False)
                ? ll.GetBoolean()
                : false;

            try
            {
                var svc = new ScreenCaptureService();
                var record = await svc.RecordScreenAsBase64Async(durationMs, captureFps, includeAudio, screenIndex, captureApi, lowLatency, cancellationToken);

                var payload = new
                {
                    format = "mp4",
                    base64 = record.Base64,
                    durationMs,
                    fps = requestedFps,
                    screenIndex,
                    hasAudio = includeAudio,
                    captureApi = record.CaptureApi,
                    hardwareEncoding = record.HardwareEncoding,
                    lowLatency = record.LowLatency
                };

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(payload)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Screen recording failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleCameraListAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var devices = await _camera.ListDevicesAsync(cancellationToken);

                var payload = new
                {
                    devices
                };

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(payload)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep command resilient in mixed environments; expose empty list instead of hard error.
                var payload = new { devices = Array.Empty<CameraCaptureService.CameraDeviceInfo>() };
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(payload)
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleCameraSnapAsync(BridgeInvokeRequest request, CancellationToken cancellationToken)
        {
            var root = ParseParams(request.ParamsJSON);

            if (root.HasValue && root.Value.TryGetProperty("facing", out var facingValue) && facingValue.ValueKind != JsonValueKind.String)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap facing must be a string");
            if (root.HasValue && root.Value.TryGetProperty("format", out var formatValue) && formatValue.ValueKind != JsonValueKind.String)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap format must be a string");
            if (root.HasValue && root.Value.TryGetProperty("maxWidth", out var maxWidthValue) && maxWidthValue.ValueKind != JsonValueKind.Number)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap maxWidth must be an integer");
            if (root.HasValue && root.Value.TryGetProperty("quality", out var qualityValue) && qualityValue.ValueKind != JsonValueKind.Number)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap quality must be a number");
            if (root.HasValue && root.Value.TryGetProperty("delayMs", out var delayValue) && delayValue.ValueKind != JsonValueKind.Number)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap delayMs must be an integer");
            if (root.HasValue && root.Value.TryGetProperty("deviceId", out var deviceValue) && deviceValue.ValueKind != JsonValueKind.String)
                return Invalid(request.Id, "INVALID_REQUEST: camera.snap deviceId must be a string");

            var facing = root != null && root.Value.TryGetProperty("facing", out var f) && f.ValueKind == JsonValueKind.String
                ? (f.GetString() ?? "front")
                : "front";

            if (!string.Equals(facing, "front", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(facing, "back", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(request.Id, "camera.snap params.facing must be 'front' or 'back'");
            }

            var outputFormat = "jpg";
            if (root != null && root.Value.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.String)
            {
                outputFormat = (formatEl.GetString() ?? "jpg").Trim().ToLowerInvariant();
                if (outputFormat is not ("jpg" or "jpeg"))
                {
                    return Invalid(request.Id, "camera.snap params.format must be 'jpg' or 'jpeg'");
                }
            }

            int? maxWidth = null;
            if (root != null && root.Value.TryGetProperty("maxWidth", out var w) && w.ValueKind == JsonValueKind.Number)
            {
                if (!w.TryGetInt32(out var parsedMaxWidth))
                {
                    return Invalid(request.Id, "camera.snap params.maxWidth must be a 32-bit integer");
                }

                maxWidth = parsedMaxWidth;
                if (maxWidth.Value <= 0 || maxWidth.Value > 8000)
                {
                    return Invalid(request.Id, "camera.snap params.maxWidth must be between 1 and 8000");
                }
            }

            var quality = root != null && root.Value.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Number
                ? q.GetDouble()
                : (double?)null;

            if (quality.HasValue && (!double.IsFinite(quality.Value) || quality.Value < 0 || quality.Value > 1))
            {
                return Invalid(request.Id, "camera.snap params.quality must be between 0 and 1");
            }

            int? delayMs = 2000;
            if (root != null && root.Value.TryGetProperty("delayMs", out var d) && d.ValueKind == JsonValueKind.Number)
            {
                if (!d.TryGetInt32(out var parsedDelayMs))
                {
                    return Invalid(request.Id, "camera.snap params.delayMs must be a 32-bit integer");
                }

                delayMs = parsedDelayMs;
                if (delayMs.Value < 0)
                {
                    return Invalid(request.Id, "camera.snap params.delayMs must be >= 0");
                }
            }

            var deviceId = root != null && root.Value.TryGetProperty("deviceId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;

            maxWidth ??= 1600;
            quality ??= 0.9;

            try
            {
                var (base64, width, height) = await _camera.CaptureJpegAsBase64Async(facing.ToLowerInvariant(), maxWidth, quality, delayMs, deviceId, cancellationToken);

                if (OperatingSystem.IsWindows() && width <= 1 && height <= 1)
                {
                    var reason = string.IsNullOrWhiteSpace(_camera.LastError) ?
                        "Camera capture unavailable. Check Windows Settings > Privacy & security > Camera, enable 'Camera access' and 'Let desktop apps access your camera'." :
                        $"Camera capture unavailable: {_camera.LastError}. Check Windows Settings > Privacy & security > Camera and enable desktop app camera access.";

                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = reason
                        }
                    };
                }

                var payload = new
                {
                    format = outputFormat,
                    base64,
                    width,
                    height
                };

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(payload)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Camera snap failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleWindowListAsync(BridgeInvokeRequest request)
        {
            try
            {
                var svc = new AutomationService();
                var windows = await svc.ListWindowsAsync();
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { windows })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Window list failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleWindowFocusAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            long? handle = null;
            if (root != null && root.Value.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number)
            {
                if (!h.TryGetInt64(out var parsedHandle))
                {
                    return Invalid(request.Id, "window.focus params.handle must be a 64-bit integer");
                }

                handle = parsedHandle;
            }
            var titleContains = root != null && root.Value.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
            {
                return Invalid(request.Id, "window.focus requires params.handle or params.titleContains");
            }

            try
            {
                var svc = new AutomationService();
                var focused = await svc.FocusWindowAsync(handle, titleContains);
                if (!focused)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Unable to focus requested window"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Window focus failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleWindowRectAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            long? handle = null;
            if (root != null && root.Value.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number)
            {
                if (!h.TryGetInt64(out var parsedHandle))
                {
                    return Invalid(request.Id, "window.rect params.handle must be a 64-bit integer");
                }

                handle = parsedHandle;
            }
            var titleContains = root != null && root.Value.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
            {
                return Invalid(request.Id, "window.rect requires params.handle or params.titleContains");
            }

            try
            {
                var svc = new AutomationService();
                var rect = await svc.GetWindowRectAsync(handle, titleContains);
                if (rect == null)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Unable to resolve requested window rect"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { rect })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Window rect failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleInputTypeAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            var text = root != null && root.Value.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (string.IsNullOrEmpty(text))
            {
                return Invalid(request.Id, "input.type requires params.text");
            }

            try
            {
                var svc = new AutomationService();
                var ok = await svc.TypeTextAsync(text);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Typing input failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Typing input failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleInputKeyAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            var key = root != null && root.Value.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(key))
            {
                return Invalid(request.Id, "input.key requires params.key");
            }

            try
            {
                var svc = new AutomationService();
                var ok = await svc.SendKeyAsync(key);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Sending key input failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Sending key input failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleInputClickAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "input.click requires params.x and params.y");
            }

            if (!root.Value.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "input.click requires numeric params.x");
            }

            if (!root.Value.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "input.click requires numeric params.y");
            }

            if (!xEl.TryGetInt32(out var x) || !yEl.TryGetInt32(out var y))
            {
                return Invalid(request.Id, "input.click params.x and params.y must be integers");
            }
            var button = root.Value.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.String
                ? (bEl.GetString() ?? "primary")
                : "primary";

            if (!string.Equals(button, "left", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "primary", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "secondary", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(request.Id, "input.click params.button must be 'primary', 'secondary', 'left', or 'right'");
            }

            var doubleClick = root.Value.TryGetProperty("doubleClick", out var dEl) &&
                              (dEl.ValueKind == JsonValueKind.True || dEl.ValueKind == JsonValueKind.False)
                ? dEl.GetBoolean()
                : false;

            try
            {
                var svc = new AutomationService();
                var ok = await svc.ClickAsync(x, y, button.ToLowerInvariant(), doubleClick);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Mouse click failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true, x, y, button = button.ToLowerInvariant(), doubleClick })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Mouse click failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleInputScrollAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "input.scroll requires params.deltaY");
            }

            if (!root.Value.TryGetProperty("deltaY", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "input.scroll requires numeric params.deltaY");
            }

            if (!deltaEl.TryGetInt32(out var deltaY))
            {
                return Invalid(request.Id, "input.scroll params.deltaY must be a 32-bit integer");
            }

            if (deltaY == 0)
            {
                return Invalid(request.Id, "input.scroll params.deltaY must be non-zero");
            }

            int? x = null;
            int? y = null;

            if (root.Value.TryGetProperty("x", out var xEl))
            {
                if (xEl.ValueKind != JsonValueKind.Number)
                {
                    return Invalid(request.Id, "input.scroll params.x must be numeric when provided");
                }

                if (!xEl.TryGetInt32(out var parsedX))
                {
                    return Invalid(request.Id, "input.scroll params.x must be a 32-bit integer when provided");
                }

                x = parsedX;
            }

            if (root.Value.TryGetProperty("y", out var yEl))
            {
                if (yEl.ValueKind != JsonValueKind.Number)
                {
                    return Invalid(request.Id, "input.scroll params.y must be numeric when provided");
                }

                if (!yEl.TryGetInt32(out var parsedY))
                {
                    return Invalid(request.Id, "input.scroll params.y must be a 32-bit integer when provided");
                }

                y = parsedY;
            }

            if (x.HasValue ^ y.HasValue)
            {
                return Invalid(request.Id, "input.scroll requires both params.x and params.y when targeting coordinates");
            }

            try
            {
                var svc = new AutomationService();
                var ok = await svc.ScrollAsync(deltaY, x, y);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Mouse scroll failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true, deltaY, x, y })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Mouse scroll failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleInputClickRelativeAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "input.click.relative requires params.offsetX and params.offsetY");
            }

            var handle = root.Value.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.Number
                ? h.GetInt64()
                : (long?)null;
            var titleContains = root.Value.TryGetProperty("titleContains", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
            {
                return Invalid(request.Id, "input.click.relative requires params.handle or params.titleContains");
            }

            if (!root.Value.TryGetProperty("offsetX", out var oxEl) || oxEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "input.click.relative requires numeric params.offsetX");
            }

            if (!root.Value.TryGetProperty("offsetY", out var oyEl) || oyEl.ValueKind != JsonValueKind.Number)
            {
                return Invalid(request.Id, "input.click.relative requires numeric params.offsetY");
            }

            if (!oxEl.TryGetInt32(out var offsetX) || !oyEl.TryGetInt32(out var offsetY))
            {
                return Invalid(request.Id, "input.click.relative params.offsetX and params.offsetY must be 32-bit integers");
            }
            var button = root.Value.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.String
                ? (bEl.GetString() ?? "primary")
                : "primary";

            if (!string.Equals(button, "left", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "primary", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "secondary", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(request.Id, "input.click.relative params.button must be 'primary', 'secondary', 'left', or 'right'");
            }

            var doubleClick = root.Value.TryGetProperty("doubleClick", out var dEl) &&
                              (dEl.ValueKind == JsonValueKind.True || dEl.ValueKind == JsonValueKind.False)
                ? dEl.GetBoolean()
                : false;

            try
            {
                var svc = new AutomationService();
                var ok = await svc.ClickRelativeToWindowAsync(handle, titleContains, offsetX, offsetY, button.ToLowerInvariant(), doubleClick);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "Relative click failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true, offsetX, offsetY, button = button.ToLowerInvariant(), doubleClick })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"Relative click failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleUiFindAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "ui.find requires params");
            }

            if (!TryParseUiSelectorParams(request.Id, root.Value, out var handle, out var titleContains, out var name, out var automationId, out var controlType, out var timeoutMs, out var invalid))
            {
                return invalid!;
            }

            try
            {
                var svc = new AutomationService();
                var find = await svc.FindUiElementDetailedAsync(handle, titleContains, name, automationId, controlType, timeoutMs);
                if (!find.Found || find.Element == null)
                {
                    var details = BuildUiSelectorDebugDetails(handle, titleContains, name, automationId, controlType, timeoutMs, find.Reason, find.Strategy);
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI element not found"
                        },
                        PayloadJSON = ToJson(new { ok = false, details })
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { element = find.Element, strategy = find.Strategy })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"UI find failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleUiClickAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "ui.click requires params");
            }

            if (!TryParseUiSelectorParams(request.Id, root.Value, out var handle, out var titleContains, out var name, out var automationId, out var controlType, out var timeoutMs, out var invalid))
            {
                return invalid!;
            }

            var button = root.Value.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.String
                ? (bEl.GetString() ?? "primary")
                : "primary";

            if (!string.Equals(button, "left", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "primary", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "secondary", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(request.Id, "ui.click params.button must be 'primary', 'secondary', 'left', or 'right'");
            }

            var doubleClick = root.Value.TryGetProperty("doubleClick", out var dEl) &&
                              (dEl.ValueKind == JsonValueKind.True || dEl.ValueKind == JsonValueKind.False)
                ? dEl.GetBoolean()
                : false;

            try
            {
                var svc = new AutomationService();
                var find = await svc.FindUiElementDetailedAsync(handle, titleContains, name, automationId, controlType, timeoutMs);
                if (!find.Found || find.Element == null)
                {
                    var details = BuildUiSelectorDebugDetails(handle, titleContains, name, automationId, controlType, timeoutMs, find.Reason, find.Strategy);
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI click failed: element not found"
                        },
                        PayloadJSON = ToJson(new { ok = false, details })
                    };
                }

                var ok = await svc.ClickAsync(find.Element.CenterX, find.Element.CenterY, button.ToLowerInvariant(), doubleClick);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI click failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true, button = button.ToLowerInvariant(), doubleClick, strategy = find.Strategy, x = find.Element.CenterX, y = find.Element.CenterY })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"UI click failed: {ex.Message}"
                    }
                };
            }
        }

        private async Task<BridgeInvokeResponse> HandleUiTypeAsync(BridgeInvokeRequest request)
        {
            var root = ParseParams(request.ParamsJSON);
            if (root == null)
            {
                return Invalid(request.Id, "ui.type requires params");
            }

            if (!TryParseUiSelectorParams(request.Id, root.Value, out var handle, out var titleContains, out var name, out var automationId, out var controlType, out var timeoutMs, out var invalid))
            {
                return invalid!;
            }

            if (!root.Value.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            {
                return Invalid(request.Id, "ui.type requires params.text");
            }

            var text = textEl.GetString();
            if (string.IsNullOrEmpty(text))
            {
                return Invalid(request.Id, "ui.type requires params.text");
            }

            try
            {
                var svc = new AutomationService();
                var find = await svc.FindUiElementDetailedAsync(handle, titleContains, name, automationId, controlType, timeoutMs);
                if (!find.Found || find.Element == null)
                {
                    var details = BuildUiSelectorDebugDetails(handle, titleContains, name, automationId, controlType, timeoutMs, find.Reason, find.Strategy);
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI type failed: element not found"
                        },
                        PayloadJSON = ToJson(new { ok = false, details })
                    };
                }

                var clicked = await svc.ClickAsync(find.Element.CenterX, find.Element.CenterY, "primary", false);
                if (!clicked)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI type failed: unable to focus element"
                        }
                    };
                }

                await Task.Delay(50);
                var ok = await svc.TypeTextAsync(text);
                if (!ok)
                {
                    return new BridgeInvokeResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = new OpenClawNodeError
                        {
                            Code = OpenClawNodeErrorCode.Unavailable,
                            Message = "UI type failed"
                        }
                    };
                }

                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    PayloadJSON = ToJson(new { ok = true, strategy = find.Strategy, x = find.Element.CenterX, y = find.Element.CenterY })
                };
            }
            catch (Exception ex)
            {
                return new BridgeInvokeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Error = new OpenClawNodeError
                    {
                        Code = OpenClawNodeErrorCode.Unavailable,
                        Message = $"UI type failed: {ex.Message}"
                    }
                };
            }
        }

        private bool TryParseUiSelectorParams(
            string requestId,
            JsonElement root,
            out long? handle,
            out string? titleContains,
            out string? name,
            out string? automationId,
            out string? controlType,
            out int timeoutMs,
            out BridgeInvokeResponse? invalid)
        {
            invalid = null;
            handle = root.TryGetProperty("handle", out var hEl) && hEl.ValueKind == JsonValueKind.Number
                ? hEl.GetInt64()
                : (long?)null;
            titleContains = root.TryGetProperty("titleContains", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()
                : null;

            name = root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;
            automationId = root.TryGetProperty("automationId", out var aEl) && aEl.ValueKind == JsonValueKind.String
                ? aEl.GetString()
                : null;
            controlType = root.TryGetProperty("controlType", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString()
                : null;

            timeoutMs = 1500;
            if (root.TryGetProperty("timeoutMs", out var tmEl) && tmEl.ValueKind == JsonValueKind.Number)
            {
                if (!tmEl.TryGetInt32(out timeoutMs))
                {
                    invalid = Invalid(requestId, "ui.* params.timeoutMs must be a 32-bit integer");
                    return false;
                }
            }

            if ((!handle.HasValue || handle.Value == 0) && string.IsNullOrWhiteSpace(titleContains))
            {
                invalid = Invalid(requestId, "ui.* requires params.handle or params.titleContains");
                return false;
            }

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(controlType))
            {
                invalid = Invalid(requestId, "ui.* requires at least one selector: params.name, params.automationId, or params.controlType");
                return false;
            }

            if (timeoutMs <= 0)
            {
                invalid = Invalid(requestId, "ui.* params.timeoutMs must be > 0");
                return false;
            }

            return true;
        }

        private static object BuildUiSelectorDebugDetails(
            long? handle,
            string? titleContains,
            string? name,
            string? automationId,
            string? controlType,
            int timeoutMs,
            string? reason,
            string? strategy)
            => new
            {
                handle,
                titleContains,
                selectors = new
                {
                    name,
                    automationId,
                    controlType,
                },
                timeoutMs,
                reason = string.IsNullOrWhiteSpace(reason) ? "not-found" : reason,
                strategy = strategy ?? string.Empty,
            };

        private static string[]? ResolveSystemWhichBins(JsonElement? root)
        {
            if (root == null) return null;

            if (root.Value.TryGetProperty("bins", out var binsEl))
            {
                if (binsEl.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                var values = binsEl.EnumerateArray().ToArray();
                if (values.Length is 0 or > 64 || values.Any(value => value.ValueKind != JsonValueKind.String)) return Array.Empty<string>();
                var bins = values.Select(value => (value.GetString() ?? string.Empty).Trim()).ToArray();
                return bins.Any(value => value.Length is 0 or > 260) ? Array.Empty<string>() : bins;
            }

            return null;
        }

        private static bool TryResolveSystemRunCommand(
            JsonElement root,
            string requestId,
            out string? fileName,
            out string[]? args,
            out string? commandText,
            out string? commandPreview,
            out string? cwd,
            out BridgeInvokeResponse? invalid)
        {
            invalid = null;
            fileName = null;
            args = null;
            commandText = null;
            commandPreview = null;
            cwd = root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? NormalizeNullableString(cwdEl.GetString())
                : null;
            var rawCommand = root.TryGetProperty("rawCommand", out var rawCommandEl) && rawCommandEl.ValueKind == JsonValueKind.String
                ? NormalizeNullableString(rawCommandEl.GetString())
                : null;

            if (!root.TryGetProperty("command", out var commandEl))
            {
                invalid = Invalid(requestId, "system.run requires params.command");
                return false;
            }

            if (commandEl.ValueKind == JsonValueKind.Array)
            {
                var parts = commandEl.EnumerateArray().ToArray();
                if (parts.Length == 0)
                {
                    invalid = Invalid(requestId, "system.run command array cannot be empty");
                    return false;
                }

                if (parts.Any(part => part.ValueKind != JsonValueKind.String))
                {
                    invalid = Invalid(requestId, "system.run params.command array entries must be strings");
                    return false;
                }

                fileName = (parts[0].GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    invalid = Invalid(requestId, "system.run command array cannot be empty");
                    return false;
                }

                args = parts.Skip(1).Select(part => part.GetString() ?? string.Empty).ToArray();
                return TryBuildSystemRunDisplay(requestId, fileName, args, rawCommand, true, out commandText, out commandPreview, out invalid);
            }

            invalid = Invalid(requestId, "system.run params.command must be a non-empty string array");
            return false;
        }

        private static bool TryBuildSystemRunDisplay(
            string requestId,
            string? fileName,
            string[]? args,
            string? rawCommand,
            bool allowLegacyShellText,
            out string? commandText,
            out string? commandPreview,
            out BridgeInvokeResponse? invalid)
        {
            invalid = null;
            commandText = null;
            commandPreview = null;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                invalid = Invalid(requestId, "system.run command array cannot be empty");
                return false;
            }

            var argv = new[] { fileName! }.Concat(args ?? Array.Empty<string>()).ToArray();
            commandText = FormatExecCommand(argv);
            commandPreview = ExtractShellCommandPreview(argv);

            if (!string.IsNullOrWhiteSpace(rawCommand))
            {
                var trimmedRaw = rawCommand!.Trim();
                var matchesCanonical = string.Equals(trimmedRaw, commandText, StringComparison.Ordinal);
                var matchesLegacyShellText = allowLegacyShellText &&
                    !string.IsNullOrWhiteSpace(commandPreview) &&
                    string.Equals(trimmedRaw, commandPreview, StringComparison.Ordinal);
                if (!matchesCanonical && !matchesLegacyShellText)
                {
                    invalid = Invalid(requestId, "INVALID_REQUEST: rawCommand does not match command");
                    return false;
                }

                if (matchesCanonical)
                {
                    commandPreview = null;
                }
            }

            return true;
        }

        private static string? NormalizeNullableString(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private static string? FirstNonEmptyLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string FormatExecCommand(IEnumerable<string> argv)
            => string.Join(" ", argv.Select(FormatExecToken));

        private static string? ExtractShellCommandPreview(string[] argv)
        {
            if (argv.Length == 0) return null;

            var executable = System.IO.Path.GetFileNameWithoutExtension((argv[0] ?? string.Empty).Trim()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(executable)) return null;

            if (executable == "cmd")
            {
                for (var i = 1; i < argv.Length; i++)
                {
                    var token = (argv[i] ?? string.Empty).Trim();
                    if (string.Equals(token, "/c", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token, "/k", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= argv.Length) return null;
                        if (argv.Skip(i + 2).Any(part => !string.IsNullOrWhiteSpace(part))) return null;
                        return NormalizeNullableString(argv[i + 1]);
                    }
                }
                return null;
            }

            var isPosixShell = executable is "sh" or "bash" or "zsh" or "dash" or "fish" or "ksh" or "ash";
            var isPowerShell = executable is "powershell" or "pwsh";
            if (!isPosixShell && !isPowerShell)
            {
                return null;
            }

            var inlineFlags = isPowerShell
                ? new[] { "-c", "-command", "--command", "-f", "-file", "-enc", "-encodedcommand" }
                : new[] { "-c", "-lc", "--command" };

            for (var i = 1; i < argv.Length; i++)
            {
                var token = (argv[i] ?? string.Empty).Trim();
                if (!inlineFlags.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
                if (i + 1 >= argv.Length) return null;
                if (argv.Skip(i + 2).Any(part => !string.IsNullOrWhiteSpace(part))) return null;
                return NormalizeNullableString(argv[i + 1]);
            }

            return null;
        }

        private static string FormatExecToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"")}\""
                : value;
        }

        private static BridgeInvokeResponse Invalid(string id, string message) => new()
        {
            Id = id,
            Ok = false,
            Error = new OpenClawNodeError
            {
                Code = OpenClawNodeErrorCode.InvalidRequest,
                Message = message
            }
        };

        private static BridgeInvokeResponse Unavailable(string id, string message) => new()
        {
            Id = id,
            Ok = false,
            Error = new OpenClawNodeError { Code = OpenClawNodeErrorCode.Unavailable, Message = message },
        };

        private static BridgeInvokeResponse Success(string id, object? payload) => new()
        {
            Id = id,
            Ok = true,
            PayloadJSON = ToJson(payload),
        };

        private static Dictionary<string, string>? ReadQuery(JsonElement root)
        {
            if (!root.TryGetProperty("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in queryEl.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        dict[prop.Name] = prop.Value.ToString();
                        break;
                }
            }

            return dict.Count == 0 ? null : dict;
        }

        private static bool TryReadOptionalFiniteDouble(JsonElement root, string name, out double? value)
        {
            value = null;
            if (!root.TryGetProperty(name, out var property)) return true;
            if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) return false;
            value = parsed;
            return true;
        }

        private static JsonElement? ParseParams(string? paramsJson)
        {
            if (string.IsNullOrWhiteSpace(paramsJson)) return null;
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("INVALID_REQUEST: params must be a JSON object");
            return doc.RootElement.Clone();
        }

        private static bool ValidateSystemRunPlan(
            JsonElement root,
            string[] argv,
            string commandText,
            string? cwd,
            string requestId,
            out BridgeInvokeResponse? invalid)
        {
            invalid = null;
            if (!root.TryGetProperty("systemRunPlan", out var plan) || plan.ValueKind == JsonValueKind.Null) return true;
            if (plan.ValueKind != JsonValueKind.Object)
            {
                invalid = Invalid(requestId, "INVALID_REQUEST: systemRunPlan must be an object");
                return false;
            }
            if (!plan.TryGetProperty("argv", out var plannedArgv) || plannedArgv.ValueKind != JsonValueKind.Array)
            {
                invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: execution plan is missing argv binding");
                return false;
            }
            var planned = plannedArgv.EnumerateArray().ToArray();
            if (planned.Length != argv.Length || planned.Where((entry, index) => entry.ValueKind != JsonValueKind.String || entry.GetString() != argv[index]).Any())
            {
                invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: command changed after preparation");
                return false;
            }
            if (plan.TryGetProperty("commandText", out var plannedText) &&
                (plannedText.ValueKind != JsonValueKind.String || plannedText.GetString() != commandText))
            {
                invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: command text changed after preparation");
                return false;
            }
            if (plan.TryGetProperty("cwd", out var plannedCwd))
            {
                var value = plannedCwd.ValueKind == JsonValueKind.String ? NormalizeNullableString(plannedCwd.GetString()) : null;
                if (!string.Equals(value, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: working directory changed after preparation");
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadSystemRunEnvironment(
            JsonElement root,
            string requestId,
            string[] argv,
            out Dictionary<string, string>? environment,
            out BridgeInvokeResponse? invalid)
        {
            environment = null;
            invalid = null;
            if (!root.TryGetProperty("env", out var env) || env.ValueKind == JsonValueKind.Null) return true;
            if (env.ValueKind != JsonValueKind.Object)
            {
                invalid = Invalid(requestId, "INVALID_REQUEST: system.run params.env must be an object of strings");
                return false;
            }
            var entries = env.EnumerateObject().ToArray();
            if (entries.Length > 128)
            {
                invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: too many environment overrides");
                return false;
            }
            var shell = Path.GetFileNameWithoutExtension(argv[0]).ToLowerInvariant();
            if (entries.Length > 0 && shell is "cmd" or "powershell" or "pwsh")
            {
                invalid = Invalid(requestId, "SYSTEM_RUN_DENIED: environment overrides are not allowed for shell wrappers");
                return false;
            }
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PATH", "PATHEXT", "COMSPEC", "PSMODULEPATH", "SYSTEMROOT", "WINDIR",
            };
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!IsPortableEnvironmentName(entry.Name) || blocked.Contains(entry.Name) || entry.Name.StartsWith("OPENCLAW_", StringComparison.OrdinalIgnoreCase))
                {
                    invalid = Invalid(requestId, $"SYSTEM_RUN_DENIED: environment override rejected ({entry.Name})");
                    return false;
                }
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    invalid = Invalid(requestId, $"INVALID_REQUEST: environment override {entry.Name} must be a string");
                    return false;
                }
                var value = entry.Value.GetString() ?? string.Empty;
                if (value.Length > 32 * 1024)
                {
                    invalid = Invalid(requestId, $"SYSTEM_RUN_DENIED: environment override {entry.Name} is too large");
                    return false;
                }
                parsed[entry.Name] = value;
            }
            environment = parsed;
            return true;
        }

        private static bool IsPortableEnvironmentName(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
            return name.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        }

        private async Task SendExecEventBestEffortAsync(
            string eventName,
            JsonElement root,
            string commandText,
            object details,
            CancellationToken cancellationToken)
        {
            if (_rpc == null) return;
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["sessionKey"] = root.TryGetProperty("sessionKey", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : string.Empty,
                    ["runId"] = root.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String ? run.GetString() : Guid.NewGuid().ToString("N"),
                    ["host"] = "node",
                    ["command"] = commandText,
                    ["suppressNotifyOnExit"] = root.TryGetProperty("suppressNotifyOnExit", out var suppress) && suppress.ValueKind is JsonValueKind.True or JsonValueKind.False && suppress.GetBoolean(),
                };
                foreach (var property in JsonSerializer.SerializeToElement(details, JsonOptions).EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
                await _rpc.SendRequestAsync("node.event", new
                {
                    @event = eventName,
                    payloadJSON = ToJson(payload),
                }, cancellationToken);
            }
            catch
            {
                // Exec completion is authoritative in node.invoke.result; the
                // lifecycle event is best-effort, matching the Gateway host.
            }
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            string[] args,
            string? workingDirectory = null,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            ChildProcessSecurity.ScrubSensitiveEnvironment(psi);
            if (environment != null)
            {
                foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var outputBudget = new OutputBudget(200_000);
            var stdOutTask = ReadCappedAsync(process.StandardOutput, outputBudget);
            var stdErrTask = ReadCappedAsync(process.StandardError, outputBudget);

            var timedOut = false;
            var cancelled = false;
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (timeoutMs.HasValue) waitCts.CancelAfter(timeoutMs.Value);
                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = !cancellationToken.IsCancellationRequested;
                    cancelled = cancellationToken.IsCancellationRequested;
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // best effort
                    }

                    try
                    {
                        await process.WaitForExitAsync();
                    }
                    catch
                    {
                        // best effort
                    }
                }
            }

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return new ProcessResult
            {
                ExitCode = timedOut || cancelled ? -1 : process.ExitCode,
                StdOut = stdOut.Text,
                StdErr = stdErr.Text,
                TimedOut = timedOut,
                Error = cancelled ? "cancelled" : null,
                Truncated = stdOut.Truncated || stdErr.Truncated,
            };
        }

        private static Task<ProcessResult> RunProcessAsync(string fileName, params string[] args)
            => RunProcessAsync(fileName, args, null, null);

        private class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public bool TimedOut { get; set; }
            public string? Error { get; set; }
            public bool Truncated { get; set; }
        }

        private sealed class OutputBudget
        {
            public OutputBudget(int remaining) => Remaining = remaining;
            public int Remaining;
        }

        private sealed record CappedOutput(string Text, bool Truncated);

        private static async Task<CappedOutput> ReadCappedAsync(TextReader reader, OutputBudget budget)
        {
            var builder = new System.Text.StringBuilder();
            var buffer = new char[4096];
            var truncated = false;
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (count == 0) break;
                var allowed = 0;
                while (true)
                {
                    var remaining = Volatile.Read(ref budget.Remaining);
                    if (remaining <= 0) break;
                    var requested = Math.Min(remaining, count);
                    if (Interlocked.CompareExchange(ref budget.Remaining, remaining - requested, remaining) == remaining)
                    {
                        allowed = requested;
                        break;
                    }
                }
                if (allowed > 0) builder.Append(buffer, 0, allowed);
                if (allowed < count) truncated = true;
            }
            return new CappedOutput(builder.ToString(), truncated);
        }

        private static OpenClawNodeErrorCode MapExceptionCode(Exception exception)
        {
            var message = exception.Message ?? string.Empty;
            if (exception is JsonException || message.StartsWith("INVALID_REQUEST", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.InvalidRequest;
            if (message.StartsWith("SYSTEM_RUN_DENIED", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.SystemRunDenied;
            if (message.StartsWith("MIC_PERMISSION_REQUIRED", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.MicPermissionRequired;
            if (message.StartsWith("MIC_BUSY", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.MicBusy;
            if (message.StartsWith("PTT_BUSY", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.PttBusy;
            if (message.StartsWith("NODE_BACKGROUND_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
                return OpenClawNodeErrorCode.BackgroundUnavailable;
            if (exception is TimeoutException or OperationCanceledException)
                return OpenClawNodeErrorCode.Timeout;
            return OpenClawNodeErrorCode.Unavailable;
        }

        public void Dispose()
        {
            _talk.Dispose();
            _canvas.Dispose();
        }
    }
}
