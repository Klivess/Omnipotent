using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Text;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.CS2LiquidityFinder;

namespace Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs
{
    /// <summary>
    /// The bot's analytics and purchase ledger.
    ///
    /// Storage is bounded. The previous version wrote one indented JSON file (with a full Steam order book)
    /// per CSFloat listing ever evaluated, kept every one in memory, and read them all — synchronously, on
    /// the service thread — before the service registered its routes. After a year of scanning that load
    /// is why /cs2arbitragebot/* answered "Route not found". Now: purchases (small) are kept in full,
    /// evaluations feed a running aggregate, and only notable evaluations are logged (daily JSONL, rotated).
    /// Legacy ScannedComparisons/ScanResults folders are left untouched on disk and never loaded.
    /// </summary>
    public class Scanalytics
    {
        public List<PurchasedListing> AllPurchasedListingsInHistory { get; private set; } = new();
        private readonly object historyLock = new();
        private readonly CS2ArbitrageBot parent;

        private ScanAggregate aggregate = new();
        private bool aggregateDirty;
        private readonly LinkedList<OpportunityEvaluation> recentNotable = new();
        private readonly List<OpportunityEvaluation> pendingNotableLog = new();
        private List<ScanCycleSummary> recentCycles = new();
        private bool cyclesDirty;

        public const int RecentNotableCapacity = 300;
        public const int RecentCycleCapacity = 500;
        public const int NotableLogRetentionDays = 30;
        /// <summary>Evaluations within this distance of a buy threshold are logged in full.</summary>
        public const double NotableRoiMargin = 0.10;

        public Scanalytics(CS2ArbitrageBot parent)
        {
            this.parent = parent;
        }

        private static string LabsDirectory => OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotLabsDirectory);
        private static string AggregatePath => Path.Combine(LabsDirectory, "scan-aggregate.json");
        private static string CyclesPath => Path.Combine(LabsDirectory, "scan-cycles.json");
        private static string NotableDirectory => Path.Combine(LabsDirectory, "NotableEvaluations");

        // ───────────────────────────── Loading ─────────────────────────────

        /// <summary>Loads the small state files. Never touches the legacy per-listing folders.</summary>
        public async Task LoadAsync()
        {
            var loadedPurchases = await JsonFiles<PurchasedListing>(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotPurchasedItemsDirectory));
            lock (historyLock) AllPurchasedListingsInHistory = loadedPurchases.Where(p => p != null && !string.IsNullOrEmpty(p.CSFloatListingID)).ToList();

