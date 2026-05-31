using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Margin/leverage behaviour. Most checks drive the public SimulatedOrderRouter directly so
    /// the accounting is exercised in isolation; one runs through the real BacktestSession to
    /// prove the config → session → host → router wiring.
    /// </summary>
    public class MarginTests
    {
        private static OHLCCandle C(decimal o, decimal h, decimal l, decimal c)
            => new(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), o, h, l, c, 1000m);

        private static (SimulatedOrderRouter r, SimulatedOrderRouter.State s) Router(
            decimal quote, decimal leverage, decimal borrow = 0m)
        {
            var state = new SimulatedOrderRouter.State
            {
                QuoteBalance = quote,
                FeeFraction = 0m,
                SlippageFraction = 0m,
                Leverage = leverage,
                BorrowAnnualRate = borrow,
                OpeningFeeFraction = 0m, // isolate sizing/accounting from the margin open fee
                SecondsPerBar = 3600,
            };
            return (new SimulatedOrderRouter(state, _ => Task.CompletedTask), state);
        }

        private static OrderRequest Order(OrderSide side, decimal qty) => new()
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Side = side,
            Type = OrderType.Market,
            Symbol = "BTCUSD",
            Qty = qty,
        };

        [Fact]
        public async Task Leverage_Permits_Position_Larger_Than_Cash()
        {
            var (r, s) = Router(quote: 10_000m, leverage: 5m);
            r.UpdateLastCandle(C(100, 100, 100, 100));

            var ok = await r.PlaceOrderAsync("d", Order(OrderSide.Buy, 300m));   // 30k notional = 3x
            Assert.Equal(OrderStatus.Filled, ok.Status);
            Assert.Equal(300m, s.BaseBalance);
            Assert.Equal(-20_000m, s.QuoteBalance);                              // borrowed 20k
            Assert.Equal(10_000m, s.QuoteBalance + s.BaseBalance * 100m);        // equity intact

            // Beyond 5x (would be 6x) must be rejected.
            var (r2, _) = Router(quote: 10_000m, leverage: 5m);
            r2.UpdateLastCandle(C(100, 100, 100, 100));
            var bad = await r2.PlaceOrderAsync("d", Order(OrderSide.Buy, 600m)); // 60k > 50k cap
            Assert.Equal(OrderStatus.Rejected, bad.Status);
            Assert.Contains("Exceeds margin", bad.Error);
        }

        [Fact]
        public async Task Shorting_Is_Allowed_On_Margin()
        {
            var (r, s) = Router(quote: 10_000m, leverage: 3m);
            r.UpdateLastCandle(C(100, 100, 100, 100));

            var ok = await r.PlaceOrderAsync("d", Order(OrderSide.Sell, 200m));  // short 20k <= 30k
            Assert.Equal(OrderStatus.Filled, ok.Status);
            Assert.Equal(-200m, s.BaseBalance);
            Assert.True(s.Position!.IsShort);
            Assert.Equal(30_000m, s.QuoteBalance);                              // +short proceeds
            Assert.Equal(10_000m, s.QuoteBalance + s.BaseBalance * 100m);

            // Price rises 10% → short loses 2,000.
            Assert.Equal(8_000m, s.QuoteBalance + s.BaseBalance * 110m);
        }

        [Fact]
        public async Task Adverse_Move_Triggers_Liquidation()
        {
            var (r, s) = Router(quote: 10_000m, leverage: 10m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Order(OrderSide.Buy, 900m));            // 90k = 9x, liq ≈ 92.6

            await r.OnCandleAsync(C(95, 96, 92, 93));                            // low 92 < liq price

            Assert.True(s.Liquidated);
            Assert.Null(s.Position);                                            // force-closed flat
            Assert.Equal(0m, s.BaseBalance);
            // Liquidated near the maintenance price, so a little equity survives (not wiped to <0).
            decimal equity = s.QuoteBalance;
            Assert.InRange(equity, 0m, 5_000m);
        }

        [Fact]
        public async Task Borrow_Fee_Accrues_Per_Bar_On_Leveraged_Position()
        {
            var (r, s) = Router(quote: 10_000m, leverage: 5m, borrow: 0.20m);
            r.UpdateLastCandle(C(100, 100, 100, 100));
            await r.PlaceOrderAsync("d", Order(OrderSide.Buy, 300m));            // borrowed 20k
            decimal before = s.QuoteBalance;

            await r.OnCandleAsync(C(100, 100, 100, 100));                        // one hourly bar

            // 0.20 * 20,000 * 3600 / 31,536,000 ≈ 0.4566
            decimal charged = before - s.QuoteBalance;
            Assert.InRange(charged, 0.40m, 0.52m);
            Assert.True(s.Fees > 0m);
        }

        [Fact]
        public async Task Spot_Mode_Is_Unchanged_At_1x()
        {
            // Buy beyond cash is rejected (no leverage).
            var (rb, sb) = Router(quote: 10_000m, leverage: 1m);
            rb.UpdateLastCandle(C(100, 100, 100, 100));
            var buy = await rb.PlaceOrderAsync("d", Order(OrderSide.Buy, 200m)); // 20k > 10k
            Assert.Equal(OrderStatus.Rejected, buy.Status);
            Assert.Contains("Insufficient quote", buy.Error);
            Assert.Null(sb.Position);

            // Selling with no inventory is rejected (no shorting).
            var (rs, ss) = Router(quote: 10_000m, leverage: 1m);
            rs.UpdateLastCandle(C(100, 100, 100, 100));
            var sell = await rs.PlaceOrderAsync("d", Order(OrderSide.Sell, 50m));
            Assert.Equal(OrderStatus.Rejected, sell.Status);
            Assert.Contains("Insufficient base", sell.Error);
            Assert.Null(ss.Position);
        }

        // Buys ~all-in (× leverage) on the first bar and holds — used to compare 1x vs 5x.
        private sealed class BuyAndHoldLeveraged : TradingStrategy
        {
            private bool _in;
            public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
            {
                if (_in || candle.Close <= 0) return Task.CompletedTask;
                _in = true;
                decimal qty = QuoteBalance * Leverage * 0.9m / candle.Close;
                return SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Symbol = Symbol,
                    Qty = qty,
                }, ct);
            }
        }

        [Fact]
        public async Task Backtest_Leverage_Amplifies_PnL_End_To_End()
        {
            // Rising market +10%, gentle so a 5x long is never liquidated.
            var candles = new List<OHLCCandle>();
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 60; i++)
            {
                decimal p = 100m + i * 0.1667m; // ~+10% over 60 bars
                candles.Add(new OHLCCandle(t0.AddHours(i), p, p + 0.2m, p - 0.2m, p, 1000m));
            }

            async Task<decimal> GainAt(decimal lev)
            {
                var cfg = new BacktestConfig
                {
                    StrategyClass = "BuyAndHoldLeveraged",
                    Coin = "BTC", Currency = "USD",
                    Interval = TimeInterval.OneHour,
                    CandleCount = candles.Count,
                    FeeFraction = 0.001m,
                    SlippageFraction = 0m,
                    Margin = new MarginSettings { Leverage = lev },
                };
                var res = await new BacktestSession(new BuyAndHoldLeveraged(), candles, cfg).RunAsync();
                return res.TotalPnL;
            }

            decimal g1 = await GainAt(1m);
            decimal g5 = await GainAt(5m);

            Assert.True(g1 > 0m, $"1x should profit on a rising market, got {g1:F2}");
            Assert.True(g5 > g1 * 3.5m, $"5x gain {g5:F2} should be ~5x the 1x gain {g1:F2}");
        }
    }
}
