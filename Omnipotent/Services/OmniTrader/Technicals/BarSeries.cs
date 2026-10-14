using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Technicals
{
    /// <summary>
    /// Columnar OHLCV — the shape every indicator computes over. Arrays are aligned by bar index;
    /// index 0 is the oldest bar.
    /// </summary>
    public sealed class BarSeries
    {
        public DateTime[] Time { get; }
        public double[] Open { get; }
        public double[] High { get; }
        public double[] Low { get; }
        public double[] Close { get; }
        public double[] Volume { get; }
        public int Count => Close.Length;

        /// <summary>Bars per year for annualised statistics (set by the caller from the interval).</summary>
        public double BarsPerYear { get; init; } = 365;

        private double[]? hl2, hlc3, ohlc4, hlcc4;

        public BarSeries(DateTime[] time, double[] open, double[] high, double[] low, double[] close, double[] volume)
        {
            Time = time; Open = open; High = high; Low = low; Close = close; Volume = volume;
        }

        public static BarSeries From(IReadOnlyList<OHLCCandle> candles, double barsPerYear = 365)
        {
            int n = candles.Count;
            var t = new DateTime[n];
            var o = new double[n];
            var h = new double[n];
            var l = new double[n];
            var c = new double[n];
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                var k = candles[i];
                t[i] = k.Timestamp;
                o[i] = (double)k.Open;
                h[i] = (double)k.High;
                l[i] = (double)k.Low;
                c[i] = (double)k.Close;
                v[i] = (double)k.Volume;
            }
            return new BarSeries(t, o, h, l, c, v) { BarsPerYear = barsPerYear };
        }

        /// <summary>The first <paramref name="count"/> bars. Used to prove causality: an indicator computed
        /// on a prefix must equal the full computation truncated to that prefix.</summary>
        public BarSeries Prefix(int count)
        {
            count = Math.Clamp(count, 0, Count);
            return new BarSeries(Time[..count], Open[..count], High[..count], Low[..count], Close[..count], Volume[..count])
            {
                BarsPerYear = BarsPerYear
            };
        }

        public double[] Hl2 => hl2 ??= Combine((i) => (High[i] + Low[i]) / 2);
        public double[] Hlc3 => hlc3 ??= Combine((i) => (High[i] + Low[i] + Close[i]) / 3);
        public double[] Ohlc4 => ohlc4 ??= Combine((i) => (Open[i] + High[i] + Low[i] + Close[i]) / 4);
        public double[] Hlcc4 => hlcc4 ??= Combine((i) => (High[i] + Low[i] + Close[i] + Close[i]) / 4);

        public static readonly string[] SourceNames = { "close", "open", "high", "low", "volume", "hl2", "hlc3", "ohlc4", "hlcc4" };

        public double[]? Source(string name) => name.ToLowerInvariant() switch
        {
            "open" or "o" => Open,
            "high" or "h" => High,
            "low" or "l" => Low,
            "close" or "c" => Close,
            "volume" or "v" => Volume,
            "hl2" => Hl2,
            "hlc3" or "typical" => Hlc3,
            "ohlc4" => Ohlc4,
            "hlcc4" => Hlcc4,
            _ => null
        };

        private double[] Combine(Func<int, double> f)
        {
            var r = new double[Count];
            for (int i = 0; i < Count; i++) r[i] = f(i);
            return r;
        }
    }
}
