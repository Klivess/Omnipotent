using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
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
        private SteamAPIWrapper steamAPIWrapper;
        private CSFloatWrapper csFloatWrapper;
        public Scanalytics scanalytics;
        public CSFloatWrapper.CSFloatAccountInformation csfloatAccountInformation;

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
            await steamAPIWrapper.SteamAPIWrapperInitialisation();
            csFloatWrapper = new CSFloatWrapper(this, csfloatAPIKey);
            scanalytics = new Scanalytics(this);
            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;

            csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();

            MonitorTradeList();

            //FindAndPurchaseParticularListing("810847654237047508");

            if (await serviceManager.timeManager.GetTask("SnipeCS2Deals") == null || OmniPaths.CheckIfOnServer() != true)
            {
                SnipeDealsAndAlertKlives();
            }
            CreateRoutes();
            Scanalytics.ScannedComparisonAnalytics analytics = new Scanalytics.ScannedComparisonAnalytics(scanalytics.AllScannedComparisonsInHistory, scanalytics.AllPurchasedListingsInHistory);
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (e.taskName == "SnipeCS2Deals")
            {
                SnipeDealsAndAlertKlives();
            }
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
                                        listingToMonitor.PredictedTimeToBeResoldOnSteam = DateTime.Parse(Convert.ToString(item.trade_protection_ends_at));
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
                        await (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("CSFloat API key is invalid or expired. Please check your CSFloat API key.");
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
            if (OmniPaths.CheckIfOnServer() == false)
            {
                await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market");
                return;
            }
            //Search highest discounts
            await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(250, maximumPriceInPence: csfloatAccountInformation.BalanceInPence, normalOnly: true, csfloatSortBy: "highest_discount"))
            {
                ProcessCSFloatListing(snipe);
            }
            //Search new listings
            await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(150, maximumPriceInPence: csfloatAccountInformation.BalanceInPence, normalOnly: true, csfloatSortBy: "most_recent"))
            {
                ProcessCSFloatListing(snipe);
            }
            await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market");
        }

        private async Task ProcessCSFloatListing(CSFloatWrapper.ItemListing snipe)
        {
            try
            {
                SteamAPIWrapper.ItemListing correspondingListing = await steamAPIWrapper.GetItemOnMarket(snipe.ItemMarketHashName);
                //Find price difference
                Scanalytics.ScannedComparison comparison = new Scanalytics.ScannedComparison(snipe, correspondingListing, DateTime.Now);
                if (scanalytics.AllScannedComparisonsInHistory.Where(k => k.CSFloatListing.ItemListingID == snipe.ItemListingID).Any())
                {
                    ServiceLog($"Snipe for {snipe.ItemMarketHashName} already exists in history, skipping.");
                    return;
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


                    //Purchase item
                    try
                    {
                        csfloatAccountInformation = await csFloatWrapper.GetAccountInformation();
                        if (csfloatAccountInformation.BalanceInPence > snipe.PriceInPence)
                        {
                            var result = await csFloatWrapper.BuyCSFloatListing(snipe);
                            if (result == true)
                            {
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

#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error while processing snipe deal.");
                return;
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
            Scanalytics.ScannedComparison comparison = new Scanalytics.ScannedComparison(csfloatlisting, correspondingListing, DateTime.Now);

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
                Scanalytics.ScannedComparisonAnalytics analytics = new Scanalytics.ScannedComparisonAnalytics(scanalytics.AllScannedComparisonsInHistory, scanalytics.AllPurchasedListingsInHistory);

                await request.ReturnResponse(JsonConvert.SerializeObject(analytics), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Guest);
        }
    }
}
