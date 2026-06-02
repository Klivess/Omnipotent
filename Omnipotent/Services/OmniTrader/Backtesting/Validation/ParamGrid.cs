using Omnipotent.Services.OmniTrader.Strategy.Params;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Builds a bounded parameter sweep grid from a strategy's [Param] schema, for generic walk-forward
    /// validation. Sweeps numeric params (int/double/decimal) that declare a Min/Max range, taking a few
    /// values across each range, and caps the cartesian product at <c>maxCombos</c> (widest-range params
    /// first) so the grid never explodes for strategies with many params.
    /// </summary>
    public static class ParamGrid
    {
        public static List<Dictionary<string, object?>> Build(
            IReadOnlyList<ParamDescriptor> schema,
            IReadOnlyDictionary<string, object?> baseValues,
            int maxCombos)
        {
            var sweepable = schema
                .Where(d => (d.Type == "int" || d.Type == "double" || d.Type == "decimal")
                            && d.Min.HasValue && d.Max.HasValue && d.Max.Value > d.Min.Value)
                .Select(d => (Descriptor: d, Values: Candidates(d)))
                .Where(x => x.Values.Count >= 2)
                .OrderByDescending(x => x.Descriptor.Max!.Value - x.Descriptor.Min!.Value)
                .ToList();

            // Greedily include params while the product stays within the cap.
            var selected = new List<(string Name, List<object> Values)>();
            int product = 1;
            foreach (var (d, values) in sweepable)
            {
                if (product * values.Count > Math.Max(1, maxCombos)) continue;
                selected.Add((d.Name, values));
                product *= values.Count;
            }

            // Cartesian product over the selected params, layered on top of the base values.
            var grid = new List<Dictionary<string, object?>>
            {
                new(baseValues, StringComparer.OrdinalIgnoreCase)
            };
            foreach (var (name, values) in selected)
            {
                var next = new List<Dictionary<string, object?>>(grid.Count * values.Count);
                foreach (var combo in grid)
                    foreach (var v in values)
                        next.Add(new Dictionary<string, object?>(combo, StringComparer.OrdinalIgnoreCase) { [name] = v });
                grid = next;
            }
            return grid;
        }

        // A few distinct values across the param's range (25/50/75%), snapped to its step and type.
        private static List<object> Candidates(ParamDescriptor d)
        {
            double min = d.Min!.Value, max = d.Max!.Value;
            var vals = new List<object>();
            foreach (var f in new[] { 0.25, 0.5, 0.75 })
            {
                double v = min + f * (max - min);
                if (d.Step is > 0) v = min + Math.Round((v - min) / d.Step.Value) * d.Step.Value;
                object boxed = d.Type switch
                {
                    "int" => (int)Math.Round(v),
                    "decimal" => (decimal)v,
                    _ => v,
                };
                if (!vals.Contains(boxed)) vals.Add(boxed);
            }
            return vals;
        }
    }
}
