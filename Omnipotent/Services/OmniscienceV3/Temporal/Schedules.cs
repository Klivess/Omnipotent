namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>
    /// Zone-anchored, DST-safe schedule arithmetic (plan rule T7). Replaces the legacy
    /// "DateTime.Today.AddDays(1).AddHours(3).AddMinutes(30)" (server-local, DST-naive, skips the
    /// current night when started before 03:30 — defects F046/F507).
    /// </summary>
    public static class Schedules
    {
        /// <summary>
        /// Next instant strictly after <paramref name="afterMs"/> at which the local clock in
        /// <paramref name="zone"/> reads <paramref name="timeOfDay"/>. On a spring-forward day where that
        /// reading does not exist, it is shifted forward by the gap (London 01:30 → 02:30 BST), so the job
        /// still fires that day. On a fall-back day where the reading exists twice, only the first occurrence
        /// counts, so a daily job fires exactly once.
        /// </summary>
        public static long NextDailyOccurrence(long afterMs, TimeOnly timeOfDay, TimeZoneInfo zone)
        {
            var localDate = Zones.LocalDate(afterMs, zone);
            for (int i = 0; i < 3; i++)
            {
                var candidateLocal = localDate.AddDays(i).ToDateTime(timeOfDay);
                var resolved = Zones.ResolveLocal(candidateLocal, zone, AmbiguityPolicy.Earlier);
                if (resolved.UtcMs > afterMs) return resolved.UtcMs;
            }
            // Unreachable for real zones (a calendar day always has the reading or its gap substitute).
            throw new InvalidOperationException("No daily occurrence found within three days.");
        }

        /// <summary>
        /// Missed-run policy shared with the KliveAgent scheduler: when the scheduled instant passed while the
        /// process was down, fire once (late) and report the lateness; never replay every missed occurrence.
        /// Returns (fireNow, lateness, nextAfterThat).
        /// </summary>
        public static (bool FireNow, TimeSpan Lateness, long NextMs) EvaluateMissed(long scheduledMs, long nowMs, TimeOnly timeOfDay, TimeZoneInfo zone)
        {
            if (nowMs < scheduledMs) return (false, TimeSpan.Zero, scheduledMs);
            var late = TimeSpan.FromMilliseconds(nowMs - scheduledMs);
            long next = NextDailyOccurrence(nowMs, timeOfDay, zone);
            return (true, late, next);
        }
    }
}
