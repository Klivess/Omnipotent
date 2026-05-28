using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    public sealed class MarketDataRouter
    {
        private readonly BinanceMarketDataProvider binance = new();
        private readonly KrakenMarketDataProvider kraken = new();
        private readonly CandleCacheRepository cache;
        private readonly ConcurrentDictionary<string, StreamSubscription> activeStreams = new();

        public MarketDataRouter(CandleCacheRepository cache)
        {
            this.cache = cache;
        }

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            // 1. Try cache.
            var cached = await cache.GetLastAsync(symbol, interval, count, ct);
            if (cached.Count >= count) return cached;

            // 2. Fetch from Binance first (faster, more reliable); fall back to Kraken.
            IReadOnlyList<OHLCCandle> candles;
            try
            {
                candles = await binance.GetHistoricalCandlesAsync(symbol, interval, count, ct);
            }
            catch
            {
                candles = await kraken.GetHistoricalCandlesAsync(symbol, interval, count, ct);
            }

            if (candles.Count > 0)
                await cache.UpsertManyAsync(symbol, interval, candles, ct);

            return candles;
        }

        public IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, CancellationToken ct = default)
        {
            // Multiplex per (symbol, interval).
            string key = $"{symbol.ToUpperInvariant()}|{interval}";
            var sub = activeStreams.GetOrAdd(key, _ => new StreamSubscription(binance, symbol, interval, OnTickPersist));
            return sub.SubscribeAsync(ct);
        }

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
