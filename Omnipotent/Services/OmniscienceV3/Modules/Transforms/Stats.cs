namespace Omnipotent.Services.OmniscienceV3.Modules.Transforms
{
    /// <summary>The few tests the comparison tactics need (X28, X29), with the corrections that keep them honest on small,
    /// tied samples. Every result is reported with its sample sizes; "significant" is always after correcting for the
    /// number of comparisons made.</summary>
    public static class Stats
    {
        /// <summary>Mann–Whitney U, two-sided, normal approximation with tie correction and continuity correction.
        /// <c>Effect</c> is the rank-biserial correlation in [-1, 1]: positive when <paramref name="a"/> tends to be larger.</summary>
        public static (double U, double Z, double P, double Effect) MannWhitney(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            int n1 = a.Count, n2 = b.Count;
            if (n1 == 0 || n2 == 0) return (0, 0, 1, 0);
            var all = a.Select(v => (Value: v, First: true)).Concat(b.Select(v => (Value: v, First: false))).OrderBy(x => x.Value).ToArray();
            var ranks = new double[all.Length];
            double tieTerm = 0;
            for (int i = 0; i < all.Length;)
            {
                int j = i;
                while (j + 1 < all.Length && all[j + 1].Value == all[i].Value) j++;
                double rank = (i + j) / 2.0 + 1;
                for (int k = i; k <= j; k++) ranks[k] = rank;
                int t = j - i + 1;
                tieTerm += (double)t * t * t - t;
                i = j + 1;
            }
            double r1 = 0;
            for (int i = 0; i < all.Length; i++)
                if (all[i].First) r1 += ranks[i];
            double u1 = r1 - n1 * (n1 + 1) / 2.0;
            double mean = n1 * n2 / 2.0;
            double n = n1 + n2;
            double variance = n1 * (double)n2 / 12.0 * (n + 1 - tieTerm / (n * (n - 1)));
            double effect = 2 * u1 / (n1 * (double)n2) - 1;
            if (variance <= 0) return (u1, 0, 1, effect);
            double diff = u1 - mean;
            double z = (Math.Abs(diff) - 0.5) / Math.Sqrt(variance);
            if (z < 0) z = 0;
            return (u1, Math.Sign(diff) * z, 2 * (1 - NormalCdf(z)), effect);
        }

        /// <summary>Pearson chi-square of two count vectors over the same categories (categories empty in both are ignored).
        /// <c>V</c> is Cramér's V. P is 1 when there is nothing to compare.</summary>
        public static (double ChiSquare, int DegreesOfFreedom, double P, double V) ChiSquare(IReadOnlyList<long> a, IReadOnlyList<long> b)
        {
            if (a.Count != b.Count) throw new ArgumentException("Both rows need the same categories.");
            var cols = Enumerable.Range(0, a.Count).Where(i => a[i] + b[i] > 0).ToList();
            double ta = cols.Sum(i => (double)a[i]), tb = cols.Sum(i => (double)b[i]), total = ta + tb;
            if (cols.Count < 2 || ta == 0 || tb == 0) return (0, 0, 1, 0);
            double chi = 0;
            foreach (int i in cols)
            {
                double col = a[i] + b[i], ea = ta * col / total, eb = tb * col / total;
                chi += (a[i] - ea) * (a[i] - ea) / ea + (b[i] - eb) * (b[i] - eb) / eb;
            }
            int df = cols.Count - 1;
            return (chi, df, ChiSquareSurvival(chi, df), Math.Sqrt(chi / total));
        }

        /// <summary>Benjamini–Hochberg: which of the p-values stay significant at false-discovery rate <paramref name="q"/>.</summary>
        public static bool[] BenjaminiHochberg(IReadOnlyList<double> p, double q = 0.05)
        {
            int m = p.Count;
            var keep = new bool[m];
            var order = Enumerable.Range(0, m).OrderBy(i => p[i]).ToArray();
            int last = -1;
            for (int k = 0; k < m; k++)
                if (p[order[k]] <= (k + 1) / (double)m * q) last = k;
            for (int k = 0; k <= last; k++) keep[order[k]] = true;
            return keep;
        }

        public static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return 0;
            var s = values.OrderBy(v => v).ToArray();
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        }

        public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

        /// <summary>Complementary error function (Numerical Recipes' Chebyshev fit; relative error below 1.2e-7).</summary>
        private static double Erfc(double x)
        {
            double z = Math.Abs(x), t = 1 / (1 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                       t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? r : 2 - r;
        }

        /// <summary>P(X ≥ x) for a chi-square distribution: the regularised upper incomplete gamma function Q(df/2, x/2).</summary>
        public static double ChiSquareSurvival(double x, int df)
        {
            if (x <= 0 || df <= 0) return 1;
            double a = df / 2.0, half = x / 2.0;
            return half < a + 1 ? 1 - GammaSeries(a, half) : GammaContinuedFraction(a, half);
        }

        private static double LogGamma(double x)
        {
            double[] c = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
            double y = x, tmp = x + 5.5;
            tmp -= (x + 0.5) * Math.Log(tmp);
            double ser = 1.000000000190015;
            foreach (double cj in c) ser += cj / ++y;
            return -tmp + Math.Log(2.5066282746310005 * ser / x);
        }

        private static double GammaSeries(double a, double x)
        {
            double sum = 1 / a, del = sum, ap = a;
            for (int n = 0; n < 500; n++)
            {
                ap++;
                del *= x / ap;
                sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-12) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        private static double GammaContinuedFraction(double a, double x)
        {
            const double Tiny = 1e-300;
            double b = x + 1 - a, c = 1 / Tiny, d = 1 / b, h = d;
            for (int i = 1; i < 500; i++)
            {
                double an = -i * (i - a);
                b += 2;
                d = an * d + b;
                if (Math.Abs(d) < Tiny) d = Tiny;
                c = b + an / c;
                if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                double del = d * c;
                h *= del;
                if (Math.Abs(del - 1) < 1e-12) break;
            }
            return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
        }
    }
}
