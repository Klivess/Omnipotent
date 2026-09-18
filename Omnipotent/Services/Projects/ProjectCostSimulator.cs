using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Token buckets for one scope (fleet, project or agent), split exactly the way an LLM provider
/// bills them. The simulator deliberately returns counts rather than money: Klives supplies the
/// per-MTok prices in the browser, so re-pricing the same window is instant and needs no rebuild.
/// </summary>
public class CostSimulationBuckets
{
    /// <summary>Prompt tokens the provider had to process fresh — total prompt minus cache reads.
    /// <see cref="CacheWriteTokens"/> is a subset of this, not an addition to it.</summary>
    public long InputTokens { get; set; }
    /// <summary>Prompt tokens served from the provider's prompt cache.</summary>
    public long CacheReadTokens { get; set; }
    /// <summary>Fresh prompt tokens written into the cache. Providers that surcharge cache writes
    /// bill these instead of (not on top of) the plain input rate.</summary>
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Input + cache reads + output. The billable surface of the window.</summary>
    public long TotalTokens { get; set; }
    /// <summary>Model turns that booked tokens. Reconciliation entries are not requests.</summary>
    public int Requests { get; set; }
    /// <summary>What this scope actually cost, from the journal — provisional costs included and
    /// reconciliation adjustments applied. The yardstick the simulated figure is compared against.</summary>
    public double ActualCostUsd { get; set; }
    /// <summary>Prompt tokens booked by turns whose provider reported no cache metrics. Their cache
    /// split is unknowable, so they are counted wholly as input and flagged here instead.</summary>
    public long UnmeasuredCachePromptTokens { get; set; }
    public int UnmeasuredCacheRequests { get; set; }
}

