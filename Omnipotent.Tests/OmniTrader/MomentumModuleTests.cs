using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Unit tests for the pure cross-sectional momentum modules (Sections 3-9 of the spec).
    /// Each is a pure function of point-in-time data, so they test in isolation without the engine.
    /// </summary>
    public class MomentumModuleTests
    {
        private static readonly DateTime T0 = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Daily candle series from explicit closes; O=H=L=C, given quote volume.
        private static List<OHLCCandle> Series(IEnumerable<decimal> closes, decimal volume = 10_000_000m)
        {
            var list = new List<OHLCCandle>();
            int i = 0;
            foreach (var c in closes) { list.Add(new OHLCCandle(T0.AddDays(i++), c, c, c, c, volume)); }
            return list;
        }

        private static List<decimal> Ramp(decimal start, decimal dailyMul, int n)
        {
            var v = new List<decimal>(n);
            decimal p = start;
            for (int i = 0; i < n; i++) { v.Add(p); p *= dailyMul; }
            return v;
        }

        // Trending but genuinely volatile (alternating ±moves) so realized-return vol is well above
        // the peg threshold. `bias` > 1 trends up, < 1 trends down.
        private static List<decimal> Noisy(decimal start, decimal bias, int n)
        {
            var v = new List<decimal>(n);
            decimal p = start;
            for (int i = 0; i < n; i++) { v.Add(p); p *= (i % 2 == 0 ? 1.08m * bias : 0.95m * bias); }
            return v;
        }

        // ── Signals (Section 4) ──────────────────────────────────────────────────

        [Fact]
        public void Signal_Is_Positive_For_Uptrend_And_Negative_For_Downtrend()
        {
            var cfg = new MomentumConfig { LookbackDays = 30, SkipDays = 1, VolLookbackDays = 30 };
            var up = MomentumSignals.Compute(Series(Noisy(100m, 1.0m, 70)), cfg);     // net uptrend
            var down = MomentumSignals.Compute(Series(Noisy(100m, 0.95m, 70)), cfg);  // net downtrend

            Assert.NotNull(up);
            Assert.NotNull(down);
            Assert.True(up!.Value.CumReturn > 0m);
            Assert.True(down!.Value.CumReturn < 0m);
            Assert.True(up.Value.Score > down.Value.Score);
            Assert.True(up.Value.RealizedVol > 0m);
        }

        [Fact]
        public void Signal_Returns_Null_When_History_Too_Short()
        {
            var cfg = new MomentumConfig { LookbackDays = 30, SkipDays = 1, VolLookbackDays = 30 };
            Assert.Null(MomentumSignals.Compute(Series(Ramp(100m, 1.01m, 20)), cfg));
        }

        [Fact]
        public void RiskAdjusted_Rewards_Smoother_Path_For_Equal_Return()
        {
            var cfg = new MomentumConfig { LookbackDays = 20, SkipDays = 0, VolLookbackDays = 20, UseRiskAdjusted = true };
            // Two names: one with small daily moves, one with large daily moves.
            var smooth = new List<decimal>();
            var choppy = new List<decimal>();
            decimal ps = 100m, pc = 100m;
            for (int i = 0; i < 40; i++)
            {
                smooth.Add(ps); ps *= (i % 2 == 0 ? 1.010m : 0.995m);
                choppy.Add(pc); pc *= (i % 2 == 0 ? 1.10m : 0.95m);
            }
            var s1 = MomentumSignals.Compute(Series(smooth), cfg)!.Value;
            var s2 = MomentumSignals.Compute(Series(choppy), cfg)!.Value;
            Assert.True(s1.RealizedVol < s2.RealizedVol);
        }

        // ── Selection (Section 5) ────────────────────────────────────────────────

        [Fact]
        public void Selection_Skips_When_Universe_Too_Small()
        {
            var cfg = new MomentumConfig { MinUniverseSize = 20 };
            var scored = Enumerable.Range(0, 10).Select(i => ($"C{i}", (decimal)i)).ToList();
            Assert.True(Selection.Select(scored, cfg).Skip);
        }

        [Fact]
        public void Selection_Picks_Top_And_Bottom_Without_Overlap()
        {
            var cfg = new MomentumConfig { MinUniverseSize = 5, TopFraction = 0.20, BottomFraction = 0.20 };
            var scored = Enumerable.Range(0, 10).Select(i => ($"C{i}", (decimal)i)).ToList(); // C9 best
            var sel = Selection.Select(scored, cfg);
            Assert.False(sel.Skip);
            Assert.Equal(2, sel.Longs.Count);
            Assert.Equal(2, sel.Shorts.Count);
            Assert.Contains("C9", sel.Longs);
            Assert.Contains("C0", sel.Shorts);
            Assert.Empty(sel.Longs.Intersect(sel.Shorts));
        }

        // ── Regime (Section 6) ───────────────────────────────────────────────────

        [Fact]
        public void Regime_On_Above_MA_Off_Below()
        {
            var cfg = new MomentumConfig { RegimeMaDays = 50 };
            Assert.True(RegimeFilter.RegimeOn(Series(Ramp(100m, 1.01m, 60)), cfg.RegimeMaDays));
            Assert.False(RegimeFilter.RegimeOn(Series(Ramp(100m, 0.99m, 60)), cfg.RegimeMaDays));
            // Not enough history → default risk-on (don't block).
            Assert.True(RegimeFilter.RegimeOn(Series(Ramp(100m, 0.99m, 10)), cfg.RegimeMaDays));
        }

        // ── Sizing (Section 7) ───────────────────────────────────────────────────

        [Fact]
        public void Sizing_Inverse_Vol_Gives_Lower_Vol_Name_More_Weight()
        {
            var cfg = new MomentumConfig { TargetPortfolioVol = 1.0, MaxWeightPerAsset = 1.0, MaxGrossLeverage = 1.0 };
            var vols = new Dictionary<string, decimal> { ["A"] = 0.2m, ["B"] = 0.8m };
            var w = Sizing.TargetWeights(new[] { "A", "B" }, Array.Empty<string>(), vols, cfg);
            Assert.True(w["A"] > w["B"]);
            Assert.True(w["A"] > 0m && w["B"] > 0m);
        }

        [Fact]
        public void Sizing_Respects_Per_Asset_Cap_And_Gross_Clamp()
        {
            var cfg = new MomentumConfig { TargetPortfolioVol = 5.0, MaxWeightPerAsset = 0.20, MaxGrossLeverage = 1.0 };
            var vols = new Dictionary<string, decimal> { ["A"] = 0.5m, ["B"] = 0.5m, ["C"] = 0.5m, ["D"] = 0.5m, ["E"] = 0.5m, ["F"] = 0.5m };
            var longs = new[] { "A", "B", "C", "D", "E", "F" };
            var w = Sizing.TargetWeights(longs, Array.Empty<string>(), vols, cfg);
            Assert.All(w.Values, v => Assert.True(Math.Abs(v) <= 0.20m + 1e-9m));
            Assert.True(w.Values.Sum(Math.Abs) <= 1.0m + 1e-6m);
        }

        [Fact]
        public void Sizing_Shorts_Are_Negative()
        {
            var cfg = new MomentumConfig { TargetPortfolioVol = 0.4, MaxWeightPerAsset = 1.0, MaxGrossLeverage = 2.0 };
            var vols = new Dictionary<string, decimal> { ["L"] = 0.4m, ["S"] = 0.4m };
            var w = Sizing.TargetWeights(new[] { "L" }, new[] { "S" }, vols, cfg);
            Assert.True(w["L"] > 0m);
            Assert.True(w["S"] < 0m);
        }

        // ── Universe (Section 3) ─────────────────────────────────────────────────

        [Fact]
        public void Universe_Excludes_Stable_Lowvol_And_Illiquid_Then_Caps_By_Mcap()
        {
            var cfg = new MomentumConfig
            {
                MinUniverseSize = 1, LookbackDays = 30, VolLookbackDays = 30, SkipDays = 1,
                LiquidityFloorUsd = 1_000_000, UniverseCap = 2
            };
            int days = cfg.MinHistoryDays + 5;
            var snaps = new List<AssetSnapshot>
            {
                new() { Symbol = "BTCUSD", History = Series(Noisy(100m, 1.0m, days), 50_000_000m), MarketCap = 1_000m },
                new() { Symbol = "ETHUSD", History = Series(Noisy(100m, 1.0m, days), 50_000_000m), MarketCap = 900m },
                new() { Symbol = "ALTUSD", History = Series(Noisy(100m, 1.0m, days), 50_000_000m), MarketCap = 100m },
                // Stable: flat → 30d vol ~0 → excluded by peg check (and the denylist).
                new() { Symbol = "USDTUSD", History = Series(Enumerable.Repeat(1m, days), 99_000_000m), MarketCap = 5_000m },
                // Illiquid: volume below floor → excluded.
                new() { Symbol = "THINUSD", History = Series(Noisy(100m, 1.0m, days), 1_000m), MarketCap = 8_000m },
                // Too little history → excluded.
                new() { Symbol = "NEWUSD", History = Series(Noisy(100m, 1.0m, 10), 50_000_000m), MarketCap = 9_000m },
            };
            var uni = UniverseBuilder.Build(snaps, cfg);
            var syms = uni.Select(a => a.Symbol).ToList();
            Assert.DoesNotContain("USDTUSD", syms);
            Assert.DoesNotContain("THINUSD", syms);
            Assert.DoesNotContain("NEWUSD", syms);
            Assert.Equal(2, uni.Count);                 // capped to top 2 by mcap
            Assert.Equal(new[] { "BTCUSD", "ETHUSD" }, syms);
        }

        // ── Rebalance (Section 8) ────────────────────────────────────────────────

        [Fact]
        public void Rebalance_Diffs_Targets_Against_Current_And_Exits_Dropped_Names()
        {
            var cfg = new MomentumConfig { ParticipationCap = 0 };
            var targets = new Dictionary<string, decimal> { ["A"] = 0.5m, ["B"] = 0.5m };   // want A,B; not C
            var current = new Dictionary<string, decimal> { ["C"] = 10m };                   // holding C
            var marks = new Dictionary<string, decimal> { ["A"] = 100m, ["B"] = 50m, ["C"] = 20m };
            var vol = new Dictionary<string, decimal>();
            var orders = Rebalance.BuildOrders(targets, current, marks, vol, equity: 10_000m, cfg);

            var bySym = orders.ToDictionary(o => o.Symbol);
            Assert.True(bySym.ContainsKey("C") && bySym["C"].Side == OrderSide.Sell && bySym["C"].Qty == 10m); // full exit
            Assert.Equal(OrderSide.Buy, bySym["A"].Side);
            Assert.Equal(50m, bySym["A"].Qty);   // 0.5*10000/100
            Assert.Equal(100m, bySym["B"].Qty);  // 0.5*10000/50
        }

        [Fact]
        public void Rebalance_Participation_Cap_Limits_Order_Size()
        {
            var cfg = new MomentumConfig { ParticipationCap = 0.05 };
            var targets = new Dictionary<string, decimal> { ["A"] = 1.0m };
            var current = new Dictionary<string, decimal>();
            var marks = new Dictionary<string, decimal> { ["A"] = 100m };
            var vol = new Dictionary<string, decimal> { ["A"] = 100_000m }; // 5% = 5,000 quote = 50 units
            var orders = Rebalance.BuildOrders(targets, current, marks, vol, equity: 1_000_000m, cfg);
            Assert.Single(orders);
            Assert.Equal(50m, orders[0].Qty); // capped to 5% of volume, not the full 10,000 units
        }

        // ── Killswitch (Section 9) ───────────────────────────────────────────────

        [Fact]
        public void Killswitch_Trips_On_Drawdown_And_Resets_On_Recovery()
        {
            var cfg = new MomentumConfig { DdKillswitch = 0.30 };
            var ks = new KillswitchState();
            Assert.False(ks.Update(100m, cfg));   // new peak
            Assert.False(ks.Update(80m, cfg));    // -20%, still ok
            Assert.True(ks.Update(65m, cfg));     // -35% ⇒ tripped
            Assert.True(ks.Update(80m, cfg));     // below reset (peak*0.85=85) ⇒ still paused
            Assert.False(ks.Update(86m, cfg));    // above reset ⇒ resumes
        }
    }
}
