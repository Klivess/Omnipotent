using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>
    /// Live session: real Kraken order execution behind a RiskGate. Starts in armed=false state;
    /// only places orders after Arm() is called.
    /// </summary>
    public sealed class LiveSession : IAsyncDisposable
    {
        public string DeploymentId { get; }
        public DeploymentConfig Config { get; }
        public TradingStrategy Strategy { get; }
        public RiskGate Risk { get; }
        public bool IsArmed { get; private set; }

        private readonly MarketDataRouter marketData;
        private readonly IOrderRouter exchange;
        private readonly OrderRepository orderRepo;
        private readonly FillRepository fillRepo;
        private readonly EquityRepository equityRepo;
        private readonly DeploymentRepository deploymentRepo;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;

        private readonly StrategyContext context;
        private CancellationTokenSource? cts;
        private Task? loopTask;
        private bool isRunning;
        private readonly LiveLedger ledger;
        private decimal lastMarkPrice;
        private DateTime? lastCandleTs;

        // Exchange orders awaiting/receiving fills, keyed by exchange order id. The reconciler diffs
        // the exchange's cumulative executed qty/fee against what we've already booked.
        private sealed class TrackedOrder
        {
            public required string InternalId { get; init; }
            public required string IntentId { get; init; }
            public required string Symbol { get; init; }
            public decimal SeenQty { get; set; }
            public decimal SeenFee { get; set; }
        }
        private readonly Dictionary<string, TrackedOrder> trackedOrders = new();

        /// <summary>Historical candles to seed so indicator strategies have warmup immediately.</summary>
        private const int PreloadCandles = 500;

        public LiveSession(
            string deploymentId,
            DeploymentConfig config,
            TradingStrategy strategy,
            MarketDataRouter marketData,
            IOrderRouter exchange,
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
            this.exchange = exchange;
            this.orderRepo = orderRepo;
            this.fillRepo = fillRepo;
            this.equityRepo = equityRepo;
            this.deploymentRepo = deploymentRepo;
            this.log = log;
            this.err = err;

            Risk = new RiskGate(config.Caps ?? new RiskCaps());
            ledger = new LiveLedger(startingQuote ?? config.InitialQuoteBalance, startingBase ?? config.InitialBaseBalance);

            var host = new StrategyHost(deploymentId, config.Mode, config.Symbol, config.Interval,
                SubmitOrderAsync, CancelByIntentAsync,
                () => ledger.Position, () => ledger.QuoteBalance, () => ledger.BaseBalance,
                m => log($"[{deploymentId}] {m}"), (m, e) => err($"[{deploymentId}] {m}", e),
                config.Margin.ClampedLeverage);
            context = new StrategyContext { Host = host };
            strategy.Attach(context);
        }

        public void Arm() { IsArmed = true; Risk.Reset(); }
        public void Disarm() => IsArmed = false;

        public async Task StartAsync(CancellationToken externalToken = default)
        {
            if (isRunning) return;
            isRunning = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            await PreloadHistoryAsync(cts.Token);
            try { await Strategy.OnStart(cts.Token); } catch (Exception ex) { err($"[{DeploymentId}] OnStart failed", ex); }
            loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }

        /// <summary>Seed CandleHistory with recent closed candles so the strategy can act on the next
        /// live bar rather than after hundreds of hours of streaming.</summary>
        private async Task PreloadHistoryAsync(CancellationToken ct)
        {
            try
            {
                var hist = await marketData.GetHistoricalCandlesAsync(Config.Symbol, Config.Interval, PreloadCandles, ct);
                foreach (var c in hist)
                {
                    context.CandleHistory.Add(c);
                    lastCandleTs = c.Timestamp;
                    lastMarkPrice = c.Close;
                }
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
            IsArmed = false;
            cts?.Cancel();
            if (loopTask != null) { try { await loopTask; } catch { } }
            try { await Strategy.OnStop(CancellationToken.None); } catch { }
        }

        public decimal GetEquity() => ledger.QuoteBalance + ledger.BaseBalance * lastMarkPrice;
        public Position? GetPosition() => ledger.Position;
        public decimal GetQuoteBalance() => ledger.QuoteBalance;
        public decimal GetBaseBalance() => ledger.BaseBalance;

        public async Task FlattenAsync(CancellationToken ct = default)
        {
            var pos = ledger.Position;
            if (pos == null || pos.Qty == 0) return;
            var side = pos.Qty > 0 ? OrderSide.Sell : OrderSide.Buy;
            decimal qty = Math.Abs(pos.Qty);
            var req = new OrderRequest
            {
                IntentId = "flatten-" + Guid.NewGuid().ToString("N"),
                Side = side,
                Type = OrderType.Market,
                Symbol = Config.Symbol,
                Qty = qty
            };
            // Bypass the gate for emergency flatten.
            var intent = await exchange.PlaceOrderAsync(DeploymentId, WithLeverage(req), ct);
            try { await orderRepo.InsertAsync(intent, ct); } catch { }
            Track(intent, req.Symbol);
        }

        // Begin tracking a placed order for fill reconciliation.
        private void Track(OrderIntent intent, string symbol)
        {
            if (intent.Status == OrderStatus.Rejected || string.IsNullOrEmpty(intent.ExchangeOrderId)) return;
            trackedOrders[intent.ExchangeOrderId!] = new TrackedOrder
            {
                InternalId = intent.Id,
                IntentId = intent.IntentId,
                Symbol = symbol
            };
        }

        // Poll the exchange and book any incremental fills into the ledger, persistence, the strategy
        // and the RiskGate. Runs each bar on the loop thread, so no locking is needed.
        private async Task ReconcileFillsAsync(CancellationToken ct)
        {
            if (trackedOrders.Count == 0) return;
            IReadOnlyList<ExchangeFill> fills;
            try { fills = await exchange.QueryFillsAsync(trackedOrders.Keys.ToList(), ct); }
            catch (Exception ex) { err($"[{DeploymentId}] fill reconciliation failed", ex); return; }

            foreach (var ef in fills)
            {
                if (!trackedOrders.TryGetValue(ef.ExchangeOrderId, out var to)) continue;

                decimal incQty = ef.CumulativeQty - to.SeenQty;
                decimal incFee = Math.Max(0m, ef.CumulativeFee - to.SeenFee);
                if (incQty > 1e-12m)
                {
                    decimal realized = ledger.ApplyFill(ef.Side, incQty, ef.AvgPrice, incFee, to.Symbol, DateTime.UtcNow);
                    to.SeenQty = ef.CumulativeQty;
                    to.SeenFee = ef.CumulativeFee;

                    var fill = new FillEvent
                    {
                        OrderId = to.InternalId,
                        IntentId = to.IntentId,
                        Qty = incQty,
                        Price = ef.AvgPrice,
                        Fee = incFee,
                        FeeCurrency = "USD",
                        FilledUtc = DateTime.UtcNow,
                        Symbol = to.Symbol
                    };
                    try { await fillRepo.InsertAsync(fill, ct); } catch { }
                    try { await orderRepo.UpdateStatusAsync(to.InternalId, ef.Closed ? OrderStatus.Filled : OrderStatus.PartiallyFilled, ct: ct); } catch { }
                    if (realized != 0m) Risk.RecordRealizedPnL(realized);
                    try { await Strategy.OnOrderFilled(fill, ct); } catch (Exception ex) { err($"[{DeploymentId}] OnOrderFilled failed", ex); }
                    log($"[{DeploymentId}] live fill {ef.Side} {incQty} {to.Symbol} @ {ef.AvgPrice:F2} (realized {realized:F2})");
                }

                if (ef.Closed) trackedOrders.Remove(ef.ExchangeOrderId);
            }
        }

        // Inject the deployment's leverage so the exchange opens/closes on margin. No-op at 1x.
        private OrderRequest WithLeverage(OrderRequest req)
        {
            decimal lev = Config.Margin.ClampedLeverage;
            if (lev <= 1m) return req;
            return new OrderRequest
            {
                IntentId = req.IntentId,
                Side = req.Side,
                Type = req.Type,
                Symbol = req.Symbol,
                Qty = req.Qty,
                LimitPrice = req.LimitPrice,
                StopPrice = req.StopPrice,
                Leverage = lev
            };
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var candle in marketData.StreamCandlesAsync(Config.Symbol, Config.Interval, ct))
                {
                    if (lastCandleTs.HasValue && candle.Timestamp <= lastCandleTs.Value) continue;
                    lastCandleTs = candle.Timestamp;
                    lastMarkPrice = candle.Close;
                    context.CandleHistory.Add(candle);
                    if (context.CandleHistory.Count > 5000) context.CandleHistory.RemoveAt(0);

                    // Book any fills from orders placed on previous bars before the strategy acts,
                    // so it sees an accurate position/balance.
                    await ReconcileFillsAsync(ct);

                    using var stratCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stratCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await Strategy.OnCandleClose(candle, stratCts.Token); }
                    catch (OperationCanceledException) { err($"[{DeploymentId}] strategy OnCandleClose timed out", null); }
                    catch (Exception ex) { err($"[{DeploymentId}] strategy OnCandleClose failed", ex); }

                    // And again after, to catch fills from orders the strategy just placed.
                    await ReconcileFillsAsync(ct);

                    decimal equity = GetEquity();
                    try
                    {
                        await equityRepo.InsertAsync(DeploymentId, new EquityPoint
                        {
                            Ts = candle.Timestamp,
                            MarkPrice = candle.Close,
                            QuoteBalance = ledger.QuoteBalance,
                            BaseBalance = ledger.BaseBalance,
                            Equity = equity
                        }, ct);
                        await deploymentRepo.UpdateEquityAsync(DeploymentId, equity, ct);
                    }
                    catch { }

                    if (Risk.Tripped && IsArmed)
                    {
                        err($"[{DeploymentId}] RiskGate tripped: {Risk.TripReason}", null);
                        try { await FlattenAsync(ct); } catch { }
                        Disarm();
                        try { await deploymentRepo.SetPausedAsync(DeploymentId, DateTime.UtcNow, ct); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                err($"[{DeploymentId}] live session loop crashed", ex);
                try { await deploymentRepo.UpdateStatusAsync(DeploymentId, DeploymentStatus.Errored, ex.Message); } catch { }
            }
        }

        private async Task<OrderIntent> SubmitOrderAsync(OrderRequest req, CancellationToken ct)
        {
            if (!IsArmed)
            {
                return new OrderIntent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IntentId = req.IntentId,
                    DeploymentId = DeploymentId,
                    Request = req,
                    Status = OrderStatus.Rejected,
                    PlacedUtc = DateTime.UtcNow,
                    Error = "Deployment not armed for live trading"
                };
            }
            if (await orderRepo.ExistsByIntentAsync(DeploymentId, req.IntentId, ct))
            {
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
            var outcome = Risk.Check(req, lastMarkPrice, out string? reason);
            if (outcome != RiskCheckOutcome.Allow)
            {
                var intent = new OrderIntent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IntentId = req.IntentId,
                    DeploymentId = DeploymentId,
                    Request = req,
                    Status = OrderStatus.Rejected,
                    PlacedUtc = DateTime.UtcNow,
                    Error = $"Blocked by RiskGate: {reason}"
                };
                try { await orderRepo.InsertAsync(intent, ct); } catch { }
                return intent;
            }
            var placed = await exchange.PlaceOrderAsync(DeploymentId, WithLeverage(req), ct);
            try { await orderRepo.InsertAsync(placed, ct); } catch { }
            Track(placed, req.Symbol);
            return placed;
        }

        private async Task CancelByIntentAsync(string intentId, CancellationToken ct)
        {
            var openOrders = await orderRepo.ListOpenAsync(DeploymentId, ct);
            var match = openOrders.FirstOrDefault(o => o.IntentId == intentId);
            if (match == null) return;
            var stub = new OrderIntent
            {
                Id = match.Id,
                IntentId = match.IntentId,
                DeploymentId = match.DeploymentId,
                Request = new OrderRequest
                {
                    IntentId = match.IntentId,
                    Side = match.Side == "buy" ? OrderSide.Buy : OrderSide.Sell,
                    Type = OrderType.Market,
                    Symbol = match.Symbol,
                    Qty = match.Qty
                },
                Status = OrderStatus.Open,
                PlacedUtc = match.PlacedUtc,
                ExchangeOrderId = match.ExchangeOrderId
            };
            await exchange.CancelOrderAsync(stub, ct);
            try { await orderRepo.UpdateStatusAsync(match.Id, OrderStatus.Cancelled, ct: ct); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            cts?.Dispose();
        }
    }
}
