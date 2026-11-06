namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>
    /// A closed uncertainty interval [EarliestMs, LatestMs] in UTC epoch milliseconds (plan rule T2).
    /// Used wherever a time is known only approximately: a birth date implied by "I'm 17", the start
    /// of "together 2 years", a plan's day, a fact's valid-from. Uncertainty is carried, never collapsed
    /// into a fake precise date (plan rule T8).
    /// </summary>
    public readonly record struct TimeInterval
    {
        public long EarliestMs { get; }
        public long LatestMs { get; }

        public TimeInterval(long earliestMs, long latestMs)
        {
            if (latestMs < earliestMs)
                throw new ArgumentException($"Interval end {latestMs} precedes start {earliestMs}.");
            EarliestMs = earliestMs;
            LatestMs = latestMs;
        }

        public static TimeInterval At(long ms) => new(ms, ms);

        /// <summary>Unbounded on both sides: "no information".</summary>
        public static TimeInterval Unknown => new(long.MinValue, long.MaxValue);

        public bool IsUnknown => EarliestMs == long.MinValue && LatestMs == long.MaxValue;

        public bool IsPoint => EarliestMs == LatestMs;

        public long WidthMs => IsUnknown ? long.MaxValue : LatestMs - EarliestMs;

        public long MidpointMs => IsUnknown ? 0 : EarliestMs + (LatestMs - EarliestMs) / 2;

        public bool Contains(long ms) => ms >= EarliestMs && ms <= LatestMs;

        public bool Overlaps(TimeInterval other) => EarliestMs <= other.LatestMs && other.EarliestMs <= LatestMs;

        /// <summary>Intersection of two constraints; null when they are inconsistent (an empty set marks a
        /// joke/misread candidate, plan rule T3).</summary>
        public TimeInterval? Intersect(TimeInterval other)
        {
            long start = Math.Max(EarliestMs, other.EarliestMs);
            long end = Math.Min(LatestMs, other.LatestMs);
            return end < start ? null : new TimeInterval(start, end);
        }

        /// <summary>Widens both ends by <paramref name="slack"/> (saturating), e.g. for zone uncertainty on imports.</summary>
        public TimeInterval Widen(TimeSpan slack)
        {
            long s = (long)slack.TotalMilliseconds;
            long start = EarliestMs == long.MinValue ? long.MinValue : SaturatingSub(EarliestMs, s);
            long end = LatestMs == long.MaxValue ? long.MaxValue : SaturatingAdd(LatestMs, s);
            return new TimeInterval(start, end);
        }

        /// <summary>Intersects every constraint; returns null as soon as they become inconsistent.</summary>
        public static TimeInterval? IntersectAll(IEnumerable<TimeInterval> constraints)
        {
            TimeInterval acc = Unknown;
            foreach (var c in constraints)
            {
                var next = acc.Intersect(c);
                if (next == null) return null;
                acc = next.Value;
            }
            return acc;
        }

        private static long SaturatingAdd(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

        private static long SaturatingSub(long a, long b) => b > 0 && a < long.MinValue + b ? long.MinValue : a - b;

        public override string ToString() => IsUnknown
            ? "[unknown]"
            : IsPoint ? $"[{Instant.Iso(EarliestMs)}]" : $"[{Instant.Iso(EarliestMs)} .. {Instant.Iso(LatestMs)}]";
    }
}
