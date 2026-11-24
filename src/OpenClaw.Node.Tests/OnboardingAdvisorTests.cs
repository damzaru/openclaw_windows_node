using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class OnboardingAdvisorTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("Review the requested role upgrade.")]
        public void PairingRemediationAlwaysNamesTheGatewayDevicesPage(string? hint)
        {
            var guidance = OnboardingAdvisor.ConnectionRemediation("PAIRING_REQUIRED", true, hint);
            Assert.Contains("Gateway Control UI", guidance);
            Assert.Contains("Settings > Devices", guidance);
            if (hint != null) Assert.Contains(hint, guidance);
            Assert.DoesNotContain("Devices", OnboardingAdvisor.ConnectionRemediation("AUTH_REQUIRED", true, hint));
        }

        [Fact]
        public void PairingRemediationDistinguishesWaitingFromAnExpiredRequest()
        {
            Assert.Contains("retry automatically", OnboardingAdvisor.ConnectionRemediation("PAIRING_REQUIRED", false, null));
            Assert.Contains("no longer pending", OnboardingAdvisor.ConnectionRemediation("PAIRING_EXPIRED", true, null));
        }

        [Fact]
        public void Evaluate_ShouldFail_WhenTokenMissing()
        {
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "");
            Assert.False(result.Ready);
            Assert.Contains("token", result.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_ShouldFail_WhenGatewayUrlInvalid()
        {
            var result = OnboardingAdvisor.Evaluate("http://127.0.0.1:18789", "abc");
            Assert.False(result.Ready);
            Assert.Contains("Invalid gateway URL", result.StatusText);
        }

        [Fact]
        public void Evaluate_ShouldPass_WhenSecureSettingsResolvedWithoutLegacyConfig()
        {
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "abc");
            Assert.True(result.Ready);
        }

        [Fact]
        public void Evaluate_ShouldPass_WhenStoredDeviceTokenSupportsReconnect()
        {
            var result = OnboardingAdvisor.Evaluate("wss://gateway.example", "", hasStoredDeviceToken: true);
            Assert.True(result.Ready);
        }

        [Fact]
        public void Evaluate_ShouldPass_WhenAllChecksGood()
        {
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "abc");
            Assert.True(result.Ready);
            Assert.Equal("Onboarding: Ready", result.StatusText);
        }

    }
}
