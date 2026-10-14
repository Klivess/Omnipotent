using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Technicals.Formula
{
    /// <summary>What a formula may refer to besides the built-ins: named scalar inputs and named series.</summary>
    public sealed class FormulaScope
    {
        public HashSet<string> Inputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllowExternal { get; init; } = true;

        public static FormulaScope Empty => new();

        public static FormulaScope With(IEnumerable<string>? inputs = null, IEnumerable<string>? variables = null)
        {
            var s = new FormulaScope();
            if (inputs != null) foreach (var i in inputs) s.Inputs.Add(i);
            if (variables != null) foreach (var v in variables) s.Variables.Add(v);
            return s;
        }
    }

    public sealed class FormulaContext
    {
        public required BarSeries Bars { get; init; }
        public IReadOnlyDictionary<string, double>? Inputs { get; init; }
        public IReadOnlyDictionary<string, double[]>? Variables { get; init; }
        /// <summary>Bars for another symbol at the same interval (for ext("SYM")).</summary>
        public Func<string, BarSeries?>? External { get; init; }
    }

    public sealed class FormulaDiagnostic
    {
        public required string Message { get; init; }
        public int Position { get; init; }
        public int Length { get; init; } = 1;
    }

    public sealed class CompiledFormula
    {
        internal CompiledFormula(string text, Node root, FormulaBinder binder)
        {
            Text = text;
            Root = root;
            Inputs = binder.ReferencedInputs.ToList();
            Variables = binder.ReferencedVariables.ToList();
            ExternalSymbols = binder.ExternalSymbols.ToList();
            Functions = binder.Functions.ToList();
            Lookback = binder.LookbackOf(root);
            IsBoolean = FormulaBinder.IsBooleanNode(root);
        }

        public string Text { get; }
        internal Node Root { get; }
        public IReadOnlyList<string> Inputs { get; }
        public IReadOnlyList<string> Variables { get; }
        public IReadOnlyList<string> ExternalSymbols { get; }
        public IReadOnlyList<string> Functions { get; }
        /// <summary>Bars of history needed before the value is defined (an estimate; sizes warm-up fetches).</summary>
        public int Lookback { get; }
        /// <summary>True when the top-level expression is a condition (1/0) rather than a number.</summary>
        public bool IsBoolean { get; }

        public double[] Evaluate(FormulaContext ctx) => new FormulaEvaluator(ctx).Series(Root);

        /// <summary>The value at the last bar, or null during warm-up.</summary>
        public double? EvaluateLast(FormulaContext ctx)
        {
            var s = Evaluate(ctx);
            return s.Length > 0 && TA.IsNum(s[^1]) ? s[^1] : null;
        }
    }

    public static class FormulaCompiler
    {
        public static CompiledFormula Compile(string text, FormulaScope? scope = null)
        {
            var root = FormulaParser.Parse(text);
            var binder = new FormulaBinder(scope ?? FormulaScope.Empty);
            binder.Bind(root);
            return new CompiledFormula(text, root, binder);
        }

        /// <summary>Validate without throwing — for editors that underline as you type.</summary>
        public static (CompiledFormula? Formula, List<FormulaDiagnostic> Errors) Check(string text, FormulaScope? scope = null)
        {
            try { return (Compile(text, scope), new List<FormulaDiagnostic>()); }
            catch (FormulaException ex)
            {
                return (null, new List<FormulaDiagnostic> { new() { Message = ex.Message, Position = ex.Position, Length = ex.Length } });
            }
        }

        /// <summary>Everything the language knows, for autocomplete and docs.</summary>
        public static object Reference() => new
        {
            Series = FormulaBinder.SeriesNames,
            Constants = new[] { "true", "false", "na", "pi", "e" },
            Operators = new[] { "+", "-", "*", "/", "%", "^", ">", ">=", "<", "<=", "==", "!=", "and", "or", "not", "?:", "x[n]", "f(...).output" },
            Functions = FormulaBinder.Builtins.Select(b => new { b.Key, b.Value.Signature, b.Value.Description }),
            Indicators = IndicatorCatalog.All.Select(s => new
            {
                s.Id,
                s.Name,
                Signature = $"{s.Id}({string.Join(", ", s.Inputs.Select(i => i.Kind == InputKind.Source ? "src" : i.Name))})",
                Outputs = s.Outputs.Select(o => o.Name)
            })
        };
    }

    internal sealed record Builtin(int MinArgs, int MaxArgs, string Signature, string Description, Func<int, ArgKind> Kind);

    internal enum ArgKind { Series, Scalar, String }

    /// <summary>Resolves names and checks arity, argument kinds and offsets before anything is evaluated.</summary>
    internal sealed class FormulaBinder
    {
        public static readonly string[] SeriesNames = { "open", "high", "low", "close", "volume", "hl2", "hlc3", "ohlc4", "hlcc4", "bar_index", "time" };

        private static ArgKind S(int _) => ArgKind.Series;

        public static readonly Dictionary<string, Builtin> Builtins = new(StringComparer.OrdinalIgnoreCase)
        {
            ["crossover"] = new(2, 2, "crossover(a, b)", "1 on the bar a crosses above b", S),
            ["crossunder"] = new(2, 2, "crossunder(a, b)", "1 on the bar a crosses below b", S),
            ["cross"] = new(2, 2, "cross(a, b)", "1 on the bar a crosses b either way", S),
            ["rising"] = new(2, 2, "rising(x, n)", "x rose on each of the last n bars", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["falling"] = new(2, 2, "falling(x, n)", "x fell on each of the last n bars", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["highest"] = new(2, 2, "highest(x, n)", "highest x over n bars", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["lowest"] = new(2, 2, "lowest(x, n)", "lowest x over n bars", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["highestbars"] = new(2, 2, "highestbars(x, n)", "bars since the n-bar high", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["lowestbars"] = new(2, 2, "lowestbars(x, n)", "bars since the n-bar low", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["sum"] = new(2, 2, "sum(x, n)", "rolling sum", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["avg"] = new(2, 2, "avg(x, n)", "rolling mean (same as sma)", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["change"] = new(1, 2, "change(x, n=1)", "x − x[n]", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["pctchange"] = new(1, 2, "pctchange(x, n=1)", "% change over n bars", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["ref"] = new(2, 2, "ref(x, n)", "x n bars ago (same as x[n])", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["barssince"] = new(1, 1, "barssince(cond)", "bars since cond was last true", S),
            ["valuewhen"] = new(2, 3, "valuewhen(cond, x, n=0)", "x when cond was last true (n-th occurrence)", i => i == 2 ? ArgKind.Scalar : ArgKind.Series),
            ["streak"] = new(1, 1, "streak(cond)", "bars in a row cond has held", S),
            ["corr"] = new(3, 3, "corr(a, b, n)", "rolling correlation", i => i == 2 ? ArgKind.Scalar : ArgKind.Series),
            ["beta"] = new(3, 3, "beta(a, b, n)", "rolling beta of a's returns on b's", i => i == 2 ? ArgKind.Scalar : ArgKind.Series),
            ["abs"] = new(1, 1, "abs(x)", "absolute value", S),
            ["sign"] = new(1, 1, "sign(x)", "−1, 0 or 1", S),
            ["sqrt"] = new(1, 1, "sqrt(x)", "square root", S),
            ["log"] = new(1, 1, "log(x)", "natural log", S),
            ["log10"] = new(1, 1, "log10(x)", "base-10 log", S),
            ["exp"] = new(1, 1, "exp(x)", "e^x", S),
            ["floor"] = new(1, 1, "floor(x)", "round down", S),
            ["ceil"] = new(1, 1, "ceil(x)", "round up", S),
            ["round"] = new(1, 2, "round(x, digits=0)", "round", i => i == 1 ? ArgKind.Scalar : ArgKind.Series),
            ["pow"] = new(2, 2, "pow(a, b)", "a^b", S),
            ["min"] = new(2, 8, "min(a, b, …)", "element-wise minimum", S),
            ["max"] = new(2, 8, "max(a, b, …)", "element-wise maximum", S),
            ["clamp"] = new(3, 3, "clamp(x, lo, hi)", "bound x", S),
            ["nz"] = new(1, 2, "nz(x, v=0)", "replace na with v", S),
            ["na"] = new(1, 1, "na(x)", "1 where x has no value", S),
            ["iff"] = new(3, 3, "iff(cond, a, b)", "a where cond, else b", S),
            ["hour"] = new(0, 0, "hour()", "UTC hour of the bar", S),
            ["minute"] = new(0, 0, "minute()", "UTC minute of the bar", S),
            ["dayofweek"] = new(0, 0, "dayofweek()", "1 = Monday … 7 = Sunday", S),
            ["dayofmonth"] = new(0, 0, "dayofmonth()", "1–31", S),
            ["month"] = new(0, 0, "month()", "1–12", S),
            ["year"] = new(0, 0, "year()", "calendar year", S),
            ["ext"] = new(1, 2, "ext(\"SYMBOL\", \"close\")", "another symbol's series at this interval, from bars already closed by this bar's open", i => ArgKind.String),
        };

        private readonly FormulaScope scope;
        public HashSet<string> ReferencedInputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExternalSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FormulaBinder(FormulaScope scope) { this.scope = scope; }

        public void Bind(Node node)
        {
            switch (node)
            {
                case NumberNode:
                    return;
                case StringNode s:
                    throw new FormulaException("A string is only valid as an argument to ext() or a choice input", s.Pos, s.Len);
                case IdentNode id:
                    BindIdent(id);
                    return;
                case UnaryNode u:
                    Bind(u.Operand);
                    return;
                case BinaryNode b:
                    Bind(b.Left);
                    Bind(b.Right);
                    return;
                case TernaryNode t:
                    Bind(t.Condition); Bind(t.WhenTrue); Bind(t.WhenFalse);
                    return;
                case IndexNode ix:
                    Bind(ix.Target);
                    Bind(ix.Offset);
                    if (!IsConstant(ix.Offset))
                        throw new FormulaException("An offset must be a number or an input, not a series", ix.Offset.Pos, ix.Offset.Len);
                    if (TryFold(ix.Offset, out var offsetValue) && (offsetValue < 0 || offsetValue != Math.Floor(offsetValue)))
                        throw new FormulaException("An offset must be a whole number ≥ 0 — a negative offset reads the future", ix.Offset.Pos, ix.Offset.Len);
                    return;
                case MemberNode m:
                    if (m.Target is not CallNode call)
                        throw new FormulaException("'.' selects an indicator output, e.g. macd(close).signal", m.Pos, m.Len);
                    Bind(call);
                    var spec = IndicatorCatalog.Get(call.Name);
                    if (spec == null)
                        throw new FormulaException($"{call.Name}() has a single output", m.Pos, m.Len);
                    if (!spec.Outputs.Any(o => string.Equals(o.Name, m.Member, StringComparison.OrdinalIgnoreCase))
                        && !AdditionalOutputs(spec.Id).Contains(m.Member, StringComparer.OrdinalIgnoreCase))
                        throw new FormulaException($"{spec.Id} has no output '{m.Member}' (has {string.Join(", ", spec.Outputs.Select(o => o.Name).Concat(AdditionalOutputs(spec.Id)))})", m.Pos, m.Len);
                    return;
                case CallNode c:
                    BindCall(c);
                    return;
            }
        }

        /// <summary>Outputs some indicators compute but do not draw by default.</summary>
        internal static string[] AdditionalOutputs(string id) => id switch
        {
            "bb" => new[] { "pctb", "width" },
            "linreg" => new[] { "slope", "r2" },
            "pivots" => new[] { "r3", "s3" },
            _ => Array.Empty<string>()
        };

        private void BindIdent(IdentNode id)
        {
            string name = id.Name.ToLowerInvariant();
            if (SeriesNames.Contains(name) || name is "true" or "false" or "na" or "pi" or "e") return;
            if (scope.Inputs.Contains(id.Name)) { ReferencedInputs.Add(id.Name); return; }
            if (scope.Variables.Contains(id.Name)) { ReferencedVariables.Add(id.Name); return; }
            if (Builtins.ContainsKey(name) || IndicatorCatalog.Get(name) != null)
                throw new FormulaException($"{id.Name} is a function — call it: {id.Name}(…)", id.Pos, id.Len);
            throw new FormulaException($"Unknown name '{id.Name}'", id.Pos, id.Len);
        }

        private void BindCall(CallNode c)
        {
            Functions.Add(c.Name);
            if (Builtins.TryGetValue(c.Name, out var builtin))
            {
                if (c.Args.Any(a => a.Name != null))
                    throw new FormulaException($"{c.Name}() takes positional arguments only", c.Pos, c.Len);
                if (c.Args.Count < builtin.MinArgs || c.Args.Count > builtin.MaxArgs)
                    throw new FormulaException($"{builtin.Signature}: expected {Arity(builtin)} argument(s), got {c.Args.Count}", c.Pos, c.Len);
                for (int i = 0; i < c.Args.Count; i++)
                {
                    var arg = c.Args[i].Value;
                    var kind = builtin.Kind(i);
                    if (kind == ArgKind.String)
                    {
                        if (arg is not StringNode str)
                            throw new FormulaException($"{c.Name}() expects a quoted string here", arg.Pos, arg.Len);
                        if (c.Name == "ext")
                        {
                            if (!scope.AllowExternal) throw new FormulaException("ext() is not available here", c.Pos, c.Len);
                            if (i == 0) ExternalSymbols.Add(str.Value.ToUpperInvariant());
                            else if (BarSeries.SourceNames.All(s => !string.Equals(s, str.Value, StringComparison.OrdinalIgnoreCase)))
                                throw new FormulaException($"Unknown field '{str.Value}'", arg.Pos, arg.Len);
                        }
                        continue;
                    }
                    Bind(arg);
                    if (kind == ArgKind.Scalar && !IsConstant(arg))
                        throw new FormulaException($"{builtin.Signature}: argument {i + 1} must be a number or input", arg.Pos, arg.Len);
                }
                return;
            }

            var spec = IndicatorCatalog.Get(c.Name)
                ?? throw new FormulaException($"Unknown function '{c.Name}'", c.Pos, c.Name.Length);
            MapIndicatorArgs(spec, c, bind: true);
        }

        private static string Arity(Builtin b) => b.MinArgs == b.MaxArgs ? b.MinArgs.ToString() : $"{b.MinArgs}–{b.MaxArgs}";

        /// <summary>
        /// Map call arguments onto an indicator's inputs: named arguments by name, positional ones in
        /// order. A leading source input may be omitted — rsi(14) means rsi(close, 14).
        /// </summary>
        internal Dictionary<IndicatorInput, Node> MapIndicatorArgs(IndicatorSpec spec, CallNode c, bool bind)
        {
            var map = new Dictionary<IndicatorInput, Node>();
            var positional = c.Args.Where(a => a.Name == null).Select(a => a.Value).ToList();
            int p = 0;
            foreach (var input in spec.Inputs)
            {
                var named = c.Args.FirstOrDefault(a => a.Name != null && string.Equals(a.Name, input.Name, StringComparison.OrdinalIgnoreCase));
                if (named != null) { map[input] = named.Value; continue; }
                if (p >= positional.Count) continue;
                var arg = positional[p];
                if (input.Kind == InputKind.Source && IsConstant(arg) && arg is not StringNode) continue; // source omitted
                map[input] = arg;
                p++;
            }
            if (p < positional.Count)
                throw new FormulaException($"{spec.Id}() takes at most {spec.Inputs.Count} argument(s): {string.Join(", ", spec.Inputs.Select(i => i.Name))}",
                    positional[p].Pos, positional[p].Len);
            foreach (var arg in c.Args.Where(a => a.Name != null))
                if (!spec.Inputs.Any(i => string.Equals(i.Name, arg.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new FormulaException($"{spec.Id}() has no input '{arg.Name}'", arg.Value.Pos, arg.Value.Len);

            if (bind)
            {
                foreach (var (input, node) in map)
                {
                    if (input.Kind == InputKind.Choice)
                    {
                        if (node is not StringNode s)
                            throw new FormulaException($"{spec.Id}: {input.Name} must be one of \"{string.Join("\", \"", input.Options ?? Array.Empty<string>())}\"", node.Pos, node.Len);
                        if (input.Options != null && !input.Options.Contains(s.Value, StringComparer.OrdinalIgnoreCase))
                            throw new FormulaException($"{spec.Id}: '{s.Value}' is not one of {string.Join(", ", input.Options)}", node.Pos, node.Len);
                        continue;
                    }
                    if (input.Kind == InputKind.Source && node is StringNode src)
                    {
                        if (BarSeries.SourceNames.All(n => !string.Equals(n, src.Value, StringComparison.OrdinalIgnoreCase)))
                            throw new FormulaException($"Unknown source '{src.Value}'", node.Pos, node.Len);
                        continue;
                    }
                    Bind(node);
                    if (input.Kind != InputKind.Source && !IsConstant(node))
                        throw new FormulaException($"{spec.Id}: {input.Name} must be a number or input, not a series", node.Pos, node.Len);
                }
            }
            return map;
        }

        /// <summary>True when the expression has one value for every bar (numbers, inputs, arithmetic on them).</summary>
        public bool IsConstant(Node node) => node switch
        {
            NumberNode => true,
            StringNode => true,
            IdentNode id => scope.Inputs.Contains(id.Name) || id.Name.ToLowerInvariant() is "pi" or "e" or "true" or "false",
            UnaryNode u => IsConstant(u.Operand),
            BinaryNode b => IsConstant(b.Left) && IsConstant(b.Right),
            TernaryNode t => IsConstant(t.Condition) && IsConstant(t.WhenTrue) && IsConstant(t.WhenFalse),
            CallNode c when c.Name is "abs" or "min" or "max" or "round" or "floor" or "ceil" or "sqrt" or "pow" or "log" or "exp" or "clamp"
                => c.Args.All(a => IsConstant(a.Value)),
            _ => false
        };

        public static bool IsBooleanNode(Node node) => node switch
        {
            BinaryNode b => b.Op is ">" or ">=" or "<" or "<=" or "==" or "!=" or "and" or "or",
            UnaryNode u => u.Op == "not",
            CallNode c => c.Name is "crossover" or "crossunder" or "cross" or "rising" or "falling" or "na",
            IdentNode id => id.Name is "true" or "false",
            TernaryNode t => IsBooleanNode(t.WhenTrue) && IsBooleanNode(t.WhenFalse),
            _ => false
        };

        /// <summary>Warm-up estimate: bars of history needed before the expression is defined.</summary>
        public int LookbackOf(Node node)
        {
            switch (node)
            {
                case UnaryNode u: return LookbackOf(u.Operand);
                case BinaryNode b: return Math.Max(LookbackOf(b.Left), LookbackOf(b.Right));
                case TernaryNode t: return Math.Max(LookbackOf(t.Condition), Math.Max(LookbackOf(t.WhenTrue), LookbackOf(t.WhenFalse)));
                case IndexNode ix: return LookbackOf(ix.Target) + (int)ConstValue(ix.Offset, 0);
                case MemberNode m: return LookbackOf(m.Target);
                case CallNode c when Builtins.ContainsKey(c.Name):
                {
                    int inner = c.Args.Where(a => a.Value is not StringNode).Select(a => LookbackOf(a.Value)).DefaultIfEmpty(0).Max();
                    int window = c.Name switch
                    {
                        "rising" or "falling" or "highest" or "lowest" or "highestbars" or "lowestbars" or "sum" or "avg" or "ref" or "change" or "pctchange"
                            => c.Args.Count > 1 ? (int)ConstValue(c.Args[1].Value, 1) : 1,
                        "corr" or "beta" => (int)ConstValue(c.Args[2].Value, 20),
                        "crossover" or "crossunder" or "cross" => 1,
                        _ => 0
                    };
                    return inner + window;
                }
                case CallNode c:
                {
                    var spec = IndicatorCatalog.Get(c.Name);
                    if (spec == null) return 0;
                    var map = MapIndicatorArgs(spec, c, bind: false);
                    var args = new IndicatorArgs { Bars = new BarSeries(Array.Empty<DateTime>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>()), Spec = spec };
                    int inner = 0;
                    foreach (var (input, n) in map)
                    {
                        if (input.Kind is InputKind.Int or InputKind.Double) args.Numbers[input.Name] = ConstValue(n, Convert.ToDouble(input.Default ?? 0, CultureInfo.InvariantCulture));
                        else if (input.Kind == InputKind.Source && n is not StringNode) inner = Math.Max(inner, LookbackOf(n));
                    }
                    int own;
                    try { own = spec.Lookback(args); } catch { own = 50; }
                    return inner + own;
                }
                default: return 0;
            }
        }

        /// <summary>Fold an expression made only of literals; false when it involves an input or a series.</summary>
        internal static bool TryFold(Node node, out double value)
        {
            value = 0;
            switch (node)
            {
                case NumberNode n: value = n.Value; return true;
                case UnaryNode { Op: "neg" } u when TryFold(u.Operand, out var inner): value = -inner; return true;
                case BinaryNode b when TryFold(b.Left, out var l) && TryFold(b.Right, out var r):
                    value = b.Op switch { "+" => l + r, "-" => l - r, "*" => l * r, "/" => r == 0 ? double.NaN : l / r, _ => double.NaN };
                    return !double.IsNaN(value);
                default: return false;
            }
        }

        /// <summary>Constant-fold a scalar expression with inputs at a neutral value (for estimates only).</summary>
        private double ConstValue(Node node, double fallback)
        {
            try
            {
                return node switch
                {
                    NumberNode n => n.Value,
                    UnaryNode { Op: "neg" } u => -ConstValue(u.Operand, fallback),
                    BinaryNode b => b.Op switch
                    {
                        "+" => ConstValue(b.Left, fallback) + ConstValue(b.Right, fallback),
                        "-" => ConstValue(b.Left, fallback) - ConstValue(b.Right, fallback),
                        "*" => ConstValue(b.Left, fallback) * ConstValue(b.Right, fallback),
                        "/" => ConstValue(b.Left, fallback) / ConstValue(b.Right, fallback),
                        _ => fallback
                    },
                    _ => fallback
                };
            }
            catch { return fallback; }
        }
    }

    /// <summary>Vectorised evaluation with sub-expression memoisation.</summary>
    internal sealed class FormulaEvaluator
    {
        private readonly FormulaContext ctx;
        private readonly int n;
        private readonly Dictionary<string, Dictionary<string, double[]>> callMemo = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double[]> externalMemo = new(StringComparer.OrdinalIgnoreCase);

        public FormulaEvaluator(FormulaContext ctx)
        {
            this.ctx = ctx;
            n = ctx.Bars.Count;
        }

        /// <summary>Evaluate to a full-length series (scalars broadcast).</summary>
        public double[] Series(Node node)
        {
            var v = Eval(node);
            return v.Series ?? TA.Const(n, v.Scalar);
        }

        private readonly record struct Value(double Scalar, double[]? Series, Dictionary<string, double[]>? Outputs = null)
        {
            public static Value Of(double d) => new(d, null);
            public static Value Of(double[] s) => new(0, s);
            public bool IsSeries => Series != null;
            public double At(int i) => Series != null ? Series[i] : Scalar;
        }

        private double ScalarOf(Node node)
        {
            var v = Eval(node);
            if (v.IsSeries) throw new FormulaException("Expected a number, got a series", node.Pos, node.Len);
            return v.Scalar;
        }

        private Value Eval(Node node)
        {
            switch (node)
            {
                case NumberNode num: return Value.Of(num.Value);
                case IdentNode id: return Ident(id);
                case UnaryNode u:
                {
                    var x = Eval(u.Operand);
                    Func<double, double> f = u.Op == "neg" ? v => -v : v => !TA.IsNum(v) ? double.NaN : v == 0 ? 1 : 0;
                    return Apply(x, f);
                }
                case BinaryNode b: return Binary(b.Op, Eval(b.Left), Eval(b.Right));
                case TernaryNode t:
                {
                    var c = Eval(t.Condition);
                    var a = Eval(t.WhenTrue);
                    var f = Eval(t.WhenFalse);
                    if (!c.IsSeries && !a.IsSeries && !f.IsSeries)
                        return Value.Of(!TA.IsNum(c.Scalar) ? double.NaN : c.Scalar != 0 ? a.Scalar : f.Scalar);
                    var r = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double cv = c.At(i);
                        r[i] = !TA.IsNum(cv) ? double.NaN : cv != 0 ? a.At(i) : f.At(i);
                    }
                    return Value.Of(r);
                }
                case IndexNode ix:
                {
                    var target = Eval(ix.Target);
                    int offset = (int)Math.Round(ScalarOf(ix.Offset));
                    if (offset < 0) throw new FormulaException("Negative offsets read the future", ix.Offset.Pos, ix.Offset.Len);
                    return target.IsSeries ? Value.Of(TA.Shift(target.Series!, offset)) : target;
                }
                case MemberNode m:
                {
                    var call = (CallNode)m.Target;
                    var outputs = Indicator(call);
                    var key = outputs.Keys.FirstOrDefault(k => string.Equals(k, m.Member, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FormulaException($"No output '{m.Member}'", m.Pos, m.Len);
                    return Value.Of(outputs[key]);
                }
                case CallNode c:
                {
                    if (FormulaBinder.Builtins.ContainsKey(c.Name)) return Builtin(c);
                    var outputs = Indicator(c);
                    var spec = IndicatorCatalog.Get(c.Name)!;
                    return new Value(0, outputs[spec.DefaultOutput], outputs);
                }
                default:
                    throw new FormulaException("Unsupported expression", node.Pos, node.Len);
            }
        }

        private Value Ident(IdentNode id)
        {
            var bars = ctx.Bars;
            switch (id.Name.ToLowerInvariant())
            {
                case "true": return Value.Of(1);
                case "false": return Value.Of(0);
                case "na": return Value.Of(double.NaN);
                case "pi": return Value.Of(Math.PI);
                case "e": return Value.Of(Math.E);
                case "bar_index":
                {
                    var r = new double[n];
                    for (int i = 0; i < n; i++) r[i] = i;
                    return Value.Of(r);
                }
                case "time": return Value.Of(TA.Calendar(bars.Time, t => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds()));
            }
            if (bars.Source(id.Name) is { } src) return Value.Of(src);
            if (ctx.Inputs != null && ctx.Inputs.TryGetValue(id.Name, out var input)) return Value.Of(input);
            if (ctx.Variables != null && ctx.Variables.TryGetValue(id.Name, out var series)) return Value.Of(series);
            throw new FormulaException($"'{id.Name}' has no value here", id.Pos, id.Len);
        }

        private static Value Apply(Value x, Func<double, double> f)
        {
            if (!x.IsSeries) return Value.Of(TA.IsNum(x.Scalar) ? f(x.Scalar) : double.NaN);
            var r = new double[x.Series!.Length];
            for (int i = 0; i < r.Length; i++) r[i] = TA.IsNum(x.Series[i]) ? f(x.Series[i]) : double.NaN;
            return Value.Of(r);
        }

        private Value Binary(string op, Value a, Value b)
        {
            Func<double, double, double> f = op switch
            {
                "+" => (x, y) => x + y,
                "-" => (x, y) => x - y,
                "*" => (x, y) => x * y,
                "/" => (x, y) => y == 0 ? double.NaN : x / y,
                "%" => (x, y) => y == 0 ? double.NaN : x % y,
                "^" => Math.Pow,
                ">" => (x, y) => x > y ? 1 : 0,
                ">=" => (x, y) => x >= y ? 1 : 0,
                "<" => (x, y) => x < y ? 1 : 0,
                "<=" => (x, y) => x <= y ? 1 : 0,
                "==" => (x, y) => Math.Abs(x - y) < 1e-12 ? 1 : 0,
                "!=" => (x, y) => Math.Abs(x - y) >= 1e-12 ? 1 : 0,
                _ => (x, y) => double.NaN
            };
            bool logical = op is "and" or "or";
            if (!a.IsSeries && !b.IsSeries)
                return Value.Of(logical ? Logic(op, a.Scalar, b.Scalar) : TA.IsNum(a.Scalar) && TA.IsNum(b.Scalar) ? f(a.Scalar, b.Scalar) : double.NaN);
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = a.At(i), y = b.At(i);
                r[i] = logical ? Logic(op, x, y) : TA.IsNum(x) && TA.IsNum(y) ? f(x, y) : double.NaN;
            }
            return Value.Of(r);
        }

        /// <summary>Three-valued logic: a known false decides an AND, a known true decides an OR.</summary>
        private static double Logic(string op, double x, double y)
        {
            bool xn = TA.IsNum(x), yn = TA.IsNum(y);
            if (op == "and")
            {
                if ((xn && x == 0) || (yn && y == 0)) return 0;
                return xn && yn ? 1 : double.NaN;
            }
            if ((xn && x != 0) || (yn && y != 0)) return 1;
            return xn && yn ? 0 : double.NaN;
        }

        private Dictionary<string, double[]> Indicator(CallNode c)
        {
            string key = Canonical(c);
            if (callMemo.TryGetValue(key, out var hit)) return hit;
            var spec = IndicatorCatalog.Get(c.Name) ?? throw new FormulaException($"Unknown function '{c.Name}'", c.Pos, c.Len);
            var binder = new FormulaBinder(FormulaScope.With(ctx.Inputs?.Keys, ctx.Variables?.Keys));
            var map = binder.MapIndicatorArgs(spec, c, bind: false);
            var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var overrides = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (input, node) in map)
            {
                switch (input.Kind)
                {
                    case InputKind.Source:
                        if (node is StringNode s) inputs[input.Name] = s.Value;
                        else overrides[input.Name] = Series(node);
                        break;
                    case InputKind.Choice:
                        inputs[input.Name] = ((StringNode)node).Value;
                        break;
                    default:
                        inputs[input.Name] = ScalarOf(node);
                        break;
                }
            }
            var result = IndicatorEngine.Compute(spec, ctx.Bars, inputs, overrides);
            foreach (var extra in result.Keys.ToList())
                if (result[extra].Length != n) result[extra] = Fit(result[extra]);
            callMemo[key] = result;
            return result;
        }

        private double[] Fit(double[] a)
        {
            if (a.Length == n) return a;
            var r = TA.NaNs(n);
            Array.Copy(a, 0, r, 0, Math.Min(a.Length, n));
            return r;
        }

        private Value Builtin(CallNode c)
        {
            var args = c.Args.Select(a => a.Value).ToList();
            double[] A(int i) => Series(args[i]);
            int N(int i, int fallback = 1) => i < args.Count ? Math.Max(1, (int)Math.Round(ScalarOf(args[i]))) : fallback;
            var bars = ctx.Bars;

            switch (c.Name)
            {
                case "crossover": return Value.Of(TA.CrossOver(A(0), A(1)));
                case "crossunder": return Value.Of(TA.CrossUnder(A(0), A(1)));
                case "cross":
                {
                    var up = TA.CrossOver(A(0), A(1));
                    var dn = TA.CrossUnder(A(0), A(1));
                    return Value.Of(TA.Zip(up, dn, (x, y) => x == 1 || y == 1 ? 1 : 0));
                }
                case "rising": return Value.Of(TA.Rising(A(0), N(1)));
                case "falling": return Value.Of(TA.Falling(A(0), N(1)));
                case "highest": return Value.Of(TA.Highest(A(0), N(1)));
                case "lowest": return Value.Of(TA.Lowest(A(0), N(1)));
                case "highestbars": return Value.Of(TA.HighestBars(A(0), N(1)));
                case "lowestbars": return Value.Of(TA.LowestBars(A(0), N(1)));
                case "sum": return Value.Of(TA.Sum(A(0), N(1)));
                case "avg": return Value.Of(TA.Sma(A(0), N(1)));
                case "change": return Value.Of(TA.Change(A(0), N(1)));
                case "pctchange": return Value.Of(TA.Roc(A(0), N(1)));
                case "ref":
                {
                    int offset = (int)Math.Round(ScalarOf(args[1]));
                    if (offset < 0) throw new FormulaException("Negative offsets read the future", args[1].Pos, args[1].Len);
                    return Value.Of(TA.Shift(A(0), offset));
                }
                case "barssince": return Value.Of(TA.BarsSince(A(0)));
                case "valuewhen": return Value.Of(TA.ValueWhen(A(0), A(1), args.Count > 2 ? (int)Math.Round(ScalarOf(args[2])) : 0));
                case "streak": return Value.Of(TA.Streak(A(0)));
                case "corr": return Value.Of(TA.Correlation(A(0), A(1), N(2)));
                case "beta": return Value.Of(TA.Beta(A(0), A(1), N(2)));
                case "abs": return Apply(Eval(args[0]), Math.Abs);
                case "sign": return Apply(Eval(args[0]), v => Math.Sign(v));
                case "sqrt": return Apply(Eval(args[0]), v => v < 0 ? double.NaN : Math.Sqrt(v));
                case "log": return Apply(Eval(args[0]), v => v <= 0 ? double.NaN : Math.Log(v));
                case "log10": return Apply(Eval(args[0]), v => v <= 0 ? double.NaN : Math.Log10(v));
                case "exp": return Apply(Eval(args[0]), Math.Exp);
                case "floor": return Apply(Eval(args[0]), Math.Floor);
                case "ceil": return Apply(Eval(args[0]), Math.Ceiling);
                case "round":
                {
                    int digits = args.Count > 1 ? Math.Clamp((int)ScalarOf(args[1]), 0, 12) : 0;
                    return Apply(Eval(args[0]), v => Math.Round(v, digits));
                }
                case "pow": return Binary("^", Eval(args[0]), Eval(args[1]));
                case "min":
                case "max":
                {
                    var acc = Eval(args[0]);
                    for (int i = 1; i < args.Count; i++)
                    {
                        var next = Eval(args[i]);
                        acc = Combine(acc, next, c.Name == "min" ? Math.Min : Math.Max);
                    }
                    return acc;
                }
                case "clamp":
                {
                    var lo = Combine(Eval(args[0]), Eval(args[1]), Math.Max);
                    return Combine(lo, Eval(args[2]), Math.Min);
                }
                case "nz":
                {
                    double v = args.Count > 1 ? ScalarOf(args[1]) : 0;
                    var x = Eval(args[0]);
                    return x.IsSeries ? Value.Of(TA.Nz(x.Series!, v)) : Value.Of(TA.IsNum(x.Scalar) ? x.Scalar : v);
                }
                case "na":
                {
                    var x = Eval(args[0]);
                    if (!x.IsSeries) return Value.Of(TA.IsNum(x.Scalar) ? 0 : 1);
                    var r = new double[n];
                    for (int i = 0; i < n; i++) r[i] = TA.IsNum(x.Series![i]) ? 0 : 1;
                    return Value.Of(r);
                }
                case "iff":
                {
                    var cond = Eval(args[0]); var a = Eval(args[1]); var b = Eval(args[2]);
                    var r = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double cv = cond.At(i);
                        r[i] = !TA.IsNum(cv) ? double.NaN : cv != 0 ? a.At(i) : b.At(i);
                    }
                    return Value.Of(r);
                }
                case "hour": return Value.Of(TA.Calendar(bars.Time, t => t.Hour));
                case "minute": return Value.Of(TA.Calendar(bars.Time, t => t.Minute));
                case "dayofweek": return Value.Of(TA.Calendar(bars.Time, t => ((int)t.DayOfWeek + 6) % 7 + 1));
                case "dayofmonth": return Value.Of(TA.Calendar(bars.Time, t => t.Day));
                case "month": return Value.Of(TA.Calendar(bars.Time, t => t.Month));
                case "year": return Value.Of(TA.Calendar(bars.Time, t => t.Year));
                case "ext":
                {
                    string symbol = ((StringNode)args[0]).Value;
                    string field = args.Count > 1 ? ((StringNode)args[1]).Value : "close";
                    return Value.Of(External(symbol, field, c));
                }
            }
            throw new FormulaException($"Unknown function '{c.Name}'", c.Pos, c.Len);
        }

        private Value Combine(Value a, Value b, Func<double, double, double> f)
        {
            if (!a.IsSeries && !b.IsSeries) return Value.Of(TA.IsNum(a.Scalar) && TA.IsNum(b.Scalar) ? f(a.Scalar, b.Scalar) : double.NaN);
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = a.At(i), y = b.At(i);
                r[i] = TA.IsNum(x) && TA.IsNum(y) ? f(x, y) : double.NaN;
            }
            return Value.Of(r);
        }

        /// <summary>
        /// Another symbol's field aligned to these bars. A bar of the other symbol is usable at bar i
        /// only if it had CLOSED by bar i's open — always causal, whatever the two markets' sessions.
        /// </summary>
        private double[] External(string symbol, string field, Node at)
        {
            string key = $"{symbol}|{field}";
            if (externalMemo.TryGetValue(key, out var hit)) return hit;
            var other = ctx.External?.Invoke(symbol.ToUpperInvariant())
                ?? throw new FormulaException($"No data for {symbol}", at.Pos, at.Len);
            var src = other.Source(field) ?? throw new FormulaException($"Unknown field '{field}'", at.Pos, at.Len);
            var r = TA.NaNs(n);
            var span = InferSpan(ctx.Bars.Time);
            int j = -1;
            for (int i = 0; i < n; i++)
            {
                var open = ctx.Bars.Time[i];
                while (j + 1 < other.Count && other.Time[j + 1] + span <= open) j++;
                if (j >= 0) r[i] = src[j];
            }
            externalMemo[key] = r;
            return r;
        }

        private static TimeSpan InferSpan(DateTime[] time)
        {
            if (time.Length < 2) return TimeSpan.FromDays(1);
            var gaps = new List<double>();
            for (int i = 1; i < Math.Min(time.Length, 200); i++) gaps.Add((time[i] - time[i - 1]).TotalSeconds);
            gaps.Sort();
            return TimeSpan.FromSeconds(gaps[gaps.Count / 2]);
        }

        private static string Canonical(Node node)
        {
            var sb = new StringBuilder();
            Write(node, sb);
            return sb.ToString();
        }

        private static void Write(Node node, StringBuilder sb)
        {
            switch (node)
            {
                case NumberNode num: sb.Append(num.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case StringNode s: sb.Append('"').Append(s.Value).Append('"'); break;
                case IdentNode id: sb.Append(id.Name.ToLowerInvariant()); break;
                case UnaryNode u: sb.Append(u.Op).Append('('); Write(u.Operand, sb); sb.Append(')'); break;
                case BinaryNode b: sb.Append('('); Write(b.Left, sb); sb.Append(b.Op); Write(b.Right, sb); sb.Append(')'); break;
                case TernaryNode t: sb.Append('('); Write(t.Condition, sb); sb.Append('?'); Write(t.WhenTrue, sb); sb.Append(':'); Write(t.WhenFalse, sb); sb.Append(')'); break;
                case IndexNode ix: Write(ix.Target, sb); sb.Append('['); Write(ix.Offset, sb); sb.Append(']'); break;
                case MemberNode m: Write(m.Target, sb); sb.Append('.').Append(m.Member.ToLowerInvariant()); break;
                case CallNode c:
                    sb.Append(c.Name).Append('(');
                    for (int i = 0; i < c.Args.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (c.Args[i].Name != null) sb.Append(c.Args[i].Name!.ToLowerInvariant()).Append('=');
                        Write(c.Args[i].Value, sb);
                    }
                    sb.Append(')');
                    break;
            }
        }
    }
}
