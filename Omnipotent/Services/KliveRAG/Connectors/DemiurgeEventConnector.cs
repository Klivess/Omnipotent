using Omnipotent.Services.Demiurge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Indexes Demiurge's per-Steward event logs so agents gain cross-Steward institutional memory
    /// (today each Steward only BM25-searches its own log). Live freshness comes from the
    /// <see cref="StewardEventLogStore.EventAppended"/> push; a sequence-watermark catch-up pass
    /// backfills history and repairs anything the push missed. A per-Steward standing-digest doc is
    /// refreshed alongside. Tool-call / tool-result / bookkeeping noise is skipped — the useful
    /// signal is thoughts, messages and outcomes.
    /// </summary>
    public sealed class DemiurgeEventConnector : RagConnector
    {
        public override string Name => RagSource.DemiurgeEvents;

        private static readonly HashSet<string> IndexedTypes = new(StringComparer.Ordinal)
        {
            StewardEventTypes.CommanderThought, StewardEventTypes.AgentThought,
            StewardEventTypes.CommanderMessage, StewardEventTypes.KlivesMessage, StewardEventTypes.AgentMessage,
            StewardEventTypes.Status, StewardEventTypes.ApprovalRequested, StewardEventTypes.ApprovalResolved,
            StewardEventTypes.BudgetWarning, StewardEventTypes.BudgetPaused, StewardEventTypes.MoneySpent,
            StewardEventTypes.WatchdogRecovery, StewardEventTypes.WatchdogReminder,
            StewardEventTypes.WatchdogEscalation, StewardEventTypes.WakeFailed,
        };

        private const int MinTextChars = 24;

        /// <summary>Per-Steward watermark key. Spelled as it was before Projects became Demiurge: a new
        /// key would restart every Steward's catch-up from the first event and re-embed all of it.</summary>
        private static string CursorKey(string stewardID) => $"projects:{stewardID}";
        private const int BackfillPage = 2000;

        private readonly Func<Task<Demiurge.Demiurge?>> resolveDemiurge;
        private int subscribed;

        public DemiurgeEventConnector(RagIndexWriter writer, Action<string> log, Func<Task<Demiurge.Demiurge?>> resolveDemiurge)
            : base(writer, log)
        {
            this.resolveDemiurge = resolveDemiurge;
        }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            var demiurge = await resolveDemiurge();
            if (demiurge?.EventLog == null) return; // Demiurge not up yet — retried next pass

            // Wire the live push exactly once.
            if (Interlocked.CompareExchange(ref subscribed, 1, 0) == 0)
                demiurge.EventLog.EventAppended += OnEventAppended;

            foreach (var pid in demiurge.EventLog.AllStewardIDsWithLogs())
            {
                ct.ThrowIfCancellationRequested();
                await CatchUpStewardAsync(demiurge, pid, ct);
                await RefreshDigestDocAsync(demiurge, pid, ct);
            }
        }

        private async Task CatchUpStewardAsync(Demiurge.Demiurge demiurge, string pid, CancellationToken ct)
        {
            string cursorKey = CursorKey(pid);
            long watermark = long.TryParse(GetCursor(cursorKey), out var w) ? w : 0;
            string? stewardName = SafeStewardName(demiurge, pid);

            while (!ct.IsCancellationRequested)
            {
                var batch = demiurge.EventLog.ReadSince(pid, watermark, BackfillPage);
                if (batch.Count == 0) break;
                foreach (var evt in batch)
                {
                    if (IndexedTypes.Contains(evt.Type))
                        await UpsertEventAsync(evt, stewardName, ct);
                    watermark = Math.Max(watermark, evt.Sequence);
                }
                await SetCursorAsync(cursorKey, watermark.ToString(), ct);
                if (batch.Count < BackfillPage) break;
            }
        }

        private void OnEventAppended(StewardEvent evt)
        {
            if (evt == null || !IndexedTypes.Contains(evt.Type)) return;
            // Fire-and-forget: the writer serialises on its WriteLock, so concurrent live upserts are safe.
            _ = Task.Run(async () =>
            {
                try
                {
                    var demiurge = await resolveDemiurge();
                    string? name = demiurge != null ? SafeStewardName(demiurge, evt.StewardID) : null;
                    await UpsertEventAsync(evt, name, CancellationToken.None);
                    // Keep the watermark ahead of live events so catch-up doesn't reprocess them.
                    await SetCursorAsync(CursorKey(evt.StewardID), evt.Sequence.ToString(), CancellationToken.None);
                }
                catch (Exception ex) { Log($"[KliveRAG] live steward-event ingest failed: {ex.Message}"); }
            });
        }

        private Task UpsertEventAsync(StewardEvent evt, string? stewardName, CancellationToken ct)
        {
            string text = ComposeText(evt);
            if (text.Length < MinTextChars) return Task.CompletedTask;

            var doc = new RagDocument
            {
                DocId = $"projevt:{evt.StewardID}:{evt.Sequence}",
                Source = RagSource.DemiurgeEvents,
                Title = $"{evt.Type}{(stewardName != null ? " · " + stewardName : "")}",
                Uri = $"steward:{evt.StewardID}",
                Content = text,
                ContentHash = RagChunker.Hash(text),
                CreatedAtUnixMs = RagTime.ToUnixMs(evt.Timestamp),
                SingleChunk = true,
                MetaJson = $"{{\"stewardId\":\"{evt.StewardID}\",\"type\":\"{evt.Type}\"}}",
            };
            return Writer.UpsertAsync(doc, ct);
        }

        private async Task RefreshDigestDocAsync(Demiurge.Demiurge demiurge, string pid, CancellationToken ct)
        {
            StewardDigest? digest;
            try { digest = demiurge.Digests.GetDigest(pid); } catch { return; }
            if (digest == null) return;

            var parts = new[] { digest.CurrentPlan, digest.OrgChart, digest.OpenThreads, digest.RollingSummary }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            string text = string.Join("\n\n", parts).Trim();
            if (text.Length < MinTextChars) return;

            string? name = SafeStewardName(demiurge, pid);
            var doc = new RagDocument
            {
                DocId = $"projdigest:{pid}",
                Source = RagSource.DemiurgeDigests,
                Title = $"Steward digest{(name != null ? " · " + name : "")}",
                Uri = $"steward:{pid}",
                Content = text,
                ContentHash = RagChunker.Hash(text),
                CreatedAtUnixMs = RagTime.ToUnixMs(digest.UpdatedAt),
                MetaJson = $"{{\"stewardId\":\"{pid}\"}}",
            };
            await Writer.UpsertAsync(doc, ct);
        }

        private static string? SafeStewardName(Demiurge.Demiurge demiurge, string pid)
        {
            try { return demiurge.Store?.GetSteward(pid)?.Name; } catch { return null; }
        }

        // Mirror of StewardRetrievalIndex.ComposeText: what a human would search for.
        private static string ComposeText(StewardEvent evt)
        {
            var parts = new List<string> { evt.Author, evt.Text };
            if (!string.IsNullOrWhiteSpace(evt.ToolName)) parts.Add(evt.ToolName);
            if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
                parts.Add(evt.PayloadJson!.Length > 600 ? evt.PayloadJson.Substring(0, 600) : evt.PayloadJson);
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
        }
    }
}
