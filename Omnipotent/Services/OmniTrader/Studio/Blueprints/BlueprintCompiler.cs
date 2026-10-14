using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTrader.Studio.Blueprints
{
    public sealed class CompiledBlueprint
    {
        public required Blueprint Source { get; init; }
        public CompiledFormula? EntryLong { get; init; }
        public CompiledFormula? ExitLong { get; init; }
        public CompiledFormula? EntryShort { get; init; }
        public CompiledFormula? ExitShort { get; init; }
        public CompiledFormula? Filter { get; init; }
        public CompiledFormula? Score { get; init; }
        public Dictionary<string, string> Texts { get; init; } = new();
        public List<StudioDiagnostic> Diagnostics { get; init; } = new();
        public int Lookback { get; init; }
        public List<string> ExternalSymbols { get; init; } = new();
        public IReadOnlyList<ParamDescriptor> Parameters { get; init; } = Array.Empty<ParamDescriptor>();
        public bool Ok => Diagnostics.All(d => d.Severity != "error");
        public bool IsPortfolio => Source.Universe.Mode is "basket" or "universe";
        public bool AllowsLong => Source.Direction is "long" or "both";
        public bool AllowsShort => Source.Direction is "short" or "both";
    }

    /// <summary>
    /// Turns a blueprint into formulas. Every condition tree becomes one expression in the formula
    /// language, so validation, causality and vectorised evaluation come for free, and the compiled
    /// text doubles as a readable summary of what the strategy does.
    /// </summary>
    public static class BlueprintCompiler
    {
        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,40}$");
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "open", "high", "low", "close", "volume", "hl2", "hlc3", "ohlc4", "hlcc4", "bar_index", "time",
            "true", "false", "na", "pi", "e", "and", "or", "not"
        };

        public static CompiledBlueprint Compile(Blueprint bp)
        {
            var diags = new List<StudioDiagnostic>();
            Validate(bp, diags);
            var scope = FormulaScope.With(inputs: bp.Inputs.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name));
            var texts = new Dictionary<string, string>();

            CompiledFormula? Rule(string key, ConditionGroup group)
            {
                if (group == null || group.IsEmpty) return null;
                string? text = GroupToFormula(group, $"rules.{key}", diags);
                if (string.IsNullOrWhiteSpace(text)) return null;
                texts[key] = text;
                try { return FormulaCompiler.Compile(text, scope); }
                catch (FormulaException ex)
                {
                    diags.Add(new StudioDiagnostic { Message = $"{key}: {ex.Message}", Path = $"rules.{key}", Code = "formula" });
                    return null;
                }
            }

            var entryLong = Rule("entryLong", bp.Rules.EntryLong);
            var exitLong = Rule("exitLong", bp.Rules.ExitLong);
            var entryShort = Rule("entryShort", bp.Rules.EntryShort);
            var exitShort = Rule("exitShort", bp.Rules.ExitShort);
            var filter = Rule("filter", bp.Rules.Filter);

            CompiledFormula? score = null;
            if (bp.Ranking != null)
            {
                texts["score"] = bp.Ranking.Score;
                try { score = FormulaCompiler.Compile(bp.Ranking.Score, scope); }
                catch (FormulaException ex) { diags.Add(new StudioDiagnostic { Message = $"score: {ex.Message}", Path = "ranking.score", Code = "formula" }); }
            }

            bool portfolio = bp.Universe.Mode is "basket" or "universe";
            bool longs = bp.Direction is "long" or "both", shorts = bp.Direction is "short" or "both";
            if (bp.Ranking == null)
            {
                if (longs && entryLong == null) diags.Add(new() { Message = "No long entry rule.", Path = "rules.entryLong", Severity = shorts ? "warning" : "error" });
                if (shorts && entryShort == null) diags.Add(new() { Message = "No short entry rule.", Path = "rules.entryShort", Severity = longs ? "warning" : "error" });
                bool hasExit = (longs && exitLong != null) || (shorts && exitShort != null)
                               || bp.Risk.StopLoss.Mode != "none" || bp.Risk.TakeProfit.Mode != "none"
                               || bp.Risk.Trailing.Mode != "none" || bp.Risk.MaxBarsInTrade > 0;
                if (!hasExit) diags.Add(new() { Message = "No exit: positions would never close. Add an exit rule, a stop, a target or a time limit.", Path = "rules.exitLong", Severity = "warning" });
            }
            else if (!portfolio)
                diags.Add(new() { Message = "Ranking needs a basket or universe to rank.", Path = "ranking", Severity = "error" });

            var all = new[] { entryLong, exitLong, entryShort, exitShort, filter, score }.Where(f => f != null).Select(f => f!).ToList();
            int lookback = all.Select(f => f.Lookback).DefaultIfEmpty(0).Max();
            if (bp.Risk.StopLoss.Mode == "atr" || bp.Risk.TakeProfit.Mode == "atr" || bp.Risk.Trailing.Mode == "atr" || bp.Sizing.Mode is "risk" or "voltarget")
                lookback = Math.Max(lookback, Math.Max(bp.Risk.StopLoss.AtrLength, 20) * 3);

            return new CompiledBlueprint
            {
                Source = bp,
                EntryLong = longs ? entryLong : null,
                ExitLong = longs ? exitLong : null,
                EntryShort = shorts ? entryShort : null,
                ExitShort = shorts ? exitShort : null,
                Filter = filter,
                Score = score,
                Texts = texts,
                Diagnostics = diags,
                Lookback = lookback,
                ExternalSymbols = all.SelectMany(f => f.ExternalSymbols).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Parameters = Parameters(bp)
            };
        }

        public static IReadOnlyList<ParamDescriptor> Parameters(Blueprint bp)
        {
            var list = new List<ParamDescriptor>
            {
                new()
                {
                    Name = "TradeSymbol", Label = "Symbol", Type = "symbol", Default = bp.Universe.Symbol, Group = "Universe",
                    Help = bp.Universe.Mode == "single" ? "Symbol traded." : "Benchmark / regime symbol."
                }
            };
            foreach (var input in bp.Inputs.Where(i => !string.IsNullOrWhiteSpace(i.Name)))
            {
                list.Add(new ParamDescriptor
                {
                    Name = input.Name,
                    Label = string.IsNullOrWhiteSpace(input.Label) ? input.Name : input.Label,
                    Type = input.Type switch { "bool" => "bool", "double" => "double", _ => "int" },
                    Default = input.Type == "bool" ? input.Default != 0 : input.Type == "double" ? input.Default : (object)(int)Math.Round(input.Default),
                    Min = input.Min,
                    Max = input.Max,
                    Step = input.Step ?? (input.Type == "int" ? 1 : null),
                    Group = "Inputs",
                    Help = input.Help
                });
            }
            return list;
        }

        private static void Validate(Blueprint bp, List<StudioDiagnostic> diags)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bp.Inputs.Count; i++)
            {
                var input = bp.Inputs[i];
                string path = $"inputs[{i}]";
                if (string.IsNullOrWhiteSpace(input.Name) || !Identifier.IsMatch(input.Name))
                    diags.Add(new() { Message = $"Input name '{input.Name}' must start with a letter and use letters, digits or _.", Path = path });
                else if (Reserved.Contains(input.Name) || IndicatorCatalog.Get(input.Name) != null)
                    diags.Add(new() { Message = $"Input name '{input.Name}' is reserved.", Path = path });
                else if (!seen.Add(input.Name))
                    diags.Add(new() { Message = $"Duplicate input '{input.Name}'.", Path = path });
                if (input.Min.HasValue && input.Max.HasValue && input.Min > input.Max)
                    diags.Add(new() { Message = $"Input '{input.Name}': min is above max.", Path = path });
            }
            if (bp.Direction is not ("long" or "short" or "both"))
                diags.Add(new() { Message = "Direction must be long, short or both.", Path = "direction" });
            if (bp.Universe.Mode is not ("single" or "basket" or "universe"))
                diags.Add(new() { Message = "Universe mode must be single, basket or universe.", Path = "universe.mode" });
            if (bp.Universe.Mode == "single" && string.IsNullOrWhiteSpace(bp.Universe.Symbol))
                diags.Add(new() { Message = "Pick a symbol.", Path = "universe.symbol" });
            if (bp.Universe.Mode == "basket" && bp.Universe.Symbols.Count < 2)
                diags.Add(new() { Message = "A basket needs at least two symbols.", Path = "universe.symbols" });
            if (bp.Universe.Mode == "universe" && bp.Universe.TopN is < 2 or > 300)
                diags.Add(new() { Message = "Universe size must be 2–300.", Path = "universe.topN" });
            if (bp.Sizing.Mode is not ("percent" or "quote" or "risk" or "voltarget"))
                diags.Add(new() { Message = "Sizing must be percent, quote, risk or voltarget.", Path = "sizing.mode" });
            if (bp.Sizing.Value <= 0)
                diags.Add(new() { Message = "Sizing value must be positive.", Path = "sizing.value" });
            if (bp.Sizing.Mode == "risk" && bp.Risk.StopLoss.Mode == "none")
                diags.Add(new() { Message = "Risk sizing needs a stop loss to measure risk against.", Path = "sizing.mode" });
            foreach (var (name, level) in new[] { ("stopLoss", bp.Risk.StopLoss), ("takeProfit", bp.Risk.TakeProfit), ("trailing", bp.Risk.Trailing) })
            {
                if (level.Mode is not ("none" or "percent" or "atr" or "r"))
                    diags.Add(new() { Message = $"{name}: mode must be none, percent, atr or r.", Path = $"risk.{name}" });
                if (level.Mode != "none" && level.Value <= 0)
                    diags.Add(new() { Message = $"{name}: value must be positive.", Path = $"risk.{name}" });
                if (level.Mode == "r" && name != "takeProfit")
                    diags.Add(new() { Message = $"{name}: 'r' only applies to the take profit.", Path = $"risk.{name}" });
                if (level.Mode == "r" && bp.Risk.StopLoss.Mode == "none")
                    diags.Add(new() { Message = "An R-multiple target needs a stop loss.", Path = "risk.takeProfit" });
            }
            if (bp.Ranking != null)
            {
                if (bp.Ranking.Top < 0 || bp.Ranking.Bottom < 0 || bp.Ranking.Top + bp.Ranking.Bottom == 0)
                    diags.Add(new() { Message = "Ranking must hold at least one position.", Path = "ranking.top" });
                if (bp.Ranking.RebalanceBars < 1)
                    diags.Add(new() { Message = "Rebalance every ≥ 1 bar.", Path = "ranking.rebalanceBars" });
                if (bp.Ranking.Weighting is not ("equal" or "inverse-vol" or "score"))
                    diags.Add(new() { Message = "Weighting must be equal, inverse-vol or score.", Path = "ranking.weighting" });
                if (bp.Ranking.GrossExposure is <= 0 or > 10)
                    diags.Add(new() { Message = "Gross exposure must be in (0, 10].", Path = "ranking.grossExposure" });
            }
            if (bp.Direction != "long")
                diags.Add(new() { Message = "Shorts only execute with leverage > 1 (spot accounts cannot short).", Path = "direction", Severity = "info" });
        }

        public static string? GroupToFormula(ConditionGroup group, string path, List<StudioDiagnostic> diags)
        {
            var parts = new List<string>();
            for (int i = 0; i < group.Conditions.Count; i++)
            {
                var c = group.Conditions[i];
                if (!c.Enabled) continue;
                string p = $"{path}.conditions[{i}]";
                string? text = c.Kind switch
                {
                    "group" => c.Group == null ? null : GroupToFormula(c.Group, p + ".group", diags),
                    "formula" => string.IsNullOrWhiteSpace(c.Formula) ? null : $"({c.Formula})",
                    _ => Compare(c, p, diags)
                };
                if (text == null)
                {
                    if (c.Kind == "formula") diags.Add(new() { Message = "Empty formula.", Path = p, Severity = "warning" });
                    continue;
                }
                parts.Add(text);
            }
            if (parts.Count == 0) return null;
            string joined = parts.Count == 1 ? parts[0] : "(" + string.Join(group.Logic == "any" ? " or " : " and ", parts) + ")";
            return group.Negate ? $"not {joined}" : joined;
        }

        private static string? Compare(ConditionNode c, string path, List<StudioDiagnostic> diags)
        {
            string? left = OperandToFormula(c.Left, path + ".left", diags);
            if (left == null) { diags.Add(new() { Message = "Missing left side.", Path = path + ".left" }); return null; }
            string cond;
            switch (c.Op)
            {
                case "rising":
                case "falling":
                    cond = $"{c.Op}({left}, {Math.Max(1, c.Bars)})";
                    return cond;
                case "crosses_above":
                case "crosses_below":
                {
                    string? right = OperandToFormula(c.Right, path + ".right", diags);
                    if (right == null) { diags.Add(new() { Message = "Missing right side.", Path = path + ".right" }); return null; }
                    cond = $"{(c.Op == "crosses_above" ? "crossover" : "crossunder")}({left}, {right})";
                    return cond;
                }
                case "between":
                case "outside":
                {
                    string? lo = OperandToFormula(c.Right, path + ".right", diags);
                    string? hi = OperandToFormula(c.Right2, path + ".right2", diags);
                    if (lo == null || hi == null) { diags.Add(new() { Message = "Between needs a low and a high.", Path = path }); return null; }
                    cond = c.Op == "between" ? $"({left} >= {lo} and {left} <= {hi})" : $"({left} < {lo} or {left} > {hi})";
                    break;
                }
                case ">": case ">=": case "<": case "<=": case "==": case "!=":
                {
                    string? right = OperandToFormula(c.Right, path + ".right", diags);
                    if (right == null) { diags.Add(new() { Message = "Missing right side.", Path = path + ".right" }); return null; }
                    cond = $"{left} {c.Op} {right}";
                    break;
                }
                default:
                    diags.Add(new() { Message = $"Unknown comparison '{c.Op}'.", Path = path + ".op" });
                    return null;
            }
            // "Held for N bars": the comparison must have been true on each of the last N bars.
            return c.Bars > 1 ? $"(streak({cond}) >= {c.Bars})" : $"({cond})";
        }

        public static string? OperandToFormula(Operand? o, string path, List<StudioDiagnostic> diags)
        {
            if (o == null) return null;
            string? expr;
            switch (o.Kind)
            {
                case "value":
                    if (!o.Value.HasValue) { diags.Add(new() { Message = "Missing value.", Path = path }); return null; }
                    expr = o.Value.Value.ToString("R", CultureInfo.InvariantCulture);
                    break;
                case "input":
                    if (string.IsNullOrWhiteSpace(o.Input)) { diags.Add(new() { Message = "Pick an input.", Path = path }); return null; }
                    expr = o.Input;
                    break;
                case "formula":
                    if (string.IsNullOrWhiteSpace(o.Formula)) { diags.Add(new() { Message = "Empty formula.", Path = path }); return null; }
                    expr = $"({o.Formula})";
                    break;
                case "indicator":
                {
                    var spec = string.IsNullOrWhiteSpace(o.Indicator) ? null : IndicatorCatalog.Get(o.Indicator);
                    if (spec == null) { diags.Add(new() { Message = $"Unknown indicator '{o.Indicator}'.", Path = path }); return null; }
                    var args = new List<string>();
                    foreach (var (name, raw) in o.Params)
                    {
                        var input = spec.Inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (input == null) { diags.Add(new() { Message = $"{spec.Short} has no input '{name}'.", Path = path + ".params", Severity = "warning" }); continue; }
                        string? value = ParamToFormula(input, raw);
                        if (value == null) { diags.Add(new() { Message = $"{spec.Short}.{name}: bad value '{raw}'.", Path = path + ".params" }); continue; }
                        args.Add($"{input.Name}={value}");
                    }
                    expr = $"{spec.Id}({string.Join(", ", args)})";
                    string output = string.IsNullOrWhiteSpace(o.Output) ? spec.DefaultOutput : o.Output!;
                    bool known = spec.Outputs.Any(x => string.Equals(x.Name, output, StringComparison.OrdinalIgnoreCase));
                    if (!known && output != "value")
                        diags.Add(new() { Message = $"{spec.Short} has no output '{output}'.", Path = path + ".output" });
                    if (!string.Equals(output, spec.DefaultOutput, StringComparison.OrdinalIgnoreCase)) expr += "." + output;
                    break;
                }
                default:
                {
                    string field = string.IsNullOrWhiteSpace(o.Field) ? "close" : o.Field!.ToLowerInvariant();
                    if (!BarSeries.SourceNames.Contains(field)) { diags.Add(new() { Message = $"Unknown price field '{field}'.", Path = path }); return null; }
                    expr = field;
                    break;
                }
            }
            if (o.Offset < 0) { diags.Add(new() { Message = "Bars ago must be ≥ 0.", Path = path + ".offset" }); return null; }
            if (o.Offset > 0) expr = $"{Wrap(expr)}[{o.Offset}]";
            if (o.Scale.HasValue && o.Scale.Value != 1) expr = $"{Wrap(expr)} * {o.Scale.Value.ToString("R", CultureInfo.InvariantCulture)}";
            return expr;
        }

        private static string Wrap(string expr) => Identifier.IsMatch(expr) || expr.EndsWith(')') && !expr.Contains(' ') ? expr : $"({expr})";

        private static string? ParamToFormula(IndicatorInput input, object? raw)
        {
            if (raw is Newtonsoft.Json.Linq.JValue jv) raw = jv.Value;
            switch (input.Kind)
            {
                case InputKind.Source:
                {
                    string field = (raw?.ToString() ?? "close").ToLowerInvariant();
                    return BarSeries.SourceNames.Contains(field) ? field : null;
                }
                case InputKind.Choice:
                {
                    string choice = raw?.ToString() ?? "";
                    var match = input.Options?.FirstOrDefault(o => string.Equals(o, choice, StringComparison.OrdinalIgnoreCase));
                    return match == null ? null : $"\"{match}\"";
                }
                default:
                {
                    if (raw is string s && Identifier.IsMatch(s)) return s; // an input name
                    try
                    {
                        double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                        return double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : null;
                    }
                    catch { return null; }
                }
            }
        }
    }
}
