using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// The generic post-backtest validation runs for ANY universe strategy: it builds a sweep grid from
    /// the strategy's [Param] ranges, walk-forwards it, and reports cost sensitivity + turnover — with no
    /// strategy-specific code. Uses a throwaway non-momentum universe strategy with one sweepable param.
    /// </summary>
    public class GenericValidationTests
    {
        // Holds the top-N highest-priced symbols, equal weight. TopN is a sweepable [Param].
        private sealed class TopNUniverseStrategy : TradingStrategy
        {
            [Param("Top N", Group = "Selection", Min = 1, Max = 5)]
            public int TopN { get; set; } = 2;

            public override StrategySymbols DeclareSymbols()
                => StrategySymbols.FromUniverse(new UniverseSpec { TopN = 10, RegimeSymbol = "AAA" });

            public override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
            {
                var marks = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, h) in bar.Histories) if (h.Count > 0) marks[sym] = h[^1].Close;

                var targets = marks.OrderByDescending(kv => kv.Value).Take(Math.Max(1, TopN))
                    .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var (sym, qty) in Positions)
                    if (!targets.Contains(sym) && qty != 0m)
                        await Order(sym, qty > 0m ? OrderSide.Sell : OrderSide.Buy, Math.Abs(qty), ct);

                decimal per = Equity * 0.9m / targets.Count;
                foreach (var sym in targets)
                {
                    if ((Positions.TryGetValue(sym, out var held) && held != 0m) || !marks.TryGetValue(sym, out var px) || px <= 0m) continue;
                    decimal qty = per / px;
                    if (qty > 0m) await Order(sym, OrderSide.Buy, qty, ct);
                }
            }

            private Task Order(string sym, OrderSide side, decimal qty, CancellationToken ct) => SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = side, Type = OrderType.Market, Symbol = sym, Qty = qty,
            }, ct);
        }

        private static IReadOnlyList<OHLCCandle> Series(DateTime t0, int n, decimal start, decimal slope)
        {
            var list = new List<OHLCCandle>(n);
            for (int i = 0; i < n; i++)
            {
                decimal p = start + slope * i;
                list.Add(new OHLCCandle(t0.AddDays(i), p, p, p, p, 1_000_000m));
            }
            return list;
        }

        [Fact]
        public async Task Validation_Runs_Generically_For_A_Universe_Strategy()
        {
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>
                {
                    ["AAA"] = Series(t0, 60, 100m, 1.0m),
                    ["BBB"] = Series(t0, 60, 120m, 0.3m),
                    ["CCC"] = Series(t0, 60, 90m, 0.6m),
                    ["DDD"] = Series(t0, 60, 110m, -0.2m),
                },
                RegimeSymbol = "AAA",
            };
            var config = new BacktestConfig
            {
                StrategyClass = nameof(TopNUniverseStrategy),
                Coin = "AAA", Currency = "",
                Interval = TimeInterval.OneDay, CandleCount = 60,
                FeeFraction = 0.001m, SlippageFraction = 0m,
            };

            var primary = await new BacktestSession(new TopNUniverseStrategy(), input, config).RunPortfolioAsync();

            var schema = StrategyParams.For(typeof(TopNUniverseStrategy));
            var baseParams = new Dictionary<string, object?> { ["TopN"] = 2 };
            var settings = new ValidationSettings
            {
                InSampleBars = 15, OosBars = 10, WarmupBars = 5,
                MaxGridCombos = 24, CostMultipliers = new[] { 1.0, 2.0, 3.0 },
            };

            var report = await GenericValidation.RunAsync(
                input, config, baseParams, schema,
                p => { var s = new TopNUniverseStrategy(); StrategyParams.Apply(s, p); return s; },
                primary, settings);

            // Cost sensitivity ran at every multiplier.
            Assert.Equal(3, report.CostSensitivity.Count);

            // The sweep grid was built from TopN's [Param] range (≥2 values) and walk-forward used it.
            Assert.True(report.TrialsTested >= 2, $"Expected a parameter sweep, trials={report.TrialsTested}");
            Assert.True(report.WalkForwardFolds >= 1, $"Expected ≥1 walk-forward fold, got {report.WalkForwardFolds}");

            // Gross PnL is reported and is ≥ net (fees only ever reduce returns).
            Assert.True(report.GrossPnLPercent >= report.NetPnLPercent - 0.001m);
        }
    }
}
