using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Bracket (take-profit / stop-loss) + OCO behaviour in the SimulatedOrderRouter — used by the
    /// backtester and the paper engine. An entry carrying TP/SL registers protective orders; when one
    /// fills (or the position otherwise flattens) the sibling is cancelled.
    /// </summary>
    public class BracketOrderTests
    {
        private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static OHLCCandle C(decimal o, decimal h, decimal l, decimal c) => new(T0, o, h, l, c, 1_000m);

        private static (SimulatedOrderRouter r, SimulatedOrderRouter.State s) Spot(decimal quote)
        {
            var s = new SimulatedOrderRouter.State { QuoteBalance = quote, FeeFraction = 0m, SlippageFraction = 0m, Leverage = 1m };
            return (new SimulatedOrderRouter(s, _ => Task.CompletedTask), s);
        }

        private static OrderRequest Buy(decimal qty, decimal? sl = null, decimal? tp = null) => new()
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Side = OrderSide.Buy, Type = OrderType.Market, Symbol = "BTCUSD", Qty = qty,
            StopLossPrice = sl, TakeProfitPrice = tp,
        };

        [Fact]
        public async Task Entry_Registers_Protective_Bracket()
        {
            var (r, s) = Spot(10_000m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Buy(50m, sl: 90m, tp: 110m));
            Assert.Equal(50m, s.BaseBalance);
            Assert.Equal(2, s.OpenOrders.Count);   // SL + TP
            Assert.All(s.OpenOrders, o => Assert.Equal("bracket:BTCUSD", o.OcoGroup));
        }

        [Fact]
        public async Task Take_Profit_Fills_And_Cancels_The_Stop()
        {
            var (r, s) = Spot(10_000m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Buy(50m, sl: 90m, tp: 110m));   // cost 5000 → quote 5000

            await r.OnCandleAsync(C(105, 112, 104, 108));               // high 112 ≥ 110 TP

            Assert.Null(s.Position);                                    // flat
            Assert.Empty(s.OpenOrders);                                 // OCO cancelled the stop
            Assert.Equal(10_500m, s.QuoteBalance);                      // sold 50 @ 110 → +5500
        }

        [Fact]
        public async Task Stop_Loss_Fills_And_Cancels_The_Take_Profit()
        {
            var (r, s) = Spot(10_000m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Buy(50m, sl: 90m, tp: 110m));

            await r.OnCandleAsync(C(95, 96, 88, 92));                   // low 88 ≤ 90 SL

            Assert.Null(s.Position);
            Assert.Empty(s.OpenOrders);
            Assert.Equal(9_500m, s.QuoteBalance);                      // sold 50 @ 90 → +4500 (−500 loss)
        }

        [Fact]
        public async Task When_Both_Hit_In_One_Bar_Stop_Wins()
        {
            var (r, s) = Spot(10_000m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Buy(50m, sl: 90m, tp: 110m));

            await r.OnCandleAsync(C(100, 112, 88, 100));               // spans both SL and TP

            Assert.Null(s.Position);
            Assert.Empty(s.OpenOrders);
            Assert.Equal(9_500m, s.QuoteBalance);                      // conservative: stop filled, not TP
        }

        [Fact]
        public async Task Portfolio_Mode_Brackets_Are_Per_Symbol()
        {
            var s = new SimulatedOrderRouter.State { PortfolioMode = true, QuoteBalance = 10_000m, FeeFraction = 0m, SlippageFraction = 0m, Leverage = 3m };
            var r = new SimulatedOrderRouter(s, _ => Task.CompletedTask);
            r.UpdateMarks(new Dictionary<string, decimal> { ["AAA"] = 100m }, T0);
            await r.PlaceOrderAsync("d", new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"), Side = OrderSide.Buy, Type = OrderType.Market,
                Symbol = "AAA", Qty = 100m, StopLossPrice = 90m, TakeProfitPrice = 110m,
            });
            Assert.Equal(2, s.OpenOrders.Count);

            await r.OnPortfolioCandlesAsync(new Dictionary<string, OHLCCandle> { ["AAA"] = C(105, 112, 104, 108) });

            Assert.False(s.Positions.ContainsKey("AAA"));   // flat
            Assert.Empty(s.OpenOrders);                     // OCO cancelled
        }
    }
}
