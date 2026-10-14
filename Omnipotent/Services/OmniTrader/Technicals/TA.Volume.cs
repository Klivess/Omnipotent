namespace Omnipotent.Services.OmniTrader.Technicals
{
    public enum VwapAnchor { Session, Week, Month }

    public static partial class TA
    {
        public static double[] Obv(double[] close, double[] volume)
        {
            int len = close.Length;
            var r = NaNs(len);
            if (len == 0) return r;
            double acc = 0;
            r[0] = 0;
            for (int i = 1; i < len; i++)
            {
                if (close[i] > close[i - 1]) acc += volume[i];
                else if (close[i] < close[i - 1]) acc -= volume[i];
                r[i] = acc;
            }
            return r;
        }

        /// <summary>Anchored VWAP on typical price, resetting at each UTC day / ISO week / month.</summary>
        public static double[] Vwap(double[] high, double[] low, double[] close, double[] volume, DateTime[] time, VwapAnchor anchor = VwapAnchor.Session)
        {
            int len = close.Length;
            var r = NaNs(len);
            double pv = 0, vv = 0;
            int key = int.MinValue;
            for (int i = 0; i < len; i++)
            {
                int k = anchor switch
                {
                    VwapAnchor.Week => (int)(time[i].Date.AddDays(-(((int)time[i].DayOfWeek + 6) % 7)) - DateTime.UnixEpoch).TotalDays,
                    VwapAnchor.Month => time[i].Year * 12 + time[i].Month,
                    _ => (int)(time[i].Date - DateTime.UnixEpoch).TotalDays
                };
                if (k != key) { key = k; pv = 0; vv = 0; }
                double tp = (high[i] + low[i] + close[i]) / 3;
                pv += tp * volume[i];
                vv += volume[i];
                r[i] = vv == 0 ? tp : pv / vv;
            }
            return r;
        }

        /// <summary>Rolling VWAP over the last <paramref name="n"/> bars.</summary>
        public static double[] RollingVwap(double[] high, double[] low, double[] close, double[] volume, int n)
        {
            int len = close.Length;
            var pv = new double[len];
            for (int i = 0; i < len; i++) pv[i] = (high[i] + low[i] + close[i]) / 3 * volume[i];
            return Zip(Sum(pv, n), Sum(volume, n), (a, b) => b == 0 ? double.NaN : a / b);
        }

        /// <summary>Accumulation/distribution line.</summary>
        public static double[] AccumulationDistribution(double[] high, double[] low, double[] close, double[] volume)
        {
            int len = close.Length;
            var r = new double[len];
            double acc = 0;
            for (int i = 0; i < len; i++)
            {
                double range = high[i] - low[i];
                double mfm = range == 0 ? 0 : ((close[i] - low[i]) - (high[i] - close[i])) / range;
                acc += mfm * volume[i];
                r[i] = acc;
            }
            return r;
        }

        /// <summary>Chaikin money flow.</summary>
        public static double[] ChaikinMoneyFlow(double[] high, double[] low, double[] close, double[] volume, int n = 20)
        {
            int len = close.Length;
            var mfv = new double[len];
            for (int i = 0; i < len; i++)
            {
                double range = high[i] - low[i];
                double mfm = range == 0 ? 0 : ((close[i] - low[i]) - (high[i] - close[i])) / range;
                mfv[i] = mfm * volume[i];
            }
            return Zip(Sum(mfv, n), Sum(volume, n), (a, b) => b == 0 ? 0 : a / b);
        }

        public static double[] ChaikinOscillator(double[] high, double[] low, double[] close, double[] volume, int fast = 3, int slow = 10)
        {
            var ad = AccumulationDistribution(high, low, close, volume);
            return Zip(Ema(ad, fast), Ema(ad, slow), (a, b) => a - b);
        }

        /// <summary>Elder force index, EMA-smoothed.</summary>
        public static double[] ForceIndex(double[] close, double[] volume, int n = 13)
        {
            int len = close.Length;
            var raw = NaNs(len);
            for (int i = 1; i < len; i++) raw[i] = (close[i] - close[i - 1]) * volume[i];
            return Ema(raw, n);
        }

        /// <summary>Ease of movement, SMA-smoothed, scaled by 10^4 / volume.</summary>
        public static double[] EaseOfMovement(double[] high, double[] low, double[] volume, int n = 14)
        {
            int len = high.Length;
            var raw = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double dm = (high[i] + low[i]) / 2 - (high[i - 1] + low[i - 1]) / 2;
                double range = high[i] - low[i];
                raw[i] = volume[i] == 0 || range == 0 ? 0 : dm * range * 10000 / volume[i];
            }
            return Sma(raw, n);
        }

        /// <summary>Price-volume trend.</summary>
        public static double[] PriceVolumeTrend(double[] close, double[] volume)
        {
            int len = close.Length;
            var r = NaNs(len);
            if (len == 0) return r;
            double acc = 0;
            r[0] = 0;
            for (int i = 1; i < len; i++)
            {
                if (close[i - 1] != 0) acc += (close[i] - close[i - 1]) / close[i - 1] * volume[i];
                r[i] = acc;
            }
            return r;
        }

        /// <summary>Klinger volume oscillator with signal.</summary>
        public static (double[] Kvo, double[] Signal) Klinger(double[] high, double[] low, double[] close, double[] volume, int fast = 34, int slow = 55, int signal = 13)
        {
            int len = close.Length;
            var vf = NaNs(len);
            for (int i = 1; i < len; i++)
            {
                double tp = high[i] + low[i] + close[i];
                double prev = high[i - 1] + low[i - 1] + close[i - 1];
                vf[i] = (tp > prev ? 1 : -1) * volume[i];
            }
            var kvo = Zip(Ema(vf, fast), Ema(vf, slow), (a, b) => a - b);
            return (kvo, Ema(kvo, signal));
        }

        /// <summary>Volume relative to its n-bar average (1 = normal).</summary>
        public static double[] RelativeVolume(double[] volume, int n = 20)
        {
            var avg = Sma(Shift(volume, 1), n);
            return Zip(volume, avg, (v, a) => a == 0 ? double.NaN : v / a);
        }

        /// <summary>Volume oscillator: % difference between fast and slow volume EMAs.</summary>
        public static double[] VolumeOscillator(double[] volume, int fast = 5, int slow = 10)
            => Zip(Ema(volume, fast), Ema(volume, slow), (a, b) => b == 0 ? 0 : 100 * (a - b) / b);

        /// <summary>Dollar volume averaged over n bars.</summary>
        public static double[] DollarVolume(double[] close, double[] volume, int n = 20)
            => Sma(Zip(close, volume, (c, v) => c * v), n);
    }
}
