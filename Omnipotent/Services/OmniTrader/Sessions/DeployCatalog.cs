using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>One deployable thing on the Deploy page's picker, with the evidence to choose between them.</summary>
    public sealed record DeployCatalogRow
    {
        /// <summary>The candidate id, or the strategy class for rows without one.</summary>
        public required string Key { get; init; }
        /// <summary>Pinned (studio:id@vN) for a candidate.</summary>
        public required string StrategyClass { get; init; }
        public string? StrategyId { get; init; }
        public required string Name { get; init; }
        /// <summary>alpha | studio | builtin</summary>
        public required string Source { get; init; }
        /// <summary>builtin | blueprint | script</summary>
        public required string Kind { get; init; }
        /// <summary>single | basket | universe</summary>
        public required string Shape { get; init; }
        public string? CandidateId { get; init; }
        public string? Status { get; init; }
        public double? Score { get; init; }
        public required string Interval { get; init; }
        public required string Symbol { get; init; }
        public List<string> Symbols { get; init; } = new();
        public int BasketSize { get; init; }
        public required string AssetClass { get; init; }
        public double? Sharpe { get; init; }
        public double? ReturnPct { get; init; }
        public double? MaxDrawdownPct { get; init; }
        public int? Trades { get; init; }
        public double? OosSharpe { get; init; }
        public double? StressedReturnPct { get; init; }
        public int? Profitable { get; init; }
        public int? Tested { get; init; }
        public bool Synthesis { get; init; }
        public string? Paper { get; init; }
        public string? Hypothesis { get; init; }
        public int? Version { get; init; }
        /// <summary>The Studio strategy has moved on since this version was tested.</summary>
        public int? CurrentVersion { get; init; }
        public DateTime? UpdatedUtc { get; init; }
        /// <summary>Promoted or Promising.</summary>
        public bool Recommended { get; init; }
        /// <summary>Sort key: lower first.</summary>
        public int Rank { get; init; }
        public int Running { get; init; }
        public int Deployed { get; init; }
    }

    public static class DeployCatalog
    {
        /// <summary>
        /// Every candidate (best evaluation per strategy version), then Studio strategies no candidate covers,
        /// then the built-ins. Ranked so the strongest evidence leads: Promoted, Promising, untested Studio
        /// strategies, Weak, unevaluated, Rejected/Failed; score within each.
        /// </summary>
        public static List<DeployCatalogRow> Build(IReadOnlyList<AlphaCandidate> candidates, IReadOnlyList<StudioStrategy> studios, StrategyRegistry registry)
        {
            var rows = new List<DeployCatalogRow>();
            var studioById = studios.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var shapeCache = new Dictionary<string, (string Shape, List<string> Basket, string Primary)>(StringComparer.OrdinalIgnoreCase);
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            (string Shape, List<string> Basket, string Primary) ShapeOf(StrategyDescriptor d)
            {
                if (shapeCache.TryGetValue(d.ClassName, out var hit)) return hit;
                (string, List<string>, string) result = ("single", new List<string>(), "");
                try
                {
                    var s = registry.CreateInstance(d.ClassName);
                    Strategy.Params.StrategyParams.Apply(s, DeployPlanner.DefaultParameters(d));
                    var declared = s.DeclareSymbols();
                    var kind = SessionManager.KindOf(declared);
                    result = (SessionManager.ShapeName(kind), kind == SessionKind.Basket ? declared.Fixed!.ToList() : new List<string>(), declared.Primary);
                }
                catch { /* a strategy that will not instantiate is still listed; the plan reports the failure */ }
                shapeCache[d.ClassName] = result;
                return result;
            }

            var best = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.StrategyId))
                .GroupBy(c => (c.StrategyId, c.StrategyVersion))
                .Select(g => g.OrderByDescending(c => c.Status == CandidateStatus.Promoted).ThenByDescending(c => c.Score ?? -1).ThenByDescending(c => c.UpdatedUtc).First());
            foreach (var c in best)
            {
                string unpinned = StudioStrategy.ClassNameFor(c.StrategyId);
                var d = registry.Resolve(unpinned);
                if (d == null) continue; // archived or no longer compiling
                covered.Add(c.StrategyId);
                studioById.TryGetValue(c.StrategyId, out var studio);
                var (shape, basket, primary) = ShapeOf(d);
                var tested = c.TestSymbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.ToUpperInvariant()).Distinct().ToList();
                string symbol = shape == "single" ? tested.FirstOrDefault() ?? studio?.DefaultSymbol ?? primary : primary;
                var e = c.Evaluation;
                rows.Add(new DeployCatalogRow
                {
                    Key = c.Id,
                    StrategyClass = c.StrategyVersion > 0 ? $"{unpinned}@v{c.StrategyVersion}" : unpinned,
                    StrategyId = c.StrategyId,
                    Name = string.IsNullOrWhiteSpace(c.StrategyName) ? d.Name : c.StrategyName,
                    Source = "alpha",
                    Kind = d.Source,
                    Shape = shape,
                    CandidateId = c.Id,
                    Status = c.Status.ToString(),
                    Score = c.Score,
                    Interval = c.Interval.ToString(),
                    Symbol = symbol,
                    Symbols = shape == "basket" ? basket.Take(8).ToList() : tested.Take(8).ToList(),
                    BasketSize = basket.Count,
                    AssetClass = DeployPlanner.AssetClassOf(shape == "basket" ? basket : new List<string> { symbol }),
                    Sharpe = e?.Primary?.Sharpe is double sh ? Math.Round(sh, 3) : null,
                    ReturnPct = e?.Primary?.ReturnPct is double r ? Math.Round(r, 2) : null,
                    MaxDrawdownPct = e?.Primary?.MaxDrawdownPct is double dd ? Math.Round(dd, 2) : null,
                    Trades = e?.Primary?.Trades,
                    OosSharpe = e?.OutOfSample?.Sharpe is double oos ? Math.Round(oos, 3) : null,
                    StressedReturnPct = e?.PrimaryStressed?.ReturnPct is double st ? Math.Round(st, 2) : null,
                    Profitable = e?.Batch?.Profitable,
                    Tested = e?.Batch?.Symbols,
                    Synthesis = c.IsSynthesis(),
                    Paper = c.PaperTitle,
                    Hypothesis = c.Hypothesis.Length > 280 ? c.Hypothesis[..280] + "…" : c.Hypothesis,
                    Version = c.StrategyVersion,
                    CurrentVersion = studio != null && studio.Version != c.StrategyVersion ? studio.Version : null,
                    UpdatedUtc = c.UpdatedUtc,
                    Recommended = c.Status is CandidateStatus.Promoted or CandidateStatus.Promising,
                    Rank = c.Status switch
                    {
                        CandidateStatus.Promoted => 0,
                        CandidateStatus.Promising => 1,
                        CandidateStatus.Weak => 3,
                        CandidateStatus.Proposed or CandidateStatus.Evaluating => 4,
                        _ => 5,
                    },
                });
            }

            foreach (var s in studios)
            {
                if (covered.Contains(s.Id) || s.Status == StudioStatus.Archived) continue;
                var d = registry.Resolve(s.ClassName);
                if (d == null) continue;
                var (shape, basket, primary) = ShapeOf(d);
                string symbol = shape == "single" ? s.DefaultSymbol : primary;
                rows.Add(new DeployCatalogRow
                {
                    Key = s.ClassName,
                    StrategyClass = s.ClassName,
                    StrategyId = s.Id,
                    Name = s.Name,
                    Source = "studio",
                    Kind = d.Source,
                    Shape = shape,
                    Interval = s.DefaultInterval.ToString(),
                    Symbol = symbol,
                    Symbols = shape == "basket" ? basket.Take(8).ToList() : new List<string> { symbol },
                    BasketSize = basket.Count,
                    AssetClass = DeployPlanner.AssetClassOf(shape == "basket" ? basket : new List<string> { symbol }),
                    Sharpe = s.LastMetrics.TryGetValue("Sharpe", out var sh) && double.IsFinite(sh) ? Math.Round(sh, 3) : null,
                    ReturnPct = s.LastMetrics.TryGetValue("ReturnPct", out var rp) && double.IsFinite(rp) ? Math.Round(rp, 2) : null,
                    MaxDrawdownPct = s.LastMetrics.TryGetValue("MaxDrawdownPct", out var md) && double.IsFinite(md) ? Math.Round(md, 2) : null,
                    Trades = s.LastMetrics.TryGetValue("Trades", out var tr) && double.IsFinite(tr) ? (int)tr : null,
                    Hypothesis = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Length > 280 ? s.Description[..280] + "…" : s.Description,
                    Version = s.Version,
                    UpdatedUtc = s.UpdatedUtc,
                    Rank = 2,
                });
            }

            foreach (var d in registry.All.Where(d => d.Source == "builtin"))
            {
                var (shape, basket, primary) = ShapeOf(d);
                string symbol = string.IsNullOrWhiteSpace(primary) ? "BTCUSDT" : primary;
                rows.Add(new DeployCatalogRow
                {
                    Key = d.ClassName,
                    StrategyClass = d.ClassName,
                    Name = d.Name,
                    Source = "builtin",
                    Kind = "builtin",
                    Shape = shape,
                    Interval = "OneHour",
                    Symbol = symbol,
                    Symbols = new List<string> { symbol },
                    BasketSize = basket.Count,
                    AssetClass = DeployPlanner.AssetClassOf(symbol),
                    Hypothesis = d.Description,
                    Rank = 2,
                });
            }

            return rows.OrderBy(r => r.Rank).ThenByDescending(r => r.Score ?? -1).ThenByDescending(r => r.Sharpe ?? -99).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
