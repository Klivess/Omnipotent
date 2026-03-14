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

            if (!OmniPaths.CheckIfOnServer())
            {
                SimpleXGBoostRegressionOmniStrategy strategy = new();
                await strategy.Initialise(this);
                //across 7.29 days
                var historicalData = await requestKlineData.GetCryptoCandlesDataAsync("ETH", "USD", RequestKlineData.TimeInterval.OneHour, 700, DateTime.Now.Subtract(TimeSpan.FromDays(365)));
                BacktestSettings backtestSettings = new BacktestSettings
                {
                    FeeFraction = 0.004M,
                    InitialBaseBalance = 0M,
                    InitialQuoteBalance = 20M,
                    SlippageFraction = 0.005M
                };
                var result = await strategy.BacktestStrategy(historicalData, backtestSettings);
                await WriteBacktestResultToDesktop(result);
                ServiceLog(result.ToString());
            }
        }

        public async Task WriteBacktestResultToDesktop(OmniBacktestResult result)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, $"BacktestResult_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filePath, result.ToString());
        }
    }
}
