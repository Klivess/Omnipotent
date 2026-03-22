using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader
{
    public class TradeSignalEventArgs : EventArgs
    {
        public required AmountType amountType;
        public required decimal inputAmount;
        public decimal? StopLossPrice;
        public decimal? TakeProfitPrice;
    }

    public class OmniTraderStrategy
    {
        public string Name;
        public string Description;

        public OmniTrader parent;
        public bool IsLoaded = false;

        public event EventHandler<TradeSignalEventArgs> OnBuy;
        public event EventHandler<TradeSignalEventArgs> OnSell;
        internal event Action<decimal>? OnStopLossUpdated;
        internal event Action<decimal>? OnTakeProfitUpdated;

        public List<OmniTraderFinanceData.OHLCCandle> candleHistory;

        public string OmniStrategyDirectoryPath = "";

        public async Task Initialise(OmniTrader parent)
        {
            string proposedDirPathName = Name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                proposedDirPathName = proposedDirPathName.Replace(c, '_');
            }
            OmniStrategyDirectoryPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTraderStrategiesDirectory), proposedDirPathName);
            Directory.CreateDirectory(OmniStrategyDirectoryPath);

            this.parent = parent;
            candleHistory = new();
            if (IsLoaded)
                return;

            await OnLoad();
            IsLoaded = true;
        }
        protected virtual async Task OnLoad() { }

        public async Task CandleClose(OmniTraderFinanceData.OHLCCandle candleData)
        {
            if (!IsLoaded)
                throw new InvalidOperationException($"Strategy '{Name}' was not initialised. Call Initialise() before Tick().");

            if (candleHistory.Count > 0 && candleData.Timestamp == candleHistory[^1].Timestamp)
                return;

            candleHistory.Add(candleData);
            await OnCandleClose(candleData);
        }

        protected virtual Task OnCandleClose(OmniTraderFinanceData.OHLCCandle candleData) => Task.CompletedTask;

        protected void RaiseBuy(AmountType amountType, decimal inputAmount, decimal? stopLossPrice = null, decimal? takeProfitPrice = null)
        {
            OnBuy?.Invoke(this, new TradeSignalEventArgs
            {
                amountType = amountType,
                inputAmount = inputAmount,
                StopLossPrice = stopLossPrice,
                TakeProfitPrice = takeProfitPrice
            });
        }

        protected void RaiseSell(AmountType amountType, decimal inputAmount)
        {
            OnSell?.Invoke(this, new TradeSignalEventArgs { amountType = amountType, inputAmount = inputAmount });
        }

        protected void UpdateStopLoss(decimal price)
        {
            OnStopLossUpdated?.Invoke(price);
        }

        protected void UpdateTakeProfit(decimal price)
        {
            OnTakeProfitUpdated?.Invoke(price);
        }

        /// <summary>Called by the backtester when a stop-loss order fills. Override to update internal state.</summary>
        protected virtual void OnStopLossHit(decimal fillPrice) { }

        /// <summary>Called by the backtester when a take-profit order fills. Override to update internal state.</summary>
        protected virtual void OnTakeProfitHit(decimal fillPrice) { }

        internal void NotifyStopLossTriggered(decimal fillPrice) => OnStopLossHit(fillPrice);
        internal void NotifyTakeProfitTriggered(decimal fillPrice) => OnTakeProfitHit(fillPrice);

        public async void StrategyLog(string message)
        {
            await parent.ServiceLog($"[{Name}] {message}");
        }

        public async Task<OmniBacktestResult> BacktestStrategy(OmniTraderFinanceData.OHLCCandlesData testSet, BacktestSettings? settings = null)
        {
            var backtester = new OmniBacktester(this, testSet, settings);
            return await backtester.RunAsync();
        }

        public async Task<OmniBacktestResult> FindBestTimeframeForStrategy(string coin, string currency, int amountOfCandles = 500, BacktestSettings? settings = null)
        {
            OmniBacktestResult bestResult = new();
            foreach(var frame in Enum.GetValues(typeof(OmniTraderFinanceData.TimeInterval)))
            {
                OmniTraderFinanceData.TimeInterval interval = (OmniTraderFinanceData.TimeInterval)frame;
                if (interval >= OmniTraderFinanceData.TimeInterval.OneWeek)
                    break;
                var testSet = await parent.data.GetCryptoCandlesDataAsync(coin, currency, interval, amountOfCandles);
                var backtester = new OmniBacktester(this, testSet, settings);
                var result = await backtester.RunAsync();
                if (result.FinalEquity > bestResult.FinalEquity)
                {
                    bestResult = result;
                }
            }
            return bestResult;
        }
    }
}
