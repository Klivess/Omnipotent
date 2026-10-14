namespace Omnipotent.Services.OmniTrader.Technicals
{
    public enum PivotStyle { Classic, Fibonacci, Camarilla, Woodie }

    public static partial class TA
    {
        // ── statistics ────────────────────────────────────────────────────────────

        public static double[] ZScore(double[] src, int n)
        {
            var mean = Sma(src, n);
            var sd = StdDev(src, n);
            var r = NaNs(src.Length);
            for (int i = 0; i < src.Length; i++)
                if (IsNum(mean[i]) && IsNum(sd[i]) && IsNum(src[i])) r[i] = sd[i] == 0 ? 0 : (src[i] - mean[i]) / sd[i];
            return r;
        }

        /// <summary>Percent of the previous n values that are ≤ the current one (0–100).</summary>
        public static double[] PercentRank(double[] src, int n)
        {
            var r = NaNs(src.Length);
            for (int i = n; i < src.Length; i++)
            {
                if (!IsNum(src[i])) continue;
                int count = 0, valid = 0;
                for (int j = i - n; j < i; j++)
                {
                    if (!IsNum(src[j])) continue;
                    valid++;
                    if (src[j] <= src[i]) count++;
                }
                if (valid == n) r[i] = 100.0 * count / n;
            }
            return r;
        }

        public static double[] Correlation(double[] a, double[] b, int n)
        {
            var r = NaNs(Math.Min(a.Length, b.Length));
            for (int i = n - 1; i < r.Length; i++)
            {
                double sa = 0, sb = 0;
                bool ok = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(a[j]) || !IsNum(b[j])) { ok = false; break; }
                    sa += a[j]; sb += b[j];
                }
                if (!ok) continue;
                double ma = sa / n, mb = sb / n, cov = 0, va = 0, vb = 0;
                for (int j = i - n + 1; j <= i; j++)
                {
                    cov += (a[j] - ma) * (b[j] - mb);
                    va += (a[j] - ma) * (a[j] - ma);
                    vb += (b[j] - mb) * (b[j] - mb);
                }
                r[i] = va == 0 || vb == 0 ? 0 : cov / Math.Sqrt(va * vb);
            }
            return r;
        }

        /// <summary>Rolling beta of a's returns on b's returns.</summary>
        public static double[] Beta(double[] a, double[] b, int n)
        {
            var ra = Roc(a, 1);
            var rb = Roc(b, 1);
            var r = NaNs(Math.Min(a.Length, b.Length));
            for (int i = n; i < r.Length; i++)
            {
                double sa = 0, sb = 0;
                bool ok = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(ra[j]) || !IsNum(rb[j])) { ok = false; break; }
                    sa += ra[j]; sb += rb[j];
                }
                if (!ok) continue;
                double ma = sa / n, mb = sb / n, cov = 0, vb = 0;
                for (int j = i - n + 1; j <= i; j++)
                {
                    cov += (ra[j] - ma) * (rb[j] - mb);
                    vb += (rb[j] - mb) * (rb[j] - mb);
                }
                r[i] = vb == 0 ? 0 : cov / vb;
            }
            return r;
        }

        public static double[] Skew(double[] src, int n) => Moment(src, n, 3);
        public static double[] Kurtosis(double[] src, int n) => Moment(src, n, 4);

        private static double[] Moment(double[] src, int n, int order)
        {
            var r = NaNs(src.Length);
            for (int i = n - 1; i < src.Length; i++)
            {
                double mean = 0;
                bool ok = true;
                for (int j = i - n + 1; j <= i; j++) { if (!IsNum(src[j])) { ok = false; break; } mean += src[j]; }
                if (!ok) continue;
                mean /= n;
                double m2 = 0, mk = 0;
                for (int j = i - n + 1; j <= i; j++)
                {
                    double d = src[j] - mean;
                    m2 += d * d;
                    mk += Math.Pow(d, order);
                }
                m2 /= n; mk /= n;
                r[i] = m2 == 0 ? 0 : order == 4 ? mk / (m2 * m2) - 3 : mk / Math.Pow(m2, 1.5);
            }
            return r;
        }

        /// <summary>Rolling lag-k autocorrelation of 1-bar returns.</summary>
        public static double[] AutoCorrelation(double[] src, int n, int lag = 1)
        {
            var ret = Roc(src, 1);
            return Correlation(ret, Shift(ret, lag), n);
        }

        /// <summary>Rolling Hurst exponent by rescaled range on log returns (≈0.5 random, &gt;0.5 trending).</summary>
        public static double[] Hurst(double[] src, int n = 100)
        {
            int len = src.Length;
            var lr = NaNs(len);
            for (int i = 1; i < len; i++) if (src[i] > 0 && src[i - 1] > 0) lr[i] = Math.Log(src[i] / src[i - 1]);
            var r = NaNs(len);
            int[] chunks = { 8, 16, 32, 64 };
            for (int i = n; i < len; i++)
            {
                var xs = new List<double>();
                var ys = new List<double>();
                foreach (int size in chunks)
                {
                    if (size > n / 2) break;
                    double rsSum = 0;
                    int count = 0;
                    for (int start = i - n + 1; start + size - 1 <= i; start += size)
                    {
                        double mean = 0;
                        bool ok = true;
                        for (int k = 0; k < size; k++) { double v = lr[start + k]; if (!IsNum(v)) { ok = false; break; } mean += v; }
                        if (!ok) continue;
                        mean /= size;
                        double cum = 0, max = double.MinValue, min = double.MaxValue, ss = 0;
                        for (int k = 0; k < size; k++)
                        {
                            double d = lr[start + k] - mean;
                            cum += d;
                            max = Math.Max(max, cum);
                            min = Math.Min(min, cum);
                            ss += d * d;
                        }
                        double sd = Math.Sqrt(ss / size);
                        if (sd > 0) { rsSum += (max - min) / sd; count++; }
                    }
                    if (count > 0) { xs.Add(Math.Log(size)); ys.Add(Math.Log(rsSum / count)); }
                }
                if (xs.Count >= 2)
                {
                    double mx = xs.Average(), my = ys.Average(), num = 0, den = 0;
                    for (int k = 0; k < xs.Count; k++) { num += (xs[k] - mx) * (ys[k] - my); den += (xs[k] - mx) * (xs[k] - mx); }
                    if (den > 0) r[i] = num / den;
                }
            }
            return r;
        }

        /// <summary>Rolling annualised Sharpe of 1-bar returns.</summary>
        public static double[] RollingSharpe(double[] close, int n, double barsPerYear)
        {
            var ret = Roc(close, 1);
            var mean = Sma(ret, n);
            var sd = StdDev(ret, n, sample: true);
            return Zip(mean, sd, (m, s) => s == 0 ? 0 : m / s * Math.Sqrt(barsPerYear));
        }

        /// <summary>Percentage drawdown from the running all-time high.</summary>
        public static double[] Drawdown(double[] close)
        {
            var r = NaNs(close.Length);
            double peak = double.NaN;
            for (int i = 0; i < close.Length; i++)
            {
                if (!IsNum(close[i])) continue;
                peak = IsNum(peak) ? Math.Max(peak, close[i]) : close[i];
                r[i] = peak == 0 ? 0 : 100 * (close[i] - peak) / peak;
            }
            return r;
        }

        // ── signal helpers ────────────────────────────────────────────────────────

        public static double[] CrossOver(double[] a, double[] b)
        {
            var r = NaNs(Math.Min(a.Length, b.Length));
            for (int i = 1; i < r.Length; i++)
                if (IsNum(a[i]) && IsNum(b[i]) && IsNum(a[i - 1]) && IsNum(b[i - 1]))
                    r[i] = a[i] > b[i] && a[i - 1] <= b[i - 1] ? 1 : 0;
            return r;
        }

        public static double[] CrossUnder(double[] a, double[] b)
        {
            var r = NaNs(Math.Min(a.Length, b.Length));
            for (int i = 1; i < r.Length; i++)
                if (IsNum(a[i]) && IsNum(b[i]) && IsNum(a[i - 1]) && IsNum(b[i - 1]))
                    r[i] = a[i] < b[i] && a[i - 1] >= b[i - 1] ? 1 : 0;
            return r;
        }

        /// <summary>1 when src has risen on each of the last n bars.</summary>
        public static double[] Rising(double[] src, int n)
        {
            var r = NaNs(src.Length);
            for (int i = n; i < src.Length; i++)
            {
                bool ok = true, rising = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(src[j]) || !IsNum(src[j - 1])) { ok = false; break; }
                    if (src[j] <= src[j - 1]) rising = false;
                }
                if (ok) r[i] = rising ? 1 : 0;
            }
            return r;
        }

        public static double[] Falling(double[] src, int n)
        {
            var r = NaNs(src.Length);
            for (int i = n; i < src.Length; i++)
            {
                bool ok = true, falling = true;
                for (int j = i - n + 1; j <= i; j++)
                {
                    if (!IsNum(src[j]) || !IsNum(src[j - 1])) { ok = false; break; }
                    if (src[j] >= src[j - 1]) falling = false;
                }
                if (ok) r[i] = falling ? 1 : 0;
            }
            return r;
        }

        /// <summary>Bars since the condition was last true (NaN before it first is).</summary>
        public static double[] BarsSince(double[] cond)
        {
            var r = NaNs(cond.Length);
            int last = -1;
            for (int i = 0; i < cond.Length; i++)
            {
                if (IsNum(cond[i]) && cond[i] != 0) last = i;
                if (last >= 0) r[i] = i - last;
            }
            return r;
        }

        /// <summary>src at the n-th most recent bar where the condition was true.</summary>
        public static double[] ValueWhen(double[] cond, double[] src, int occurrence = 0)
        {
            var r = NaNs(cond.Length);
            var hits = new List<int>();
            for (int i = 0; i < cond.Length; i++)
            {
                if (IsNum(cond[i]) && cond[i] != 0) hits.Add(i);
                if (hits.Count > occurrence) r[i] = src[hits[hits.Count - 1 - occurrence]];
            }
            return r;
        }

        /// <summary>Bars in a row the condition has held.</summary>
        public static double[] Streak(double[] cond)
        {
            var r = new double[cond.Length];
            double run = 0;
            for (int i = 0; i < cond.Length; i++)
            {
                run = IsNum(cond[i]) && cond[i] != 0 ? run + 1 : 0;
                r[i] = run;
            }
            return r;
        }

        // ── price structure ───────────────────────────────────────────────────────

        /// <summary>Internal bar strength: (close − low) / (high − low).</summary>
        public static double[] Ibs(double[] high, double[] low, double[] close)
        {
            var r = NaNs(close.Length);
            for (int i = 0; i < close.Length; i++)
            {
                double range = high[i] - low[i];
                r[i] = range == 0 ? 0.5 : (close[i] - low[i]) / range;
            }
            return r;
        }

        /// <summary>Opening gap vs the previous close, %.</summary>
        public static double[] Gap(double[] open, double[] close)
        {
            var r = NaNs(close.Length);
            for (int i = 1; i < close.Length; i++) if (close[i - 1] != 0) r[i] = 100 * (open[i] / close[i - 1] - 1);
            return r;
        }

        public static (double[] Open, double[] High, double[] Low, double[] Close) HeikinAshi(double[] open, double[] high, double[] low, double[] close)
        {
            int len = close.Length;
            var o = new double[len]; var h = new double[len]; var l = new double[len]; var c = new double[len];
            for (int i = 0; i < len; i++)
            {
                c[i] = (open[i] + high[i] + low[i] + close[i]) / 4;
                o[i] = i == 0 ? (open[i] + close[i]) / 2 : (o[i - 1] + c[i - 1]) / 2;
                h[i] = Math.Max(high[i], Math.Max(o[i], c[i]));
                l[i] = Math.Min(low[i], Math.Min(o[i], c[i]));
            }
            return (o, h, l, c);
        }

        /// <summary>
        /// Swing high confirmed <paramref name="right"/> bars after it formed: at bar i the value is
        /// high[i−right] when that bar is the highest of the surrounding left+right window, else NaN.
        /// Reported on the confirming bar, never on the pivot bar itself — that would be look-ahead.
        /// </summary>
        public static double[] PivotHigh(double[] high, int left = 5, int right = 5) => Pivot(high, left, right, max: true);
        public static double[] PivotLow(double[] low, int left = 5, int right = 5) => Pivot(low, left, right, max: false);

        private static double[] Pivot(double[] src, int left, int right, bool max)
        {
            var r = NaNs(src.Length);
            for (int i = left + right; i < src.Length; i++)
            {
                int p = i - right;
                double v = src[p];
                if (!IsNum(v)) continue;
                bool isPivot = true;
                for (int j = p - left; j <= i && isPivot; j++)
                {
                    if (j == p) continue;
                    if (max ? src[j] > v || (j > p && src[j] == v) : src[j] < v || (j > p && src[j] == v)) isPivot = false;
                }
                if (isPivot) r[i] = v;
            }
            return r;
        }

        /// <summary>
        /// Floor-trader pivots from the previous UTC day's high/low/close, applied to every bar of the
        /// current day. (p, r1, r2, r3, s1, s2, s3).
        /// </summary>
        public static (double[] P, double[] R1, double[] R2, double[] R3, double[] S1, double[] S2, double[] S3) Pivots(
            double[] high, double[] low, double[] close, DateTime[] time, PivotStyle style = PivotStyle.Classic)
        {
            int len = close.Length;
            var p = NaNs(len); var r1 = NaNs(len); var r2 = NaNs(len); var r3 = NaNs(len);
            var s1 = NaNs(len); var s2 = NaNs(len); var s3 = NaNs(len);
            DateTime day = DateTime.MinValue;
            double dh = double.NaN, dl = double.NaN, dc = double.NaN;      // current day running
            double ph = double.NaN, pl = double.NaN, pc = double.NaN;      // previous day
            for (int i = 0; i < len; i++)
            {
                var d = time[i].Date;
                if (d != day)
                {
                    if (day != DateTime.MinValue) { ph = dh; pl = dl; pc = dc; }
                    day = d; dh = high[i]; dl = low[i];
                }
                dh = Math.Max(dh, high[i]); dl = Math.Min(dl, low[i]); dc = close[i];
                if (!IsNum(ph)) continue;
                double range = ph - pl;
                double pp = style == PivotStyle.Woodie ? (ph + pl + 2 * pc) / 4 : (ph + pl + pc) / 3;
                p[i] = pp;
                switch (style)
                {
                    case PivotStyle.Fibonacci:
                        r1[i] = pp + 0.382 * range; r2[i] = pp + 0.618 * range; r3[i] = pp + range;
                        s1[i] = pp - 0.382 * range; s2[i] = pp - 0.618 * range; s3[i] = pp - range;
                        break;
                    case PivotStyle.Camarilla:
                        r1[i] = pc + range * 1.1 / 12; r2[i] = pc + range * 1.1 / 6; r3[i] = pc + range * 1.1 / 4;
                        s1[i] = pc - range * 1.1 / 12; s2[i] = pc - range * 1.1 / 6; s3[i] = pc - range * 1.1 / 4;
                        break;
                    default:
                        r1[i] = 2 * pp - pl; s1[i] = 2 * pp - ph;
                        r2[i] = pp + range; s2[i] = pp - range;
                        r3[i] = ph + 2 * (pp - pl); s3[i] = pl - 2 * (ph - pp);
                        break;
                }
            }
            return (p, r1, r2, r3, s1, s2, s3);
        }

        // ── candlestick patterns (1 = present, −1 = bearish variant where it has one) ──

        public static double[] Doji(double[] open, double[] high, double[] low, double[] close, double bodyPct = 0.1)
        {
            var r = new double[close.Length];
            for (int i = 0; i < close.Length; i++)
            {
                double range = high[i] - low[i];
                r[i] = range > 0 && Math.Abs(close[i] - open[i]) <= bodyPct * range ? 1 : 0;
            }
            return r;
        }

        /// <summary>Hammer (+1, after a decline) / shooting star (−1, after an advance).</summary>
        public static double[] Hammer(double[] open, double[] high, double[] low, double[] close)
        {
            var r = new double[close.Length];
            var trend = Sma(close, 10);
            for (int i = 1; i < close.Length; i++)
            {
                double body = Math.Abs(close[i] - open[i]);
                double range = high[i] - low[i];
                if (range <= 0 || body == 0) continue;
                double upper = high[i] - Math.Max(open[i], close[i]);
                double lower = Math.Min(open[i], close[i]) - low[i];
                bool down = IsNum(trend[i - 1]) && close[i - 1] < trend[i - 1];
                bool up = IsNum(trend[i - 1]) && close[i - 1] > trend[i - 1];
                if (lower >= 2 * body && upper <= body * 0.5 && down) r[i] = 1;
                else if (upper >= 2 * body && lower <= body * 0.5 && up) r[i] = -1;
            }
            return r;
        }

        /// <summary>Bullish engulfing (+1) / bearish engulfing (−1).</summary>
        public static double[] Engulfing(double[] open, double[] close)
        {
            var r = new double[close.Length];
            for (int i = 1; i < close.Length; i++)
            {
                bool prevDown = close[i - 1] < open[i - 1], prevUp = close[i - 1] > open[i - 1];
                if (prevDown && close[i] > open[i] && open[i] <= close[i - 1] && close[i] >= open[i - 1]) r[i] = 1;
                else if (prevUp && close[i] < open[i] && open[i] >= close[i - 1] && close[i] <= open[i - 1]) r[i] = -1;
            }
            return r;
        }

        public static double[] InsideBar(double[] high, double[] low)
        {
            var r = new double[high.Length];
            for (int i = 1; i < high.Length; i++) r[i] = high[i] < high[i - 1] && low[i] > low[i - 1] ? 1 : 0;
            return r;
        }

        public static double[] OutsideBar(double[] high, double[] low)
        {
            var r = new double[high.Length];
            for (int i = 1; i < high.Length; i++) r[i] = high[i] > high[i - 1] && low[i] < low[i - 1] ? 1 : 0;
            return r;
        }

        /// <summary>Three consecutive higher-closing up bars (+1) / lower-closing down bars (−1).</summary>
        public static double[] ThreeSoldiersCrows(double[] open, double[] close)
        {
            var r = new double[close.Length];
            for (int i = 2; i < close.Length; i++)
            {
                bool soldiers = true, crows = true;
                for (int k = i - 2; k <= i; k++)
                {
                    if (!(close[k] > open[k] && (k == i - 2 || close[k] > close[k - 1]))) soldiers = false;
                    if (!(close[k] < open[k] && (k == i - 2 || close[k] < close[k - 1]))) crows = false;
                }
                r[i] = soldiers ? 1 : crows ? -1 : 0;
            }
            return r;
        }

        // ── calendar ──────────────────────────────────────────────────────────────

        public static double[] Calendar(DateTime[] time, Func<DateTime, double> f)
        {
            var r = new double[time.Length];
            for (int i = 0; i < time.Length; i++) r[i] = f(time[i]);
            return r;
        }
    }
}
