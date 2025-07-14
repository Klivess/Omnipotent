using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using Org.BouncyCastle.Asn1.Esf;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CS2ArbitrageBot : OmniService
    {
        public float ExchangeRate;
        private SteamAPIWrapper steamAPIWrapper;
        private CSFloatWrapper csFloatWrapper;
        public Scanalytics scanalytics;

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


            if (await serviceManager.timeManager.GetTask("SnipeCS2Deals") == null || OmniPaths.CheckIfOnServer() != true)
            {
                SnipeDealsAndAlertKlives();
            }

            Scanalytics.ScannedComparisonAnalytics analytics = new Scanalytics.ScannedComparisonAnalytics(scanalytics.AllScannedComparisonsInHistory);
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
            //Exchange Rate: {ExchangeRate} GBP = 1 USD"
        }

        public async Task SnipeDealsAndAlertKlives()
        {
            await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(250, maximumPriceInPence: 80 * 100, normalOnly: true, csfloatSortBy: "highest_discount"))
            {
                try
                {
                    SteamAPIWrapper.ItemListing correspondingListing = await steamAPIWrapper.GetItemOnMarket(snipe.ItemMarketHashName);
                    //Find price difference
                    Scanalytics.ScannedComparison comparison = new Scanalytics.ScannedComparison(snipe, correspondingListing, DateTime.Now);
                    await scanalytics.SaveScannedComparison(comparison);
                    //If doesnt already exist in AllScannedComparisonsInHistory, add it
                    if (scanalytics.AllScannedComparisonsInHistory.Any(k => k.ItemMarketHashName == comparison.ItemMarketHashName))
                    {
                        var existingComparison = scanalytics.AllScannedComparisonsInHistory.First(k => k.ItemMarketHashName == comparison.ItemMarketHashName);
                        if (existingComparison.PredictedOverallArbitrageGain < comparison.PredictedOverallArbitrageGain)
                        {
                            //If the new comparison has a better predicted overall arbitrage gain, replace the old one
                            scanalytics.AllScannedComparisonsInHistory.Remove(existingComparison);
                            await scanalytics.SaveScannedComparison(comparison);
                            scanalytics.AllScannedComparisonsInHistory.Add(comparison);
                        }
                        else
                        {
                            //If the existing one is better, skip adding this one
                            continue;
                        }
                    }
                    else
                    {
                        scanalytics.AllScannedComparisonsInHistory.Add(comparison);
                    }
                    if ((comparison.PredictedOverallArbitrageGain - 1) * 100 > MinimumPercentReturnToSnipe)
                    {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found!",
                            $"Name: {snipe.ItemMarketHashName}\n" +
                            $"CSFloat Price: {snipe.PriceText}\n" +
                            $"Steam Price: {correspondingListing.PriceText}\n" +
                            $"Raw Arbitrage Gain: **{Math.Round((comparison.RawArbitrageGain - 1) * 100, 2).ToString()}%**\n" +
                            $"Arbitrage Gain After Steam Tax: **{Math.Round((comparison.ArbitrageGainAfterSteamTax - 1) * 100, 2).ToString()}%**\n" +
                            $"Predicted Overall Gain After {((scanalytics.expectedSteamToCSFloatConversionPercentage * 100)).ToString()}% Conversion: **{Math.Round((comparison.PredictedOverallArbitrageGain - 1) * 100, 2).ToString()}%**\n" +
                            $"CSFloat Listing URL: {snipe.ListingURL}\n" +
                            $"Steam Listing URL: {correspondingListing.ListingURL}\n"
                            , DSharpPlus.Entities.DiscordColor.Orange, new Uri(snipe.ImageURL)));
#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    }
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "Error while processing snipe deal.");
                    continue;
                }
            }
            await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "SnipeCS2Deals", "CS2ArbitrageSearch", "Search through CSFloat and compare listings to Steam Market");
        }
    }
}
