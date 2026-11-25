using System;

namespace OpenClaw.Node.Services
{
    public sealed class CompanionSettings
    {
        public int Version { get; set; } = 1;
        public string GatewayUrl { get; set; } = "ws://127.0.0.1:18789";
        public string? GatewayToken { get; set; }
        public string? TlsCertificateSha256 { get; set; }
        public bool StartWithWindows { get; set; }
        public bool EnableBrowserProxy { get; set; } = true;
        public int BrowserDiskCacheMiB { get; set; } = 128;
        public int BrowserCodeCacheMiB { get; set; } = 64;
        public bool AllowUnrestrictedExecutionPolicy { get; set; }
        public bool EnableFileRead { get; set; }
        public bool EnableFileWrite { get; set; }
        public bool EnableWindowInspection { get; set; }
        public bool EnableComputerControl { get; set; }
        public bool EnableDesktopSharing { get; set; }
        public int DesktopVncPort { get; set; } = 5900;
        public string? DesktopVncPassword { get; set; }
        public bool EnableCanvas { get; set; } = true;
        public bool EnableLocation { get; set; }
        public bool EnableCameraSnapshots { get; set; }
        public bool EnableCameraClips { get; set; }
        public bool EnableScreenRecording { get; set; }
        public bool EnableTalkPushToTalk { get; set; }
        public string TalkSessionKey { get; set; } = "agent:main:main";
        public int MaximumCameraClipSeconds { get; set; } = 15;
        public int MaximumScreenRecordSeconds { get; set; } = 30;

        public void Normalize()
        {
            Version = 1;
            GatewayUrl = string.IsNullOrWhiteSpace(GatewayUrl) ? "ws://127.0.0.1:18789" : GatewayUrl.Trim();
            GatewayToken = NullIfWhiteSpace(GatewayToken);
            TlsCertificateSha256 = NullIfWhiteSpace(TlsCertificateSha256);
            DesktopVncPort = Math.Clamp(DesktopVncPort, 1, 65535);
            BrowserDiskCacheMiB = Math.Clamp(BrowserDiskCacheMiB, 16, 2048);
            BrowserCodeCacheMiB = Math.Clamp(BrowserCodeCacheMiB, 16, 2048);
            // Whitespace can be part of a VNC password; never change the credential.
            DesktopVncPassword = string.IsNullOrEmpty(DesktopVncPassword) ? null : DesktopVncPassword;
            TalkSessionKey = string.IsNullOrWhiteSpace(TalkSessionKey) ? "agent:main:main" : TalkSessionKey.Trim();
            MaximumCameraClipSeconds = Math.Clamp(MaximumCameraClipSeconds, 1, 60);
            MaximumScreenRecordSeconds = Math.Clamp(MaximumScreenRecordSeconds, 1, 120);
            if (!EnableCameraSnapshots) EnableCameraClips = false;
        }

        private static string? NullIfWhiteSpace(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class CompanionSettingsStore
    {
        private readonly SecureStore _store;

        public CompanionSettingsStore(SecureStore? store = null) => _store = store ?? new SecureStore();

        public bool HasSavedSettings => _store.Exists("companion-settings");

        public CompanionSettings Load()
        {
            var settings = _store.Load<CompanionSettings>("companion-settings") ?? new CompanionSettings();
            settings.Normalize();
            return settings;
        }

        public void Save(CompanionSettings settings)
        {
            settings.Normalize();
            _store.Save("companion-settings", settings);
        }
    }
}
