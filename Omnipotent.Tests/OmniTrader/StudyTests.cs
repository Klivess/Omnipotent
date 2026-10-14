using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Studies end to end, offline: the candle cache is seeded with synthetic bars that end at the last
    /// closed bar, so the router serves them as fresh without touching the network.
    /// </summary>
    public class StudyTests : IDisposable
    {
        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly StrategyRegistry registry = new();
        private readonly MarketDataRouter router;
        private readonly BacktestJobQueue queue;
        private readonly StudioService studio;
        private readonly StudyRunner runner;
        private readonly CancellationTokenSource cts = new();

        public StudyTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-study-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            var cache = new CandleCacheRepository(db);
            router = new MarketDataRouter(cache);
            var jobs = new BacktestJobRepository(db);
            queue = new BacktestJobQueue(jobs, router, registry, new UniverseRepository(db), _ => { }, (_, _) => { });
            studio = new StudioService(db, registry, queue, router, _ => { });
            runner = new StudyRunner(db, queue, jobs, (_, _) => Task.FromResult(new List<string>()), (_, _) => { });
            runner.Start(cts.Token);
            Seed(cache, "AAAUSDT", 1).GetAwaiter().GetResult();
            Seed(cache, "BBBUSDT", 2).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            cts.Cancel();
            db.Dispose();
            try { File.Delete(dbPath); } catch { }
        }

        private static async Task Seed(CandleCacheRepository cache, string symbol, int seed, int n = 900)
        {
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, TimeInterval.OneDay);
            var rng = new Random(seed);
            double price = 100;
            var bars = new List<OHLCCandle>(n);
            for (int i = 0; i < n; i++)
            {
                // Trending regimes with noise, so moving-average strategies have something to trade.
                double drift = Math.Sin(i / 60.0) * 0.004;
                double next = Math.Max(5, price * (1 + drift + (rng.NextDouble() - 0.5) * 0.03));
                var t = last.AddDays(i - (n - 1));
                bars.Add(new OHLCCandle(t, (decimal)price, (decimal)(Math.Max(price, next) * 1.01), (decimal)(Math.Min(price, next) * 0.99), (decimal)next, 1000));
                price = next;
            }
            await cache.UpsertManyAsync(symbol, TimeInterval.OneDay, bars);
        }

        private async Task<StudyRecord> WaitAsync(string id)
        {
            for (int i = 0; i < 600; i++)
            {
                var s = await runner.GetAsync(id);
                if (s != null && s.Status is StudyStatus.Done or StudyStatus.Failed or StudyStatus.Cancelled) return s;
                await Task.Delay(100);
            }
            throw new TimeoutException("study did not finish");
        }

        private async Task<BacktestConfig> BaseConfigAsync(string symbol = "AAAUSDT")
        {
            var s = await studio.CreateFromTemplateAsync("ema-cross", "klives");
            return studio.BuildConfig(s, new StudioRunRequest { Symbol = symbol, Bars = 800, Execution = ExecutionTiming.NextOpen }, "test");
        }

        [Fact]
        public void Combinations_Grid_And_Random()
        {
            var req = new StudyRequest
            {
                Base = new BacktestConfig { StrategyClass = "x", Coin = "X", Currency = "", Interval = TimeInterval.OneDay, CandleCount = 10 },
                Params = { new SweepParam { Name = "a", Min = 1, Max = 5, Step = 1, Integer = true }, new SweepParam { Name = "b", Values = { 10, 20 } } }
            };
            Assert.Equal(10, StudyRunner.Combinations(req).Count);
            req.MaxRuns = 4;
            var random = StudyRunner.Combinations(req);
            Assert.Equal(4, random.Count);
            Assert.Equal(4, random.Select(c => $"{c["a"]}|{c["b"]}").Distinct().Count());
        }

        [Fact]
        public async Task Sweep_Ranks_Runs_And_Deflates_The_Winner()
        {
            var config = await BaseConfigAsync();
            var record = await runner.EnqueueAsync(new StudyRequest
            {
                Kind = StudyKind.Sweep,
                Base = config,
                Params = { new SweepParam { Name = "fast", Values = { 5, 10, 20 } }, new SweepParam { Name = "slow", Values = { 30, 60, 90 } } },
                MinTrades = 1,
            }, "test");
            var done = await WaitAsync(record.Id);
            Assert.Equal(StudyStatus.Done, done.Status);
            var r = done.Result!;
            Assert.Equal(9, r.Runs.Count);
            Assert.NotNull(r.Best);
            Assert.NotNull(r.Best!.Equity);
            Assert.NotNull(r.DeflatedSharpe);
            Assert.InRange(r.DeflatedSharpe!.Value, 0, 1);
            Assert.Equal(3, r.Heatmap!.XValues.Count);
            Assert.Equal(3, r.Heatmap.Z.Count);
            Assert.True(r.Runs.Zip(r.Runs.Skip(1)).All(p => p.First.Objective >= p.Second.Objective));
        }

        [Fact]
        public async Task Walk_Forward_Stitches_Out_Of_Sample()
        {
            var config = await BaseConfigAsync();
            var record = await runner.EnqueueAsync(new StudyRequest
            {
                Kind = StudyKind.WalkForward, Base = config,
                Params = { new SweepParam { Name = "fast", Values = { 5, 15 } }, new SweepParam { Name = "slow", Values = { 40, 80 } } },
                InSampleBars = 300, OutOfSampleBars = 100, MinTrades = 1,
            }, "test");
            var done = await WaitAsync(record.Id);
            Assert.Equal(StudyStatus.Done, done.Status);
            Assert.True(done.Result!.Folds.Count >= 4);
            Assert.All(done.Result.Folds, f => Assert.True(f.OutOfSampleFrom > f.InSampleTo));
            Assert.NotEmpty(done.Result.OutOfSampleEquity);
            Assert.NotNull(done.Result.OutOfSample);
        }

        [Fact]
        public async Task Monte_Carlo_Percentiles_Are_Ordered()
        {
            var config = await BaseConfigAsync();
            var record = await runner.EnqueueAsync(new StudyRequest { Kind = StudyKind.MonteCarlo, Base = config, Iterations = 500 }, "test");
            var done = await WaitAsync(record.Id);
            Assert.Equal(StudyStatus.Done, done.Status);
            var mc = done.Result!.MonteCarlo!;
            if (mc.Trades >= 3)
            {
                Assert.True(mc.FinalReturnPct["p5"] <= mc.FinalReturnPct["p50"] && mc.FinalReturnPct["p50"] <= mc.FinalReturnPct["p95"]);
                Assert.True(mc.MaxDrawdownPct["p5"] <= mc.MaxDrawdownPct["p95"]);
                Assert.NotEmpty(mc.SamplePaths);
            }
        }

        [Fact]
        public async Task Batch_Runs_Every_Symbol()
        {
            var config = await BaseConfigAsync();
            var record = await runner.EnqueueAsync(new StudyRequest { Kind = StudyKind.Batch, Base = config, Symbols = { "AAAUSDT", "BBBUSDT" } }, "test");
            var done = await WaitAsync(record.Id);
            Assert.Equal(StudyStatus.Done, done.Status);
            Assert.Equal(2, done.Result!.Runs.Count);
            Assert.All(done.Result.Runs, r => Assert.NotNull(r.Metrics));
            Assert.Equal(2, done.Result.Batch!.Symbols);
        }

        [Fact]
        public async Task Validation_Runs_Single_Symbol_Strategies()
        {
            var config = await BaseConfigAsync();
            var withValidation = new BacktestConfig
            {
                StrategyClass = config.StrategyClass, Coin = config.Coin, Currency = "", Interval = config.Interval, CandleCount = 800,
                InitialQuoteBalance = 10_000, FeeFraction = 0.001m, SlippageFraction = 0.0005m, Execution = ExecutionTiming.NextOpen,
                Parameters = config.Parameters, Validation = new ValidationSettings { InSampleBars = 300, OosBars = 100, WarmupBars = 60, MaxGridCombos = 6 }
            };
            var result = await queue.RunAsync(withValidation);
            Assert.NotNull(result.Validation);
            Assert.NotEmpty(result.Validation!.CostSensitivity);
            // Before the fix a single-symbol strategy got universe bars here and never traded.
            Assert.Contains(result.Validation.CostSensitivity, c => c.Trades > 0);
        }
    }
}
