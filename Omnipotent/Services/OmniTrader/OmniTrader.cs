using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.OmniTrader.Agent;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.ServiceTools;
using System.Reflection;

namespace Omnipotent.Services.OmniTrader
{
    [OmniServiceTools("omnitrader", "OmniTrader: Klives' trading terminal. Market data for crypto, equities, FX, indices and macro; a strategy studio (blueprints and C# scripts); backtests and studies; and the Alpha Lab, which mines research papers for strategies.")]
    [OmniToolGroup("research", "OmniTrader Alpha Lab: research papers on trading alpha and the candidate strategies built from them. Read a paper, implement its idea in the studio, test it, submit it with alpha_submit, record a verdict.")]
    [OmniToolGroup("studio", "OmniTrader strategy studio: write, validate and version strategies as blueprint JSON or C# scripts. op=reference explains both formats and every indicator.")]
    [OmniToolGroup("lab", "OmniTrader lab: backtest any strategy (inline quick tests or queued jobs) and run parameter sweeps and walk-forward studies.")]
    [OmniToolGroup("data", "OmniTrader market data: quotes, candles, screens, company fundamentals, crypto derivatives, macro and news. All free sources.")]
    public sealed class OmniTrader : OmniService
    {
        public OmniTraderDb Db { get; private set; } = null!;
        public DeploymentRepository DeploymentRepo { get; private set; } = null!;
        public OrderRepository OrderRepo { get; private set; } = null!;
        public FillRepository FillRepo { get; private set; } = null!;
        public EquityRepository EquityRepo { get; private set; } = null!;
        public BacktestJobRepository BacktestJobRepo { get; private set; } = null!;
        public CandleCacheRepository CandleCacheRepo { get; private set; } = null!;
        public UniverseRepository UniverseRepo { get; private set; } = null!;
        public KrakenNonceStore NonceStore { get; private set; } = null!;
        public MarketDataRouter MarketData { get; private set; } = null!;
        public StrategyRegistry StrategyRegistry { get; private set; } = null!;
        public SessionManager SessionManager { get; private set; } = null!;
        public BacktestJobQueue BacktestQueue { get; private set; } = null!;

        /// <summary>Terminal data: quotes, fundamentals, filings, options, derivatives, macro, news.</summary>
        public DataHub Data { get; private set; } = null!;
        public ScreenerEngine Screener { get; private set; } = null!;
        public MarketAlertService MarketAlerts { get; private set; } = null!;
        public StudioService? Studio { get; private set; }
        public StudyRunner Studies { get; private set; } = null!;
        public CandidateEvaluator? Evaluator { get; private set; }
        public AlphaLab? AlphaLab { get; private set; }

        /// <summary>KliveAgent's tool surface over the terminal (discovered by the service-tool registry).</summary>
        public OmniTraderAgentTools Agent { get; }
        private TerminalRoutes terminalRoutes = null!;

        private OmniTraderRoutes routes = null!;
        private FirmRoutes firmRoutes = null!;
        private KrakenOrderRouter? krakenRouter;

        /// <summary>The trading operating system layered over the strategy engine: venues, instruments,
        /// risk, order flow, ledger, reconciliation, journal, research and operations.</summary>
        public FirmContext Firm { get; private set; } = null!;

        public bool IsKrakenConfigured => krakenRouter != null;
        public KrakenOrderRouter? KrakenRouter => krakenRouter;
        public string GetDbPath() => Db?.DbPath ?? "(uninitialised)";

