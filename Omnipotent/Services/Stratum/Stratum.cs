using Omnipotent.Service_Manager;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Stratum — agentic mechatronics design platform.
    ///
    /// Phase 1 scope (this commit):
    ///   * Multi-user project / revision / artifact / attachment data model.
    ///   * On-disk storage subsystem (content-addressed blobs, JSON index).
    ///   * REST routes for project CRUD, revision listing, artifact + attachment upload/download.
    ///
    /// Later phases will add the High-Level Planner agent, Mechanical/Simulation/Electronics/Firmware
    /// agents, and the per-iteration human-in-the-loop approval gates described in arXiv 2504.14681.
    /// </summary>
    public class Stratum : OmniService
    {
        public StratumStorage Storage { get; private set; } = null!;
        public StratumRunStore RunStore { get; private set; } = null!;
        public StratumAgentManager AgentManager { get; private set; } = null!;
        public StratumPythonRunner PythonRunner { get; private set; } = null!;
        public StratumToolManager ToolManager { get; private set; } = null!;
        public StratumTimelineStore Timeline { get; private set; } = null!;
        public StratumEngineerTurnRunner EngineerTurnRunner { get; private set; } = null!;
        private StratumPartsCatalog? partsCatalog;
        private readonly SemaphoreSlim partsCatalogLock = new SemaphoreSlim(1, 1);
        private StratumRoutes routes = null!;
        private StratumConversationRoutes conversationRoutes = null!;

        /// <summary>
        /// Lazily resolves the Mouser-backed parts catalog. The OmniSetting prompt only fires the
        /// first time the Electronics Agent runs, and the result is cached for the service lifetime.
        /// </summary>
        public async Task<StratumPartsCatalog> GetPartsCatalogAsync()
        {
            if (partsCatalog != null) return partsCatalog;
            await partsCatalogLock.WaitAsync();
            try
            {
                if (partsCatalog != null) return partsCatalog;
                string mouserKey = await GetOmniSetting("MouserAPIKey", OmniSettingType.String, sensitive: true, askKlivesForFulfillment: true);
                partsCatalog = new StratumPartsCatalog(mouserKey, msg => _ = ServiceLog($"PartsCatalog: {msg}"));
                return partsCatalog;
            }
            finally { partsCatalogLock.Release(); }
        }

        public Stratum()
        {
            name = "Stratum";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            Storage = new StratumStorage(msg => ServiceLog(msg));
            Storage.Load();

            RunStore = new StratumRunStore(msg => ServiceLog(msg));
            AgentManager = new StratumAgentManager(this, RunStore);
            PythonRunner = new StratumPythonRunner();
            ToolManager = new StratumToolManager();
            Timeline = new StratumTimelineStore(msg => ServiceLog(msg));
            EngineerTurnRunner = new StratumEngineerTurnRunner(this, Timeline);

            // Mark any runs that were Running/AwaitingApproval before a restart as Interrupted —
            // their in-memory gate TCS is gone, so the user must start fresh.
            try
            {
                var allProjectIDs = Storage.AllProjectIDsSnapshot();
                AgentManager.RecoverInterruptedRuns(allProjectIDs);
                EngineerTurnRunner.RecoverInterruptedTurns();
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Stratum: failed to recover interrupted runs"); }

            routes = new StratumRoutes(this);
            await routes.RegisterRoutes();
            conversationRoutes = new StratumConversationRoutes(this, Timeline, EngineerTurnRunner);
            await conversationRoutes.RegisterRoutes();

            ServiceLog("Stratum service started.");
        }
    }
}
