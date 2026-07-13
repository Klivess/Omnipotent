using Omnipotent.Services.Projects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Indexes Projects' per-project event logs so agents gain cross-project institutional memory
    /// (today each project only BM25-searches its own log). Live freshness comes from the
    /// <see cref="ProjectEventLogStore.EventAppended"/> push; a sequence-watermark catch-up pass
    /// backfills history and repairs anything the push missed. A per-project standing-digest doc is
    /// refreshed alongside. Tool-call / tool-result / bookkeeping noise is skipped — the useful
    /// signal is thoughts, messages and outcomes.
    /// </summary>
    public sealed class ProjectsEventConnector : RagConnector
    {
        public override string Name => RagSource.ProjectsEvents;

        private static readonly HashSet<string> IndexedTypes = new(StringComparer.Ordinal)
        {
            ProjectEventTypes.CommanderThought, ProjectEventTypes.AgentThought,
            ProjectEventTypes.CommanderMessage, ProjectEventTypes.KlivesMessage, ProjectEventTypes.AgentMessage,
            ProjectEventTypes.Status, ProjectEventTypes.ApprovalRequested, ProjectEventTypes.ApprovalResolved,
            ProjectEventTypes.BudgetWarning, ProjectEventTypes.BudgetPaused, ProjectEventTypes.MoneySpent,
            ProjectEventTypes.WatchdogRecovery, ProjectEventTypes.WatchdogReminder,
            ProjectEventTypes.WatchdogEscalation, ProjectEventTypes.WakeFailed,
        };

        private const int MinTextChars = 24;
        private const int BackfillPage = 2000;

        private readonly Func<Task<Projects.Projects?>> resolveProjects;
        private int subscribed;

        public ProjectsEventConnector(RagIndexWriter writer, Action<string> log, Func<Task<Projects.Projects?>> resolveProjects)
            : base(writer, log)
        {
            this.resolveProjects = resolveProjects;
        }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            var projects = await resolveProjects();
            if (projects?.EventLog == null) return; // Projects not up yet — retried next pass

            // Wire the live push exactly once.
            if (Interlocked.CompareExchange(ref subscribed, 1, 0) == 0)
                projects.EventLog.EventAppended += OnEventAppended;

            foreach (var pid in projects.EventLog.AllProjectIDsWithLogs())
            {
                ct.ThrowIfCancellationRequested();
                await CatchUpProjectAsync(projects, pid, ct);
                await RefreshDigestDocAsync(projects, pid, ct);
            }
        }

        private async Task CatchUpProjectAsync(Projects.Projects projects, string pid, CancellationToken ct)
        {
            string cursorKey = $"projects:{pid}";
            long watermark = long.TryParse(GetCursor(cursorKey), out var w) ? w : 0;
            string? projectName = SafeProjectName(projects, pid);

            while (!ct.IsCancellationRequested)
            {
                var batch = projects.EventLog.ReadSince(pid, watermark, BackfillPage);
                if (batch.Count == 0) break;
                foreach (var evt in batch)
                {
                    if (IndexedTypes.Contains(evt.Type))
                        await UpsertEventAsync(evt, projectName, ct);
                    watermark = Math.Max(watermark, evt.Sequence);
                }
                await SetCursorAsync(cursorKey, watermark.ToString(), ct);
                if (batch.Count < BackfillPage) break;
            }
        }

        private void OnEventAppended(ProjectEvent evt)
        {
            if (evt == null || !IndexedTypes.Contains(evt.Type)) return;
            // Fire-and-forget: the writer serialises on its WriteLock, so concurrent live upserts are safe.
            _ = Task.Run(async () =>
            {
                try
                {
                    var projects = await resolveProjects();
                    string? name = projects != null ? SafeProjectName(projects, evt.ProjectID) : null;
                    await UpsertEventAsync(evt, name, CancellationToken.None);
                    // Keep the watermark ahead of live events so catch-up doesn't reprocess them.
                    await SetCursorAsync($"projects:{evt.ProjectID}", evt.Sequence.ToString(), CancellationToken.None);
                }
                catch (Exception ex) { Log($"[KliveRAG] live project-event ingest failed: {ex.Message}"); }
            });
        }

        private Task UpsertEventAsync(ProjectEvent evt, string? projectName, CancellationToken ct)
        {
            string text = ComposeText(evt);
            if (text.Length < MinTextChars) return Task.CompletedTask;

            var doc = new RagDocument
            {
                DocId = $"projevt:{evt.ProjectID}:{evt.Sequence}",
                Source = RagSource.ProjectsEvents,
                Title = $"{evt.Type}{(projectName != null ? " · " + projectName : "")}",
                Uri = $"project:{evt.ProjectID}",
                Content = text,
                ContentHash = RagChunker.Hash(text),
                CreatedAtUnixMs = RagTime.ToUnixMs(evt.Timestamp),
                SingleChunk = true,
                MetaJson = $"{{\"projectId\":\"{evt.ProjectID}\",\"type\":\"{evt.Type}\"}}",
            };
            return Writer.UpsertAsync(doc, ct);
        }

        private async Task RefreshDigestDocAsync(Projects.Projects projects, string pid, CancellationToken ct)
        {
            ProjectDigest? digest;
            try { digest = projects.Digests.GetDigest(pid); } catch { return; }
            if (digest == null) return;

            var parts = new[] { digest.CurrentPlan, digest.OrgChart, digest.OpenThreads, digest.RollingSummary }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            string text = string.Join("\n\n", parts).Trim();
            if (text.Length < MinTextChars) return;

            string? name = SafeProjectName(projects, pid);
            var doc = new RagDocument
            {
                DocId = $"projdigest:{pid}",
                Source = RagSource.ProjectsDigests,
                Title = $"Project digest{(name != null ? " · " + name : "")}",
                Uri = $"project:{pid}",
                Content = text,
                ContentHash = RagChunker.Hash(text),
                CreatedAtUnixMs = RagTime.ToUnixMs(digest.UpdatedAt),
                MetaJson = $"{{\"projectId\":\"{pid}\"}}",
            };
            await Writer.UpsertAsync(doc, ct);
        }

        private static string? SafeProjectName(Projects.Projects projects, string pid)
        {
            try { return projects.Store?.GetProject(pid)?.Name; } catch { return null; }
        }

        // Mirror of ProjectRetrievalIndex.ComposeText: what a human would search for.
        private static string ComposeText(ProjectEvent evt)
        {
            var parts = new List<string> { evt.Author, evt.Text };
            if (!string.IsNullOrWhiteSpace(evt.ToolName)) parts.Add(evt.ToolName);
            if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
                parts.Add(evt.PayloadJson!.Length > 600 ? evt.PayloadJson.Substring(0, 600) : evt.PayloadJson);
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
        }
    }
}
