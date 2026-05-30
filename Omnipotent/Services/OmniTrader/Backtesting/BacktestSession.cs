using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    /// <summary>
    /// Drives a strategy against a sequence of historical candles. Pure in-memory — no DB writes.
    /// Use this from the BacktestJobQueue worker, which is responsible for persistence.
    /// </summary>
    public sealed class BacktestSession
    {
        private readonly TradingStrategy strategy;
        private readonly IReadOnlyList<OHLCCandle> candles;
        private readonly BacktestConfig config;
        private readonly Func<double, int, int, Task>? onProgress;
        private readonly Func<Task<bool>>? cancellationCheck;
        private readonly Action<string>? log;

        public BacktestSession(
            TradingStrategy strategy,
            IReadOnlyList<OHLCCandle> candles,
            BacktestConfig config,
            Func<double, int, int, Task>? onProgress = null,
            Func<Task<bool>>? cancellationCheck = null,
            Action<string>? log = null)
        {
            this.strategy = strategy;
            this.candles = candles;
            this.config = config;
            this.onProgress = onProgress;
            this.cancellationCheck = cancellationCheck;
            this.log = log;
        }

        public async Task<BacktestResult> RunAsync(CancellationToken ct = default)
        {
            var simState = new SimulatedOrderRouter.State
            {
                QuoteBalance = config.InitialQuoteBalance,
                BaseBalance = config.InitialBaseBalance,
                FeeFraction = config.FeeFraction,
                SlippageFraction = config.SlippageFraction
            };

            var trades = new List<TradeRecord>();
            decimal? entryPrice = null;
            DateTime entryTime = default;
            decimal entryQty = 0;
            bool entryShort = false;
            decimal accruedFees = 0;

            async Task OnFillAsync(FillEvent fill)
            {
                // Reconstruct round-trip trades when position flattens.
                if (simState.Position == null && entryPrice.HasValue)
                {
                    trades.Add(new TradeRecord
                    {
                        EntryTime = entryTime,
                        ExitTime = fill.FilledUtc,
                        EntryPrice = entryPrice.Value,
                        ExitPrice = fill.Price,
                        Qty = entryQty,
                        IsShort = entryShort,
                        Fees = accruedFees + fill.Fee
                    });
                    entryPrice = null;
                    accruedFees = 0;
                }
                else if (entryPrice == null && simState.Position != null)
                {
                    entryPrice = simState.Position.AveragePrice;
                    entryTime = fill.FilledUtc;
                    entryQty = Math.Abs(simState.Position.Qty);
                    entryShort = simState.Position.IsShort;
                    accruedFees = fill.Fee;
                }
                else
                {
                    accruedFees += fill.Fee;
                }
                try { await strategy.OnOrderFilled(fill, ct); } catch { }
            }

            var router = new SimulatedOrderRouter(simState, OnFillAsync);
            var equityCurve = new List<EquityPoint>(candles.Count);

            var context = new StrategyContext
            {
                Host = new Sessions.StrategyHost(
                    "backtest", SessionMode.Backtest, config.Coin + config.Currency, config.Interval,
                    (req, c) => router.PlaceOrderAsync("backtest", req, c),
                    async (intentId, c) =>
                    {
                        var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId);
                        if (match != null) await router.CancelOrderAsync(match, c);
                    },
                    () => simState.Position,
                    () => simState.QuoteBalance,
                    () => simState.BaseBalance,
                    m => log?.Invoke(m),
                    (m, e) => log?.Invoke($"ERR: {m} {e?.Message}"))
            };
            strategy.Attach(context);

            DateTime startTs = candles.Count > 0 ? candles[0].Timestamp : DateTime.UtcNow;
            try { await strategy.OnStart(ct); } catch { }

            int total = candles.Count;
            int lastProgressEmit = -1;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (cancellationCheck != null && await cancellationCheck())
                    throw new OperationCanceledException("Backtest cancelled by user");

                var candle = candles[i];
                router.UpdateLastCandle(candle);
                context.CandleHistory.Add(candle);
                try { await router.OnCandleAsync(candle, ct); } catch { }
                try { await strategy.OnCandleClose(candle, ct); } catch { }

                decimal equity = simState.QuoteBalance + simState.BaseBalance * candle.Close;
                equityCurve.Add(new EquityPoint
                {
                    Ts = candle.Timestamp,
                    MarkPrice = candle.Close,
                    QuoteBalance = simState.QuoteBalance,
                    BaseBalance = simState.BaseBalance,
                    Equity = equity
                });

                int pct = total == 0 ? 100 : (i + 1) * 100 / total;
                if (pct != lastProgressEmit && onProgress != null)
                {
                    lastProgressEmit = pct;
                    await onProgress(pct, i + 1, total);
                }
            }

            try { await strategy.OnStop(ct); } catch { }

            decimal finalMark = candles.Count > 0 ? candles[^1].Close : 0;
            decimal initialEquity = config.InitialQuoteBalance + config.InitialBaseBalance * (candles.Count > 0 ? candles[0].Close : 0);
            decimal finalEquity = simState.QuoteBalance + simState.BaseBalance * finalMark;

            var wins = trades.Where(t => t.IsWin).ToList();
            var losses = trades.Where(t => !t.IsWin).ToList();
            var (maxDD, maxDDPct) = BacktestMetrics.MaxDrawdown(equityCurve);

            return new BacktestResult
            {
                InitialEquity = initialEquity,
                FinalEquity = finalEquity,
                FinalQuoteBalance = simState.QuoteBalance,
                FinalBaseBalance = simState.BaseBalance,
                TotalTrades = trades.Count,
                WinningTrades = wins.Count,
                LosingTrades = losses.Count,
                TotalFeesPaid = simState.Fees,
                AverageWin = wins.Count > 0 ? wins.Average(t => t.RealizedPnL) : 0,
                AverageLoss = losses.Count > 0 ? losses.Average(t => Math.Abs(t.RealizedPnL)) : 0,
                LargestWin = wins.Count > 0 ? wins.Max(t => t.RealizedPnL) : 0,
                LargestLoss = losses.Count > 0 ? losses.Min(t => t.RealizedPnL) : 0,
                ProfitFactor = BacktestMetrics.ProfitFactor(trades),
                MaxDrawdown = maxDD,
                MaxDrawdownPercent = maxDDPct,
                SharpeRatio = BacktestMetrics.Sharpe(equityCurve),
                BuyAndHoldPnLPercent = BacktestMetrics.BuyAndHoldPnLPercent(candles),
                TotalCandles = candles.Count,
                Duration = candles.Count > 0 ? candles[^1].Timestamp - candles[0].Timestamp : TimeSpan.Zero,
                StartTime = candles.Count > 0 ? candles[0].Timestamp : default,
                EndTime = candles.Count > 0 ? candles[^1].Timestamp : default,
                Trades = trades,
                EquityCurve = equityCurve,
                Candles = candles.ToList()
            };
        }
    }
}
