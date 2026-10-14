namespace Omnipotent.Services.OmniTrader.Technicals
{
    /// <summary>
    /// Technical-analysis library. Every function is vectorised over a full series and strictly
    /// causal: output[i] depends only on inputs at indices ≤ i. Warm-up bars are NaN. NaN inputs
    /// propagate (window functions) or hold state (recursive smoothers) rather than poisoning the
    /// rest of the series. Definitions follow the common charting conventions (Wilder smoothing for
    /// RSI/ATR/ADX, SMA-seeded RMA, first-value-seeded EMA).
    /// </summary>
    public static partial class TA
    {
        public static double[] NaNs(int n)
        {
            var r = new double[n];
            Array.Fill(r, double.NaN);
            return r;
        }

        public static bool IsNum(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static int FirstValid(double[] src)
        {
            for (int i = 0; i < src.Length; i++) if (IsNum(src[i])) return i;
            return src.Length;
        }

        // ── moving averages ───────────────────────────────────────────────────────

        public static double[] Sma(double[] src, int n)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            double sum = 0;
            int valid = 0;
            for (int i = 0; i < src.Length; i++)
            {
                if (IsNum(src[i])) { sum += src[i]; valid++; }
                if (i >= n)
                {
                    if (IsNum(src[i - n])) { sum -= src[i - n]; valid--; }
                }
                if (i >= n - 1 && valid == n) r[i] = sum / n;
            }
            return r;
        }

        /// <summary>Exponential MA, α = 2/(n+1), seeded with the first valid value; the first n−1 values are warm-up.</summary>
        public static double[] Ema(double[] src, int n) => Exponential(src, 2.0 / (n + 1), n, seedWithSma: false);

        /// <summary>Wilder's moving average (RMA/SMMA), α = 1/n, seeded with the SMA of the first n values.</summary>
        public static double[] Rma(double[] src, int n) => Exponential(src, 1.0 / n, n, seedWithSma: true);

        private static double[] Exponential(double[] src, double alpha, int n, bool seedWithSma)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            int start = FirstValid(src);
            if (start >= src.Length) return r;
            double state;
            int i;
            if (seedWithSma)
            {
                if (start + n > src.Length) return r;
                double sum = 0;
                for (i = start; i < start + n; i++)
                {
                    if (!IsNum(src[i])) return r;
                    sum += src[i];
                }
                state = sum / n;
                r[start + n - 1] = state;
                i = start + n;
            }
            else
            {
                state = src[start];
                if (n == 1) r[start] = state;
                i = start + 1;
            }
            for (; i < src.Length; i++)
            {
                if (IsNum(src[i])) state = alpha * src[i] + (1 - alpha) * state;
                if (i >= start + n - 1) r[i] = IsNum(src[i]) ? state : double.NaN;
            }
            return r;
        }

        public static double[] Wma(double[] src, int n)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            double denom = n * (n + 1) / 2.0;
            for (int i = n - 1; i < src.Length; i++)
            {
                double s = 0;
                bool ok = true;
                for (int j = 0; j < n; j++)
                {
                    double v = src[i - j];
                    if (!IsNum(v)) { ok = false; break; }
                    s += v * (n - j);
                }
                if (ok) r[i] = s / denom;
            }
            return r;
        }

        public static double[] Dema(double[] src, int n)
        {
            var e1 = Ema(src, n);
            var e2 = Ema(e1, n);
            return Zip(e1, e2, (a, b) => 2 * a - b);
        }

        public static double[] Tema(double[] src, int n)
        {
            var e1 = Ema(src, n);
            var e2 = Ema(e1, n);
            var e3 = Ema(e2, n);
            var r = NaNs(src.Length);
            for (int i = 0; i < r.Length; i++)
                if (IsNum(e1[i]) && IsNum(e2[i]) && IsNum(e3[i])) r[i] = 3 * e1[i] - 3 * e2[i] + e3[i];
            return r;
        }

        /// <summary>Hull MA: WMA(2·WMA(n/2) − WMA(n), √n).</summary>
        public static double[] Hma(double[] src, int n)
        {
            int half = Math.Max(1, n / 2);
            int root = Math.Max(1, (int)Math.Round(Math.Sqrt(n)));
            var raw = Zip(Wma(src, half), Wma(src, n), (a, b) => 2 * a - b);
            return Wma(raw, root);
        }

        /// <summary>Zero-lag EMA: EMA of (src + (src − src[lag])), lag = (n−1)/2.</summary>
        public static double[] Zlema(double[] src, int n)
        {
            int lag = (n - 1) / 2;
            var adj = NaNs(src.Length);
            for (int i = lag; i < src.Length; i++)
                if (IsNum(src[i]) && IsNum(src[i - lag])) adj[i] = 2 * src[i] - src[i - lag];
            return Ema(adj, n);
        }

        /// <summary>Kaufman adaptive MA.</summary>
        public static double[] Kama(double[] src, int n, int fast = 2, int slow = 30)
        {
            var r = NaNs(src.Length);
            if (src.Length <= n) return r;
            double fastSc = 2.0 / (fast + 1), slowSc = 2.0 / (slow + 1);
            double prev = double.NaN;
            for (int i = n; i < src.Length; i++)
            {
                double change = Math.Abs(src[i] - src[i - n]);
                double vol = 0;
                for (int j = i - n + 1; j <= i; j++) vol += Math.Abs(src[j] - src[j - 1]);
                if (!IsNum(change) || !IsNum(vol)) continue;
                double er = vol == 0 ? 0 : change / vol;
                double sc = Math.Pow(er * (fastSc - slowSc) + slowSc, 2);
                prev = IsNum(prev) ? prev + sc * (src[i] - prev) : src[i];
                r[i] = prev;
            }
            return r;
        }

        /// <summary>Arnaud Legoux MA.</summary>
        public static double[] Alma(double[] src, int n, double offset = 0.85, double sigma = 6)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            double m = offset * (n - 1), s = n / sigma;
            var w = new double[n];
            double norm = 0;
            for (int j = 0; j < n; j++) { w[j] = Math.Exp(-((j - m) * (j - m)) / (2 * s * s)); norm += w[j]; }
            for (int i = n - 1; i < src.Length; i++)
            {
                double sum = 0;
                bool ok = true;
                for (int j = 0; j < n; j++)
                {
                    double v = src[i - (n - 1) + j];
                    if (!IsNum(v)) { ok = false; break; }
                    sum += w[j] * v;
                }
                if (ok) r[i] = sum / norm;
            }
            return r;
        }

        /// <summary>Volume-weighted MA.</summary>
        public static double[] Vwma(double[] src, double[] volume, int n)
        {
            var pv = Zip(src, volume, (a, b) => a * b);
            var num = Sum(pv, n);
            var den = Sum(volume, n);
            return Zip(num, den, (a, b) => b == 0 ? double.NaN : a / b);
        }

        /// <summary>Tillson T3.</summary>
        public static double[] T3(double[] src, int n, double v = 0.7)
        {
            var e1 = Ema(src, n); var e2 = Ema(e1, n); var e3 = Ema(e2, n);
            var e4 = Ema(e3, n); var e5 = Ema(e4, n); var e6 = Ema(e5, n);
            double c1 = -v * v * v, c2 = 3 * v * v + 3 * v * v * v, c3 = -6 * v * v - 3 * v - 3 * v * v * v, c4 = 1 + 3 * v + v * v * v + 3 * v * v;
            var r = NaNs(src.Length);
            for (int i = 0; i < r.Length; i++)
                if (IsNum(e6[i])) r[i] = c1 * e6[i] + c2 * e5[i] + c3 * e4[i] + c4 * e3[i];
            return r;
        }

        /// <summary>McGinley dynamic.</summary>
        public static double[] McGinley(double[] src, int n)
        {
            var r = NaNs(src.Length);
            int start = FirstValid(src);
            if (start >= src.Length) return r;
            double md = src[start];
            for (int i = start; i < src.Length; i++)
            {
                if (!IsNum(src[i])) continue;
                if (i > start && md != 0) md = md + (src[i] - md) / (n * Math.Pow(src[i] / md, 4));
                if (i >= start + n - 1) r[i] = md;
            }
            return r;
        }

        // ── window primitives ─────────────────────────────────────────────────────

        public static double[] Sum(double[] src, int n)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            double sum = 0;
            int valid = 0;
            for (int i = 0; i < src.Length; i++)
            {
                if (IsNum(src[i])) { sum += src[i]; valid++; }
                if (i >= n && IsNum(src[i - n])) { sum -= src[i - n]; valid--; }
                if (i >= n - 1 && valid == n) r[i] = sum;
            }
            return r;
        }

        public static double[] Highest(double[] src, int n) => Extreme(src, n, max: true);
        public static double[] Lowest(double[] src, int n) => Extreme(src, n, max: false);

        /// <summary>Rolling max/min with a monotonic deque — O(n).</summary>
        private static double[] Extreme(double[] src, int n, bool max)
        {
            var r = NaNs(src.Length);
            if (n < 1) return r;
            var dq = new LinkedList<int>();
            int lastNaN = -1;
            for (int i = 0; i < src.Length; i++)
            {
                if (!IsNum(src[i])) { lastNaN = i; dq.Clear(); continue; }
                while (dq.Count > 0 && (max ? src[dq.Last!.Value] <= src[i] : src[dq.Last!.Value] >= src[i])) dq.RemoveLast();
                dq.AddLast(i);
                while (dq.First!.Value <= i - n) dq.RemoveFirst();
                if (i - lastNaN >= n) r[i] = src[dq.First.Value];
            }
            return r;
        }

        /// <summary>Bars since the rolling max (0 = this bar is the high).</summary>
        public static double[] HighestBars(double[] src, int n) => ExtremeBars(src, n, max: true);
        public static double[] LowestBars(double[] src, int n) => ExtremeBars(src, n, max: false);

        private static double[] ExtremeBars(double[] src, int n, bool max)
        {
            var r = NaNs(src.Length);
            for (int i = n - 1; i < src.Length; i++)
            {
                int best = i;
                bool ok = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(src[j])) { ok = false; break; }
                    if (max ? src[j] >= src[best] : src[j] <= src[best]) best = j;
                }
                if (ok) r[i] = i - best;
            }
            return r;
        }

        public static double[] StdDev(double[] src, int n, bool sample = false)
        {
            var r = NaNs(src.Length);
            if (n < 2) return r;
            for (int i = n - 1; i < src.Length; i++)
            {
                double mean = 0;
                bool ok = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(src[j])) { ok = false; break; }
                    mean += src[j];
                }
                if (!ok) continue;
                mean /= n;
                double ss = 0;
                for (int j = i - n + 1; j <= i; j++) ss += (src[j] - mean) * (src[j] - mean);
                r[i] = Math.Sqrt(ss / (sample ? n - 1 : n));
            }
            return r;
        }

        public static double[] Variance(double[] src, int n)
            => Map(StdDev(src, n), s => s * s);

        /// <summary>src − src[n].</summary>
        public static double[] Change(double[] src, int n = 1)
        {
            var r = NaNs(src.Length);
            for (int i = n; i < src.Length; i++)
                if (IsNum(src[i]) && IsNum(src[i - n])) r[i] = src[i] - src[i - n];
            return r;
        }

        /// <summary>Rate of change, %: 100·(src/src[n] − 1).</summary>
        public static double[] Roc(double[] src, int n)
        {
            var r = NaNs(src.Length);
            for (int i = n; i < src.Length; i++)
                if (IsNum(src[i]) && IsNum(src[i - n]) && src[i - n] != 0) r[i] = 100 * (src[i] / src[i - n] - 1);
            return r;
        }

        /// <summary>Value <paramref name="n"/> bars ago.</summary>
        public static double[] Shift(double[] src, int n)
        {
            var r = NaNs(src.Length);
            if (n < 0) throw new ArgumentException("A negative offset would read the future.");
            for (int i = n; i < src.Length; i++) r[i] = src[i - n];
            return r;
        }

        // ── element-wise helpers ──────────────────────────────────────────────────

        public static double[] Zip(double[] a, double[] b, Func<double, double, double> f)
        {
            int n = Math.Min(a.Length, b.Length);
            var r = NaNs(Math.Max(a.Length, b.Length));
            for (int i = 0; i < n; i++)
                if (IsNum(a[i]) && IsNum(b[i])) r[i] = f(a[i], b[i]);
            return r;
        }

        public static double[] Map(double[] a, Func<double, double> f)
        {
            var r = NaNs(a.Length);
            for (int i = 0; i < a.Length; i++) if (IsNum(a[i])) r[i] = f(a[i]);
            return r;
        }

        public static double[] Const(int n, double v)
        {
            var r = new double[n];
            Array.Fill(r, v);
            return r;
        }

        /// <summary>Replace NaN with a value.</summary>
        public static double[] Nz(double[] a, double v = 0)
        {
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = IsNum(a[i]) ? a[i] : v;
            return r;
        }
    }
}
