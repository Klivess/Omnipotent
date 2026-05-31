using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    /// <summary>A point on a coin's daily market-cap series (used for point-in-time universe ranking).</summary>
    public readonly record struct MarketCapPoint(DateTime Date, decimal MarketCap);

    /// <summary>
    /// Inputs for a multi-asset (cross-sectional) backtest. <see cref="Candles"/> maps symbol → its
    /// daily candle series; <see cref="MarketCaps"/> maps symbol → its daily market-cap series (may be
    /// empty for a symbol with no cap data). <see cref="RegimeSymbol"/> is the asset used for the
    /// buy-and-hold benchmark and as the default regime asset (e.g. BTCUSD).
    /// </summary>
    public sealed class PortfolioInput
    {
        public required IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> Candles { get; init; }
        public IReadOnlyDictionary<string, IReadOnlyList<MarketCapPoint>> MarketCaps { get; init; }
            = new Dictionary<string, IReadOnlyList<MarketCapPoint>>();
        public string? RegimeSymbol { get; init; }
    }

    /// <summary>
    /// Drives a strategy against a sequence of historical candles. Pure in-memory — no DB writes.
    /// Use this from the BacktestJobQueue worker, which is responsible for persistence.
    /// </summary>
    public sealed class BacktestSession
    {
        private readonly TradingStrategy strategy;
        private readonly IReadOnlyList<OHLCCandle> candles;
        private readonly PortfolioInput? portfolioInput;
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

        /// <summary>Multi-asset (portfolio) constructor. Drive it with <see cref="RunPortfolioAsync"/>.</summary>
        public BacktestSession(
            TradingStrategy strategy,
            PortfolioInput portfolioInput,
            BacktestConfig config,
            Func<double, int, int, Task>? onProgress = null,
            Func<Task<bool>>? cancellationCheck = null,
            Action<string>? log = null)
        {
            this.strategy = strategy;
            this.portfolioInput = portfolioInput;
            this.candles = Array.Empty<OHLCCandle>();
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
                SlippageFraction = config.SlippageFraction,
                Leverage = config.Margin.ClampedLeverage,
                LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                SecondsPerBar = (int)config.Interval * 60
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

            var router = new SimulatedOrderRouter(simState, OnFillAsync, m => log?.Invoke(m));
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
                    (m, e) => log?.Invoke($"ERR: {m} {e?.Message}"),
                    config.Margin.ClampedLeverage)
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

        // ══ Portfolio (cross-sectional) run ══════════════════════════════════════

        private sealed class OpenLeg
        {
            public decimal? EntryPrice;
            public DateTime EntryTime;
            public decimal EntryQty;
            public bool EntryShort;
            public decimal AccruedFees;
        }

        /// <summary>
        /// Drives a multi-asset strategy over a synchronized daily timeline (the union of every
        /// symbol's candle timestamps). Each bar: update marks for present symbols, accrue margin
        /// borrow/liquidation, extend each symbol's point-in-time history, hand the strategy a
        /// <see cref="PortfolioBar"/>, then record one portfolio equity point. Reuses the same
        /// <see cref="SimulatedOrderRouter"/> (in portfolio mode) and <see cref="BacktestMetrics"/>.
        /// </summary>
        public async Task<BacktestResult> RunPortfolioAsync(CancellationToken ct = default)
        {
            if (portfolioInput == null) throw new InvalidOperationException("RunPortfolioAsync requires the portfolio constructor.");
            var input = portfolioInput;

            var simState = new SimulatedOrderRouter.State
            {
                PortfolioMode = true,
                QuoteBalance = config.InitialQuoteBalance,
                BaseBalance = 0m,
                FeeFraction = config.FeeFraction,
                SlippageFraction = config.SlippageFraction,
                Leverage = config.Margin.ClampedLeverage,
                LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                SecondsPerBar = (int)config.Interval * 60
            };

            var trades = new List<TradeRecord>();
            var legs = new Dictionary<string, OpenLeg>(StringComparer.OrdinalIgnoreCase);

            async Task OnFillAsync(FillEvent fill)
            {
                string sym = fill.Symbol;
                if (!legs.TryGetValue(sym, out var leg)) { leg = new OpenLeg(); legs[sym] = leg; }
                bool flatNow = !simState.Positions.ContainsKey(sym);

                if (flatNow && leg.EntryPrice.HasValue)
                {
                    trades.Add(new TradeRecord
                    {
                        EntryTime = leg.EntryTime,
                        ExitTime = fill.FilledUtc,
                        EntryPrice = leg.EntryPrice.Value,
                        ExitPrice = fill.Price,
                        Qty = leg.EntryQty,
                        IsShort = leg.EntryShort,
                        Fees = leg.AccruedFees + fill.Fee
                    });
                    leg.EntryPrice = null;
                    leg.AccruedFees = 0m;
                }
                else if (leg.EntryPrice == null && !flatNow)
                {
                    var pos = simState.Positions[sym];
                    leg.EntryPrice = pos.AveragePrice;
                    leg.EntryTime = fill.FilledUtc;
                    leg.EntryQty = Math.Abs(pos.Qty);
                    leg.EntryShort = pos.IsShort;
                    leg.AccruedFees = fill.Fee;
                }
                else
                {
                    leg.AccruedFees += fill.Fee;
                }
                try { await strategy.OnOrderFilled(fill, ct); } catch { }
            }

            var router = new SimulatedOrderRouter(simState, OnFillAsync, m => log?.Invoke(m));

            var host = new Sessions.StrategyHost(
                "backtest", SessionMode.Backtest, input.RegimeSymbol ?? "PORTFOLIO", config.Interval,
                (req, c) => router.PlaceOrderAsync("backtest", req, c),
                async (intentId, c) =>
                {
                    var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId);
                    if (match != null) await router.CancelOrderAsync(match, c);
                },
                () => null,
                () => simState.QuoteBalance,
                () => 0m,
                m => log?.Invoke(m),
                (m, e) => log?.Invoke($"ERR: {m} {e?.Message}"),
                config.Margin.ClampedLeverage,
                portfolioPositionsFunc: () => simState.NonZeroPositions(),
                equityFunc: () => simState.PortfolioEquity());
            var context = new StrategyContext { Host = host };
            strategy.Attach(context);

            // Build the master daily timeline (union of all symbols' candle timestamps).
            var timeline = new SortedSet<DateTime>();
            foreach (var series in input.Candles.Values)
                foreach (var c in series) timeline.Add(c.Timestamp);
            var bars = timeline.ToList();

            // Per-symbol fast lookups: ts → candle, growing history, and as-of market-cap cursor.
            var byTs = new Dictionary<string, Dictionary<DateTime, OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var histories = new Dictionary<string, List<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var historyViews = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, series) in input.Candles)
            {
                var map = new Dictionary<DateTime, OHLCCandle>(series.Count);
                foreach (var c in series) map[c.Timestamp] = c;
                byTs[sym] = map;
                var h = new List<OHLCCandle>(series.Count);
                histories[sym] = h;
                historyViews[sym] = h;
            }
            var capCursor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in input.MarketCaps.Keys) capCursor[sym] = 0;

            try { await strategy.OnStart(ct); } catch { }

            var equityCurve = new List<EquityPoint>(bars.Count);
            int total = bars.Count;
            int lastProgressEmit = -1;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (cancellationCheck != null && await cancellationCheck())
                    throw new OperationCanceledException("Backtest cancelled by user");

                DateTime t = bars[i];

                // Symbols trading this bar.
                var barCandles = new Dictionary<string, OHLCCandle>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, map) in byTs)
                    if (map.TryGetValue(t, out var c)) barCandles[sym] = c;

                // Marks + margin/liquidation + conditional fills, then extend histories.
                try { await router.OnPortfolioCandlesAsync(barCandles, ct); } catch { }
                foreach (var (sym, c) in barCandles) histories[sym].Add(c);

                // As-of market caps for this bar.
                var caps = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, capSeries) in input.MarketCaps)
                {
                    int cur = capCursor[sym];
                    while (cur + 1 < capSeries.Count && capSeries[cur + 1].Date <= t) cur++;
                    capCursor[sym] = cur;
                    if (capSeries.Count > 0 && capSeries[cur].Date <= t) caps[sym] = capSeries[cur].MarketCap;
                }

                var presentHistories = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sym in barCandles.Keys) presentHistories[sym] = historyViews[sym];

                var pbar = new PortfolioBar { T = t, Histories = presentHistories, MarketCaps = caps };
                try { await strategy.OnUniverseBar(pbar, ct); } catch (Exception ex) { log?.Invoke($"ERR: OnUniverseBar {ex.Message}"); }

                decimal equity = simState.PortfolioEquity();
                equityCurve.Add(new EquityPoint
                {
                    Ts = t,
                    MarkPrice = input.RegimeSymbol != null && barCandles.TryGetValue(input.RegimeSymbol, out var rc) ? rc.Close : 0m,
                    QuoteBalance = simState.QuoteBalance,
                    BaseBalance = simState.GrossNotional(),
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

            decimal initialEquity = config.InitialQuoteBalance;
            decimal finalEquity = simState.PortfolioEquity();

            var winsP = trades.Where(t => t.IsWin).ToList();
            var lossesP = trades.Where(t => !t.IsWin).ToList();
            var (maxDDp, maxDDpPct) = BacktestMetrics.MaxDrawdown(equityCurve);

            // Benchmark candles = the regime asset's series (BTC by default), if present.
            var benchCandles = input.RegimeSymbol != null && input.Candles.TryGetValue(input.RegimeSymbol, out var bc)
                ? bc.ToList() : new List<OHLCCandle>();

            return new BacktestResult
            {
                InitialEquity = initialEquity,
                FinalEquity = finalEquity,
                FinalQuoteBalance = simState.QuoteBalance,
                FinalBaseBalance = simState.GrossNotional(),
                TotalTrades = trades.Count,
                WinningTrades = winsP.Count,
                LosingTrades = lossesP.Count,
                TotalFeesPaid = simState.Fees,
                AverageWin = winsP.Count > 0 ? winsP.Average(t => t.RealizedPnL) : 0,
                AverageLoss = lossesP.Count > 0 ? lossesP.Average(t => Math.Abs(t.RealizedPnL)) : 0,
                LargestWin = winsP.Count > 0 ? winsP.Max(t => t.RealizedPnL) : 0,
                LargestLoss = lossesP.Count > 0 ? lossesP.Min(t => t.RealizedPnL) : 0,
                ProfitFactor = BacktestMetrics.ProfitFactor(trades),
                MaxDrawdown = maxDDp,
                MaxDrawdownPercent = maxDDpPct,
                SharpeRatio = BacktestMetrics.Sharpe(equityCurve),
                BuyAndHoldPnLPercent = BacktestMetrics.BuyAndHoldPnLPercent(benchCandles),
                TotalCandles = bars.Count,
                Duration = bars.Count > 0 ? bars[^1] - bars[0] : TimeSpan.Zero,
                StartTime = bars.Count > 0 ? bars[0] : default,
                EndTime = bars.Count > 0 ? bars[^1] : default,
                Trades = trades,
                EquityCurve = equityCurve,
                Candles = benchCandles
            };
        }
    }
}
