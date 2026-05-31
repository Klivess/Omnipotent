using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>Section 11 validation suite: deflated Sharpe, cost sensitivity, survivorship, walk-forward, turnover.</summary>
    public class ValidationTests
    {
        private static readonly DateTime T0 = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static List<OHLCCandle> Coin(int seed, decimal drift, int days, decimal vol = 50_000_000m)
        {
            var rng = new Random(seed);
            var list = new List<OHLCCandle>(days);
            decimal p = 100m;
            for (int i = 0; i < days; i++)
            {
                list.Add(new OHLCCandle(T0.AddDays(i), p, p, p, p, vol));
                p *= (1m + drift + (decimal)((rng.NextDouble() - 0.5) * 0.04));
                if (p < 1m) p = 1m;
            }
            return list;
        }

        // ── Deflated Sharpe (11.2) ───────────────────────────────────────────────

        [Fact]
        public void Normal_Cdf_And_Inverse_Are_Accurate()
        {
            Assert.Equal(0.5, DeflatedSharpe.NormalCdf(0), 3);
            Assert.Equal(0.975, DeflatedSharpe.NormalCdf(1.96), 2);
            Assert.Equal(1.96, DeflatedSharpe.InverseNormalCdf(0.975), 1);
        }

        [Fact]
        public void ExpectedMaxSharpe_Grows_With_Trials()
        {
            Assert.True(DeflatedSharpe.ExpectedMaxSharpe(0.5, 100) > DeflatedSharpe.ExpectedMaxSharpe(0.5, 5));
            Assert.Equal(0, DeflatedSharpe.ExpectedMaxSharpe(0.5, 1));   // a single trial has nothing to deflate against
        }

        [Fact]
        public void More_Trials_Lowers_The_Deflated_Sharpe()
        {
            var rng = new Random(7);
            var returns = new List<double>();
            for (int i = 0; i < 250; i++) returns.Add(0.004 + (rng.NextDouble() - 0.5) * 0.02);

            var oneTrial = DeflatedSharpe.Compute(returns, trials: 1, trialSharpeStd: 0.0);
            var manyTrials = DeflatedSharpe.Compute(returns, trials: 50, trialSharpeStd: 0.1);
            Assert.True(manyTrials.Dsr < oneTrial.Dsr,
                $"50 trials DSR {manyTrials.Dsr:F3} should be below single-trial DSR {oneTrial.Dsr:F3}");
            Assert.InRange(manyTrials.Dsr, 0.0, 1.0);
        }

        // ── Survivorship audit (11.4) ────────────────────────────────────────────

        [Fact]
        public void Survivorship_Audit_Flags_Delisted_Coins()
        {
            var window = new Dictionary<string, List<UniverseDailyPoint>>();
            // "alive" runs to day 200; "dead" stops at day 80 (delisted mid-window).
            window["alive"] = Enumerable.Range(0, 200).Select(i => new UniverseDailyPoint(T0.AddDays(i), 100m, 1e9m, 1e7m)).ToList();
            window["dead"] = Enumerable.Range(0, 80).Select(i => new UniverseDailyPoint(T0.AddDays(i), 50m, 1e8m, 1e6m)).ToList();

            var audit = SurvivorshipAudit.Audit(window);
            Assert.Equal(2, audit.TotalCoins);
            Assert.Equal(1, audit.DelistedCoins);
            Assert.Contains("dead", audit.DelistedExamples);
            Assert.True(audit.PointInTime);
        }

        // ── Turnover & capacity (11.5) ───────────────────────────────────────────

        [Fact]
        public void TurnoverCapacity_Produces_Positive_Capacity()
        {
            var res = new BacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 11_000m,
                Duration = TimeSpan.FromDays(28),
                EquityCurve = Enumerable.Range(0, 28).Select(i => new EquityPoint { Ts = T0.AddDays(i), MarkPrice = 0, QuoteBalance = 10_000m, BaseBalance = 0, Equity = 10_000m + i * 35m }).ToList(),
                Trades = new List<TradeRecord>
                {
                    new() { EntryTime = T0, ExitTime = T0.AddDays(7), EntryPrice = 100m, ExitPrice = 110m, Qty = 10m, IsShort = false, Fees = 1m },
                },
            };
            var cfg = new MomentumConfig { MaxWeightPerAsset = 0.2, ParticipationCap = 0.05 };
            var tc = TurnoverCapacity.Compute(res, cfg, new decimal[] { 5_000_000m, 8_000_000m, 10_000_000m });
            Assert.True(tc.EstimatedCapacityUsd > 0m);
            Assert.True(tc.WeeklyTurnover > 0m);
        }

        // ── Cost sensitivity (11.3) + Walk-forward (11.1) ────────────────────────

        private static (PortfolioInput input, Func<MomentumConfig> mkCfg) BuildUniverse(int days)
        {
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>();
            var caps = new Dictionary<string, IReadOnlyList<MarketCapPoint>>();
            candles["BTC"] = Coin(1, 0.003m, days);
            for (int i = 0; i < 8; i++) candles[$"WIN{i}"] = Coin(100 + i, 0.006m, days);
            for (int i = 0; i < 14; i++) candles[$"LOSE{i}"] = Coin(200 + i, -0.002m, days);
            foreach (var sym in candles.Keys)
                caps[sym] = candles[sym].Select(c => new MarketCapPoint(c.Timestamp, c.Close * 1_000_000m)).ToList();

            MomentumConfig Cfg() => new()
            {
                MinUniverseSize = 10, TopFraction = 0.30, BottomFraction = 0.0,
                LookbackDays = 30, VolLookbackDays = 30, SkipDays = 1, RebalanceDays = 7,
                RegimeMaDays = 50, TargetPortfolioVol = 0.5, MaxGrossLeverage = 1.0,
                MaxWeightPerAsset = 0.30, LiquidityFloorUsd = 0, ParticipationCap = 0, UniverseCap = 100,
            };
            return (new PortfolioInput { Candles = candles, MarketCaps = caps, RegimeSymbol = "BTC" }, Cfg);
        }

        [Fact]
        public async Task Cost_Sensitivity_Net_Pnl_Degrades_As_Costs_Rise()
        {
            var (input, mkCfg) = BuildUniverse(220);
            BacktestSession Factory(BacktestConfig c)
            {
                var strat = new CrossSectionalMomentumStrategy { RegimeSymbol = "BTC", Config = mkCfg() };
                return new BacktestSession(strat, input, c);
            }
            var baseCfg = new BacktestConfig
            {
                StrategyClass = "CrossSectionalMomentumStrategy", Coin = "BTC", Currency = "USD",
                Interval = TimeInterval.OneDay, CandleCount = 220, FeeFraction = 0.001m, SlippageFraction = 0.001m,
            };

            var rows = await CostSensitivity.RunAsync(Factory, baseCfg, new[] { 1.0, 2.0, 3.0 });
            Assert.Equal(3, rows.Count);
            Assert.True(rows[2].NetPnLPercent <= rows[0].NetPnLPercent,
                $"3x cost PnL {rows[2].NetPnLPercent:F2} should be <= 1x PnL {rows[0].NetPnLPercent:F2}");
            Assert.True(rows[2].TotalFees > rows[0].TotalFees);
        }

        [Fact]
        public async Task WalkForward_Produces_Stitched_Oos_Curve_And_Deflated_Sharpe()
        {
            var (input, mkCfg) = BuildUniverse(360);
            var baseCfg = new BacktestConfig
            {
                StrategyClass = "CrossSectionalMomentumStrategy", Coin = "BTC", Currency = "USD",
                Interval = TimeInterval.OneDay, CandleCount = 360, FeeFraction = 0.0005m, SlippageFraction = 0.0005m,
            };
            BacktestSession Factory(MomentumConfig p, PortfolioInput slice)
            {
                var strat = new CrossSectionalMomentumStrategy { RegimeSymbol = "BTC", Config = p };
                return new BacktestSession(strat, slice, baseCfg);
            }

            var grid = WalkForward.DefaultGrid(mkCfg());
            var res = await WalkForward.RunAsync(input, grid, Factory, initialEquity: 10_000m,
                inSampleDays: 150, oosDays: 45, warmupDays: 62);

            Assert.True(res.Folds > 0, "expected at least one walk-forward fold");
            Assert.Equal(grid.Count, res.TrialsPerFold);
            Assert.True(res.StitchedOosEquity.Count > 1);
            Assert.InRange(res.DeflatedSharpe, 0.0, 1.0);
        }
    }
}
