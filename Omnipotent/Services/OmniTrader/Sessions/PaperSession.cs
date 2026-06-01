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
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;

        private readonly StrategyContext context;
        private readonly SimulatedOrderRouter.State simState;
        private readonly SimulatedOrderRouter router;
        private CancellationTokenSource? cts;
        private Task? loopTask;
        private bool isRunning;
        private DateTime? lastCandleTs;

        // Live tick state (forming candle between closed bars) + a gate so the websocket and the REST
        // fallback never process a closed candle concurrently.
        private readonly SemaphoreSlim candleGate = new(1, 1);
        private decimal lastTickPrice;
        private OHLCCandle? formingCandle;

        /// <summary>Latest live price + the in-progress (forming) candle, for the deployment chart.</summary>
        public (decimal price, OHLCCandle? forming, DateTime? ts) GetLiveTick() => (lastTickPrice, formingCandle, lastCandleTs);

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
            decimal? startingBase = null)
        {
            DeploymentId = deploymentId;
            Config = config;
            Strategy = strategy;
            this.marketData = marketData;
            this.orderRepo = orderRepo;
            this.fillRepo = fillRepo;
            this.equityRepo = equityRepo;
            this.deploymentRepo = deploymentRepo;
            this.log = log;
            this.err = err;

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
            router = new SimulatedOrderRouter(simState, OnFillAsync, m => log($"[{deploymentId}] {m}"));

            var host = new StrategyHost(deploymentId, config.Mode, config.Symbol, config.Interval,
                SubmitOrderAsync, CancelByIntentAsync,
                () => simState.Position, () => simState.QuoteBalance, () => simState.BaseBalance,
                m => log($"[{deploymentId}] {m}"), (m, e) => err($"[{deploymentId}] {m}", e),
                config.Margin.ClampedLeverage);
            context = new StrategyContext { Host = host };
            strategy.Attach(context);
        }

        /// <summary>How many historical candles to seed so indicator strategies have their warmup
        /// immediately instead of waiting many days for live candles to accumulate.</summary>
        private const int PreloadCandles = 500;

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
                var hist = await marketData.GetHistoricalCandlesAsync(Config.Symbol, Config.Interval, PreloadCandles, ct);
                foreach (var c in hist)
                {
                    context.CandleHistory.Add(c);
                    lastCandleTs = c.Timestamp;
                }
                if (hist.Count > 0) router.UpdateLastCandle(hist[^1]);
                if (context.CandleHistory.Count > 5000)
                    context.CandleHistory.RemoveRange(0, context.CandleHistory.Count - 5000);
                log($"[{DeploymentId}] preloaded {hist.Count} {Config.Interval} candles for {Config.Symbol}.");
            }
            catch (Exception ex) { err($"[{DeploymentId}] history preload failed", ex); }
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
        }

        public decimal GetEquity()
        {
            decimal mark = lastCandleTs.HasValue ? GetMarkPrice() : 0;
            return simState.QuoteBalance + simState.BaseBalance * mark;
        }

        public Position? GetPosition() => simState.Position;
        public decimal GetQuoteBalance() => simState.QuoteBalance;
        public decimal GetBaseBalance() => simState.BaseBalance;

        private decimal GetMarkPrice() => simState.Position?.AveragePrice ?? 0;

        private async Task RunLoopAsync(CancellationToken ct)
        {
            // Three concurrent feeds keep the strategy advancing and the chart ticking even if any one
            // source stalls: the websocket (closed candles), a REST poll fallback (in case the socket is
            // blocked), and a fast price-tick poller (the live, forming candle).
            var tasks = new[]
            {
                Task.Run(() => WebSocketLoopAsync(ct), ct),
                Task.Run(() => RestFallbackLoopAsync(ct), ct),
                Task.Run(() => TickPollLoopAsync(ct), ct),
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

        /// <summary>Poll a lightweight price every few seconds to drive the live forming candle.</summary>
        private async Task TickPollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    decimal px = await marketData.GetLatestPriceAsync(Config.Symbol, ct);
                    if (px > 0m)
                    {
                        lastTickPrice = px;
                        var now = DateTime.UtcNow;
                        if (formingCandle is { } f && f.Timestamp == BarStart(now))
                            formingCandle = new OHLCCandle(f.Timestamp, f.Open, Math.Max(f.High, px), Math.Min(f.Low, px), px, f.Volume);
                        else
                            formingCandle = new OHLCCandle(BarStart(now), px, px, px, px, 0m);
                    }
                }
                catch { /* transient */ }
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { break; }
            }
        }

        private DateTime BarStart(DateTime t)
        {
            long sec = (int)Config.Interval * 60;
            long unix = ((DateTimeOffset)DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return DateTimeOffset.FromUnixTimeSeconds(unix - unix % sec).UtcDateTime;
        }

        private async Task ProcessClosedCandleAsync(OHLCCandle candle, CancellationToken ct)
        {
            await candleGate.WaitAsync(ct);
            try
            {
                // Dedup: skip candles already processed (preload, websocket, and REST may overlap).
                if (lastCandleTs.HasValue && candle.Timestamp <= lastCandleTs.Value) return;

                router.UpdateLastCandle(candle);
                context.CandleHistory.Add(candle);
                if (context.CandleHistory.Count > 5000) context.CandleHistory.RemoveAt(0);

                try { await router.OnCandleAsync(candle, ct); } catch (Exception ex) { err($"[{DeploymentId}] router.OnCandle failed", ex); }

                using var stratCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stratCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await Strategy.OnCandleClose(candle, stratCts.Token); }
                catch (OperationCanceledException) { err($"[{DeploymentId}] strategy OnCandleClose timed out", null); }
                catch (Exception ex) { err($"[{DeploymentId}] strategy OnCandleClose failed", ex); }

                lastCandleTs = candle.Timestamp;
                lastTickPrice = candle.Close;
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
            }
            finally { candleGate.Release(); }
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
            var intent = await router.PlaceOrderAsync(DeploymentId, req, ct);
            try { await orderRepo.InsertAsync(intent, ct); } catch { }
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
            try { await fillRepo.InsertAsync(fill); } catch { }
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
