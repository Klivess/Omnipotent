using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Attribution supplied by a caller when an LLM turn is booked to the project ledger.
/// The ledger remains the authority for cumulative totals; this context makes the same
/// spend useful for time-series, model, wake and agent analytics.
/// </summary>
public sealed class ProjectTokenUsageContext
{
    public DateTime? OccurredAt { get; set; }
    public string? WakeID { get; set; }
    public string? AgentID { get; set; }
    public string Source { get; set; } = "unknown";
    public string? Operation { get; set; }
    public string? Model { get; set; }
    public string? SourceReference { get; set; }
    public string? Label { get; set; }
}

/// <summary>One immutable token-spend or cost-reconciliation journal entry.</summary>
public sealed class ProjectTokenUsageRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string RecordKind { get; set; } = "usage";
    public long Sequence { get; set; }
    public string UsageID { get; set; } = "";
    public string ProjectID { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? WakeID { get; set; }
    public string AgentID { get; set; } = "system";
    public string Source { get; set; } = "unknown";
    public string? Operation { get; set; }
    public string Model { get; set; } = "unknown";
    public string? SourceReference { get; set; }
    public string? Label { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    /// <summary>
    /// Effective USD booked by this entry. Reconciliation entries may be negative so
    /// the sum of the provisional record and its adjustment equals the provider truth.
    /// </summary>
    public double CostUsd { get; set; }
    public string CostBasis { get; set; } = "unknown";
    public string? GenerationID { get; set; }
    public string? ReconcilesUsageID { get; set; }
}

/// <summary>
/// Append-only per-project JSONL journal for structured LLM usage attribution.
/// Layout: Projects/AnalyticsUsage/&lt;projectID&gt;.usage.jsonl.
/// </summary>
public sealed class ProjectTokenUsageStore
{
    private readonly string root;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> sequenceCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> cutoverCache = new(StringComparer.Ordinal);

    private static string CacheKey(string projectID) => "projects:token-usage:" + projectID;
    private const string CacheKeyAll = "projects:token-usage";

    public ProjectTokenUsageStore(Action<string> log)
    {
        this.log = log ?? (_ => { });
        root = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "AnalyticsUsage");
        Directory.CreateDirectory(root);
    }

    private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
    private string PathFor(string projectID) => Path.Combine(root, projectID + ".usage.jsonl");

    public ProjectTokenUsageRecord? TryAppend(ProjectTokenUsageRecord record)
    {
        try
        {
            if (record == null || string.IsNullOrWhiteSpace(record.ProjectID))
                throw new ArgumentException("token usage requires ProjectID");

            lock (LockFor(record.ProjectID))
            {
                record.Sequence = GetLastSequenceLocked(record.ProjectID) + 1;
                if (string.IsNullOrWhiteSpace(record.UsageID))
                    record.UsageID = Guid.NewGuid().ToString("N");
                record.OccurredAt = NormalizeUtc(record.OccurredAt == default ? DateTime.UtcNow : record.OccurredAt);
                record.RecordedAt = NormalizeUtc(record.RecordedAt == default ? DateTime.UtcNow : record.RecordedAt);
                record.AgentID = Clean(record.AgentID, 160, "system");
                record.Source = Clean(record.Source, 80, "unknown");
                record.RecordKind = Clean(record.RecordKind, 40, "usage");
                record.Operation = CleanNullable(record.Operation, 120);
                record.Model = Clean(record.Model, 240, "unknown");
                record.SourceReference = CleanNullable(record.SourceReference, 240);
                record.Label = CleanNullable(record.Label, 240);
                record.GenerationID = CleanNullable(record.GenerationID, 240);
                record.ReconcilesUsageID = CleanNullable(record.ReconcilesUsageID, 64);
                record.PromptTokens = Math.Max(0, record.PromptTokens);
                record.CompletionTokens = Math.Max(0, record.CompletionTokens);
                if (!double.IsFinite(record.CostUsd)) record.CostUsd = 0;

                string json = JsonConvert.SerializeObject(record, Formatting.None);
                string path = PathFor(record.ProjectID);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
                sequenceCache[record.ProjectID] = record.Sequence;
                if (IsUsageCharge(record))
                {
                    cutoverCache.TryAdd(record.ProjectID, record.OccurredAt);
                }
                CacheDeps.Bump(CacheKey(record.ProjectID));
                CacheDeps.Bump(CacheKeyAll);
                return record;
            }
        }
        catch (Exception ex)
        {
            log($"Project token-usage journal append failed for {record?.ProjectID ?? "unknown"}: {ex.Message}");
            return null;
        }
    }

    public long GetLastSequence(string projectID)
    {
        CacheDeps.NoteRead(CacheKey(projectID));
        lock (LockFor(projectID)) return GetLastSequenceLocked(projectID);
    }

    public IEnumerable<ProjectTokenUsageRecord> EnumerateRange(
        string projectID,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        CacheDeps.NoteRead(CacheKey(projectID));
        string path = PathFor(projectID);
        if (!File.Exists(path)) yield break;
        DateTime? from = fromUtc?.ToUniversalTime();
        DateTime? to = toUtc?.ToUniversalTime();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ProjectTokenUsageRecord? record;
            try { record = JsonConvert.DeserializeObject<ProjectTokenUsageRecord>(line); }
            catch { continue; }
            if (record == null) continue;
            DateTime occurredAt = NormalizeUtc(record.OccurredAt);
            if (from.HasValue && occurredAt < from.Value) continue;
            if (to.HasValue && occurredAt > to.Value) continue;
            record.OccurredAt = occurredAt;
            yield return record;
        }
    }

    public ProjectTokenUsageRecord? FindProvisionalByGeneration(
        string projectID,
        string generationID)
    {
        if (string.IsNullOrWhiteSpace(generationID)) return null;
        return EnumerateRange(projectID, null, null)
            .Where(record =>
                string.Equals(record.GenerationID, generationID, StringComparison.Ordinal)
                && string.Equals(record.CostBasis, "provisional", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.Sequence)
            .FirstOrDefault();
    }

    /// <summary>
    /// First structured charge for a project. Analytics use this durable cutover to avoid
    /// combining post-journal records with historical prose/diagnostic usage fallback.
    /// </summary>
    public DateTime? GetCutoverUtc(string projectID)
    {
        CacheDeps.NoteRead(CacheKey(projectID));
        if (cutoverCache.TryGetValue(projectID, out DateTime cached)) return cached;

        DateTime? earliest = EnumerateRange(projectID, null, null)
            .Where(IsUsageCharge)
            .OrderBy(record => record.Sequence)
            .Select(record => (DateTime?)record.OccurredAt.ToUniversalTime())
            .FirstOrDefault();
        if (earliest.HasValue) cutoverCache[projectID] = earliest.Value;
        return earliest;
    }

    private long GetLastSequenceLocked(string projectID)
    {
        if (sequenceCache.TryGetValue(projectID, out long cached)) return cached;
        long last = 0;
        string path = PathFor(projectID);
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonConvert.DeserializeObject<ProjectTokenUsageRecord>(line);
                    if (record != null && record.Sequence > last) last = record.Sequence;
                }
                catch { }
            }
        }
        sequenceCache[projectID] = last;
        return last;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsUsageCharge(ProjectTokenUsageRecord record)
        => !string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)
            && (record.PromptTokens > 0 || record.CompletionTokens > 0);

    private static string Clean(string? value, int maxLength, string fallback)
    {
        string clean = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string clean = value.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
