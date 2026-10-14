using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Coinbase Exchange public candles — keyless, deep history, USD-quoted. Used as the second
    /// crypto source after Binance. Coinbase serves six granularities; anything else is folded
    /// from the nearest finer one.
    /// </summary>
    public sealed class CoinbaseMarketDataProvider : ICandleSource
    {
        public string Name => "Coinbase";
        private const string ApiBase = "https://api.exchange.coinbase.com/products/";
        private const int PageSize = 300;
        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OmniTrader/2.0");
            return client;
        }

        public bool Supports(TimeInterval interval) => true;

        public async Task<CandleFetch> FetchLatestAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            var (granularity, sourceInterval) = GranularityFor(interval);
            int factor = Math.Max(1, (int)Math.Ceiling(CandleClock.Span(interval).TotalSeconds / granularity));
            int needed = Math.Min((count + 2) * factor, PageSize * 40);
            DateTime end = DateTime.UtcNow;
            DateTime start = end.AddSeconds(-(double)needed * granularity);
            return await FetchCoreAsync(symbol, interval, sourceInterval, granularity, start, end, count, ct);
        }

        public Task<CandleFetch> FetchRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            var (granularity, sourceInterval) = GranularityFor(interval);
            return FetchCoreAsync(symbol, interval, sourceInterval, granularity, fromUtc, toUtc, int.MaxValue, ct);
        }

        private async Task<CandleFetch> FetchCoreAsync(string symbol, TimeInterval interval, TimeInterval sourceInterval,
            int granularity, DateTime fromUtc, DateTime toUtc, int count, CancellationToken ct)
        {
            string product = ToProduct(symbol);
            var raw = new Dictionary<DateTime, OHLCCandle>();
            DateTime cursor = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            DateTime end = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
            int pages = 0;
            while (cursor < end && pages++ < 400)
            {
                DateTime pageEnd = cursor.AddSeconds((double)granularity * PageSize);
                if (pageEnd > end) pageEnd = end;
                string url = $"{ApiBase}{product}/candles?granularity={granularity}"
                    + $"&start={Uri.EscapeDataString(cursor.ToString("o"))}&end={Uri.EscapeDataString(pageEnd.ToString("o"))}";
                using var resp = await http.GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"Coinbase candles {(int)resp.StatusCode}");
                foreach (var row in JArray.Parse(body))
                {
                    var t = DateTimeOffset.FromUnixTimeSeconds((long)row[0]!).UtcDateTime;
                    raw[t] = new OHLCCandle(t, D(row[3]), D(row[2]), D(row[1]), D(row[4]), D(row[5]));
                }
                cursor = pageEnd;
                // Coinbase allows ~10 public requests/second; stay well under it.
                if (cursor < end) await Task.Delay(120, ct);
            }

            var ordered = raw.Values.OrderBy(b => b.Timestamp).ToList();
            var bars = interval == sourceInterval
                ? ordered
                : CandleAggregator.Aggregate(ordered, interval, continuousMarket: true, sourceInterval);

            OHLCCandle? forming = null;
            if (bars.Count > 0 && !CandleClock.IsClosed(bars[^1].Timestamp, interval, DateTime.UtcNow))
            {
                forming = bars[^1];
                bars.RemoveAt(bars.Count - 1);
            }
            // A folded leading bucket is partial when the fetch window started after its open.
            if (interval != sourceInterval && bars.Count > 0 && ordered.Count > 0 && ordered[0].Timestamp > bars[0].Timestamp)
                bars.RemoveAt(0);
            if (bars.Count > count) bars = bars.GetRange(bars.Count - count, count);
            return new CandleFetch(bars, forming, Name);
        }

        private static (int Granularity, TimeInterval Source) GranularityFor(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => (60, TimeInterval.OneMinute),
            TimeInterval.FiveMinute => (300, TimeInterval.FiveMinute),
            TimeInterval.FifteenMinute => (900, TimeInterval.FifteenMinute),
            TimeInterval.ThirtyMinute => (900, TimeInterval.FifteenMinute),
            TimeInterval.OneHour => (3600, TimeInterval.OneHour),
            TimeInterval.TwoHour => (3600, TimeInterval.OneHour),
            TimeInterval.FourHour => (3600, TimeInterval.OneHour),
            TimeInterval.TwelveHour => (21600, TimeInterval.OneHour),
            TimeInterval.OneDay => (86400, TimeInterval.OneDay),
            TimeInterval.OneWeek => (86400, TimeInterval.OneDay),
            TimeInterval.OneMonth => (86400, TimeInterval.OneDay),
            _ => (3600, TimeInterval.OneHour)
        };

        /// <summary>BTCUSDT → BTC-USD. Stablecoin quotes collapse onto USD for this fallback.</summary>
        public static string ToProduct(string symbol)
        {
            string s = symbol.ToUpperInvariant().Replace("/", "").Replace("-", "");
            foreach (var quote in new[] { "USDT", "USDC", "BUSD" })
                if (s.EndsWith(quote) && s.Length > quote.Length) return $"{s[..^quote.Length]}-USD";
            foreach (var quote in new[] { "USD", "EUR", "GBP", "BTC", "ETH" })
                if (s.EndsWith(quote) && s.Length > quote.Length) return $"{s[..^quote.Length]}-{quote}";
            return $"{s}-USD";
        }

        private static decimal D(JToken? t) => t == null ? 0m : t.Type switch
        {
            JTokenType.Integer or JTokenType.Float => t.Value<decimal>(),
            _ => decimal.Parse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture)
        };
    }
}
