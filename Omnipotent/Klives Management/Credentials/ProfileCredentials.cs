using System.Security.Cryptography;
using System.Text;
using Omnipotent.Data_Handling;

namespace Omnipotent.Profiles.Credentials
{
    /// <summary>
    /// How profile passwords are stored. Login has no username — the password alone identifies the
    /// profile — so a lookup must be possible without the plaintext:
    /// <list type="bullet">
    /// <item><b>Lookup</b>: HMAC-SHA256 under a local secret key. Deterministic, so login and
    /// password API access are an O(1) dictionary hit; useless offline without the key.</item>
    /// <item><b>Hash</b>: salted PBKDF2-SHA256, checked once per login. If the lookup key is ever
    /// lost, logins fall back to verifying hashes directly and rebuild their lookups.</item>
    /// </list>
    /// </summary>
    public sealed class ProfileCredentials
    {
        public const int Pbkdf2Iterations = 600_000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int LookupKeyBytes = 32;
        private const string HashPrefix = "pbkdf2-sha256";

        /// <summary>Every session token starts with this, so the pipeline can tell it from a password.</summary>
        public const string SessionTokenPrefix = "kms_";

        private readonly string keyPath;
        private readonly Func<bool> hasLookups;
        private readonly Action<string> log;
        private readonly object keyGate = new();
        private byte[]? lookupKey;

        public ProfileCredentials(string keyPath, Func<bool> hasLookups, Action<string> log)
        {
            this.keyPath = keyPath;
            this.hasLookups = hasLookups;
            this.log = log;
        }

        /// <summary>
        /// True when the lookup key had to be regenerated (lost or corrupt): stored lookups no longer
        /// match, so logins must verify PBKDF2 hashes directly until each profile signs in again.
        /// </summary>
        public bool LookupsStale { get; private set; }

        private byte[] Key()
        {
            if (lookupKey != null) return lookupKey;
            lock (keyGate)
            {
                if (lookupKey != null) return lookupKey;
                lookupKey = AtomicSecretRootKey.LoadOrCreate(keyPath, LookupKeyBytes, hasLookups, RestrictKeyFilePermissions, log,
                    "KMProfiles credential lookup", () =>
                    {
                        // Nothing is lost: every profile still has its PBKDF2 hash. Lookups are
                        // rebuilt as profiles sign in.
                        LookupsStale = true;
                        return "the password lookup index (rebuilt from password hashes at each profile's next login)";
                    });
                return lookupKey;
            }
        }

        /// <summary>
        /// Detects a regenerated key. <see cref="AtomicSecretRootKey"/> only reports a <i>corrupt</i>
        /// key; a deleted key file is silently recreated, which would orphan every stored lookup. A
        /// small check value written beside the key makes that case visible too.
        /// </summary>
        public void VerifyKeyCheck(string checkFilePath)
        {
            string current = Convert.ToHexString(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes("km-lookup-check-v1")));
            try
            {
                if (File.Exists(checkFilePath))
                {
                    string stored = File.ReadAllText(checkFilePath).Trim();
                    if (!string.Equals(stored, current, StringComparison.OrdinalIgnoreCase) && hasLookups())
                    {
                        LookupsStale = true;
                        log("KMProfiles: the credential lookup key changed since profiles were last saved. " +
                            "Logins will verify password hashes directly and rebuild their lookups.");
                    }
                }
                else if (hasLookups())
                {
                    // Lookups exist but no check was ever written: be conservative.
                    LookupsStale = true;
                }
                File.WriteAllText(checkFilePath, current);
            }
            catch (Exception ex)
            {
                log("KMProfiles: could not verify the credential lookup key check: " + ex.Message);
            }
        }

        public string ComputeLookup(string password)
        {
            byte[] mac = HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(password ?? string.Empty));
            return Convert.ToHexString(mac);
        }

        public static string HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), salt,
                Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
            return $"{HashPrefix}${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool VerifyPassword(string password, string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return false;
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != HashPrefix) return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations < 1) return false;
            try
            {
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), salt,
                    iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException) { return false; }
        }

        /// <summary>A readable random password (no look-alike characters), ~119 bits for 20 chars.</summary>
        public static string GeneratePassword(int length = 20)
        {
            const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            return sb.ToString();
        }

        public static string NewSessionToken()
            => SessionTokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        public static string NewSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        /// <summary>Only token hashes are stored; a leaked sessions file cannot be replayed.</summary>
        public static string HashToken(string token)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

        public static bool IsSessionToken(string? credential)
            => credential != null && credential.StartsWith(SessionTokenPrefix, StringComparison.Ordinal);

        /// <summary>Strips an optional "Bearer " scheme from an Authorization value.</summary>
        public static string? NormalizeCredential(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = raw.Trim();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
            return value.Length == 0 ? null : value;
        }

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static void RestrictKeyFilePermissions(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var info = new FileInfo(path);
                    var sec = info.GetAccessControl();
                    sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    var current = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    if (current != null)
                        sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                            current,
                            System.Security.AccessControl.FileSystemRights.FullControl,
                            System.Security.AccessControl.AccessControlType.Allow));
                    info.SetAccessControl(sec);
                }
                else
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { /* best-effort; the key still lives under the app's private data dir */ }
        }
    }
}
