using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    public readonly record struct RebalanceOrder(string Symbol, OrderSide Side, decimal Qty);

    /// <summary>
    /// Section 8: convert target weights into an order list. Target weight → target notional (current
    /// equity) → target qty → diff against the current position. Names held but absent from the
    /// targets are fully exited. A per-order participation cap (≤ <see cref="MomentumConfig.ParticipationCap"/>
    /// of the bar's quote volume) keeps the backtest from assuming fills it could not get; residuals
    /// are simply carried to the next rebalance (the next call re-diffs from the new position).
    /// </summary>
    public static class Rebalance
    {
        /// <summary>Skip deltas whose notional is below this (dust) to avoid churn.</summary>
        public const decimal MinOrderNotionalUsd = 1m;

        public static List<RebalanceOrder> BuildOrders(
            IReadOnlyDictionary<string, decimal> targetWeights,
            IReadOnlyDictionary<string, decimal> currentQty,
            IReadOnlyDictionary<string, decimal> marks,
            IReadOnlyDictionary<string, decimal> barQuoteVolume,
            decimal equity,
            MomentumConfig cfg)
        {
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in targetWeights.Keys) symbols.Add(s);
            foreach (var s in currentQty.Keys) symbols.Add(s);

            var orders = new List<RebalanceOrder>();
            foreach (var sym in symbols)
            {
                if (!marks.TryGetValue(sym, out var price) || price <= 0m) continue;

                decimal targetWeight = targetWeights.TryGetValue(sym, out var w) ? w : 0m;
                decimal targetQty = targetWeight * equity / price;
                decimal curQty = currentQty.TryGetValue(sym, out var q) ? q : 0m;
                decimal delta = targetQty - curQty;

                if (Math.Abs(delta * price) < MinOrderNotionalUsd) continue;

                // Participation cap: never trade more than a slice of the bar's quote volume.
                if (cfg.ParticipationCap > 0 && barQuoteVolume.TryGetValue(sym, out var vol) && vol > 0m)
                {
                    decimal maxQty = (decimal)cfg.ParticipationCap * vol / price;
                    if (Math.Abs(delta) > maxQty) delta = Math.Sign(delta) * maxQty;
                }

                if (delta == 0m) continue;
                orders.Add(new RebalanceOrder(sym, delta > 0m ? OrderSide.Buy : OrderSide.Sell, Math.Abs(delta)));
            }

            // Exits/reductions first (free up margin/cash before adding), then entries.
            orders.Sort((a, b) => IsReducing(a, currentQty).CompareTo(IsReducing(b, currentQty)) * -1);
            return orders;
        }

        private static int IsReducing(RebalanceOrder o, IReadOnlyDictionary<string, decimal> currentQty)
        {
            decimal cur = currentQty.TryGetValue(o.Symbol, out var q) ? q : 0m;
            bool reducing = (o.Side == OrderSide.Sell && cur > 0m) || (o.Side == OrderSide.Buy && cur < 0m);
            return reducing ? 1 : 0;
        }
    }
}
