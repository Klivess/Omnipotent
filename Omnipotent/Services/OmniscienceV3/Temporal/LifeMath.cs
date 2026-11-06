namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>Education systems with different school-year boundaries (plan rule T3).</summary>
    public enum EducationSystem
    {
        Unknown = 0,
        /// <summary>England and Wales: Year N runs 1 Sep–31 Aug; children turn N+5 during Year N.</summary>
        EnglandWales,
        /// <summary>Northern Ireland: same shape as England but the birthday cut-off is 1 July.</summary>
        NorthernIreland,
        /// <summary>Scotland: P1–P7 then S1–S6; cut-off ~1 March with common deferrals, so intervals are widened.</summary>
        Scotland,
        /// <summary>United States grade N (kindergarten = 0); cut-off dates vary by state, so intervals are widened.</summary>
        UnitedStates,
    }

    /// <summary>
    /// Turns age and school-year statements into birth-date intervals, and back into age ranges at any
    /// instant (plan rule T2 "time-derived" attributes). Age is never stored as a static value: the
    /// birth interval is the anchor and current age is computed at query time (legacy defects F026/F208).
    /// All arithmetic is calendar arithmetic in UTC; a few hours of zone error is irrelevant at year scale.
    /// </summary>
    public static class LifeMath
    {
        /// <summary>"I'm N" said at <paramref name="assertedAtMs"/>: birth ∈ (t − (N+1)y, t − N y].</summary>
        public static TimeInterval BirthFromAge(int age, long assertedAtMs)
        {
            if (age < 0 || age > 130) throw new ArgumentOutOfRangeException(nameof(age));
            var t = Instant.ToUtcDateTime(assertedAtMs);
            long latest = Instant.FromUtc(t.AddYears(-age));
            long earliest = Instant.FromUtc(t.AddYears(-(age + 1))) + 1;
            return new TimeInterval(earliest, latest);
        }

        /// <summary>"Just turned N": birthday within the last <paramref name="recency"/> (default ~5 weeks).</summary>
        public static TimeInterval BirthFromJustTurned(int age, long assertedAtMs, TimeSpan? recency = null)
        {
            var slack = recency ?? TimeSpan.FromDays(35);
            var t = Instant.ToUtcDateTime(assertedAtMs);
            long latest = Instant.FromUtc(t.AddYears(-age));
            long earliest = Instant.FromUtc((t - slack).AddYears(-age));
            return new TimeInterval(earliest, latest);
        }

        /// <summary>"Turning N next month" / "in K days": birthday falls within [t + minAhead, t + maxAhead].</summary>
        public static TimeInterval BirthFromTurning(int age, long assertedAtMs, TimeSpan minAhead, TimeSpan maxAhead)
        {
            if (maxAhead < minAhead) throw new ArgumentException("maxAhead < minAhead");
            var t = Instant.ToUtcDateTime(assertedAtMs);
            long earliest = Instant.FromUtc((t + minAhead).AddYears(-age));
            long latest = Instant.FromUtc((t + maxAhead).AddYears(-age));
            return new TimeInterval(earliest, latest);
        }

        /// <summary>A birthday on a known calendar date (year unknown) intersected with an existing interval
        /// narrows it to at most one candidate per year; returned as candidates.</summary>
        public static IReadOnlyList<TimeInterval> BirthdayCandidates(int month, int day, TimeInterval within)
        {
            var list = new List<TimeInterval>();
            if (within.IsUnknown) return list;
            int fromYear = Instant.ToUtcDateTime(within.EarliestMs).Year;
            int toYear = Instant.ToUtcDateTime(within.LatestMs).Year;
            for (int y = fromYear; y <= toYear; y++)
            {
                // Feb 29 in a non-leap year is not a valid birth date; skip rather than invent Feb 28.
                if (month == 2 && day == 29 && !DateTime.IsLeapYear(y)) continue;
                if (day > DateTime.DaysInMonth(y, month)) continue;
                var start = new DateTime(y, month, day, 0, 0, 0, DateTimeKind.Utc);
                var dayInterval = new TimeInterval(Instant.FromUtc(start), Instant.FromUtc(start.AddDays(1)) - 1);
                var hit = dayInterval.Intersect(within);
                if (hit != null) list.Add(hit.Value);
            }
            return list;
        }

        /// <summary>Possible ages at <paramref name="atMs"/> given a birth interval (min, max). Null when unknown.</summary>
        public static (int Min, int Max)? AgeRangeAt(TimeInterval birth, long atMs)
        {
            if (birth.IsUnknown) return null;
            int max = WholeYearsBetween(birth.EarliestMs, atMs);
            int min = WholeYearsBetween(birth.LatestMs, atMs);
            if (max < 0) return null; // asked about a time before any possible birth
            return (Math.Max(0, min), max);
        }

        /// <summary>True when the person is certainly under <paramref name="age"/> at <paramref name="atMs"/>.</summary>
        public static bool CertainlyUnder(TimeInterval birth, int age, long atMs) =>
            AgeRangeAt(birth, atMs) is { } r && r.Max < age;

        /// <summary>True when the person might be under <paramref name="age"/> at <paramref name="atMs"/>.</summary>
        public static bool PossiblyUnder(TimeInterval birth, int age, long atMs) =>
            AgeRangeAt(birth, atMs) is { } r && r.Min < age;

        private static int WholeYearsBetween(long fromMs, long toMs)
        {
            var from = Instant.ToUtcDateTime(fromMs);
            var to = Instant.ToUtcDateTime(toMs);
            int years = to.Year - from.Year;
            if (to < from.AddYears(years)) years--;
            return years;
        }

        /// <summary>
        /// Birth interval implied by "I'm in year/grade <paramref name="level"/>" said at <paramref name="assertedAtMs"/>.
        /// Returns <see cref="TimeInterval.Unknown"/> for an unknown system with no level mapping.
        /// </summary>
        public static TimeInterval BirthFromSchoolLevel(int level, EducationSystem system, long assertedAtMs)
        {
            var t = Instant.ToUtcDateTime(assertedAtMs);
            switch (system)
            {
                case EducationSystem.EnglandWales:
                {
                    // Academic year containing t starts 1 Sep of startYear. Year N children turn N+5 during it:
                    // born 1 Sep (startYear − N − 5) .. 31 Aug (startYear − N − 4).
                    int startYear = t.Month >= 9 ? t.Year : t.Year - 1;
                    var from = new DateTime(startYear - level - 5, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                    var to = new DateTime(startYear - level - 4, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                    return new TimeInterval(Instant.FromUtc(from), Instant.FromUtc(to) - 1);
                }
                case EducationSystem.NorthernIreland:
                {
                    // Same shape; birthday cut-off 1 July: born 2 Jul (startYear − N − 5) .. 1 Jul (startYear − N − 4).
                    int startYear = t.Month >= 9 ? t.Year : t.Year - 1;
                    var from = new DateTime(startYear - level - 5, 7, 2, 0, 0, 0, DateTimeKind.Utc);
                    var to = new DateTime(startYear - level - 4, 7, 2, 0, 0, 0, DateTimeKind.Utc);
                    return new TimeInterval(Instant.FromUtc(from), Instant.FromUtc(to) - 1);
                }
                case EducationSystem.Scotland:
                {
                    // Session starts mid-August. P1 children are ~4.5–5.5 at entry (cut-off 1 March, deferrals
                    // common): treat "P{n}" as level n and "S{n}" callers pass n + 7. Widen by 6 months each side.
                    int startYear = t.Month >= 8 ? t.Year : t.Year - 1;
                    var from = new DateTime(startYear - level - 5, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                    var to = new DateTime(startYear - level - 4, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                    return new TimeInterval(Instant.FromUtc(from), Instant.FromUtc(to) - 1).Widen(TimeSpan.FromDays(183));
                }
                case EducationSystem.UnitedStates:
                {
                    // Grade N (K = 0) children turn N+6 during the school year (cut-offs vary by state: widen 4 months).
                    int startYear = t.Month >= 8 ? t.Year : t.Year - 1;
                    var from = new DateTime(startYear - level - 6, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                    var to = new DateTime(startYear - level - 5, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                    return new TimeInterval(Instant.FromUtc(from), Instant.FromUtc(to) - 1).Widen(TimeSpan.FromDays(122));
                }
                default:
                {
                    // Unknown system: assume "level N ≈ age N+5", widened by a year either side.
                    var from = t.AddYears(-(level + 7));
                    var to = t.AddYears(-(level + 4));
                    return new TimeInterval(Instant.FromUtc(from), Instant.FromUtc(to));
                }
            }
        }

        /// <summary>Start interval implied by a duration statement ("together 2 years", "been here 6 months").</summary>
        public static TimeInterval StartFromDuration(TimeSpan duration, long assertedAtMs, double relativeSlack = 0.15)
        {
            long d = (long)duration.TotalMilliseconds;
            long slack = (long)(d * Math.Clamp(relativeSlack, 0, 1));
            long center = assertedAtMs - d;
            return new TimeInterval(center - slack, Math.Min(assertedAtMs, center + slack));
        }
    }
}
