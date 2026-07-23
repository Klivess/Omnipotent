using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Read-only analytics over the Projects event log, budget ledger, council transcripts and
/// current agent roster. Lifetime totals come from the authoritative ledger; historical charts
/// use the per-wake/council attribution that is available in the append-only records.
/// </summary>
public sealed class ProjectAnalyticsService
{
    private readonly ProjectStore projects;
    private readonly ProjectBudgetLedger budgets;
    private readonly ProjectEventLogStore events;
    private readonly ProjectSubAgentManager agents;
    private readonly ProjectCouncilStore councils;
    private readonly ProjectTokenUsageStore? tokenUsage;
    private readonly ConcurrentDictionary<string, CachedProjectSnapshot> cache = new(StringComparer.Ordinal);

    private sealed record CachedProjectSnapshot(
        long LastSequence,
        long LastUsageSequence,
        DateTime LedgerUpdatedAt,
        double TokenBudgetUsd,
        ProjectStatus Status,
        ProjectAnalyticsSnapshot Snapshot);

    public ProjectAnalyticsService(
        ProjectStore projects,
        ProjectBudgetLedger budgets,
        ProjectEventLogStore events,
        ProjectSubAgentManager agents,
        ProjectCouncilStore councils,
        ProjectTokenUsageStore? tokenUsage = null)
    {
        this.projects = projects;
        this.budgets = budgets;
        this.events = events;
        this.agents = agents;
        this.councils = councils;
        this.tokenUsage = tokenUsage;
    }

    public ProjectAnalyticsSnapshot? GetProject(string projectID, string? rangeKey, DateTime? utcNow = null)
    {
        CacheDeps.MarkUncacheable("project analytics uses a rolling time window");
        var project = projects.GetProject(projectID);
        if (project == null) return null;

        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var range = ProjectAnalyticsCalculator.ResolveRange(rangeKey, project.CreatedAt, now);
        return GetProject(project, range, now);
    }

    private ProjectAnalyticsSnapshot GetProject(Project project, AnalyticsRange range, DateTime now)
    {
        string projectID = project.ProjectID;
        var ledger = budgets.GetLedger(projectID);
        long lastSequence = events.GetLastSequence(projectID);
        long lastUsageSequence = tokenUsage?.GetLastSequence(projectID) ?? 0;
        DateTime? usageCutoverUtc = tokenUsage?.GetCutoverUtc(projectID);
        string cacheKey = projectID + "|" + range.Key + "|" + range.FromUtc.Ticks + "|" + range.Bucket;

        if (cache.TryGetValue(cacheKey, out var cached)
            && cached.LastSequence == lastSequence
            && cached.LastUsageSequence == lastUsageSequence
            && cached.LedgerUpdatedAt == ledger.UpdatedAt
            && Math.Abs(cached.TokenBudgetUsd - project.TokenBudgetUsd) < 0.0000001
            && cached.Status == project.Status
            && now - cached.Snapshot.GeneratedAt < TimeSpan.FromSeconds(15))
            return cached.Snapshot;

        var snapshot = ProjectAnalyticsCalculator.BuildProject(
            project,
            ledger,
            // Lifecycle reconstruction needs the status immediately before the selected range.
            // Read from creation through the range end once; BuildProject still limits every
            // ordinary activity/spend metric to the requested window.
            events.EnumerateRange(projectID, project.CreatedAt, range.ToUtc),
            agents.ListActive(projectID),
            councils.List(projectID),
            range,
            now,
            tokenUsage?.EnumerateRange(projectID, range.FromUtc, range.ToUtc),
            usageCutoverUtc);

        cache[cacheKey] = new CachedProjectSnapshot(
            lastSequence, lastUsageSequence, ledger.UpdatedAt, project.TokenBudgetUsd, project.Status, snapshot);
        TrimCache(now);
        return snapshot;
    }

    public PortfolioAnalyticsSnapshot GetPortfolio(string? rangeKey, DateTime? utcNow = null)
    {
        CacheDeps.MarkUncacheable("project analytics uses a rolling time window");
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var allProjects = projects.ListProjects();
        DateTime earliest = allProjects.Count == 0
            ? now.Date
            : allProjects.Min(p => p.CreatedAt).ToUniversalTime();
        var range = ProjectAnalyticsCalculator.ResolveRange(rangeKey, earliest, now);

        var snapshots = new List<ProjectAnalyticsSnapshot>(allProjects.Count);
        foreach (var project in allProjects)
            snapshots.Add(GetProject(project, range, now));

        return ProjectAnalyticsCalculator.BuildPortfolio(snapshots, range, now);
    }

    private void TrimCache(DateTime now)
    {
        if (cache.Count <= 512) return;

        DateTime staleBefore = now.AddMinutes(-2);
        foreach (var entry in cache)
        {
            if (entry.Value.Snapshot.GeneratedAt < staleBefore)
                cache.TryRemove(entry.Key, out _);
        }

        int excess = cache.Count - 512;
        if (excess <= 0) return;
        foreach (string key in cache
            .OrderBy(entry => entry.Value.Snapshot.GeneratedAt)
            .Take(excess)
            .Select(entry => entry.Key))
            cache.TryRemove(key, out _);
    }
}

public sealed class AnalyticsRange
{
    public string Key { get; set; } = "30d";
    public string Label { get; set; } = "Last 30 days";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string Bucket { get; set; } = "day";
}

