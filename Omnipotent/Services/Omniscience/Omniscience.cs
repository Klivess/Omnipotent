using Omnipotent.Service_Manager;
using Omnipotent.Services.Omniscience.Analytics;
using Omnipotent.Services.Omniscience.Ingest;
using Omnipotent.Services.Omniscience.Ingest.Discord;
using Omnipotent.Services.Omniscience.Profiling;
using Omnipotent.Services.Omniscience.Replica;
using Omnipotent.Services.Omniscience.Scheduling;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience
{
    /// <summary>
    /// Omniscience: a personal-intelligence platform that ingests every reachable message
    /// across every connected platform, aggregates per-person datasets, runs an extensible
    /// suite of behavioural analytics, and synthesises natural-language personality
    /// dossiers via the local KliveLLM. Multi-platform from day one (Discord first).
    /// </summary>
    public class Omniscience : OmniService
    {
        public OmniscienceDb Db { get; private set; } = null!;
        public IngestPipeline Pipeline { get; private set; } = null!;
        public DiscordIngester Discord { get; private set; } = null!;
        public AnalyticsEngine Analytics { get; private set; } = null!;
        public PersonalityProfiler Profiler { get; private set; } = null!;
        public OmniscienceScheduler Scheduler { get; private set; } = null!;
        public ReplicaTrainer ReplicaTrainer { get; private set; } = null!;
        public ReplicaChatOrchestrator ReplicaChat { get; private set; } = null!;

        public HttpClient Http { get; private set; } = null!;

        // Allow other services / routes to cancel everything cleanly.
        private readonly CancellationTokenSource cts = new();

        public Omniscience()
        {
            name = "Omniscience";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            await ServiceLog("[Omniscience] Bringing up SQLite store and ingest pipeline...");

            Db = new OmniscienceDb();
            Db.Migrate();

            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

            Pipeline = new IngestPipeline(this, Db, Http);
            Discord = new DiscordIngester(this, Db, Pipeline, Http);

            Analytics = new AnalyticsEngine(this, Db);
            Profiler = new PersonalityProfiler(this, Db);
            Scheduler = new OmniscienceScheduler(this, Analytics, Profiler);
            ReplicaTrainer = new ReplicaTrainer(this, Db, Http);
            ReplicaChat = new ReplicaChatOrchestrator(this, Db, Http);

            // Routes first so the UI is responsive even before sources load.
            var routes = new OmniscienceRoutes(this);
            await routes.RegisterRoutes();
            var replicaRoutes = new ReplicaRoutes(this);
            await replicaRoutes.RegisterRoutes();
            await ServiceLog("[Omniscience] API routes registered.");

            // Start Discord ingest (will start gateways for every saved source).
            try
            {
                await Discord.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[Omniscience] Discord ingester failed to start");
            }

            // Hook nightly schedule.
            Scheduler.HookSchedule();

            await ServiceLog("[Omniscience] Online.");
        }
    }
}
