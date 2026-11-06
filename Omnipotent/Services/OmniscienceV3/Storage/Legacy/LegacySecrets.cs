using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>
    /// v2 stored source tokens with DPAPI (LocalMachine scope, fixed entropy), so a blob only decrypts on the
    /// machine that wrote it. The migration copies blobs verbatim and only checks that each still decrypts here;
    /// the plaintext is never returned, logged or kept (the buffer is zeroed). A blob that fails means Klives has
    /// to re-add that token before v3 can use the source.
    /// </summary>
    public static class LegacySecrets
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Omniscience.HarvestSource.v1");

        public const string Empty = "empty";
        public const string Decryptable = "decryptable";
        public const string NotDecryptable = "not-decryptable";
        public const string Unavailable = "dpapi-unavailable";

        public static string Probe(byte[]? blob)
        {
            if (blob == null || blob.Length == 0) return Empty;
            if (!OperatingSystem.IsWindows()) return Unavailable;
            byte[]? plain = null;
            try
            {
                plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
                return plain.Length > 0 ? Decryptable : NotDecryptable;
            }
            catch (CryptographicException)
            {
                return NotDecryptable;
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }
    }
}
