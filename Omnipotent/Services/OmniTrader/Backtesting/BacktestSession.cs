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
    /// The OmniTrader backtester. It is multi-asset-native: one engine drives a portfolio of N symbols
    /// over a synchronized timeline, with single-symbol backtests being simply the N=1 case. Both
    /// <see cref="RunAsync"/> (single-symbol, dispatches <c>OnCandleClose</c>) and
    /// <see cref="RunPortfolioAsync"/> (cross-sectional, dispatches <c>OnUniverseBar</c>) are thin
    /// adapters over the same <see cref="RunCoreAsync"/> core, the same <see cref="SimulatedOrderRouter"/>
    /// (in portfolio mode), and the same <see cref="BacktestMetrics"/>/<see cref="BacktestResult"/>.
    /// Pure in-memory — no DB writes; the BacktestJobQueue worker handles persistence.
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

        /// <summary>Single-symbol backtest — the N=1 case of the multi-asset engine.</summary>
        public Task<BacktestResult> RunAsync(CancellationToken ct = default)
        {
            string symbol = config.Coin + config.Currency;
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>> { [symbol] = candles },
                RegimeSymbol = symbol,
            };
            return RunCoreAsync(input, singleSymbol: symbol, ct);
        }

        /// <summary>Multi-asset (cross-sectional) backtest.</summary>
        public Task<BacktestResult> RunPortfolioAsync(CancellationToken ct = default)
        {
            if (portfolioInput == null) throw new InvalidOperationException("RunPortfolioAsync requires the portfolio constructor.");
            return RunCoreAsync(portfolioInput, singleSymbol: null, ct);
        }

        private sealed class OpenLeg
        {
            public decimal? EntryPrice;
            public DateTime EntryTime;
            public decimal EntryQty;
            public bool EntryShort;
            public decimal AccruedFees;
        }

        /// <summary>
        /// The one backtest engine. Drives the strategy over the union of every symbol's candle
        /// timestamps. Each bar: mark + margin/liquidation + conditional fills via the router, extend
        /// per-symbol histories, dispatch to the strategy (<c>OnCandleClose</c> for single-symbol,
        /// <c>OnUniverseBar</c> for portfolio), then record one equity point.
        /// </summary>
        private async Task<BacktestResult> RunCoreAsync(PortfolioInput input, string? singleSymbol, CancellationToken ct)
        {
            bool isSingle = singleSymbol != null;

            var simState = new SimulatedOrderRouter.State
            {
                PortfolioMode = true,
                QuoteBalance = config.InitialQuoteBalance,
                FeeFraction = config.FeeFraction,
                SlippageFraction = config.SlippageFraction,
                Leverage = config.Margin.ClampedLeverage,
                LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                SecondsPerBar = (int)config.Interval * 60,
            };
            // Single-symbol backtests may start holding the base asset.
            if (isSingle && config.InitialBaseBalance != 0m)
                simState.BaseBalances[singleSymbol!] = config.InitialBaseBalance;

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
                        Fees = leg.AccruedFees + fill.Fee,
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

            // Host wiring: in single-symbol mode the single-symbol accessors map onto that one book.
            var host = new Sessions.StrategyHost(
                "backtest", SessionMode.Backtest, singleSymbol ?? input.RegimeSymbol ?? "PORTFOLIO", config.Interval,
                (req, c) => router.PlaceOrderAsync("backtest", req, c),
                async (intentId, c) =>
                {
                    var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId);
                    if (match != null) await router.CancelOrderAsync(match, c);
                },
                positionFunc: isSingle
                    ? () => simState.Positions.TryGetValue(singleSymbol!, out var p) ? p : null
                    : () => null,
                quoteFunc: () => simState.QuoteBalance,
                baseFunc: isSingle ? () => simState.GetBaseBalance(singleSymbol!) : () => 0m,
                m => log?.Invoke(m),
                (m, e) => log?.Invoke($"ERR: {m} {e?.Message}"),
                config.Margin.ClampedLeverage,
                portfolioPositionsFunc: () => simState.NonZeroPositions(),
                equityFunc: () => simState.PortfolioEquity());
            var context = new StrategyContext { Host = host };
            strategy.Attach(context);

            // Per-symbol candle lookups + growing point-in-time histories.
            var timeline = new SortedSet<DateTime>();
            foreach (var series in input.Candles.Values)
                foreach (var c in series) timeline.Add(c.Timestamp);
            var bars = timeline.ToList();

            var byTs = new Dictionary<string, Dictionary<DateTime, OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var histories = new Dictionary<string, List<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var historyViews = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, series) in input.Candles)
            {
                var map = new Dictionary<DateTime, OHLCCandle>(series.Count);
                foreach (var c in series) map[c.Timestamp] = c;
                byTs[sym] = map;
                // Single-symbol strategies read Ctx.CandleHistory, so back that symbol's history with it.
                var h = isSingle && sym == singleSymbol ? context.CandleHistory : new List<OHLCCandle>(series.Count);
                histories[sym] = h;
                historyViews[sym] = h;
            }
            var capCursor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in input.MarketCaps.Keys) capCursor[sym] = 0;

            try { await strategy.OnStart(ct); } catch { }

            var equityCurve = new List<EquityPoint>(bars.Count);
            int total = bars.Count;
            int lastProgressEmit = -1;
            decimal lastSingleClose = 0m;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (cancellationCheck != null && await cancellationCheck())
                    throw new OperationCanceledException("Backtest cancelled by user");

                DateTime t = bars[i];

                var barCandles = new Dictionary<string, OHLCCandle>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, map) in byTs)
                    if (map.TryGetValue(t, out var c)) barCandles[sym] = c;

                // Router per-bar: marks, margin borrow + intrabar liquidation, conditional-order fills.
                try { await router.OnPortfolioCandlesAsync(barCandles, ct); } catch { }
                foreach (var (sym, c) in barCandles) histories[sym].Add(c);

                if (isSingle)
                {
                    if (barCandles.TryGetValue(singleSymbol!, out var candle))
                    {
                        lastSingleClose = candle.Close;
                        try { await strategy.OnCandleClose(candle, ct); }
                        catch (Exception ex) { log?.Invoke($"ERR: OnCandleClose {ex.Message}"); }
                    }
                }
                else
                {
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
                    try { await strategy.OnUniverseBar(pbar, ct); }
                    catch (Exception ex) { log?.Invoke($"ERR: OnUniverseBar {ex.Message}"); }
                }

                decimal equity = simState.PortfolioEquity();
                equityCurve.Add(new EquityPoint
                {
                    Ts = t,
                    MarkPrice = isSingle
                        ? lastSingleClose
                        : (input.RegimeSymbol != null && barCandles.TryGetValue(input.RegimeSymbol, out var rc) ? rc.Close : 0m),
                    QuoteBalance = simState.QuoteBalance,
                    BaseBalance = isSingle ? simState.GetBaseBalance(singleSymbol!) : simState.GrossNotional(),
                    Equity = equity,
                });

                int pct = total == 0 ? 100 : (i + 1) * 100 / total;
                if (pct != lastProgressEmit && onProgress != null)
                {
                    lastProgressEmit = pct;
                    await onProgress(pct, i + 1, total);
                }
            }

            try { await strategy.OnStop(ct); } catch { }

            // Benchmark candles = the single symbol's series, or the regime asset's for a portfolio.
            var benchCandles = isSingle
                ? candles.ToList()
                : (input.RegimeSymbol != null && input.Candles.TryGetValue(input.RegimeSymbol, out var bc) ? bc.ToList() : new List<OHLCCandle>());

            decimal firstClose = benchCandles.Count > 0 ? benchCandles[0].Close : 0m;
            decimal initialEquity = isSingle
                ? config.InitialQuoteBalance + config.InitialBaseBalance * firstClose
                : config.InitialQuoteBalance;
            decimal finalEquity = simState.PortfolioEquity();

            var wins = trades.Where(t => t.IsWin).ToList();
            var losses = trades.Where(t => !t.IsWin).ToList();
            var (maxDD, maxDDPct) = BacktestMetrics.MaxDrawdown(equityCurve);

            return new BacktestResult
            {
                InitialEquity = initialEquity,
                FinalEquity = finalEquity,
                FinalQuoteBalance = simState.QuoteBalance,
                FinalBaseBalance = isSingle ? simState.GetBaseBalance(singleSymbol!) : simState.GrossNotional(),
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
                BuyAndHoldPnLPercent = BacktestMetrics.BuyAndHoldPnLPercent(benchCandles),
                TotalCandles = isSingle ? candles.Count : bars.Count,
                Duration = bars.Count > 0 ? bars[^1] - bars[0] : TimeSpan.Zero,
                StartTime = bars.Count > 0 ? bars[0] : default,
                EndTime = bars.Count > 0 ? bars[^1] : default,
                Trades = trades,
                EquityCurve = equityCurve,
                Candles = benchCandles,
            };
        }
    }
}
