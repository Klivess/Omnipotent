using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public class OmniBacktester
    {
        private readonly BacktestSettings _settings;
        private readonly OmniTraderStrategy _strategy;
        private readonly List<RequestKlineData.OHLCCandle> _candles;

        private decimal _quoteBalance;
        private decimal _baseBalance;
        private decimal _totalFees;

        // Open position tracking
        private bool _inPosition;
        private decimal _positionEntryPrice;
        private decimal _positionQuantity;
        private decimal _positionCost;
        private decimal _positionEntryFee;
        private DateTime _positionEntryTime;

        private readonly List<TradeRecord> _trades = [];
        private readonly List<decimal> _equityCurve = [];

        public OmniBacktester(OmniTraderStrategy strategy, List<RequestKlineData.OHLCCandle> candles, BacktestSettings? settings = null)
        {
            _strategy = strategy;
            _candles = candles;
            _settings = settings ?? new BacktestSettings();
        }

        public async Task<OmniBacktestResult> RunAsync()
        {
            _quoteBalance = _settings.InitialQuoteBalance;
            _baseBalance = _settings.InitialBaseBalance;
            _totalFees = 0;
            _inPosition = false;
            _trades.Clear();
            _equityCurve.Clear();

            decimal initialEquity = _quoteBalance + _baseBalance * (_candles.Count > 0 ? _candles[0].Close : 0);

            // Wire up strategy signals
            _strategy.OnBuy += HandleBuy;
            _strategy.OnSell += HandleSell;

            try
            {
                for (int i = 0; i < _candles.Count; i++)
                {
                    var currentCandle = _candles[i];

                    // Record equity at this candle's close
                    decimal equity = _quoteBalance + _baseBalance * currentCandle.Close;
                    _equityCurve.Add(equity);

                    // Feed the strategy a single-candle tick
                    var tickData = new RequestKlineData.OHLCCandlesData
                    {
                        candles = [currentCandle]
                    };
                    await _strategy.Tick(tickData);
                }
            }
            finally
            {
                _strategy.OnBuy -= HandleBuy;
                _strategy.OnSell -= HandleSell;
            }

            // If still in a position at the end, force-close at last close
            if (_inPosition && _candles.Count > 0)
            {
                ForceClosePosition(_candles[^1]);
            }

            decimal finalEquity = _quoteBalance + _baseBalance * (_candles.Count > 0 ? _candles[^1].Close : 0);

            return BuildResult(initialEquity, finalEquity);
        }

        private void HandleBuy(object? sender, TradeSignalEventArgs args)
        {
            if (_inPosition) return; // Already in a position

            var candle = _candles[_equityCurve.Count - 1]; // Current candle being processed
            decimal executionPrice = candle.Close * (1 + _settings.SlippageFraction); // Slippage: buy higher

            decimal quoteToSpend = args.amountType == AmountType.Percentage
                ? _quoteBalance * (args.inputAmount / 100m)
                : args.inputAmount;

            if (quoteToSpend <= 0 || quoteToSpend > _quoteBalance) return;

            decimal fee = quoteToSpend * _settings.FeeFraction;
            decimal netQuote = quoteToSpend - fee;
            decimal quantity = netQuote / executionPrice;

            _quoteBalance -= quoteToSpend;
            _baseBalance += quantity;
            _totalFees += fee;

            _inPosition = true;
            _positionEntryPrice = executionPrice;
            _positionQuantity = quantity;
            _positionCost = quoteToSpend;
            _positionEntryFee = fee;
            _positionEntryTime = candle.Timestamp;
        }

        private void HandleSell(object? sender, TradeSignalEventArgs args)
        {
            if (!_inPosition) return; // Nothing to sell

            var candle = _candles[_equityCurve.Count - 1];
            decimal executionPrice = candle.Close * (1 - _settings.SlippageFraction); // Slippage: sell lower

            decimal quantityToSell = args.amountType == AmountType.Percentage
                ? _baseBalance * (args.inputAmount / 100m)
                : Math.Min(args.inputAmount, _baseBalance);

            if (quantityToSell <= 0) return;

            decimal grossProceeds = quantityToSell * executionPrice;
            decimal fee = grossProceeds * _settings.FeeFraction;
            decimal netProceeds = grossProceeds - fee;

            _baseBalance -= quantityToSell;
            _quoteBalance += netProceeds;
            _totalFees += fee;

            _trades.Add(new TradeRecord
            {
                EntryTime = _positionEntryTime,
                EntryPrice = _positionEntryPrice,
                EntryQuantity = _positionQuantity,
                EntryCost = _positionCost,
                EntryFee = _positionEntryFee,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = netProceeds,
                ExitFee = fee
            });

            _inPosition = false;
        }

        private void ForceClosePosition(RequestKlineData.OHLCCandle candle)
        {
            decimal executionPrice = candle.Close * (1 - _settings.SlippageFraction);
            decimal grossProceeds = _baseBalance * executionPrice;
            decimal fee = grossProceeds * _settings.FeeFraction;
            decimal netProceeds = grossProceeds - fee;

            _trades.Add(new TradeRecord
            {
                EntryTime = _positionEntryTime,
                EntryPrice = _positionEntryPrice,
                EntryQuantity = _positionQuantity,
                EntryCost = _positionCost,
                EntryFee = _positionEntryFee,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = netProceeds,
                ExitFee = fee
            });

            _quoteBalance += netProceeds;
            _totalFees += fee;
            _baseBalance = 0;
            _inPosition = false;
        }

        private OmniBacktestResult BuildResult(decimal initialEquity, decimal finalEquity)
        {
            var wins = _trades.Where(t => t.IsWin).ToList();
            var losses = _trades.Where(t => !t.IsWin).ToList();

            decimal totalWinAmount = wins.Sum(t => t.RealizedPnL);
            decimal totalLossAmount = losses.Sum(t => Math.Abs(t.RealizedPnL));

            // Max drawdown from equity curve
            decimal peak = 0;
            decimal maxDrawdown = 0;
            decimal maxDrawdownPercent = 0;
            foreach (decimal equity in _equityCurve)
            {
                if (equity > peak) peak = equity;
                decimal drawdown = peak - equity;
                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                    maxDrawdownPercent = peak == 0 ? 0 : drawdown / peak * 100;
                }
            }

            // Sharpe ratio (annualised, assuming each candle = 1 period)
            decimal sharpe = 0;
            if (_equityCurve.Count > 1)
            {
                var returns = new List<decimal>();
                for (int i = 1; i < _equityCurve.Count; i++)
                {
                    decimal prev = _equityCurve[i - 1];
                    if (prev != 0)
                        returns.Add((_equityCurve[i] - prev) / prev);
                }

                if (returns.Count > 0)
                {
                    decimal meanReturn = returns.Average();
                    decimal variance = returns.Sum(r => (r - meanReturn) * (r - meanReturn)) / returns.Count;
                    decimal stdDev = (decimal)Math.Sqrt((double)variance);
                    if (stdDev != 0)
                        sharpe = meanReturn / stdDev * (decimal)Math.Sqrt(returns.Count);
                }
            }

            // Buy & hold: what if all initial equity bought at first close and sold at last close
            decimal buyAndHoldPnLPercent = 0;
            if (_candles.Count >= 2 && _candles[0].Close != 0)
            {
                buyAndHoldPnLPercent = (_candles[^1].Close - _candles[0].Close) / _candles[0].Close * 100;
            }

            TimeSpan duration = _candles.Count >= 2
                ? _candles[^1].Timestamp - _candles[0].Timestamp
                : TimeSpan.Zero;

            return new OmniBacktestResult
            {
                InitialEquity = initialEquity,
                FinalEquity = finalEquity,
                FinalQuoteBalance = _quoteBalance,
                FinalBaseBalance = _baseBalance,
                TotalTrades = _trades.Count,
                WinningTrades = wins.Count,
                LosingTrades = losses.Count,
                TotalFeesPaid = _totalFees,
                AverageWin = wins.Count > 0 ? totalWinAmount / wins.Count : 0,
                AverageLoss = losses.Count > 0 ? totalLossAmount / losses.Count : 0,
                LargestWin = wins.Count > 0 ? wins.Max(t => t.RealizedPnL) : 0,
                LargestLoss = losses.Count > 0 ? losses.Min(t => t.RealizedPnL) : 0,
                ProfitFactor = totalLossAmount == 0 ? (totalWinAmount > 0 ? decimal.MaxValue : 0) : totalWinAmount / totalLossAmount,
                MaxDrawdown = maxDrawdown,
                MaxDrawdownPercent = maxDrawdownPercent,
                SharpeRatio = sharpe,
                BuyAndHoldPnLPercent = buyAndHoldPnLPercent,
                TotalCandles = _candles.Count,
                BacktestDuration = duration,
                Trades = _trades
            };
        }
    }
}
