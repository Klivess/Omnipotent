using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Bar arithmetic. Every candle in OmniTrader is stamped with its bar OPEN time in UTC; a bar is
    /// closed once the next bar's open has passed. Weekly bars open on Monday 00:00 UTC and monthly
    /// bars on the 1st, matching the exchanges.
    /// </summary>
    public static class CandleClock
    {
        public static TimeSpan Span(TimeInterval interval) => interval == TimeInterval.OneMonth
            ? TimeSpan.FromDays(30)
            : TimeSpan.FromMinutes((int)interval);

        public static int Seconds(TimeInterval interval) => (int)Span(interval).TotalSeconds;

        /// <summary>Open of the bar containing <paramref name="utc"/> on a 24/7 venue clock.</summary>
        public static DateTime BarOpen(DateTime utc, TimeInterval interval)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            switch (interval)
            {
                case TimeInterval.OneMonth:
                    return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                case TimeInterval.OneWeek:
                {
                    var day = utc.Date;
                    int offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
                    return DateTime.SpecifyKind(day.AddDays(-offset), DateTimeKind.Utc);
                }
                default:
                {
                    long seconds = Seconds(interval);
                    long unix = new DateTimeOffset(utc).ToUnixTimeSeconds();
                    return DateTimeOffset.FromUnixTimeSeconds(unix - Mod(unix, seconds)).UtcDateTime;
                }
            }
        }

        public static DateTime NextOpen(DateTime barOpen, TimeInterval interval) => interval == TimeInterval.OneMonth
            ? barOpen.AddMonths(1)
            : barOpen + Span(interval);

        public static DateTime PreviousOpen(DateTime barOpen, TimeInterval interval) => interval == TimeInterval.OneMonth
            ? barOpen.AddMonths(-1)
            : barOpen - Span(interval);

        public static bool IsClosed(DateTime barOpen, TimeInterval interval, DateTime nowUtc)
            => NextOpen(barOpen, interval) <= nowUtc;

        /// <summary>Open of the newest bar that has fully closed by <paramref name="nowUtc"/> (24/7 markets).</summary>
        public static DateTime LastClosedOpen(DateTime nowUtc, TimeInterval interval)
            => PreviousOpen(BarOpen(nowUtc, interval), interval);

        /// <summary>
        /// Bars in a year, used to annualise per-bar statistics. Continuous markets trade 365 days a
        /// year around the clock; session markets trade ~252 days of ~6.5 hours.
        /// </summary>
        public static double BarsPerYear(TimeInterval interval, bool continuousMarket)
        {
            if (interval == TimeInterval.OneMonth) return 12;
            if (interval == TimeInterval.OneWeek) return 52;
            if (interval == TimeInterval.OneDay) return continuousMarket ? 365 : 252;
            double minutes = (int)interval;
            double minutesPerYear = continuousMarket ? 365.0 * 24 * 60 : 252.0 * 6.5 * 60;
            return Math.Max(1, minutesPerYear / minutes);
        }

        public static IEnumerable<TimeInterval> All => Enum.GetValues<TimeInterval>().OrderBy(i => (int)i);

        public static string Short(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => "1m",
            TimeInterval.FiveMinute => "5m",
            TimeInterval.FifteenMinute => "15m",
            TimeInterval.ThirtyMinute => "30m",
            TimeInterval.OneHour => "1h",
            TimeInterval.TwoHour => "2h",
            TimeInterval.FourHour => "4h",
            TimeInterval.TwelveHour => "12h",
            TimeInterval.OneDay => "1D",
            TimeInterval.OneWeek => "1W",
            TimeInterval.OneMonth => "1M",
            _ => interval.ToString()
        };

        /// <summary>Accepts enum names ("OneHour"), short codes ("1h", "4H", "1D", "W", "M") or minutes ("60").</summary>
        public static TimeInterval? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (Enum.TryParse<TimeInterval>(s, true, out var named) && Enum.IsDefined(named)) return named;
            switch (s)
            {
                case "1m": case "1": return TimeInterval.OneMinute;
                case "5m": case "5": return TimeInterval.FiveMinute;
                case "15m": case "15": return TimeInterval.FifteenMinute;
                case "30m": case "30": return TimeInterval.ThirtyMinute;
                case "1h": case "1H": case "60": case "60m": return TimeInterval.OneHour;
                case "2h": case "2H": case "120": return TimeInterval.TwoHour;
                case "4h": case "4H": case "240": return TimeInterval.FourHour;
                case "12h": case "12H": case "720": return TimeInterval.TwelveHour;
                case "1d": case "1D": case "D": case "d": case "1440": return TimeInterval.OneDay;
                case "1w": case "1W": case "W": case "w": case "10080": return TimeInterval.OneWeek;
                case "1M": case "M": case "1mo": case "43200": return TimeInterval.OneMonth;
            }
            return null;
        }

        private static long Mod(long a, long b) => ((a % b) + b) % b;
    }

    /// <summary>
    /// What one provider returned: the closed bars (safe to cache and to feed a strategy) and,
    /// separately, the bar still forming (charts only — it changes until it closes).
    /// </summary>
    public sealed record CandleFetch(IReadOnlyList<OHLCCandle> Closed, OHLCCandle? Forming, string Source)
    {
        public static CandleFetch Empty(string source) => new(Array.Empty<OHLCCandle>(), null, source);
        public bool HasData => Closed.Count > 0 || Forming.HasValue;
    }

    /// <summary>A provider that can answer bar requests with closed/forming separated.</summary>
    public interface ICandleSource
    {
        string Name { get; }
        bool Supports(TimeInterval interval);
        Task<CandleFetch> FetchLatestAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default);
        Task<CandleFetch> FetchRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    }

    /// <summary>
    /// Folds finer bars into a coarser interval. Continuous markets bucket on the venue clock;
    /// session markets (equities) group by trading day and take consecutive runs, which is how
    /// charting platforms build a 2h/4h bar for a 6.5-hour session.
    /// </summary>
    public static class CandleAggregator
    {
        public static List<OHLCCandle> Aggregate(IReadOnlyList<OHLCCandle> bars, TimeInterval target, bool continuousMarket,
            TimeInterval source)
        {
            if (bars.Count == 0) return new List<OHLCCandle>();
            if (continuousMarket || target is TimeInterval.OneDay or TimeInterval.OneWeek or TimeInterval.OneMonth)
            {
                return bars
                    .GroupBy(b => CandleClock.BarOpen(b.Timestamp, target))
                    .OrderBy(g => g.Key)
                    .Select(g => Fold(g.Key, g.OrderBy(b => b.Timestamp).ToList()))
                    .ToList();
            }

            int per = Math.Max(1, (int)target / Math.Max(1, (int)source));
            var output = new List<OHLCCandle>();
            foreach (var day in bars.GroupBy(b => b.Timestamp.Date).OrderBy(g => g.Key))
            {
                var ordered = day.OrderBy(b => b.Timestamp).ToList();
                for (int i = 0; i < ordered.Count; i += per)
                {
                    var chunk = ordered.Skip(i).Take(per).ToList();
                    output.Add(Fold(chunk[0].Timestamp, chunk));
                }
            }
            return output;
        }

        private static OHLCCandle Fold(DateTime open, List<OHLCCandle> bars)
        {
            decimal high = bars[0].High, low = bars[0].Low, volume = 0m;
            foreach (var b in bars)
            {
                if (b.High > high) high = b.High;
                if (b.Low < low) low = b.Low;
                volume += b.Volume;
            }
            return new OHLCCandle(open, bars[0].Open, high, low, bars[^1].Close, volume);
        }
    }
}
