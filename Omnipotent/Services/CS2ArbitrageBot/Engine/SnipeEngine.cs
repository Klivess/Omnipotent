using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Diagnostics;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>Runtime knobs (refreshed from OmniSettings by the service).</summary>
    public sealed class EngineSettings
    {
        public bool ScanningEnabled { get; set; } = true;
        public bool PurchasingEnabled { get; set; } = true;
        public EvaluationSettings Evaluation { get; set; } = new();
        /// <summary>Conversion coefficient used until the model has verified converters (cases convert at ~0.68).</summary>
        public double DefaultConversionCoefficient { get; set; } = 0.68;
        /// <summary>Largest share of the CSFloat balance one item may cost.</summary>
        public double MaxSpendPerItemFraction { get; set; } = 0.4;
        public int MaxUnitsPerItem { get; set; } = 3;
        /// <summary>0 = unlimited.</summary>
        public int DailySpendLimitPence { get; set; }
        public TimeSpan FeedMinInterval { get; set; } = TimeSpan.FromSeconds(15);
        /// <summary>Listing-search requests per window kept back from the feed for sweeps and targeted lookups.</summary>
        public int FeedReserve { get; set; } = 25;
        /// <summary>
        /// Sweeps catch what the creation-ordered feed cannot: price cuts on existing listings (a common way to
        /// dump) and anything listed while the bot was down.
        /// </summary>
        public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan StructuralInterval { get; set; } = TimeSpan.FromMinutes(60);
        public int StructuralBookChecks { get; set; } = 120;
        public int StructuralTargetedLookups { get; set; } = 12;
        public TimeSpan BookFreshness { get; set; } = TimeSpan.FromMinutes(15);
        public TimeSpan PurchaseRecheckAge { get; set; } = TimeSpan.FromSeconds(90);
        /// <summary>CSFloat category filter ("1" normal, "2" StatTrak, "3" souvenir, comma-separated); null = all.</summary>
        public string? Categories { get; set; }
        public int SteamRequestSpacingMs { get; set; } = 1000;
        public bool AlertOnUnboughtOpportunities { get; set; } = true;
        /// <summary>Economics of the exit model (capital cost, risk aversion, settlement times).</summary>
        public ExitModelSettings Exit { get; set; } = new();
        /// <summary>Let listing reviews re-price or withdraw CSFloat relists on their own (otherwise they only advise).</summary>
        public bool AutoManageRelists { get; set; } = true;
        /// <summary>How long confirming a Steam Market listing in the mobile app typically takes (it is a person).</summary>
        public double SteamConfirmHours { get; set; } = 12;
        /// <summary>Steam's wallet cap (≈ $2,000-equivalent): a Market listing that would exceed it is refused.</summary>
        public double SteamWalletCapUsd { get; set; } = 2000;
        /// <summary>Steam's maximum Market listing price (≈ $1,800-equivalent).</summary>
        public double SteamMaxListingUsd { get; set; } = 1800;
        /// <summary>How often a few random items are sampled to keep the market drift and forecast calibration current.</summary>
        public TimeSpan MarketPanelInterval { get; set; } = TimeSpan.FromMinutes(90);
        public int MarketPanelItems { get; set; } = 4;
    }

    /// <summary>What the engine needs from its owning service. Implemented by CS2ArbitrageBot (and fakes in tests).</summary>
    public interface ISnipeHost
    {
        CSFloatWrapper CSFloat { get; }
        SteamMarketClient SteamMarket { get; }
        SteamReferencePrices ReferencePrices { get; }
        Scanalytics Analytics { get; }
        EngineSettings Settings { get; }
        double GbpPerUsd { get; }
        double ConversionCoefficient { get; }
        /// <summary>Spendable CSFloat balance, USD cents; null when unknown.</summary>
        int? BalanceCents { get; }
        Task RefreshBalanceAsync(bool force, CancellationToken ct);
        void DebitBalance(int cents);
        int OpenPositionsFor(string marketHashName);
        int SpentTodayPence { get; }
        double TargetConversionVolumePence { get; }
        ExitSignalsProvider ExitSignals { get; }
        MarketDriftModel MarketModel { get; }
        ExitCalibrationSnapshot ExitCalibration { get; }
        /// <summary>Trade locks and holds (measured), the conversion route's hold risk, and account limits.</summary>
        ExitEnvironment ExitEnvironment { get; }
        /// <summary>The account's Steam id (its own listings are not competition); null when unknown.</summary>
        string? OwnSteamId { get; }
        Task OnPurchasedAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book);
        Task OnOpportunityNotBoughtAsync(CSFloatListing listing, OpportunityEvaluation evaluation, string why);
        Task OnConversionModelComputedAsync(ConversionModelSnapshot snapshot, LiquidityPlan plan);
        void Log(string message);
        void LogError(Exception? ex, string message);
    }

    public sealed class EngineStatus
    {
        public string State { get; set; } = "";
        public DateTime? StartedUtc { get; set; }
        public DateTime? LastFeedPollUtc { get; set; }
        public double CurrentFeedIntervalSeconds { get; set; }
        public long FeedPolls { get; set; }
        public long FeedCoverageGaps { get; set; }
        public long FeedGapFillPages { get; set; }
        /// <summary>New buy-now listings per minute observed in the feed's price window.</summary>
        public double ObservedListingsPerMinute { get; set; }
        public DateTime? LastSweepUtc { get; set; }
        public DateTime? LastStructuralScanUtc { get; set; }
        public DateTime? LastConversionModelUtc { get; set; }
        public DateTime? PriceListLoadedUtc { get; set; }
        public int PriceListItems { get; set; }
        public DateTime? ReferencePricesLoadedUtc { get; set; }
        public int ReferencePriceItems { get; set; }
        public long ListingsSeen { get; set; }
        public long Evaluated { get; set; }
        public long Prefiltered { get; set; }
        public long SteamLookups { get; set; }
        public long Opportunities { get; set; }
        public long PurchaseAttempts { get; set; }
        public long Purchases { get; set; }
        public DateTime? CSFloatPausedUntilUtc { get; set; }
        public List<string> RecentErrors { get; set; } = new();
        public string? LastStructuralSummary { get; set; }
    }

    /// <summary>
    /// The scanning and buying engine. Replaces the old 30-minute TimeManager chain, which scanned ~450
    /// listings per half hour off random pages (sort_by was never sent), did two blocking Steam requests per
    /// listing, and could only find an opportunity long after other snipers had bought it.
    /// </summary>
    public sealed class SnipeEngine
    {
        public const double PrefilterMarginPercent = 3;
        private const int FeedPageSize = 50;

        private readonly ISnipeHost host;
        private readonly Func<DateTime> utcNow;
        private readonly RecentIdSet seen = new(60_000);
        private readonly SemaphoreSlim buyLock = new(1, 1);
        private readonly SemaphoreSlim processingSlots = new(2, 2);
        private readonly SemaphoreSlim scanNowSignal = new(0, 1);
        private readonly HttpClient referenceHttp = SteamReferencePrices.CreateHttpClient();
        private readonly object statusGate = new();
        private readonly LinkedList<string> recentErrors = new();
        private readonly Dictionary<string, (DateTime FetchedUtc, List<CSFloatWrapper.SalesGraphPoint> Graph)> salesGraphCache = new(StringComparer.Ordinal);

        private DateTime? previousNewestCreatedUtc;
        private DateTime csfloatPausedUntilUtc = DateTime.MinValue;
        private List<CSFloatWrapper.PriceListEntry>? priceList;
        private DateTime priceListLoadedUtc;
        private DateTime nextReferenceRefreshUtc = DateTime.MinValue;
        private DateTime nextConversionRunUtc = DateTime.MinValue;
        private DateTime nextStructuralRunUtc = DateTime.MinValue;
        private DateTime nextSweepUtc = DateTime.MinValue;
        private DateTime nextPanelUtc = DateTime.MinValue;
        private int sweepRotation;
        private int panelRotation;
        private double observedListingsPerMinute;

        private readonly EngineStatus status = new();
        private long listingsSeen, evaluated, prefiltered, steamLookups, opportunities, purchaseAttempts, purchases;

        public SnipeEngine(ISnipeHost host, Func<DateTime>? utcNow = null)
        {
            this.host = host;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool Running { get; private set; }

        public void Start(CancellationToken ct)
        {
            if (Running) return;
            Running = true;
            status.StartedUtc = utcNow();
            status.State = "running";
            _ = Task.Run(() => SupervisedLoopAsync("feed", FeedLoopAsync, ct));
            _ = Task.Run(() => SupervisedLoopAsync("sweep+structural", SweepAndStructuralLoopAsync, ct));
            _ = Task.Run(() => SupervisedLoopAsync("maintenance", MaintenanceLoopAsync, ct));
        }

        /// <summary>Wakes the sweep/structural loop now (the scanNow route).</summary>
        public void RequestScanNow()
        {
            nextSweepUtc = DateTime.MinValue;
            nextStructuralRunUtc = DateTime.MinValue;
            try { scanNowSignal.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>Restarts a crashed loop after a pause instead of letting the engine die silently.</summary>
        private async Task SupervisedLoopAsync(string name, Func<CancellationToken, Task> loop, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await loop(ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    NoteError($"{name} loop crashed: {ex.Message}");
                    host.LogError(ex, $"CS2 engine {name} loop crashed; restarting in 30s.");
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        // ───────────────────────────── Feed ─────────────────────────────

        private async Task FeedLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            while (!ct.IsCancellationRequested)
            {
                var settings = host.Settings;
                if (!settings.ScanningEnabled || host.GbpPerUsd <= 0)
                {
                    SetState(!settings.ScanningEnabled ? "paused (PerformCS2Scans is off)" : "waiting for exchange rates");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                SetState("running");
                await WaitForCSFloatAsync(ct);

                var poll = Stopwatch.StartNew();
                try
                {
                    await RunFeedPollAsync(ct);
                }
                catch (CSFloatRateLimitedException ex)
                {
                    PauseCSFloat(ex.RetryAtUtc, $"listing search rate-limited until {ex.RetryAtUtc:HH:mm:ss} UTC");
                }
                catch (CSFloatAuthException ex)
                {
                    PauseCSFloat(utcNow().AddMinutes(30), ex.Message);
                    host.LogError(ex, "CSFloat rejected the API key; the feed pauses 30 minutes.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NoteError("feed poll failed: " + ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                }

                TimeSpan interval = host.CSFloat.RateLimits.SuggestedInterval(CSFloatWrapper.ListingsBucket, settings.FeedReserve, settings.FeedMinInterval, TimeSpan.FromMinutes(5));
                lock (statusGate) status.CurrentFeedIntervalSeconds = interval.TotalSeconds;
                TimeSpan remaining = interval - poll.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            }
        }

        /// <summary>One poll of the newest listings (plus cursor pages when listings arrive faster than one page).</summary>
        public async Task RunFeedPollAsync(CancellationToken ct)
        {
            var settings = host.Settings;
            var cycle = new ScanCycleSummary { StartedUtc = utcNow(), Strategy = "feed" };
            var clock = Stopwatch.StartNew();
            var query = new CSFloatWrapper.ListingQuery
            {
                SortBy = "most_recent",
                Limit = FeedPageSize,
                MinPriceCents = settings.Evaluation.MinimumPriceCents,
                MaxPriceCents = FeedMaxPriceCents(host.BalanceCents, settings),
                Categories = settings.Categories,
            };
            var page = await host.CSFloat.SearchListingsAsync(query, ct);
            var listings = new List<CSFloatListing>(page.Listings);
            cycle.ListingsReturned = page.Listings.Count;

            bool gap = DetectCoverageGap(page.Listings, previousNewestCreatedUtc, FeedPageSize);
            if (gap)
            {
                cycle.CoverageGap = true;
                Increment(s => s.FeedCoverageGaps++);
                // Fill the gap from the cursor while the budget allows (keeps sweeps' reserve intact).
                string? cursor = page.Cursor;
                for (int extra = 0; extra < 2 && !string.IsNullOrEmpty(cursor); extra++)
                {
                    if (!host.CSFloat.RateLimits.CanSpend(CSFloatWrapper.ListingsBucket, settings.FeedReserve + 5)) break;
                    query.Cursor = cursor;
                    var more = await host.CSFloat.SearchListingsAsync(query, ct);
                    Increment(s => s.FeedGapFillPages++);
                    listings.AddRange(more.Listings);
                    cursor = more.Cursor;
                    if (more.Listings.Count == 0 || more.Listings.Min(l => l.CreatedAtUtc) <= previousNewestCreatedUtc) break;
                }
            }

            if (page.Listings.Count > 1)
            {
                var span = page.Listings.Max(l => l.CreatedAtUtc) - page.Listings.Min(l => l.CreatedAtUtc);
                if (span > TimeSpan.Zero)
                {
                    double rate = page.Listings.Count / span.TotalMinutes;
                    observedListingsPerMinute = observedListingsPerMinute <= 0 ? rate : 0.7 * observedListingsPerMinute + 0.3 * rate;
                }
            }
            if (listings.Count > 0)
            {
                var newest = listings.Max(l => l.CreatedAtUtc);
                if (previousNewestCreatedUtc == null || newest > previousNewestCreatedUtc) previousNewestCreatedUtc = newest;
            }

            var fresh = listings.Where(l => seen.Add(l.Id)).ToList();
            cycle.NewListings = fresh.Count;
            Interlocked.Add(ref listingsSeen, fresh.Count);
            lock (statusGate)
            {
                status.LastFeedPollUtc = utcNow();
                status.FeedPolls++;
                status.ObservedListingsPerMinute = Math.Round(observedListingsPerMinute, 1);
            }

            // Valuation can wait on Steam; run it beside the next poll rather than in front of it.
            if (fresh.Count == 0)
            {
                cycle.DurationMs = clock.Elapsed.TotalMilliseconds;
                host.Analytics.RecordCycle(cycle);
                return;
            }
            await processingSlots.WaitAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessListingsAsync(fresh, "feed", cycle, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    cycle.Errors++;
                    NoteError("feed processing failed: " + ex.Message);
                }
                finally
                {
                    processingSlots.Release();
                    cycle.DurationMs = clock.Elapsed.TotalMilliseconds;
                    host.Analytics.RecordCycle(cycle);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// True when a full page holds only listings newer than everything the previous poll saw — i.e. more
        /// listings arrived between polls than one page holds, so some were never looked at.
        /// </summary>
        public static bool DetectCoverageGap(IReadOnlyList<CSFloatListing> page, DateTime? previousNewestUtc, int pageSize)
        {
            if (previousNewestUtc == null || page.Count < pageSize) return false;
            return page.Min(l => l.CreatedAtUtc) > previousNewestUtc.Value;
        }

        /// <summary>
        /// Upper price bound for queries: the per-item share of the balance. With a tiny or unknown balance the
        /// bound is dropped so opportunities are still found (and reported) rather than filtered out unseen.
        /// </summary>
        public static int? FeedMaxPriceCents(int? balanceCents, EngineSettings settings)
        {
            int absoluteMax = settings.Evaluation.MaximumPriceCents;
            int? cap = absoluteMax is > 0 and < int.MaxValue ? absoluteMax : null;
            if (balanceCents is not int balance) return cap;
            int share = (int)Math.Floor(balance * settings.MaxSpendPerItemFraction);
            if (share < Math.Max(500, settings.Evaluation.MinimumPriceCents * 2)) return cap;
            return cap is int c ? Math.Min(c, share) : share;
        }

        // ───────────────────────────── Valuation ─────────────────────────────

        /// <summary>
        /// Values listings: no-network evaluation first (cached book, CSFloat-relist exit, bulk-price prefilter),
        /// then Steam lookups for the survivors, most promising first, buying as soon as one qualifies.
        /// </summary>
        public async Task ProcessListingsAsync(IReadOnlyList<CSFloatListing> listings, string source, ScanCycleSummary cycle, CancellationToken ct)
        {
            var settings = host.Settings;
            double fx = host.GbpPerUsd;
            double k = host.ConversionCoefficient;
            if (fx <= 0) return;
            var needBook = new List<(CSFloatListing Listing, double Upper)>();

            foreach (var listing in listings)
            {
                if (host.Analytics.HasPurchased(listing.Id)) continue;
                var cached = host.SteamMarket.TryGetCached(listing.MarketHashName);
                bool fresh = cached != null && cached.Age(utcNow()) <= settings.BookFreshness;
                if (fresh)
                {
                    await RecordAndActAsync(listing, cached, source, cycle, ct);
                    continue;
                }
                int cost = ArbitrageMath.UsdCentsToPenceCeil(listing.PriceCents, fx);
                int? optimistic = OptimisticSteamPence(listing.MarketHashName, cached, fx);
                double upper = optimistic is int o ? OpportunityEvaluator.OptimisticSteamRoi(cost, o, k) : double.PositiveInfinity;
                if (upper * 100 < settings.Evaluation.MinimumSteamRoiPercent - PrefilterMarginPercent)
                {
                    cycle.Prefiltered++;
                    Interlocked.Increment(ref prefiltered);
                    await RecordAndActAsync(listing, null, source, cycle, ct, upper);
                    continue;
                }
                needBook.Add((listing, upper));
            }

            foreach (var (listing, _) in needBook.OrderByDescending(x => x.Upper))
            {
                ct.ThrowIfCancellationRequested();
                SteamOrderBook? book = null;
                try
                {
                    book = await host.SteamMarket.GetOrderBookAsync(listing.MarketHashName, settings.BookFreshness, RequestPriority.High, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    cycle.Errors++;
                    NoteError($"Steam lookup for {listing.MarketHashName} failed: {ex.Message}");
                }
                cycle.SteamLookups++;
                Interlocked.Increment(ref steamLookups);
                await RecordAndActAsync(listing, book, source, cycle, ct);
            }
        }

        private int? OptimisticSteamPence(string marketHashName, SteamOrderBook? staleBook, double fx)
        {
            int? fromFeed = host.ReferencePrices.OptimisticPence(marketHashName, fx);
            int? fromBook = staleBook != null && (staleBook.HighestBuyOrderPence > 0 || staleBook.LowestSellOrderPence > 0)
                ? (int)Math.Ceiling(Math.Max(staleBook.HighestBuyOrderPence, staleBook.LowestSellOrderPence) * 1.10)
                : null;
            if (fromFeed is int a && fromBook is int b) return Math.Max(a, b);
            return fromFeed ?? fromBook;
        }

        private async Task RecordAndActAsync(CSFloatListing listing, SteamOrderBook? book, string source, ScanCycleSummary cycle, CancellationToken ct, double? prefilterUpper = null)
        {
            var settings = host.Settings;
            var evaluation = OpportunityEvaluator.Evaluate(listing, book, host.ConversionCoefficient, host.GbpPerUsd, settings.Evaluation, utcNow(), source);
            evaluation.SteamPrefilterUpperRoi = prefilterUpper;
            host.Analytics.RecordEvaluation(evaluation, settings.Evaluation);
            cycle.Evaluated++;
            Interlocked.Increment(ref evaluated);
            if (evaluation.BestRoi > cycle.BestRoi || cycle.BestItem == null)
            {
                cycle.BestRoi = evaluation.BestRoi;
                cycle.BestItem = listing.MarketHashName;
            }
            if (!evaluation.ShouldBuy) return;
            cycle.Opportunities++;
            Interlocked.Increment(ref opportunities);
            if (await TryBuyAsync(listing, evaluation, book, ct)) cycle.Purchased++;
        }

        // ───────────────────────────── Buying ─────────────────────────────

        /// <summary>
        /// Re-checks, then buys. Serialised so concurrent valuations can never overspend the balance. The
        /// Discord alert is sent after the purchase — the old code awaited a Discord message and an account
        /// refresh before even trying, while other snipers took the listing.
        /// </summary>
        public async Task<bool> TryBuyAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book, CancellationToken ct)
        {
            await buyLock.WaitAsync(ct);
            try
            {
                var settings = host.Settings;
                if (host.Analytics.HasPurchased(listing.Id)) return false;

                // The Steam exit was valued from a book up to BookFreshness old: re-check it now.
                bool steamMatters = evaluation.BestRoute == ExitRoute.SteamMarket || listing.PriceCents < (listing.BasePriceCents ?? 0) * 0.6;
                if (steamMatters && (book == null || book.Age(utcNow()) > settings.PurchaseRecheckAge))
                {
                    var recheck = await host.SteamMarket.GetOrderBookAsync(listing.MarketHashName, settings.PurchaseRecheckAge, RequestPriority.Critical, ct);
                    if (recheck != null && recheck.Age(utcNow()) <= settings.PurchaseRecheckAge) book = recheck;
                    evaluation = OpportunityEvaluator.Evaluate(listing, book, host.ConversionCoefficient, host.GbpPerUsd, settings.Evaluation, utcNow(), evaluation.Source);
                    if (!evaluation.ShouldBuy)
                    {
                        host.Log($"Skipped {listing.MarketHashName} ({listing.Id}) on re-check: {evaluation.Reason}");
                        return false;
                    }
                }

                // The screen valued the exits from the listing alone. Before spending, value them with live
                // evidence: does the item actually sell on CSFloat and how fast, what buyers really pay, how
                // volatile it is over the hold, the market's drift, and the bot's own track record.
                evaluation = await ConfirmWithExitModelAsync(listing, evaluation, book, ct);
                if (!evaluation.ShouldBuy)
                {
                    host.Log($"Skipped {listing.MarketHashName} ({listing.Id}) after the exit model: {evaluation.Reason}");
                    await host.OnOpportunityNotBoughtAsync(listing, evaluation, evaluation.Reason);
                    return false;
                }

                string? blocker = await PurchaseBlockerAsync(listing, evaluation, settings, ct);
                if (blocker != null)
                {
                    await host.OnOpportunityNotBoughtAsync(listing, evaluation, blocker);
                    return false;
                }

                Interlocked.Increment(ref purchaseAttempts);
                var result = await host.CSFloat.BuyListingAsync(listing.Id, listing.PriceCents, ct);
                host.Analytics.RecordPurchaseOutcome(result.Success);
                if (!result.Success)
                {
                    await host.OnOpportunityNotBoughtAsync(listing, evaluation, $"purchase rejected ({(int)result.StatusCode}): {result.Message}");
                    return false;
                }
                Interlocked.Increment(ref purchases);
                host.DebitBalance(listing.PriceCents);
                await host.OnPurchasedAsync(listing, evaluation, book);
                return true;
            }
            catch (CSFloatAuthException ex)
            {
                PauseCSFloat(utcNow().AddMinutes(30), ex.Message);
                host.LogError(ex, "CSFloat rejected the API key while buying.");
                return false;
            }
            finally
            {
                buyLock.Release();
            }
        }

        /// <summary>Re-values a listing the screen passed with the full exit model and live market evidence.</summary>
        public async Task<OpportunityEvaluation> ConfirmWithExitModelAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book, CancellationToken ct)
        {
            var settings = host.Settings;
            var signals = await host.ExitSignals.GetAsync(listing.MarketHashName, SignalUse.Purchase, ct, book);
            var plan = PlanPurchase(listing, evaluation.CostPence, signals, settings, host, utcNow());
            OpportunityEvaluator.ApplyExitPlan(evaluation, listing, plan, settings.Evaluation, signals.Missing);
            return evaluation;
        }

        /// <summary>Plans both exits for a listing as if bought now, as of the day it becomes tradable.</summary>
        public static ExitPlan PlanPurchase(CSFloatListing listing, int costPence, ExitSignals signals, EngineSettings settings, ISnipeHost host, DateTime nowUtc)
        {
            int anchor = PurchaseAnchorCents(listing);
            var demand = anchor > 0
                ? CSFloatDemand.Build(listing.MarketHashName, anchor, signals.SalesGraph, signals.RecentSales, signals.Competitors,
                    listing.ReferenceQuantity ?? signals.ReferenceQuantity, host.OwnSteamId, listing.Id, nowUtc)
                : null;
            var environment = host.ExitEnvironment;
            var context = new ExitContext
            {
                MarketHashName = listing.MarketHashName,
                CostPence = costPence,
                DaysUntilTradable = DaysUntilTradable(listing, environment.Timeline, nowUtc),
                AnchorCents = anchor,
                GbpPerUsd = host.GbpPerUsd,
                ConversionCoefficient = host.ConversionCoefficient,
                Book = signals.Book,
                Forecast = PriceForecaster.Forecast(signals.SteamHistory, listing.MarketHashName, nowUtc, host.MarketModel),
                Demand = demand,
                AllowRelist = settings.Evaluation.AllowRelistExit,
                Calibration = host.ExitCalibration,
                // The account's away mode only blocks selling now; by the time this unit unlocks it may be back.
                Environment = environment with { CSFloatSellingPaused = false },
                TradeFailureProbability = environment.Timeline.PurchaseFailureProbability(listing.SellerTotalTrades, listing.SellerFailedTrades + listing.SellerAvoidedTrades),
            };
            return ExitPlanner.Plan(context, settings.Exit, nowUtc);
        }

        /// <summary>CSFloat's value for the unit being bought, never counting a float premium (as the screen).</summary>
        public static int PurchaseAnchorCents(CSFloatListing listing)
        {
            int? baseValue = listing.BasePriceCents is > 0 ? listing.BasePriceCents : null;
            int? predicted = listing.PredictedPriceCents is > 0 ? listing.PredictedPriceCents : null;
            if (baseValue is int b && predicted is int p) return Math.Min(b, p);
            return baseValue ?? predicted ?? 0;
        }

        /// <summary>
        /// Days from buying <paramref name="listing"/> now until the unit can be sold: this seller's handover (their
        /// median trade time, capped at two days, never quicker than the account's measured handover), then Valve's
        /// 7-day trade protection, which ends on its daily boundary, then an hour's buffer.
        /// </summary>
        public static double DaysUntilTradable(CSFloatListing listing, TradeTimeline timeline, DateTime nowUtc) =>
            timeline.DaysUntilTradable(nowUtc, Math.Max(0, listing.SellerMedianTradeTimeSeconds) / 86400.0);

        /// <summary>Why an otherwise-qualifying listing must not be bought right now (null = buy).</summary>
        private async Task<string?> PurchaseBlockerAsync(CSFloatListing listing, OpportunityEvaluation evaluation, EngineSettings settings, CancellationToken ct)
        {
            if (!settings.PurchasingEnabled) return "purchasing is disabled (PurchaseCSFloatArbitrageOpportunities)";
            int held = host.OpenPositionsFor(listing.MarketHashName);
            if (held >= settings.MaxUnitsPerItem) return $"already holding {held} of this item";
            if (settings.DailySpendLimitPence > 0 && host.SpentTodayPence + evaluation.CostPence > settings.DailySpendLimitPence)
                return $"daily spend limit (£{settings.DailySpendLimitPence / 100.0:F2}) reached";
            await host.RefreshBalanceAsync(false, ct);
            if (host.BalanceCents is not int balance) return "CSFloat balance unknown";
            if (listing.PriceCents > balance) return $"insufficient CSFloat balance (${balance / 100.0:F2})";
            int perItemCap = (int)Math.Floor(balance * settings.MaxSpendPerItemFraction);
            if (listing.PriceCents > perItemCap) return $"above the per-item cap ({settings.MaxSpendPerItemFraction:P0} of balance = ${perItemCap / 100.0:F2})";
            return null;
        }

        // ───────────────────────────── Sweeps & structural scan ─────────────────────────────

        private async Task SweepAndStructuralLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            while (!ct.IsCancellationRequested)
            {
                var settings = host.Settings;
                if (settings.ScanningEnabled && host.GbpPerUsd > 0 && utcNow() >= csfloatPausedUntilUtc)
                {
                    try
                    {
                        if (utcNow() >= nextSweepUtc && host.CSFloat.RateLimits.CanSpend(CSFloatWrapper.ListingsBucket, settings.FeedReserve / 2 + 2))
                        {
                            string sort = (sweepRotation++ % 2 == 0) ? "highest_discount" : "best_deal";
                            await RunSweepAsync(sort, ct);
                            nextSweepUtc = utcNow() + settings.SweepInterval;
                        }
                        if (utcNow() >= nextStructuralRunUtc)
                        {
                            await RunStructuralScanAsync(ct);
                            nextStructuralRunUtc = utcNow() + settings.StructuralInterval;
                        }
                    }
                    catch (CSFloatRateLimitedException ex)
                    {
                        PauseCSFloat(ex.RetryAtUtc, $"sweep rate-limited until {ex.RetryAtUtc:HH:mm:ss} UTC");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        NoteError("sweep/structural failed: " + ex.Message);
                    }
                }
                try { await scanNowSignal.WaitAsync(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }

        /// <summary>One page of a non-chronological sort — catches underpriced listings the feed missed.</summary>
        public async Task RunSweepAsync(string sortBy, CancellationToken ct)
        {
            var settings = host.Settings;
            var cycle = new ScanCycleSummary { StartedUtc = utcNow(), Strategy = "sweep:" + sortBy };
            var clock = Stopwatch.StartNew();
            var page = await host.CSFloat.SearchListingsAsync(new CSFloatWrapper.ListingQuery
            {
                SortBy = sortBy,
                Limit = FeedPageSize,
                MinPriceCents = settings.Evaluation.MinimumPriceCents,
                MaxPriceCents = FeedMaxPriceCents(host.BalanceCents, settings),
                Categories = settings.Categories,
                MinReferenceQuantity = sortBy == "highest_discount" ? settings.Evaluation.MinimumRelistReferenceQuantity : null,
            }, ct);
            cycle.ListingsReturned = page.Listings.Count;
            // Sweeps revisit old listings on purpose: conditions (Steam prices, conversion) change.
            var candidates = page.Listings.ToList();
            cycle.NewListings = candidates.Count(l => seen.Add(l.Id));
            await ProcessListingsAsync(candidates, cycle.Strategy, cycle, ct);
            cycle.DurationMs = clock.Elapsed.TotalMilliseconds;
            host.Analytics.RecordCycle(cycle);
            lock (statusGate) status.LastSweepUtc = utcNow();
        }

        /// <summary>
        /// Whole-market pass: ranks every item's lowest CSFloat ask against bulk Steam prices, confirms the best
        /// with live order books, then fetches the actual cheapest listings of those that still qualify. This is
        /// what finds structurally profitable items (e.g. cases, which carry a ~1.45× Steam premium) that are
        /// never "deals" on CSFloat and so never surface in a discount sort.
        /// </summary>
        public async Task RunStructuralScanAsync(CancellationToken ct)
        {
            var settings = host.Settings;
            double fx = host.GbpPerUsd;
            double k = host.ConversionCoefficient;
            var cycle = new ScanCycleSummary { StartedUtc = utcNow(), Strategy = "structural" };
            var clock = Stopwatch.StartNew();
            var list = await GetPriceListAsync(ct);
            if (list == null || list.Count == 0 || host.ReferencePrices.Count == 0)
            {
                cycle.Note = "price list or bulk Steam prices unavailable";
                host.Analytics.RecordCycle(cycle);
                lock (statusGate) { status.LastStructuralScanUtc = utcNow(); status.LastStructuralSummary = cycle.Note; }
                return;
            }

            int? maxPrice = FeedMaxPriceCents(host.BalanceCents, settings);
            var ranked = new List<(CSFloatWrapper.PriceListEntry Entry, double Upper)>();
            foreach (var entry in list)
            {
                if (entry.MinPriceCents < settings.Evaluation.MinimumPriceCents) continue;
                if (maxPrice is int m && entry.MinPriceCents > m) continue;
                if (entry.Quantity < 2) continue;
                int? steamTypical = host.ReferencePrices.TryGet(entry.MarketHashName, out var r) && r.TypicalUsd > 0
                    ? (int)Math.Floor(r.TypicalUsd * 100 * fx) : null;
                if (steamTypical == null) continue;
                double upper = OpportunityEvaluator.OptimisticSteamRoi(ArbitrageMath.UsdCentsToPenceCeil(entry.MinPriceCents, fx), steamTypical.Value, k);
                if (upper * 100 >= settings.Evaluation.MinimumSteamRoiPercent - PrefilterMarginPercent) ranked.Add((entry, upper));
            }

            var confirmed = new List<(CSFloatWrapper.PriceListEntry Entry, double Roi)>();
            foreach (var (entry, _) in ranked.OrderByDescending(x => x.Upper).Take(settings.StructuralBookChecks))
            {
                ct.ThrowIfCancellationRequested();
                var book = await host.SteamMarket.GetOrderBookAsync(entry.MarketHashName, TimeSpan.FromMinutes(30), RequestPriority.Normal, ct);
                cycle.SteamLookups++;
                if (book == null) continue;
                var exit = OpportunityEvaluator.EvaluateSteamExit(book, ArbitrageMath.UsdCentsToPenceCeil(entry.MinPriceCents, fx), k, settings.Evaluation);
                if (exit.MeetsThreshold) confirmed.Add((entry, exit.Roi));
            }

            int lookups = 0;
            foreach (var (entry, roi) in confirmed.OrderByDescending(x => x.Roi))
            {
                if (lookups >= settings.StructuralTargetedLookups) break;
                if (!host.CSFloat.RateLimits.CanSpend(CSFloatWrapper.ListingsBucket, settings.FeedReserve / 2 + 2)) { cycle.Note = "listing budget reserved for the feed"; break; }
                if (host.OpenPositionsFor(entry.MarketHashName) >= settings.MaxUnitsPerItem) continue;
                lookups++;
                var page = await host.CSFloat.SearchListingsAsync(new CSFloatWrapper.ListingQuery
                {
                    SortBy = "lowest_price",
                    Limit = 10,
                    MarketHashName = entry.MarketHashName,
                    MaxPriceCents = maxPrice,
                }, ct);
                cycle.ListingsReturned += page.Listings.Count;
                foreach (var l in page.Listings) seen.Add(l.Id);
                await ProcessListingsAsync(page.Listings, "structural", cycle, ct);
            }

            cycle.DurationMs = clock.Elapsed.TotalMilliseconds;
            cycle.Note = $"{list.Count} items ranked, {ranked.Count} plausible, {confirmed.Count} confirmed on Steam, {lookups} targeted lookups";
            host.Analytics.RecordCycle(cycle);
            lock (statusGate)
            {
                status.LastStructuralScanUtc = utcNow();
                status.LastStructuralSummary = cycle.Note;
            }
        }

        private async Task<List<CSFloatWrapper.PriceListEntry>?> GetPriceListAsync(CancellationToken ct)
        {
            if (priceList != null && utcNow() - priceListLoadedUtc < TimeSpan.FromMinutes(30)) return priceList;
            try
            {
                priceList = await host.CSFloat.GetPriceListAsync(ct);
                priceListLoadedUtc = utcNow();
                lock (statusGate)
                {
                    status.PriceListLoadedUtc = priceListLoadedUtc;
                    status.PriceListItems = priceList.Count;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NoteError("CSFloat price list failed: " + ex.Message);
            }
            return priceList;
        }

        // ───────────────────────────── Maintenance: bulk prices + conversion model ─────────────────────────────

        private async Task MaintenanceLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (utcNow() >= nextReferenceRefreshUtc)
                    {
                        bool ok = await host.ReferencePrices.RefreshAsync(referenceHttp, ct);
                        nextReferenceRefreshUtc = utcNow() + (ok ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30));
                        if (!ok) NoteError("bulk Steam price feed: " + host.ReferencePrices.LastError);
                        lock (statusGate)
                        {
                            status.ReferencePricesLoadedUtc = host.ReferencePrices.LoadedAtUtc;
                            status.ReferencePriceItems = host.ReferencePrices.Count;
                        }
                    }
                    if (utcNow() >= nextConversionRunUtc && host.GbpPerUsd > 0)
                    {
                        bool ok = await RunConversionModelAsync(ct);
                        nextConversionRunUtc = utcNow() + (ok ? TimeSpan.FromHours(3) : TimeSpan.FromMinutes(20));
                    }
                    if (utcNow() >= nextPanelUtc)
                    {
                        int observed = await RunMarketPanelAsync(ct);
                        nextPanelUtc = utcNow() + (observed > 0 ? host.Settings.MarketPanelInterval : TimeSpan.FromMinutes(20));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NoteError("maintenance failed: " + ex.Message);
                    nextConversionRunUtc = utcNow() + TimeSpan.FromMinutes(20);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }

        /// <summary>Recomputes the Steam→CSFloat conversion coefficient and the conversion plan from live data.</summary>
        public async Task<bool> RunConversionModelAsync(CancellationToken ct)
        {
            double fx = host.GbpPerUsd;
            var list = await GetPriceListAsync(ct);
            if (list == null || list.Count == 0) return false;
            var options = new ConversionModel.Options();
            var ranked = ConversionModel.RankCandidates(list, host.ReferencePrices.Count > 0 ? host.ReferencePrices : null, fx, options);
            // Always price the most-listed cases too: they absorb unlimited volume and set the floor rate.
            var anchors = list.Where(e => e.MarketHashName.EndsWith(" Case", StringComparison.Ordinal) && e.MinPriceCents >= options.MinAskCents)
                .OrderByDescending(e => e.Quantity).Take(3)
                .Select(e => new ConverterCandidate { MarketHashName = e.MarketHashName, CSFloatMinAskCents = e.MinPriceCents, CSFloatListingCount = e.Quantity });
            var candidates = ranked.Take(options.BookLookups).Concat(anchors)
                .GroupBy(c => c.MarketHashName).Select(g => g.First()).ToList();

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var book = await host.SteamMarket.GetOrderBookAsync(candidate.MarketHashName, TimeSpan.FromHours(2), RequestPriority.Low, ct);
                if (book != null && book.HasSellOrders) ConversionModel.ApplySteamBook(candidate, book, fx, options.UnitsToPrice);
            }

            var anchorNames = new HashSet<string>(anchors.Select(a => a.MarketHashName), StringComparer.Ordinal);
            var toVerify = candidates.Where(c => c.Coefficient > 0)
                .OrderByDescending(c => anchorNames.Contains(c.MarketHashName))
                .ThenByDescending(ConversionModel.VerificationScore)
                .Take(options.SalesVerifications + anchorNames.Count)
                .ToList();
            foreach (var candidate in toVerify)
            {
                ct.ThrowIfCancellationRequested();
                var graph = await GetSalesGraphCachedAsync(candidate.MarketHashName, ct);
                if (graph != null) ConversionModel.ApplySalesGraph(candidate, graph, fx, options);
            }

            // Converters bought on Steam sit in Steam's 7-day hold before they can go to a CSFloat buyer: value each at
            // the price its category is expected to have by then.
            double holdDays = host.ExitEnvironment.Timeline.ConverterHoldDays;
            double HoldGrowth(string name) => Math.Exp(host.MarketModel.DriftPerDay(ItemCategories.Of(name)) * holdDays);
            var snapshot = ConversionModel.Combine(candidates, host.TargetConversionVolumePence, host.Settings.DefaultConversionCoefficient, options, utcNow(),
                HoldGrowth, Math.Exp(host.MarketModel.DriftPerDay(ItemCategory.Container) * holdDays));
            var histories = new Dictionary<string, List<SteamPricePoint>>(StringComparer.Ordinal);
            foreach (var converter in snapshot.Converters.Where(c => c.VerifiedBySales).Take(5))
            {
                var history = await host.SteamMarket.GetPriceHistoryAsync(converter.MarketHashName, RequestPriority.Low, ct);
                if (history != null) histories[converter.MarketHashName] = history;
            }
            var plan = BuildLiquidityPlan(snapshot, histories, fx);
            await host.OnConversionModelComputedAsync(snapshot, plan);
            lock (statusGate) status.LastConversionModelUtc = utcNow();
            return true;
        }

        /// <summary>Skins and stickers are where deals happen, so they are sampled twice as often.</summary>
        private static readonly ItemCategory[] PanelCategories =
            { ItemCategory.Skin, ItemCategory.Sticker, ItemCategory.Container, ItemCategory.Charm, ItemCategory.Skin, ItemCategory.Sticker, ItemCategory.Patch, ItemCategory.Other };

        /// <summary>
        /// Samples a few random items (not deal candidates, which are biased towards falling prices) and feeds
        /// the market model: each item's realised drift over the last month, and how far a forecast made from
        /// its history 9 days ago landed from what happened. This keeps the category drift and the forecast's
        /// uncertainty calibrated against live evidence. Costs a few low-priority Steam requests.
        /// </summary>
        public async Task<int> RunMarketPanelAsync(CancellationToken ct)
        {
            var list = await GetPriceListAsync(ct);
            if (list == null || list.Count == 0) return 0;
            var pools = list.Where(e => e.MinPriceCents is >= 100 and <= 4000 && e.Quantity >= 15)
                .GroupBy(e => ItemCategories.Of(e.MarketHashName))
                .ToDictionary(g => g.Key, g => g.ToList());
            int observed = 0;
            for (int i = 0; i < host.Settings.MarketPanelItems; i++)
            {
                ct.ThrowIfCancellationRequested();
                var category = PanelCategories[panelRotation++ % PanelCategories.Length];
                if (!pools.TryGetValue(category, out var pool) || pool.Count == 0) continue;
                var entry = pool[Random.Shared.Next(pool.Count)];
                var history = await host.SteamMarket.GetPriceHistoryAsync(entry.MarketHashName, RequestPriority.Low, ct);
                if (history == null || history.Count == 0) continue;
                DateTime now = utcNow();
                double? drift = PriceForecaster.TrailingDrift(PriceForecaster.Daily(history), now);
                double? z = PriceForecaster.WalkForwardZ(history, entry.MarketHashName, now, host.MarketModel);
                if (host.MarketModel.Observe(entry.MarketHashName, category, drift, z, now)) observed++;
            }
            return observed;
        }

        private async Task<List<CSFloatWrapper.SalesGraphPoint>?> GetSalesGraphCachedAsync(string marketHashName, CancellationToken ct)
        {
            lock (salesGraphCache)
            {
                if (salesGraphCache.TryGetValue(marketHashName, out var cached) && utcNow() - cached.FetchedUtc < TimeSpan.FromHours(12))
                    return cached.Graph;
            }
            // The history endpoints allow 500/day; never let the model eat the last of it.
            if (!host.CSFloat.RateLimits.CanSpend(CSFloatWrapper.HistoryBucket, 100)) return null;
            try
            {
                var graph = await host.CSFloat.GetSalesGraphAsync(marketHashName, ct);
                lock (salesGraphCache) salesGraphCache[marketHashName] = (utcNow(), graph);
                return graph;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NoteError($"CSFloat sales history for {marketHashName}: {ex.Message}");
                return null;
            }
        }

        // ───────────────────────────── Status ─────────────────────────────

        public EngineStatus GetStatus()
        {
            lock (statusGate)
            {
                status.ListingsSeen = Interlocked.Read(ref listingsSeen);
                status.Evaluated = Interlocked.Read(ref evaluated);
                status.Prefiltered = Interlocked.Read(ref prefiltered);
                status.SteamLookups = Interlocked.Read(ref steamLookups);
                status.Opportunities = Interlocked.Read(ref opportunities);
                status.PurchaseAttempts = Interlocked.Read(ref purchaseAttempts);
                status.Purchases = Interlocked.Read(ref purchases);
                status.CSFloatPausedUntilUtc = csfloatPausedUntilUtc > utcNow() ? csfloatPausedUntilUtc : null;
                status.RecentErrors = recentErrors.ToList();
                return new EngineStatus
                {
                    State = status.State, StartedUtc = status.StartedUtc, LastFeedPollUtc = status.LastFeedPollUtc,
                    CurrentFeedIntervalSeconds = status.CurrentFeedIntervalSeconds, FeedPolls = status.FeedPolls,
                    FeedCoverageGaps = status.FeedCoverageGaps, FeedGapFillPages = status.FeedGapFillPages,
                    ObservedListingsPerMinute = status.ObservedListingsPerMinute, LastSweepUtc = status.LastSweepUtc,
                    LastStructuralScanUtc = status.LastStructuralScanUtc, LastConversionModelUtc = status.LastConversionModelUtc,
                    PriceListLoadedUtc = status.PriceListLoadedUtc, PriceListItems = status.PriceListItems,
                    ReferencePricesLoadedUtc = status.ReferencePricesLoadedUtc, ReferencePriceItems = status.ReferencePriceItems,
                    ListingsSeen = status.ListingsSeen, Evaluated = status.Evaluated, Prefiltered = status.Prefiltered,
                    SteamLookups = status.SteamLookups, Opportunities = status.Opportunities,
                    PurchaseAttempts = status.PurchaseAttempts, Purchases = status.Purchases,
                    CSFloatPausedUntilUtc = status.CSFloatPausedUntilUtc, RecentErrors = status.RecentErrors,
                    LastStructuralSummary = status.LastStructuralSummary,
                };
            }
        }

        private void SetState(string state)
        {
            lock (statusGate) status.State = state;
        }

        private void Increment(Action<EngineStatus> mutate)
        {
            lock (statusGate) mutate(status);
        }

        private void NoteError(string message)
        {
            lock (statusGate)
            {
                recentErrors.AddFirst($"{utcNow():yyyy-MM-dd HH:mm:ss}Z {message}");
                while (recentErrors.Count > 25) recentErrors.RemoveLast();
            }
        }

        private void PauseCSFloat(DateTime untilUtc, string why)
        {
            if (untilUtc > utcNow().AddHours(2)) untilUtc = utcNow().AddHours(2);
            if (untilUtc > csfloatPausedUntilUtc) csfloatPausedUntilUtc = untilUtc;
            NoteError(why);
        }

        private async Task WaitForCSFloatAsync(CancellationToken ct)
        {
            TimeSpan wait = csfloatPausedUntilUtc - utcNow();
            if (wait > TimeSpan.Zero)
            {
                SetState($"waiting for CSFloat rate limit (until {csfloatPausedUntilUtc:HH:mm:ss} UTC)");
                await Task.Delay(wait, ct);
                SetState("running");
            }
        }
    }
}
