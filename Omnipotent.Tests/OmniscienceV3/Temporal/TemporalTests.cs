using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Temporal
{
    /// <summary>Plan rule T9 fixtures for the v3 temporal library: DST in both hemispheres, leap days,
    /// year boundaries, snowflakes, ages and school years, schedules. Synthetic values only.</summary>
    public class TemporalTests
    {
        private static long Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) =>
            Instant.FromUtc(new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Utc));

        private static TimeZoneInfo London => Zones.GetOrThrow("Europe/London");
        private static TimeZoneInfo NewYork => Zones.GetOrThrow("America/New_York");
        private static TimeZoneInfo Sydney => Zones.GetOrThrow("Australia/Sydney");

        // ── Instants ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void FromUtc_RejectsNonUtcKinds(DateTimeKind kind)
        {
            var dt = DateTime.SpecifyKind(new DateTime(2024, 1, 1, 12, 0, 0), kind);
            Assert.Throws<ArgumentException>(() => Instant.FromUtc(dt));
        }

        [Fact]
        public void Snowflake_DecodesDiscordDocumentationExample()
        {
            // Discord's documented example snowflake: 2016-04-30T11:18:25.796Z.
            Assert.Equal(Utc(2016, 4, 30, 11, 18, 25, 796), Instant.FromDiscordSnowflake(175928847299117063UL));
            Assert.True(Instant.TryFromDiscordSnowflake("175928847299117063", out long ms));
            Assert.Equal(Utc(2016, 4, 30, 11, 18, 25, 796), ms);
            Assert.False(Instant.TryFromDiscordSnowflake("not-a-snowflake", out _));
            Assert.False(Instant.TryFromDiscordSnowflake("-5", out _));
        }

        [Fact]
        public void IsoParsing_RequiresAnExplicitZone()
        {
            Assert.True(Instant.TryParseIsoWithOffset("2023-01-01 12:00:00.000000+00:00", out long a));
            Assert.Equal(Utc(2023, 1, 1, 12), a);
            Assert.True(Instant.TryParseIsoWithOffset("2023-06-01T09:30:00+01:00", out long b));
            Assert.Equal(Utc(2023, 6, 1, 8, 30), b);
            Assert.True(Instant.TryParseIsoWithOffset("2023-06-01T09:30:00Z", out long c));
            Assert.Equal(Utc(2023, 6, 1, 9, 30), c);
            Assert.False(Instant.TryParseIsoWithOffset("2023-06-01T09:30:00", out _));
            Assert.False(Instant.TryParseIsoWithOffset("2023-06-01", out _));
        }

        // ── Intervals ────────────────────────────────────────────────────────────

        [Fact]
        public void Interval_IntersectWidenAndInconsistency()
        {
            var a = new TimeInterval(10, 20);
            var b = new TimeInterval(15, 30);
            Assert.Equal(new TimeInterval(15, 20), a.Intersect(b));
            Assert.Null(a.Intersect(new TimeInterval(21, 25)));
            Assert.Equal(new TimeInterval(5, 25), a.Widen(TimeSpan.FromMilliseconds(5)));
            Assert.True(TimeInterval.Unknown.Widen(TimeSpan.FromDays(1)).IsUnknown);
            Assert.Throws<ArgumentException>(() => new TimeInterval(5, 4));
            Assert.Null(TimeInterval.IntersectAll(new[] { a, b, new TimeInterval(0, 14) }));
        }

        // ── Zones & DST ──────────────────────────────────────────────────────────

        [Fact]
        public void Zones_ResolveIanaIdsAndRejectUnknown()
        {
            Assert.True(Zones.TryGet("Europe/London", out _));
            Assert.True(Zones.TryGet("America/New_York", out _));
            Assert.True(Zones.TryGet("Australia/Sydney", out _));
            Assert.True(Zones.TryGet("UTC", out var utc));
            Assert.Equal(TimeSpan.Zero, utc.BaseUtcOffset);
            Assert.False(Zones.TryGet("Mars/Olympus_Mons", out _));
            Assert.False(Zones.TryGet("", out _));
        }

        [Fact]
        public void London_SpringForwardGap_ShiftsForward()
        {
            // 2024-03-31: 01:00 GMT → 02:00 BST. 01:30 does not exist.
            var r = Zones.ResolveLocal(new DateTime(2024, 3, 31, 1, 30, 0), London);
            Assert.True(r.WasInvalid);
            Assert.Equal(Utc(2024, 3, 31, 1, 30), r.UtcMs);
            Assert.Equal(new DateTime(2024, 3, 31, 2, 30, 0), Zones.ToLocal(r.UtcMs, London));
        }

        [Fact]
        public void London_FallBackOverlap_FollowsPolicy()
        {
            // 2024-10-27: 02:00 BST → 01:00 GMT. 01:30 happens twice.
            var local = new DateTime(2024, 10, 27, 1, 30, 0);
            var earlier = Zones.ResolveLocal(local, London, AmbiguityPolicy.Earlier);
            var later = Zones.ResolveLocal(local, London, AmbiguityPolicy.Later);
            Assert.True(earlier.WasAmbiguous);
            Assert.Equal(Utc(2024, 10, 27, 0, 30), earlier.UtcMs);
            Assert.Equal(Utc(2024, 10, 27, 1, 30), later.UtcMs);
        }

        [Fact]
        public void ChatExportSequence_StaysMonotonicAcrossFallBack()
        {
            var seq = new[]
            {
                new DateTime(2024, 10, 27, 1, 50, 0), // BST reading
                new DateTime(2024, 10, 27, 1, 10, 0), // after clocks went back: GMT reading
                new DateTime(2024, 10, 27, 1, 20, 0),
                new DateTime(2024, 10, 27, 2, 5, 0),
            };
            var r = Zones.ResolveSequence(seq, London);
            Assert.Equal(Utc(2024, 10, 27, 0, 50), r[0].UtcMs);
            Assert.Equal(Utc(2024, 10, 27, 1, 10), r[1].UtcMs);
            Assert.Equal(Utc(2024, 10, 27, 1, 20), r[2].UtcMs);
            Assert.Equal(Utc(2024, 10, 27, 2, 5), r[3].UtcMs);
            for (int i = 1; i < r.Count; i++) Assert.True(r[i].UtcMs >= r[i - 1].UtcMs);
            Assert.True(r[1].WasAmbiguous && r[2].WasAmbiguous);
        }

        [Fact]
        public void NewYork_SpringForwardGap()
        {
            // 2024-03-10: 02:00 EST → 03:00 EDT. 02:30 does not exist; EST (−5) applies.
            var r = Zones.ResolveLocal(new DateTime(2024, 3, 10, 2, 30, 0), NewYork);
            Assert.True(r.WasInvalid);
            Assert.Equal(Utc(2024, 3, 10, 7, 30), r.UtcMs);
        }

        [Fact]
        public void Sydney_SouthernHemisphereTransitions()
        {
            // DST starts 2024-10-06 02:00 AEST → 03:00 AEDT (gap).
            var gap = Zones.ResolveLocal(new DateTime(2024, 10, 6, 2, 30, 0), Sydney);
            Assert.True(gap.WasInvalid);
            Assert.Equal(Utc(2024, 10, 5, 16, 30), gap.UtcMs);

            // DST ends 2024-04-07 03:00 AEDT → 02:00 AEST (overlap).
            var local = new DateTime(2024, 4, 7, 2, 30, 0);
            Assert.Equal(Utc(2024, 4, 6, 15, 30), Zones.ResolveLocal(local, Sydney, AmbiguityPolicy.Earlier).UtcMs);
            Assert.Equal(Utc(2024, 4, 6, 16, 30), Zones.ResolveLocal(local, Sydney, AmbiguityPolicy.Later).UtcMs);
        }

        [Fact]
        public void LocalDays_Have23And25HoursOnTransitionDays()
        {
            var spring = Zones.LocalDayRange(new DateOnly(2024, 3, 31), London);
            var autumn = Zones.LocalDayRange(new DateOnly(2024, 10, 27), London);
            var normal = Zones.LocalDayRange(new DateOnly(2024, 6, 1), London);
            Assert.Equal(TimeSpan.FromHours(23), TimeSpan.FromMilliseconds(spring.EndMs - spring.StartMs));
            Assert.Equal(TimeSpan.FromHours(25), TimeSpan.FromMilliseconds(autumn.EndMs - autumn.StartMs));
            Assert.Equal(TimeSpan.FromHours(24), TimeSpan.FromMilliseconds(normal.EndMs - normal.StartMs));
        }

        [Fact]
        public void LocalHourAndDate_AreZoneAware()
        {
            long t = Utc(2024, 7, 14, 23, 41);
            Assert.Equal(0, Zones.LocalHour(t, London));                 // 00:41 BST next day
            Assert.Equal(new DateOnly(2024, 7, 15), Zones.LocalDate(t, London));
            Assert.Equal(new DateOnly(2024, 7, 14), Zones.LocalDate(t, NewYork));
        }

        // ── Stamps ───────────────────────────────────────────────────────────────

        [Fact]
        public void Stamp_ShowsAuthorLocalTimeWeekdayOffsetAndUtc()
        {
            Assert.Equal("[m:1 · 2024-01-14 Sun 23:41 author-local (UTC+0) · 2024-01-14 23:41Z]",
                Stamps.Message("m:1", Utc(2024, 1, 14, 23, 41), London));
            Assert.Equal("[m:2 · 2024-07-15 Mon 00:41 author-local (UTC+1) · 2024-07-14 23:41Z]",
                Stamps.Message("m:2", Utc(2024, 7, 14, 23, 41), London));
            Assert.Equal("[m:3 · 2024-07-14 Sun 23:41Z (author zone unknown)]",
                Stamps.Message("m:3", Utc(2024, 7, 14, 23, 41), null));
            Assert.Equal("[m:4 · 2024-07-14 Sun 23:41Z (author zone unknown) · zone inferred]",
                Stamps.Message("m:4", Utc(2024, 7, 14, 23, 41), null, TimeQuality.InferredZone));
            Assert.Equal("UTC+5:30", Stamps.FormatOffset(new TimeSpan(5, 30, 0)));
            Assert.Equal("UTC-5", Stamps.FormatOffset(TimeSpan.FromHours(-5)));
        }

        // ── Ages & school years ──────────────────────────────────────────────────

        [Fact]
        public void AgeStatement_BecomesBirthIntervalAndAgesOverTime()
        {
            long said = Utc(2024, 1, 15, 12);
            var birth = LifeMath.BirthFromAge(17, said);
            Assert.Equal(Utc(2007, 1, 15, 12), birth.LatestMs);
            Assert.Equal(Utc(2006, 1, 15, 12) + 1, birth.EarliestMs);

            Assert.Equal((17, 17), LifeMath.AgeRangeAt(birth, said));
            Assert.Equal((19, 20), LifeMath.AgeRangeAt(birth, Utc(2026, 6, 1)));
            Assert.False(LifeMath.CertainlyUnder(birth, 18, Utc(2026, 6, 1)));
            Assert.True(LifeMath.CertainlyUnder(birth, 18, said));
        }

        [Fact]
        public void InconsistentAgeClaims_IntersectToNothing()
        {
            var a = LifeMath.BirthFromAge(17, Utc(2023, 1, 1));
            var b = LifeMath.BirthFromAge(15, Utc(2023, 6, 1));
            Assert.Null(a.Intersect(b));
        }

        [Fact]
        public void JustTurnedAndTurningNarrowTheInterval()
        {
            long said = Utc(2024, 5, 10);
            var just = LifeMath.BirthFromJustTurned(18, said);
            Assert.Equal(Utc(2006, 5, 10), just.LatestMs);
            Assert.True(just.WidthMs <= (long)TimeSpan.FromDays(36).TotalMilliseconds);

            var turning = LifeMath.BirthFromTurning(18, said, TimeSpan.FromDays(20), TimeSpan.FromDays(50));
            Assert.Equal(Utc(2006, 5, 30), turning.EarliestMs);
            Assert.Equal(Utc(2006, 6, 29), turning.LatestMs);
        }

        [Fact]
        public void LeapDayBirthday_OnlyMatchesLeapYears()
        {
            var within = new TimeInterval(Utc(1999, 6, 1), Utc(2005, 6, 1));
            var candidates = LifeMath.BirthdayCandidates(2, 29, within);
            Assert.Equal(2, candidates.Count);
            Assert.Equal(Utc(2000, 2, 29), candidates[0].EarliestMs);
            Assert.Equal(Utc(2004, 2, 29), candidates[1].EarliestMs);
        }

        [Fact]
        public void EnglandYear11_MapsToTheRightBirthYear()
        {
            // Year 11 in the 2023/24 academic year: born 1 Sep 2007 – 31 Aug 2008.
            var autumn = LifeMath.BirthFromSchoolLevel(11, EducationSystem.EnglandWales, Utc(2023, 10, 1));
            var spring = LifeMath.BirthFromSchoolLevel(11, EducationSystem.EnglandWales, Utc(2024, 3, 1));
            Assert.Equal(Utc(2007, 9, 1), autumn.EarliestMs);
            Assert.Equal(Utc(2008, 9, 1) - 1, autumn.LatestMs);
            Assert.Equal(autumn, spring);
            Assert.True(LifeMath.CertainlyUnder(autumn, 18, Utc(2024, 6, 1)));
        }

        [Fact]
        public void UsGradeAndUnknownSystems_AreWidened()
        {
            var us = LifeMath.BirthFromSchoolLevel(10, EducationSystem.UnitedStates, Utc(2024, 10, 1));
            Assert.True(us.Contains(Utc(2009, 1, 1)));
            Assert.True(us.WidthMs > (long)TimeSpan.FromDays(365).TotalMilliseconds);
            var unknown = LifeMath.BirthFromSchoolLevel(10, EducationSystem.Unknown, Utc(2024, 10, 1));
            Assert.True(unknown.WidthMs >= (long)TimeSpan.FromDays(3 * 365).TotalMilliseconds);
        }

        // ── Windows & decay ──────────────────────────────────────────────────────

        [Fact]
        public void Decay_IsRelativeToAsOfAndIgnoresFutureEvidence()
        {
            var p = new DecayParameters(HalfLifeDays: 100, Floor: 0.05);
            long asOf = Utc(2024, 1, 1);
            Assert.Equal(0, p.Weight(asOf + 1, asOf));
            Assert.Equal(1.0, p.Weight(asOf, asOf), 6);
            Assert.Equal(0.5, p.Weight(asOf - (long)TimeSpan.FromDays(100).TotalMilliseconds, asOf), 6);
            Assert.Equal(0.05, p.Weight(asOf - (long)TimeSpan.FromDays(10_000).TotalMilliseconds, asOf), 6);
        }

        [Fact]
        public void TrailingWindows_AreAnchoredOnAsOf()
        {
            long asOf = Utc(2024, 3, 31);
            var w = TimeWindows.Trailing(asOf, 30);
            Assert.Equal(Utc(2024, 3, 1), w.StartMs);
            var prev = TimeWindows.PreviousOf(w);
            Assert.Equal(w.StartMs, prev.EndMs);
            Assert.Equal(w.EndMs - w.StartMs, prev.EndMs - prev.StartMs);
        }

        // ── Schedules ────────────────────────────────────────────────────────────

        [Fact]
        public void DailySchedule_FiresTheSameNightWhenStartedAfterMidnight()
        {
            // Legacy bug F046: starting at 01:00 skipped that night's 03:30 run.
            long next = Schedules.NextDailyOccurrence(Utc(2024, 11, 6, 1, 0), new TimeOnly(3, 30), London);
            Assert.Equal(Utc(2024, 11, 6, 3, 30), next);
        }

        [Fact]
        public void DailySchedule_HandlesSpringGapAndFiresOnceOnFallBack()
        {
            var at = new TimeOnly(1, 30);
            // Spring: 01:30 does not exist on 2024-03-31 → shifted to 02:30 BST (01:30Z), still that day.
            Assert.Equal(Utc(2024, 3, 31, 1, 30), Schedules.NextDailyOccurrence(Utc(2024, 3, 30, 2, 0), at, London));

            // Autumn: 01:30 occurs twice on 2024-10-27 → only the first (00:30Z) fires; next is the following day.
            long first = Schedules.NextDailyOccurrence(Utc(2024, 10, 26, 23, 0), at, London);
            Assert.Equal(Utc(2024, 10, 27, 0, 30), first);
            long second = Schedules.NextDailyOccurrence(first, at, London);
            Assert.Equal(Utc(2024, 10, 28, 1, 30), second);
        }

        [Fact]
        public void MissedRun_FiresOnceLateNeverCatchesUp()
        {
            long scheduled = Utc(2024, 11, 1, 3, 30);
            long now = Utc(2024, 11, 4, 10, 0); // three runs missed
            var (fire, late, next) = Schedules.EvaluateMissed(scheduled, now, new TimeOnly(3, 30), London);
            Assert.True(fire);
            Assert.Equal(TimeSpan.FromHours(3 * 24 + 6.5), late);
            Assert.Equal(Utc(2024, 11, 5, 3, 30), next);
            Assert.False(Schedules.EvaluateMissed(scheduled, scheduled - 1, new TimeOnly(3, 30), London).FireNow);
        }

        // ── Clock ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Europe/London")]
        [InlineData("America/New_York")]
        [InlineData("Australia/Sydney")]
        [InlineData("Australia/Lord_Howe")] // half-hour daylight saving
        [InlineData("Asia/Kathmandu")]      // +05:45, no daylight saving
        [InlineData("Etc/UTC")]
        public void OffsetSegments_AgreeWithTheZoneAtEveryInstant(string zoneId)
        {
            var zone = Zones.GetOrThrow(zoneId);
            long from = Instant.FromUtc(new DateTime(2012, 3, 1, 0, 0, 0, DateTimeKind.Utc)), to = Instant.FromUtc(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
            var segments = Zones.OffsetSegments(zone, from, to);
            Assert.Equal(long.MinValue, segments[0].FromMs);
            Assert.True(segments.Zip(segments.Skip(1)).All(p => p.First.FromMs < p.Second.FromMs && p.First.OffsetMs != p.Second.OffsetMs));
            long OffsetFromSegments(long ms) => segments.Last(s => s.FromMs <= ms).OffsetMs;
            var rng = new Random(11);
            for (int i = 0; i < 5_000; i++)
            {
                long ms = from + (long)(rng.NextDouble() * (to - from));
                Assert.Equal((long)Zones.OffsetAt(ms, zone).TotalMilliseconds, OffsetFromSegments(ms));
            }
            // Exactly at, just before and just after every transition.
            foreach (var s in segments.Skip(1))
                foreach (long ms in new[] { s.FromMs - 1000, s.FromMs - 1, s.FromMs, s.FromMs + 1, s.FromMs + 1000 })
                    Assert.Equal((long)Zones.OffsetAt(ms, zone).TotalMilliseconds, OffsetFromSegments(ms));
            if (zoneId == "Europe/London") Assert.Equal(31, segments.Count); // 2012 … 2026: the clocks change twice in each of 15 years
            if (zoneId is "Asia/Kathmandu" or "Etc/UTC") Assert.Single(segments);
        }

        [Fact]
        public void FixedClock_OnlyMovesWhenTold()
        {
            var c = new FixedClock(1000);
            Assert.Equal(1000, c.NowMs);
            c.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(3000, c.NowMs);
            c.Set(5);
            Assert.Equal(5, c.NowMs);
        }
    }
}
