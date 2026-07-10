using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.AccountRegistry
{
    /// <summary>Lifecycle state of a registered account.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AccountStatus { Active, Dead, Banned }

    /// <summary>
    /// One named secret on an account (password, apiKey, totpSecret, or any free-form field).
    /// Stored AES-256-GCM encrypted; the plaintext is only ever decrypted host-side at
    /// input-injection time and never returned to a model.
    /// </summary>
    public class AccountSecret
    {
        public string Name { get; set; } = "";
        /// <summary>nonce | tag | ciphertext, base64.</summary>
        public string CipherB64 { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A single account an agent created on an external service, shared across ALL projects and
    /// KliveAgent so nobody creates a redundant duplicate. Non-secret metadata (service, username,
    /// email, description, owners, timestamps) is plaintext for enumeration and dedup; the actual
    /// credentials live encrypted in <see cref="Secrets"/> and are referenced by agents only as
    /// {account:&lt;service&gt;/&lt;field&gt;} placeholders.
    /// </summary>
    public class RegisteredAccount
    {
        public string AccountID { get; set; } = "";
        /// <summary>Normalized dedup key, e.g. "github.com" (see AccountRegistryStore.NormalizeService).</summary>
        public string ServiceKey { get; set; } = "";
        /// <summary>Service as the agent named it, e.g. "GitHub".</summary>
        public string ServiceDisplay { get; set; } = "";
        public string Username { get; set; } = "";
        public string? Email { get; set; }
        /// <summary>What the account is for (≤ MaxDescriptionLength).</summary>
        public string? Description { get; set; }
        /// <summary>Free-form operator notes (≤ MaxNotesLength).</summary>
        public string? Notes { get; set; }
        public AccountStatus Status { get; set; } = AccountStatus.Active;
        /// <summary>"KliveAgent" | "project:&lt;id&gt;" | "klives".</summary>
        public string CreatedBy { get; set; } = "";
        /// <summary>Every owner/user of the account: project IDs (as "project:&lt;id&gt;") and/or "KliveAgent".</summary>
        public List<string> Owners { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsedAt { get; set; }
        public List<AccountSecret> Secrets { get; set; } = new();
    }
}
