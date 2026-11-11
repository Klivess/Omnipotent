using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Telemetry;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using System.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Provider-reported, per-call cache journal for interactive KliveAgent. No prompt content is stored.</summary>
/// <remarks>
/// The journal is parsed once per process and then only the lines appended since the last read are
/// parsed. Until 2026-11-11 every read of the statistics re-read and re-parsed the whole file under
/// the journal lock: on the server that was 11–21 s per page view (7,325 lines) whatever range was
/// asked for, while the same file parses in about 0.2 s on a quiet machine. The spans name the two
/// steps so the cost of the first read after a start stays visible in telemetry.
/// </remarks>
public sealed class KliveAgentPromptCache
{
    private const string Version = "kliveagent-prefix-v1";
    private readonly string path;
    private readonly object gate = new();
    private readonly List<ProjectTokenUsageRecord> journal = new();
    /// <summary>Bytes of the file already parsed into <see cref="journal"/>.</summary>
    private long consumedBytes;
    private long sequence;

    public KliveAgentPromptCache(string? path = null)
    {
        this.path = path ?? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory), "PromptCache", "usage.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
    }

    /// <summary>How many lines a read has parsed in this process; lets tests see that a second read parses nothing new.</summary>
    internal long ParsedLines { get; private set; }

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
        using (RequestTrace.AmbientSpan("prompt-cache-read"))
        {
            lock (gate)
            {
                CatchUpLocked();
                records = new List<ProjectTokenUsageRecord>(journal);
            }
        }
        using (RequestTrace.AmbientSpan("prompt-cache-build"))
        {
            var now = DateTime.UtcNow;
            var earliest = records.Count > 0 ? records.Min(x => x.OccurredAt) : now;
            var range = ProjectAnalyticsRange.Resolve(rangeKey, earliest, now, null, null, bucket);
            return ProjectPromptCacheAnalytics.Build(
                records.Where(x => x.OccurredAt >= range.FromUtc && x.OccurredAt <= range.ToUtc), range, Version);
        }
    }

    /// <summary>Parses the journal once in the background, so the first page view after a start is quick too.</summary>
    public void Warm()
    {
        _ = Task.Run(() =>
        {
            try { lock (gate) CatchUpLocked(); }
            catch { /* the next read tries again */ }
        });
    }

    /// <summary>Parses whatever was appended since the last read. Only whole lines are consumed, so a line
    /// another process is still writing is read next time. A file that shrank was replaced: read it again.</summary>
    private void CatchUpLocked()
    {
        if (!File.Exists(path))
        {
            journal.Clear();
            consumedBytes = 0;
            return;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (stream.Length < consumedBytes)
        {
            journal.Clear();
            consumedBytes = 0;
        }
        if (stream.Length == consumedBytes) return;

        stream.Seek(consumedBytes, SeekOrigin.Begin);
        var tail = new byte[stream.Length - consumedBytes];
        int read = 0;
        while (read < tail.Length)
        {
            int n = stream.Read(tail, read, tail.Length - read);
            if (n <= 0) break;
            read += n;
        }
        int end = Array.LastIndexOf(tail, (byte)'\n', Math.Max(0, read - 1));
        if (end < 0) return;

        int start = consumedBytes == 0 && read >= 3 && tail[0] == 0xEF && tail[1] == 0xBB && tail[2] == 0xBF ? 3 : 0;
        string text = Encoding.UTF8.GetString(tail, start, end + 1 - start);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            ParsedLines++;
            try
            {
                var record = JsonConvert.DeserializeObject<ProjectTokenUsageRecord>(trimmed);
                if (record == null) continue;
                journal.Add(record);
                if (record.Sequence > sequence) sequence = record.Sequence;
            }
            catch { /* a damaged line is skipped, as before */ }
        }
        consumedBytes += end + 1;
    }
}
