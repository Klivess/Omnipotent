using Omnipotent.Services.OmniTrader.Analytics;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Performance;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Portfolio;
using Omnipotent.Services.OmniTrader.Research;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader
{
    /// <summary>
    /// The firm layer: venues, instruments, accounts, risk, order flow, ledger, reconciliation,
    /// portfolio, journal, research, performance and operations, wired together.
    ///
    /// It sits *on top of* the existing strategy engine rather than replacing it — the engine keeps
    /// owning strategies, sessions, backtests and market data, and this layer owns everything about
    /// how the firm as a whole trades, accounts and stays safe.
    /// </summary>
    public sealed class FirmContext : IAsyncDisposable
    {
        private readonly OmniTrader parent;
        private RiskLimits limits = RiskLimits.Conservative;
        private CancellationTokenSource? backgroundCts;

        // ── stores ────────────────────────────────────────────────────────────────
        public InstrumentRepository InstrumentRepo { get; }
        public AccountRepository Accounts { get; }
        public RiskDecisionRepository RiskRepo { get; }
        public FirmOrderRepository OrderRepo { get; }
        public LedgerRepository LedgerRepo { get; }
        public ReconciliationRepository ReconRepo { get; }
        public JournalRepository JournalRepo { get; }
        public AlertRepository AlertRepo { get; }
        public AuditRepository Audit { get; }
        public ExperimentRepository ExperimentRepo { get; }
        public StrategyVersionRepository VersionRepo { get; }
        public WatchlistRepository WatchlistRepo { get; }
        public FirmSettingsRepository Settings { get; }

        // ── services ──────────────────────────────────────────────────────────────
        public VenueRegistry Venues { get; }
        public InstrumentMaster Instruments { get; }
        public AlertService Alerts { get; }
        public EmergencyControls Emergency { get; }
        public RiskEngine Risk { get; }
        public FirmLedger Ledger { get; }
        public PortfolioService Portfolio { get; }
        public OrderService Orders { get; }
        public ReconciliationService Reconciliation { get; }
        public JournalService Journal { get; }
        public WatchlistService Watchlists { get; }
        public ExperimentRegistry Research { get; }
        public PerformanceService PerformanceService { get; }
        public HealthMonitor Health { get; }

        public RiskLimits Limits => limits;

        public FirmContext(OmniTrader parent)
        {
            this.parent = parent;
            var db = parent.Db;

            InstrumentRepo = new InstrumentRepository(db);
            Accounts = new AccountRepository(db);
            RiskRepo = new RiskDecisionRepository(db);
            OrderRepo = new FirmOrderRepository(db);
            LedgerRepo = new LedgerRepository(db);
            ReconRepo = new ReconciliationRepository(db);
            JournalRepo = new JournalRepository(db);
            AlertRepo = new AlertRepository(db);
            Audit = new AuditRepository(db);
            ExperimentRepo = new ExperimentRepository(db);
            VersionRepo = new StrategyVersionRepository(db);
            WatchlistRepo = new WatchlistRepository(db);
            Settings = new FirmSettingsRepository(db);

            Venues = new VenueRegistry();
            Instruments = new InstrumentMaster(InstrumentRepo, Venues);

            Alerts = new AlertService(AlertRepo,
                push: message => parent.PushToDiscordAsync(message),
                log: m => _ = parent.ServiceLog($"[alert] {m}"));

            // Emergency-control changes are themselves audited and alerted — losing the record of who
            // stopped trading, and when, would defeat the point of having the control.
            Emergency = new EmergencyControls((action, detail) =>
            {
                _ = Audit.AppendAsync("emergency-controls", action, "firm", detail);
                _ = parent.ServiceLog($"[emergency] {action}: {detail}");
                if (action == "safe_mode_entered")
                    _ = Alerts.CriticalAsync("risk", "Safe mode engaged", detail, dedupeKey: "safe-mode",
                        recoveryHint: "Resolve the underlying condition, then clear safe mode on the Risk page.");
                else if (action == "safe_mode_cleared")
                    _ = Alerts.ResolveByDedupeAsync("safe-mode");
            });

            Risk = new RiskEngine(() => limits);
            Ledger = new FirmLedger(LedgerRepo, ReconRepo, Instruments);
            Portfolio = new PortfolioService(Ledger, Instruments, Venues, parent.MarketData, LedgerRepo, Accounts);

            Orders = new OrderService(Venues, Instruments, Risk, Emergency, OrderRepo, RiskRepo, Ledger,
                Alerts, Audit, () => Portfolio.BuildRiskStateAsync(), m => _ = parent.ServiceLog($"[orders] {m}"));

            Reconciliation = new ReconciliationService(Venues, Instruments, Ledger, OrderRepo, ReconRepo,
                Accounts, Orders, Alerts, Emergency, m => _ = parent.ServiceLog($"[reconcile] {m}"));

            Journal = new JournalService(JournalRepo, RiskRepo, Instruments, parent.MarketData);
            Watchlists = new WatchlistService(WatchlistRepo, Instruments, parent.MarketData);
            Research = new ExperimentRegistry(ExperimentRepo, VersionRepo, parent.BacktestJobRepo);
            PerformanceService = new PerformanceService(LedgerRepo, OrderRepo, JournalRepo, Accounts);
            Health = new HealthMonitor(Venues, Instruments, OrderRepo, ReconRepo, Emergency, Alerts,
                () => Reconciliation.LastRunUtc);
        }

        /// <summary>
        /// Bring the firm layer up. Order matters: venues before instruments (the master folds their
        /// directories), ledger before reconciliation (there must be something to compare), and
        /// reconciliation before anything is allowed to trade.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            await LoadSettingsAsync(ct);
            await RegisterVenuesAsync(ct);
            await Instruments.LoadAsync(ct);
            await EnsureAccountsAsync(ct);
            await Watchlists.EnsureDefaultAsync(ct);
            await Ledger.RehydrateAsync(ct);

            // Collapse repeats left by earlier versions before reconciling, so an operator inheriting
            // a wall of identical rows gets one per real condition.
            try { await Reconciliation.CollapseDuplicateBreaksAsync(ct); }
            catch (Exception ex) { await parent.ServiceLogError(ex, "collapsing duplicate breaks failed"); }

            // Startup is one of the mandated reconciliation triggers.
            try { await Reconciliation.ReconcileAllAsync("startup", ct); }
            catch (Exception ex) { await parent.ServiceLogError(ex, "startup reconciliation failed"); }

            await Audit.AppendAsync("system", "firm.started", "firm",
                $"{Venues.All.Count} venue(s), {Instruments.Count} instrument(s)", ct: ct);

            StartBackgroundLoops();
        }

        private async Task LoadSettingsAsync(CancellationToken ct)
        {
            var stored = await Settings.GetAsync<RiskLimits>("risk.limits", ct);
            if (stored != null) limits = stored;

            var reporting = await Settings.GetAsync<ReportingCurrencySetting>("reporting.currency", ct);
            Portfolio.ReportingCurrency = reporting?.Currency ?? "GBP";
        }

        public async Task UpdateLimitsAsync(RiskLimits updated, CancellationToken ct = default)
        {
            limits = updated;
            await Settings.SetAsync("risk.limits", updated, ct);
        }

        /// <summary>
        /// Register the venues this deployment is configured for. Kraken reuses the engine's existing
        /// order router; IG demo and IG live are registered as genuinely separate adapters with
        /// separate credentials, so nothing can cross between them.
        /// </summary>
        private async Task RegisterVenuesAsync(CancellationToken ct)
        {
            Venues.Register(new InternalPaperVenueAdapter(parent.MarketData));

            var kraken = parent.KrakenRouter;
            if (kraken != null)
            {
                var adapter = new KrakenVenueAdapter(kraken, parent.MarketData);
                Venues.Register(adapter);
                try { await adapter.ConnectAsync(ct); }
                catch (Exception ex) { await parent.ServiceLogError(ex, "Kraken venue connect failed"); }
            }

            await RegisterIgAsync(TradingEnvironment.Demo, "OmniTrader.IG.Demo", ct);
            await RegisterIgAsync(TradingEnvironment.Live, "OmniTrader.IG.Live", ct);

            await RegisterTrading212Async(TradingEnvironment.Demo, "OmniTrader.Trading212.Demo", ct);
            await RegisterTrading212Async(TradingEnvironment.Live, "OmniTrader.Trading212.Live", ct);
        }

        /// <summary>
        /// Where each registered venue's credentials actually came from, keyed by
        /// `{venue}:{environment}`. Surfaced on the Systems page: when a shared key is doing the work
        /// of two, the operator should be able to see that rather than infer it.
        /// </summary>
        public IReadOnlyDictionary<string, string> CredentialSources => credentialSources;
        private readonly Dictionary<string, string> credentialSources = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve one credential from an environment-specific setting, falling back to a shared one.
        ///
        /// The two brokers issue keys differently and the platform has to accommodate both rather
        /// than imposing its own shape: IG allows only **one API key per account** (the demo
        /// *username and password* are what differ), while Trading 212 issues a key that only works
        /// in the environment it was generated in. Demanding a per-environment value for everything
        /// meant an operator with a single IG key had to paste it into two settings and got silence
        /// when they did not.
        /// </summary>
        private async Task<(string Value, string Source)> ResolveCredentialAsync(string sharedKey, string environmentKey)
        {
            string specific = await parent.GetStringOmniSetting(environmentKey, sensitive: true);
            if (!string.IsNullOrWhiteSpace(specific)) return (specific, environmentKey);

            string shared = await parent.GetStringOmniSetting(sharedKey, sensitive: true);
            return (shared ?? "", string.IsNullOrWhiteSpace(shared) ? "" : sharedKey);
        }

        /// <summary>
        /// Trading 212 Invest/ISA — owned shares. A T212 key is bound to the environment it was
        /// generated in (you switch the app to Practice mode to mint a demo key), so a shared key is
        /// only a convenience for someone running one environment; using it for both will simply see
        /// the other rejected, and the Systems page says so.
        /// </summary>
        private async Task RegisterTrading212Async(TradingEnvironment environment, string settingPrefix, CancellationToken ct)
        {
            try
            {
                var (apiKey, source) = await ResolveCredentialAsync("OmniTrader.Trading212.ApiKey", $"{settingPrefix}.ApiKey");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    await parent.ServiceLog($"Trading 212 {environment} key not configured — venue not registered.");
                    return;
                }

                var adapter = new Trading212VenueAdapter(apiKey, environment, parent.MarketData);
                Venues.Register(adapter);
                credentialSources[VenueRegistry.Key(VenueId.Trading212, environment)] = source;
                bool connected = await adapter.ConnectAsync(ct);
                await parent.ServiceLog($"Trading 212 {environment} venue registered from {source} "
                    + $"({(connected ? "authenticated" : "key rejected — a Trading 212 key only works in the environment it was generated in")}).");
            }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, $"Trading 212 {environment} venue registration failed");
            }
        }

        /// <summary>
        /// IG CFD. IG issues one API key per account, so the key falls back to a shared setting; the
        /// demo username and password are genuinely separate values that IG makes you create, so
        /// those are per-environment first.
        /// </summary>
        private async Task RegisterIgAsync(TradingEnvironment environment, string settingPrefix, CancellationToken ct)
        {
            try
            {
                var (apiKey, keySource) = await ResolveCredentialAsync("OmniTrader.IG.ApiKey", $"{settingPrefix}.ApiKey");
                var (identifier, _) = await ResolveCredentialAsync("OmniTrader.IG.Username", $"{settingPrefix}.Username");
                var (password, _) = await ResolveCredentialAsync("OmniTrader.IG.Password", $"{settingPrefix}.Password");
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(password))
                {
                    await parent.ServiceLog($"IG {environment} credentials not configured — venue not registered.");
                    return;
                }
                credentialSources[VenueRegistry.Key(VenueId.IG, environment)] = keySource;

                var client = new IGRestClient(apiKey, identifier, password, environment);
                var adapter = new IGVenueAdapter(client);
                Venues.Register(adapter);
                bool connected = await adapter.ConnectAsync(ct);
                await parent.ServiceLog($"IG {environment} venue registered ({(connected ? "authenticated" : "login failed")}).");
            }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, $"IG {environment} venue registration failed");
            }
        }

        /// <summary>Create an account record per registered venue so the environment model is explicit
        /// from the first run. Live accounts start at <see cref="ExecutionAuthority.Observe"/>.</summary>
        private async Task EnsureAccountsAsync(CancellationToken ct)
        {
            var existing = await Accounts.ListAsync(ct);
            foreach (var adapter in Venues.All)
            {
                string id = $"{adapter.Venue}-{adapter.Environment}".ToLowerInvariant();
                if (existing.Any(a => a.Id == id)) continue;

                // Ask the broker what currency the account is actually denominated in. Guessing it
                // from the venue was wrong the moment a third venue existed, and a mis-set base
                // currency silently mis-converts every value the account reports.
                string baseCurrency = "USD";
                try
                {
                    var snapshot = await adapter.GetAccountAsync(ct);
                    if (!string.IsNullOrWhiteSpace(snapshot.BaseCurrency)) baseCurrency = snapshot.BaseCurrency;
                }
                catch { /* an unreachable venue keeps the default until it next connects */ }

                await Accounts.UpsertAsync(new TradingAccount
                {
                    Id = id,
                    Venue = adapter.Venue,
                    Environment = adapter.Environment,
                    DisplayName = adapter.Capabilities.DisplayName,
                    BaseCurrency = baseCurrency,
                    // Nothing gains real-money authority implicitly; it must be granted explicitly.
                    Authority = adapter.Environment switch
                    {
                        TradingEnvironment.Paper => ExecutionAuthority.Paper,
                        TradingEnvironment.Demo => ExecutionAuthority.Demo,
                        _ => ExecutionAuthority.Observe
                    }
                }, ct);
            }
        }

        /// <summary>
        /// Scheduled reconciliation and health sweeps. These are what turn "the platform is correct
        /// when someone looks at it" into "the platform notices on its own".
        /// </summary>
        private void StartBackgroundLoops()
        {
            backgroundCts = new CancellationTokenSource();
            var ct = backgroundCts.Token;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), ct);
                        await Orders.ReconcileOutstandingAsync(ct);
                        await Reconciliation.ReconcileAllAsync("scheduled", ct);
                        var state = await Portfolio.BuildRiskStateAsync(ct);
                        var operations = await Orders.BuildOperationalStateAsync(null, ct);
                        Emergency.EvaluateAutomaticTriggers(state, operations, limits);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { await parent.ServiceLogError(ex, "scheduled reconciliation loop"); }
                }
            }, ct);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), ct);
                        await Health.SweepAsync(ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { await parent.ServiceLogError(ex, "health sweep loop"); }
                }
            }, ct);

            // Journal completed orders so the decision record exists without anyone remembering to
            // write it.
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(2), ct);
                        var recent = await OrderRepo.ListFilledSinceAsync(DateTime.UtcNow.AddHours(-6), ct);
                        foreach (var order in recent) await Journal.RecordOrderAsync(order, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { await parent.ServiceLogError(ex, "journal writer loop"); }
                }
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            try { backgroundCts?.Cancel(); } catch { }
            backgroundCts?.Dispose();
            await Task.CompletedTask;
        }

        private sealed class ReportingCurrencySetting
        {
            public string Currency { get; set; } = "GBP";
        }
    }
}
