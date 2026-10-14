using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Kraken public OHLC. Kraken returns at most the 720 most recent bars of an interval and always
    /// ends with the uncommitted (forming) frame, so this is a fallback for recent history only.
    /// </summary>
    public sealed class KrakenMarketDataProvider : IMarketDataProvider, ICandleSource
    {
        public string Name => "Kraken";

        private const string ApiBase = "https://api.kraken.com/";
        private const int MaxBars = 720;
        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

        public bool Supports(TimeInterval interval) => interval is TimeInterval.OneMinute or TimeInterval.FiveMinute
            or TimeInterval.FifteenMinute or TimeInterval.ThirtyMinute or TimeInterval.OneHour or TimeInterval.FourHour
            or TimeInterval.OneDay or TimeInterval.OneWeek;

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
            => (await FetchLatestAsync(symbol, interval, count, ct)).Closed;

        public async Task<CandleFetch> FetchLatestAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            if (!Supports(interval)) throw new NotSupportedException($"Kraken has no {interval} bars");
            var (coin, currency) = SplitSymbol(symbol);
            string pair = $"{coin}/{currency}";
            long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)Math.Min(count + 1, MaxBars) * CandleClock.Seconds(interval);
            string url = $"{ApiBase}0/public/OHLC?pair={Uri.EscapeDataString(pair)}&interval={(int)interval}&since={since}";

            JObject json = await GetWithRetryAsync(url, ct);
            var result = json["result"] as JObject;
            JArray? rows = result?.Properties().FirstOrDefault(p => p.Name != "last")?.Value as JArray;
            if (rows == null || rows.Count == 0) return CandleFetch.Empty(Name);

            var bars = new List<OHLCCandle>(rows.Count);
            foreach (var c in rows)
            {
                bars.Add(new OHLCCandle(
                    DateTimeOffset.FromUnixTimeSeconds((long)c[0]!).UtcDateTime,
                    D(c[1]), D(c[2]), D(c[3]), D(c[4]), D(c[6])));
            }
            bars = bars.GroupBy(b => b.Timestamp).Select(g => g.Last()).OrderBy(b => b.Timestamp).ToList();

            // The last frame is uncommitted unless its bar has already ended.
            OHLCCandle? forming = null;
            if (bars.Count > 0 && !CandleClock.IsClosed(bars[^1].Timestamp, interval, DateTime.UtcNow))
            {
                forming = bars[^1];
                bars.RemoveAt(bars.Count - 1);
            }
            if (bars.Count > count) bars = bars.GetRange(bars.Count - count, count);
            return new CandleFetch(bars, forming, Name);
        }

        public async Task<CandleFetch> FetchRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            // Kraken cannot page back beyond its 720-bar window; serve what overlaps the request.
            var latest = await FetchLatestAsync(symbol, interval, MaxBars, ct);
            var closed = latest.Closed.Where(b => b.Timestamp >= fromUtc && b.Timestamp <= toUtc).ToList();
            return new CandleFetch(closed, latest.Forming is { } f && f.Timestamp <= toUtc ? f : null, Name);
        }

        private static async Task<JObject> GetWithRetryAsync(string url, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var response = await httpClient.GetAsync(url, ct);
                string body = await response.Content.ReadAsStringAsync(ct);
                bool rateLimited = (int)response.StatusCode == 429
                    || body.Contains("EAPI:Rate limit", StringComparison.Ordinal)
                    || body.Contains("EGeneral:Too many requests", StringComparison.Ordinal);
                if (rateLimited && attempt < 3)
                {
                    await Task.Delay(1500 * (1 << attempt), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Kraken OHLC {(int)response.StatusCode}");
                var json = JObject.Parse(body);
                if (json["error"] is JArray errors && errors.Count > 0)
                    throw new HttpRequestException($"Kraken: {string.Join(", ", errors)}");
                return json;
            }
        }

        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
        {
            DateTime? lastEmitted = null;
            var poll = TimeSpan.FromSeconds(Math.Clamp(CandleClock.Seconds(interval) / 6, 15, 300));
            while (!ct.IsCancellationRequested)
            {
                CandleFetch? fetch = null;
                try { fetch = await FetchLatestAsync(symbol, interval, 3, ct); }
                catch (OperationCanceledException) { yield break; }
                catch { }

                if (fetch != null)
                {
                    foreach (var bar in fetch.Closed)
                    {
                        if (lastEmitted.HasValue && bar.Timestamp <= lastEmitted.Value) continue;
                        lastEmitted = bar.Timestamp;
                        yield return bar;
                    }
                }
                try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { yield break; }
            }
        }

        private static (string coin, string currency) SplitSymbol(string symbol)
        {
            symbol = symbol.ToUpperInvariant().Replace("-", "");
            if (symbol.Contains('/'))
            {
                var parts = symbol.Split('/');
                return (parts[0], parts[1]);
            }
            foreach (var quote in new[] { "USDT", "USDC" })
                if (symbol.EndsWith(quote) && symbol.Length > quote.Length) return (symbol[..^quote.Length], "USD");
            foreach (var quote in new[] { "USD", "EUR", "GBP" })
                if (symbol.EndsWith(quote) && symbol.Length > quote.Length) return (symbol[..^quote.Length], quote);
            return (symbol, "USD");
        }

        private static decimal D(JToken? t) => decimal.Parse(t!.Type == JTokenType.String ? (string)t! : t.ToString(),
            NumberStyles.Any, CultureInfo.InvariantCulture);
    }
}
