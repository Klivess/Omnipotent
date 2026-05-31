using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Portfolio (multi-symbol) mode of the extended SimulatedOrderRouter / BacktestSession.
    /// The single-symbol path is covered by <see cref="MarginTests"/>; these prove the per-symbol
    /// books, shared cash, portfolio-wide leverage cap, and the end-to-end RunPortfolioAsync wiring.
    /// </summary>
    public class PortfolioEngineTests
    {
        private static SimulatedOrderRouter.State PortfolioState(decimal quote, decimal leverage)
            => new()
            {
                PortfolioMode = true,
                QuoteBalance = quote,
                FeeFraction = 0m,
                SlippageFraction = 0m,
                Leverage = leverage,
                OpeningFeeFraction = 0m,
                SecondsPerBar = 3600,
            };

        private static OrderRequest Order(string sym, OrderSide side, decimal qty) => new()
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Side = side,
            Type = OrderType.Market,
            Symbol = sym,
            Qty = qty,
        };

        [Fact]
        public async Task Spot_Books_Are_Per_Symbol_With_Shared_Cash()
        {
            var s = PortfolioState(quote: 10_000m, leverage: 1m);
            var r = new SimulatedOrderRouter(s, _ => Task.CompletedTask);
            r.UpdateMarks(new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 50m }, DateTime.UtcNow);

            Assert.Equal(OrderStatus.Filled, (await r.PlaceOrderAsync("d", Order("AAA", OrderSide.Buy, 50m))).Status);
            Assert.Equal(OrderStatus.Filled, (await r.PlaceOrderAsync("d", Order("BBB", OrderSide.Buy, 100m))).Status);

            Assert.Equal(50m, s.GetBaseBalance("AAA"));
            Assert.Equal(100m, s.GetBaseBalance("BBB"));
            Assert.Equal(0m, s.QuoteBalance);                       // 5k + 5k spent
            Assert.Equal(10_000m, s.PortfolioEquity());             // 50*100 + 100*50

            // No shorting at 1x: selling more AAA than held is rejected.
            var bad = await r.PlaceOrderAsync("d", Order("AAA", OrderSide.Sell, 60m));
            Assert.Equal(OrderStatus.Rejected, bad.Status);
            Assert.Contains("Insufficient base", bad.Error);
        }

        [Fact]
        public async Task Gross_Leverage_Cap_Is_Portfolio_Wide()
        {
            var s = PortfolioState(quote: 10_000m, leverage: 3m);   // cap = 30k gross
            var r = new SimulatedOrderRouter(s, _ => Task.CompletedTask);
            r.UpdateMarks(new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 100m }, DateTime.UtcNow);

            Assert.Equal(OrderStatus.Filled, (await r.PlaceOrderAsync("d", Order("AAA", OrderSide.Buy, 200m))).Status); // 20k
            var over = await r.PlaceOrderAsync("d", Order("BBB", OrderSide.Buy, 150m));                                 // +15k = 35k
            Assert.Equal(OrderStatus.Rejected, over.Status);
            Assert.Contains("Exceeds margin", over.Error);

            var ok = await r.PlaceOrderAsync("d", Order("BBB", OrderSide.Buy, 100m));                                   // +10k = 30k
            Assert.Equal(OrderStatus.Filled, ok.Status);
            Assert.Equal(30_000m, s.GrossNotional());
        }

        [Fact]
        public async Task Short_On_Margin_Updates_Equity_With_Price()
        {
            var s = PortfolioState(quote: 10_000m, leverage: 3m);
            var r = new SimulatedOrderRouter(s, _ => Task.CompletedTask);
            r.UpdateMarks(new Dictionary<string, decimal> { ["AAA"] = 100m }, DateTime.UtcNow);

            Assert.Equal(OrderStatus.Filled, (await r.PlaceOrderAsync("d", Order("AAA", OrderSide.Sell, 200m))).Status); // short 20k
            Assert.Equal(-200m, s.GetBaseBalance("AAA"));
            Assert.True(s.Positions["AAA"].IsShort);
            Assert.Equal(30_000m, s.QuoteBalance);                  // short proceeds added
            Assert.Equal(10_000m, s.PortfolioEquity());

            r.UpdateMarks(new Dictionary<string, decimal> { ["AAA"] = 110m }, DateTime.UtcNow);
            Assert.Equal(8_000m, s.PortfolioEquity());              // short lost 2k on a 10% rise
        }

        // Buys an equal-weight basket of every symbol on the first universe bar, then holds.
        private sealed class EqualWeightBuyAndHold : TradingStrategy
        {
            private bool _in;
            public override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
            {
                if (_in) return;
                _in = true;
                var syms = bar.Histories.Keys.ToList();
                decimal per = Equity / syms.Count * 0.95m;
                foreach (var sym in syms)
                {
                    decimal price = bar.Mark(sym);
                    if (price <= 0) continue;
                    await SubmitOrder(new OrderRequest
                    {
                        IntentId = Guid.NewGuid().ToString("N"),
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Symbol = sym,
                        Qty = per / price,
                    }, ct);
                }
            }
        }

        [Fact]
        public async Task RunPortfolioAsync_Holds_Equal_Weight_Basket_End_To_End()
        {
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            List<OHLCCandle> Series(decimal start, decimal endMul)
            {
                var list = new List<OHLCCandle>();
                for (int i = 0; i < 30; i++)
                {
                    decimal p = start * (1m + (endMul - 1m) * i / 29m);
                    list.Add(new OHLCCandle(t0.AddDays(i), p, p, p, p, 1_000_000m));
                }
                return list;
            }

            // AAA +20%, BBB +40% over the window.
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>
                {
                    ["AAA"] = Series(100m, 1.20m),
                    ["BBB"] = Series(10m, 1.40m),
                },
                RegimeSymbol = "AAA",
            };
            var cfg = new BacktestConfig
            {
                StrategyClass = "EqualWeightBuyAndHold",
                Coin = "AAA", Currency = "USD",
                Interval = TimeInterval.OneDay,
                CandleCount = 30,
                FeeFraction = 0m,
                SlippageFraction = 0m,
            };

            var res = await new BacktestSession(new EqualWeightBuyAndHold(), input, cfg).RunPortfolioAsync();

            // ~95% deployed equally → roughly (0.20+0.40)/2 = 30% on the deployed portion.
            Assert.True(res.TotalPnL > 0m, $"expected profit, got {res.TotalPnL:F2}");
            Assert.InRange(res.TotalPnLPercent, 20m, 34m);
            Assert.Equal(30, res.EquityCurve.Count);
            Assert.Empty(res.Trades);            // basket is held open → no completed round-trips
        }
    }
}
