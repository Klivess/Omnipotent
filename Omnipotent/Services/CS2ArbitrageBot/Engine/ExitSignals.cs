using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Collections.Concurrent;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>Why the signals are wanted; sets how fresh each one must be and how much budget may be spent.</summary>
    public enum SignalUse
    {
        /// <summary>Confirming a purchase (latency matters: the listing can be sniped by someone else).</summary>
        Purchase,
        /// <summary>Deciding how to sell once trade protection lifts.</summary>
        Sale,
        /// <summary>Re-checking a live CSFloat relist.</summary>
        Review,
    }

    /// <summary>The market evidence behind one exit decision. Missing pieces are listed, never guessed.</summary>
    public sealed class ExitSignals
    {
        public string MarketHashName { get; set; } = "";
        public DateTime FetchedAtUtc { get; set; }
        public SteamOrderBook? Book { get; set; }
        public List<SteamPricePoint>? SteamHistory { get; set; }
        public List<CSFloatWrapper.SalesGraphPoint>? SalesGraph { get; set; }
        public List<CSFloatWrapper.RecentSale>? RecentSales { get; set; }
        public List<CSFloatListing>? Competitors { get; set; }
        public List<string> Missing { get; } = new();

        /// <summary>True when there is real sales evidence for the CSFloat exit (not just a proxy).</summary>
        public bool HasCSFloatDemand => SalesGraph != null || RecentSales != null;

        /// <summary>CSFloat's current base value for the item, from competing listings or failing that the latest sale.</summary>
        public int? ReferenceBaseCents =>
            Competitors?.Select(c => c.BasePriceCents).FirstOrDefault(v => v is > 0)
            ?? RecentSales?.OrderByDescending(s => s.SoldAtUtc).Select(s => s.BasePriceCents).FirstOrDefault(v => v is > 0);

        public int? ReferenceQuantity => Competitors?.Select(c => c.ReferenceQuantity).FirstOrDefault(v => v is > 0);
    }

    /// <summary>
    /// Fetches and caches the signals the exit model needs. Every CSFloat call is budgeted: the per-sale and
    /// daily histories allow 500 requests a day each, and listing searches share the feed's 200 an hour, so
    /// each source keeps a reserve back and is skipped (and reported missing) rather than starving the feed.
    /// </summary>
    public sealed class ExitSignalsProvider
    {
        private sealed record Policy(TimeSpan Book, TimeSpan History, TimeSpan Graph, TimeSpan Sales, TimeSpan Competitors,
            int HistoryReserve, int SalesReserve, int ListingsReserve, RequestPriority Priority, TimeSpan Timeout);

        private static readonly Dictionary<SignalUse, Policy> Policies = new()
        {
            [SignalUse.Purchase] = new(TimeSpan.FromSeconds(90), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(6), TimeSpan.FromMinutes(10), 60, 40, 12, RequestPriority.Critical, TimeSpan.FromSeconds(6)),
            [SignalUse.Sale] = new(TimeSpan.FromMinutes(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5), 20, 20, 8, RequestPriority.Normal, TimeSpan.FromSeconds(45)),
            [SignalUse.Review] = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(12), TimeSpan.FromHours(24), TimeSpan.FromHours(6), TimeSpan.FromMinutes(20), 40, 40, 10, RequestPriority.Low, TimeSpan.FromSeconds(60)),
        };

        private readonly CSFloatWrapper csfloat;
        private readonly SteamMarketClient steam;
        private readonly Func<DateTime> utcNow;
        private readonly ConcurrentDictionary<string, (DateTime At, List<SteamPricePoint> Value)> histories = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime At, List<CSFloatWrapper.SalesGraphPoint> Value)> graphs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime At, List<CSFloatWrapper.RecentSale> Value)> sales = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime At, List<CSFloatListing> Value)> competitors = new(StringComparer.Ordinal);

        public long Fetches;
        public long CacheHits;
        public long SkippedForBudget;

        public ExitSignalsProvider(CSFloatWrapper csfloat, SteamMarketClient steam, Func<DateTime>? utcNow = null)
        {
            this.csfloat = csfloat;
            this.steam = steam;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Gathers the signals for one item. <paramref name="knownBook"/> (e.g. the purchase re-check's) is used
        /// as is when fresh enough. Never throws for a missing source; cancellation still propagates.
        /// </summary>
        public async Task<ExitSignals> GetAsync(string marketHashName, SignalUse use, CancellationToken ct, SteamOrderBook? knownBook = null)
        {
            var policy = Policies[use];
            var signals = new ExitSignals { MarketHashName = marketHashName, FetchedAtUtc = utcNow() };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(policy.Timeout);
            var token = timeout.Token;

            Task<SteamOrderBook?> book = knownBook != null && knownBook.Age(utcNow()) <= policy.Book
                ? Task.FromResult<SteamOrderBook?>(knownBook)
                : Guard(signals, "Steam order book", () => steam.GetOrderBookAsync(marketHashName, policy.Book, policy.Priority, token), ct);
            var history = Cached(histories, marketHashName, policy.History, signals, "Steam price history", null, 0,
                async () => await steam.GetPriceHistoryAsync(marketHashName, policy.Priority, token), ct);
            var graph = Cached(graphs, marketHashName, policy.Graph, signals, "CSFloat daily sales", CSFloatWrapper.HistoryBucket, policy.HistoryReserve,
                async () => await csfloat.GetSalesGraphAsync(marketHashName, token), ct);
            var recent = Cached(sales, marketHashName, policy.Sales, signals, "CSFloat recent sales", CSFloatWrapper.HistorySalesBucket, policy.SalesReserve,
                async () => await csfloat.GetRecentSalesAsync(marketHashName, token), ct);
            var rivals = Cached(competitors, marketHashName, policy.Competitors, signals, "CSFloat competing listings", CSFloatWrapper.ListingsBucket, policy.ListingsReserve,
                async () => (await csfloat.SearchListingsAsync(new CSFloatWrapper.ListingQuery { SortBy = "lowest_price", Limit = 50, MarketHashName = marketHashName }, token)).Listings, ct);

            await Task.WhenAll(book, history, graph, recent, rivals);
            signals.Book = await book;
            signals.SteamHistory = await history;
            signals.SalesGraph = await graph;
            signals.RecentSales = await recent;
            signals.Competitors = await rivals;
            if (signals.Book == null && !signals.Missing.Any(m => m.StartsWith("Steam order book", StringComparison.Ordinal))) signals.Missing.Add("Steam order book: unavailable");
            return signals;
        }

        /// <summary>Forgets cached competition for an item (after our own listing changed it).</summary>
        public void InvalidateCompetition(string marketHashName) => competitors.TryRemove(marketHashName, out _);

        private async Task<T?> Guard<T>(ExitSignals signals, string what, Func<Task<T?>> fetch, CancellationToken outer) where T : class
        {
            try
            {
                Interlocked.Increment(ref Fetches);
                var value = await fetch();
                if (value == null) lock (signals.Missing) signals.Missing.Add($"{what}: unavailable");
                return value;
            }
            catch (OperationCanceledException) when (!outer.IsCancellationRequested)
            {
                lock (signals.Missing) signals.Missing.Add($"{what}: timed out");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (signals.Missing) signals.Missing.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        private async Task<List<T>?> Cached<T>(ConcurrentDictionary<string, (DateTime At, List<T> Value)> cache, string key, TimeSpan maxAge,
            ExitSignals signals, string what, string? bucket, int reserve, Func<Task<List<T>?>> fetch, CancellationToken outer)
        {
            if (cache.TryGetValue(key, out var hit) && utcNow() - hit.At <= maxAge)
            {
                Interlocked.Increment(ref CacheHits);
                return hit.Value;
            }
            if (bucket != null && !csfloat.RateLimits.CanSpend(bucket, reserve))
            {
                Interlocked.Increment(ref SkippedForBudget);
                lock (signals.Missing) signals.Missing.Add($"{what}: rate budget reserved");
                return hit.Value; // a stale copy beats nothing; null when there is none
            }
            var value = await Guard(signals, what, fetch, outer);
            if (value != null) cache[key] = (utcNow(), value);
            return value ?? hit.Value;
        }
    }
}
