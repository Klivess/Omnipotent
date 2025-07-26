using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.ComponentModel;
using System.Reflection.Metadata.Ecma335;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CS2LiquidityFinder
    {
        public CS2ArbitrageBot parent;
        public CS2LiquidityFinder(CS2ArbitrageBot parent)
        {
            this.parent = parent;
        }

        public enum ContainerType
        {
            WeaponCase,
            StickerCapsule,
            AutographCapsule,
        }

        public struct Container
        {
            public string MarketHashName;
            public int PriceInCents;
            public int PriceInPence;
            public double PriceInPounds;
            public string ImageURL;
            public ContainerType containerType;
        }

        public struct ContainerGap
        {
            public Container csfloatContainer;
            public SteamAPIWrapper.ItemListing steamListing;
            public double ReturnCoefficientFromSteamtoCSFloat;
            public double ReturnCoefficientFromSteamToCSFloatTaxIncluded;

            public List<SteamPriceHistoryDataPoint> priceHistory;

            public int IdealCSFloatSellPriceInCents;
            public double IdealCSFloatSellPriceInPounds;
            public int IdealCSFloatSellPriceInPence;

            public float IdealPriceToPurchaseOnSteamInPounds;
            public double IdealReturnCoefficientFromSteamtoCSFloat;
            public double IdealReturnCoefficientFromSteamToCSFloatTaxIncluded;
        }

        public async Task<List<Container>> GetAllWeaponCasePricesInPoundsOnCSFloat()
        {
            try
            {
                List<Container> weaponCases = new List<Container>();
                string url = "https://csfloat.com/api/v1/schema/browse?type=containers";
                var response = await parent.csFloatWrapper.Client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject(content);
                    foreach (dynamic weaponCaseList in json.data)
                    {
                        ContainerType type;
                        if (weaponCaseList.type == "weapon_case")
                        {
                            type = ContainerType.WeaponCase;
                        }
                        else if (weaponCaseList.type == "sticker_capsule")
                        {
                            type = ContainerType.StickerCapsule;
                        }
                        else if (weaponCaseList.type == "autograph_capsule")
                        {
                            type = ContainerType.AutographCapsule;
                        }
                        else
                        {
                            continue;
                        }
                        foreach (dynamic item in weaponCaseList.items)
                        {
                            Container container = new();
                            container.MarketHashName = item.market_hash_name;
                            container.PriceInCents = item.price;
                            container.PriceInPence = Convert.ToInt32(Math.Ceiling(Convert.ToDecimal(container.PriceInCents * parent.ExchangeRate)));
                            container.PriceInPounds = Convert.ToDouble(container.PriceInPence) / 100;
                            container.ImageURL = item.image;
                            container.containerType = type;
                            weaponCases.Add(container);
                        }
                    }
                    return weaponCases;
                }
                else
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(5000);
                        return await GetAllWeaponCasePricesInPoundsOnCSFloat();
                    }
                    else
                    {
                        parent.ServiceLogError($"Failed to fetch weapon cases from CSFloat. Status Code: {response.StatusCode} Content: {response.Content.ReadAsStringAsync().Result}");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, "Error in GetAllWeaponCasePricesInPoundsOnCSFloat");
                return null;
            }
        }

        public async Task<LiquiditySearchResult> CompareCSFloatContainersToSteamContainers()
        {
            var startTime = DateTime.UtcNow;
            try
            {
                List<ContainerGap> gaps = new List<ContainerGap>();
                var getallWeaponCases = await GetAllWeaponCasePricesInPoundsOnCSFloat();
                foreach (var item in getallWeaponCases)
                {
                    ContainerGap gap;
                    SteamAPIWrapper.ItemListing listing = await parent.steamAPIWrapper.GetItemOnMarket(item.MarketHashName);
                    gap.csfloatContainer = item;
                    gap.steamListing = listing;
                    double returnCoefficient = 0;
                    returnCoefficient = Convert.ToDouble(item.PriceInPounds / listing.CheapestSellOrderPriceInPounds);
                    gap.ReturnCoefficientFromSteamtoCSFloat = returnCoefficient;
                    gap.ReturnCoefficientFromSteamToCSFloatTaxIncluded = returnCoefficient / 1.02;

                    //Linear regression has shown this to be the best fit for the CSFloat price prediction
                    //                              y=1.09215x+0.000318599
                    gap.IdealCSFloatSellPriceInCents = Convert.ToInt32(((1.09215) * item.PriceInCents) + 0.000318599);
                    gap.IdealCSFloatSellPriceInPence = Convert.ToInt32(Math.Ceiling(Convert.ToDecimal(gap.IdealCSFloatSellPriceInCents * parent.ExchangeRate)));
                    gap.IdealCSFloatSellPriceInPounds = Convert.ToDouble(gap.IdealCSFloatSellPriceInPence) / 100;

                    gap.priceHistory = await GetPriceHistoryOfSteamItem(item.MarketHashName);
                    gap.IdealPriceToPurchaseOnSteamInPounds = FindIdealPriceToPlaceBuyOrder(gap.priceHistory);
                    gap.IdealReturnCoefficientFromSteamtoCSFloat = Convert.ToDouble(gap.csfloatContainer.PriceInPounds / gap.IdealPriceToPurchaseOnSteamInPounds);
                    gap.IdealReturnCoefficientFromSteamToCSFloatTaxIncluded = gap.IdealReturnCoefficientFromSteamtoCSFloat / 1.02;

                    gaps.Add(gap);
                }

                // Analytics
                var result = new LiquiditySearchResult();
                result.LiquiditySearchID = RandomGeneration.GenerateRandomLengthOfNumbers(10);
                result.AllGapsFound = gaps;
                result.DateOfSearch = DateTime.UtcNow;
                result.TotalContainersAnalyzed = gaps.Count;
                result.TimeToCompleteSearch = DateTime.UtcNow - startTime;

                if (gaps.Count > 0)
                {
                    result.HighestReturnCoefficientFound = gaps.OrderByDescending(g => g.ReturnCoefficientFromSteamtoCSFloat).First();
                    result.WorstReturnCoefficientFound = gaps.OrderBy(g => g.ReturnCoefficientFromSteamtoCSFloat).First();
                    result.Top5ReturnCoefficients = gaps.OrderByDescending(g => g.ReturnCoefficientFromSteamtoCSFloat).Take(5).ToList();

                    var returnCoefficients = gaps.Select(g => g.ReturnCoefficientFromSteamtoCSFloat).ToList();
                    result.AverageReturnCoefficient = returnCoefficients.Average();

                    var sortedCoefficients = returnCoefficients.OrderBy(x => x).ToList();
                    int mid = sortedCoefficients.Count / 2;
                    result.MedianReturnCoefficient = sortedCoefficients.Count % 2 == 0
                        ? (sortedCoefficients[mid - 1] + sortedCoefficients[mid]) / 2.0
                        : sortedCoefficients[mid];

                    double avg = result.AverageReturnCoefficient;
                    result.StandardDeviationReturnCoefficient = Math.Sqrt(returnCoefficients.Sum(x => Math.Pow(x - avg, 2)) / returnCoefficients.Count);

                    double threshold = 1.1; // Example threshold, adjust as needed
                    result.CountAboveThreshold = returnCoefficients.Count(x => x > threshold);

                    // Average price history volatility: mean of standard deviation of price history for each gap
                    var volatilities = gaps
                        .Where(g => g.priceHistory != null && g.priceHistory.Count > 1)
                        .Select(g =>
                        {
                            var prices = g.priceHistory.Select(p => p.PriceInPounds).ToList();
                            double mean = prices.Average();
                            return Math.Sqrt(prices.Sum(p => Math.Pow(p - mean, 2)) / prices.Count);
                        }).ToList();
                    result.AveragePriceHistoryVolatility = volatilities.Count > 0 ? volatilities.Average() : 0.0;
                }
                else
                {
                    result.HighestReturnCoefficientFound = default;
                    result.WorstReturnCoefficientFound = default;
                    result.Top5ReturnCoefficients = new List<ContainerGap>();
                    result.AverageReturnCoefficient = 0;
                    result.MedianReturnCoefficient = 0;
                    result.StandardDeviationReturnCoefficient = 0;
                    result.CountAboveThreshold = 0;
                    result.AveragePriceHistoryVolatility = 0;
                }

                return result;
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, "Error in CompareCSFloatContainersToSteamContainers");
                return default;
            }
        }

        public struct SteamPriceHistoryDataPoint
        {
            public DateTime DateTimeRecorded;
            public double PriceInPounds;
            public double PriceInPence => Convert.ToDouble(Math.Ceiling(PriceInPounds * 100));
            public int QuantitySold;
        }

        public async Task<List<SteamPriceHistoryDataPoint>?> GetPriceHistoryOfSteamItem(string marketHashName)
        {
            try
            {
                string url = "https://steamcommunity.com/market/listings/730/" + marketHashName;
                var options = new OpenQA.Selenium.Chrome.ChromeOptions();
                options.AddArgument("--headless");
                using var driver = new OpenQA.Selenium.Chrome.ChromeDriver(options);

                driver.Navigate().GoToUrl(url);

                // Wait for the page to load and execute the JavaScript  
                var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(10));
                wait.Until(d => ((OpenQA.Selenium.IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

                // Execute JavaScript to retrieve price history data  
                string jsonData = null;
                try
                {
                    jsonData = (string)((OpenQA.Selenium.IJavaScriptExecutor)driver).ExecuteScript("return JSON.stringify(g_plotPriceHistory._stackData);");
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError(ex, $"Error executing JavaScript for item: {marketHashName}, assuming ratelimit, so waiting 25 seconds,");
                    await Task.Delay(25000); // Wait for 25 seconds before retrying
                    return await GetPriceHistoryOfSteamItem(marketHashName); // Retry the operation
                }

                // Parse the JSON data  
                dynamic priceHistory = Newtonsoft.Json.JsonConvert.DeserializeObject(jsonData);
                List<SteamPriceHistoryDataPoint> historyDataPoints = new List<SteamPriceHistoryDataPoint>();

                foreach (var dataPoint in priceHistory[0])
                {
                    string epoch = dataPoint[0].ToString();
                    SteamPriceHistoryDataPoint point = new SteamPriceHistoryDataPoint
                    {
                        DateTimeRecorded = OmniPaths.EpochMsToDateTime(epoch),
                        PriceInPounds = Convert.ToDouble(dataPoint[1]) * parent.ExchangeRate,
                        QuantitySold = Convert.ToInt32(dataPoint[2])
                    };
                    historyDataPoints.Add(point);
                }

                return historyDataPoints;
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, $"Error fetching price history for item: {marketHashName}");
                return null;
            }
        }

        //Price of steam items tend to oscillate. This function finds the points of minimum price in past price history data.
        //window param: The number of points to consider on each side of the current point to determine if it is a local minimum.
        //minProminence param: The minimum difference between the current point and the highest point on either side to consider it a local minimum.
        public static IEnumerable<SteamPriceHistoryDataPoint> GetPriceBottoms(
        IEnumerable<SteamPriceHistoryDataPoint> rawPoints,
        int window = 3,
        double minProminence = 0.0)
        {
            if (rawPoints == null) yield break;

            var points = rawPoints
                .OrderBy(p => p.DateTimeRecorded)
                .ToList();

            if (points.Count < window * 2 + 1) yield break;
            if (window < 1) window = 1;

            for (int i = window; i < points.Count - window; i++)
            {
                double cur = points[i].PriceInPounds;

                double leftMin = double.MaxValue, rightMin = double.MaxValue;
                double leftMax = double.MinValue, rightMax = double.MinValue;

                for (int j = i - window; j < i; j++)
                {
                    double v = points[j].PriceInPounds;
                    if (v < leftMin) leftMin = v;
                    if (v > leftMax) leftMax = v;
                }
                for (int j = i + 1; j <= i + window; j++)
                {
                    double v = points[j].PriceInPounds;
                    if (v < rightMin) rightMin = v;
                    if (v > rightMax) rightMax = v;
                }

                bool isLocalMin =
                    cur <= leftMin && cur <= rightMin &&
                    (points[i - 1].PriceInPounds > cur || points[i + 1].PriceInPounds > cur);

                if (!isLocalMin) continue;

                double prominence = Math.Min(leftMax - cur, rightMax - cur);
                if (prominence + 1e-12 < minProminence) continue;

                yield return points[i];
            }
        }

        public static float FindIdealPriceToPlaceBuyOrder(List<SteamPriceHistoryDataPoint> dataPoints)
        {
            var minimas = CS2LiquidityFinder.GetPriceBottoms(dataPoints, 9, 0).ToList();

            // Filter minimas to only include points from the last week  
            DateTime oneWeekAgo = DateTime.Now.AddDays(-7);
            var recentMinimas = minimas.Where(m => m.DateTimeRecorded >= oneWeekAgo).ToList();

            // Get the minimum price from the filtered minimas  
            if (recentMinimas.Count == 0)
            {
                throw new InvalidOperationException("No minimas found in the last week.");
            }
            float minimumPriceInLastWeek = (float)recentMinimas.Min(m => m.PriceInPounds);

            return minimumPriceInLastWeek * 1.01f;
        }



        public class LiquiditySearchResult
        {
            public List<ContainerGap> AllGapsFound;
            public DateTime DateOfSearch;
            public ContainerGap HighestReturnCoefficientFound;
            public string LiquiditySearchID;

            // Analytics
            public double AverageReturnCoefficient;
            public double MedianReturnCoefficient;
            public double StandardDeviationReturnCoefficient;
            public int TotalContainersAnalyzed;
            public int CountAboveThreshold;
            public ContainerGap WorstReturnCoefficientFound;
            public TimeSpan TimeToCompleteSearch;
            public List<ContainerGap> Top5ReturnCoefficients;
            public double AveragePriceHistoryVolatility;
        }
    }
}
