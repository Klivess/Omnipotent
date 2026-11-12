using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>One isotonic block: raw probabilities in [Lower, Upper] map to <see cref="Value"/>.</summary>
    public sealed record IsotonicBlock(double Lower, double Upper, double Value, int Count);

    /// <summary>A fitted calibration curve for one scope (a fact category, "global", or "inquiry").</summary>
    public sealed record CalibrationCurve(string Scope, string Method, IReadOnlyList<IsotonicBlock> Blocks, int NGold, double? BaseRate, long FittedAtMs)
    {
        /// <summary>Maps a raw probability through the curve (piecewise-linear between block centres, flat at the ends).</summary>
        public double Apply(double raw)
        {
            if (Blocks.Count == 0) return raw;
            raw = Math.Clamp(raw, 0, 1);
            var centers = Blocks.Select(b => (X: (b.Lower + b.Upper) / 2, Y: b.Value)).ToList();
            if (raw <= centers[0].X) return centers[0].Y;
            if (raw >= centers[^1].X) return centers[^1].Y;
            for (int i = 1; i < centers.Count; i++)
            {
                if (raw > centers[i].X) continue;
                var (x0, y0) = centers[i - 1];
                var (x1, y1) = centers[i];
                return x1 - x0 < 1e-12 ? y1 : y0 + (y1 - y0) * (raw - x0) / (x1 - x0);
            }
            return centers[^1].Y;
        }
    }

    /// <summary>
    /// Calibration from gold labels (plan "calibration from gold labels", N18): isotonic regression by pool-adjacent-violators
    /// over (raw probability, Klives's verdict). A scope with too little gold falls back to the global curve, and with too
    /// little global gold to the identity (and the assertion stays marked uncalibrated) — never a curve fitted on noise.
    /// Probabilities are shrunk away from 0 and 1 by the number of labels behind each block (add-one smoothing), so a
    /// block of three confirmations never claims certainty.
    /// </summary>
    public static class Isotonic
    {
        public const int MinimumGold = 30;

        /// <summary>
        /// Fits the curve in three steps: pool-adjacent-violators on the observed rates; blocks backed by too few labels
        /// are folded into a neighbour (a step fitted on three labels is noise, and a curve of many tiny steps does not
        /// carry over to new labels); then the smoothed values are made monotone again, because smoothing pulls a small
        /// block towards one half harder than a large one and can reverse their order.
        /// </summary>
        public static IReadOnlyList<IsotonicBlock> Fit(IEnumerable<(double Predicted, bool Outcome)> samples)
        {
            var sorted = samples.Where(s => double.IsFinite(s.Predicted)).OrderBy(s => s.Predicted).ToList();
            var blocks = new List<(double Lo, double Hi, double Sum, int N)>();
            foreach (var (p, y) in sorted)
            {
                blocks.Add((p, p, y ? 1 : 0, 1));
                Pool(blocks, b => b.Sum / b.N);
            }

            // At least a twentieth of the labels (and never fewer than five) behind every step.
            int minimum = Math.Max(5, sorted.Count / 20);
            while (blocks.Count > 1)
            {
                int small = -1;
                for (int i = 0; i < blocks.Count; i++)
                    if (blocks[i].N < minimum && (small < 0 || blocks[i].N < blocks[small].N)) small = i;
                if (small < 0) break;
                // Into the neighbour whose rate is closer (the one there is, at either end).
                double rate = blocks[small].Sum / blocks[small].N;
                int into = small == 0 ? 1
                    : small == blocks.Count - 1 ? small - 1
                    : Math.Abs(blocks[small - 1].Sum / blocks[small - 1].N - rate) <= Math.Abs(blocks[small + 1].Sum / blocks[small + 1].N - rate) ? small - 1 : small + 1;
                int first = Math.Min(small, into);
                var a = blocks[first];
                var b = blocks[first + 1];
                blocks[first] = (a.Lo, b.Hi, a.Sum + b.Sum, a.N + b.N);
                blocks.RemoveAt(first + 1);
            }

            var monotone = new List<(double Lo, double Hi, double Sum, int N)>();
            foreach (var b in blocks)
            {
                monotone.Add(b);
                Pool(monotone, Smoothed);
            }
            return monotone.Select(b => new IsotonicBlock(b.Lo, b.Hi, Smoothed(b), b.N)).ToList();
        }

        /// <summary>Add-one smoothing: a block of three confirmations never claims certainty.</summary>
        private static double Smoothed((double Lo, double Hi, double Sum, int N) b) => (b.Sum + 0.5) / (b.N + 1.0);

        /// <summary>Merges the last block into its predecessor while it breaks the order of <paramref name="value"/>.</summary>
        private static void Pool(List<(double Lo, double Hi, double Sum, int N)> blocks, Func<(double Lo, double Hi, double Sum, int N), double> value)
        {
            while (blocks.Count >= 2 && value(blocks[^2]) > value(blocks[^1]))
            {
                var a = blocks[^2];
                var b = blocks[^1];
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add((a.Lo, b.Hi, a.Sum + b.Sum, a.N + b.N));
            }
        }
    }

    /// <summary>Persisted calibration curves (intel.db <c>kn_calibration</c>), cached in memory.</summary>
    public sealed class CalibrationStore
    {
        public const string GlobalScope = "global";
        public const string InquiryScope = "inquiry";

        private readonly SqliteStore intel;
        private readonly IClock clock;
        private readonly ConcurrentDictionary<string, (CalibrationCurve? Curve, long ReadAtMs)> cache = new(StringComparer.Ordinal);

        public CalibrationStore(SqliteStore intel, IClock clock)
        {
            this.intel = intel;
            this.clock = clock;
        }

        public static string CategoryScope(string category) => "category:" + category;

        public CalibrationCurve? Get(string scope)
        {
            long now = clock.NowMs;
            if (cache.TryGetValue(scope, out var hit) && now - hit.ReadAtMs < 60_000) return hit.Curve;
            using var c = intel.OpenRead();
            var curve = c.Query(null, "SELECT scope, method, points_json, n_gold, base_rate, fitted_at_ms FROM kn_calibration WHERE scope=$s",
                r => new CalibrationCurve(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<List<IsotonicBlock>>(r.GetString(2), KernelJson.Options) ?? new(),
                    r.GetInt32(3), r.GetDoubleOrNull(4), r.GetInt64(5)), ("$s", scope)).FirstOrDefault();
            cache[scope] = (curve, now);
            return curve;
        }

        /// <summary>Calibrated probability for a fact of <paramref name="category"/>: the category curve, else the global one,
        /// else the raw value (Calibrated=false).</summary>
        public (double Probability, bool Calibrated) Apply(double raw, string? category)
        {
            if (category != null && Get(CategoryScope(category)) is { NGold: >= Isotonic.MinimumGold } own) return (own.Apply(raw), true);
            if (Get(GlobalScope) is { NGold: >= Isotonic.MinimumGold } global) return (global.Apply(raw), true);
            return (raw, false);
        }

        public (double Probability, bool Calibrated) ApplyScope(double raw, string scope) =>
            Get(scope) is { NGold: >= Isotonic.MinimumGold } curve ? (curve.Apply(raw), true) : (raw, false);

        public async Task<CalibrationCurve> FitAsync(string scope, IReadOnlyList<(double Predicted, bool Outcome)> samples, CancellationToken ct)
        {
            var blocks = Isotonic.Fit(samples);
            double? baseRate = samples.Count == 0 ? null : samples.Average(s => s.Outcome ? 1.0 : 0.0);
            var curve = new CalibrationCurve(scope, "isotonic-pav", blocks, samples.Count, baseRate, clock.NowMs);
            await intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_calibration(scope, method, points_json, n_gold, base_rate, fitted_at_ms) VALUES($s,$m,$p,$n,$b,$t)
                ON CONFLICT(scope) DO UPDATE SET method=excluded.method, points_json=excluded.points_json, n_gold=excluded.n_gold,
                    base_rate=excluded.base_rate, fitted_at_ms=excluded.fitted_at_ms
                """, ("$s", scope), ("$m", curve.Method), ("$p", JsonSerializer.Serialize(blocks, KernelJson.Options)), ("$n", samples.Count),
                ("$b", baseRate), ("$t", curve.FittedAtMs)), ct).ConfigureAwait(false);
            cache[scope] = (curve, clock.NowMs);
            return curve;
        }
    }
}
