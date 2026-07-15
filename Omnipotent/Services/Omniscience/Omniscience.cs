using Omnipotent.Service_Manager;
using Omnipotent.Services.Omniscience.Analytics;
using Omnipotent.Services.Omniscience.Deduction;
using Omnipotent.Services.Omniscience.Ingest;
using Omnipotent.Services.Omniscience.Ingest.Discord;
using Omnipotent.Services.Omniscience.Ingest.Imports;
using Omnipotent.Services.Omniscience.Ingest.KliveBot;
using Omnipotent.Services.Omniscience.Profiling;
using Omnipotent.Services.Omniscience.Radar;
using Omnipotent.Services.Omniscience.Replica;
using Omnipotent.Services.Omniscience.Scheduling;
using Omnipotent.Services.Omniscience.Search;
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
        public DiscordEventRecorder EventRecorder { get; private set; } = null!;
        public KliveBotIngester KliveBotFeed { get; private set; } = null!;
        public KlivesRadar Radar { get; private set; } = null!;
        public AnalyticsEngine Analytics { get; private set; } = null!;
        public PersonalityProfiler Profiler { get; private set; } = null!;
        public OmniscienceScheduler Scheduler { get; private set; } = null!;
        public MaintenanceJobs Maintenance { get; private set; } = null!;
        public OcrService Ocr { get; private set; } = null!;
        public MessageEmbeddingIndex SearchIndex { get; private set; } = null!;
        public ExtractionJob Extraction { get; private set; } = null!;
        public GraphAssembler Graph { get; private set; } = null!;
        public AliasResolver Aliases { get; private set; } = null!;
        public DetectivePass Detective { get; private set; } = null!;
        public TargetSuggestionEngine TargetSuggestions { get; private set; } = null!;
        public HypothesisWatcher Hypotheses { get; private set; } = null!;
        public IdentityLinkEngine IdentityLinks { get; private set; } = null!;
        public ReplicaTrainer ReplicaTrainer { get; private set; } = null!;
        public ReplicaChatOrchestrator ReplicaChat { get; private set; } = null!;
        public ReplicaFidelity ReplicaFidelity { get; private set; } = null!;
        public ImportWatcher Imports { get; private set; } = null!;
        public DailyBriefing Briefing { get; private set; } = null!;

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

            // Recency weighting for analytics/profiling: floored exponential decay so 6+
            // year old corpora stay audible while recent behaviour dominates.
            try
            {
                int halfLife = Math.Clamp(await GetIntOmniSetting("OmniscienceRecencyHalfLifeDays", 180), 7, 3650);
                int floorPct = Math.Clamp(await GetIntOmniSetting("OmniscienceRecencyFloorPercent", 5), 0, 100);
                TemporalWeighting.Configure(halfLife, floorPct / 100.0);
            }
            catch (Exception ex)
            {
                _ = ServiceLogError(ex, "[Omniscience] Failed to read recency settings; using defaults.");
            }

            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

            Pipeline = new IngestPipeline(this, Db, Http);
            EventRecorder = new DiscordEventRecorder(this, Db);
            Discord = new DiscordIngester(this, Db, Pipeline, Http, EventRecorder);
            KliveBotFeed = new KliveBotIngester(this, Db, Pipeline, EventRecorder);

            // Klives radar: immediate DM when anyone mentions Klives in live traffic.
            Radar = new KlivesRadar(this, Db);
            await Radar.StartAsync();
            Pipeline.OnMessagePersisted += msg => Radar.InspectMessage(msg);

            // OCR: text inside screenshots/memes joins the corpus.
            Ocr = new OcrService(this, Db, Http);
            await Ocr.ConfigureAsync();
            Pipeline.OnImageAttachmentSaved += (attachmentId, path) => Ocr.Enqueue(attachmentId, path);

            Analytics = new AnalyticsEngine(this, Db);
            Profiler = new PersonalityProfiler(this, Db);
            Scheduler = new OmniscienceScheduler(this, Analytics, Profiler);
            Maintenance = new MaintenanceJobs(this, Db, Ocr);
            Maintenance.Start();

            // Corpus-wide semantic index: embeds Tracked persons' messages incrementally.
            SearchIndex = new MessageEmbeddingIndex(this, Db, Http);
            SearchIndex.StartBackgroundIndexing();

            // Deduction engine: extraction (stage 1) → knowledge graph (stage 2) →
            // detective synthesis (stage 3), plus alias resolution, target suggestions
            // and ingest-time hypothesis watchers.
            Extraction = new ExtractionJob(this, Db);
            Graph = new GraphAssembler(this, Db);
            Aliases = new AliasResolver(this, Db);
            Detective = new DetectivePass(this, Db);
            TargetSuggestions = new TargetSuggestionEngine(this, Db);
            IdentityLinks = new IdentityLinkEngine(this, Db);
            Hypotheses = new HypothesisWatcher(this, Db);
            Pipeline.OnMessagePersisted += msg => Hypotheses.InspectMessage(msg);

            // Self-disclosure fast-track: an info-dense live message jumps its
            // conversation to the front of the next extraction pass.
            Pipeline.OnMessagePersisted += msg =>
            {
                if (!ExtractionJob.IsSelfDisclosure(msg.Content)) return;
                if (DateTime.UtcNow - msg.SentAt > TimeSpan.FromMinutes(30)) return; // backfill noise
                _ = Task.Run(() => Extraction.MarkConversationPriorityAsync(msg.Platform, msg.ChannelId ?? ""));
            };
            ReplicaTrainer = new ReplicaTrainer(this, Db, Http);
            ReplicaChat = new ReplicaChatOrchestrator(this, Db, Http);
            ReplicaFidelity = new ReplicaFidelity(this, Db);

            // Drop-folder imports (Discord data packages, WhatsApp exports) + daily briefing.
            Imports = new ImportWatcher(this, Db, Pipeline);
            Imports.Start();
            Briefing = new DailyBriefing(this, Db);

            // Routes first so the UI is responsive even before sources load.
            var routes = new OmniscienceRoutes(this);
            await routes.RegisterRoutes();
            var replicaRoutes = new ReplicaRoutes(this);
            await replicaRoutes.RegisterRoutes();
            var searchRoutes = new SearchRoutes(this);
            await searchRoutes.RegisterRoutes();
            var deductionRoutes = new DeductionRoutes(this);
            await deductionRoutes.RegisterRoutes();
            await ReplicaFidelity.RegisterRoutes();
            // Compose() runs several time-filtered aggregates over the full messages /
            // qa_pairs tables, and the dashboard batch requests this on every refresh —
            // cache + warm it so the batch never waits on it. A 5-minute TTL is fine
            // for a 24-hour digest.
            OmniscienceRoutes.RegisterWarmTarget("briefing/preview", TimeSpan.FromMinutes(5), ComposeBriefingPayload);
            await CreateAPIRoute("/omniscience/briefing/preview", async req =>
            {
                try
                {
                    // Compose-only: renders the digest for the console WITHOUT sending a DM.
                    await OmniscienceRoutes.CachedRead(req, "briefing/preview", TimeSpan.FromMinutes(5), ComposeBriefingPayload);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}", code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Klives);

            await CreateAPIRoute("/omniscience/briefing/run", async req =>
            {
                try
                {
                    string md = await Briefing.ComposeAndSendAsync(cts.Token);
                    await req.ReturnResponse(new Newtonsoft.Json.Linq.JObject(
                        new Newtonsoft.Json.Linq.JProperty("ok", true),
                        new Newtonsoft.Json.Linq.JProperty("markdown", md)).ToString(Newtonsoft.Json.Formatting.None));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}", code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Klives);
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

            // KliveBot sensor: live events + REST backfill through the bot token.
            _ = Task.Run(async () =>
            {
                try { await KliveBotFeed.StartAsync(cts.Token); }
                catch (Exception ex) { _ = ServiceLogError(ex, "[Omniscience] KliveBot feed failed to start"); }
            });

            // Hook nightly schedule.
            Scheduler.HookSchedule();

            await ServiceLog("[Omniscience] Online.");
        }

        // Shared by the /omniscience/briefing/preview route and its warm target.
        private string ComposeBriefingPayload()
        {
            return new Newtonsoft.Json.Linq.JObject(
                new Newtonsoft.Json.Linq.JProperty("markdown", Briefing.Compose()))
                .ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
