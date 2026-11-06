namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>
    /// Time windows and recency weights, always relative to an explicit as-of instant (plan rule T6),
    /// never to the wall clock (legacy defects F010/F487). Pure functions: no static mutable configuration.
    /// </summary>
    public static class TimeWindows
    {
        /// <summary>The trailing window [asOf − days, asOf] in UTC ms.</summary>
        public static (long StartMs, long EndMs) Trailing(long asOfMs, int days) =>
            (asOfMs - (long)TimeSpan.FromDays(days).TotalMilliseconds, asOfMs);

        /// <summary>The previous window of the same length, for "vs previous period" baselines.</summary>
        public static (long StartMs, long EndMs) PreviousOf((long StartMs, long EndMs) window)
        {
            long len = window.EndMs - window.StartMs;
            return (window.StartMs - len, window.StartMs);
        }

        public static bool InWindow(long ms, (long StartMs, long EndMs) window) => ms >= window.StartMs && ms <= window.EndMs;
    }

    /// <summary>Recency decay parameters, carried per run (immutable) rather than as process-global state.</summary>
    public readonly record struct DecayParameters(double HalfLifeDays, double Floor)
    {
        public static DecayParameters Default => new(180, 0.05);

        /// <summary>Floored exponential decay of evidence at <paramref name="evidenceMs"/> seen from <paramref name="asOfMs"/>.
        /// Evidence dated after the as-of instant weighs 0: it does not exist yet from that vantage point.</summary>
        public double Weight(long evidenceMs, long asOfMs)
        {
            if (evidenceMs > asOfMs) return 0;
            double ageDays = (asOfMs - evidenceMs) / 86_400_000.0;
            double hl = Math.Max(1, HalfLifeDays);
            return Math.Max(Math.Clamp(Floor, 0, 1), Math.Pow(0.5, ageDays / hl));
        }
    }
}
