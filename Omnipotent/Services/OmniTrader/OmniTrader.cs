using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Strategies;
using SimpleBacktestLib;

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

            SimpleXGBoostRegressionOmniStrategy simpleXGBoostRegressionOmniStrategy = new();
            simpleXGBoostRegressionOmniStrategy.Initialise(this);


            var backtestSet = await requestKlineData.GetCryptoCandlesDataAsync("BTC", "USD", RequestKlineData.TimeInterval.OneHour, 500);
            var results = await simpleXGBoostRegressionOmniStrategy.BacktestStrategy(backtestSet);
            ServiceLog(JsonConvert.SerializeObject(results));
        }
    }
}
