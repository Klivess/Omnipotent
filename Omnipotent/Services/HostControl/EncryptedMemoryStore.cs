using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// "EncryptedMemories": a credential vault the agent can write to and reference by name, but never
    /// read back. Values are DPAPI-encrypted (CurrentUser) and stored in the existing OmniSettings
    /// sensitive store (key prefix "EncMem_"); a names-only index lets the agent list what exists without
    /// ever exposing a value. Decryption happens only inside HostControl at SendInput time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class EncryptedMemoryStore
    {
        private readonly HostControlManager service;
        private readonly string indexPath;
        private readonly SemaphoreSlim gate = new(1, 1);
        private List<string> names = new();

        private sealed class NameIndex { public List<string> Names { get; set; } = new(); }

        public EncryptedMemoryStore(HostControlManager service)
        {
            this.service = service;
            indexPath = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory),
                "HostControl", "encrypted-memories.index.json");
        }

        public async Task InitializeAsync()
        {
            try
            {
                var dir = Path.GetDirectoryName(indexPath)!;
                if (!Directory.Exists(dir)) await service.GetDataHandler().CreateDirectory(dir);
                if (File.Exists(indexPath))
                {
                    var idx = await service.GetDataHandler().ReadAndDeserialiseDataFromFile<NameIndex>(indexPath);
                    if (idx?.Names != null) names = idx.Names;
                }
            }
            catch { /* best-effort: start empty */ }
        }

        private static string KeyFor(string name) => $"EncMem_{name}";

        public async Task SaveAsync(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Encrypted memory name is required.", nameof(name));
            await service.SetSettingRaw(KeyFor(name), Protect(value ?? string.Empty));
            await gate.WaitAsync();
            try
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                await PersistIndexAsync();
            }
            finally { gate.Release(); }
        }

        /// <summary>Decrypt a stored secret. ONLY called inside HostControl at action time — never returned to the model.</summary>
        public async Task<string?> GetDecryptedAsync(string name)
        {
            var raw = await service.GetSettingRaw(KeyFor(name));
            if (string.IsNullOrEmpty(raw)) return null;
            try { return Unprotect(raw); }
            catch { return null; }
        }

        public List<string> ListNames()
        {
            gate.Wait();
            try { return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
            finally { gate.Release(); }
        }

        public bool Exists(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);

        public async Task<bool> DeleteAsync(string name)
        {
            await gate.WaitAsync();
            try
            {
                int removed = names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) await PersistIndexAsync();
                // Blank the underlying setting value so the ciphertext no longer lingers.
                try { await service.SetSettingRaw(KeyFor(name), string.Empty); } catch { }
                return removed > 0;
            }
            finally { gate.Release(); }
        }

        private async Task PersistIndexAsync()
        {
            try { await service.GetDataHandler().SerialiseObjectToFile(indexPath, new NameIndex { Names = names }); }
            catch { }
        }

        private static string Protect(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var enc = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        private static string Unprotect(string b64)
        {
            var enc = Convert.FromBase64String(b64);
            var dec = ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
    }
}
