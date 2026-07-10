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
        /// Builds the full seed message for one Commander wake, triggered by
        /// <paramref name="triggerDescription"/> (a confirmed stimulus payload + verdict,
        /// a Klives message, a timer keepalive, or a watchdog force-wake reason).
        /// </summary>
        public async Task<string> BuildWakeSeed(Project project, string triggerDescription)
        {
            var digest = digests.GetDigest(project.ProjectID);

            // Recent verbatim window: everything after the digest watermark, newest kept.
            var recent = eventLog.ReadSince(project.ProjectID, digest.LastDigestedSequence, max: 2000)
                .Where(e => e.Type != ProjectEventTypes.DigestRebuilt)
                .TakeLast(ProjectCommanderPrompts.RecentEventsConsidered)
                .ToList();

            // Retrieval into the deep log, keyed by the trigger. Events already in the recent
            // window are excluded — retrieval exists to reach past it.
            var hits = retrieval.Search(project.ProjectID, triggerDescription)
                .Where(h => recent.Count == 0 || h.Sequence < recent[0].Sequence)
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

            return ProjectCommanderPrompts.BuildWakeSeed(project, digest, recent, hits, triggerDescription, knowledge, observables, grandPlan, accounts);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
    }
}
