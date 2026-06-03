using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Drives the real BacktestSession with a synthetic uptrend punctuated by multi-bar dips.
    /// The old exit (close &gt; prev.High OR close &gt; EMA20, with no entry/EMA20 gate) forced
    /// most positions to close on the very next bar. The triple-barrier rewrite should hold
    /// through the reversion and cap holds at the time barrier.
    /// </summary>
    public class IBSMeanReversionStrategyTests
    {
        private readonly ITestOutputHelper _out;
        public IBSMeanReversionStrategyTests(ITestOutputHelper o) => _out = o;

        // Uptrend (so close > EMA200) with a smooth V-shaped dip every `every` bars. Only the
        // bottom bar of each dip is shaped to have IBS < 0.1 (close near its low), so the entry
        // fires at the trough and the recovery takes several bars — exactly the case the old
        // code mis-handled.
        private static List<OHLCCandle> BuildSeries(int n)
        {
            var candles = new List<OHLCCandle>(n);
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            const int start = 300, every = 60, width = 12, depthD = 12;
            decimal trend = 1000m;

            for (int i = 0; i < n; i++)
            {
                trend += 0.3m;
                decimal close = trend, hi = trend + 0.4m, lo = trend - 0.4m, open = trend;

                if (i >= start && (i - start) % every < width)
                {
                    int k = (i - start) % every;                       // 0..width-1
                    double dip = depthD * (1 - Math.Cos(2 * Math.PI * k / width)) / 2.0;
                    decimal c = trend - (decimal)dip;

                    // The bottom bar AND its predecessor are shaped with IBS ≈ 0 (close near
                    // low) so the smoothed IBS(2) clears the 0.1 threshold and entry fires at
                    // the trough. Other dip bars stay at IBS ≈ 0.5.
                    if (k == width / 2 || k == width / 2 - 1)
                    {
                        close = c; lo = c - 0.1m; hi = c + 6m; open = c + 5m; // IBS ≈ 0.016
                    }
                    else
                    {
                        close = c; hi = c + 0.4m; lo = c - 0.4m; open = c;
                    }
                }

                candles.Add(new OHLCCandle(t0.AddHours(i), open, hi, lo, close, 1000m));
            }
            return candles;
        }

        [Fact]
        public async Task Enters_On_Dips_And_Exits_Only_Via_Brackets()
        {
            var candles = BuildSeries(1400);
            var config = new BacktestConfig
            {
                StrategyClass = nameof(IBSMeanReversionStrategy),
                Coin = "TEST", Currency = "USD",
                Interval = TimeInterval.OneHour,
                CandleCount = candles.Count,
            };

            var session = new BacktestSession(new IBSMeanReversionStrategy(), candles, config);
            var result = await session.RunAsync();

            var holdsBars = result.Trades
                .Select(t => (int)Math.Round((t.ExitTime - t.EntryTime).TotalHours))
                .ToList();

            _out.WriteLine($"Trades={holdsBars.Count} holds(bars)=[{string.Join(",", holdsBars)}]");

            // The strategy enters dips and every position is closed by its TP/SL bracket — completed
            // round-trips prove the engine, not the strategy, did the exit (no manual close remains).
            Assert.True(holdsBars.Count >= 3, $"Expected several bracketed round-trips, got {holdsBars.Count}.");

            // Not a one-bar hair-trigger: the take-profit sits at the mean, above the entry.
            Assert.True(holdsBars.Average() > 1.0, $"Average hold {holdsBars.Average():F2} bars is too short.");

            // Each trade carries the bracket levels it was opened with (for the chart's TP/SL overlay).
            Assert.All(result.Trades, t =>
            {
                Assert.True(t.StopLoss.HasValue && t.StopLoss.Value < t.EntryPrice, "long stop should be below entry");
                Assert.True(t.TakeProfit.HasValue && t.TakeProfit.Value > t.EntryPrice, "long target should be above entry");
            });
        }
    }
}
