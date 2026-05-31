using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using System.Reflection;

namespace Omnipotent.Services.OmniTrader
{
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

        private OmniTraderRoutes routes = null!;
        private KrakenOrderRouter? krakenRouter;

        public bool IsKrakenConfigured => krakenRouter != null;
        public string GetDbPath() => Db?.DbPath ?? "(uninitialised)";

        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Standard;
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

                StrategyRegistry = new StrategyRegistry();
                StrategyRegistry.DiscoverFrom(Assembly.GetExecutingAssembly());

                await BacktestJobRepo.MarkOrphansFailedAsync("interrupted by restart");

                await TryInitKrakenAsync();

                SessionManager = new SessionManager(
                    MarketData, StrategyRegistry,
                    DeploymentRepo, OrderRepo, FillRepo, EquityRepo,
                    () => krakenRouter,
                    m => _ = ServiceLog(m),
                    (m, e) => _ = (e == null ? ServiceLogError(m) : ServiceLogError(e, m)));

                BacktestQueue = new BacktestJobQueue(BacktestJobRepo, MarketData, StrategyRegistry,
                    UniverseRepo,
                    m => _ = ServiceLog(m),
                    (m, e) => _ = (e == null ? ServiceLogError(m) : ServiceLogError(e, m)));
                BacktestQueue.Start();
                await BacktestQueue.RestoreQueuedAsync();

                routes = new OmniTraderRoutes(this);
                await routes.RegisterAsync();

                await SessionManager.RecoverAsync();

                await ServiceLog($"OmniTrader started. DB={Db.DbPath}. Strategies={StrategyRegistry.All.Count}. Kraken={(IsKrakenConfigured ? "configured" : "not configured")}.");
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
