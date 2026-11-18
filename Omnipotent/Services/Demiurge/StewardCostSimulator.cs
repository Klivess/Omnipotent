using Newtonsoft.Json;

namespace Omnipotent.Services.Demiurge;

/// <summary>
/// Token buckets for one scope (fleet, Steward or agent), split exactly the way an LLM provider
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

public sealed class CostSimulationSteward : CostSimulationBuckets
{
    public string StewardID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? LastActivityAt { get; set; }
    public List<CostSimulationAgent> Agents { get; set; } = new();

    /// <summary>Per-model tallies, merged into the fleet list rather than served per Steward.</summary>
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
    /// <summary>Only Stewards that booked tokens in the window, heaviest first.</summary>
    public List<CostSimulationSteward> Stewards { get; set; } = new();
    public List<CostSimulationModel> Models { get; set; } = new();
    /// <summary>Demiurge examined but silent in this window.</summary>
    public int SilentStewards { get; set; }
    public string Note { get; set; } =
        "Counts come from the structured usage journal, which starts at each Steward's first "
        + "journalled turn. Earlier spend is visible in the ledger but has no token-level split, "
        + "so it cannot be re-priced here.";
}

/// <summary>
/// Read-only re-pricing of recorded Demiurge token usage. Answers "what would the fleet have cost
/// at these prices?" by replaying the usage journal into input / cache-read / output buckets per
/// Steward and per agent; the arithmetic itself belongs to the caller supplying the prices.
/// </summary>
public sealed class StewardCostSimulatorService
{
    internal static readonly string[] DefaultRanges =
        ["1h", "6h", "24h", "7d", "30d", "90d", "365d", "all"];
    private readonly StewardStore stewards;
    private readonly StewardTokenUsageStore usage;
    private readonly StewardSubAgentManager agents;

    public StewardCostSimulatorService(
        StewardStore stewards,
        StewardTokenUsageStore usage,
        StewardSubAgentManager agents)
    {
        this.stewards = stewards;
        this.usage = usage;
        this.agents = agents;
    }

    /// <summary>One journal read per Steward builds every standard range. Only background workers call this.</summary>
    internal IReadOnlyDictionary<string, CostSimulationSnapshot> BuildDefaultSnapshots()
        => BuildDefaultSnapshots(stewards.ListStewards(),
            (stewardID, from, to) => usage.EnumerateRange(stewardID, from, to),
            stewardID => agents.ListActive(stewardID), DateTime.UtcNow);

    internal static IReadOnlyDictionary<string, CostSimulationSnapshot> BuildDefaultSnapshots(
        IReadOnlyList<Steward> allStewards,
        Func<string, DateTime, DateTime, IEnumerable<StewardTokenUsageRecord>> enumerateUsage,
        Func<string, IReadOnlyList<StewardAgentRecord>> listAgents,
        DateTime now)
    {
        DateTime earliest = allStewards.Count == 0
            ? now.Date
            : allStewards.Min(p => p.CreatedAt).ToUniversalTime();
        var activeStewards = allStewards.Where(p => p.Status != StewardStatus.Archived).ToList();
        DateTime activeEarliest = activeStewards.Count == 0
            ? now.Date : activeStewards.Min(p => p.CreatedAt).ToUniversalTime();
        var ranges = DefaultRanges.Select(key => StewardAnalyticsCalculator.ResolveRange(key, earliest, now)).ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = ranges.Select(_ => new CostSimulationSteward[allStewards.Count]).ToArray();
        // The usage store streams a complete JSONL journal to reach any window. Parse it once,
        // then feed the bounded set of range accumulators in memory.
        Parallel.For(0, allStewards.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(2, Environment.ProcessorCount)),
        }, index =>
        {
            var steward = allStewards[index];
            var roster = listAgents(steward.StewardID);
            var accumulators = ranges.Select(_ =>
                new StewardCostSimulatorCalculator.StewardAccumulator(steward, roster)).ToArray();
            foreach (var record in enumerateUsage(steward.StewardID, earliest, now))
            {
                DateTime occurredAt = record.OccurredAt.ToUniversalTime();
                for (int r = 0; r < ranges.Length; r++)
                    if (occurredAt >= ranges[r].FromUtc && occurredAt <= ranges[r].ToUtc)
                        accumulators[r].Add(record);
            }
            for (int r = 0; r < ranges.Length; r++) rows[r][index] = accumulators[r].Complete();
        });
        stopwatch.Stop();
        var result = new Dictionary<string, CostSimulationSnapshot>(StringComparer.Ordinal);
        for (int r = 0; r < ranges.Length; r++)
        {
            string key = DefaultRanges[r];
            var all = StewardCostSimulatorCalculator.BuildSnapshot(rows[r], ranges[r], now);
            all.BuildDurationMs = stopwatch.ElapsedMilliseconds;
            result[StewardCostSimulatorMaterializer.Key(key, true)] = all;

            var activeRange = key == "all"
                ? StewardAnalyticsCalculator.ResolveRange(key, activeEarliest, now) : ranges[r];
            var active = StewardCostSimulatorCalculator.BuildSnapshot(
                rows[r].Where(row => row.Status != StewardStatus.Archived.ToString()).ToArray(),
                activeRange, now);
            active.BuildDurationMs = stopwatch.ElapsedMilliseconds;
            result[StewardCostSimulatorMaterializer.Key(key, false)] = active;
        }
        return result;
    }

    /// <summary>Custom windows are queued and built by one bounded background worker.</summary>
    internal CostSimulationSnapshot BuildCustom(AnalyticsRange range, bool includeArchived)
    {
        DateTime now = DateTime.UtcNow;
        var allStewards = stewards.ListStewards();
        if (!includeArchived)
            allStewards = allStewards.Where(p => p.Status != StewardStatus.Archived).ToList();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new CostSimulationSteward[allStewards.Count];
        Parallel.For(0, allStewards.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(2, Environment.ProcessorCount)),
        }, index =>
        {
            var steward = allStewards[index];
            rows[index] = StewardCostSimulatorCalculator.BuildSteward(steward,
                usage.EnumerateRange(steward.StewardID, range.FromUtc, range.ToUtc),
                agents.ListActive(steward.StewardID));
        });
        stopwatch.Stop();
        var snapshot = StewardCostSimulatorCalculator.BuildSnapshot(rows, range, now);
        snapshot.BuildDurationMs = stopwatch.ElapsedMilliseconds;
        return snapshot;
    }
}

