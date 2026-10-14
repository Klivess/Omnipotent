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
    /// <summary>The strategy failed at runtime often enough that its result would be meaningless.</summary>
    public sealed class StrategyRuntimeException : Exception
    {
        public StrategyRuntimeException(string message) : base(message) { }
    }

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

        /// <summary>Benchmark series for the analysis block. Never traded and never on the timeline.</summary>
        public IReadOnlyList<OHLCCandle>? BenchmarkCandles { get; set; }

        /// <summary>Single-symbol backtest — the N=1 case of the multi-asset engine. <paramref name="series"/>
        /// may carry the benchmark's bars alongside the traded symbol's.</summary>
        public Task<BacktestResult> RunAsync(CancellationToken ct = default,
            IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>>? series = null)
        {
            string symbol = config.Coin + config.Currency;
            if (BenchmarkCandles == null && series != null && !string.IsNullOrWhiteSpace(config.Benchmark)
                && !string.Equals(config.Benchmark, symbol, StringComparison.OrdinalIgnoreCase)
                && series.TryGetValue(config.Benchmark!, out var bench))
                BenchmarkCandles = bench;
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>> { [symbol] = candles },
                RegimeSymbol = symbol,
            };
            return RunCoreAsync(input, singleSymbol: symbol, ct);
        }

        /// <summary>
        /// Multi-asset (cross-sectional) backtest. A strategy that declares exactly one fixed symbol is
        /// run as the N=1 case (OnCandleClose) even when handed a multi-series input — validation and
        /// studies pass a shared input for every strategy shape, and a single-symbol strategy fed
        /// universe bars would simply never trade.
        /// </summary>
        public Task<BacktestResult> RunPortfolioAsync(CancellationToken ct = default)
        {
            if (portfolioInput == null) throw new InvalidOperationException("RunPortfolioAsync requires the portfolio constructor.");
            var declared = strategy.DeclareSymbols();
            if (!declared.IsUniverse && declared.Fixed is { Count: 1 } && portfolioInput.Candles.TryGetValue(declared.Fixed[0], out var only))
            {
                string symbol = declared.Fixed[0];
                if (BenchmarkCandles == null && !string.IsNullOrWhiteSpace(config.Benchmark)
                    && !string.Equals(config.Benchmark, symbol, StringComparison.OrdinalIgnoreCase)
                    && portfolioInput.Candles.TryGetValue(config.Benchmark!, out var bench))
                    BenchmarkCandles = bench;
                var single = new PortfolioInput
                {
                    Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase) { [symbol] = only },
                    RegimeSymbol = symbol,
                };
                return RunCoreAsync(single, singleSymbol: symbol, ct);
            }
            return RunCoreAsync(portfolioInput, singleSymbol: null, ct);
        }

        private sealed class OpenLeg
        {
            public decimal? EntryPrice;
            public DateTime EntryTime;
            public decimal EntryQty;
            public bool EntryShort;
            public decimal AccruedFees;
            public decimal? TakeProfit;
            public decimal? StopLoss;
            public int EntryBar;
        }

        /// <summary>Strategy exceptions tolerated before the run is failed rather than reported as a quiet zero.</summary>
        private const int MaxStrategyErrors = 25;

        /// <summary>
        /// The one backtest engine. Drives the strategy over the union of every symbol's candle
        /// timestamps. Each bar: extend histories, next-open fills, marks + margin/liquidation +
        /// conditional fills via the router, dispatch to the strategy (<c>OnCandleClose</c> for
        /// single-symbol, <c>OnUniverseBar</c> for portfolio), then record one equity point.
        /// </summary>
        private async Task<BacktestResult> RunCoreAsync(PortfolioInput input, string? singleSymbol, CancellationToken ct)
        {
            bool isSingle = singleSymbol != null;

            var simState = new SimulatedOrderRouter.State
            {
                PortfolioMode = true,
                Execution = config.Execution,
                QuoteBalance = config.InitialQuoteBalance,
                FeeFraction = config.FeeFraction,
                SlippageFraction = config.SlippageFraction,
                Leverage = config.Margin.ClampedLeverage,
                LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                SecondsPerBar = MarketData.CandleClock.Seconds(config.Interval),
            };
            // Single-symbol backtests may start holding the base asset.
            if (isSingle && config.InitialBaseBalance != 0m)
                simState.BaseBalances[singleSymbol!] = config.InitialBaseBalance;

            var trades = new List<TradeRecord>();
            var legs = new Dictionary<string, OpenLeg>(StringComparer.OrdinalIgnoreCase);

            // Per-symbol candle lookups + growing point-in-time histories.
            var timeline = new SortedSet<DateTime>();
            foreach (var series in input.Candles.Values)
                foreach (var c in series) timeline.Add(c.Timestamp);
            var bars = timeline.ToList();

            var byTs = new Dictionary<string, Dictionary<DateTime, OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var histories = new Dictionary<string, List<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var historyViews = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);

            TradeRecord CloseLeg(string sym, OpenLeg leg, DateTime exitTime, decimal exitPrice, decimal exitFee, string reason)
            {
                var h = histories.TryGetValue(sym, out var list) ? list : null;
                int exitBar = h != null ? h.Count - 1 : leg.EntryBar;
                double mae = 0, mfe = 0;
                if (h != null && leg.EntryPrice is > 0m)
                {
                    double entry = (double)leg.EntryPrice.Value;
                    for (int i = Math.Max(0, leg.EntryBar); i <= exitBar && i < h.Count; i++)
                    {
                        double hi = (double)h[i].High, lo = (double)h[i].Low;
                        double fav = leg.EntryShort ? (entry - lo) / entry : (hi - entry) / entry;
                        double adv = leg.EntryShort ? (hi - entry) / entry : (entry - lo) / entry;
                        if (fav > mfe) mfe = fav;
                        if (adv > mae) mae = adv;
                    }
                }
                return new TradeRecord
                {
                    Symbol = sym,
                    EntryTime = leg.EntryTime,
                    ExitTime = exitTime,
                    EntryPrice = leg.EntryPrice!.Value,
                    ExitPrice = exitPrice,
                    Qty = leg.EntryQty,
                    IsShort = leg.EntryShort,
                    Fees = leg.AccruedFees + exitFee,
                    TakeProfit = leg.TakeProfit,
                    StopLoss = leg.StopLoss,
                    BarsHeld = Math.Max(0, exitBar - leg.EntryBar),
                    MaePct = mae * 100,
                    MfePct = mfe * 100,
                    ExitReason = reason,
                };
            }

            int BarIndex(string sym) => histories.TryGetValue(sym, out var h) ? Math.Max(0, h.Count - 1) : 0;

            async Task OnFillAsync(FillEvent fill)
            {
                string sym = fill.Symbol;
                if (!legs.TryGetValue(sym, out var leg)) { leg = new OpenLeg(); legs[sym] = leg; }
                bool flatNow = !simState.Positions.ContainsKey(sym);
                string reason = fill.Reason == "market" ? "signal" : fill.Reason;

                if (flatNow && leg.EntryPrice.HasValue)
                {
                    trades.Add(CloseLeg(sym, leg, fill.FilledUtc, fill.Price, fill.Fee, reason));
                    leg.EntryPrice = null;
                    leg.AccruedFees = 0m;
                    leg.TakeProfit = null;
                    leg.StopLoss = null;
                }
                else if (leg.EntryPrice == null && !flatNow)
                {
                    var pos = simState.Positions[sym];
                    leg.EntryPrice = pos.AveragePrice;
                    leg.EntryTime = fill.FilledUtc;
                    leg.EntryQty = Math.Abs(pos.Qty);
                    leg.EntryShort = pos.IsShort;
                    leg.AccruedFees = fill.Fee;
                    leg.TakeProfit = fill.TakeProfit;
                    leg.StopLoss = fill.StopLoss;
                    leg.EntryBar = BarIndex(sym);
                }
                else if (!flatNow && leg.EntryPrice.HasValue && simState.Positions[sym].IsShort != leg.EntryShort)
                {
                    // A reversal through zero (margin) closes one leg and opens the other in one fill.
                    trades.Add(CloseLeg(sym, leg, fill.FilledUtc, fill.Price, fill.Fee, reason));
                    var pos = simState.Positions[sym];
                    leg.EntryPrice = pos.AveragePrice;
                    leg.EntryTime = fill.FilledUtc;
                    leg.EntryQty = Math.Abs(pos.Qty);
                    leg.EntryShort = pos.IsShort;
                    leg.AccruedFees = 0m;
                    leg.TakeProfit = fill.TakeProfit;
                    leg.StopLoss = fill.StopLoss;
                    leg.EntryBar = BarIndex(sym);
                }
                else
                {
                    leg.AccruedFees += fill.Fee;
                    if (!flatNow && leg.EntryPrice.HasValue)
                    {
                        // Scaling in or out: the leg carries the running average and its largest size.
                        var pos = simState.Positions[sym];
                        leg.EntryPrice = pos.AveragePrice;
                        leg.EntryQty = Math.Max(leg.EntryQty, Math.Abs(pos.Qty));
                    }
                }
                try { await strategy.OnOrderFilled(fill, ct); }
                catch (Exception ex) { log?.Invoke($"ERR: OnOrderFilled {ex.Message}"); }
            }

            var router = new SimulatedOrderRouter(simState, OnFillAsync, m => log?.Invoke(m));

            // Host wiring: in single-symbol mode the single-symbol accessors map onto that one book.
            var host = new Sessions.StrategyHost(
                "backtest", SessionMode.Backtest, singleSymbol ?? input.RegimeSymbol ?? "PORTFOLIO", config.Interval,
                (req, c) => router.PlaceOrderAsync("backtest", req, c),
                async (intentId, c) =>
                {
                    var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId)
                             ?? simState.PendingMarket.FirstOrDefault(o => o.IntentId == intentId);
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
                equityFunc: () => simState.PortfolioEquity(),
                workingOrdersFunc: () => simState.PendingMarket.Concat(simState.OpenOrders).ToList());
            var context = new StrategyContext { Host = host };
            strategy.Attach(context);

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

            int strategyErrors = 0;
            string? firstError = null;
            void NoteError(string where, Exception ex)
            {
                strategyErrors++;
                firstError ??= $"{where}: {ex.GetType().Name}: {ex.Message}";
                log?.Invoke($"ERR: {where} {ex.Message}");
            }

            if (strategy is IPrimedStrategy primed)
            {
                try { primed.Prime(input.Candles, config.Interval); }
                catch (Exception ex) { NoteError("Prime", ex); }
            }
            try { await strategy.OnStart(ct); } catch (Exception ex) { NoteError("OnStart", ex); }

            var equityCurve = new List<EquityPoint>(bars.Count);
            int total = bars.Count;
            int lastProgressEmit = -1;
            decimal lastSingleClose = 0m;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (cancellationCheck != null && i % 64 == 0 && await cancellationCheck())
                    throw new OperationCanceledException("Backtest cancelled by user");

                DateTime t = bars[i];

                var barCandles = new Dictionary<string, OHLCCandle>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, map) in byTs)
                    if (map.TryGetValue(t, out var c)) barCandles[sym] = c;

                // Histories first, so a fill booked on this bar indexes it for MAE/MFE.
                foreach (var (sym, c) in barCandles) histories[sym].Add(c);
                // Router per-bar: next-open fills, marks, margin borrow + intrabar liquidation, conditionals.
                await router.OnPortfolioCandlesAsync(barCandles, ct);

                if (isSingle)
                {
                    if (barCandles.TryGetValue(singleSymbol!, out var candle))
                    {
                        lastSingleClose = candle.Close;
                        try { await strategy.OnCandleClose(candle, ct); }
                        catch (Exception ex) { NoteError("OnCandleClose", ex); }
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
                    catch (Exception ex) { NoteError("OnUniverseBar", ex); }
                }

                if (strategyErrors > MaxStrategyErrors)
                    throw new StrategyRuntimeException(
                        $"Strategy threw {strategyErrors} times by bar {i + 1}/{total}. First: {firstError}");

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

            try { await strategy.OnStop(ct); } catch (Exception ex) { NoteError("OnStop", ex); }

            // Positions still open at the end count as trades marked at the last close, so trade
            // statistics describe the whole run. Equity already carries them.
            foreach (var (sym, leg) in legs)
            {
                if (!leg.EntryPrice.HasValue || !simState.Positions.ContainsKey(sym)) continue;
                var h = histories[sym];
                if (h.Count == 0) continue;
                trades.Add(CloseLeg(sym, leg, h[^1].Timestamp, h[^1].Close, 0m, "open"));
            }
            trades.Sort((x, y) => x.EntryTime.CompareTo(y.EntryTime));

            // Benchmark = an explicit benchmark symbol when supplied, else the single symbol / regime asset.
            string? benchSymbol = !string.IsNullOrWhiteSpace(config.Benchmark) && input.Candles.ContainsKey(config.Benchmark!)
                ? config.Benchmark
                : (isSingle ? singleSymbol : input.RegimeSymbol);
            if (BenchmarkCandles != null) benchSymbol = config.Benchmark;
            var primarySeries = isSingle && input.Candles.TryGetValue(singleSymbol!, out var ps) ? ps : candles;
            var benchCandles = BenchmarkCandles != null
                ? BenchmarkCandles.ToList()
                : benchSymbol != null && input.Candles.TryGetValue(benchSymbol, out var bc)
                    ? bc.ToList()
                    : (isSingle ? primarySeries.ToList() : new List<OHLCCandle>());

            decimal firstClose = isSingle && primarySeries.Count > 0 ? primarySeries[0].Close : (benchCandles.Count > 0 ? benchCandles[0].Close : 0m);
            decimal initialEquity = isSingle
                ? config.InitialQuoteBalance + config.InitialBaseBalance * firstClose
                : config.InitialQuoteBalance;
            decimal finalEquity = simState.PortfolioEquity();

            var wins = trades.Where(t => t.IsWin).ToList();
            var losses = trades.Where(t => !t.IsWin).ToList();
            var (maxDD, maxDDPct) = BacktestMetrics.MaxDrawdown(equityCurve);

            string referenceSymbol = singleSymbol ?? input.RegimeSymbol ?? input.Candles.Keys.FirstOrDefault() ?? "BTCUSDT";
            bool continuous = !MarketData.MarketDataRouter.UsesEquityFeed(referenceSymbol, Venues.AssetClass.Unknown);
            double barsPerYear = MarketData.CandleClock.BarsPerYear(config.Interval, continuous);

            var result = new BacktestResult
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
                BarsPerYear = barsPerYear,
                SharpeRatio = BacktestMetrics.Sharpe(equityCurve, barsPerYear),
                BuyAndHoldPnLPercent = BacktestMetrics.BuyAndHoldPnLPercent(benchCandles),
                TotalCandles = isSingle ? primarySeries.Count : bars.Count,
                Duration = bars.Count > 0 ? bars[^1] - bars[0] : TimeSpan.Zero,
                StartTime = bars.Count > 0 ? bars[0] : default,
                EndTime = bars.Count > 0 ? bars[^1] : default,
                Trades = trades,
                EquityCurve = equityCurve,
                Candles = benchCandles,
                Symbols = input.Candles.Keys.ToList(),
                BenchmarkSymbol = benchSymbol,
                Execution = config.Execution,
                StrategyErrors = strategyErrors,
                FirstStrategyError = firstError,
                Liquidated = simState.Liquidated,
            };
            result.Analysis = BacktestAnalyzer.Analyze(equityCurve, trades, benchCandles, barsPerYear, continuous, simState.Fees);
            if (strategy is Scripting.IReportsPlots plotting && plotting.Plots.Count > 0)
            {
                result.Plots = plotting.Plots.Take(12).ToDictionary(
                    p => p.Key,
                    p => BacktestAnalyzer.Downsample(p.Value.Select(x => x.T).ToArray(), p.Value.Select(x => x.V).ToArray(), 1500));
            }
            return result;
        }
    }
}
