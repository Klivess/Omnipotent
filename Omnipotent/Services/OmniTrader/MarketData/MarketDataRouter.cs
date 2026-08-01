using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    public sealed class MarketDataRouter
    {
        private readonly BinanceMarketDataProvider binance = new();
        private readonly KrakenMarketDataProvider kraken = new();
        private readonly YahooMarketDataProvider yahoo = new();
        private readonly CandleCacheRepository cache;
        private readonly ConcurrentDictionary<string, StreamSubscription> activeStreams = new();

        public MarketDataRouter(CandleCacheRepository cache)
        {
            this.cache = cache;
        }

        public YahooMarketDataProvider Yahoo => yahoo;

        public Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
            => GetHistoricalCandlesAsync(symbol, interval, count, AssetClass.Unknown, ct);

        /// <summary>
        /// Bars for any symbol. Crypto comes from the exchanges; everything else — shares, ETFs,
        /// indices, FX, commodities — comes from the equities feed. <paramref name="assetClass"/> is
        /// a hint: when it is <see cref="AssetClass.Unknown"/> the symbol's own shape decides, so a
        /// caller that only has a ticker still gets the right provider.
        /// </summary>
        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count,
            AssetClass assetClass, CancellationToken ct = default)
        {
            // Live market data — chart/tick responses must never be frozen by the cache.
            CacheDeps.MarkUncacheable("market-data");

            var cached = await cache.GetLastAsync(symbol, interval, count, ct);
            if (cached.Count >= count) return cached;

            IReadOnlyList<OHLCCandle> candles;
            if (UsesEquityFeed(symbol, assetClass))
            {
                try { candles = await yahoo.GetHistoricalCandlesAsync(symbol, interval, count, ct); }
                catch { candles = Array.Empty<OHLCCandle>(); }
            }
            else
            {
                // Binance first (faster, more reliable); fall back to Kraken.
                try { candles = await binance.GetHistoricalCandlesAsync(symbol, interval, count, ct); }
                catch { candles = await kraken.GetHistoricalCandlesAsync(symbol, interval, count, ct); }
            }

            if (candles.Count > 0)
                await cache.UpsertManyAsync(symbol, interval, candles, ct);

            return candles;
        }

        public Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
            => GetLatestPriceAsync(symbol, AssetClass.Unknown, ct);

        /// <summary>Latest live price, falling back to the last cached close so a provider outage
        /// degrades to a stale-but-labelled number rather than a zero.</summary>
        public async Task<decimal> GetLatestPriceAsync(string symbol, AssetClass assetClass, CancellationToken ct = default)
        {
            CacheDeps.MarkUncacheable("market-data");
            if (UsesEquityFeed(symbol, assetClass))
            {
                try
                {
                    var quote = await yahoo.GetQuoteAsync(symbol, ct);
                    if (quote is { Price: > 0m }) return quote.Price;
                }
                catch { }
            }
            else
            {
                try
                {
                    decimal p = await binance.GetLatestPriceAsync(symbol, ct);
                    if (p > 0m) return p;
                }
                catch { }
            }

            var cached = await cache.GetLastAsync(symbol, TimeInterval.OneMinute, 1, ct);
            return cached.Count > 0 ? cached[^1].Close : 0m;
        }

        /// <summary>
        /// Which feed answers for a symbol. Crypto pairs are the exception rather than the rule
        /// here: they end in a known quote asset (`BTCUSDT`, `ETHGBP`), while equity tickers carry
        /// an exchange suffix (`VOD.L`), a caret for indices (`^FTSE`) or an `=X` for FX.
        /// </summary>
        public static bool UsesEquityFeed(string symbol, AssetClass assetClass)
        {
            if (assetClass == AssetClass.Crypto) return false;
            if (assetClass is AssetClass.Equity or AssetClass.Index or AssetClass.Forex or AssetClass.Commodity) return true;

            if (string.IsNullOrWhiteSpace(symbol)) return false;
            if (symbol.StartsWith('^') || symbol.Contains('.') || symbol.Contains("=X", StringComparison.OrdinalIgnoreCase)) return true;

            string upper = symbol.ToUpperInvariant();
            string[] cryptoQuotes = { "USDT", "USDC", "BUSD", "USD", "GBP", "EUR", "BTC", "ETH" };
            // `BTCUSDT` is crypto; `AAPL` is not — an unsuffixed ticker with no crypto quote leg
            // belongs to the equity feed.
            return !cryptoQuotes.Any(q => upper.EndsWith(q, StringComparison.Ordinal) && upper.Length > q.Length);
        }

        /// <summary>Fetch candles in a date range (Binance), caching the result. Empty on failure.</summary>
        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            IReadOnlyList<OHLCCandle> candles;
            try { candles = await binance.GetHistoricalCandlesRangeAsync(symbol, interval, fromUtc, toUtc, ct); }
            catch { candles = Array.Empty<OHLCCandle>(); }
            if (candles.Count > 0)
                await cache.UpsertManyAsync(symbol, interval, candles, ct);
            return candles;
        }

        public IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, CancellationToken ct = default)
            => StreamCandlesAsync(symbol, interval, AssetClass.Unknown, ct);

        /// <summary>
        /// Live bars for any symbol. Crypto is a genuine websocket stream; equities are polled by the
        /// equity provider, which is why the two are labelled differently in the UI rather than both
        /// being called "live".
        /// </summary>
        public IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval,
            AssetClass assetClass, CancellationToken ct = default)
        {
            IMarketDataProvider provider = UsesEquityFeed(symbol, assetClass) ? yahoo : binance;
            // Multiplex per (symbol, interval) so ten viewers of one chart cost one upstream feed.
            string key = $"{symbol.ToUpperInvariant()}|{interval}|{provider.Name}";
            var sub = activeStreams.GetOrAdd(key, _ => new StreamSubscription(provider, symbol, interval, OnTickPersist));
            return sub.SubscribeAsync(ct);
        }

        /// <summary>True when the symbol's live data is a real push stream rather than polling.</summary>
        public static bool IsStreamingLive(string symbol, AssetClass assetClass) => !UsesEquityFeed(symbol, assetClass);

        private async Task OnTickPersist(string symbol, TimeInterval interval, OHLCCandle candle)
        {
            try { await cache.UpsertManyAsync(symbol, interval, new[] { candle }); }
            catch { }
        }

        private sealed class StreamSubscription
        {
            private readonly IMarketDataProvider provider;
            private readonly string symbol;
            private readonly TimeInterval interval;
            private readonly Func<string, TimeInterval, OHLCCandle, Task> onCandle;
            private readonly object syncRoot = new();
            private readonly List<System.Threading.Channels.Channel<OHLCCandle>> subscribers = new();
            private CancellationTokenSource? sourceCts;
            private Task? producerTask;

            public StreamSubscription(IMarketDataProvider provider, string symbol, TimeInterval interval,
                Func<string, TimeInterval, OHLCCandle, Task> onCandle)
            {
                this.provider = provider;
                this.symbol = symbol;
                this.interval = interval;
                this.onCandle = onCandle;
            }

            public async IAsyncEnumerable<OHLCCandle> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
            {
                var channel = System.Threading.Channels.Channel.CreateUnbounded<OHLCCandle>();
                lock (syncRoot)
                {
                    subscribers.Add(channel);
                    if (producerTask == null)
                    {
                        sourceCts = new CancellationTokenSource();
                        producerTask = Task.Run(() => ProducerLoop(sourceCts.Token));
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
                    }
                }
            }

            private async Task ProducerLoop(CancellationToken ct)
            {
                try
                {
                    await foreach (var candle in provider.StreamCandlesAsync(symbol, interval, ct))
                    {
                        try { await onCandle(symbol, interval, candle); } catch { }
                        List<System.Threading.Channels.Channel<OHLCCandle>> snapshot;
                        lock (syncRoot)
                        {
                            snapshot = subscribers.ToList();
                        }
                        foreach (var sub in snapshot)
                            sub.Writer.TryWrite(candle);
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}
