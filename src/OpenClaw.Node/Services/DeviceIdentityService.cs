using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private static readonly object IdentityGate = new();

        public sealed class DeviceIdentity
        {
            public string DeviceId { get; set; } = string.Empty;
            public string PublicKeyBase64Url { get; set; } = string.Empty;
            public string PrivateKeyBase64Url { get; set; } = string.Empty;
        }

        private sealed class StoredIdentity
        {
            public int Version { get; set; } = 2;
            public string DeviceId { get; set; } = string.Empty;
            public string PublicKeyBase64Url { get; set; } = string.Empty;
            public string PrivateKeyBase64Url { get; set; } = string.Empty;
            public long CreatedAtMs { get; set; }
            public string? ImportedFrom { get; set; }
        }

        private readonly SecureStore _secureStore;

        public DeviceIdentityService(SecureStore? secureStore = null) => _secureStore = secureStore ?? new SecureStore();

        public DeviceIdentity LoadOrCreate(string? filePath = null)
        {
            lock (IdentityGate)
            {
                // Explicit paths retain the old JSON behavior for development tools.
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    return LoadOrCreateLegacyFile(filePath);
                }

                var secure = _secureStore.Load<StoredIdentity>("device-identity");
                if (TryMaterialize(secure, out var existing)) return existing;

                var legacyPath = ResolveLegacyIdentityPath();
                var imported = TryReadLegacy(legacyPath);
                if (TryMaterialize(imported, out var legacyIdentity))
                {
                    imported!.Version = 2;
                    imported.ImportedFrom = legacyPath;
                    _secureStore.Save("device-identity", imported);
                    return legacyIdentity;
                }

                var created = GenerateIdentity();
                _secureStore.Save("device-identity", ToStored(created));
                return created;
            }
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
            string nonce,
            string? platform = null,
            string? deviceFamily = null)
        {
            var scopesJoined = string.Join(',', scopes ?? Array.Empty<string>());
            return string.Join('|',
                "v3",
                deviceId,
                clientId,
                clientMode,
                role,
                scopesJoined,
                signedAtMs.ToString(CultureInfo.InvariantCulture),
                token ?? string.Empty,
                nonce,
                NormalizeMetadata(platform),
                NormalizeMetadata(deviceFamily));
        }

        private DeviceIdentity LoadOrCreateLegacyFile(string path)
        {
            var parsed = TryReadLegacy(path);
            if (TryMaterialize(parsed, out var existing)) return existing;
            var created = GenerateIdentity();
            WriteLegacy(path, ToStored(created));
            return created;
        }

        private static StoredIdentity? TryReadLegacy(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryMaterialize(StoredIdentity? stored, out DeviceIdentity identity)
        {
            identity = new DeviceIdentity();
            if (stored == null || string.IsNullOrWhiteSpace(stored.PublicKeyBase64Url) || string.IsNullOrWhiteSpace(stored.PrivateKeyBase64Url)) return false;
            try
            {
                var publicKey = Base64UrlDecode(stored.PublicKeyBase64Url);
                var privateKey = Base64UrlDecode(stored.PrivateKeyBase64Url);
                if (publicKey.Length != Ed25519PublicKeyParameters.KeySize || privateKey.Length != Ed25519PrivateKeyParameters.KeySize) return false;
                identity = new DeviceIdentity
                {
                    DeviceId = DeriveDeviceId(publicKey),
                    PublicKeyBase64Url = Base64UrlEncode(publicKey),
                    PrivateKeyBase64Url = Base64UrlEncode(privateKey),
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static StoredIdentity ToStored(DeviceIdentity identity) => new()
        {
            Version = 2,
            DeviceId = identity.DeviceId,
            PublicKeyBase64Url = identity.PublicKeyBase64Url,
            PrivateKeyBase64Url = identity.PrivateKeyBase64Url,
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        private static DeviceIdentity GenerateIdentity()
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
            var privateKey = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
            return new DeviceIdentity
            {
                DeviceId = DeriveDeviceId(publicKey),
                PublicKeyBase64Url = Base64UrlEncode(publicKey),
                PrivateKeyBase64Url = Base64UrlEncode(privateKey),
            };
        }

        private static string NormalizeMetadata(string? value)
        {
            var input = value?.Trim() ?? string.Empty;
            if (input.Length == 0) return string.Empty;
            var chars = input.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch).ToArray();
            return new string(chars);
        }

        private static string DeriveDeviceId(byte[] publicKeyRaw)
            => Convert.ToHexString(SHA256.HashData(publicKeyRaw)).ToLowerInvariant();

        private static string ResolveLegacyIdentityPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "identity", "device.json");
        }

        private static void WriteLegacy(string path, StoredIdentity stored)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static byte[] Base64UrlDecode(string input)
        {
            var value = input.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(value + new string('=', (4 - value.Length % 4) % 4));
        }
    }
}
