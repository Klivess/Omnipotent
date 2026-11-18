using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public class StewardAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SnapshotCacheKey_QuantizesMovingEndButSeparatesDifferentStarts()
    {
        var first = StewardAnalyticsCalculator.ResolveRange("30d", Now.AddYears(-1), Now);
        var sameDay = StewardAnalyticsCalculator.ResolveRange(
            "30d", Now.AddYears(-1), Now.AddMinutes(1));
        var nextDay = StewardAnalyticsCalculator.ResolveRange(
            "30d", Now.AddYears(-1), Now.AddDays(1));
        var differentAllTimeStart = StewardAnalyticsCalculator.ResolveRange(
            "all", Now.AddYears(-2), Now);
        var firstAllTimeStart = StewardAnalyticsCalculator.ResolveRange(
            "all", Now.AddYears(-1), Now);

        Assert.Equal(
            StewardAnalyticsService.SnapshotCacheKey("steward", first),
            StewardAnalyticsService.SnapshotCacheKey("steward", sameDay));
        Assert.NotEqual(
            StewardAnalyticsService.SnapshotCacheKey("steward", first),
            StewardAnalyticsService.SnapshotCacheKey("steward", nextDay));
        Assert.NotEqual(
            StewardAnalyticsService.SnapshotCacheKey("steward", firstAllTimeStart),
            StewardAnalyticsService.SnapshotCacheKey("steward", differentAllTimeStart));
    }

    [Fact]
    public void SnapshotFreshness_UsesOneAbsoluteFiveMinuteBucket()
    {
        DateTime beforeBoundary = new(2026, 7, 23, 12, 4, 59, DateTimeKind.Utc);
        Assert.Equal(
            StewardAnalyticsService.SnapshotTimeBucket(beforeBoundary.AddMinutes(-4)),
            StewardAnalyticsService.SnapshotTimeBucket(beforeBoundary));
        Assert.NotEqual(
            StewardAnalyticsService.SnapshotTimeBucket(beforeBoundary),
            StewardAnalyticsService.SnapshotTimeBucket(beforeBoundary.AddSeconds(1)));
    }

    [Fact]
    public void ForcedRefresh_MarksTheOuterResponseFillUncacheable()
    {
        var stewards = new StewardStore(_ => { });
        var events = new StewardEventLogStore(_ => { });
        var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
        var budgets = new StewardBudgetLedger(stewards, events, fetcher, _ => { });
        var service = new StewardAnalyticsService(
            stewards,
            budgets,
            events,
            new StewardSubAgentManager(stewards, events),
            new StewardCouncilStore(_ => { }));

        var scope = CacheFillProbe.Fill(() =>
            service.GetSteward("missing-steward", "30d", Now, forceRefresh: true));

        Assert.NotNull(scope.UncacheableReason);
        Assert.False(scope.WouldBeCached());
    }

    [Fact]
    public void BuildSteward_UsesLedgerForLifetimeAndAttributedRecordsForTheRange()
    {
        var steward = NewSteward("p1", "Research", StewardStatus.Active, budget: 10);
        steward.MoneyBudgetUsd = 100;
        var ledger = new StewardBudgetLedger.Ledger
        {
            StewardID = steward.StewardID,
            TokenSpendUsd = 2,
            PromptTokens = 1_000,
            CompletionTokens = 500,
            MoneySpendUsd = 20,
            UpdatedAt = Now,
        };
        var events = new List<StewardEvent>
        {
            Outcome(steward.StewardID, "w1", StewardEventTypes.WakeCompleted, Now.AddDays(-1),
                "finished (this wake: ~$0.2, 800 tokens)"),
            Diagnostic(steward.StewardID, "w1", "commander", Now.AddDays(-1), new
            {
                schemaVersion = 2,
                outcome = StewardEventTypes.WakeCompleted,
                elapsedMs = 1200,
                dispatchedToolCalls = 2,
                productiveActions = 1,
                finalModel = "provider/model-a",
                promptTokens = 600,
                completionTokens = 200,
                totalTokens = 800,
                costUsd = 0.2,
            }),
            Outcome(steward.StewardID, "w2", StewardEventTypes.WakeFailed, Now.AddDays(-2),
                "failed (this wake: ~$0.1, 300 tokens)"),
            Diagnostic(steward.StewardID, "w2", "agent-1", Now.AddDays(-2), new
            {
                schemaVersion = 1,
                outcome = StewardEventTypes.WakeFailed,
                elapsedMs = 800,
                dispatchedToolCalls = 1,
                productiveActions = 0,
                finalModel = "provider/model-b",
            }),
            new()
            {
                StewardID = steward.StewardID,
                Timestamp = Now.AddDays(-1),
                Type = StewardEventTypes.ToolCall,
                AgentID = "commander",
            },
            new()
            {
                StewardID = steward.StewardID,
                Timestamp = Now.AddDays(-1),
                Type = StewardEventTypes.ArtifactAdded,
                AgentID = "commander",
            },
            new()
            {
                StewardID = steward.StewardID,
                Timestamp = Now.AddHours(-6),
                Type = StewardEventTypes.MoneySpent,
                AgentID = "commander",
                Text = "Real-money spend $12.50: research subscription",
            },
        };
        var council = new CouncilSession
        {
            StewardID = steward.StewardID,
            WakeID = "w1",
            Model = "provider/council",
            CreatedAt = Now.AddHours(-8),
            CompletedAt = Now.AddHours(-7),
            Status = CouncilStatus.Completed,
            Statements =
            {
                new CouncilStatement
                {
                    Timestamp = Now.AddHours(-7),
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    CostUsd = 0.05,
                },
            },
        };
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            ledger,
            events,
            new[]
            {
                new StewardAgentRecord { StewardID = steward.StewardID, AgentID = "commander", Role = "commander" },
                new StewardAgentRecord { StewardID = steward.StewardID, AgentID = "agent-1", Role = "researcher" },
            },
            new[] { council },
            range,
            Now);

        Assert.Equal(2, result.Summary.LifetimeSpendUsd);
        Assert.Equal(20, result.Summary.LifetimeMoneySpendUsd);
        Assert.Equal(12.5, result.Summary.RangeMoneySpendUsd);
        Assert.Equal(1_500, result.Summary.LifetimeTokens);
        Assert.Equal(0.35, result.Summary.RangeSpendUsd, precision: 6);
        Assert.Equal(1_250, result.Summary.RangeTokens);
        Assert.Equal(700, result.Summary.RangePromptTokens);
        Assert.Equal(250, result.Summary.RangeCompletionTokens);
        Assert.Equal(300, result.Summary.RangeUnclassifiedTokens);
        Assert.Equal(2, result.Summary.Wakes);
        Assert.Equal(50, result.Summary.SuccessRate);
        Assert.Equal(1_000, result.Summary.AvgWakeDurationMs);
        Assert.Equal(1, result.Summary.Tools);
        Assert.Equal(1, result.Summary.Artifacts);
        Assert.Equal(1, result.Summary.Councils);
        Assert.Equal(0.05, result.Summary.CouncilSpendUsd, precision: 6);
        Assert.Equal(1_250, result.Series.Sum(p => p.TotalTokens));
        Assert.Equal(0.35, result.Series.Sum(p => p.SpendUsd), precision: 6);
        Assert.Equal(12.5, result.Series.Sum(p => p.MoneySpendUsd), precision: 6);
        Assert.Contains(result.Models, m => m.Key == "provider/model-a" && m.Tokens == 800);
        Assert.Contains(result.Agents, a => a.AgentID == "agent-1" && a.Label == "researcher");
        Assert.Equal(950, result.Coverage.RangeDetailedTokens);
        Assert.Equal(300, result.Coverage.RangeUnclassifiedTokens);
        Assert.Equal(168, result.Heatmap.Count);
    }

    [Fact]
    public void BuildPortfolio_SumsStewardsAndKeepsBudgetAndRangeMetricsDistinct()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("30d", Now.AddDays(-90), Now);
        var first = Snapshot("a", "Alpha", "Active", lifetimeSpend: 3, budget: 10, rangeSpend: 1,
            lifetimeTokens: 1_000, rangeTokens: 400, wakes: 4, successful: 3);
        var second = Snapshot("b", "Beta", "Archived", lifetimeSpend: 2, budget: 5, rangeSpend: 0.5,
            lifetimeTokens: 800, rangeTokens: 200, wakes: 2, successful: 1);
        first.Range = range;
        second.Range = range;
        first.Series = EmptySeries(range);
        second.Series = EmptySeries(range);
        first.Series[^1].SpendUsd = 1;
        first.Series[^1].TotalTokens = 400;
        second.Series[^1].SpendUsd = 0.5;
        second.Series[^1].TotalTokens = 200;

        var result = StewardAnalyticsCalculator.BuildPortfolio(new[] { first, second }, range, Now);

        Assert.Equal(2, result.Summary.StewardCount);
        Assert.Equal(1, result.Summary.ActiveStewards);
        Assert.Equal(5, result.Summary.LifetimeSpendUsd);
        Assert.Equal(15, result.Summary.TotalBudgetUsd);
        Assert.Equal(1.5, result.Summary.RangeSpendUsd);
        Assert.Equal(1_800, result.Summary.LifetimeTokens);
        Assert.Equal(600, result.Summary.RangeTokens);
        Assert.Equal(6, result.Summary.Wakes);
        Assert.Equal(66.7, result.Summary.SuccessRate);
        Assert.Equal(1.5, result.Series.Sum(p => p.SpendUsd), precision: 6);
        Assert.Equal("Alpha", result.Stewards[0].Name);
        Assert.Contains(result.Statuses, s => s.Key == "Archived" && s.Count == 1);
    }

    [Fact]
    public void BuildSteward_PrefersStructuredUsageJournalWithoutDoubleCountingFallbackRecords()
    {
        var steward = NewSteward("journal", "Journalled", StewardStatus.Active, budget: 10);
        DateTime wakeAt = Now.AddHours(-4);
        DateTime councilAt = Now.AddHours(-3);
        var events = new[]
        {
            Outcome(steward.StewardID, "w1", StewardEventTypes.WakeCompleted, wakeAt,
                "finished (this wake: ~$0.2, 800 tokens)"),
            Diagnostic(steward.StewardID, "w1", "commander", wakeAt, new
            {
                schemaVersion = 2,
                outcome = StewardEventTypes.WakeCompleted,
                finalModel = "provider/model-a",
                promptTokens = 600,
                completionTokens = 200,
                totalTokens = 800,
                costUsd = 0.2,
            }),
        };
        var council = new CouncilSession
        {
            CouncilID = "council-1",
            StewardID = steward.StewardID,
            WakeID = "w1",
            Model = "provider/council",
            CreatedAt = councilAt,
            CompletedAt = councilAt,
            Status = CouncilStatus.Completed,
            Statements =
            {
                new CouncilStatement
                {
                    Timestamp = councilAt,
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    CostUsd = 0.05,
                },
            },
        };
        var usage = new[]
        {
            new StewardTokenUsageRecord
            {
                Sequence = 1,
                UsageID = "direct-1",
                StewardID = steward.StewardID,
                OccurredAt = wakeAt,
                WakeID = "w1",
                AgentID = "commander",
                Source = "commander",
                Model = "provider/model-a",
                PromptTokens = 600,
                CompletionTokens = 200,
                CostUsd = 0.3,
                CostBasis = "provisional",
            },
            new StewardTokenUsageRecord
            {
                Sequence = 2,
                UsageID = "reconcile-1",
                StewardID = steward.StewardID,
                OccurredAt = wakeAt,
                WakeID = "w1",
                AgentID = "commander",
                Source = "commander",
                Model = "provider/model-a",
                CostUsd = -0.05,
                CostBasis = "reconciliation",
                ReconcilesUsageID = "direct-1",
            },
            new StewardTokenUsageRecord
            {
                Sequence = 3,
                UsageID = "utility-1",
                StewardID = steward.StewardID,
                OccurredAt = Now.AddHours(-2),
                AgentID = "system",
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 50,
                CompletionTokens = 10,
                CostUsd = 0.01,
                CostBasis = "actual",
            },
            new StewardTokenUsageRecord
            {
                Sequence = 4,
                UsageID = "council-usage-1",
                StewardID = steward.StewardID,
                OccurredAt = councilAt,
                WakeID = "w1",
                AgentID = "commander",
                Source = "council",
                SourceReference = council.CouncilID,
                Model = council.Model,
                PromptTokens = 100,
                CompletionTokens = 50,
                CostUsd = 0.06,
                CostBasis = "actual",
            },
        };
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger { StewardID = steward.StewardID },
            events,
            new[] { new StewardAgentRecord { StewardID = steward.StewardID, AgentID = "commander", Role = "commander" } },
            new[] { council },
            range,
            Now,
            usage);

        Assert.Equal(0.32, result.Summary.RangeSpendUsd, precision: 6);
        Assert.Equal(1_010, result.Summary.RangeTokens);
        Assert.Equal(750, result.Summary.RangePromptTokens);
        Assert.Equal(260, result.Summary.RangeCompletionTokens);
        Assert.Equal(0.06, result.Summary.CouncilSpendUsd, precision: 6);
        Assert.Equal(3, result.Coverage.StructuredUsageRecords);
        Assert.Equal(1, result.Coverage.ReconciliationRecords);
        Assert.Equal(1, result.Coverage.UtilityUsageRecords);
        Assert.Equal(0, result.Coverage.ProvisionalCostRecords);
        Assert.Equal(0.32, result.Series.Sum(point => point.SpendUsd), precision: 6);
        Assert.Contains(result.Models, model =>
            model.Key == "provider/model-a"
            && model.Tokens == 800
            && model.CostUsd == 0.25
            && model.Calls == 1
            && model.Wakes == 0);
    }

    [Fact]
    public void BuildSteward_DoesNotUseLegacySpendAfterTheStructuredJournalCutover()
    {
        var steward = NewSteward("cutover", "Cutover", StewardStatus.Active, budget: 10);
        DateTime cutover = Now.AddHours(-5);
        DateTime wakeAt = Now.AddHours(-4);
        DateTime councilAt = Now.AddHours(-3);
        var events = new[]
        {
            Outcome(steward.StewardID, "w-after", StewardEventTypes.WakeCompleted, wakeAt,
                "finished (this wake: ~$0.2, 800 tokens)"),
        };
        var council = new CouncilSession
        {
            CouncilID = "c-after",
            StewardID = steward.StewardID,
            CreatedAt = councilAt,
            CompletedAt = councilAt,
            Statements =
            {
                new CouncilStatement
                {
                    Timestamp = councilAt,
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    CostUsd = 0.05,
                },
            },
        };
        var usage = new[]
        {
            new StewardTokenUsageRecord
            {
                UsageID = "utility-cutover",
                StewardID = steward.StewardID,
                OccurredAt = cutover,
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 50,
                CompletionTokens = 10,
                CostUsd = 0.01,
                CostBasis = "actual",
            },
        };
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger { StewardID = steward.StewardID },
            events,
            Array.Empty<StewardAgentRecord>(),
            new[] { council },
            range,
            Now,
            usage,
            cutover);

        Assert.Equal(0.01, result.Summary.RangeSpendUsd, precision: 6);
        Assert.Equal(60, result.Summary.RangeTokens);
        Assert.Equal(0, result.Summary.CouncilSpendUsd);
        Assert.Equal(1, result.Coverage.PostCutoverUsageGaps);
        Assert.Equal(1, result.Coverage.PostCutoverCouncilUsageGaps);
    }

    [Fact]
    public void BuildSteward_ConsumesCouncilJournalEntriesOnceWhenDetectingGaps()
    {
        var steward = NewSteward("council-gap", "Council gap", StewardStatus.Active, budget: 10);
        DateTime cutover = Now.AddHours(-5);
        DateTime statementAt = Now.AddHours(-3);
        var council = new CouncilSession
        {
            CouncilID = "c-gap",
            StewardID = steward.StewardID,
            CreatedAt = statementAt,
            CompletedAt = statementAt,
            Statements =
            {
                new CouncilStatement
                {
                    Role = "Strategist",
                    Round = 1,
                    Timestamp = statementAt,
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    CostUsd = 0.05,
                },
                new CouncilStatement
                {
                    Role = "Skeptic",
                    Round = 1,
                    Timestamp = statementAt,
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    CostUsd = 0.05,
                },
            },
        };
        var usage = new[]
        {
            new StewardTokenUsageRecord
            {
                UsageID = "cutover",
                StewardID = steward.StewardID,
                OccurredAt = cutover,
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 10,
                CompletionTokens = 5,
                CostUsd = 0.01,
            },
            new StewardTokenUsageRecord
            {
                UsageID = "strategist",
                StewardID = steward.StewardID,
                OccurredAt = statementAt,
                Source = "council",
                SourceReference = "c-gap:1:strategist",
                Model = "provider/council",
                PromptTokens = 100,
                CompletionTokens = 50,
                CostUsd = 0.05,
            },
        };
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger { StewardID = steward.StewardID },
            Array.Empty<StewardEvent>(),
            Array.Empty<StewardAgentRecord>(),
            new[] { council },
            range,
            Now,
            usage,
            cutover);

        Assert.Equal(0.06, result.Summary.RangeSpendUsd, precision: 6);
        Assert.Equal(165, result.Summary.RangeTokens);
        Assert.Equal(0.05, result.Summary.CouncilSpendUsd, precision: 6);
        Assert.Equal(1, result.Coverage.PostCutoverCouncilUsageGaps);
    }

    [Fact]
    public void BuildSteward_ReconstructsPausesAcrossRangeStartAndNormalizesBurnToActiveTime()
    {
        var steward = NewSteward("pause-history", "Pause history", StewardStatus.Active, budget: 20);
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);
        DateTime firstResume = range.FromUtc.AddDays(1);
        DateTime secondPause = range.FromUtc.AddDays(3.5);
        DateTime secondResume = range.FromUtc.AddDays(5.5);
        var events = new[]
        {
            // Legacy pause predates the range. Analytics must replay it to seed range-start state.
            new StewardEvent
            {
                Sequence = 1,
                StewardID = steward.StewardID,
                Timestamp = range.FromUtc.AddDays(-2),
                Type = StewardEventTypes.Status,
                Author = "klives",
                Text = "Steward paused by Klives.",
            },
            new StewardEvent
            {
                Sequence = 2,
                StewardID = steward.StewardID,
                Timestamp = firstResume,
                Type = StewardEventTypes.Status,
                Author = "klives",
                Text = "Steward resumed by Klives.",
            },
            Lifecycle(steward.StewardID, 3, secondPause,
                StewardStatus.Active, StewardStatus.Paused, "manual-pause"),
            // A repeated pause is still a timeline event, but must not split/reset the pause.
            Lifecycle(steward.StewardID, 4, range.FromUtc.AddDays(4),
                StewardStatus.Paused, StewardStatus.Paused, "repeat-pause"),
            Lifecycle(steward.StewardID, 5, secondResume,
                StewardStatus.Paused, StewardStatus.Active, "manual-resume"),
        };
        var usage = new[]
        {
            new StewardTokenUsageRecord
            {
                Sequence = 1,
                UsageID = "pause-spend",
                StewardID = steward.StewardID,
                OccurredAt = Now.AddHours(-1),
                Source = "utility",
                Model = "provider/model",
                PromptTokens = 600,
                CompletionTokens = 100,
                CostUsd = 7,
                CostBasis = "actual",
            },
        };

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger
            {
                StewardID = steward.StewardID,
                TokenSpendUsd = 7,
                PromptTokens = 600,
                CompletionTokens = 100,
            },
            events,
            Array.Empty<StewardAgentRecord>(),
            Array.Empty<CouncilSession>(),
            range,
            Now,
            usage);

        Assert.Equal(TimeSpan.FromDays(6.5).TotalMilliseconds,
            result.Summary.RangeTrackedDurationMs, precision: 3);
        Assert.Equal(TimeSpan.FromDays(3.5).TotalMilliseconds,
            result.Summary.RangeActiveDurationMs, precision: 3);
        Assert.Equal(TimeSpan.FromDays(3).TotalMilliseconds,
            result.Summary.RangePausedDurationMs, precision: 3);
        Assert.Equal(0, result.Summary.RangeInactiveDurationMs);
        Assert.Equal(53.8, result.Summary.RangeAvailabilityPct);
        Assert.Equal(2, result.Summary.PauseCount);
        Assert.Equal(2, result.Budget.AverageDailySpendUsd, precision: 6);
        Assert.Equal(7.0 / 6.5, result.Budget.CalendarAverageDailySpendUsd, precision: 6);
        Assert.Equal(2, result.Budget.CurrentActiveDailySpendUsd, precision: 6);
        Assert.True(result.Budget.CurrentlyActive);
        Assert.False(result.Budget.CurrentlyPaused);
        Assert.Equal(6.5, result.Budget.EstimatedDaysRemaining);
        Assert.Equal(Now.AddDays(6.5), result.Budget.EstimatedExhaustionAt);
        Assert.Equal(result.Summary.RangeTrackedDurationMs,
            result.Series.Sum(point => point.ActiveDurationMs
                + point.PausedDurationMs
                + point.InactiveDurationMs),
            precision: 3);
        Assert.Equal(4, result.Summary.Events); // pre-range status is lifecycle-only
    }

    [Fact]
    public void BuildSteward_CurrentPauseFreezesCalendarEtaAndDoesNotResetOnDuplicateOrUntrustedStatus()
    {
        var steward = NewSteward("paused", "Paused", StewardStatus.Paused, budget: 10);
        var range = StewardAnalyticsCalculator.ResolveRange("7d", steward.CreatedAt, Now);
        DateTime pauseStartedAt = Now.AddDays(-2);
        var events = new[]
        {
            new StewardEvent
            {
                Sequence = 1,
                StewardID = steward.StewardID,
                Timestamp = range.FromUtc.AddHours(4),
                Type = StewardEventTypes.ToolCall,
                Author = "agent",
                Text = "ordinary tool payload with a status field",
                PayloadJson = JsonConvert.SerializeObject(new { status = "Paused" }),
            },
            new StewardEvent
            {
                Sequence = 2,
                StewardID = steward.StewardID,
                Timestamp = range.FromUtc.AddHours(8),
                Type = StewardEventTypes.Status,
                Author = "agent",
                Text = "Steward paused by Klives.",
            },
            Lifecycle(steward.StewardID, 3, pauseStartedAt,
                StewardStatus.Active, StewardStatus.Paused, "manual-pause"),
            Lifecycle(steward.StewardID, 4, Now.AddDays(-1),
                StewardStatus.Paused, StewardStatus.Paused, "repeat-pause"),
        };
        var usage = new[]
        {
            new StewardTokenUsageRecord
            {
                UsageID = "before-pause",
                StewardID = steward.StewardID,
                OccurredAt = Now.AddDays(-3),
                Source = "utility",
                Model = "provider/model",
                PromptTokens = 400,
                CostUsd = 4,
            },
            new StewardTokenUsageRecord
            {
                UsageID = "during-pause",
                StewardID = steward.StewardID,
                OccurredAt = Now.AddDays(-1),
                Source = "utility",
                Model = "provider/model",
                PromptTokens = 100,
                CostUsd = 1,
            },
        };
        var roster = new[]
        {
            new StewardAgentRecord
            {
                StewardID = steward.StewardID,
                AgentID = "commander",
                Role = "commander",
            },
            new StewardAgentRecord
            {
                StewardID = steward.StewardID,
                AgentID = "worker",
                Role = "worker",
            },
        };

        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger
            {
                StewardID = steward.StewardID,
                TokenSpendUsd = 4,
                PromptTokens = 400,
            },
            events,
            roster,
            Array.Empty<CouncilSession>(),
            range,
            Now,
            usage);

        Assert.Equal(TimeSpan.FromDays(4.5).TotalMilliseconds,
            result.Summary.RangeActiveDurationMs, precision: 3);
        Assert.Equal(TimeSpan.FromDays(2).TotalMilliseconds,
            result.Summary.RangePausedDurationMs, precision: 3);
        Assert.Equal(1, result.Summary.PauseCount);
        Assert.Equal(pauseStartedAt, result.Summary.CurrentPauseStartedAt);
        Assert.Equal(2, result.Summary.RosterAgents);
        Assert.Equal(0, result.Summary.ActiveAgents);
        Assert.Equal(5, result.Summary.RangeSpendUsd); // spend is factual even inside a pause
        Assert.True(result.Budget.CurrentlyPaused);
        Assert.False(result.Budget.CurrentlyActive);
        Assert.Equal(0, result.Budget.CurrentActiveDailySpendUsd);
        Assert.NotNull(result.Budget.EstimatedDaysRemaining); // active-runtime runway remains useful
        Assert.Null(result.Budget.EstimatedExhaustionAt); // wall-clock countdown is frozen
    }

    [Fact]
    public void BuildPortfolio_AggregatesStewardHoursAndUsesOnlyCurrentActiveFleetBurn()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("7d", Now.AddDays(-10), Now);
        var active = NewSteward("active", "Active", StewardStatus.Active, budget: 10);
        active.CreatedAt = Now.AddDays(-2);
        var paused = NewSteward("paused", "Paused", StewardStatus.Paused, budget: 10);
        paused.CreatedAt = Now.AddDays(-4);

        var activeSnapshot = StewardAnalyticsCalculator.BuildSteward(
            active,
            new StewardBudgetLedger.Ledger
            {
                StewardID = active.StewardID,
                TokenSpendUsd = 2,
                PromptTokens = 200,
            },
            Array.Empty<StewardEvent>(),
            new[]
            {
                new StewardAgentRecord
                {
                    StewardID = active.StewardID,
                    AgentID = "commander",
                    Role = "commander",
                },
            },
            Array.Empty<CouncilSession>(),
            range,
            Now,
            new[]
            {
                new StewardTokenUsageRecord
                {
                    UsageID = "active-spend",
                    StewardID = active.StewardID,
                    OccurredAt = Now.AddDays(-1),
                    Source = "utility",
                    Model = "provider/model",
                    PromptTokens = 200,
                    CostUsd = 2,
                },
            });
        var pausedSnapshot = StewardAnalyticsCalculator.BuildSteward(
            paused,
            new StewardBudgetLedger.Ledger
            {
                StewardID = paused.StewardID,
                TokenSpendUsd = 4,
                PromptTokens = 400,
            },
            new[]
            {
                Lifecycle(paused.StewardID, 1, Now.AddDays(-2),
                    StewardStatus.Active, StewardStatus.Paused, "manual-pause"),
            },
            new[]
            {
                new StewardAgentRecord
                {
                    StewardID = paused.StewardID,
                    AgentID = "commander",
                    Role = "commander",
                },
            },
            Array.Empty<CouncilSession>(),
            range,
            Now,
            new[]
            {
                new StewardTokenUsageRecord
                {
                    UsageID = "paused-spend",
                    StewardID = paused.StewardID,
                    OccurredAt = Now.AddDays(-3),
                    Source = "utility",
                    Model = "provider/model",
                    PromptTokens = 400,
                    CostUsd = 4,
                },
            });

        var result = StewardAnalyticsCalculator.BuildPortfolio(
            new[] { activeSnapshot, pausedSnapshot },
            range,
            Now);

        Assert.Equal(1, result.Summary.ActiveStewards);
        Assert.Equal(1, result.Summary.PausedStewards);
        Assert.Equal(1, result.Summary.ActiveAgents);
        Assert.Equal(2, result.Summary.RosterAgents);
        Assert.Equal(TimeSpan.FromDays(6).TotalMilliseconds,
            result.Summary.RangeTrackedDurationMs, precision: 3);
        Assert.Equal(TimeSpan.FromDays(4).TotalMilliseconds,
            result.Summary.RangeActiveDurationMs, precision: 3);
        Assert.Equal(TimeSpan.FromDays(2).TotalMilliseconds,
            result.Summary.RangePausedDurationMs, precision: 3);
        Assert.Equal(66.7, result.Summary.RangeAvailabilityPct);
        Assert.Equal(3, result.Budget.AverageDailySpendUsd, precision: 6);
        Assert.Equal(1, result.Budget.CurrentActiveDailySpendUsd, precision: 6);
        Assert.Equal(8, result.Budget.EstimatedDaysRemaining);
        Assert.Equal(Now.AddDays(8), result.Budget.EstimatedExhaustionAt);
        Assert.True(result.Budget.CurrentlyActive);
        Assert.False(result.Budget.CurrentlyPaused);
        Assert.Contains(result.Stewards, row =>
            row.StewardID == paused.StewardID
            && row.ActiveAgents == 0
            && row.RosterAgents == 1
            && row.PauseCount == 1
            && row.EstimatedExhaustionAt == null);
    }

    [Fact]
    public void BuildPortfolio_ActiveStewardsMeansOnlyActiveOrPlanning()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("7d", Now.AddDays(-10), Now);
        string[] statuses =
        {
            StewardStatus.Active.ToString(),
            StewardStatus.Planning.ToString(),
            StewardStatus.Paused.ToString(),
            StewardStatus.BudgetPaused.ToString(),
            StewardStatus.Blocked.ToString(),
            StewardStatus.Archived.ToString(),
            StewardStatus.Completed.ToString(),
        };
        var snapshots = statuses
            .Select((status, index) => Snapshot(
                $"status-{index}",
                status,
                status,
                lifetimeSpend: 0,
                budget: 0,
                rangeSpend: 0,
                lifetimeTokens: 0,
                rangeTokens: 0,
                wakes: 0,
                successful: 0))
            .ToList();

        var result = StewardAnalyticsCalculator.BuildPortfolio(snapshots, range, Now);

        Assert.Equal(2, result.Summary.ActiveStewards);
        Assert.Equal(2, result.Summary.PausedStewards);
    }

    [Fact]
    public void ResolveRange_CapsLongViewsWithWeeklyOrMonthlyBuckets()
    {
        var oneYear = StewardAnalyticsCalculator.ResolveRange("365d", Now.AddYears(-3), Now);
        Assert.Equal("week", oneYear.Bucket);
        Assert.InRange(EmptySeries(oneYear).Count, 52, 54);

        var all = StewardAnalyticsCalculator.ResolveRange("all", Now.AddYears(-3), Now);
        Assert.Equal("month", all.Bucket);
        Assert.InRange(EmptySeries(all).Count, 36, 38);
    }

    private static Steward NewSteward(string id, string name, StewardStatus status, double budget) => new()
    {
        StewardID = id,
        Name = name,
        Status = status,
        TokenBudgetUsd = budget,
        CreatedAt = Now.AddDays(-60),
    };

    private static StewardEvent Outcome(string stewardID, string wakeID, string type, DateTime timestamp, string text)
        => new()
        {
            StewardID = stewardID,
            WakeID = wakeID,
            AgentID = wakeID == "w1" ? "commander" : "agent-1",
            Type = type,
            Timestamp = timestamp,
            Text = text,
        };

    private static StewardEvent Diagnostic(
        string stewardID, string wakeID, string agentID, DateTime timestamp, object payload)
        => new()
        {
            StewardID = stewardID,
            WakeID = wakeID,
            AgentID = agentID,
            Type = StewardEventTypes.WakeDiagnostic,
            Timestamp = timestamp,
            PayloadJson = JsonConvert.SerializeObject(payload),
        };

    private static StewardEvent Lifecycle(
        string stewardID,
        long sequence,
        DateTime timestamp,
        StewardStatus from,
        StewardStatus to,
        string reason)
        => new()
        {
            Sequence = sequence,
            StewardID = stewardID,
            Timestamp = timestamp,
            Type = StewardEventTypes.Status,
            Author = "klives",
            Text = $"Lifecycle {from} to {to}.",
            PayloadJson = StewardLifecycleEvents.Payload(from, to, reason),
        };

    private static StewardAnalyticsSnapshot Snapshot(
        string id,
        string name,
        string status,
        double lifetimeSpend,
        double budget,
        double rangeSpend,
        long lifetimeTokens,
        long rangeTokens,
        int wakes,
        int successful)
        => new()
        {
            Steward = new AnalyticsStewardIdentity
            {
                StewardID = id,
                Name = name,
                Status = status,
                CreatedAt = Now.AddDays(-60),
            },
            Summary = new AnalyticsSummary
            {
                LifetimeSpendUsd = lifetimeSpend,
                TokenBudgetUsd = budget,
                RangeSpendUsd = rangeSpend,
                LifetimeTokens = lifetimeTokens,
                RangeTokens = rangeTokens,
                Wakes = wakes,
                SuccessfulWakes = successful,
                FailedWakes = wakes - successful,
                SuccessRate = wakes > 0 ? successful * 100.0 / wakes : 0,
            },
            Outcomes = new List<AnalyticsCountItem>
            {
                new() { Key = StewardEventTypes.WakeCompleted, Label = "Completed", Count = successful },
                new() { Key = StewardEventTypes.WakeFailed, Label = "Failed", Count = wakes - successful },
            },
            Coverage = new AnalyticsCoverage
            {
                RangeTrackedSpendUsd = rangeSpend,
                RangeTrackedTokens = rangeTokens,
                RangeDetailedTokens = rangeTokens,
            },
        };

    private static List<AnalyticsSeriesPoint> EmptySeries(AnalyticsRange range)
    {
        var steward = NewSteward("empty", "Empty", StewardStatus.Active, 1);
        var result = StewardAnalyticsCalculator.BuildSteward(
            steward,
            new StewardBudgetLedger.Ledger { StewardID = steward.StewardID },
            Array.Empty<StewardEvent>(),
            Array.Empty<StewardAgentRecord>(),
            Array.Empty<CouncilSession>(),
            range,
            Now);
        return result.Series;
    }
}
