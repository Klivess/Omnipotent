using System.Globalization;

namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>How trustworthy a stored instant is (plan rule T1). Stored next to every timestamp that
    /// did not come from a platform clock with an explicit zone.</summary>
    public enum TimeQuality
    {
        /// <summary>Platform-provided instant with an explicit zone (Discord snowflake, unix ms).</summary>
        Exact = 0,
        /// <summary>Second precision with an explicit zone.</summary>
        Second = 1,
        /// <summary>Minute precision with an explicit zone (most chat exports).</summary>
        Minute = 2,
        /// <summary>Only the calendar day is known. Excluded from hour/minute statistics (plan R4).</summary>
        Day = 3,
        /// <summary>Wall-clock time from a source without a zone; the zone was chosen at import time.
        /// DST-ambiguous or non-existent local times were resolved by rule and are flagged too.</summary>
        InferredZone = 4,
        /// <summary>Migrated from the legacy store whose zone handling is unknown (old WhatsApp imports).</summary>
        LegacyUnknownZone = 5,
    }

    /// <summary>
    /// Conversions into and out of the one stored representation: UTC epoch milliseconds (plan rule T1).
    /// Every entry point refuses ambiguous input instead of guessing: a <see cref="DateTime"/> whose
    /// <see cref="DateTime.Kind"/> is not Utc is rejected, because <c>new DateTimeOffset(DateTime)</c>
    /// silently treats Unspecified as server-local time (legacy defect F396).
    /// </summary>
    public static class Instant
    {
        /// <summary>Discord's epoch (2015-01-01T00:00:00Z) in unix milliseconds.</summary>
        public const long DiscordEpochMs = 1420070400000L;

        public static long FromUtc(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
                throw new ArgumentException($"DateTime must have Kind=Utc (got {utc.Kind}); convert explicitly at the boundary.", nameof(utc));
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        }

        public static long FromOffset(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

        public static DateTime ToUtcDateTime(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        public static DateTimeOffset ToOffset(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

        /// <summary>Creation instant encoded in a Discord snowflake (ms precision, exact).</summary>
        public static long FromDiscordSnowflake(ulong snowflake) => (long)(snowflake >> 22) + DiscordEpochMs;

        public static bool TryFromDiscordSnowflake(string? snowflake, out long ms)
        {
            ms = 0;
            if (string.IsNullOrWhiteSpace(snowflake) || !ulong.TryParse(snowflake, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return false;
            ms = FromDiscordSnowflake(id);
            return true;
        }

        /// <summary>Parses an ISO-8601 instant that carries an offset or 'Z'. Values without an offset are
        /// rejected: they are wall-clock times and must go through <see cref="Zones.ResolveLocal"/>.</summary>
        public static bool TryParseIsoWithOffset(string? text, out long ms)
        {
            ms = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            bool hasZone = t.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                           || HasOffsetSuffix(t);
            if (!hasZone) return false;
            if (!DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) return false;
            ms = dto.ToUnixTimeMilliseconds();
            return true;
        }

        private static bool HasOffsetSuffix(string t)
        {
            // ...+01:00, ...-0530, ...+00:00 after a time component.
            int tIndex = t.IndexOfAny(new[] { 'T', 't', ' ' });
            if (tIndex < 0) return false;
            int sign = t.LastIndexOfAny(new[] { '+', '-' });
            return sign > tIndex;
        }

        /// <summary>ISO-8601 UTC rendering used in logs, exports and API debugging.</summary>
        public static string Iso(long ms) => ToUtcDateTime(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
