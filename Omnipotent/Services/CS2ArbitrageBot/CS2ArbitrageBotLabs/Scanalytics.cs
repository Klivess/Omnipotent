using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
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
        public double expectedSteamToCSFloatConversionPercentage = 0.84;

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

            public ScannedComparisonAnalytics(List<ScannedComparison> data)
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
                foreach (var item in comparisons.Where(c => c.PredictedOverallArbitrageGain > 1))
                {
                    bal = bal * (float)item.PredictedOverallArbitrageGain;
                }
                TotalExpectedProfitPercent = ((bal / 100) - 1) * 100;
                FirstListingDateRecorded = comparisons.Min(c => c.LastUpdate);
                AnalyticsGeneratedAt = DateTime.Now;
            }
        }
    }
}
