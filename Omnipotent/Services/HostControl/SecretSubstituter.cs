using System.Text.RegularExpressions;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// Replaces {EncryptedMemoryName} tokens in text with their decrypted values — the ONLY place a
    /// secret becomes plaintext, immediately before it is typed via SendInput. Tokens that don't match a
    /// known encrypted memory are left untouched (so ordinary braces in normal text survive). Also
    /// provides a redaction helper so audit logs record token names, never values.
    /// </summary>
    public sealed class SecretSubstituter
    {
        private readonly EncryptedMemoryStore store;
        private static readonly Regex Token = new(@"\{([A-Za-z0-9_\-\.]+)\}", RegexOptions.Compiled);

        public SecretSubstituter(EncryptedMemoryStore store)
        {
            this.store = store;
        }

        /// <summary>Returns the text with secret tokens resolved, plus the names of any secrets used.</summary>
        public async Task<(string text, List<string> used)> ResolveAsync(string input)
        {
            var used = new List<string>();
            if (string.IsNullOrEmpty(input) || !input.Contains('{')) return (input ?? string.Empty, used);

            var matches = Token.Matches(input);
            if (matches.Count == 0) return (input, used);

            // Resolve each distinct token once.
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                var name = m.Groups[1].Value;
                if (resolved.ContainsKey(name)) continue;
                if (!store.Exists(name)) continue;
                var value = await store.GetDecryptedAsync(name);
                if (value != null)
                {
                    resolved[name] = value;
                    used.Add(name);
                }
            }

            if (resolved.Count == 0) return (input, used);

            var output = Token.Replace(input, m =>
                resolved.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            return (output, used);
        }

        /// <summary>Replace any known secret token with ‹secret:Name› for safe logging.</summary>
        public string Redact(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains('{')) return input ?? string.Empty;
            return Token.Replace(input, m => store.Exists(m.Groups[1].Value) ? $"‹secret:{m.Groups[1].Value}›" : m.Value);
        }
    }
}
