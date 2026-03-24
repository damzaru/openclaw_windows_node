using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Node.Services
{
    internal static class ExecApprovalsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        internal sealed class ExecApprovalsSocket
        {
            public string? Path { get; set; }
            public string? Token { get; set; }
        }

        internal class ExecApprovalsDefaults
        {
            public string? Security { get; set; }
            public string? Ask { get; set; }
            public string? AskFallback { get; set; }
            public bool? AutoAllowSkills { get; set; }
        }

        internal sealed class ExecAllowlistEntry
        {
            public string? Id { get; set; }
            public string Pattern { get; set; } = string.Empty;
            public long? LastUsedAt { get; set; }
            public string? LastUsedCommand { get; set; }
            public string? LastResolvedPath { get; set; }
        }

        internal sealed class ExecApprovalsAgent : ExecApprovalsDefaults
        {
            public List<ExecAllowlistEntry>? Allowlist { get; set; }
        }

        internal sealed class ExecApprovalsFile
        {
            public int Version { get; set; } = 1;
            public ExecApprovalsSocket? Socket { get; set; }
            public ExecApprovalsDefaults? Defaults { get; set; }
            public Dictionary<string, ExecApprovalsAgent>? Agents { get; set; }
        }

        internal sealed class ExecApprovalsSnapshot
        {
            public string Path { get; set; } = string.Empty;
            public bool Exists { get; set; }
            public string Hash { get; set; } = string.Empty;
            public string? Raw { get; set; }
            public ExecApprovalsFile File { get; set; } = new();
        }

        internal sealed class ExecApprovalsSetParams
        {
            public ExecApprovalsFile? File { get; set; }
            public string? BaseHash { get; set; }
        }

        public static ExecApprovalsSnapshot ReadSnapshot()
        {
            var path = ResolvePath();
            if (!File.Exists(path))
            {
                return new ExecApprovalsSnapshot
                {
                    Path = path,
                    Exists = false,
                    Raw = null,
                    Hash = HashRaw(null),
                    File = Normalize(new ExecApprovalsFile()),
                };
            }

            var raw = File.ReadAllText(path, Encoding.UTF8);
            ExecApprovalsFile? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<ExecApprovalsFile>(raw, JsonOptions);
            }
            catch
            {
                parsed = null;
            }

            return new ExecApprovalsSnapshot
            {
                Path = path,
                Exists = true,
                Raw = raw,
                Hash = HashRaw(raw),
                File = Normalize(parsed ?? new ExecApprovalsFile()),
            };
        }

        public static ExecApprovalsSnapshot Save(ExecApprovalsFile incoming, string? baseHash)
        {
            var current = ReadSnapshot();
            RequireBaseHash(baseHash, current);

            var normalized = Normalize(incoming);
            normalized.Socket = MergeSocket(normalized.Socket, current.File.Socket);

            var path = current.Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ResolveBaseDir());
            var raw = JsonSerializer.Serialize(normalized, JsonOptions);
            File.WriteAllText(path, raw + Environment.NewLine, Encoding.UTF8);
            return ReadSnapshot();
        }

        public static object ToPayload(ExecApprovalsSnapshot snapshot)
        {
            return new
            {
                path = snapshot.Path,
                exists = snapshot.Exists,
                hash = snapshot.Hash,
                file = Redact(snapshot.File),
            };
        }

        public static ExecApprovalsSetParams DecodeSetParams(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ExecApprovalsSetParams();
            }
            return JsonSerializer.Deserialize<ExecApprovalsSetParams>(json, JsonOptions) ?? new ExecApprovalsSetParams();
        }

        private static string ResolveBaseDir()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".openclaw");
        }

        private static string ResolvePath() => System.IO.Path.Combine(ResolveBaseDir(), "exec-approvals.json");

        private static string HashRaw(string? raw)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(raw ?? string.Empty);
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        private static void RequireBaseHash(string? baseHash, ExecApprovalsSnapshot snapshot)
        {
            if (!snapshot.Exists)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(snapshot.Hash))
            {
                throw new InvalidOperationException("INVALID_REQUEST: exec approvals base hash unavailable; reload and retry");
            }
            var trimmed = baseHash?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("INVALID_REQUEST: exec approvals base hash required; reload and retry");
            }
            if (!string.Equals(trimmed, snapshot.Hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("INVALID_REQUEST: exec approvals changed; reload and retry");
            }
        }

        private static ExecApprovalsSocket? MergeSocket(ExecApprovalsSocket? incoming, ExecApprovalsSocket? current)
        {
            var path = (incoming?.Path ?? current?.Path)?.Trim();
            var token = (incoming?.Token ?? current?.Token)?.Trim();
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            return new ExecApprovalsSocket
            {
                Path = string.IsNullOrWhiteSpace(path) ? null : path,
                Token = string.IsNullOrWhiteSpace(token) ? null : token,
            };
        }

        private static ExecApprovalsFile Normalize(ExecApprovalsFile file)
        {
            file.Version = 1;
            file.Socket = NormalizeSocket(file.Socket);
            file.Defaults = NormalizeDefaults(file.Defaults);
            file.Agents = NormalizeAgents(file.Agents);
            return file;
        }

        private static ExecApprovalsSocket? NormalizeSocket(ExecApprovalsSocket? socket)
        {
            if (socket == null) return null;
            var path = socket.Path?.Trim();
            var token = socket.Token?.Trim();
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            return new ExecApprovalsSocket
            {
                Path = string.IsNullOrWhiteSpace(path) ? null : path,
                Token = string.IsNullOrWhiteSpace(token) ? null : token,
            };
        }

        private static ExecApprovalsDefaults? NormalizeDefaults(ExecApprovalsDefaults? defaults)
        {
            if (defaults == null) return null;
            defaults.Security = TrimOrNull(defaults.Security);
            defaults.Ask = TrimOrNull(defaults.Ask);
            defaults.AskFallback = TrimOrNull(defaults.AskFallback);
            if (defaults.Security == null && defaults.Ask == null && defaults.AskFallback == null && defaults.AutoAllowSkills == null)
            {
                return null;
            }
            return defaults;
        }

        private static Dictionary<string, ExecApprovalsAgent>? NormalizeAgents(Dictionary<string, ExecApprovalsAgent>? agents)
        {
            if (agents == null || agents.Count == 0) return null;
            var next = new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal);
            foreach (var pair in agents)
            {
                var key = pair.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;
                var normalized = NormalizeAgent(pair.Value);
                if (normalized != null)
                {
                    next[key] = normalized;
                }
            }
            return next.Count > 0 ? next : null;
        }

        private static ExecApprovalsAgent? NormalizeAgent(ExecApprovalsAgent? agent)
        {
            if (agent == null) return null;
            agent.Security = TrimOrNull(agent.Security);
            agent.Ask = TrimOrNull(agent.Ask);
            agent.AskFallback = TrimOrNull(agent.AskFallback);
            agent.Allowlist = NormalizeAllowlist(agent.Allowlist);
            if (agent.Security == null && agent.Ask == null && agent.AskFallback == null && agent.AutoAllowSkills == null && (agent.Allowlist == null || agent.Allowlist.Count == 0))
            {
                return null;
            }
            return agent;
        }

        private static List<ExecAllowlistEntry>? NormalizeAllowlist(List<ExecAllowlistEntry>? allowlist)
        {
            if (allowlist == null || allowlist.Count == 0) return null;
            var next = new List<ExecAllowlistEntry>();
            foreach (var entry in allowlist)
            {
                var pattern = entry?.Pattern?.Trim();
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                next.Add(new ExecAllowlistEntry
                {
                    Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString() : entry.Id,
                    Pattern = pattern,
                    LastUsedAt = entry.LastUsedAt,
                    LastUsedCommand = TrimOrNull(entry.LastUsedCommand),
                    LastResolvedPath = TrimOrNull(entry.LastResolvedPath),
                });
            }
            return next.Count > 0 ? next : null;
        }

        private static ExecApprovalsFile Redact(ExecApprovalsFile file)
        {
            return new ExecApprovalsFile
            {
                Version = 1,
                Socket = string.IsNullOrWhiteSpace(file.Socket?.Path)
                    ? null
                    : new ExecApprovalsSocket { Path = file.Socket!.Path!.Trim() },
                Defaults = file.Defaults == null ? null : new ExecApprovalsDefaults
                {
                    Security = file.Defaults.Security,
                    Ask = file.Defaults.Ask,
                    AskFallback = file.Defaults.AskFallback,
                    AutoAllowSkills = file.Defaults.AutoAllowSkills,
                },
                Agents = file.Agents?.ToDictionary(
                    pair => pair.Key,
                    pair => new ExecApprovalsAgent
                    {
                        Security = pair.Value.Security,
                        Ask = pair.Value.Ask,
                        AskFallback = pair.Value.AskFallback,
                        AutoAllowSkills = pair.Value.AutoAllowSkills,
                        Allowlist = pair.Value.Allowlist?.Select(entry => new ExecAllowlistEntry
                        {
                            Id = entry.Id,
                            Pattern = entry.Pattern,
                            LastUsedAt = entry.LastUsedAt,
                            LastUsedCommand = entry.LastUsedCommand,
                            LastResolvedPath = entry.LastResolvedPath,
                        }).ToList(),
                    },
                    StringComparer.Ordinal),
            };
        }

        private static string? TrimOrNull(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }
}
