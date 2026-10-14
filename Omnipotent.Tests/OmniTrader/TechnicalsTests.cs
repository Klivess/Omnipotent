using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// The indicator library and the formula language. The causality test is the important one:
    /// every catalog indicator computed on a prefix of a series must equal the full computation
    /// truncated to that prefix, which is what makes priming strategies with whole series safe.
    /// </summary>
    public class TechnicalsTests
    {
        private static BarSeries RandomWalk(int n, int seed = 7, double start = 100)
        {
            var rng = new Random(seed);
            var candles = new List<OHLCCandle>(n);
            double price = start;
            var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < n; i++)
            {
                double open = price;
                double close = Math.Max(1, open * (1 + (rng.NextDouble() - 0.5) * 0.04));
                double high = Math.Max(open, close) * (1 + rng.NextDouble() * 0.01);
                double low = Math.Min(open, close) * (1 - rng.NextDouble() * 0.01);
                candles.Add(new OHLCCandle(t0.AddHours(i), (decimal)open, (decimal)high, (decimal)low, (decimal)close, (decimal)(1000 + rng.Next(0, 5000))));
                price = close;
            }
            return BarSeries.From(candles, 365 * 24);
        }

        [Fact]
        public void Every_Catalog_Indicator_Is_Causal()
        {
            var full = RandomWalk(420);
            var failures = new List<string>();
            foreach (var spec in IndicatorCatalog.All)
            {
                var whole = IndicatorEngine.Compute(spec, full, null);
                foreach (int cut in new[] { 150, 260, 333 })
                {
                    var prefix = IndicatorEngine.Compute(spec, full.Prefix(cut), null);
                    foreach (var (output, values) in whole)
                    {
                        if (!prefix.TryGetValue(output, out var p)) { failures.Add($"{spec.Id}.{output}: missing on prefix"); continue; }
                        for (int i = 0; i < cut; i++)
                        {
                            double a = values[i], b = p[i];
                            bool same = (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));
                            if (!same) { failures.Add($"{spec.Id}.{output}[{i}] full={a} prefix({cut})={b}"); break; }
                        }
                    }
                }
            }
            Assert.True(failures.Count == 0, "Look-ahead detected:\n" + string.Join("\n", failures.Take(20)));
        }

        [Fact]
        public void Every_Catalog_Indicator_Produces_Values_After_Warmup()
        {
            var bars = RandomWalk(600, seed: 11);
            var empty = new List<string>();
            foreach (var spec in IndicatorCatalog.All)
            {
                var outputs = IndicatorEngine.Compute(spec, bars, null);
                foreach (var output in spec.Outputs)
                {
                    var v = outputs[output.Name];
                    Assert.Equal(bars.Count, v.Length);
                    if (spec.Category != "Patterns" && output.Style != "dots" && !v.Skip(300).Any(TA.IsNum))
                        empty.Add($"{spec.Id}.{output.Name}");
                }
            }
            Assert.True(empty.Count == 0, "No values after warm-up: " + string.Join(", ", empty));
        }

        [Fact]
        public void Moving_Averages_Of_A_Constant_Are_The_Constant()
        {
            var c = TA.Const(100, 5.0);
            Assert.Equal(5.0, TA.Sma(c, 10)[^1], 9);
            Assert.Equal(5.0, TA.Ema(c, 10)[^1], 9);
            Assert.Equal(5.0, TA.Wma(c, 10)[^1], 9);
            Assert.Equal(5.0, TA.Hma(c, 16)[^1], 9);
            Assert.Equal(5.0, TA.Rma(c, 14)[^1], 9);
            Assert.True(double.IsNaN(TA.Sma(c, 10)[8]));
            Assert.Equal(5.0, TA.Sma(c, 10)[9], 9);
        }

        [Fact]
        public void Ema_Matches_The_Recurrence()
        {
            double[] x = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var ema = TA.Ema(x, 3);
            double alpha = 0.5, state = 1;
            for (int i = 1; i < x.Length; i++) state = alpha * x[i] + (1 - alpha) * state;
            Assert.Equal(state, ema[^1], 12);
            Assert.True(double.IsNaN(ema[1]));
            Assert.False(double.IsNaN(ema[2]));
        }

        [Fact]
        public void Rsi_Extremes()
        {
            var up = Enumerable.Range(1, 60).Select(i => (double)i).ToArray();
            Assert.Equal(100, TA.Rsi(up, 14)[^1], 9);
            var down = up.Reverse().ToArray();
            Assert.Equal(0, TA.Rsi(down, 14)[^1], 9);
            Assert.Equal(50, TA.Rsi(TA.Const(60, 3), 14)[^1], 9);
        }

        [Fact]
        public void Rsi_Wilder_Reference_Value()
        {
            // Wilder's own worked example (New Concepts, 1978): 14-period RSI on these closes ≈ 70.53 at bar 15.
            double[] closes = { 44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28 };
            var rsi = TA.Rsi(closes, 14);
            Assert.Equal(70.53, rsi[14], 1);
        }

        [Fact]
        public void Bollinger_Width_Is_Zero_On_A_Flat_Line()
        {
            var bb = TA.Bollinger(TA.Const(50, 10), 20, 2);
            Assert.Equal(10, bb.Upper[^1], 9);
            Assert.Equal(10, bb.Lower[^1], 9);
            Assert.Equal(0, bb.Width[^1], 9);
        }

        [Fact]
        public void Highest_And_Lowest_Use_The_Window()
        {
            double[] x = { 1, 5, 3, 2, 8, 1, 1, 1 };
            var hi = TA.Highest(x, 3);
            Assert.Equal(new[] { 5.0, 5, 8, 8, 8, 1 }, hi.Skip(2).ToArray());
            var lo = TA.Lowest(x, 3);
            Assert.Equal(new[] { 1.0, 2, 2, 1, 1, 1 }, lo.Skip(2).ToArray());
        }

        [Fact]
        public void Pivots_Are_Reported_On_The_Confirming_Bar()
        {
            double[] high = { 1, 2, 5, 2, 1, 1, 1 };
            var ph = TA.PivotHigh(high, 2, 2);
            Assert.True(double.IsNaN(ph[2]));       // the pivot bar itself never knows
            Assert.Equal(5, ph[4]);                 // confirmed two bars later
        }

        [Fact]
        public void Supertrend_Direction_Follows_A_Clean_Trend()
        {
            var up = Enumerable.Range(0, 120).Select(i => 100.0 + i).ToArray();
            var high = up.Select(v => v + 0.5).ToArray();
            var low = up.Select(v => v - 0.5).ToArray();
            var st = TA.Supertrend(high, low, up, 10, 3);
            Assert.Equal(1, st.Direction[^1]);
            Assert.True(st.Line[^1] < up[^1]);
        }

        // ── formula language ──────────────────────────────────────────────────────

        private static FormulaContext Ctx(BarSeries bars, Dictionary<string, double>? inputs = null, Func<string, BarSeries?>? ext = null)
            => new() { Bars = bars, Inputs = inputs, External = ext };

        [Fact]
        public void Formula_Comparison_Is_Boolean_And_Vectorised()
        {
            var bars = RandomWalk(300);
            var f = FormulaCompiler.Compile("close > sma(close, 20)");
            Assert.True(f.IsBoolean);
            var v = f.Evaluate(Ctx(bars));
            var sma = TA.Sma(bars.Close, 20);
            for (int i = 0; i < bars.Count; i++)
            {
                if (double.IsNaN(sma[i])) Assert.True(double.IsNaN(v[i]));
                else Assert.Equal(bars.Close[i] > sma[i] ? 1 : 0, v[i]);
            }
        }

        [Fact]
        public void Formula_Source_Can_Be_Omitted_And_Outputs_Selected()
        {
            var bars = RandomWalk(300);
            var a = FormulaCompiler.Compile("rsi(14)").Evaluate(Ctx(bars));
            var b = FormulaCompiler.Compile("rsi(close, 14)").Evaluate(Ctx(bars));
            Assert.Equal(a[^1], b[^1]);
            var signal = FormulaCompiler.Compile("macd(close, 12, 26, 9).signal").Evaluate(Ctx(bars));
            Assert.Equal(TA.Macd(bars.Close).Signal[^1], signal[^1], 9);
            var upper = FormulaCompiler.Compile("bb(close, 20, mult=2.5).upper").Evaluate(Ctx(bars));
            Assert.Equal(TA.Bollinger(bars.Close, 20, 2.5).Upper[^1], upper[^1], 9);
        }

        [Fact]
        public void Formula_Nested_Series_And_Offsets()
        {
            var bars = RandomWalk(300);
            var v = FormulaCompiler.Compile("sma(rsi(close, 14), 5)[2]").Evaluate(Ctx(bars));
            var expected = TA.Shift(TA.Sma(TA.Rsi(bars.Close, 14), 5), 2);
            Assert.Equal(expected[^1], v[^1], 9);
            var cross = FormulaCompiler.Compile("crossover(ema(close, 5), ema(close, 20))").Evaluate(Ctx(bars));
            Assert.Equal(TA.CrossOver(TA.Ema(bars.Close, 5), TA.Ema(bars.Close, 20)).Count(x => x == 1), cross.Count(x => x == 1));
        }

        [Fact]
        public void Formula_Inputs_Bind_As_Scalars()
        {
            var bars = RandomWalk(300);
            var scope = FormulaScope.With(inputs: new[] { "len", "level" });
            var f = FormulaCompiler.Compile("rsi(close, len) < level and close > ema(close, len * 4)", scope);
            Assert.Contains("len", f.Inputs);
            var v = f.Evaluate(Ctx(bars, new Dictionary<string, double> { ["len"] = 7, ["level"] = 40 }));
            Assert.Equal(bars.Count, v.Length);
            Assert.True(f.Lookback >= 28);
        }

        [Theory]
        [InlineData("close[-1]", "negative offset reads the future")]
        [InlineData("foo(close)", "Unknown function")]
        [InlineData("sma(close, close)", "must be a number or input")]
        [InlineData("close >", "ends early")]
        [InlineData("rsi(close, 14, 3)", "at most")]
        [InlineData("macd(close).nope", "no output")]
        [InlineData("sma", "is a function")]
        public void Formula_Errors_Are_Explained(string text, string fragment)
        {
            var (formula, errors) = FormulaCompiler.Check(text);
            Assert.Null(formula);
            Assert.Single(errors);
            Assert.Contains(fragment, errors[0].Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Formula_Three_Valued_Logic()
        {
            var bars = RandomWalk(50);
            // During warm-up the sma is unknown, but a known false still decides the AND.
            var v = FormulaCompiler.Compile("false and close > sma(close, 40)").Evaluate(Ctx(bars));
            Assert.Equal(0, v[0]);
            var w = FormulaCompiler.Compile("true or close > sma(close, 40)").Evaluate(Ctx(bars));
            Assert.Equal(1, w[0]);
            var x = FormulaCompiler.Compile("close > sma(close, 40) ? 1 : 2").Evaluate(Ctx(bars));
            Assert.True(double.IsNaN(x[0]));
        }

        [Fact]
        public void Formula_External_Series_Lags_Until_Closed()
        {
            var bars = RandomWalk(100);
            var other = RandomWalk(100, seed: 99, start: 50);
            var v = FormulaCompiler.Compile("ext(\"OTHER\")").Evaluate(Ctx(bars, ext: s => s == "OTHER" ? other : null));
            // Same hourly clock: at bar i only bar i−1 of the other symbol has closed by bar i's open.
            Assert.True(double.IsNaN(v[0]));
            Assert.Equal(other.Close[0], v[1]);
            Assert.Equal(other.Close[98], v[99]);
        }

        [Fact]
        public void Formula_Reference_Lists_Every_Indicator()
        {
            var reference = Newtonsoft.Json.Linq.JObject.FromObject(FormulaCompiler.Reference());
            Assert.Equal(IndicatorCatalog.All.Count, ((Newtonsoft.Json.Linq.JArray)reference["Indicators"]!).Count);
            Assert.NotEmpty((Newtonsoft.Json.Linq.JArray)reference["Functions"]!);
        }
    }
}
