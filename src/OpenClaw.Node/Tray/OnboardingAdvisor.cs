using System;

namespace OpenClaw.Node.Tray
{
    public sealed record OnboardingAssessment(bool Ready, string StatusText, string ActionHint);

    public static class OnboardingAdvisor
    {
        internal static string ConnectionRemediation(string code, bool reconnectPaused, string? serverHint)
        {
            if (!code.StartsWith("PAIRING_", StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(serverHint) ? serverHint.Trim() : "Review the Gateway authentication requirements and companion connection settings, then restart the companion.";
            const string devices = "Gateway Control UI > Settings > Devices";
            var guidance = code is "PAIRING_EXPIRED" or "PAIRING_REJECTED"
                ? $"This request is no longer pending. If this PC should connect, restart the companion to request pairing again and review the new request in {devices}."
                : $"Review this PC's pending request in {devices} and approve it there if expected. " +
                  (reconnectPaused ? "Then restart the companion." : "The companion will retry automatically.");
            return string.IsNullOrWhiteSpace(serverHint) ? guidance : guidance + "\n\n" + serverHint.Trim();
        }

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
