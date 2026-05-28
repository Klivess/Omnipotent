using Omnipotent.Services.OmniTrader.Contracts;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    /// <summary>
    /// Listens for FlowSignal webhook payloads pushed via POST /api/omnitrader/signals/flowsignal.
    /// Each live instance is registered into a static dispatch table keyed by symbol;
    /// the route looks up instances and pushes signals in. Trades stop-loss and take-profit
    /// values come from the signal payload.
    /// </summary>
    [TradingStrategy(
        "FlowSignal Trader",
        "Trades crypto based on FlowSignal webhook signals. Posts to /api/omnitrader/signals/flowsignal trigger entries with SL/TP from the payload.")]
    public sealed class FlowSignalTraderStrategy : TradingStrategy
    {
        private const decimal PositionFraction = 0.10m;

        // Active deployments listening for signals, keyed by uppercased symbol.
        private static readonly ConcurrentDictionary<string, List<FlowSignalTraderStrategy>> SubscribersBySymbol
            = new(StringComparer.OrdinalIgnoreCase);

        public sealed class FlowSignalPayload
        {
            [JsonPropertyName("symbol")] public string? Symbol { get; set; }
            [JsonPropertyName("direction")] public string? Direction { get; set; }
            [JsonPropertyName("setup_type")] public string? SetupType { get; set; }
            [JsonPropertyName("strength")] public string? Strength { get; set; }
            [JsonPropertyName("price")] public double Price { get; set; }
            [JsonPropertyName("stop")] public double Stop { get; set; }
            [JsonPropertyName("tp1")] public double Tp1 { get; set; }
            [JsonPropertyName("tp2")] public double Tp2 { get; set; }
            [JsonPropertyName("reason")] public string? Reason { get; set; }
            [JsonPropertyName("score")] public int Score { get; set; }
        }

        public static IReadOnlyList<FlowSignalTraderStrategy> Subscribers(string symbol)
        {
            if (SubscribersBySymbol.TryGetValue(symbol, out var list))
                return list.ToList();
            return Array.Empty<FlowSignalTraderStrategy>();
        }

        public override Task OnStart(CancellationToken ct)
        {
            var list = SubscribersBySymbol.GetOrAdd(Symbol, _ => new List<FlowSignalTraderStrategy>());
            lock (list) list.Add(this);
            Log($"FlowSignal subscriber attached on {Symbol}");
            return Task.CompletedTask;
        }

        public override Task OnStop(CancellationToken ct)
        {
            if (SubscribersBySymbol.TryGetValue(Symbol, out var list))
            {
                lock (list) list.Remove(this);
            }
            return Task.CompletedTask;
        }

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct) => Task.CompletedTask;

        public async Task HandleSignalAsync(FlowSignalPayload signal, CancellationToken ct = default)
        {
            if (Position != null && !Position.IsFlat)
            {
                Log("FlowSignal received but position already open; skipping.");
                return;
            }
            if (string.IsNullOrWhiteSpace(signal.Direction)) return;

            var side = signal.Direction.Trim().Equals("LONG", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy
                : OrderSide.Sell;
            decimal price = (decimal)signal.Price;
            if (price <= 0) return;

            decimal qty = QuoteBalance * PositionFraction / price;
            if (qty <= 0) return;

            string intentId = Guid.NewGuid().ToString("N");
            await SubmitOrder(new OrderRequest
            {
                IntentId = intentId,
                Side = side,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty
            }, ct);

            if (signal.Stop > 0)
            {
                await SubmitOrder(new OrderRequest
                {
                    IntentId = intentId + "-sl",
                    Side = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                    Type = OrderType.StopLoss,
                    Symbol = Symbol,
                    Qty = qty,
                    StopPrice = (decimal)signal.Stop
                }, ct);
            }
            if (signal.Tp1 > 0)
            {
                await SubmitOrder(new OrderRequest
                {
                    IntentId = intentId + "-tp",
                    Side = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                    Type = OrderType.TakeProfit,
                    Symbol = Symbol,
                    Qty = qty,
                    LimitPrice = (decimal)signal.Tp1
                }, ct);
            }
        }
    }
}
