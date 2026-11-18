using Newtonsoft.Json;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public sealed class StewardOverviewTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 1, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "steward-overview-tests", Guid.NewGuid().ToString("N"));
    private static StewardObservable Metric(string id = "revenue") => new()
    {
        ObservableID = id, Name = id, Type = ObservableType.Numeric, Format = ObservableFormat.Currency,
        NumericValue = 140, ObservedAt = Now, Validity = ObservableValidity.Valid, StaleAfter = TimeSpan.FromHours(2),
        History = [new() { Timestamp = Now.AddHours(-25), NumericValue = 100 }, new() { Timestamp = Now, NumericValue = 140 }],
        EvidenceEventSequence = 42,
    };
    private static StewardResultSelection Choice(string id) => new() { ObservableID = id, Direction = "higher", Rationale = "Goal outcome" };

    [Fact]
    public void UserPinSurvivesCommanderNominationAndRestart_AndClearRestoresCommander()
    {
        var store = new StewardStore(_ => { }, Path.Combine(root, "stewards.json"));
        var steward = store.CreateSteward("Overview test", "Outcome", 10, 0, 0, 1);
        store.SetResultSelection(steward.StewardID, Choice("manual"), true);
        store.SetResultSelection(steward.StewardID, Choice("commander"), false);
        var reopened = new StewardStore(_ => { }, Path.Combine(root, "stewards.json"));
        steward = reopened.GetSteward(steward.StewardID)!;
        var selected = StewardOverviewService.BuildResult(steward, [Metric("manual"), Metric("commander")], 7, Now.AddHours(-24), Now);
        Assert.Equal("manual", selected.ObservableID);
        Assert.Equal("pinned", selected.Selection);
        reopened.SetResultSelection(steward.StewardID, null, true);
        selected = StewardOverviewService.BuildResult(steward, [Metric("commander")], 7, Now.AddHours(-24), Now);
        Assert.Equal("commander", selected.ObservableID);
        Assert.Equal(40, selected.Delta);
    }

    [Fact]
    public void DeletedSelectionStaysUnavailable_AndUnselectedStewardUsesCompletedSteps()
    {
        var steward = new Steward { PinnedResult = Choice("deleted"), CommanderResult = Choice("other") };
        var selected = StewardOverviewService.BuildResult(steward, [Metric("other")], 9, Now.AddDays(-1), Now);
        Assert.Null(selected.Value);
        Assert.Equal("deleted", selected.ObservableID);
        Assert.Equal("pinned", selected.Selection);
        var fallback = StewardOverviewService.BuildResult(new Steward(), [], 9, Now.AddDays(-1), Now);
        Assert.Equal("fallback", fallback.Selection);
        Assert.Equal(9, fallback.Value);
        Assert.Null(fallback.Delta);
    }

    [Fact]
    public void LegacyObservableWithNullCollectionsDoesNotBreakOverviewResult()
    {
        var metric = Metric();
        metric.EvidenceArtifactIDs = null!;
        metric.History = null!;

        var result = StewardOverviewService.BuildResult(
            new Steward { CommanderResult = Choice(metric.ObservableID) }, [metric], 0, Now.AddDays(-1), Now);

        Assert.Equal(140, result.Value);
        Assert.Empty(result.EvidenceArtifactIDs);
        Assert.Empty(result.History);
    }

    [Fact]
    public void EmptyStewardIndexReturnsACompleteImmediateOverview()
    {
        var store = new StewardStore(_ => { }, Path.Combine(root, "empty-stewards.json"));
        var events = new StewardEventLogStore(_ => { });
        var budgets = new StewardBudgetLedger(store, events,
            new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { });
        var analytics = new StewardAnalyticsService(store, budgets, events,
            new StewardSubAgentManager(store, events), new StewardCouncilStore(_ => { }));
        var parent = new Omnipotent.Services.Demiurge.Demiurge();
        void Set(string property, object value) => parent.GetType().GetProperty(property)!.SetValue(parent, value);
        Set("Store", store); Set("Budget", budgets); Set("Analytics", analytics);
        Set("Activity", new StewardAgentActivityTracker());
        Set("RuntimeState", new StewardRuntimeStateStore(_ => { }, Path.Combine(root, "empty-runtime")));
        Set("Gates", new StewardGateManager(events, _ => { }));
        Set("Digests", new StewardDigestStore(_ => { }));
        Set("Observables", new StewardObservableStore(_ => { }));

        var result = new StewardOverviewService(parent).Get("24h");

        Assert.Empty(result.Stewards);
        Assert.Empty(result.Series);
        Assert.False(result.Degraded);
        Assert.False(result.HistoricalLoading);
        Assert.NotEmpty(JsonConvert.SerializeObject(result));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unverified")]
    [InlineData("invalid")]
    [InlineData("missing-baseline")]
    public void UnreliableOrIncompleteMeasurementsDoNotProducePeriodDelta(string problem)
    {
        var metric = Metric();
        if (problem == "stale") metric.ObservedAt = Now.AddHours(-3);
        if (problem == "unverified") metric.Validity = ObservableValidity.Unknown;
        if (problem == "invalid") metric.Validity = ObservableValidity.Invalid;
        if (problem == "missing-baseline") metric.History.RemoveAt(0);
        var result = StewardOverviewService.BuildResult(new Steward { CommanderResult = Choice(metric.ObservableID) }, [metric], 0, Now.AddDays(-1), Now);
        Assert.Null(result.Delta);
        Assert.Equal(140, result.Value);
        Assert.Equal(problem == "stale", result.Stale);
    }

    [Fact]
    public void SelectionIsStewardLocalNumericAndDirectionValidated()
    {
        Assert.Throws<ArgumentException>(() => StewardOverviewService.ValidateSelection([Metric()], "foreign-id", "higher", "Result"));
        Assert.Throws<ArgumentException>(() => StewardOverviewService.ValidateSelection([Metric()], "revenue", "invented", "Result"));
        var text = Metric(); text.Type = ObservableType.Text;
        Assert.Throws<ArgumentException>(() => StewardOverviewService.ValidateSelection([text], "revenue", "higher", "Result"));
        Assert.True(StewardTierRouter.IsCommanderOnly("select_primary_result"));
        var tool = StewardToolFacade.Unfold("observable", "{\"op\":\"select_result\",\"name\":\"revenue\",\"direction\":\"higher\",\"rationale\":\"Goal outcome\"}");
        Assert.True(tool.IsValid);
        Assert.Equal("select_primary_result", tool.ToolName);
    }

    [Fact]
    public void CompletionCountsComeFromDurableEventsBeyondRetainedSteps_ExcludeOtherOutcomesAndDeduplicate()
    {
        var steward = new Steward { StewardID = "test", CreatedAt = Now.AddDays(-3) };
        var events = Enumerable.Range(0, 90).Select(i => Closed("s" + i, Now.AddHours(-1))).ToList();
        events.Add(Closed("s0", Now.AddMinutes(-10)));
        events.Add(Closed("outside", Now.AddDays(-2)));
        events.Add(Closed("abandoned", Now.AddHours(-1), "Abandoned"));
        events.Add(Closed("blocked", Now.AddHours(-1), "Blocked"));
        events.Add(new StewardEvent { Type = StewardEventTypes.CheckpointChanged, Author = "klives", Timestamp = Now.AddMinutes(-5), Text = "Klives closed step legacy as Done: checked" });
        var range = StewardAnalyticsCalculator.ResolveRange("24h", steward.CreatedAt, Now);
        var snapshot = StewardAnalyticsCalculator.BuildSteward(steward, new StewardBudgetLedger.Ledger(), events, [], [], range, Now);
        Assert.Equal(91, snapshot.Summary.CompletedSteps);
        Assert.Equal(91, snapshot.Series.Sum(p => p.CompletedSteps));
        Assert.Equal(0, snapshot.Summary.RangeSpendUsd);
        Assert.Null(snapshot.Execution.ProductiveRate);
    }

    [Fact]
    public void OverviewExcludesShelvedTotalsAndReusesHistoricalSnapshotsAcrossLiveRefreshes()
    {
        var store = new StewardStore(_ => { }, Path.Combine(root, "stewards.json"));
        var steward = store.CreateSteward("Live", "Outcome", 20, 0, 0, 2);
        var archived = store.CreateSteward("Shelved", "Outcome", 20, 0, 0, 2);
        archived.Status = StewardStatus.Archived; store.SaveSteward(archived);
        var events = new StewardEventLogStore(_ => { });
        var evt = Closed("one", DateTime.UtcNow.AddMinutes(-10)); evt.StewardID = steward.StewardID; events.Append(evt);
        evt = Closed("shelved", DateTime.UtcNow.AddMinutes(-10)); evt.StewardID = archived.StewardID; events.Append(evt);
        var budgets = new StewardBudgetLedger(store, events, new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { });
        var agents = new StewardSubAgentManager(store, events);
        var councils = new StewardCouncilStore(_ => { });
        var analytics = new StewardAnalyticsService(store, budgets, events, agents, councils);
        var activity = new StewardAgentActivityTracker();
        activity.BeginThinking(steward.StewardID, "commander", "commander", null);
        var parent = new Omnipotent.Services.Demiurge.Demiurge();
        void Set(string property, object value) => parent.GetType().GetProperty(property)!.SetValue(parent, value);
        Set("Store", store); Set("Budget", budgets); Set("Analytics", analytics); Set("Activity", activity);
        Set("RuntimeState", new StewardRuntimeStateStore(_ => { }, Path.Combine(root, "runtime")));
        Set("Gates", new StewardGateManager(events, _ => { })); Set("Digests", new StewardDigestStore(_ => { }));
        Set("Observables", new StewardObservableStore(_ => { }));
        var service = new StewardOverviewService(parent);
        var cold = service.Get("24h");
        Assert.True(cold.HistoricalLoading);
        Assert.Equal("Live", cold.Stewards.Single(p => p.Status != "Archived").Name);
        Assert.False(cold.Stewards.Single(p => p.Status != "Archived").HistoricalReady);

        // The request above queued the expensive scan instead of blocking. A direct analytics read
        // joins that one build, after which the next operational snapshot is fully populated.
        analytics.GetSteward(steward.StewardID, "24h");
        var first = service.Get("24h");
        Assert.False(first.HistoricalLoading);
        Assert.Equal(2, first.Stewards.Count);
        Assert.Equal(1, first.CompletedSteps);
        Assert.Equal(1, first.WorkingStewards);
        var cached = analytics.GetSteward(steward.StewardID, "24h", Now);
        Assert.Same(cached, analytics.GetSteward(steward.StewardID, "24h", Now.AddSeconds(10)));
        var second = service.Get("24h");
        Assert.Equal(first.CompletedSteps, second.CompletedSteps);
        Assert.Empty(second.Attention);
        Assert.Throws<ArgumentException>(() => service.Get("all"));
    }

    private static StewardEvent Closed(string id, DateTime at, string result = "Done") => new()
    { Type = StewardEventTypes.CheckpointChanged, Author = "commander", Timestamp = at,
        PayloadJson = JsonConvert.SerializeObject(new { op = "close_step", stepID = id, result }) };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
