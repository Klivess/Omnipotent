using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// One entry point for bars and prices on any symbol.
    ///
    /// Crypto: Binance → Coinbase → Kraken. Everything else: Yahoo. Closed bars are cached in SQLite
    /// and topped up when the newest cached bar is older than the last bar that has closed; the
    /// forming bar is never cached and is only returned by <see cref="GetChartCandlesAsync"/>.
    /// </summary>
    public sealed class MarketDataRouter
    {
        private readonly BinanceMarketDataProvider binance = new();
        private readonly CoinbaseMarketDataProvider coinbase = new();
        private readonly KrakenMarketDataProvider kraken = new();
        private readonly YahooMarketDataProvider yahoo;
        private readonly CandleCacheRepository cache;
        private readonly ConcurrentDictionary<string, StreamSubscription> activeStreams = new();
        private readonly ConcurrentDictionary<string, DateTime> lastRefreshUtc = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> lastSource = new(StringComparer.Ordinal);
        private readonly SingleFlight<CandleFetch> fetches = new();
        private readonly SemaphoreSlim yahooGate = new(6, 6);

        public MarketDataRouter(CandleCacheRepository cache, YahooSession? yahooSession = null)
        {
            this.cache = cache;
            yahoo = new YahooMarketDataProvider(yahooSession);
        }

        public YahooMarketDataProvider Yahoo => yahoo;
        public BinanceMarketDataProvider Binance => binance;
        public CandleCacheRepository Cache => cache;

        /// <summary>The provider that last answered for a symbol/interval ("Binance", "Coinbase", "yahoo"…).</summary>
        public string? SourceFor(string symbol, TimeInterval interval)
            => lastSource.TryGetValue(Key(symbol, interval), out var s) ? s : null;

        // ── closed bars ───────────────────────────────────────────────────────────

        public Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
            => GetHistoricalCandlesAsync(symbol, interval, count, AssetClass.Unknown, ct);

        /// <summary>The most recent <paramref name="count"/> CLOSED bars. What strategies, backtests
        /// and analytics consume.</summary>
        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count,
            AssetClass assetClass, CancellationToken ct = default)
        {
            CacheDeps.MarkUncacheable("market-data");
            if (string.IsNullOrWhiteSpace(symbol) || count <= 0) return Array.Empty<OHLCCandle>();
            count = Math.Min(count, 50_000);

            bool equity = UsesEquityFeed(symbol, assetClass);
            var cached = await cache.GetLastAsync(symbol, interval, count, ct);
            if (IsFresh(symbol, interval, cached, count, equity)) return cached;

            // A long-enough crypto series that has merely gone stale only needs its tail.
            if (!equity && cached.Count >= count && cached.Count > 0)
            {
                var expected = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
                long missing = (long)Math.Ceiling((expected - cached[^1].Timestamp).TotalSeconds / CandleClock.Seconds(interval));
                if (missing is >= 0 and < 1000)
                {
                    var tail = await FetchAsync(symbol, interval, (int)missing + 3, equity, ct);
                    if (tail.Closed.Count > 0)
                    {
                        await cache.UpsertManyAsync(symbol, interval, tail.Closed, ct);
                        MarkRefreshed(symbol, interval);
                        var merged = await cache.GetLastAsync(symbol, interval, count, ct);
                        if (merged.Count >= cached.Count) return merged;
                    }
                }
            }

            var fetch = await FetchAsync(symbol, interval, count, equity, ct);
            if (fetch.Closed.Count == 0) return cached;
            await cache.UpsertManyAsync(symbol, interval, fetch.Closed, ct);
            MarkRefreshed(symbol, interval);
            return fetch.Closed.Count >= cached.Count ? fetch.Closed : await cache.GetLastAsync(symbol, interval, count, ct);
        }

        /// <summary>Closed bars plus the bar still forming, for charts and live quotes.</summary>
        public async Task<CandleFetch> GetChartCandlesAsync(string symbol, TimeInterval interval, int count,
            AssetClass assetClass = AssetClass.Unknown, CancellationToken ct = default)
        {
            CacheDeps.MarkUncacheable("market-data");
            bool equity = UsesEquityFeed(symbol, assetClass);
            var closed = await GetHistoricalCandlesAsync(symbol, interval, count, assetClass, ct);
            OHLCCandle? forming = null;
            try
            {
                // The forming bar is the only part that needs a live call; two bars covers a close
                // that happened between the cache read and now.
                var live = await FetchAsync(symbol, interval, 2, equity, ct);
                if (live.Closed.Count > 0)
                {
                    var newer = live.Closed.Where(b => closed.Count == 0 || b.Timestamp > closed[^1].Timestamp).ToList();
                    if (newer.Count > 0)
                    {
                        await cache.UpsertManyAsync(symbol, interval, newer, ct);
                        closed = closed.Concat(newer).TakeLast(count).ToList();
                    }
                }
                forming = live.Forming;
                if (forming is { } f && closed.Count > 0 && f.Timestamp <= closed[^1].Timestamp) forming = null;
            }
            catch { /* closed bars are still worth serving */ }
            return new CandleFetch(closed, forming, SourceFor(symbol, interval) ?? (equity ? yahoo.Name : binance.Name));
        }

        /// <summary>Closed bars whose open falls in [from, to]. Crypto ranges are cache-first.</summary>
        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesRangeAsync(string symbol, TimeInterval interval,
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
            => await GetHistoricalCandlesRangeAsync(symbol, interval, fromUtc, toUtc, AssetClass.Unknown, ct);

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesRangeAsync(string symbol, TimeInterval interval,
            DateTime fromUtc, DateTime toUtc, AssetClass assetClass, CancellationToken ct = default)
        {
            CacheDeps.MarkUncacheable("market-data");
            fromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            toUtc = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
            if (toUtc <= fromUtc) return Array.Empty<OHLCCandle>();
            bool equity = UsesEquityFeed(symbol, assetClass);

            if (!equity)
            {
                var cached = await cache.GetRangeAsync(symbol, interval, fromUtc, toUtc, ct);
                var lastClosed = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
                var end = toUtc < lastClosed ? toUtc : lastClosed;
                double expected = Math.Max(1, (end - CandleClock.BarOpen(fromUtc, interval)).TotalSeconds / CandleClock.Seconds(interval));
                if (cached.Count > 0 && cached.Count >= expected * 0.995
                    && cached[0].Timestamp <= CandleClock.NextOpen(CandleClock.BarOpen(fromUtc, interval), interval)
                    && cached[^1].Timestamp >= CandleClock.PreviousOpen(end, interval))
                    return cached;
            }

            CandleFetch fetch = CandleFetch.Empty("none");
            foreach (var source in SourcesFor(equity, interval))
            {
                if (SourceHealth.IsBackingOff(source.Name)) continue;
                try
                {
                    fetch = await SourceHealth.TrackAsync(source.Name, "market-data",
                        () => GateAsync(source, () => source.FetchRangeAsync(symbol, interval, fromUtc, toUtc, ct)));
                    if (fetch.Closed.Count > 0) { lastSource[Key(symbol, interval)] = source.Name; break; }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            if (fetch.Closed.Count > 0) await cache.UpsertManyAsync(symbol, interval, fetch.Closed, ct);
            return fetch.Closed;
        }

        private bool IsFresh(string symbol, TimeInterval interval, List<OHLCCandle> cached, int count, bool equity)
        {
            if (cached.Count < count || cached.Count == 0) return false;
            if (!equity)
                return cached[^1].Timestamp >= CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            // Session markets have no clock to compare against, so recency of the last refresh decides.
            return lastRefreshUtc.TryGetValue(Key(symbol, interval), out var at)
                && DateTime.UtcNow - at < EquityRefreshTtl(interval);
        }

        private static TimeSpan EquityRefreshTtl(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => TimeSpan.FromSeconds(30),
            TimeInterval.FiveMinute => TimeSpan.FromMinutes(1),
            TimeInterval.FifteenMinute or TimeInterval.ThirtyMinute => TimeSpan.FromMinutes(3),
            TimeInterval.OneHour or TimeInterval.TwoHour or TimeInterval.FourHour => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(15)
        };

        private void MarkRefreshed(string symbol, TimeInterval interval) => lastRefreshUtc[Key(symbol, interval)] = DateTime.UtcNow;

        /// <summary>Latest-N bars from the first source that answers. Concurrent identical calls share one request.</summary>
        private Task<CandleFetch> FetchAsync(string symbol, TimeInterval interval, int count, bool equity, CancellationToken ct)
            => fetches.RunAsync($"{Key(symbol, interval)}|{count}", async () =>
            {
                foreach (var source in SourcesFor(equity, interval))
                {
                    if (SourceHealth.IsBackingOff(source.Name)) continue;
                    try
                    {
                        var fetch = await SourceHealth.TrackAsync(source.Name, "market-data",
                            () => GateAsync(source, () => source.FetchLatestAsync(symbol, interval, count, ct)));
                        if (fetch.HasData)
                        {
                            lastSource[Key(symbol, interval)] = source.Name;
                            return fetch;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                return CandleFetch.Empty("none");
            });

        private async Task<CandleFetch> GateAsync(ICandleSource source, Func<Task<CandleFetch>> call)
        {
            if (source != yahoo) return await call();
            await yahooGate.WaitAsync();
            try { return await call(); }
            finally { yahooGate.Release(); }
        }

        private IEnumerable<ICandleSource> SourcesFor(bool equity, TimeInterval interval)
        {
            if (equity)
            {
                yield return yahoo;
                yield break;
            }
            yield return binance;
            yield return coinbase;
            if (kraken.Supports(interval)) yield return kraken;
        }

        // ── prices ────────────────────────────────────────────────────────────────

        public Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
            => GetLatestPriceAsync(symbol, AssetClass.Unknown, ct);

        /// <summary>Latest live price, falling back to the newest cached close so an outage degrades
        /// to a stale number rather than a zero.</summary>
        public async Task<decimal> GetLatestPriceAsync(string symbol, AssetClass assetClass, CancellationToken ct = default)
        {
            CacheDeps.MarkUncacheable("market-data");
            if (UsesEquityFeed(symbol, assetClass))
            {
                try
                {
                    var quote = await SourceHealth.TrackAsync("yahoo", "market-data", () => yahoo.GetQuoteAsync(symbol, ct));
                    if (quote is { Price: > 0m }) return quote.Price;
                }
                catch { }
            }
            else
            {
                if (!SourceHealth.IsBackingOff(binance.Name))
                {
                    try
                    {
                        decimal p = await SourceHealth.TrackAsync(binance.Name, "market-data", () => binance.GetLatestPriceAsync(symbol, ct));
                        if (p > 0m) return p;
                    }
                    catch { }
                }
                try
                {
                    var live = await FetchAsync(symbol, TimeInterval.OneMinute, 2, equity: false, ct);
                    if (live.Forming is { } f) return f.Close;
                    if (live.Closed.Count > 0) return live.Closed[^1].Close;
                }
                catch { }
            }

            foreach (var interval in new[] { TimeInterval.OneMinute, TimeInterval.OneHour, TimeInterval.OneDay })
            {
                var cached = await cache.GetLastAsync(symbol, interval, 1, ct);
                if (cached.Count > 0) return cached[^1].Close;
            }
            return 0m;
        }

        /// <summary>
        /// Which feed answers for a symbol. Crypto pairs end in a known quote asset (`BTCUSDT`,
        /// `ETHGBP`); equity tickers carry an exchange suffix (`VOD.L`), a caret (`^FTSE`), `=X`
        /// (FX), `=F` (futures) or a bare ticker (`AAPL`). Yahoo-style crypto (`BTC-USD`) goes to Yahoo.
        /// </summary>
        public static bool UsesEquityFeed(string symbol, AssetClass assetClass)
        {
            if (assetClass == AssetClass.Crypto) return false;
            if (assetClass is AssetClass.Equity or AssetClass.Index or AssetClass.Forex or AssetClass.Commodity) return true;

            if (string.IsNullOrWhiteSpace(symbol)) return false;
            if (symbol.StartsWith('^') || symbol.Contains('.') || symbol.Contains('=') || symbol.Contains('-')) return true;

            string upper = symbol.ToUpperInvariant();
            string[] cryptoQuotes = { "USDT", "USDC", "FDUSD", "BUSD", "USD", "GBP", "EUR", "BTC", "ETH", "BNB", "TRY", "BRL" };
            return !cryptoQuotes.Any(q => upper.EndsWith(q, StringComparison.Ordinal) && upper.Length > q.Length + 1);
        }

        /// <summary>True when the symbol's live data is a push stream rather than polling.</summary>
        public static bool IsStreamingLive(string symbol, AssetClass assetClass) => !UsesEquityFeed(symbol, assetClass);

        // ── streams ───────────────────────────────────────────────────────────────

        public IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, CancellationToken ct = default)
            => StreamCandlesAsync(symbol, interval, AssetClass.Unknown, ct);

        /// <summary>
        /// Closed bars as they complete. Crypto uses the Binance websocket while Binance is healthy and
        /// falls back to polling Coinbase/Kraken; equities are polled. One upstream feed per
        /// (symbol, interval) however many subscribers there are.
        /// </summary>
        public IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval,
            AssetClass assetClass, CancellationToken ct = default)
        {
            bool equity = UsesEquityFeed(symbol, assetClass);
            string mode = equity ? "yahoo" : "crypto";
            string key = $"{symbol.ToUpperInvariant()}|{interval}|{mode}";
            var sub = activeStreams.GetOrAdd(key, _ => new StreamSubscription(
                token => equity ? yahoo.StreamCandlesAsync(symbol, interval, token) : CryptoStreamAsync(symbol, interval, token),
                candle => OnTickPersist(symbol, interval, candle)));
            return sub.SubscribeAsync(ct);
        }

        private async IAsyncEnumerable<OHLCCandle> CryptoStreamAsync(string symbol, TimeInterval interval,
            [EnumeratorCancellation] CancellationToken ct)
        {
            DateTime? last = null;
            while (!ct.IsCancellationRequested)
            {
                if (!SourceHealth.IsBackingOff(BinanceMarketDataProvider.WebSocketSource) && !SourceHealth.IsBackingOff(binance.Name))
                {
                    await foreach (var bar in binance.StreamCandlesAsync(symbol, interval, ct).WithCancellation(ct))
                    {
                        if (last.HasValue && bar.Timestamp <= last.Value) continue;
                        last = bar.Timestamp;
                        yield return bar;
                    }
                    if (ct.IsCancellationRequested) yield break;
                }

                // Websocket unavailable: poll the REST chain until Binance recovers.
                CandleFetch? fetch = null;
                try { fetch = await FetchAsync(symbol, interval, 3, equity: false, ct); }
                catch (OperationCanceledException) { yield break; }
                catch { }
                if (fetch != null)
                {
                    foreach (var bar in fetch.Closed)
                    {
                        if (last.HasValue && bar.Timestamp <= last.Value) continue;
                        if (!last.HasValue && bar.Timestamp != fetch.Closed[^1].Timestamp) continue;
                        last = bar.Timestamp;
                        yield return bar;
                    }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(CandleClock.Seconds(interval) / 6, 15, 300)), ct); }
                catch (OperationCanceledException) { yield break; }
            }
        }

        private async Task OnTickPersist(string symbol, TimeInterval interval, OHLCCandle candle)
        {
            try
            {
                await cache.UpsertManyAsync(symbol, interval, new[] { candle });
                MarkRefreshed(symbol, interval);
            }
            catch { }
        }

        private static string Key(string symbol, TimeInterval interval) => $"{symbol.ToUpperInvariant()}|{interval}";

        private sealed class StreamSubscription
        {
            private readonly Func<CancellationToken, IAsyncEnumerable<OHLCCandle>> producer;
            private readonly Func<OHLCCandle, Task> onCandle;
            private readonly object syncRoot = new();
            private readonly List<System.Threading.Channels.Channel<OHLCCandle>> subscribers = new();
            private CancellationTokenSource? sourceCts;
            private Task? producerTask;

            public StreamSubscription(Func<CancellationToken, IAsyncEnumerable<OHLCCandle>> producer, Func<OHLCCandle, Task> onCandle)
            {
                this.producer = producer;
                this.onCandle = onCandle;
            }

            public async IAsyncEnumerable<OHLCCandle> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
            {
                var channel = System.Threading.Channels.Channel.CreateUnbounded<OHLCCandle>();
                lock (syncRoot)
                {
                    subscribers.Add(channel);
                    if (producerTask == null || producerTask.IsCompleted)
                    {
                        sourceCts = new CancellationTokenSource();
                        var token = sourceCts.Token;
                        producerTask = Task.Run(() => ProducerLoop(token));
                    }
                }

                try
                {
                    await foreach (var candle in channel.Reader.ReadAllAsync(ct))
                        yield return candle;
                }
                finally
                {
                    lock (syncRoot)
                    {
                        subscribers.Remove(channel);
                        channel.Writer.TryComplete();
                        // The last subscriber leaving stops the upstream feed.
                        if (subscribers.Count == 0)
                        {
                            try { sourceCts?.Cancel(); } catch { }
                            producerTask = null;
                        }
                    }
                }
            }

            private async Task ProducerLoop(CancellationToken ct)
            {
                try
                {
                    await foreach (var candle in producer(ct).WithCancellation(ct))
                    {
                        try { await onCandle(candle); } catch { }
                        List<System.Threading.Channels.Channel<OHLCCandle>> snapshot;
                        lock (syncRoot) snapshot = subscribers.ToList();
                        foreach (var sub in snapshot) sub.Writer.TryWrite(candle);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }
        }
    }
}
