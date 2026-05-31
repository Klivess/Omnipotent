using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// End-to-end test of the cross-sectional momentum strategy over a synthetic multi-asset universe,
    /// driven through the real portfolio backtest path (BacktestSession.RunPortfolioAsync). Proves the
    /// modules wire together: universe → signals → selection → regime → sizing → rebalance → fills.
    /// </summary>
    public class CrossSectionalMomentumStrategyTests
    {
        private static readonly DateTime T0 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A coin's daily series with a persistent drift and reproducible noise.
        private static List<OHLCCandle> Coin(int seed, decimal dailyDrift, int days, decimal volume = 50_000_000m)
        {
            var rng = new Random(seed);
            var list = new List<OHLCCandle>(days);
            decimal p = 100m;
            for (int i = 0; i < days; i++)
            {
                list.Add(new OHLCCandle(T0.AddDays(i), p, p, p, p, volume));
                decimal noise = (decimal)((rng.NextDouble() - 0.5) * 0.04); // ±2% daily
                p *= (1m + dailyDrift + noise);
                if (p < 1m) p = 1m;
            }
            return list;
        }

        [Fact]
        public async Task Momentum_Longs_Trending_Winners_And_Profits()
        {
            const int days = 220;
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>();
            var caps = new Dictionary<string, IReadOnlyList<MarketCapPoint>>();

            // 8 persistent winners (+0.6%/day), 17 losers (-0.3%/day). Regime asset BTC trends up.
            candles["BTC"] = Coin(1, 0.004m, days);
            for (int i = 0; i < 8; i++) candles[$"WIN{i}"] = Coin(100 + i, 0.006m, days);
            for (int i = 0; i < 17; i++) candles[$"LOSE{i}"] = Coin(200 + i, -0.003m, days);

            foreach (var sym in candles.Keys)
            {
                var mc = new List<MarketCapPoint>();
                foreach (var c in candles[sym]) mc.Add(new MarketCapPoint(c.Timestamp, c.Close * 1_000_000m));
                caps[sym] = mc;
            }

            var strategy = new CrossSectionalMomentumStrategy
            {
                RegimeSymbol = "BTC",
                Config = new MomentumConfig
                {
                    MinUniverseSize = 10,
                    TopFraction = 0.30,
                    BottomFraction = 0.0,        // long-only
                    LookbackDays = 30,
                    VolLookbackDays = 30,
                    SkipDays = 1,
                    RebalanceDays = 7,
                    RegimeMaDays = 50,
                    TargetPortfolioVol = 0.5,
                    MaxGrossLeverage = 1.0,
                    MaxWeightPerAsset = 0.30,
                    LiquidityFloorUsd = 0,
                    ParticipationCap = 0,        // unbounded fills for the test
                    UniverseCap = 100,
                },
            };

            var input = new PortfolioInput { Candles = candles, MarketCaps = caps, RegimeSymbol = "BTC" };
            var cfg = new BacktestConfig
            {
                StrategyClass = "CrossSectionalMomentumStrategy",
                Coin = "BTC", Currency = "USD",
                Interval = TimeInterval.OneDay,
                CandleCount = days,
                FeeFraction = 0.0005m,
                SlippageFraction = 0.0005m,
            };

            var res = await new BacktestSession(strategy, input, cfg).RunPortfolioAsync();

            Assert.True(res.TotalFeesPaid > 0m, "strategy should have traded (paid fees)");
            Assert.True(res.TotalTrades > 0, "strategy should have completed round-trip trades on rebalances");
            Assert.True(res.TotalPnL > 0m, $"momentum should profit longing persistent winners, got {res.TotalPnL:F2}");
            Assert.Equal(days, res.EquityCurve.Count);
            Assert.All(res.EquityCurve, p => Assert.True(p.Equity > 0m));
        }

        [Fact]
        public async Task Risk_Off_Regime_Moves_Long_Only_Book_To_Cash()
        {
            const int days = 160;
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>();
            var caps = new Dictionary<string, IReadOnlyList<MarketCapPoint>>();

            // BTC crashes after day 90 → regime turns off; long-only book should de-risk to cash.
            var btc = new List<OHLCCandle>();
            decimal bp = 100m;
            for (int i = 0; i < days; i++)
            {
                btc.Add(new OHLCCandle(T0.AddDays(i), bp, bp, bp, bp, 50_000_000m));
                bp *= i < 90 ? 1.004m : 0.97m;
            }
            candles["BTC"] = btc;
            for (int i = 0; i < 20; i++) candles[$"ALT{i}"] = Coin(300 + i, i < 10 ? 0.005m : -0.004m, days);

            foreach (var sym in candles.Keys)
            {
                var mc = new List<MarketCapPoint>();
                foreach (var c in candles[sym]) mc.Add(new MarketCapPoint(c.Timestamp, c.Close * 1_000_000m));
                caps[sym] = mc;
            }

            var strategy = new CrossSectionalMomentumStrategy
            {
                RegimeSymbol = "BTC",
                Config = new MomentumConfig
                {
                    MinUniverseSize = 10, TopFraction = 0.30, BottomFraction = 0.0,
                    LookbackDays = 20, VolLookbackDays = 20, SkipDays = 1, RebalanceDays = 7,
                    RegimeMaDays = 30, TargetPortfolioVol = 0.5, MaxGrossLeverage = 1.0,
                    LiquidityFloorUsd = 0, ParticipationCap = 0, RiskOffScalar = 0.0,
                },
            };

            var input = new PortfolioInput { Candles = candles, MarketCaps = caps, RegimeSymbol = "BTC" };
            var cfg = new BacktestConfig
            {
                StrategyClass = "CrossSectionalMomentumStrategy",
                Coin = "BTC", Currency = "USD",
                Interval = TimeInterval.OneDay, CandleCount = days,
                FeeFraction = 0.0005m, SlippageFraction = 0.0005m,
            };

            var res = await new BacktestSession(strategy, input, cfg).RunPortfolioAsync();

            // By the final bar the regime is off, so the book should be flat (gross ≈ 0).
            Assert.True(res.FinalBaseBalance < res.InitialEquity * 0.05m,
                $"expected near-cash at the end, gross notional was {res.FinalBaseBalance:F2}");
        }
    }
}