public sealed class ProjectAnalyticsSnapshot
{
    public string Scope { get; set; } = "project";
    public DateTime GeneratedAt { get; set; }
    public AnalyticsRange Range { get; set; } = new();
    public AnalyticsProjectIdentity Project { get; set; } = new();
    public AnalyticsSummary Summary { get; set; } = new();
    public List<AnalyticsSeriesPoint> Series { get; set; } = new();
    public List<AnalyticsCountItem> Outcomes { get; set; } = new();
    public List<AnalyticsModelMetric> Models { get; set; } = new();
    public List<AnalyticsAgentMetric> Agents { get; set; } = new();
    public List<AnalyticsCountItem> EventTypes { get; set; } = new();
    public List<AnalyticsHeatmapCell> Heatmap { get; set; } = new();
    public AnalyticsBudgetForecast Budget { get; set; } = new();
    public AnalyticsCoverage Coverage { get; set; } = new();
    [Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, int> FullEventTypeCounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    [Newtonsoft.Json.JsonIgnore]
    public List<string> ActiveDateKeys { get; set; } = new();
    [Newtonsoft.Json.JsonIgnore]
    public int WakeDurationSamples { get; set; }
}

public sealed class PortfolioAnalyticsSnapshot
{
    public string Scope { get; set; } = "all";
    public DateTime GeneratedAt { get; set; }
    public AnalyticsRange Range { get; set; } = new();
    public AnalyticsSummary Summary { get; set; } = new();
    public List<AnalyticsSeriesPoint> Series { get; set; } = new();
    public List<AnalyticsCountItem> Outcomes { get; set; } = new();
    public List<AnalyticsModelMetric> Models { get; set; } = new();
    public List<AnalyticsCountItem> EventTypes { get; set; } = new();
    public List<AnalyticsHeatmapCell> Heatmap { get; set; } = new();
    public List<AnalyticsProjectRow> Projects { get; set; } = new();
    public List<AnalyticsCountItem> Statuses { get; set; } = new();
    public AnalyticsBudgetForecast Budget { get; set; } = new();
    public AnalyticsCoverage Coverage { get; set; } = new();
}

public sealed class AnalyticsProjectIdentity
{
    public string ProjectID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class AnalyticsSummary
{
    public int ProjectCount { get; set; }
    public int ActiveProjects { get; set; }
    public double LifetimeSpendUsd { get; set; }
    public double RangeSpendUsd { get; set; }
    public double TokenBudgetUsd { get; set; }
    public double TotalBudgetUsd { get; set; }
    public double RemainingBudgetUsd { get; set; }
    public double BudgetUsedPct { get; set; }
    public double LifetimeMoneySpendUsd { get; set; }
    public double RangeMoneySpendUsd { get; set; }
    public double MoneyBudgetUsd { get; set; }
    public double RemainingMoneyBudgetUsd { get; set; }
    public double MoneyBudgetUsedPct { get; set; }
    public long LifetimeTokens { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long RangeTokens { get; set; }
    public long RangePromptTokens { get; set; }
    public long RangeCompletionTokens { get; set; }
    public long RangeUnclassifiedTokens { get; set; }
    public int Events { get; set; }
    public int Wakes { get; set; }
    public int SuccessfulWakes { get; set; }
    public int FailedWakes { get; set; }
    public int DeferredWakes { get; set; }
    public int CancelledWakes { get; set; }
    public double SuccessRate { get; set; }
    public int ActiveDays { get; set; }
    /// <summary>
    /// Project-time represented by the selected range. For a portfolio this is the sum of each
    /// project's eligible duration (project-hours), not wall-clock duration.
    /// </summary>
    public double RangeTrackedDurationMs { get; set; }
    /// <summary>Time in Active or Planning during the selected range.</summary>
    public double RangeActiveDurationMs { get; set; }
    /// <summary>Time in Paused or BudgetPaused during the selected range.</summary>
    public double RangePausedDurationMs { get; set; }
    /// <summary>Blocked, archived or completed time in the range, excluding paused time.</summary>
    public double RangeInactiveDurationMs { get; set; }
    public double RangeAvailabilityPct { get; set; }
    /// <summary>Distinct paused intervals intersecting the selected range.</summary>
    public int PauseCount { get; set; }
    /// <summary>Portfolio-only count of projects currently Paused or BudgetPaused.</summary>
    public int PausedProjects { get; set; }
    public DateTime? CurrentPauseStartedAt { get; set; }
    public double AvgWakeDurationMs { get; set; }
    public double AvgCostPerWake { get; set; }
    public int Tools { get; set; }
    public int ProductiveActions { get; set; }
    public int Artifacts { get; set; }
    public int Councils { get; set; }
    public double CouncilSpendUsd { get; set; }
    /// <summary>Non-retired agent records attached to the project, whether or not it is running.</summary>
    public int RosterAgents { get; set; }
    /// <summary>Roster agents whose project is currently Active or Planning.</summary>
    public int ActiveAgents { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public sealed class AnalyticsSeriesPoint
{
    public string Date { get; set; } = "";
    public double SpendUsd { get; set; }
    public double CumulativeSpendUsd { get; set; }
    public double MoneySpendUsd { get; set; }
    public long TotalTokens { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long UnclassifiedTokens { get; set; }
    public int Events { get; set; }
    public int Wakes { get; set; }
    public int SuccessfulWakes { get; set; }
    public int FailedWakes { get; set; }
    public int DeferredWakes { get; set; }
    public int CancelledWakes { get; set; }
    public int ToolCalls { get; set; }
    public int ProductiveActions { get; set; }
    public double ActiveDurationMs { get; set; }
    public double PausedDurationMs { get; set; }
    public double InactiveDurationMs { get; set; }
    public double AvailabilityPct { get; set; }
}

public sealed class AnalyticsCountItem
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class AnalyticsModelMetric
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Historical wake aggregates attributed to this model.</summary>
    public int Wakes { get; set; }
    /// <summary>Structured or council model turns attributed to this model.</summary>
    public int Calls { get; set; }
    public long Tokens { get; set; }
    public double CostUsd { get; set; }
}

public sealed class AnalyticsAgentMetric
{
    public string AgentID { get; set; } = "";
    public string Label { get; set; } = "";
    public int Wakes { get; set; }
    public int SuccessfulWakes { get; set; }
    public double SuccessRate { get; set; }
    public long Tokens { get; set; }
    public double CostUsd { get; set; }
    public int ToolCalls { get; set; }
    public int ProductiveActions { get; set; }
    public double AvgDurationMs { get; set; }
}

public sealed class AnalyticsHeatmapCell
{
    /// <summary>Monday = 0, Sunday = 6.</summary>
    public int Day { get; set; }
    public int Hour { get; set; }
    public int Count { get; set; }
}

public sealed class AnalyticsBudgetForecast
{
    public double SpentUsd { get; set; }
    public double BudgetUsd { get; set; }
    public double RemainingUsd { get; set; }
    public double UsedPct { get; set; }
    /// <summary>Spend per 24 hours of Active/Planning time.</summary>
    public double AverageDailySpendUsd { get; set; }
    /// <summary>Spend divided by all eligible wall-clock time, including pauses.</summary>
    public double CalendarAverageDailySpendUsd { get; set; }
    /// <summary>
    /// Current burn contribution. Zero while this project is not Active/Planning; for a portfolio
    /// it is the sum of currently active projects' active-time-normalized rates.
    /// </summary>
    public double CurrentActiveDailySpendUsd { get; set; }
    public bool CurrentlyPaused { get; set; }
    public bool CurrentlyActive { get; set; }
    public double? EstimatedDaysRemaining { get; set; }
    public DateTime? EstimatedExhaustionAt { get; set; }
}

public sealed class AnalyticsCoverage
{
    public double RangeTrackedSpendUsd { get; set; }
    public long RangeTrackedTokens { get; set; }
    public long RangeDetailedTokens { get; set; }
    public long RangeUnclassifiedTokens { get; set; }
    public double DetailedTokenPct { get; set; }
    public DateTime? StructuredUsageCutoverAt { get; set; }
    public DateTime? LatestStructuredUsageCutoverAt { get; set; }
    public int JournalledProjectCount { get; set; }
    public int StructuredWakeRecords { get; set; }
    public int StructuredUsageRecords { get; set; }
    public int ReconciliationRecords { get; set; }
    public int UtilityUsageRecords { get; set; }
    public int LegacyWakeRecords { get; set; }
    public int PostCutoverUsageGaps { get; set; }
    public int PostCutoverCouncilUsageGaps { get; set; }
    public int ProvisionalCostRecords { get; set; }
    public double? LifetimeUnattributedSpendUsd { get; set; }
    public long? LifetimeUnattributedTokens { get; set; }
    public string Note { get; set; } =
        "Lifetime totals are authoritative from the ledger. The structured usage journal powers new range attribution; historical wake/council records fill earlier gaps and older totals remain visibly unclassified.";
}

public sealed class AnalyticsProjectRow
{
    public string ProjectID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public double TokenSpendUsd { get; set; }
    public double BudgetUsd { get; set; }
    public double BudgetUsedPct { get; set; }
    public double MoneySpendUsd { get; set; }
    public double MoneyBudgetUsd { get; set; }
    public double MoneyBudgetUsedPct { get; set; }
    public double RangeSpendUsd { get; set; }
    public long LifetimeTokens { get; set; }
    public long RangeTokens { get; set; }
    public int Events { get; set; }
    public int Wakes { get; set; }
    public double SuccessRate { get; set; }
    public int ActiveAgents { get; set; }
    public int RosterAgents { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public double RangeTrackedDurationMs { get; set; }
    public double RangeActiveDurationMs { get; set; }
    public double RangePausedDurationMs { get; set; }
    public double RangeInactiveDurationMs { get; set; }
    public double RangeAvailabilityPct { get; set; }
    public int PauseCount { get; set; }
    public DateTime? CurrentPauseStartedAt { get; set; }
    public double AverageDailySpendUsd { get; set; }
    public double? EstimatedDaysRemaining { get; set; }
    public DateTime? EstimatedExhaustionAt { get; set; }
}

internal static class ProjectAnalyticsCalculator
{
    private static readonly Regex WakeSpendPattern = new(
        @"this wake:\s*~\$(?<cost>[0-9]+(?:\.[0-9]+)?),\s*(?<tokens>[\d,]+)\s+tokens",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SpawnPattern = new(
        @"Spawned\s+.+?\s+agent\s+'(?<role>[^']+)'\s+\((?<id>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MoneySpendPattern = new(
        @"Real-money\s+spend\s+\$(?<cost>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> WakeOutcomeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ProjectEventTypes.WakeCompleted,
        ProjectEventTypes.WakeFailed,
        ProjectEventTypes.WakeDeferred,
        ProjectEventTypes.WakeCancelled,
    };

    internal static AnalyticsRange ResolveRange(string? rawKey, DateTime earliestUtc, DateTime nowUtc)
    {
        string key = (rawKey ?? "30d").Trim().ToLowerInvariant();
        if (key is "1y" or "year") key = "365d";
        if (key is not ("7d" or "30d" or "90d" or "365d" or "all")) key = "30d";

        nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        earliestUtc = earliestUtc.Kind == DateTimeKind.Utc ? earliestUtc : earliestUtc.ToUniversalTime();
        DateTime from;
        string label;
        switch (key)
        {
            case "7d": from = nowUtc.Date.AddDays(-6); label = "Last 7 days"; break;
            case "90d": from = nowUtc.Date.AddDays(-89); label = "Last 90 days"; break;
            case "365d": from = nowUtc.Date.AddDays(-364); label = "Last 12 months"; break;
            case "all": from = earliestUtc.Date; label = "All time"; break;
            default: from = nowUtc.Date.AddDays(-29); label = "Last 30 days"; break;
        }
        if (from > nowUtc) from = nowUtc.Date;

        double days = Math.Max(1, (nowUtc.Date - from.Date).TotalDays + 1);
        string bucket = days > 730 ? "month" : days > 120 ? "week" : "day";
        return new AnalyticsRange
        {
            Key = key,
            Label = label,
            FromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc),
            ToUtc = nowUtc,
            Bucket = bucket,
        };
    }

    internal static ProjectAnalyticsSnapshot BuildProject(
        Project project,
        ProjectBudgetLedger.Ledger ledger,
        IEnumerable<ProjectEvent> sourceEvents,
        IEnumerable<ProjectAgentRecord> activeAgents,
        IEnumerable<CouncilSession> sourceCouncils,
        AnalyticsRange range,
        DateTime nowUtc,
        IEnumerable<ProjectTokenUsageRecord>? sourceUsage = null,
        DateTime? usageCutoverUtc = null)
    {
        // The service streams history from project creation so range-start state can be seeded.
        // Retain only lifecycle transitions before the requested window; old ordinary activity
        // must not turn a long-lived project's analytics request into an unbounded allocation.
        var eventList = new List<ProjectEvent>();
        var lifecycleEventList = new List<ProjectEvent>();
        foreach (var evt in sourceEvents)
        {
            DateTime timestamp = evt.Timestamp.ToUniversalTime();
            if (timestamp > range.ToUtc) continue;
            if (timestamp >= range.FromUtc) eventList.Add(evt);
            if (ProjectLifecycleEvents.TryReadToStatus(evt, out _))
                lifecycleEventList.Add(evt);
        }
        eventList = eventList
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Sequence)
            .ToList();
        lifecycleEventList = lifecycleEventList
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Sequence)
            .ToList();
        var councilList = sourceCouncils
            .Where(c => c.CreatedAt.ToUniversalTime() <= range.ToUtc
                && (c.CompletedAt ?? c.CreatedAt).ToUniversalTime() >= range.FromUtc)
            .ToList();
        var usageList = (sourceUsage ?? Array.Empty<ProjectTokenUsageRecord>())
            .Where(u => u.OccurredAt.ToUniversalTime() >= range.FromUtc
                && u.OccurredAt.ToUniversalTime() <= range.ToUtc)
            .OrderBy(u => u.OccurredAt)
            .ThenBy(u => u.Sequence)
            .ToList();
        DateTime? cutoverUtc = usageCutoverUtc?.ToUniversalTime()
            ?? usageList
                .Where(usage => usage.PromptTokens > 0 || usage.CompletionTokens > 0)
                .Select(usage => (DateTime?)usage.OccurredAt.ToUniversalTime())
                .OrderBy(timestamp => timestamp)
                .FirstOrDefault();
        var roster = activeAgents.ToList();

        var series = CreateSeries(range);
        var lifecycle = BuildLifecycle(project, lifecycleEventList, range);
        ApplyLifecycleToSeries(series, lifecycle.Intervals, range);
        var seriesByKey = series.ToDictionary(p => p.Date, StringComparer.Ordinal);
        var eventTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var heatmapCounts = new int[7, 24];
        var activeDates = new HashSet<DateOnly>();
        var agentLabels = roster.ToDictionary(
            a => a.AgentID,
            a => string.Equals(a.AgentID, "commander", StringComparison.OrdinalIgnoreCase)
                ? "Commander"
                : string.IsNullOrWhiteSpace(a.Role) ? a.AgentID : a.Role,
            StringComparer.OrdinalIgnoreCase);
        agentLabels["commander"] = "Commander";

        DateTime? lastActivityAt = null;
        int artifacts = 0;
        double rangeMoneySpend = 0;
        foreach (var evt in eventList)
        {
            DateTime timestamp = evt.Timestamp.ToUniversalTime();
            lastActivityAt = !lastActivityAt.HasValue || timestamp > lastActivityAt ? timestamp : lastActivityAt;
            activeDates.Add(DateOnly.FromDateTime(timestamp));
            heatmapCounts[MondayBasedDay(timestamp.DayOfWeek), timestamp.Hour]++;

            string type = string.IsNullOrWhiteSpace(evt.Type) ? "unknown" : evt.Type;
            eventTypeCounts[type] = eventTypeCounts.GetValueOrDefault(type) + 1;
            if (seriesByKey.TryGetValue(BucketKey(timestamp, range.Bucket), out var point))
            {
                point.Events++;
                if (type == ProjectEventTypes.ToolCall) point.ToolCalls++;
                if (type == ProjectEventTypes.MoneySpent)
                {
                    double moneyAmount = ReadMoneySpend(evt);
                    point.MoneySpendUsd += moneyAmount;
                    rangeMoneySpend += moneyAmount;
                }
            }

            if (type is ProjectEventTypes.ArtifactAdded or ProjectEventTypes.ProjectFileChanged)
                artifacts++;

            if (type == ProjectEventTypes.AgentSpawned && !string.IsNullOrWhiteSpace(evt.Text))
            {
                var match = SpawnPattern.Match(evt.Text);
                if (match.Success)
                    agentLabels[match.Groups["id"].Value] = match.Groups["role"].Value;
            }
        }

        var wakes = BuildWakeMetrics(eventList);
        var outcomeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modelMetrics = new Dictionary<string, AnalyticsModelMetric>(StringComparer.OrdinalIgnoreCase);
        var agentMetrics = new Dictionary<string, AgentAccumulator>(StringComparer.OrdinalIgnoreCase);
        var directJournalWakeIDs = usageList
            .Where(IsDirectWakeUsage)
            .Select(u => u.WakeID)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reconciledUsageIDs = usageList
            .Select(u => u.ReconcilesUsageID)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        long rangePrompt = 0, rangeCompletion = 0, rangeUnclassified = 0;
        double rangeSpend = 0;
        double councilSpend = 0;
        int successful = 0, failed = 0, deferred = 0, cancelled = 0;
        long elapsedTotal = 0;
        int elapsedCount = 0;
        int productiveActions = 0;
        int structuredWakeRecords = 0;
        int structuredUsageRecords = 0;
        int reconciliationRecords = 0;
        int utilityUsageRecords = 0;
        int legacyWakeRecords = 0;
        int postCutoverUsageGaps = 0;
        int postCutoverCouncilUsageGaps = 0;
        int provisionalCostRecords = 0;

        foreach (var usage in usageList)
        {
            long promptTokens = Math.Max(0, usage.PromptTokens);
            long completionTokens = Math.Max(0, usage.CompletionTokens);
            long totalTokens = promptTokens + completionTokens;
            double costUsd = double.IsFinite(usage.CostUsd) ? usage.CostUsd : 0;
            DateTime timestamp = usage.OccurredAt.ToUniversalTime();
            bool isReconciliation =
                string.Equals(usage.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(usage.CostBasis, "reconciliation", StringComparison.OrdinalIgnoreCase);

            if (isReconciliation) reconciliationRecords++;
            else if (totalTokens > 0) structuredUsageRecords++;
            if (!isReconciliation
                && string.Equals(usage.Source, "utility", StringComparison.OrdinalIgnoreCase))
                utilityUsageRecords++;
            if (string.Equals(usage.Source, "council", StringComparison.OrdinalIgnoreCase))
                councilSpend += costUsd;
            if (string.Equals(usage.CostBasis, "provisional", StringComparison.OrdinalIgnoreCase)
                && !reconciledUsageIDs.Contains(usage.UsageID)
                && costUsd > 0)
                provisionalCostRecords++;

            rangePrompt += promptTokens;
            rangeCompletion += completionTokens;
            rangeSpend += costUsd;
            if (seriesByKey.TryGetValue(BucketKey(timestamp, range.Bucket), out var usagePoint))
            {
                usagePoint.SpendUsd += costUsd;
                usagePoint.TotalTokens += totalTokens;
                usagePoint.PromptTokens += promptTokens;
                usagePoint.CompletionTokens += completionTokens;
            }

            string model = string.IsNullOrWhiteSpace(usage.Model) ? "unknown" : usage.Model;
            if (!modelMetrics.TryGetValue(model, out var usageModelMetric))
            {
                usageModelMetric = new AnalyticsModelMetric { Key = model, Label = model };
                modelMetrics[model] = usageModelMetric;
            }
            if (!isReconciliation && totalTokens > 0) usageModelMetric.Calls++;
            usageModelMetric.Tokens += totalTokens;
            usageModelMetric.CostUsd += costUsd;

            string agentID = string.IsNullOrWhiteSpace(usage.AgentID) ? "system" : usage.AgentID;
            if (!agentMetrics.TryGetValue(agentID, out var usageAgentMetric))
            {
                usageAgentMetric = new AgentAccumulator { AgentID = agentID };
                agentMetrics[agentID] = usageAgentMetric;
            }
            usageAgentMetric.Tokens += totalTokens;
            usageAgentMetric.CostUsd += costUsd;
        }

        foreach (var wake in wakes)
        {
            bool postJournalCutover = cutoverUtc.HasValue
                && wake.Timestamp.ToUniversalTime() >= cutoverUtc.Value;
            bool hasDirectJournalUsage = directJournalWakeIDs.Contains(wake.WakeID);
            bool journalBacked = postJournalCutover || hasDirectJournalUsage;
            if (hasDirectJournalUsage || !postJournalCutover && wake.DetailedUsage)
                structuredWakeRecords++;
            else if (!postJournalCutover && (wake.TotalTokens > 0 || wake.CostUsd > 0))
                legacyWakeRecords++;
            else if (postJournalCutover && (wake.TotalTokens > 0 || wake.CostUsd > 0))
                postCutoverUsageGaps++;
            if (!journalBacked
                && wake.CostBasis.Contains("provisional", StringComparison.OrdinalIgnoreCase)
                && wake.CostUsd > 0)
                provisionalCostRecords++;
            outcomeCounts[wake.Outcome] = outcomeCounts.GetValueOrDefault(wake.Outcome) + 1;
            switch (wake.Outcome)
            {
                case ProjectEventTypes.WakeCompleted: successful++; break;
                case ProjectEventTypes.WakeFailed: failed++; break;
                case ProjectEventTypes.WakeDeferred: deferred++; break;
                case ProjectEventTypes.WakeCancelled: cancelled++; break;
            }

            if (!journalBacked)
            {
                rangePrompt += wake.PromptTokens;
                rangeCompletion += wake.CompletionTokens;
                rangeUnclassified += wake.UnclassifiedTokens;
                rangeSpend += wake.CostUsd;
            }
            productiveActions += wake.ProductiveActions;
            if (wake.ElapsedMs.HasValue)
            {
                elapsedTotal += wake.ElapsedMs.Value;
                elapsedCount++;
            }

            if (seriesByKey.TryGetValue(BucketKey(wake.Timestamp, range.Bucket), out var point))
            {
                point.Wakes++;
                if (!journalBacked)
                {
                    point.SpendUsd += wake.CostUsd;
                    point.TotalTokens += wake.TotalTokens;
                    point.PromptTokens += wake.PromptTokens;
                    point.CompletionTokens += wake.CompletionTokens;
                    point.UnclassifiedTokens += wake.UnclassifiedTokens;
                }
                point.ProductiveActions += wake.ProductiveActions;
                switch (wake.Outcome)
                {
                    case ProjectEventTypes.WakeCompleted: point.SuccessfulWakes++; break;
                    case ProjectEventTypes.WakeFailed: point.FailedWakes++; break;
                    case ProjectEventTypes.WakeDeferred: point.DeferredWakes++; break;
                    case ProjectEventTypes.WakeCancelled: point.CancelledWakes++; break;
                }
            }

            if (!journalBacked)
            {
                string model = string.IsNullOrWhiteSpace(wake.Model) ? "unknown" : wake.Model;
                if (!modelMetrics.TryGetValue(model, out var modelMetric))
                {
                    modelMetric = new AnalyticsModelMetric { Key = model, Label = model };
                    modelMetrics[model] = modelMetric;
                }
                modelMetric.Wakes++;
                modelMetric.Tokens += wake.TotalTokens;
                modelMetric.CostUsd += wake.CostUsd;
            }

            string agentID = string.IsNullOrWhiteSpace(wake.AgentID) ? "commander" : wake.AgentID;
            if (!agentMetrics.TryGetValue(agentID, out var agentMetric))
            {
                agentMetric = new AgentAccumulator { AgentID = agentID };
                agentMetrics[agentID] = agentMetric;
            }
            agentMetric.Wakes++;
            if (wake.Outcome == ProjectEventTypes.WakeCompleted) agentMetric.SuccessfulWakes++;
            if (!journalBacked)
            {
                agentMetric.Tokens += wake.TotalTokens;
                agentMetric.CostUsd += wake.CostUsd;
            }
            agentMetric.ToolCalls += wake.DispatchedToolCalls;
            agentMetric.ProductiveActions += wake.ProductiveActions;
            if (wake.ElapsedMs.HasValue)
            {
                agentMetric.DurationTotalMs += wake.ElapsedMs.Value;
                agentMetric.DurationCount++;
            }
        }

        int councilCount = 0;
        var consumedCouncilUsage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var council in councilList)
        {
            bool counted = false;
            foreach (var statement in council.Statements)
            {
                DateTime timestamp = statement.Timestamp.ToUniversalTime();
                if (timestamp < range.FromUtc || timestamp > range.ToUtc) continue;
                counted = true;
                string statementReference =
                    $"{council.CouncilID}:{statement.Round}:{UsageSlug(statement.Role)}";
                bool IsCandidate(ProjectTokenUsageRecord usage) =>
                    string.Equals(usage.Source, "council", StringComparison.OrdinalIgnoreCase)
                    && !consumedCouncilUsage.Contains(UsageIdentity(usage))
                    && usage.PromptTokens == statement.PromptTokens
                    && usage.CompletionTokens == statement.CompletionTokens
                    && Math.Abs((usage.OccurredAt.ToUniversalTime() - timestamp).TotalSeconds) < 1
                    && !string.Equals(usage.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(usage.CostBasis, "reconciliation", StringComparison.OrdinalIgnoreCase);
                var journalStatement = usageList.FirstOrDefault(usage =>
                        IsCandidate(usage)
                        && string.Equals(
                            usage.SourceReference, statementReference, StringComparison.OrdinalIgnoreCase))
                    ?? usageList.FirstOrDefault(usage =>
                        IsCandidate(usage)
                        && string.Equals(
                            usage.SourceReference, council.CouncilID, StringComparison.OrdinalIgnoreCase));
                bool journalBackedStatement = journalStatement != null;
                if (journalStatement != null)
                    consumedCouncilUsage.Add(UsageIdentity(journalStatement));
                if (cutoverUtc.HasValue && timestamp >= cutoverUtc.Value)
                {
                    if (!journalBackedStatement
                        && (statement.PromptTokens > 0
                            || statement.CompletionTokens > 0
                            || statement.CostUsd > 0))
                        postCutoverCouncilUsageGaps++;
                    continue;
                }
                if (journalBackedStatement) continue;

                long statementTokens = Math.Max(0, statement.PromptTokens) + Math.Max(0, statement.CompletionTokens);
                double statementCost = Math.Max(0, statement.CostUsd);
                rangePrompt += Math.Max(0, statement.PromptTokens);
                rangeCompletion += Math.Max(0, statement.CompletionTokens);
                rangeSpend += statementCost;
                councilSpend += statementCost;

                if (seriesByKey.TryGetValue(BucketKey(timestamp, range.Bucket), out var point))
                {
                    point.SpendUsd += statementCost;
                    point.TotalTokens += statementTokens;
                    point.PromptTokens += Math.Max(0, statement.PromptTokens);
                    point.CompletionTokens += Math.Max(0, statement.CompletionTokens);
                }

                string model = string.IsNullOrWhiteSpace(council.Model) ? "council / unknown" : council.Model;
                if (!modelMetrics.TryGetValue(model, out var modelMetric))
                {
                    modelMetric = new AnalyticsModelMetric { Key = model, Label = model };
                    modelMetrics[model] = modelMetric;
                }
                modelMetric.Calls++;
                modelMetric.Tokens += statementTokens;
                modelMetric.CostUsd += statementCost;
            }
            if (counted || council.CreatedAt.ToUniversalTime() >= range.FromUtc) councilCount++;
        }

        long rangeTokens = rangePrompt + rangeCompletion + rangeUnclassified;
        int totalOutcomes = successful + failed + deferred + cancelled;
        double successRate = totalOutcomes > 0 ? successful * 100.0 / totalOutcomes : 0;
        int toolCalls = eventList.Count(e => e.Type == ProjectEventTypes.ToolCall);
        double lifetimeSpend = Math.Max(0, ledger.TokenSpendUsd);
        double budget = Math.Max(0, project.TokenBudgetUsd);
        double remaining = Math.Max(0, budget - lifetimeSpend);
        double usedPct = budget > 0 ? lifetimeSpend * 100.0 / budget : 0;
        double moneySpend = Math.Max(0, ledger.MoneySpendUsd);
        double moneyBudget = Math.Max(0, project.MoneyBudgetUsd);
        double moneyRemaining = Math.Max(0, moneyBudget - moneySpend);
        double moneyUsedPct = moneyBudget > 0 ? moneySpend * 100.0 / moneyBudget : 0;
        double trackedDays = lifecycle.TrackedDuration.TotalDays;
        double activeDays = lifecycle.ActiveDuration.TotalDays;
        double calendarDailySpend = trackedDays > 0 ? rangeSpend / trackedDays : 0;
        double activeDailySpend = activeDays > 0 ? rangeSpend / activeDays : 0;
        bool currentlyActive = IsRunnableStatus(project.Status);
        bool currentlyPaused = IsPausedStatus(project.Status);
        double currentActiveDailySpend = currentlyActive ? activeDailySpend : 0;
        double? daysRemaining = activeDailySpend > 0 && remaining > 0
            ? remaining / activeDailySpend
            : null;

        double cumulative = 0;
        foreach (var point in series)
        {
            cumulative += point.SpendUsd;
            point.CumulativeSpendUsd = cumulative;
        }

        var summary = new AnalyticsSummary
        {
            ProjectCount = 1,
            ActiveProjects = currentlyActive ? 1 : 0,
            LifetimeSpendUsd = RoundMoney(lifetimeSpend),
            RangeSpendUsd = RoundMoney(rangeSpend),
            TokenBudgetUsd = RoundMoney(budget),
            TotalBudgetUsd = RoundMoney(budget),
            RemainingBudgetUsd = RoundMoney(remaining),
            BudgetUsedPct = Math.Round(usedPct, 2),
            LifetimeMoneySpendUsd = RoundMoney(moneySpend),
            RangeMoneySpendUsd = RoundMoney(rangeMoneySpend),
            MoneyBudgetUsd = RoundMoney(moneyBudget),
            RemainingMoneyBudgetUsd = RoundMoney(moneyRemaining),
            MoneyBudgetUsedPct = Math.Round(moneyUsedPct, 2),
            LifetimeTokens = Math.Max(0, ledger.PromptTokens) + Math.Max(0, ledger.CompletionTokens),
            PromptTokens = Math.Max(0, ledger.PromptTokens),
            CompletionTokens = Math.Max(0, ledger.CompletionTokens),
            RangeTokens = rangeTokens,
            RangePromptTokens = rangePrompt,
            RangeCompletionTokens = rangeCompletion,
            RangeUnclassifiedTokens = rangeUnclassified,
            Events = eventList.Count,
            Wakes = wakes.Count,
            SuccessfulWakes = successful,
            FailedWakes = failed,
            DeferredWakes = deferred,
            CancelledWakes = cancelled,
            SuccessRate = Math.Round(successRate, 1),
            ActiveDays = activeDates.Count,
            RangeTrackedDurationMs = lifecycle.TrackedDuration.TotalMilliseconds,
            RangeActiveDurationMs = lifecycle.ActiveDuration.TotalMilliseconds,
            RangePausedDurationMs = lifecycle.PausedDuration.TotalMilliseconds,
            RangeInactiveDurationMs = lifecycle.InactiveDuration.TotalMilliseconds,
            RangeAvailabilityPct = lifecycle.TrackedDuration > TimeSpan.Zero
                ? Math.Round(lifecycle.ActiveDuration.TotalMilliseconds * 100.0
                    / lifecycle.TrackedDuration.TotalMilliseconds, 1)
                : 0,
            PauseCount = lifecycle.PauseCount,
            PausedProjects = currentlyPaused ? 1 : 0,
            CurrentPauseStartedAt = currentlyPaused ? lifecycle.CurrentPauseStartedAt : null,
            AvgWakeDurationMs = elapsedCount > 0 ? Math.Round(elapsedTotal / (double)elapsedCount, 0) : 0,
            AvgCostPerWake = wakes.Count > 0 ? RoundMoney(rangeSpend / wakes.Count) : 0,
            Tools = toolCalls,
            ProductiveActions = productiveActions,
            Artifacts = artifacts,
            Councils = councilCount,
            CouncilSpendUsd = RoundMoney(councilSpend),
            RosterAgents = roster.Count,
            ActiveAgents = currentlyActive ? roster.Count : 0,
            LastActivityAt = lastActivityAt,
        };

        return new ProjectAnalyticsSnapshot
        {
            GeneratedAt = nowUtc,
            Range = CloneRange(range),
            Project = new AnalyticsProjectIdentity
            {
                ProjectID = project.ProjectID,
                Name = project.Name,
                Status = project.Status.ToString(),
                CreatedAt = project.CreatedAt,
            },
            Summary = summary,
            Series = series,
            Outcomes = OutcomeItems(outcomeCounts),
            Models = modelMetrics.Values
                .OrderByDescending(m => m.CostUsd)
                .ThenByDescending(m => m.Tokens)
                .Select(RoundModel)
                .ToList(),
            Agents = agentMetrics.Values
                .OrderByDescending(a => a.CostUsd)
                .ThenByDescending(a => a.Wakes)
                .Select(a => new AnalyticsAgentMetric
                {
                    AgentID = a.AgentID,
                    Label = agentLabels.GetValueOrDefault(a.AgentID)
                        ?? (a.AgentID.Length > 12 ? a.AgentID[..12] : a.AgentID),
                    Wakes = a.Wakes,
                    SuccessfulWakes = a.SuccessfulWakes,
                    SuccessRate = a.Wakes > 0 ? Math.Round(a.SuccessfulWakes * 100.0 / a.Wakes, 1) : 0,
                    Tokens = a.Tokens,
                    CostUsd = RoundMoney(a.CostUsd),
                    ToolCalls = a.ToolCalls,
                    ProductiveActions = a.ProductiveActions,
                    AvgDurationMs = a.DurationCount > 0
                        ? Math.Round(a.DurationTotalMs / (double)a.DurationCount, 0)
                        : 0,
                })
                .ToList(),
            EventTypes = eventTypeCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(14)
                .Select(kv => new AnalyticsCountItem
                {
                    Key = kv.Key,
                    Label = Humanize(kv.Key),
                    Count = kv.Value,
                })
                .ToList(),
            Heatmap = BuildHeatmap(heatmapCounts),
            Budget = new AnalyticsBudgetForecast
            {
                SpentUsd = RoundMoney(lifetimeSpend),
                BudgetUsd = RoundMoney(budget),
                RemainingUsd = RoundMoney(remaining),
                UsedPct = Math.Round(usedPct, 2),
                AverageDailySpendUsd = RoundMoney(activeDailySpend),
                CalendarAverageDailySpendUsd = RoundMoney(calendarDailySpend),
                CurrentActiveDailySpendUsd = RoundMoney(currentActiveDailySpend),
                CurrentlyPaused = currentlyPaused,
                CurrentlyActive = currentlyActive,
                EstimatedDaysRemaining = daysRemaining.HasValue ? Math.Round(daysRemaining.Value, 1) : null,
                EstimatedExhaustionAt = currentlyActive
                    && daysRemaining.HasValue && daysRemaining.Value < 36500
                    ? nowUtc.AddDays(daysRemaining.Value)
                    : null,
            },
            Coverage = new AnalyticsCoverage
            {
                RangeTrackedSpendUsd = RoundMoney(rangeSpend),
                RangeTrackedTokens = rangeTokens,
                RangeDetailedTokens = rangePrompt + rangeCompletion,
                RangeUnclassifiedTokens = rangeUnclassified,
                DetailedTokenPct = rangeTokens > 0
                    ? Math.Round((rangePrompt + rangeCompletion) * 100.0 / rangeTokens, 1)
                    : 0,
                StructuredUsageCutoverAt = cutoverUtc,
                LatestStructuredUsageCutoverAt = cutoverUtc,
                JournalledProjectCount = cutoverUtc.HasValue ? 1 : 0,
                StructuredWakeRecords = structuredWakeRecords,
                StructuredUsageRecords = structuredUsageRecords,
                ReconciliationRecords = reconciliationRecords,
                UtilityUsageRecords = utilityUsageRecords,
                LegacyWakeRecords = legacyWakeRecords,
                PostCutoverUsageGaps = postCutoverUsageGaps,
                PostCutoverCouncilUsageGaps = postCutoverCouncilUsageGaps,
                ProvisionalCostRecords = provisionalCostRecords,
                LifetimeUnattributedSpendUsd = range.Key == "all"
                    ? RoundMoney(Math.Max(0, lifetimeSpend - rangeSpend))
                    : null,
                LifetimeUnattributedTokens = range.Key == "all"
                    ? Math.Max(0, Math.Max(0, ledger.PromptTokens)
                        + Math.Max(0, ledger.CompletionTokens) - rangeTokens)
                    : null,
            },
            FullEventTypeCounts = new Dictionary<string, int>(
                eventTypeCounts, StringComparer.OrdinalIgnoreCase),
            ActiveDateKeys = activeDates
                .OrderBy(date => date)
                .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList(),
            WakeDurationSamples = elapsedCount,
        };
    }

    internal static PortfolioAnalyticsSnapshot BuildPortfolio(
        IReadOnlyList<ProjectAnalyticsSnapshot> projects,
        AnalyticsRange range,
        DateTime nowUtc)
    {
        var series = CreateSeries(range);
        var seriesByKey = series.ToDictionary(p => p.Date, StringComparer.Ordinal);
        var outcomes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, AnalyticsModelMetric>(StringComparer.OrdinalIgnoreCase);
        var statuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var heatmap = new int[7, 24];

        foreach (var snapshot in projects)
        {
            statuses[snapshot.Project.Status] = statuses.GetValueOrDefault(snapshot.Project.Status) + 1;
            foreach (var source in snapshot.Series)
            {
                if (!seriesByKey.TryGetValue(source.Date, out var target)) continue;
                target.SpendUsd += source.SpendUsd;
                target.MoneySpendUsd += source.MoneySpendUsd;
                target.TotalTokens += source.TotalTokens;
                target.PromptTokens += source.PromptTokens;
                target.CompletionTokens += source.CompletionTokens;
                target.UnclassifiedTokens += source.UnclassifiedTokens;
                target.Events += source.Events;
                target.Wakes += source.Wakes;
                target.SuccessfulWakes += source.SuccessfulWakes;
                target.FailedWakes += source.FailedWakes;
                target.DeferredWakes += source.DeferredWakes;
                target.CancelledWakes += source.CancelledWakes;
                target.ToolCalls += source.ToolCalls;
                target.ProductiveActions += source.ProductiveActions;
                target.ActiveDurationMs += source.ActiveDurationMs;
                target.PausedDurationMs += source.PausedDurationMs;
                target.InactiveDurationMs += source.InactiveDurationMs;
            }
            foreach (var item in snapshot.Outcomes)
                outcomes[item.Key] = outcomes.GetValueOrDefault(item.Key) + item.Count;
            var sourceEventTypes = snapshot.FullEventTypeCounts.Count > 0
                ? snapshot.FullEventTypeCounts
                : snapshot.EventTypes.ToDictionary(
                    item => item.Key, item => item.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var item in sourceEventTypes)
                eventTypes[item.Key] = eventTypes.GetValueOrDefault(item.Key) + item.Value;
            foreach (var item in snapshot.Models)
            {
                if (!models.TryGetValue(item.Key, out var aggregate))
                {
                    aggregate = new AnalyticsModelMetric { Key = item.Key, Label = item.Label };
                    models[item.Key] = aggregate;
                }
                aggregate.Wakes += item.Wakes;
                aggregate.Calls += item.Calls;
                aggregate.Tokens += item.Tokens;
                aggregate.CostUsd += item.CostUsd;
            }
            foreach (var cell in snapshot.Heatmap)
                if (cell.Day is >= 0 and < 7 && cell.Hour is >= 0 and < 24)
                    heatmap[cell.Day, cell.Hour] += cell.Count;
        }

        double cumulative = 0;
        foreach (var point in series)
        {
            cumulative += point.SpendUsd;
            point.CumulativeSpendUsd = cumulative;
            double tracked = point.ActiveDurationMs
                + point.PausedDurationMs
                + point.InactiveDurationMs;
            point.AvailabilityPct = tracked > 0
                ? Math.Round(point.ActiveDurationMs * 100.0 / tracked, 1)
                : 0;
        }

        double lifetimeSpend = projects.Sum(p => p.Summary.LifetimeSpendUsd);
        double totalBudget = projects.Sum(p => p.Summary.TokenBudgetUsd);
        double remaining = Math.Max(0, totalBudget - lifetimeSpend);
        double moneySpend = projects.Sum(p => p.Summary.LifetimeMoneySpendUsd);
        double rangeMoneySpend = projects.Sum(p => p.Summary.RangeMoneySpendUsd);
        double moneyBudget = projects.Sum(p => p.Summary.MoneyBudgetUsd);
        double moneyRemaining = Math.Max(0, moneyBudget - moneySpend);
        double rangeSpend = projects.Sum(p => p.Summary.RangeSpendUsd);
        int wakes = projects.Sum(p => p.Summary.Wakes);
        int successful = projects.Sum(p => p.Summary.SuccessfulWakes);
        int failed = projects.Sum(p => p.Summary.FailedWakes);
        int deferred = projects.Sum(p => p.Summary.DeferredWakes);
        int cancelled = projects.Sum(p => p.Summary.CancelledWakes);
        int outcomeCount = successful + failed + deferred + cancelled;
        double wallClockDays = Math.Max(
            0,
            (range.ToUtc.ToUniversalTime() - range.FromUtc.ToUniversalTime()).TotalDays);
        double calendarDailySpend = wallClockDays > 0 ? rangeSpend / wallClockDays : 0;
        double averageActiveDailySpend = projects.Sum(p => p.Budget.AverageDailySpendUsd);
        double currentActiveDailySpend = projects.Sum(p => p.Budget.CurrentActiveDailySpendUsd);
        double currentActiveRemaining = projects
            .Where(p => p.Budget.CurrentlyActive)
            .Sum(p => p.Budget.RemainingUsd);
        double? daysRemaining = currentActiveDailySpend > 0 && currentActiveRemaining > 0
            ? currentActiveRemaining / currentActiveDailySpend
            : null;
        long durationWeight = projects.Sum(p =>
            p.WakeDurationSamples > 0 ? p.WakeDurationSamples : p.Summary.Wakes);
        double weightedDuration = durationWeight > 0
            ? projects.Sum(p => p.Summary.AvgWakeDurationMs
                * (p.WakeDurationSamples > 0 ? p.WakeDurationSamples : p.Summary.Wakes)) / durationWeight
            : 0;
        var portfolioActiveDates = projects
            .SelectMany(project => project.ActiveDateKeys)
            .ToHashSet(StringComparer.Ordinal);

        var summary = new AnalyticsSummary
        {
            ProjectCount = projects.Count,
            ActiveProjects = projects.Count(p => IsRunnableStatus(p.Project.Status)),
            LifetimeSpendUsd = RoundMoney(lifetimeSpend),
            RangeSpendUsd = RoundMoney(rangeSpend),
            TokenBudgetUsd = RoundMoney(totalBudget),
            TotalBudgetUsd = RoundMoney(totalBudget),
            RemainingBudgetUsd = RoundMoney(remaining),
            BudgetUsedPct = totalBudget > 0 ? Math.Round(lifetimeSpend * 100.0 / totalBudget, 2) : 0,
            LifetimeMoneySpendUsd = RoundMoney(moneySpend),
            RangeMoneySpendUsd = RoundMoney(rangeMoneySpend),
            MoneyBudgetUsd = RoundMoney(moneyBudget),
            RemainingMoneyBudgetUsd = RoundMoney(moneyRemaining),
            MoneyBudgetUsedPct = moneyBudget > 0 ? Math.Round(moneySpend * 100.0 / moneyBudget, 2) : 0,
            LifetimeTokens = projects.Sum(p => p.Summary.LifetimeTokens),
            PromptTokens = projects.Sum(p => p.Summary.PromptTokens),
            CompletionTokens = projects.Sum(p => p.Summary.CompletionTokens),
            RangeTokens = projects.Sum(p => p.Summary.RangeTokens),
            RangePromptTokens = projects.Sum(p => p.Summary.RangePromptTokens),
            RangeCompletionTokens = projects.Sum(p => p.Summary.RangeCompletionTokens),
            RangeUnclassifiedTokens = projects.Sum(p => p.Summary.RangeUnclassifiedTokens),
            Events = projects.Sum(p => p.Summary.Events),
            Wakes = wakes,
            SuccessfulWakes = successful,
            FailedWakes = failed,
            DeferredWakes = deferred,
            CancelledWakes = cancelled,
            SuccessRate = outcomeCount > 0 ? Math.Round(successful * 100.0 / outcomeCount, 1) : 0,
            ActiveDays = portfolioActiveDates.Count > 0
                ? portfolioActiveDates.Count
                : series.Count(p => p.Events > 0),
            RangeTrackedDurationMs = projects.Sum(p => p.Summary.RangeTrackedDurationMs),
            RangeActiveDurationMs = projects.Sum(p => p.Summary.RangeActiveDurationMs),
            RangePausedDurationMs = projects.Sum(p => p.Summary.RangePausedDurationMs),
            RangeInactiveDurationMs = projects.Sum(p => p.Summary.RangeInactiveDurationMs),
            RangeAvailabilityPct = projects.Sum(p => p.Summary.RangeTrackedDurationMs) > 0
                ? Math.Round(projects.Sum(p => p.Summary.RangeActiveDurationMs) * 100.0
                    / projects.Sum(p => p.Summary.RangeTrackedDurationMs), 1)
                : 0,
            PauseCount = projects.Sum(p => p.Summary.PauseCount),
            PausedProjects = projects.Count(p => IsPausedStatus(p.Project.Status)),
            CurrentPauseStartedAt = projects
                .Where(p => IsPausedStatus(p.Project.Status))
                .Select(p => p.Summary.CurrentPauseStartedAt)
                .Where(timestamp => timestamp.HasValue)
                .Select(timestamp => timestamp!.Value)
                .DefaultIfEmpty()
                .Min() is var earliestPause && earliestPause != default
                    ? earliestPause
                    : null,
            AvgWakeDurationMs = Math.Round(weightedDuration, 0),
            AvgCostPerWake = wakes > 0 ? RoundMoney(rangeSpend / wakes) : 0,
            Tools = projects.Sum(p => p.Summary.Tools),
            ProductiveActions = projects.Sum(p => p.Summary.ProductiveActions),
            Artifacts = projects.Sum(p => p.Summary.Artifacts),
            Councils = projects.Sum(p => p.Summary.Councils),
            CouncilSpendUsd = RoundMoney(projects.Sum(p => p.Summary.CouncilSpendUsd)),
            RosterAgents = projects.Sum(p => p.Summary.RosterAgents),
            ActiveAgents = projects.Sum(p => p.Summary.ActiveAgents),
            LastActivityAt = projects.Select(p => p.Summary.LastActivityAt).Where(x => x.HasValue)
                .Select(x => x!.Value).DefaultIfEmpty().Max() is var latest && latest != default
                    ? latest
                    : null,
        };

        return new PortfolioAnalyticsSnapshot
        {
            GeneratedAt = nowUtc,
            Range = CloneRange(range),
            Summary = summary,
            Series = series,
            Outcomes = OutcomeItems(outcomes),
            Models = models.Values.OrderByDescending(m => m.CostUsd).ThenByDescending(m => m.Tokens)
                .Select(RoundModel).ToList(),
            EventTypes = eventTypes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(14)
                .Select(kv => new AnalyticsCountItem
                {
                    Key = kv.Key,
                    Label = Humanize(kv.Key),
                    Count = kv.Value,
                })
                .ToList(),
            Heatmap = BuildHeatmap(heatmap),
            Projects = projects
                .OrderByDescending(p => p.Summary.LifetimeSpendUsd)
                .Select(p => new AnalyticsProjectRow
                {
                    ProjectID = p.Project.ProjectID,
                    Name = p.Project.Name,
                    Status = p.Project.Status,
                    CreatedAt = p.Project.CreatedAt,
                    TokenSpendUsd = p.Summary.LifetimeSpendUsd,
                    BudgetUsd = p.Summary.TokenBudgetUsd,
                    BudgetUsedPct = p.Summary.BudgetUsedPct,
                    MoneySpendUsd = p.Summary.LifetimeMoneySpendUsd,
                    MoneyBudgetUsd = p.Summary.MoneyBudgetUsd,
                    MoneyBudgetUsedPct = p.Summary.MoneyBudgetUsedPct,
                    RangeSpendUsd = p.Summary.RangeSpendUsd,
                    LifetimeTokens = p.Summary.LifetimeTokens,
                    RangeTokens = p.Summary.RangeTokens,
                    Events = p.Summary.Events,
                    Wakes = p.Summary.Wakes,
                    SuccessRate = p.Summary.SuccessRate,
                    ActiveAgents = p.Summary.ActiveAgents,
                    RosterAgents = p.Summary.RosterAgents,
                    LastActivityAt = p.Summary.LastActivityAt,
                    RangeTrackedDurationMs = p.Summary.RangeTrackedDurationMs,
                    RangeActiveDurationMs = p.Summary.RangeActiveDurationMs,
                    RangePausedDurationMs = p.Summary.RangePausedDurationMs,
                    RangeInactiveDurationMs = p.Summary.RangeInactiveDurationMs,
                    RangeAvailabilityPct = p.Summary.RangeAvailabilityPct,
                    PauseCount = p.Summary.PauseCount,
                    CurrentPauseStartedAt = p.Summary.CurrentPauseStartedAt,
                    AverageDailySpendUsd = p.Budget.AverageDailySpendUsd,
                    EstimatedDaysRemaining = p.Budget.EstimatedDaysRemaining,
                    EstimatedExhaustionAt = p.Budget.EstimatedExhaustionAt,
                })
                .ToList(),
            Statuses = statuses.OrderByDescending(kv => kv.Value)
                .Select(kv => new AnalyticsCountItem
                {
                    Key = kv.Key,
                    Label = Humanize(kv.Key),
                    Count = kv.Value,
                })
                .ToList(),
            Budget = new AnalyticsBudgetForecast
            {
                SpentUsd = RoundMoney(lifetimeSpend),
                BudgetUsd = RoundMoney(totalBudget),
                RemainingUsd = RoundMoney(remaining),
                UsedPct = totalBudget > 0 ? Math.Round(lifetimeSpend * 100.0 / totalBudget, 2) : 0,
                AverageDailySpendUsd = RoundMoney(averageActiveDailySpend),
                CalendarAverageDailySpendUsd = RoundMoney(calendarDailySpend),
                CurrentActiveDailySpendUsd = RoundMoney(currentActiveDailySpend),
                CurrentlyPaused = projects.Count > 0
                    && projects.All(p => !p.Budget.CurrentlyActive)
                    && projects.Any(p => p.Budget.CurrentlyPaused),
                CurrentlyActive = projects.Any(p => p.Budget.CurrentlyActive),
                EstimatedDaysRemaining = daysRemaining.HasValue ? Math.Round(daysRemaining.Value, 1) : null,
                EstimatedExhaustionAt = daysRemaining.HasValue && daysRemaining.Value < 36500
                    ? nowUtc.AddDays(daysRemaining.Value)
                    : null,
            },
            Coverage = new AnalyticsCoverage
            {
                RangeTrackedSpendUsd = RoundMoney(projects.Sum(p => p.Coverage.RangeTrackedSpendUsd)),
                RangeTrackedTokens = projects.Sum(p => p.Coverage.RangeTrackedTokens),
                RangeDetailedTokens = projects.Sum(p => p.Coverage.RangeDetailedTokens),
                RangeUnclassifiedTokens = projects.Sum(p => p.Coverage.RangeUnclassifiedTokens),
                DetailedTokenPct = projects.Sum(p => p.Coverage.RangeTrackedTokens) > 0
                    ? Math.Round(projects.Sum(p => p.Coverage.RangeDetailedTokens) * 100.0
                        / projects.Sum(p => p.Coverage.RangeTrackedTokens), 1)
                    : 0,
                StructuredUsageCutoverAt = projects
                    .Select(p => p.Coverage.StructuredUsageCutoverAt)
                    .Where(timestamp => timestamp.HasValue)
                    .Select(timestamp => timestamp!.Value)
                    .DefaultIfEmpty()
                    .Min() is var earliestCutover && earliestCutover != default
                        ? earliestCutover
                        : null,
                LatestStructuredUsageCutoverAt = projects
                    .Select(p => p.Coverage.StructuredUsageCutoverAt)
                    .Where(timestamp => timestamp.HasValue)
                    .Select(timestamp => timestamp!.Value)
                    .DefaultIfEmpty()
                    .Max() is var latestCutover && latestCutover != default
                        ? latestCutover
                        : null,
                JournalledProjectCount = projects.Count(
                    p => p.Coverage.StructuredUsageCutoverAt.HasValue),
                StructuredWakeRecords = projects.Sum(p => p.Coverage.StructuredWakeRecords),
                StructuredUsageRecords = projects.Sum(p => p.Coverage.StructuredUsageRecords),
                ReconciliationRecords = projects.Sum(p => p.Coverage.ReconciliationRecords),
                UtilityUsageRecords = projects.Sum(p => p.Coverage.UtilityUsageRecords),
                LegacyWakeRecords = projects.Sum(p => p.Coverage.LegacyWakeRecords),
                PostCutoverUsageGaps = projects.Sum(p => p.Coverage.PostCutoverUsageGaps),
                PostCutoverCouncilUsageGaps = projects.Sum(
                    p => p.Coverage.PostCutoverCouncilUsageGaps),
                ProvisionalCostRecords = projects.Sum(p => p.Coverage.ProvisionalCostRecords),
                LifetimeUnattributedSpendUsd = range.Key == "all"
                    ? RoundMoney(projects.Sum(
                        p => p.Coverage.LifetimeUnattributedSpendUsd ?? 0))
                    : null,
                LifetimeUnattributedTokens = range.Key == "all"
                    ? projects.Sum(p => p.Coverage.LifetimeUnattributedTokens ?? 0)
                    : null,
            },
        };
    }

    private static List<WakeMetric> BuildWakeMetrics(IReadOnlyList<ProjectEvent> events)
    {
        var byWake = new Dictionary<string, WakeMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            if (!WakeOutcomeTypes.Contains(evt.Type)) continue;
            string key = string.IsNullOrWhiteSpace(evt.WakeID) ? "event:" + evt.EventID : evt.WakeID;
            var wake = byWake.GetValueOrDefault(key) ?? new WakeMetric { WakeID = key };
            wake.Timestamp = evt.Timestamp.ToUniversalTime();
            wake.AgentID = string.IsNullOrWhiteSpace(evt.AgentID) ? wake.AgentID : evt.AgentID;
            wake.Outcome = evt.Type;
            ParseLegacyWakeSpend(evt.Text, wake);
            byWake[key] = wake;
        }

        foreach (var evt in events.Where(e => e.Type == ProjectEventTypes.WakeDiagnostic))
        {
            string key = string.IsNullOrWhiteSpace(evt.WakeID) ? "event:" + evt.EventID : evt.WakeID;
            var wake = byWake.GetValueOrDefault(key) ?? new WakeMetric
            {
                WakeID = key,
                Timestamp = evt.Timestamp.ToUniversalTime(),
            };
            wake.AgentID = string.IsNullOrWhiteSpace(evt.AgentID) ? wake.AgentID : evt.AgentID;
            wake.Timestamp = wake.Timestamp == default ? evt.Timestamp.ToUniversalTime() : wake.Timestamp;

            JObject? payload = null;
            try { if (!string.IsNullOrWhiteSpace(evt.PayloadJson)) payload = JObject.Parse(evt.PayloadJson); }
            catch { }

            string? outcome = ReadString(payload, "outcome");
            if (!string.IsNullOrWhiteSpace(outcome)) wake.Outcome = outcome;
            wake.ElapsedMs = ReadLong(payload, "elapsedMs") ?? wake.ElapsedMs;
            wake.DispatchedToolCalls = (int)(ReadLong(payload, "dispatchedToolCalls") ?? wake.DispatchedToolCalls);
            wake.ProductiveActions = (int)(ReadLong(payload, "productiveActions") ?? wake.ProductiveActions);
            wake.Model = ReadString(payload, "finalModel") ?? ReadString(payload, "initialModel") ?? wake.Model;
            wake.CostBasis = ReadString(payload, "costBasis") ?? wake.CostBasis;

            long? prompt = ReadLong(payload, "promptTokens");
            long? completion = ReadLong(payload, "completionTokens");
            long? total = ReadLong(payload, "totalTokens");
            double? cost = ReadDouble(payload, "costUsd");
            if (prompt.HasValue || completion.HasValue)
            {
                wake.DetailedUsage = true;
                wake.PromptTokens = Math.Max(0, prompt ?? 0);
                wake.CompletionTokens = Math.Max(0, completion ?? 0);
                wake.TotalTokens = Math.Max(wake.PromptTokens + wake.CompletionTokens, total ?? 0);
                wake.UnclassifiedTokens = Math.Max(0, wake.TotalTokens - wake.PromptTokens - wake.CompletionTokens);
            }
            else if (total.HasValue)
            {
                wake.TotalTokens = Math.Max(0, total.Value);
                wake.UnclassifiedTokens = wake.TotalTokens;
            }
            if (cost.HasValue) wake.CostUsd = Math.Max(0, cost.Value);
            byWake[key] = wake;
        }

        foreach (var wake in byWake.Values)
        {
            if (string.IsNullOrWhiteSpace(wake.Outcome)) wake.Outcome = "unknown";
            if (wake.TotalTokens <= 0)
                wake.TotalTokens = wake.PromptTokens + wake.CompletionTokens + wake.UnclassifiedTokens;
            if (wake.UnclassifiedTokens <= 0 && wake.TotalTokens > wake.PromptTokens + wake.CompletionTokens)
                wake.UnclassifiedTokens = wake.TotalTokens - wake.PromptTokens - wake.CompletionTokens;
        }
        return byWake.Values.OrderBy(w => w.Timestamp).ToList();
    }

    private static void ParseLegacyWakeSpend(string? text, WakeMetric wake)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var match = WakeSpendPattern.Match(text);
        if (!match.Success) return;
        if (double.TryParse(match.Groups["cost"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost))
            wake.CostUsd = Math.Max(0, cost);
        if (long.TryParse(match.Groups["tokens"].Value.Replace(",", ""),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long tokens))
        {
            wake.TotalTokens = Math.Max(0, tokens);
            wake.UnclassifiedTokens = wake.TotalTokens;
        }
    }

    private static double ReadMoneySpend(ProjectEvent evt)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
            {
                var payload = JObject.Parse(evt.PayloadJson);
                double? amount = ReadDouble(payload, "amountUsd");
                if (amount.HasValue && double.IsFinite(amount.Value))
                    return Math.Max(0, amount.Value);
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(evt.Text)) return 0;
        var match = MoneySpendPattern.Match(evt.Text);
        return match.Success
            && double.TryParse(
                match.Groups["cost"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double cost)
            && double.IsFinite(cost)
                ? Math.Max(0, cost)
                : 0;
    }

    private static LifecycleMetrics BuildLifecycle(
        Project project,
        IReadOnlyList<ProjectEvent> events,
        AnalyticsRange range)
    {
        DateTime createdAt = project.CreatedAt.ToUniversalTime();
        DateTime rangeStart = range.FromUtc.ToUniversalTime();
        DateTime rangeEnd = range.ToUtc.ToUniversalTime();
        DateTime trackedStart = createdAt > rangeStart ? createdAt : rangeStart;
        if (trackedStart >= rangeEnd)
            return new LifecycleMetrics();

        var transitions = events
            .Select(evt => new
            {
                Event = evt,
                Timestamp = evt.Timestamp.ToUniversalTime(),
                HasStatus = ProjectLifecycleEvents.TryReadToStatus(evt, out ProjectStatus status),
                Status = status,
            })
            .Where(item => item.HasStatus
                && item.Timestamp >= createdAt
                && item.Timestamp <= rangeEnd)
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.Event.Sequence)
            .ToList();

        // Projects have always begun in a runnable phase (historically Active, now Planning).
        // The distinction does not affect availability; durable transitions refine it from there.
        AvailabilityState state = AvailabilityState.Active;
        DateTime stateStartedAt = createdAt;
        var intervals = new List<LifecycleInterval>();

        void AddInterval(DateTime from, DateTime to, AvailabilityState intervalState)
        {
            DateTime clampedFrom = from > trackedStart ? from : trackedStart;
            DateTime clampedTo = to < rangeEnd ? to : rangeEnd;
            if (clampedTo <= clampedFrom) return;
            intervals.Add(new LifecycleInterval(clampedFrom, clampedTo, intervalState));
        }

        foreach (var transition in transitions)
        {
            AvailabilityState next = AvailabilityFor(transition.Status);
            if (next == state) continue; // repeated pause/status writes do not restart intervals
            AddInterval(stateStartedAt, transition.Timestamp, state);
            state = next;
            stateStartedAt = transition.Timestamp;
        }
        AddInterval(stateStartedAt, rangeEnd, state);

        TimeSpan tracked = rangeEnd - trackedStart;
        TimeSpan active = SumDuration(intervals, AvailabilityState.Active);
        TimeSpan paused = SumDuration(intervals, AvailabilityState.Paused);
        TimeSpan inactive = SumDuration(intervals, AvailabilityState.Inactive);
        int pauseCount = intervals.Count(interval => interval.State == AvailabilityState.Paused);

        DateTime? currentPauseStartedAt = IsPausedStatus(project.Status)
            && state == AvailabilityState.Paused
                ? stateStartedAt
                : null;

        return new LifecycleMetrics
        {
            Intervals = intervals,
            TrackedDuration = tracked,
            ActiveDuration = active,
            PausedDuration = paused,
            InactiveDuration = inactive,
            PauseCount = pauseCount,
            CurrentPauseStartedAt = currentPauseStartedAt,
        };
    }

    private static TimeSpan SumDuration(
        IEnumerable<LifecycleInterval> intervals,
        AvailabilityState state)
        => TimeSpan.FromTicks(intervals
            .Where(interval => interval.State == state)
            .Sum(interval => (interval.ToUtc - interval.FromUtc).Ticks));

    private static void ApplyLifecycleToSeries(
        IReadOnlyList<AnalyticsSeriesPoint> series,
        IReadOnlyList<LifecycleInterval> intervals,
        AnalyticsRange range)
    {
        foreach (var point in series)
        {
            DateTime bucketStart = DateTime.SpecifyKind(
                DateTime.ParseExact(
                    point.Date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
            DateTime bucketEnd = range.Bucket switch
            {
                "month" => bucketStart.AddMonths(1),
                "week" => bucketStart.AddDays(7),
                _ => bucketStart.AddDays(1),
            };

            foreach (var interval in intervals)
            {
                DateTime overlapStart = interval.FromUtc > bucketStart
                    ? interval.FromUtc
                    : bucketStart;
                DateTime overlapEnd = interval.ToUtc < bucketEnd
                    ? interval.ToUtc
                    : bucketEnd;
                if (overlapEnd <= overlapStart) continue;
                double milliseconds = (overlapEnd - overlapStart).TotalMilliseconds;
                switch (interval.State)
                {
                    case AvailabilityState.Active:
                        point.ActiveDurationMs += milliseconds;
                        break;
                    case AvailabilityState.Paused:
                        point.PausedDurationMs += milliseconds;
                        break;
                    default:
                        point.InactiveDurationMs += milliseconds;
                        break;
                }
            }

            double tracked = point.ActiveDurationMs
                + point.PausedDurationMs
                + point.InactiveDurationMs;
            point.AvailabilityPct = tracked > 0
                ? Math.Round(point.ActiveDurationMs * 100.0 / tracked, 1)
                : 0;
        }
    }

    private static List<AnalyticsSeriesPoint> CreateSeries(AnalyticsRange range)
    {
        var result = new List<AnalyticsSeriesPoint>();
        DateTime cursor = BucketStart(range.FromUtc, range.Bucket);
        DateTime end = BucketStart(range.ToUtc, range.Bucket);
        while (cursor <= end)
        {
            result.Add(new AnalyticsSeriesPoint { Date = BucketKey(cursor, range.Bucket) });
            cursor = range.Bucket switch
            {
                "month" => cursor.AddMonths(1),
                "week" => cursor.AddDays(7),
                _ => cursor.AddDays(1),
            };
        }
        return result;
    }

    private static DateTime BucketStart(DateTime timestamp, string bucket)
    {
        DateTime date = timestamp.ToUniversalTime().Date;
        if (bucket == "month") return new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (bucket == "week")
        {
            int daysSinceMonday = MondayBasedDay(date.DayOfWeek);
            return date.AddDays(-daysSinceMonday);
        }
        return date;
    }

    private static string BucketKey(DateTime timestamp, string bucket)
        => BucketStart(timestamp, bucket).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int MondayBasedDay(DayOfWeek day) => ((int)day + 6) % 7;

    private static bool IsDirectWakeUsage(ProjectTokenUsageRecord usage)
        => !string.IsNullOrWhiteSpace(usage.WakeID)
            && (string.Equals(usage.Source, "commander", StringComparison.OrdinalIgnoreCase)
                || string.Equals(usage.Source, "subagent", StringComparison.OrdinalIgnoreCase));

    private static string UsageIdentity(ProjectTokenUsageRecord usage)
        => string.IsNullOrWhiteSpace(usage.UsageID)
            ? "sequence:" + usage.Sequence.ToString(CultureInfo.InvariantCulture)
            : usage.UsageID;

    private static string UsageSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static List<AnalyticsHeatmapCell> BuildHeatmap(int[,] counts)
    {
        var result = new List<AnalyticsHeatmapCell>(7 * 24);
        for (int day = 0; day < 7; day++)
        for (int hour = 0; hour < 24; hour++)
            result.Add(new AnalyticsHeatmapCell { Day = day, Hour = hour, Count = counts[day, hour] });
        return result;
    }

    private static List<AnalyticsCountItem> OutcomeItems(IReadOnlyDictionary<string, int> counts)
    {
        string[] order =
        {
            ProjectEventTypes.WakeCompleted,
            ProjectEventTypes.WakeFailed,
            ProjectEventTypes.WakeDeferred,
            ProjectEventTypes.WakeCancelled,
        };
        var result = order.Select(key => new AnalyticsCountItem
        {
            Key = key,
            Label = Humanize(key.Replace("wake-", "")),
            Count = counts.GetValueOrDefault(key),
        }).ToList();
        foreach (var extra in counts.Where(kv => !order.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)))
            result.Add(new AnalyticsCountItem { Key = extra.Key, Label = Humanize(extra.Key), Count = extra.Value });
        return result;
    }

    private static AnalyticsRange CloneRange(AnalyticsRange range) => new()
    {
        Key = range.Key,
        Label = range.Label,
        FromUtc = range.FromUtc,
        ToUtc = range.ToUtc,
        Bucket = range.Bucket,
    };

    private static AnalyticsModelMetric RoundModel(AnalyticsModelMetric model) => new()
    {
        Key = model.Key,
        Label = model.Label,
        Wakes = model.Wakes,
        Calls = model.Calls,
        Tokens = model.Tokens,
        CostUsd = RoundMoney(model.CostUsd),
    };

    private static bool IsRunnableStatus(ProjectStatus status)
        => status is ProjectStatus.Active or ProjectStatus.Planning;

    private static bool IsRunnableStatus(string status)
        => string.Equals(status, ProjectStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, ProjectStatus.Planning.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsPausedStatus(ProjectStatus status)
        => status is ProjectStatus.Paused or ProjectStatus.BudgetPaused;

    private static bool IsPausedStatus(string status)
        => string.Equals(status, ProjectStatus.Paused.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, ProjectStatus.BudgetPaused.ToString(), StringComparison.OrdinalIgnoreCase);

    private static AvailabilityState AvailabilityFor(ProjectStatus status)
        => IsRunnableStatus(status)
            ? AvailabilityState.Active
            : IsPausedStatus(status)
                ? AvailabilityState.Paused
                : AvailabilityState.Inactive;

    private enum AvailabilityState
    {
        Active,
        Paused,
        Inactive,
    }

    private sealed record LifecycleInterval(
        DateTime FromUtc,
        DateTime ToUtc,
        AvailabilityState State);

    private sealed class LifecycleMetrics
    {
        public List<LifecycleInterval> Intervals { get; set; } = new();
        public TimeSpan TrackedDuration { get; set; }
        public TimeSpan ActiveDuration { get; set; }
        public TimeSpan PausedDuration { get; set; }
        public TimeSpan InactiveDuration { get; set; }
        public int PauseCount { get; set; }
        public DateTime? CurrentPauseStartedAt { get; set; }
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            value.Replace('-', ' ').Replace('_', ' ').ToLowerInvariant());
    }

    private static long? ReadLong(JObject? obj, string name)
    {
        var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        return token == null || token.Type == JTokenType.Null ? null : token.Value<long?>();
    }

    private static double? ReadDouble(JObject? obj, string name)
    {
        var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        return token == null || token.Type == JTokenType.Null ? null : token.Value<double?>();
    }

    private static string? ReadString(JObject? obj, string name)
    {
        var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        return token == null || token.Type == JTokenType.Null ? null : token.Value<string>();
    }

    private static double RoundMoney(double value) => Math.Round(Math.Max(0, value), 6);

    private sealed class WakeMetric
    {
        public string WakeID { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string AgentID { get; set; } = "commander";
        public string Outcome { get; set; } = "";
        public string Model { get; set; } = "unknown";
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long UnclassifiedTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public long? ElapsedMs { get; set; }
        public int DispatchedToolCalls { get; set; }
        public int ProductiveActions { get; set; }
        public bool DetailedUsage { get; set; }
        public string CostBasis { get; set; } = "unknown";
    }

    private sealed class AgentAccumulator
    {
        public string AgentID { get; set; } = "";
        public int Wakes { get; set; }
        public int SuccessfulWakes { get; set; }
        public long Tokens { get; set; }
        public double CostUsd { get; set; }
        public int ToolCalls { get; set; }
        public int ProductiveActions { get; set; }
        public long DurationTotalMs { get; set; }
        public int DurationCount { get; set; }
    }
}
