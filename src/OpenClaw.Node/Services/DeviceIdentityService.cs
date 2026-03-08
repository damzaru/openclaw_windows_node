using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace OpenClaw.Node.Services
{
    public sealed class DeviceIdentityService
    {
        public sealed class DeviceIdentity
        {
            public string DeviceId { get; set; } = string.Empty;
            public string PublicKeyBase64Url { get; set; } = string.Empty;
            public string PrivateKeyBase64Url { get; set; } = string.Empty;
        }

        private sealed class StoredIdentity
        {
            public int Version { get; set; } = 1;
            public string DeviceId { get; set; } = string.Empty;
            public string PublicKeyBase64Url { get; set; } = string.Empty;
            public string PrivateKeyBase64Url { get; set; } = string.Empty;
            public long CreatedAtMs { get; set; }
        }

        public DeviceIdentity LoadOrCreate(string? filePath = null)
        {
            var path = ResolveIdentityPath(filePath);

            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var parsed = JsonSerializer.Deserialize<StoredIdentity>(json);
                    if (parsed != null &&
                        parsed.Version == 1 &&
                        !string.IsNullOrWhiteSpace(parsed.PublicKeyBase64Url) &&
                        !string.IsNullOrWhiteSpace(parsed.PrivateKeyBase64Url))
                    {
                        var pub = Base64UrlDecode(parsed.PublicKeyBase64Url);
                        var derived = DeriveDeviceId(pub);
                        if (!string.Equals(derived, parsed.DeviceId, StringComparison.Ordinal))
                        {
                            parsed.DeviceId = derived;
                            WriteStoredIdentity(path, parsed);
                        }

                        return new DeviceIdentity
                        {
                            DeviceId = parsed.DeviceId,
                            PublicKeyBase64Url = Base64UrlEncode(pub),
                            PrivateKeyBase64Url = Base64UrlEncode(Base64UrlDecode(parsed.PrivateKeyBase64Url)),
                        };
                    }
                }
            }
            catch
            {
                // fall through and regenerate
            }

            var created = GenerateIdentity();
            WriteStoredIdentity(path, new StoredIdentity
            {
                Version = 1,
                DeviceId = created.DeviceId,
                PublicKeyBase64Url = created.PublicKeyBase64Url,
                PrivateKeyBase64Url = created.PrivateKeyBase64Url,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            return created;
        }

        public string SignPayloadBase64Url(string privateKeyBase64Url, string payload)
        {
            var privateKey = new Ed25519PrivateKeyParameters(Base64UrlDecode(privateKeyBase64Url), 0);
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            signer.Init(true, privateKey);
            var bytes = Encoding.UTF8.GetBytes(payload);
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return Base64UrlEncode(signer.GenerateSignature());
        }

        public string BuildDeviceAuthPayload(
            string deviceId,
            string clientId,
            string clientMode,
            string role,
            string[] scopes,
            long signedAtMs,
            string? token,
            string nonce)
        {
            var scopesJoined = string.Join(',', scopes ?? Array.Empty<string>());
            var tokenSafe = token ?? string.Empty;
            return string.Join('|',
                "v2",
                deviceId,
                clientId,
                clientMode,
                role,
                scopesJoined,
                signedAtMs.ToString(),
                tokenSafe,
                nonce);
        }

        private static DeviceIdentity GenerateIdentity()
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var kp = gen.GenerateKeyPair();

            var pub = ((Ed25519PublicKeyParameters)kp.Public).GetEncoded();
            var priv = ((Ed25519PrivateKeyParameters)kp.Private).GetEncoded();
            var deviceId = DeriveDeviceId(pub);

            return new DeviceIdentity
            {
                DeviceId = deviceId,
                PublicKeyBase64Url = Base64UrlEncode(pub),
                PrivateKeyBase64Url = Base64UrlEncode(priv),
            };
        }

        private static string DeriveDeviceId(byte[] publicKeyRaw)
        {
            var hash = SHA256.HashData(publicKeyRaw);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ResolveIdentityPath(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "identity", "device.json");
        }

        private static void WriteStoredIdentity(string path, StoredIdentity stored)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            File.WriteAllText(path, json);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            var padded = s + new string('=', (4 - (s.Length % 4)) % 4);
            return Convert.FromBase64String(padded);
        }
    }
}
