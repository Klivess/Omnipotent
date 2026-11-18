using Newtonsoft.Json;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public sealed class StewardExecutionAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RoundedNinetyNinePercentDoesNotFalselyMeetTheTarget()
    {
        var metric = new StewardExecutionAnalytics { ObservedWakes = 100_000, ActiveWakes = 98_999 };
        Assert.Equal(99, metric.ActiveRate);
        Assert.False(metric.MeetsActiveTarget);
        metric.ActiveWakes++;
        Assert.True(metric.MeetsActiveTarget);
    }

    [Theory]
    [InlineData("1h", "minute", 61)]
    [InlineData("6h", "5minute", 73)]
    [InlineData("24h", "hour", 25)]
    public void ShortRangesHaveDistinctUtcBucketsAndAccountForPartialLifecycle(string key, string bucket, int count)
    {
        var range = StewardAnalyticsCalculator.ResolveRange(key, Now.AddYears(-1), Now);
        var snapshot = Build(range, []);
        Assert.Equal(bucket, range.Bucket);
        Assert.Equal(count, snapshot.Series.Count);
        Assert.Equal(count, snapshot.Series.Select(p => p.Date).Distinct().Count());
        Assert.Equal(snapshot.Series.Select(p => p.Date), snapshot.PromptCache.Series.Select(p => p.Date));
        Assert.All(snapshot.Series, p => Assert.EndsWith("Z", p.Date));
        Assert.Equal((range.ToUtc - range.FromUtc).TotalMilliseconds, snapshot.Series.Sum(p => p.ActiveDurationMs));
    }

    [Fact]
    public void CustomRangesNormalizeOffsetsAndCacheByBothExactEndpoints()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("custom", Now.AddYears(-1), Now,
            "2026-09-06T11:00:30+01:00", "2026-09-06T11:10:00+01:00", "minute");
        Assert.Equal(Now.AddHours(-2).AddSeconds(30), range.FromUtc);
        var differentEnd = StewardAnalyticsCalculator.ResolveRange("custom", Now.AddYears(-1), Now,
            "2026-09-06T11:00:30+01:00", "2026-09-06T11:11:00+01:00", "minute");
        Assert.NotEqual(StewardAnalyticsService.SnapshotCacheKey("p", range), StewardAnalyticsService.SnapshotCacheKey("p", differentEnd));
        Assert.Equal(TimeSpan.FromMinutes(9.5).TotalMilliseconds, Build(range, []).Series.Sum(p => p.ActiveDurationMs));
    }

    [Theory]
    [InlineData(null, "2026-09-06T11:00:00Z", "minute")]
    [InlineData("2026-09-06T10:00:00", "2026-09-06T11:00:00Z", "minute")]
    [InlineData("2026-09-06T11:00:00Z", "2026-09-06T10:00:00Z", "minute")]
    [InlineData("2026-09-06T11:00:00Z", "2026-09-07T10:00:00Z", "hour")]
    [InlineData("2026-08-01T00:00:00Z", "2026-09-06T11:00:00Z", "minute")]
    [InlineData("2026-09-06T10:00:00Z", "2026-09-06T11:00:00Z", "invalid")]
    public void InvalidOrUnboundedRangesAreRejected(string? from, string to, string bucket) =>
        Assert.Throws<ArgumentException>(() => StewardAnalyticsCalculator.ResolveRange("custom", Now.AddYears(-1), Now, from, to, bucket));

    [Fact]
    public void TerminalOutcomesStayTruthfulWhileExecutionAndReasonsAggregateByFinishTime()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("1h", Now.AddYears(-1), Now);
        var events = new List<StewardEvent>();
        void Wake(string id, string outcome, int responses, int productive, bool diagnostic = true)
        {
            events.Add(new() { EventID = id, WakeID = id, StewardID = "p", Type = outcome, Timestamp = Now.AddMinutes(-2), PayloadJson = "{\"kind\":\"RateLimited\"}" });
            if (diagnostic) events.Add(new() { WakeID = id, StewardID = "p", Type = StewardEventTypes.WakeDiagnostic, Timestamp = Now.AddMinutes(-2),
                PayloadJson = JsonConvert.SerializeObject(new { outcome, modelResponses = responses, productiveActions = productive, providerRetries = 1, providerWaitMs = 5000, loopTrips = 1 }) });
        }
        Wake("completed", StewardEventTypes.WakeCompleted, 1, 2);
        Wake("partial", StewardEventTypes.WakeDeferred, 1, 1);
        Wake("no-response", StewardEventTypes.WakeDeferred, 0, 0);
        Wake("legacy", StewardEventTypes.WakeCancelled, 0, 0, false);
        var snapshot = Build(range, events);
        Assert.Equal(2, snapshot.Summary.DeferredWakes);
        Assert.Equal(1, snapshot.Summary.CancelledWakes);
        Assert.Equal(3, snapshot.Execution.ObservedWakes);
        Assert.Equal(2, snapshot.Execution.ActiveWakes);
        Assert.Equal(2, snapshot.Execution.ProductiveWakes);
        Assert.Equal(66.67, snapshot.Execution.ActiveRate);
        Assert.Equal(75, snapshot.Execution.InterruptionRate);
        Assert.Equal(75, snapshot.Execution.CoveragePct);
        Assert.Equal(3, snapshot.Execution.InterruptionReasons["RateLimited"]);
        Assert.Equal(2, snapshot.Series.Single(p => p.Date == "2026-09-06T11:58:00Z").DeferredWakes);
        var fleet = StewardAnalyticsCalculator.BuildPortfolio(new[] { snapshot, snapshot }, range, Now);
        Assert.Equal(6, fleet.Execution.ObservedWakes);
        Assert.Equal(66.67, fleet.Execution.ActiveRate);
        Assert.Equal(4, fleet.Series.Sum(p => p.Execution.ActiveWakes));
        Assert.Equal(6, fleet.Execution.ProviderRetries);
    }

    private static StewardAnalyticsSnapshot Build(AnalyticsRange range, IEnumerable<StewardEvent> events) =>
        StewardAnalyticsCalculator.BuildSteward(new Steward { StewardID = "p", Name = "P", CreatedAt = Now.AddYears(-1), Status = StewardStatus.Active },
            new StewardBudgetLedger.Ledger { StewardID = "p" }, events, [], [], range, Now);
}
