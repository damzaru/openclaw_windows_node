using System;
using System.IO;

namespace OpenClaw.Node.Tray
{
    public sealed record OnboardingAssessment(bool Ready, string StatusText, string ActionHint, string? Details = null);

    public static class OnboardingAdvisor
    {
        public static OnboardingAssessment Evaluate(string gatewayUrl, string gatewayToken, string configPath, string? configReadError = null)
        {
            if (string.IsNullOrWhiteSpace(gatewayToken))
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Gateway token missing",
                    "Open Settings and enter the Gateway token",
                    configReadError);
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
