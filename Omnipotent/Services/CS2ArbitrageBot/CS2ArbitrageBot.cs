using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;
using Org.BouncyCastle.Asn1.Esf;
using static Omnipotent.Profiles.KMProfileManager;
using System.Net;
using System.Threading.Tasks;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using DSharpPlus.Entities;
using System.Numerics.Tensors;
using Humanizer;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CS2ArbitrageBot : OmniService
    {
        public float ExchangeRate;
        public SteamAPIWrapper steamAPIWrapper;
        public CSFloatWrapper csFloatWrapper;
        public Scanalytics scanalytics;
        private CS2LiquidityFinder liquidityFinder;
        public CSFloatWrapper.CSFloatAccountInformation csfloatAccountInformation;

        public SteamAPIProfileWrapper.SteamBalance? steamBalance;

        public static float MinimumPercentReturnToSnipe = 10;

        public CS2ArbitrageBot()
        {
            name = "CS2ArbitrageBot";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            GetExchangeRate().Wait();
            string csfloatAPIKey = await GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotCSFloatAPIKey));
            if (string.IsNullOrEmpty(csfloatAPIKey))
            {
                await ServiceLogError("CSFloat API Key is not set. Contacting Klives");
                string response = await (await serviceManager.GetNotificationsService()).SendTextPromptToKlivesDiscord("CSFloat API Key is not set for CS2ArbitrageBot. Set it."
                    , "Get the CSFloat API key by going to CSFloat Profile Developer tab", TimeSpan.FromDays(7), "Enter your API key", "API key");
                csfloatAPIKey = response;
                if (string.IsNullOrEmpty(csfloatAPIKey))
                {
                    await ServiceLogError("CSFloat API Key is still not set. Exiting CS2ArbitrageBot service.");
                    await (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("CSFloat API Key is empty.... Exiting CS2ArbitrageBot service.");
                    await TerminateService();
                }
                else
                {
                    await GetDataHandler().WriteToFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotCSFloatAPIKey), csfloatAPIKey);
                    await ServiceLog($"CSFloat API Key set to: {csfloatAPIKey}");
                }
            }
            steamAPIWrapper = new SteamAPIWrapper(this);
            csFloatWrapper = new CSFloatWrapper(this, csfloatAPIKey);
            liquidityFinder = new CS2LiquidityFinder(this);
            scanalytics = new Scanalytics(this);
            await steamAPIWrapper.SteamAPIWrapperInitialisation();
            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;

            CreateRoutes();

            await UpdateAccountInformation();
            MonitorTradeList();
            if (await serviceManager.timeManager.GetTask("SnipeCS2Deals") == null)
            {
                SnipeDealsAndAlertKlives();
            }
            if (await serviceManager.timeManager.GetTask("CompareLiquidItemOptions") == null)
            {
                CompareLiquidItemOptions();
            }
        }

        private async Task CompareLiquidItemOptions()
        {
            if (OmniPaths.CheckIfOnServer() == false)
            {
                ServiceLog("Not on server, skipping liquidity search.");
                ServiceCreateScheduledTask(DateTime.Now.AddDays(1), "CompareLiquidItemOptions", "CS2ArbitrageAnalytics", "Compare price gaps in liquid items to convert Steam Credit to CSFloat Credit", false);
                return;
            }
            try
            {
                ServiceLog("Starting liquidity search for CSFloat containers to Steam containers comparison.");
                var result = await liquidityFinder.CompareCSFloatContainersToSteamContainers();
                if (result == null)
                {
                    ServiceLog("Liquidity search returned null?");
                }
                else
                {
                    await scanalytics.UpdateLiquiditySearch(result);
                    ServiceLog($"Liquidity search completed with {result.HighestReturnCoefficientFound} highest return coefficient found");
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in CompareLiquidItemOptions");
                (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"Error in CompareLiquidItemOptions: {ex.Message}");
            }
            ServiceCreateScheduledTask(DateTime.Now.AddDays(1), "CompareLiquidItemOptions", "CS2ArbitrageAnalytics", "Compare price gaps in liquid items to convert Steam Credit to CSFloat Credit", false);
        }

        private async Task UpdateAccountInformation()
        {
            csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (e.taskName == "SnipeCS2Deals")
            {
                SnipeDealsAndAlertKlives();
            }
            if (e.taskName.Contains("SellCS2ArbitrageListingOnSteam"))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<Scanalytics.PurchasedListing>(Convert.ToString(e.PassableData));
                    SellSkinOnSteam(data);
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Couldn't start selling the skin on steam.");
                }
            }
            if (e.taskName == "CompareLiquidItemOptions")
            {
                CompareLiquidItemOptions();
            }
        }

        private async Task SellSkinOnSteam(Scanalytics.PurchasedListing data)
        {
            ServiceLog($"Item {data.ItemMarketHashName} with float {data.ItemFloatValue} is ready to be sold on the Steam Market");
            (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"Item {data.ItemMarketHashName} with float {data.ItemFloatValue} is ready to be sold on the Steam Market");
            data.CurrentStrategicStage = StrategicStages.WaitingForMarketSaleOnSteam;
            await scanalytics.UpdatePurchasedListing(data);
            //Message Klives
        }
        private async Task GetExchangeRate()
        {
            HttpClient client = new();
            HttpRequestMessage message = new();
            message.RequestUri = new Uri("https://csfloat.com/api/v1/meta/exchange-rates");
            message.Method = HttpMethod.Get;
            var response = client.SendAsync(message).Result;
            dynamic json = JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
            ExchangeRate = json.data.gbp;
            ServiceLog($"Exchange Rate: {ExchangeRate} GBP = 1 USD");
            //Exchange Rate: {ExchangeRate} GBP = 1 USD"
        }
        public async Task MonitorTradeList()
        {
            if (scanalytics.AllPurchasedListingsInHistory.Where(k => k.CurrentStrategicStage < Scanalytics.StrategicStages.JustRetrieved).Any() == false)
            {
                await Task.Delay(1000);
                MonitorTradeList();
            }
            else
            {
                string url = "https://csfloat.com/api/v1/me/trades?limit=1000";
                var response = await csFloatWrapper.Client.GetAsync(url);
                string responseString = await response.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(responseString);
                if (response.IsSuccessStatusCode)
                {
                    //Create copy of listings to monitor
                    foreach (var item in json.trades)
                    {
                        string itemstringed = JsonConvert.SerializeObject(item);
                        string contractID = item.contract_id;
                        //if listingstoMonitorFromTradeList contains a csfloat listings with a listing id equal to the contract id, find it and set its strategic stage
                        Scanalytics.PurchasedListing? listingToMonitor;
                        try
                        {
                            listingToMonitor = scanalytics.AllPurchasedListingsInHistory.Where(k => k.CurrentStrategicStage < Scanalytics.StrategicStages.JustRetrieved).First(k => k.CSFloatListingID == contractID);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (listingToMonitor != null)
                        {
                            try
                            {
                                if (listingToMonitor.CurrentStrategicStage == Scanalytics.StrategicStages.WaitingForCSFloatSellerToAcceptSale)
                                {
                                    string acceptedAt;
                                    try
                                    {
                                        acceptedAt = Convert.ToString(item.accepted_at);
                                    }
                                    catch (Exception)
                                    {
                                        continue;
                                    }
                                    if (!string.IsNullOrEmpty(acceptedAt))
                                    {
                                        listingToMonitor.TimeOfSellerToAcceptSale = DateTime.Parse(acceptedAt);
                                        //Tell Klives that this listing has been accepted
                                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"CSFloat listing {listingToMonitor.ItemMarketHashName} " +
                                            $"with price {listingToMonitor.comparison.CSFloatListing.PriceText} has been accepted by the seller.");
                                        ServiceLog($"CSFloat listing {listingToMonitor.ItemMarketHashName} with price {listingToMonitor.comparison.CSFloatListing.PriceText} has been accepted by the seller.");
                                        listingToMonitor.CurrentStrategicStage = Scanalytics.StrategicStages.WaitingForCSFloatTradeToBeSent;
                                        scanalytics.UpdatePurchasedListing(listingToMonitor);
                                    }
                                }
                                else if (listingToMonitor.CurrentStrategicStage == Scanalytics.StrategicStages.WaitingForCSFloatTradeToBeSent)
                                {
                                    //If steam_offer or sent_at does not exist, then the trade offer has not been sent yet.
                                    string sentAt;
                                    try
                                    {
                                        sentAt = Convert.ToString(item.steam_offer.sent_at);
                                    }
                                    catch (Exception)
                                    {
                                        continue;
                                    }
                                    if (!string.IsNullOrEmpty(sentAt))
                                    {
                                        listingToMonitor.TimeOfSellerToSendTradeOffer = DateTime.Parse(sentAt);
                                        listingToMonitor.CSFloatToSteamTradeOfferLink = "https://steamcommunity.com/tradeoffer/" + Convert.ToString(item.steam_offer.id) + "/";
                                        //Tell Klives that this listing has been sent using an embed
                                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CSFloat Trade Sent",
                                            $"Listing {listingToMonitor.ItemMarketHashName}\n" +
                                            $"Price: {listingToMonitor.comparison.CSFloatListing.PriceText}\n" +
                                            $"\nTrade offer has been sent by the seller. Please accept before the deadline at " + listingToMonitor.TimeOfSellerToSendTradeOffer.ToString("dd/MM/yyyy HH:mm:ss")
                                            + "\nTrade Offer Link: " + listingToMonitor.CSFloatToSteamTradeOfferLink,
                                            DSharpPlus.Entities.DiscordColor.Orange, new Uri(listingToMonitor.comparison.CSFloatListing.ImageURL)));
                                        //log it
                                        ServiceLog($"CSFloat listing {listingToMonitor.ItemMarketHashName} with price {listingToMonitor.comparison.CSFloatListing.PriceText} trade offer has been sent by the seller.");
                                        listingToMonitor.CurrentStrategicStage = Scanalytics.StrategicStages.WaitingForCSFloatTradeToBeAccepted;
                                        scanalytics.UpdatePurchasedListing(listingToMonitor);
                                    }
                                }
                                else if (listingToMonitor.CurrentStrategicStage == Scanalytics.StrategicStages.WaitingForCSFloatTradeToBeAccepted)
                                {
                                    string verifySaleAt;
                                    try
                                    {
                                        verifySaleAt = Convert.ToString(item.verify_sale_at);
                                    }
                                    catch (Exception)
                                    {
                                        continue;
                                    }
                                    if (!string.IsNullOrEmpty(verifySaleAt))
                                    {
                                        DateTime reselltime;
                                        try
                                        {
                                            reselltime = DateTime.Parse(Convert.ToString(item.trade_protection_ends_at));
                                            reselltime = reselltime.AddHours(1.1);
                                        }
                                        catch (Exception ex)
                                        {
                                            reselltime = DateTime.Now.AddDays(8);
                                        }
                                        listingToMonitor.PredictedTimeToBeResoldOnSteam = reselltime;
                                        string filename = "SellCS2ArbitrageListingOnSteam" + listingToMonitor.ItemMarketHashName;
                                        filename = string.Join("-", filename.Split(Path.GetInvalidFileNameChars()));
                                        ServiceCreateScheduledTask(listingToMonitor.PredictedTimeToBeResoldOnSteam, filename, "CS2ArbitrageStrategy", $"{listingToMonitor.ItemMarketHashName} will no longer be on steam tradelock.", true, JsonConvert.SerializeObject(listingToMonitor));
                                        //Tell Klives that this listing has been accepted
                                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"CSFloat trade for skin {listingToMonitor.ItemMarketHashName} of price {listingToMonitor.comparison.CSFloatListing.PriceText} has been detected as completed.");
                                        ServiceLog($"CSFloat trade for skin {listingToMonitor.ItemMarketHashName} of price {listingToMonitor.comparison.CSFloatListing.PriceText} has been detected as completed.");
                                        listingToMonitor.CurrentStrategicStage = Scanalytics.StrategicStages.JustRetrieved;
                                        listingToMonitor.TimeOfItemRetrieval = DateTime.Parse(Convert.ToString(item.steam_offer.updated_at));
                                        scanalytics.UpdatePurchasedListing(listingToMonitor);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                ServiceLogError(ex);
                            }
                        }
                    }
                }
                else
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        //If the response is 429 Too Many Requests, wait for 60 seconds and try again
                        ServiceLog("CSFloat API rate limit reached. Waiting for 60 seconds before retrying.");
                        await Task.Delay(60000);
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        //If the response is 401 Unauthorized, log it and exit
                        ServiceLogError("CSFloat API key is invalid or expired. Please check your CSFloat API key.");
                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("CSFloat API key is invalid or expired. Please check your CSFloat API key.");
                        return;
                    }
                    else
                    {
                        await ServiceLogError($"Failed to get trade list from CSFloat. Status Code: {response.StatusCode}");
                        //Tell Klives
                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"Failed to get trade list from CSFloat. \nStatus Code: {response.StatusCode}\nResponse: {await response.Content.ReadAsStringAsync()}");
                    }
                }
                await Task.Delay(3000);
                MonitorTradeList();
            }
        }
        public async Task SnipeDealsAndAlertKlives()
        {
            try
            {
                csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();
                steamBalance = await steamAPIWrapper.profileWrapper.GetSteamBalance();
                if (csfloatAccountInformation.BalanceInPounds < 2)
                {
                    await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(60), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market after insufficient balance in CSFloat Account.", false);
                    return;
                }
                if (OmniPaths.CheckIfOnServer() == false)
                {
                    await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market", false);
                    return;
                }

                ScanResults scanResults = new ScanResults();

                //Search highest discounts
                ScanStrategyResult highestDiscountScanStrategy = new ScanStrategyResult();
                await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(250, maximumPriceInPence: csfloatAccountInformation.BalanceInPence, normalOnly: true, csfloatSortBy: "highest_discount"))
                {
                    try
                    {
                        var result = await ProcessCSFloatListing(snipe);
                        if (result == null)
                        {
                            highestDiscountScanStrategy.DuplicateListingsFound++;
                        }
                        else
                        {
                            highestDiscountScanStrategy.ScannedComparisons.Add(result.Value.Key);
                            if (result.Value.Value != null)
                            {
                                highestDiscountScanStrategy.PurchasedListings.Add(result.Value.Value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        highestDiscountScanStrategy.ErrorsOccurred++;
                    }
                }
                highestDiscountScanStrategy.StrategyUsed = ScanStrategy.SearchingThroughCSFloatHighestDiscount;
                highestDiscountScanStrategy.StrategyUsedString = Enum.GetName(typeof(ScanStrategy), highestDiscountScanStrategy.StrategyUsed);
                highestDiscountScanStrategy.ParentScanID = scanResults.ScanID;
                scanResults.ScanStrategyResults.Add(highestDiscountScanStrategy);


                //Search new listings
                ScanStrategyResult searchNewListings = new ScanStrategyResult();
                await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(200, noRepeatedItems: false, maximumPriceInPence: csfloatAccountInformation.BalanceInPence, normalOnly: true, csfloatSortBy: "most_recent"))
                {
                    try
                    {
                        var result = await ProcessCSFloatListing(snipe);
                        if (result == null)
                        {
                            searchNewListings.DuplicateListingsFound++;
                        }
                        else
                        {
                            searchNewListings.ScannedComparisons.Add(result.Value.Key);
                            if (result.Value.Value != null)
                            {
                                searchNewListings.PurchasedListings.Add(result.Value.Value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        searchNewListings.ErrorsOccurred++;
                    }
                }
                searchNewListings.StrategyUsed = ScanStrategy.SearchingThroughCSFloatNewest;
                searchNewListings.StrategyUsedString = Enum.GetName(typeof(ScanStrategy), searchNewListings.StrategyUsed);
                searchNewListings.ParentScanID = scanResults.ScanID;
                scanResults.ScanStrategyResults.Add(searchNewListings);

                scanResults.ProduceOverallAnalytics();
                scanalytics.UpdateScanResult(scanResults);
                await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market", false);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in SnipeDealsAndAlertKlives");
                await (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"Error in SnipeDealsAndAlertKlives: {ex.Message}");
                await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(15), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market after an error.", false);
            }
        }
        private async Task<KeyValuePair<ScannedComparison, PurchasedListing>?> ProcessCSFloatListing(CSFloatWrapper.ItemListing snipe)
        {
            try
            {
                SteamAPIWrapper.ItemListing correspondingListing = await steamAPIWrapper.GetItemOnMarket(snipe.ItemMarketHashName);
                //Find price difference
                Scanalytics.ScannedComparison comparison = new Scanalytics.ScannedComparison(snipe, correspondingListing, DateTime.Now, scanalytics.expectedSteamToCSFloatConversionPercentage);
                if (scanalytics.AllScannedComparisonsInHistory.Where(k => k.CSFloatListing.ItemListingID == snipe.ItemListingID).Any())
                {
                    ServiceLog($"Snipe for {snipe.ItemMarketHashName} already exists in history, skipping.");
                    return null;
                }
                else
                {
                    await scanalytics.SaveScannedComparison(comparison);
                    scanalytics.AllScannedComparisonsInHistory.Add(comparison);
                }
                if ((comparison.PredictedOverallArbitrageGain - 1) * 100 > MinimumPercentReturnToSnipe)
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                    string bodytext = $"Name: {snipe.ItemMarketHashName}\n" +
                        $"CSFloat Price: {snipe.PriceText}\n" +
                        $"Steam Price: {correspondingListing.PriceText}\n" +
                        $"Raw Arbitrage Gain: **{Math.Round((comparison.RawArbitrageGain - 1) * 100, 2).ToString()}%**\n" +
                        $"Arbitrage Gain After Steam Tax: **{Math.Round((comparison.ArbitrageGainAfterSteamTax - 1) * 100, 2).ToString()}%**\n" +
                        $"Predicted Overall Gain After {((scanalytics.expectedSteamToCSFloatConversionPercentage * 100)).ToString()}% Conversion: **{Math.Round((comparison.PredictedOverallArbitrageGain - 1) * 100, 2).ToString()}%**\n" +
                        $"CSFloat Listing URL: {snipe.ListingURL}\n" +
                        $"Steam Listing URL: {correspondingListing.ListingURL}\n\n" +
                        $"Purchase Status: Purchasing...";
                    var message = await (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found!",
                        bodytext, DSharpPlus.Entities.DiscordColor.Orange, new Uri(snipe.ImageURL)));

                    bool itemPurchased = false;
                    //Purchase item
                    try
                    {
                        csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();
                        if (csfloatAccountInformation.BalanceInPence > snipe.PriceInPence)
                        {
                            var result = await csFloatWrapper.BuyCSFloatListing(snipe);
                            if (result == true)
                            {
                                itemPurchased = true;
                                message.ModifyAsync(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found and purchased!",
                                    bodytext.Replace("Purchase Status: Purchasing...", "Purchase Status: Purchased"), DiscordColor.DarkRed, new Uri(snipe.ImageURL)));
                            }
                        }
                        else
                        {
                            message.ModifyAsync(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found but not purchased.",
                                bodytext.Replace("Purchase Status: Purchasing...", "Purchase Status: Couldnt Afford"), DiscordColor.DarkRed, new Uri(snipe.ImageURL)));
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, "Error while purchasing CSFloat listing.");
                        message.ModifyAsync(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found and purchased!",
                            bodytext.Replace("Purchase Status: Purchasing...", "Purchase Status: Couldnt Purchase - Error: " + ex.Message), DiscordColor.DarkRed, new Uri(snipe.ImageURL)));
                    }

                    //Scanalytics
                    if (itemPurchased)
                    {
                        Scanalytics.PurchasedListing purchasedListing = new Scanalytics.PurchasedListing
                        {
                            comparison = comparison,
                            TimeOfPurchase = DateTime.Now,
                            CSFloatListingID = snipe.ItemListingID,
                            ExpectedAbsoluteProfitInPence = ((int)(comparison.PredictedOverallArbitrageGain * comparison.CSFloatListing.PriceInPence)) - comparison.CSFloatListing.PriceInPence,
                            ExpectedAbsoluteProfitInPounds = (float)(((comparison.PredictedOverallArbitrageGain * comparison.CSFloatListing.PriceInPence) / 100) - comparison.CSFloatListing.PriceInPounds),
                            ExpectedProfitPercentage = (float)((comparison.PredictedOverallArbitrageGain - 1) * 100),

                            ItemFloatValue = (float)snipe.FloatValue,
                            ItemMarketHashName = snipe.ItemMarketHashName,

                            CurrentStrategicStage = Scanalytics.StrategicStages.WaitingForCSFloatSellerToAcceptSale,
                        };

                        scanalytics.UpdatePurchasedListing(purchasedListing);
                        return new KeyValuePair<Scanalytics.ScannedComparison, Scanalytics.PurchasedListing>(comparison, purchasedListing);
                    }
                    else
                    {
                        return new KeyValuePair<Scanalytics.ScannedComparison, Scanalytics.PurchasedListing>(comparison, null);
                    }

#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
                else
                {
                    return new KeyValuePair<ScannedComparison, PurchasedListing>(comparison, null);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error while processing snipe deal.");
                throw;
            }
        }
        public async Task<Scanalytics.PurchasedListing> FindAndPurchaseParticularListing(string CSFloatListingID)
        {
            string url = $"https://csfloat.com/api/v1/listings/{CSFloatListingID}";
            var response = await csFloatWrapper.Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                await ServiceLogError($"Failed to get CSFloat listing with ID {CSFloatListingID}. Status Code: {response.StatusCode}");
                throw new Exception($"Failed to get CSFloat listing with ID {CSFloatListingID}. Status Code: {response.StatusCode}");
            }
            string responseString = await response.Content.ReadAsStringAsync();
            dynamic json = JsonConvert.DeserializeObject(responseString);
            CSFloatWrapper.ItemListing csfloatlisting = csFloatWrapper.ConvertItemListingJSONItemToStruct(json, false);
            SteamAPIWrapper.ItemListing correspondingListing = await steamAPIWrapper.GetItemOnMarket(csfloatlisting.ItemMarketHashName);
            Scanalytics.ScannedComparison comparison = new Scanalytics.ScannedComparison(csfloatlisting, correspondingListing, DateTime.Now, scanalytics.expectedSteamToCSFloatConversionPercentage);

            if (csfloatAccountInformation.BalanceInPence > csfloatlisting.PriceInPence)
            {

                var result = await csFloatWrapper.BuyCSFloatListing(csfloatlisting);
                Scanalytics.PurchasedListing purchasedListing = new Scanalytics.PurchasedListing
                {
                    comparison = comparison,
                    TimeOfPurchase = DateTime.Now,
                    CSFloatListingID = csfloatlisting.ItemListingID,
                    ExpectedAbsoluteProfitInPence = (int)(comparison.PredictedOverallArbitrageGain * comparison.CSFloatListing.PriceInPence),
                    ExpectedAbsoluteProfitInPounds = (float)((comparison.PredictedOverallArbitrageGain * comparison.CSFloatListing.PriceInPence)) / 100,
                    ExpectedProfitPercentage = (float)((comparison.PredictedOverallArbitrageGain - 1) * 100),

                    ItemFloatValue = (float)csfloatlisting.FloatValue,
                    ItemMarketHashName = csfloatlisting.ItemMarketHashName,

                    CurrentStrategicStage = Scanalytics.StrategicStages.WaitingForCSFloatSellerToAcceptSale,
                };

                scanalytics.UpdatePurchasedListing(purchasedListing);

                //tell klives that the bot purchased what he asked him to, this is not a snipe.
                (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Arbitrage Bot Purchase",
                    $"Purchased CSFloat listing {csfloatlisting.ItemMarketHashName} with price {csfloatlisting.PriceText}.\n" +
                    $"Predicted Overall Gain After {((scanalytics.expectedSteamToCSFloatConversionPercentage * 100)).ToString()}% Conversion: **{Math.Round((comparison.PredictedOverallArbitrageGain - 1) * 100, 2).ToString()}%**\n" +
                    $"CSFloat Listing URL: {csfloatlisting.ListingURL}\n" +
                    $"Steam Listing URL: {correspondingListing.ListingURL}",
                    DSharpPlus.Entities.DiscordColor.Green, new Uri(csfloatlisting.ImageURL)));
                return purchasedListing;
            }
            return null;
        }
        public async Task CreateRoutes()
        {
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/cs2arbitragebot/getscanalytics", async (request) =>
            {
                try
                {
                    Scanalytics.ScannedComparisonAnalytics analytics = new Scanalytics.ScannedComparisonAnalytics(scanalytics.AllScannedComparisonsInHistory, scanalytics.AllPurchasedListingsInHistory);

                    await request.ReturnResponse(JsonConvert.SerializeObject(analytics), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, "Error in /cs2arbitragebot/getscanalytics route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/cs2arbitragebot/scanresults", async (request) =>
            {
                try
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(scanalytics.AllScanResultsInHistory), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, "Error in /cs2arbitragebot/scanresults route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/cs2arbitragebot/latestliquidityplan", async (request) =>
            {
                try
                {
                    var plan = scanalytics.ProduceLiquidityPlanAsync(scanalytics.GetLatestLiquiditySearchResult());
                    await request.ReturnResponse(JsonConvert.SerializeObject(plan), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, "Error in /cs2arbitragebot/latestliquidityplan route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/cs2arbitragebot/balanceHistory", async (request) =>
            {
                try
                {
                    var logs = await scanalytics.GetAllLogsOfCSFloatAndSteamBalance();
                    await request.ReturnResponse(JsonConvert.SerializeObject(logs), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);

        }
    }
}
