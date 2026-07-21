using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Node.Services
{
    public sealed class DeviceTokenStore
    {
        public sealed class Entry
        {
            public string GatewayId { get; set; } = string.Empty;
            public string DeviceId { get; set; } = string.Empty;
            public string Role { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
            public List<string> Scopes { get; set; } = new();
            public long UpdatedAtMs { get; set; }
        }

        private sealed class State
        {
            public List<Entry> Entries { get; set; } = new();
        }

        private readonly SecureStore _store;
        private static readonly object Gate = new();

        public DeviceTokenStore(SecureStore? store = null) => _store = store ?? new SecureStore();

        public Entry? Load(Uri gatewayUri, string deviceId, string role)
        {
            lock (Gate)
            {
                var gatewayId = StableGatewayId(gatewayUri);
                return LoadState().Entries.FirstOrDefault(entry =>
                    string.Equals(entry.GatewayId, gatewayId, StringComparison.Ordinal) &&
                    string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal) &&
                    string.Equals(entry.Role, NormalizeRole(role), StringComparison.Ordinal));
            }
        }

        public void Save(Uri gatewayUri, string deviceId, string role, string token, IEnumerable<string>? scopes)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            lock (Gate)
            {
                var state = LoadState();
                var gatewayId = StableGatewayId(gatewayUri);
                var normalizedRole = NormalizeRole(role);
                state.Entries.RemoveAll(entry =>
                    entry.GatewayId == gatewayId && entry.DeviceId == deviceId && entry.Role == normalizedRole);
                state.Entries.Add(new Entry
                {
                    GatewayId = gatewayId,
                    DeviceId = deviceId,
                    Role = normalizedRole,
                    Token = token.Trim(),
                    Scopes = (scopes ?? Array.Empty<string>()).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
                    UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                _store.Save("device-tokens", state);
            }
        }

        public void Clear(Uri gatewayUri, string deviceId, string role)
        {
            lock (Gate)
            {
                var state = LoadState();
                var gatewayId = StableGatewayId(gatewayUri);
                var normalizedRole = NormalizeRole(role);
                state.Entries.RemoveAll(entry =>
                    entry.GatewayId == gatewayId && entry.DeviceId == deviceId && entry.Role == normalizedRole);
                _store.Save("device-tokens", state);
            }
        }

        public static string StableGatewayId(Uri uri)
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.Host.ToLowerInvariant(),
                Fragment = string.Empty,
                Query = string.Empty,
            };
            var normalized = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        }

        private State LoadState() => _store.Load<State>("device-tokens") ?? new State();
        private static string NormalizeRole(string role) => role.Trim().ToLowerInvariant();
    }
}
