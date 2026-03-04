using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader
{
    public class TradeSignalEventArgs : EventArgs
    {
        public required AmountType amountType;
        public required decimal inputAmount;
    }

    public class OmniTraderStrategy
    {
        public string Name;
        public string Description;

        public OmniTrader parent;
        public bool IsLoaded = false;

        public event EventHandler<TradeSignalEventArgs> OnBuy;
        public event EventHandler<TradeSignalEventArgs> OnSell;

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

        protected void RaiseBuy(AmountType amountType, decimal inputAmount)
        {
            OnBuy?.Invoke(this, new TradeSignalEventArgs { amountType = amountType, inputAmount = inputAmount });
        }

        protected void RaiseSell(AmountType amountType, decimal inputAmount)
        {
            OnSell?.Invoke(this, new TradeSignalEventArgs { amountType = amountType, inputAmount = inputAmount });
        }

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
