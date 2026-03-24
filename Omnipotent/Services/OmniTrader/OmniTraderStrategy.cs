using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using System.Management.Automation.Remoting;

namespace Omnipotent.Services.OmniTrader
{
    public class TradeSignalEventArgs : EventArgs
    {
        public required AmountType amountType;
        public required decimal inputAmount;
        public decimal? StopLossPrice;
        public decimal? TakeProfitPrice;
    }
    public enum TradeSessionType
    {
        None,
        Backtester,
        Simulator,
        Testnet,
        Live
    }
    public class TradeSessionState
    {
        public TradeSessionType sessionType;
        public List<TradePosition> Positions = new();
        public List<TradePosition> GetOpenPositions()
        {
            return Positions.Where(p => p.closedTime == default).ToList();
        }
    }
    public class TradePosition
    {
        public AmountType amountType;
        public decimal inputAmount;
        public decimal stopLossPrice;
        public decimal takeProfitPrice;
        public decimal positionEntryPrice;
        public decimal positionClosedPrice;
        public DateTime entryTime;
        public DateTime closedTime;
    }
    public class OmniTraderStrategy
    {
        public string Name;
        public string Description;

        public OmniTrader parent;
        public bool IsLoaded = false;

        public event EventHandler<TradeSignalEventArgs> OnLong;
        public event EventHandler<TradeSignalEventArgs> OnSell;
        public event EventHandler<TradeSignalEventArgs> OnShort;
        public event EventHandler<TradePosition> ClosePosition;
        public TradeSessionState tradeSessionState;


        public List<OmniTraderFinanceData.OHLCCandle> candleHistory;

        public string OmniStrategyDirectoryPath = "";

        
        public async Task Initialise(OmniTrader parent)
        {
            tradeSessionState = new TradeSessionState { sessionType = TradeSessionType.None };
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

        protected void RaiseLong(AmountType amountType, decimal inputAmount, decimal? stopLossPrice = null, decimal? takeProfitPrice = null)
        {
            OnLong?.Invoke(this, new TradeSignalEventArgs
            {
                amountType = amountType,
                inputAmount = inputAmount,
                StopLossPrice = stopLossPrice,
                TakeProfitPrice = takeProfitPrice,
            });
        }

        protected void RaiseShort(AmountType amountType, decimal inputAmount, decimal? stopLossPrice = null, decimal? takeProfitPrice = null)
        {
            OnShort?.Invoke(this, new TradeSignalEventArgs { 
                amountType = amountType, inputAmount = inputAmount, StopLossPrice=stopLossPrice, TakeProfitPrice=takeProfitPrice
            });
        }

        protected void RaiseSell(AmountType amountType, decimal inputAmount)
        {
            OnSell?.Invoke(this, new TradeSignalEventArgs
            {
                amountType = amountType,
                inputAmount = inputAmount
            });
        }

        protected void RaiseClosePosition(TradePosition position)
        {
            ClosePosition?.Invoke(this, position);
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

        public async void StrategyLogError(Exception ex, string message)
        {
            await parent.ServiceLogError(ex, $"[{Name}] {message}");
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
