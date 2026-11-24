using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class CompanionConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArgumentsOverrideEnvironmentAndSavedSettingsWithoutMutatingThem(bool hasSavedSettings)
    {
        WithEnvironment(() =>
        {
            var settings = new CompanionSettings { GatewayUrl = "wss://saved.example", GatewayToken = "saved-token" };
            var resolved = Program.ResolveConnectionSettings(new[] { "--gateway-url", "wss://argument.example", "--gateway-token", "argument-token" }, settings, hasSavedSettings);
            Assert.Equal(("wss://argument.example", "argument-token"), resolved);
            Assert.Equal("wss://saved.example", settings.GatewayUrl);
            Assert.Equal("saved-token", settings.GatewayToken);
        });
    }

    [Fact]
    public void EnvironmentOverridesSavedSettings()
        => WithEnvironment(() => Assert.Equal(("wss://environment.example", "environment-token"), Program.ResolveConnectionSettings(Array.Empty<string>(), new CompanionSettings { GatewayUrl = "wss://saved.example", GatewayToken = "saved-token" }, true)));

    [Fact]
    public void SavedSettingsAreTheOnlyPersistentConnectionSource()
        => WithEnvironment(() => Assert.Equal(("wss://saved.example", "saved-token"), Program.ResolveConnectionSettings(Array.Empty<string>(), new CompanionSettings { GatewayUrl = "wss://saved.example", GatewayToken = "saved-token" }, true)), clear: true);

    [Fact]
    public void UnsavedStartupUsesLoopbackAndRequiresCredentials()
        => WithEnvironment(() => Assert.Equal(("ws://127.0.0.1:18789", ""), Program.ResolveConnectionSettings(Array.Empty<string>(), new CompanionSettings { GatewayUrl = "wss://unsaved.example", GatewayToken = "unsaved-token" }, false)), clear: true);

    [Fact]
    public void UnrestrictedPoliciesAreOffByDefaultAndPersistThroughLocalSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openclaw-settings-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CompanionSettingsStore(new SecureStore(directory));
            var settings = store.Load();
            Assert.False(settings.AllowUnrestrictedExecutionPolicy);
            Assert.False(settings.EnableFileRead); Assert.False(settings.EnableFileWrite);
            settings.AllowUnrestrictedExecutionPolicy = true;
            settings.EnableFileRead = true;
            store.Save(settings);
            Assert.True(store.Load().AllowUnrestrictedExecutionPolicy);
            Assert.True(store.Load().EnableFileRead); Assert.False(store.Load().EnableFileWrite);
            settings.AllowUnrestrictedExecutionPolicy = false;
            settings.EnableFileRead = false; settings.EnableFileWrite = true;
            store.Save(settings);
            Assert.False(store.Load().AllowUnrestrictedExecutionPolicy);
            Assert.False(store.Load().EnableFileRead); Assert.True(store.Load().EnableFileWrite);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void WithEnvironment(Action action, bool clear = false)
    {
        var url = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_URL");
        var token = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_URL", clear ? null : "wss://environment.example");
            Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN", clear ? null : "environment-token");
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_URL", url);
            Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN", token);
        }
    }
}
