using Markdig.Renderers.Html;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs
{
    public class Scanalytics
    {
        public List<ScannedComparison> AllScannedComparisonsInHistory;
        public List<PurchasedListing> AllPurchasedListingsInHistory;
        public List<ScanResults> AllScanResultsInHistory;
        public Scanalytics(CS2ArbitrageBot parent)
        {
            this.parent = parent;
            AllScannedComparisonsInHistory = new List<ScannedComparison>();
            AllPurchasedListingsInHistory = new();
            AllScanResultsInHistory = new List<ScanResults>();
            LoadScannedComparisons().Wait();
            LoadPurchasedItems().Wait();
            LoadScanResults().Wait();
        }


        private CS2ArbitrageBot parent;
        public double expectedSteamToCSFloatConversionPercentage = 0.84;

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

            public ScannedComparison(CSFloatWrapper.ItemListing csfloatListing, SteamAPIWrapper.ItemListing steamListing, DateTime lastUpdate)
            {
                ItemMarketHashName = csfloatListing.ItemMarketHashName;
                PriceTextCSFloat = csfloatListing.PriceText;
                PriceTextSteamMarket = steamListing.PriceText;


                double percentageDifference = Convert.ToDouble((steamListing.HighestBuyOrderPriceInPounds / csfloatListing.PriceInPounds));
                double gainAfterSteamTax = (((steamListing.HighestBuyOrderPriceInPounds / 1.15) / csfloatListing.PriceInPounds));
                double predictedOverallGain = (((((steamListing.HighestBuyOrderPriceInPounds / 1.15)) * 0.8) / csfloatListing.PriceInPounds));

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

            public void ProduceOverallAnalytics()
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
                Analytics = new ScannedComparisonAnalytics(totalComparisons, purchasedListings);
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

            public void ProduceAnalytics()
            {
                Analytics = new ScannedComparisonAnalytics(ScannedComparisons, PurchasedListings);
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

            public ScannedComparisonAnalytics(List<ScannedComparison> data, List<PurchasedListing> purchasedListings)
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