        /// <summary>Route a message to Klives on Discord. Used by the alert service for High and
        /// Critical severities; failures are swallowed because the alert is already durable.</summary>
        public async Task PushToDiscordAsync(string message)
        {
            try
            {
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", message);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to push OmniTrader alert to Discord");
            }
        }

        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Standard;
            Agent = new OmniTraderAgentTools(this);
        }

        private Action<string> Log => m => _ = ServiceLog(m);
        private Action<string, Exception?> Err => (m, e) => _ = (e == null ? ServiceLogError(m) : ServiceLogError(e, m));

        private async Task<string?> FredKeyAsync()
        {
            try { return await GetStringOmniSetting("OmniTrader.FRED.ApiKey", sensitive: true); }
            catch { return null; }
        }

        /// <summary>Watchlist id or name to engine symbols, for screens and studies over "watchlist:{id}".</summary>
        private async Task<List<string>> ResolveWatchlistAsync(string idOrName, CancellationToken ct)
        {
            if (Firm == null) return new();
            var lists = await Firm.Watchlists.ListAsync(ct);
            var list = lists.FirstOrDefault(l => l.Id == idOrName)
                ?? lists.FirstOrDefault(l => string.Equals(l.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            if (list == null) return new();
            return list.InstrumentIds
                .Select(i => Firm.Instruments.Resolve(i) is { } inst ? Firm.Instruments.EngineSymbolFor(inst.Id) : i)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal async Task<List<string>> ResolveUniverseSymbolsAsync(string universe, CancellationToken ct)
            => (await Screener.ResolveUniverseAsync(new ScreenDefinition { Universe = universe }, ct))
                .Select(m => m.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Hands the Alpha Lab's brief to KliveAgent as its own conversation. Null when the
        /// agent is not running, so the papers go back in the queue instead of being marked failed.</summary>
        private async Task<string?> AskAgentAsync(string message, string conversationId, CancellationToken ct)
        {
            var agent = GetActiveServices().OfType<Omnipotent.Services.KliveAgent.KliveAgent>().FirstOrDefault(s => s.IsServiceActive());
            if (agent == null) return null;
            var reply = await agent.HandleIncomingMessage(message, AgentSourceChannel.API, conversationId,
                senderName: "OmniTrader Alpha Lab", cancellationToken: ct);
            if (!reply.Success) throw new InvalidOperationException(reply.ErrorMessage ?? reply.Response ?? "KliveAgent failed");
            return reply.Response ?? "";
        }

        // ── typed route registration ─────────────────────────────────────────────
        // The inherited CreateAPIRoute dispatches through ExecuteServiceMethod's reflection, which
        // fails silently and partially when KliveAPI is not active yet (an arbitrary subset of routes
        // 404s with nothing logged). Routes register against the typed KliveAPI instead, resolved once
        // with a bounded wait, and a failure names the route.

        private Omnipotent.Services.KliveAPI.KliveAPI? routeApi;
        private int routesRegistered;

        public int RoutesRegistered => routesRegistered;

        private async Task<Omnipotent.Services.KliveAPI.KliveAPI> ResolveRouteApiAsync()
        {
            if (routeApi != null) return routeApi;
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                var api = GetActiveServices().OfType<Omnipotent.Services.KliveAPI.KliveAPI>().FirstOrDefault();
                if (api != null) return routeApi = api;
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("KliveAPI did not become active within 90s; OmniTrader routes cannot register.");
                await Task.Delay(250);
            }
        }

        internal async Task RegisterRouteAsync(string path,
            Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler,
            HttpMethod method, Omnipotent.Profiles.Permissions.PermissionDef permission)
        {
            var api = await ResolveRouteApiAsync();
            try
            {
                await api.CreateRoute(path, handler, method, permission);
                Interlocked.Increment(ref routesRegistered);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"OmniTrader: failed to register route {path}");
                throw;
            }
        }

        protected override async void ServiceMain()
        {
            try
            {
                Db = new OmniTraderDb();
                await Db.InitialiseAsync();

                DeploymentRepo = new DeploymentRepository(Db);
                OrderRepo = new OrderRepository(Db);
                FillRepo = new FillRepository(Db);
                EquityRepo = new EquityRepository(Db);
                BacktestJobRepo = new BacktestJobRepository(Db);
                CandleCacheRepo = new CandleCacheRepository(Db);
                UniverseRepo = new UniverseRepository(Db);
                NonceStore = new KrakenNonceStore(Db);
                await NonceStore.InitialiseAsync();

                MarketData = new MarketDataRouter(CandleCacheRepo);
                Data = new DataHub(MarketData, FredKeyAsync);
                Screener = new ScreenerEngine(MarketData, Data, ResolveWatchlistAsync);
                MarketAlerts = new MarketAlertService(Db, MarketData, PushToDiscordAsync, Err);

                StrategyRegistry = new StrategyRegistry();
                StrategyRegistry.DiscoverFrom(Assembly.GetExecutingAssembly());

                await BacktestJobRepo.MarkOrphansFailedAsync("interrupted by restart");

                await TryInitKrakenAsync();

                SessionManager = new SessionManager(
                    MarketData, StrategyRegistry,
                    DeploymentRepo, OrderRepo, FillRepo, EquityRepo,
                    () => krakenRouter,
                    Log, Err);

                BacktestQueue = new BacktestJobQueue(BacktestJobRepo, MarketData, StrategyRegistry,
                    UniverseRepo, Log, Err);

                // Studio strategies register their classes before anything that resolves a strategy by
                // name runs: queued backtests, studies and recovered deployments may reference them.
                Studio = new StudioService(Db, StrategyRegistry, BacktestQueue, MarketData, Log);
                try { await Studio.LoadAsync(cancellationToken.Token); }
                catch (Exception ex) { await ServiceLogError(ex, "Studio failed to load strategies"); }

                // A finished job of a studio strategy becomes that strategy's latest run.
                BacktestQueue.Completed += async job =>
                {
                    if (Studio == null || job.Status != BacktestJobStatus.Succeeded
                        || !job.StrategyClass.StartsWith("studio:", StringComparison.Ordinal)) return;
                    string studioId = job.StrategyClass["studio:".Length..].Split('@')[0];
                    try { await Studio.RecordRunAsync(studioId, job.Id, job.Result); }
                    catch (Exception ex) { await ServiceLogError(ex, $"Studio could not record run {job.Id}"); }
                };
                BacktestQueue.Start();
                await BacktestQueue.RestoreQueuedAsync();

                Studies = new StudyRunner(Db, BacktestQueue, BacktestJobRepo, ResolveUniverseSymbolsAsync, Err);
                await Studies.RestoreAsync(cancellationToken.Token);
                Studies.Start(cancellationToken.Token);

                Evaluator = new CandidateEvaluator(Studio, BacktestQueue, BacktestJobRepo, Studies, StrategyRegistry);
                var lab = new AlphaLab(Db, Studio, Evaluator, AskAgentAsync, PushToDiscordAsync, Log, Err);
                try
                {
                    await lab.LoadAsync(cancellationToken.Token);
                    AlphaLab = lab;
                }
                catch (Exception ex) { await ServiceLogError(ex, "Alpha Lab failed to load"); }

                routes = new OmniTraderRoutes(this);
                await routes.RegisterAsync();

                // The firm layer comes up after the engine's stores and market data exist, and before
                // sessions are recovered: a recovered deployment must find risk and reconciliation
                // already in place rather than trading into an unguarded platform.
                Firm = new FirmContext(this);
                firmRoutes = new FirmRoutes(this);
                await firmRoutes.RegisterAsync();
                bool firmStarted = false;
                try
                {
                    await Firm.StartAsync();
                    firmStarted = true;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "Firm layer failed to start, trading authority is unavailable");
                }

                terminalRoutes = new TerminalRoutes(this);
                await terminalRoutes.RegisterAsync();

                // The command-centre view is built independently of HTTP requests. Keep
                // the last completed snapshot available while the next refresh runs.
                if (firmStarted)
                {
                    firmRoutes.StartOverviewSnapshotLoop(cancellationToken.Token);
                    firmRoutes.StartRiskSnapshotLoop(cancellationToken.Token);
                }

                await SessionManager.RecoverAsync();
                routes.StartListSnapshotLoops(cancellationToken.Token);
                AlphaLab?.Start(cancellationToken.Token);
                MarketAlerts.Start(cancellationToken.Token);

                await ServiceLog($"OmniTrader started. DB={Db.DbPath}. Strategies={StrategyRegistry.All.Count}. "
                    + $"Kraken={(IsKrakenConfigured ? "configured" : "not configured")}. "
                    + $"Venues={Firm?.Venues.All.Count ?? 0}. Instruments={Firm?.Instruments.Count ?? 0}. "
                    + $"Studio={(Studio == null ? 0 : await Studio.CountAsync())}. Routes={RoutesRegistered}.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniTrader startup failed");
            }
        }

        private async Task TryInitKrakenAsync()
        {
            try
            {
                string apiKey = await GetStringOmniSetting("OmniTrader.Kraken.ApiKey", sensitive: true);
                string apiSecret = await GetStringOmniSetting("OmniTrader.Kraken.ApiSecret", sensitive: true);
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
                {
                    await ServiceLog("Kraken credentials missing — live trading disabled.");
                    return;
                }
                krakenRouter = new KrakenOrderRouter(apiKey, apiSecret, NonceStore);
                await ServiceLog("Kraken order router initialised.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to initialise Kraken order router");
                krakenRouter = null;
            }
        }
    }
}
