using System;
using System.IO;

namespace OpenClaw.Node.Tray
{
    public sealed record OnboardingAssessment(bool Ready, string StatusText, string ActionHint, string? Details = null);

    public static class OnboardingAdvisor
    {
        public static OnboardingAssessment Evaluate(string gatewayUrl, string gatewayToken, string configPath, string? configReadError = null)
        {
            if (!string.IsNullOrWhiteSpace(configReadError))
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Config format issue",
                    "Open Config and fix JSON format",
                    configReadError);
            }

            if (!File.Exists(configPath))
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Config file missing",
                    "Open Config to create and edit ~/.openclaw/openclaw.json");
            }

            if (string.IsNullOrWhiteSpace(gatewayToken))
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Gateway token missing",
                    "Open Config and set gateway.auth.token");
            }

            if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri) ||
                !(string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Invalid gateway URL",
                    "Use ws:// or wss:// gateway URL");
            }

            return new OnboardingAssessment(true, "Onboarding: Ready", "No action needed");
        }
    }
}
