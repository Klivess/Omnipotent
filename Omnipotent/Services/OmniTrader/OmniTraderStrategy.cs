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

        public async Task Initialise(OmniTrader parent)
        {
            this.parent = parent;

            if (IsLoaded)
                return;

            await OnLoad();
            IsLoaded = true;
        }
        protected virtual async Task OnLoad() { }

        public async Task Tick(RequestKlineData.OHLCCandlesData candlesData)
        {
            if (!IsLoaded)
                throw new InvalidOperationException($"Strategy '{Name}' was not initialised. Call Initialise() before Tick().");

            await OnTick(candlesData);
        }

        protected virtual Task OnTick(RequestKlineData.OHLCCandlesData candlesData) => Task.CompletedTask;

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

        public async Task<OmniBacktestResult> BacktestStrategy(RequestKlineData.OHLCCandlesData testSet, BacktestSettings? settings = null)
        {
            var backtester = new OmniBacktester(this, testSet.candles, settings);
            return await backtester.RunAsync();
        }
    }
}
