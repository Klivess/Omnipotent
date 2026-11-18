using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Rehydrates current Steward state from durable stores. The runner can append changed
    /// sections to a compatible bounded conversation; cold starts, expired sessions and context
    /// rotations use this same full seed. No correctness depends on process-local conversation state.
    /// </summary>
    public class StewardWakeCycle
    {
        private readonly StewardEventLogStore eventLog;
        private readonly StewardDigestStore digests;
        private readonly StewardRetrievalIndex retrieval;

        public StewardWakeCycle(StewardEventLogStore eventLog, StewardDigestStore digests, StewardRetrievalIndex retrieval)
        {
            this.eventLog = eventLog;
            this.digests = digests;
            this.retrieval = retrieval;
        }

        /// <summary>
        /// Optional cross-system knowledge leg (KliveRAG): (query, excludeStewardId) → hits. Set by the
        /// Demiurge service in ServiceMain; null when KliveRAG isn't present. Fails soft (returns []).
        /// </summary>
        public Func<string, string?, Task<List<Omnipotent.Services.KliveRAG.KnowledgeHit>>>? KnowledgeSearchAsync;

        /// <summary>
        /// Optional live-observables leg: stewardID → current-values block for the seed. Set by the
        /// Demiurge service in ServiceMain (StewardObservableStore.DescribeAll).
        /// </summary>
        public Func<string, string>? DescribeObservables;

        /// <summary>
        /// Optional Grand Plan leg: stewardID → the approved plan's summary line (the standing north
        /// star). Set by the Demiurge service in ServiceMain (StewardGrandPlanStore.DescribeForSeed).
        /// </summary>
        public Func<string, string>? DescribeGrandPlan;

        /// <summary>
        /// Optional shared-accounts leg: stewardID → a block of accounts in the GLOBAL registry so
        /// agents reuse them instead of creating duplicates. Set by the Demiurge service in
        /// ServiceMain (AccountRegistry.DescribeForPrompt).
        /// </summary>
        public Func<string, string>? DescribeAccounts;

        /// <summary>
        /// Optional shared-files leg: stewardID → a compact description of the Steward's shared
        /// filesystem, including important inputs/assets, recent changes and provenance. Set by the
        /// Demiurge service from the Steward file store; agents can use list_files/stat_file for more.
        /// </summary>
        public Func<string, string>? DescribeFiles;

        /// <summary>Machine-owned typed checkpoint/runtime state, seeded ahead of narrative digest.</summary>
        public Func<string, string>? DescribeRuntimeState;

        /// <summary>
        /// Durable Klives rules/tasks scoped to the Commander. Unlike the event-log and digest
        /// legs, this block is injected on every wake and is never compacted away.
        /// </summary>
        /// <summary>(stewardID, triggerDescription) -> a budget-fitted durable directive block.</summary>
        public Func<string, string, string>? DescribeDirectives;

        /// <summary>
        /// Pending and recently resolved approval gates. Without this the Commander cannot see its own
        /// outstanding requests, so it re-opens a card that is already sitting in front of Klives.
        /// </summary>
        public Func<string, string>? DescribeApprovals;

        /// <summary>
        /// The live roster with slot arithmetic: who is working, who is idle, who can be retired and
        /// how many slots are free. Set by the Demiurge service in ServiceMain
        /// (StewardSubAgentManager.DescribeTaskForce). Without it the Commander plans staffing from a
        /// one-line org chart that cannot express "eight slots are sitting empty".
        /// </summary>
        public Func<string, string>? DescribeTaskForce;

        /// <summary>Optional live KliveAgent bridge summary: active services, registered capabilities,
        /// reusable shortcuts and a compact task-relevant repository map. This keeps Steward wakes
        /// grounded in the same operating environment as interactive KliveAgent without copying a
        /// long-lived conversation into the Steward log.</summary>
        public Func<string, Task<string>>? DescribeKliveAgentContextAsync;

        /// <summary>
        /// (stewardID, agentID) → the agent's own computer as it stands: awake or asleep, the browser's
        /// open tabs, and anything that happened to it since the agent last looked. Lets a wake resume on
        /// the page it left instead of re-navigating to rediscover it. Never provisions or wakes a desktop.
        /// </summary>
        public Func<string, string, Task<string>>? DescribeComputerAsync;

        /// <summary>stewardID → Klives' autonomy limits for the Steward ("" when none are set).</summary>
        public Func<string, string>? DescribeAutonomyPolicy;

        /// <summary>
        /// Builds the full seed message for one Commander wake, triggered by
        /// <paramref name="triggerDescription"/> (a confirmed stimulus payload + verdict,
        /// a Klives message, a timer keepalive, or a watchdog force-wake reason).
        /// </summary>
        /// <param name="recentEventsConsidered">Overrides how many recent events are considered (Steward setting).</param>
        /// <param name="recentEventsBudget">Overrides the token budget for the recent-event block (Steward setting).</param>
        /// <param name="tokensUnmetered">True when the active router bills a flat fee, so the seed
        /// states that model calls are unmetered instead of quoting a token budget nobody is spending.</param>
        public async Task<string> BuildWakeSeed(Steward steward, string triggerDescription,
            int? recentEventsConsidered = null, int? recentEventsBudget = null,
            bool chronologicalEvents = true, bool tokensUnmetered = false,
            List<ToolSessionBriefSection>? briefSections = null)
        {
            var digest = digests.GetDigest(steward.StewardID);

            // Recent verbatim window: everything after the digest watermark, newest kept. The window
            // reaches deeper than the configured event count because everything past the newest handful
            // is compacted before it is fitted — the token budget, not the count, is the real bound.
            int considered = recentEventsConsidered ?? StewardCommanderPrompts.RecentEventsConsidered;
            int deepWindow = Math.Clamp(considered * DemiurgeContextBudget.CompactHistoryMultiplier, considered, 800);
            var recent = eventLog.ReadRecentSince(steward.StewardID, digest.LastDigestedSequence, count: 2000)
                .Where(StewardPromptHygiene.IsAgentVisibleEvent)
                .TakeLast(deepWindow)
                .ToList();

            // Retrieval into the deep log, keyed by the trigger. Events already in the recent
            // window are excluded — retrieval exists to reach past it.
            //
            // Distinct terms, not a blob: this used to concatenate the goal, focus, eight next steps AND
            // 1,200 characters of plan prose, which is a broad enough BM25 query to match most of the log
            // and therefore ranked long-tail noise to the top.
            string retrievalQuery = DemiurgeContextBudget.BuildRetrievalQuery(40,
                steward.Goal,
                StewardPromptHygiene.ScrubTrigger(triggerDescription),
                StewardPromptHygiene.ScrubState(digest.CurrentFocus, ""),
                string.Join(" ", digest.NextSteps
                    .Where(step => !StewardPromptHygiene.ContainsContextBookkeeping(step))
                    .Take(3)));
            var hits = retrieval.Search(steward.StewardID, retrievalQuery)
                .Where(h => recent.Count == 0 || h.Sequence < recent[0].Sequence)
                .Where(StewardPromptHygiene.IsAgentVisibleRetrievalHit)
                .ToList();

            // Cross-system knowledge, excluding THIS Steward's own events/digests (covered by the log leg).
            List<Omnipotent.Services.KliveRAG.KnowledgeHit>? knowledge = null;
            if (KnowledgeSearchAsync != null)
            {
                string query = $"{steward.Goal} {Truncate(triggerDescription, 300)}";
                try { knowledge = await KnowledgeSearchAsync(query, steward.StewardID); } catch { knowledge = null; }
            }

            string? observables = null;
            if (DescribeObservables != null)
            {
                try { observables = DescribeObservables(steward.StewardID); } catch { observables = null; }
            }

            string? grandPlan = null;
            if (DescribeGrandPlan != null)
            {
                try { grandPlan = DescribeGrandPlan(steward.StewardID); } catch { grandPlan = null; }
            }

            string? accounts = null;
            if (DescribeAccounts != null)
            {
                try { accounts = DescribeAccounts(steward.StewardID); } catch { accounts = null; }
            }

            string? files = null;
            if (DescribeFiles != null)
            {
                try { files = DescribeFiles(steward.StewardID); } catch { files = null; }
            }

            string? runtimeState = null;
            if (DescribeRuntimeState != null)
            {
                try { runtimeState = DescribeRuntimeState(steward.StewardID); } catch { runtimeState = null; }
            }

            string? directives = null;
            if (DescribeDirectives != null)
            {
                try { directives = DescribeDirectives(steward.StewardID, triggerDescription); } catch { directives = null; }
            }

            string? approvals = null;
            if (DescribeApprovals != null)
            {
                try { approvals = DescribeApprovals(steward.StewardID); } catch { approvals = null; }
            }

            string? taskForce = null;
            if (DescribeTaskForce != null)
            {
                try { taskForce = DescribeTaskForce(steward.StewardID); } catch { taskForce = null; }
            }

            string? kliveAgentContext = null;
            if (DescribeKliveAgentContextAsync != null)
            {
                try { kliveAgentContext = await DescribeKliveAgentContextAsync(steward.StewardID); } catch { kliveAgentContext = null; }
            }

            string? externalActions = null;
            try { externalActions = StewardExternalActions.DescribeForPrompt(eventLog, steward.StewardID); }
            catch { externalActions = null; }

            string? computer = null;
            if (DescribeComputerAsync != null)
            {
                try { computer = await DescribeComputerAsync(steward.StewardID, "commander"); } catch { computer = null; }
            }

            string? autonomy = null;
            if (DescribeAutonomyPolicy != null)
            {
                try { autonomy = DescribeAutonomyPolicy(steward.StewardID); } catch { autonomy = null; }
            }

            return StewardCommanderPrompts.BuildWakeSeed(steward, digest, recent, hits,
                StewardPromptHygiene.ScrubTrigger(triggerDescription),
                knowledge, observables, grandPlan, accounts, files, runtimeState, kliveAgentContext, directives,
                recentEventsBudget, chronologicalEvents, approvals, taskForce, externalActions,
                tokensUnmetered, briefSections, computer, autonomy);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
    }
}
