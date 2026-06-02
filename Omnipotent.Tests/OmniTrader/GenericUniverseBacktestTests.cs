using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Proves the multi-symbol backtest engine is generic: an arbitrary universe strategy with ZERO
    /// momentum-specific dependencies runs through the same RunPortfolioAsync path. The engine decides
    /// single- vs multi-symbol purely from DeclareSymbols() — no per-strategy branching.
    /// </summary>
    public class GenericUniverseBacktestTests
    {
        // Each bar, hold (long) the single highest-priced symbol; flatten everything else. No momentum
        // helpers, no injected tickers/shortable, no special config — just the generic OnUniverseBar API.
        private sealed class TopSymbolStrategy : TradingStrategy
        {
            public override StrategySymbols DeclareSymbols()
                => StrategySymbols.FromUniverse(new UniverseSpec { TopN = 5, RegimeSymbol = "AAA" });

            public override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
            {
                string? best = null;
                decimal bestPx = 0m;
                var marks = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, h) in bar.Histories)
                {
                    if (h.Count == 0) continue;
                    marks[sym] = h[^1].Close;
                    if (h[^1].Close > bestPx) { bestPx = h[^1].Close; best = sym; }
                }
                if (best == null) return;

                // Exit anything that isn't the winner.
                foreach (var (sym, qty) in Positions)
                {
                    if (sym == best || qty == 0m) continue;
                    await SubmitOrder(new OrderRequest
                    {
                        IntentId = Guid.NewGuid().ToString("N"),
                        Side = qty > 0m ? OrderSide.Sell : OrderSide.Buy,
                        Type = OrderType.Market, Symbol = sym, Qty = Math.Abs(qty),
                    }, ct);
                }

                // Enter the winner if flat in it.
                if (!Positions.TryGetValue(best, out var held) || held == 0m)
                {
                    decimal qty = Equity * 0.95m / bestPx;
                    if (qty > 0m)
                        await SubmitOrder(new OrderRequest
                        {
                            IntentId = Guid.NewGuid().ToString("N"),
                            Side = OrderSide.Buy, Type = OrderType.Market, Symbol = best, Qty = qty,
                        }, ct);
                }
            }
        }

        [Fact]
        public async Task Arbitrary_Universe_Strategy_Runs_Through_Generic_Portfolio_Backtest()
        {
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            IReadOnlyList<OHLCCandle> Series(decimal start, decimal slope)
            {
                var list = new List<OHLCCandle>();
                for (int i = 0; i < 30; i++)
                {
                    decimal p = start + slope * i;
                    list.Add(new OHLCCandle(t0.AddDays(i), p, p, p, p, 1_000_000m));
                }
                return list;
            }

            // BBB leads early (flat 150); AAA rises and overtakes it around bar 17 — forcing the
            // strategy to close BBB (a completed round-trip) and rotate into AAA, which it then holds.
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>
                {
                    ["AAA"] = Series(100m, 3.0m),    // 100 → 187, overtakes BBB ~bar 17
                    ["BBB"] = Series(150m, 0.0m),    // flat leader early
                    ["CCC"] = Series(120m, 0.0m),
                },
                RegimeSymbol = "AAA",
            };
            var config = new BacktestConfig
            {
                StrategyClass = nameof(TopSymbolStrategy),
                Coin = "AAA", Currency = "",
                Interval = TimeInterval.OneDay,
                CandleCount = 30,
                FeeFraction = 0m, SlippageFraction = 0m,
            };

            var result = await new BacktestSession(new TopSymbolStrategy(), input, config).RunPortfolioAsync();

            Assert.True(result.TotalTrades >= 1, "Generic universe strategy should have completed a round-trip (BBB→AAA rotation).");
            Assert.True(result.FinalEquity > result.InitialEquity,
                $"Rotating into the rising symbol should profit; final {result.FinalEquity} vs initial {result.InitialEquity}");
        }
    }
}
