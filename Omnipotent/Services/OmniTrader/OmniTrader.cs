using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Strategies;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public RequestKlineData requestKlineData;
        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            requestKlineData = new RequestKlineData(this);

            MetaLabeledRLStrategy strategy = new MetaLabeledRLStrategy();
            await strategy.Initialise(this);
            var historicalData = await requestKlineData.GetCryptoCandlesDataAsync("ETH", "USD", RequestKlineData.TimeInterval.FifteenMinute, 700);
            var result = await strategy.BacktestStrategy(historicalData);
            ServiceLog(result.ToString());
        }
    }
}
