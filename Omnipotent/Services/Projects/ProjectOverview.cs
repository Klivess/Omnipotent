using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects;

public sealed class ProjectResultSelection
{
    public string ObservableID { get; set; } = "";
    public string Direction { get; set; } = "neutral";
    public string Rationale { get; set; } = "";
}

public sealed record OverviewPoint(string Date, double SpendUsd, double MoneySpendUsd, int CompletedSteps,
    int Completed, int Failed, int Deferred, int Cancelled);
public sealed record ResultPoint(DateTime Timestamp, double Value);
public sealed record ResultOption(string ObservableID, string Name);
public sealed record OverviewAttention(string ProjectID, string Name, string Kind, string Label);

public sealed class OverviewResult
{
    public string? ObservableID { get; set; }
    public string Name { get; set; } = "Completed steps";
    public string Selection { get; set; } = "fallback";
    public string Direction { get; set; } = "higher";
    public string Rationale { get; set; } = "No primary result selected; durable step completions in this range.";
    public double? Value { get; set; }
    public string Format { get; set; } = "Count";
    public string? Unit { get; set; }
    public double? Delta { get; set; }
    public DateTime? ObservedAt { get; set; }
    public bool Stale { get; set; }
    public string Validity { get; set; } = "Valid";
    public string Source { get; set; } = "System";
    public long? EvidenceEventSequence { get; set; }
    public List<string> EvidenceArtifactIDs { get; set; } = new();
    public List<ResultPoint> History { get; set; } = new();
}

