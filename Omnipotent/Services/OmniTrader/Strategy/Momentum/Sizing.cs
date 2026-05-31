namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>
    /// Section 7: position sizing. Inverse-vol weight within each side, scale the whole book to the
    /// target portfolio vol (diagonal estimator), cap per name, then clamp gross leverage. Returns
    /// signed target weights (long &gt; 0, short &lt; 0) that sum in absolute value to ≤ max gross.
    /// </summary>
    public static class Sizing
    {
        public static Dictionary<string, decimal> TargetWeights(
            IReadOnlyList<string> longs,
            IReadOnlyList<string> shorts,
            IReadOnlyDictionary<string, decimal> realizedVols,
            MomentumConfig cfg)
        {
            var raw = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            AddInverseVolSide(longs, +1m, realizedVols, raw);
            AddInverseVolSide(shorts, -1m, realizedVols, raw);
            if (raw.Count == 0) return raw;

            // Scale gross exposure to hit the target portfolio vol (diagonal / inverse-vol estimator).
            decimal portVol = DiagonalPortfolioVol(raw, realizedVols);
            decimal scale = portVol <= 0m
                ? 0m
                : Math.Clamp((decimal)cfg.TargetPortfolioVol / portVol, 0m, (decimal)cfg.MaxGrossLeverage);

            var w = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw) w[kv.Key] = kv.Value * scale;

            ApplyCap(w, (decimal)cfg.MaxWeightPerAsset);
            ClampGross(w, (decimal)cfg.MaxGrossLeverage);
            return w;
        }

        private static void AddInverseVolSide(
            IReadOnlyList<string> side, decimal sign,
            IReadOnlyDictionary<string, decimal> vols, Dictionary<string, decimal> into)
        {
            var inv = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            decimal sum = 0m;
            foreach (var a in side)
            {
                if (!vols.TryGetValue(a, out var v) || v <= 0m) continue;
                decimal iv = 1m / v;
                inv[a] = iv;
                sum += iv;
            }
            if (sum <= 0m) return;
            foreach (var kv in inv) into[kv.Key] = sign * kv.Value / sum;
        }

        /// <summary>√(Σ (w_a · vol_a)²) — the diagonal (uncorrelated) portfolio vol estimate.</summary>
        public static decimal DiagonalPortfolioVol(
            IReadOnlyDictionary<string, decimal> weights, IReadOnlyDictionary<string, decimal> vols)
        {
            double sumSq = 0;
            foreach (var kv in weights)
            {
                if (!vols.TryGetValue(kv.Key, out var v)) continue;
                double term = (double)(kv.Value * v);
                sumSq += term * term;
            }
            return (decimal)Math.Sqrt(sumSq);
        }

        /// <summary>Cap each |weight| at the per-asset limit (sign preserved). Mutates in place.</summary>
        public static void ApplyCap(Dictionary<string, decimal> weights, decimal maxPerAsset)
        {
            foreach (var key in weights.Keys.ToList())
            {
                decimal w = weights[key];
                if (Math.Abs(w) > maxPerAsset) weights[key] = Math.Sign(w) * maxPerAsset;
            }
        }

        /// <summary>Scale the whole book down so Σ|w| ≤ maxGross. Mutates in place.</summary>
        public static void ClampGross(Dictionary<string, decimal> weights, decimal maxGross)
        {
            decimal gross = weights.Values.Sum(Math.Abs);
            if (gross <= maxGross || gross <= 0m) return;
            decimal scale = maxGross / gross;
            foreach (var key in weights.Keys.ToList()) weights[key] *= scale;
        }
    }
}
