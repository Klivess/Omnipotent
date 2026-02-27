using JetBrains.Annotations;
using Omnipotent.Services.OmniTrader.Data;
using SimpleBacktestLib;

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

        public void Initialise(OmniTrader parent)
        {
            this.parent = parent;

            if (IsLoaded)
                return;

            OnLoad();
            IsLoaded = true;
        }
        protected virtual async void OnLoad() { }
        public virtual void OnTick(RequestKlineData.OHLCCandlesData last200Candles) { }

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

        private List<BacktestCandle> ConvertOmniCandleToBacktestableCandle(List<RequestKlineData.OHLCCandle> candles)
        {
            return candles.Select(c => new BacktestCandle
            {
                Time = c.Timestamp,
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume
            }).ToList();
        }

        public async Task<BacktestResult> BacktestStrategy(RequestKlineData.OHLCCandlesData testSet)
        {
            //convert the candles to backtestable candles
            List<BacktestCandle> backtestCandles = ConvertOmniCandleToBacktestableCandle(testSet.candles);

            BacktestBuilder builder = BacktestBuilder.CreateBuilder(backtestCandles)
            .OnTick(state =>
            {
                var currentCandle = testSet.candles.FirstOrDefault(c => c.Timestamp == state.GetCurrentCandle().Time);

                OnBuy += (sender, args) =>
                {
                    state.Trade.Spot.Buy(args.amountType, args.inputAmount);
                };

                OnSell += (sender, args) =>
                {
                    state.Trade.Spot.Sell(args.amountType, args.inputAmount);
                };


                OnTick(new RequestKlineData.OHLCCandlesData { candles = new List<RequestKlineData.OHLCCandle> { currentCandle } });
            });

            return await builder.RunAsync();
        }
    }
}
