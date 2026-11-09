using System.Globalization;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>Structural facts about a message's form (not its meaning), measured exactly (plan A1: "exact measurements stay exact").</summary>
    public sealed record ContentShape(
        int Graphemes,
        int LetterGraphemes,
        int EmojiGraphemes,
        int Urls,
        double CodeShare,
        bool QuotedOnly,
        bool EmojiOnly,
        bool UrlOnly);

    /// <summary>What the gate decided for one message.</summary>
    public sealed record GateDecision(bool Skip, string? Reason, bool NonAuthored, double AbstainBoost, ContentShape Shape)
    {
        public static GateDecision SkipFor(string reason, ContentShape shape) => new(true, reason, true, 0, shape);
    }

    /// <summary>Message facts the gate needs besides the text.</summary>
    public sealed record GateInput(string? Text, bool IsBot, bool IsSystem, bool IsWebhook, bool IsForwarded, int SameTextAuthors = 1);

    /// <summary>
    /// Decides, from form alone, which messages the A2 tasks label and how cautiously (plan A5):
    /// <list type="bullet">
    /// <item>nothing to read (no text, system messages, bots and webhooks) → skipped (bots are excluded from person analytics);</item>
    /// <item>ultra-short, emoji-only and reaction-like messages → labelled with a higher abstention bar (they rely on the
    /// context head; a lone "lol" is not evidence of anything);</item>
    /// <item>forwarded messages, code-dominated text, a bare link and copypasta (the same text from several authors) →
    /// labelled but marked non-authored, so style and behaviour metrics leave them out while topic metrics keep them.</item>
    /// </list>
    /// </summary>
    public static class ContentGate
    {
        private static readonly Regex Url = new(@"(?i)\b(?:https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Fence = new(@"```[\s\S]*?(```|$)", RegexOptions.Compiled);
        private static readonly Regex InlineCode = new(@"`[^`\n]+`", RegexOptions.Compiled);
        private static readonly Regex CustomEmoji = new(@"<a?:[A-Za-z0-9_~]+:\d+>", RegexOptions.Compiled);

        public const double ShortAbstainBoost = 0.15;

        public static ContentShape Measure(string text)
        {
            int urls = Url.Matches(text).Count;
            string noUrls = Url.Replace(text, " ");
            int codeChars = Fence.Matches(noUrls).Sum(m => m.Length) + InlineCode.Matches(noUrls).Sum(m => m.Length);
            double codeShare = text.Length == 0 ? 0 : Math.Min(1.0, (double)codeChars / text.Length);
            string noCustom = CustomEmoji.Replace(noUrls, "⭐"); // a custom emoji is one emoji
            // Emptiness counts everything visible (a bare link is not empty); letters and emoji exclude the links.
            int graphemes = 0, letters = 0, emoji = 0;
            var all = StringInfo.GetTextElementEnumerator(text);
            while (all.MoveNext())
                if (!string.IsNullOrWhiteSpace(all.GetTextElement())) graphemes++;
            var e = StringInfo.GetTextElementEnumerator(noCustom);
            while (e.MoveNext())
            {
                string el = e.GetTextElement();
                if (string.IsNullOrWhiteSpace(el)) continue;
                if (char.IsLetterOrDigit(el, 0)) letters++;
                else if (IsEmoji(el)) emoji++;
            }
            var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            bool quotedOnly = lines.Count > 0 && lines.All(l => l.TrimStart().StartsWith('>'));
            return new ContentShape(graphemes, letters, emoji, urls, codeShare, quotedOnly,
                EmojiOnly: graphemes > 0 && letters == 0 && emoji > 0,
                UrlOnly: urls > 0 && letters == 0 && emoji == 0);
        }

        /// <summary>Emoji by Unicode property: symbols (So), regional indicators, keycaps and ZWJ sequences.</summary>
        public static bool IsEmoji(string grapheme)
        {
            int cp = char.ConvertToUtf32(grapheme, 0);
            if (cp is >= 0x1F000 and <= 0x1FAFF) return true; // emoji blocks incl. regional indicators and flags
            if (cp is >= 0x2600 and <= 0x27BF) return true;   // misc symbols, dingbats (☀ ✨ ❤)
            if (cp is >= 0x2B00 and <= 0x2BFF) return true;   // ⭐ ⬆
            if (cp is 0x2122 or 0x2139 or 0x231A or 0x231B or 0x2328 or 0x23CF || (cp >= 0x23E9 && cp <= 0x23FA)) return true; // ™ ℹ ⌚ ⌛ ⌨ ⏏ ⏩–⏺
            if (grapheme.Length > 1 && grapheme.Contains('⃣')) return true; // keycap 1️⃣
            return CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.OtherSymbol;
        }

        public static GateDecision Decide(GateInput input)
        {
            string text = input.Text ?? "";
            var shape = Measure(text);
            if (input.IsSystem) return GateDecision.SkipFor("system", shape);
            if (input.IsBot || input.IsWebhook) return GateDecision.SkipFor("bot", shape);
            if (shape.Graphemes == 0) return GateDecision.SkipFor("empty", shape);
            bool nonAuthored = input.IsForwarded || shape.CodeShare >= 0.5 || shape.UrlOnly || shape.QuotedOnly || input.SameTextAuthors >= 3;
            string? reason = input.IsForwarded ? "forwarded" : shape.CodeShare >= 0.5 ? "code" : shape.UrlOnly ? "link" : shape.QuotedOnly ? "quoted"
                : input.SameTextAuthors >= 3 ? "copypasta" : null;
            double boost = shape.EmojiOnly || shape.LetterGraphemes <= 3 ? ShortAbstainBoost : 0;
            return new GateDecision(false, reason, nonAuthored, boost, shape);
        }

        /// <summary>The text hash used to fold duplicates (case, whitespace and diacritics folded).</summary>
        public static string NormalizedHash(string? text)
        {
            string t = (text ?? "").Normalize(System.Text.NormalizationForm.FormKD);
            var sb = new System.Text.StringBuilder(t.Length);
            foreach (char ch in t)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString().Trim())))[..24];
        }
    }
}
