using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenClaw.Node.Services
{
    internal sealed class BundledBrowserRuntimeDescriptor
    {
        public string AppBaseDirectory { get; init; } = string.Empty;
        public string RuntimeRootDirectory { get; init; } = string.Empty;
        public string NodeExecutablePath { get; init; } = string.Empty;
        public string McpWorkingDirectory { get; init; } = string.Empty;
        public string McpEntrypointPath { get; init; } = string.Empty;
        public string BrowserUrl { get; init; } = "http://127.0.0.1:9222";
        public string BackendMode { get; init; } = "managed-or-existing-bundled";

        public object ToManifestModel() => new
        {
            backendMode = BackendMode,
            browserUrl = BrowserUrl,
            appBaseDirectory = AppBaseDirectory,
            runtimeRootDirectory = RuntimeRootDirectory,
            nodeExecutablePath = NodeExecutablePath,
            mcpWorkingDirectory = McpWorkingDirectory,
            mcpEntrypointPath = McpEntrypointPath,
        };
    }

    internal static class BundledBrowserRuntimeLocator
    {
        public const string DefaultBrowserUrl = "http://127.0.0.1:9222";
        public const string BackendMode = "managed-or-existing-bundled";

        public static BundledBrowserRuntimeDescriptor Locate()
        {
            var appBaseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var runtimeRootDirectory = Path.Combine(appBaseDirectory, "runtimes", "browser");

            if (!Directory.Exists(runtimeRootDirectory))
            {
                throw new InvalidOperationException(
                    $"browser backend not installed correctly: runtime root missing ({runtimeRootDirectory}). " +
                    "Publish the bundled browser runtime under runtimes/browser.");
            }

            var nodeExecutablePath = ResolveNodeExecutable(runtimeRootDirectory);
            var (mcpWorkingDirectory, mcpEntrypointPath) = ResolveMcpEntrypoint(runtimeRootDirectory);

            return new BundledBrowserRuntimeDescriptor
            {
                AppBaseDirectory = appBaseDirectory,
                RuntimeRootDirectory = runtimeRootDirectory,
                NodeExecutablePath = nodeExecutablePath,
                McpWorkingDirectory = mcpWorkingDirectory,
                McpEntrypointPath = mcpEntrypointPath,
                BrowserUrl = DefaultBrowserUrl,
                BackendMode = BackendMode,
            };
        }

        public static object DescribeForManifest()
        {
            var expectedMcpLayout = new[]
            {
                Path.Combine("runtimes", "browser", "chrome-devtools-mcp", "build", "src", "bin", "chrome-devtools-mcp.js"),
                Path.Combine("runtimes", "browser", "chrome-devtools-mcp", "dist", "server.js"),
                Path.Combine("runtimes", "browser", "mcp", "server.js"),
            };

            try
            {
                var descriptor = Locate();
                return new
                {
                    available = true,
                    mode = descriptor.BackendMode,
                    browserUrl = descriptor.BrowserUrl,
                    bundled = descriptor.ToManifestModel(),
                    requiresInstalledBrowser = true,
                    requiresRemoteDebugging = true,
                    expectedLayout = new
                    {
                        runtimeRoot = Path.Combine("runtimes", "browser"),
                        node = Path.Combine("runtimes", "browser", "node", OperatingSystem.IsWindows() ? "node.exe" : "node"),
                        mcp = expectedMcpLayout,
                    }
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    available = false,
                    mode = BackendMode,
                    browserUrl = DefaultBrowserUrl,
                    error = ex.Message,
                    requiresInstalledBrowser = true,
                    requiresRemoteDebugging = true,
                    expectedLayout = new
                    {
                        runtimeRoot = Path.Combine("runtimes", "browser"),
                        node = Path.Combine("runtimes", "browser", "node", OperatingSystem.IsWindows() ? "node.exe" : "node"),
                        mcp = expectedMcpLayout,
                    }
                };
            }
        }

        private static string ResolveNodeExecutable(string runtimeRootDirectory)
        {
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(runtimeRootDirectory, "node", "node.exe"));
            }
            else
            {
                candidates.Add(Path.Combine(runtimeRootDirectory, "node", "node"));
                candidates.Add(Path.Combine(runtimeRootDirectory, "node", "bin", "node"));
            }

            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null)
            {
                throw new InvalidOperationException(
                    "browser backend not installed correctly: bundled node runtime missing. " +
                    $"Expected one of: {string.Join(", ", candidates)}");
            }

            return Path.GetFullPath(found);
        }

        private static (string WorkingDirectory, string EntrypointPath) ResolveMcpEntrypoint(string runtimeRootDirectory)
        {
            var candidates = new[]
            {
                Path.Combine(runtimeRootDirectory, "chrome-devtools-mcp", "build", "src", "bin", "chrome-devtools-mcp.js"),
                Path.Combine(runtimeRootDirectory, "chrome-devtools-mcp", "dist", "server.js"),
                Path.Combine(runtimeRootDirectory, "chrome-devtools-mcp", "server.js"),
                Path.Combine(runtimeRootDirectory, "mcp", "server.js"),
                Path.Combine(runtimeRootDirectory, "mcp", "dist", "server.js"),
            };

            var entrypoint = candidates.FirstOrDefault(File.Exists);
            if (entrypoint == null)
            {
                throw new InvalidOperationException(
                    "browser backend not installed correctly: bundled chrome-devtools-mcp entrypoint missing. " +
                    $"Expected one of: {string.Join(", ", candidates)}");
            }

            var packageRoot = FindNearestPackageRoot(Path.GetDirectoryName(entrypoint) ?? runtimeRootDirectory);
            return (packageRoot, Path.GetFullPath(entrypoint));
        }

        private static string FindNearestPackageRoot(string startDirectory)
        {
            var current = Path.GetFullPath(startDirectory);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "package.json")))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return startDirectory;
        }
    }
}
