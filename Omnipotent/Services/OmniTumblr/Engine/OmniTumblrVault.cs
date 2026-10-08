using Omnipotent.Data_Handling;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Encrypts OmniTumblr's secrets at rest (consumer key/secret, OAuth tokens): AES-256-GCM with a
    /// per-purpose key derived (HKDF-SHA256) from a root key file. Binding each ciphertext to its purpose
    /// ("conn:{id}:token") means a token cannot be swapped into another slot. The root key is created
    /// atomically by <see cref="AtomicSecretRootKey"/>, as AccountRegistry does.
    /// </summary>
    internal sealed class OmniTumblrVault
    {
        public const string Prefix = "otv1:";
        private const int RootKeyBytes = 32;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;

        private readonly string? keyPath;
        private readonly Func<bool> hasCiphertext;
        private readonly Action<string> log;
        private readonly Func<string>? quarantine;
        private readonly object gate = new();
        private byte[]? rootKey;

        public OmniTumblrVault(string keyPath, Func<bool> hasCiphertext, Action<string> log, Func<string>? quarantine)
        {
            this.keyPath = keyPath;
            this.hasCiphertext = hasCiphertext;
            this.log = log;
            this.quarantine = quarantine;
        }

        /// <summary>In-memory key, for tests.</summary>
        internal OmniTumblrVault(byte[] key)
        {
            if (key.Length != RootKeyBytes) throw new ArgumentException("A 32-byte key is required.", nameof(key));
            rootKey = key.ToArray();
            hasCiphertext = () => false;
            log = _ => { };
        }

        public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

        /// <summary>
        /// Loads (or creates) the root key now. Called once at startup outside the store lock: the first
        /// load consults the store (does ciphertext exist?), so doing it lazily from inside a store
        /// mutation on one thread while another thread decrypts could deadlock the two locks.
        /// </summary>
        public void EnsureReady() => _ = RootKey();

        public string Protect(string plaintext, string purpose)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            byte[] key = DeriveKey(purpose);
            try
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                byte[] plain = Encoding.UTF8.GetBytes(plaintext);
                byte[] cipher = new byte[plain.Length];
                byte[] tag = new byte[TagBytes];
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
                byte[] blob = new byte[NonceBytes + TagBytes + cipher.Length];
                Buffer.BlockCopy(nonce, 0, blob, 0, NonceBytes);
                Buffer.BlockCopy(tag, 0, blob, NonceBytes, TagBytes);
                Buffer.BlockCopy(cipher, 0, blob, NonceBytes + TagBytes, cipher.Length);
                return Prefix + Convert.ToBase64String(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>Decrypts, or returns null when the value is absent, foreign or fails authentication.</summary>
        public string? Unprotect(string? value, string purpose)
        {
            if (string.IsNullOrEmpty(value) || !IsProtected(value)) return null;
            byte[] key = DeriveKey(purpose);
            try
            {
                byte[] blob = Convert.FromBase64String(value[Prefix.Length..]);
                if (blob.Length < NonceBytes + TagBytes) return null;
                byte[] nonce = blob[..NonceBytes];
                byte[] tag = blob[NonceBytes..(NonceBytes + TagBytes)];
                byte[] cipher = blob[(NonceBytes + TagBytes)..];
                byte[] plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        private byte[] DeriveKey(string purpose) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, RootKey(), 32, salt: null, info: Encoding.UTF8.GetBytes("omnitumblr:" + purpose));

        private byte[] RootKey()
        {
            if (rootKey != null) return rootKey;
            lock (gate)
            {
                if (rootKey != null) return rootKey;
                rootKey = AtomicSecretRootKey.LoadOrCreate(keyPath!, RootKeyBytes, hasCiphertext, RestrictPermissions, log, "OmniTumblr", quarantine);
                return rootKey;
            }
        }

        private static void RestrictPermissions(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var info = new FileInfo(path);
                    var security = info.GetAccessControl();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    var current = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    if (current != null)
                        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(current,
                            System.Security.AccessControl.FileSystemRights.FullControl,
                            System.Security.AccessControl.AccessControlType.Allow));
                    info.SetAccessControl(security);
                }
                else
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { /* best effort; the key still lives in the app's own data directory */ }
        }
    }
}
