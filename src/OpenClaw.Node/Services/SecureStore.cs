using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Node.Services
{
    /// <summary>
    /// Small CurrentUser-DPAPI store for companion-owned secrets. Gateway
    /// configuration remains an import-only compatibility source.
    /// </summary>
    public sealed class SecureStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenClaw.Windows.Companion.v2");
        private readonly string _baseDirectory;

        public SecureStore(string? baseDirectory = null)
        {
            var overrideDirectory = Environment.GetEnvironmentVariable("OPENCLAW_WINDOWS_HOME")?.Trim();
            _baseDirectory = baseDirectory ?? (!string.IsNullOrWhiteSpace(overrideDirectory)
                ? overrideDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClaw Companion"));
        }

        public string BaseDirectory => _baseDirectory;

        public bool Exists(string name) => File.Exists(ResolvePath(name));

        public T? Load<T>(string name)
        {
            var path = ResolvePath(name);
            if (!File.Exists(path)) return default;

            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var bytes = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser)
                    : protectedBytes;
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            }
            catch
            {
                return default;
            }
        }

        public void Save<T>(string name, T value)
        {
            Directory.CreateDirectory(_baseDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var protectedBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes;
            AtomicWrite(ResolvePath(name), protectedBytes);
            CryptographicOperations.ZeroMemory(bytes);
        }

        public void Delete(string name)
        {
            var path = ResolvePath(name);
            if (File.Exists(path)) File.Delete(path);
        }

        private string ResolvePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Invalid secure-store name", nameof(name));
            }
            return Path.Combine(_baseDirectory, name + ".dat");
        }

        internal static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
