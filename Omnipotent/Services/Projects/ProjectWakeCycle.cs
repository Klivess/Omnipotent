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
        /// Builds the full seed message for one Commander wake, triggered by
        /// <paramref name="triggerDescription"/> (a confirmed stimulus payload + verdict,
        /// a Klives message, a timer keepalive, or a watchdog force-wake reason).
        /// </summary>
        public string BuildWakeSeed(Project project, string triggerDescription)
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

            return ProjectCommanderPrompts.BuildWakeSeed(project, digest, recent, hits, triggerDescription);
        }
    }
}
