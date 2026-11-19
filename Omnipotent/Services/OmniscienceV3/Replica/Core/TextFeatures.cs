using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Writing systems a letter can belong to (exact Unicode block arithmetic, no language guessing).</summary>
    public enum Script { Latin, Cyrillic, Greek, Arabic, Hebrew, Han, Kana, Hangul, Thai, Devanagari, Other }

    /// <summary>
    /// Exact, structural text measurements the replica needs: grapheme-cluster emoji (skin tones, ZWJ sequences, flags,
    /// keycaps, the bare ❤ that v2 missed: F313), Discord custom emoji, URLs, mentions, code fences, Unicode-aware
    /// tokens (multilingual: scripts without spaces become character bigrams, F019), scripts, and normalisation for
    /// duplicate detection. Nothing here judges meaning: that is left to models (plan A1). The regexes only parse
    /// syntax (URLs, Discord markup), never words.
    /// </summary>
    public static class TextFeatures
    {
        private static readonly Regex UrlPattern = new(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CustomEmojiPattern = new(@"<a?:([A-Za-z0-9_~]{1,64}):(\d{5,25})>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MentionPattern = new(@"<(?:@!?|@&|#)(\d{5,25})>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CodeFencePattern = new(@"```[\s\S]*?(?:```|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Extended_Pictographic (Unicode 15 emoji-data.txt), merged into ranges.
        private static readonly (int Start, int End)[] Pictographic =
        {
            (0x00A9, 0x00A9), (0x00AE, 0x00AE), (0x203C, 0x203C), (0x2049, 0x2049), (0x2122, 0x2122), (0x2139, 0x2139),
            (0x2194, 0x2199), (0x21A9, 0x21AA), (0x231A, 0x231B), (0x2328, 0x2328), (0x2388, 0x2388), (0x23CF, 0x23CF),
            (0x23E9, 0x23F3), (0x23F8, 0x23FA), (0x24C2, 0x24C2), (0x25AA, 0x25AB), (0x25B6, 0x25B6), (0x25C0, 0x25C0),
            (0x25FB, 0x25FE), (0x2600, 0x2605), (0x2607, 0x2612), (0x2614, 0x2685), (0x2690, 0x2705), (0x2708, 0x2712),
            (0x2714, 0x2714), (0x2716, 0x2716), (0x271D, 0x271D), (0x2721, 0x2721), (0x2728, 0x2728), (0x2733, 0x2734),
            (0x2744, 0x2744), (0x2747, 0x2747), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757),
            (0x2763, 0x2767), (0x2795, 0x2797), (0x27A1, 0x27A1), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2934, 0x2935),
            (0x2B05, 0x2B07), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D),
            (0x3297, 0x3297), (0x3299, 0x3299), (0x1F000, 0x1F0FF), (0x1F10D, 0x1F10F), (0x1F12F, 0x1F12F), (0x1F16C, 0x1F171),
            (0x1F17E, 0x1F17F), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F1AD, 0x1F1E5), (0x1F201, 0x1F20F), (0x1F21A, 0x1F21A),
            (0x1F22F, 0x1F22F), (0x1F232, 0x1F23A), (0x1F23C, 0x1F23F), (0x1F249, 0x1F3FA), (0x1F400, 0x1F53D), (0x1F546, 0x1F64F),
            (0x1F680, 0x1F6FF), (0x1F774, 0x1F77F), (0x1F7D5, 0x1F7FF), (0x1F80C, 0x1F80F), (0x1F848, 0x1F84F), (0x1F85A, 0x1F85F),
            (0x1F888, 0x1F88F), (0x1F8AE, 0x1F8FF), (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1FAFF), (0x1FC00, 0x1FFFD),
        };

        /// <summary>Pictographic code points that are ordinary text symbols unless the emoji presentation selector follows.</summary>
        private static readonly HashSet<int> TextDefaultSymbols = new() { 0x00A9, 0x00AE, 0x2122, 0x203C, 0x2049, 0x2139, 0x2194, 0x2195, 0x2196, 0x2197, 0x2198, 0x2199, 0x25AA, 0x25AB, 0x25B6, 0x25C0 };

        private const int Vs16 = 0xFE0F, Zwj = 0x200D, Keycap = 0x20E3;

        public static bool IsPictographic(int cp)
        {
            int lo = 0, hi = Pictographic.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < Pictographic[mid].Start) hi = mid - 1;
                else if (cp > Pictographic[mid].End) lo = mid + 1;
                else return true;
            }
            return false;
        }

        public static bool IsRegionalIndicator(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

        /// <summary>The grapheme clusters of <paramref name="text"/> (user-perceived characters).</summary>
        public static IEnumerable<string> Graphemes(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) yield return e.GetTextElement();
        }

        /// <summary>True when a grapheme cluster renders as an emoji (ZWJ sequences, modifiers, flags, keycaps included).</summary>
        public static bool IsEmojiCluster(string cluster)
        {
            if (string.IsNullOrEmpty(cluster)) return false;
            bool vs16 = false, keycap = false, zwj = false;
            int regional = 0, firstPictographic = -1;
            for (int i = 0; i < cluster.Length; i += char.IsSurrogatePair(cluster, i) ? 2 : 1)
            {
                int cp = Omnipotent.Services.OmniscienceV3.Ml.UnicodeText.CodePointAt(cluster, i);
                if (cp == Vs16) vs16 = true;
                else if (cp == Keycap) keycap = true;
                else if (cp == Zwj) zwj = true;
                else if (IsRegionalIndicator(cp)) regional++;
                else if (firstPictographic < 0 && IsPictographic(cp)) firstPictographic = cp;
            }
            if (regional >= 2) return true;
            if (keycap && vs16) return true;
            if (firstPictographic < 0) return false;
            if (vs16 || zwj) return true;
            return !TextDefaultSymbols.Contains(firstPictographic);
        }

        /// <summary>Emoji clusters in order (unicode emoji only; Discord custom emoji are separate).</summary>
        public static List<string> Emoji(string text)
        {
            var list = new List<string>();
            foreach (var g in Graphemes(StripMarkup(text)))
                if (IsEmojiCluster(g)) list.Add(g);
            return list;
        }

        /// <summary>Discord custom emoji names in order, e.g. <c>&lt;:pepe_cry:123&gt;</c> → <c>:pepe_cry:</c>.</summary>
        public static List<string> CustomEmoji(string text) =>
            string.IsNullOrEmpty(text) ? new List<string>() : CustomEmojiPattern.Matches(text).Select(m => ":" + m.Groups[1].Value + ":").ToList();

        public static List<string> Urls(string text) =>
            string.IsNullOrEmpty(text) ? new List<string>() : UrlPattern.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', '!', '?')).ToList();

        /// <summary>Host of a URL without "www." (null when it does not parse).</summary>
        public static string? Domain(string url)
        {
            string candidate = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
            string host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }

        public static bool HasCodeFence(string text) => !string.IsNullOrEmpty(text) && text.Contains("```", StringComparison.Ordinal);

        /// <summary>Text with code blocks removed (code is not the person's prose, plan A5).</summary>
        public static string WithoutCode(string text) => string.IsNullOrEmpty(text) ? "" : CodeFencePattern.Replace(text, " ").Trim();

        /// <summary>Text with URLs, Discord custom emoji and mentions removed: what remains is the person's own prose.</summary>
        public static string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = UrlPattern.Replace(text, " ");
            t = CustomEmojiPattern.Replace(t, " ");
            t = MentionPattern.Replace(t, " ");
            return t;
        }

        /// <summary>
        /// Text as an LLM should see it in an exemplar: URLs become <c>[link: domain]</c> (the model must never invent a
        /// URL), Discord markup becomes readable placeholders, code blocks are marked.
        /// </summary>
        public static string ForPrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = CodeFencePattern.Replace(text, "[code block]");
            t = UrlPattern.Replace(t, m => "[link: " + (Domain(m.Value.TrimEnd('.', ',', ')', '!', '?')) ?? "url") + "]");
            t = CustomEmojiPattern.Replace(t, m => ":" + m.Groups[1].Value + ":");
            t = MentionPattern.Replace(t, "@someone");
            return t;
        }

        public static bool HasLetters(string text)
        {
            foreach (var ch in text) if (char.IsLetter(ch)) return true;
            return false;
        }

        public static Script ScriptOf(int cp)
        {
            if (cp < 0x0250 || (cp >= 0x1E00 && cp < 0x1F00) || (cp >= 0x2C60 && cp < 0x2C80) || (cp >= 0xA720 && cp < 0xA800) || (cp >= 0xFF21 && cp < 0xFF5B)) return Script.Latin;
            if ((cp >= 0x0370 && cp < 0x0400) || (cp >= 0x1F00 && cp < 0x2000)) return Script.Greek;
            if (cp >= 0x0400 && cp < 0x0530) return Script.Cyrillic;
            if (cp >= 0x0590 && cp < 0x0600) return Script.Hebrew;
            if ((cp >= 0x0600 && cp < 0x0780) || (cp >= 0x08A0 && cp < 0x0900) || (cp >= 0xFB50 && cp < 0xFE00) || (cp >= 0xFE70 && cp < 0xFF00)) return Script.Arabic;
            if (cp >= 0x0900 && cp < 0x0980) return Script.Devanagari;
            if (cp >= 0x0E00 && cp < 0x0E80) return Script.Thai;
            if ((cp >= 0x1100 && cp < 0x1200) || (cp >= 0x3130 && cp < 0x3190) || (cp >= 0xAC00 && cp < 0xD7B0)) return Script.Hangul;
            if (cp >= 0x3040 && cp < 0x3100) return Script.Kana;
            if ((cp >= 0x3400 && cp < 0xA000) || (cp >= 0x20000 && cp < 0x2FA20) || (cp >= 0xF900 && cp < 0xFB00)) return Script.Han;
            return Script.Other;
        }

        /// <summary>Scripts written without spaces between words: tokenised as character bigrams.</summary>
        private static bool IsUnspaced(Script s) => s is Script.Han or Script.Kana or Script.Thai;

        /// <summary>
        /// Unicode-aware tokens for retrieval and vocabulary statistics: lowercased letter/digit runs (apostrophes kept
        /// inside words), character bigrams for unspaced scripts, emoji clusters, custom emoji as <c>:name:</c>, links as
        /// <c>&lt;link:domain&gt;</c>.
        /// </summary>
        public static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            foreach (var url in Urls(text)) tokens.Add("<link:" + (Domain(url) ?? "url") + ">");
            tokens.AddRange(CustomEmoji(text));
            var sb = new StringBuilder();
            var unspaced = new List<string>();
            void Flush()
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString().Trim('\'', '’'));
                    if (tokens[^1].Length == 0) tokens.RemoveAt(tokens.Count - 1);
                    sb.Clear();
                }
                if (unspaced.Count > 0)
                {
                    if (unspaced.Count == 1) tokens.Add(unspaced[0]);
                    for (int i = 0; i + 1 < unspaced.Count; i++) tokens.Add(unspaced[i] + unspaced[i + 1]);
                    unspaced.Clear();
                }
            }
            foreach (var g in Graphemes(StripMarkup(text)))
            {
                int cp = Omnipotent.Services.OmniscienceV3.Ml.UnicodeText.CodePointAt(g, 0);
                if (IsEmojiCluster(g)) { Flush(); tokens.Add(g); continue; }
                if (char.IsLetterOrDigit(g, 0))
                {
                    var script = ScriptOf(cp);
                    if (IsUnspaced(script))
                    {
                        if (sb.Length > 0) Flush();
                        unspaced.Add(g);
                    }
                    else
                    {
                        if (unspaced.Count > 0) Flush();
                        sb.Append(g.ToLowerInvariant());
                    }
                    continue;
                }
                if ((g == "'" || g == "’") && sb.Length > 0) { sb.Append('\''); continue; }
                Flush();
            }
            Flush();
            return tokens;
        }

        /// <summary>Lowercase letters/digits only, single-spaced, URLs and markup removed: equal for trivially different copies.</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (var g in Graphemes(StripMarkup(text)))
            {
                if (char.IsLetterOrDigit(g, 0))
                {
                    if (space && sb.Length > 0) sb.Append(' ');
                    space = false;
                    sb.Append(g.ToLowerInvariant());
                }
                else space = true;
            }
            return sb.ToString();
        }

        /// <summary>Character 3-gram shingles of the normalised text (for near-duplicate detection).</summary>
        public static HashSet<string> Shingles(string text, int n = 3)
        {
            string norm = Normalize(text);
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (norm.Length < n) { if (norm.Length > 0) set.Add(norm); return set; }
            for (int i = 0; i + n <= norm.Length; i++) set.Add(norm.Substring(i, n));
            return set;
        }

        public static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1;
            if (a.Count == 0 || b.Count == 0) return 0;
            int inter = a.Count < b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
            return (double)inter / (a.Count + b.Count - inter);
        }

        /// <summary>Fuzzy containment of a quote in a source text after normalisation (the verifier's ≥0.92 rule).</summary>
        public static bool QuoteMatches(string? quote, string source, double threshold = 0.92)
        {
            if (string.IsNullOrWhiteSpace(quote)) return true;
            string q = Normalize(quote), s = Normalize(source);
            if (q.Length == 0) return true;
            if (s.Contains(q, StringComparison.Ordinal)) return true;
            if (q.Length > s.Length * 1.3 + 5) return false;
            // Best window similarity: share of the quote's 3-grams found in the source.
            var qs = Shingles(q);
            var ss = Shingles(s);
            if (qs.Count == 0) return false;
            double covered = (double)qs.Count(ss.Contains) / qs.Count;
            return covered >= threshold;
        }

        /// <summary>Hex SHA-256 prefix: a stable content id (identical text gives an identical id on every machine).</summary>
        public static string Hash(string text, int hexChars = 16)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..Math.Clamp(hexChars, 4, 64)];
        }

        /// <summary>A deterministic 32-bit seed from text (for reproducible sampling).</summary>
        public static int Seed(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        }

        /// <summary>A deterministic value in [0,1) from text (stable hash-based sampling).</summary>
        public static double Unit(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return (BitConverter.ToUInt64(bytes, 0) >> 11) * (1.0 / (1UL << 53));
        }

        /// <summary>Head and tail of a long text with a visible marker (plan A5: never a silent cut).</summary>
        public static string HeadTail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
            int head = (int)(maxChars * 0.7), tail = Math.Max(0, maxChars - head - 20);
            return text[..head] + " […cut " + (text.Length - head - tail) + " chars…] " + text[^tail..];
        }
    }
}
