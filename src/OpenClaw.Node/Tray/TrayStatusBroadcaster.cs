using System;

namespace OpenClaw.Node.Tray
{
    public sealed class TrayStatusBroadcaster
    {
        private readonly Func<DateTimeOffset> _clock;
        private readonly string _buildVersion;

        public TrayStatusBroadcaster(Func<DateTimeOffset>? clock = null, string buildVersion = "unknown")
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _buildVersion = string.IsNullOrWhiteSpace(buildVersion) ? "unknown" : buildVersion;
            Current = new TrayStatusSnapshot(NodeRuntimeState.Starting, "Starting", _clock(), OnboardingStatus: "Onboarding: Ready", BuildVersion: _buildVersion);
        }

        public TrayStatusSnapshot Current { get; private set; }

        public event Action<TrayStatusSnapshot>? OnStatusChanged;

        public void Set(NodeRuntimeState state, string message, int pendingPairs = 0, long? lastReconnectMs = null, string onboardingStatus = "Onboarding: Ready")
        {
            if (string.IsNullOrWhiteSpace(message)) message = state.ToString();
            if (string.IsNullOrWhiteSpace(onboardingStatus)) onboardingStatus = "Onboarding: Ready";
            Current = new TrayStatusSnapshot(state, message, _clock(), pendingPairs, lastReconnectMs, onboardingStatus, _buildVersion);
            OnStatusChanged?.Invoke(Current);
        }
    }
}
