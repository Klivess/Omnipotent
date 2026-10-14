namespace Omnipotent.Services.OmniTrader.Technicals
{
    public static partial class TA
    {
        /// <summary>Wilder RSI (RMA of gains and losses).</summary>
        public static double[] Rsi(double[] src, int n)
        {
            int len = src.Length;
            var up = NaNs(len);
            var dn = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                if (!IsNum(src[i]) || !IsNum(src[i - 1])) continue;
                double ch = src[i] - src[i - 1];
                up[i] = Math.Max(ch, 0);
                dn[i] = Math.Max(-ch, 0);
            }
            var au = Rma(up, n);
            var ad = Rma(dn, n);
            var r = NaNs(len);
            for (int i = 0; i < len; i++)
            {
                if (!IsNum(au[i]) || !IsNum(ad[i])) continue;
                r[i] = ad[i] == 0 ? (au[i] == 0 ? 50 : 100) : 100 - 100 / (1 + au[i] / ad[i]);
            }
            return r;
        }

        /// <summary>Stochastic oscillator: %K smoothed by <paramref name="smoothK"/>, %D = SMA(%K, d).</summary>
        public static (double[] K, double[] D) Stoch(double[] high, double[] low, double[] close, int kLen = 14, int smoothK = 3, int d = 3)
        {
            var hh = Highest(high, kLen);
            var ll = Lowest(low, kLen);
            var raw = NaNs(close.Length);
            for (int i = 0; i < close.Length; i++)
            {
                if (!IsNum(hh[i]) || !IsNum(ll[i]) || !IsNum(close[i])) continue;
                double range = hh[i] - ll[i];
                raw[i] = range == 0 ? 50 : 100 * (close[i] - ll[i]) / range;
            }
            var k = smoothK > 1 ? Sma(raw, smoothK) : raw;
            return (k, Sma(k, d));
        }

        public static (double[] K, double[] D) StochRsi(double[] src, int rsiLen = 14, int stochLen = 14, int smoothK = 3, int d = 3)
        {
            var rsi = Rsi(src, rsiLen);
            return Stoch(rsi, rsi, rsi, stochLen, smoothK, d);
        }

        /// <summary>Commodity Channel Index on typical price.</summary>
        public static double[] Cci(double[] tp, int n)
        {
            var sma = Sma(tp, n);
            var r = NaNs(tp.Length);
            for (int i = n - 1; i < tp.Length; i++)
            {
                if (!IsNum(sma[i])) continue;
                double md = 0;
                for (int j = i - n + 1; j <= i; j++) md += Math.Abs(tp[j] - sma[i]);
                md /= n;
                r[i] = md == 0 ? 0 : (tp[i] - sma[i]) / (0.015 * md);
            }
            return r;
        }

        public static double[] WilliamsR(double[] high, double[] low, double[] close, int n)
        {
            var hh = Highest(high, n);
            var ll = Lowest(low, n);
            var r = NaNs(close.Length);
            for (int i = 0; i < close.Length; i++)
            {
                if (!IsNum(hh[i]) || !IsNum(ll[i])) continue;
                double range = hh[i] - ll[i];
                r[i] = range == 0 ? -50 : -100 * (hh[i] - close[i]) / range;
            }
            return r;
        }

        /// <summary>Money Flow Index.</summary>
        public static double[] Mfi(double[] high, double[] low, double[] close, double[] volume, int n)
        {
            int len = close.Length;
            var tp = new double[len];
            for (int i = 0; i < len; i++) tp[i] = (high[i] + low[i] + close[i]) / 3;
            var pos = NaNs(len);
            var neg = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double flow = tp[i] * volume[i];
                pos[i] = tp[i] > tp[i - 1] ? flow : 0;
                neg[i] = tp[i] < tp[i - 1] ? flow : 0;
            }
            var ps = Sum(pos, n);
            var ns = Sum(neg, n);
            var r = NaNs(len);
            for (int i = 0; i < len; i++)
            {
                if (!IsNum(ps[i]) || !IsNum(ns[i])) continue;
                r[i] = ns[i] == 0 ? (ps[i] == 0 ? 50 : 100) : 100 - 100 / (1 + ps[i] / ns[i]);
            }
            return r;
        }

        /// <summary>Ultimate Oscillator (Williams).</summary>
        public static double[] UltimateOscillator(double[] high, double[] low, double[] close, int s = 7, int m = 14, int l = 28)
        {
            int len = close.Length;
            var bp = NaNs(len);
            var tr = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double lowTrue = Math.Min(low[i], close[i - 1]);
                double highTrue = Math.Max(high[i], close[i - 1]);
                bp[i] = close[i] - lowTrue;
                tr[i] = highTrue - lowTrue;
            }
            var a1 = Zip(Sum(bp, s), Sum(tr, s), (a, b) => b == 0 ? 0 : a / b);
            var a2 = Zip(Sum(bp, m), Sum(tr, m), (a, b) => b == 0 ? 0 : a / b);
            var a3 = Zip(Sum(bp, l), Sum(tr, l), (a, b) => b == 0 ? 0 : a / b);
            var r = NaNs(len);
            for (int i = 0; i < len; i++)
                if (IsNum(a1[i]) && IsNum(a2[i]) && IsNum(a3[i])) r[i] = 100 * (4 * a1[i] + 2 * a2[i] + a3[i]) / 7;
            return r;
        }

        /// <summary>Awesome Oscillator: SMA(hl2, 5) − SMA(hl2, 34).</summary>
        public static double[] AwesomeOscillator(double[] hl2, int fast = 5, int slow = 34)
            => Zip(Sma(hl2, fast), Sma(hl2, slow), (a, b) => a - b);

        /// <summary>Percentage price oscillator.</summary>
        public static (double[] Ppo, double[] Signal, double[] Hist) Ppo(double[] src, int fast = 12, int slow = 26, int signal = 9)
        {
            var ppo = Zip(Ema(src, fast), Ema(src, slow), (a, b) => b == 0 ? double.NaN : 100 * (a - b) / b);
            var sig = Ema(ppo, signal);
            return (ppo, sig, Zip(ppo, sig, (a, b) => a - b));
        }

        /// <summary>Chande Momentum Oscillator.</summary>
        public static double[] Cmo(double[] src, int n)
        {
            int len = src.Length;
            var up = NaNs(len);
            var dn = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double ch = src[i] - src[i - 1];
                up[i] = Math.Max(ch, 0);
                dn[i] = Math.Max(-ch, 0);
            }
            return Zip(Sum(up, n), Sum(dn, n), (u, d) => u + d == 0 ? 0 : 100 * (u - d) / (u + d));
        }

        /// <summary>True Strength Index.</summary>
        public static (double[] Tsi, double[] Signal) Tsi(double[] src, int longLen = 25, int shortLen = 13, int signal = 13)
        {
            var mom = Change(src, 1);
            var num = Ema(Ema(mom, longLen), shortLen);
            var den = Ema(Ema(Map(mom, Math.Abs), longLen), shortLen);
            var tsi = Zip(num, den, (a, b) => b == 0 ? 0 : 100 * a / b);
            return (tsi, Ema(tsi, signal));
        }

        /// <summary>Ehlers Fisher transform of hl2 over <paramref name="n"/>.</summary>
        public static (double[] Fisher, double[] Trigger) Fisher(double[] high, double[] low, int n = 9)
        {
            int len = high.Length;
            var hl2 = new double[len];
            for (int i = 0; i < len; i++) hl2[i] = (high[i] + low[i]) / 2;
            var hh = Highest(hl2, n);
            var ll = Lowest(hl2, n);
            var fish = NaNs(len);
            var trig = NaNs(len);
            double value = 0, prevFish = 0;
            for (int i = 0; i < len; i++)
            {
                if (!IsNum(hh[i]) || !IsNum(ll[i])) continue;
                double range = hh[i] - ll[i];
                double x = range == 0 ? 0 : 2 * ((hl2[i] - ll[i]) / range - 0.5);
                value = 0.66 * x + 0.67 * value;
                value = Math.Clamp(value, -0.999, 0.999);
                double f = 0.5 * Math.Log((1 + value) / (1 - value)) + 0.5 * prevFish;
                trig[i] = prevFish;
                fish[i] = f;
                prevFish = f;
            }
            return (fish, trig);
        }

        /// <summary>Connors RSI: average of RSI(price), RSI(streak) and percent-rank of 1-bar ROC.</summary>
        public static double[] ConnorsRsi(double[] src, int rsiLen = 3, int streakLen = 2, int rankLen = 100)
        {
            int len = src.Length;
            var streak = NaNs(len);
            if (len > 0) streak[0] = 0;
            for (int i = 1; i < len; i++)
            {
                double prev = IsNum(streak[i - 1]) ? streak[i - 1] : 0;
                if (src[i] > src[i - 1]) streak[i] = prev > 0 ? prev + 1 : 1;
                else if (src[i] < src[i - 1]) streak[i] = prev < 0 ? prev - 1 : -1;
                else streak[i] = 0;
            }
            var r1 = Rsi(src, rsiLen);
            var r2 = Rsi(streak, streakLen);
            var r3 = PercentRank(Roc(src, 1), rankLen);
            var r = NaNs(len);
            for (int i = 0; i < len; i++)
                if (IsNum(r1[i]) && IsNum(r2[i]) && IsNum(r3[i])) r[i] = (r1[i] + r2[i] + r3[i]) / 3;
            return r;
        }

        /// <summary>Coppock curve: WMA(ROC(long) + ROC(short), wma).</summary>
        public static double[] Coppock(double[] src, int wmaLen = 10, int longRoc = 14, int shortRoc = 11)
            => Wma(Zip(Roc(src, longRoc), Roc(src, shortRoc), (a, b) => a + b), wmaLen);

        /// <summary>Detrended price oscillator, causal form: src − SMA(src, n) shifted back n/2+1 bars.</summary>
        public static double[] Dpo(double[] src, int n)
        {
            var sma = Sma(src, n);
            int shift = n / 2 + 1;
            var r = NaNs(src.Length);
            for (int i = shift; i < src.Length; i++)
                if (IsNum(src[i - shift]) && IsNum(sma[i])) r[i] = src[i - shift] - sma[i];
            return r;
        }

        /// <summary>TRIX: 1-bar ROC of a triple EMA, in %.</summary>
        public static double[] Trix(double[] src, int n)
            => Roc(Ema(Ema(Ema(src, n), n), n), 1);

        /// <summary>Know Sure Thing (Pring), with signal line.</summary>
        public static (double[] Kst, double[] Signal) Kst(double[] src)
        {
            var r1 = Sma(Roc(src, 10), 10);
            var r2 = Sma(Roc(src, 15), 10);
            var r3 = Sma(Roc(src, 20), 10);
            var r4 = Sma(Roc(src, 30), 15);
            var kst = NaNs(src.Length);
            for (int i = 0; i < src.Length; i++)
                if (IsNum(r1[i]) && IsNum(r2[i]) && IsNum(r3[i]) && IsNum(r4[i]))
                    kst[i] = r1[i] + 2 * r2[i] + 3 * r3[i] + 4 * r4[i];
            return (kst, Sma(kst, 9));
        }

        public static double[] Momentum(double[] src, int n) => Change(src, n);
    }
}
