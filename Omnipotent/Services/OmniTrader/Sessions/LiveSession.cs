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
        private Position? position;
        private decimal quoteBalance;
        private decimal baseBalance;
        private decimal lastMarkPrice;

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
            quoteBalance = startingQuote ?? config.InitialQuoteBalance;
            baseBalance = startingBase ?? config.InitialBaseBalance;

            var host = new StrategyHost(deploymentId, config.Mode, config.Symbol, config.Interval,
                SubmitOrderAsync, CancelByIntentAsync,
                () => position, () => quoteBalance, () => baseBalance,
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
        }

        public decimal GetEquity() => quoteBalance + baseBalance * lastMarkPrice;
        public Position? GetPosition() => position;
        public decimal GetQuoteBalance() => quoteBalance;
        public decimal GetBaseBalance() => baseBalance;

        public async Task FlattenAsync(CancellationToken ct = default)
        {
            if (position == null || position.Qty == 0) return;
            var side = position.Qty > 0 ? OrderSide.Sell : OrderSide.Buy;
            decimal qty = Math.Abs(position.Qty);
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
                    lastMarkPrice = candle.Close;
                    context.CandleHistory.Add(candle);
                    if (context.CandleHistory.Count > 5000) context.CandleHistory.RemoveAt(0);

                    using var stratCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stratCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await Strategy.OnCandleClose(candle, stratCts.Token); }
                    catch (OperationCanceledException) { err($"[{DeploymentId}] strategy OnCandleClose timed out", null); }
                    catch (Exception ex) { err($"[{DeploymentId}] strategy OnCandleClose failed", ex); }

                    decimal equity = GetEquity();
                    try
                    {
                        await equityRepo.InsertAsync(DeploymentId, new EquityPoint
                        {
                            Ts = candle.Timestamp,
                            MarkPrice = candle.Close,
                            QuoteBalance = quoteBalance,
                            BaseBalance = baseBalance,
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