public sealed class OverviewProject
{
    public string ProjectID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Halted { get; set; }
    public string ExecutionDisposition { get; set; } = "";
    public string ExecutionHealth { get; set; } = "";
    public string? Blocker { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public int PendingApprovals { get; set; }
    public string CurrentWork { get; set; } = "No current step";
    public string CurrentWorkSource { get; set; } = "None";
    public DateTime? CurrentWorkAt { get; set; }
    public int WorkingAgents { get; set; }
    public string? ActivityPhase { get; set; }
    public int SubAgentCap { get; set; }
    public double TokenSpendUsd { get; set; }
    public double MoneySpendUsd { get; set; }
    public double TokenBudgetUsd { get; set; }
    public double MoneyBudgetUsd { get; set; }
    public double RangeSpendUsd { get; set; }
    public double RangeMoneySpendUsd { get; set; }
    public int CompletedSteps { get; set; }
    public DateTime? HistoricalAt { get; set; }
    public OverviewResult Result { get; set; } = new();
    public List<ResultOption> ResultOptions { get; set; } = new();
    public List<OverviewPoint> Series { get; set; } = new();
}

public sealed class ProjectOverviewSnapshot
{
    public DateTime LiveAt { get; set; }
    public DateTime? HistoricalAt { get; set; }
    public AnalyticsRange Range { get; set; } = new();
    public string Scope { get; set; } = "All unshelved projects";
    public int WorkingProjects { get; set; }
    public int CompletedSteps { get; set; }
    public double ModelSpendUsd { get; set; }
    public double ExternalSpendUsd { get; set; }
    public ProjectExecutionAnalytics Execution { get; set; } = new();
    public List<OverviewPoint> Series { get; set; } = new();
    public List<OverviewProject> Projects { get; set; } = new();
    public List<OverviewAttention> Attention { get; set; } = new();
}

/// <summary>Lightweight live projection joined to existing five-minute analytics snapshots.</summary>
public sealed class ProjectOverviewService(Projects parent)
{
    public ProjectOverviewSnapshot Get(string rangeKey)
    {
        if (rangeKey is not ("1h" or "24h" or "7d")) throw new ArgumentException("Choose range 1h, 24h or 7d.");
        // The outer response cache must not freeze ephemeral agent state for five minutes.
        CacheDeps.MarkUncacheable("project overview contains live agent state");
        var now = DateTime.UtcNow;
        var range = ProjectAnalyticsCalculator.ResolveRange(rangeKey, now.AddDays(-7), now);
        var result = new ProjectOverviewSnapshot { LiveAt = now, Range = range };
        var projects = parent.Store.ListProjects();
        var snapshots = new ProjectAnalyticsSnapshot?[projects.Count];
        Parallel.For(0, projects.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            if (projects[i].Status != ProjectStatus.Archived)
                snapshots[i] = parent.Analytics.GetProject(projects[i].ProjectID, rangeKey, now);
        });
        for (int i = 0; i < projects.Count; i++)
        {
            var p = projects[i];
            var row = new OverviewProject { ProjectID = p.ProjectID, Name = p.Name, Status = p.Status.ToString(),
                Halted = p.HaltedFromStatus.HasValue, SubAgentCap = p.SubAgentCap,
                TokenBudgetUsd = p.TokenBudgetUsd, MoneyBudgetUsd = p.MoneyBudgetUsd };
            var spend = parent.Budget.GetSpend(p.ProjectID);
            row.TokenSpendUsd = spend.TokenSpendUsd;
            row.MoneySpendUsd = spend.MoneySpendUsd;
            result.Projects.Add(row);
            if (p.Status == ProjectStatus.Archived) continue;
            var runtime = parent.RuntimeState.GetSummary(p.ProjectID);
            row.ExecutionDisposition = runtime.Disposition.ToString();
            row.ExecutionHealth = runtime.HealthStatus.ToString();
            row.Blocker = runtime.BlockerSummary ?? p.BlockedReason;
            row.NextRetryAt = runtime.NextRetryAt;
            row.PendingApprovals = parent.Gates.CountPending(p.ProjectID);
            var step = parent.RuntimeState.GetActiveStep(p.ProjectID);
            if (step != null)
            {
                row.CurrentWork = Clip(step.NextConcreteAction ?? step.Title, 220);
                row.CurrentWorkSource = "Active step";
                row.CurrentWorkAt = step.UpdatedAt;
            }
            else
            {
                var digest = parent.Digests.GetDigest(p.ProjectID);
                if (!string.IsNullOrWhiteSpace(digest.CurrentFocus))
                {
                    row.CurrentWork = Clip(digest.CurrentFocus, 220);
                    row.CurrentWorkSource = "Digest focus";
                    row.CurrentWorkAt = digest.UpdatedAt;
                }
            }
            var activity = parent.Activity.ListForProject(p.ProjectID);
            row.WorkingAgents = activity.Count;
            row.ActivityPhase = activity.FirstOrDefault()?.Phase;
            var snapshot = snapshots[i]!;
            row.RangeSpendUsd = snapshot.Summary.RangeSpendUsd;
            row.RangeMoneySpendUsd = snapshot.Summary.RangeMoneySpendUsd;
            row.CompletedSteps = snapshot.Summary.CompletedSteps;
            row.HistoricalAt = snapshot.GeneratedAt;
            row.Series = snapshot.Series.Select(Point).ToList();
            var observables = parent.Observables.List(p.ProjectID);
            row.ResultOptions = observables.Where(o => o.Type == ObservableType.Numeric)
                .Select(o => new ResultOption(o.ObservableID, o.Name)).ToList();
            row.Result = BuildResult(p, observables, row.CompletedSteps, range.FromUtc, now);
            result.Execution.Add(snapshot.Execution);
            if (row.PendingApprovals > 0) result.Attention.Add(new(p.ProjectID, p.Name, "approval", $"{row.PendingApprovals} approval(s) pending"));
            if (p.Status == ProjectStatus.BudgetPaused) result.Attention.Add(new(p.ProjectID, p.Name, "budget", "Budget exhausted — paused"));
            if (!string.IsNullOrWhiteSpace(row.Blocker) || p.Status == ProjectStatus.Blocked)
                result.Attention.Add(new(p.ProjectID, p.Name, "blocked", Clip(row.Blocker ?? "Action required", 220)));
        }
        var live = result.Projects.Where(p => p.Status != "Archived").ToList();
        result.WorkingProjects = live.Count(p => p.WorkingAgents > 0);
        result.CompletedSteps = live.Sum(p => p.CompletedSteps);
        result.ModelSpendUsd = live.Sum(p => p.RangeSpendUsd);
        result.ExternalSpendUsd = live.Sum(p => p.RangeMoneySpendUsd);
        result.HistoricalAt = live.Select(p => p.HistoricalAt).Min();
        result.Series = live.SelectMany(p => p.Series).GroupBy(p => p.Date).OrderBy(g => g.Key)
            .Select(g => new OverviewPoint(g.Key, g.Sum(p => p.SpendUsd), g.Sum(p => p.MoneySpendUsd),
                g.Sum(p => p.CompletedSteps), g.Sum(p => p.Completed), g.Sum(p => p.Failed),
                g.Sum(p => p.Deferred), g.Sum(p => p.Cancelled))).ToList();
        // Cached projects may have different minute boundaries. Every lane uses the same axis.
        foreach (var row in live)
        {
            var byDate = row.Series.ToDictionary(p => p.Date);
            row.Series = result.Series.Select(p => byDate.GetValueOrDefault(p.Date)
                ?? new OverviewPoint(p.Date, 0, 0, 0, 0, 0, 0, 0)).ToList();
        }
        return result;
    }

