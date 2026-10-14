using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Scripting;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Studio.Blueprints;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// The strategy studio: blueprints compile to formulas and trade as written; scripts compile under
    /// the sandbox; the guard stops runaway loops and recursion; saved strategies resolve by class name,
    /// including pinned versions.
    /// </summary>
    public class StudioTests : IDisposable
    {
        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly StrategyRegistry registry = new();
        private readonly StudioService studio;

        public StudioTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-studio-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            var router = new MarketDataRouter(new CandleCacheRepository(db));
            var queue = new BacktestJobQueue(new BacktestJobRepository(db), router, registry, new UniverseRepository(db), _ => { }, (_, _) => { });
            studio = new StudioService(db, registry, queue, router, _ => { });
        }

        public void Dispose()
        {
            db.Dispose();
            try { File.Delete(dbPath); } catch { }
        }

        private static List<OHLCCandle> Series(Func<int, double> close, int n = 400)
        {
            var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var list = new List<OHLCCandle>(n);
            double prev = close(0);
            for (int i = 0; i < n; i++)
            {
                double c = close(i);
                double o = prev;
                list.Add(new OHLCCandle(t0.AddDays(i), (decimal)o, (decimal)(Math.Max(o, c) * 1.002), (decimal)(Math.Min(o, c) * 0.998), (decimal)c, 1000));
                prev = c;
            }
            return list;
        }

        private static BacktestConfig Config(string strategyClass, string symbol, ExecutionTiming timing = ExecutionTiming.SignalClose) => new()
        {
            StrategyClass = strategyClass, Coin = symbol, Currency = "", Interval = TimeInterval.OneDay, CandleCount = 400,
            FeeFraction = 0, SlippageFraction = 0, InitialQuoteBalance = 10_000, Execution = timing,
            Parameters = new() { ["TradeSymbol"] = symbol }
        };

        // ── blueprints ────────────────────────────────────────────────────────────

        [Fact]
        public void Every_Template_Compiles()
        {
            foreach (var t in StudioTemplates.All)
            {
                var check = studio.Check(t.Kind, t.Blueprint, t.Source, "tpl_" + t.Id.Replace("-", "_"));
                Assert.True(check.Ok, $"{t.Id}: {string.Join("; ", check.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Message))}");
            }
        }

        [Fact]
        public void Blueprint_Compiles_To_Readable_Formulas()
        {
            var bp = StudioTemplates.All.First(t => t.Id == "rsi2-reversion").Blueprint!;
            var compiled = BlueprintCompiler.Compile(bp);
            Assert.True(compiled.Ok);
            Assert.Contains("rsi(length=rsiLen)", compiled.Texts["entryLong"]);
            Assert.Contains("sma(length=trend)", compiled.Texts["entryLong"]);
            Assert.Contains("TradeSymbol", compiled.Parameters.Select(p => p.Name));
            Assert.Contains("entry", compiled.Parameters.Select(p => p.Name));
        }

        [Fact]
        public void Blueprint_Errors_Point_At_The_Rule()
        {
            var bp = new Blueprint
            {
                Inputs = { new BlueprintInput { Name = "close", Type = "int", Default = 3 } },
                Rules = new()
                {
                    EntryLong = new ConditionGroup
                    {
                        Conditions = { new ConditionNode { Left = new Operand { Kind = "indicator", Indicator = "nope" }, Op = ">", Right = new Operand { Kind = "value", Value = 1 } } }
                    }
                }
            };
            var compiled = BlueprintCompiler.Compile(bp);
            Assert.False(compiled.Ok);
            Assert.Contains(compiled.Diagnostics, d => d.Path == "inputs[0]" && d.Message.Contains("reserved"));
            Assert.Contains(compiled.Diagnostics, d => d.Path!.StartsWith("rules.entryLong.conditions[0]") && d.Message.Contains("Unknown indicator"));
        }

        [Fact]
        public async Task Blueprint_Trades_A_Moving_Average_Cross()
        {
            // Down, then up, then down: one long trade from the up-cross to the down-cross.
            var candles = Series(i => i < 150 ? 200 - i * 0.5 : i < 280 ? 125 + (i - 150) * 1.0 : 255 - (i - 280) * 1.2);
            var bp = StudioTemplates.All.First(t => t.Id == "ema-cross").Blueprint!;
            var compiled = BlueprintCompiler.Compile(bp);
            var strategy = new BlueprintStrategy(compiled);
            StrategyParams.Apply(strategy, new Dictionary<string, object?> { ["TradeSymbol"] = "TESTUSDT", ["fast"] = 10, ["slow"] = 30 });
            var result = await new BacktestSession(strategy, candles, Config("bp", "TESTUSDT")).RunAsync();
            Assert.True(result.TotalTrades >= 1, "expected a trade");
            var trade = result.Trades[0];
            Assert.False(trade.IsShort);
            Assert.True(trade.EntryTime > candles[150].Timestamp && trade.EntryTime < candles[200].Timestamp, $"entry {trade.EntryTime}");
            Assert.True(trade.ExitPrice > trade.EntryPrice, "the up-leg should be profitable");
        }

        [Fact]
        public async Task Blueprint_Stop_Loss_Exits_Through_The_Bracket()
        {
            // Enters on bar 30 (close > sma), then the price collapses: the 5% stop must fire.
            var candles = Series(i => i < 30 ? 100 : i < 32 ? 102 : 102 - (i - 31) * 3, 60);
            var bp = new Blueprint
            {
                Universe = new() { Mode = "single", Symbol = "TESTUSDT" },
                Rules = new()
                {
                    EntryLong = new ConditionGroup { Conditions = { new ConditionNode { Left = new Operand { Kind = "price" }, Op = ">", Right = new Operand { Kind = "indicator", Indicator = "sma", Params = { ["length"] = 10 } } } } },
                },
                Risk = new() { StopLoss = new() { Mode = "percent", Value = 5 } },
            };
            var strategy = new BlueprintStrategy(BlueprintCompiler.Compile(bp));
            var result = await new BacktestSession(strategy, candles, Config("bp", "TESTUSDT")).RunAsync();
            var trade = Assert.Single(result.Trades, t => t.ExitReason != "open");
            Assert.Equal("stop", trade.ExitReason);
            Assert.True((double)trade.ExitPrice <= (double)trade.EntryPrice * 0.95 + 1e-6);
        }

        [Fact]
        public async Task Blueprint_Rotation_Holds_The_Strongest()
        {
            // Three assets: A trends up, B is flat, C trends down. Top-1 rotation must hold A.
            var a = Series(i => 100 + i, 120);
            var b = Series(_ => 100, 120);
            var c = Series(i => 200 - i, 120);
            var bp = new Blueprint
            {
                Universe = new() { Mode = "basket", Symbols = { "AAAUSDT", "BBBUSDT", "CCCUSDT" }, RegimeSymbol = "AAAUSDT" },
                Ranking = new() { Score = "roc(close, 20)", Top = 1, RebalanceBars = 5, Weighting = "equal" },
            };
            var strategy = new BlueprintStrategy(BlueprintCompiler.Compile(bp));
            var input = new PortfolioInput
            {
                Candles = new Dictionary<string, IReadOnlyList<OHLCCandle>> { ["AAAUSDT"] = a, ["BBBUSDT"] = b, ["CCCUSDT"] = c },
                RegimeSymbol = "AAAUSDT"
            };
            var result = await new BacktestSession(strategy, input, Config("bp", "AAAUSDT")).RunPortfolioAsync();
            Assert.NotEmpty(result.Trades);
            Assert.All(result.Trades, t => Assert.Equal("AAAUSDT", t.Symbol));
            Assert.True(result.TotalPnL > 0);
        }

        // ── scripts ───────────────────────────────────────────────────────────────

        private const string GoodScript = """
            [TradingStrategy("Script SMA", "test")]
            public class ScriptSma : ScriptStrategy
            {
                [Param("Length", Min = 2, Max = 100)] public int Length { get; set; } = 10;
                public override void OnBar()
                {
                    if (double.IsNaN(Sma(Length))) return;
                    if (Close > Sma(Length) && IsFlat) BuyPercent(90);
                    else if (Close < Sma(Length) && IsLong) ExitLong();
                    Plot("dist", Close - Sma(Length));
                }
            }
            """;

        [Fact]
        public void Script_Compiles_And_Exposes_Params()
        {
            var result = ScriptCompiler.Compile(GoodScript, "t_good");
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal("Script SMA", result.DisplayName);
            var schema = StrategyParams.For(result.StrategyType!);
            Assert.Contains(schema, p => p.Name == "Length");
            Assert.Contains(schema, p => p.Name == "TradeSymbol");
        }

        [Theory]
        [InlineData("System.IO.File.ReadAllText(\"x\");", "not available")]
        [InlineData("var c = new System.Net.Http.HttpClient();", "not available")]
        [InlineData("System.Diagnostics.Process.Start(\"cmd\");", "not available")]
        [InlineData("var t = typeof(string).GetMethods();", "not available")]
        [InlineData("System.Threading.Tasks.Task.Run(() => { });", "not available")]
        [InlineData("var e = System.Environment.MachineName;", "not available")]
        [InlineData("var now = DateTime.UtcNow;", "wall clock")]
        [InlineData("System.Threading.Thread.Sleep(10);", "not available")]
        public void Sandbox_Rejects_Dangerous_Code(string statement, string fragment)
        {
            string source = "public class Bad : ScriptStrategy { public override void OnBar() { " + statement + " } }";
            var result = ScriptCompiler.Compile(source, "t_bad_" + Math.Abs(statement.GetHashCode()));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            Assert.All(result.Diagnostics.Where(d => d.Severity == "error"), d => Assert.Equal(1, d.Line));
        }

        [Fact]
        public void Sandbox_Rejects_Using_Omnipotent_Internals()
        {
            string source = "using Omnipotent.Service_Manager;\npublic class Bad : ScriptStrategy { public override void OnBar() { } }";
            var result = ScriptCompiler.Compile(source, "t_bad_using");
            Assert.False(result.Success);
        }

        [Fact]
        public void Compile_Errors_Report_Line_And_Column()
        {
            string source = "public class Bad : ScriptStrategy\n{\n    public override void OnBar()\n    {\n        int x = \"nope\";\n    }\n}";
            var result = ScriptCompiler.Compile(source, "t_err");
            Assert.False(result.Success);
            var d = Assert.Single(result.Diagnostics.Where(x => x.Severity == "error"));
            Assert.Equal(5, d.Line);
        }

        [Fact]
        public async Task Script_Infinite_Loop_Fails_The_Run_Not_The_Host()
        {
            string source = """
                public class Spin : ScriptStrategy
                {
                    public override void OnBar() { long i = 0; while (true) { i++; } }
                }
                """;
            var result = ScriptCompiler.Compile(source, "t_spin");
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            var strategy = (TradingStrategy)Activator.CreateInstance(result.StrategyType!)!;
            StrategyParams.Apply(strategy, new Dictionary<string, object?> { ["TradeSymbol"] = "TESTUSDT" });
            var candles = Series(i => 100 + i, 40);
            var ex = await Assert.ThrowsAsync<StrategyRuntimeException>(() => new BacktestSession(strategy, candles, Config("spin", "TESTUSDT")).RunAsync());
            Assert.Contains("loop iterations", ex.Message);
        }

        [Fact]
        public async Task Script_Runaway_Recursion_Is_Stopped()
        {
            string source = """
                public class Deep : ScriptStrategy
                {
                    int Recurse(int n) => Recurse(n + 1);
                    public override void OnBar() { Recurse(0); }
                }
                """;
            var result = ScriptCompiler.Compile(source, "t_deep");
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            var strategy = (TradingStrategy)Activator.CreateInstance(result.StrategyType!)!;
            StrategyParams.Apply(strategy, new Dictionary<string, object?> { ["TradeSymbol"] = "TESTUSDT" });
            var ex = await Assert.ThrowsAsync<StrategyRuntimeException>(() => new BacktestSession(strategy, Series(i => 100, 40), Config("deep", "TESTUSDT")).RunAsync());
            Assert.Contains("recursion", ex.Message);
        }

        [Fact]
        public async Task Script_Backtest_Records_Trades_And_Plots()
        {
            var result = ScriptCompiler.Compile(GoodScript, "t_run");
            var strategy = (TradingStrategy)Activator.CreateInstance(result.StrategyType!)!;
            StrategyParams.Apply(strategy, new Dictionary<string, object?> { ["TradeSymbol"] = "TESTUSDT", ["Length"] = 20 });
            var candles = Series(i => 100 + 10 * Math.Sin(i / 15.0), 300);
            var bt = await new BacktestSession(strategy, candles, Config("script", "TESTUSDT")).RunAsync();
            Assert.True(bt.TotalTrades > 2);
            Assert.NotNull(bt.Plots);
            Assert.True(bt.Plots!["dist"].Count > 100);
        }

        // ── persistence & registry ────────────────────────────────────────────────

        [Fact]
        public async Task Saved_Strategies_Resolve_And_Pin_Versions()
        {
            var s = await studio.CreateFromTemplateAsync("ema-cross", "klives");
            Assert.Equal(StudioStatus.Valid, s.Status);
            Assert.NotNull(registry.Resolve(s.ClassName));

            var changed = StudioService.Clone(s.Blueprint!);
            changed.Inputs[0].Default = 12;
            var updated = await studio.UpdateAsync(s.Id, new StudioDraft { Blueprint = changed, Note = "faster" }, "klives");
            Assert.Equal(2, updated!.Version);
            var history = await studio.VersionsAsync(s.Id);
            Assert.Equal(new[] { 1, 2 }, history.Select(v => v.Version).OrderBy(v => v).ToArray());

            var pinned = registry.Resolve($"{s.ClassName}@v1");
            Assert.NotNull(pinned);
            var instance = registry.CreateInstance($"{s.ClassName}@v1");
            Assert.IsType<BlueprintStrategy>(instance);
            Assert.DoesNotContain(registry.All, d => d.ClassName.Contains('@'));
        }

        [Fact]
        public async Task Invalid_Script_Is_Saved_But_Not_Registered()
        {
            var s = await studio.CreateAsync(new StudioDraft { Kind = StudioKind.Script, Source = "public class X : ScriptStrategy { }", Name = "broken" }, "kliveagent");
            Assert.Equal(StudioStatus.Invalid, s.Status);
            Assert.NotEmpty(s.Diagnostics);
            Assert.Null(registry.Resolve(s.ClassName));
            var fixedUp = await studio.UpdateAsync(s.Id, new StudioDraft { Source = GoodScript }, "kliveagent");
            Assert.Equal(StudioStatus.Valid, fixedUp!.Status);
            Assert.NotNull(registry.Resolve(s.ClassName));
        }
    }
}
