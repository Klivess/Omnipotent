using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>How a wall-clock time that occurs twice (DST fall-back) is resolved.</summary>
    public enum AmbiguityPolicy
    {
        /// <summary>The first occurrence (the daylight-time reading).</summary>
        Earlier,
        /// <summary>The second occurrence (the standard-time reading).</summary>
        Later,
    }

    /// <summary>Outcome of resolving a local wall-clock time to an instant.</summary>
    public readonly record struct LocalResolution(long UtcMs, bool WasAmbiguous, bool WasInvalid, TimeSpan OffsetUsed);

    /// <summary>
    /// IANA time-zone handling for v3 (plan rule T4). Zones are always IANA ids ("Europe/London") so DST
    /// is automatic; on Windows .NET resolves IANA ids through ICU. All conversions are explicit about
    /// the two DST edge cases: a local time that does not exist (spring-forward gap) and a local time that
    /// exists twice (fall-back overlap).
    /// </summary>
    public static class Zones
    {
        public const string Utc = "Etc/UTC";
        public const string DefaultKlivesZone = "Europe/London";

        private static readonly ConcurrentDictionary<string, TimeZoneInfo?> cache = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string? ianaId, out TimeZoneInfo zone)
        {
            zone = TimeZoneInfo.Utc;
            if (string.IsNullOrWhiteSpace(ianaId)) return false;
            var found = cache.GetOrAdd(ianaId.Trim(), static id =>
            {
                if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals(Utc, StringComparison.OrdinalIgnoreCase))
                    return TimeZoneInfo.Utc;
                // .NET resolves IANA ids through ICU on Windows; fall back to the Windows id mapping when
                // ICU is unavailable (invariant globalization).
                return FindOrNull(id)
                       ?? (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out string? windowsId) ? FindOrNull(windowsId) : null);
            });
            if (found == null) return false;
            zone = found;
            return true;
        }

        private static TimeZoneInfo? FindOrNull(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { return null; }
            catch (InvalidTimeZoneException) { return null; }
        }

        public static TimeZoneInfo GetOrThrow(string ianaId) =>
            TryGet(ianaId, out var zone) ? zone : throw new TimeZoneNotFoundException($"Unknown IANA time zone '{ianaId}'.");

        /// <summary>Local wall-clock reading of an instant in <paramref name="zone"/> (Kind=Unspecified on purpose:
        /// it is a reading, not an instant).</summary>
        public static DateTime ToLocal(long utcMs, TimeZoneInfo zone)
        {
            var utc = Instant.ToUtcDateTime(utcMs);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        }

        public static TimeSpan OffsetAt(long utcMs, TimeZoneInfo zone) => zone.GetUtcOffset(Instant.ToUtcDateTime(utcMs));

        /// <summary>
        /// Resolves a wall-clock reading in <paramref name="zone"/> to an instant.
        /// Non-existent times (spring gap) shift forward by the gap length, so 01:30 on a London
        /// spring-forward day becomes 02:30 BST (= 01:30 UTC). Ambiguous times follow <paramref name="policy"/>.
        /// </summary>
        public static LocalResolution ResolveLocal(DateTime wallClock, TimeZoneInfo zone, AmbiguityPolicy policy = AmbiguityPolicy.Earlier)
        {
            var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

            if (zone.IsInvalidTime(local))
            {
                // The reading falls in a spring-forward gap. Interpret it with the offset in force just
                // before the transition, which is the same as shifting it forward by the gap length:
                // London 01:30 on spring-forward day → 01:30Z, displayed afterwards as 02:30 BST.
                // Step back minute by minute to the last valid reading (gaps are at most a few hours;
                // minute steps are exact for every real zone, including 30/45-minute offsets).
                var before = local;
                for (int i = 0; i < 24 * 60 && zone.IsInvalidTime(before); i++) before = before.AddMinutes(-1);
                var offsetBefore = zone.GetUtcOffset(before);
                long ms = Instant.FromUtc(DateTime.SpecifyKind(local - offsetBefore, DateTimeKind.Utc));
                return new LocalResolution(ms, WasAmbiguous: false, WasInvalid: true, OffsetUsed: offsetBefore);
            }

            if (zone.IsAmbiguousTime(local))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(local);
                // Larger offset = earlier instant (daylight reading).
                var ordered = offsets.OrderByDescending(o => o).ToArray();
                var chosen = policy == AmbiguityPolicy.Earlier ? ordered[0] : ordered[^1];
                long ms = Instant.FromUtc(DateTime.SpecifyKind(local - chosen, DateTimeKind.Utc));
                return new LocalResolution(ms, WasAmbiguous: true, WasInvalid: false, OffsetUsed: chosen);
            }

            var normalOffset = zone.GetUtcOffset(local);
            long normalMs = Instant.FromUtc(DateTime.SpecifyKind(local - normalOffset, DateTimeKind.Utc));
            return new LocalResolution(normalMs, WasAmbiguous: false, WasInvalid: false, OffsetUsed: normalOffset);
        }

        /// <summary>
        /// Resolves a chronologically ordered sequence of wall-clock readings (a chat export) so the result
        /// stays non-decreasing across a DST fall-back: an ambiguous reading takes the earlier instant unless
        /// that would go backwards relative to the previous message, in which case it takes the later one.
        /// Every ambiguous or invalid reading is reported so callers can flag time_quality.
        /// </summary>
        public static IReadOnlyList<LocalResolution> ResolveSequence(IReadOnlyList<DateTime> wallClocks, TimeZoneInfo zone)
        {
            var results = new List<LocalResolution>(wallClocks.Count);
            long previous = long.MinValue;
            foreach (var wc in wallClocks)
            {
                var r = ResolveLocal(wc, zone, AmbiguityPolicy.Earlier);
                if (r.WasAmbiguous && r.UtcMs < previous)
                    r = ResolveLocal(wc, zone, AmbiguityPolicy.Later);
                results.Add(r);
                previous = Math.Max(previous, r.UtcMs);
            }
            return results;
        }

        private static readonly ConcurrentDictionary<(string Zone, long From, long To), IReadOnlyList<(long FromMs, long OffsetMs)>> segmentCache = new();
        private const long SegmentGridMs = 30L * 86_400_000;

        /// <summary>
        /// The UTC offset of <paramref name="zone"/> across [<paramref name="fromMs"/>, <paramref name="toMs"/>] as ordered
        /// segments: each offset applies from its <c>FromMs</c> (the first is <see cref="long.MinValue"/>) until the next
        /// segment begins. Lets a set-based engine convert instants to local wall-clock time with plain arithmetic, exactly
        /// as <see cref="OffsetAt"/> would for each one (transitions are located to the second).
        /// </summary>
        public static IReadOnlyList<(long FromMs, long OffsetMs)> OffsetSegments(TimeZoneInfo zone, long fromMs, long toMs)
        {
            if (toMs < fromMs) (fromMs, toMs) = (toMs, fromMs);
            // Snapped outwards to a coarse grid so a slowly growing range reuses its cached answer.
            long from = Math.Max(0, fromMs) / SegmentGridMs * SegmentGridMs, to = (Math.Max(0, toMs) / SegmentGridMs + 1) * SegmentGridMs;
            return segmentCache.GetOrAdd((zone.Id, from, to), key =>
            {
                long Offset(long sec) => (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime).TotalMilliseconds;
                const long Step = 6 * 3600;
                long at = key.From / 1000, end = key.To / 1000, offset = Offset(at);
                var segments = new List<(long FromMs, long OffsetMs)> { (long.MinValue, offset) };
                while (at < end)
                {
                    long next = Math.Min(end, at + Step), nextOffset = Offset(next);
                    if (nextOffset != offset)
                    {
                        long lo = at, hi = next; // lo still has the old offset, hi already the new one
                        while (hi - lo > 1)
                        {
                            long mid = lo + (hi - lo) / 2;
                            if (Offset(mid) == offset) lo = mid; else hi = mid;
                        }
                        segments.Add((hi * 1000, nextOffset));
                        offset = nextOffset;
                    }
                    at = next;
                }
                if (segmentCache.Count > 512) segmentCache.Clear();
                return segments;
            });
        }

        /// <summary>Local calendar day of an instant.</summary>
        public static DateOnly LocalDate(long utcMs, TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(utcMs, zone));

        /// <summary>UTC instant at which local day <paramref name="date"/> starts in <paramref name="zone"/>
        /// (handles zones whose midnight falls in a DST gap by taking the first existing instant).</summary>
        public static long DayStartUtcMs(DateOnly date, TimeZoneInfo zone) =>
            ResolveLocal(date.ToDateTime(TimeOnly.MinValue), zone).UtcMs;

        /// <summary>[start, end) of a local day in UTC ms. DST days are 23 or 25 hours long.</summary>
        public static (long StartMs, long EndMs) LocalDayRange(DateOnly date, TimeZoneInfo zone) =>
            (DayStartUtcMs(date, zone), DayStartUtcMs(date.AddDays(1), zone));

        /// <summary>Local hour-of-day (0–23) of an instant, DST-correct.</summary>
        public static int LocalHour(long utcMs, TimeZoneInfo zone) => ToLocal(utcMs, zone).Hour;
    }
}
