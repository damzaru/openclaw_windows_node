using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class GatewayParityTests
{
    [Fact]
    public void ProtocolVersions_MatchCurrentGatewayContract()
    {
        Assert.Equal(4, Constants.GatewayProtocolVersion);
        Assert.Equal(3, Constants.MinimumNodeProtocolVersion);
        Assert.Equal(4, Constants.MaximumNodeProtocolVersion);
        Assert.Equal(16 * 1024, Constants.MaximumInvokeInputBytes);
        Assert.Equal(16 * 1024, Constants.MaximumProgressChunkBytes);
    }

    [Fact]
    public void DeviceAuthPayload_UsesCanonicalV3ShapeAndAsciiMetadataNormalization()
    {
        var service = new DeviceIdentityService(new SecureStore(TestEnvironment.Directory));
        var payload = service.BuildDeviceAuthPayload(
            "dev-1",
            "node-host",
            "ui",
            "operator",
            new[] { "operator.admin", "operator.read" },
            1_800_000_000_000,
            "tok-123",
            "nonce-abc",
            " IoS ",
            "IPhone");

        Assert.Equal("v3|dev-1|node-host|ui|operator|operator.admin,operator.read|1800000000000|tok-123|nonce-abc|ios|iphone", payload);
    }

    [Fact]
    public void HelloModels_UseCurrentDeviceTokenAndPolicyFieldNames()
    {
        var handoff = JsonSerializer.Serialize(new DeviceTokenHandoff
        {
            Role = "node",
            Token = "device-token",
            Scopes = new() { "operator.read" },
        });
        Assert.Contains("\"deviceToken\":\"device-token\"", handoff);
        Assert.DoesNotContain("\"token\":", handoff);

        var hello = JsonSerializer.Deserialize<HelloOkPayload>("""
            {"type":"hello-ok","protocol":4,"pluginSurfaceUrls":{"canvas":"https://gateway.test/__openclaw__/cap/token"},"policy":{"maxPayload":12345,"maxBufferedBytes":99999,"tickIntervalMs":30000}}
            """);
        Assert.Equal(12345, hello?.Policy?.MaxPayload);
        Assert.Equal("https://gateway.test/__openclaw__/cap/token", hello?.PluginSurfaceUrls["canvas"]);
    }

    [Fact]
    public void StoredDeviceTokenScopes_UseCurrentOperatorCompatibilityRules()
    {
        var admin = new DeviceTokenStore.Entry { Scopes = new() { "operator.admin" } };
        var write = new DeviceTokenStore.Entry { Scopes = new() { "operator.write" } };
        var read = new DeviceTokenStore.Entry { Scopes = new() { "operator.read" } };

        Assert.False(GatewayConnection.RequestedScopesExceedStoredToken(
            "operator", new[] { "operator.read", "operator.write" }, admin));
        Assert.False(GatewayConnection.RequestedScopesExceedStoredToken(
            "operator", new[] { "operator.read" }, write));
        Assert.True(GatewayConnection.RequestedScopesExceedStoredToken(
            "operator", new[] { "operator.write" }, read));
        Assert.True(GatewayConnection.RequestedScopesExceedStoredToken(
            "operator", new[] { "node.read" }, admin));
    }

    [Fact]
    public void ReconnectPolicy_HonorsTerminalAndPairingRetryDetails()
    {
        static GatewayErrorShape ErrorWithDetails(string json) => new()
        {
            Code = "UNAUTHORIZED",
            Details = JsonDocument.Parse(json).RootElement.Clone(),
        };

        Assert.True(GatewayConnection.ShouldPauseAfterConnectError(
            ErrorWithDetails("""{"code":"AUTH_DEVICE_TOKEN_MISMATCH"}""")));
        Assert.True(GatewayConnection.ShouldPauseAfterConnectError(
            ErrorWithDetails("""{"code":"CLIENT_VERSION_MISMATCH"}""")));
        Assert.True(GatewayConnection.ShouldPauseAfterConnectError(
            ErrorWithDetails("""{"code":"PAIRING_REQUIRED"}""")));
        Assert.False(GatewayConnection.ShouldPauseAfterConnectError(
            ErrorWithDetails("""{"code":"PAIRING_REQUIRED","pauseReconnect":false,"recommendedNextStep":"wait_then_retry"}""")));
        Assert.False(GatewayConnection.ShouldPauseAfterConnectError(
            ErrorWithDetails("""{"code":"SOME_FUTURE_RECOVERABLE_CODE"}""")));
    }

    [Fact]
    public void CanvasHostedResolver_RequiresCanonicalCapabilityScopedTargets()
    {
        const string surface = "https://gateway.test/__openclaw__/cap/opaque-token";
        var resolved = CanvasService.ResolveHostedTarget(surface, "/__openclaw__/a2ui/?platform=windows");
        Assert.Equal("https://gateway.test/__openclaw__/cap/opaque-token/__openclaw__/a2ui/?platform=windows", resolved?.AbsoluteUri);
        Assert.True(CanvasService.IsHostedTarget("/__openclaw__/canvas/dashboard"));
        Assert.False(CanvasService.IsHostedTarget("/__openclaw__/canvas/%252e%252e/secrets"));
        Assert.Null(CanvasService.ResolveHostedTarget("https://gateway.test/not-a-capability", "/__openclaw__/canvas/"));
    }

    [Fact]
    public void ChildProcesses_DoNotInheritGatewayOrApiSecrets()
    {
        var start = new ProcessStartInfo { FileName = "ignored.exe", UseShellExecute = false };
        start.Environment["OPENCLAW_GATEWAY_TOKEN"] = "secret";
        start.Environment["EXAMPLE_API_KEY"] = "secret";
        start.Environment["SAFE_VALUE"] = "preserved";

        ChildProcessSecurity.ScrubSensitiveEnvironment(start);

        Assert.False(start.Environment.ContainsKey("OPENCLAW_GATEWAY_TOKEN"));
        Assert.False(start.Environment.ContainsKey("EXAMPLE_API_KEY"));
        Assert.Equal("preserved", start.Environment["SAFE_VALUE"]);
    }

    [Fact]
    public void CapabilityRegistry_DefaultsToSafeCurrentGatewaySurface()
    {
        var manifest = NodeCapabilityRegistry.Build(new CompanionSettings());

        Assert.Contains("system.run.prepare", manifest.GatewayCommands);
        Assert.Contains("system.execApprovals.get", manifest.GatewayCommands);
        Assert.Contains("screen.snapshot", manifest.GatewayCommands);
        Assert.Contains("camera.list", manifest.GatewayCommands);
        Assert.Contains("device.info", manifest.GatewayCommands);
        Assert.Contains("canvas.a2ui.pushJSONL", manifest.GatewayCommands);
        Assert.DoesNotContain("screen.capture", manifest.GatewayCommands);
        Assert.DoesNotContain("screen.list", manifest.GatewayCommands);
        Assert.DoesNotContain("window.list", manifest.GatewayCommands);
        Assert.DoesNotContain("input.click", manifest.GatewayCommands);
        Assert.DoesNotContain("ui.find", manifest.GatewayCommands);
        Assert.DoesNotContain("screen.record", manifest.GatewayCommands);
        Assert.DoesNotContain("camera.snap", manifest.GatewayCommands);
        Assert.DoesNotContain("location.get", manifest.GatewayCommands);
        Assert.DoesNotContain("talk.ptt.start", manifest.GatewayCommands);
        Assert.DoesNotContain("mcp.tools.call.v1", manifest.GatewayCommands);
        Assert.DoesNotContain("agent.cli.claude.run.v1", manifest.GatewayCommands);
        Assert.Contains("ipc.window.list", manifest.LocalOnlyCommands);
        Assert.All(manifest.LocalOnlyCommands, command => Assert.StartsWith("ipc.", command));
    }

    [Fact]
    public void CapabilityRegistry_RequiresExplicitOptInForSensitiveCommands()
    {
        var manifest = NodeCapabilityRegistry.Build(new CompanionSettings
        {
            EnableScreenRecording = true,
            EnableCameraSnapshots = true,
            EnableCameraClips = true,
            EnableLocation = true,
            EnableTalkPushToTalk = true,
        });

        Assert.Contains("screen.record", manifest.GatewayCommands);
        Assert.Contains("camera.snap", manifest.GatewayCommands);
        Assert.Contains("camera.clip", manifest.GatewayCommands);
        Assert.Contains("location.get", manifest.GatewayCommands);
        Assert.Contains("talk.ptt.once", manifest.GatewayCommands);
    }

    [Fact]
    public async Task NativeExecApprovals_RejectUnsafeDefaultAndDangerousShellAllowRules()
    {
        var prior = Environment.GetEnvironmentVariable("OPENCLAW_WINDOWS_HOME");
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-approval-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", directory);
        try
        {
            using var executor = new NodeCommandExecutor();
            var snapshot = await executor.ExecuteAsync(new BridgeInvokeRequest
            {
                Id = "get",
                Command = "system.execApprovals.get",
                ParamsJSON = "{}"
            });
            using var snapshotJson = JsonDocument.Parse(snapshot.PayloadJSON!);
            var hash = snapshotJson.RootElement.GetProperty("hash").GetString();

            var defaultAllow = await executor.ExecuteAsync(new BridgeInvokeRequest
            {
                Id = "set-default",
                Command = "system.execApprovals.set",
                ParamsJSON = JsonSerializer.Serialize(new { baseHash = hash, defaultAction = "allow", rules = Array.Empty<object>() })
            });
            Assert.False(defaultAllow.Ok);
            Assert.Contains("defaultAction=allow", defaultAllow.Error?.Message);

            var dangerousRule = await executor.ExecuteAsync(new BridgeInvokeRequest
            {
                Id = "set-shell",
                Command = "system.execApprovals.set",
                ParamsJSON = JsonSerializer.Serialize(new
                {
                    baseHash = hash,
                    defaultAction = "deny",
                    rules = new[] { new { pattern = "powershell.exe *", action = "allow" } }
                })
            });
            Assert.False(dangerousRule.Ok);
            Assert.Contains("dangerous shell or loader", dangerousRule.Error?.Message);

            var unknownNestedField = await executor.ExecuteAsync(new BridgeInvokeRequest
            {
                Id = "set-typo",
                Command = "system.execApprovals.set",
                ParamsJSON = JsonSerializer.Serialize(new
                {
                    baseHash = hash,
                    defaultAction = "deny",
                    rules = new[] { new { pattern = "notepad.exe", action = "allow", enabeld = true } }
                })
            });
            Assert.False(unknownNestedField.Ok);
            Assert.Contains("unknown exec approvals rule field", unknownNestedField.Error?.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", prior);
            try { System.IO.Directory.Delete(directory, true); } catch { }
        }
    }
}
