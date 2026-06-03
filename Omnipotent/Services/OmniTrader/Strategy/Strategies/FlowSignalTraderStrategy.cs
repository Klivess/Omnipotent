using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Params;
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
        [Param("Symbol", Group = "Market", IsSymbol = true)] public string TradeSymbol { get; set; } = "BTCUSDT";
        public override StrategySymbols DeclareSymbols() => StrategySymbols.Of(TradeSymbol);

        [Param("Position Fraction", Group = "Sizing", Min = 0.01, Max = 1, Step = 0.01)]
        public decimal PositionFraction { get; set; } = 0.10m;

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

            // Enter with the signal's stop and target as a protective bracket (OCO): the engine fills
            // whichever is reached first and cancels the other — the strategy never closes manually.
            await SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = side,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty,
                Leverage = Leverage,
                StopLossPrice = signal.Stop > 0 ? (decimal)signal.Stop : null,
                TakeProfitPrice = signal.Tp1 > 0 ? (decimal)signal.Tp1 : null,
            }, ct);
        }
    }
}
