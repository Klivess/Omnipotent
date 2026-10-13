using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// Turns a reply that is still streaming in (markdown, possibly with code) into speakable segments,
/// as early as each one is safe to say.
///
/// The first segment of each model turn is cut short on purpose — at the first clause boundary after a
/// few words — because time-to-first-audio is set by how much text the synthesiser has to render before
/// anything can play. Later segments are whole sentences, which sound better and cost fewer requests;
/// they are synthesised while the earlier ones play.
///
/// Anything that should be seen rather than heard never reaches the synthesiser: fenced blocks
/// (including the ```screen blocks the spoken-turn rules ask for), {{{ script }}} blocks, tables, URLs,
/// long identifiers and file paths.
/// </summary>
internal sealed class SpeechSegmenter
{
    internal const int FirstClauseMinWords = 4;
    internal const int FirstSegmentMaxWords = 12;
    internal const int MaxSegmentChars = 250;
    internal const int StallMinWords = 3;

    private enum Mode { Prose, Fence, Script }

    private readonly StringBuilder raw = new();
    private readonly StringBuilder prose = new();
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> lexicon;
    private Mode mode = Mode.Prose;
    private int emittedThisModelTurn;

    internal SpeechSegmenter(IReadOnlyList<(Regex Pattern, string Replacement)>? lexicon = null)
    {
        this.lexicon = lexicon ?? Array.Empty<(Regex, string)>();
    }

    /// <summary>Words waiting for a boundary.</summary>
    internal int PendingWords => CountWords(prose.ToString());

