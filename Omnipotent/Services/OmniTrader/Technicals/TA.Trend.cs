namespace Omnipotent.Services.OmniTrader.Technicals
{
    public static partial class TA
    {
        public static (double[] Macd, double[] Signal, double[] Hist) Macd(double[] src, int fast = 12, int slow = 26, int signal = 9)
        {
            var macd = Zip(Ema(src, fast), Ema(src, slow), (a, b) => a - b);
            // The slow EMA's warm-up makes the first slow−1 values NaN; the signal line starts after.
            var sig = Ema(macd, signal);
            return (macd, sig, Zip(macd, sig, (a, b) => a - b));
        }

        /// <summary>Wilder ADX with +DI and −DI.</summary>
        public static (double[] Adx, double[] PlusDi, double[] MinusDi) Adx(double[] high, double[] low, double[] close, int n = 14)
        {
            int len = close.Length;
            var tr = TrueRange(high, low, close);
            var pdm = NaNs(len);
            var mdm = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double up = high[i] - high[i - 1];
                double down = low[i - 1] - low[i];
                pdm[i] = up > down && up > 0 ? up : 0;
                mdm[i] = down > up && down > 0 ? down : 0;
            }
            var trs = Rma(SkipFirst(tr), n);
            var ps = Rma(pdm, n);
            var ms = Rma(mdm, n);
            var pdi = Zip(ps, trs, (a, b) => b == 0 ? 0 : 100 * a / b);
            var mdi = Zip(ms, trs, (a, b) => b == 0 ? 0 : 100 * a / b);
            var dx = Zip(pdi, mdi, (a, b) => a + b == 0 ? 0 : 100 * Math.Abs(a - b) / (a + b));
            return (Rma(dx, n), pdi, mdi);
        }

        /// <summary>The first bar has no previous close, so its true range is excluded from Wilder smoothing.</summary>
        private static double[] SkipFirst(double[] a)
        {
            var r = (double[])a.Clone();
            if (r.Length > 0) r[0] = double.NaN;
            return r;
        }

        /// <summary>Aroon up/down (percent of the window since the high/low) and oscillator.</summary>
        public static (double[] Up, double[] Down, double[] Osc) Aroon(double[] high, double[] low, int n = 25)
        {
            int len = high.Length;
            var up = NaNs(len);
            var dn = NaNs(len);
            for (int i = n; i < len; i++)
            {
                int hi = i, lo = i;
                for (int j = i - n; j <= i; j++)
                {
                    if (high[j] >= high[hi]) hi = j;
                    if (low[j] <= low[lo]) lo = j;
                }
                up[i] = 100.0 * (n - (i - hi)) / n;
                dn[i] = 100.0 * (n - (i - lo)) / n;
            }
            return (up, dn, Zip(up, dn, (a, b) => a - b));
        }

        /// <summary>Supertrend: line and direction (+1 up-trend, −1 down-trend).</summary>
        public static (double[] Line, double[] Direction) Supertrend(double[] high, double[] low, double[] close, int n = 10, double mult = 3)
        {
            // Band ratchet and flip rules as charting platforms define them; direction is reported as
            // +1 while price is above the line (up-trend) and −1 below.
            int len = close.Length;
            var atr = Atr(high, low, close, n);
            var line = NaNs(len);
            var dir = NaNs(len);
            double prevUpper = 0, prevLower = 0, prevLine = double.NaN;
            bool started = false;
            for (int i = 0; i < len; i++)
            {
                if (!IsNum(atr[i])) continue;
                double mid = (high[i] + low[i]) / 2;
                double upper = mid + mult * atr[i];
                double lower = mid - mult * atr[i];
                if (started)
                {
                    lower = lower > prevLower || close[i - 1] < prevLower ? lower : prevLower;
                    upper = upper < prevUpper || close[i - 1] > prevUpper ? upper : prevUpper;
                }

                int up;
                if (!started) up = -1;
                else if (prevLine == prevUpper) up = close[i] > upper ? 1 : -1;
                else up = close[i] < lower ? -1 : 1;

                double value = up == 1 ? lower : upper;
                line[i] = value;
                dir[i] = up;
                prevUpper = upper; prevLower = lower; prevLine = value;
                started = true;
            }
            return (line, dir);
        }

        /// <summary>Parabolic SAR (Wilder).</summary>
        public static double[] Psar(double[] high, double[] low, double step = 0.02, double max = 0.2)
        {
            int len = high.Length;
            var r = NaNs(len);
            if (len < 2) return r;
            bool longTrend = high[1] + low[1] >= high[0] + low[0];
            double af = step;
            double ep = longTrend ? high[0] : low[0];
            double sar = longTrend ? low[0] : high[0];
            for (int i = 1; i < len; i++)
            {
                sar += af * (ep - sar);
                if (longTrend)
                {
                    sar = Math.Min(sar, Math.Min(low[i - 1], i > 1 ? low[i - 2] : low[i - 1]));
                    if (low[i] < sar)
                    {
                        longTrend = false; sar = ep; ep = low[i]; af = step;
                    }
                    else if (high[i] > ep) { ep = high[i]; af = Math.Min(max, af + step); }
                }
                else
                {
                    sar = Math.Max(sar, Math.Max(high[i - 1], i > 1 ? high[i - 2] : high[i - 1]));
                    if (high[i] > sar)
                    {
                        longTrend = true; sar = ep; ep = high[i]; af = step;
                    }
                    else if (low[i] < ep) { ep = low[i]; af = Math.Min(max, af + step); }
                }
                r[i] = sar;
            }
            return r;
        }

        /// <summary>
        /// Ichimoku in causal form. Tenkan/Kijun as usual; Span A/B are the values that apply AT bar i
        /// (computed <paramref name="displacement"/> bars earlier, which is how the cloud is drawn);
        /// Chikou is the close, displayed <paramref name="displacement"/> bars back by the chart.
        /// </summary>
        public static (double[] Tenkan, double[] Kijun, double[] SpanA, double[] SpanB, double[] Chikou) Ichimoku(
            double[] high, double[] low, double[] close, int conversion = 9, int baseLen = 26, int spanBLen = 52, int displacement = 26)
        {
            var tenkan = Zip(Highest(high, conversion), Lowest(low, conversion), (a, b) => (a + b) / 2);
            var kijun = Zip(Highest(high, baseLen), Lowest(low, baseLen), (a, b) => (a + b) / 2);
            var spanARaw = Zip(tenkan, kijun, (a, b) => (a + b) / 2);
            var spanBRaw = Zip(Highest(high, spanBLen), Lowest(low, spanBLen), (a, b) => (a + b) / 2);
            int shift = Math.Max(0, displacement - 1);
            return (tenkan, kijun, Shift(spanARaw, shift), Shift(spanBRaw, shift), (double[])close.Clone());
        }

        /// <summary>Vortex indicator (+VI, −VI).</summary>
        public static (double[] Plus, double[] Minus) Vortex(double[] high, double[] low, double[] close, int n = 14)
        {
            int len = close.Length;
            var vmp = NaNs(len);
            var vmm = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                vmp[i] = Math.Abs(high[i] - low[i - 1]);
                vmm[i] = Math.Abs(low[i] - high[i - 1]);
            }
            var trs = Sum(SkipFirst(TrueRange(high, low, close)), n);
            return (Zip(Sum(vmp, n), trs, (a, b) => b == 0 ? 0 : a / b), Zip(Sum(vmm, n), trs, (a, b) => b == 0 ? 0 : a / b));
        }

        /// <summary>Choppiness index (0–100; high = range-bound, low = trending).</summary>
        public static double[] Choppiness(double[] high, double[] low, double[] close, int n = 14)
        {
            var atrSum = Sum(SkipFirst(TrueRange(high, low, close)), n);
            var hh = Highest(high, n);
            var ll = Lowest(low, n);
            var r = NaNs(close.Length);
            for (int i = 0; i < close.Length; i++)
            {
                if (!IsNum(atrSum[i]) || !IsNum(hh[i]) || !IsNum(ll[i])) continue;
                double range = hh[i] - ll[i];
                r[i] = range <= 0 ? 100 : 100 * Math.Log10(atrSum[i] / range) / Math.Log10(n);
            }
            return r;
        }

        /// <summary>Least-squares regression over a window: value at the window end, slope per bar,
        /// intercept and R².</summary>
        public static (double[] Value, double[] Slope, double[] R2) LinReg(double[] src, int n)
        {
            int len = src.Length;
            var value = NaNs(len);
            var slope = NaNs(len);
            var r2 = NaNs(len);
            if (n < 2) return (value, slope, r2);
            double sx = n * (n - 1) / 2.0;
            double sxx = (n - 1) * n * (2 * n - 1) / 6.0;
            double denom = n * sxx - sx * sx;
            for (int i = n - 1; i < len; i++)
            {
                double sy = 0, sxy = 0, syy = 0;
                bool ok = true;
                for (int j = 0; j < n; j++)
                {
                    double y = src[i - n + 1 + j];
                    if (!IsNum(y)) { ok = false; break; }
                    sy += y; sxy += j * y; syy += y * y;
                }
                if (!ok) continue;
                double b = (n * sxy - sx * sy) / denom;
                double a = (sy - b * sx) / n;
                value[i] = a + b * (n - 1);
                slope[i] = b;
                double ssTot = syy - sy * sy / n;
                double ssRes = 0;
                for (int j = 0; j < n; j++)
                {
                    double y = src[i - n + 1 + j];
                    double e = y - (a + b * j);
                    ssRes += e * e;
                }
                r2[i] = ssTot <= 0 ? 1 : Math.Max(0, 1 - ssRes / ssTot);
            }
            return (value, slope, r2);
        }

        /// <summary>Elder-style trend score in [−1, 1] from price vs fast/slow EMAs and the slow EMA's slope.</summary>
        public static double[] TrendScore(double[] close, int fast = 20, int slow = 50)
        {
            var f = Ema(close, fast);
            var s = Ema(close, slow);
            var r = NaNs(close.Length);
            for (int i = 1; i < close.Length; i++)
            {
                if (!IsNum(f[i]) || !IsNum(s[i]) || !IsNum(s[i - 1])) continue;
                double score = (close[i] > f[i] ? 1 : -1) + (f[i] > s[i] ? 1 : -1) + (s[i] > s[i - 1] ? 1 : -1);
                r[i] = score / 3;
            }
            return r;
        }
    }
}
