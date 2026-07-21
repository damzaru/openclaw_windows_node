using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Node.Services
{
    /// <summary>
    /// Windows-owned execution policy. This intentionally implements the
    /// Gateway host-native contract rather than the Gateway's file-backed
    /// ~/.openclaw/exec-approvals.json format.
    /// </summary>
    internal static class ExecApprovalsStore
    {
        internal const string Deny = "deny";
        internal const string Allow = "allow";
        internal const string Prompt = "prompt";

        internal sealed class NativeRule
        {
            public string Pattern { get; set; } = string.Empty;
            public string Action { get; set; } = Deny;
            public List<string>? Shells { get; set; }
            public string? Description { get; set; }
            public bool? Enabled { get; set; }
        }

        internal sealed class NativePolicy
        {
            public string? DefaultAction { get; set; }
            public List<NativeRule> Rules { get; set; } = new();
        }

        private sealed class StoredPolicy
        {
            public int Version { get; set; } = 1;
            public string DefaultAction { get; set; } = Deny;
            public List<NativeRule> Rules { get; set; } = new();
        }

        internal sealed class NativeSnapshot
        {
            public bool Enabled { get; init; } = true;
            public string Hash { get; init; } = string.Empty;
            public string BaseHash { get; init; } = string.Empty;
            public string DefaultAction { get; init; } = Deny;
            public List<NativeRule> Rules { get; init; } = new();
            public object Constraints { get; init; } = BuildConstraints();
        }

        internal sealed class NativeSetParams
        {
            public string? DefaultAction { get; set; }
            public List<NativeRule>? Rules { get; set; }
            public string? BaseHash { get; set; }
        }

        internal sealed record Decision(string Action, NativeRule? Rule, string Shell);

        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        private static readonly HashSet<string> SupportedShells = new(StringComparer.OrdinalIgnoreCase)
        {
            "direct", "cmd", "powershell", "pwsh",
        };

        private static readonly HashSet<string> DangerousAllowExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
            "wscript", "wscript.exe", "cscript", "cscript.exe", "mshta", "mshta.exe",
            "rundll32", "rundll32.exe", "regsvr32", "regsvr32.exe", "certutil", "certutil.exe",
        };

        public static NativeSnapshot ReadSnapshot()
        {
            lock (Gate)
            {
                var policy = ReadPolicyUnsafe();
                return ToSnapshot(policy);
            }
        }

        public static NativeSnapshot Save(NativePolicy incoming, string? baseHash)
        {
            lock (Gate)
            {
                var current = ReadPolicyUnsafe();
                var currentSnapshot = ToSnapshot(current);
                if (string.IsNullOrWhiteSpace(baseHash))
                {
                    throw new InvalidOperationException("INVALID_REQUEST: exec approvals base hash required; reload and retry");
                }
                if (!string.Equals(baseHash.Trim(), currentSnapshot.Hash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("INVALID_REQUEST: exec approvals changed; reload and retry");
                }

                var next = NormalizeAndValidate(incoming, current.DefaultAction);
                WritePolicyUnsafe(next);
                return ToSnapshot(next);
            }
        }

        public static object ToPayload(NativeSnapshot snapshot) => new
        {
            enabled = true,
            hash = snapshot.Hash,
            baseHash = snapshot.Hash,
            defaultAction = snapshot.DefaultAction,
            rules = snapshot.Rules,
            constraints = snapshot.Constraints,
        };

        public static NativeSetParams DecodeSetParams(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new NativeSetParams();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("INVALID_REQUEST: exec approvals policy must be an object");
            }
            var allowed = new HashSet<string>(new[] { "defaultAction", "rules", "baseHash" }, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                    throw new InvalidOperationException($"INVALID_REQUEST: unknown exec approvals field: {property.Name}");
            }
            if (document.RootElement.TryGetProperty("rules", out var rules))
            {
                if (rules.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("INVALID_REQUEST: exec approvals rules must be an array");
                var allowedRuleFields = new HashSet<string>(new[] { "pattern", "action", "shells", "description", "enabled" }, StringComparer.Ordinal);
                var index = 0;
                foreach (var rule in rules.EnumerateArray())
                {
                    index++;
                    if (rule.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException($"INVALID_REQUEST: rule {index} must be an object");
                    foreach (var property in rule.EnumerateObject())
                    {
                        if (!allowedRuleFields.Contains(property.Name))
                            throw new InvalidOperationException($"INVALID_REQUEST: unknown exec approvals rule field: {property.Name}");
                    }
                }
            }
            return JsonSerializer.Deserialize<NativeSetParams>(json, JsonOptions) ?? new NativeSetParams();
        }

        public static Decision Evaluate(string commandText, IReadOnlyList<string> argv)
        {
            var snapshot = ReadSnapshot();
            var shell = ResolveShell(argv.Count > 0 ? argv[0] : string.Empty);
            foreach (var rule in snapshot.Rules)
            {
                if (rule.Enabled == false) continue;
                if (rule.Shells is { Count: > 0 } && !rule.Shells.Contains(shell, StringComparer.OrdinalIgnoreCase)) continue;
                if (RuleMatches(rule.Pattern, commandText, argv)) return new Decision(rule.Action, rule, shell);
            }
            return new Decision(snapshot.DefaultAction, null, shell);
        }

        private static StoredPolicy ReadPolicyUnsafe()
        {
            var path = ResolvePath();
            if (!File.Exists(path)) return new StoredPolicy();
            try
            {
                var parsed = JsonSerializer.Deserialize<StoredPolicy>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
                return parsed == null
                    ? new StoredPolicy()
                    : NormalizeAndValidate(new NativePolicy { DefaultAction = parsed.DefaultAction, Rules = parsed.Rules }, Deny);
            }
            catch
            {
                // Corrupt or unsafe state is never interpreted as permission.
                return new StoredPolicy();
            }
        }

        private static void WritePolicyUnsafe(StoredPolicy policy)
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policy, JsonOptions) + Environment.NewLine);
            SecureStore.AtomicWrite(path, bytes);
        }

        private static StoredPolicy NormalizeAndValidate(NativePolicy incoming, string currentDefault)
        {
            if (incoming.Rules == null) throw new InvalidOperationException("INVALID_REQUEST: exec approvals rules are required");
            if (incoming.Rules.Count > 512) throw new InvalidOperationException("INVALID_REQUEST: exec approvals supports at most 512 rules");
            var defaultAction = NormalizeAction(incoming.DefaultAction ?? currentDefault, "defaultAction");
            if (defaultAction == Allow)
            {
                throw new InvalidOperationException("INVALID_REQUEST: defaultAction=allow is not permitted on Windows");
            }

            var rules = new List<NativeRule>(incoming.Rules.Count);
            for (var index = 0; index < incoming.Rules.Count; index++)
            {
                var rule = incoming.Rules[index] ?? throw new InvalidOperationException($"INVALID_REQUEST: rule {index + 1} must be an object");
                var pattern = rule.Pattern?.Trim() ?? string.Empty;
                if (pattern.Length == 0 || pattern.Length > 1024)
                    throw new InvalidOperationException($"INVALID_REQUEST: rule {index + 1} requires a pattern of at most 1024 characters");
                var action = NormalizeAction(rule.Action, $"rule {index + 1} action");
                var shells = rule.Shells?.Select(shell => shell?.Trim() ?? string.Empty).ToList();
                if (shells is { Count: > 16 } || shells?.Any(shell => shell.Length == 0 || !SupportedShells.Contains(shell)) == true)
                    throw new InvalidOperationException($"INVALID_REQUEST: rule {index + 1} contains an unsupported shell");
                shells = shells?.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var description = rule.Description?.Trim();
                if (description?.Length > 1024) throw new InvalidOperationException($"INVALID_REQUEST: rule {index + 1} description is too long");
                if (action == Allow) ValidateAllowRule(pattern, index + 1);
                rules.Add(new NativeRule
                {
                    Pattern = pattern,
                    Action = action,
                    Shells = shells is { Count: > 0 } ? shells : null,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    Enabled = rule.Enabled,
                });
            }
            return new StoredPolicy { DefaultAction = defaultAction, Rules = rules };
        }

        private static string NormalizeAction(string? value, string label)
        {
            var action = value?.Trim().ToLowerInvariant() ?? string.Empty;
            return action is Allow or Deny or Prompt
                ? action
                : throw new InvalidOperationException($"INVALID_REQUEST: {label} must be allow, deny, or prompt");
        }

        private static void ValidateAllowRule(string pattern, int index)
        {
            var trimmed = pattern.Trim();
            if (trimmed.All(character => character is '*' or '?' or ' ' or '\t'))
                throw new InvalidOperationException($"INVALID_REQUEST: rule {index} is too broad to allow");
            var executablePattern = FirstToken(trimmed);
            if (executablePattern.IndexOfAny(new[] { '*', '?' }) >= 0)
                throw new InvalidOperationException($"INVALID_REQUEST: rule {index} may not wildcard the executable");
            var baseName = Path.GetFileName(executablePattern.Trim('"'));
            if (DangerousAllowExecutables.Contains(baseName))
                throw new InvalidOperationException($"INVALID_REQUEST: rule {index} may not allow a dangerous shell or loader");
        }

        private static bool RuleMatches(string pattern, string commandText, IReadOnlyList<string> argv)
        {
            if (argv.Count == 0) return false;
            var hasCommandShape = pattern.Any(char.IsWhiteSpace) || pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
            if (hasCommandShape) return GlobMatch(pattern, commandText);
            var executable = argv[0];
            var baseName = Path.GetFileName(executable);
            var stem = Path.GetFileNameWithoutExtension(executable);
            return string.Equals(pattern, executable, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pattern, baseName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pattern, stem, StringComparison.OrdinalIgnoreCase);
        }

        private static bool GlobMatch(string pattern, string value)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }

        private static string ResolveShell(string executable)
        {
            var baseName = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            return baseName switch
            {
                "cmd" => "cmd",
                "powershell" => "powershell",
                "pwsh" => "pwsh",
                _ => "direct",
            };
        }

        private static string FirstToken(string command)
        {
            if (command.StartsWith('"'))
            {
                var close = command.IndexOf('"', 1);
                return close > 1 ? command[1..close] : command.Trim('"');
            }
            var whitespace = command.IndexOfAny(new[] { ' ', '\t' });
            return whitespace < 0 ? command : command[..whitespace];
        }

        private static NativeSnapshot ToSnapshot(StoredPolicy policy)
        {
            var canonical = JsonSerializer.SerializeToUtf8Bytes(policy, new JsonSerializerOptions(JsonOptions) { WriteIndented = false });
            var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
            return new NativeSnapshot
            {
                Hash = hash,
                BaseHash = hash,
                DefaultAction = policy.DefaultAction,
                Rules = policy.Rules,
            };
        }

        private static object BuildConstraints() => new
        {
            baseHashRequired = true,
            defaultAllowAllowed = false,
            broadAllowRulesAllowed = false,
            dangerousAllowRulesAllowed = false,
        };

        private static string ResolvePath()
        {
            var overrideDirectory = Environment.GetEnvironmentVariable("OPENCLAW_WINDOWS_HOME")?.Trim();
            var directory = !string.IsNullOrWhiteSpace(overrideDirectory)
                ? overrideDirectory!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClaw Companion");
            return Path.Combine(directory, "exec-approvals-native.json");
        }
    }
}
