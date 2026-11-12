namespace Omnipotent.Services.OmniscienceV3.Knowledge.Quality
{
    /// <summary>A proportion with its Wilson 95% interval (plan N18: "metrics carry confidence intervals").</summary>
    public readonly record struct Proportion(double Value, double Low, double High, int Successes, int Trials)
    {
        public static Proportion Of(int successes, int trials, double z = 1.96)
        {
            if (trials <= 0) return new Proportion(double.NaN, 0, 1, 0, 0);
            double p = (double)successes / trials;
            double z2 = z * z;
            double denom = 1 + z2 / trials;
            double center = (p + z2 / (2 * trials)) / denom;
            double half = z * Math.Sqrt(p * (1 - p) / trials + z2 / (4.0 * trials * trials)) / denom;
            return new Proportion(p, Math.Max(0, center - half), Math.Min(1, center + half), successes, trials);
        }
    }

    /// <summary>One reliability-diagram bin.</summary>
    public sealed record CalibrationBin(double Lower, double Upper, int Count, double MeanPredicted, double ObservedRate);

    /// <summary>Precision/recall, calibration error and Brier score (exact arithmetic over labelled outcomes).</summary>
    public static class Metrics
    {
        public static Proportion Precision(int truePositives, int falsePositives) => Proportion.Of(truePositives, truePositives + falsePositives);

        public static Proportion Recall(int truePositives, int falseNegatives) => Proportion.Of(truePositives, truePositives + falseNegatives);

        public static double F1(double precision, double recall) =>
            precision + recall <= 0 || double.IsNaN(precision) || double.IsNaN(recall) ? 0 : 2 * precision * recall / (precision + recall);

        /// <summary>Expected calibration error over equal-width bins.</summary>
        public static double ExpectedCalibrationError(IReadOnlyList<(double Predicted, bool Outcome)> samples, int bins = 10) =>
            samples.Count == 0 ? double.NaN : Reliability(samples, bins).Sum(b => (double)b.Count / samples.Count * Math.Abs(b.MeanPredicted - b.ObservedRate));

        public static double Brier(IReadOnlyList<(double Predicted, bool Outcome)> samples) =>
            samples.Count == 0 ? double.NaN : samples.Average(s => Math.Pow(s.Predicted - (s.Outcome ? 1 : 0), 2));

        public static IReadOnlyList<CalibrationBin> Reliability(IReadOnlyList<(double Predicted, bool Outcome)> samples, int bins = 10)
        {
            var result = new List<CalibrationBin>();
            for (int b = 0; b < bins; b++)
            {
                double lo = (double)b / bins, hi = (double)(b + 1) / bins;
                var inBin = samples.Where(s => s.Predicted >= lo && (b == bins - 1 ? s.Predicted <= hi : s.Predicted < hi)).ToList();
                if (inBin.Count == 0) continue;
                result.Add(new CalibrationBin(lo, hi, inBin.Count, inBin.Average(s => s.Predicted), inBin.Average(s => s.Outcome ? 1.0 : 0.0)));
            }
            return result;
        }

        /// <summary>Jensen–Shannon divergence (base 2, in [0,1]) between two label distributions: the drift statistic.</summary>
        public static double JensenShannon(IReadOnlyDictionary<string, double> p, IReadOnlyDictionary<string, double> q)
        {
            var keys = p.Keys.Concat(q.Keys).Distinct(StringComparer.Ordinal).ToList();
            double sp = keys.Sum(k => p.GetValueOrDefault(k)), sq = keys.Sum(k => q.GetValueOrDefault(k));
            if (sp <= 0 || sq <= 0) return double.NaN;
            double js = 0;
            foreach (var k in keys)
            {
                double a = p.GetValueOrDefault(k) / sp, b = q.GetValueOrDefault(k) / sq, m = (a + b) / 2;
                if (a > 0) js += 0.5 * a * Math.Log2(a / m);
                if (b > 0) js += 0.5 * b * Math.Log2(b / m);
            }
            return Math.Clamp(js, 0, 1);
        }
    }
}
