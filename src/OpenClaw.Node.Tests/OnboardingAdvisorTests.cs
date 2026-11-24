using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class OnboardingAdvisorTests
    {
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
