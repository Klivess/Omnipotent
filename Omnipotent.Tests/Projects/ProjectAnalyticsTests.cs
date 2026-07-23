using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildProject_UsesLedgerForLifetimeAndAttributedRecordsForTheRange()
    {
        var project = NewProject("p1", "Research", ProjectStatus.Active, budget: 10);
        project.MoneyBudgetUsd = 100;
        var ledger = new ProjectBudgetLedger.Ledger
        {
            ProjectID = project.ProjectID,
            TokenSpendUsd = 2,
            PromptTokens = 1_000,
            CompletionTokens = 500,
            MoneySpendUsd = 20,
            UpdatedAt = Now,
        };
        var events = new List<ProjectEvent>
        {
            Outcome(project.ProjectID, "w1", ProjectEventTypes.WakeCompleted, Now.AddDays(-1),
                "finished (this wake: ~$0.2, 800 tokens)"),
            Diagnostic(project.ProjectID, "w1", "commander", Now.AddDays(-1), new
            {
                schemaVersion = 2,
                outcome = ProjectEventTypes.WakeCompleted,
                elapsedMs = 1200,
                dispatchedToolCalls = 2,
                productiveActions = 1,
                finalModel = "provider/model-a",
                promptTokens = 600,
                completionTokens = 200,
                totalTokens = 800,
                costUsd = 0.2,
            }),
            Outcome(project.ProjectID, "w2", ProjectEventTypes.WakeFailed, Now.AddDays(-2),
                "failed (this wake: ~$0.1, 300 tokens)"),
            Diagnostic(project.ProjectID, "w2", "agent-1", Now.AddDays(-2), new
            {
                schemaVersion = 1,
                outcome = ProjectEventTypes.WakeFailed,
                elapsedMs = 800,
                dispatchedToolCalls = 1,
                productiveActions = 0,
                finalModel = "provider/model-b",
            }),
            new()
            {
                ProjectID = project.ProjectID,
                Timestamp = Now.AddDays(-1),
                Type = ProjectEventTypes.ToolCall,
                AgentID = "commander",
            },
            new()
            {
                ProjectID = project.ProjectID,
                Timestamp = Now.AddDays(-1),
                Type = ProjectEventTypes.ArtifactAdded,
                AgentID = "commander",
            },
            new()
            {
                ProjectID = project.ProjectID,
                Timestamp = Now.AddHours(-6),
                Type = ProjectEventTypes.MoneySpent,
                AgentID = "commander",
                Text = "Real-money spend $12.50: research subscription",
            },
        };
        var council = new CouncilSession
        {
            ProjectID = project.ProjectID,
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
        var range = ProjectAnalyticsCalculator.ResolveRange("7d", project.CreatedAt, Now);

        var result = ProjectAnalyticsCalculator.BuildProject(
            project,
            ledger,
            events,
            new[]
            {
                new ProjectAgentRecord { ProjectID = project.ProjectID, AgentID = "commander", Role = "commander" },
                new ProjectAgentRecord { ProjectID = project.ProjectID, AgentID = "agent-1", Role = "researcher" },
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
    public void BuildPortfolio_SumsProjectsAndKeepsBudgetAndRangeMetricsDistinct()
    {
        var range = ProjectAnalyticsCalculator.ResolveRange("30d", Now.AddDays(-90), Now);
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

        var result = ProjectAnalyticsCalculator.BuildPortfolio(new[] { first, second }, range, Now);

        Assert.Equal(2, result.Summary.ProjectCount);
        Assert.Equal(1, result.Summary.ActiveProjects);
        Assert.Equal(5, result.Summary.LifetimeSpendUsd);
        Assert.Equal(15, result.Summary.TotalBudgetUsd);
        Assert.Equal(1.5, result.Summary.RangeSpendUsd);
        Assert.Equal(1_800, result.Summary.LifetimeTokens);
        Assert.Equal(600, result.Summary.RangeTokens);
        Assert.Equal(6, result.Summary.Wakes);
        Assert.Equal(66.7, result.Summary.SuccessRate);
        Assert.Equal(1.5, result.Series.Sum(p => p.SpendUsd), precision: 6);
        Assert.Equal("Alpha", result.Projects[0].Name);
        Assert.Contains(result.Statuses, s => s.Key == "Archived" && s.Count == 1);
    }

    [Fact]
    public void BuildProject_PrefersStructuredUsageJournalWithoutDoubleCountingFallbackRecords()
    {
        var project = NewProject("journal", "Journalled", ProjectStatus.Active, budget: 10);
        DateTime wakeAt = Now.AddHours(-4);
        DateTime councilAt = Now.AddHours(-3);
        var events = new[]
        {
            Outcome(project.ProjectID, "w1", ProjectEventTypes.WakeCompleted, wakeAt,
                "finished (this wake: ~$0.2, 800 tokens)"),
            Diagnostic(project.ProjectID, "w1", "commander", wakeAt, new
            {
                schemaVersion = 2,
                outcome = ProjectEventTypes.WakeCompleted,
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
            ProjectID = project.ProjectID,
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
            new ProjectTokenUsageRecord
            {
                Sequence = 1,
                UsageID = "direct-1",
                ProjectID = project.ProjectID,
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
            new ProjectTokenUsageRecord
            {
                Sequence = 2,
                UsageID = "reconcile-1",
                ProjectID = project.ProjectID,
                OccurredAt = wakeAt,
                WakeID = "w1",
                AgentID = "commander",
                Source = "commander",
                Model = "provider/model-a",
                CostUsd = -0.05,
                CostBasis = "reconciliation",
                ReconcilesUsageID = "direct-1",
            },
            new ProjectTokenUsageRecord
            {
                Sequence = 3,
                UsageID = "utility-1",
                ProjectID = project.ProjectID,
                OccurredAt = Now.AddHours(-2),
                AgentID = "system",
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 50,
                CompletionTokens = 10,
                CostUsd = 0.01,
                CostBasis = "actual",
            },
            new ProjectTokenUsageRecord
            {
                Sequence = 4,
                UsageID = "council-usage-1",
                ProjectID = project.ProjectID,
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
        var range = ProjectAnalyticsCalculator.ResolveRange("7d", project.CreatedAt, Now);

        var result = ProjectAnalyticsCalculator.BuildProject(
            project,
            new ProjectBudgetLedger.Ledger { ProjectID = project.ProjectID },
            events,
            new[] { new ProjectAgentRecord { ProjectID = project.ProjectID, AgentID = "commander", Role = "commander" } },
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
    public void BuildProject_DoesNotUseLegacySpendAfterTheStructuredJournalCutover()
    {
        var project = NewProject("cutover", "Cutover", ProjectStatus.Active, budget: 10);
        DateTime cutover = Now.AddHours(-5);
        DateTime wakeAt = Now.AddHours(-4);
        DateTime councilAt = Now.AddHours(-3);
        var events = new[]
        {
            Outcome(project.ProjectID, "w-after", ProjectEventTypes.WakeCompleted, wakeAt,
                "finished (this wake: ~$0.2, 800 tokens)"),
        };
        var council = new CouncilSession
        {
            CouncilID = "c-after",
            ProjectID = project.ProjectID,
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
            new ProjectTokenUsageRecord
            {
                UsageID = "utility-cutover",
                ProjectID = project.ProjectID,
                OccurredAt = cutover,
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 50,
                CompletionTokens = 10,
                CostUsd = 0.01,
                CostBasis = "actual",
            },
        };
        var range = ProjectAnalyticsCalculator.ResolveRange("7d", project.CreatedAt, Now);

        var result = ProjectAnalyticsCalculator.BuildProject(
            project,
            new ProjectBudgetLedger.Ledger { ProjectID = project.ProjectID },
            events,
            Array.Empty<ProjectAgentRecord>(),
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
    public void BuildProject_ConsumesCouncilJournalEntriesOnceWhenDetectingGaps()
    {
        var project = NewProject("council-gap", "Council gap", ProjectStatus.Active, budget: 10);
        DateTime cutover = Now.AddHours(-5);
        DateTime statementAt = Now.AddHours(-3);
        var council = new CouncilSession
        {
            CouncilID = "c-gap",
            ProjectID = project.ProjectID,
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
            new ProjectTokenUsageRecord
            {
                UsageID = "cutover",
                ProjectID = project.ProjectID,
                OccurredAt = cutover,
                Source = "utility",
                Model = "provider/utility",
                PromptTokens = 10,
                CompletionTokens = 5,
                CostUsd = 0.01,
            },
            new ProjectTokenUsageRecord
            {
                UsageID = "strategist",
                ProjectID = project.ProjectID,
                OccurredAt = statementAt,
                Source = "council",
                SourceReference = "c-gap:1:strategist",
                Model = "provider/council",
                PromptTokens = 100,
                CompletionTokens = 50,
                CostUsd = 0.05,
            },
        };
        var range = ProjectAnalyticsCalculator.ResolveRange("7d", project.CreatedAt, Now);

        var result = ProjectAnalyticsCalculator.BuildProject(
            project,
            new ProjectBudgetLedger.Ledger { ProjectID = project.ProjectID },
            Array.Empty<ProjectEvent>(),
            Array.Empty<ProjectAgentRecord>(),
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
    public void ResolveRange_CapsLongViewsWithWeeklyOrMonthlyBuckets()
    {
        var oneYear = ProjectAnalyticsCalculator.ResolveRange("365d", Now.AddYears(-3), Now);
        Assert.Equal("week", oneYear.Bucket);
        Assert.InRange(EmptySeries(oneYear).Count, 52, 54);

        var all = ProjectAnalyticsCalculator.ResolveRange("all", Now.AddYears(-3), Now);
        Assert.Equal("month", all.Bucket);
        Assert.InRange(EmptySeries(all).Count, 36, 38);
    }

    private static Project NewProject(string id, string name, ProjectStatus status, double budget) => new()
    {
        ProjectID = id,
        Name = name,
        Status = status,
        TokenBudgetUsd = budget,
        CreatedAt = Now.AddDays(-60),
    };

    private static ProjectEvent Outcome(string projectID, string wakeID, string type, DateTime timestamp, string text)
        => new()
        {
            ProjectID = projectID,
            WakeID = wakeID,
            AgentID = wakeID == "w1" ? "commander" : "agent-1",
            Type = type,
            Timestamp = timestamp,
            Text = text,
        };

    private static ProjectEvent Diagnostic(
        string projectID, string wakeID, string agentID, DateTime timestamp, object payload)
        => new()
        {
            ProjectID = projectID,
            WakeID = wakeID,
            AgentID = agentID,
            Type = ProjectEventTypes.WakeDiagnostic,
            Timestamp = timestamp,
            PayloadJson = JsonConvert.SerializeObject(payload),
        };

    private static ProjectAnalyticsSnapshot Snapshot(
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
            Project = new AnalyticsProjectIdentity
            {
                ProjectID = id,
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
                new() { Key = ProjectEventTypes.WakeCompleted, Label = "Completed", Count = successful },
                new() { Key = ProjectEventTypes.WakeFailed, Label = "Failed", Count = wakes - successful },
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
        var project = NewProject("empty", "Empty", ProjectStatus.Active, 1);
        var result = ProjectAnalyticsCalculator.BuildProject(
            project,
            new ProjectBudgetLedger.Ledger { ProjectID = project.ProjectID },
            Array.Empty<ProjectEvent>(),
            Array.Empty<ProjectAgentRecord>(),
            Array.Empty<CouncilSession>(),
            range,
            Now);
        return result.Series;
    }
}