public sealed class CostSimulationAgent : CostSimulationBuckets
{
    public string AgentID { get; set; } = "";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>False once the agent has been retired off the roster — its spend still happened.</summary>
    public bool OnRoster { get; set; }
    public bool IsCommander { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public sealed class CostSimulationProject : CostSimulationBuckets
{
    public string ProjectID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? LastActivityAt { get; set; }
    public List<CostSimulationAgent> Agents { get; set; } = new();

    /// <summary>Per-model tallies, merged into the fleet list rather than served per project.</summary>
    [JsonIgnore]
    internal Dictionary<string, CostSimulationModel> ModelTally { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>How much of the window ran on each model. One price triple across several models is a
/// hypothetical, so the simulator says out loud which models it just re-priced.</summary>
public sealed class CostSimulationModel
{
    public string Key { get; set; } = "";
    public long TotalTokens { get; set; }
    public int Requests { get; set; }
}

public sealed class CostSimulationSnapshot
{
    public string Scope { get; set; } = "cost-simulator";
    public DateTime GeneratedAt { get; set; }
    public long BuildDurationMs { get; set; }
    public AnalyticsRange Range { get; set; } = new();
    public CostSimulationBuckets Totals { get; set; } = new();
    /// <summary>Only projects that booked tokens in the window, heaviest first.</summary>
    public List<CostSimulationProject> Projects { get; set; } = new();
    public List<CostSimulationModel> Models { get; set; } = new();
    /// <summary>Projects examined but silent in this window.</summary>
    public int SilentProjects { get; set; }
    public string Note { get; set; } =
        "Counts come from the structured usage journal, which starts at each project's first "
        + "journalled turn. Earlier spend is visible in the ledger but has no token-level split, "
        + "so it cannot be re-priced here.";
}

/// <summary>
/// Read-only re-pricing of recorded Projects token usage. Answers "what would the fleet have cost
/// at these prices?" by replaying the usage journal into input / cache-read / output buckets per
/// project and per agent; the arithmetic itself belongs to the caller supplying the prices.
/// </summary>
public sealed class ProjectCostSimulatorService
{
    private readonly ProjectStore projects;
    private readonly ProjectTokenUsageStore usage;
    private readonly ProjectSubAgentManager agents;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<string, CostSimulationSnapshot> cache = new(StringComparer.Ordinal);

    public ProjectCostSimulatorService(
        ProjectStore projects,
        ProjectTokenUsageStore usage,
        ProjectSubAgentManager agents,
        Action<string>? log = null)
    {
        this.projects = projects;
        this.usage = usage;
        this.agents = agents;
        this.log = log ?? (_ => { });
    }

    public CostSimulationSnapshot Get(
        string? rangeKey,
        DateTime? utcNow = null,
        bool forceRefresh = false,
        string? fromUtc = null,
        string? toUtc = null,
        bool includeArchived = true)
    {
        if (forceRefresh) CacheDeps.MarkUncacheable("forced cost simulator refresh");
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var allProjects = projects.ListProjects();
        if (!includeArchived)
            allProjects = allProjects.Where(p => p.Status != ProjectStatus.Archived).ToList();
        DateTime earliest = allProjects.Count == 0
            ? now.Date
            : allProjects.Min(p => p.CreatedAt).ToUniversalTime();
        var range = ProjectAnalyticsCalculator.ResolveRange(rangeKey, earliest, now, fromUtc, toUtc);

        string cacheKey = ProjectAnalyticsService.SnapshotCacheKey(
            "cost-simulator:" + includeArchived, range);
        if (!forceRefresh
            && cache.TryGetValue(cacheKey, out var cached)
            && cached.GeneratedAt.ToUniversalTime() <= now
            && ProjectAnalyticsService.SnapshotTimeBucket(cached.GeneratedAt) ==
               ProjectAnalyticsService.SnapshotTimeBucket(now))
        {
            CacheDeps.NoteTimeBucket(ProjectAnalyticsService.SnapshotTtl);
            return cached;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new CostSimulationProject[allProjects.Count];
        // Every project's journal is its own file, so the scan parallelises cleanly. Bound it the
        // same way the portfolio build does rather than handing the whole box to one report.
        Parallel.For(0, allProjects.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
        }, index =>
        {
            var project = allProjects[index];
            try
            {
                rows[index] = ProjectCostSimulatorCalculator.BuildProject(
                    project,
                    usage.EnumerateRange(project.ProjectID, range.FromUtc, range.ToUtc),
                    agents.ListActive(project.ProjectID));
            }
            catch (Exception ex)
            {
                log($"Cost simulator could not read {project.ProjectID}: {ex.Message}");
                rows[index] = new CostSimulationProject
                {
                    ProjectID = project.ProjectID,
                    Name = project.Name,
                    Status = project.Status.ToString(),
                };
            }
        });

        var snapshot = ProjectCostSimulatorCalculator.BuildSnapshot(rows, range, now);
        stopwatch.Stop();
        snapshot.BuildDurationMs = stopwatch.ElapsedMilliseconds;
        if (!utcNow.HasValue) snapshot.GeneratedAt = DateTime.UtcNow;
        cache[cacheKey] = snapshot;
        TrimCache(now);
        if (!forceRefresh) CacheDeps.NoteTimeBucket(ProjectAnalyticsService.SnapshotTtl);
        return snapshot;
    }

    private void TrimCache(DateTime now)
    {
        if (cache.Count <= 64) return;
        DateTime staleBefore = now.AddMinutes(-10);
        foreach (var entry in cache)
            if (entry.Value.GeneratedAt < staleBefore)
                cache.TryRemove(entry.Key, out _);
    }
}

internal static class ProjectCostSimulatorCalculator
{
    internal static CostSimulationProject BuildProject(
        Project project,
        IEnumerable<ProjectTokenUsageRecord> usage,
        IReadOnlyList<ProjectAgentRecord> roster)
    {
        var row = new CostSimulationProject
        {
            ProjectID = project.ProjectID,
            Name = project.Name,
            Status = project.Status.ToString(),
        };
        var rosterByID = new Dictionary<string, ProjectAgentRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in roster) rosterByID[agent.AgentID] = agent;
        var byAgent = new Dictionary<string, CostSimulationAgent>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in usage)
        {
            string agentID = string.IsNullOrWhiteSpace(record.AgentID) ? "system" : record.AgentID;
            if (!byAgent.TryGetValue(agentID, out var agentRow))
            {
                rosterByID.TryGetValue(agentID, out var known);
                agentRow = new CostSimulationAgent
                {
                    AgentID = agentID,
                    Role = known?.Role ?? "",
                    Label = string.IsNullOrWhiteSpace(known?.Role) ? agentID : known!.Role,
                    OnRoster = known != null,
                    IsCommander = known != null
                        ? ProjectSubAgentManager.IsCommander(known)
                        : string.Equals(agentID, "commander", StringComparison.OrdinalIgnoreCase),
                };
                byAgent[agentID] = agentRow;
            }

            DateTime occurredAt = record.OccurredAt.ToUniversalTime();
            if (agentRow.LastActivityAt == null || occurredAt > agentRow.LastActivityAt)
                agentRow.LastActivityAt = occurredAt;
            if (row.LastActivityAt == null || occurredAt > row.LastActivityAt)
                row.LastActivityAt = occurredAt;

            // A reconciliation carries no tokens of its own — it only corrects the cost the
            // provisional entry already booked. Counting it as a request would double the turn.
            if (string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase))
            {
                agentRow.ActualCostUsd += record.CostUsd;
                row.ActualCostUsd += record.CostUsd;
                continue;
            }

            long cacheRead = record.CacheMetricsAvailable
                ? Math.Clamp(record.CachedPromptTokens, 0, Math.Max(0, record.PromptTokens))
                : 0;
            long input = Math.Max(0, record.PromptTokens - cacheRead);
            long cacheWrite = record.CacheMetricsAvailable
                ? Math.Clamp(record.CacheWritePromptTokens, 0, input)
                : 0;
            long output = Math.Max(0, record.CompletionTokens);

            Add(agentRow, input, cacheRead, cacheWrite, output, record);
            Add(row, input, cacheRead, cacheWrite, output, record);

            string model = string.IsNullOrWhiteSpace(record.Model) ? "unknown" : record.Model;
            if (!row.ModelTally.TryGetValue(model, out var modelRow))
                row.ModelTally[model] = modelRow = new CostSimulationModel { Key = model };
            modelRow.TotalTokens += input + cacheRead + output;
            if (input + cacheRead + output > 0) modelRow.Requests++;
        }

        row.Agents = byAgent.Values
            .OrderByDescending(a => a.TotalTokens)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return row;
    }

    private static void Add(
        CostSimulationBuckets bucket,
        long input,
        long cacheRead,
        long cacheWrite,
        long output,
        ProjectTokenUsageRecord record)
    {
        bucket.InputTokens += input;
        bucket.CacheReadTokens += cacheRead;
        bucket.CacheWriteTokens += cacheWrite;
        bucket.OutputTokens += output;
        bucket.TotalTokens += input + cacheRead + output;
        bucket.ActualCostUsd += record.CostUsd;
        if (input + cacheRead + output > 0) bucket.Requests++;
        if (!record.CacheMetricsAvailable && record.PromptTokens > 0)
        {
            bucket.UnmeasuredCachePromptTokens += record.PromptTokens;
            bucket.UnmeasuredCacheRequests++;
        }
    }

    internal static CostSimulationSnapshot BuildSnapshot(
        IReadOnlyList<CostSimulationProject> rows,
        AnalyticsRange range,
        DateTime now)
    {
        var totals = new CostSimulationBuckets();
        var models = new Dictionary<string, CostSimulationModel>(StringComparer.OrdinalIgnoreCase);
        var spending = new List<CostSimulationProject>();
        int silent = 0;

        foreach (var row in rows)
        {
            if (row == null) continue;
            if (row.TotalTokens <= 0 && row.Requests == 0 && Math.Abs(row.ActualCostUsd) < 0.0000001)
            {
                silent++;
                continue;
            }
            spending.Add(row);
            totals.InputTokens += row.InputTokens;
            totals.CacheReadTokens += row.CacheReadTokens;
            totals.CacheWriteTokens += row.CacheWriteTokens;
            totals.OutputTokens += row.OutputTokens;
            totals.TotalTokens += row.TotalTokens;
            totals.Requests += row.Requests;
            totals.ActualCostUsd += row.ActualCostUsd;
            totals.UnmeasuredCachePromptTokens += row.UnmeasuredCachePromptTokens;
            totals.UnmeasuredCacheRequests += row.UnmeasuredCacheRequests;
            foreach (var model in row.ModelTally.Values)
            {
                if (!models.TryGetValue(model.Key, out var merged))
                    models[model.Key] = merged = new CostSimulationModel { Key = model.Key };
                merged.TotalTokens += model.TotalTokens;
                merged.Requests += model.Requests;
            }
        }

        return new CostSimulationSnapshot
        {
            GeneratedAt = now,
            Range = range,
            Totals = totals,
            SilentProjects = silent,
            Projects = spending
                .OrderByDescending(p => p.TotalTokens)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Models = models.Values
                .OrderByDescending(m => m.TotalTokens)
                .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