    /// <summary>Feed more of the streamed reply; returns any segments that became complete.</summary>
    internal IReadOnlyList<string> Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return Array.Empty<string>();
        raw.Append(delta);
        Scan(final: false);
        return Cut(final: false);
    }

    /// <summary>The model turn ended (a tool call is next, or the reply is done): say what is left.</summary>
    internal IReadOnlyList<string> Flush()
    {
        Scan(final: true);
        var result = Cut(final: true);
        mode = Mode.Prose;
        emittedThisModelTurn = 0;
        return result;
    }

    /// <summary>
    /// The stream paused mid-sentence (a slow model, or a long sentence). Rather than leave Klives in
    /// silence, say the words so far once there are enough of them to be worth a request.
    /// </summary>
    internal IReadOnlyList<string> TakeStalled()
    {
        string text = prose.ToString();
        // The last word may still be arriving, so only whole words before the last space count.
        int cut = text.LastIndexOfAny(new[] { ' ', '\n', '\t' });
        if (cut <= 0 || CountWords(text[..cut]) < StallMinWords) return Array.Empty<string>();
        var result = new List<string>();
        Emit(text[..cut], result);
        prose.Remove(0, cut + 1);
        return result;
    }

    /// <summary>Forget everything (a barge-in, or a model call that is about to be retried).</summary>
    internal void Reset()
    {
        raw.Clear();
        prose.Clear();
        mode = Mode.Prose;
        emittedThisModelTurn = 0;
    }

    // ── Pass 1: drop fenced code / screen blocks and {{{ script }}} blocks ──

    private void Scan(bool final)
    {
        string text = raw.ToString();
        int i = 0;
        while (i < text.Length)
        {
            if (mode == Mode.Prose)
            {
                int fence = IndexOfMarker(text, i, "```", "{{{", out string? marker);
                if (fence < 0)
                {
                    // Hold back a trailing partial marker ("`", "``", "{", "{{") until we know what it is.
                    int keep = final ? 0 : TrailingPartialMarker(text);
                    prose.Append(text, i, text.Length - keep - i);
                    i = text.Length - keep;
                    break;
                }
                prose.Append(text, i, fence - i);
                // Whatever came before the block is a complete thought: never glue it onto what follows.
                prose.Append('\n');
                mode = marker == "```" ? Mode.Fence : Mode.Script;
                i = fence + 3;
            }
            else
            {
                string close = mode == Mode.Fence ? "```" : "}}}";
                int end = text.IndexOf(close, i, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Inside a block: discard, but keep a possible partial closing marker.
                    int keep = final ? 0 : Math.Min(2, text.Length - i);
                    i = text.Length - keep;
                    break;
                }
                mode = Mode.Prose;
                i = end + 3;
            }
        }
        raw.Remove(0, Math.Min(i, raw.Length));
        if (final) raw.Clear();
    }

    private static int IndexOfMarker(string text, int from, string a, string b, out string? marker)
    {
        int ia = text.IndexOf(a, from, StringComparison.Ordinal);
        int ib = text.IndexOf(b, from, StringComparison.Ordinal);
        if (ia < 0 && ib < 0) { marker = null; return -1; }
        if (ib < 0 || (ia >= 0 && ia <= ib)) { marker = a; return ia; }
        marker = b;
        return ib;
    }

    private static int TrailingPartialMarker(string text)
    {
        int n = 0;
        for (int k = text.Length - 1; k >= 0 && n < 2; k--)
        {
            char c = text[k];
            if (c != '`' && c != '{') break;
            if (n > 0 && c != text[k + 1]) break;
            n++;
        }
        return n;
    }

    // ── Pass 2: cut speakable segments at sentence (or, for the first, clause) boundaries ──

    // Not "no", "min" or "max": in a conversational reply those end sentences far more often than they
    // abbreviate anything.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "mr", "mrs", "ms", "dr", "st", "approx", "fig", "inc", "ltd", "jr", "sr",
        "eg", "ie", "cf", "al", "dept",
    };

    private IReadOnlyList<string> Cut(bool final)
    {
        var result = new List<string>();
        while (true)
        {
            string text = prose.ToString();
            if (text.Length == 0) break;
            int end = FindBoundary(text, final);
            if (end < 0)
            {
                if (final)
                {
                    Emit(text, result);
                    prose.Clear();
                }
                break;
            }
            Emit(text[..end], result);
            prose.Remove(0, end);
        }
        return result;
    }

    /// <summary>Index just past the next segment, or -1 if no segment is complete yet.</summary>
    private int FindBoundary(string text, bool final)
    {
        bool first = emittedThisModelTurn == 0;
        int words = 0;
        bool inWord = false;
        int lastClause = -1;
        int lastSpace = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool wordChar = char.IsLetterOrDigit(c) || c == '\'';
            if (wordChar && !inWord) words++;
            inWord = wordChar;

            bool followedByBreak = i + 1 < text.Length ? char.IsWhiteSpace(text[i + 1]) : final;

            if (c == '\n')
            {
                if (words > 0) return i + 1;
                continue;
            }

            if ((c is '.' or '!' or '?' or '…') && followedByBreak && words > 0
                && !(c == '.' && IsAbbreviationOrNumber(text, i)))
                return i + 1;

            if (char.IsWhiteSpace(c)) lastSpace = i;

            bool clause = ((c is ',' or ';' or ':') && followedByBreak)
                || c is '—' or '–'
                || (c == '-' && i > 0 && text[i - 1] == ' ' && followedByBreak);
            if (clause)
            {
                // First segment of the turn: speak at the first clause boundary that has enough words
                // before it. An earlier, shorter one ("Sure, ...") is not worth its own request.
                if (first && words >= FirstClauseMinWords) return i + 1;
                lastClause = i + 1;
            }

            // First segment with no usable clause boundary: cut after a dozen words.
            if (first && words > FirstSegmentMaxWords && lastSpace > 0) return lastSpace + 1;

            // Never let a run-on sentence grow past what one request should carry.
            if (i >= MaxSegmentChars)
            {
                if (lastClause > MaxSegmentChars / 3) return lastClause;
                if (lastSpace > 0) return lastSpace + 1;
                return i + 1;
            }
        }
        return -1;
    }

    private static bool IsAbbreviationOrNumber(string text, int dot)
    {
        // "3.5", "v1.2", "klive.dev" — no whitespace after the dot, already excluded by the caller;
        // here: the word right before the dot.
        int start = dot - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '.')) start--;
        string word = text.Substring(start + 1, dot - start - 1);
        if (word.Length == 0) return false;
        if (Abbreviations.Contains(word)) return true;
        // A numbered list item ("1. First thing") is a marker, not a sentence.
        int wordStart = start + 1;
        int lineBegin = wordStart == 0 ? 0 : text.LastIndexOf('\n', wordStart - 1) + 1;
        bool lineStart = string.IsNullOrWhiteSpace(text[lineBegin..wordStart]);
        if (lineStart && word.All(char.IsDigit) && word.Length <= 3) return true;
        // A single capital letter is an initial ("J. Smith").
        return word.Length == 1 && char.IsUpper(word[0]);
    }

    private void Emit(string segment, List<string> output)
    {
        string spoken = ToSpeech(segment, lexicon);
        if (spoken.Length == 0 || !spoken.Any(char.IsLetterOrDigit)) return;
        output.Add(spoken);
        emittedThisModelTurn++;
    }

    private static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool wordChar = char.IsLetterOrDigit(c) || c == '\'';
            if (wordChar && !inWord) words++;
            inWord = wordChar;
        }
        return words;
    }

    // ── Normalisation: markdown → something a synthesiser can read aloud ──

    private static readonly Regex TableRow = new(@"(?m)^[^\n]*\|[^\n]*\|[^\n]*$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"(?m)^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"(?m)^\s*>\s?", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"(?m)^\s*(?:[-*+•]|\d{1,3}[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)\s]+\)", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"\b(?:https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InlineCode = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"\*\*|__|~~|\*", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HexId = new(@"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{12,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsPath = new(@"\b[A-Za-z]:\\(?:[^\\\s]+\\)*([^\\\s]+)", RegexOptions.Compiled);
    // Needs a letter somewhere in the run, so a date like 10/12/2026 is left for the synthesiser.
    private static readonly Regex UnixPath = new(@"(?<![\w.:/])(?=\S*[A-Za-z])(?:/?[\w.\-]+/){2,}([\w.\-]+)", RegexOptions.Compiled);
    private static readonly Regex Arrow = new(@"\s*(?:->|=>|→)\s*", RegexOptions.Compiled);
    private static readonly Regex Approx = new(@"[~≈]\s*(?=\d)", RegexOptions.Compiled);
    private static readonly Regex Emoji = new(@"[\p{So}\p{Cs}️‍]", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"[\[\]{}<>|\\^]", RegexOptions.Compiled);
    private static readonly Regex Underscore = new(@"(?<=\w)_(?=\w)|(?<!\w)_|_(?!\w)", RegexOptions.Compiled);
    private static readonly Regex CamelLower = new(@"(?<=\p{Ll})(?=\p{Lu})", RegexOptions.Compiled);
    private static readonly Regex CamelAcronym = new(@"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    internal static string ToSpeech(string segment, IReadOnlyList<(Regex Pattern, string Replacement)>? lexicon = null)
    {
        string s = segment ?? string.Empty;
        s = TableRow.Replace(s, " ");
        s = Heading.Replace(s, "");
        s = Quote.Replace(s, "");
        s = Bullet.Replace(s, "");
        s = Link.Replace(s, "$1");
        s = Url.Replace(s, "the link");
        s = InlineCode.Replace(s, "$1");
        s = s.Replace("`", "");
        s = Emphasis.Replace(s, "");
        s = Guid.Replace(s, "an ID");
        s = HexId.Replace(s, "an ID");
        s = WindowsPath.Replace(s, "$1");
        s = UnixPath.Replace(s, "$1");
        s = Arrow.Replace(s, " to ");
        s = Approx.Replace(s, "about ");
        s = s.Replace("&", " and ");
        s = Emoji.Replace(s, "");
        s = Brackets.Replace(s, " ");
        s = Underscore.Replace(s, " ");
        if (lexicon != null)
            foreach (var (pattern, replacement) in lexicon)
                s = pattern.Replace(s, replacement);
        s = CamelLower.Replace(s, " ");
        s = CamelAcronym.Replace(s, " ");
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>
    /// Parses the pronunciation setting: <c>Written=Spoken</c> pairs separated by ';' or new lines, e.g.
    /// <c>Klives=Clives; KM=K M</c>. Whole words, case-insensitive. Applied before CamelCase splitting so
    /// keys can be written the way they appear in replies.
    /// </summary>
    internal static IReadOnlyList<(Regex Pattern, string Replacement)> ParseLexicon(string? setting)
    {
        var result = new List<(Regex, string)>();
        foreach (string entry in (setting ?? string.Empty).Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            string written = entry[..eq].Trim();
            string spoken = entry[(eq + 1)..].Trim();
            if (written.Length == 0) continue;
            result.Add((new Regex(@"(?<![\w])" + Regex.Escape(written) + @"(?![\w])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                spoken.Replace("$", "$$")));
        }
        return result;
    }
}
