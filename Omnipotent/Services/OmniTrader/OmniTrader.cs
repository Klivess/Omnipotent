using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        string prefix = "https://live.trading212.com";
        string apiKey = "";

        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            apiKey = await GetSavedTrading212APIKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                ServiceLogError("No Trading212 API key found! Asking Klives to set it.");
                string key = await (await serviceManager.GetNotificationsService()).SendTextPromptToKlivesDiscord("I require a Trading212 API key!",
                    "Get an API key from either the practice or live account or whatever and give it to me.", TimeSpan.FromDays(999), "T212 Api Key", "API KEY");
                if (string.IsNullOrEmpty(key))
                {
                    ServiceLogError("No API key was provided by Klives! Exiting service.");
                    //Tell klives
                    await (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed("No API key provided!",
                        "No API key was provided by Klives! Exiting OmniTrader service.", DSharpPlus.Entities.DiscordColor.Red));
                    TerminateService();
                }
                apiKey = key.Trim();
                await SaveTrading212APIKey(apiKey);
            }
            ServiceLog("Trading212 API key loaded successfully.");




        }

        public async Task<string> GetSavedTrading212APIKey()
        {
            string key = await GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTraderAPIKey));
            return key;
        }

        public async Task SaveTrading212APIKey(string apiKey)
        {
            await GetDataHandler().WriteToFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTraderAPIKey), apiKey, true);
        }
    }
}
