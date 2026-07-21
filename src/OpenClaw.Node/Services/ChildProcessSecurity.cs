using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OpenClaw.Node.Services
{
    internal static class ChildProcessSecurity
    {
        public static void ScrubSensitiveEnvironment(ProcessStartInfo startInfo)
        {
            foreach (var key in startInfo.Environment.Keys.ToArray())
            {
                if (IsSensitiveEnvironmentKey(key)) startInfo.Environment.Remove(key);
            }
        }

        public static Dictionary<string, string?> SensitiveEnvironmentRemovals()
        {
            var removals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && IsSensitiveEnvironmentKey(key)) removals[key] = null;
            }
            return removals;
        }

        public static bool IsSensitiveEnvironmentKey(string key)
        {
            var normalized = key.ToUpperInvariant();
            return normalized.StartsWith("OPENCLAW_", StringComparison.Ordinal) ||
                   normalized.Contains("GATEWAY_TOKEN", StringComparison.Ordinal) ||
                   normalized.EndsWith("_API_KEY", StringComparison.Ordinal) ||
                   normalized.EndsWith("_CLIENT_SECRET", StringComparison.Ordinal);
        }
    }
}
