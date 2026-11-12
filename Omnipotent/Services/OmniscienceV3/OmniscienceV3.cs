using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniscienceV3.Agent;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Services.OmniscienceV3
{
    /// <summary>
    /// Omniscience v3, the rewrite of the personal-intelligence service (plan: indexed-baking-dahl).
    /// <para>
    /// During the rewrite it runs in shadow mode beside v2: it is registered always but stays idle unless
    /// <c>OmniscienceV3_ShadowEnabled</c> is true; then it tails v2's database (no Discord gateways of its own), so
    /// its outputs can be compared with v2's before cutover. Its routes live under <c>/omniscience/v3/</c> and its
    /// data under <c>SavedData/Omniscience/v3/</c>.
    /// </para>
    /// Boot is staged and supervised (F363): routes register first and answer an honest 503 with the current stage,
    /// then the stores open, then the kernel starts its modules. Shutdown runs in reverse (F362).
    /// </summary>
    [OmniServiceTools("omniscience_v3", "Omniscience v3 (shadow mode beside v2): the rebuilt personal-intelligence engine. Engine state, jobs, modules and LLM usage.")]
    [OmniToolGroup("engine", "Omniscience v3 engine: status, background jobs (list, inspect, retry, cancel), modules and LLM usage with cache efficiency.")]
    [OmniToolGroup("tactics", "Omniscience v3 tactics: list what can be run on a selection of people, messages or other data, start runs and read their results.")]
    public sealed class OmniscienceV3 : OmniService
    {
        public const string ShadowEnabledSetting = "OmniscienceV3_ShadowEnabled";

        private readonly V3Host host = new();
        private readonly RouteRegistrar registrar;
        private int routesRegistered;

        public OmniscienceV3(KliveAPI.KliveAPI? api = null)
        {
            name = "OmniscienceV3";
            threadAnteriority = ThreadAnteriority.Standard;
            registrar = new RouteRegistrar(api, () => GetActiveServices(), LogErrorSync);
            Queries = new EngineQueries(host, SystemClock.Instance, () => registrar.Registered);
            Agent = new OmniscienceV3AgentTools(host, Queries);
            // Subscribed once here, not in ServiceMain: a restarted service must not stop twice.
            ServiceQuitRequest += OnQuit;
        }

        /// <summary>The engine read side (also used by KliveAgent through <see cref="Agent"/>).</summary>
        public EngineQueries Queries { get; }

        /// <summary>KliveAgent's tools (discovered by the tool registry through this property).</summary>
        public OmniscienceV3AgentTools Agent { get; }

        internal V3Host Host => host;

        protected override async void ServiceMain()
        {
            try
            {
                await RunAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (host.Stopping.IsCancellationRequested)
            {
                await ServiceLog("OmniscienceV3: startup cancelled because the service is stopping.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.SetFailed(Describe(ex));
                await ServiceLogError(ex, "OmniscienceV3: startup failed; its routes answer 503 with the reason.").ConfigureAwait(false);
                await host.StopAsync().ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            var clock = SystemClock.Instance;
            host.Begin(clock.NowMs);
            var stopping = host.Stopping;

            if (Interlocked.Exchange(ref routesRegistered, 1) == 0)
            {
                host.SetStage("Registering routes");
                try
                {
                    await new V3Routes(host, registrar, Queries).RegisterAsync(stopping).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Exchange(ref routesRegistered, 0);
                    throw;
                }
            }

            host.SetStage("Reading settings");
            if (!await GetBoolOmniSetting(ShadowEnabledSetting, false).ConfigureAwait(false))
            {
                host.SetDisabled("Off: set OmniscienceV3_ShadowEnabled=true to run v3 in shadow mode beside v2.");
                await ServiceLog("OmniscienceV3 is installed but off (OmniscienceV3_ShadowEnabled=false); its routes answer 503 'disabled'.").ConfigureAwait(false);
                return;
            }

            var runtime = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = StorePaths.Default(),
                Clock = clock,
                LlmClient = new KliveLlmClient(ResolveKliveLlmAsync),
                Log = LogSync,
                LogError = LogErrorSync,
                Modules = BuildModules(),
                EncoderFactory = Ml.Encoders.SharedEncoder.Production(LogSync),
            }, host.SetStage, stopping).ConfigureAwait(false);

            if (!host.SetReady(runtime, clock.NowMs))
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await ServiceLog($"OmniscienceV3 ready in shadow mode: {runtime.Host.Modules.Count} module(s), {registrar.Registered.Count} route(s).").ConfigureAwait(false);
        }

        /// <summary>The modules this build runs. Workstreams add theirs here.</summary>
        private IReadOnlyList<IIntelModule> BuildModules()
        {
            // Discord through the connection the KliveBot service already holds; v3 opens none of its own. Off until cutover.
            var discord = new Ingest.Discord.DiscordBotModule(
                new Ingest.Discord.KliveBotFeed(() => GetActiveServices().OfType<KliveBot_Discord.KliveBotDiscord>().FirstOrDefault()?.Client,
                    Omnipotent.Data_Handling.OmniPaths.KlivesDiscordAccountID.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ReadKliveBotTokenAsync,
                Omnipotent.Data_Handling.OmniPaths.KlivesDiscordAccountID.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Signals also takes the findings of other modules (notices), so it is built first and handed to them.
            var signals = new Modules.Signalling.SignalsModule();
            var modules = new List<IIntelModule>
            {
                // Shadow mode's data source until cutover: v2's database, snapshot then tail (plan R8).
                new Storage.Legacy.LegacyImportModule(() => Omnipotent.Data_Handling.OmniPaths.GetPath(Omnipotent.Data_Handling.OmniPaths.GlobalPaths.OmniscienceDbFile)),
                // WS-A: imports, the media store, text from media, Discord through KliveBot.
                new Ingest.Imports.ImportModule(),
                // An expired Discord link is refreshed through the bot API, only once Discord capture is switched on (F403).
                new Ingest.Media.MediaModule(links: () => discord.LinkRefresher),
                // Speech-to-text comes from KliveLLM's AIRouter audio client, resolved when a transcript is first needed.
                new Ingest.Media.MediaTextModule(async () =>
                    await ResolveKliveLlmAsync().ConfigureAwait(false) is { } llm ? new Ingest.Media.AiRouterTranscriber(llm.Audio) : null),
                discord,
                // WS-C: classification, rollups, projections, Explore and signals.
                new Modules.Classification.ClassificationModule(),
                new Modules.Rollups.RollupModule(),
                new Modules.Projections.ProjectionModule(),
                signals,
                new Modules.DataControls.DataControlsModule(),
                new Modules.Links.LinksModule(),
                new Modules.Translate.TranslateModule(),
                // Monitors hand their findings to the signals module's notices.
                new Modules.Monitors.MonitorsModule(signals),
                // WS-B: knowledge, deduction, identity, timeline, plans, the Quality Lab and inquiries.
                new Knowledge.Deduction.DeductionModule(),
                new Knowledge.Entities.IdentityModule(),
                new Knowledge.Timeline.TimelineModule(),
                new Knowledge.Plans.PlansModule(),
                new Knowledge.Quality.QualityModule(),
                new Knowledge.Inquiry.InquiryModule(),
                // WS-D: replicas.
                new Replica.ReplicaModule(),
            };
            // Digests read a foreign-language message with its stored translation beside it.
            modules.Add(new Modules.Briefs.BriefsModule(() => modules.OfType<Kernel.Modules.ITranslations>().FirstOrDefault()));
            // Explore mirrors the datasets of every module in this list that offers them (IExploreSourceProvider).
            modules.Add(new Modules.Insights.InsightsModule(signals, () => modules.OfType<Kernel.Explore.IExploreEngine>().FirstOrDefault()));
            // Transforms checks saved charts against the datasets the modules in this list declare.
            modules.Add(new Modules.Transforms.TransformsModule(() => modules));
            modules.Add(new Modules.Explore.ExploreModule(() => modules));
            return modules;
        }

        /// <summary>KliveBot's token, read from where the bot service keeps it, for requests to Discord's bot API only.</summary>
        private async Task<string?> ReadKliveBotTokenAsync() =>
            await GetDataHandler().ReadDataFromFile(Omnipotent.Data_Handling.OmniPaths.GetPath(Omnipotent.Data_Handling.OmniPaths.GlobalPaths.KliveBotDiscordTokenText), true).ConfigureAwait(false);

        private Task<KliveLLM.KliveLLM?> ResolveKliveLlmAsync() =>
            Task.FromResult(GetActiveServices().OfType<KliveLLM.KliveLLM>().FirstOrDefault(s => s.IsServiceActive()));

        private void OnQuit()
        {
            // TerminateService raises this synchronously and does not await it. The stop is bounded so a wedged
            // component cannot hang shutdown; anything it skips is recovered at the next start (orphaned leases are
            // requeued, SQLite's WAL is durable).
            try
            {
                if (!host.StopAsync().Wait(TimeSpan.FromSeconds(20)))
                    LogErrorSync("OmniscienceV3: shutdown did not finish within 20s; continuing (state is recovered at the next start).", null);
            }
            catch (Exception ex)
            {
                LogErrorSync("OmniscienceV3: shutdown failed.", ex);
            }
        }

        private static string Describe(Exception ex) => ex switch
        {
            InstanceLockedException locked => locked.Message,
            AggregateException { InnerException: { } inner } => Describe(inner),
            _ => $"{ex.GetType().Name}: {ex.Message}",
        };

        private void LogSync(string message) => _ = ServiceLog(message);

        private void LogErrorSync(string message, Exception? ex) => _ = ex == null ? ServiceLogError(message) : ServiceLogError(ex, message);
    }
}
