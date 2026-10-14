using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Venues;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Shares, ETFs, indices, FX, futures and Yahoo-listed crypto via Yahoo's keyless chart API.
    /// Intraday depth is capped by Yahoo (1m ≈ 30d, ≤30m ≈ 60d, 1h ≈ 730d); 2h/4h/12h are folded
    /// from hourly bars per trading day. The trailing bar is reported as forming until its bar has
    /// ended or the regular session has closed.
    /// </summary>
    public sealed class YahooMarketDataProvider : IMarketDataProvider, ICandleSource
    {
        private const string ChartBase = "https://query1.finance.yahoo.com/v8/finance/chart/";
        private const string SearchBase = "https://query1.finance.yahoo.com/v1/finance/search";

        public YahooSession Session { get; }

        public YahooMarketDataProvider(YahooSession? session = null)
        {
            Session = session ?? new YahooSession();
        }

        public string Name => "yahoo";
        public bool Supports(TimeInterval interval) => true;

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
            => (await FetchLatestAsync(symbol, interval, count, ct)).Closed;

        public async Task<CandleFetch> FetchLatestAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            var (yahooInterval, sourceInterval) = Native(interval);
            DateTime now = DateTime.UtcNow;
            DateTime from = now - LookbackFor(sourceInterval, interval, count);
            DateTime floor = now - MaxLookback(sourceInterval);
            if (from < floor) from = floor;
            var chart = await GetChartAsync(symbol, yahooInterval, from, now, ct);
            return ToFetch(chart, interval, sourceInterval, count);
        }

        public async Task<CandleFetch> FetchRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            var (yahooInterval, sourceInterval) = Native(interval);
            DateTime floor = DateTime.UtcNow - MaxLookback(sourceInterval);
            if (fromUtc < floor) fromUtc = floor;
            var chart = await GetChartAsync(symbol, yahooInterval, fromUtc, toUtc, ct);
            return ToFetch(chart, interval, sourceInterval, int.MaxValue);
        }

        private CandleFetch ToFetch(YahooChart? chart, TimeInterval interval, TimeInterval sourceInterval, int count)
        {
            if (chart == null || chart.Bars.Count == 0) return CandleFetch.Empty(Name);
            bool continuous = chart.IsContinuous;
            var bars = interval == sourceInterval
                ? chart.Bars.ToList()
                : CandleAggregator.Aggregate(chart.Bars, interval, continuous, sourceInterval);

            OHLCCandle? forming = null;
            if (bars.Count > 0 && !IsBarComplete(bars[^1], interval, chart, DateTime.UtcNow))
            {
                forming = bars[^1];
                bars.RemoveAt(bars.Count - 1);
            }
            if (bars.Count > count) bars = bars.GetRange(bars.Count - count, count);
            return new CandleFetch(bars, forming, Name);
        }

        /// <summary>A bar is complete when its span has elapsed, or the session it belongs to is over.</summary>
        internal static bool IsBarComplete(OHLCCandle bar, TimeInterval interval, YahooChart chart, DateTime nowUtc)
        {
            if (CandleClock.IsClosed(bar.Timestamp, interval, nowUtc)) return true;
            if (interval is TimeInterval.OneWeek or TimeInterval.OneMonth) return false;
            if (chart.RegularSessionEndUtc is { } end && nowUtc >= end && bar.Timestamp < end) return true;
            return false;
        }

        /// <summary>Raw chart call. <paramref name="yahooInterval"/> is Yahoo's own code (1m, 60m, 1d, 1wk, 1mo).</summary>
        public async Task<YahooChart?> GetChartAsync(string symbol, string yahooInterval, DateTime fromUtc, DateTime toUtc,
            CancellationToken ct = default, bool includeEvents = false)
        {
            long p1 = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            long p2 = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            string url = $"{ChartBase}{Uri.EscapeDataString(symbol)}?interval={yahooInterval}&period1={p1}&period2={p2}&includePrePost=false"
                + (includeEvents ? "&events=div%2Csplits" : "");
            var (status, body) = await Session.GetAsync(url, ct);
            if ((int)status == 404) return null;
            if ((int)status >= 400) throw new HttpRequestException($"Yahoo chart {(int)status}");
            return ParseChart(body);
        }

        internal static YahooChart? ParseChart(string body)
        {
            var json = JObject.Parse(body);
            var result = json["chart"]?["result"]?.FirstOrDefault();
            if (result == null) return null;
            var meta = result["meta"] as JObject ?? new JObject();

            var bars = new List<OHLCCandle>();
            var stamps = result["timestamp"] as JArray;
            var quote = result["indicators"]?["quote"]?.FirstOrDefault();
            if (stamps != null && quote != null)
            {
                var opens = quote["open"] as JArray;
                var highs = quote["high"] as JArray;
                var lows = quote["low"] as JArray;
                var closes = quote["close"] as JArray;
                var volumes = quote["volume"] as JArray;
                for (int i = 0; i < stamps.Count; i++)
                {
                    // Yahoo pads holidays and halts with nulls — missing data, not a zero price.
                    decimal? close = Dec(closes?.ElementAtOrDefault(i));
                    if (close is not > 0m) continue;
                    decimal open = Dec(opens?.ElementAtOrDefault(i)) ?? close.Value;
                    decimal high = Dec(highs?.ElementAtOrDefault(i)) ?? close.Value;
                    decimal low = Dec(lows?.ElementAtOrDefault(i)) ?? close.Value;
                    decimal volume = Dec(volumes?.ElementAtOrDefault(i)) ?? 0m;
                    bars.Add(new OHLCCandle(DateTimeOffset.FromUnixTimeSeconds(stamps[i].Value<long>()).UtcDateTime,
                        open, Math.Max(high, Math.Max(open, close.Value)), Math.Min(low, Math.Min(open, close.Value)), close.Value, volume));
                }
            }
            bars = bars.GroupBy(b => b.Timestamp).Select(g => g.Last()).OrderBy(b => b.Timestamp).ToList();

            var chart = new YahooChart
            {
                Symbol = meta["symbol"]?.Value<string>() ?? "",
                Currency = meta["currency"]?.Value<string>(),
                ExchangeName = meta["fullExchangeName"]?.Value<string>() ?? meta["exchangeName"]?.Value<string>(),
                InstrumentType = meta["instrumentType"]?.Value<string>(),
                Timezone = meta["exchangeTimezoneName"]?.Value<string>(),
                RegularMarketPrice = Dec(meta["regularMarketPrice"]),
                PreviousClose = Dec(meta["chartPreviousClose"]) ?? Dec(meta["previousClose"]),
                RegularMarketTimeUtc = Unix(meta["regularMarketTime"]),
                RegularSessionStartUtc = Unix(meta["currentTradingPeriod"]?["regular"]?["start"]),
                RegularSessionEndUtc = Unix(meta["currentTradingPeriod"]?["regular"]?["end"]),
                FiftyTwoWeekHigh = Dec(meta["fiftyTwoWeekHigh"]),
                FiftyTwoWeekLow = Dec(meta["fiftyTwoWeekLow"]),
                DayHigh = Dec(meta["regularMarketDayHigh"]),
                DayLow = Dec(meta["regularMarketDayLow"]),
                DayVolume = Dec(meta["regularMarketVolume"]),
                LongName = meta["longName"]?.Value<string>() ?? meta["shortName"]?.Value<string>(),
                Bars = bars
            };

            if (result["events"]?["dividends"] is JObject divs)
                foreach (var p in divs.Properties())
                    if (Unix(p.Value["date"]) is { } d && Dec(p.Value["amount"]) is { } a) chart.Dividends.Add((d, a));
            if (result["events"]?["splits"] is JObject splits)
                foreach (var p in splits.Properties())
                    if (Unix(p.Value["date"]) is { } d)
                        chart.Splits.Add((d, p.Value["splitRatio"]?.Value<string>() ?? ""));
            return chart;
        }

        /// <summary>Chart by Yahoo range code (1d, 5d, 1mo, …). With range=1d the meta's
        /// chartPreviousClose is the prior session's close, which is what a day change needs.</summary>
        public async Task<YahooChart?> GetChartByRangeAsync(string symbol, string yahooInterval, string range, CancellationToken ct = default)
        {
            string url = $"{ChartBase}{Uri.EscapeDataString(symbol)}?interval={yahooInterval}&range={range}&includePrePost=false";
            var (status, body) = await Session.GetAsync(url, ct);
            if ((int)status == 404) return null;
            if ((int)status >= 400) throw new HttpRequestException($"Yahoo chart {(int)status}");
            return ParseChart(body);
        }

        /// <summary>Latest trade price and session context, in one request.</summary>
        public async Task<YahooQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        {
            var chart = await GetChartByRangeAsync(symbol, "1d", "1d", ct);
            if (chart == null) return null;
            return new YahooQuote
            {
                Symbol = string.IsNullOrEmpty(chart.Symbol) ? symbol : chart.Symbol,
                Price = chart.RegularMarketPrice ?? (chart.Bars.Count > 0 ? chart.Bars[^1].Close : 0m),
                PreviousClose = chart.PreviousClose ?? 0m,
                Currency = chart.Currency ?? "USD",
                ExchangeName = chart.ExchangeName,
                MarketState = chart.MarketStateNow(DateTime.UtcNow),
                AsOfUtc = chart.RegularMarketTimeUtc ?? DateTime.UtcNow,
                DisplayName = chart.LongName,
                DayHigh = chart.DayHigh,
                DayLow = chart.DayLow,
                DayVolume = chart.DayVolume,
                FiftyTwoWeekHigh = chart.FiftyTwoWeekHigh,
                FiftyTwoWeekLow = chart.FiftyTwoWeekLow,
                InstrumentType = chart.InstrumentType,
            };
        }

        /// <summary>Search every listed symbol, not just the ones already in the instrument master.</summary>
        public async Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SymbolMatch>();
            string url = $"{SearchBase}?q={Uri.EscapeDataString(query)}&quotesCount={limit}&newsCount=0&listsCount=0";
            var (status, body) = await Session.GetAsync(url, ct);
            if ((int)status >= 400) return Array.Empty<SymbolMatch>();

            var json = JObject.Parse(body);
            var quotes = json["quotes"] as JArray ?? new JArray();
            return quotes.Select(q => new SymbolMatch
            {
                Symbol = q["symbol"]?.Value<string>() ?? "",
                DisplayName = q["shortname"]?.Value<string>() ?? q["longname"]?.Value<string>() ?? q["symbol"]?.Value<string>() ?? "",
                Exchange = q["exchDisp"]?.Value<string>(),
                Sector = q["sectorDisp"]?.Value<string>() ?? q["sector"]?.Value<string>(),
                Industry = q["industryDisp"]?.Value<string>() ?? q["industry"]?.Value<string>(),
                QuoteType = q["quoteType"]?.Value<string>(),
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
        /// Yahoo has no public stream, so this polls and emits each bar once it is complete. The poll
        /// period scales with the bar size; hammering REST faster than that only earns a rate limit.
        /// </summary>
        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var poll = TimeSpan.FromSeconds(Math.Clamp(CandleClock.Seconds(interval) / 4.0, 20, 600));
            DateTime? lastEmitted = null;
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
                        // On the first poll only the newest closed bar is new to the subscriber.
                        if (!lastEmitted.HasValue && bar.Timestamp != fetch.Closed[^1].Timestamp) continue;
                        lastEmitted = bar.Timestamp;
                        yield return bar;
                    }
                }
                try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { yield break; }
            }
        }

        private static (string Yahoo, TimeInterval Source) Native(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => ("1m", TimeInterval.OneMinute),
            TimeInterval.FiveMinute => ("5m", TimeInterval.FiveMinute),
            TimeInterval.FifteenMinute => ("15m", TimeInterval.FifteenMinute),
            TimeInterval.ThirtyMinute => ("30m", TimeInterval.ThirtyMinute),
            TimeInterval.OneHour => ("60m", TimeInterval.OneHour),
            TimeInterval.TwoHour => ("60m", TimeInterval.OneHour),
            TimeInterval.FourHour => ("60m", TimeInterval.OneHour),
            TimeInterval.TwelveHour => ("60m", TimeInterval.OneHour),
            TimeInterval.OneDay => ("1d", TimeInterval.OneDay),
            TimeInterval.OneWeek => ("1wk", TimeInterval.OneWeek),
            TimeInterval.OneMonth => ("1mo", TimeInterval.OneMonth),
            _ => ("1d", TimeInterval.OneDay)
        };

        /// <summary>Calendar time needed to collect <paramref name="count"/> bars of a session market.</summary>
        private static TimeSpan LookbackFor(TimeInterval source, TimeInterval target, int count)
        {
            double targetMinutes = CandleClock.Span(target).TotalMinutes;
            if (target is TimeInterval.OneDay) return TimeSpan.FromDays(count * 1.5 + 10);
            if (target is TimeInterval.OneWeek) return TimeSpan.FromDays(count * 7 + 14);
            if (target is TimeInterval.OneMonth) return TimeSpan.FromDays(count * 31 + 40);
            // Intraday: a regular session is 390 minutes; FX and crypto listings overshoot harmlessly.
            double barsPerDay = Math.Max(1, Math.Ceiling(390 / targetMinutes));
            double tradingDays = Math.Ceiling(count / barsPerDay) + 2;
            return TimeSpan.FromDays(tradingDays * 1.5 + 4);
        }

        private static TimeSpan MaxLookback(TimeInterval source) => source switch
        {
            TimeInterval.OneMinute => TimeSpan.FromDays(7),
            TimeInterval.FiveMinute or TimeInterval.FifteenMinute or TimeInterval.ThirtyMinute => TimeSpan.FromDays(59),
            TimeInterval.OneHour => TimeSpan.FromDays(729),
            _ => TimeSpan.FromDays(365 * 60)
        };

        private static decimal? Dec(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<decimal>(); } catch { return null; }
        }

        private static DateTime? Unix(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(t.Value<long>()).UtcDateTime; } catch { return null; }
        }
    }

    /// <summary>Parsed Yahoo chart response: bars plus the session metadata that says whether they are final.</summary>
    public sealed class YahooChart
    {
        public string Symbol { get; init; } = "";
        public string? Currency { get; init; }
        public string? ExchangeName { get; init; }
        public string? InstrumentType { get; init; }
        public string? Timezone { get; init; }
        public string? LongName { get; init; }
        public decimal? RegularMarketPrice { get; init; }
        public decimal? PreviousClose { get; init; }
        public DateTime? RegularMarketTimeUtc { get; init; }
        public DateTime? RegularSessionStartUtc { get; init; }
        public DateTime? RegularSessionEndUtc { get; init; }
        public decimal? FiftyTwoWeekHigh { get; init; }
        public decimal? FiftyTwoWeekLow { get; init; }
        public decimal? DayHigh { get; init; }
        public decimal? DayLow { get; init; }
        public decimal? DayVolume { get; init; }
        public List<OHLCCandle> Bars { get; init; } = new();
        public List<(DateTime Date, decimal Amount)> Dividends { get; } = new();
        public List<(DateTime Date, string Ratio)> Splits { get; } = new();

        /// <summary>Crypto and (most) FX listings trade around the clock; everything else keeps sessions.</summary>
        public bool IsContinuous => string.Equals(InstrumentType, "CRYPTOCURRENCY", StringComparison.OrdinalIgnoreCase);

        public string MarketStateNow(DateTime nowUtc)
        {
            if (IsContinuous) return "REGULAR";
            if (RegularSessionStartUtc is { } start && RegularSessionEndUtc is { } end)
            {
                if (nowUtc >= start && nowUtc < end) return "REGULAR";
                return nowUtc < start ? "PRE" : "CLOSED";
            }
            return "UNKNOWN";
        }
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
        public string? DisplayName { get; init; }
        public decimal? DayHigh { get; init; }
        public decimal? DayLow { get; init; }
        public decimal? DayVolume { get; init; }
        public decimal? FiftyTwoWeekHigh { get; init; }
        public decimal? FiftyTwoWeekLow { get; init; }
        public string? InstrumentType { get; init; }

        public decimal ChangePercent => PreviousClose > 0m ? (Price - PreviousClose) / PreviousClose * 100m : 0m;
    }

    public sealed class SymbolMatch
    {
        public required string Symbol { get; init; }
        public required string DisplayName { get; init; }
        public string? Exchange { get; init; }
        public AssetClass AssetClass { get; init; }
        public string Source { get; init; } = "";
        public string? Sector { get; init; }
        public string? Industry { get; init; }
        public string? QuoteType { get; init; }
        /// <summary>Set when the symbol already exists in the instrument master.</summary>
        public string? InstrumentId { get; init; }
        public List<string> TradableOn { get; init; } = new();
    }
}
