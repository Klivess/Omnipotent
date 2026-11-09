using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>One message as placed in a prompt: its slice-local reference and everything needed to verify claims about it.</summary>
    public sealed record SliceMessage(
        string Ref,
        string MessageId,
        int Version,
        string? AuthorPersonId,
        string AuthorHandle,
        long SentAtMs,
        TimeQuality Quality,
        string ConversationId,
        string? GuildId,
        string Platform,
        string? ConversationKind,
        string SourceKind,
        string BestSourceId,
        string Text,
        bool Deleted,
        bool NonAuthored,
        string? AuthorZoneId,
        long IngestSeq,
        string? AttachmentText = null);

    /// <summary>
    /// Renders corpus messages for an LLM (plan T3 and the F-LLM injection rule). Every line carries its slice-local reference
    /// and the author-local stamp from <see cref="Stamps"/>; speakers are roster handles; replies quote their parent inline
    /// with its author when the parent is outside the excerpt (F194); mentions are resolved to handles (F193); quotes, code
    /// blocks, forwarded content, attachment text (OCR, transcripts, captions: not typed by the author) are marked as such;
    /// newlines are kept (F211); oversized messages are cut head-and-tail with a visible marker. All corpus text is fenced as
    /// data and anything resembling the fence or a reference is neutralised, so a message cannot inject instructions or forge
    /// a citation.
    /// </summary>
    public static class EvidenceRenderer
    {
        public const string FenceOpen = "<corpus-data>";
        public const string FenceClose = "</corpus-data>";

        private static readonly Regex UserMention = new(@"<@!?(\d+)>", RegexOptions.Compiled);
        private static readonly Regex RoleMention = new(@"<@&(\d+)>", RegexOptions.Compiled);
        private static readonly Regex ChannelMention = new(@"<#(\d+)>", RegexOptions.Compiled);
        private static readonly Regex CustomEmoji = new(@"<a?:([A-Za-z0-9_~\-]+):\d+>", RegexOptions.Compiled);
        private static readonly Regex DiscordTimestamp = new(@"<t:(-?\d{1,12})(?::[tTdDfFR])?>", RegexOptions.Compiled);
        private static readonly Regex RefLike = new(@"\[(m|e|f|E|F|M)\s*:\s*", RegexOptions.Compiled);

        /// <summary>Neutralises fence markers and citation-shaped text inside corpus content.</summary>
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace(FenceOpen, "‹corpus-data›", StringComparison.OrdinalIgnoreCase)
                .Replace(FenceClose, "‹/corpus-data›", StringComparison.OrdinalIgnoreCase);
            return RefLike.Replace(s, m => "[" + m.Groups[1].Value + "ː"); // modifier-letter colon: looks alike, never parses as a ref
        }

        /// <summary>Head-and-tail truncation with a marker (plan A5 / F-LLM "huge single messages").</summary>
        public static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars || maxChars < 200) return text;
            int head = (int)(maxChars * 0.7), tail = maxChars - head;
            return text[..head] + $"\n[… {text.Length - maxChars:N0} characters omitted …]\n" + text[^tail..];
        }

        /// <summary>Message content with platform markup resolved and structure marked.</summary>
        public static string Content(string? raw, Func<string, string?> handleForPlatformUserId, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = UserMention.Replace(raw, m => handleForPlatformUserId(m.Groups[1].Value) is { } h ? "@" + h : "@(unknown user)");
            s = RoleMention.Replace(s, "@(role)");
            s = ChannelMention.Replace(s, "#(channel)");
            s = CustomEmoji.Replace(s, m => ":" + m.Groups[1].Value + ":");
            s = DiscordTimestamp.Replace(s, m => long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long secs)
                ? "(time " + Instant.Iso(secs * 1000) + ")" : m.Value);
            s = MarkStructure(s);
            return Sanitize(Truncate(s, maxChars));
        }

        /// <summary>Marks quotes and code blocks so the model never mistakes quoted or pasted text for the author's own words.</summary>
        private static string MarkStructure(string s)
        {
            var sb = new StringBuilder(s.Length + 32);
            bool inCode = false;
            foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    sb.Append(inCode ? "[/code block]" : "[code block: pasted, not prose]").Append('\n');
                    inCode = !inCode;
                    continue;
                }
                if (!inCode && (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">"))
                    sb.Append("[quoting] ").Append(trimmed.Length > 2 ? trimmed[2..] : "").Append('\n');
                else
                    sb.Append(line).Append('\n');
            }
            if (inCode) sb.Append("[/code block]\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The stamp of a message (T3): author-local with weekday and offset, plus UTC; day-precision evidence shows no fake time.</summary>
        public static string Stamp(string reference, long sentAtMs, TimeQuality quality, TimeZoneInfo? authorZone)
        {
            if (quality == TimeQuality.Day)
            {
                var date = authorZone == null ? DateOnly.FromDateTime(Instant.ToUtcDateTime(sentAtMs)) : Zones.LocalDate(sentAtMs, authorZone);
                return Stamps.Day(reference, date);
            }
            return Stamps.Message(reference, sentAtMs, authorZone, quality);
        }

        /// <summary>Indents continuation lines so a multi-line message stays one visual block.</summary>
        public static string Indent(string text, string indent = "    ") =>
            string.Join("\n", text.Split('\n').Select((l, i) => i == 0 ? l : indent + l));
    }
}