    private static OverviewPoint Point(AnalyticsSeriesPoint p) => new(p.Date, p.SpendUsd, p.MoneySpendUsd,
        p.CompletedSteps, p.SuccessfulWakes, p.FailedWakes, p.DeferredWakes, p.CancelledWakes);
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    public static ProjectResultSelection ValidateSelection(IEnumerable<ProjectObservable> observables,
        string observableID, string direction, string rationale)
    {
        if (!observables.Any(o => o.ObservableID == observableID && o.Type == ObservableType.Numeric))
            throw new ArgumentException("Select an existing numeric observable in this project.");
        if (direction is not ("higher" or "lower" or "neutral"))
            throw new ArgumentException("Direction must be higher, lower or neutral.");
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 300)
            throw new ArgumentException("Provide a result rationale of 1–300 characters.");
        return new() { ObservableID = observableID, Direction = direction, Rationale = rationale.Trim() };
    }

    public static OverviewResult BuildResult(Project project, IEnumerable<ProjectObservable> observables,
        int completedSteps, DateTime from, DateTime now)
    {
        var choice = project.PinnedResult ?? project.CommanderResult;
        if (choice == null) return new() { Value = completedSteps };
        var result = new OverviewResult { ObservableID = choice.ObservableID,
            Selection = project.PinnedResult != null ? "pinned" : "commander", Direction = choice.Direction,
            Rationale = choice.Rationale, Name = "Selected result unavailable", Validity = "Unknown", Source = "Unknown" };
        var observable = observables.FirstOrDefault(o => o.ObservableID == choice.ObservableID);
        if (observable == null) return result;
        result.Name = observable.Name;
        result.Value = observable.NumericValue;
        result.Format = observable.Format.ToString();
        result.Unit = observable.Unit;
        result.ObservedAt = observable.ObservedAt;
        result.Stale = observable.StaleAfter.HasValue && now - observable.ObservedAt > observable.StaleAfter;
        result.Validity = observable.Validity.ToString();
        result.Source = observable.SourceKind.ToString();
        result.EvidenceEventSequence = observable.EvidenceEventSequence;
        result.EvidenceArtifactIDs = observable.EvidenceArtifactIDs.Take(12).ToList();
        var history = observable.History.Where(s => s.NumericValue.HasValue && double.IsFinite(s.NumericValue.Value)
            && s.Timestamp <= now).OrderBy(s => s.Timestamp).ToList();
        var baseline = history.LastOrDefault(s => s.Timestamp <= from);
        var latest = history.LastOrDefault();
        // Never substitute the previous update for a missing period baseline.
        if (baseline != null && latest != null && latest.Timestamp >= from && !result.Stale && observable.Validity == ObservableValidity.Valid)
            result.Delta = latest.NumericValue - baseline.NumericValue;
        var points = history.Where(s => s.Timestamp >= from).ToList();
        if (baseline != null && baseline.Timestamp < from) points.Insert(0, baseline);
        result.History = points.Where((_, i) => points.Count <= 48 || i == points.Count - 1 || i % (int)Math.Ceiling(points.Count / 47.0) == 0)
            .Select(s => new ResultPoint(s.Timestamp, s.NumericValue!.Value)).ToList();
        return result;
    }

    public static string? CompletedStepID(ProjectEvent evt)
    {
        if (evt.Type != ProjectEventTypes.CheckpointChanged) return null;
        if (!string.IsNullOrEmpty(evt.PayloadJson))
        {
            try
            {
                var p = JObject.Parse(evt.PayloadJson);
                if ((string?)p["op"] == "close_step" && string.Equals((string?)p["result"], "Done", StringComparison.OrdinalIgnoreCase))
                    return (string?)p["stepID"];
            }
            catch (Newtonsoft.Json.JsonException) { }
        }
        // Older human closures had no structured payload; this is the exact server-authored format.
        var match = Regex.Match(evt.Text, @"^Klives closed step (\S+) as Done:");
        return evt.Author == "klives" && match.Success ? match.Groups[1].Value : null;
    }
}
