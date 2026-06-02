using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>
    /// Live counterpart of <see cref="Backtesting.BacktestSession"/>'s portfolio core: drives a
    /// cross-sectional (multi-asset) strategy over a dynamically-resolved universe, in PAPER (against the
    /// portfolio-mode <see cref="SimulatedOrderRouter"/>) or real-money LIVE (per-symbol Kraken orders).
    /// <para>Bar stepping is REST-poll driven (robust — it advances even when websockets are blocked),
    /// mirroring the backtest's deterministic stepping. A fast tick poll keeps live marks fresh. The
    /// universe is re-resolved periodically. Live starts disarmed and routes through a per-order risk
    /// check; positions are tracked optimistically from placed market orders. Brackets (TP/SL) are
    /// engine-managed in paper and venue-managed (Kraken close orders) in live.</para>
    /// </summary>
    public sealed class MultiAssetSession : IAsyncDisposable
    {
        public string DeploymentId { get; }
        public DeploymentConfig Config { get; }
        public TradingStrategy Strategy { get; }
        public SessionMode Mode { get; }
        public bool IsArmed { get; private set; }

        private readonly MarketDataRouter marketData;
        private readonly BinanceUniverseProvider universeProvider;
        private readonly IOrderRouter? exchange;          // null for paper
        private readonly OrderRepository orderRepo;
        private readonly FillRepository fillRepo;
        private readonly EquityRepository equityRepo;
        private readonly DeploymentRepository deploymentRepo;
        private readonly RiskGate risk;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;

        private readonly UniverseSpec spec;
        private readonly StrategyContext context;

        // Paper book.
        private readonly SimulatedOrderRouter.State? simState;
        private readonly SimulatedOrderRouter? router;
        // Live book (optimistic, reconciled periodically against the exchange).
        private decimal liveQuote;
        private readonly Dictionary<string, decimal> livePositions = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<OHLCCandle>> histories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> marks = new(StringComparer.OrdinalIgnoreCase);
        private volatile List<string> universe = new();
        private DateTime lastBarTs;
        private DateTime lastUniverseResolve;

        private CancellationTokenSource? cts;
        private Task? loopTask;
        private bool isRunning;

        private const int PreloadBars = 400;

        public MultiAssetSession(
            string deploymentId, DeploymentConfig config, TradingStrategy strategy, SessionMode mode,
            MarketDataRouter marketData, BinanceUniverseProvider universeProvider, IOrderRouter? exchange,
            OrderRepository orderRepo, FillRepository fillRepo, EquityRepository equityRepo, DeploymentRepository deploymentRepo,
            Action<string> log, Action<string, Exception?> err, decimal? startingQuote = null)
        {
            DeploymentId = deploymentId;
            Config = config;
            Strategy = strategy;
            Mode = mode;
            this.marketData = marketData;
            this.universeProvider = universeProvider;
            this.exchange = exchange;
            this.orderRepo = orderRepo;
            this.fillRepo = fillRepo;
            this.equityRepo = equityRepo;
            this.deploymentRepo = deploymentRepo;
            this.log = log;
            this.err = err;
            this.risk = new RiskGate(config.Caps ?? new RiskCaps());
            spec = strategy.DeclareSymbols().Universe ?? new UniverseSpec();
            liveQuote = startingQuote ?? config.InitialQuoteBalance;

            if (mode == SessionMode.Paper)
            {
                simState = new SimulatedOrderRouter.State
                {
                    PortfolioMode = true,
                    QuoteBalance = startingQuote ?? config.InitialQuoteBalance,
                    FeeFraction = config.FeeFraction,
                    SlippageFraction = config.SlippageFraction,
                    Leverage = config.Margin.ClampedLeverage,
                    LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                    BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                    OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                    SecondsPerBar = (int)config.Interval * 60,
                };
                router = new SimulatedOrderRouter(simState, OnPaperFillAsync, m => log($"[{deploymentId}] {m}"));
            }

            var host = new StrategyHost(
                deploymentId, mode, spec.RegimeSymbol, config.Interval,
                SubmitOrderAsync, CancelByIntentAsync,
                () => null, () => Mode == SessionMode.Paper ? simState!.QuoteBalance : liveQuote, () => 0m,
                m => log($"[{deploymentId}] {m}"), (m, e) => err($"[{deploymentId}] {m}", e),
                config.Margin.ClampedLeverage,
                portfolioPositionsFunc: PortfolioPositions,
                equityFunc: Equity);
            context = new StrategyContext { Host = host };
            strategy.Attach(context);
        }

        public void Arm() { IsArmed = true; risk.Reset(); }
        public void Disarm() => IsArmed = false;

        public decimal Equity()
        {
            if (Mode == SessionMode.Paper) return simState!.PortfolioEquity();
            decimal eq = liveQuote;
            foreach (var kv in livePositions) eq += kv.Value * Mark(kv.Key);
            return eq;
        }

        public IReadOnlyDictionary<string, decimal> PortfolioPositions()
        {
            if (Mode == SessionMode.Paper) return simState!.NonZeroPositions();
            var d = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in livePositions) if (kv.Value != 0m) d[kv.Key] = kv.Value;
            return d;
        }

        private decimal Mark(string symbol) => marks.TryGetValue(symbol, out var m) ? m : 0m;

        public async Task StartAsync(CancellationToken externalToken = default)
        {
            if (isRunning) return;
            isRunning = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            try { await ResolveUniverseAsync(cts.Token); } catch (Exception ex) { err($"[{DeploymentId}] universe resolve failed", ex); }
            try { await PreloadAsync(cts.Token); } catch (Exception ex) { err($"[{DeploymentId}] preload failed", ex); }
            try { await Strategy.OnStart(cts.Token); } catch (Exception ex) { err($"[{DeploymentId}] OnStart failed", ex); }

            loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }

        public async Task StopAsync()
        {
            if (!isRunning) return;
            isRunning = false;
            IsArmed = false;
            cts?.Cancel();
            if (loopTask != null) { try { await loopTask; } catch { } }
            try { await Strategy.OnStop(CancellationToken.None); } catch { }
            try { await deploymentRepo.UpdateEquityAsync(DeploymentId, Equity()); } catch { }
        }

        private async Task ResolveUniverseAsync(CancellationToken ct)
        {
            var resolved = await universeProvider.ResolveUniverseAsync(spec.TopN, ct);
            if (!resolved.Contains(spec.RegimeSymbol, StringComparer.OrdinalIgnoreCase)) resolved.Add(spec.RegimeSymbol);
            universe = resolved;
            lastUniverseResolve = DateTime.UtcNow;
            log($"[{DeploymentId}] universe resolved: {resolved.Count} symbols.");
        }

        private async Task PreloadAsync(CancellationToken ct)
        {
            foreach (var sym in universe)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bars = await marketData.GetHistoricalCandlesAsync(sym, Config.Interval, PreloadBars, ct);
                    histories[sym] = bars.Select(ToUsdVolume).ToList();
                    if (histories[sym].Count > 0) marks[sym] = histories[sym][^1].Close;
                }
                catch (Exception ex) { err($"[{DeploymentId}] preload {sym} failed", ex); }
            }
            // Clock off the regime symbol's latest bar.
            if (histories.TryGetValue(spec.RegimeSymbol, out var rh) && rh.Count > 0)
                lastBarTs = rh[^1].Timestamp;
        }

        // Convert a candle's base volume into approximate USD quote volume (×close) so the momentum
        // liquidity filter and volume ranking see USD, matching the backtest's universe data.
        private static OHLCCandle ToUsdVolume(OHLCCandle c)
            => new(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume * c.Close);

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int barSeconds = (int)Config.Interval * 60;
            var step = TimeSpan.FromSeconds(Math.Clamp(barSeconds / 6.0, 20, 600));
            var tick = TimeSpan.FromSeconds(5);
            var tickTask = Task.Run(() => TickLoopAsync(ct), ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await StepAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { err($"[{DeploymentId}] step error", ex); }
                    try { await Task.Delay(step, ct); } catch { break; }
                }
            }
            finally { try { await tickTask; } catch { } }
        }

        private async Task TickLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Refresh the regime mark cheaply for the live equity/chart.
                    decimal px = await marketData.GetLatestPriceAsync(spec.RegimeSymbol, ct);
                    if (px > 0m) marks[spec.RegimeSymbol] = px;
                }
                catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
            }
        }

        private async Task StepAsync(CancellationToken ct)
        {
            // Periodically re-resolve the universe (new listings / volume shifts).
            if ((DateTime.UtcNow - lastUniverseResolve) > TimeSpan.FromHours(6))
            {
                try { await ResolveUniverseAsync(ct); } catch { }
            }

            // Pull the latest candles for every universe symbol and append newly-closed bars.
            DateTime newest = lastBarTs;
            foreach (var sym in universe.ToList())
            {
                try
                {
                    var bars = await marketData.GetHistoricalCandlesAsync(sym, Config.Interval, 3, ct);
                    if (!histories.TryGetValue(sym, out var h)) { h = new List<OHLCCandle>(); histories[sym] = h; }
                    DateTime last = h.Count > 0 ? h[^1].Timestamp : DateTime.MinValue;
                    foreach (var b in bars)
                    {
                        if (b.Timestamp > last) { h.Add(ToUsdVolume(b)); if (h.Count > 5000) h.RemoveAt(0); }
                    }
                    if (h.Count > 0) marks[sym] = h[^1].Close;
                    if (h.Count > 0 && h[^1].Timestamp > newest) newest = h[^1].Timestamp;
                }
                catch { /* skip this symbol this step */ }
            }

            if (newest <= lastBarTs) return; // no new bar yet
            lastBarTs = newest;

            // Paper: process marks, margin/borrow, and bracket fills via the router before dispatch.
            if (Mode == SessionMode.Paper && router != null)
            {
                var bar = new Dictionary<string, OHLCCandle>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, h) in histories) if (h.Count > 0) bar[sym] = h[^1];
                try { await router.OnPortfolioCandlesAsync(bar, ct); } catch (Exception ex) { err($"[{DeploymentId}] router bar failed", ex); }
            }

            var presentHistories = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, h) in histories) if (h.Count > 0) presentHistories[sym] = h;

            var pbar = new PortfolioBar { T = lastBarTs, Histories = presentHistories, MarketCaps = new Dictionary<string, decimal>() };
            using var stratCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stratCts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await Strategy.OnUniverseBar(pbar, stratCts.Token); }
            catch (OperationCanceledException) { err($"[{DeploymentId}] OnUniverseBar timed out", null); }
            catch (Exception ex) { err($"[{DeploymentId}] OnUniverseBar failed", ex); }

            decimal equity = Equity();
            try
            {
                await equityRepo.InsertAsync(DeploymentId, new EquityPoint
                {
                    Ts = lastBarTs, MarkPrice = Mark(spec.RegimeSymbol),
                    QuoteBalance = Mode == SessionMode.Paper ? simState!.QuoteBalance : liveQuote,
                    BaseBalance = 0m, Equity = equity
                }, ct);
                await deploymentRepo.UpdateEquityAsync(DeploymentId, equity, ct);
            }
            catch { }
        }

        // ── Order routing ────────────────────────────────────────────────────────

        private async Task<OrderIntent> SubmitOrderAsync(OrderRequest req, CancellationToken ct)
        {
            if (Mode == SessionMode.Paper)
            {
                if (await orderRepo.ExistsByIntentAsync(DeploymentId, req.IntentId, ct))
                    return Reject(req, "duplicate intent_id");
                var intent = await router!.PlaceOrderAsync(DeploymentId, req, ct);
                try { await orderRepo.InsertAsync(intent, ct); } catch { }
                return intent;
            }

            // Live.
            if (!IsArmed) return Reject(req, "Deployment not armed for live trading");
            if (await orderRepo.ExistsByIntentAsync(DeploymentId, req.IntentId, ct)) return Reject(req, "duplicate intent_id");
            var outcome = risk.Check(req, Mark(req.Symbol), out string? reason);
            if (outcome != RiskCheckOutcome.Allow)
            {
                var rj = Reject(req, $"Blocked by RiskGate: {reason}");
                try { await orderRepo.InsertAsync(rj, ct); } catch { }
                return rj;
            }
            var placed = await exchange!.PlaceOrderAsync(DeploymentId, WithLeverage(req), ct);
            if (placed.Status != OrderStatus.Rejected)
            {
                // Optimistic position/cash update at the current mark (reconciled periodically).
                decimal mark = Mark(req.Symbol);
                decimal signed = req.Side == OrderSide.Buy ? req.Qty : -req.Qty;
                livePositions[req.Symbol] = (livePositions.TryGetValue(req.Symbol, out var cur) ? cur : 0m) + signed;
                liveQuote -= signed * mark;
            }
            try { await orderRepo.InsertAsync(placed, ct); } catch { }
            return placed;
        }

        private async Task CancelByIntentAsync(string intentId, CancellationToken ct)
        {
            if (Mode == SessionMode.Paper && router != null && simState != null)
            {
                var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId);
                if (match != null) await router.CancelOrderAsync(match, ct);
            }
            // Live cancels are venue-managed (bracket close orders); explicit cancel omitted here.
        }

        private async Task OnPaperFillAsync(FillEvent fill)
        {
            try { await fillRepo.InsertAsync(fill); } catch { }
            try { await orderRepo.UpdateStatusAsync(fill.OrderId, OrderStatus.Filled); } catch { }
            try { await Strategy.OnOrderFilled(fill, CancellationToken.None); } catch (Exception ex) { err($"[{DeploymentId}] OnOrderFilled failed", ex); }
        }

        private OrderRequest WithLeverage(OrderRequest req)
        {
            decimal lev = Config.Margin.ClampedLeverage;
            if (lev <= 1m) return req;
            return new OrderRequest
            {
                IntentId = req.IntentId, Side = req.Side, Type = req.Type, Symbol = req.Symbol, Qty = req.Qty,
                LimitPrice = req.LimitPrice, StopPrice = req.StopPrice, Leverage = lev,
                TakeProfitPrice = req.TakeProfitPrice, StopLossPrice = req.StopLossPrice,
            };
        }

        private OrderIntent Reject(OrderRequest req, string error) => new()
        {
            Id = Guid.NewGuid().ToString("N"), IntentId = req.IntentId, DeploymentId = DeploymentId,
            Request = req, Status = OrderStatus.Rejected, PlacedUtc = DateTime.UtcNow, Error = error,
        };

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            cts?.Dispose();
        }
    }
}
