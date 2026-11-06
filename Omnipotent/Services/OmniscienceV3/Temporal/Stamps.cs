using System.Globalization;

namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>
    /// The one way a message's time is shown to an LLM (plan rule T3). The stamp carries the author's
    /// local wall-clock reading (with weekday and offset) next to UTC, so relative words ("tomorrow",
    /// "tonight", "this weekend") resolve against the author's local day rather than the server's
    /// (legacy defects F025/F206). When the author's zone is unknown it says so instead of pretending.
    /// </summary>
    public static class Stamps
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// <c>[m:123 · 2024-01-14 Sun 23:41 author-local (UTC+0) · 2024-01-14 23:41Z]</c>
        /// </summary>
        public static string Message(string reference, long utcMs, TimeZoneInfo? authorZone, TimeQuality quality = TimeQuality.Exact)
        {
            string utc = Instant.ToUtcDateTime(utcMs).ToString("yyyy-MM-dd HH:mm", Inv) + "Z";
            string q = QualitySuffix(quality);
            if (authorZone == null)
            {
                var u = Instant.ToUtcDateTime(utcMs);
                return $"[{reference} · {u.ToString("yyyy-MM-dd ddd HH:mm", Inv)}Z (author zone unknown){q}]";
            }
            var local = Zones.ToLocal(utcMs, authorZone);
            string offset = FormatOffset(Zones.OffsetAt(utcMs, authorZone));
            return $"[{reference} · {local.ToString("yyyy-MM-dd ddd HH:mm", Inv)} author-local ({offset}) · {utc}{q}]";
        }

        /// <summary>Date-only stamp for <see cref="TimeQuality.Day"/> evidence (no fake time of day).</summary>
        public static string Day(string reference, DateOnly date) =>
            $"[{reference} · {date.ToString("yyyy-MM-dd ddd", Inv)} (time unknown)]";

        public static string FormatOffset(TimeSpan offset)
        {
            if (offset == TimeSpan.Zero) return "UTC+0";
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            var abs = offset.Duration();
            return abs.Minutes == 0
                ? $"UTC{sign}{abs.Hours}"
                : $"UTC{sign}{abs.Hours}:{abs.Minutes:00}";
        }

        private static string QualitySuffix(TimeQuality quality) => quality switch
        {
            TimeQuality.InferredZone => " · zone inferred",
            TimeQuality.LegacyUnknownZone => " · zone unknown (legacy)",
            TimeQuality.Day => " · day precision",
            _ => "",
        };
    }
}
