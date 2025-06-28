using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Org.BouncyCastle.Asn1.Esf;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CS2ArbitrageBot : OmniService
    {
        public float ExchangeRate;
        private SteamAPIWrapper steamAPIWrapper;
        private CSFloatWrapper csFloatWrapper;

        public float MinimumPercentReturnToSnipe = 10;

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
            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;
            if (await serviceManager.timeManager.GetTask("SnipeCS2Deals") == null || OmniPaths.CheckIfOnServer() != true)
            {
                SnipeDealsAndAlertKlives();
            }
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
            await foreach (CSFloatWrapper.ItemListing snipe in csFloatWrapper.SnipeBestDealsOnCSFloat(250, maximumPriceInPence: 1000, normalOnly: true))
            {
                try
                {
                    SteamAPIWrapper.ItemListing correspondingListing = await steamAPIWrapper.GetItemOnMarket(snipe.ItemMarketHashName);
                    //Find price difference
                    double percentageDifference = Convert.ToDouble((correspondingListing.PriceInPounds / snipe.PriceInPounds) - 1) * 100;
                    double gainAfterSteamTax = (((correspondingListing.PriceInPounds / 1.15) / snipe.PriceInPounds) - 1) * 100;
                    double expectedSteamToCSFloatConversionPercentage = 0.8;
                    double predictedOverallGain = (((((correspondingListing.PriceInPounds / 1.15)) * 0.8) / snipe.PriceInPounds) - 1) * 100;
                    if (predictedOverallGain > predictedOverallGain)
                    {
                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("CS2 Snipe Opportunity Found!",
                            $"Name: {snipe.ItemMarketHashName}\n" +
                            $"CSFloat Price: {snipe.PriceText}\n" +
                            $"Steam Price: {correspondingListing.PriceText}\n" +
                            $"Raw Arbitrage Gain: **{Math.Round(percentageDifference, 2).ToString()}%**\n" +
                            $"Arbitrage Gain After Steam Tax: **{Math.Round(gainAfterSteamTax, 2).ToString()}%**\n" +
                            $"Predicted Overall Gain After {((expectedSteamToCSFloatConversionPercentage * 100)).ToString()}% Conversion: **{Math.Round(predictedOverallGain, 2).ToString()}%**\n" +
                            $"CSFloat Listing URL: {snipe.ListingURL}\n" +
                            $"Steam Listing URL: {correspondingListing.ListingURL}\n"
                            , DSharpPlus.Entities.DiscordColor.Orange, new Uri(snipe.ImageURL)));
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
