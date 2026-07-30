namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// The rehydrate-on-wake assembler (§7). The Commander has NO persistent conversation:
    /// each wake rebuilds its context from the standing digest + budget-fitted recent events +
    /// BM25 retrieval into the full log, so a process restart is indistinguishable from a wake.
    /// Phase 3's ProjectCommanderRunner calls <see cref="BuildWakeSeed"/> and hands the result
    /// to a fresh KliveLLM tool session; nothing survives between wakes except what's on disk.
    /// </summary>
    public class ProjectWakeCycle
    {
        private readonly ProjectEventLogStore eventLog;
        private readonly ProjectDigestStore digests;
        private readonly ProjectRetrievalIndex retrieval;

        public ProjectWakeCycle(ProjectEventLogStore eventLog, ProjectDigestStore digests, ProjectRetrievalIndex retrieval)
        {
            this.eventLog = eventLog;
            this.digests = digests;
            this.retrieval = retrieval;
        }

        /// <summary>
        /// Optional cross-system knowledge leg (KliveRAG): (query, excludeProjectId) → hits. Set by the
        /// Projects service in ServiceMain; null when KliveRAG isn't present. Fails soft (returns []).
        /// </summary>
        public Func<string, string?, Task<List<Omnipotent.Services.KliveRAG.KnowledgeHit>>>? KnowledgeSearchAsync;

        /// <summary>
        /// Optional live-observables leg: projectID → current-values block for the seed. Set by the
        /// Projects service in ServiceMain (ProjectObservableStore.DescribeAll).
        /// </summary>
        public Func<string, string>? DescribeObservables;

        /// <summary>
        /// Optional Grand Plan leg: projectID → the approved plan's summary line (the standing north
        /// star). Set by the Projects service in ServiceMain (ProjectGrandPlanStore.DescribeForSeed).
        /// </summary>
        public Func<string, string>? DescribeGrandPlan;

        /// <summary>
        /// Optional shared-accounts leg: projectID → a block of accounts in the GLOBAL registry so
        /// agents reuse them instead of creating duplicates. Set by the Projects service in
        /// ServiceMain (AccountRegistry.DescribeForPrompt).
        /// </summary>
        public Func<string, string>? DescribeAccounts;

        /// <summary>
        /// Optional shared-files leg: projectID → a compact description of the project's shared
        /// filesystem, including important inputs/assets, recent changes and provenance. Set by the
        /// Projects service from the project file store; agents can use list_files/stat_file for more.
        /// </summary>
        public Func<string, string>? DescribeFiles;

        /// <summary>Machine-owned typed checkpoint/runtime state, seeded ahead of narrative digest.</summary>
        public Func<string, string>? DescribeRuntimeState;

        /// <summary>
        /// Durable Klives rules/tasks scoped to the Commander. Unlike the event-log and digest
        /// legs, this block is injected on every wake and is never compacted away.
        /// </summary>
        /// <summary>(projectID, triggerDescription) -> a budget-fitted durable directive block.</summary>
        public Func<string, string, string>? DescribeDirectives;

        /// <summary>
        /// Pending and recently resolved approval gates. Without this the Commander cannot see its own
        /// outstanding requests, so it re-opens a card that is already sitting in front of Klives.
        /// </summary>
        public Func<string, string>? DescribeApprovals;

        /// <summary>Optional live KliveAgent bridge summary: active services, registered capabilities,
        /// reusable shortcuts and a compact task-relevant repository map. This keeps Project wakes
        /// grounded in the same operating environment as interactive KliveAgent without copying a
        /// long-lived conversation into the project log.</summary>
        public Func<string, Task<string>>? DescribeKliveAgentContextAsync;

        /// <summary>
        /// Builds the full seed message for one Commander wake, triggered by
        /// <paramref name="triggerDescription"/> (a confirmed stimulus payload + verdict,
        /// a Klives message, a timer keepalive, or a watchdog force-wake reason).
        /// </summary>
        /// <param name="recentEventsConsidered">Overrides how many recent events are considered (project setting).</param>
        /// <param name="recentEventsBudget">Overrides the token budget for the recent-event block (project setting).</param>
        public async Task<string> BuildWakeSeed(Project project, string triggerDescription,
            int? recentEventsConsidered = null, int? recentEventsBudget = null,
            bool chronologicalEvents = true)
        {
            var digest = digests.GetDigest(project.ProjectID);

            // Recent verbatim window: everything after the digest watermark, newest kept. The window
            // reaches deeper than the configured event count because everything past the newest handful
            // is compacted before it is fitted — the token budget, not the count, is the real bound.
            int considered = recentEventsConsidered ?? ProjectCommanderPrompts.RecentEventsConsidered;
            int deepWindow = Math.Clamp(considered * ProjectsContextBudget.CompactHistoryMultiplier, considered, 800);
            var recent = eventLog.ReadRecentSince(project.ProjectID, digest.LastDigestedSequence, count: 2000)
                .Where(ProjectPromptHygiene.IsAgentVisibleEvent)
                .TakeLast(deepWindow)
                .ToList();

            // Retrieval into the deep log, keyed by the trigger. Events already in the recent
            // window are excluded — retrieval exists to reach past it.
            //
            // Distinct terms, not a blob: this used to concatenate the goal, focus, eight next steps AND
            // 1,200 characters of plan prose, which is a broad enough BM25 query to match most of the log
            // and therefore ranked long-tail noise to the top.
            string retrievalQuery = ProjectsContextBudget.BuildRetrievalQuery(40,
                project.Goal,
                ProjectPromptHygiene.ScrubTrigger(triggerDescription),
                ProjectPromptHygiene.ScrubState(digest.CurrentFocus, ""),
                string.Join(" ", digest.NextSteps
                    .Where(step => !ProjectPromptHygiene.ContainsContextBookkeeping(step))
                    .Take(3)));
            var hits = retrieval.Search(project.ProjectID, retrievalQuery)
                .Where(h => recent.Count == 0 || h.Sequence < recent[0].Sequence)
                .Where(ProjectPromptHygiene.IsAgentVisibleRetrievalHit)
                .ToList();

            // Cross-system knowledge, excluding THIS project's own events/digests (covered by the log leg).
            List<Omnipotent.Services.KliveRAG.KnowledgeHit>? knowledge = null;
            if (KnowledgeSearchAsync != null)
            {
                string query = $"{project.Goal} {Truncate(triggerDescription, 300)}";
                try { knowledge = await KnowledgeSearchAsync(query, project.ProjectID); } catch { knowledge = null; }
            }

            string? observables = null;
            if (DescribeObservables != null)
            {
                try { observables = DescribeObservables(project.ProjectID); } catch { observables = null; }
            }

            string? grandPlan = null;
            if (DescribeGrandPlan != null)
            {
                try { grandPlan = DescribeGrandPlan(project.ProjectID); } catch { grandPlan = null; }
            }

            string? accounts = null;
            if (DescribeAccounts != null)
            {
                try { accounts = DescribeAccounts(project.ProjectID); } catch { accounts = null; }
            }

            string? files = null;
            if (DescribeFiles != null)
            {
                try { files = DescribeFiles(project.ProjectID); } catch { files = null; }
            }

            string? runtimeState = null;
            if (DescribeRuntimeState != null)
            {
                try { runtimeState = DescribeRuntimeState(project.ProjectID); } catch { runtimeState = null; }
            }

            string? directives = null;
            if (DescribeDirectives != null)
            {
                try { directives = DescribeDirectives(project.ProjectID, triggerDescription); } catch { directives = null; }
            }

            string? approvals = null;
            if (DescribeApprovals != null)
            {
                try { approvals = DescribeApprovals(project.ProjectID); } catch { approvals = null; }
            }

            string? kliveAgentContext = null;
            if (DescribeKliveAgentContextAsync != null)
            {
                try { kliveAgentContext = await DescribeKliveAgentContextAsync(project.ProjectID); } catch { kliveAgentContext = null; }
            }

            return ProjectCommanderPrompts.BuildWakeSeed(project, digest, recent, hits,
                ProjectPromptHygiene.ScrubTrigger(triggerDescription),
                knowledge, observables, grandPlan, accounts, files, runtimeState, kliveAgentContext, directives,
                recentEventsBudget, chronologicalEvents, approvals);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
    }
}
