using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System.Runtime.InteropServices.Marshalling;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using XGBoostSharp;
using static LLama.Native.NativeLibraryConfig;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    public class FlowSignalTraderStrategy : OmniTraderStrategy
    {
        public string apiKeyPath;
        public string telegramBotToken;
        private HttpClient _http = new HttpClient();
        private int _updateOffset = 0;
        private readonly long _targetChatId = -1003783537817L;

        public FlowSignalTraderStrategy()
        {
            Name = "FlowSignal Trader Strategy";
            Description = "Listens to signals from FlowSignal application, and places orders.";
        }

        protected override async Task OnLoad()
        {
            apiKeyPath = Path.Combine(OmniStrategyDirectoryPath, "telegramBotToken.txt");
            telegramBotToken = await parent.GetDataHandler().ReadDataFromFile(apiKeyPath);

            if (string.IsNullOrEmpty(telegramBotToken))
            {
                string response = (string)await parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
    "Telegram Bot Token attached to FlowSignal needed. Set it."
    , "Set the bot token for FlowSignalKliveListenerbot", TimeSpan.FromDays(7), "Enter your token", "Token");
                telegramBotToken = response.Trim();
                await parent.GetDataHandler().WriteToFile(apiKeyPath, telegramBotToken);
            }
            StrategyLog("Telegram token secured.");
        }
        protected override async Task OnCandleClose(OmniTraderFinanceData.OHLCCandle latest)
        {
        }
    }
}
