using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>A request GPT-Live delegated: Klives's words, and what else was said on the call since the
/// last request (null when nothing was).</summary>
internal sealed record LiveTask(string Request, string? Context);

/// <summary>
/// The call's transcript as GPT-Live streams it (<c>session.input_transcript.delta</c> for Klives,
/// <c>session.output_transcript.delta</c> for the voice), and what each delegation asks for.
///
/// GPT-Live's <c>session.delegation.created</c> carries no task text, only the moment on the session
/// timeline it was made (<c>offset_ms</c>); the docs leave it to the backend to read the request out of
/// the transcript. Here a delegation takes every word Klives began saying up to that moment that no
/// earlier delegation took. Anything the voice and Klives said in between (a clarifying question and its
/// answer, an exchange the voice handled itself) goes with it as context, so KliveAgent sees the whole
/// thread and not just the last sentence.
/// </summary>
internal sealed class LiveTranscript
{
    /// <summary>A gap this long between Klives's fragments starts a new utterance.</summary>
    internal const long UtteranceGapMs = 700;
    /// <summary>Words that started just after the delegation still belong to it: recognition timestamps
    /// and the delegation moment are not exactly aligned.</summary>
    internal const long OffsetSlackMs = 300;
    private const int MaxFragments = 4000;

    private sealed class Fragment
    {
        internal Fragment(bool user, string text, long startMs, long endMs, long order)
        {
            User = user;
            Text = text;
            StartMs = startMs;
            EndMs = endMs;
            Order = order;
        }

        internal bool User { get; }
        internal string Text { get; }
        internal long StartMs { get; }
        internal long EndMs { get; }
        /// <summary>Arrival order, the tie-break for fragments with the same start.</summary>
        internal long Order { get; }
        internal bool Taken { get; set; }
    }

    private readonly object sync = new();
    private readonly List<Fragment> fragments = new();
    private long arrivals;
    private long lastUserDeltaTicks;

    internal void AddUser(string? delta, long startMs, long endMs) => Add(true, delta, startMs, endMs);

    internal void AddVoice(string? delta, long startMs, long endMs) => Add(false, delta, startMs, endMs);

    private void Add(bool user, string? delta, long startMs, long endMs)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (sync)
        {
            fragments.Add(new Fragment(user, delta, startMs, Math.Max(startMs, endMs), arrivals++));
            if (user) lastUserDeltaTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (fragments.Count > MaxFragments)
            {
                // A very long call: forget the oldest words, taken or not.
                fragments.RemoveRange(0, fragments.Count - MaxFragments);
            }
        }
    }

    /// <summary>How long since Klives's words last changed (MaxValue when he has said nothing yet).</summary>
    internal TimeSpan SinceLastUserWords
    {
        get
        {
            lock (sync)
                return lastUserDeltaTicks == 0 ? TimeSpan.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(lastUserDeltaTicks);
        }
    }

    /// <summary>Klives's words no delegation has taken yet ("" when there are none).</summary>
    internal string PendingUserText()
    {
        lock (sync) return Join(Ordered(fragments.Where(f => f.User && !f.Taken)));
    }

    /// <summary>
    /// What the delegation made at <paramref name="offsetMs"/> asks for, and marks those words taken.
    /// Null when Klives has said nothing since the last delegation (the voice delegated on its own
    /// initiative, or his words have not been transcribed yet).
    /// </summary>
    internal LiveTask? TakeTask(long offsetMs)
    {
        lock (sync)
        {
            var pending = fragments.Where(f => !f.Taken).ToList();
            var window = offsetMs > 0 ? pending.Where(f => f.StartMs <= offsetMs + OffsetSlackMs).ToList() : pending;
            var user = Ordered(window.Where(f => f.User));
            if (user.Count == 0)
            {
                // Timestamps that do not line up must not lose his words: take whatever he has said.
                window = pending;
                user = Ordered(window.Where(f => f.User));
                if (user.Count == 0) return null;
            }
            foreach (var f in window) f.Taken = true;

            var utterances = Utterances(user);
            string request = utterances[^1];
            var lastUser = user[^1];
            // What came before his last utterance — his earlier words and the voice's — is context.
            bool voiceBefore = window.Any(f => !f.User && f.StartMs < LastUtteranceStart(user));
            string? context = null;
            if (voiceBefore || utterances.Count > 1)
            {
                var lines = Lines(Ordered(window.Where(f => f.StartMs <= lastUser.EndMs)));
                if (lines.Count > 0 && lines[^1].User) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0)
                    context = string.Join("\n", lines.Select(l => (l.User ? "Klives: " : "Voice: ") + l.Text));
            }
            // A pause split one thought ("check the trader … and the logs") when nothing was said between
            // the parts and the last is a fragment: send it whole.
            if (!voiceBefore && utterances.Count > 1 && WordCount(request) <= 3)
            {
                request = string.Join(" ", utterances);
                context = null;
            }
            return new LiveTask(request, context);
        }
    }

    private static long LastUtteranceStart(List<Fragment> user)
    {
        long start = user[^1].StartMs;
        for (int i = user.Count - 2; i >= 0; i--)
        {
            if (start - user[i].EndMs > UtteranceGapMs) break;
            start = user[i].StartMs;
        }
        return start;
    }

    private static List<Fragment> Ordered(IEnumerable<Fragment> source) =>
        source.OrderBy(f => f.StartMs).ThenBy(f => f.Order).ToList();

    private sealed record Line(bool User, string Text);

    private static List<Line> Lines(List<Fragment> window)
    {
        var lines = new List<Line>();
        var current = new StringBuilder();
        bool? who = null;
        long lastEnd = 0;
        foreach (var f in window)
        {
            bool newLine = who != f.User || (f.User && f.StartMs - lastEnd > UtteranceGapMs);
            if (newLine && who != null)
            {
                string text = Clean(current.ToString());
                if (text.Length > 0) lines.Add(new Line(who.Value, text));
                current.Clear();
            }
            who = f.User;
            current.Append(f.Text);
            lastEnd = f.EndMs;
        }
        if (who != null)
        {
            string text = Clean(current.ToString());
            if (text.Length > 0) lines.Add(new Line(who.Value, text));
        }
        return lines;
    }

    private static List<string> Utterances(List<Fragment> user)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        long lastEnd = long.MinValue;
        foreach (var f in user)
        {
            if (current.Length > 0 && f.StartMs - lastEnd > UtteranceGapMs)
            {
                string text = Clean(current.ToString());
                if (text.Length > 0) result.Add(text);
                current.Clear();
            }
            current.Append(f.Text);
            lastEnd = Math.Max(lastEnd, f.EndMs);
        }
        string last = Clean(current.ToString());
        if (last.Length > 0) result.Add(last);
        if (result.Count == 0) result.Add(string.Empty);
        return result;
    }

    /// <summary>Deltas are pieces of words with their own spacing (" the", "trad", "er"), so within an
    /// utterance they are joined exactly as sent; a new utterance may open without a leading space.</summary>
    private static string Join(List<Fragment> user)
    {
        var sb = new StringBuilder();
        long lastEnd = long.MinValue;
        foreach (var f in user)
        {
            if (sb.Length > 0 && f.StartMs - lastEnd > UtteranceGapMs) sb.Append(' ');
            sb.Append(f.Text);
            lastEnd = Math.Max(lastEnd, f.EndMs);
        }
        return Clean(sb.ToString());
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Clean(string text) => Whitespace.Replace(text, " ").Trim();

    private static int WordCount(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
