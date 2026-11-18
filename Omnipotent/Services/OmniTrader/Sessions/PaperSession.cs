using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>
    /// Paper session: live market data + SimulatedOrderRouter, all P&L synthetic.
    /// </summary>
    public sealed class PaperSession : IAsyncDisposable
    {
        public string DeploymentId { get; }
        public DeploymentConfig Config { get; }
        public TradingStrategy Strategy { get; }

        private readonly MarketDataRouter marketData;
        private readonly OrderRepository orderRepo;
        private readonly FillRepository fillRepo;
        private readonly EquityRepository equityRepo;
        private readonly DeploymentRepository deploymentRepo;
        private readonly PaperBookStore? books;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;

        private readonly StrategyContext context;
        private readonly SimulatedOrderRouter.State simState;
        private readonly SimulatedOrderRouter router;
        private CancellationTokenSource? cts;
        private Task? loopTask;
        private bool isRunning;
        private DateTime? lastCandleTs;
        private decimal lastClose;

        // Pulse for the health check.
        private readonly DateTime startedUtc = DateTime.UtcNow;
        private DateTime? lastBarSeenUtc;
        private int barsThisRun;
        private readonly int barsBefore;
        private readonly SessionErrors errors = new();

        // A market order fills inside router.PlaceOrderAsync, before SubmitOrderAsync has written the
        // order row the fill references (fills.order_id → orders.id, foreign keys on). Fills raised while
        // an order is being placed wait here until its row exists.
        private int placing;
        private readonly Queue<FillEvent> deferredFills = new();

        // Next-open execution (DeploymentConfig.Execution = NextOpen, what the Alpha Lab tests): market
        // orders a strategy places at a bar's close wait here and are placed at the next bar's open, before
        // the strategy sees that bar, exactly as the backtester's portfolio router does. They are carried
        // across a restart in the paper book, so a restart between a signal and the next open does not drop one.
        private readonly List<OrderRequest> atNextOpen = new();
        private bool NextOpen => Config.Execution == ExecutionTiming.NextOpen;

        // Gate so the websocket and the REST fallback never process a closed candle concurrently.
        private readonly SemaphoreSlim candleGate = new(1, 1);

        public PaperSession(
            string deploymentId,
            DeploymentConfig config,
            TradingStrategy strategy,
            MarketDataRouter marketData,
            OrderRepository orderRepo,
            FillRepository fillRepo,
            EquityRepository equityRepo,
            DeploymentRepository deploymentRepo,
            Action<string> log,
            Action<string, Exception?> err,
            decimal? startingQuote = null,
            decimal? startingBase = null,
            PaperBookStore? books = null,
            PaperBook? restore = null)
        {
            DeploymentId = deploymentId;
            Config = config;
            Strategy = strategy;
            this.marketData = marketData;
            this.orderRepo = orderRepo;
            this.fillRepo = fillRepo;
            this.equityRepo = equityRepo;
            this.deploymentRepo = deploymentRepo;
            this.books = books;
            this.log = log;
            this.err = (m, e) => { errors.Note(e == null ? m : $"{m}: {e.Message}"); err(m, e); };

            simState = new SimulatedOrderRouter.State
            {
                QuoteBalance = startingQuote ?? config.InitialQuoteBalance,
                BaseBalance = startingBase ?? config.InitialBaseBalance,
                FeeFraction = config.FeeFraction,
                SlippageFraction = config.SlippageFraction,
                Leverage = config.Margin.ClampedLeverage,
                LiquidationMarginLevel = config.Margin.LiquidationMarginLevel,
                BorrowAnnualRate = config.Margin.BorrowAnnualRate,
                OpeningFeeFraction = config.Margin.OpeningFeeFraction,
                SecondsPerBar = (int)config.Interval * 60
            };
            if (restore != null)
            {
                restore.ApplyTo(simState);
                barsBefore = restore.Bars;
                atNextOpen.AddRange(restore.AtNextOpen ?? new());
            }
            router = new SimulatedOrderRouter(simState, OnFillAsync, m => log($"[{deploymentId}] {m}"));

            // Equity is cash plus the position at the last close. Without equityFunc the host reported
            // cash alone, so a strategy sizing off equity (SetTargetPercent, BuyPercent) saw its equity
            // collapse to its spare cash the bar after it bought, unlike the backtest.
            var host = new StrategyHost(deploymentId, config.Mode, config.Symbol, config.Interval,
                SubmitOrderAsync, CancelByIntentAsync,
                () => simState.Position, () => simState.QuoteBalance, () => simState.BaseBalance,
                m => log($"[{deploymentId}] {m}"), (m, e) => this.err($"[{deploymentId}] {m}", e),
                config.Margin.ClampedLeverage,
                equityFunc: GetEquity);
            context = new StrategyContext { Host = host };
            strategy.Attach(context);
        }

        /// <summary>How many historical candles to seed so indicator strategies have their warmup
        /// immediately instead of waiting many days for live candles to accumulate.</summary>
        public const int PreloadCandles = 500;

        public SessionPulse Pulse()
        {
            var (count, last, lastUtc) = errors.Read();
            return new SessionPulse
            {
                Kind = "single",
                StartedUtc = startedUtc,
                LastBarUtc = lastCandleTs,
                LastBarSeenUtc = lastBarSeenUtc,
                BarsThisRun = barsThisRun,
                BarsTotal = barsBefore + barsThisRun,
                SymbolsTracked = 1,
                Errors = count,
                LastError = last,
                LastErrorUtc = lastUtc,
            };
        }

        public async Task StartAsync(CancellationToken externalToken = default)
        {
            if (isRunning) return;
            isRunning = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            await PreloadHistoryAsync(cts.Token);

            try { await Strategy.OnStart(cts.Token); }
            catch (Exception ex) { err($"[{DeploymentId}] OnStart failed", ex); }

            loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }

        /// <summary>Seed CandleHistory (and the router's last mark) with recent closed candles so the
        /// strategy can act on the next live bar rather than after hundreds of hours of streaming.</summary>
        private async Task PreloadHistoryAsync(CancellationToken ct)
        {
            try
            {
                int bars = Math.Clamp(Config.HistoryBars ?? PreloadCandles, 50, 3000);
                var hist = await marketData.GetHistoricalCandlesAsync(Config.Symbol, Config.Interval, bars, ct);
                Seed(hist);
                log($"[{DeploymentId}] preloaded {hist.Count} {Config.Interval} candles for {Config.Symbol}.");
            }
            catch (Exception ex) { err($"[{DeploymentId}] history preload failed", ex); }
        }

        /// <summary>Load closed history without dispatching it (preload; tests).</summary>
        internal void Seed(IReadOnlyList<OHLCCandle> hist)
        {
            foreach (var c in hist)
            {
                context.CandleHistory.Add(c);
                lastCandleTs = c.Timestamp;
                lastClose = c.Close;
            }
            if (hist.Count > 0) router.UpdateLastCandle(hist[^1]);
            if (context.CandleHistory.Count > 5000)
                context.CandleHistory.RemoveRange(0, context.CandleHistory.Count - 5000);
        }

        public async Task StopAsync()
        {
            if (!isRunning) return;
            isRunning = false;
            cts?.Cancel();
            if (loopTask != null)
            {
                try { await loopTask; } catch { }
            }
            try { await Strategy.OnStop(CancellationToken.None); } catch { }
            try { await deploymentRepo.UpdateEquityAsync(DeploymentId, GetEquity()); } catch { }
            await SaveBookAsync(CancellationToken.None);
        }

        /// <summary>Cash plus base units at the last close (the entry price only before any bar is known).</summary>
        public decimal GetEquity() => simState.QuoteBalance + simState.BaseBalance * GetMarkPrice();

        public Position? GetPosition() => simState.Position;
        public decimal GetQuoteBalance() => simState.QuoteBalance;
        public decimal GetBaseBalance() => simState.BaseBalance;

        // Was Position.AveragePrice, so the equity written when a session stopped valued an open position
        // at its entry price and threw its unrealised P&L away.
        private decimal GetMarkPrice() => lastClose > 0m ? lastClose : simState.Position?.AveragePrice ?? 0m;

        private async Task RunLoopAsync(CancellationToken ct)
        {
            // Two concurrent feeds keep the strategy advancing even if one source stalls: the websocket
            // (closed candles) and a REST poll fallback (in case the socket is blocked). The live
            // forming candle for the chart is served generically from market data by the ticks endpoint.
            var tasks = new[]
            {
                Task.Run(() => WebSocketLoopAsync(ct), ct),
                Task.Run(() => RestFallbackLoopAsync(ct), ct),
            };
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                err($"[{DeploymentId}] paper session loop crashed", ex);
                try { await deploymentRepo.UpdateStatusAsync(DeploymentId, DeploymentStatus.Errored, ex.Message); } catch { }
            }
        }

        private async Task WebSocketLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var candle in marketData.StreamCandlesAsync(Config.Symbol, Config.Interval, ct))
                    await ProcessClosedCandleAsync(candle, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { err($"[{DeploymentId}] websocket loop error", ex); }
        }

        /// <summary>Poll the latest closed candle on a cadence so the strategy advances even when the
        /// websocket delivers nothing (firewalled/blocked) — this is the safety net for "no trades".</summary>
        private async Task RestFallbackLoopAsync(CancellationToken ct)
        {
            int barSeconds = (int)Config.Interval * 60;
            var poll = TimeSpan.FromSeconds(Math.Clamp(barSeconds / 4.0, 15, 300));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(poll, ct); } catch { break; }
                try
                {
                    var recent = await marketData.GetHistoricalCandlesAsync(Config.Symbol, Config.Interval, 2, ct);
                    // The last element may be the still-forming bar; use the last fully-closed one.
                    if (recent.Count >= 2) await ProcessClosedCandleAsync(recent[^2], ct);
                }
                catch (Exception ex) { err($"[{DeploymentId}] REST fallback error", ex); }
            }
        }

        internal async Task ProcessClosedCandleAsync(OHLCCandle candle, CancellationToken ct)
        {
            await candleGate.WaitAsync(ct);
            try
            {
                // Dedup: skip candles already processed (preload, websocket, and REST may overlap).
                if (lastCandleTs.HasValue && candle.Timestamp <= lastCandleTs.Value) return;

                if (atNextOpen.Count > 0)
                {
                    // Fill last bar's market orders at this bar's open (± slippage): the router prices a
                    // market order off its last candle, so it is shown the open as that candle's close.
                    router.UpdateLastCandle(candle with { Close = candle.Open });
                    var queued = atNextOpen.ToList();
                    atNextOpen.Clear();
                    foreach (var req in queued) await PlaceAsync(req, ct);
                }

                router.UpdateLastCandle(candle);
                lastClose = candle.Close;
                context.CandleHistory.Add(candle);
                if (context.CandleHistory.Count > 5000) context.CandleHistory.RemoveAt(0);

                try { await router.OnCandleAsync(candle, ct); } catch (Exception ex) { err($"[{DeploymentId}] router.OnCandle failed", ex); }

                using var stratCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stratCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await Strategy.OnCandleClose(candle, stratCts.Token); }
                catch (OperationCanceledException) { err($"[{DeploymentId}] strategy OnCandleClose timed out", null); }
                catch (Exception ex) { err($"[{DeploymentId}] strategy OnCandleClose failed", ex); }

                lastCandleTs = candle.Timestamp;
                lastBarSeenUtc = DateTime.UtcNow;
                barsThisRun++;
                decimal equity = simState.QuoteBalance + simState.BaseBalance * candle.Close;
                try
                {
                    await equityRepo.InsertAsync(DeploymentId, new EquityPoint
                    {
                        Ts = candle.Timestamp,
                        MarkPrice = candle.Close,
                        QuoteBalance = simState.QuoteBalance,
                        BaseBalance = simState.BaseBalance,
                        Equity = equity
                    }, ct);
                    await deploymentRepo.UpdateEquityAsync(DeploymentId, equity, ct);
                }
                catch { }
                await SaveBookAsync(ct);
            }
            finally { candleGate.Release(); }
        }

        private async Task SaveBookAsync(CancellationToken ct)
        {
            if (books == null) return;
            try { await books.SaveAsync(PaperBook.Capture(DeploymentId, simState, lastCandleTs, barsBefore + barsThisRun, atNextOpen), ct); }
            catch (Exception ex) { err($"[{DeploymentId}] paper book save failed", ex); }
        }

        private async Task<OrderIntent> SubmitOrderAsync(OrderRequest req, CancellationToken ct)
        {
            if (await orderRepo.ExistsByIntentAsync(DeploymentId, req.IntentId, ct))
            {
                // Idempotency: duplicate intent ignored.
                return new OrderIntent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IntentId = req.IntentId,
                    DeploymentId = DeploymentId,
                    Request = req,
                    Status = OrderStatus.Rejected,
                    PlacedUtc = DateTime.UtcNow,
                    Error = "duplicate intent_id"
                };
            }
            if (NextOpen && req.Type == OrderType.Market)
            {
                atNextOpen.Add(req);
                return new OrderIntent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IntentId = req.IntentId,
                    DeploymentId = DeploymentId,
                    Request = req,
                    Status = OrderStatus.Pending,
                    PlacedUtc = DateTime.UtcNow,
                };
            }
            return await PlaceAsync(req, ct);
        }

        private async Task<OrderIntent> PlaceAsync(OrderRequest req, CancellationToken ct)
        {
            OrderIntent intent;
            placing++;
            try { intent = await router.PlaceOrderAsync(DeploymentId, req, ct); }
            finally { placing--; }
            try { await orderRepo.InsertAsync(intent, ct); }
            catch (Exception ex) { err($"[{DeploymentId}] order {intent.Id} could not be recorded", ex); }
            if (placing == 0) await FlushDeferredFillsAsync();
            return intent;
        }

        private async Task CancelByIntentAsync(string intentId, CancellationToken ct)
        {
            var match = simState.OpenOrders.FirstOrDefault(o => o.IntentId == intentId);
            if (match == null) return;
            await router.CancelOrderAsync(match, ct);
            try { await orderRepo.UpdateStatusAsync(match.Id, OrderStatus.Cancelled, ct: ct); } catch { }
        }

        private async Task OnFillAsync(FillEvent fill)
        {
            if (placing > 0) { deferredFills.Enqueue(fill); return; }
            await RecordFillAsync(fill);
        }

        private async Task FlushDeferredFillsAsync()
        {
            while (deferredFills.TryDequeue(out var fill)) await RecordFillAsync(fill);
        }

        private async Task RecordFillAsync(FillEvent fill)
        {
            try { await fillRepo.InsertAsync(fill); }
            catch (Exception ex) { err($"[{DeploymentId}] fill for order {fill.OrderId} could not be recorded", ex); }
            try { await orderRepo.UpdateStatusAsync(fill.OrderId, OrderStatus.Filled); } catch { }
            try { await Strategy.OnOrderFilled(fill, CancellationToken.None); } catch (Exception ex) { err($"[{DeploymentId}] OnOrderFilled failed", ex); }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            cts?.Dispose();
        }
    }
}