            try
            {
                if (File.Exists(AggregatePath))
                    aggregate = JsonConvert.DeserializeObject<ScanAggregate>(await File.ReadAllTextAsync(AggregatePath)) ?? new ScanAggregate();
            }
            catch (Exception ex) { await parent.ServiceLogError(ex, "Couldn't read the CS2 scan aggregate; starting a new one.", false); }
            try
            {
                if (File.Exists(CyclesPath))
                    recentCycles = JsonConvert.DeserializeObject<List<ScanCycleSummary>>(await File.ReadAllTextAsync(CyclesPath)) ?? new();
            }
            catch (Exception ex) { await parent.ServiceLogError(ex, "Couldn't read the CS2 scan cycle summaries.", false); }
        }

        private static async Task<List<T>> JsonFiles<T>(string directory)
        {
            var result = new List<T>();
            if (!Directory.Exists(directory)) return result;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var item = JsonConvert.DeserializeObject<T>(await File.ReadAllTextAsync(file));
                    if (item != null) result.Add(item);
                }
                catch { /* a corrupt file must not block startup */ }
            }
            return result;
        }

        /// <summary>Counts legacy per-listing files (reported on the status route; nothing is read or deleted).</summary>
        public static long CountLegacyFiles(CancellationToken ct)
        {
            long count = 0;
            foreach (string dir in new[] { OmniPaths.GlobalPaths.CS2ArbitrageBotScannedComparisonsDirectory, OmniPaths.GlobalPaths.CS2ArbitrageBotScanResultsDirectory })
            {
                string path = OmniPaths.GetPath(dir);
                if (!Directory.Exists(path)) continue;
                foreach (var _ in Directory.EnumerateFiles(path))
                {
                    if (ct.IsCancellationRequested) return count;
                    count++;
                }
            }
            return count;
        }

        // ───────────────────────────── Evaluations ─────────────────────────────

        public void RecordEvaluation(OpportunityEvaluation evaluation, EvaluationSettings settings)
        {
            lock (historyLock)
            {
                aggregate.Record(evaluation);
                aggregateDirty = true;
                double nearestThreshold = Math.Min(settings.MinimumSteamRoiPercent, settings.MinimumRelistRoiPercent) / 100.0;
                if (evaluation.ShouldBuy || evaluation.BestRoi >= nearestThreshold - NotableRoiMargin)
                {
                    recentNotable.AddFirst(evaluation);
                    while (recentNotable.Count > RecentNotableCapacity) recentNotable.RemoveLast();
                    pendingNotableLog.Add(evaluation);
                }
            }
        }

        public void RecordPurchaseOutcome(bool purchased)
        {
            lock (historyLock)
            {
                aggregate.RecordPurchaseAttempt(purchased);
                aggregateDirty = true;
            }
        }

        public List<OpportunityEvaluation> RecentNotable(int max = 100)
        {
            lock (historyLock) return recentNotable.Take(max).ToList();
        }

        public void RecordCycle(ScanCycleSummary cycle)
        {
            lock (historyLock)
            {
                recentCycles.Add(cycle);
                if (recentCycles.Count > RecentCycleCapacity) recentCycles.RemoveRange(0, recentCycles.Count - RecentCycleCapacity);
                cyclesDirty = true;
            }
        }

        public List<ScanCycleSummary> RecentCycles(int max = RecentCycleCapacity)
        {
            lock (historyLock) return recentCycles.Skip(Math.Max(0, recentCycles.Count - max)).ToList();
        }

        public ScanAggregate SnapshotAggregate()
        {
            lock (historyLock) return JsonConvert.DeserializeObject<ScanAggregate>(JsonConvert.SerializeObject(aggregate))!;
        }

        public ScannedComparisonAnalytics BuildAnalytics(double conversionCoefficient)
        {
            ScanAggregate copy;
            List<PurchasedListing> purchases;
            lock (historyLock)
            {
                copy = JsonConvert.DeserializeObject<ScanAggregate>(JsonConvert.SerializeObject(aggregate))!;
                purchases = new List<PurchasedListing>(AllPurchasedListingsInHistory);
            }
            return new ScannedComparisonAnalytics(copy, purchases, conversionCoefficient);
        }

        /// <summary>Persists dirty state. Called on a timer and at shutdown.</summary>
        public async Task FlushAsync()
        {
            string? aggregateJson = null, cyclesJson = null;
            List<OpportunityEvaluation> notable;
            lock (historyLock)
            {
                if (aggregateDirty) { aggregateJson = JsonConvert.SerializeObject(aggregate); aggregateDirty = false; }
                if (cyclesDirty) { cyclesJson = JsonConvert.SerializeObject(recentCycles); cyclesDirty = false; }
                notable = new List<OpportunityEvaluation>(pendingNotableLog);
                pendingNotableLog.Clear();
            }
            Directory.CreateDirectory(LabsDirectory);
            if (aggregateJson != null) await parent.GetDataHandler().WriteToFile(AggregatePath, aggregateJson);
            if (cyclesJson != null) await parent.GetDataHandler().WriteToFile(CyclesPath, cyclesJson);
            if (notable.Count > 0)
            {
                Directory.CreateDirectory(NotableDirectory);
                var byDay = notable.GroupBy(e => e.EvaluatedAtUtc.ToString("yyyy-MM-dd"));
                foreach (var day in byDay)
                {
                    var sb = new StringBuilder();
                    foreach (var e in day) sb.AppendLine(JsonConvert.SerializeObject(e, Formatting.None));
                    await parent.GetDataHandler().AppendContentToFile(Path.Combine(NotableDirectory, day.Key + ".jsonl"), sb.ToString());
                }
                PruneNotableLogs();
            }
        }

        private static void PruneNotableLogs()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.Date.AddDays(-NotableLogRetentionDays);
                foreach (string file in Directory.EnumerateFiles(NotableDirectory, "*.jsonl"))
                {
                    if (DateTime.TryParse(Path.GetFileNameWithoutExtension(file), out var day) && day < cutoff) File.Delete(file);
                }
            }
            catch { /* best effort */ }
        }

        // ───────────────────────────── Purchases ─────────────────────────────

        public bool HasPurchased(string csfloatListingId)
        {
            lock (historyLock) return AllPurchasedListingsInHistory.Any(p => p.CSFloatListingID == csfloatListingId);
        }

        public List<PurchasedListing> PurchasesSnapshot()
        {
            lock (historyLock) return new List<PurchasedListing>(AllPurchasedListingsInHistory);
        }

        public async Task SavePurchasedListing(PurchasedListing purchasedListing)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotPurchasedItemsDirectory);
            string filename = purchasedListing.ItemMarketHashName + purchasedListing.CSFloatListingID + "id.json";
            filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(purchasedListing, Formatting.Indented));
        }

        public async Task UpdatePurchasedListing(PurchasedListing purchasedListing)
        {
            lock (historyLock)
            {
                AllPurchasedListingsInHistory.RemoveAll(k => k.CSFloatListingID == purchasedListing.CSFloatListingID);
                AllPurchasedListingsInHistory.Add(purchasedListing);
            }
            await SavePurchasedListing(purchasedListing);
            parent.QueueAnalyticsRefresh();
        }

        // ───────────────────────────── Balances ─────────────────────────────

        public async Task<List<CSFloatAndSteamBalance>> GetAllLogsOfCSFloatAndSteamBalance()
        {
            var balances = await JsonFiles<CSFloatAndSteamBalance>(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDailyAccountInfoDirectory));
            return balances.OrderBy(b => b.DateTimeOfBalanceRecord).ToList();
        }

        /// <summary>Records today's balances. A missing Steam balance carries the last known value forward.</summary>
        public async Task RecordAccountInfoAsync()
        {
            var info = new CSFloatAndSteamBalance();
            var account = await parent.csFloatWrapper.GetAccountInformation();
            parent.csfloatAccountInformation = account;
            info.CSFloatUsableBalanceInPounds = account.BalanceInPounds;
            info.CSFloatPendingBalanceInPounds = account.PendingBalanceInPounds;
            info.CSFloatTotalBalanceInPounds = info.CSFloatUsableBalanceInPounds + info.CSFloatPendingBalanceInPounds;
            info.CSFloatProfileStatistics = account.Statistics;
            info.CSFloatFee = account.Fee;
            info.CSFloatWithdrawFee = account.WithdrawFee;

            SteamAPIProfileWrapper.SteamBalance? steam = null;
            try { steam = await parent.steamAPIWrapper.profileWrapper.GetSteamBalance(); }
            catch (Exception ex) { await parent.ServiceLogError(ex, "Steam balance unavailable for the daily record.", false); }
            if (steam is { } s)
            {
                info.SteamUsableBalanceInPounds = s.UsableBalanceInPounds;
                info.SteamPendingBalanceInPounds = s.PendingBalanceInPounds;
            }
            else
            {
                var last = (await GetAllLogsOfCSFloatAndSteamBalance()).LastOrDefault();
                info.SteamUsableBalanceInPounds = last?.SteamUsableBalanceInPounds ?? 0;
                info.SteamPendingBalanceInPounds = last?.SteamPendingBalanceInPounds ?? 0;
                info.SteamBalanceCarriedForward = true;
            }
            info.SteamTotalBalanceInPounds = info.SteamUsableBalanceInPounds + info.SteamPendingBalanceInPounds;
            info.DateTimeOfBalanceRecord = DateTime.Now;

            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDailyAccountInfoDirectory);
            string filename = string.Join("-", $"AccountInfo{DateTime.Now:yyyy-MM-dd}.json".Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(info, Formatting.Indented));
            await parent.ServiceLog($"Recorded account info: £{info.CSFloatUsableBalanceInPounds:F2} CSFloat, £{info.SteamUsableBalanceInPounds:F2} Steam{(info.SteamBalanceCarriedForward ? " (carried forward)" : "")}.");
        }

        public class CSFloatAndSteamBalance
        {
            public double CSFloatUsableBalanceInPounds { get; set; }
            public double CSFloatTotalBalanceInPounds { get; set; }
            public double CSFloatPendingBalanceInPounds { get; set; }
            public double SteamUsableBalanceInPounds { get; set; }
            public double SteamPendingBalanceInPounds { get; set; }
            public double SteamTotalBalanceInPounds { get; set; }
            public bool SteamBalanceCarriedForward { get; set; }

            public DateTime DateTimeOfBalanceRecord { get; set; }
            public CSFloatWrapper.Statistics CSFloatProfileStatistics { get; set; } = new();
            public double CSFloatFee;
            public double CSFloatWithdrawFee;
        }

        // ───────────────────────────── Conversion plan (KM "liquidity plan") ─────────────────────────────

        public class LiquidityPlan
        {
            public DateTime ProductionDateOfLiquiditySearchResultUsed;
            public List<ContainerGap> Top10Gaps = new();
            public Dictionary<string, List<LiquidityPlanBuyTactics>> BuyOrderTacticsAndCorrespondingReturns = new();
            public Dictionary<string, SteamPriceHistoryDataPoint> OptimalPurchasePointsForEachContainerGap = new();
            public string LiquidityPlanDescription = "";
            public double ConversionCoefficientUsed;
            public string ConversionBasis = "";

            public struct LiquidityPlanBuyTactics
            {
                public string ItemMarketHashName;
                public double ReturnCoefficient;
                public double PriceNeededToBuyOnSteam;
                public double PriceNeededToSellOnCSFloat;
                public SteamPriceHistoryDataPoint LastTimeSoldAtThisPriceOrBelow;
            }
        }

        /// <summary>
        /// Turns the conversion model into the plan the KM page and Discord command show: which items to buy
        /// on Steam (and at what price) to carry Steam wallet funds back to CSFloat.
        /// </summary>
        public static LiquidityPlan BuildLiquidityPlan(ConversionModelSnapshot model, IReadOnlyDictionary<string, List<SteamPricePoint>> histories, double gbpPerUsd)
        {
            var plan = new LiquidityPlan
            {
                ProductionDateOfLiquiditySearchResultUsed = model.ComputedAtUtc,
                ConversionCoefficientUsed = model.Coefficient,
                ConversionBasis = model.Basis,
            };
            foreach (var converter in model.Converters.Where(c => c.Coefficient > 0).Take(10))
            {
                histories.TryGetValue(converter.MarketHashName, out var history);
                var points = (history ?? new List<SteamPricePoint>())
                    .Select(p => new SteamPriceHistoryDataPoint { DateTimeRecorded = p.TimeUtc, PriceInPounds = p.MedianPriceGbp, QuantitySold = p.Purchases })
                    .ToList();
                double csfloatSaleCents = converter.CSFloatAverageSaleCents > 0 ? converter.CSFloatAverageSaleCents : converter.CSFloatMinAskCents;
                int csfloatPence = ArbitrageMath.UsdCentsToPenceFloor((long)Math.Floor(csfloatSaleCents), gbpPerUsd);
                double steamPounds = (converter.SteamUnitCostPence > 0 ? converter.SteamUnitCostPence : converter.SteamLowestSellPence) / 100.0;
                var gap = new ContainerGap
                {
                    csfloatContainer = new Container
                    {
                        MarketHashName = converter.MarketHashName,
                        PriceInCents = (int)Math.Round(csfloatSaleCents),
                        PriceInPence = csfloatPence,
                        PriceInPounds = csfloatPence / 100.0,
                        ImageURL = "",
                        containerType = ContainerType.WeaponCase,
                    },
                    steamListing = new SteamAPIWrapper.ItemListing
                    {
                        Name = converter.MarketHashName,
                        CheapestSellOrderPriceInPence = converter.SteamLowestSellPence,
                        CheapestSellOrderPriceInPounds = converter.SteamLowestSellPence / 100.0,
                        HighestBuyOrderPriceInPence = converter.SteamHighestBuyPence,
                        HighestBuyOrderPriceInPounds = converter.SteamHighestBuyPence / 100.0,
                        PriceText = "£" + (converter.SteamLowestSellPence / 100.0).ToString("F2"),
                        ListingURL = "https://steamcommunity.com/market/listings/730/" + Uri.EscapeDataString(converter.MarketHashName),
                        SellListings = converter.SteamSellOrderCount.ToString(),
                    },
                    ReturnCoefficientFromSteamtoCSFloat = steamPounds > 0 ? (csfloatPence / 100.0) / steamPounds : 0,
                    ReturnCoefficientFromSteamToCSFloatTaxIncluded = converter.Coefficient,
                    priceHistory = points.Where(p => p.DateTimeRecorded >= DateTime.UtcNow.AddDays(-14)).ToList(),
                    IdealCSFloatSellPriceInCents = (int)Math.Round(csfloatSaleCents),
                    IdealCSFloatSellPriceInPence = csfloatPence,
                    IdealCSFloatSellPriceInPounds = csfloatPence / 100.0,
                    // Buying with a buy order one penny over the current best one is usually filled within days.
                    IdealPriceToPurchaseOnSteamInPounds = converter.SteamHighestBuyPence > 0 ? (converter.SteamHighestBuyPence + 1) / 100.0 : steamPounds,
                };
                gap.IdealReturnCoefficientFromSteamtoCSFloat = gap.IdealPriceToPurchaseOnSteamInPounds > 0 ? gap.csfloatContainer.PriceInPounds / gap.IdealPriceToPurchaseOnSteamInPounds : 0;
                gap.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded = gap.IdealReturnCoefficientFromSteamtoCSFloat * 0.98;
                plan.Top10Gaps.Add(gap);

                var tactics = new List<LiquidityPlan.LiquidityPlanBuyTactics>();
                for (int i = 84; i < 100; i++)
                {
                    double coefficient = i / 100.0;
                    double buyAt = gap.csfloatContainer.PriceInPounds * 0.98 / coefficient;
                    var match = points.Where(p => p.PriceInPounds <= buyAt).OrderByDescending(p => p.DateTimeRecorded).FirstOrDefault();
                    tactics.Add(new LiquidityPlan.LiquidityPlanBuyTactics
                    {
                        ItemMarketHashName = converter.MarketHashName,
                        ReturnCoefficient = coefficient,
                        PriceNeededToBuyOnSteam = buyAt,
                        PriceNeededToSellOnCSFloat = gap.csfloatContainer.PriceInPounds,
                        LastTimeSoldAtThisPriceOrBelow = match,
                    });
                }
                plan.BuyOrderTacticsAndCorrespondingReturns[converter.MarketHashName] = tactics;
            }
            plan.OptimalPurchasePointsForEachContainerGap = GetOptimalPurchasePoints(plan);

            var sb = new StringBuilder();
            sb.AppendLine($"Steam→CSFloat conversion coefficient: {model.Coefficient:P1} ({model.Basis}).");
            foreach (var gap in plan.Top10Gaps.Take(5))
            {
                sb.AppendLine($"Item: {gap.csfloatContainer.MarketHashName} — buy on Steam at ≤ £{gap.IdealPriceToPurchaseOnSteamInPounds:F2}, sell on CSFloat at ~£{gap.csfloatContainer.PriceInPounds:F2} " +
                              $"(returns {gap.ReturnCoefficientFromSteamToCSFloatTaxIncluded:P0} buying at the ask, {gap.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded:P0} via buy order).");
            }
            sb.AppendLine("Steam holds Market purchases for 7 days before they can be traded to a CSFloat buyer, and the CSFloat sale then pays out " +
                          "after the buyer's own 7-day protection: allow ~15 days from wallet to spendable CSFloat cash. The coefficient above is at " +
                          "prices expected after the hold; prefer converters that sell daily, so the hold is the only wait.");
            plan.LiquidityPlanDescription = sb.ToString().TrimEnd();
            return plan;
        }

        public static Dictionary<string, SteamPriceHistoryDataPoint> GetOptimalPurchasePoints(LiquidityPlan plan)
        {
            var optimalPoints = new Dictionary<string, SteamPriceHistoryDataPoint>();
            var now = DateTime.UtcNow;
            foreach (var kvp in plan.BuyOrderTacticsAndCorrespondingReturns)
            {
                var scored = kvp.Value
                    .Where(t => t.LastTimeSoldAtThisPriceOrBelow.DateTimeRecorded != default)
                    .Select(t =>
                    {
                        var daysSince = (now - t.LastTimeSoldAtThisPriceOrBelow.DateTimeRecorded).TotalDays;
                        double score = (t.ReturnCoefficient * t.LastTimeSoldAtThisPriceOrBelow.QuantitySold) / (1.0 + daysSince);
                        return new { DataPoint = t.LastTimeSoldAtThisPriceOrBelow, Score = score, Age = daysSince };
                    })
                    .ToList();
                var recent = scored.Where(x => x.Age <= 7).ToList();
                var candidates = recent.Any() ? recent : scored;
                if (candidates.Any()) optimalPoints[kvp.Key] = candidates.OrderByDescending(x => x.Score).First().DataPoint;
            }
            return optimalPoints;
        }

        /// <summary>Kept for callers of the old API: the current model coefficient (no disk I/O any more).</summary>
        public Task<double> ExpectedSteamToCSFloatConversionPercentage() => Task.FromResult(parent.CurrentConversionCoefficient);

        // ───────────────────────────── Data shapes ─────────────────────────────

        /// <summary>Strategy stages. Values are persisted and shown by the KM site — only ever append.</summary>
        public enum StrategicStages
        {
            WaitingForCSFloatSellerToAcceptSale,
            WaitingForCSFloatTradeToBeSent,
            WaitingForCSFloatTradeToBeAccepted,
            JustRetrieved,
            WaitingForMarketSaleOnSteam,
            WaitingForConversionItemsToPurchase,
            WaitingForConversionItemsToSell,
            StrategyCompleted,
            /// <summary>The CSFloat trade was cancelled or failed; the purchase was refunded.</summary>
            TradeCancelled,
            /// <summary>Relisted on CSFloat after trade protection; waiting for a buyer.</summary>
            WaitingForCSFloatResale,
        }

        public class PurchasedListing
        {
            public ScannedComparison comparison = null!;
            public string CSFloatListingID = "";
            public int ExpectedAbsoluteProfitInPence;
            public float ExpectedAbsoluteProfitInPounds;
            public float ExpectedProfitPercentage;

            public float ActualProfitPercentage;
            public float ActualAbsoluteProfitInPounds;
            public float ActualAbsoluteProfitInPence;

            public DateTime TimeOfPurchase;
            public DateTime TimeOfSellerToAcceptSale;
            public DateTime TimeOfSellerToSendTradeOffer;
            public DateTime TimeOfItemRetrieval;
            public DateTime PredictedTimeToBeResoldOnSteam;
            public DateTime ActualTimeResoldOnSteam;
            public DateTime TimeOfConvertToRealFunds;
            public DateTime TimeOfCollectedRevenue;

            public string CSFloatToSteamTradeOfferLink = "";

            public float ItemFloatValue;
            public string ItemMarketHashName = "";

            public float ActualSalePriceOnSteam;

            public StrategicStages CurrentStrategicStage;

            // ── v2 fields (absent in old files → defaults) ──
            /// <summary>"SteamMarket" or "CSFloatRelist" — the exit chosen at purchase time.</summary>
            public string PlannedExit = nameof(ExitRoute.SteamMarket);
            public int PurchasePriceCents;
            public int PurchaseCostPence;
            public int ExpectedNetCashPence;
            public double ConversionCoefficientAtPurchase;
            public string PurchaseSource = "";
            public string LastTradeState = "";
            public int SaleAttempts;
            public DateTime LastSaleAttemptUtc;
            public string CSFloatResaleListingID = "";
            public int CSFloatResalePriceCents;
            public string Notes = "";

            // ── v3 fields: the exit model (absent in old files → defaults) ──
            /// <summary>The exit plan made at purchase, with its forecasts for when the item becomes tradable.</summary>
            public ExitDecision? PurchasePlan;
            /// <summary>Exit decisions since (the sale decision, listing reviews), oldest first; bounded.</summary>
            public List<ExitDecision> ExitHistory = new();
            /// <summary>CSFloat's value for this unit at purchase (float-adjusted), USD cents, and that ÷ the item's base value.</summary>
            public int AnchorCentsAtPurchase;
            public double AnchorFloatFactor = 1;
            public DateTime ListedOnCSFloatAtUtc;
            /// <summary>
            /// No-sale evidence at the current price: sales the model expected by now without one happening (Σ time ÷
            /// expected days). A re-price carries over only the part that bears on the new price.
            /// </summary>
            public double RelistModelExposure;
            /// <summary>The same, accumulated over the whole relist and never reduced (the track record calibrates on it).</summary>
            public double RelistTotalExposure;
            public DateTime RelistExposureUpdatedUtc;
            /// <summary>Expected days to sell (base model) of the current listing price; exposure accrues against it.</summary>
            public double RelistBaseDaysToSell;
            public int RelistRepriceCount;
            public DateTime LastRelistChangeUtc;
            /// <summary>When a buyer bought the relisted item (the trade still has to be sent and verified).</summary>
            public DateTime ResaleSoldAtUtc;
            public DateTime NextExitReviewUtc;
            /// <summary>Sale decisions postponed because the market data to make them was unavailable.</summary>
            public int SaleDeferrals;
            public DateTime SaleDeferredUntilUtc;

            public void RecordExitDecision(ExitDecision decision)
            {
                ExitHistory ??= new List<ExitDecision>();
                ExitHistory.Add(decision);
                if (ExitHistory.Count > 40) ExitHistory.RemoveRange(1, ExitHistory.Count - 40); // keep the first (the sale decision) for calibration
            }
        }

        /// <summary>A CSFloat listing paired with Steam data at evaluation time (persisted inside purchases).</summary>
        public class ScannedComparison
        {
            public string ItemMarketHashName = "";
            public string PriceTextCSFloat = "";
            public string PriceTextSteamMarket = "";
            public double RawArbitrageGain;
            public double ArbitrageGainAfterSteamTax;
            public double PredictedOverallArbitrageGain;
            public string CSFloatURL = "";
            public string SteamListingURL = "";
            public CSFloatWrapper.ItemListing CSFloatListing;
            public SteamAPIWrapper.ItemListing SteamListing;
            public DateTime LastUpdate;

            [JsonConstructor]
            public ScannedComparison() { }

            public ScannedComparison(CSFloatWrapper.ItemListing csfloatListing, SteamAPIWrapper.ItemListing steamListing, DateTime lastUpdate, double expectedConversionCoeff)
            {
                ItemMarketHashName = csfloatListing.ItemMarketHashName;
                PriceTextCSFloat = csfloatListing.PriceText;
                PriceTextSteamMarket = steamListing.PriceText;
                double cost = Math.Max(0.01, csfloatListing.PriceInPounds);
                double receives = ArbitrageMath.SteamSellerReceives(steamListing.HighestBuyOrderPriceInPence) / 100.0;
                RawArbitrageGain = steamListing.HighestBuyOrderPriceInPounds / cost;
                ArbitrageGainAfterSteamTax = receives / cost;
                PredictedOverallArbitrageGain = receives * expectedConversionCoeff / cost;
                CSFloatURL = csfloatListing.ListingURL;
                SteamListingURL = steamListing.ListingURL;
                CSFloatListing = csfloatListing;
                SteamListing = steamListing;
                LastUpdate = lastUpdate;
            }

            public static ScannedComparison FromEvaluation(CSFloatListing listing, SteamOrderBook? book, OpportunityEvaluation evaluation, double gbpPerUsd)
            {
                var csfloat = CSFloatWrapper.ToLegacyItemListing(listing, gbpPerUsd);
                var steam = SteamAPIWrapper.ItemListing.FromOrderBook(book, listing.MarketHashName, listing.ImageUrl);
                var comparison = new ScannedComparison(csfloat, steam, evaluation.EvaluatedAtUtc, evaluation.ConversionCoefficient);
                comparison.PredictedOverallArbitrageGain = 1 + evaluation.BestRoi;
                return comparison;
            }
        }

        /// <summary>One feed poll / sweep / structural pass, for the scanresults route.</summary>
        public class ScanCycleSummary
        {
            public DateTime StartedUtc { get; set; }
            public double DurationMs { get; set; }
            public string Strategy { get; set; } = "";
            public int ListingsReturned { get; set; }
            public int NewListings { get; set; }
            public int Evaluated { get; set; }
            public int SteamLookups { get; set; }
            public int Prefiltered { get; set; }
            public int Opportunities { get; set; }
            public int Purchased { get; set; }
            public int Errors { get; set; }
            public bool CoverageGap { get; set; }
            public double BestRoi { get; set; }
            public string? BestItem { get; set; }
            public string? Note { get; set; }
        }

        /// <summary>Running totals behind the KM analytics page. O(1) memory regardless of history length.</summary>
        public class ScanAggregate
        {
            public int Version { get; set; } = 2;
            public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
            public long TotalEvaluated { get; set; }
            public long[] BucketCounts { get; set; } = new long[5];
            public double[] BucketSteamPriceSums { get; set; } = new double[5];
            public long PositiveCount { get; set; }
            public long NegativeCount { get; set; }
            public double ProfitableFloatSum { get; set; }
            public long ProfitableFloatCount { get; set; }
            public double ProfitablePriceSum { get; set; }
            public double ProfitableGainSum { get; set; }
            public double UnprofitableFloatSum { get; set; }
            public long UnprofitableFloatCount { get; set; }
            public double UnprofitablePriceSum { get; set; }
            public double? HighestRoi { get; set; }
            public string HighestRoiItem { get; set; } = "None";
            public DateTime? HighestRoiAtUtc { get; set; }
            public long QualifiedOpportunities { get; set; }
            /// <summary>Sum of ln(1+ROI) over qualified opportunities (compounded expected return).</summary>
            public double QualifiedLogReturnSum { get; set; }
            public long PurchaseAttempts { get; set; }
            public long Purchases { get; set; }
            public long SteamExitBest { get; set; }
            public long RelistExitBest { get; set; }
            public Dictionary<string, DailyCounts> Daily { get; set; } = new();

            public class DailyCounts
            {
                public long Evaluated { get; set; }
                public long Qualified { get; set; }
                public long Purchased { get; set; }
                public double BestRoi { get; set; } = -1;
            }

            public static int BucketOf(double roi) => roi < 0 ? 0 : roi < 0.05 ? 1 : roi < 0.10 ? 2 : roi < 0.20 ? 3 : 4;

            public void Record(OpportunityEvaluation e)
            {
                bool available = e.Steam.Available || e.Relist.Available;
                double roi = available ? e.BestRoi : -1;
                TotalEvaluated++;
                int bucket = BucketOf(roi);
                BucketCounts[bucket]++;
                BucketSteamPriceSums[bucket] += e.SteamHighestBuyOrderPence / 100.0;
                double pricePounds = e.CostPence / 100.0;
                if (roi > 0)
                {
                    PositiveCount++;
                    ProfitablePriceSum += pricePounds;
                    ProfitableGainSum += 1 + roi;
                    if (e.FloatValue is double f) { ProfitableFloatSum += f; ProfitableFloatCount++; }
                }
                else
                {
                    NegativeCount++;
                    UnprofitablePriceSum += pricePounds;
                    if (e.FloatValue is double f) { UnprofitableFloatSum += f; UnprofitableFloatCount++; }
                }
                if (available && (HighestRoi == null || roi > HighestRoi))
                {
                    HighestRoi = roi;
                    HighestRoiItem = e.MarketHashName;
                    HighestRoiAtUtc = e.EvaluatedAtUtc;
                }
                if (e.ShouldBuy)
                {
                    QualifiedOpportunities++;
                    QualifiedLogReturnSum += Math.Log(1 + Math.Max(-0.99, roi));
                    if (e.BestRoute == ExitRoute.SteamMarket) SteamExitBest++;
                    if (e.BestRoute == ExitRoute.CSFloatRelist) RelistExitBest++;
                }
                var day = Day(e.EvaluatedAtUtc);
                day.Evaluated++;
                if (e.ShouldBuy) day.Qualified++;
                if (available && roi > day.BestRoi) day.BestRoi = roi;
            }

            public void RecordPurchaseAttempt(bool purchased)
            {
                PurchaseAttempts++;
                if (purchased)
                {
                    Purchases++;
                    Day(DateTime.UtcNow).Purchased++;
                }
            }

            private DailyCounts Day(DateTime utc)
            {
                string key = utc.ToString("yyyy-MM-dd");
                if (!Daily.TryGetValue(key, out var counts))
                {
                    counts = new DailyCounts();
                    Daily[key] = counts;
                    if (Daily.Count > 90)
                        foreach (var old in Daily.Keys.OrderBy(k => k).Take(Daily.Count - 90).ToList()) Daily.Remove(old);
                }
                return counts;
            }
        }

        /// <summary>The KM website's analytics DTO (field names are its contract).</summary>
        public class ScannedComparisonAnalytics
        {
            public int NumberOfListingsBelow0PercentGain { get; set; }
            public double MeanPriceOfListingsBelow0PercentGain { get; set; }
            public int NumberOfListingsBetween0And5PercentGain { get; set; }
            public double MeanPriceOfListingsBetween0And5PercentGain { get; set; }
            public int NumberOfListingsBetween5And10PercentGain { get; set; }
            public double MeanPriceOfListingsBetween5And10PercentGain { get; set; }
            public int NumberOfListingsBetween10And20PercentGain { get; set; }
            public double MeanPriceOfListingsBetween10And20PercentGain { get; set; }
            public int NumberOfListingsAbove20PercentGain { get; set; }
            public double MeanPriceOfListingsAbove20PercentGain { get; set; }
            public int TotalListingsScanned { get; set; }

            public double HighestPredictedGainFoundSoFar { get; set; }
            public string NameOfItemWithHighestPredictedGain { get; set; } = "None";
            public int CountListingsWithPositiveGain { get; set; }
            public int CountListingsWithNegativeGain { get; set; }
            public double PercentageChanceOfFindingPositiveGainListing { get; set; }
            public double MeanFloatValueOfProfitableListings { get; set; }
            public double MeanPriceOfProfitableListings { get; set; }
            public double MeanPriceOfUnprofitableListings { get; set; }
            public double MeanFloatValueOfUnprofitableListings { get; set; }

            public double MeanGainOfProfitableListings;

            /// <summary>Compounded expected return of every qualifying opportunity, in percent.</summary>
            public float TotalExpectedProfitPercent { get; set; }

            public DateTime FirstListingDateRecorded { get; set; }

            public DateTime AnalyticsGeneratedAt;

            public List<PurchasedListing> AllPurchasedItems = new();
            public List<TimeSpan> TimeTakenToPurchaseAllPurchasedItems = new();

            public double CurrentExpectedReturnCoefficientOfSteamToCSFloat;

            // v2 additions
            public long QualifiedOpportunities { get; set; }
            public long PurchaseAttempts { get; set; }
            public long Purchases { get; set; }
            public Dictionary<string, ScanAggregate.DailyCounts> Daily { get; set; } = new();

            [JsonConstructor]
            public ScannedComparisonAnalytics() { }

            /// <summary>Legacy constructor (tests and old callers) over raw comparisons.</summary>
            public ScannedComparisonAnalytics(List<ScannedComparison> data, List<PurchasedListing> purchasedListings, double currentExpectedReturnCoefficientOfSteamToCSFloat)
            {
                var aggregate = new ScanAggregate();
                foreach (var c in data)
                {
                    aggregate.Record(new OpportunityEvaluation
                    {
                        MarketHashName = c.ItemMarketHashName,
                        BestRoi = c.PredictedOverallArbitrageGain - 1,
                        CostPence = c.CSFloatListing.PriceInPence,
                        FloatValue = c.CSFloatListing.FloatValue,
                        SteamHighestBuyOrderPence = c.SteamListing.HighestBuyOrderPriceInPence,
                        EvaluatedAtUtc = c.LastUpdate,
                        Steam = new ExitEstimate { Available = true, Route = ExitRoute.SteamMarket, Roi = c.PredictedOverallArbitrageGain - 1 },
                    });
                }
                if (data.Count > 0) aggregate.StartedUtc = data.Min(c => c.LastUpdate);
                Fill(aggregate, purchasedListings, currentExpectedReturnCoefficientOfSteamToCSFloat, data.Count == 0);
            }

            public ScannedComparisonAnalytics(ScanAggregate aggregate, List<PurchasedListing> purchasedListings, double currentExpectedReturnCoefficientOfSteamToCSFloat)
            {
                Fill(aggregate, purchasedListings, currentExpectedReturnCoefficientOfSteamToCSFloat, aggregate.TotalEvaluated == 0);
            }

            private void Fill(ScanAggregate a, List<PurchasedListing> purchasedListings, double coefficient, bool empty)
            {
                double Mean(double sum, long count) => count > 0 ? sum / count : 0;
                NumberOfListingsBelow0PercentGain = (int)a.BucketCounts[0];
                NumberOfListingsBetween0And5PercentGain = (int)a.BucketCounts[1];
                NumberOfListingsBetween5And10PercentGain = (int)a.BucketCounts[2];
                NumberOfListingsBetween10And20PercentGain = (int)a.BucketCounts[3];
                NumberOfListingsAbove20PercentGain = (int)a.BucketCounts[4];
                MeanPriceOfListingsBelow0PercentGain = Mean(a.BucketSteamPriceSums[0], a.BucketCounts[0]);
                MeanPriceOfListingsBetween0And5PercentGain = Mean(a.BucketSteamPriceSums[1], a.BucketCounts[1]);
                MeanPriceOfListingsBetween5And10PercentGain = Mean(a.BucketSteamPriceSums[2], a.BucketCounts[2]);
                MeanPriceOfListingsBetween10And20PercentGain = Mean(a.BucketSteamPriceSums[3], a.BucketCounts[3]);
                MeanPriceOfListingsAbove20PercentGain = Mean(a.BucketSteamPriceSums[4], a.BucketCounts[4]);
                TotalListingsScanned = (int)Math.Min(int.MaxValue, a.TotalEvaluated);

                if (empty)
                {
                    HighestPredictedGainFoundSoFar = 0;
                    NameOfItemWithHighestPredictedGain = "None";
                    FirstListingDateRecorded = default;
                }
                else
                {
                    HighestPredictedGainFoundSoFar = a.HighestRoi is double best ? 1 + best : 0;
                    NameOfItemWithHighestPredictedGain = a.HighestRoiItem;
                    FirstListingDateRecorded = a.StartedUtc;
                }
                CountListingsWithPositiveGain = (int)Math.Min(int.MaxValue, a.PositiveCount);
                CountListingsWithNegativeGain = (int)Math.Min(int.MaxValue, a.NegativeCount);
                PercentageChanceOfFindingPositiveGainListing = a.TotalEvaluated > 0 ? a.PositiveCount * 100.0 / a.TotalEvaluated : 0;
                MeanFloatValueOfProfitableListings = Mean(a.ProfitableFloatSum, a.ProfitableFloatCount);
                MeanPriceOfProfitableListings = Mean(a.ProfitablePriceSum, a.PositiveCount);
                MeanGainOfProfitableListings = Mean(a.ProfitableGainSum, a.PositiveCount);
                MeanFloatValueOfUnprofitableListings = Mean(a.UnprofitableFloatSum, a.UnprofitableFloatCount);
                MeanPriceOfUnprofitableListings = Mean(a.UnprofitablePriceSum, a.NegativeCount);
                TotalExpectedProfitPercent = (float)((Math.Exp(Math.Min(50, a.QualifiedLogReturnSum)) - 1) * 100);
                AnalyticsGeneratedAt = DateTime.Now;
                AllPurchasedItems = purchasedListings;
                CurrentExpectedReturnCoefficientOfSteamToCSFloat = coefficient;
                QualifiedOpportunities = a.QualifiedOpportunities;
                PurchaseAttempts = a.PurchaseAttempts;
                Purchases = a.Purchases;
                Daily = a.Daily;
                TimeTakenToPurchaseAllPurchasedItems = purchasedListings
                    .Where(p => p.comparison != null && p.comparison.CSFloatListing.DateTimeListingCreated != default && p.TimeOfPurchase != default)
                    .Select(p => p.TimeOfPurchase.ToUniversalTime() - p.comparison.CSFloatListing.DateTimeListingCreated.ToUniversalTime())
                    .ToList();
            }
        }
    }
}
