using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Per-project credential vault (design doc §3/§8). Deliberately NOT an OmniSetting and NOT
    /// HostControl's EncryptedMemoryStore: that store is DPAPI-CurrentUser scoped (ciphertext
    /// dies if the Windows account changes), single flat namespace, and Windows-only — none of
    /// which suits an unattended 24/7 server with N isolated projects.
    ///
    /// Design:
    ///   * AES-GCM (BCL, no new package). Authenticated encryption; tamper is detected.
    ///   * One root key held by Omnipotent, portable (a key file with restrictive ACLs, created
    ///     on first use), independent of Windows user identity — survives account changes and
    ///     works cross-platform.
    ///   * Per-project data key derived via HKDF-SHA256 with the projectID as the context, so
    ///     every project is cryptographically isolated and rotating the root rotates all of them.
    ///   * Values never leave the host: the vault decrypts host-side only, at input-injection
    ///     time (ContainerToolAdapter's resolveSecrets delegate), the same boundary
    ///     SecretSubstituter enforces. Containers never hold plaintext at rest.
    ///   * A names-only index per project lets an agent enumerate what exists without ever
    ///     seeing a value — the "write/reference by name, never read back" contract.
    ///
    /// Layout: Projects/Vaults/&lt;projectID&gt;.vault.json (name → ciphertext) and root.key.
    /// </summary>
    public class ProjectVault
    {
        private readonly string vaultsDir;
        private readonly string rootKeyPath;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        private byte[]? rootKey;
        private readonly object rootKeyGate = new();

        private const int NonceBytes = 12;   // AES-GCM standard nonce
        private const int TagBytes = 16;      // AES-GCM tag
        private const int RootKeyBytes = 32;  // AES-256

        private sealed class VaultEntry
        {
            public string Name { get; set; } = "";
            public string CipherB64 { get; set; } = ""; // nonce | tag | ciphertext
        }

        private sealed class VaultFile
        {
            public List<VaultEntry> Entries { get; set; } = new();
        }

        public ProjectVault(Action<string> log)
        {
            this.log = log ?? (_ => { });
            vaultsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsVaultsDirectory);
            Directory.CreateDirectory(vaultsDir);
            rootKeyPath = Path.Combine(vaultsDir, "root.key");
        }

        internal ProjectVault(Action<string> log, string vaultsDir)
        {
            this.log = log ?? (_ => { });
            this.vaultsDir = Path.GetFullPath(vaultsDir);
            Directory.CreateDirectory(this.vaultsDir);
            rootKeyPath = Path.Combine(this.vaultsDir, "root.key");
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string VaultPath(string projectID) => Path.Combine(vaultsDir, projectID + ".vault.json");

        // ── root key ──

        private byte[] RootKey()
        {
            if (rootKey != null) return rootKey;
            lock (rootKeyGate)
            {
                if (rootKey != null) return rootKey;
                rootKey = AtomicSecretRootKey.LoadOrCreate(rootKeyPath, RootKeyBytes,
                    HasEncryptedSecretsOnDisk, RestrictKeyFilePermissions, log, "ProjectVault");
                return rootKey;
            }
        }

        private bool HasEncryptedSecretsOnDisk()
        {
            foreach (string path in Directory.EnumerateFiles(vaultsDir, "*.vault.json", SearchOption.TopDirectoryOnly))
            {
                var file = JsonConvert.DeserializeObject<VaultFile>(File.ReadAllText(path))
                    ?? new VaultFile();
                if (file.Entries.Any(e => !string.IsNullOrWhiteSpace(e.CipherB64))) return true;
            }
            return false;
        }

        /// <summary>HKDF-SHA256(rootKey, info = "projectvault:" + projectID) → a 32-byte per-project key.</summary>
        private byte[] DeriveProjectKey(string projectID)
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: RootKey(),
                outputLength: 32,
                salt: null,
                info: Encoding.UTF8.GetBytes("projectvault:" + projectID));
        }

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
            catch { /* best-effort hardening; the key file still lives under the app's private data dir */ }
        }

        // ── crypto ──

        private string Encrypt(string projectID, string plaintext)
        {
            byte[] key = DeriveProjectKey(projectID);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            byte[] plain = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[TagBytes];
            using (var gcm = new AesGcm(key, TagBytes))
                gcm.Encrypt(nonce, plain, cipher, tag);
            CryptographicOperations.ZeroMemory(key);

            byte[] blob = new byte[NonceBytes + TagBytes + cipher.Length];
            Buffer.BlockCopy(nonce, 0, blob, 0, NonceBytes);
            Buffer.BlockCopy(tag, 0, blob, NonceBytes, TagBytes);
            Buffer.BlockCopy(cipher, 0, blob, NonceBytes + TagBytes, cipher.Length);
            return Convert.ToBase64String(blob);
        }

        private string? Decrypt(string projectID, string cipherB64)
        {
            try
            {
                byte[] blob = Convert.FromBase64String(cipherB64);
                if (blob.Length < NonceBytes + TagBytes) return null;
                byte[] nonce = blob[..NonceBytes];
                byte[] tag = blob[NonceBytes..(NonceBytes + TagBytes)];
                byte[] cipher = blob[(NonceBytes + TagBytes)..];
                byte[] plain = new byte[cipher.Length];
                byte[] key = DeriveProjectKey(projectID);
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Decrypt(nonce, cipher, tag, plain);
                CryptographicOperations.ZeroMemory(key);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; } // authentication failure or corrupt entry
        }

        // ── public API (mirrors EncryptedMemoryStore's contract, project-scoped) ──

        public void Save(string projectID, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Vault entry name is required.", nameof(name));
            lock (LockFor(projectID))
            {
                var file = LoadLocked(projectID);
                file.Entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                file.Entries.Add(new VaultEntry { Name = name, CipherB64 = Encrypt(projectID, value ?? "") });
                SaveLocked(projectID, file);
            }
        }

        /// <summary>Decrypts a secret. ONLY called host-side at input-injection time; never returned to a model.</summary>
        public string? GetDecrypted(string projectID, string name)
        {
            lock (LockFor(projectID))
            {
                var file = LoadLocked(projectID);
                var entry = file.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                return entry == null ? null : Decrypt(projectID, entry.CipherB64);
            }
        }

        public List<string> ListNames(string projectID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public bool Exists(string projectID, string name)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool Delete(string projectID, string name)
        {
            lock (LockFor(projectID))
            {
                var file = LoadLocked(projectID);
                int removed = file.Entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) SaveLocked(projectID, file);
                return removed > 0;
            }
        }

        /// <summary>
        /// Resolves {Name} tokens in text to their plaintext values for this project — the
        /// container tool adapter's secret-substitution delegate. Unknown tokens are left intact
        /// so ordinary braces survive.
        /// </summary>
        public string ResolveSecrets(string projectID, string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains('{')) return input ?? "";
            return System.Text.RegularExpressions.Regex.Replace(input, @"\{([A-Za-z0-9_\-\.]+)\}", m =>
            {
                var value = GetDecrypted(projectID, m.Groups[1].Value);
                return value ?? m.Value;
            });
        }

        private VaultFile LoadLocked(string projectID)
        {
            string path = VaultPath(projectID);
            if (!File.Exists(path)) return new VaultFile();
            try { return JsonConvert.DeserializeObject<VaultFile>(File.ReadAllText(path)) ?? new VaultFile(); }
            catch (Exception ex) { log($"ProjectVault: load failed for {projectID} ({ex.Message})."); return new VaultFile(); }
        }

        private void SaveLocked(string projectID, VaultFile file)
        {
            string path = VaultPath(projectID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(file, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
