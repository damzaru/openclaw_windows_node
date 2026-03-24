using System;
using System.Collections.Generic;

namespace OpenClaw.Node.Services
{
    internal static class CapabilityManifestService
    {
        public static object Build()
        {
            return new
            {
                buildVersion = BuildInfo.BuildVersion,
                appVersion = $"dev+{BuildInfo.BuildVersion}",
                device = new
                {
                    family = "Windows",
                    host = Environment.MachineName,
                },
                capabilities = new[]
                {
                    "browser",
                    "window",
                    "ui",
                    "screen",
                    "camera",
                    "system",
                    "notifications",
                    "microphone",
                },
                commands = new object[]
                {
                    new
                    {
                        name = "system.describe",
                        stable = true,
                        category = "system",
                        description = "Return a machine-readable manifest of node capabilities, routes, params, runtime caveats, and build info."
                    },
                    new
                    {
                        name = "browser.proxy",
                        stable = true,
                        category = "browser",
                        routes = new object[]
                        {
                            new { method = "GET", path = "/profiles", description = "List browser profiles/status overview" },
                            new { method = "GET", path = "/", description = "Browser runtime status" },
                            new { method = "GET", path = "/tabs", description = "List tabs with ids/titles/urls" },
                            new { method = "POST", path = "/tabs/open", description = "Open a tab", bodySchema = new { url = "string" } },
                            new { method = "POST", path = "/tabs/focus", description = "Focus a tab", bodySchema = new { targetId = "string" } },
                            new { method = "DELETE", path = "/tabs/:targetId", description = "Close a tab" },
                            new { method = "POST", path = "/navigate", description = "Navigate a tab", bodySchema = new { targetId = "string?", url = "string" } },
                            new { method = "POST", path = "/act", description = "Browser action route", supportedKinds = new[] { "evaluate" } },
                            new { method = "POST", path = "/screenshot", description = "Capture screenshot", bodySchema = new { targetId = "string", fullPage = "bool?", type = "png|jpeg?" } },
                            new { method = "GET", path = "/console", description = "Read buffered console entries", querySchema = new { targetId = "string?", level = "string?", limit = "int?", clear = "bool?" } },
                            new { method = "GET", path = "/requests", description = "Read buffered network requests", querySchema = new { targetId = "string?", filter = "string?", limit = "int?", clear = "bool?" } },
                            new { method = "GET", path = "/requests/matches", description = "List slim exact-match request candidates", querySchema = new { targetId = "string", filter = "string?", limit = "int?" } },
                            new { method = "GET", path = "/request-body", description = "Fetch exact request body by requestId", querySchema = new { targetId = "string", requestId = "string" } },
                            new { method = "GET", path = "/requests/latest-body", description = "Fetch latest matching request body convenience helper", querySchema = new { targetId = "string", filter = "string?" } },
                        },
                        notes = new[]
                        {
                            "Uses DevTools HTTP/CDP for stable tab lifecycle ops.",
                            "Uses persistent DevTools monitors for console/network live buffers.",
                            "Console/network buffers contain activity only after monitor attach.",
                            "Use exact requestId flows for highest debugging accuracy; latest-body is a convenience helper.",
                        }
                    },
                    new { name = "system.execApprovals.get", stable = true, category = "system" },
                    new { name = "system.execApprovals.set", stable = true, category = "system" },
                    new { name = "system.run.prepare", stable = true, category = "system" },
                    new { name = "system.run", stable = true, category = "system" },
                    new { name = "system.which", stable = true, category = "system" },
                    new { name = "system.notify", stable = true, category = "notifications" },
                    new { name = "screen.capture", stable = true, category = "screen" },
                    new { name = "screen.list", stable = true, category = "screen" },
                    new { name = "screen.record", stable = true, category = "screen" },
                    new { name = "camera.list", stable = true, category = "camera" },
                    new { name = "camera.snap", stable = true, category = "camera" },
                    new { name = "window.list", stable = true, category = "window" },
                    new { name = "window.focus", stable = true, category = "window" },
                    new { name = "window.rect", stable = true, category = "window" },
                    new { name = "input.type", stable = true, category = "ui" },
                    new { name = "input.key", stable = true, category = "ui" },
                    new { name = "input.click", stable = true, category = "ui" },
                    new { name = "input.scroll", stable = true, category = "ui" },
                    new { name = "input.click.relative", stable = true, category = "ui" },
                    new { name = "ui.find", stable = true, category = "ui" },
                    new { name = "ui.click", stable = true, category = "ui" },
                    new { name = "ui.type", stable = true, category = "ui" },
                },
                runtime = new
                {
                    browser = new
                    {
                        attachMode = "managed-or-existing",
                        browserUrl = BundledBrowserRuntimeLocator.DefaultBrowserUrl,
                        backendMode = BundledBrowserRuntimeLocator.BackendMode,
                        autoLaunchManagedBrowser = true,
                        requiresRemoteDebugging = true,
                        requiresInstalledBrowser = true,
                        requiresManualRemoteDebugging = false,
                        supportedBrowsers = new[] { "chrome", "edge" },
                        liveBuffers = new[] { "console", "requests" },
                        transports = new[]
                        {
                            "Bundled Node runtime launches bundled chrome-devtools-mcp by absolute path",
                            "Node-managed Chrome/Edge launch with dedicated remote-debugging profile when needed",
                            "DevTools HTTP for tab lifecycle",
                            "CDP websocket for navigate/evaluate/screenshot/body fetch",
                            "C# MCP SDK retained for selected browser actions/future expansion"
                        },
                        bundledRuntime = BundledBrowserRuntimeLocator.DescribeForManifest()
                    }
                }
            };
        }
    }
}
