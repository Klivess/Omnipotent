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

            StrategyLog("Telegram token secured.");
        }
        protected override async Task OnCandleClose(OmniTraderFinanceData.OHLCCandle latest)
        {
        }
    }
}
