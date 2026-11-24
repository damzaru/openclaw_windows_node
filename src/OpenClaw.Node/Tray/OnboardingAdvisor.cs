using System;

namespace OpenClaw.Node.Tray
{
    public sealed record OnboardingAssessment(bool Ready, string StatusText, string ActionHint);

    public static class OnboardingAdvisor
    {
        public static OnboardingAssessment Evaluate(string gatewayUrl, string gatewayToken, bool hasStoredDeviceToken = false)
        {
            if (string.IsNullOrWhiteSpace(gatewayToken) && !hasStoredDeviceToken)
            {
                return new OnboardingAssessment(
                    false,
                    "Onboarding: Gateway token missing",
                    "Open Settings and enter the Gateway token");
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
