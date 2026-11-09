using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>
    /// Verbatim-quote verification (plan "Verifier": quotes not found, fuzzy ≥ 0.92 after normalisation, in the cited
    /// message version are rejected). Normalisation removes what a model legitimately changes when it copies text
    /// (case, Unicode compatibility forms, curly quotes, dashes, zero-width characters, markdown emphasis, whitespace);
    /// the match is an approximate-substring alignment, so a quote may sit anywhere in the message.
    /// </summary>
    public static class TextMatch
    {
        public const double QuoteThreshold = 0.92;

        public static string NormalizeForMatch(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Normalize(NormalizationForm.FormKC);
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char raw in s)
            {
                char ch = char.ToLowerInvariant(raw);
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.Format) continue; // zero-width joiners, RTL marks, BOM
                ch = ch switch
                {
                    '‘' or '’' or '‛' or '′' or '`' => '\'',
                    '“' or '”' or '‟' or '″' => '"',
                    '–' or '—' or '−' => '-',
                    '…' => '.',
                    _ => ch,
                };
                if (ch is '*' or '_' or '~' or '|') continue; // markdown emphasis and spoiler bars
                if (char.IsWhiteSpace(ch))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Similarity in [0,1] of the best alignment of <paramref name="quote"/> anywhere inside <paramref name="text"/>.</summary>
        public static double BestSubstringSimilarity(string quote, string text)
        {
            string q = NormalizeForMatch(quote);
            string t = NormalizeForMatch(text);
            if (q.Length == 0) return 0;
            if (t.Contains(q, StringComparison.Ordinal)) return 1;
            if (t.Length == 0) return 0;
            // Semi-global edit distance: a free start and end in the text. O(|q|·|t|) with two rows.
            int m = q.Length, n = t.Length;
            if ((long)m * n > 8_000_000) return LongTextFallback(q, t);
            var prev = new int[n + 1];
            var cur = new int[n + 1];
            for (int i = 1; i <= m; i++)
            {
                cur[0] = i;
                char qc = q[i - 1];
                for (int j = 1; j <= n; j++)
                {
                    int cost = qc == t[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            int best = int.MaxValue;
            for (int j = 0; j <= n; j++) best = Math.Min(best, prev[j]);
            return Math.Max(0, 1.0 - (double)best / m);
        }

        /// <summary>For very long messages: compare against windows around the rarest matching word of the quote.</summary>
        private static double LongTextFallback(string q, string t)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).Take(3).ToList();
            double best = 0;
            foreach (var w in words)
            {
                int at = t.IndexOf(w, StringComparison.Ordinal);
                while (at >= 0)
                {
                    int start = Math.Max(0, at - q.Length);
                    int len = Math.Min(t.Length - start, q.Length * 3);
                    best = Math.Max(best, BestSubstringSimilarity(q, t.Substring(start, len)));
                    if (best >= 1) return 1;
                    at = t.IndexOf(w, at + w.Length, StringComparison.Ordinal);
                }
            }
            return best;
        }

        public static bool QuoteFound(string? quote, string? text) =>
            !string.IsNullOrWhiteSpace(quote) && text != null && BestSubstringSimilarity(quote, text) >= QuoteThreshold;

        /// <summary>A stable fingerprint of a message's wording for near-duplicate folding (copypasta, repeated lines count once).</summary>
        public static string Fingerprint(string? text)
        {
            string n = NormalizeForMatch(text);
            var sb = new StringBuilder(n.Length);
            foreach (char c in n) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return Store.NaturalKeys.Sha256Hex(sb.ToString())[..16];
        }
    }
}
