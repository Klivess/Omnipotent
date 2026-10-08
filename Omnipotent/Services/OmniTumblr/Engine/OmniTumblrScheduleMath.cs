using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Turns a strategy's weekly local-time slots into concrete UTC instants. Everything inside
    /// OmniTumblr is UTC; local time exists only here (and in the UI). DST is handled explicitly: a slot
    /// that falls in the spring-forward gap moves to the first valid minute after it, and a slot in the
    /// repeated autumn hour uses its first occurrence — so a weekly slot fires exactly once a week.
    /// </summary>
    public static class OmniTumblrScheduleMath
    {
        /// <summary>Resolves an IANA ("Europe/London") or Windows ("GMT Standard Time") id.</summary>
        public static bool TryResolveTimeZone(string? id, out TimeZoneInfo tz)
        {
            tz = TimeZoneInfo.Utc;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string trimmed = id.Trim();
            if (trimmed.Equals("UTC", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
                return true;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(trimmed); return true; } catch { }
            try
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsId))
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    return true;
                }
            }
            catch { }
            try
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var ianaId))
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
                    return true;
                }
            }
            catch { }
            tz = TimeZoneInfo.Utc;
            return false;
        }

        /// <summary>The zone for <paramref name="id"/>, or UTC when it is unknown.</summary>
        public static TimeZoneInfo ResolveTimeZone(string? id) => TryResolveTimeZone(id, out var tz) ? tz : TimeZoneInfo.Utc;

        public static bool IsValidTimeZone(string? id) => TryResolveTimeZone(id, out _);

        public static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        /// <summary>Every slot instant in [fromUtc, toUtc), ascending, de-duplicated.</summary>
        public static List<DateTime> SlotsBetween(OmniTumblrStrategy strategy, DateTime fromUtc, DateTime toUtc)
        {
            var result = new SortedSet<DateTime>();
            if (strategy.Slots == null || strategy.Slots.Count == 0) return result.ToList();
            fromUtc = AsUtc(fromUtc);
            toUtc = AsUtc(toUtc);
            if (toUtc <= fromUtc) return result.ToList();

            var tz = ResolveTimeZone(strategy.TimeZone);
            DateTime firstDay = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz).Date.AddDays(-1);
            DateTime lastDay = TimeZoneInfo.ConvertTimeFromUtc(toUtc, tz).Date.AddDays(1);
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                foreach (var slot in strategy.Slots)
                {
                    if (slot.Day != day.DayOfWeek) continue;
                    var local = DateTime.SpecifyKind(day.AddMinutes(Math.Clamp(slot.Minute, 0, 1439)), DateTimeKind.Unspecified);
                    var utc = LocalToUtc(local, tz);
                    if (utc >= fromUtc && utc < toUtc) result.Add(utc);
                }
            }
            return result.ToList();
        }

        public static DateTime? NextSlotAfter(OmniTumblrStrategy strategy, DateTime afterUtc)
        {
            var slots = SlotsBetween(strategy, AsUtc(afterUtc).AddTicks(1), AsUtc(afterUtc).AddDays(8));
            return slots.Count > 0 ? slots[0] : null;
        }

        public static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(local))
            {
                var probe = local;
                for (int i = 0; i < 24 * 60 && tz.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
                local = probe;
            }
            if (tz.IsAmbiguousTime(local))
            {
                // The first occurrence is the one still on the larger (daylight) offset.
                var offset = tz.GetAmbiguousTimeOffsets(local).Max();
                return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
            }
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        public static DateTime UtcToLocal(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), tz);

        /// <summary>The UTC instant at which the local calendar day containing <paramref name="utc"/> starts.</summary>
        public static DateTime LocalDayStartUtc(DateTime utc, TimeZoneInfo tz) => LocalToUtc(UtcToLocal(utc, tz).Date, tz);

        /// <summary>
        /// A stable offset of up to ± <paramref name="jitterMinutes"/> from the slot, derived from a hash
        /// of the seed and slot — re-planning a slot always lands on the same minute, but different slots
        /// and blogs don't all fire on the dot.
        /// </summary>
        public static DateTime ApplyJitter(DateTime slotUtc, int jitterMinutes, string seed)
        {
            if (jitterMinutes <= 0) return slotUtc;
            uint h = Fnv1a(seed + ":" + AsUtc(slotUtc).Ticks);
            int range = jitterMinutes * 2 + 1;
            int minutes = (int)(h % (uint)range) - jitterMinutes;
            int seconds = (int)((h >> 20) % 60u);
            return slotUtc.AddMinutes(minutes).AddSeconds(seconds);
        }

        public static int SlotsPerWeek(OmniTumblrStrategy strategy) =>
            strategy.Slots?.Select(s => (s.Day, s.Minute)).Distinct().Count() ?? 0;

        internal static uint Fnv1a(string value)
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
