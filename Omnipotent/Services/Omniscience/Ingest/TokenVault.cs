using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.Omniscience.Ingest
{
    /// <summary>
    /// Wraps DPAPI (LocalMachine scope) to encrypt/decrypt small secrets
    /// (Discord tokens etc.). DB blobs encrypted with this helper are non-portable
    /// across hosts \u2014 by design.
    /// </summary>
    public static class TokenVault
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Omniscience.HarvestSource.v1");

        public static byte[] Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return Array.Empty<byte>();
            var bytes = Encoding.UTF8.GetBytes(plaintext);
#pragma warning disable CA1416
            return ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
#pragma warning restore CA1416
        }

        public static string? Decrypt(byte[]? cipher)
        {
            if (cipher == null || cipher.Length == 0) return null;
            try
            {
#pragma warning disable CA1416
                var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
#pragma warning restore CA1416
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }
}
