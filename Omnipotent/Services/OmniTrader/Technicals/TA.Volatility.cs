namespace Omnipotent.Services.OmniTrader.Technicals
{
    public static partial class TA
    {
        /// <summary>True range; bar 0 uses high − low.</summary>
        public static double[] TrueRange(double[] high, double[] low, double[] close)
        {
            int len = close.Length;
            var r = NaNs(len);
            for (int i = 0; i < len; i++)
            {
                double hl = high[i] - low[i];
                r[i] = i == 0 ? hl : Math.Max(hl, Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
            }
            return r;
        }

        /// <summary>Average true range (Wilder RMA of TR).</summary>
        public static double[] Atr(double[] high, double[] low, double[] close, int n = 14)
            => Rma(TrueRange(high, low, close), n);

        /// <summary>ATR as a percentage of the close.</summary>
        public static double[] Natr(double[] high, double[] low, double[] close, int n = 14)
            => Zip(Atr(high, low, close, n), close, (a, c) => c == 0 ? double.NaN : 100 * a / c);

        public static (double[] Mid, double[] Upper, double[] Lower, double[] PercentB, double[] Width) Bollinger(double[] src, int n = 20, double mult = 2)
        {
            var mid = Sma(src, n);
            var sd = StdDev(src, n);
            int len = src.Length;
            var up = NaNs(len); var lo = NaNs(len); var pb = NaNs(len); var w = NaNs(len);
            for (int i = 0; i < len; i++)
            {
                if (!IsNum(mid[i]) || !IsNum(sd[i])) continue;
                up[i] = mid[i] + mult * sd[i];
                lo[i] = mid[i] - mult * sd[i];
                double range = up[i] - lo[i];
                pb[i] = range == 0 ? 0.5 : (src[i] - lo[i]) / range;
                w[i] = mid[i] == 0 ? 0 : 100 * range / mid[i];
            }
            return (mid, up, lo, pb, w);
        }

        /// <summary>Keltner channel: EMA midline ± mult·ATR.</summary>
        public static (double[] Mid, double[] Upper, double[] Lower) Keltner(double[] high, double[] low, double[] close, int n = 20, double mult = 2, int atrLen = 10)
        {
            var mid = Ema(close, n);
            var atr = Atr(high, low, close, atrLen);
            return (mid, Zip(mid, atr, (m, a) => m + mult * a), Zip(mid, atr, (m, a) => m - mult * a));
        }

        public static (double[] Upper, double[] Lower, double[] Mid) Donchian(double[] high, double[] low, int n = 20)
        {
            var up = Highest(high, n);
            var lo = Lowest(low, n);
            return (up, lo, Zip(up, lo, (a, b) => (a + b) / 2));
        }

        /// <summary>Annualised close-to-close volatility of log returns, %.</summary>
        public static double[] HistoricalVolatility(double[] close, int n, double barsPerYear)
        {
            int len = close.Length;
            var lr = NaNs(len);
            for (int i = 1; i < len; i++)
                if (close[i] > 0 && close[i - 1] > 0) lr[i] = Math.Log(close[i] / close[i - 1]);
            return Map(StdDev(lr, n, sample: true), s => s * Math.Sqrt(barsPerYear) * 100);
        }

        /// <summary>Parkinson high-low volatility, annualised %.</summary>
        public static double[] ParkinsonVolatility(double[] high, double[] low, int n, double barsPerYear)
        {
            int len = high.Length;
            var x = NaNs(len);
            for (int i = 0; i < len; i++)
                if (high[i] > 0 && low[i] > 0) { double l = Math.Log(high[i] / low[i]); x[i] = l * l; }
            return Map(Sma(x, n), m => Math.Sqrt(m / (4 * Math.Log(2)) * barsPerYear) * 100);
        }

        /// <summary>Garman–Klass OHLC volatility, annualised %.</summary>
        public static double[] GarmanKlassVolatility(double[] open, double[] high, double[] low, double[] close, int n, double barsPerYear)
        {
            int len = close.Length;
            var x = NaNs(len);
            for (int i = 0; i < len; i++)
            {
                if (open[i] <= 0 || high[i] <= 0 || low[i] <= 0 || close[i] <= 0) continue;
                double hl = Math.Log(high[i] / low[i]);
                double co = Math.Log(close[i] / open[i]);
                x[i] = 0.5 * hl * hl - (2 * Math.Log(2) - 1) * co * co;
            }
            return Map(Sma(x, n), m => Math.Sqrt(Math.Max(0, m) * barsPerYear) * 100);
        }

        /// <summary>Ulcer index: RMS of percentage drawdown from the rolling high.</summary>
        public static double[] UlcerIndex(double[] close, int n = 14)
        {
            var hh = Highest(close, n);
            var dd = Zip(close, hh, (c, h) => h == 0 ? 0 : 100 * (c - h) / h);
            return Map(Sma(Map(dd, d => d * d), n), Math.Sqrt);
        }

        /// <summary>Chaikin volatility: ROC of the EMA of high−low.</summary>
        public static double[] ChaikinVolatility(double[] high, double[] low, int n = 10, int roc = 10)
            => Roc(Ema(Zip(high, low, (h, l) => h - l), n), roc);

        /// <summary>Squeeze: Bollinger inside Keltner (1) or not (0).</summary>
        public static double[] Squeeze(double[] high, double[] low, double[] close, int n = 20, double bbMult = 2, double kcMult = 1.5)
        {
            var bb = Bollinger(close, n, bbMult);
            var kc = Keltner(high, low, close, n, kcMult, n);
            var r = NaNs(close.Length);
            for (int i = 0; i < close.Length; i++)
                if (IsNum(bb.Upper[i]) && IsNum(kc.Upper[i])) r[i] = bb.Upper[i] < kc.Upper[i] && bb.Lower[i] > kc.Lower[i] ? 1 : 0;
            return r;
        }
    }
}
