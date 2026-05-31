using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Section 11.2: the Deflated Sharpe Ratio (Bailey &amp; López de Prado, 2014). It penalises an
    /// observed Sharpe for (a) the number of independent trials that were run, (b) the non-normality
    /// (skew/kurtosis) of the return stream, and (c) the sample length. A raw Sharpe can look strong
    /// purely because many parameter variants were tried; the DSR is the probability that the true
    /// Sharpe is positive after accounting for that selection.
    /// </summary>
    public static class DeflatedSharpe
    {
        private const double Euler = 0.5772156649015329;

        public readonly record struct Result(
            double ObservedSharpePerPeriod, double ExpectedMaxSharpe, double Dsr, int Trials, int Observations);

        /// <summary>Per-period returns from an equity curve.</summary>
        public static List<double> PeriodReturns(IReadOnlyList<EquityPoint> equityCurve)
        {
            var r = new List<double>(Math.Max(0, equityCurve.Count - 1));
            for (int i = 1; i < equityCurve.Count; i++)
            {
                decimal prev = equityCurve[i - 1].Equity;
                if (prev == 0m) continue;
                r.Add((double)((equityCurve[i].Equity - prev) / prev));
            }
            return r;
        }

        /// <summary>
        /// Compute the DSR. <paramref name="trials"/> is the number of parameter combinations tested,
        /// <paramref name="trialSharpeStd"/> the standard deviation of the (per-period) Sharpe ratios
        /// across those trials. If only one trial is available, the variance term collapses and the DSR
        /// reduces to the probabilistic Sharpe ratio against a zero benchmark.
        /// </summary>
        public static Result Compute(IReadOnlyList<double> returns, int trials, double trialSharpeStd)
        {
            int T = returns.Count;
            if (T < 4) return new Result(0, 0, 0, trials, T);

            double mean = returns.Average();
            double var0 = returns.Sum(x => (x - mean) * (x - mean)) / T;
            double sd = Math.Sqrt(var0);
            if (sd == 0) return new Result(0, 0, 0, trials, T);

            double sr = mean / sd;                       // per-period observed Sharpe
            double skew = StandardisedMoment(returns, mean, sd, 3);
            double kurt = StandardisedMoment(returns, mean, sd, 4); // raw (normal ⇒ 3)

            double sr0 = ExpectedMaxSharpe(trialSharpeStd, trials);

            double denom = Math.Sqrt(Math.Max(1e-12, 1 - skew * sr + (kurt - 1) / 4.0 * sr * sr));
            double z = (sr - sr0) * Math.Sqrt(T - 1) / denom;
            return new Result(sr, sr0, NormalCdf(z), trials, T);
        }

        /// <summary>Expected maximum of N i.i.d. trial Sharpes (the benchmark the observed SR must beat).</summary>
        public static double ExpectedMaxSharpe(double trialSharpeStd, int trials)
        {
            if (trialSharpeStd <= 0 || trials <= 1) return 0;
            double a = InverseNormalCdf(1.0 - 1.0 / trials);
            double b = InverseNormalCdf(1.0 - 1.0 / (trials * Math.E));
            return trialSharpeStd * ((1 - Euler) * a + Euler * b);
        }

        private static double StandardisedMoment(IReadOnlyList<double> xs, double mean, double sd, int k)
        {
            double s = 0;
            foreach (var x in xs) s += Math.Pow((x - mean) / sd, k);
            return s / xs.Count;
        }

        // ── Normal CDF (Abramowitz & Stegun 7.1.26) and inverse (Acklam) ─────────────
        public static double NormalCdf(double x)
        {
            double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
            double d = 0.3989422804014327 * Math.Exp(-x * x / 2.0);
            double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
            return x >= 0 ? 1.0 - p : p;
        }

        public static double InverseNormalCdf(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;
            // Acklam's rational approximation.
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            double plow = 0.02425, phigh = 1 - plow;
            if (p < plow)
            {
                double q = Math.Sqrt(-2 * Math.Log(p));
                return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                       ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            if (p > phigh)
            {
                double q = Math.Sqrt(-2 * Math.Log(1 - p));
                return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                        ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            double r = p - 0.5, r2 = r * r;
            return (((((a[0] * r2 + a[1]) * r2 + a[2]) * r2 + a[3]) * r2 + a[4]) * r2 + a[5]) * r /
                   (((((b[0] * r2 + b[1]) * r2 + b[2]) * r2 + b[3]) * r2 + b[4]) * r2 + 1);
        }
    }
}
