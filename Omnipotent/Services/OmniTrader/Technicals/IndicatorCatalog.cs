namespace Omnipotent.Services.OmniTrader.Technicals
{
    public enum InputKind { Source, Int, Double, Bool, Choice }

    public sealed class IndicatorInput
    {
        public required string Name { get; init; }
        public required string Label { get; init; }
        public InputKind Kind { get; init; } = InputKind.Int;
        public object? Default { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Step { get; init; }
        public string[]? Options { get; init; }
    }

    public sealed class IndicatorOutput
    {
        public required string Name { get; init; }
        public required string Label { get; init; }
        /// <summary>line | histogram | area | dots | step | markers</summary>
        public string Style { get; init; } = "line";
        /// <summary>Categorical palette slot 1–8, or 0 for the neutral/up-down colouring.</summary>
        public int Color { get; init; } = 1;
        /// <summary>Bars the chart shifts this output by when drawing (negative = back).</summary>
        public int DisplayOffset { get; init; }
    }

    public sealed class IndicatorSpec
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Short { get; init; }
        public required string Category { get; init; }
        public string Description { get; init; } = "";
        /// <summary>Drawn on the price pane (true) or its own pane (false).</summary>
        public bool Overlay { get; init; }
        public List<IndicatorInput> Inputs { get; init; } = new();
        public List<IndicatorOutput> Outputs { get; init; } = new();
        /// <summary>Horizontal reference lines (e.g. 30/70 for RSI).</summary>
        public List<double> Levels { get; init; } = new();
        public double? RangeMin { get; init; }
        public double? RangeMax { get; init; }
        /// <summary>Longest lookback in bars for the given inputs — sizes the warm-up a caller must fetch.</summary>
        [Newtonsoft.Json.JsonIgnore] public Func<IndicatorArgs, int> Lookback { get; init; } = _ => 50;
        [Newtonsoft.Json.JsonIgnore] public required Func<IndicatorArgs, Dictionary<string, double[]>> Compute { get; init; }

        public string DefaultOutput => Outputs.Count > 0 ? Outputs[0].Name : "value";
    }

    /// <summary>Resolved arguments for one indicator call.</summary>
    public sealed class IndicatorArgs
    {
        public required BarSeries Bars { get; init; }
        public Dictionary<string, double> Numbers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double[]> Series { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Choices { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal IndicatorSpec? Spec { get; set; }

        public int I(string name)
        {
            double v = D(name);
            return (int)Math.Round(v);
        }

        public double D(string name)
        {
            if (Numbers.TryGetValue(name, out var v)) return v;
            var input = Spec?.Inputs.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return input?.Default switch
            {
                int i => i,
                double d => d,
                bool b => b ? 1 : 0,
                _ => 0
            };
        }

        public bool B(string name) => D(name) != 0;

        public string C(string name)
        {
            if (Choices.TryGetValue(name, out var v)) return v;
            var input = Spec?.Inputs.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return input?.Default as string ?? input?.Options?.FirstOrDefault() ?? "";
        }

        /// <summary>A source input: an explicit series, a named price field, or close.</summary>
        public double[] S(string name = "source")
        {
            if (Series.TryGetValue(name, out var s)) return s;
            if (Choices.TryGetValue(name, out var field) && Bars.Source(field) is { } named) return named;
            return Bars.Close;
        }
    }

    /// <summary>
    /// Every indicator OmniTrader can compute, with the metadata the chart picker, blueprint builder,
    /// screener, formula language and agent tools all read. One table, so a new indicator appears in
    /// every surface at once.
    /// </summary>
    public static class IndicatorCatalog
    {
        private static readonly Dictionary<string, IndicatorSpec> byId = new(StringComparer.OrdinalIgnoreCase);
        public static IReadOnlyList<IndicatorSpec> All { get; }

        public static IndicatorSpec? Get(string id) => byId.TryGetValue(id, out var s) ? s : null;

        static IndicatorCatalog()
        {
            var list = Build();
            foreach (var s in list) byId[s.Id] = s;
            All = list;
        }

        // ── input/output shorthands ───────────────────────────────────────────────

        private static IndicatorInput Src(string name = "source", string def = "close") => new()
        {
            Name = name, Label = "Source", Kind = InputKind.Source, Default = def, Options = BarSeries.SourceNames
        };
        private static IndicatorInput Len(int def, string name = "length", string label = "Length", int min = 1, int max = 1000) => new()
        {
            Name = name, Label = label, Kind = InputKind.Int, Default = def, Min = min, Max = max, Step = 1
        };
        private static IndicatorInput Num(string name, string label, double def, double min, double max, double step) => new()
        {
            Name = name, Label = label, Kind = InputKind.Double, Default = def, Min = min, Max = max, Step = step
        };
        private static IndicatorInput Pick(string name, string label, string def, params string[] options) => new()
        {
            Name = name, Label = label, Kind = InputKind.Choice, Default = def, Options = options
        };
        private static IndicatorOutput Out(string name, string label, int color = 1, string style = "line", int offset = 0) => new()
        {
            Name = name, Label = label, Color = color, Style = style, DisplayOffset = offset
        };

        private static Dictionary<string, double[]> One(double[] v) => new() { ["value"] = v };

        private static List<IndicatorSpec> Build() => new()
        {
            // ── moving averages (overlay) ───────────────────────────────────────
            Ma("sma", "Simple Moving Average", "SMA", (s, n) => TA.Sma(s, n), 20),
            Ma("ema", "Exponential Moving Average", "EMA", (s, n) => TA.Ema(s, n), 20),
            Ma("wma", "Weighted Moving Average", "WMA", (s, n) => TA.Wma(s, n), 20),
            Ma("rma", "Wilder Moving Average", "RMA", (s, n) => TA.Rma(s, n), 14),
            Ma("dema", "Double EMA", "DEMA", (s, n) => TA.Dema(s, n), 20),
            Ma("tema", "Triple EMA", "TEMA", (s, n) => TA.Tema(s, n), 20),
            Ma("hma", "Hull Moving Average", "HMA", (s, n) => TA.Hma(s, n), 21),
            Ma("zlema", "Zero-Lag EMA", "ZLEMA", (s, n) => TA.Zlema(s, n), 20),
            Ma("mcginley", "McGinley Dynamic", "MGD", (s, n) => TA.McGinley(s, n), 14),
            Ma("lsma", "Least Squares MA", "LSMA", (s, n) => TA.LinReg(s, n).Value, 25),
            new()
            {
                Id = "kama", Name = "Kaufman Adaptive MA", Short = "KAMA", Category = "Moving averages", Overlay = true,
                Inputs = { Src(), Len(10), Len(2, "fast", "Fast"), Len(30, "slow", "Slow") },
                Outputs = { Out("value", "KAMA", 3) },
                Lookback = a => a.I("length") + a.I("slow"),
                Compute = a => One(TA.Kama(a.S(), a.I("length"), a.I("fast"), a.I("slow")))
            },
            new()
            {
                Id = "alma", Name = "Arnaud Legoux MA", Short = "ALMA", Category = "Moving averages", Overlay = true,
                Inputs = { Src(), Len(9), Num("offset", "Offset", 0.85, 0, 1, 0.05), Num("sigma", "Sigma", 6, 1, 20, 0.5) },
                Outputs = { Out("value", "ALMA", 4) },
                Lookback = a => a.I("length"),
                Compute = a => One(TA.Alma(a.S(), a.I("length"), a.D("offset"), a.D("sigma")))
            },
            new()
            {
                Id = "vwma", Name = "Volume-Weighted MA", Short = "VWMA", Category = "Moving averages", Overlay = true,
                Inputs = { Src(), Len(20) },
                Outputs = { Out("value", "VWMA", 5) },
                Lookback = a => a.I("length"),
                Compute = a => One(TA.Vwma(a.S(), a.Bars.Volume, a.I("length")))
            },
            new()
            {
                Id = "t3", Name = "Tillson T3", Short = "T3", Category = "Moving averages", Overlay = true,
                Inputs = { Src(), Len(5), Num("vfactor", "Volume factor", 0.7, 0, 1, 0.05) },
                Outputs = { Out("value", "T3", 6) },
                Lookback = a => a.I("length") * 6,
                Compute = a => One(TA.T3(a.S(), a.I("length"), a.D("vfactor")))
            },
            new()
            {
                Id = "vwap", Name = "VWAP (anchored)", Short = "VWAP", Category = "Volume", Overlay = true,
                Description = "Typical-price VWAP that resets each UTC session, week or month.",
                Inputs = { Pick("anchor", "Anchor", "Session", "Session", "Week", "Month") },
                Outputs = { Out("value", "VWAP", 7) },
                Lookback = _ => 1,
                Compute = a => One(TA.Vwap(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.Bars.Time,
                    Enum.TryParse<VwapAnchor>(a.C("anchor"), true, out var anchor) ? anchor : VwapAnchor.Session))
            },
            new()
            {
                Id = "rvwap", Name = "Rolling VWAP", Short = "RVWAP", Category = "Volume", Overlay = true,
                Inputs = { Len(50) },
                Outputs = { Out("value", "RVWAP", 7) },
                Lookback = a => a.I("length"),
                Compute = a => One(TA.RollingVwap(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.I("length")))
            },

            // ── bands & channels (overlay) ───────────────────────────────────────
            new()
            {
                Id = "bb", Name = "Bollinger Bands", Short = "BB", Category = "Volatility", Overlay = true,
                Inputs = { Src(), Len(20), Num("mult", "StdDev", 2, 0.1, 5, 0.1) },
                Outputs = { Out("mid", "Basis", 4), Out("upper", "Upper", 1), Out("lower", "Lower", 1) },
                Lookback = a => a.I("length"),
                Compute = a =>
                {
                    var b = TA.Bollinger(a.S(), a.I("length"), a.D("mult"));
                    return new() { ["mid"] = b.Mid, ["upper"] = b.Upper, ["lower"] = b.Lower, ["pctb"] = b.PercentB, ["width"] = b.Width };
                }
            },
            new()
            {
                Id = "bbpct", Name = "Bollinger %B", Short = "%B", Category = "Volatility",
                Inputs = { Src(), Len(20), Num("mult", "StdDev", 2, 0.1, 5, 0.1) },
                Outputs = { Out("value", "%B", 1) }, Levels = { 0, 0.5, 1 },
                Lookback = a => a.I("length"),
                Compute = a => One(TA.Bollinger(a.S(), a.I("length"), a.D("mult")).PercentB)
            },
            new()
            {
                Id = "bbw", Name = "Bollinger Bandwidth", Short = "BBW", Category = "Volatility",
                Inputs = { Src(), Len(20), Num("mult", "StdDev", 2, 0.1, 5, 0.1) },
                Outputs = { Out("value", "Width %", 1) },
                Lookback = a => a.I("length"),
                Compute = a => One(TA.Bollinger(a.S(), a.I("length"), a.D("mult")).Width)
            },
            new()
            {
                Id = "kc", Name = "Keltner Channels", Short = "KC", Category = "Volatility", Overlay = true,
                Inputs = { Len(20), Num("mult", "ATR mult", 2, 0.1, 6, 0.1), Len(10, "atrLength", "ATR length") },
                Outputs = { Out("mid", "Basis", 3), Out("upper", "Upper", 2), Out("lower", "Lower", 2) },
                Lookback = a => Math.Max(a.I("length"), a.I("atrLength")),
                Compute = a =>
                {
                    var k = TA.Keltner(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"), a.D("mult"), a.I("atrLength"));
                    return new() { ["mid"] = k.Mid, ["upper"] = k.Upper, ["lower"] = k.Lower };
                }
            },
            new()
            {
                Id = "dc", Name = "Donchian Channels", Short = "DC", Category = "Volatility", Overlay = true,
                Inputs = { Len(20) },
                Outputs = { Out("upper", "Upper", 3), Out("lower", "Lower", 3), Out("mid", "Basis", 4) },
                Lookback = a => a.I("length"),
                Compute = a =>
                {
                    var d = TA.Donchian(a.Bars.High, a.Bars.Low, a.I("length"));
                    return new() { ["upper"] = d.Upper, ["lower"] = d.Lower, ["mid"] = d.Mid };
                }
            },
            new()
            {
                Id = "supertrend", Name = "Supertrend", Short = "ST", Category = "Trend", Overlay = true,
                Inputs = { Len(10, "length", "ATR length"), Num("mult", "Factor", 3, 0.5, 10, 0.1) },
                Outputs = { Out("line", "Supertrend", 0, "step"), Out("dir", "Direction", 0, "markers") },
                Lookback = a => a.I("length") * 3,
                Compute = a =>
                {
                    var s = TA.Supertrend(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"), a.D("mult"));
                    return new() { ["line"] = s.Line, ["dir"] = s.Direction };
                }
            },
            new()
            {
                Id = "psar", Name = "Parabolic SAR", Short = "SAR", Category = "Trend", Overlay = true,
                Inputs = { Num("step", "Step", 0.02, 0.001, 0.2, 0.001), Num("max", "Max", 0.2, 0.01, 1, 0.01) },
                Outputs = { Out("value", "SAR", 5, "dots") },
                Lookback = _ => 50,
                Compute = a => One(TA.Psar(a.Bars.High, a.Bars.Low, a.D("step"), a.D("max")))
            },
            new()
            {
                Id = "ichimoku", Name = "Ichimoku Cloud", Short = "ICH", Category = "Trend", Overlay = true,
                Inputs = { Len(9, "conversion", "Conversion"), Len(26, "base", "Base"), Len(52, "spanB", "Span B"), Len(26, "displacement", "Displacement") },
                Outputs =
                {
                    Out("tenkan", "Tenkan", 1), Out("kijun", "Kijun", 2), Out("spanA", "Span A", 3, "area"),
                    Out("spanB", "Span B", 8, "area"), Out("chikou", "Chikou", 7, "line", -25)
                },
                Lookback = a => a.I("spanB") + a.I("displacement"),
                Compute = a =>
                {
                    var c = TA.Ichimoku(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("conversion"), a.I("base"), a.I("spanB"), a.I("displacement"));
                    return new() { ["tenkan"] = c.Tenkan, ["kijun"] = c.Kijun, ["spanA"] = c.SpanA, ["spanB"] = c.SpanB, ["chikou"] = c.Chikou };
                }
            },
            new()
            {
                Id = "pivots", Name = "Pivot Points (daily)", Short = "PIV", Category = "Levels", Overlay = true,
                Description = "Previous UTC day's high/low/close projected onto the current day.",
                Inputs = { Pick("style", "Style", "Classic", "Classic", "Fibonacci", "Camarilla", "Woodie") },
                Outputs = { Out("p", "P", 4, "step"), Out("r1", "R1", 8, "step"), Out("r2", "R2", 8, "step"), Out("s1", "S1", 3, "step"), Out("s2", "S2", 3, "step") },
                Lookback = _ => 2,
                Compute = a =>
                {
                    var p = TA.Pivots(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Time,
                        Enum.TryParse<PivotStyle>(a.C("style"), true, out var st) ? st : PivotStyle.Classic);
                    return new() { ["p"] = p.P, ["r1"] = p.R1, ["r2"] = p.R2, ["r3"] = p.R3, ["s1"] = p.S1, ["s2"] = p.S2, ["s3"] = p.S3 };
                }
            },
            new()
            {
                Id = "swings", Name = "Swing Highs / Lows", Short = "SWG", Category = "Levels", Overlay = true,
                Description = "Fractal pivots, reported on the bar that confirms them.",
                Inputs = { Len(5, "left", "Left"), Len(5, "right", "Right") },
                Outputs = { Out("high", "Swing high", 8, "dots"), Out("low", "Swing low", 3, "dots") },
                Lookback = a => a.I("left") + a.I("right"),
                Compute = a => new()
                {
                    ["high"] = TA.PivotHigh(a.Bars.High, a.I("left"), a.I("right")),
                    ["low"] = TA.PivotLow(a.Bars.Low, a.I("left"), a.I("right"))
                }
            },
            new()
            {
                Id = "linreg", Name = "Linear Regression", Short = "LR", Category = "Statistics", Overlay = true,
                Inputs = { Src(), Len(50) },
                Outputs = { Out("value", "LinReg", 7) },
                Lookback = a => a.I("length"),
                Compute = a =>
                {
                    var l = TA.LinReg(a.S(), a.I("length"));
                    return new() { ["value"] = l.Value, ["slope"] = l.Slope, ["r2"] = l.R2 };
                }
            },

            // ── momentum oscillators ─────────────────────────────────────────────
            Osc("rsi", "Relative Strength Index", "RSI", a => One(TA.Rsi(a.S(), a.I("length"))),
                new() { Src(), Len(14) }, levels: new() { 30, 50, 70 }, min: 0, max: 100),
            new()
            {
                Id = "stoch", Name = "Stochastic", Short = "STOCH", Category = "Momentum",
                Inputs = { Len(14), Len(3, "smoothK", "Smooth %K"), Len(3, "d", "%D") },
                Outputs = { Out("k", "%K", 1), Out("d", "%D", 2) }, Levels = { 20, 80 }, RangeMin = 0, RangeMax = 100,
                Lookback = a => a.I("length") + a.I("smoothK") + a.I("d"),
                Compute = a =>
                {
                    var s = TA.Stoch(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"), a.I("smoothK"), a.I("d"));
                    return new() { ["k"] = s.K, ["d"] = s.D };
                }
            },
            new()
            {
                Id = "stochrsi", Name = "Stochastic RSI", Short = "SRSI", Category = "Momentum",
                Inputs = { Src(), Len(14, "rsiLength", "RSI length"), Len(14, "length", "Stoch length"), Len(3, "smoothK", "%K"), Len(3, "d", "%D") },
                Outputs = { Out("k", "%K", 1), Out("d", "%D", 2) }, Levels = { 20, 80 }, RangeMin = 0, RangeMax = 100,
                Lookback = a => a.I("rsiLength") * 3 + a.I("length"),
                Compute = a =>
                {
                    var s = TA.StochRsi(a.S(), a.I("rsiLength"), a.I("length"), a.I("smoothK"), a.I("d"));
                    return new() { ["k"] = s.K, ["d"] = s.D };
                }
            },
            Osc("cci", "Commodity Channel Index", "CCI", a => One(TA.Cci(a.S(), a.I("length"))),
                new() { Src("source", "hlc3"), Len(20) }, levels: new() { -100, 0, 100 }),
            Osc("willr", "Williams %R", "%R", a => One(TA.WilliamsR(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"))),
                new() { Len(14) }, levels: new() { -80, -20 }, min: -100, max: 0),
            Osc("mfi", "Money Flow Index", "MFI", a => One(TA.Mfi(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.I("length"))),
                new() { Len(14) }, levels: new() { 20, 80 }, min: 0, max: 100, category: "Volume"),
            new()
            {
                Id = "uo", Name = "Ultimate Oscillator", Short = "UO", Category = "Momentum",
                Inputs = { Len(7, "fast", "Fast"), Len(14, "mid", "Middle"), Len(28, "slow", "Slow") },
                Outputs = { Out("value", "UO", 1) }, Levels = { 30, 70 }, RangeMin = 0, RangeMax = 100,
                Lookback = a => a.I("slow"),
                Compute = a => One(TA.UltimateOscillator(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("fast"), a.I("mid"), a.I("slow")))
            },
            new()
            {
                Id = "ao", Name = "Awesome Oscillator", Short = "AO", Category = "Momentum",
                Inputs = { Len(5, "fast", "Fast"), Len(34, "slow", "Slow") },
                Outputs = { Out("value", "AO", 0, "histogram") }, Levels = { 0 },
                Lookback = a => a.I("slow"),
                Compute = a => One(TA.AwesomeOscillator(a.Bars.Hl2, a.I("fast"), a.I("slow")))
            },
            new()
            {
                Id = "macd", Name = "MACD", Short = "MACD", Category = "Trend",
                Inputs = { Src(), Len(12, "fast", "Fast"), Len(26, "slow", "Slow"), Len(9, "signal", "Signal") },
                Outputs = { Out("macd", "MACD", 1), Out("signal", "Signal", 2), Out("hist", "Histogram", 0, "histogram") }, Levels = { 0 },
                Lookback = a => a.I("slow") + a.I("signal"),
                Compute = a =>
                {
                    var m = TA.Macd(a.S(), a.I("fast"), a.I("slow"), a.I("signal"));
                    return new() { ["macd"] = m.Macd, ["signal"] = m.Signal, ["hist"] = m.Hist };
                }
            },
            new()
            {
                Id = "ppo", Name = "Percentage Price Oscillator", Short = "PPO", Category = "Momentum",
                Inputs = { Src(), Len(12, "fast", "Fast"), Len(26, "slow", "Slow"), Len(9, "signal", "Signal") },
                Outputs = { Out("ppo", "PPO", 1), Out("signal", "Signal", 2), Out("hist", "Histogram", 0, "histogram") }, Levels = { 0 },
                Lookback = a => a.I("slow") + a.I("signal"),
                Compute = a =>
                {
                    var p = TA.Ppo(a.S(), a.I("fast"), a.I("slow"), a.I("signal"));
                    return new() { ["ppo"] = p.Ppo, ["signal"] = p.Signal, ["hist"] = p.Hist };
                }
            },
            Osc("cmo", "Chande Momentum Oscillator", "CMO", a => One(TA.Cmo(a.S(), a.I("length"))),
                new() { Src(), Len(9) }, levels: new() { -50, 0, 50 }, min: -100, max: 100),
            new()
            {
                Id = "tsi", Name = "True Strength Index", Short = "TSI", Category = "Momentum",
                Inputs = { Src(), Len(25, "long", "Long"), Len(13, "short", "Short"), Len(13, "signal", "Signal") },
                Outputs = { Out("tsi", "TSI", 1), Out("signal", "Signal", 2) }, Levels = { 0 },
                Lookback = a => a.I("long") + a.I("short") + a.I("signal"),
                Compute = a =>
                {
                    var t = TA.Tsi(a.S(), a.I("long"), a.I("short"), a.I("signal"));
                    return new() { ["tsi"] = t.Tsi, ["signal"] = t.Signal };
                }
            },
            new()
            {
                Id = "fisher", Name = "Fisher Transform", Short = "FISH", Category = "Momentum",
                Inputs = { Len(9) },
                Outputs = { Out("fisher", "Fisher", 1), Out("trigger", "Trigger", 2) }, Levels = { -1.5, 0, 1.5 },
                Lookback = a => a.I("length") * 2,
                Compute = a =>
                {
                    var f = TA.Fisher(a.Bars.High, a.Bars.Low, a.I("length"));
                    return new() { ["fisher"] = f.Fisher, ["trigger"] = f.Trigger };
                }
            },
            new()
            {
                Id = "crsi", Name = "Connors RSI", Short = "CRSI", Category = "Momentum",
                Inputs = { Src(), Len(3, "rsiLength", "RSI"), Len(2, "streakLength", "Streak"), Len(100, "rankLength", "Rank") },
                Outputs = { Out("value", "CRSI", 1) }, Levels = { 10, 90 }, RangeMin = 0, RangeMax = 100,
                Lookback = a => a.I("rankLength") + 5,
                Compute = a => One(TA.ConnorsRsi(a.S(), a.I("rsiLength"), a.I("streakLength"), a.I("rankLength")))
            },
            new()
            {
                Id = "coppock", Name = "Coppock Curve", Short = "COPP", Category = "Momentum",
                Inputs = { Src(), Len(10, "wma", "WMA"), Len(14, "longRoc", "Long ROC"), Len(11, "shortRoc", "Short ROC") },
                Outputs = { Out("value", "Coppock", 1) }, Levels = { 0 },
                Lookback = a => a.I("wma") + a.I("longRoc"),
                Compute = a => One(TA.Coppock(a.S(), a.I("wma"), a.I("longRoc"), a.I("shortRoc")))
            },
            Osc("roc", "Rate of Change", "ROC", a => One(TA.Roc(a.S(), a.I("length"))), new() { Src(), Len(10) }, levels: new() { 0 }),
            Osc("mom", "Momentum", "MOM", a => One(TA.Momentum(a.S(), a.I("length"))), new() { Src(), Len(10) }, levels: new() { 0 }),
            Osc("trix", "TRIX", "TRIX", a => One(TA.Trix(a.S(), a.I("length"))), new() { Src(), Len(15) }, levels: new() { 0 }),
            Osc("dpo", "Detrended Price Oscillator", "DPO", a => One(TA.Dpo(a.S(), a.I("length"))), new() { Src(), Len(20) }, levels: new() { 0 }),
            new()
            {
                Id = "kst", Name = "Know Sure Thing", Short = "KST", Category = "Momentum",
                Inputs = { Src() },
                Outputs = { Out("kst", "KST", 1), Out("signal", "Signal", 2) }, Levels = { 0 },
                Lookback = _ => 50,
                Compute = a =>
                {
                    var k = TA.Kst(a.S());
                    return new() { ["kst"] = k.Kst, ["signal"] = k.Signal };
                }
            },

            // ── trend strength ───────────────────────────────────────────────────
            new()
            {
                Id = "adx", Name = "Average Directional Index", Short = "ADX", Category = "Trend",
                Inputs = { Len(14) },
                Outputs = { Out("adx", "ADX", 1), Out("pdi", "+DI", 3), Out("mdi", "−DI", 8) }, Levels = { 20, 25 }, RangeMin = 0, RangeMax = 100,
                Lookback = a => a.I("length") * 3,
                Compute = a =>
                {
                    var d = TA.Adx(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"));
                    return new() { ["adx"] = d.Adx, ["pdi"] = d.PlusDi, ["mdi"] = d.MinusDi };
                }
            },
            new()
            {
                Id = "aroon", Name = "Aroon", Short = "AROON", Category = "Trend",
                Inputs = { Len(25) },
                Outputs = { Out("up", "Up", 3), Out("down", "Down", 8), Out("osc", "Oscillator", 1) }, Levels = { 30, 70 },
                Lookback = a => a.I("length") + 1,
                Compute = a =>
                {
                    var r = TA.Aroon(a.Bars.High, a.Bars.Low, a.I("length"));
                    return new() { ["up"] = r.Up, ["down"] = r.Down, ["osc"] = r.Osc };
                }
            },
            new()
            {
                Id = "vortex", Name = "Vortex Indicator", Short = "VI", Category = "Trend",
                Inputs = { Len(14) },
                Outputs = { Out("plus", "+VI", 3), Out("minus", "−VI", 8) }, Levels = { 1 },
                Lookback = a => a.I("length") + 1,
                Compute = a =>
                {
                    var v = TA.Vortex(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"));
                    return new() { ["plus"] = v.Plus, ["minus"] = v.Minus };
                }
            },
            Osc("chop", "Choppiness Index", "CHOP", a => One(TA.Choppiness(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"))),
                new() { Len(14) }, levels: new() { 38.2, 61.8 }, min: 0, max: 100, category: "Trend"),
            Osc("trendscore", "Trend Score", "TRND", a => One(TA.TrendScore(a.Bars.Close, a.I("fast"), a.I("slow"))),
                new() { Len(20, "fast", "Fast"), Len(50, "slow", "Slow") }, levels: new() { 0 }, min: -1, max: 1, category: "Trend"),

            // ── volatility ───────────────────────────────────────────────────────
            Osc("atr", "Average True Range", "ATR", a => One(TA.Atr(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"))),
                new() { Len(14) }, category: "Volatility"),
            Osc("natr", "Normalized ATR", "NATR", a => One(TA.Natr(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"))),
                new() { Len(14) }, category: "Volatility"),
            Osc("tr", "True Range", "TR", a => One(TA.TrueRange(a.Bars.High, a.Bars.Low, a.Bars.Close)), new(), category: "Volatility"),
            Osc("stdev", "Standard Deviation", "SD", a => One(TA.StdDev(a.S(), a.I("length"))), new() { Src(), Len(20) }, category: "Volatility"),
            Osc("hv", "Historical Volatility", "HV", a => One(TA.HistoricalVolatility(a.Bars.Close, a.I("length"), a.Bars.BarsPerYear)),
                new() { Len(20) }, category: "Volatility"),
            Osc("parkinson", "Parkinson Volatility", "PKV", a => One(TA.ParkinsonVolatility(a.Bars.High, a.Bars.Low, a.I("length"), a.Bars.BarsPerYear)),
                new() { Len(20) }, category: "Volatility"),
            Osc("gkvol", "Garman-Klass Volatility", "GKV", a => One(TA.GarmanKlassVolatility(a.Bars.Open, a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"), a.Bars.BarsPerYear)),
                new() { Len(20) }, category: "Volatility"),
            Osc("ulcer", "Ulcer Index", "UI", a => One(TA.UlcerIndex(a.Bars.Close, a.I("length"))), new() { Len(14) }, category: "Volatility"),
            Osc("chaikinvol", "Chaikin Volatility", "CHV", a => One(TA.ChaikinVolatility(a.Bars.High, a.Bars.Low, a.I("length"), a.I("roc"))),
                new() { Len(10), Len(10, "roc", "ROC") }, levels: new() { 0 }, category: "Volatility"),
            Osc("squeeze", "Bollinger/Keltner Squeeze", "SQZ", a => One(TA.Squeeze(a.Bars.High, a.Bars.Low, a.Bars.Close, a.I("length"), a.D("bbMult"), a.D("kcMult"))),
                new() { Len(20), Num("bbMult", "BB mult", 2, 0.5, 4, 0.1), Num("kcMult", "KC mult", 1.5, 0.5, 4, 0.1) },
                min: 0, max: 1, category: "Volatility", style: "histogram"),

            // ── volume ───────────────────────────────────────────────────────────
            new()
            {
                Id = "volume", Name = "Volume", Short = "VOL", Category = "Volume",
                Inputs = { Len(20, "length", "MA length") },
                Outputs = { Out("value", "Volume", 0, "histogram"), Out("ma", "MA", 4) },
                Lookback = a => a.I("length"),
                Compute = a => new() { ["value"] = (double[])a.Bars.Volume.Clone(), ["ma"] = TA.Sma(a.Bars.Volume, a.I("length")) }
            },
            Osc("obv", "On-Balance Volume", "OBV", a => One(TA.Obv(a.Bars.Close, a.Bars.Volume)), new(), category: "Volume"),
            Osc("ad", "Accumulation/Distribution", "A/D", a => One(TA.AccumulationDistribution(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume)), new(), category: "Volume"),
            Osc("cmf", "Chaikin Money Flow", "CMF", a => One(TA.ChaikinMoneyFlow(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.I("length"))),
                new() { Len(20) }, levels: new() { 0 }, category: "Volume"),
            Osc("chaikinosc", "Chaikin Oscillator", "CHO", a => One(TA.ChaikinOscillator(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.I("fast"), a.I("slow"))),
                new() { Len(3, "fast", "Fast"), Len(10, "slow", "Slow") }, levels: new() { 0 }, category: "Volume"),
            Osc("fi", "Force Index", "FI", a => One(TA.ForceIndex(a.Bars.Close, a.Bars.Volume, a.I("length"))), new() { Len(13) }, levels: new() { 0 }, category: "Volume"),
            Osc("eom", "Ease of Movement", "EOM", a => One(TA.EaseOfMovement(a.Bars.High, a.Bars.Low, a.Bars.Volume, a.I("length"))), new() { Len(14) }, levels: new() { 0 }, category: "Volume"),
            Osc("pvt", "Price-Volume Trend", "PVT", a => One(TA.PriceVolumeTrend(a.Bars.Close, a.Bars.Volume)), new(), category: "Volume"),
            new()
            {
                Id = "klinger", Name = "Klinger Oscillator", Short = "KVO", Category = "Volume",
                Inputs = { Len(34, "fast", "Fast"), Len(55, "slow", "Slow"), Len(13, "signal", "Signal") },
                Outputs = { Out("kvo", "KVO", 1), Out("signal", "Signal", 2) }, Levels = { 0 },
                Lookback = a => a.I("slow") + a.I("signal"),
                Compute = a =>
                {
                    var k = TA.Klinger(a.Bars.High, a.Bars.Low, a.Bars.Close, a.Bars.Volume, a.I("fast"), a.I("slow"), a.I("signal"));
                    return new() { ["kvo"] = k.Kvo, ["signal"] = k.Signal };
                }
            },
            Osc("relvol", "Relative Volume", "RVOL", a => One(TA.RelativeVolume(a.Bars.Volume, a.I("length"))), new() { Len(20) }, levels: new() { 1 }, category: "Volume", style: "histogram"),
            Osc("volosc", "Volume Oscillator", "VO", a => One(TA.VolumeOscillator(a.Bars.Volume, a.I("fast"), a.I("slow"))),
                new() { Len(5, "fast", "Fast"), Len(10, "slow", "Slow") }, levels: new() { 0 }, category: "Volume"),
            Osc("dollarvol", "Dollar Volume", "$VOL", a => One(TA.DollarVolume(a.Bars.Close, a.Bars.Volume, a.I("length"))), new() { Len(20) }, category: "Volume"),

            // ── statistics ───────────────────────────────────────────────────────
            Osc("zscore", "Z-Score", "Z", a => One(TA.ZScore(a.S(), a.I("length"))), new() { Src(), Len(20) }, levels: new() { -2, 0, 2 }, category: "Statistics"),
            Osc("percentrank", "Percent Rank", "PR", a => One(TA.PercentRank(a.S(), a.I("length"))), new() { Src(), Len(100) }, levels: new() { 10, 50, 90 }, min: 0, max: 100, category: "Statistics"),
            Osc("slope", "Regression Slope", "SLP", a => One(TA.LinReg(a.S(), a.I("length")).Slope), new() { Src(), Len(20) }, levels: new() { 0 }, category: "Statistics"),
            Osc("r2", "Regression R²", "R²", a => One(TA.LinReg(a.S(), a.I("length")).R2), new() { Src(), Len(20) }, min: 0, max: 1, category: "Statistics"),
            Osc("skew", "Rolling Skew (returns)", "SKEW", a => One(TA.Skew(TA.Roc(a.S(), 1), a.I("length"))), new() { Src(), Len(60) }, levels: new() { 0 }, category: "Statistics"),
            Osc("kurt", "Rolling Excess Kurtosis (returns)", "KURT", a => One(TA.Kurtosis(TA.Roc(a.S(), 1), a.I("length"))), new() { Src(), Len(60) }, levels: new() { 0 }, category: "Statistics"),
            Osc("hurst", "Hurst Exponent", "HURST", a => One(TA.Hurst(a.S(), a.I("length"))), new() { Src(), Len(100, min: 40) }, levels: new() { 0.5 }, min: 0, max: 1, category: "Statistics"),
            Osc("autocorr", "Return Autocorrelation", "ACF", a => One(TA.AutoCorrelation(a.S(), a.I("length"), a.I("lag"))), new() { Src(), Len(60), Len(1, "lag", "Lag") }, levels: new() { 0 }, min: -1, max: 1, category: "Statistics"),
            Osc("rsharpe", "Rolling Sharpe", "RSHP", a => One(TA.RollingSharpe(a.Bars.Close, a.I("length"), a.Bars.BarsPerYear)), new() { Len(60) }, levels: new() { 0 }, category: "Statistics"),
            Osc("drawdown", "Drawdown from High", "DD", a => One(TA.Drawdown(a.Bars.Close)), new(), levels: new() { 0 }, category: "Statistics", style: "area"),

            // ── price structure & patterns ───────────────────────────────────────
            Osc("ibs", "Internal Bar Strength", "IBS", a => One(TA.Ibs(a.Bars.High, a.Bars.Low, a.Bars.Close)), new(), levels: new() { 0.2, 0.8 }, min: 0, max: 1, category: "Price"),
            Osc("gap", "Opening Gap %", "GAP", a => One(TA.Gap(a.Bars.Open, a.Bars.Close)), new(), levels: new() { 0 }, category: "Price", style: "histogram"),
            Pattern("doji", "Doji", a => TA.Doji(a.Bars.Open, a.Bars.High, a.Bars.Low, a.Bars.Close)),
            Pattern("hammer", "Hammer / Shooting Star", a => TA.Hammer(a.Bars.Open, a.Bars.High, a.Bars.Low, a.Bars.Close)),
            Pattern("engulfing", "Engulfing", a => TA.Engulfing(a.Bars.Open, a.Bars.Close)),
            Pattern("insidebar", "Inside Bar", a => TA.InsideBar(a.Bars.High, a.Bars.Low)),
            Pattern("outsidebar", "Outside Bar", a => TA.OutsideBar(a.Bars.High, a.Bars.Low)),
            Pattern("soldiers", "Three Soldiers / Crows", a => TA.ThreeSoldiersCrows(a.Bars.Open, a.Bars.Close)),
        };

        private static IndicatorSpec Ma(string id, string name, string shortName, Func<double[], int, double[]> f, int def) => new()
        {
            Id = id, Name = name, Short = shortName, Category = "Moving averages", Overlay = true,
            Inputs = { Src(), Len(def) },
            Outputs = { Out("value", shortName, 1) },
            Lookback = a => a.I("length") * 3,
            Compute = a => One(f(a.S(), a.I("length")))
        };

        private static IndicatorSpec Osc(string id, string name, string shortName, Func<IndicatorArgs, Dictionary<string, double[]>> compute,
            List<IndicatorInput> inputs, List<double>? levels = null, double? min = null, double? max = null,
            string category = "Momentum", string style = "line") => new()
        {
            Id = id, Name = name, Short = shortName, Category = category,
            Inputs = inputs,
            Outputs = { Out("value", shortName, 1, style) },
            Levels = levels ?? new List<double>(),
            RangeMin = min, RangeMax = max,
            Lookback = a => Math.Max(20, inputs.Where(i => i.Kind == InputKind.Int).Select(i => a.I(i.Name)).DefaultIfEmpty(20).Max() * 3),
            Compute = compute
        };

        private static IndicatorSpec Pattern(string id, string name, Func<IndicatorArgs, double[]> f) => new()
        {
            Id = id, Name = name, Short = id.ToUpperInvariant(), Category = "Patterns", Overlay = true,
            Description = "1 where the pattern prints (−1 for the bearish variant).",
            Outputs = { Out("value", name, 0, "markers") },
            Lookback = _ => 12,
            Compute = a => One(f(a))
        };
    }
}
