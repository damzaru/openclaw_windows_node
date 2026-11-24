using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class UnrestrictedExecutionPolicyTests : IDisposable
{
    private readonly string? _priorHome = Environment.GetEnvironmentVariable("OPENCLAW_WINDOWS_HOME");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openclaw-unrestricted-test-" + Guid.NewGuid().ToString("N"));

    public UnrestrictedExecutionPolicyTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", _directory);
    }

    [Fact]
    public void LocalOptinChangesConstraintsButDoesNotAutomaticallyAllowCommands()
    {
        var snapshot = ExecApprovalsStore.ReadSnapshot(true);
        Assert.Equal("deny", snapshot.DefaultAction);
        Assert.Equal("deny", ExecApprovalsStore.Evaluate("powershell.exe -NoProfile", new[] { "powershell.exe", "-NoProfile" }, true).Action);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ExecApprovalsStore.ToPayload(snapshot)));
        var constraints = json.RootElement.GetProperty("constraints");
        Assert.True(constraints.GetProperty("baseHashRequired").GetBoolean());
        Assert.True(constraints.GetProperty("defaultAllowAllowed").GetBoolean());
        Assert.True(constraints.GetProperty("broadAllowRulesAllowed").GetBoolean());
        Assert.True(constraints.GetProperty("dangerousAllowRulesAllowed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_directory, "exec-approvals-native.json")));
    }

    [Fact]
    public async Task RemoteApiCannotEnableTheLocalPermissionCeiling()
    {
        using var executor = new NodeCommandExecutor();
        var get = await Invoke(executor, "system.execApprovals.get", new { });
        using var json = JsonDocument.Parse(get.PayloadJSON!);
        var hash = json.RootElement.GetProperty("hash").GetString();
        var rejected = await Invoke(executor, "system.execApprovals.set", new { baseHash = hash, defaultAction = "allow", rules = Array.Empty<object>() });
        Assert.False(rejected.Ok);
        Assert.Contains("local companion setting", rejected.Error?.Message);
        var bypass = await Invoke(executor, "system.execApprovals.set", new { baseHash = hash, defaultAction = "allow", rules = Array.Empty<object>(), allowUnrestrictedExecutionPolicy = true });
        Assert.False(bypass.Ok);
        Assert.Contains("unknown exec approvals field", bypass.Error?.Message);
        Assert.Equal(hash, ExecApprovalsStore.ReadSnapshot().Hash);
    }

    [Theory]
    [InlineData("powershell.exe *", "powershell.exe -NoProfile", "powershell.exe")]
    [InlineData("pwsh *", "pwsh -NoProfile", "pwsh")]
    [InlineData("cmd.exe *", "cmd.exe /c echo test", "cmd.exe")]
    [InlineData("wscript.exe *", "wscript.exe script.vbs", "wscript.exe")]
    [InlineData("rundll32.exe *", "rundll32.exe library.dll,Entry", "rundll32.exe")]
    [InlineData("*", "powershell.exe -NoProfile", "powershell.exe")]
    [InlineData("power*.exe *", "powershell.exe -NoProfile", "powershell.exe")]
    public void BroadAndShellAllowRulesRequireLocalOptinAndAreRevokedWhenItIsOff(string pattern, string command, string executable)
    {
        var policy = new ExecApprovalsStore.NativePolicy
        {
            DefaultAction = "deny",
            Rules = new() { new() { Pattern = pattern, Action = "allow" } },
        };
        Assert.Throws<InvalidOperationException>(() => ExecApprovalsStore.Save(policy, ExecApprovalsStore.ReadSnapshot().Hash));
        ExecApprovalsStore.Save(policy, ExecApprovalsStore.ReadSnapshot(true).Hash, true);
        Assert.Equal("allow", ExecApprovalsStore.Evaluate(command, new[] { executable }, true).Action);
        Assert.Equal("deny", ExecApprovalsStore.Evaluate(command, new[] { executable }, false).Action);
        Assert.False(ExecApprovalsStore.ReadSnapshot(false).Rules.Single().Enabled);
        Assert.NotEqual(false, ExecApprovalsStore.ReadSnapshot(true).Rules.Single().Enabled);
    }

    [Fact]
    public void OptinRevocationPreservesMaintenanceRulesAndDoesNotRewriteStoredPolicy()
    {
        var policy = new ExecApprovalsStore.NativePolicy
        {
            DefaultAction = "allow",
            Rules = new()
            {
                new() { Pattern = "dotnet --version", Action = "allow" },
                new() { Pattern = "powershell.exe *", Action = "allow" },
                new() { Pattern = "blocked.exe", Action = "deny" },
            },
        };
        var snapshot = ExecApprovalsStore.Save(policy, ExecApprovalsStore.ReadSnapshot(true).Hash, true);
        var path = Path.Combine(_directory, "exec-approvals-native.json");
        var before = File.ReadAllBytes(path);
        Assert.Equal("deny", ExecApprovalsStore.Evaluate("blocked.exe", new[] { "blocked.exe" }, true).Action);
        Assert.Equal("allow", ExecApprovalsStore.Evaluate("dotnet --version", new[] { "dotnet", "--version" }, false).Action);
        Assert.Equal("deny", ExecApprovalsStore.Evaluate("unlisted.exe", new[] { "unlisted.exe" }, false).Action);
        Assert.Equal("deny", ExecApprovalsStore.ReadSnapshot(false).DefaultAction);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.NotEqual(snapshot.Hash, ExecApprovalsStore.ReadSnapshot(false).Hash);
        Assert.Throws<InvalidOperationException>(() => ExecApprovalsStore.Save(new() { DefaultAction = "deny", Rules = new() }, snapshot.Hash, false));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("invalid", "dotnet", "allow", "direct")]
    [InlineData("allow", "", "allow", "direct")]
    [InlineData("allow", "dotnet", "invalid", "direct")]
    [InlineData("allow", "dotnet", "allow", "unsupported-shell")]
    public void OptinDoesNotBypassPolicyValidation(string defaultAction, string pattern, string action, string shell)
    {
        var policy = new ExecApprovalsStore.NativePolicy { DefaultAction = defaultAction, Rules = new() { new() { Pattern = pattern, Action = action, Shells = new() { shell } } } };
        Assert.Throws<InvalidOperationException>(() => ExecApprovalsStore.Save(policy, ExecApprovalsStore.ReadSnapshot(true).Hash, true));
        Assert.False(File.Exists(Path.Combine(_directory, "exec-approvals-native.json")));
    }

    [Fact]
    public async Task AllowAllExecutesHarmlessPowerShellCommandAndRevocationBlocksIt()
    {
        using var enabled = new NodeCommandExecutor(settings: new CompanionSettings { AllowUnrestrictedExecutionPolicy = true });
        var get = await Invoke(enabled, "system.execApprovals.get", new { });
        using var json = JsonDocument.Parse(get.PayloadJSON!);
        var set = await Invoke(enabled, "system.execApprovals.set", new { baseHash = json.RootElement.GetProperty("hash").GetString(), defaultAction = "allow", rules = Array.Empty<object>() });
        Assert.True(set.Ok, set.Error?.Message);
        var command = new[] { "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'openclaw-policy-test'" };
        var run = await Invoke(enabled, "system.run", new { command, timeoutMs = 5000 });
        Assert.True(run.Ok, run.Error?.Message);
        using var result = JsonDocument.Parse(run.PayloadJSON!);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("openclaw-policy-test", result.RootElement.GetProperty("stdout").GetString());
        using var disabled = new NodeCommandExecutor();
        var rejected = await Invoke(disabled, "system.run", new { command });
        Assert.False(rejected.Ok);
        Assert.Equal(OpenClawNodeErrorCode.SystemRunDenied, rejected.Error?.Code);
    }

    [Fact]
    public async Task PromptStillFailsClosedWithoutLocalApprovalUi()
    {
        ExecApprovalsStore.Save(new() { DefaultAction = "prompt", Rules = new() }, ExecApprovalsStore.ReadSnapshot(true).Hash, true);
        using var executor = new NodeCommandExecutor(settings: new CompanionSettings { AllowUnrestrictedExecutionPolicy = true });
        var result = await Invoke(executor, "system.run", new { command = new[] { "powershell.exe", "-Command", "Write-Output test" } });
        Assert.False(result.Ok);
        Assert.Contains("no interactive approval", result.Error?.Message);
    }

    private static Task<BridgeInvokeResponse> Invoke(NodeCommandExecutor executor, string command, object parameters)
        => executor.ExecuteAsync(new BridgeInvokeRequest { Id = Guid.NewGuid().ToString("N"), Command = command, ParamsJSON = JsonSerializer.Serialize(parameters) });

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", _priorHome);
        Directory.Delete(_directory, true);
    }
}
