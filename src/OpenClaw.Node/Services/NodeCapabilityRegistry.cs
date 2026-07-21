using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Node.Services
{
    public sealed record NodeCommandSpec(string Name, string Capability, bool Dangerous = false, bool LocalOnly = false);

    public sealed class NodeCapabilityManifest
    {
        public required IReadOnlyList<string> Capabilities { get; init; }
        public required IReadOnlyList<string> GatewayCommands { get; init; }
        public required IReadOnlyList<string> LocalOnlyCommands { get; init; }
    }

    /// <summary>
    /// Single authority for advertised commands and their settings gates. The
    /// executor and connection manifest consume this same catalog so commands
    /// cannot accidentally be exposed merely because a handler exists.
    /// </summary>
    public static class NodeCapabilityRegistry
    {
        private static readonly NodeCommandSpec[] Catalog =
        {
            new("system.run.prepare", "system"),
            new("system.run", "system"),
            new("system.which", "system"),
            new("system.execApprovals.get", "system"),
            new("system.execApprovals.set", "system"),
            new("system.notify", "notifications"),
            new("fs.listDir", "system"),
            new("browser.proxy", "browser"),
            new("screen.snapshot", "screen"),
            new("screen.record", "screen", Dangerous: true),
            new("camera.list", "camera"),
            new("camera.snap", "camera", Dangerous: true),
            new("camera.clip", "camera", Dangerous: true),
            new("location.get", "location"),
            new("device.info", "device"),
            new("device.status", "device"),
            new("canvas.present", "canvas"),
            new("canvas.hide", "canvas"),
            new("canvas.navigate", "canvas"),
            new("canvas.eval", "canvas"),
            new("canvas.snapshot", "canvas"),
            new("canvas.a2ui.push", "canvas"),
            new("canvas.a2ui.pushJSONL", "canvas"),
            new("canvas.a2ui.reset", "canvas"),
            new("talk.ptt.start", "talk"),
            new("talk.ptt.stop", "talk"),
            new("talk.ptt.cancel", "talk"),
            new("talk.ptt.once", "talk"),

            // Compatibility surface deliberately confined to the same-user IPC server.
            new("ipc.ping", "system", LocalOnly: true),
            new("ipc.window.list", "window", LocalOnly: true),
            new("ipc.window.focus", "window", LocalOnly: true),
            new("ipc.window.rect", "window", LocalOnly: true),
            new("ipc.input.type", "ui", LocalOnly: true),
            new("ipc.input.key", "ui", LocalOnly: true),
            new("ipc.input.click", "ui", LocalOnly: true),
            new("ipc.input.scroll", "ui", LocalOnly: true),
            new("ipc.input.click.relative", "ui", LocalOnly: true),
        };

        public static NodeCapabilityManifest Build(CompanionSettings settings)
        {
            settings.Normalize();
            var enabled = Catalog.Where(spec => spec.LocalOnly || IsEnabled(spec.Name, settings)).ToArray();
            var gateway = enabled.Where(spec => !spec.LocalOnly).Select(spec => spec.Name).Distinct(StringComparer.Ordinal).Order().ToArray();
            var local = enabled.Where(spec => spec.LocalOnly).Select(spec => spec.Name).Distinct(StringComparer.Ordinal).Order().ToArray();
            var capabilities = enabled.Where(spec => !spec.LocalOnly).Select(spec => spec.Capability).Distinct(StringComparer.Ordinal).Order().ToArray();
            return new NodeCapabilityManifest
            {
                Capabilities = capabilities,
                GatewayCommands = gateway,
                LocalOnlyCommands = local,
            };
        }

        public static bool IsGatewayCommandEnabled(string command, CompanionSettings settings)
            => Catalog.Any(spec => !spec.LocalOnly && spec.Name == command && IsEnabled(command, settings));

        private static bool IsEnabled(string command, CompanionSettings settings) => command switch
        {
            "browser.proxy" => settings.EnableBrowserProxy,
            "screen.record" => settings.EnableScreenRecording,
            "camera.snap" => settings.EnableCameraSnapshots,
            "camera.clip" => settings.EnableCameraSnapshots && settings.EnableCameraClips,
            "location.get" => settings.EnableLocation,
            var value when value.StartsWith("canvas.", StringComparison.Ordinal) => settings.EnableCanvas,
            var value when value.StartsWith("talk.", StringComparison.Ordinal) => settings.EnableTalkPushToTalk,
            _ => true,
        };
    }
}
