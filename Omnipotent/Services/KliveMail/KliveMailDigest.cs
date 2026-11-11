using System.Net;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Services.KliveMail
{
    /// <summary>
    /// What a list row needs from a message's body, worked out once when the message is stored:
    /// a one-line preview and, for sign-in mail, the code it carries.
    ///
    /// Two reasons this is not computed per request. The preview used to be
    /// <c>substr(replace(body_text…), 1, 200)</c> in every list query, which made SQLite read every
    /// message's whole body to sort the page (35 s on the live server when those pages were not
    /// cached), and it was blank for HTML-only mail, a third of the inbox. And about a quarter of
    /// all mail is a sign-in code that had to be opened and hunted for.
    /// </summary>
    public static class KliveMailDigest
    {
        /// <summary>Raise when the rules below change: stored digests older than this are rebuilt at start.</summary>
        public const int Version = 1;

        public const int SnippetLength = 180;

        /// <summary>Codes sit at the top of a mail; only this much of the body is searched.</summary>
        private const int CodeSearchChars = 2000;

        public sealed record Digest(string Snippet, string? Code);

        public static Digest Build(string? subject, string? bodyText, string? bodyHtml)
        {
            string text = HasWords(bodyText) ? bodyText! : KliveMailRepository.HtmlToText(bodyHtml);
            return new Digest(Snippet(text), FindCode(subject, bodyText, bodyHtml));
        }

        private static bool HasWords(string? text) => !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetter);

        // Marketing mail pads its preheader with invisible joiners so clients show its own preview.
        private static readonly int[] InvisibleCodePoints = { 0x00AD, 0x034F, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180E, 0x3164, 0xFEFF, 0xFFA0 };
        private static readonly Regex Invisible = new(@"[\p{Cf}\p{Zl}\p{Zp}" + string.Concat(InvisibleCodePoints.Select(c => (char)c)) + "]", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        internal static string Snippet(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = Invisible.Replace(WebUtility.HtmlDecode(text), " ");
            s = Spaces.Replace(s, " ").Trim();
            if (s.Length <= SnippetLength) return s;
            int cut = s.LastIndexOf(' ', SnippetLength);
            return (cut > SnippetLength * 2 / 3 ? s[..cut] : s[..SnippetLength]).TrimEnd() + "…";
        }

        // A code is a 4–8 digit run (or "123 456" / "123-456") sitting next to a word that names it,
        // before it ("Enter the following code to continue: 123456") or after it ("123456 is your
        // verification code"). A loose "first number in the mail" rule finds a code in the same
        // messages and also a year, a price or an order number in 50 more of the live inbox, so the
        // cue is required. "security" alone is not a cue: it matched a street number in a footer.
        private const string Cue = @"(?:pass\s?code|code|otp|one[- ]?time|verification|verify|confirm(?:ation)?|2fa|two[- ]factor)";
        // Not part of a longer group of digits either side ("0800 1234" is a phone number, not a code).
        private const string Number = @"(?<![0-9A-Za-z#$£€@/.,_-])(?<![0-9][ -])([0-9]{4,8}|[0-9]{3}[ -][0-9]{3})(?![0-9A-Za-z%@/_-])(?![ -][0-9])";
        private static readonly Regex CueThenCode = new(@"\b" + Cue + @"\b[^0-9]{0,40}?" + Number, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CodeThenCue = new(Number + @"[^0-9]{0,30}?\b" + Cue, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The sign-in or verification code in a message, or null. The subject is the
        /// most reliable place ("483920 is your TikTok code"), then the top of the text body, then
        /// the top of the HTML body as text.</summary>
        public static string? FindCode(string? subject, string? bodyText, string? bodyHtml)
        {
            foreach (string? source in new[] { subject, Head(bodyText), Head(KliveMailRepository.HtmlToText(Head(bodyHtml, 60_000))) })
            {
                string? code = FindCodeIn(source);
                if (code != null) return code;
            }
            return null;
        }

        internal static string? FindCodeIn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = Invisible.Replace(WebUtility.HtmlDecode(text), " ");
            foreach (var pattern in new[] { CueThenCode, CodeThenCue })
            {
                for (var match = pattern.Match(s); match.Success; match = match.NextMatch())
                {
                    string code = match.Groups[1].Value;
                    if (LooksLikeAYear(code)) continue;
                    return code;
                }
            }
            return null;
        }

        private static bool LooksLikeAYear(string code)
            => code.Length == 4 && int.TryParse(code, out int n) && n >= 1900 && n <= 2099;

        private static string? Head(string? text, int max = CodeSearchChars)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

        // ── links ────────────────────────────────────────────────────────────────────────

        private static readonly Regex HrefPattern = new(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BareUrlPattern = new(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] VerificationCues = { "verify", "verif", "confirm", "activate", "activation", "validate", "email", "token", "auth", "magic", "login", "signin", "sign-in", "register", "welcome", "complete" };
        private static readonly string[] NoiseCues = { "unsubscribe", "privacy", "terms", "help", "support", "preferences", "policy", "facebook.com", "twitter.com", "instagram.com", "apple.com/app", "play.google", "mailto:" };

        public sealed record RankedLink(string Url, int Score);

        /// <summary>Unique http(s) links from the HTML and text bodies, verification-looking ones first,
        /// footer noise (unsubscribe, privacy, app stores, socials) last.</summary>
        public static List<RankedLink> RankLinks(string? html, string? text, int max = 8)
        {
            var found = new List<string>();
            void Add(string raw)
            {
                string url = WebUtility.HtmlDecode(raw).Trim().TrimEnd('.', ',', ';');
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
                if (url.Length > 2000) return;
                if (!found.Contains(url, StringComparer.Ordinal)) found.Add(url);
            }
            if (!string.IsNullOrEmpty(html))
                foreach (Match match in HrefPattern.Matches(html)) Add(match.Groups[1].Value);
            foreach (var source in new[] { text, html })
                if (!string.IsNullOrEmpty(source))
                    foreach (Match match in BareUrlPattern.Matches(source)) Add(match.Value);

            int Score(string url)
            {
                string lower = url.ToLowerInvariant();
                int score = VerificationCues.Count(cue => lower.Contains(cue)) * 10;
                if (NoiseCues.Any(cue => lower.Contains(cue))) score -= 50;
                return score;
            }
            return found
                .Select((url, index) => (url, index, score: Score(url)))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Take(max)
                .Select(x => new RankedLink(x.url, x.score))
                .ToList();
        }
    }
}
