using DSharpPlus.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    /// <summary>
    /// Buys CS2 items on CSFloat below what they can be sold for — on the Steam Community Market (then
    /// converting the Steam wallet back to CSFloat cash) or by relisting on CSFloat — and drives each
    /// purchase through trade, Valve's 7-day trade protection, and resale.
    ///
    /// Rebuilt Oct 2026 after a year without a single opportunity: Steam's May 2026 market redesign broke
    /// the only price endpoint it used, its profit model needed a Steam/CSFloat price ratio the market never
    /// offers, its conversion coefficient was silently a hard-coded 0.75, and its startup loaded every
    /// listing it had ever evaluated before registering routes. See Engine/ for the replacement pipeline.
    /// </summary>
    public class CS2ArbitrageBot : OmniService, ISnipeHost, IExitHost
    {
        // ── Public state other code reads ──
        public double? ExchangeRate;
        public IReadOnlyDictionary<string, double>? UsdExchangeRates { get; private set; }
        public SteamAPIWrapper steamAPIWrapper = null!;
        public CSFloatWrapper csFloatWrapper = null!;
        public Scanalytics scanalytics = null!;
        public CSFloatWrapper.CSFloatAccountInformation? csfloatAccountInformation;
        public SteamAPIProfileWrapper.SteamBalance? steamBalance;
        public SnipeEngine? Engine { get; private set; }
        public ExitManager? Exits { get; private set; }
        public LiquidityPlan? CurrentLiquidityPlan { get; private set; }
        public ConversionModelSnapshot? CurrentConversionModel { get; private set; }

        public CancellationToken ServiceCancellation => cancellationToken.Token;
        public double CurrentConversionCoefficient =>
            CurrentConversionModel is { } model && DateTime.UtcNow - model.ComputedAtUtc < TimeSpan.FromHours(48)
                ? model.Coefficient
                : engineSettings.DefaultConversionCoefficient;

        // ── Internals ──
        private readonly Omnipotent.Services.KliveAPI.KliveAPI? injectedApi;
        private readonly SteamReferencePrices referencePrices = new();
        private volatile EngineSettings engineSettings = new();
        private readonly TaskCompletionSource initialised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch startupClock = new();
        private volatile string startupState = "starting";
        private DateTime balanceFetchedUtc = DateTime.MinValue;
        private readonly SemaphoreSlim balanceLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> alertCooldowns = new();
        private readonly ConcurrentDictionary<string, byte> salesInFlight = new();
        private readonly ConcurrentQueue<DateTime> unboughtAlertTimes = new();
        private DateTime lastSteamIssueLogUtc = DateTime.MinValue;
        private long legacyFilesOnDisk = -1;
        private DateTime? lastTradePollUtc;
        private string? lastTradePollError;
        // The poll in progress, so one that never returns is reported (and not stacked on) instead of silently
        // ending trade tracking: on the live server the monitor's last poll was 2026-10-26 21:46, with no error,
        // two days before anyone looked — while a relist it should have been following sat unsettled.
        private Task? tradePollInFlight;
        private DateTime tradePollStartedUtc;
        internal static readonly TimeSpan TradePollTimeout = TimeSpan.FromMinutes(3);
        internal static readonly TimeSpan TradePollStuckAlertAfter = TimeSpan.FromMinutes(30);
        private bool automationEnabled;
        private ExitSignalsProvider exitSignals = null!;
        private MarketDriftModel marketModel = new();
        private volatile TradeTimeline tradeTimeline = new();
        private (DateTime At, ExitCalibrationSnapshot Value) exitCalibration = (DateTime.MinValue, ExitCalibrationSnapshot.Neutral);
        private readonly ConcurrentDictionary<string, byte> reviewsInFlight = new();
        // Discord alert cooldowns, kept on disk so a restart doesn't send every standing alert again (see AlertLedger).
        private AlertLedger alertLedger = new(null);
        // How long each start-up step took: the live start took 237 s to "running" with nothing saying where.
        private readonly List<(string Step, long Ms)> startupSteps = new();
        private string? startupStepName;
        private long startupStepStartedMs;
        // When the trade monitor last read the seller side of /me/trades, and what it last said.
        private DateTime lastSellerTradesUtc;
        private string? lastSellerTradesError;

        private sealed class JsonSnapshot
        {
            public DateTime GeneratedAtUtc { get; set; }
            public string? Json { get; set; }
            [JsonIgnore] public string? CompactJson { get; set; }
        }
        private JsonSnapshot? analyticsSnapshot;
        private JsonSnapshot? balanceHistorySnapshot;
        private JsonSnapshot? liquidityPlanSnapshot;
        private readonly SemaphoreSlim analyticsRefreshRequested = new(0, 1);

        internal static bool IsAnalyticsSnapshotFresh(DateTime generatedAtUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - generatedAtUtc;
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(3);
        }

        public CS2ArbitrageBot() : this(null) { }

        /// <summary>Program.cs hands over KliveAPI so routes register without the reflection lookup (see MemeScraper).</summary>
        public CS2ArbitrageBot(Omnipotent.Services.KliveAPI.KliveAPI? kliveApi)
        {
            name = "CS2ArbitrageBot";
            threadAnteriority = ThreadAnteriority.High;
            injectedApi = kliveApi;
        }

        private static string LabsDirectory => OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotLabsDirectory);
        private static string LiquidityPlanSnapshotPath => Path.Combine(LabsDirectory, "liquidity-plan.snapshot.json");
        private static string ConversionModelPath => Path.Combine(LabsDirectory, "conversion-model.json");
        private static string BookCachePath => Path.Combine(LabsDirectory, "steam-book-cache.json");
        private static string MarketModelPath => Path.Combine(LabsDirectory, "market-drift-model.json");
        private static string TradeTimelinePath => Path.Combine(LabsDirectory, "trade-timeline.json");

        // ───────────────────────────── Startup ─────────────────────────────

        protected override async void ServiceMain()
        {
            startupClock.Start();
            // Routes first: they used to wait behind a synchronous load of every listing ever evaluated.
            _ = CreateRoutesAsync();
            try
            {
                await InitialiseAsync();
                initialised.TrySetResult();
            }
            catch (Exception ex)
            {
                startupState = "failed to start: " + ex.Message;
                initialised.TrySetException(ex);
                await ServiceLogError(ex, "CS2ArbitrageBot failed to initialise; it is idle until restarted.");
                await AlertKlivesAsync("init-failed", "CS2 Arbitrage — failed to start", ex.Message, TimeSpan.FromHours(6));
                return;
            }

            GetTimeManagerService().TaskDue += TimeManager_TaskDue;
            ServiceQuitRequest += () => { try { scanalytics.FlushAsync().Wait(TimeSpan.FromSeconds(5)); } catch { } };
            var ct = cancellationToken.Token;
            _ = Task.Run(() => HousekeepingLoopAsync(ct));

            automationEnabled = OmniPaths.CheckIfOnServer() || Environment.GetEnvironmentVariable("CS2_ENGINE_LOCAL") == "1";
            if (!automationEnabled)
            {
                startupState = "ready (automation disabled: not the server; set CS2_ENGINE_LOCAL=1 to override)";
                await ServiceLog($"CS2ArbitrageBot ready in {startupClock.ElapsedMilliseconds} ms; scanning/buying/selling only run on the server.");
                return;
            }

            _ = Task.Run(async () =>
            {
                try { await steamAPIWrapper.SteamAPIWrapperInitialisation(); }
                catch (Exception ex) { await ServiceLogError(ex, "Steam login initialisation failed; selling and Steam balance are unavailable until it works."); }
            });
            Engine!.Start(ct);
            _ = Task.Run(() => TradeMonitorLoopAsync(ct));
            _ = Task.Run(() => SaleSchedulerLoopAsync(ct));
            _ = Task.Run(() => ListingReviewLoopAsync(ct));
            _ = Task.Run(() => HealthLoopAsync(ct));
            if (await GetTimeManagerService().GetTask("RecordCSFloatAndSteamBalance") == null) _ = RecordDailyBalancesAsync();
            startupState = "running";
            StartupStep(null);
            await ServiceLog($"CS2ArbitrageBot running {startupClock.ElapsedMilliseconds} ms after start (k = {CurrentConversionCoefficient:F3}); " +
                             $"steps: {string.Join(", ", StartupStepsSnapshot().Select(s => $"{s.Step} {s.Ms / 1000.0:0.0} s"))}.");
        }

        /// <summary>Closes the step in progress (timing it) and opens the next; null just closes.</summary>
        private void StartupStep(string? name)
        {
            long now = startupClock.ElapsedMilliseconds;
            lock (startupSteps)
            {
                if (startupStepName != null) startupSteps.Add((startupStepName, now - startupStepStartedMs));
                startupStepName = name;
                startupStepStartedMs = now;
            }
            if (name != null) startupState = name;
        }

        private List<(string Step, long Ms)> StartupStepsSnapshot()
        {
            lock (startupSteps) return new List<(string, long)>(startupSteps);
        }

        private async Task InitialiseAsync()
        {
            StartupStep("loading settings");
            engineSettings = await LoadEngineSettingsAsync();
            string apiKey = await GetStringOmniSetting("CSFloatAPIKey", null!, sensitive: true, askKlivesForFulfillment: true);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("CSFloatAPIKey setting is empty.");
            csFloatWrapper = new CSFloatWrapper(this, apiKey);
            alertLedger = new AlertLedger(Path.Combine(LabsDirectory, "alert-cooldowns.json"));
            alertLedger.Load();

            StartupStep("loading exchange rates");
            await RefreshExchangeRatesAsync(throwOnFailure: false);

            StartupStep("loading positions and scan history");
            steamAPIWrapper = new SteamAPIWrapper(this, TimeSpan.FromMilliseconds(engineSettings.SteamRequestSpacingMs));
            scanalytics = new Scanalytics(this);
            await scanalytics.LoadAsync();
            StartupStep("loading saved models");
            await LoadSnapshotsAsync();
            exitSignals = new ExitSignalsProvider(csFloatWrapper, steamAPIWrapper.Market);
            Engine = new SnipeEngine(this);
            Exits = new ExitManager(this);
            StartupStep("reading the CSFloat balance");
            try { await RefreshBalanceAsync(true, cancellationToken.Token); }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the CSFloat balance at startup.", false); }
            StartupStep("starting loops");
            QueueAnalyticsRefresh();
        }

        private async Task LoadSnapshotsAsync()
        {
            try
            {
                if (File.Exists(ConversionModelPath))
                    CurrentConversionModel = JsonConvert.DeserializeObject<ConversionModelSnapshot>(await File.ReadAllTextAsync(ConversionModelPath));
            }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the saved conversion model.", false); }
            try
            {
                if (File.Exists(LiquidityPlanSnapshotPath))
                {
                    var saved = JsonConvert.DeserializeObject<JsonSnapshot>(await File.ReadAllTextAsync(LiquidityPlanSnapshotPath));
                    if (!string.IsNullOrWhiteSpace(saved?.Json))
                    {
                        liquidityPlanSnapshot = saved;
                        try { CurrentLiquidityPlan = JsonConvert.DeserializeObject<LiquidityPlan>(saved.Json); } catch { }
                    }
                }
            }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the saved liquidity plan.", false); }
            try
            {
                if (File.Exists(BookCachePath))
                {
                    var books = JsonConvert.DeserializeObject<List<SteamOrderBook>>(await File.ReadAllTextAsync(BookCachePath));
                    if (books != null) steamAPIWrapper.Market.Seed(books);
                }
            }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the saved Steam order-book cache.", false); }
            try
            {
                if (File.Exists(MarketModelPath))
                    marketModel = JsonConvert.DeserializeObject<MarketDriftModel>(await File.ReadAllTextAsync(MarketModelPath)) ?? new MarketDriftModel();
            }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the saved market drift model; starting from the backtested defaults.", false); }
            try
            {
                if (File.Exists(TradeTimelinePath))
                    tradeTimeline = JsonConvert.DeserializeObject<TradeTimeline>(await File.ReadAllTextAsync(TradeTimelinePath)) ?? new TradeTimeline();
            }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't read the saved trade timeline; using the measured defaults.", false); }
        }

        private async Task<EngineSettings> LoadEngineSettingsAsync()
        {
            var s = new EngineSettings();
            s.ScanningEnabled = await GetBoolOmniSetting("PerformCS2Scans", true);
            s.PurchasingEnabled = await GetBoolOmniSetting("PurchaseCSFloatArbitrageOpportunities", true);
            s.Evaluation.MinimumSteamRoiPercent = await GetIntOmniSetting("CS2ArbitrageMinimumSteamROIPercent", 10);
            s.Evaluation.MinimumRelistRoiPercent = await GetIntOmniSetting("CS2ArbitrageMinimumRelistROIPercent", 10);
            s.Evaluation.AllowRelistExit = await GetBoolOmniSetting("CS2ArbitrageAllowCSFloatRelistExit", true);
            s.Evaluation.MinimumProfitPence = await GetIntOmniSetting("CS2ArbitrageMinimumProfitPence", 40);
            // Below ~$1.80 no realistic deal clears the minimum profit, and the live feed carries ~190 new listings
            // a minute — more than one 50-listing page per poll. Skipping the noise keeps coverage complete.
            s.Evaluation.MinimumPriceCents = Math.Max(3, await GetIntOmniSetting("CS2ArbitrageMinimumListingPriceCents", 200));
            int maxDollars = await GetIntOmniSetting("CS2ArbitrageMaximumListingPriceDollars", 0);
            s.Evaluation.MaximumPriceCents = maxDollars > 0 ? maxDollars * 100 : int.MaxValue;
            s.Evaluation.SteamPriceHaircutPercent = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageSteamPriceHaircutPercent", 3), 0, 50);
            s.Evaluation.MinimumSteamBuyOrders = Math.Max(0, await GetIntOmniSetting("CS2ArbitrageMinimumSteamBuyOrders", 15));
            s.MaxSpendPerItemFraction = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageMaxSpendPerItemPercent", 40), 1, 100) / 100.0;
            s.MaxUnitsPerItem = Math.Max(1, await GetIntOmniSetting("CS2ArbitrageMaxUnitsPerItem", 3));
            s.DailySpendLimitPence = Math.Max(0, await GetIntOmniSetting("CS2ArbitrageDailySpendLimitPounds", 0)) * 100;
            s.DefaultConversionCoefficient = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageDefaultConversionPercent", 68), 40, 100) / 100.0;
            s.FeedMinInterval = TimeSpan.FromSeconds(Math.Max(5, await GetIntOmniSetting("CS2ArbitrageFeedMinIntervalSeconds", 15)));
            s.SteamRequestSpacingMs = Math.Max(250, await GetIntOmniSetting("CS2ArbitrageSteamRequestSpacingMs", 1000));
            s.AlertOnUnboughtOpportunities = await GetBoolOmniSetting("CS2ArbitrageAlertOnUnboughtOpportunities", true);
            // The relist exit pays this account's actual CSFloat seller fee (2% today, read from /me).
            if (csfloatAccountInformation?.Fee is double fee and > 0 and < 0.5)
            {
                s.Evaluation.CSFloatSellerFee = fee;
                s.Exit.SellerFee = fee;
            }
            // Exit model economics (see Docs/cs2-arbitrage-bot.md, "Exit model").
            s.Exit.CapitalCostPerDay = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageCapitalCostBasisPointsPerDay", 20), 0, 500) / 10_000.0;
            s.Exit.RiskAversion = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageRiskAversionTenths", 20), 0, 200) / 10.0;
            s.Exit.RelistHorizonDays = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageRelistHorizonDays", 21), 3, 120);
            s.Exit.MinimumSteamBuyOrders = s.Evaluation.MinimumSteamBuyOrders;
            s.AutoManageRelists = await GetBoolOmniSetting("CS2ArbitrageAutoManageRelists", true);
            s.SteamConfirmHours = Math.Clamp(await GetIntOmniSetting("CS2ArbitrageSteamConfirmHours", 12), 0, 168);
            s.SteamWalletCapUsd = Math.Max(0, await GetIntOmniSetting("CS2ArbitrageSteamWalletCapDollars", 2000));
            return s;
        }

        private async Task RefreshExchangeRatesAsync(bool throwOnFailure)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var rates = await CSFloatWrapper.GetExchangeRatesAsync(csFloatWrapper.Client, cancellationToken.Token);
                    UsdExchangeRates = rates;
                    ExchangeRate = rates["gbp"];
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= 3)
                    {
                        await ServiceLogError(ex, "Couldn't fetch exchange rates from CSFloat; valuation waits until they load.", false);
                        if (throwOnFailure) throw;
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
            }
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (e?.taskName == null) return;
            // Only where automation runs: off the server this would trigger a Steam login (and its Discord prompts).
            if (e.taskName == "RecordCSFloatAndSteamBalance" && automationEnabled) _ = RecordDailyBalancesAsync();
            // Legacy chains ("SnipeCS2Deals", "CompareLiquidItemOptions", "SellCS2ArbitrageListingOnSteam…") are
            // superseded by the engine's own loops and the persisted sale schedule; let them lapse.
        }

        private async Task RecordDailyBalancesAsync()
        {
            try
            {
                if (initialised.Task.IsCompletedSuccessfully) await scanalytics.RecordAccountInfoAsync();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error recording daily CS2 balances.");
            }
            await ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(12), "RecordCSFloatAndSteamBalance", "CS2ArbitrageLabs", "Record daily balances of CSFloat account and Steam account.", false);
        }

        // ───────────────────────────── ISnipeHost ─────────────────────────────

        CSFloatWrapper ISnipeHost.CSFloat => csFloatWrapper;
        SteamMarketClient ISnipeHost.SteamMarket => steamAPIWrapper.Market;
        SteamReferencePrices ISnipeHost.ReferencePrices => referencePrices;
        Scanalytics ISnipeHost.Analytics => scanalytics;
        EngineSettings ISnipeHost.Settings => engineSettings;
        double ISnipeHost.GbpPerUsd => ExchangeRate ?? 0;
        double ISnipeHost.ConversionCoefficient => CurrentConversionCoefficient;
        int? ISnipeHost.BalanceCents => csfloatAccountInformation?.BalanceInCents;
        ExitSignalsProvider ISnipeHost.ExitSignals => exitSignals;
        MarketDriftModel ISnipeHost.MarketModel => marketModel;
        ExitCalibrationSnapshot ISnipeHost.ExitCalibration => CurrentExitCalibration;
        string? ISnipeHost.OwnSteamId => OwnSteamId;

        /// <summary>What the bot's own exits say about the exit model (recomputed at most every 10 minutes).</summary>
        public ExitCalibrationSnapshot CurrentExitCalibration
        {
            get
            {
                var cached = exitCalibration;
                if (DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return cached.Value;
                var fresh = scanalytics == null ? ExitCalibrationSnapshot.Neutral : ExitCalibration.Compute(scanalytics.PurchasesSnapshot(), DateTime.UtcNow);
                exitCalibration = (DateTime.UtcNow, fresh);
                return fresh;
            }
        }

        public string? OwnSteamId => string.IsNullOrEmpty(csfloatAccountInformation?.SteamID) ? null : csfloatAccountInformation.SteamID;

        private object CurrentExitEnvironmentSummary()
        {
            var env = CurrentExitEnvironment;
            return new
            {
                timeline = env.Timeline.Describe(),
                converterHoldGrowth = Math.Round(env.ConverterHoldGrowth, 4),
                converterHoldSigma = Math.Round(env.ConverterHoldSigma, 4),
                steamWalletHeadroomGbp = env.SteamWalletHeadroomPence is double h ? Math.Round(h / 100, 2) : (double?)null,
                csfloatSellingPaused = env.CSFloatSellingPaused,
            };
        }

        ExitEnvironment ISnipeHost.ExitEnvironment => CurrentExitEnvironment;
        ExitEnvironment IExitHost.ExitEnvironment => CurrentExitEnvironment;

        /// <summary>
        /// The locks, holds and limits every exit decision runs against: the measured trade timeline (with the human
        /// confirmation step from settings), the converters' price risk over Steam's 7-day hold, the room left in the
        /// Steam wallet (after Steam sales still pending), Steam's listing limit, and whether CSFloat selling is paused.
        /// </summary>
        public ExitEnvironment CurrentExitEnvironment
        {
            get
            {
                var settings = engineSettings;
                var timeline = tradeTimeline.Clone();
                timeline.SteamConfirmDays = settings.SteamConfirmHours / 24.0;
                var (growth, sigma) = ConversionModel.HoldRisk(CurrentConversionModel, marketModel, timeline.ConverterHoldDays);
                double fx = ExchangeRate ?? 0;
                double? headroom = null;
                if (steamBalance is { } wallet && fx > 0 && settings.SteamWalletCapUsd > 0)
                {
                    double pendingProceeds = scanalytics?.PurchasesSnapshot()
                        .Where(p => p.CurrentStrategicStage == StrategicStages.WaitingForMarketSaleOnSteam && p.ActualSalePriceOnSteam > 0)
                        .Sum(p => ArbitrageMath.SteamSellerReceives((int)Math.Round(p.ActualSalePriceOnSteam * 100))) ?? 0;
                    headroom = settings.SteamWalletCapUsd * 100 * fx - wallet.TotalBalanceInPounds * 100 - pendingProceeds;
                }
                return new ExitEnvironment
                {
                    Timeline = timeline,
                    ConverterHoldGrowth = growth,
                    ConverterHoldSigma = sigma,
                    SteamWalletHeadroomPence = headroom,
                    SteamMaxListingPence = fx > 0 && settings.SteamMaxListingUsd > 0 ? settings.SteamMaxListingUsd * 100 * fx : null,
                    CSFloatSellingPaused = csfloatAccountInformation?.Away ?? false,
                    SteamUnavailableReason = steamAPIWrapper?.profileWrapper?.SessionProblem,
                };
            }
        }

        // ───────────────────────────── IExitHost ─────────────────────────────

        ExitSignalsProvider IExitHost.ExitSignals => exitSignals;
        EngineSettings IExitHost.Settings => engineSettings;
        double IExitHost.GbpPerUsd => ExchangeRate ?? 0;
        double IExitHost.ConversionCoefficient => CurrentConversionCoefficient;
        MarketDriftModel IExitHost.MarketModel => marketModel;
        ExitCalibrationSnapshot IExitHost.ExitCalibration => CurrentExitCalibration;
        string? IExitHost.OwnSteamId => OwnSteamId;
        Task<SteamSellResult> IExitHost.SellOnSteamAsync(PurchasedListing position, int pricePence) => SellSkinOnSteam(position, pricePence);
        Task<string?> IExitHost.ListOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct) => ListOnCSFloatAsync(position, priceCents, ct);
        Task IExitHost.SaveAsync(PurchasedListing position) => scanalytics.UpdatePurchasedListing(position);
        Task IExitHost.NotifyAsync(PurchasedListing position, string title, string message) =>
            SendEmbedAsync(title, message, DiscordColor.Teal, position.comparison?.CSFloatListing.ImageURL);
        Task IExitHost.AlertAsync(string key, string title, string message) => AlertKlivesAsync(key, title, message, TimeSpan.FromHours(12));
        void IExitHost.Log(string message) => _ = ServiceLog(message, false);

        async Task<bool> IExitHost.RepriceOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct)
        {
            try
            {
                int? confirmed = await csFloatWrapper.UpdateListingPriceAsync(position.CSFloatResaleListingID, priceCents, ct);
                if (confirmed is int c && c != priceCents)
                    await ServiceLogError($"CSFloat re-priced {position.ItemMarketHashName} to {c}c instead of {priceCents}c.", false);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                // CSFloat only re-prices a live listing ("you can only modify listed contracts"), so the listing has
                // sold or been taken down. The exit manager now asks CSFloat which (ExitManager.SettleRelistAsync) and
                // settles the position, saying so itself; only a listing CSFloat still shows as live is alerted.
                await ServiceLog($"CSFloat refused to re-price {position.ItemMarketHashName}: {ex.Message}", false);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ServiceLogError(ex, $"Couldn't re-price the CSFloat listing for {position.ItemMarketHashName}.", false);
                return false;
            }
        }

        async Task IExitHost.DelistFromCSFloatAsync(PurchasedListing position, CancellationToken ct) =>
            await csFloatWrapper.DelistAsync(position.CSFloatResaleListingID, ct);

        Task<RelistFacts> IExitHost.InspectRelistAsync(PurchasedListing position, CancellationToken ct) => InspectRelistAsync(position, ct);

        /// <summary>
        /// The relist's own state on CSFloat (one single-listing request) and, when it is not live, whether the unit is
        /// still in the inventory (read through CSFloat, so no Steam login is needed). Unreadable parts stay null.
        /// </summary>
        private async Task<RelistFacts> InspectRelistAsync(PurchasedListing p, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(p.CSFloatResaleListingID)) return RelistFacts.Unknown;
            string? state = null;
            int? price = null;
            try
            {
                var listing = await csFloatWrapper.GetListingAsync(p.CSFloatResaleListingID, ct);
                if (listing == null) state = "missing";
                else
                {
                    state = string.IsNullOrWhiteSpace(listing.State) ? null : listing.State.Trim().ToLowerInvariant();
                    price = listing.PriceCents > 0 ? listing.PriceCents : null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ServiceLog($"Couldn't read CSFloat listing {p.CSFloatResaleListingID} ({p.ItemMarketHashName}): {ex.Message}", false);
            }

            bool? inInventory = null;
            if (state is not null and not "listed")
            {
                try
                {
                    var inventory = await csFloatWrapper.GetInventoryAsync(ct);
                    // An empty answer is likelier a hiccup than an emptied inventory: never settle on it.
                    if (inventory.Count > 0) inInventory = PositionsWithoutAUnit(new[] { p }, inventory).Count == 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServiceLog($"Couldn't read the inventory while checking the relist of {p.ItemMarketHashName}: {ex.Message}", false);
                }
            }
            var facts = new RelistFacts(state, price, inInventory);
            await ServiceLog($"Relist check for {p.ItemMarketHashName} ({p.CSFloatResaleListingID}): listing {state ?? "unreadable"}, unit {(inInventory == null ? "unknown" : inInventory.Value ? "still in the inventory" : "gone")}.", false);
            return facts;
        }

        public async Task RefreshBalanceAsync(bool force, CancellationToken ct)
        {
            if (!force && DateTime.UtcNow - balanceFetchedUtc < TimeSpan.FromSeconds(60)) return;
            await balanceLock.WaitAsync(ct);
            try
            {
                if (!force && DateTime.UtcNow - balanceFetchedUtc < TimeSpan.FromSeconds(60)) return;
                csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();
                balanceFetchedUtc = DateTime.UtcNow;
            }
            finally
            {
                balanceLock.Release();
            }
        }

        void ISnipeHost.DebitBalance(int cents)
        {
            var account = csfloatAccountInformation;
            if (account == null) return;
            account.BalanceInCents = Math.Max(0, account.BalanceInCents - cents);
            account.BalanceInPence = CentsToPence(account.BalanceInCents);
            account.BalanceInPounds = account.BalanceInPence / 100f;
        }

        private int CentsToPence(int cents) => ArbitrageMath.UsdCentsToPenceFloor(cents, ExchangeRate ?? 0);

        /// <summary>Units of this item bought and not yet sold (concentration limit).</summary>
        public int OpenPositionsFor(string marketHashName) =>
            scanalytics.PurchasesSnapshot().Count(p => p.ItemMarketHashName == marketHashName && IsHolding(p));

        /// <summary>Anything not finished (shown on the status route).</summary>
        private static bool IsOpen(PurchasedListing p) =>
            p.CurrentStrategicStage is not (StrategicStages.StrategyCompleted or StrategicStages.TradeCancelled or StrategicStages.LeftInventory);

        /// <summary>The item itself is still ours: bought, in trade, in protection, or relisted on CSFloat.</summary>
        private static bool IsHolding(PurchasedListing p) =>
            p.CurrentStrategicStage <= StrategicStages.JustRetrieved || p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale;

        int ISnipeHost.SpentTodayPence => scanalytics.PurchasesSnapshot()
            .Where(p => p.TimeOfPurchase.ToUniversalTime().Date == DateTime.UtcNow.Date && p.CurrentStrategicStage != StrategicStages.TradeCancelled)
            .Sum(p => p.PurchaseCostPence > 0 ? p.PurchaseCostPence : (int)Math.Round((p.comparison?.CSFloatListing.PriceInPounds ?? 0) * 100));

        double ISnipeHost.TargetConversionVolumePence
        {
            get
            {
                double wallet = (steamBalance?.TotalBalanceInPounds ?? 0) * 100;
                double pendingSteam = scanalytics.PurchasesSnapshot()
                    .Where(p => IsHolding(p) && p.PlannedExit == nameof(ExitRoute.SteamMarket))
                    .Sum(p => p.PurchaseCostPence * 1.25);
                return Math.Max(2500, wallet + pendingSteam);
            }
        }

        async Task ISnipeHost.OnPurchasedAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book)
        {
            double fx = ExchangeRate ?? 0;
            var chosen = evaluation.BestRoute == ExitRoute.CSFloatRelist ? evaluation.Relist : evaluation.Steam;
            // The exit model's valuation (risk-adjusted, after time and fees) when it ran; the screen's otherwise.
            var plan = evaluation.ExitPlan;
            int expectedCash = plan?.Best != null ? (int)Math.Floor(plan.Best.CertaintyEquivalentPence) : chosen.NetCashPence;
            int expectedProfit = plan?.Best != null ? plan.ProfitPence : chosen.ProfitPence;
            double expectedRoi = plan?.Best != null ? plan.Roi : chosen.Roi;
            int anchor = SnipeEngine.PurchaseAnchorCents(listing);
            var purchase = new PurchasedListing
            {
                comparison = ScannedComparison.FromEvaluation(listing, book, evaluation, fx),
                CSFloatListingID = listing.Id,
                TimeOfPurchase = DateTime.Now,
                ItemMarketHashName = listing.MarketHashName,
                ItemFloatValue = (float)(listing.FloatValue ?? 0),
                CurrentStrategicStage = StrategicStages.WaitingForCSFloatSellerToAcceptSale,
                PlannedExit = evaluation.BestRoute.ToString(),
                PurchasePriceCents = listing.PriceCents,
                PurchaseCostPence = evaluation.CostPence,
                ExpectedNetCashPence = expectedCash,
                ExpectedAbsoluteProfitInPence = expectedProfit,
                ExpectedAbsoluteProfitInPounds = expectedProfit / 100f,
                ExpectedProfitPercentage = (float)(expectedRoi * 100),
                ConversionCoefficientAtPurchase = evaluation.ConversionCoefficient,
                PurchaseSource = evaluation.Source,
                AnchorCentsAtPurchase = anchor,
                AnchorFloatFactor = listing.BasePriceCents is > 0 && anchor > 0 ? anchor / (double)listing.BasePriceCents.Value : 1,
                PurchasePlan = plan == null ? null : ExitDecision.From(plan, "purchase", "buy", book?.PriceAtBuyDepth(3) ?? 0, anchor),
            };
            await scanalytics.UpdatePurchasedListing(purchase);
            await ServiceLog($"Bought {listing.MarketHashName} ({listing.Id}) for ${listing.PriceCents / 100.0:F2}: {evaluation.Reason}");
            await SendEmbedAsync("CS2 Snipe Purchased ✓", DescribeOpportunity(listing, evaluation) + "\n\nThe seller now has to accept and send the trade.", DiscordColor.Green, listing.ImageUrl);
        }

        async Task ISnipeHost.OnOpportunityNotBoughtAsync(CSFloatListing listing, OpportunityEvaluation evaluation, string why)
        {
            // A listing that stays up is re-screened every sweep with the same verdict. Logged every time, one
            // listing filled 386 of the 4000-line log buffer in a day and pushed everything else out of it.
            if (TryTakeCooldown("unbought-log:" + listing.Id, TimeSpan.FromHours(6)))
                await ServiceLog($"Opportunity not bought — {listing.MarketHashName} ({listing.Id}): {why}");
            if (!engineSettings.AlertOnUnboughtOpportunities) return;
            // At most one alert per item per hour and ten per hour overall.
            if (!TryTakeCooldown("unbought:" + listing.MarketHashName, TimeSpan.FromHours(1))) return;
            DateTime now = DateTime.UtcNow;
            while (unboughtAlertTimes.TryPeek(out var t) && now - t > TimeSpan.FromHours(1)) unboughtAlertTimes.TryDequeue(out _);
            if (unboughtAlertTimes.Count >= 10) return;
            unboughtAlertTimes.Enqueue(now);
            await SendEmbedAsync("CS2 Opportunity Not Bought", DescribeOpportunity(listing, evaluation) + $"\n\n**Not bought:** {why}", DiscordColor.Orange, listing.ImageUrl);
        }

        async Task ISnipeHost.OnConversionModelComputedAsync(ConversionModelSnapshot snapshot, LiquidityPlan plan)
        {
            CurrentConversionModel = snapshot;
            CurrentLiquidityPlan = plan;
            var planSnapshot = new JsonSnapshot { GeneratedAtUtc = DateTime.UtcNow, Json = JsonConvert.SerializeObject(plan) };
            Volatile.Write(ref liquidityPlanSnapshot, planSnapshot);
            Directory.CreateDirectory(LabsDirectory);
            await GetDataHandler().WriteToFile(ConversionModelPath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            await GetDataHandler().WriteToFile(LiquidityPlanSnapshotPath, JsonConvert.SerializeObject(planSnapshot));
            await ServiceLog($"Conversion model: k = {snapshot.Coefficient:F3} ({snapshot.Basis}).", false);
            QueueAnalyticsRefresh();
        }

        void ISnipeHost.Log(string message) => _ = ServiceLog(message, false);
        void ISnipeHost.LogError(Exception? ex, string message)
        {
            if (ex != null) _ = ServiceLogError(ex, message, false);
            else _ = ServiceLogError(message, false);
        }

        /// <summary>Throttled sink for Steam client errors (the client reports every failed fetch).</summary>
        public void NoteSteamIssue(string message)
        {
            if (DateTime.UtcNow - lastSteamIssueLogUtc < TimeSpan.FromMinutes(1)) return;
            lastSteamIssueLogUtc = DateTime.UtcNow;
            _ = ServiceLogError("Steam market data: " + message, false);
        }

        private string DescribeOpportunity(CSFloatListing listing, OpportunityEvaluation e)
        {
            string Exit(ExitEstimate x, string label) => x.Available
                ? $"{label}: **{x.Roi:P1}** (profit £{x.ProfitPence / 100.0:F2}, cash back £{x.NetCashPence / 100.0:F2})"
                : $"{label}: n/a ({x.UnavailableReason})";
            return $"**{listing.MarketHashName}**" + (listing.FloatValue is double f ? $" (float {f:F5})" : "") + "\n" +
                   $"CSFloat price: **${listing.PriceCents / 100.0:F2}** (£{e.CostPence / 100.0:F2}); CSFloat value ${(listing.BasePriceCents ?? 0) / 100.0:F2}\n" +
                   $"Steam: buy order £{e.SteamHighestBuyOrderPence / 100.0:F2}, ask £{e.SteamLowestSellOrderPence / 100.0:F2}, {e.SteamBuyOrderCount} orders\n" +
                   Exit(e.Steam, $"Sell on Steam (k={e.ConversionCoefficient:F2})") + "\n" +
                   Exit(e.Relist, "Relist on CSFloat") + "\n" +
                   (e.ExitModelChecked
                       ? $"**Exit model** (live evidence; worth {e.BestRoi:P1} after time, risk and fees): {e.ExitModelSummary}" +
                         (e.CSFloatSalesPerDay > 0 ? $"\nCSFloat demand: {e.CSFloatSalesPerDay:0.##} sales/day" : "") +
                         (e.MissingSignals is { Count: > 0 } missing ? $"\nMissing evidence: {string.Join("; ", missing)}" : "") + "\n"
                       : "") +
                   $"Found via {e.Source}, listed {FormatAgo(DateTime.UtcNow - listing.CreatedAtUtc)}\n" +
                   $"{listing.ListingUrl}\nhttps://steamcommunity.com/market/listings/730/{Uri.EscapeDataString(listing.MarketHashName)}";
        }

        /// <summary>"45 seconds ago", "12 minutes ago", "3 hours ago", "9 days ago" — largest whole unit, rounded down.</summary>
        internal static string FormatAgo(TimeSpan age)
        {
            static string Unit(double n, string unit) => $"{(long)n} {unit}{((long)n == 1 ? "" : "s")} ago";
            if (age < TimeSpan.FromSeconds(1)) return "just now";
            if (age.TotalMinutes < 1) return Unit(age.TotalSeconds, "second");
            if (age.TotalHours < 1) return Unit(age.TotalMinutes, "minute");
            if (age.TotalDays < 1) return Unit(age.TotalHours, "hour");
            return Unit(age.TotalDays, "day");
        }

        // ───────────────────────────── Trades ─────────────────────────────

        /// <summary>
        /// Follows every open purchase (and relisted item) through CSFloat's trade states. Polls every 90s
        /// while anything is in flight and every 15 minutes otherwise — the trades endpoint allows 100 per window.
        /// </summary>
        private async Task TradeMonitorLoopAsync(CancellationToken ct)
        {
            DateTime nextPoll = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var purchases = scanalytics.PurchasesSnapshot();
                    bool active = purchases.Any(p => p.CurrentStrategicStage < StrategicStages.JustRetrieved || p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale);
                    if (tradePollInFlight is { IsCompleted: false })
                    {
                        // Never start a second poll behind one that has not returned: it would hang the same way.
                        TimeSpan stuck = DateTime.UtcNow - tradePollStartedUtc;
                        lastTradePollError = $"the trade poll started {tradePollStartedUtc:u} has not returned ({FormatAgo(stuck)})";
                        if (stuck > TradePollStuckAlertAfter)
                            await AlertKlivesAsync("trade-poll-stuck", "CS2 Arbitrage — trade tracking is stuck",
                                $"The CSFloat trade monitor's poll has not returned for {FormatAgo(stuck).Replace(" ago", "")}. Sales, deliveries and resales are not being followed until it does (a restart clears it).",
                                TimeSpan.FromHours(12));
                    }
                    else if (DateTime.UtcNow >= nextPoll && (active || lastTradePollUtc == null || DateTime.UtcNow - lastTradePollUtc > TimeSpan.FromMinutes(15)))
                    {
                        nextPoll = DateTime.UtcNow + (active ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(15));
                        tradePollStartedUtc = DateTime.UtcNow;
                        tradePollInFlight = PollTradesOnceAsync(purchases, ct);
                        bool returned = true;
                        try { await tradePollInFlight.WaitAsync(TradePollTimeout, ct); }
                        catch (TimeoutException)
                        {
                            returned = false;
                            lastTradePollError = $"the trade poll did not return within {TradePollTimeout.TotalMinutes:0} minutes";
                            await ServiceLogError(new TimeoutException(lastTradePollError), "CS2 trade monitor poll is hanging.", false);
                        }
                        if (returned)
                        {
                            lastTradePollUtc = DateTime.UtcNow;
                            lastTradePollError = null;
                        }
                    }
                }
                catch (CSFloatRateLimitedException ex)
                {
                    nextPoll = ex.RetryAtUtc;
                    lastTradePollError = ex.Message;
                }
                catch (CSFloatAuthException ex)
                {
                    nextPoll = DateTime.UtcNow.AddMinutes(30);
                    lastTradePollError = ex.Message;
                    await AlertKlivesAsync("csfloat-auth", "CS2 Arbitrage — CSFloat API key rejected", ex.Message, TimeSpan.FromHours(6));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastTradePollError = ex.Message;
                    await ServiceLogError(ex, "CS2 trade monitor poll failed.", false);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task PollTradesOnceAsync(List<PurchasedListing> purchases, CancellationToken ct)
        {
            JArray trades = await csFloatWrapper.GetTradesAsync(100, ct);
            // While a relist is open, the seller side too: the newest 100 trades were all purchases on the live account,
            // so a relist's sale could sit outside them and never be followed (salesObserved 0 on 2026-11-13).
            if (purchases.Any(p => p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale) && DateTime.UtcNow - lastSellerTradesUtc >= TimeSpan.FromMinutes(5))
            {
                lastSellerTradesUtc = DateTime.UtcNow;
                try
                {
                    trades = MergeTrades(trades, await csFloatWrapper.GetTradesAsync(100, ct, role: "seller"));
                    lastSellerTradesError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not CSFloatAuthException)
                {
                    if (lastSellerTradesError != ex.Message) await ServiceLog($"Couldn't read the seller side of the CSFloat trades: {ex.Message}", false);
                    lastSellerTradesError = ex.Message;
                }
            }
            // The same trades measure the locks and holds every exit decision depends on.
            var measured = tradeTimeline.Clone();
            measured.Measure(trades, DateTime.UtcNow);
            tradeTimeline = measured;
            // Newest first: a contract can have an old cancelled trade and a newer live one.
            var newestByContract = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var trade in trades.OfType<JObject>())
            {
                string? contractId = trade.Value<string>("contract_id");
                if (!string.IsNullOrEmpty(contractId) && !newestByContract.ContainsKey(contractId)) newestByContract[contractId] = trade;
            }

            foreach (var purchase in purchases)
            {
                TradeTransition transition = TradeTransition.None;
                string before = purchase.LastTradeState;
                if (purchase.CurrentStrategicStage < StrategicStages.JustRetrieved && newestByContract.TryGetValue(purchase.CSFloatListingID, out var trade))
                    transition = TradeProgress.ApplyPurchaseTrade(purchase, trade, DateTime.UtcNow);
                else if (purchase.CurrentStrategicStage < StrategicStages.JustRetrieved && purchase.TimeOfPurchase != default
                         && DateTime.UtcNow - purchase.TimeOfPurchase.ToUniversalTime() > TimeSpan.FromDays(14))
                {
                    // CSFloat settles or cancels a purchase within days; one missing from the recent trades
                    // after two weeks is not coming back, and would otherwise keep this loop polling every 90s.
                    purchase.CurrentStrategicStage = StrategicStages.TradeCancelled;
                    purchase.Notes = (string.IsNullOrEmpty(purchase.Notes) ? "" : purchase.Notes + Environment.NewLine)
                                     + $"No CSFloat trade found 14 days after purchase ({DateTime.UtcNow:u}).";
                    transition = TradeTransition.Cancelled;
                }
                else if (purchase.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale && !string.IsNullOrEmpty(purchase.CSFloatResaleListingID)
                         && newestByContract.TryGetValue(purchase.CSFloatResaleListingID, out var resale))
                    transition = TradeProgress.ApplyResaleTrade(purchase, resale, DateTime.UtcNow, ExchangeRate ?? 0, csfloatAccountInformation?.Fee is double fee and > 0 and < 0.5 ? fee : 0.02);
                else
                    continue;

                if (transition == TradeTransition.None && before == purchase.LastTradeState) continue;
                await scanalytics.UpdatePurchasedListing(purchase);
                if (transition != TradeTransition.None) await NotifyTradeTransitionAsync(purchase, transition);
            }
        }

        /// <summary>Both pages of trades as one newest-first list, each trade once (by id).</summary>
        internal static JArray MergeTrades(JArray first, JArray second)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<JObject>();
            foreach (var trade in first.OfType<JObject>().Concat(second.OfType<JObject>()))
            {
                string id = trade.Value<string>("id") ?? trade.ToString(Formatting.None);
                if (seen.Add(id)) all.Add(trade);
            }
            return new JArray(all
                .Select((t, i) => (Trade: t, Index: i, At: TradeProgress.ReadUtc(t["created_at"]) ?? DateTime.MinValue))
                .OrderByDescending(x => x.At).ThenBy(x => x.Index)
                .Select(x => x.Trade));
        }

        private async Task NotifyTradeTransitionAsync(PurchasedListing p, TradeTransition transition)
        {
            string name = p.ItemMarketHashName;
            string price = p.comparison?.CSFloatListing.PriceText ?? "";
            string? image = p.comparison?.CSFloatListing.ImageURL;
            switch (transition)
            {
                case TradeTransition.SellerAccepted:
                    await SendEmbedAsync("CSFloat sale accepted", $"The seller accepted the sale of **{name}** ({price}). Waiting for their trade offer.", DiscordColor.Teal, image);
                    break;
                case TradeTransition.TradeOfferSent:
                    await SendEmbedAsync("CSFloat trade offer sent — accept it", $"**{name}** ({price})\nAccept the trade offer in Steam: {p.CSFloatToSteamTradeOfferLink}", DiscordColor.Orange, image);
                    break;
                case TradeTransition.Retrieved:
                    await SendEmbedAsync("CS2 item received", $"**{name}** ({price}) is in the inventory. Trade protection lifts around {p.PredictedTimeToBeResoldOnSteam:dd/MM HH:mm} UTC; it will be sold ({p.PlannedExit}) then.", DiscordColor.Green, image);
                    break;
                case TradeTransition.Cancelled:
                    await SendEmbedAsync("CSFloat trade cancelled", $"The trade for **{name}** ({price}) was {p.LastTradeState}. CSFloat refunds the purchase.\n{p.Notes}", DiscordColor.Red, image);
                    break;
                case TradeTransition.ResaleSold:
                    await SendEmbedAsync("CSFloat resale sold — send the trade", $"**{name}** sold on CSFloat for ${p.CSFloatResalePriceCents / 100.0:F2}. Send the trade offer from CSFloat (Trades page) before the deadline.", DiscordColor.Orange, image);
                    break;
                case TradeTransition.ResaleCompleted:
                    await SendEmbedAsync("CS2 arbitrage completed ✓", $"**{name}**: profit £{p.ActualAbsoluteProfitInPounds:F2} ({p.ActualProfitPercentage:F1}%).", DiscordColor.Green, image);
                    break;
                case TradeTransition.ResaleCancelled:
                    await SendEmbedAsync("CSFloat resale cancelled", $"The resale of **{name}** was cancelled ({p.LastTradeState}). " +
                        (p.CurrentStrategicStage == StrategicStages.JustRetrieved ? "It will be re-sold automatically." : "The listing is still up."), DiscordColor.Red, image);
                    break;
            }
        }

        // ───────────────────────────── Selling ─────────────────────────────

        /// <summary>
        /// Sells items whose trade protection has lifted. The schedule lives in the purchase records, so it
        /// survives restarts — unlike the old one-shot TimeManager tasks (which also double-fired).
        /// </summary>
        private async Task SaleSchedulerLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Positions whose unit has gone (sold or moved by hand) are closed first, so nothing below tries to
                    // sell them or reports them.
                    await ReconcileHeldPositionsAsync(ct);
                    await WakeSalesWaitingForSteamAsync();

                    // Only items whose trade protection has lifted (the exact moment CSFloat reported) can be sold.
                    var due = scanalytics.PurchasesSnapshot().Where(p =>
                        p.CurrentStrategicStage == StrategicStages.JustRetrieved
                        && p.PredictedTimeToBeResoldOnSteam != default && p.PredictedTimeToBeResoldOnSteam.ToUniversalTime() <= DateTime.UtcNow
                        && p.SaleDeferredUntilUtc <= DateTime.UtcNow
                        && (p.LastSaleAttemptUtc == default || DateTime.UtcNow - p.LastSaleAttemptUtc >= TimeSpan.FromHours(6))).ToList();
                    if (due.Count > 0) await RefreshAccountForExitsAsync(ct);
                    await ReportPositionsNeedingAttentionAsync(due.Where(p => !IsAutoSellable(p, DateTime.UtcNow)).ToList());
                    foreach (var p in due)
                    {
                        if (!IsAutoSellable(p, DateTime.UtcNow)) continue;
                        if (!salesInFlight.TryAdd(p.CSFloatListingID, 0)) continue;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string outcome = await Exits!.RunSaleAsync(p, ct);
                                await ServiceLog($"Sale decision for {p.ItemMarketHashName} ({p.CSFloatListingID}): {outcome}.", false);
                            }
                            catch (Exception ex) { await ServiceLogError(ex, $"Selling {p.ItemMarketHashName} failed."); }
                            finally { salesInFlight.TryRemove(p.CSFloatListingID, out _); }
                        }, CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServiceLogError(ex, "CS2 sale scheduler tick failed.", false);
                }
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        public const int MaxSaleAttempts = 5;

        // ── Positions the inventory no longer has ──
        // Positions Klives had sold by hand stayed "in inventory" for ever: every sale attempt failed (no unit to sell),
        // each one ran out of attempts, and each was reported as "needs a manual look" — again on every restart, because
        // the alert's cooldown lived in memory. The inventory (read through CSFloat, so no Steam login is needed) is now
        // checked every hour, and a position with no unit on two checks at least an hour apart is closed.
        internal static readonly TimeSpan InventoryCheckInterval = TimeSpan.FromHours(1);
        internal static readonly TimeSpan MissingConfirmAfter = TimeSpan.FromHours(1);
        private DateTime lastInventoryCheckUtc;
        private DateTime lastInventoryCheckOkUtc;

        /// <summary>Held, unlocked positions the bot means to sell: their unit should be in the inventory.</summary>
        internal static bool ShouldBeInInventory(PurchasedListing p, DateTime nowUtc) =>
            p.CurrentStrategicStage == StrategicStages.JustRetrieved
            && (p.PredictedTimeToBeResoldOnSteam == default || p.PredictedTimeToBeResoldOnSteam.ToUniversalTime() <= nowUtc);

        /// <summary>
        /// The positions the inventory has no unit for. Units are matched per item — a skin to the unit with its float
        /// first — and only a shortfall counts, so units of the same item Klives keeps himself never close a position
        /// (at worst a sold one stays open).
        /// </summary>
        internal static List<PurchasedListing> PositionsWithoutAUnit(IEnumerable<PurchasedListing> held, IReadOnlyCollection<CSFloatWrapper.InventoryItem> inventory)
        {
            var missing = new List<PurchasedListing>();
            foreach (var group in held.GroupBy(p => p.ItemMarketHashName, StringComparer.Ordinal))
            {
                var units = inventory.Where(i => string.Equals(i.MarketHashName, group.Key, StringComparison.Ordinal)).ToList();
                // First each position whose float matches a unit exactly; then the rest by count, newest purchases first
                // (an older position is the likelier one to have been sold by hand).
                var unmatched = group.OrderByDescending(p => p.TimeOfPurchase).ToList();
                foreach (var p in unmatched.Where(p => p.ItemFloatValue > 0).ToList())
                {
                    var unit = units.FirstOrDefault(u => u.FloatValue is double f && Math.Abs(f - p.ItemFloatValue) < 1e-5);
                    if (unit == null) continue;
                    units.Remove(unit);
                    unmatched.Remove(p);
                }
                foreach (var p in unmatched)
                {
                    if (units.Count > 0) units.RemoveAt(0);
                    else missing.Add(p);
                }
            }
            return missing;
        }

        private async Task ReconcileHeldPositionsAsync(CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;
            if (now - lastInventoryCheckUtc < InventoryCheckInterval) return;
            // A sale in flight may be moving the unit into a Steam listing right now.
            var held = scanalytics.PurchasesSnapshot().Where(p => ShouldBeInInventory(p, now) && !salesInFlight.ContainsKey(p.CSFloatListingID)).ToList();
            if (held.Count == 0) return;
            lastInventoryCheckUtc = now;

            List<CSFloatWrapper.InventoryItem> inventory;
            try { inventory = await csFloatWrapper.GetInventoryAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ServiceLog($"Couldn't read the inventory to check held positions: {ex.Message}", false);
                return;
            }
            // An empty answer is far more likely a CSFloat/Steam hiccup than an emptied inventory: never close on it.
            if (inventory.Count == 0)
            {
                await ServiceLog("The inventory came back empty; not closing any position on that.", false);
                return;
            }
            lastInventoryCheckOkUtc = now;

            var missing = PositionsWithoutAUnit(held, inventory).ToHashSet();
            var closed = new List<PurchasedListing>();
            foreach (var p in held)
            {
                if (!missing.Contains(p))
                {
                    if (p.MissingFromInventorySinceUtc != default)
                    {
                        p.MissingFromInventorySinceUtc = default;
                        await scanalytics.UpdatePurchasedListing(p);
                    }
                    continue;
                }
                if (p.MissingFromInventorySinceUtc == default)
                {
                    p.MissingFromInventorySinceUtc = now;
                    await scanalytics.UpdatePurchasedListing(p);
                    continue;
                }
                if (now - p.MissingFromInventorySinceUtc < MissingConfirmAfter) continue;
                p.CurrentStrategicStage = StrategicStages.LeftInventory;
                p.WaitingForSteamLoginSinceUtc = default;
                p.Notes = (string.IsNullOrEmpty(p.Notes) ? "" : p.Notes + Environment.NewLine)
                          + $"Closed {now:u}: no longer in the inventory (missing since {p.MissingFromInventorySinceUtc:u}) — sold or moved outside the bot.";
                await scanalytics.UpdatePurchasedListing(p);
                closed.Add(p);
            }
            if (closed.Count == 0) return;
            await ServiceLog($"Closed {closed.Count} position(s) whose unit left the inventory: {string.Join(", ", closed.Select(p => p.ItemMarketHashName))}.", false);
            await SendEmbedAsync("CS2 Arbitrage — positions closed",
                $"{closed.Count} position{(closed.Count == 1 ? " is" : "s are")} no longer in the inventory (sold or moved by hand), so the bot has stopped trying to sell {(closed.Count == 1 ? "it" : "them")}:\n" +
                BulletList(closed.Select(p => $"**{p.ItemMarketHashName}**")), DiscordColor.Teal, null);
        }

        /// <summary>
        /// One message for every position that has just stopped being sold automatically — not one alert each, and
        /// never again after a restart (the time it was reported is saved on the position). A position the latest
        /// inventory check could not vouch for is left to <see cref="ReconcileHeldPositionsAsync"/> rather than reported.
        /// </summary>
        private async Task ReportPositionsNeedingAttentionAsync(List<PurchasedListing> stuck)
        {
            DateTime now = DateTime.UtcNow;
            bool inventoryVouches = now - lastInventoryCheckOkUtc < InventoryCheckInterval + TimeSpan.FromMinutes(30);
            var fresh = stuck.Where(p => p.ManualAttentionAlertedUtc == default && p.MissingFromInventorySinceUtc == default && inventoryVouches).ToList();
            if (fresh.Count == 0) return;
            foreach (var p in fresh)
            {
                p.ManualAttentionAlertedUtc = now;
                await scanalytics.UpdatePurchasedListing(p);
            }
            string Reason(PurchasedListing p) => p.SaleAttempts >= MaxSaleAttempts
                ? $"{p.SaleAttempts} sale attempts failed" + (string.IsNullOrWhiteSpace(p.LastSaleFailure) ? "" : $" (last: {p.LastSaleFailure})")
                : "a pre-rewrite position whose sale date passed long ago";
            await ServiceLogError($"{fresh.Count} CS2 position(s) are no longer sold automatically: {string.Join("; ", fresh.Select(p => p.ItemMarketHashName + " — " + Reason(p)))}", false);
            await SendEmbedAsync("CS2 Arbitrage — positions need a manual look",
                $"{(fresh.Count == 1 ? "This item is" : $"These {fresh.Count} items are")} still in the inventory but no longer sold automatically:\n" +
                BulletList(fresh.Select(p => $"**{p.ItemMarketHashName}** — {Reason(p)}")) +
                "\nYou won't be told about these again.", DiscordColor.Red, null);
        }

        private static string BulletList(IEnumerable<string> lines, int max = 15)
        {
            var all = lines.ToList();
            string shown = string.Join("\n", all.Take(max).Select(l => "• " + l));
            return all.Count > max ? shown + $"\n…and {all.Count - max} more" : shown;
        }

        /// <summary>
        /// Sales parked on a lapsed Steam login: once the login works again they go now (not at their next check), and
        /// while it doesn't, Klives is asked to approve one — a single prompt that says what is waiting, rate-limited by
        /// the profile wrapper.
        /// </summary>
        private async Task WakeSalesWaitingForSteamAsync()
        {
            var profile = steamAPIWrapper?.profileWrapper;
            if (profile == null) return;
            DateTime now = DateTime.UtcNow;
            var waiting = scanalytics.PurchasesSnapshot()
                .Where(p => p.CurrentStrategicStage == StrategicStages.JustRetrieved && p.WaitingForSteamLoginSinceUtc != default && p.MissingFromInventorySinceUtc == default)
                .ToList();
            if (waiting.Count == 0) return;
            if (profile.SessionKnownGood)
            {
                foreach (var p in waiting.Where(p => p.SaleDeferredUntilUtc > now))
                {
                    p.SaleDeferredUntilUtc = default;
                    await scanalytics.UpdatePurchasedListing(p);
                }
                return;
            }
            if (profile.SessionProblem == null || profile.LoginPromptOpen) return;
            string reason = $"{waiting.Count} item{(waiting.Count == 1 ? " is" : "s are")} waiting to be sold on Steam: " +
                            string.Join(", ", waiting.Take(5).Select(p => p.ItemMarketHashName)) + (waiting.Count > 5 ? $" and {waiting.Count - 5} more" : "") + ".";
            _ = Task.Run(async () =>
            {
                try { await profile.RequestInteractiveLoginAsync(reason); }
                catch (Exception ex) { await ServiceLogError(ex, "Asking for a Steam login failed.", false); }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Exit decisions depend on the account's state (away mode blocks CSFloat selling; the fee is the account's),
        /// so it is refreshed (at most once a minute) before any are made. A failure keeps the last known state.
        /// </summary>
        private async Task RefreshAccountForExitsAsync(CancellationToken ct)
        {
            try { await RefreshBalanceAsync(false, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await ServiceLogError(ex, "Couldn't refresh the CSFloat account before exit decisions; using the last known state.", false); }
        }

        /// <summary>
        /// Whether the scheduler may sell this position. Old (pre-rewrite) positions whose sale date passed
        /// weeks ago were most likely handled by hand; retrying them would just spam failure alerts.
        /// </summary>
        internal static bool IsAutoSellable(PurchasedListing p, DateTime nowUtc)
        {
            if (p.SaleAttempts >= MaxSaleAttempts) return false;
            bool legacy = p.PurchaseCostPence <= 0;
            return !(legacy && p.PredictedTimeToBeResoldOnSteam.ToUniversalTime() < nowUtc.AddDays(-30));
        }

        /// <summary>
        /// Re-checks live CSFloat relists with the exit model (see <see cref="ExitManager.ReviewListingAsync"/>):
        /// a listing that is not selling is re-priced, or withdrawn and sold on Steam, once the evidence says so.
        /// </summary>
        private async Task ListingReviewLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var due = scanalytics.PurchasesSnapshot().Where(p =>
                        p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale && !string.IsNullOrEmpty(p.CSFloatResaleListingID)
                        && p.NextExitReviewUtc <= DateTime.UtcNow).ToList();
                    if (due.Count > 0) await RefreshAccountForExitsAsync(ct);
                    foreach (var p in due)
                    {
                        if (!reviewsInFlight.TryAdd(p.CSFloatListingID, 0)) continue;
                        try
                        {
                            string outcome = await Exits!.ReviewListingAsync(p, ct);
                            if (outcome is not ("keep" or "skip" or "deferred")) await ServiceLog($"Relist review for {p.ItemMarketHashName}: {outcome}.", false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            p.NextExitReviewUtc = DateTime.UtcNow.AddHours(1);
                            await ServiceLogError(ex, $"Reviewing the relist of {p.ItemMarketHashName} failed.", false);
                        }
                        finally { reviewsInFlight.TryRemove(p.CSFloatListingID, out _); }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServiceLogError(ex, "CS2 relist review tick failed.", false);
                }
                try { await Task.Delay(TimeSpan.FromMinutes(10), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Lists the unit on CSFloat at the price the exit model chose; null when it is not tradable there yet.</summary>
        private async Task<string?> ListOnCSFloatAsync(PurchasedListing p, int priceCents, CancellationToken ct)
        {
            var inventory = await csFloatWrapper.GetInventoryAsync(ct);
            var candidates = inventory.Where(i => i.MarketHashName == p.ItemMarketHashName && i.Tradable).ToList();
            var item = candidates.OrderBy(i => i.FloatValue is double f ? Math.Abs(f - p.ItemFloatValue) : double.MaxValue).FirstOrDefault();
            if (item == null)
            {
                await ServiceLog($"{p.ItemMarketHashName} isn't tradable in the CSFloat inventory yet; retrying the sale later.");
                return null;
            }
            string? listingId = await csFloatWrapper.CreateListingAsync(item.AssetId, priceCents, ct);
            if (string.IsNullOrEmpty(listingId)) throw new InvalidOperationException($"CSFloat created a listing for {p.ItemMarketHashName} without returning its id.");
            return listingId;
        }

        /// <summary>
        /// Lists the unit on the Steam Market. Discord hears about it only when it is listed (the confirmation prompt,
        /// then "Listed ✓"): a failed attempt is recorded on the position and retried, and only a position that keeps
        /// failing is reported, once (see the sale scheduler). It used to post "Listing on Steam" and "Failed to List"
        /// on every attempt.
        /// </summary>
        private async Task<SteamSellResult> SellSkinOnSteam(PurchasedListing data, int salePriceInPence)
        {
            try
            {
                var result = await steamAPIWrapper.profileWrapper.SellItem(data, salePriceInPence);
                if (result.Listed)
                {
                    data.ActualSalePriceOnSteam = salePriceInPence / 100f;
                    data.CurrentStrategicStage = StrategicStages.WaitingForMarketSaleOnSteam;
                    data.ActualTimeResoldOnSteam = DateTime.Now;
                    await scanalytics.UpdatePurchasedListing(data);
                    await SendEmbedAsync("CS2 Arbitrage — Listed on Steam ✓",
                        $"Item: {data.ItemMarketHashName}\nListed for: **£{data.ActualSalePriceOnSteam:F2}** (highest buy order; you receive £{ArbitrageMath.SteamSellerReceives(salePriceInPence) / 100.0:F2})\n" +
                        $"Bought on CSFloat for: {data.comparison?.CSFloatListing.PriceText}\nConvert the Steam wallet back with the liquidity plan (KM → CS2 Arbitrage).", DiscordColor.Green, null);
                    return result;
                }
                await ServiceLogError($"Couldn't list {data.ItemMarketHashName} on the Steam Market: {result.Outcome} — {result.Detail}.", false);
                return result;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"Error in SellSkinOnSteam for {data.ItemMarketHashName}");
                return new SteamSellResult(SteamSellOutcome.Refused, "listing threw: " + ex.Message);
            }
        }

        // ───────────────────────────── Manual purchase ─────────────────────────────

        /// <summary>Buys a specific CSFloat listing on request (not a snipe), valuing it the same way.</summary>
        public async Task<PurchasedListing?> FindAndPurchaseParticularListing(string CSFloatListingID)
        {
            await initialised.Task;
            var listing = await csFloatWrapper.GetListingAsync(CSFloatListingID, cancellationToken.Token)
                          ?? throw new Exception($"CSFloat listing {CSFloatListingID} was not found.");
            var book = await steamAPIWrapper.Market.GetOrderBookAsync(listing.MarketHashName, TimeSpan.FromMinutes(2), RequestPriority.Critical, cancellationToken.Token);
            var evaluation = OpportunityEvaluator.Evaluate(listing, book, CurrentConversionCoefficient, ExchangeRate ?? 0, engineSettings.Evaluation, DateTime.UtcNow, "manual");
            // A manual buy goes ahead regardless, but its plan (exit, trade lock, value) is still made and recorded
            // so the sale and the track record treat it like any other position.
            if (Engine != null)
            {
                try { evaluation = await Engine.ConfirmWithExitModelAsync(listing, evaluation, book, cancellationToken.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { await ServiceLogError(ex, "Couldn't plan the exit of a manual purchase; buying anyway.", false); }
            }
            await RefreshBalanceAsync(true, cancellationToken.Token);
            if ((csfloatAccountInformation?.BalanceInCents ?? 0) < listing.PriceCents) return null;
            var result = await csFloatWrapper.BuyListingAsync(listing.Id, listing.PriceCents, cancellationToken.Token);
            if (!result.Success) throw new Exception($"CSFloat rejected the purchase: {result.Message}");
            evaluation.BestRoute = evaluation.BestRoute == ExitRoute.None ? ExitRoute.SteamMarket : evaluation.BestRoute;
            await ((ISnipeHost)this).OnPurchasedAsync(listing, evaluation, book);
            return scanalytics.PurchasesSnapshot().FirstOrDefault(p => p.CSFloatListingID == listing.Id);
        }

        // ───────────────────────────── Housekeeping & health ─────────────────────────────

        private async Task HousekeepingLoopAsync(CancellationToken ct)
        {
            DateTime nextSettings = DateTime.UtcNow.AddMinutes(1), nextRates = DateTime.UtcNow.AddMinutes(30), nextBookSave = DateTime.UtcNow.AddMinutes(10);
            _ = Task.Run(() => { try { Interlocked.Exchange(ref legacyFilesOnDisk, Scanalytics.CountLegacyFiles(ct)); } catch { } });
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await scanalytics.FlushAsync();
                    if (DateTime.UtcNow >= nextSettings)
                    {
                        engineSettings = await LoadEngineSettingsAsync();
                        nextSettings = DateTime.UtcNow.AddMinutes(1);
                    }
                    if (DateTime.UtcNow >= nextRates || ExchangeRate == null)
                    {
                        await RefreshExchangeRatesAsync(throwOnFailure: false);
                        nextRates = DateTime.UtcNow.AddMinutes(30);
                    }
                    if (DateTime.UtcNow >= nextBookSave)
                    {
                        var books = steamAPIWrapper.Market.CachedBooks
                            .Where(b => DateTime.UtcNow - b.FetchedAtUtc < TimeSpan.FromHours(24))
                            .OrderByDescending(b => b.FetchedAtUtc).Take(8000).ToList();
                        await GetDataHandler().WriteToFile(BookCachePath, JsonConvert.SerializeObject(books));
                        await GetDataHandler().WriteToFile(MarketModelPath, marketModel.ToJson());
                        await GetDataHandler().WriteToFile(TradeTimelinePath, JsonConvert.SerializeObject(tradeTimeline));
                        nextBookSave = DateTime.UtcNow.AddMinutes(15);
                    }
                    if (automationEnabled && DateTime.UtcNow.Minute % 10 == 0)
                    {
                        try { steamBalance = await steamAPIWrapper.profileWrapper.GetSteamBalance() ?? steamBalance; } catch { }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServiceLogError(ex, "CS2 housekeeping failed.", false);
                }
                try { await analyticsRefreshRequested.WaitAsync(TimeSpan.FromSeconds(60), ct); }
                catch (OperationCanceledException) { return; }
                analyticsSnapshot = null; // rebuilt lazily by the route
            }
        }

        private async Task HealthLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var market = steamAPIWrapper.Market;
                    if (market.ConsecutiveFailures >= 15 && (market.LastSuccessUtc == null || DateTime.UtcNow - market.LastSuccessUtc > TimeSpan.FromMinutes(30)))
                        await AlertKlivesAsync("steam-source", "CS2 Arbitrage — Steam prices unavailable",
                            $"{market.ConsecutiveFailures} consecutive Steam order-book failures. Last error: {market.LastError}\nSteam may have changed its market endpoints again.", TimeSpan.FromHours(6));
                    var status = Engine?.GetStatus();
                    if (engineSettings.ScanningEnabled && status?.LastFeedPollUtc is DateTime last && DateTime.UtcNow - last > TimeSpan.FromMinutes(20) && status.CSFloatPausedUntilUtc == null)
                        await AlertKlivesAsync("feed-stalled", "CS2 Arbitrage — listing feed stalled",
                            $"No CSFloat feed poll since {last:HH:mm} UTC. Recent errors:\n{string.Join("\n", status.RecentErrors.Take(5))}", TimeSpan.FromHours(6));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ServiceLogError(ex, "CS2 health check failed.", false);
                }
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        private bool TryTakeCooldown(string key, TimeSpan cooldown)
        {
            DateTime now = DateTime.UtcNow;
            if (alertCooldowns.TryGetValue(key, out var last) && now - last < cooldown) return false;
            alertCooldowns[key] = now;
            return true;
        }

        private async Task AlertKlivesAsync(string key, string title, string message, TimeSpan cooldown)
        {
            DateTime now = DateTime.UtcNow;
            if (!alertLedger.TryTake(key, cooldown, now, title)) return;
            try { await alertLedger.SaveIfChangedAsync((path, json) => GetDataHandler().WriteToFile(path, json), now); }
            catch (Exception ex) { await ServiceLogError(ex, "Couldn't save the CS2 alert ledger; an alert may repeat after a restart.", false); }
            await ServiceLogError($"{title}: {message}", false);
            await SendEmbedAsync(title, message, DiscordColor.Red, null);
        }

        private async Task SendEmbedAsync(string title, string description, DiscordColor color, string? imageUrl)
        {
            try
            {
                if (description.Length > 3900) description = description[..3900] + "…";
                var builder = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http")
                    ? KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed(title, description, color, uri)
                    : KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed(title, description, color);
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", builder);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Couldn't send a CS2 Discord message.", false);
            }
        }

        // ───────────────────────────── Routes ─────────────────────────────

        internal void QueueAnalyticsRefresh()
        {
            try { analyticsRefreshRequested.Release(); }
            catch (SemaphoreFullException) { }
        }

        private Omnipotent.Services.KliveAPI.KliveAPI? routeApi;

        private async Task RegisterRouteAsync(string path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, HttpMethod method, PermissionDef permission)
        {
            routeApi ??= injectedApi;
            if (routeApi == null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while ((routeApi = FindKliveApi()) == null)
                {
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("KliveAPI did not appear within 90s; CS2 routes are unavailable.");
                    await Task.Delay(100);
                }
            }
            await routeApi.CreateRoute(path, handler, method, permission);
        }

        private Omnipotent.Services.KliveAPI.KliveAPI? FindKliveApi()
        {
            try { return GetActiveServices().ToArray().OfType<Omnipotent.Services.KliveAPI.KliveAPI>().FirstOrDefault(); }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private async Task CreateRoutesAsync()
        {
            var routes = new (string Path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> Handler, HttpMethod Method, PermissionDef Permission, bool NeedsData)[]
            {
                ("/cs2arbitragebot/status", StatusRoute, HttpMethod.Get, Cs2Perms.StatusView, false),
                ("/cs2arbitragebot/getscanalytics", AnalyticsRoute, HttpMethod.Get, Cs2Perms.ScansRead, true),
                ("/cs2arbitragebot/scanresults", async request =>
                {
                    CacheDeps.MarkUncacheable("cs2 live scan cycles");
                    await request.ReturnResponse(JsonConvert.SerializeObject(scanalytics.RecentCycles(200)), code: HttpStatusCode.OK);
                }, HttpMethod.Get, Cs2Perms.ScansRead, true),
                ("/cs2arbitragebot/opportunities", async request =>
                {
                    CacheDeps.MarkUncacheable("cs2 live opportunities");
                    int max = int.TryParse(request.userParameters["limit"], out int l) ? Math.Clamp(l, 1, 300) : 100;
                    await request.ReturnResponse(JsonConvert.SerializeObject(scanalytics.RecentNotable(max)), code: HttpStatusCode.OK);
                }, HttpMethod.Get, Cs2Perms.ScansRead, true),
                ("/cs2arbitragebot/latestliquidityplan", async request =>
                {
                    CacheDeps.MarkUncacheable("cs2 liquidity plan");
                    string? json = Volatile.Read(ref liquidityPlanSnapshot)?.Json;
                    await request.ReturnResponse(json ?? "{\"error\":\"The conversion plan has not been computed yet.\"}",
                        code: json == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                }, HttpMethod.Get, Cs2Perms.ScansRead, true),
                ("/cs2arbitragebot/balanceHistory", BalanceHistoryRoute, HttpMethod.Get, Cs2Perms.ScansRead, true),
                ("/cs2arbitragebot/scanNow", async request =>
                {
                    CacheDeps.MarkUncacheable("cs2 action");
                    if (Engine == null || !Engine.Running)
                    {
                        await request.ReturnResponse(JsonConvert.SerializeObject(new { error = "The engine is not running on this machine." }), code: HttpStatusCode.Conflict);
                        return;
                    }
                    Engine.RequestScanNow();
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { queued = true }), code: HttpStatusCode.Accepted);
                }, HttpMethod.Post, Cs2Perms.ScansRun, true),
                ("/cs2arbitragebot/steam/login", SteamLoginRoute, HttpMethod.Post, Cs2Perms.Manage, true),
                ("/cs2arbitragebot/positions/recheck", RecheckPositionRoute, HttpMethod.Post, Cs2Perms.Manage, true),
                ("/cs2arbitragebot/positions/close", ClosePositionRoute, HttpMethod.Post, Cs2Perms.Manage, true),
            };

            int registered = 0;
            foreach (var (path, handler, method, permission, needsData) in routes)
            {
                try
                {
                    await RegisterRouteAsync(path, Guard(path, handler, needsData), method, permission);
                    registered++;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"CS2ArbitrageBot: failed to register route {path}");
                }
            }
            await ServiceLog($"CS2ArbitrageBot: {registered}/{routes.Length} routes registered {startupClock.ElapsedMilliseconds} ms after start.", false);
        }

        private Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> Guard(string path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, bool needsData) => async request =>
        {
            try
            {
                // A short wait: start-up takes minutes (237 s live), and every gated request used to hang 20 s before its
                // 503. The page reads /status (not gated) for the start-up state.
                if (needsData && !initialised.Task.IsCompleted) await Task.WhenAny(initialised.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                if (needsData && !initialised.Task.IsCompletedSuccessfully)
                {
                    string why = initialised.Task.IsFaulted ? "CS2ArbitrageBot failed to start: " + initialised.Task.Exception?.GetBaseException().Message : "CS2ArbitrageBot is still starting (" + startupState + ").";
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = why, pending = true }), code: HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await handler(request);
            }
            catch (Exception e)
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                await ServiceLogError(e, $"Error in {path} route.");
            }
        };

        private async Task AnalyticsRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.NoteTimeBucket(TimeSpan.FromSeconds(30));
            bool compact = request.userParameters["compact"] is "1" or "true";
            var snapshot = Volatile.Read(ref analyticsSnapshot);
            if (snapshot == null || DateTime.UtcNow - snapshot.GeneratedAtUtc > TimeSpan.FromSeconds(30))
            {
                string json = JsonConvert.SerializeObject(scanalytics.BuildAnalytics(CurrentConversionCoefficient));
                snapshot = new JsonSnapshot { GeneratedAtUtc = DateTime.UtcNow, Json = json, CompactJson = CompactAnalyticsJson(json) };
                Volatile.Write(ref analyticsSnapshot, snapshot);
            }
            await request.ReturnResponse((compact ? snapshot.CompactJson : null) ?? snapshot.Json!, code: HttpStatusCode.OK);
        }

        /// <summary>
        /// The analytics without what the page never draws: the Steam order books and listing details copied into every
        /// purchase, all but the last three exit decisions of each position (the count is kept), unset fields, and the
        /// purchase-latency list. The page fetched the full 526 KB every minute (563 times in 30 days); on that captured
        /// payload this is 42% of the size (30 KB against 87 KB gzipped).
        /// </summary>
        internal static string CompactAnalyticsJson(string fullJson)
        {
            var root = JObject.Parse(fullJson);
            root.Remove("TimeTakenToPurchaseAllPurchasedItems");
            if (root["AllPurchasedItems"] is JArray items)
            {
                foreach (var item in items.OfType<JObject>())
                {
                    if (item["comparison"] is JObject c)
                    {
                        var listing = c["CSFloatListing"] as JObject;
                        var steam = c["SteamListing"] as JObject;
                        item["comparison"] = new JObject
                        {
                            ["CSFloatURL"] = c["CSFloatURL"],
                            ["CSFloatListing"] = listing == null ? null : new JObject
                            {
                                ["PriceText"] = listing["PriceText"],
                                ["PriceInPounds"] = listing["PriceInPounds"],
                                ["ImageURL"] = listing["ImageURL"],
                            },
                            ["SteamListing"] = steam == null ? null : new JObject
                            {
                                ["PriceText"] = steam["PriceText"],
                                ["HighestBuyOrderPriceInPounds"] = steam["HighestBuyOrderPriceInPounds"],
                            },
                        };
                    }
                    if (item["ExitHistory"] is JArray history)
                    {
                        item["ExitHistoryCount"] = history.Count;
                        if (history.Count > 3) item["ExitHistory"] = new JArray(history.Skip(history.Count - 3));
                    }
                    // Unset fields (null, "", never-happened dates, empty lists) repeated across every position were most of
                    // what was left; numbers and flags are kept, so stage 0 and a zero result still read as such.
                    foreach (var property in item.Properties().ToList())
                        if (IsEmptyValue(property.Value)) property.Remove();
                }
            }
            return root.ToString(Formatting.None);
        }

        private static bool IsEmptyValue(JToken value) => value.Type switch
        {
            JTokenType.Null or JTokenType.Undefined => true,
            JTokenType.String => value.Value<string>() is not { Length: > 0 } s || s.StartsWith("0001-01-01", StringComparison.Ordinal),
            JTokenType.Date => value.Value<DateTime>() == DateTime.MinValue,
            JTokenType.Array => !value.HasValues,
            _ => false,
        };

        private static JObject ReadBody(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try { return string.IsNullOrWhiteSpace(request.userMessageContent) ? new JObject() : JObject.Parse(request.userMessageContent); }
            catch (JsonException) { return new JObject(); }
        }

        private Task Reply(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request, object body, HttpStatusCode code) =>
            request.ReturnResponse(JsonConvert.SerializeObject(body), code: code);

        /// <summary>
        /// Klives asks for a Steam login from the page: one Discord prompt and a sign-in request in his Steam app. The
        /// bot otherwise asks only when a sale is waiting for Steam, so a dead login with nothing to sell (live: since
        /// 28 Oct) was never put to him at all.
        /// </summary>
        private async Task SteamLoginRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.MarkUncacheable("cs2 action");
            var profile = automationEnabled ? steamAPIWrapper?.profileWrapper : null;
            if (profile == null)
            {
                await Reply(request, new { error = "Steam logins are only handled where the bot's automation runs (the server)." }, HttpStatusCode.Conflict);
                return;
            }
            if (profile.LoginPromptOpen || profile.InteractiveLoginRunning)
            {
                await Reply(request, new { requested = false, open = true, message = "A login request is already waiting for approval in the Steam app." }, HttpStatusCode.OK);
                return;
            }
            string who = request.user?.Name ?? "Klives";
            await ServiceLog($"{who} asked for a Steam login from the KM page.");
            _ = Task.Run(async () =>
            {
                try
                {
                    bool ok = await profile.RequestInteractiveLoginAsync($"{who} asked from the KM CS2 Arbitrage page.", askedByKlives: true);
                    await ServiceLog(ok ? "Steam login approved; selling on Steam works again." : $"The Steam login asked for from the page did not complete ({profile.SessionProblem ?? profile.LastLoginPromptOutcome ?? "no answer"}).");
                }
                catch (Exception ex) { await ServiceLogError(ex, "Asking for a Steam login from the page failed.", false); }
            }, CancellationToken.None);
            await Reply(request, new { requested = true, message = "Approve the sign-in request in the Steam app (a Discord message explains it)." }, HttpStatusCode.Accepted);
        }

        private PurchasedListing? FindPosition(JObject body)
        {
            string id = body.Value<string>("id") ?? "";
            return string.IsNullOrEmpty(id) ? null : scanalytics.PurchasesSnapshot().FirstOrDefault(p => p.CSFloatListingID == id);
        }

        /// <summary>Asks CSFloat now what became of a relist and settles the position if it is no longer live.</summary>
        private async Task RecheckPositionRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.MarkUncacheable("cs2 action");
            var p = FindPosition(ReadBody(request));
            if (p == null) { await Reply(request, new { error = "No position with that id." }, HttpStatusCode.NotFound); return; }
            if (p.CurrentStrategicStage != StrategicStages.WaitingForCSFloatResale || string.IsNullOrEmpty(p.CSFloatResaleListingID))
            {
                await Reply(request, new { error = "Only a position relisted on CSFloat can be checked there." }, HttpStatusCode.Conflict);
                return;
            }
            if (!reviewsInFlight.TryAdd(p.CSFloatListingID, 0))
            {
                await Reply(request, new { error = "A review of this relist is running right now; try again in a moment." }, HttpStatusCode.Conflict);
                return;
            }
            try
            {
                string? outcome = await Exits!.SettleRelistAsync(p, DateTime.UtcNow, cancellationToken.Token);
                string message = outcome switch
                {
                    null => p.LastTradeState == "resale:listed" || p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale
                        ? "CSFloat still shows the listing as live (or couldn't be read); nothing changed."
                        : "Nothing changed.",
                    "sold" => "CSFloat shows it sold; its trade isn't among the recent trades. Send it from CSFloat's Trades page if it's waiting.",
                    "completed" => "Sold and delivered: the position is closed with the profit worked out from the listing price.",
                    "relist-gone" => "The listing is no longer up and the unit is still in the inventory: the bot will decide its exit again.",
                    _ => "The listing is gone and so is the unit: the position is closed.",
                };
                await Reply(request, new { outcome = outcome ?? "unchanged", message, stage = p.CurrentStrategicStage.ToString(), lastTradeState = p.LastTradeState }, HttpStatusCode.OK);
            }
            finally { reviewsInFlight.TryRemove(p.CSFloatListingID, out _); }
        }

        /// <summary>
        /// Closes a position Klives settled himself. With a sale price (USD cents on CSFloat) it is recorded as completed
        /// with that result; without one it is closed as having left the bot (no result). The note says who closed it.
        /// </summary>
        private async Task ClosePositionRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.MarkUncacheable("cs2 action");
            var body = ReadBody(request);
            var p = FindPosition(body);
            if (p == null) { await Reply(request, new { error = "No position with that id." }, HttpStatusCode.NotFound); return; }
            if (!IsOpen(p)) { await Reply(request, new { error = "That position is already closed." }, HttpStatusCode.Conflict); return; }
            if (salesInFlight.ContainsKey(p.CSFloatListingID) || reviewsInFlight.ContainsKey(p.CSFloatListingID))
            {
                await Reply(request, new { error = "The bot is selling or reviewing this position right now; try again in a moment." }, HttpStatusCode.Conflict);
                return;
            }
            int? soldCents = body["soldForCents"]?.Type is JTokenType.Integer or JTokenType.Float ? (int?)Math.Round(body.Value<double>("soldForCents")) : null;
            if (soldCents is <= 0 or > 10_000_000) { await Reply(request, new { error = "soldForCents must be a positive price in US cents." }, HttpStatusCode.BadRequest); return; }
            string note = (body.Value<string>("note") ?? "").Trim();
            if (note.Length > 300) note = note[..300];
            string who = request.user?.Name ?? "Klives";
            DateTime now = DateTime.UtcNow;
            string summary = ClosePositionByHand(p, soldCents, note, who, now, ExchangeRate ?? 0, csfloatAccountInformation?.Fee is double fee and > 0 and < 0.5 ? fee : engineSettings.Exit.SellerFee);
            await scanalytics.UpdatePurchasedListing(p);
            await ServiceLog($"{who} closed the position {p.ItemMarketHashName} ({p.CSFloatListingID}) by hand: {summary}");
            await Reply(request, new { closed = true, stage = p.CurrentStrategicStage.ToString(), message = summary }, HttpStatusCode.OK);
        }

        /// <summary>Applies a by-hand close (pure apart from the position; unit-tested).</summary>
        internal static string ClosePositionByHand(PurchasedListing p, int? soldForCents, string note, string who, DateTime nowUtc, double gbpPerUsd, double sellerFee)
        {
            string summary;
            if (soldForCents is int cents)
            {
                double netPounds = ArbitrageMath.CSFloatNetProceedsCents(cents, sellerFee) * gbpPerUsd / 100.0;
                double costPounds = p.PurchaseCostPence > 0 ? p.PurchaseCostPence / 100.0 : p.comparison?.CSFloatListing.PriceInPounds ?? 0;
                if (gbpPerUsd > 0)
                {
                    p.ActualAbsoluteProfitInPounds = (float)(netPounds - costPounds);
                    p.ActualAbsoluteProfitInPence = (float)Math.Round((netPounds - costPounds) * 100);
                    p.ActualProfitPercentage = costPounds > 0 ? (float)((netPounds / costPounds - 1) * 100) : 0;
                }
                p.CSFloatResalePriceCents = cents;
                p.TimeOfCollectedRevenue = nowUtc;
                if (p.ResaleSoldAtUtc == default) p.ResaleSoldAtUtc = nowUtc;
                p.LastTradeState = "closed:sold";
                p.CurrentStrategicStage = StrategicStages.StrategyCompleted;
                summary = $"sold for ${cents / 100.0:F2}" + (gbpPerUsd > 0 ? $" (profit £{p.ActualAbsoluteProfitInPounds:F2})" : "");
            }
            else
            {
                p.LastTradeState = "closed:gone";
                p.CurrentStrategicStage = StrategicStages.LeftInventory;
                summary = "closed with no recorded result";
            }
            p.WaitingForSteamLoginSinceUtc = default;
            string line = $"Closed by hand by {who} {nowUtc:u}: {summary}" + (string.IsNullOrEmpty(note) ? "." : $" — {note}");
            p.Notes = string.IsNullOrEmpty(p.Notes) ? line : p.Notes + Environment.NewLine + line;
            return summary;
        }

        private async Task BalanceHistoryRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.NoteTimeBucket(TimeSpan.FromMinutes(5));
            var snapshot = Volatile.Read(ref balanceHistorySnapshot);
            if (snapshot == null || DateTime.UtcNow - snapshot.GeneratedAtUtc > TimeSpan.FromMinutes(5))
            {
                snapshot = new JsonSnapshot { GeneratedAtUtc = DateTime.UtcNow, Json = JsonConvert.SerializeObject(await scanalytics.GetAllLogsOfCSFloatAndSteamBalance()) };
                Volatile.Write(ref balanceHistorySnapshot, snapshot);
            }
            await request.ReturnResponse(snapshot.Json!, code: HttpStatusCode.OK);
        }

        private async Task StatusRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            CacheDeps.MarkUncacheable("cs2 live status");
            bool ready = initialised.Task.IsCompletedSuccessfully;
            var market = ready ? steamAPIWrapper.Market : null;
            var aggregate = ready ? scanalytics.SnapshotAggregate() : null;
            var purchases = ready ? scanalytics.PurchasesSnapshot() : new List<PurchasedListing>();
            var payload = new
            {
                startupState,
                automationEnabled,
                startupMs = startupClock.ElapsedMilliseconds,
                settings = ready ? engineSettings : null,
                exchangeRateGbpPerUsd = ExchangeRate,
                conversion = new
                {
                    coefficient = CurrentConversionCoefficient,
                    computedAtUtc = CurrentConversionModel?.ComputedAtUtc,
                    basis = CurrentConversionModel?.Basis ?? "default (no model computed yet)",
                    topConverters = CurrentConversionModel?.Converters.Take(8).Select(c => new { c.MarketHashName, c.Coefficient, c.CSFloatSales7d, c.WeeklyCapacityPence, c.VerifiedBySales }),
                },
                balances = new
                {
                    csfloatUsd = csfloatAccountInformation?.BalanceInCents / 100.0,
                    csfloatGbp = csfloatAccountInformation?.BalanceInPounds,
                    csfloatFetchedUtc = balanceFetchedUtc == DateTime.MinValue ? (DateTime?)null : balanceFetchedUtc,
                    steamGbp = steamBalance?.TotalBalanceInPounds,
                },
                engine = Engine?.GetStatus(),
                csfloatRateLimits = ready ? csFloatWrapper.RateLimits.Snapshot() : null,
                steam = market == null ? null : new
                {
                    market.Requests, market.Successes, market.Failures, market.FallbackUses, market.CacheHits,
                    market.ConsecutiveFailures, market.LastSuccessUtc, market.LastFailureUtc, market.LastError,
                    cachedBooks = market.CachedCount,
                    misses = market.Misses,
                    missSkips = market.MissSkips,
                    knownMisses = market.KnownMisses,
                    backedOff = market.BackedOffItems(5).Select(m => new { name = m.Name, untilUtc = m.UntilUtc, misses = m.Count }),
                    pacerIntervalMs = market.Pacer.CurrentInterval.TotalMilliseconds,
                    pacerQueue = market.Pacer.QueueLength,
                    pacerPausedUntilUtc = market.Pacer.PausedUntilUtc > DateTime.UtcNow ? market.Pacer.PausedUntilUtc : (DateTime?)null,
                },
                bulkSteamPrices = new { referencePrices.Count, referencePrices.LoadedAtUtc, referencePrices.LastError },
                trades = new
                {
                    lastPollUtc = lastTradePollUtc,
                    lastError = lastTradePollError,
                    pollRunningSinceUtc = tradePollInFlight is { IsCompleted: false } ? tradePollStartedUtc : (DateTime?)null,
                    sellerSideUtc = lastSellerTradesUtc == default ? (DateTime?)null : lastSellerTradesUtc,
                    sellerSideError = lastSellerTradesError,
                },
                startupSteps = StartupStepsSnapshot().Select(s => new { step = s.Step, ms = s.Ms }),
                // The Discord alerts the bot sent (last 31 days, newest first): each standing one is sent once per cooldown.
                alerts = alertLedger.Recent(10).Select(a => new { key = a.Key, atUtc = a.AtUtc, title = a.Title }),
                steamSession = !ready || steamAPIWrapper?.profileWrapper is not { } profile ? null : new
                {
                    problem = profile.SessionProblem,
                    problemSinceUtc = profile.SessionProblemSinceUtc,
                    lastWorkedUtc = profile.SessionOkUtc,
                    checkedUtc = profile.SessionCheckedUtc,
                    loginPromptOpen = profile.LoginPromptOpen || profile.InteractiveLoginRunning,
                    lastLoginPromptUtc = profile.LastLoginPromptUtc,
                    lastLoginPromptOutcome = profile.LastLoginPromptOutcome,
                    refreshTokenRejectedUtc = profile.RefreshTokenRejectedUtc,
                },
                inventoryCheck = new
                {
                    lastCheckUtc = lastInventoryCheckUtc == default ? (DateTime?)null : lastInventoryCheckUtc,
                    lastOkUtc = lastInventoryCheckOkUtc == default ? (DateTime?)null : lastInventoryCheckOkUtc,
                },
                exitModel = !ready ? null : new
                {
                    settings = engineSettings.Exit,
                    autoManageRelists = engineSettings.AutoManageRelists,
                    locks = CurrentExitEnvironmentSummary(),
                    market = marketModel.Describe(),
                    calibration = CurrentExitCalibration,
                    signals = new { exitSignals.Fetches, exitSignals.CacheHits, exitSignals.SkippedForBudget },
                },
                positions = purchases.Where(IsOpen).Select(p => new
                {
                    p.ItemMarketHashName, p.CSFloatListingID, stage = p.CurrentStrategicStage.ToString(), p.PlannedExit, p.PredictedTimeToBeResoldOnSteam, p.LastTradeState,
                    relist = p.CurrentStrategicStage == StrategicStages.WaitingForCSFloatResale
                        ? new
                        {
                            priceCents = p.CSFloatResalePriceCents, listedUtc = p.ListedOnCSFloatAtUtc, p.RelistRepriceCount, expectedDaysToSell = p.RelistBaseDaysToSell,
                            p.RelistModelExposure, totalExposure = p.RelistTotalExposure, nextReviewUtc = p.NextExitReviewUtc, listingId = p.CSFloatResaleListingID,
                            soldUtc = p.ResaleSoldAtUtc == default ? (DateTime?)null : p.ResaleSoldAtUtc,
                            overdue = p.ResaleSoldAtUtc == default && p.RelistTotalExposure >= ExitManager.OverdueExpectedSales,
                        }
                        : null,
                    saleDeferrals = p.SaleDeferrals,
                    saleAttempts = p.SaleAttempts,
                    lastSaleFailure = string.IsNullOrEmpty(p.LastSaleFailure) ? null : p.LastSaleFailure,
                    autoSelling = p.CurrentStrategicStage != StrategicStages.JustRetrieved || IsAutoSellable(p, DateTime.UtcNow),
                    waitingForSteamLoginSinceUtc = p.WaitingForSteamLoginSinceUtc == default ? (DateTime?)null : p.WaitingForSteamLoginSinceUtc,
                    missingFromInventorySinceUtc = p.MissingFromInventorySinceUtc == default ? (DateTime?)null : p.MissingFromInventorySinceUtc,
                    lastDecision = p.ExitHistory?.LastOrDefault() ?? p.PurchasePlan,
                }),
                totals = aggregate == null ? null : new { aggregate.TotalEvaluated, aggregate.QualifiedOpportunities, aggregate.PurchaseAttempts, aggregate.Purchases, aggregate.HighestRoi, aggregate.HighestRoiItem },
                legacyFilesOnDisk = Interlocked.Read(ref legacyFilesOnDisk),
            };
            await request.ReturnResponse(JsonConvert.SerializeObject(payload), code: HttpStatusCode.OK);
        }
    }
}
