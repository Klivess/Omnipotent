using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>
    /// One-time import of the Projects usage journals (Projects/AnalyticsUsage/*.usage.jsonl) into the
    /// ledger, so Projects has months of history on the day the ledger starts. Only turns from before
    /// the ledger's first live call are imported (later ones the ledger saw itself); reconciliation
    /// entries are folded into the turn they correct. Marked done in the ledger's meta table.
    /// </summary>
    public static class LlmProjectsImport
    {
        public const string MetaKey = "projects_import";

        public static async Task<int> RunAsync(LlmTelemetry ledger, string journalDirectory, Action<string> log, CancellationToken ct = default)
        {
            if (ledger.Db.GetMeta(MetaKey) != null) return 0;
            if (!Directory.Exists(journalDirectory))
            {
                ledger.Db.SetMeta(MetaKey, JsonConvert.SerializeObject(new { at = DateTime.UtcNow, imported = 0, note = "no journal directory" }));
                return 0;
            }
            long cutoff = ledger.FirstLiveMs;
            int imported = 0, files = 0;
            foreach (string file in Directory.EnumerateFiles(journalDirectory, "*.usage.jsonl"))
            {
                ct.ThrowIfCancellationRequested();
                files++;
                var usage = new Dictionary<string, ProjectTokenUsageRecord>(StringComparer.Ordinal);
                var adjustments = new List<ProjectTokenUsageRecord>();
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        ProjectTokenUsageRecord? record;
                        try { record = JsonConvert.DeserializeObject<ProjectTokenUsageRecord>(line); } catch { continue; }
                        if (record == null) continue;
                        if (string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)) adjustments.Add(record);
                        else if (record.PromptTokens > 0 || record.CompletionTokens > 0) usage[record.UsageID] = record;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"KliveLLM import: {Path.GetFileName(file)} could not be read ({ex.Message}).");
                    continue;
                }
                foreach (var adjustment in adjustments)
                    if (adjustment.ReconcilesUsageID != null && usage.TryGetValue(adjustment.ReconcilesUsageID, out var target))
                    {
                        target.CostUsd += adjustment.CostUsd;
                        target.CostBasis = "actual";
                    }

                foreach (var record in usage.Values)
                {
                    long ended = new DateTimeOffset(DateTime.SpecifyKind(record.OccurredAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    if (ended >= cutoff) continue;
                    ledger.Enqueue(ToCall(record, ended));
                    imported++;
                    // Let the writer keep up instead of filling the channel (a full channel drops).
                    if (imported % 2000 == 0) await ledger.FlushAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                }
            }
            await ledger.FlushAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            ledger.Db.SetMeta(MetaKey, JsonConvert.SerializeObject(new { at = DateTime.UtcNow, imported, files, cutoffMs = cutoff }));
            ledger.AddMark("import", $"Imported {imported:N0} Projects turns", $"From {files} usage journals, before the ledger started.");
            return imported;
        }

        internal static LlmCallRecord ToCall(ProjectTokenUsageRecord r, long endedMs)
        {
            string router = (r.Provider ?? "").ToLowerInvariant() switch
            {
                "airouter" => LlmRouterIds.AIRouter,
                "openrouter" => LlmRouterIds.OpenRouter,
                "customopenai" => LlmRouterIds.Custom,
                "huggingface" => LlmRouterIds.HuggingFace,
                "local" => LlmRouterIds.Local,
                _ => string.IsNullOrWhiteSpace(r.Provider) ? "unknown" : r.Provider!.ToLowerInvariant(),
            };
            string lane = (r.Source ?? "").ToLowerInvariant() switch
            {
                "commander" => "commander",
                "subagent" or "sub-agent" or "agent" or "worker" => "subagent",
                "council" => "council",
                "frontdesk" or "front-desk" or "instant-reply" => "frontdesk",
                _ => "utility",
            };
            bool flat = router == LlmRouterIds.AIRouter;
            var flags = LlmCallFlags.Imported;
            if (r.ContextWasCompacted) flags |= LlmCallFlags.Compacted;
            long total = r.RequestDurationMs > 0 ? r.RequestDurationMs : r.QueueDurationMs + r.ProviderDurationMs;
            return new LlmCallRecord
            {
                StartedMs = endedMs - total,
                EndedMs = endedMs,
                Service = "Projects",
                Lane = lane,
                Subject = r.ProjectID,
                Operation = r.Operation,
                SessionId = r.CacheSessionID,
                Kind = "tool",
                Router = router,
                Provider = r.RoutedProvider,
                ModelRequested = r.Model,
                ModelServed = r.Model,
                Outcome = LlmOutcome.Ok,
                Attempts = 1,
                PromptTokens = r.PromptTokens,
                CachedTokens = Math.Min(r.CachedPromptTokens, r.PromptTokens),
                CacheWriteTokens = r.CacheWritePromptTokens,
                CompletionTokens = r.CompletionTokens,
                CacheMetrics = r.CacheMetricsAvailable,
                // Only an authoritative figure counts as reported; a provisional estimate is re-costed by the price book.
                CostUsd = !flat && string.Equals(r.CostBasis, "actual", StringComparison.OrdinalIgnoreCase) ? r.CostUsd : null,
                UpstreamCostUsd = r.UpstreamInferenceCostUsd,
                Billing = flat ? "flat" : "per_token",
                QueueMs = r.QueueDurationMs,
                ProviderMs = r.ProviderDurationMs,
                TotalMs = total,
                Flags = flags,
                CacheEpoch = r.CacheEpochID,
                CacheEpochTurn = r.CacheEpochTurnIndex,
            };
        }
    }
}
