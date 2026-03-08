using System;

namespace OpenClaw.Node.Tray
{
    public sealed record TrayStatusSnapshot(
        NodeRuntimeState State,
        string Message,
        DateTimeOffset UpdatedAtUtc,
        int PendingPairs = 0,
        long? LastReconnectMs = null,
        string OnboardingStatus = "Onboarding: Ready");
}