internal static class StewardCostSimulatorCalculator
{
    internal static CostSimulationSteward BuildSteward(
        Steward steward,
        IEnumerable<StewardTokenUsageRecord> usage,
        IReadOnlyList<StewardAgentRecord> roster)
    {
        var accumulator = new StewardAccumulator(steward, roster);
        foreach (var record in usage) accumulator.Add(record);
        return accumulator.Complete();
    }

    internal sealed class StewardAccumulator
    {
        private readonly CostSimulationSteward row;
        private readonly Dictionary<string, StewardAgentRecord> rosterByID =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CostSimulationAgent> byAgent =
            new(StringComparer.OrdinalIgnoreCase);

        internal StewardAccumulator(Steward steward, IReadOnlyList<StewardAgentRecord> roster)
        {
            row = new CostSimulationSteward
            {
                StewardID = steward.StewardID,
                Name = steward.Name,
                Status = steward.Status.ToString(),
            };
            foreach (var agent in roster) rosterByID[agent.AgentID] = agent;
        }

        internal void Add(StewardTokenUsageRecord record)
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
                        ? StewardSubAgentManager.IsCommander(known)
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
                return;
            }

            long cacheRead = record.CacheMetricsAvailable
                ? Math.Clamp(record.CachedPromptTokens, 0, Math.Max(0, record.PromptTokens))
                : 0;
            long input = Math.Max(0, record.PromptTokens - cacheRead);
            long cacheWrite = record.CacheMetricsAvailable
                ? Math.Clamp(record.CacheWritePromptTokens, 0, input)
                : 0;
            long output = Math.Max(0, record.CompletionTokens);

            StewardCostSimulatorCalculator.Add(agentRow, input, cacheRead, cacheWrite, output, record);
            StewardCostSimulatorCalculator.Add(row, input, cacheRead, cacheWrite, output, record);

            string model = string.IsNullOrWhiteSpace(record.Model) ? "unknown" : record.Model;
            if (!row.ModelTally.TryGetValue(model, out var modelRow))
                row.ModelTally[model] = modelRow = new CostSimulationModel { Key = model };
            modelRow.TotalTokens += input + cacheRead + output;
            if (input + cacheRead + output > 0) modelRow.Requests++;
        }

        internal CostSimulationSteward Complete()
        {
            row.Agents = byAgent.Values
                .OrderByDescending(a => a.TotalTokens)
                .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return row;
        }
    }

    private static void Add(
        CostSimulationBuckets bucket,
        long input,
        long cacheRead,
        long cacheWrite,
        long output,
        StewardTokenUsageRecord record)
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
        IReadOnlyList<CostSimulationSteward> rows,
        AnalyticsRange range,
        DateTime now)
    {
        var totals = new CostSimulationBuckets();
        var models = new Dictionary<string, CostSimulationModel>(StringComparer.OrdinalIgnoreCase);
        var spending = new List<CostSimulationSteward>();
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
            SilentStewards = silent,
            Stewards = spending
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
