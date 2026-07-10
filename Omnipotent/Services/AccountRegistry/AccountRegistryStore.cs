using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.AccountRegistry
{
    /// <summary>
    /// The single GLOBAL registry of accounts agents create on external services, shared by
    /// KliveAgent and every Project so nobody re-creates a duplicate. One JSON index behind one
    /// lock, atomic tmp+move writes (same shape as ProjectStore/ProjectObservableStore).
    ///
    /// Secrets at rest use AES-256-GCM with a portable root key + per-account HKDF derivation —
    /// the exact scheme ProjectVault uses (authenticated, tamper-detected, survives Windows
    /// account changes, cross-platform), but with its OWN root key so this standalone service
    /// never depends on the Projects subsystem's key file. Plaintext is only ever produced
    /// host-side at input-injection time (<see cref="ResolveAccountPlaceholders"/>) or the
    /// Klives-only reveal route — never returned to a model.
    ///
    /// Layout: SavedData/AccountRegistry/accounts.json and root.key
    /// </summary>
    public class AccountRegistryStore
    {
        public const int MaxDescriptionLength = 300;
        public const int MaxNotesLength = 2000;

        private const int NonceBytes = 12;   // AES-GCM standard nonce
        private const int TagBytes = 16;     // AES-GCM tag
        private const int RootKeyBytes = 32; // AES-256

        private readonly Action<string> log;
        private readonly string indexPath;
        private readonly string rootKeyPath;
        private readonly object gate = new();
        private byte[]? rootKey;
        private readonly object rootKeyGate = new();

        // {account:<service>/<field>} or {account:<service>/<username>/<field>}. The ':' and '/'
        // are absent from the vault/EncryptedMemory {name} regex, so the two can never collide.
        private static readonly Regex PlaceholderRegex =
            new(@"\{account:([^{}]+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AccountRegistryStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryDirectory);
            Directory.CreateDirectory(dir);
            indexPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryIndexFile);
            rootKeyPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryRootKeyFile);
        }

        // ── service normalization / dedup key ──

        /// <summary>
        /// Normalizes a service name into the dedup key: URL → host, strip leading "www.",
        /// lowercase, trim. "https://www.GitHub.com/login" → "github.com"; "GitHub" → "github".
        /// </summary>
        public static string NormalizeService(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            // Pull the host out of anything URL-shaped (with or without scheme).
            if (s.Contains("://") && Uri.TryCreate(s, UriKind.Absolute, out var abs))
                s = abs.Host;
            else if (Uri.TryCreate("https://" + s, UriKind.Absolute, out var rel) && s.Contains('.') && (s.Contains('/') || s.Contains('.')))
                s = rel.Host;
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith("www.")) s = s[4..];
            return s.TrimEnd('/');
        }

        // ── public API (all lock-guarded; readers return deep clones) ──

        public record RegisterResult(bool Created, RegisteredAccount? Account, List<RegisteredAccount> Existing);

        /// <summary>
        /// Registers a new account. Warn-but-allow dedup: if the service already has an account and
        /// <paramref name="allowDuplicate"/> is false, returns Created=false with the existing
        /// accounts and creates nothing. The caller (tool/facade) formats the refusal.
        /// </summary>
        public RegisterResult Register(string service, string username, string? email,
            Dictionary<string, string>? secrets, string? description, string createdBy, string? owner,
            bool allowDuplicate, string? duplicateReason)
        {
            string serviceKey = NormalizeService(service);
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new InvalidOperationException("A service name is required.");
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("A username is required.");

            lock (gate)
            {
                var all = LoadLocked();
                var existing = all.Where(a => a.ServiceKey == serviceKey).ToList();
                if (existing.Count > 0 && !allowDuplicate)
                    return new RegisterResult(false, null, existing.Select(Clone).ToList());

                var account = new RegisteredAccount
                {
                    AccountID = Guid.NewGuid().ToString("N"),
                    ServiceKey = serviceKey,
                    ServiceDisplay = string.IsNullOrWhiteSpace(service) ? serviceKey : service.Trim(),
                    Username = username.Trim(),
                    Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                    Description = TrimOrNull(description, MaxDescriptionLength),
                    CreatedBy = createdBy,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                if (!string.IsNullOrWhiteSpace(owner)) account.Owners.Add(owner);
                if (existing.Count > 0 && !string.IsNullOrWhiteSpace(duplicateReason))
                    account.Notes = TrimOrNull($"Intentional duplicate ({existing.Count} existing): {duplicateReason}", MaxNotesLength);
                if (secrets != null)
                    foreach (var kv in secrets)
                        if (!string.IsNullOrWhiteSpace(kv.Key))
                            account.Secrets.Add(new AccountSecret
                            {
                                Name = kv.Key.Trim(),
                                CipherB64 = Encrypt(account.AccountID, kv.Value ?? ""),
                            });

                all.Add(account);
                SaveLocked(all);
                return new RegisterResult(true, Clone(account), new());
            }
        }

        public List<RegisteredAccount> List(string? serviceKey = null)
        {
            lock (gate)
            {
                var all = LoadLocked();
                if (!string.IsNullOrWhiteSpace(serviceKey))
                {
                    string key = NormalizeService(serviceKey);
                    all = all.Where(a => a.ServiceKey == key).ToList();
                }
                return all.Select(Clone).ToList();
            }
        }

        public List<RegisteredAccount> FindByService(string service)
        {
            string key = NormalizeService(service);
            lock (gate) return LoadLocked().Where(a => a.ServiceKey == key).Select(Clone).ToList();
        }

        public RegisteredAccount? Get(string accountID)
        {
            lock (gate)
            {
                var a = LoadLocked().FirstOrDefault(x => x.AccountID == accountID);
                return a == null ? null : Clone(a);
            }
        }

        public bool AddOwner(string accountID, string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) return false;
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null || a.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase)) return false;
                a.Owners.Add(owner);
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        public bool TouchUsed(string accountID)
        {
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.LastUsedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        public bool UpdateStatus(string accountID, AccountStatus status)
        {
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.Status = status;
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        public bool UpdateNotes(string accountID, string? notes)
        {
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.Notes = TrimOrNull(notes, MaxNotesLength);
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        public bool UpdateDescription(string accountID, string? description)
        {
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.Description = TrimOrNull(description, MaxDescriptionLength);
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        public bool UpdateEmail(string accountID, string? email)
        {
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        /// <summary>Add-or-replace a named secret (case-insensitive on name). Value is encrypted.</summary>
        public bool SetSecret(string accountID, string name, string plaintext)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A secret name is required.");
            lock (gate)
            {
                var all = LoadLocked();
                var a = all.FirstOrDefault(x => x.AccountID == accountID);
                if (a == null) return false;
                a.Secrets.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                a.Secrets.Add(new AccountSecret { Name = name.Trim(), CipherB64 = Encrypt(accountID, plaintext ?? "") });
                a.UpdatedAt = DateTime.UtcNow;
                SaveLocked(all);
                return true;
            }
        }

        /// <summary>Decrypts a secret. ONLY called host-side (typing/reveal); never returned to a model.</summary>
        public string? GetDecryptedSecret(string accountID, string secretName)
        {
            lock (gate)
            {
                var a = LoadLocked().FirstOrDefault(x => x.AccountID == accountID);
                var s = a?.Secrets.FirstOrDefault(x => string.Equals(x.Name, secretName, StringComparison.OrdinalIgnoreCase));
                return s == null ? null : Decrypt(accountID, s.CipherB64);
            }
        }

        public bool Delete(string accountID)
        {
            lock (gate)
            {
                var all = LoadLocked();
                int removed = all.RemoveAll(x => x.AccountID == accountID);
                if (removed > 0) SaveLocked(all);
                return removed > 0;
            }
        }

        // ── placeholder resolution (Phase 7) ──

        /// <summary>The outcome of a typing-time substitution attempt.</summary>
        public record ResolveResult(string Text, List<string> Used, string? Error);

        /// <summary>
        /// Substitutes {account:&lt;service&gt;/&lt;field&gt;} and
        /// {account:&lt;service&gt;/&lt;username&gt;/&lt;field&gt;} tokens with decrypted values.
        /// On a unique match: substitutes, marks the account used, and auto-claims
        /// <paramref name="usingOwner"/> as an owner. Ambiguous (multiple accounts, no username) or
        /// unknown references THROW an actionable InvalidOperationException — an explicit
        /// account: prefix is unambiguous intent, so we fail loudly rather than type a literal token
        /// into a login form. Non-account braces are untouched.
        /// </summary>
        public string ResolveAccountPlaceholders(string text, string? usingOwner)
            => ResolveCore(text, usingOwner, null);

        /// <summary>Non-throwing wrapper for HostControl: returns resolved text, the account names used, or an error.</summary>
        public ResolveResult TryResolveForTyping(string text, string? usingOwner)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{account:", StringComparison.OrdinalIgnoreCase))
                return new ResolveResult(text ?? "", new(), null);
            var used = new List<string>();
            try { return new ResolveResult(ResolveCore(text, usingOwner, used), used, null); }
            catch (Exception ex) { return new ResolveResult(text, new(), ex.Message); }
        }

        /// <summary>
        /// Core substitution. <paramref name="usedSink"/>, when non-null, collects a "service/field"
        /// entry per resolved token for the HostControl audit trail (which also keeps typo-simulation
        /// hard-off). Throws an actionable message on any ambiguous/unknown/undecryptable reference.
        /// </summary>
        private string ResolveCore(string text, string? usingOwner, List<string>? usedSink)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{account:", StringComparison.OrdinalIgnoreCase))
                return text ?? "";

            return PlaceholderRegex.Replace(text, m =>
            {
                var (serviceKey, username, field, error) = ParseRef(m.Groups[1].Value);
                if (error != null) throw new InvalidOperationException(error);

                lock (gate)
                {
                    var all = LoadLocked();
                    var candidates = all.Where(a => a.ServiceKey == serviceKey).ToList();
                    if (username != null)
                        candidates = candidates.Where(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (candidates.Count == 0)
                        throw new InvalidOperationException(
                            $"No registered account for '{serviceKey}'{(username != null ? $" / '{username}'" : "")}. " +
                            "account_list to see what exists; account_register to add one.");
                    if (candidates.Count > 1)
                    {
                        var names = string.Join(", ", candidates.Select(c => c.Username));
                        throw new InvalidOperationException(
                            $"Ambiguous {m.Value}: {candidates.Count} accounts exist ({names}). " +
                            $"Use {{account:{serviceKey}/<username>/{field}}}.");
                    }

                    var account = candidates[0];
                    var secret = account.Secrets.FirstOrDefault(s => string.Equals(s.Name, field, StringComparison.OrdinalIgnoreCase));
                    if (secret == null)
                    {
                        var have = account.Secrets.Count == 0 ? "none" : string.Join(", ", account.Secrets.Select(s => s.Name));
                        throw new InvalidOperationException(
                            $"Account '{serviceKey}' ({account.Username}) has no secret '{field}'. Available: {have}.");
                    }

                    string? value = Decrypt(account.AccountID, secret.CipherB64);
                    if (value == null)
                        throw new InvalidOperationException($"Secret '{field}' for '{serviceKey}' could not be decrypted (corrupt or wrong key).");

                    // Mark used + auto-claim ownership, inside the same lock (direct list mutation).
                    account.LastUsedAt = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(usingOwner) && !account.Owners.Contains(usingOwner, StringComparer.OrdinalIgnoreCase))
                    {
                        account.Owners.Add(usingOwner);
                        account.UpdatedAt = DateTime.UtcNow;
                    }
                    SaveLocked(all);
                    usedSink?.Add($"{serviceKey}/{field}");
                    return value;
                }
            });
        }

        private static (string serviceKey, string? username, string field, string? error) ParseRef(string body)
        {
            var parts = body.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                return (NormalizeService(parts[0]), null, parts[1], null);
            if (parts.Length == 3)
                return (NormalizeService(parts[0]), parts[1], parts[2], null);
            return ("", null, "",
                $"Malformed account reference '{{account:{body}}}'. Use {{account:service/field}} or {{account:service/username/field}}.");
        }

        // ── crypto (copied from ProjectVault: AES-256-GCM + HKDF-SHA256) ──

        private byte[] RootKey()
        {
            if (rootKey != null) return rootKey;
            lock (rootKeyGate)
            {
                if (rootKey != null) return rootKey;
                if (File.Exists(rootKeyPath))
                {
                    rootKey = File.ReadAllBytes(rootKeyPath);
                    if (rootKey.Length != RootKeyBytes)
                        throw new InvalidOperationException($"Account registry root key is corrupt ({rootKey.Length} bytes).");
                }
                else
                {
                    rootKey = RandomNumberGenerator.GetBytes(RootKeyBytes);
                    File.WriteAllBytes(rootKeyPath, rootKey);
                    RestrictKeyFilePermissions(rootKeyPath);
                    log("AccountRegistry: generated a new root key.");
                }
                return rootKey;
            }
        }

        private byte[] DeriveAccountKey(string accountID)
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: RootKey(),
                outputLength: 32,
                salt: null,
                info: Encoding.UTF8.GetBytes("accountregistry:" + accountID));
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
            catch { /* best-effort; the key still lives under the app's private data dir */ }
        }

        private string Encrypt(string accountID, string plaintext)
        {
            byte[] key = DeriveAccountKey(accountID);
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

        private string? Decrypt(string accountID, string cipherB64)
        {
            try
            {
                byte[] blob = Convert.FromBase64String(cipherB64);
                if (blob.Length < NonceBytes + TagBytes) return null;
                byte[] nonce = blob[..NonceBytes];
                byte[] tag = blob[NonceBytes..(NonceBytes + TagBytes)];
                byte[] cipher = blob[(NonceBytes + TagBytes)..];
                byte[] plain = new byte[cipher.Length];
                byte[] key = DeriveAccountKey(accountID);
                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Decrypt(nonce, cipher, tag, plain);
                CryptographicOperations.ZeroMemory(key);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; } // authentication failure or corrupt entry
        }

        // ── persistence (single global index; atomic tmp+move; deep-clone on read) ──

        private static string? TrimOrNull(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length <= max ? s : s[..max];
        }

        private static RegisteredAccount Clone(RegisteredAccount a)
            => JsonConvert.DeserializeObject<RegisteredAccount>(JsonConvert.SerializeObject(a))!;

        private List<RegisteredAccount> LoadLocked()
        {
            if (!File.Exists(indexPath)) return new();
            try { return JsonConvert.DeserializeObject<List<RegisteredAccount>>(File.ReadAllText(indexPath)) ?? new(); }
            catch (Exception ex)
            {
                log($"AccountRegistryStore: failed to load {indexPath} ({ex.Message}) — starting empty, file preserved.");
                return new();
            }
        }

        private void SaveLocked(List<RegisteredAccount> all)
        {
            string tmp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, indexPath, overwrite: true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(15); }
            }
        }
    }
}
