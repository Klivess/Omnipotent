using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// The "temporal lobe" beyond perception: prospective memory (the scheduler's time grammar,
    /// recurrence advance, and fire-message composition), time-windowed recall, and observable
    /// trend perception. Pure-logic tests — the firing loop itself is service-bound.
    /// </summary>
    public class KliveAgentTemporalLobeTests
    {
        private static readonly DateTime Now = new(2026, 7, 12, 18, 0, 0, DateTimeKind.Utc);

        // ── TemporalParse: one shared grammar for every temporal tool ──

        [Theory]
        [InlineData("in 2h30m", 2.5)]
        [InlineData("45m", 0.75)]
        [InlineData("in 3d", 72)]
        [InlineData("1d 6h", 30)]
        public void FutureInstants_ParseRelativeDelays(string text, double hoursAhead)
        {
            Assert.True(TemporalParse.TryParseFutureInstant(text, Now, out var due));
            Assert.Equal(Now.AddHours(hoursAhead), due);
        }

        [Fact]
        public void FutureInstants_ParseAbsoluteUtc()
        {
            Assert.True(TemporalParse.TryParseFutureInstant("2026-07-15 09:00", Now, out var due));
            Assert.Equal(new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc), due);
        }

        [Fact]
        public void FutureInstants_BareTimeOfDayRollsForwardToNextOccurrence()
        {
            // 09:00 is already past at Now (18:00) → tomorrow 09:00, not today's past instant.
            Assert.True(TemporalParse.TryParseFutureInstant("09:00", Now, out var due));
            Assert.Equal(new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc), due);
        }

        [Theory]
        [InlineData("7d", -7 * 24)]
        [InlineData("24h", -24)]
        [InlineData("90m ago", -1.5)]
        public void PastInstants_ParseLookbacks(string text, double hoursOffset)
        {
            Assert.True(TemporalParse.TryParsePastInstant(text, Now, out var instant));
            Assert.Equal(Now.AddHours(hoursOffset), instant);
        }

        [Theory]
        [InlineData("")]
        [InlineData("whenever")]
        [InlineData("in")]
        public void UnparseableExpressions_AreRejectedNotGuessed(string text)
        {
            Assert.False(TemporalParse.TryParseFutureInstant(text, Now, out _));
            Assert.False(TemporalParse.TryParseDuration(text, out _));
        }

        // ── Scheduler: recurrence + the fire message the future self reads ──

        [Fact]
        public void Recurrence_SkipsMissedOccurrences_FiringOnceNotPerMissedInterval()
        {
            // Due 06:00, hourly, now 18:00 — 12 occurrences were missed while offline. The next
            // due slot must be the first FUTURE one (19:00), not 12 back-to-back catch-up firings.
            var next = KliveAgentScheduler.AdvanceRecurrence(
                Now.Date.AddHours(6), TimeSpan.FromHours(1), Now);
            Assert.Equal(Now.AddHours(1), next);
        }

        [Fact]
        public void FireMessage_CarriesFullTemporalContext()
        {
            var task = new AgentScheduledTask
            {
                Instruction = "Check the deploy finished and report.",
                CreatedAt = Now.AddHours(-3),
                DueAtUtc = Now.AddMinutes(-1),
            };
            string msg = KliveAgentScheduler.ComposeFireMessage(task, Now);
            Assert.Contains("SCHEDULED TASK", msg);
            Assert.Contains("2026-07-12 15:00", msg);            // created stamp
            Assert.Contains("Check the deploy finished", msg);
            Assert.DoesNotContain("LATE", msg);                   // 1 minute is on time
        }

        [Fact]
        public void FireMessage_FlagsLateFirings_SoTheAgentJudgesRelevance()
        {
            var task = new AgentScheduledTask
            {
                Instruction = "Send the morning summary.",
                CreatedAt = Now.AddDays(-1),
                DueAtUtc = Now.AddHours(-5),
            };
            string msg = KliveAgentScheduler.ComposeFireMessage(task, Now);
            Assert.Contains("5h 0m LATE", msg);
            Assert.Contains("still relevant", msg);
        }

        // ── Time-windowed recall ──

        [Fact]
        public async Task Recall_SinceUntil_RestrictsToTheWindow()
        {
            var memory = new KliveAgentMemory(null!);
            // Seed the cache directly (reflection): three memories across three weeks.
            var cache = (List<AgentMemoryEntry>)typeof(KliveAgentMemory)
                .GetField("cachedMemories", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(memory)!;
            cache.Add(new AgentMemoryEntry { Content = "deploy pipeline fixed", CreatedAt = DateTime.UtcNow.AddDays(-20) });
            cache.Add(new AgentMemoryEntry { Content = "deploy keys rotated", CreatedAt = DateTime.UtcNow.AddDays(-10) });
            cache.Add(new AgentMemoryEntry { Content = "deploy dashboard built", CreatedAt = DateTime.UtcNow.AddDays(-1) });
            typeof(KliveAgentMemory)
                .GetField("cacheLoaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(memory, true);

            var window = await memory.RecallMemoriesAsync("deploy", 10,
                sinceUtc: DateTime.UtcNow.AddDays(-14), untilUtc: DateTime.UtcNow.AddDays(-5));
            Assert.Single(window);
            Assert.Equal("deploy keys rotated", window[0].Content);

            // Empty query + window = browse the period, newest first.
            var browse = await memory.RecallMemoriesAsync("", 10, sinceUtc: DateTime.UtcNow.AddDays(-14));
            Assert.Equal(2, browse.Count);
            Assert.Equal("deploy dashboard built", browse[0].Content);
        }

        // ── Observable trend perception ──

        [Fact]
        public void ObservableTrend_ReportsDeltaOverTheWindow()
        {
            var o = new ProjectObservable
            {
                Type = ObservableType.Numeric,
                NumericValue = 150,
                History = new List<ObservableSample>
                {
                    new() { Timestamp = Now.AddHours(-30), NumericValue = 100 },
                    new() { Timestamp = Now.AddHours(-10), NumericValue = 120 },
                    new() { Timestamp = Now, NumericValue = 150 },
                },
            };
            string trend = ProjectObservableStore.DescribeTrend(o, Now);
            Assert.Contains("+50", trend);   // vs the 24h+ baseline (100), not the 10h sample
            Assert.Contains("↑", trend);
            Assert.Contains("30h", trend);
        }

        [Fact]
        public void ObservableTrend_SilentWhenFlatOrHistoryless()
        {
            var flat = new ProjectObservable
            {
                Type = ObservableType.Numeric,
                NumericValue = 100,
                History = new List<ObservableSample>
                {
                    new() { Timestamp = Now.AddHours(-2), NumericValue = 100 },
                    new() { Timestamp = Now, NumericValue = 100 },
                },
            };
            Assert.Equal("", ProjectObservableStore.DescribeTrend(flat, Now));
            Assert.Equal("", ProjectObservableStore.DescribeTrend(
                new ProjectObservable { Type = ObservableType.Text, TextValue = "ok" }, Now));
        }
    }
}
