using Markdig.Renderers.Html;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using OpenQA.Selenium.DevTools.V136.CSS;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.Intrinsics.X86;
using System.Text;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.CS2LiquidityFinder;

namespace Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs
{
    public class Scanalytics
    {
        public List<ScannedComparison> AllScannedComparisonsInHistory;
        public List<PurchasedListing> AllPurchasedListingsInHistory;
        public List<ScanResults> AllScanResultsInHistory;
        private CS2ArbitrageBot parent;
        public Scanalytics(CS2ArbitrageBot parent)
        {
            this.parent = parent;
            AllScannedComparisonsInHistory = new List<ScannedComparison>();
            AllPurchasedListingsInHistory = new();
            AllScanResultsInHistory = new List<ScanResults>();
            SetUpScanalytics();
            LoadScannedComparisons().Wait();
            LoadPurchasedItems().Wait();
            LoadScanResults().Wait();
        }

        private async void SetUpScanalytics()
        {
            parent.serviceManager.timeManager.TaskDue += TimeManager_TaskDue;
            if ((await parent.serviceManager.timeManager.GetTask("RecordCSFloatAndSteamBalance")) == null)
            {
                RecordAccountInfoAsync();
            }
        }

        public async Task<List<CSFloatAndSteamBalance>> GetAllLogsOfCSFloatAndSteamBalance()
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDailyAccountInfoDirectory);
            List<CSFloatAndSteamBalance> balances = new();
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        string content = await parent.GetDataHandler().ReadDataFromFile(file, true);
                        CSFloatAndSteamBalance balance = JsonConvert.DeserializeObject<CSFloatAndSteamBalance>(content);
                        balances.Add(balance);
                    }
                    catch (Exception e)
                    {
                        parent.ServiceLogError(e, "Error loading CSFloat and Steam balance log.");
                    }
                }
            }
            return balances;
        }

        private async Task RecordAccountInfoAsync()
        {
            try
            {
                CSFloatAndSteamBalance info = new();
                parent.csfloatAccountInformation = await parent.csFloatWrapper.GetAccountInformation();
                info.CSFloatUsableBalanceInPounds = Convert.ToDouble(parent.csfloatAccountInformation.BalanceInPounds);
                info.CSFloatPendingBalanceInPounds = Convert.ToDouble(parent.csfloatAccountInformation.PendingBalanceInPounds);
                info.CSFloatTotalBalanceInPounds = Convert.ToDouble(info.CSFloatUsableBalanceInPounds) + Convert.ToDouble(info.CSFloatPendingBalanceInPounds);
                SteamAPIProfileWrapper.SteamBalance steamBal = (await parent.steamAPIWrapper.profileWrapper.GetSteamBalance()).Value;
                info.SteamUsableBalanceInPounds = steamBal.UsableBalanceInPounds;
                info.SteamPendingBalanceInPounds = steamBal.PendingBalanceInPounds;
                info.SteamTotalBalanceInPounds = info.SteamUsableBalanceInPounds + info.SteamPendingBalanceInPounds;
                info.DateTimeOfBalanceRecord = DateTime.Now;
                info.CSFloatProfileStatistics = parent.csfloatAccountInformation.Statistics;
                info.CSFloatFee = parent.csfloatAccountInformation.Fee;
                info.CSFloatWithdrawFee = parent.csfloatAccountInformation.WithdrawFee;

                string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDailyAccountInfoDirectory);
                string filename = $"AccountInfo{DateTime.Now.ToString("yyyy-MM-dd")}.json";
                //Ensure filename's name can actually be saved as a file's name
                filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
                await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(info, Formatting.Indented));
                parent.ServiceLog($"Recorded account info: {info.CSFloatUsableBalanceInPounds} CSFloat, {info.SteamUsableBalanceInPounds} Steam.");

                await parent.ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(12), "RecordCSFloatAndSteamBalance", "CS2ArbitrageLabs", "Record daily balances of CSFloat account and Steam account.", false);
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, "Error recording account info.");
                await parent.ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(12), "RecordCSFloatAndSteamBalance", "CS2ArbitrageLabs", "Record daily balances of CSFloat account and Steam account after an error.", false);
            }
        }

        private void TimeManager_TaskDue(object? sender, Service_Manager.TimeManager.ScheduledTask e)
        {
            if (e.taskName == "RecordCSFloatAndSteamBalance")
            {
                RecordAccountInfoAsync();
            }
        }

        public class CSFloatAndSteamBalance
        {
            public double CSFloatUsableBalanceInPounds { get; set; }
            public double CSFloatTotalBalanceInPounds { get; set; }
            public double CSFloatPendingBalanceInPounds { get; set; }
            public double SteamUsableBalanceInPounds { get; set; }
            public double SteamPendingBalanceInPounds { get; set; }
            public double SteamTotalBalanceInPounds { get; set; }

            public DateTime DateTimeOfBalanceRecord { get; set; }
            public CSFloatWrapper.Statistics CSFloatProfileStatistics { get; set; }
            public double CSFloatFee;
            public double CSFloatWithdrawFee;
        }


        public class LiquidityPlan
        {
            public DateTime ProductionDateOfLiquiditySearchResultUsed;
            public List<ContainerGap> Top10Gaps = new();
            public Dictionary<string, List<LiquidityPlanBuyTactics>> BuyOrderTacticsAndCorrespondingReturns;
            public Dictionary<string, SteamPriceHistoryDataPoint> OptimalPurchasePointsForEachContainerGap;
            public string LiquidityPlanDescription;

            public struct LiquidityPlanBuyTactics
            {
                public string ItemMarketHashName;
                public double ReturnCoefficient;
                public double PriceNeededToBuyOnSteam;
                public double PriceNeededToSellOnCSFloat;
                public SteamPriceHistoryDataPoint LastTimeSoldAtThisPriceOrBelow;
            }
        }
        public async Task<double> ExpectedSteamToCSFloatConversionPercentage()
        {
            try
            {
                LiquidityPlan liquidityPlan = ProduceLiquidityPlanAsync(await GetLatestLiquiditySearchResult(), 10);
                if (liquidityPlan.Top10Gaps.Count > 0)
                {
                    //Get the gap of the Top 10 Gaps which has the highest quantity sold in the last 5 days
                    var fiveDaysAgo = DateTime.UtcNow.AddDays(-5);
                    var gapWithHighestQuantitySold = liquidityPlan.Top10Gaps
                        .Where(g => g.priceHistory != null)
                        .OrderByDescending(g => g.priceHistory
                            .Where(p => p.DateTimeRecorded >= fiveDaysAgo)
                            .Sum(p => p.QuantitySold))
                        .FirstOrDefault();

                    if (gapWithHighestQuantitySold.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded > 0.85)
                    {
                        return 0.85;
                    }
                    return gapWithHighestQuantitySold.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded;
                }
                else
                {
                    return 0.75;
                }
            }
            catch (Exception e)
            {
                return 0.75;
            }
        }

        public LiquidityPlan ProduceLiquidityPlanAsync(LiquiditySearchResult liquiditySearchResult, double? maxPrice = null)
        {
            LiquidityPlan plan = new();
            try
            {
                plan.ProductionDateOfLiquiditySearchResultUsed = liquiditySearchResult.DateOfSearch;

                // Remove all ContainerGaps in AllGapsFound which have a return coefficient of Infinity  
                List<ContainerGap> filteredGaps = liquiditySearchResult.AllGapsFound
                    .Where(g => g.ReturnCoefficientFromSteamtoCSFloat != double.PositiveInfinity)
                    .ToList();

                // Remove all ContainerGaps with a steam price greater than half of the current steamwallet balance.  
                if (maxPrice == null)
                {
                    if (parent.steamBalance != null)
                    {
                        maxPrice = parent.steamBalance.Value.UsableBalanceInPounds;
                    }
                }
                filteredGaps = filteredGaps
                    .Where(g => g.steamListing.CheapestSellOrderPriceInPounds < (maxPrice))
                    .ToList();

                // Remove all ContainerGaps that do not meet the liquidity requirement of at least 500 items sold in the last 5 days  
                var fiveDaysAgo = DateTime.UtcNow.AddDays(-5);
                filteredGaps = filteredGaps
                   .Where(g => g.priceHistory != null &&
                               g.priceHistory
                                   .Where(p => p.DateTimeRecorded >= fiveDaysAgo)
                                   .Sum(p => p.QuantitySold) >= 500)
                   .ToList();

                // Sort the gaps by Ideal return coefficient  
                filteredGaps = filteredGaps
                    .OrderByDescending(g => g.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded)
                    .ToList();

                // Take the top 10 and set it to plan.Top10Gaps  
                plan.Top10Gaps = filteredGaps.Take(10).ToList();

                // Create a dictionary of the price needed to buy for 90% profit  
                plan.BuyOrderTacticsAndCorrespondingReturns = new();
                foreach (var gap in plan.Top10Gaps)
                {
                    List<LiquidityPlan.LiquidityPlanBuyTactics> tacticsList = new();
                    for (int i = 84; i < 100; i++)
                    {
                        LiquidityPlan.LiquidityPlanBuyTactics tactic = new();
                        double returnCoeff = i / 100.0;
                        double priceNeededToBuyOnSteam = Convert.ToDouble((gap.csfloatContainer.PriceInPounds / 1.02) / returnCoeff);
                        tactic.ReturnCoefficient = returnCoeff;
                        tactic.ItemMarketHashName = gap.csfloatContainer.MarketHashName;
                        tactic.PriceNeededToBuyOnSteam = priceNeededToBuyOnSteam;
                        tactic.PriceNeededToSellOnCSFloat = gap.csfloatContainer.PriceInPounds;

                        // Look for the latest (datetime wise) time that gap.priceHistory has a price below to priceNeededToBuyOnSteam  
                        var matchingPricePoint = gap.priceHistory != null
                            ? gap.priceHistory
                                .Where(p => p.PriceInPounds <= priceNeededToBuyOnSteam)
                                .OrderByDescending(p => p.DateTimeRecorded)
                                .FirstOrDefault()
                            : default;

                        tactic.LastTimeSoldAtThisPriceOrBelow = matchingPricePoint;
                        tacticsList.Add(tactic);
                    }
                    plan.BuyOrderTacticsAndCorrespondingReturns.Add(gap.csfloatContainer.MarketHashName, tacticsList);
                }

                plan.OptimalPurchasePointsForEachContainerGap = GetOptimalPurchasePoints(plan);

                // Build description string  
                var sb = new StringBuilder();
                foreach (var kvp in plan.OptimalPurchasePointsForEachContainerGap)
                {
                    string name = kvp.Key;
                    var dp = kvp.Value;
                    var tactic = plan.BuyOrderTacticsAndCorrespondingReturns[name]
                        .FirstOrDefault(t => t.LastTimeSoldAtThisPriceOrBelow.DateTimeRecorded == dp.DateTimeRecorded
                                          && Math.Abs(t.LastTimeSoldAtThisPriceOrBelow.PriceInPence - dp.PriceInPence) < 0.01);

                    if (tactic.LastTimeSoldAtThisPriceOrBelow.DateTimeRecorded != default)
                    {
                        sb.AppendLine(
                            $"Item: {name} — Buy at £{tactic.PriceNeededToBuyOnSteam:F2} for a return of {tactic.ReturnCoefficient:P0} " +
                            $"(last seen {dp.DateTimeRecorded:yyyy-MM-dd}).");
                    }
                }
                plan.LiquidityPlanDescription = sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                // Log the error and rethrow it for higher-level handling  
                parent.ServiceLogError(ex, $"Error in ProduceLiquidityPlan");
                throw;
            }

            //Omit everything but the last 2 weeks of pricehistory for each containergap in Top10Gaps
            var twoWeeksAgo = DateTime.UtcNow.AddDays(-14);
            var updatedGaps = new List<ContainerGap>();
            foreach (var gap in plan.Top10Gaps)
            {
                var updatedGap = gap; // Create a copy of the gap object  
                updatedGap.priceHistory = updatedGap.priceHistory != null
                    ? updatedGap.priceHistory
                        .Where(p => p.DateTimeRecorded >= twoWeeksAgo)
                        .ToList()
                    : new List<SteamPriceHistoryDataPoint>();
                updatedGaps.Add(updatedGap);
            }
            plan.Top10Gaps = updatedGaps; // Replace the original list with the updated list  

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
                        var last = t.LastTimeSoldAtThisPriceOrBelow.DateTimeRecorded;
                        var daysSince = (now - last).TotalDays;
                        // score = (return × quantity) penalized by age
                        double score = (t.ReturnCoefficient * t.LastTimeSoldAtThisPriceOrBelow.QuantitySold)
                                       / (1.0 + daysSince);
                        return new { DataPoint = t.LastTimeSoldAtThisPriceOrBelow, Score = score, Age = daysSince };
                    })
                    .ToList();

                // Prefer those seen within the last 7 days
                var recent = scored.Where(x => x.Age <= 7).ToList();
                var candidates = recent.Any() ? recent : scored;

                if (candidates.Any())
                {
                    var best = candidates.OrderByDescending(x => x.Score).First().DataPoint;
                    optimalPoints[kvp.Key] = best;
                }
            }

            return optimalPoints;
        }
        public enum StrategicStages
        {
            WaitingForCSFloatSellerToAcceptSale,
            WaitingForCSFloatTradeToBeSent,
            WaitingForCSFloatTradeToBeAccepted,
            JustRetrieved,
            WaitingForMarketSaleOnSteam,
            WaitingForConversionItemsToPurchase,
            WaitingForConversionItemsToSell,
            StrategyCompleted
        }
        public class PurchasedListing
        {
            public ScannedComparison comparison;
            public string CSFloatListingID;
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

            public string CSFloatToSteamTradeOfferLink;

            public float ItemFloatValue;
            public string ItemMarketHashName;

            public float ActualSalePriceOnSteam;

            public StrategicStages CurrentStrategicStage;
        }
        public class ScannedComparison
        {
            public string ItemMarketHashName;
            public string PriceTextCSFloat;
            public string PriceTextSteamMarket;
            public double RawArbitrageGain;
            public double ArbitrageGainAfterSteamTax;
            public double PredictedOverallArbitrageGain;
            public string CSFloatURL;
            public string SteamListingURL;
            public CSFloatWrapper.ItemListing CSFloatListing;
            public SteamAPIWrapper.ItemListing SteamListing;
            public DateTime LastUpdate;

            public ScannedComparison(CSFloatWrapper.ItemListing csfloatListing, SteamAPIWrapper.ItemListing steamListing, DateTime lastUpdate, double expectedConversionCoeff)
            {
                ItemMarketHashName = csfloatListing.ItemMarketHashName;
                PriceTextCSFloat = csfloatListing.PriceText;
                PriceTextSteamMarket = steamListing.PriceText;


                double percentageDifference = Convert.ToDouble((steamListing.HighestBuyOrderPriceInPounds / csfloatListing.PriceInPounds));
                double gainAfterSteamTax = (((steamListing.HighestBuyOrderPriceInPounds / 1.15) / csfloatListing.PriceInPounds));
                double predictedOverallGain = (((((steamListing.HighestBuyOrderPriceInPounds / 1.15)) * expectedConversionCoeff) / csfloatListing.PriceInPounds));

                RawArbitrageGain = percentageDifference;
                ArbitrageGainAfterSteamTax = gainAfterSteamTax; // Assuming 15% Steam tax
                PredictedOverallArbitrageGain = predictedOverallGain; // Placeholder for future calculations
                CSFloatURL = csfloatListing.ListingURL;
                SteamListingURL = steamListing.ListingURL;
                CSFloatListing = csfloatListing;
                SteamListing = steamListing;
                LastUpdate = lastUpdate;
            }
        }
        public enum ScanStrategy
        {
            SearchingThroughCSFloatHighestDiscount,
            SearchingThroughCSFloatNewest,

        }
        public class ScanResults
        {
            public string ScanID;
            public List<ScanStrategyResult> ScanStrategyResults;
            public ScannedComparisonAnalytics Analytics;
            public ScanResults()
            {
                ScanID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                ScanStrategyResults = new List<ScanStrategyResult>();
            }

            public void ProduceOverallAnalytics(double ExpectedSteamToCSFloatConversionPercentage)
            {
                List<ScannedComparison> totalComparisons = new();
                List<PurchasedListing> purchasedListings = new();

                foreach (var strategyResult in ScanStrategyResults)
                {
                    totalComparisons.AddRange(strategyResult.ScannedComparisons);
                    purchasedListings.AddRange(strategyResult.PurchasedListings);
                }
                //remove all duplicates, where duplicates are defined as having the same CSFloatListing.ItemListingID
                totalComparisons = totalComparisons.GroupBy(c => c.CSFloatListing.ItemListingID).Select(g => g.First()).ToList();
                purchasedListings = purchasedListings.GroupBy(c => c.CSFloatListingID).Select(g => g.First()).ToList();
                Analytics = new ScannedComparisonAnalytics(totalComparisons, purchasedListings, ExpectedSteamToCSFloatConversionPercentage);
            }
        }
        public class ScanStrategyResult
        {
            public string ParentScanID;
            public string ScanStrategyResultID;
            public ScanStrategy StrategyUsed;
            public string StrategyUsedString;
            public List<ScannedComparison> ScannedComparisons;
            public List<PurchasedListing> PurchasedListings;
            public ScannedComparisonAnalytics Analytics;
            public int DuplicateListingsFound;
            public int ErrorsOccurred;

            public ScanStrategyResult()
            {
                ScanStrategyResultID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                ScannedComparisons = new List<ScannedComparison>();
                PurchasedListings = new List<PurchasedListing>();
            }

            public void ProduceAnalytics(double ExpectedSteamToCSFloatConversionPercentage)
            {
                Analytics = new ScannedComparisonAnalytics(ScannedComparisons, PurchasedListings, ExpectedSteamToCSFloatConversionPercentage);
            }
        }
        public async Task SaveScannedComparison(ScannedComparison scannedComparison)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotScannedComparisonsDirectory);
            string filename = scannedComparison.ItemMarketHashName + scannedComparison.CSFloatListing.ItemListingID.ToString() + "id.json";
            //Ensure filename's name can actually be saved as a file's name
            //Try saying that 3 times lol
            filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(scannedComparison, Formatting.Indented));
        }
        public async Task LoadScannedComparisons()
        {
            AllScannedComparisonsInHistory = new List<ScannedComparison>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotScannedComparisonsDirectory);
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        string content = await parent.GetDataHandler().ReadDataFromFile(file, true);
                        ScannedComparison comparison = JsonConvert.DeserializeObject<ScannedComparison>(content);
                        AllScannedComparisonsInHistory.Add(comparison);
                    }
                    catch (Exception e) { }
                }
            }
        }
        public async Task LoadPurchasedItems()
        {
            AllPurchasedListingsInHistory = new List<PurchasedListing>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotPurchasedItemsDirectory);
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        string content = await parent.GetDataHandler().ReadDataFromFile(file, true);
                        PurchasedListing comparison = JsonConvert.DeserializeObject<PurchasedListing>(content);
                        AllPurchasedListingsInHistory.Add(comparison);
                    }
                    catch (Exception e) { }
                }
            }
        }
        public async Task SavePurchasedListing(PurchasedListing purchasedListing)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotPurchasedItemsDirectory);
            string filename = purchasedListing.ItemMarketHashName + purchasedListing.comparison.CSFloatListing.ItemListingID.ToString() + "id.json";
            //Ensure filename's name can actually be saved as a file's name
            //Try saying that 3 times lol
            filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(purchasedListing, Formatting.Indented));
        }
        public async Task UpdatePurchasedListing(PurchasedListing purchasedListing)
        {
            if (AllPurchasedListingsInHistory.Where(k => k.CSFloatListingID == purchasedListing.CSFloatListingID).Any())
            {
                //if it already exists
                //replace it
                AllPurchasedListingsInHistory.RemoveAll(k => k.CSFloatListingID == purchasedListing.CSFloatListingID);
                AllPurchasedListingsInHistory.Add(purchasedListing);
            }
            else
            {
                AllPurchasedListingsInHistory.Add(purchasedListing);
            }
            await SavePurchasedListing(purchasedListing);
        }
        public async Task SaveScanResult(ScanResults scanResult)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotScanResultsDirectory);
            string filename = $"ScanResult{scanResult.ScanID}id.json";
            //Ensure filename's name can actually be saved as a file's name
            //Try saying that 3 times lol
            filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(scanResult, Formatting.Indented));
        }
        public async Task UpdateScanResult(ScanResults scanResult)
        {
            if (AllScanResultsInHistory.Where(k => k.ScanID == scanResult.ScanID).Any())
            {
                //if it already exists
                //replace it
                AllScanResultsInHistory.RemoveAll(k => k.ScanID == scanResult.ScanID);
                AllScanResultsInHistory.Add(scanResult);
            }
            else
            {
                AllScanResultsInHistory.Add(scanResult);
            }
            await SaveScanResult(scanResult);
        }
        public async Task LoadScanResults()
        {
            AllScanResultsInHistory = new List<ScanResults>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotScanResultsDirectory);
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        string content = await parent.GetDataHandler().ReadDataFromFile(file, true);
                        ScanResults comparison = JsonConvert.DeserializeObject<ScanResults>(content);
                        AllScanResultsInHistory.Add(comparison);
                    }
                    catch (Exception e) { }
                }
            }
        }
        /*
        public async Task LoadLiquiditySearches()
        {
            AllLiquiditySearchesInHistory = new();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotLiquiditySearchesDirectory);
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        string content = await parent.GetDataHandler().ReadDataFromFile(file, true);
                        var comparison = JsonConvert.DeserializeObject<LiquiditySearchResult>(content);
                        AllLiquiditySearchesInHistory.Add(comparison);
                    }
                    catch (Exception e) { }
                }
            }
        }
        */
        public async Task SaveLiquiditySearch(LiquiditySearchResult liquidSearchResult)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotLiquiditySearchesDirectory);
            string filename = $"LiquidSearch{DateTime.Now.ToString("D")}{liquidSearchResult.LiquiditySearchID}id.json";
            //Ensure filename's name can actually be saved as a file's name
            //Try saying that 3 times lol
            filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
            await parent.GetDataHandler().WriteToFile(Path.Combine(path, filename), JsonConvert.SerializeObject(liquidSearchResult, Formatting.Indented));
        }
        /*
        public async Task UpdateLiquiditySearch(LiquiditySearchResult scanResult)
        {
            if (AllLiquiditySearchesInHistory.Where(k => k.LiquiditySearchID == scanResult.LiquiditySearchID).Any())
            {
                //if it already exists
                //replace it
                AllLiquiditySearchesInHistory.RemoveAll(k => k.LiquiditySearchID == scanResult.LiquiditySearchID);
                AllLiquiditySearchesInHistory.Add(scanResult);
            }
            else
            {
                AllLiquiditySearchesInHistory.Add(scanResult);
            }
            await SaveLiquiditySearch(scanResult);
        }
        */
        public async Task<LiquiditySearchResult> GetLatestLiquiditySearchResult()
        {
            DateTime dateTime = DateTime.MinValue;
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotLiquiditySearchesDirectory);
            string[] files = Directory.GetFiles(path);
            string filePath = "";
            foreach (var item in files)
            {
                DateTime fileDate = File.GetCreationTime(item);
                if (fileDate > dateTime)
                {
                    dateTime = fileDate;
                    filePath = item;
                }
            }
            if (filePath != "")
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(filePath, true);
                    var comparison = JsonConvert.DeserializeObject<LiquiditySearchResult>(content);
                    return comparison;
                }
                catch (Exception e)
                {
                    parent.ServiceLogError(e, "Error loading latest liquidity search result.");
                    throw e;
                }
            }
            return null;
        }
        //Scanned Comparisons Analytics
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
            public string NameOfItemWithHighestPredictedGain { get; set; }
            public int CountListingsWithPositiveGain { get; set; }
            public int CountListingsWithNegativeGain { get; set; }
            public double PercentageChanceOfFindingPositiveGainListing { get; set; }
            public double MeanFloatValueOfProfitableListings { get; set; }
            public double MeanPriceOfProfitableListings { get; set; }
            public double MeanPriceOfUnprofitableListings { get; set; }
            public double MeanFloatValueOfUnprofitableListings { get; set; }

            public double MeanGainOfProfitableListings;

            public float TotalExpectedProfitPercent { get; set; } // This will be calculated based on the expected gain and the number of listings

            public DateTime FirstListingDateRecorded { get; set; }

            public DateTime AnalyticsGeneratedAt;

            public List<PurchasedListing> AllPurchasedItems;
            public List<TimeSpan> TimeTakenToPurchaseAllPurchasedItems;

            public double CurrentExpectedReturnCoefficientOfSteamToCSFloat;

            public ScannedComparisonAnalytics(List<ScannedComparison> data, List<PurchasedListing> purchasedListings, double currentExpectedReturnCoefficientOfSteamToCSFloat)
            {
                List<ScannedComparison> comparisons = data;
                List<ScannedComparison> comparisonsBelow0PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain < 1).ToList();
                List<ScannedComparison> comparisonsBetween0and5PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 1 && c.PredictedOverallArbitrageGain < 1.05).ToList();
                List<ScannedComparison> comparisonsBetween5and10PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 1.05 && c.PredictedOverallArbitrageGain < 1.1).ToList();
                List<ScannedComparison> comparisonsBetween10and20PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 1.1 && c.PredictedOverallArbitrageGain < 1.2).ToList();
                List<ScannedComparison> comparisonsAbove20PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 1.2).ToList();
                NumberOfListingsBelow0PercentGain = comparisonsBelow0PercentGain.Count;
                MeanPriceOfListingsBelow0PercentGain = comparisonsBelow0PercentGain.Count > 0 ? comparisonsBelow0PercentGain.Average(c => c.SteamListing.HighestBuyOrderPriceInPounds) : 0;
                NumberOfListingsBetween0And5PercentGain = comparisonsBetween0and5PercentGain.Count;
                MeanPriceOfListingsBetween0And5PercentGain = comparisonsBetween0and5PercentGain.Count > 0 ? comparisonsBetween0and5PercentGain.Average(c => c.SteamListing.HighestBuyOrderPriceInPounds) : 0;
                NumberOfListingsBetween5And10PercentGain = comparisonsBetween5and10PercentGain.Count;
                MeanPriceOfListingsBetween5And10PercentGain = comparisonsBetween5and10PercentGain.Count > 0 ? comparisonsBetween5and10PercentGain.Average(c => c.SteamListing.HighestBuyOrderPriceInPounds) : 0;
                NumberOfListingsBetween10And20PercentGain = comparisonsBetween10and20PercentGain.Count;
                MeanPriceOfListingsBetween10And20PercentGain = comparisonsBetween10and20PercentGain.Count > 0 ? comparisonsBetween10and20PercentGain.Average(c => c.SteamListing.HighestBuyOrderPriceInPounds) : 0;
                NumberOfListingsAbove20PercentGain = comparisonsAbove20PercentGain.Count;
                MeanPriceOfListingsAbove20PercentGain = comparisonsAbove20PercentGain.Count > 0 ? comparisonsAbove20PercentGain.Average(c => c.SteamListing.HighestBuyOrderPriceInPounds) : 0;
                TotalListingsScanned = comparisons.Count;

                if (comparisons.Count > 0)
                {
                    HighestPredictedGainFoundSoFar = comparisons.Max(c => c.PredictedOverallArbitrageGain);
                    NameOfItemWithHighestPredictedGain = comparisons.FirstOrDefault(c => c.PredictedOverallArbitrageGain == HighestPredictedGainFoundSoFar)?.ItemMarketHashName ?? "Unknown";
                    CountListingsWithPositiveGain = comparisons.Count(c => c.PredictedOverallArbitrageGain > 1);
                    CountListingsWithNegativeGain = comparisons.Count(c => c.PredictedOverallArbitrageGain < 1);
                    PercentageChanceOfFindingPositiveGainListing = (double)CountListingsWithPositiveGain / TotalListingsScanned * 100;
                    try
                    {
                        MeanFloatValueOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 1).Average(c => c.CSFloatListing.FloatValue);
                        MeanPriceOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 1).Average(c => c.SteamListing.HighestBuyOrderPriceInPounds);
                        MeanGainOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 1).Average(c => c.PredictedOverallArbitrageGain);
                    }
                    catch (Exception ex)
                    {
                        MeanFloatValueOfProfitableListings = 0;
                        MeanPriceOfProfitableListings = 0;
                        MeanGainOfProfitableListings = 0;
                    }

                    try
                    {
                        MeanPriceOfUnprofitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain < 1).Average(c => c.SteamListing.HighestBuyOrderPriceInPounds);
                        MeanFloatValueOfUnprofitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain < 1).Average(c => c.CSFloatListing.FloatValue);
                    }
                    catch (Exception ex)
                    {
                        MeanPriceOfUnprofitableListings = 0;
                        MeanFloatValueOfUnprofitableListings = 0;
                    }
                }
                else
                {
                    HighestPredictedGainFoundSoFar = 0;
                    NameOfItemWithHighestPredictedGain = "None";
                    CountListingsWithPositiveGain = 0;
                    CountListingsWithNegativeGain = 0;
                    PercentageChanceOfFindingPositiveGainListing = 0;
                    MeanFloatValueOfProfitableListings = 0;
                    MeanPriceOfProfitableListings = 0;
                    MeanPriceOfUnprofitableListings = 0;
                    MeanFloatValueOfUnprofitableListings = 0;
                    MeanGainOfProfitableListings = 0;
                }

                //Calculate total profit
                float bal = 100;
                foreach (var item in comparisons.Where(c => c.PredictedOverallArbitrageGain > 1).Where(c => (c.PredictedOverallArbitrageGain - 1) > CS2ArbitrageBot.MinimumPercentReturnToSnipe))
                {
                    bal = bal * (float)item.PredictedOverallArbitrageGain;
                }
                TotalExpectedProfitPercent = ((bal / 100) - 1) * 100;
                FirstListingDateRecorded = comparisons.Min(c => c.LastUpdate);
                AnalyticsGeneratedAt = DateTime.Now;

                AllPurchasedItems = purchasedListings;

                try
                {
                    CurrentExpectedReturnCoefficientOfSteamToCSFloat = currentExpectedReturnCoefficientOfSteamToCSFloat;
                }
                catch (Exception e) { }

                TimeTakenToPurchaseAllPurchasedItems = new();
                foreach (var item in purchasedListings)
                {
                    try
                    {
                        if (item.comparison.CSFloatListing.DateTimeListingCreated != DateTime.MinValue && item.TimeOfPurchase != DateTime.MinValue)
                        {
                            TimeSpan timeTaken = item.TimeOfPurchase - item.comparison.CSFloatListing.DateTimeListingCreated;
                            TimeTakenToPurchaseAllPurchasedItems.Add(timeTaken);
                        }
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }
        }
    }
}
