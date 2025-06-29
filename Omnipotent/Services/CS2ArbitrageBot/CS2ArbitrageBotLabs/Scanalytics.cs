using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Management.Automation.Language;

namespace Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs
{
    public class Scanalytics
    {
        public List<ScannedComparison> AllScannedComparisonsInHistory;

        public Scanalytics(CS2ArbitrageBot parent)
        {
            this.parent = parent;
            AllScannedComparisonsInHistory = new List<ScannedComparison>();
            LoadScannedComparisons();
        }


        private CS2ArbitrageBot parent;
        public double expectedSteamToCSFloatConversionPercentage = 0.8;

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


                double percentageDifference = Convert.ToDouble((steamListing.PriceInPounds / csfloatListing.PriceInPounds));
                double gainAfterSteamTax = (((steamListing.PriceInPounds / 1.15) / csfloatListing.PriceInPounds));
                double predictedOverallGain = (((((steamListing.PriceInPounds / 1.15)) * 0.8) / csfloatListing.PriceInPounds));

                RawArbitrageGain = percentageDifference;
                ArbitrageGainAfterSteamTax = RawArbitrageGain * 0.85; // Assuming 15% Steam tax
                PredictedOverallArbitrageGain = ArbitrageGainAfterSteamTax; // Placeholder for future calculations
                CSFloatURL = csfloatListing.ListingURL;
                SteamListingURL = steamListing.ListingURL;
                CSFloatListing = csfloatListing;
                SteamListing = steamListing;
                LastUpdate = lastUpdate;
            }
        }

        public async Task SaveScannedComparison(ScannedComparison scannedComparison)
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotScannedComparisonsDirectory);
            await parent.GetDataHandler().WriteToFile(path + scannedComparison.ItemMarketHashName + scannedComparison.CSFloatListing.FloatValue.ToString() + ".json", JsonConvert.SerializeObject(scannedComparison));
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

            public DateTime AnalyticsGeneratedAt;

            public ScannedComparisonAnalytics(List<ScannedComparison> comparisons)
            {
                List<ScannedComparison> comparisonsBelow0PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain < 0).ToList();
                List<ScannedComparison> comparisonsBetween0and5PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 0 && c.PredictedOverallArbitrageGain < 0.05).ToList();
                List<ScannedComparison> comparisonsBetween5and10PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 0.05 && c.PredictedOverallArbitrageGain < 0.1).ToList();
                List<ScannedComparison> comparisonsBetween10and20PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 0.1 && c.PredictedOverallArbitrageGain < 0.2).ToList();
                List<ScannedComparison> comparisonsAbove20PercentGain = comparisons.Where(c => c.PredictedOverallArbitrageGain >= 0.2).ToList();
                NumberOfListingsBelow0PercentGain = comparisonsBelow0PercentGain.Count;
                MeanPriceOfListingsBelow0PercentGain = comparisonsBelow0PercentGain.Count > 0 ? comparisonsBelow0PercentGain.Average(c => c.SteamListing.PriceInPounds) : 0;
                NumberOfListingsBetween0And5PercentGain = comparisonsBetween0and5PercentGain.Count;
                MeanPriceOfListingsBetween0And5PercentGain = comparisonsBetween0and5PercentGain.Count > 0 ? comparisonsBetween0and5PercentGain.Average(c => c.SteamListing.PriceInPounds) : 0;
                NumberOfListingsBetween5And10PercentGain = comparisonsBetween5and10PercentGain.Count;
                MeanPriceOfListingsBetween5And10PercentGain = comparisonsBetween5and10PercentGain.Count > 0 ? comparisonsBetween5and10PercentGain.Average(c => c.SteamListing.PriceInPounds) : 0;
                NumberOfListingsBetween10And20PercentGain = comparisonsBetween10and20PercentGain.Count;
                MeanPriceOfListingsBetween10And20PercentGain = comparisonsBetween10and20PercentGain.Count > 0 ? comparisonsBetween10and20PercentGain.Average(c => c.SteamListing.PriceInPounds) : 0;
                NumberOfListingsAbove20PercentGain = comparisonsAbove20PercentGain.Count;
                MeanPriceOfListingsAbove20PercentGain = comparisonsAbove20PercentGain.Count > 0 ? comparisonsAbove20PercentGain.Average(c => c.SteamListing.PriceInPounds) : 0;
                TotalListingsScanned = comparisons.Count;

                if (comparisons.Count > 0)
                {
                    HighestPredictedGainFoundSoFar = comparisons.Max(c => c.PredictedOverallArbitrageGain);
                    NameOfItemWithHighestPredictedGain = comparisons.FirstOrDefault(c => c.PredictedOverallArbitrageGain == HighestPredictedGainFoundSoFar)?.ItemMarketHashName ?? "Unknown";
                    CountListingsWithPositiveGain = comparisons.Count(c => c.PredictedOverallArbitrageGain > 0);
                    CountListingsWithNegativeGain = comparisons.Count(c => c.PredictedOverallArbitrageGain < 0);
                    PercentageChanceOfFindingPositiveGainListing = (double)CountListingsWithPositiveGain / TotalListingsScanned * 100;
                    MeanFloatValueOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 0).Average(c => c.CSFloatListing.FloatValue);
                    MeanPriceOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 0).Average(c => c.SteamListing.PriceInPounds);
                    MeanPriceOfUnprofitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain < 0).Average(c => c.SteamListing.PriceInPounds);
                    MeanFloatValueOfUnprofitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain < 0).Average(c => c.CSFloatListing.FloatValue);
                    MeanGainOfProfitableListings = comparisons.Where(c => c.PredictedOverallArbitrageGain > 0).Average(c => c.PredictedOverallArbitrageGain);
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
            }
        }
    }
}
