using System.Security.Cryptography;

namespace Omnipotent.Services.KliveGames.Runtime
{
    public static class FileHashing
    {
        public static string Sha1(string path) => Hash(path, SHA1.Create());
        public static string Sha256(string path) => Hash(path, SHA256.Create());

        private static string Hash(string path, HashAlgorithm algo)
        {
            using (algo)
            using (var fs = File.OpenRead(path))
                return Convert.ToHexString(algo.ComputeHash(fs)).ToLowerInvariant();
        }

        /// <summary>Throws if the file's hash doesn't match an expected (non-empty) value.</summary>
        public static void VerifyOrThrow(string path, string? expectedSha1 = null, string? expectedSha256 = null)
        {
            if (!string.IsNullOrEmpty(expectedSha1))
            {
                var actual = Sha1(path);
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Checksum mismatch (sha1) for {Path.GetFileName(path)}: expected {expectedSha1}, got {actual}.");
            }
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                var actual = Sha256(path);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Checksum mismatch (sha256) for {Path.GetFileName(path)}: expected {expectedSha256}, got {actual}.");
            }
        }
    }
}
