using System;
using System.IO;
using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class NodeCommandExecutorTests
    {
        [Fact]
        public async Task SystemWhich_ShouldReturnPath_ForKnownCommand()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "1",
                Command = "system.which",
                ParamsJSON = JsonSerializer.Serialize(new { bins = new[] { "dotnet" } })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("bins", out var bins));
            Assert.Equal(JsonValueKind.Object, bins.ValueKind);
            Assert.True(bins.TryGetProperty("dotnet", out var path));
            Assert.Equal(JsonValueKind.String, path.ValueKind);
            Assert.False(root.TryGetProperty("paths", out _));
            Assert.False(root.TryGetProperty("missing", out _));
        }

        [Fact]
        public async Task SystemRunPrepare_ShouldReturnPlan_ForCommandArray()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "run-prepare-1",
                Command = "system.run.prepare",
                ParamsJSON = JsonSerializer.Serialize(new { command = new[] { "cmd.exe", "/c", "echo", "hi" }, cwd = "C:/tmp", agentId = "main", sessionKey = "session:test" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var plan = doc.RootElement.GetProperty("plan");
            var argv = plan.GetProperty("argv").EnumerateArray().ToArray();
            Assert.Equal("cmd.exe", argv[0].GetString());
            Assert.Equal("C:/tmp", plan.GetProperty("cwd").GetString());
            Assert.Equal("main", plan.GetProperty("agentId").GetString());
            Assert.Equal("session:test", plan.GetProperty("sessionKey").GetString());
        }

        [Fact]
        public async Task SystemRunPrepare_ShouldUseGatewayStyleCommandFormatting_ForQuotedWindowsArgs()
        {
            var executor = new NodeCommandExecutor();
            var ps = "Get-ChildItem 'C:\\Users\\david\\AppData\\Roaming\\Thunderbird' | Format-Table -AutoSize";
            var req = new BridgeInvokeRequest
            {
                Id = "run-prepare-quoted-1",
                Command = "system.run.prepare",
                ParamsJSON = JsonSerializer.Serialize(new
                {
                    command = new[] { "powershell", "-NoProfile", "-Command", ps },
                    rawCommand = $"powershell -NoProfile -Command \"{ps}\""
                })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var plan = doc.RootElement.GetProperty("plan");
            Assert.Equal(
                "powershell -NoProfile -Command \"Get-ChildItem 'C:\\Users\\david\\AppData\\Roaming\\Thunderbird' | Format-Table -AutoSize\"",
                plan.GetProperty("commandText").GetString());
            Assert.True(plan.GetProperty("commandPreview").ValueKind == JsonValueKind.Null);
        }

        [Fact]
        public async Task SystemRunPrepare_ShouldRejectMismatchedRawCommand()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "run-prepare-mismatch-1",
                Command = "system.run.prepare",
                ParamsJSON = JsonSerializer.Serialize(new
                {
                    command = new[] { "cmd.exe", "/d", "/s", "/c", "echo hi" },
                    rawCommand = "echo bye"
                })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("rawCommand does not match command", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SystemExecApprovalsGet_ShouldReturnSnapshot()
        {
            await WithTempOpenClawHome(async () =>
            {
                var executor = new NodeCommandExecutor();
                var req = new BridgeInvokeRequest
                {
                    Id = "exec-approvals-get-1",
                    Command = "system.execApprovals.get",
                    ParamsJSON = "{}"
                };

                var res = await executor.ExecuteAsync(req);

                Assert.True(res.Ok);
                Assert.NotNull(res.PayloadJSON);
                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal("deny", root.GetProperty("defaultAction").GetString());
                Assert.Empty(root.GetProperty("rules").EnumerateArray());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("hash").GetString()));
                Assert.True(root.GetProperty("constraints").GetProperty("baseHashRequired").GetBoolean());
                Assert.False(root.GetProperty("constraints").GetProperty("defaultAllowAllowed").GetBoolean());
            });
        }

        [Fact]
        public async Task SystemExecApprovalsSet_ShouldRequireMatchingBaseHash_AndPersist()
        {
            await WithTempOpenClawHome(async () =>
            {
                var executor = new NodeCommandExecutor();

                var firstGet = await executor.ExecuteAsync(new BridgeInvokeRequest
                {
                    Id = "exec-approvals-get-2",
                    Command = "system.execApprovals.get",
                    ParamsJSON = "{}"
                });

                using var getDoc = JsonDocument.Parse(firstGet.PayloadJSON!);
                var baseHash = getDoc.RootElement.GetProperty("hash").GetString();

                var setReq = new BridgeInvokeRequest
                {
                    Id = "exec-approvals-set-1",
                    Command = "system.execApprovals.set",
                    ParamsJSON = JsonSerializer.Serialize(new
                    {
                        baseHash,
                        defaultAction = "deny",
                        rules = new[] { new { pattern = "dotnet", action = "allow", shells = new[] { "direct" } } }
                    })
                };

                var setRes = await executor.ExecuteAsync(setReq);
                Assert.True(setRes.Ok);
                Assert.NotNull(setRes.PayloadJSON);

                using var setDoc = JsonDocument.Parse(setRes.PayloadJSON!);
                var root = setDoc.RootElement;
                Assert.Equal("deny", root.GetProperty("defaultAction").GetString());
                var rules = root.GetProperty("rules").EnumerateArray().ToArray();
                Assert.Single(rules);
                Assert.Equal("dotnet", rules[0].GetProperty("pattern").GetString());
                Assert.Equal("allow", rules[0].GetProperty("action").GetString());

                var staleSet = await executor.ExecuteAsync(new BridgeInvokeRequest
                {
                    Id = "exec-approvals-set-2",
                    Command = "system.execApprovals.set",
                    ParamsJSON = JsonSerializer.Serialize(new
                    {
                        baseHash = "deadbeef",
                        rules = Array.Empty<object>()
                    })
                });

                Assert.False(staleSet.Ok);
                Assert.NotNull(staleSet.Error);
                Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, staleSet.Error!.Code);
                Assert.Contains("changed", staleSet.Error.Message, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public async Task ScreenList_ShouldNotBeGatewayInvokable()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-list-1",
                Command = "screen.list"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error?.Code);
        }

        [Fact]
        public async Task ScreenRecord_ShouldReturnExpectedResult_ForCurrentPlatform()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-1",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { durationMs = 500, fps = 8, includeAudio = false, screenIndex = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            if (OperatingSystem.IsWindows())
            {
                Assert.True(res.Ok);
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("mp4", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
                Assert.Equal(500, root.GetProperty("durationMs").GetInt32());
                Assert.Equal(8, root.GetProperty("fps").GetInt32());
                Assert.Equal(0, root.GetProperty("screenIndex").GetInt32());
                Assert.False(root.GetProperty("hasAudio").GetBoolean());
                Assert.True(root.TryGetProperty("captureApi", out var captureApi));
                Assert.Equal(JsonValueKind.String, captureApi.ValueKind);
                Assert.True(root.TryGetProperty("hardwareEncoding", out var hw));
                Assert.True(hw.ValueKind == JsonValueKind.True || hw.ValueKind == JsonValueKind.False);
                Assert.True(root.TryGetProperty("lowLatency", out var ll));
                Assert.True(ll.ValueKind == JsonValueKind.True || ll.ValueKind == JsonValueKind.False);
            }
            else
            {
                // Non-Windows dev fallback keeps command path alive.
                Assert.True(res.Ok);
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("mp4", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
            }
        }

        [Fact]
        public async Task CameraList_ShouldReturnDevicesArray()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableCameraSnapshots = true });
            var req = new BridgeInvokeRequest
            {
                Id = "camera-list-1",
                Command = "camera.list"
            };

            var res = await executor.ExecuteAsync(req);

            if (!res.Ok) throw new Exception(res.Error?.Message ?? "(no error)");
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("devices", out var devices));
            Assert.Equal(JsonValueKind.Array, devices.ValueKind);

            foreach (var d in devices.EnumerateArray())
            {
                Assert.True(d.TryGetProperty("id", out var id));
                Assert.Equal(JsonValueKind.String, id.ValueKind);

                Assert.True(d.TryGetProperty("name", out var name));
                Assert.Equal(JsonValueKind.String, name.ValueKind);

                Assert.True(d.TryGetProperty("position", out var position));
                Assert.Equal(JsonValueKind.String, position.ValueKind);

                Assert.True(d.TryGetProperty("deviceType", out var deviceType));
                Assert.Equal(JsonValueKind.String, deviceType.ValueKind);
            }
        }

        [Fact]
        public async Task CameraSnap_ShouldReturnExpectedPayloadShape()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableCameraSnapshots = true });
            var req = new BridgeInvokeRequest
            {
                Id = "camera-1",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { facing = "front", maxWidth = 1280, quality = 0.9, delayMs = 1 })
            };

            var res = await executor.ExecuteAsync(req);

            if (res.Ok)
            {
                Assert.NotNull(res.PayloadJSON);

                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.Equal("jpg", root.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("base64").GetString()));
                Assert.True(root.GetProperty("width").GetInt32() > 0);
                Assert.True(root.GetProperty("height").GetInt32() > 0);
            }
            else
            {
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            }
        }

        [Fact]
        public async Task SystemRun_LegacyCommandAndArgs_ShouldBeRejected()
        {
            var executor = new NodeCommandExecutor();
            object command = OperatingSystem.IsWindows() ? "cmd.exe" : "bash";
            object args = OperatingSystem.IsWindows()
                ? new[] { "/c", "echo WINDOWS_OK" }
                : new[] { "-lc", "echo UNIX_OK" };
            var req = new BridgeInvokeRequest
            {
                Id = "run-legacy-args",
                Command = "system.run",
                ParamsJSON = JsonSerializer.Serialize(new { command, args, timeoutMs = 5000 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error?.Code);
            Assert.Contains("string array", res.Error?.Message);
        }

        [Fact]
        public async Task SystemRun_InvalidTimeoutType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "run-invalid-timeout",
                Command = "system.run",
                ParamsJSON = "{\"command\":[\"dotnet\",\"--info\"],\"timeoutMs\":\"1000\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("timeoutMs", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SystemRun_ShouldRejectMismatchedRawCommand()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "run-invalid-raw-command",
                Command = "system.run",
                ParamsJSON = JsonSerializer.Serialize(new
                {
                    command = new[] { "cmd.exe", "/d", "/s", "/c", "echo hi" },
                    rawCommand = "echo bye",
                    timeoutMs = 1000
                })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("rawCommand does not match command", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SystemNotify_EmptyNotification_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "notify-empty-1",
                Command = "system.notify",
                ParamsJSON = JsonSerializer.Serialize(new { title = "   ", body = "   " })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("empty notification", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SystemRun_Timeout_ShouldKillProcessTree_AndReturnTimedOut()
        {
            await WithTempOpenClawHome(async () =>
            {
                var executor = new NodeCommandExecutor();
                var executable = OperatingSystem.IsWindows() ? "ping.exe" : "sleep";
                var firstGet = await executor.ExecuteAsync(new BridgeInvokeRequest
                {
                    Id = "timeout-policy-get",
                    Command = "system.execApprovals.get",
                    ParamsJSON = "{}"
                });
                using var getDocument = JsonDocument.Parse(firstGet.PayloadJSON!);
                var baseHash = getDocument.RootElement.GetProperty("hash").GetString();
                var setPolicy = await executor.ExecuteAsync(new BridgeInvokeRequest
                {
                    Id = "timeout-policy-set",
                    Command = "system.execApprovals.set",
                    ParamsJSON = JsonSerializer.Serialize(new
                    {
                        baseHash,
                        defaultAction = "deny",
                        rules = new[] { new { pattern = executable, action = "allow", shells = new[] { "direct" } } }
                    })
                });
                Assert.True(setPolicy.Ok, setPolicy.Error?.Message);

                object command = OperatingSystem.IsWindows()
                    ? new[] { executable, "127.0.0.1", "-n", "5" }
                    : new[] { executable, "2" };

                var req = new BridgeInvokeRequest
                {
                    Id = "run-timeout",
                    Command = "system.run",
                    ParamsJSON = JsonSerializer.Serialize(new { command, timeoutMs = 100 })
                };

                var res = await executor.ExecuteAsync(req);

                Assert.True(res.Ok, res.Error?.Message);
                Assert.Null(res.Error);
                Assert.NotNull(res.PayloadJSON);
                using var doc = JsonDocument.Parse(res.PayloadJSON!);
                var root = doc.RootElement;
                Assert.True(root.GetProperty("timedOut").GetBoolean());
                Assert.Equal(-1, root.GetProperty("exitCode").GetInt32());
                Assert.False(root.GetProperty("success").GetBoolean());
            });
        }

        [Fact]
        public async Task ScreenRecord_InvalidDuration_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-duration",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { durationMs = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task SystemNotify_UsesNativeNotificationSink()
        {
            string? observed = null;
            using var executor = new NodeCommandExecutor(notificationSink: (title, body, _) =>
            {
                observed = title + "|" + body;
                return Task.CompletedTask;
            });
            var res = await executor.ExecuteAsync(new BridgeInvokeRequest
            {
                Id = "notify-1",
                Command = "system.notify",
                ParamsJSON = JsonSerializer.Serialize(new { title = "OpenClaw", body = "Ready" })
            });

            Assert.True(res.Ok);
            Assert.Equal("OpenClaw|Ready", observed);
        }

        [Fact]
        public async Task ScreenRecord_InvalidFps_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-fps",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { fps = -1 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidIncludeAudioType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-audio",
                Command = "screen.record",
                ParamsJSON = "{\"includeAudio\":\"yes\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidScreenIndex_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-index",
                Command = "screen.record",
                ParamsJSON = JsonSerializer.Serialize(new { screenIndex = -1 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidCaptureApiType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-capture-api",
                Command = "screen.record",
                ParamsJSON = "{\"captureApi\":123}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidLowLatencyType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-low-latency",
                Command = "screen.record",
                ParamsJSON = "{\"lowLatency\":\"yes\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task ScreenRecord_InvalidDurationType_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableScreenRecording = true });
            var req = new BridgeInvokeRequest
            {
                Id = "screen-invalid-duration-type",
                Command = "screen.record",
                ParamsJSON = "{\"durationMs\":\"1000\"}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidFacing_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableCameraSnapshots = true });
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-facing",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { facing = "side" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidFormat_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableCameraSnapshots = true });
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-format",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { format = "png" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task CameraSnap_InvalidQuality_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor(settings: new CompanionSettings { EnableCameraSnapshots = true });
            var req = new BridgeInvokeRequest
            {
                Id = "camera-invalid-quality",
                Command = "camera.snap",
                ParamsJSON = JsonSerializer.Serialize(new { quality = 1.5 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task WindowList_ShouldNotBeGatewayInvokable()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-list-1", Command = "window.list" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error?.Code);
        }

        [Fact]
        public async Task WindowFocus_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-focus-1", Command = "window.focus", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task WindowRect_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "window-rect-1", Command = "window.rect", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputType_MissingText_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-type-1", Command = "input.type", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputKey_MissingKey_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-key-1", Command = "input.key", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_MissingCoordinates_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-click-1", Command = "input.click", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_InvalidButton_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-2",
                Command = "input.click",
                ParamsJSON = JsonSerializer.Serialize(new { x = 10, y = 20, button = "middle" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClick_NonIntegerCoordinates_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-3",
                Command = "input.click",
                ParamsJSON = "{\"x\":1.5,\"y\":2}"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("Unsupported or disabled gateway command", res.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InputScroll_MissingDelta_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "input-scroll-1", Command = "input.scroll", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputScroll_ZeroDelta_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-scroll-2",
                Command = "input.scroll",
                ParamsJSON = JsonSerializer.Serialize(new { deltaY = 0 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClickRelative_MissingTarget_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-relative-1",
                Command = "input.click.relative",
                ParamsJSON = JsonSerializer.Serialize(new { offsetX = 10, offsetY = 10 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task InputClickRelative_MissingOffsets_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "input-click-relative-2",
                Command = "input.click.relative",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

                        [Fact]
        public async Task ScreenCapture_WithoutRouting_ShouldReturnInvalidRequest_OnWindows()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "screen-capture-1",
                Command = "screen.capture"
            };

            var res = await executor.ExecuteAsync(req);

            if (OperatingSystem.IsWindows())
            {
                Assert.False(res.Ok);
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            }
            else
            {
                Assert.False(res.Ok);
                Assert.NotNull(res.Error);
                Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            }
        }

        [Fact]
        public async Task UiFind_MissingParams_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "ui-find-1", Command = "ui.find", ParamsJSON = "{}" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiFind_MissingSelectors_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-find-2",
                Command = "ui.find",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiClick_InvalidButton_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-click-1",
                Command = "ui.click",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit", button = "middle" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiType_MissingText_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-type-1",
                Command = "ui.type",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        [Fact]
        public async Task UiFind_OnNonWindows_ShouldReturnUnavailable_WhenSelectorsProvided()
        {
            if (OperatingSystem.IsWindows()) return;

            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "ui-find-3",
                Command = "ui.find",
                ParamsJSON = JsonSerializer.Serialize(new { titleContains = "Notepad", name = "Edit", timeoutMs = 1000 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.Unavailable, res.Error!.Code);
            Assert.NotNull(res.PayloadJSON);

            using var doc = JsonDocument.Parse(res.PayloadJSON!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("details", out var details));
            Assert.Equal("not-windows", details.GetProperty("reason").GetString());
        }

        [Fact]
        public async Task SystemDescribe_ShouldNotBeGatewayInvokable()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest
            {
                Id = "describe-1",
                Command = "system.describe"
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error?.Code);
        }

        [Fact]
        public async Task BrowserProxy_ShouldReturnPayload_FromInjectedService()
        {
            var browser = new FakeBrowserProxyService("{\"result\":{\"ok\":true,\"profiles\":[\"chrome-relay\"]}} ");
            var executor = new NodeCommandExecutor(browserProxyService: browser);
            var req = new BridgeInvokeRequest
            {
                Id = "browser-1",
                Command = "browser.proxy",
                ParamsJSON = JsonSerializer.Serialize(new { path = "/profiles", method = "GET", timeoutMs = 1234 })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.True(res.Ok);
            Assert.NotNull(res.PayloadJSON);
            Assert.NotNull(browser.LastRequest);
            Assert.Equal("/profiles", browser.LastRequest!.Path);
            Assert.Equal("GET", browser.LastRequest.Method);
            Assert.Equal(1234, browser.LastRequest.TimeoutMs);
        }

        [Fact]
        public async Task BrowserProxy_ShouldValidatePath()
        {
            var executor = new NodeCommandExecutor(browserProxyService: new FakeBrowserProxyService("{\"result\":{}}"));
            var req = new BridgeInvokeRequest
            {
                Id = "browser-2",
                Command = "browser.proxy",
                ParamsJSON = JsonSerializer.Serialize(new { method = "GET" })
            };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
            Assert.Contains("params.path", res.Error.Message);
        }

        [Fact]
        public async Task UnknownCommand_ShouldReturnInvalidRequest()
        {
            var executor = new NodeCommandExecutor();
            var req = new BridgeInvokeRequest { Id = "2", Command = "nope.command" };

            var res = await executor.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.NotNull(res.Error);
            Assert.Equal(OpenClawNodeErrorCode.InvalidRequest, res.Error!.Code);
        }

        private static async Task WithTempOpenClawHome(Func<Task> action)
        {
            var original = Environment.GetEnvironmentVariable("USERPROFILE");
            var originalWindowsHome = Environment.GetEnvironmentVariable("OPENCLAW_WINDOWS_HOME");
            var tempRoot = Path.Combine(Path.GetTempPath(), "oc-node-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            Environment.SetEnvironmentVariable("USERPROFILE", tempRoot);
            Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", tempRoot);
            try
            {
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", original);
                Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", originalWindowsHome);
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                }
            }
        }

        private sealed class FakeBrowserProxyService : IBrowserProxyService
        {
            private readonly string _payload;

            public FakeBrowserProxyService(string payload)
            {
                _payload = payload;
            }

            public BrowserProxyRequest? LastRequest { get; private set; }

            public Task<string> ProxyAsync(BrowserProxyRequest request, System.Threading.CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(_payload);
            }
        }
    }
}
