using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Provider-reported, per-call cache journal for interactive KliveAgent. No prompt content is stored.</summary>
public sealed class KliveAgentPromptCache
{
    private const string Version = "kliveagent-prefix-v1";
    private readonly string path;
    private readonly object gate = new();
    private long sequence;

    public KliveAgentPromptCache(string? path = null)
    {
        this.path = path ?? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory), "PromptCache", "usage.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
    }

    public void Record(string conversationId, int turnIndex, KliveLLM.KliveLLM.KliveLLMResponse response)
    {
        if (response.PromptTokens <= 0) return;
        var record = new ProjectTokenUsageRecord
        {
            UsageID = Guid.NewGuid().ToString("N"),
            ProjectID = "kliveagent",
            OccurredAt = DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            WakeID = conversationId,
            AgentID = "kliveagent",
            Source = "chat",
            Model = response.Model ?? "unknown",
            Provider = response.Provider ?? "unknown",
            RoutedProvider = response.RoutedProvider,
            PromptTokens = response.PromptTokens,
            CompletionTokens = response.CompletionTokens,
            CachedPromptTokens = response.CachedPromptTokens,
            CacheWritePromptTokens = response.CacheWritePromptTokens,
            CacheMetricsAvailable = response.CacheMetricsAvailable,
            TurnIndex = turnIndex,
            CacheEpochTurnIndex = response.CacheEpochTurnIndex > 0 ? response.CacheEpochTurnIndex : turnIndex,
            CacheEpochID = response.CacheEpochID ?? conversationId,
            CacheSessionID = conversationId,
            ContextWasCompacted = response.ContextWasCompacted,
            RequestDurationMs = response.RequestDurationMs,
            QueueDurationMs = response.QueueDurationMs,
            ProviderDurationMs = response.ProviderDurationMs,
            LatencyBreakdownAvailable = response.LatencyBreakdownAvailable,
            RouterStrategy = response.RouterStrategy,
            RouterAttempt = response.RouterAttempt,
            ResponseCacheStatus = response.ResponseCacheStatus,
            GenerationID = response.GenerationId,
            PromptCacheTelemetryVersion = Version,
        };
        lock (gate)
        {
            record.Sequence = ++sequence;
            File.AppendAllText(path, JsonConvert.SerializeObject(record) + Environment.NewLine);
        }
    }

    public AnalyticsPromptCacheSnapshot GetSnapshot(string? rangeKey, string? bucket = null)
    {
        List<ProjectTokenUsageRecord> records;
        lock (gate)
        {
            records = File.Exists(path)
                ? File.ReadLines(path).Select(line =>
                {
                    try { return JsonConvert.DeserializeObject<ProjectTokenUsageRecord>(line); }
                    catch { return null; }
                }).Where(x => x != null).Select(x => x!).ToList()
                : new();
        }
        var now = DateTime.UtcNow;
        var earliest = records.Count > 0 ? records.Min(x => x.OccurredAt) : now;
        var range = ProjectAnalyticsRange.Resolve(rangeKey, earliest, now, null, null, bucket);
        return ProjectPromptCacheAnalytics.Build(
            records.Where(x => x.OccurredAt >= range.FromUtc && x.OccurredAt <= range.ToUtc), range, Version);
    }
}
