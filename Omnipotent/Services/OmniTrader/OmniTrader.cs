using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Strategies;
using static Omnipotent.Services.OmniTrader.Data.OmniTraderFinanceData;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public OmniTraderFinanceData data;
        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            data = new OmniTraderFinanceData(this);

            if (!OmniPaths.CheckIfOnServer())
            {
                SimpleXGBoostRegressionOmniStrategy strategy = new();
                await strategy.Initialise(this);
                ServiceLog("");

                string symbol = "BTCUSDT";

                // Fetch the top 1000 levels of the order book (500 bids, 500 asks)
                OmniOrderBook book = await data.GetLiveOrderBookAsync(symbol, 1000);

                ServiceLog($"--- LEVEL 2 SNAPSHOT FOR {symbol} ---");
                ServiceLog($"Last Update ID: {book.LastUpdateId}");

                // The best ask is the LOWEST price a seller is willing to accept (index 0)
                ServiceLog($"Best Ask (Lowest Seller) : {book.Asks[0].Price} | Volume: {book.Asks[0].Quantity}");

                // The best bid is the HIGHEST price a buyer is willing to pay (index 0)
                ServiceLog($"Best Bid (Highest Buyer): {book.Bids[0].Price} | Volume: {book.Bids[0].Quantity}");

                ServiceLog($"Current Spread: {book.Asks[0].Price - book.Bids[0].Price}");

                // Example bot logic: Find a massive "Sell Wall" (Liquidity)
                var massiveSellWall = book.Asks.FirstOrDefault(ask => ask.Quantity > 50); // Look for > 50 BTC
                if (massiveSellWall != null)
                {
                    ServiceLog($"Massive Sell Wall detected at: {massiveSellWall.Price} with {massiveSellWall.Quantity} BTC");
                }
                /*
                //across 7.29 days
                var historicalData = await requestKlineData.GetCryptoCandlesDataAsync("ETH", "USD", OmniTraderFinanceData.TimeInterval.OneHour, 700, DateTime.Now.Subtract(TimeSpan.FromDays(365)));
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
                */
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
