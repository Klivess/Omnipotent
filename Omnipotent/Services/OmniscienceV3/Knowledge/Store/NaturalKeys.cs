using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>
    /// Natural keys and canonical text (plan R3): gold labels in operator.db reference what a decision was about
    /// (canonical person + attribute + normalised value + episode), never intel.db row ids, so intel.db can be rebuilt
    /// without losing a single decision. Keys are canonical JSON with sorted property names, so the same fact always
    /// produces the same key whichever code path built it.
    /// </summary>
    public static class NaturalKeys
    {
        /// <summary>Normalises a value for comparison: NFKC, case-folded, whitespace collapsed, quotes unified,
        /// surrounding punctuation trimmed. "  London. " and "london" are the same value.</summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s)
            {
                char c = ch switch
                {
                    '‘' or '’' or '‛' or '′' or '`' => '\'',
                    '“' or '”' or '‟' or '″' => '"',
                    '–' or '—' or '−' => '-',
                    _ => ch,
                };
                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString().Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']');
        }

        /// <summary>Canonical JSON of a flat set of fields (sorted names, nulls dropped).</summary>
        public static string Canonical(IEnumerable<KeyValuePair<string, object?>> fields)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in fields)
                if (v != null) sorted[k] = v;
            return JsonSerializer.Serialize(sorted);
        }

        public static string Canonical(params (string Key, object? Value)[] fields) =>
            Canonical(fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)));

        /// <summary>
        /// The natural key of an assertion. State and event values carry an episode (the quarter their validity starts,
        /// for events the quarter they occur), so "lived in London 2019–2020" and "lives in London again since 2023" are
        /// two assertions and a decision about one never applies to the other.
        /// </summary>
        public static string Assertion(string subjectKind, string canonicalSubjectId, string attribute, string? normalizedValue,
            string? objectKind = null, string? objectId = null, string? episode = null) =>
            Canonical(("a", attribute.ToLowerInvariant()), ("ep", episode), ("o", objectId == null ? null : (objectKind ?? "person") + ":" + objectId),
                ("s", subjectKind + ":" + canonicalSubjectId), ("v", Normalize(normalizedValue)));

        /// <summary>Episode label of an instant: the UTC calendar quarter ("2023Q2").</summary>
        public static string Quarter(long utcMs)
        {
            var d = Temporal.Instant.ToUtcDateTime(utcMs);
            return d.Year.ToString(CultureInfo.InvariantCulture) + "Q" + ((d.Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A key for an unordered pair (identity links, symmetric relations): the smaller id first.</summary>
        public static (string A, string B) Pair(string x, string y) => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

        public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>A short, stable id with a prefix (deterministic for the same input).</summary>
        public static string Id(string prefix, string material, int hexChars = 24) => prefix + "_" + Sha256Hex(material)[..Math.Clamp(hexChars, 8, 64)];
    }
}
