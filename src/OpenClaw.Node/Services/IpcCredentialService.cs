using System;
using System.Security.Cryptography;

namespace OpenClaw.Node.Services
{
    internal sealed class IpcCredentialService
    {
        private sealed class Credential { public string Token { get; set; } = string.Empty; }
        private readonly SecureStore _store;

        public IpcCredentialService(SecureStore? store = null) => _store = store ?? new SecureStore();

        public string LoadOrCreate()
        {
            var existing = _store.Load<Credential>("ipc-credential");
            if (!string.IsNullOrWhiteSpace(existing?.Token)) return existing.Token;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _store.Save("ipc-credential", new Credential { Token = token });
            return token;
        }
    }
}
