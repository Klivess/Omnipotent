using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Technicals
{
    /// <summary>One requested indicator: catalog id plus input overrides (anything omitted uses its default).</summary>
    public sealed class IndicatorRequest
    {
        public string Id { get; set; } = "";
        /// <summary>Client-side key so a chart can match results to its panes.</summary>
        public string? Key { get; set; }
        public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class IndicatorResult
    {
        public required string Id { get; init; }
        public string? Key { get; init; }
        public required string Name { get; init; }
        public string? Error { get; init; }
        public Dictionary<string, object?> Inputs { get; init; } = new();
        /// <summary>output name → values aligned to the bars (null = warm-up / no value).</summary>
        public Dictionary<string, double?[]> Values { get; init; } = new();
    }

    public static class IndicatorEngine
    {
        /// <summary>Resolve inputs against the spec, apply bounds, and compute.</summary>
        public static Dictionary<string, double[]> Compute(IndicatorSpec spec, BarSeries bars, IReadOnlyDictionary<string, object?>? inputs,
            IReadOnlyDictionary<string, double[]>? seriesOverrides = null)
        {
            var args = BuildArgs(spec, bars, inputs, seriesOverrides);
            return spec.Compute(args);
        }

        public static IndicatorArgs BuildArgs(IndicatorSpec spec, BarSeries bars, IReadOnlyDictionary<string, object?>? inputs,
            IReadOnlyDictionary<string, double[]>? seriesOverrides = null)
        {
            var args = new IndicatorArgs { Bars = bars, Spec = spec };
            foreach (var input in spec.Inputs)
            {
                object? raw = null;
                if (inputs != null)
                    foreach (var (k, v) in inputs)
                        if (string.Equals(k, input.Name, StringComparison.OrdinalIgnoreCase)) { raw = v; break; }
                raw = Unwrap(raw);

                switch (input.Kind)
                {
                    case InputKind.Source:
                        if (seriesOverrides != null && seriesOverrides.TryGetValue(input.Name, out var series))
                            args.Series[input.Name] = series;
                        else
                        {
                            string field = raw as string ?? input.Default as string ?? "close";
                            if (bars.Source(field) == null) throw new ArgumentException($"{spec.Short}: unknown source '{field}'");
                            args.Choices[input.Name] = field;
                        }
                        break;
                    case InputKind.Choice:
                        string choice = raw?.ToString() ?? input.Default as string ?? input.Options?.FirstOrDefault() ?? "";
                        if (input.Options != null && !input.Options.Contains(choice, StringComparer.OrdinalIgnoreCase))
                            throw new ArgumentException($"{spec.Short}: '{choice}' is not one of {string.Join(", ", input.Options)}");
                        args.Choices[input.Name] = choice;
                        break;
                    default:
                        double value = raw switch
                        {
                            null => Convert.ToDouble(input.Default ?? 0, CultureInfo.InvariantCulture),
                            bool b => b ? 1 : 0,
                            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                            IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
                            _ => Convert.ToDouble(input.Default ?? 0, CultureInfo.InvariantCulture)
                        };
                        if (!double.IsFinite(value)) throw new ArgumentException($"{spec.Short}: {input.Label} must be a number");
                        if (input.Min.HasValue) value = Math.Max(input.Min.Value, value);
                        if (input.Max.HasValue) value = Math.Min(input.Max.Value, value);
                        if (input.Kind == InputKind.Int) value = Math.Round(value);
                        args.Numbers[input.Name] = value;
                        break;
                }
            }
            return args;
        }

        /// <summary>Compute a batch for the chart API. One bad request never fails the rest.</summary>
        public static List<IndicatorResult> ComputeAll(BarSeries bars, IEnumerable<IndicatorRequest> requests)
        {
            var results = new List<IndicatorResult>();
            foreach (var req in requests)
            {
                var spec = IndicatorCatalog.Get(req.Id);
                if (spec == null)
                {
                    results.Add(new IndicatorResult { Id = req.Id, Key = req.Key, Name = req.Id, Error = $"Unknown indicator '{req.Id}'" });
                    continue;
                }
                try
                {
                    var args = BuildArgs(spec, bars, req.Inputs);
                    var outputs = spec.Compute(args);
                    var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in args.Numbers) resolved[k] = v;
                    foreach (var (k, v) in args.Choices) resolved[k] = v;
                    results.Add(new IndicatorResult
                    {
                        Id = spec.Id,
                        Key = req.Key,
                        Name = Label(spec, args),
                        Inputs = resolved,
                        Values = outputs.ToDictionary(o => o.Key, o => ToNullable(o.Value), StringComparer.OrdinalIgnoreCase)
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new IndicatorResult { Id = spec.Id, Key = req.Key, Name = spec.Short, Error = ex.Message });
                }
            }
            return results;
        }

        /// <summary>"RSI(14)", "BB(20, 2)", "EMA(50, hl2)".</summary>
        public static string Label(IndicatorSpec spec, IndicatorArgs args)
        {
            var parts = new List<string>();
            foreach (var input in spec.Inputs)
            {
                if (input.Kind == InputKind.Source)
                {
                    string field = args.Choices.TryGetValue(input.Name, out var f) ? f : "close";
                    if (!string.Equals(field, "close", StringComparison.OrdinalIgnoreCase)) parts.Add(field);
                }
                else if (input.Kind == InputKind.Choice) parts.Add(args.C(input.Name));
                else parts.Add(args.D(input.Name).ToString("0.###", CultureInfo.InvariantCulture));
            }
            return parts.Count == 0 ? spec.Short : $"{spec.Short}({string.Join(", ", parts)})";
        }

        public static double?[] ToNullable(double[] values)
        {
            var r = new double?[values.Length];
            for (int i = 0; i < values.Length; i++) r[i] = TA.IsNum(values[i]) ? Math.Round(values[i], 8) : null;
            return r;
        }

        private static object? Unwrap(object? raw) => raw switch
        {
            JValue jv => jv.Value,
            JToken jt => jt.ToString(),
            _ => raw
        };

        /// <summary>Catalog as the API and the UI pickers read it.</summary>
        public static object Describe(IndicatorSpec s) => new
        {
            s.Id, s.Name, s.Short, s.Category, s.Description, s.Overlay,
            Inputs = s.Inputs.Select(i => new { i.Name, i.Label, Kind = i.Kind.ToString(), i.Default, i.Min, i.Max, i.Step, i.Options }),
            Outputs = s.Outputs.Select(o => new { o.Name, o.Label, o.Style, o.Color, o.DisplayOffset }),
            s.Levels, s.RangeMin, s.RangeMax
        };
    }
}
