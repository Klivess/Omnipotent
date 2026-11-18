using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public class StewardCostSimulatorTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSteward_SplitsPromptTokensIntoFreshInputAndCacheReads()
    {
        var steward = NewSteward("p1", "Research");
        var row = StewardCostSimulatorCalculator.BuildSteward(steward, new[]
        {
            Usage("commander", prompt: 10_000, cached: 8_000, cacheWrite: 500, completion: 400, cost: 0.02),
        }, Array.Empty<StewardAgentRecord>());

        // 10,000 prompt tokens of which 8,000 were served from cache: only 2,000 were billed at the
        // input rate, and the 500 cache writes come out of those 2,000 rather than adding to them.
        Assert.Equal(2_000, row.InputTokens);
        Assert.Equal(8_000, row.CacheReadTokens);
        Assert.Equal(500, row.CacheWriteTokens);
        Assert.Equal(400, row.OutputTokens);
        Assert.Equal(10_400, row.TotalTokens);
        Assert.Equal(1, row.Requests);
        Assert.Equal(0.02, row.ActualCostUsd, 6);
    }

    [Fact]
    public void BuildSteward_CountsUnmeasuredCacheTokensAsInputAndFlagsThem()
    {
        var steward = NewSteward("p1", "Research");
        var row = StewardCostSimulatorCalculator.BuildSteward(steward, new[]
        {
            Usage("commander", prompt: 5_000, cached: 4_000, cacheWrite: 0, completion: 100,
                cost: 0.01, cacheMetrics: false),
        }, Array.Empty<StewardAgentRecord>());

        // The provider reported nothing about caching, so the journal's cached count cannot be
        // trusted: price it all as input, and say how much of the window is in that position.
        Assert.Equal(5_000, row.InputTokens);
        Assert.Equal(0, row.CacheReadTokens);
        Assert.Equal(5_000, row.UnmeasuredCachePromptTokens);
        Assert.Equal(1, row.UnmeasuredCacheRequests);
    }

    [Fact]
    public void BuildSteward_AppliesReconciliationToCostWithoutCountingItAsATurn()
    {
        var steward = NewSteward("p1", "Research");
        var provisional = Usage("commander", prompt: 1_000, cached: 0, cacheWrite: 0,
            completion: 200, cost: 0.05);
        var adjustment = Usage("commander", prompt: 0, cached: 0, cacheWrite: 0, completion: 0,
            cost: -0.02);
        adjustment.RecordKind = "cost-adjustment";

        var row = StewardCostSimulatorCalculator.BuildSteward(
            steward, new[] { provisional, adjustment }, Array.Empty<StewardAgentRecord>());

        Assert.Equal(1, row.Requests);
        Assert.Equal(1_200, row.TotalTokens);
        Assert.Equal(0.03, row.ActualCostUsd, 6);
    }

    [Fact]
    public void BuildSteward_GroupsPerAgentAndLabelsFromTheLiveRoster()
    {
        var steward = NewSteward("p1", "Research");
        var roster = new List<StewardAgentRecord>
        {
            new() { AgentID = "commander", StewardID = "p1", Role = "commander" },
            new() { AgentID = "agent-7", StewardID = "p1", Role = "market-researcher" },
        };
        var row = StewardCostSimulatorCalculator.BuildSteward(steward, new[]
        {
            Usage("agent-7", prompt: 40_000, cached: 30_000, cacheWrite: 0, completion: 2_000, cost: 0.4),
            Usage("commander", prompt: 8_000, cached: 2_000, cacheWrite: 0, completion: 300, cost: 0.1),
            Usage("retired-3", prompt: 1_000, cached: 0, cacheWrite: 0, completion: 50, cost: 0.01),
        }, roster);

        Assert.Equal(3, row.Agents.Count);
        // Heaviest agent first, so the dropdown opens on whatever is actually driving the bill.
        Assert.Equal("agent-7", row.Agents[0].AgentID);
        Assert.Equal("market-researcher", row.Agents[0].Label);
        Assert.True(row.Agents[0].OnRoster);
        Assert.True(row.Agents.Single(a => a.AgentID == "commander").IsCommander);

        // An agent retired since the window still spent the money — it keeps its own row, labelled
        // by ID, rather than disappearing into the Steward total.
        var retired = row.Agents.Single(a => a.AgentID == "retired-3");
        Assert.False(retired.OnRoster);
        Assert.Equal("retired-3", retired.Label);
        Assert.Equal(1_050, retired.TotalTokens);
        Assert.Equal(row.TotalTokens, row.Agents.Sum(a => a.TotalTokens));
        Assert.Equal(row.InputTokens, row.Agents.Sum(a => a.InputTokens));
        Assert.Equal(row.CacheReadTokens, row.Agents.Sum(a => a.CacheReadTokens));
        Assert.Equal(row.OutputTokens, row.Agents.Sum(a => a.OutputTokens));
    }

    [Fact]
    public void BuildSnapshot_TotalsSpendingStewardsAndSetsSilentOnesAside()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("30d", Now.AddDays(-60), Now);
        var busy = StewardCostSimulatorCalculator.BuildSteward(NewSteward("p1", "Research"), new[]
        {
            Usage("commander", prompt: 10_000, cached: 6_000, cacheWrite: 1_000, completion: 500,
                cost: 0.2, model: "qwen/qwen3-max"),
        }, Array.Empty<StewardAgentRecord>());
        var alsoBusy = StewardCostSimulatorCalculator.BuildSteward(NewSteward("p2", "Outreach"), new[]
        {
            Usage("agent-1", prompt: 2_000, cached: 0, cacheWrite: 0, completion: 100, cost: 0.05,
                model: "qwen/qwen3-max"),
        }, Array.Empty<StewardAgentRecord>());
        var silent = StewardCostSimulatorCalculator.BuildSteward(
            NewSteward("p3", "Idle"), Array.Empty<StewardTokenUsageRecord>(),
            Array.Empty<StewardAgentRecord>());

        var snapshot = StewardCostSimulatorCalculator.BuildSnapshot(
            new[] { silent, alsoBusy, busy }, range, Now);

        Assert.Equal(2, snapshot.Stewards.Count);
        Assert.Equal(1, snapshot.SilentStewards);
        Assert.Equal("p1", snapshot.Stewards[0].StewardID);
        Assert.Equal(6_000, snapshot.Totals.InputTokens);
        Assert.Equal(6_000, snapshot.Totals.CacheReadTokens);
        Assert.Equal(600, snapshot.Totals.OutputTokens);
        Assert.Equal(12_600, snapshot.Totals.TotalTokens);
        Assert.Equal(0.25, snapshot.Totals.ActualCostUsd, 6);
        // One model carried the whole window, so a single price triple is an honest simulation here.
        Assert.Equal(new[] { "qwen/qwen3-max" }, snapshot.Models.Select(m => m.Key).ToArray());
        Assert.Equal(12_600, snapshot.Models[0].TotalTokens);
        Assert.Equal(2, snapshot.Models[0].Requests);
    }

    [Fact]
    public void BuildSnapshot_KeepsAStewardWhoseOnlyRecordIsACostAdjustment()
    {
        var range = StewardAnalyticsCalculator.ResolveRange("30d", Now.AddDays(-60), Now);
        var adjustment = Usage("commander", prompt: 0, cached: 0, cacheWrite: 0, completion: 0,
            cost: -0.4);
        adjustment.RecordKind = "cost-adjustment";
        var row = StewardCostSimulatorCalculator.BuildSteward(
            NewSteward("p1", "Refunded"), new[] { adjustment }, Array.Empty<StewardAgentRecord>());

        var snapshot = StewardCostSimulatorCalculator.BuildSnapshot(new[] { row }, range, Now);

        // Zero tokens but real money moved: dropping the row would make the actual-cost comparison
        // silently disagree with the ledger.
        Assert.Single(snapshot.Stewards);
        Assert.Equal(0, snapshot.SilentStewards);
        Assert.Equal(-0.4, snapshot.Totals.ActualCostUsd, 6);
    }

    [Fact]
    public void DefaultSnapshots_ReadEachJournalOnceAndSeparateArchivedStewards()
    {
        var active = NewSteward("active", "Active");
        var archived = NewSteward("archived", "Archived");
        archived.Status = StewardStatus.Archived;
        archived.CreatedAt = Now.AddDays(-100);
        var recentActive = Usage("commander", 100, 0, 0, 0, 0.01);
        recentActive.OccurredAt = Now.AddMinutes(-30);
        var oldActive = Usage("commander", 50, 0, 0, 0, 0.01);
        oldActive.OccurredAt = Now.AddDays(-40);
        var recentArchived = Usage("commander", 200, 0, 0, 0, 0.02);
        recentArchived.OccurredAt = Now.AddMinutes(-30);
        int scans = 0;

        var snapshots = StewardCostSimulatorService.BuildDefaultSnapshots(
            new[] { active, archived },
            (id, _, _) =>
            {
                Interlocked.Increment(ref scans);
                return id == "active"
                    ? new[] { recentActive, oldActive }
                    : new[] { recentArchived };
            },
            _ => Array.Empty<StewardAgentRecord>(), Now);

        Assert.Equal(2, scans);
        Assert.Equal(300, snapshots[StewardCostSimulatorMaterializer.Key("1h", true)].Totals.TotalTokens);
        Assert.Equal(100, snapshots[StewardCostSimulatorMaterializer.Key("1h", false)].Totals.TotalTokens);
        Assert.Equal(300, snapshots[StewardCostSimulatorMaterializer.Key("30d", true)].Totals.TotalTokens);
        Assert.Equal(350, snapshots[StewardCostSimulatorMaterializer.Key("90d", true)].Totals.TotalTokens);
        Assert.Equal(active.CreatedAt.Date,
            snapshots[StewardCostSimulatorMaterializer.Key("all", false)].Range.FromUtc);
    }

    private static Steward NewSteward(string id, string name) => new()
    {
        StewardID = id,
        Name = name,
        Status = StewardStatus.Active,
        TokenBudgetUsd = 100,
        CreatedAt = Now.AddDays(-60),
    };

    private static StewardTokenUsageRecord Usage(
        string agentID,
        long prompt,
        long cached,
        long cacheWrite,
        long completion,
        double cost,
        bool cacheMetrics = true,
        string model = "test/model") => new()
        {
            StewardID = "p1",
            AgentID = agentID,
            OccurredAt = Now.AddDays(-1),
            RecordedAt = Now.AddDays(-1),
            Model = model,
            PromptTokens = prompt,
            CachedPromptTokens = cached,
            CacheWritePromptTokens = cacheWrite,
            CacheMetricsAvailable = cacheMetrics,
            CompletionTokens = completion,
            CostUsd = cost,
        };
}
