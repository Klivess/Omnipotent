namespace Omnipotent.Data_Handling
{
    /// <summary>
    /// One shared vocabulary for rendering time to LLM agents. Every message an agent reads and
    /// every event/artifact/memory it is shown carries a timestamp in THIS format, so the model
    /// builds a consistent temporal picture: absolute wall-clock is always UTC and marked as such,
    /// and long-lived items also carry a human-scale relative age ("3h ago") so the model doesn't
    /// have to do date arithmetic to notice staleness.
    /// </summary>
    public static class TemporalFormat
    {
        /// <summary>Current wall-clock, second precision: "2026-07-12 18:04:33 UTC".</summary>
        public static string NowStamp() => Stamp(DateTime.UtcNow);

        /// <summary>Absolute stamp, second precision: "2026-07-12 18:04:33 UTC". Unspecified kinds are
        /// treated as UTC (every store in this codebase writes DateTime.UtcNow).</summary>
        public static string Stamp(DateTime value) => AsUtc(value).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

        /// <summary>Absolute stamp, minute precision, for dense line-per-item renderings: "2026-07-12 18:04 UTC".</summary>
        public static string StampMinute(DateTime value) => AsUtc(value).ToString("yyyy-MM-dd HH:mm") + " UTC";

        /// <summary>Human-scale age relative to now: "just now", "5m ago", "3h 20m ago", "12d 4h ago".
        /// A future instant renders as "in 5m" etc. (used for retry/expiry times).</summary>
        public static string Age(DateTime value) => Age(value, DateTime.UtcNow);

        internal static string Age(DateTime value, DateTime nowUtc)
        {
            var delta = nowUtc - AsUtc(value);
            bool future = delta < TimeSpan.Zero;
            if (future) delta = -delta;
            string span = delta < TimeSpan.FromSeconds(60)
                ? (future ? "under 1m" : "just now")
                : Span(delta);
            if (!future) return span == "just now" ? span : span + " ago";
            return "in " + span;
        }

        /// <summary>A bare human-scale duration ("3h 20m", "12d 4h", "45m") with no ago/in framing —
        /// for lateness notes, elapsed-time reports and trend windows.</summary>
        public static string Span(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero) delta = -delta;
            if (delta < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)delta.TotalSeconds)}s";
            if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m";
            if (delta < TimeSpan.FromHours(48)) return $"{(int)delta.TotalHours}h {delta.Minutes}m";
            return $"{(int)delta.TotalDays}d {delta.Hours}h";
        }

        /// <summary>Absolute + relative in one: "2026-07-12 18:04 UTC (3h 20m ago)".</summary>
        public static string StampWithAge(DateTime value) => $"{StampMinute(value)} ({Age(value)})";

        /// <summary>The current-time header line agents get at the top of a turn/wake:
        /// "Saturday 2026-07-12 18:04:33 UTC". Includes the weekday because scheduling
        /// reasoning ("is it a weekend?") is otherwise a silent failure mode.</summary>
        public static string ClockLine() => DateTime.UtcNow.ToString("dddd yyyy-MM-dd HH:mm:ss") + " UTC";

        private static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    /// <summary>
    /// The parsing counterpart of <see cref="TemporalFormat"/>: turns the time expressions agents
    /// naturally produce ("in 2h30m", "45m", "7d", "2026-07-15 09:00") into UTC instants and
    /// durations. One shared grammar so schedule_task, recall since/until and query_events all
    /// accept the same forms.
    /// </summary>
    public static class TemporalParse
    {
        private static readonly System.Text.RegularExpressions.Regex DurationRegex = new(
            @"^\s*(?:in\s+)?(?:(\d+)\s*d)?\s*(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?\s*(?:ago)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Parses a duration like "2h30m", "45m", "1d 6h", "90s" (units d/h/m/s; at least one required).</summary>
        public static bool TryParseDuration(string? text, out TimeSpan duration)
        {
            duration = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = DurationRegex.Match(text);
            if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success && !m.Groups[3].Success && !m.Groups[4].Success))
                return false;
            duration =
                TimeSpan.FromDays(m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0) +
                TimeSpan.FromHours(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) +
                TimeSpan.FromMinutes(m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) +
                TimeSpan.FromSeconds(m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
            return duration > TimeSpan.Zero;
        }

        /// <summary>
        /// Parses a FUTURE instant for scheduling: "in 2h30m"/"45m" (relative to now), or an absolute
        /// date-time treated as UTC ("2026-07-15 09:00"). A time-of-day with no year ("09:00") that
        /// falls in the past rolls forward to the next occurrence; an explicitly dated past instant
        /// is returned as-is for the caller to reject with a useful message.
        /// </summary>
        public static bool TryParseFutureInstant(string? text, DateTime nowUtc, out DateTime instantUtc)
        {
            instantUtc = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (TryParseDuration(text, out var span))
            {
                instantUtc = nowUtc + span;
                return true;
            }
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                bool hasExplicitYear = System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{4}");
                bool hasDateComponent = hasExplicitYear
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}\s*[-/]\s*\d{1,2}")
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)");
                if (!hasDateComponent)
                {
                    // Bare time-of-day: DateTime.TryParse resolves "09:00" against the
                    // machine's real today, but the instant must be anchored to the
                    // caller's reference date (they can differ — tests, replays).
                    parsed = DateTime.SpecifyKind(nowUtc.Date + parsed.TimeOfDay, DateTimeKind.Utc);
                }
                if (!hasExplicitYear)
                    while (parsed <= nowUtc) parsed = parsed.AddDays(1);
                instantUtc = parsed;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Parses a PAST instant for time filters (since/until): a bare duration means "that long
        /// ago" ("7d" → now-7d), or an absolute date-time treated as UTC.
        /// </summary>
        public static bool TryParsePastInstant(string? text, DateTime nowUtc, out DateTime instantUtc)
        {
            instantUtc = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (TryParseDuration(text, out var span))
            {
                instantUtc = nowUtc - span;
                return true;
            }
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                instantUtc = parsed;
                return true;
            }
            return false;
        }
    }
}
