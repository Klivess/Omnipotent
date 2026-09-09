using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public sealed class ProjectOverviewTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 1, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "project-overview-tests", Guid.NewGuid().ToString("N"));
    private static ProjectObservable Metric(string id = "revenue") => new()
    {
        ObservableID = id, Name = id, Type = ObservableType.Numeric, Format = ObservableFormat.Currency,
        NumericValue = 140, ObservedAt = Now, Validity = ObservableValidity.Valid, StaleAfter = TimeSpan.FromHours(2),
        History = [new() { Timestamp = Now.AddHours(-25), NumericValue = 100 }, new() { Timestamp = Now, NumericValue = 140 }],
        EvidenceEventSequence = 42,
    };
    private static ProjectResultSelection Choice(string id) => new() { ObservableID = id, Direction = "higher", Rationale = "Goal outcome" };

    [Fact]
    public void UserPinSurvivesCommanderNominationAndRestart_AndClearRestoresCommander()
    {
        var store = new ProjectStore(_ => { }, Path.Combine(root, "projects.json"));
        var project = store.CreateProject("Overview test", "Outcome", 10, 0, 0, 1);
        store.SetResultSelection(project.ProjectID, Choice("manual"), true);
        store.SetResultSelection(project.ProjectID, Choice("commander"), false);
        var reopened = new ProjectStore(_ => { }, Path.Combine(root, "projects.json"));
        project = reopened.GetProject(project.ProjectID)!;
        var selected = ProjectOverviewService.BuildResult(project, [Metric("manual"), Metric("commander")], 7, Now.AddHours(-24), Now);
        Assert.Equal("manual", selected.ObservableID);
        Assert.Equal("pinned", selected.Selection);
        reopened.SetResultSelection(project.ProjectID, null, true);
        selected = ProjectOverviewService.BuildResult(project, [Metric("commander")], 7, Now.AddHours(-24), Now);
        Assert.Equal("commander", selected.ObservableID);
        Assert.Equal(40, selected.Delta);
    }

    [Fact]
    public void DeletedSelectionStaysUnavailable_AndUnselectedProjectUsesCompletedSteps()
    {
        var project = new Project { PinnedResult = Choice("deleted"), CommanderResult = Choice("other") };
        var selected = ProjectOverviewService.BuildResult(project, [Metric("other")], 9, Now.AddDays(-1), Now);
        Assert.Null(selected.Value);
        Assert.Equal("deleted", selected.ObservableID);
        Assert.Equal("pinned", selected.Selection);
        var fallback = ProjectOverviewService.BuildResult(new Project(), [], 9, Now.AddDays(-1), Now);
        Assert.Equal("fallback", fallback.Selection);
        Assert.Equal(9, fallback.Value);
        Assert.Null(fallback.Delta);
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
        var result = ProjectOverviewService.BuildResult(new Project { CommanderResult = Choice(metric.ObservableID) }, [metric], 0, Now.AddDays(-1), Now);
        Assert.Null(result.Delta);
        Assert.Equal(140, result.Value);
        Assert.Equal(problem == "stale", result.Stale);
    }

    [Fact]
    public void SelectionIsProjectLocalNumericAndDirectionValidated()
    {
        Assert.Throws<ArgumentException>(() => ProjectOverviewService.ValidateSelection([Metric()], "foreign-id", "higher", "Result"));
        Assert.Throws<ArgumentException>(() => ProjectOverviewService.ValidateSelection([Metric()], "revenue", "invented", "Result"));
        var text = Metric(); text.Type = ObservableType.Text;
        Assert.Throws<ArgumentException>(() => ProjectOverviewService.ValidateSelection([text], "revenue", "higher", "Result"));
        Assert.True(ProjectTierRouter.IsCommanderOnly("select_primary_result"));
        var tool = ProjectToolFacade.Unfold("observable", "{\"op\":\"select_result\",\"name\":\"revenue\",\"direction\":\"higher\",\"rationale\":\"Goal outcome\"}");
        Assert.True(tool.IsValid);
        Assert.Equal("select_primary_result", tool.ToolName);
    }

    [Fact]
    public void CompletionCountsComeFromDurableEventsBeyondRetainedSteps_ExcludeOtherOutcomesAndDeduplicate()
    {
        var project = new Project { ProjectID = "test", CreatedAt = Now.AddDays(-3) };
        var events = Enumerable.Range(0, 90).Select(i => Closed("s" + i, Now.AddHours(-1))).ToList();
        events.Add(Closed("s0", Now.AddMinutes(-10)));
        events.Add(Closed("outside", Now.AddDays(-2)));
        events.Add(Closed("abandoned", Now.AddHours(-1), "Abandoned"));
        events.Add(Closed("blocked", Now.AddHours(-1), "Blocked"));
        events.Add(new ProjectEvent { Type = ProjectEventTypes.CheckpointChanged, Author = "klives", Timestamp = Now.AddMinutes(-5), Text = "Klives closed step legacy as Done: checked" });
        var range = ProjectAnalyticsCalculator.ResolveRange("24h", project.CreatedAt, Now);
        var snapshot = ProjectAnalyticsCalculator.BuildProject(project, new ProjectBudgetLedger.Ledger(), events, [], [], range, Now);
        Assert.Equal(91, snapshot.Summary.CompletedSteps);
        Assert.Equal(91, snapshot.Series.Sum(p => p.CompletedSteps));
        Assert.Equal(0, snapshot.Summary.RangeSpendUsd);
        Assert.Null(snapshot.Execution.ProductiveRate);
    }

    [Fact]
    public void OverviewExcludesShelvedTotalsAndReusesHistoricalSnapshotsAcrossLiveRefreshes()
    {
        var store = new ProjectStore(_ => { }, Path.Combine(root, "projects.json"));
        var project = store.CreateProject("Live", "Outcome", 20, 0, 0, 2);
        var archived = store.CreateProject("Shelved", "Outcome", 20, 0, 0, 2);
        archived.Status = ProjectStatus.Archived; store.SaveProject(archived);
        var events = new ProjectEventLogStore(_ => { });
        var evt = Closed("one", DateTime.UtcNow.AddMinutes(-10)); evt.ProjectID = project.ProjectID; events.Append(evt);
        evt = Closed("shelved", DateTime.UtcNow.AddMinutes(-10)); evt.ProjectID = archived.ProjectID; events.Append(evt);
        var budgets = new ProjectBudgetLedger(store, events, new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { });
        var agents = new ProjectSubAgentManager(store, events);
        var councils = new ProjectCouncilStore(_ => { });
        var analytics = new ProjectAnalyticsService(store, budgets, events, agents, councils);
        var activity = new ProjectAgentActivityTracker();
        activity.BeginThinking(project.ProjectID, "commander", "commander", null);
        var parent = new Omnipotent.Services.Projects.Projects();
        void Set(string property, object value) => parent.GetType().GetProperty(property)!.SetValue(parent, value);
        Set("Store", store); Set("Budget", budgets); Set("Analytics", analytics); Set("Activity", activity);
        Set("RuntimeState", new ProjectRuntimeStateStore(_ => { }, Path.Combine(root, "runtime")));
        Set("Gates", new ProjectGateManager(events, _ => { })); Set("Digests", new ProjectDigestStore(_ => { }));
        Set("Observables", new ProjectObservableStore(_ => { }));
        var service = new ProjectOverviewService(parent);
        var first = service.Get("24h");
        Assert.Equal(2, first.Projects.Count);
        Assert.Equal(1, first.CompletedSteps);
        Assert.Equal(1, first.WorkingProjects);
        var cached = analytics.GetProject(project.ProjectID, "24h", Now);
        Assert.Same(cached, analytics.GetProject(project.ProjectID, "24h", Now.AddSeconds(10)));
        var second = service.Get("24h");
        Assert.Equal(first.CompletedSteps, second.CompletedSteps);
        Assert.Empty(second.Attention);
        Assert.Throws<ArgumentException>(() => service.Get("all"));
    }

    private static ProjectEvent Closed(string id, DateTime at, string result = "Done") => new()
    { Type = ProjectEventTypes.CheckpointChanged, Author = "commander", Timestamp = at,
        PayloadJson = JsonConvert.SerializeObject(new { op = "close_step", stepID = id, result }) };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
