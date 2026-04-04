using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public class OmniBacktester
    {
        private enum PositionSide
        {
            None,
            Long,
            Short
        }

        private readonly BacktestSettings _settings;
        private readonly OmniTraderStrategy _strategy;
        private readonly OmniTraderFinanceData.OHLCCandlesData _candles;

        private decimal _quoteBalance;
        private decimal _baseBalance;
        private decimal _totalFees;

        // Open position tracking
        private PositionSide _positionSide;
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

        public OmniBacktester(OmniTraderStrategy strategy, OmniTraderFinanceData.OHLCCandlesData candles, BacktestSettings? settings = null)
        {
            _strategy = strategy;
            _candles = candles;
            _settings = settings ?? new BacktestSettings();
        }
        public OmniBacktester(OmniTraderStrategy strategy, List<OmniTraderFinanceData.OHLCCandle> candles, OmniTraderFinanceData.TimeInterval interval, BacktestSettings? settings = null)
        {
            _strategy = strategy;
            OmniTraderFinanceData.OHLCCandlesData candlesData = new(candles, interval);
            _candles = candlesData;
            _settings = settings ?? new BacktestSettings();
        }

        public async Task<OmniBacktestResult> RunAsync()
        {
            await _strategy.PrepareForSession(TradeSessionType.Backtester);

            _quoteBalance = _settings.InitialQuoteBalance;
            _baseBalance = _settings.InitialBaseBalance;
            _totalFees = 0;
            _positionSide = PositionSide.None;
            _positionEntryPrice = 0;
            _positionQuantity = 0;
            _positionCost = 0;
            _positionEntryFee = 0;
            _positionEntryTime = default;
            _stopLossPrice = null;
            _takeProfitPrice = null;
            _trades.Clear();
            _equityCurve.Clear();

            decimal initialEquity = _quoteBalance + _baseBalance * (_candles.candles.Count > 0 ? _candles.candles[0].Close : 0);

            // Wire up strategy signals
            _strategy.OnLong += HandleLong;
            _strategy.OnSell += HandleSell;
            _strategy.OnShort += HandleShort;
            _strategy.ClosePosition += HandleClosePosition;

            try
            {
                for (int i = 0; i < _candles.candles.Count; i++)
                {
                    var currentCandle = _candles.candles[i];

                    // Enforce pending SL/TP orders before the strategy sees this candle
                    if (HasOpenPosition())
                        CheckStopLossTakeProfit(currentCandle);

                    // Record equity at this candle's close
                    decimal equity = _quoteBalance + _baseBalance * currentCandle.Close;
                    _equityCurve.Add(equity);

                    await _strategy.CandleClose(currentCandle);
                }
            }
            finally
            {
                _strategy.OnLong -= HandleLong;
                _strategy.OnSell -= HandleSell;
                _strategy.OnShort -= HandleShort;
                _strategy.ClosePosition -= HandleClosePosition;
            }

            // If still in a position at the end, force-close at last close
            if (HasOpenPosition() && _candles.candles.Count > 0)
            {
                ForceClosePosition(_candles.candles[^1]);
            }

            decimal finalEquity = _quoteBalance + _baseBalance * (_candles.candles.Count > 0 ? _candles.candles[^1].Close : 0);

            var result = BuildResult(initialEquity, finalEquity);
            result.StartTime = _candles.startDate;
            result.EndTime = _candles.endDate;
            return result;
        }

        private bool HasOpenPosition() => _positionSide != PositionSide.None && _positionQuantity > 0;

        private void CheckStopLossTakeProfit(OmniTraderFinanceData.OHLCCandle candle)
        {
            bool slHit;
            bool tpHit;

            if (_positionSide == PositionSide.Short)
            {
                slHit = _stopLossPrice.HasValue && candle.High >= _stopLossPrice.Value;
                tpHit = _takeProfitPrice.HasValue && candle.Low <= _takeProfitPrice.Value;
            }
            else
            {
                slHit = _stopLossPrice.HasValue && candle.Low <= _stopLossPrice.Value;
                tpHit = _takeProfitPrice.HasValue && candle.High >= _takeProfitPrice.Value;
            }

            if (!slHit && !tpHit)
                return;

            // If both could trigger on the same bar, stop-loss takes priority (capital protection)
            if (slHit)
            {
                decimal fillPrice = _positionSide == PositionSide.Short
                    ? Math.Max(_stopLossPrice!.Value, candle.Open)
                    : Math.Min(_stopLossPrice!.Value, candle.Open);

                ExecuteSLTPExit(candle, fillPrice);
                _strategy.NotifyStopLossTriggered(fillPrice);
            }
            else
            {
                decimal fillPrice = _positionSide == PositionSide.Short
                    ? Math.Min(_takeProfitPrice!.Value, candle.Open)
                    : Math.Max(_takeProfitPrice!.Value, candle.Open);

                ExecuteSLTPExit(candle, fillPrice);
                _strategy.NotifyTakeProfitTriggered(fillPrice);
            }
        }

        private void ExecuteSLTPExit(OmniTraderFinanceData.OHLCCandle candle, decimal fillPrice)
        {
            if (_positionSide == PositionSide.Short)
                CloseShortQuantity(candle, _positionQuantity, fillPrice);
            else
                CloseLongQuantity(candle, _positionQuantity, fillPrice);
        }

        private OmniTraderFinanceData.OHLCCandle GetCurrentCandle()
        {
            int index = Math.Max(0, _equityCurve.Count - 1);
            return _candles.candles[index];
        }

        private void HandleLong(object? sender, TradeSignalEventArgs args)
        {
            if (HasOpenPosition())
                return;

            var candle = GetCurrentCandle();
            decimal executionPrice = candle.Close * (1 + _settings.SlippageFraction);

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

            _positionSide = PositionSide.Long;
            _positionEntryPrice = executionPrice;
            _positionQuantity = quantity;
            _positionCost = quoteToSpend;
            _positionEntryFee = fee;
            _positionEntryTime = candle.Timestamp;
            _stopLossPrice = args.StopLossPrice;
            _takeProfitPrice = args.TakeProfitPrice;
        }

        private void HandleShort(object? sender, TradeSignalEventArgs args)
        {
            if (HasOpenPosition())
                return;

            var candle = GetCurrentCandle();
            decimal executionPrice = candle.Close * (1 - _settings.SlippageFraction);

            decimal quoteNotional = args.amountType == AmountType.Percentage
                ? _quoteBalance * (args.inputAmount / 100m)
                : args.inputAmount;

            if (quoteNotional <= 0)
                return;

            decimal quantityToShort = quoteNotional / executionPrice;
            decimal fee = quoteNotional * _settings.FeeFraction;
            decimal netProceeds = quoteNotional - fee;

            _baseBalance -= quantityToShort;
            _quoteBalance += netProceeds;
            _totalFees += fee;

            _positionSide = PositionSide.Short;
            _positionEntryPrice = executionPrice;
            _positionQuantity = quantityToShort;
            _positionCost = netProceeds;
            _positionEntryFee = fee;
            _positionEntryTime = candle.Timestamp;
            _stopLossPrice = args.StopLossPrice;
            _takeProfitPrice = args.TakeProfitPrice;
        }

        private void HandleSell(object? sender, TradeSignalEventArgs args)
        {
            if (!HasOpenPosition())
                return;

            var candle = GetCurrentCandle();

            decimal quantityToClose = args.amountType == AmountType.Percentage
                ? _positionQuantity * (args.inputAmount / 100m)
                : Math.Min(args.inputAmount, _positionQuantity);

            if (quantityToClose <= 0)
                return;

            if (_positionSide == PositionSide.Short)
                CloseShortQuantity(candle, quantityToClose, candle.Close);
            else
                CloseLongQuantity(candle, quantityToClose, candle.Close);
        }

        private void HandleClosePosition(object? sender, TradePosition position)
        {
            if (!HasOpenPosition())
                return;

            var candle = GetCurrentCandle();
            if (_positionSide == PositionSide.Short)
                CloseShortQuantity(candle, _positionQuantity, candle.Close);
            else
                CloseLongQuantity(candle, _positionQuantity, candle.Close);
        }

        private void CloseLongQuantity(OmniTraderFinanceData.OHLCCandle candle, decimal quantityToClose, decimal fillPrice)
        {
            decimal executionPrice = fillPrice * (1 - _settings.SlippageFraction);
            decimal ratio = _positionQuantity == 0 ? 1 : quantityToClose / _positionQuantity;
            ratio = Math.Clamp(ratio, 0, 1);

            decimal entryCostShare = _positionCost * ratio;
            decimal entryFeeShare = _positionEntryFee * ratio;

            decimal grossProceeds = quantityToClose * executionPrice;
            decimal fee = grossProceeds * _settings.FeeFraction;
            decimal netProceeds = grossProceeds - fee;

            _trades.Add(new TradeRecord
            {
                IsShort = false,
                EntryTime = _positionEntryTime,
                EntryPrice = _positionEntryPrice,
                EntryQuantity = quantityToClose,
                EntryCost = entryCostShare,
                EntryFee = entryFeeShare,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = netProceeds,
                ExitFee = fee
            });

            _baseBalance -= quantityToClose;
            _quoteBalance += netProceeds;
            _totalFees += fee;

            _positionQuantity -= quantityToClose;
            _positionCost -= entryCostShare;
            _positionEntryFee -= entryFeeShare;

            if (_positionQuantity <= 0)
                ResetOpenPosition();
        }

        private void CloseShortQuantity(OmniTraderFinanceData.OHLCCandle candle, decimal quantityToClose, decimal fillPrice)
        {
            decimal executionPrice = fillPrice * (1 + _settings.SlippageFraction);
            decimal ratio = _positionQuantity == 0 ? 1 : quantityToClose / _positionQuantity;
            ratio = Math.Clamp(ratio, 0, 1);

            decimal entryProceedsShare = _positionCost * ratio;
            decimal entryFeeShare = _positionEntryFee * ratio;

            decimal grossCost = quantityToClose * executionPrice;
            decimal fee = grossCost * _settings.FeeFraction;
            decimal totalCoverCost = grossCost + fee;

            _trades.Add(new TradeRecord
            {
                IsShort = true,
                EntryTime = _positionEntryTime,
                EntryPrice = _positionEntryPrice,
                EntryQuantity = quantityToClose,
                EntryCost = entryProceedsShare,
                EntryFee = entryFeeShare,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = totalCoverCost,
                ExitFee = fee
            });

            _baseBalance += quantityToClose;
            _quoteBalance -= totalCoverCost;
            _totalFees += fee;

            _positionQuantity -= quantityToClose;
            _positionCost -= entryProceedsShare;
            _positionEntryFee -= entryFeeShare;

            if (_positionQuantity <= 0)
                ResetOpenPosition();
        }

        private void ResetOpenPosition()
        {
            _positionSide = PositionSide.None;
            _positionEntryPrice = 0;
            _positionQuantity = 0;
            _positionCost = 0;
            _positionEntryFee = 0;
            _positionEntryTime = default;
            _stopLossPrice = null;
            _takeProfitPrice = null;
        }

        private void ForceClosePosition(OmniTraderFinanceData.OHLCCandle candle)
        {
            if (_positionSide == PositionSide.Short)
                CloseShortQuantity(candle, _positionQuantity, candle.Close);
            else
                CloseLongQuantity(candle, _positionQuantity, candle.Close);
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
