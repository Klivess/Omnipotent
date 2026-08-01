using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Venues;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Market data for everything that is not crypto: shares, ETFs, indices, FX and commodities.
    ///
    /// Yahoo's chart endpoint is keyless and covers every listing Trading 212 and IG deal in, which
    /// is what makes "chart any symbol" possible without another paid subscription. It is a *data*
    /// source only — nothing here can place an order — and it has no real stream, so
    /// <see cref="StreamCandlesAsync"/> polls at the bar interval and says so rather than pretending
    /// to be a live feed.
    /// </summary>
    public sealed class YahooMarketDataProvider : IMarketDataProvider
    {
        private const string ChartBase = "https://query1.finance.yahoo.com/v8/finance/chart/";
        private const string SearchBase = "https://query1.finance.yahoo.com/v1/finance/search";

        private readonly HttpClient http;

        public string Name => "yahoo";

        public YahooMarketDataProvider()
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // Yahoo refuses requests without a browser-shaped user agent.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        }

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            string yahooInterval = ToYahooInterval(interval);
            string range = ToRange(interval, count);
            string url = $"{ChartBase}{Uri.EscapeDataString(symbol)}?interval={yahooInterval}&range={range}&includePrePost=false";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return Array.Empty<OHLCCandle>();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = json["chart"]?["result"]?.FirstOrDefault();
            if (result == null) return Array.Empty<OHLCCandle>();

            var stamps = result["timestamp"] as JArray;
            var quote = result["indicators"]?["quote"]?.FirstOrDefault();
            if (stamps == null || quote == null) return Array.Empty<OHLCCandle>();

            var opens = quote["open"] as JArray;
            var highs = quote["high"] as JArray;
            var lows = quote["low"] as JArray;
            var closes = quote["close"] as JArray;
            var volumes = quote["volume"] as JArray;
            if (closes == null) return Array.Empty<OHLCCandle>();

            var candles = new List<OHLCCandle>(stamps.Count);
            for (int i = 0; i < stamps.Count; i++)
            {
                // Yahoo pads gaps (holidays, halts) with nulls. A null bar is missing data, not a
                // zero price, so it is dropped rather than drawn as a crash to zero.
                decimal? close = closes.ElementAtOrDefault(i)?.Value<decimal?>();
                if (close is not > 0m) continue;

                decimal open = opens?.ElementAtOrDefault(i)?.Value<decimal?>() ?? close.Value;
                decimal high = highs?.ElementAtOrDefault(i)?.Value<decimal?>() ?? close.Value;
                decimal low = lows?.ElementAtOrDefault(i)?.Value<decimal?>() ?? close.Value;
                decimal volume = volumes?.ElementAtOrDefault(i)?.Value<decimal?>() ?? 0m;

                candles.Add(new OHLCCandle(
                    DateTimeOffset.FromUnixTimeSeconds(stamps[i].Value<long>()).UtcDateTime,
                    open, high, low, close.Value, volume));
            }

            return candles.Count > count ? candles.Skip(candles.Count - count).ToList() : candles;
        }

        /// <summary>Latest trade price and the session's context, in one request.</summary>
        public async Task<YahooQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        {
            string url = $"{ChartBase}{Uri.EscapeDataString(symbol)}?interval=1m&range=1d";
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var meta = json["chart"]?["result"]?.FirstOrDefault()?["meta"];
            if (meta == null) return null;

            decimal price = meta["regularMarketPrice"]?.Value<decimal?>() ?? 0m;
            decimal previousClose = meta["chartPreviousClose"]?.Value<decimal?>()
                                 ?? meta["previousClose"]?.Value<decimal?>() ?? 0m;

            return new YahooQuote
            {
                Symbol = meta["symbol"]?.Value<string>() ?? symbol,
                Price = price,
                PreviousClose = previousClose,
                Currency = meta["currency"]?.Value<string>() ?? "USD",
                ExchangeName = meta["fullExchangeName"]?.Value<string>() ?? meta["exchangeName"]?.Value<string>(),
                // "The market is closed" is a fact an operator needs; a flat chart otherwise reads
                // as a dead feed.
                MarketState = meta["marketState"]?.Value<string>(),
                AsOfUtc = meta["regularMarketTime"] is { } t && t.Type != JTokenType.Null
                    ? DateTimeOffset.FromUnixTimeSeconds(t.Value<long>()).UtcDateTime
                    : DateTime.UtcNow
            };
        }

        /// <summary>Search every listed symbol, not just the ones already in the instrument master.</summary>
        public async Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SymbolMatch>();
            string url = $"{SearchBase}?q={Uri.EscapeDataString(query)}&quotesCount={limit}&newsCount=0";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return Array.Empty<SymbolMatch>();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var quotes = json["quotes"] as JArray ?? new JArray();

            return quotes.Select(q => new SymbolMatch
            {
                Symbol = q["symbol"]?.Value<string>() ?? "",
                DisplayName = q["shortname"]?.Value<string>() ?? q["longname"]?.Value<string>() ?? q["symbol"]?.Value<string>() ?? "",
                Exchange = q["exchDisp"]?.Value<string>(),
                AssetClass = (q["quoteType"]?.Value<string>() ?? "").ToUpperInvariant() switch
                {
                    "EQUITY" => AssetClass.Equity,
                    "ETF" or "MUTUALFUND" => AssetClass.Equity,
                    "INDEX" => AssetClass.Index,
                    "CURRENCY" => AssetClass.Forex,
                    "CRYPTOCURRENCY" => AssetClass.Crypto,
                    "FUTURE" => AssetClass.Commodity,
                    _ => AssetClass.Unknown
                },
                Source = "yahoo"
            }).Where(m => !string.IsNullOrWhiteSpace(m.Symbol)).ToList();
        }

        /// <summary>
        /// There is no public Yahoo stream, so this polls the latest bar. The interval is the bar
        /// size, never faster: pretending to stream by hammering a REST endpoint gets the caller
        /// rate-limited and still does not produce tick data.
        /// </summary>
        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var poll = TimeSpan.FromSeconds(Math.Max(15, (int)interval * 60 / 4.0));
            OHLCCandle? last = null;
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<OHLCCandle> candles;
                try { candles = await GetHistoricalCandlesAsync(symbol, interval, 2, ct); }
                catch { candles = Array.Empty<OHLCCandle>(); }

                if (candles.Count > 0 && (last == null || candles[^1].Timestamp >= last.Value.Timestamp))
                {
                    last = candles[^1];
                    yield return last.Value;
                }

                try { await Task.Delay(poll, ct); }
                catch (TaskCanceledException) { yield break; }
            }
        }

        private static string ToYahooInterval(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => "1m",
            TimeInterval.FiveMinute => "5m",
            TimeInterval.FifteenMinute => "15m",
            TimeInterval.ThirtyMinute => "30m",
            TimeInterval.OneHour => "60m",
            TimeInterval.FourHour => "1h",   // Yahoo has no 4h bar; the caller gets hourly bars.
            TimeInterval.OneDay => "1d",
            TimeInterval.OneWeek => "1wk",
            _ => "1d"
        };

        /// <summary>Yahoo takes a range, not a bar count, and caps intraday history hard.</summary>
        private static string ToRange(TimeInterval interval, int count) => interval switch
        {
            TimeInterval.OneMinute => count <= 390 ? "1d" : "7d",
            TimeInterval.FiveMinute or TimeInterval.FifteenMinute => count <= 500 ? "1mo" : "60d",
            TimeInterval.ThirtyMinute or TimeInterval.OneHour or TimeInterval.FourHour => count <= 500 ? "3mo" : "2y",
            TimeInterval.OneDay => count <= 250 ? "1y" : count <= 1300 ? "5y" : "max",
            _ => "max"
        };
    }

    public sealed class YahooQuote
    {
        public required string Symbol { get; init; }
        public decimal Price { get; init; }
        public decimal PreviousClose { get; init; }
        public string Currency { get; init; } = "USD";
        public string? ExchangeName { get; init; }
        public string? MarketState { get; init; }
        public DateTime AsOfUtc { get; init; }

        public decimal ChangePercent => PreviousClose > 0m ? (Price - PreviousClose) / PreviousClose * 100m : 0m;
    }

    public sealed class SymbolMatch
    {
        public required string Symbol { get; init; }
        public required string DisplayName { get; init; }
        public string? Exchange { get; init; }
        public AssetClass AssetClass { get; init; }
        public string Source { get; init; } = "";
        /// <summary>Set when the symbol already exists in the instrument master.</summary>
        public string? InstrumentId { get; init; }
        public List<string> TradableOn { get; init; } = new();
    }
}
