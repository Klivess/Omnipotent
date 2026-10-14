using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Studio
{
    public sealed class StudioTemplate
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required StudioKind Kind { get; init; }
        public string Category { get; init; } = "Trend";
        public Blueprint? Blueprint { get; init; }
        public string? Source { get; init; }
        public string Symbol { get; init; } = "BTCUSDT";
        public TimeInterval Interval { get; init; } = TimeInterval.OneDay;
    }

    /// <summary>Starting points for new strategies — each a complete, runnable strategy.</summary>
    public static class StudioTemplates
    {
        // ── operand helpers ───────────────────────────────────────────────────────

        private static Operand Price(string field = "close", int offset = 0) => new() { Kind = "price", Field = field, Offset = offset };
        private static Operand Value(double v) => new() { Kind = "value", Value = v };
        private static Operand Input(string name) => new() { Kind = "input", Input = name };
        private static Operand Ind(string id, string? output = null, int offset = 0, params (string Key, object Value)[] p)
        {
            var o = new Operand { Kind = "indicator", Indicator = id, Output = output, Offset = offset };
            foreach (var (k, v) in p) o.Params[k] = v;
            return o;
        }
        private static ConditionNode Cmp(Operand l, string op, Operand r, Operand? r2 = null, int bars = 1)
            => new() { Kind = "compare", Left = l, Op = op, Right = r, Right2 = r2, Bars = bars };
        private static ConditionGroup AllOf(params ConditionNode[] c) => new() { Logic = "all", Conditions = c.ToList() };
        private static ConditionGroup AnyOf(params ConditionNode[] c) => new() { Logic = "any", Conditions = c.ToList() };
        private static BlueprintInput Int(string name, string label, int def, int min, int max)
            => new() { Name = name, Label = label, Type = "int", Default = def, Min = min, Max = max, Step = 1 };
        private static BlueprintInput Num(string name, string label, double def, double min, double max, double step)
            => new() { Name = name, Label = label, Type = "double", Default = def, Min = min, Max = max, Step = step };

        public static IReadOnlyList<StudioTemplate> All { get; } = Build();

        private static List<StudioTemplate> Build() => new()
        {
            new()
            {
                Id = "ema-cross", Name = "EMA crossover", Category = "Trend", Kind = StudioKind.Blueprint,
                Description = "Long when the fast EMA crosses above the slow EMA; out on the cross back.",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("fast", "Fast EMA", 20, 2, 200), Int("slow", "Slow EMA", 50, 5, 400) },
                    Universe = new() { Mode = "single", Symbol = "BTCUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Ind("ema", null, 0, ("length", "fast")), "crosses_above", Ind("ema", null, 0, ("length", "slow")))),
                        ExitLong = AllOf(Cmp(Ind("ema", null, 0, ("length", "fast")), "crosses_below", Ind("ema", null, 0, ("length", "slow")))),
                    },
                    Sizing = new() { Mode = "percent", Value = 95 },
                }
            },
            new()
            {
                Id = "rsi2-reversion", Name = "RSI(2) pullback", Category = "Mean reversion", Kind = StudioKind.Blueprint, Symbol = "SPY",
                Description = "Buy short-term oversold dips in a long-term uptrend; exit on the bounce.",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("rsiLen", "RSI length", 2, 2, 10), Num("entry", "Entry below", 10, 1, 40, 1), Int("trend", "Trend SMA", 200, 50, 400), Int("exitMa", "Exit SMA", 5, 2, 30) },
                    Universe = new() { Mode = "single", Symbol = "SPY" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Ind("rsi", null, 0, ("length", "rsiLen")), "<", Input("entry")), Cmp(Price(), ">", Ind("sma", null, 0, ("length", "trend")))),
                        ExitLong = AllOf(Cmp(Price(), ">", Ind("sma", null, 0, ("length", "exitMa")))),
                    },
                    Sizing = new() { Mode = "percent", Value = 95 },
                    Risk = new() { MaxBarsInTrade = 10, StopLoss = new() { Mode = "percent", Value = 8 } },
                }
            },
            new()
            {
                Id = "donchian-breakout", Name = "Donchian breakout", Category = "Breakout", Kind = StudioKind.Blueprint,
                Description = "Turtle-style: enter on a 20-bar high, exit on a 10-bar low, ATR stop, 1% risk per trade.",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("entryLen", "Entry channel", 20, 5, 100), Int("exitLen", "Exit channel", 10, 3, 60) },
                    Universe = new() { Mode = "single", Symbol = "BTCUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Price(), ">", Ind("dc", "upper", 1, ("length", "entryLen")))),
                        ExitLong = AllOf(Cmp(Price(), "<", Ind("dc", "lower", 1, ("length", "exitLen")))),
                    },
                    Sizing = new() { Mode = "risk", Value = 1, MaxPositionPct = 100 },
                    Risk = new() { StopLoss = new() { Mode = "atr", Value = 2, AtrLength = 20 } },
                }
            },
            new()
            {
                Id = "bollinger-reversion", Name = "Bollinger reversion", Category = "Mean reversion", Kind = StudioKind.Blueprint, Interval = TimeInterval.FourHour,
                Description = "Buy a close below the lower band, sell at the basis, ATR stop.",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("len", "Length", 20, 5, 100), Num("mult", "StdDev", 2, 1, 4, 0.1) },
                    Universe = new() { Mode = "single", Symbol = "ETHUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Price(), "<", Ind("bb", "lower", 0, ("length", "len"), ("mult", "mult")))),
                        ExitLong = AllOf(Cmp(Price(), ">", Ind("bb", "mid", 0, ("length", "len"), ("mult", "mult")))),
                    },
                    Sizing = new() { Mode = "percent", Value = 95 },
                    Risk = new() { StopLoss = new() { Mode = "atr", Value = 3 }, MaxBarsInTrade = 30 },
                }
            },
            new()
            {
                Id = "macd-trend", Name = "MACD with trend filter", Category = "Trend", Kind = StudioKind.Blueprint,
                Description = "MACD line crosses its signal above the 200 EMA; trailing ATR stop.",
                Blueprint = new Blueprint
                {
                    Universe = new() { Mode = "single", Symbol = "BTCUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Ind("macd"), "crosses_above", Ind("macd", "signal")), Cmp(Price(), ">", Ind("ema", null, 0, ("length", 200)))),
                        ExitLong = AllOf(Cmp(Ind("macd"), "crosses_below", Ind("macd", "signal"))),
                    },
                    Sizing = new() { Mode = "percent", Value = 95 },
                    Risk = new() { Trailing = new() { Mode = "atr", Value = 3 } },
                }
            },
            new()
            {
                Id = "supertrend", Name = "Supertrend long/short", Category = "Trend", Kind = StudioKind.Blueprint, Interval = TimeInterval.FourHour,
                Description = "Long on an up-flip, short on a down-flip (shorts need leverage > 1).",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("atrLen", "ATR length", 10, 5, 50), Num("factor", "Factor", 3, 1, 6, 0.1) },
                    Direction = "both",
                    Universe = new() { Mode = "single", Symbol = "SOLUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(Cmp(Ind("supertrend", "dir", 0, ("length", "atrLen"), ("mult", "factor")), "crosses_above", Value(0))),
                        ExitLong = AllOf(Cmp(Ind("supertrend", "dir", 0, ("length", "atrLen"), ("mult", "factor")), "<", Value(0))),
                        EntryShort = AllOf(Cmp(Ind("supertrend", "dir", 0, ("length", "atrLen"), ("mult", "factor")), "crosses_below", Value(0))),
                        ExitShort = AllOf(Cmp(Ind("supertrend", "dir", 0, ("length", "atrLen"), ("mult", "factor")), ">", Value(0))),
                    },
                    Sizing = new() { Mode = "percent", Value = 90 },
                }
            },
            new()
            {
                Id = "squeeze-breakout", Name = "Squeeze breakout", Category = "Volatility", Kind = StudioKind.Blueprint,
                Description = "Enter when price leaves a Bollinger-inside-Keltner squeeze to the upside.",
                Blueprint = new Blueprint
                {
                    Universe = new() { Mode = "single", Symbol = "BTCUSDT" },
                    Rules = new()
                    {
                        EntryLong = AllOf(
                            Cmp(Ind("squeeze", null, 1), "==", Value(1)),
                            Cmp(Ind("squeeze"), "==", Value(0)),
                            Cmp(Price(), ">", Ind("kc", "upper"))),
                        ExitLong = AllOf(Cmp(Price(), "<", Ind("kc", "mid"))),
                    },
                    Sizing = new() { Mode = "percent", Value = 95 },
                    Risk = new() { StopLoss = new() { Mode = "atr", Value = 2 }, TakeProfit = new() { Mode = "r", Value = 3 } },
                }
            },
            new()
            {
                Id = "crypto-rotation", Name = "Crypto momentum rotation", Category = "Cross-sectional", Kind = StudioKind.Blueprint,
                Description = "Hold the top 5 of the 30 most liquid coins by risk-adjusted 30-day momentum, weekly; cash when BTC is below its 100-day.",
                Blueprint = new Blueprint
                {
                    Inputs = { Int("lookback", "Momentum lookback", 30, 5, 180) },
                    Universe = new() { Mode = "universe", TopN = 30, RegimeSymbol = "BTCUSDT" },
                    Rules = new() { Filter = AllOf(Cmp(Price(), ">", Ind("sma", null, 0, ("length", 100)))) },
                    Ranking = new() { Score = "roc(close, lookback) / (stdev(pctchange(close, 1), lookback) + 0.0001)", Top = 5, RebalanceBars = 7, Weighting = "inverse-vol", GrossExposure = 0.95 },
                }
            },
            new()
            {
                Id = "dual-momentum", Name = "Dual momentum (ETFs)", Category = "Cross-sectional", Kind = StudioKind.Blueprint, Symbol = "SPY",
                Description = "Each month hold whichever of US, international and bonds has the best 12-month return; cash when SPY is below its 200-day.",
                Blueprint = new Blueprint
                {
                    Universe = new() { Mode = "basket", Symbols = { "SPY", "EFA", "IEF", "GLD" }, RegimeSymbol = "SPY" },
                    Rules = new() { Filter = AllOf(Cmp(Price(), ">", Ind("sma", null, 0, ("length", 200)))) },
                    Ranking = new() { Score = "roc(close, 252)", Top = 1, RebalanceBars = 21, Weighting = "equal", GrossExposure = 0.98 },
                }
            },
            new()
            {
                Id = "script-blank", Name = "Script: blank", Category = "Script", Kind = StudioKind.Script,
                Description = "An empty single-symbol script with the API at hand.",
                Source = """
                [TradingStrategy("My strategy", "What it does.")]
                public class MyStrategy : ScriptStrategy
                {
                    [Param("Fast", Min = 2, Max = 200)] public int Fast { get; set; } = 20;
                    [Param("Slow", Min = 5, Max = 400)] public int Slow { get; set; } = 50;

                    public override void OnBar()
                    {
                        // Prices: Close, Open, High, Low, Volume, C(ago) …  Indicators: Ema(n), Rsi(n), Atr(n), F("any formula") …
                        // Orders: BuyPercent(pct), ShortPercent(pct), SetTargetPercent(pct), ExitLong(), Exit() …
                        bool up = Ema(Fast) > Ema(Slow);
                        if (up && IsFlat) BuyPercent(95);
                        else if (!up && IsLong) ExitLong();
                        Plot("spread %", (Ema(Fast) / Ema(Slow) - 1) * 100);
                    }
                }
                """
            },
            new()
            {
                Id = "script-keltner", Name = "Script: Keltner breakout + ATR trail", Category = "Script", Kind = StudioKind.Script,
                Description = "Breakout above the Keltner upper band with an ATR trailing stop managed in code.",
                Source = """
                [TradingStrategy("Keltner breakout", "Close above the upper Keltner band; trail at 3 ATR from the highest close.")]
                public class KeltnerBreakout : ScriptStrategy
                {
                    [Param("Length", Min = 5, Max = 100)] public int Length { get; set; } = 20;
                    [Param("Band mult", Min = 0.5, Max = 5, Step = 0.1)] public double Mult { get; set; } = 2;
                    [Param("Trail ATR", Min = 1, Max = 8, Step = 0.5)] public double Trail { get; set; } = 3;

                    private double highest;

                    public override void OnBar()
                    {
                        var kc = Keltner(Length, Mult);
                        if (IsFlat)
                        {
                            if (Close > kc.Upper) { BuyPercent(95); highest = Close; }
                            return;
                        }
                        highest = Math.Max(highest, Close);
                        double stop = highest - Trail * Atr(14);
                        Plot("trail", stop);
                        if (Close < stop) ExitLong();
                    }
                }
                """
            },
            new()
            {
                Id = "script-zscore", Name = "Script: z-score mean reversion (long/short)", Category = "Script", Kind = StudioKind.Script, Interval = TimeInterval.OneHour,
                Description = "Fade 2-sigma moves from the rolling mean; flat at the mean. Shorts need leverage.",
                Source = """
                [TradingStrategy("Z-score reversion", "Short above +Z, long below −Z, flat when the z-score crosses zero.")]
                public class ZScoreReversion : ScriptStrategy
                {
                    [Param("Lookback", Min = 10, Max = 500)] public int Lookback { get; set; } = 48;
                    [Param("Entry Z", Min = 0.5, Max = 4, Step = 0.1)] public double EntryZ { get; set; } = 2;

                    public override void OnBar()
                    {
                        double z = ZScore(Lookback);
                        Plot("z", z);
                        if (double.IsNaN(z)) return;
                        if (IsFlat)
                        {
                            if (z < -EntryZ) SetTargetPercent(90);
                            else if (z > EntryZ) SetTargetPercent(-90);
                        }
                        else if ((IsLong && z >= 0) || (IsShort && z <= 0)) Exit();
                    }
                }
                """
            },
            new()
            {
                Id = "script-xsmom", Name = "Script: cross-sectional momentum", Category = "Script", Kind = StudioKind.Script,
                Description = "Universe script: rank the liquid crypto universe by 60-day return, hold the top 8 equal-weight, monthly.",
                Source = """
                [TradingStrategy("XS momentum (script)", "Top-N by trailing return, equal weight, rebalanced every 30 bars.", RequiresUniverse = true)]
                public class CrossSectionalMomentum : ScriptUniverseStrategy
                {
                    [Param("Lookback", Min = 10, Max = 252)] public int Lookback { get; set; } = 60;
                    [Param("Hold", Min = 1, Max = 30)] public int Hold { get; set; } = 8;
                    [Param("Rebalance every", Min = 1, Max = 60)] public int Every { get; set; } = 30;

                    public override void OnBar(UniverseView u)
                    {
                        if ((BarNumber - 1) % Every != 0) return;
                        var regimeOk = u.Close(RegimeSymbol) > u.F(RegimeSymbol, "sma(close, 100)");
                        var ranked = u.Symbols
                            .Where(s => u.Bars(s) > Lookback)
                            .Select(s => (s, ret: u.F(s, $"roc(close, {Lookback})")))
                            .Where(x => !double.IsNaN(x.ret))
                            .OrderByDescending(x => x.ret)
                            .Take(Hold)
                            .ToList();
                        var weights = new Dictionary<string, double>();
                        if (regimeOk) foreach (var (s, _) in ranked) weights[s] = 0.98 / ranked.Count;
                        SetWeights(weights);
                    }
                }
                """
            },
        };
    }
}
