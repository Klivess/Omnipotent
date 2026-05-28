using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Execution
{
    /// <summary>
    /// In-memory order router that fills orders against candle highs/lows.
    /// Used by both BacktestSession and PaperSession. Maintains its own balance/position state
    /// (the session reads back via the OnFill callback to update its own ledger).
    /// </summary>
    public sealed class SimulatedOrderRouter : IOrderRouter
    {
        public sealed class State
        {
            public decimal QuoteBalance;
            public decimal BaseBalance;
            public decimal Fees;
            public Position? Position;
            public decimal FeeFraction;
            public decimal SlippageFraction;
            public List<OrderIntent> OpenOrders { get; } = new();
        }

        private readonly State state;
        private readonly Func<FillEvent, Task> onFillAsync;
        private OHLCCandle? lastCandle;

        public SimulatedOrderRouter(State state, Func<FillEvent, Task> onFillAsync)
        {
            this.state = state;
            this.onFillAsync = onFillAsync;
        }

        public void UpdateLastCandle(OHLCCandle candle) => lastCandle = candle;

        public async Task<OrderIntent> PlaceOrderAsync(string deploymentId, OrderRequest request, CancellationToken ct = default)
        {
            string orderId = Guid.NewGuid().ToString("N");
            var intent = new OrderIntent
            {
                Id = orderId,
                IntentId = request.IntentId,
                DeploymentId = deploymentId,
                Request = request,
                Status = OrderStatus.Pending,
                PlacedUtc = DateTime.UtcNow
            };

            switch (request.Type)
            {
                case OrderType.Market:
                    if (!lastCandle.HasValue)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "No market data yet";
                        return intent;
                    }
                    decimal price = ApplySlippage(lastCandle.Value.Close, request.Side);
                    await FillMarketAsync(intent, request, price, lastCandle.Value.Timestamp);
                    break;
                case OrderType.Limit:
                case OrderType.StopLoss:
                case OrderType.TakeProfit:
                    intent.Status = OrderStatus.Open;
                    state.OpenOrders.Add(intent);
                    break;
            }
            return intent;
        }

        public Task CancelOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            state.OpenOrders.RemoveAll(o => o.Id == intent.Id);
            intent.Status = OrderStatus.Cancelled;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Process open conditional orders against the candle highs/lows.
        /// </summary>
        public async Task OnCandleAsync(OHLCCandle candle, CancellationToken ct = default)
        {
            lastCandle = candle;
            // Snapshot to allow mutation during iteration.
            var toCheck = state.OpenOrders.ToList();
            foreach (var intent in toCheck)
            {
                if (intent.Status != OrderStatus.Open && intent.Status != OrderStatus.PartiallyFilled) continue;
                var req = intent.Request;
                bool triggered = req.Type switch
                {
                    OrderType.Limit => req.Side == OrderSide.Buy
                        ? candle.Low <= req.LimitPrice
                        : candle.High >= req.LimitPrice,
                    OrderType.StopLoss => req.Side == OrderSide.Sell
                        ? candle.Low <= req.StopPrice
                        : candle.High >= req.StopPrice,
                    OrderType.TakeProfit => req.Side == OrderSide.Sell
                        ? candle.High >= req.LimitPrice
                        : candle.Low <= req.LimitPrice,
                    _ => false
                };
                if (!triggered) continue;

                decimal fillPrice = req.Type switch
                {
                    OrderType.Limit => req.LimitPrice!.Value,
                    OrderType.StopLoss => req.StopPrice!.Value,
                    OrderType.TakeProfit => req.LimitPrice!.Value,
                    _ => candle.Close
                };
                fillPrice = ApplySlippage(fillPrice, req.Side);
                state.OpenOrders.Remove(intent);
                await FillMarketAsync(intent, req, fillPrice, candle.Timestamp);
            }
        }

        private async Task FillMarketAsync(OrderIntent intent, OrderRequest req, decimal price, DateTime ts)
        {
            decimal notional = req.Qty * price;
            decimal fee = notional * state.FeeFraction;

            if (req.Side == OrderSide.Buy)
            {
                decimal cost = notional + fee;
                if (cost > state.QuoteBalance)
                {
                    intent.Status = OrderStatus.Rejected;
                    intent.Error = "Insufficient quote balance";
                    return;
                }
                state.QuoteBalance -= cost;
                state.BaseBalance += req.Qty;
            }
            else
            {
                if (req.Qty > state.BaseBalance)
                {
                    intent.Status = OrderStatus.Rejected;
                    intent.Error = "Insufficient base balance";
                    return;
                }
                state.BaseBalance -= req.Qty;
                state.QuoteBalance += notional - fee;
            }
            state.Fees += fee;
            UpdatePosition(req.Side, req.Qty, price, ts, req.Symbol);

            intent.Status = OrderStatus.Filled;
            var fill = new FillEvent
            {
                OrderId = intent.Id,
                IntentId = intent.IntentId,
                Qty = req.Qty,
                Price = price,
                Fee = fee,
                FeeCurrency = "USD",
                FilledUtc = ts
            };
            await onFillAsync(fill);
        }

        private void UpdatePosition(OrderSide side, decimal qty, decimal price, DateTime ts, string symbol)
        {
            state.Position ??= new Position { Symbol = symbol, OpenedUtc = ts };
            decimal signed = side == OrderSide.Buy ? qty : -qty;
            decimal newQty = state.Position.Qty + signed;
            if (Math.Sign(state.Position.Qty) != Math.Sign(newQty) || state.Position.Qty == 0)
            {
                state.Position.AveragePrice = price;
                state.Position.OpenedUtc = ts;
            }
            else if (Math.Sign(signed) == Math.Sign(state.Position.Qty))
            {
                decimal totalCost = state.Position.AveragePrice * Math.Abs(state.Position.Qty) + price * qty;
                state.Position.AveragePrice = totalCost / (Math.Abs(state.Position.Qty) + qty);
            }
            state.Position.Qty = newQty;
            if (newQty == 0) state.Position = null;
        }

        private decimal ApplySlippage(decimal price, OrderSide side)
            => side == OrderSide.Buy
                ? price * (1 + state.SlippageFraction)
                : price * (1 - state.SlippageFraction);
    }
}
