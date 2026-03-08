using System;
using System.IO;
using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class OnboardingAdvisorTests
    {
        [Fact]
        public void Evaluate_ShouldFail_WhenTokenMissing()
        {
            var configPath = CreateTempConfig();
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "", configPath);
            Assert.False(result.Ready);
            Assert.Contains("token", result.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_ShouldFail_WhenGatewayUrlInvalid()
        {
            var configPath = CreateTempConfig();
            var result = OnboardingAdvisor.Evaluate("http://127.0.0.1:18789", "abc", configPath);
            Assert.False(result.Ready);
            Assert.Contains("Invalid gateway URL", result.StatusText);
        }

        [Fact]
        public void Evaluate_ShouldFail_WhenConfigMissing()
        {
            var missingPath = Path.Combine(Path.GetTempPath(), $"openclaw-missing-{Guid.NewGuid():N}.json");
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "abc", missingPath);
            Assert.False(result.Ready);
            Assert.Contains("Config file missing", result.StatusText);
        }

        [Fact]
        public void Evaluate_ShouldFail_WhenConfigHasParseError()
        {
            var configPath = CreateTempConfig();
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "abc", configPath, "Unexpected token at line 1");
            Assert.False(result.Ready);
            Assert.Contains("Config format", result.StatusText);
        }

        [Fact]
        public void Evaluate_ShouldPass_WhenAllChecksGood()
        {
            var configPath = CreateTempConfig();
            var result = OnboardingAdvisor.Evaluate("ws://127.0.0.1:18789", "abc", configPath);
            Assert.True(result.Ready);
            Assert.Equal("Onboarding: Ready", result.StatusText);
        }

        private static string CreateTempConfig()
        {
            var path = Path.Combine(Path.GetTempPath(), $"openclaw-config-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{\"gateway\":{\"port\":18789,\"auth\":{\"token\":\"abc\"}}}");
            return path;
        }
    }
}
