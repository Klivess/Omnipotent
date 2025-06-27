using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CS2ArbitrageBot : OmniService
    {
        public float ExchangeRate;
        private SteamAPIWrapper steamAPIWrapper;
        private CSFloatWrapper csFloatWrapper;

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
            StartBotLogic();
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

        public async Task StartBotLogic()
        {
            var resp = await csFloatWrapper.GetBestDealsOnCSFloat(5);
            //json serialise and send to klives
            string json = JsonConvert.SerializeObject(resp, Formatting.Indented);
            await ServiceLog($"Best Deals on CSFloat: {json}");
            (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed("Best Deals on CSFloat", json, DSharpPlus.Entities.DiscordColor.Green));
        }
    }
}
