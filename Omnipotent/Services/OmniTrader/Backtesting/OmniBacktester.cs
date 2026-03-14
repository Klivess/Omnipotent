using Omnipotent.Services.OmniTrader.Data;
using System.Reactive;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public class OmniBacktester
    {
        private readonly BacktestSettings _settings;
        private readonly OmniTraderStrategy _strategy;
        private readonly RequestKlineData.OHLCCandlesData _candles;

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

        // Pending stop-loss / take-profit levels (null = not set)
        private decimal? _stopLossPrice;
        private decimal? _takeProfitPrice;

        private readonly List<TradeRecord> _trades = [];
        private readonly List<decimal> _equityCurve = [];

        public OmniBacktester(OmniTraderStrategy strategy, RequestKlineData.OHLCCandlesData candles, BacktestSettings? settings = null)
        {
            _strategy = strategy;
            _candles = candles;
            _settings = settings ?? new BacktestSettings();
        }
        public OmniBacktester(OmniTraderStrategy strategy, List<RequestKlineData.OHLCCandle> candles, RequestKlineData.TimeInterval interval, BacktestSettings? settings = null)
        {
            _strategy = strategy;
            RequestKlineData.OHLCCandlesData candlesData = new(candles, interval);
            _candles = candlesData;
            _settings = settings ?? new BacktestSettings();
        }

        public async Task<OmniBacktestResult> RunAsync()
        {
            _quoteBalance = _settings.InitialQuoteBalance;
            _baseBalance = _settings.InitialBaseBalance;
            _totalFees = 0;
            _inPosition = false;
            _stopLossPrice = null;
            _takeProfitPrice = null;
            _trades.Clear();
            _equityCurve.Clear();



            decimal initialEquity = _quoteBalance + _baseBalance * (_candles.candles.Count > 0 ? _candles.candles[0].Close : 0);

            // Wire up strategy signals
            _strategy.OnBuy += HandleBuy;
            _strategy.OnSell += HandleSell;
            _strategy.OnStopLossUpdated += HandleStopLossUpdated;
            _strategy.OnTakeProfitUpdated += HandleTakeProfitUpdated;

            try
            {
                for (int i = 0; i < _candles.candles.Count; i++)
                {
                    var currentCandle = _candles.candles[i];

                    // Enforce pending SL/TP orders before the strategy sees this candle
                    if (_inPosition)
                        CheckStopLossTakeProfit(currentCandle);

                    // Record equity at this candle's close
                    decimal equity = _quoteBalance + _baseBalance * currentCandle.Close;
                    _equityCurve.Add(equity);

                    await _strategy.Tick(currentCandle);
                }
            }
            finally
            {
                _strategy.OnBuy -= HandleBuy;
                _strategy.OnSell -= HandleSell;
                _strategy.OnStopLossUpdated -= HandleStopLossUpdated;
                _strategy.OnTakeProfitUpdated -= HandleTakeProfitUpdated;
            }

            // If still in a position at the end, force-close at last close
            if (_inPosition && _candles.candles.Count > 0)
            {
                ForceClosePosition(_candles.candles[^1]);
            }

            decimal finalEquity = _quoteBalance + _baseBalance * (_candles.candles.Count > 0 ? _candles.candles[^1].Close : 0);

            var result = BuildResult(initialEquity, finalEquity);
            result.StartTime = _candles.startDate;
            result.EndTime = _candles.endDate;
            return result;
        }

        private void HandleStopLossUpdated(decimal price)
        {
            _stopLossPrice = price > 0 ? price : null;
        }

        private void HandleTakeProfitUpdated(decimal price)
        {
            _takeProfitPrice = price > 0 ? price : null;
        }

        private void CheckStopLossTakeProfit(RequestKlineData.OHLCCandle candle)
        {
            bool slHit = _stopLossPrice.HasValue && candle.Low <= _stopLossPrice.Value;
            bool tpHit = _takeProfitPrice.HasValue && candle.High >= _takeProfitPrice.Value;

            if (!slHit && !tpHit)
                return;

            // If both could trigger on the same bar, stop-loss takes priority (capital protection)
            if (slHit)
            {
                // Gap-through: if the candle opens below SL, fill at the (worse) open price
                decimal fillPrice = Math.Min(_stopLossPrice!.Value, candle.Open);
                ExecuteSLTPExit(candle, fillPrice);
                _strategy.NotifyStopLossTriggered(fillPrice);
            }
            else
            {
                // Gap-through: if the candle opens above TP, fill at the (better) open price
                decimal fillPrice = Math.Max(_takeProfitPrice!.Value, candle.Open);
                ExecuteSLTPExit(candle, fillPrice);
                _strategy.NotifyTakeProfitTriggered(fillPrice);
            }
        }

        private void ExecuteSLTPExit(RequestKlineData.OHLCCandle candle, decimal fillPrice)
        {
            decimal executionPrice = fillPrice * (1 - _settings.SlippageFraction);
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
            _stopLossPrice = null;
            _takeProfitPrice = null;
        }

        private void HandleBuy(object? sender, TradeSignalEventArgs args)
        {
            if (_inPosition) return; // Already in a position

            var candle = _candles.candles[_equityCurve.Count - 1]; // Current candle being processed
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
            _stopLossPrice = args.StopLossPrice;
            _takeProfitPrice = args.TakeProfitPrice;
        }

        private void HandleSell(object? sender, TradeSignalEventArgs args)
        {
            if (!_inPosition) return; // Nothing to sell

            var candle = _candles.candles[_equityCurve.Count - 1];
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
            _stopLossPrice = null;
            _takeProfitPrice = null;
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
            _stopLossPrice = null;
            _takeProfitPrice = null;
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
            if (_candles.candles.Count >= 2 && _candles.candles[0].Close != 0)
            {
                buyAndHoldPnLPercent = (_candles.candles[^1].Close - _candles.candles[0].Close) / _candles.candles[0].Close * 100;
            }

            TimeSpan duration = _candles.candles.Count >= 2
                ? _candles.candles[^1].Timestamp - _candles.candles[0].Timestamp
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
                TotalCandles = _candles.candles.Count,
                BacktestDuration = duration,
                Trades = _trades
            };
        }
    }
}
