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

            SimpleXGBoostRegressionOmniStrategy simpleXGBoostRegressionOmniStrategy = new();
            await simpleXGBoostRegressionOmniStrategy.Initialise(this);

            IBSMeanReversionStrategy ibsMeanReversionStrategy = new();
            await ibsMeanReversionStrategy.Initialise(this);

            var backtestSet = await requestKlineData.GetCryptoCandlesDataAsync("BTC", "USD", RequestKlineData.TimeInterval.OneDay, 1000);
            CandlestickChartGenerator candlestickChartGenerator = new();


            var settings = new BacktestSettings
            {
                InitialQuoteBalance = 1000,
                FeeFraction = 0.001m,
                SlippageFraction = 0.0005m
            };

            var ibsResults = await ibsMeanReversionStrategy.BacktestStrategy(backtestSet, settings);
            await ServiceLog(ibsResults.ToString());
        }
    }
}
