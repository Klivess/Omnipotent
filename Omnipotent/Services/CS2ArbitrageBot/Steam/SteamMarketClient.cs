using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    /// <summary>A Steam daily/hourly median-price point (GBP).</summary>
    public readonly record struct SteamPricePoint(DateTime TimeUtc, double MedianPriceGbp, int Purchases);

    /// <summary>
    /// Anonymous Steam Community Market data: order books and price history, paced and cached.
    ///
    /// Order books come from the endpoint Steam's redesigned (May 2026) market SPA itself uses:
    /// <c>GET /market/orderbook?q=Load&amp;qp=[730,"name"]</c> with header <c>x-valve-request-type: queryAction</c>.
    /// It needs no login and no item_nameid. The legacy <c>itemordershistogram</c> is the fallback when an
    /// item_nameid is known. Lookups are coalesced per item and served from cache when fresh enough, so the
    /// snipe loop normally never waits on Steam at all.
    /// </summary>
    public sealed class SteamMarketClient : IDisposable
    {
        public const string Host = "https://steamcommunity.com";
        private const int MaxLevelsKept = 40;

        private readonly HttpClient http;
        private readonly RequestPacer pacer;
        private readonly ConcurrentDictionary<string, SteamOrderBook> cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<SteamOrderBook?>>> inflight = new(StringComparer.Ordinal);
        private readonly Func<IReadOnlyDictionary<string, double>?> usdRates;
        private readonly Func<string, int?> nameIdLookup;
        private readonly Action<string>? log;

        public long Requests;
        public long Successes;
        public long Failures;
        public long FallbackUses;
        public long CacheHits;
        public DateTime? LastSuccessUtc { get; private set; }
        public DateTime? LastFailureUtc { get; private set; }
        public string? LastError { get; private set; }
        /// <summary>Consecutive failed fetches across all items — a rising count means the source is down.</summary>
        public int ConsecutiveFailures => Volatile.Read(ref consecutiveFailures);
        private int consecutiveFailures;

        public RequestPacer Pacer => pacer;
        public int CachedCount => cache.Count;

        public SteamMarketClient(Func<IReadOnlyDictionary<string, double>?> usdRates, Func<string, int?> nameIdLookup,
            TimeSpan requestSpacing, Action<string>? log = null, HttpMessageHandler? handler = null)
        {
            this.usdRates = usdRates;
            this.nameIdLookup = nameIdLookup;
            this.log = log;
            pacer = new RequestPacer(requestSpacing, TimeSpan.FromSeconds(20));
            http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AllowAutoRedirect = false, // a redirect means the endpoint moved again; surface it, don't parse HTML
                UseCookies = false,
            });
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
        }

        public SteamOrderBook? TryGetCached(string marketHashName) =>
            cache.TryGetValue(marketHashName, out var book) ? book : null;

        public IReadOnlyCollection<SteamOrderBook> CachedBooks => cache.Values.ToList();

        /// <summary>Seeds the cache (e.g. from a snapshot saved before a restart). Newer entries win.</summary>
        public void Seed(IEnumerable<SteamOrderBook> books)
        {
            foreach (var book in books)
            {
                if (string.IsNullOrEmpty(book.MarketHashName)) continue;
                cache.AddOrUpdate(book.MarketHashName, book, (_, existing) => existing.FetchedAtUtc >= book.FetchedAtUtc ? existing : book);
            }
        }

        /// <summary>
        /// Returns a book no older than <paramref name="maxAge"/>, fetching if needed. On fetch failure the
        /// stale cached book (if any) is returned rather than null, flagged by its FetchedAtUtc.
        /// </summary>
        public async Task<SteamOrderBook?> GetOrderBookAsync(string marketHashName, TimeSpan maxAge, RequestPriority priority, CancellationToken ct = default)
        {
            if (cache.TryGetValue(marketHashName, out var cached) && cached.Age(DateTime.UtcNow) <= maxAge)
            {
                Interlocked.Increment(ref CacheHits);
                return cached;
            }
            // A pre-purchase re-check must not queue behind a coalesced background fetch of the same item.
            if (priority == RequestPriority.Critical) return await FetchAndCacheAsync(marketHashName, priority, ct);
            var lazy = inflight.GetOrAdd(marketHashName, name => new Lazy<Task<SteamOrderBook?>>(() => FetchAndCacheAsync(name, priority, ct)));
            try
            {
                return await lazy.Value.WaitAsync(ct);
            }
            finally
            {
                inflight.TryRemove(new KeyValuePair<string, Lazy<Task<SteamOrderBook?>>>(marketHashName, lazy));
            }
        }

        private async Task<SteamOrderBook?> FetchAndCacheAsync(string marketHashName, RequestPriority priority, CancellationToken ct)
        {
            SteamOrderBook? book = null;
            try
            {
                book = await FetchOrderbookAsync(marketHashName, priority, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                NoteFailure($"orderbook {marketHashName}: {ex.Message}");
            }

            if (book == null)
            {
                int? nameId = nameIdLookup(marketHashName);
                if (nameId is > 0)
                {
                    try
                    {
                        book = await FetchHistogramAsync(marketHashName, nameId.Value, priority, ct);
                        if (book != null) Interlocked.Increment(ref FallbackUses);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        NoteFailure($"histogram {marketHashName}: {ex.Message}");
                    }
                }
            }

            if (book == null)
            {
                Interlocked.Increment(ref Failures);
                Interlocked.Increment(ref consecutiveFailures);
                return cache.TryGetValue(marketHashName, out var stale) ? stale : null;
            }

            Trim(book);
            cache[marketHashName] = book;
            Interlocked.Increment(ref Successes);
            Interlocked.Exchange(ref consecutiveFailures, 0);
            LastSuccessUtc = DateTime.UtcNow;
            return book;
        }

        private async Task<SteamOrderBook?> FetchOrderbookAsync(string marketHashName, RequestPriority priority, CancellationToken ct)
        {
            string url = Host + "/market/orderbook?q=Load&qp=" + Uri.EscapeDataString(JsonConvert.SerializeObject(new object[] { 730, marketHashName }));
            string? body = await GetAsync(url, priority, queryAction: true, ct);
            if (body == null) return null;
            var book = SteamOrderBook.ParseOrderbookResponse(body, marketHashName, ToGbpPence, DateTime.UtcNow);
            if (book == null) NoteFailure($"orderbook {marketHashName}: unsuccessful or unconvertible response");
            return book;
        }

        private async Task<SteamOrderBook?> FetchHistogramAsync(string marketHashName, int nameId, RequestPriority priority, CancellationToken ct)
        {
            string url = $"{Host}/market/itemordershistogram?country=GB&language=english&currency={SteamCurrency.GBP}&item_nameid={nameId}&two_factor=0";
            string? body = await GetAsync(url, priority, queryAction: false, ct);
            return body == null ? null : SteamOrderBook.ParseHistogramResponse(body, marketHashName, SteamCurrency.GBP, ToGbpPence, DateTime.UtcNow);
        }

        /// <summary>Hourly/daily median sale prices (GBP) via the SPA's QueryPriceHistory action.</summary>
        public async Task<List<SteamPricePoint>?> GetPriceHistoryAsync(string marketHashName, RequestPriority priority, CancellationToken ct = default)
        {
            string url = Host + "/market/actions?q=QueryPriceHistory&qp=" + Uri.EscapeDataString(JsonConvert.SerializeObject(new object[] { 730, marketHashName }));
            string? body;
            try { body = await GetAsync(url, priority, queryAction: true, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { NoteFailure($"pricehistory {marketHashName}: {ex.Message}"); return null; }
            return body == null ? null : ParsePriceHistory(body, ToGbpPence);
        }

        public static List<SteamPricePoint> ParsePriceHistory(string body, Func<int, int, int?> toGbpPence)
        {
            var points = new List<SteamPricePoint>();
            JToken root = JToken.Parse(body);
            JToken? data = root["data"] ?? root;
            if (data?["prices"] is not JArray prices) return points;
            int currency = data.Value<int?>("ecurrency") ?? SteamCurrency.GBP;
            foreach (var p in prices.OfType<JObject>())
            {
                long time = p.Value<long?>("time") ?? 0;
                double median = p.Value<double?>("price_median") ?? 0;
                if (time <= 0 || median <= 0) continue;
                int? pence = toGbpPence((int)Math.Round(median * 100), currency);
                if (pence is not > 0) continue;
                points.Add(new SteamPricePoint(DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime, pence.Value / 100.0, p.Value<int?>("purchases") ?? 0));
            }
            return points;
        }

        private async Task<string?> GetAsync(string url, RequestPriority priority, bool queryAction, CancellationToken ct)
        {
            await pacer.WaitTurnAsync(priority, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (queryAction) request.Headers.TryAddWithoutValidation("x-valve-request-type", "queryAction");
            Interlocked.Increment(ref Requests);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                pacer.ReportThrottled(response.Headers.RetryAfter?.Delta);
                NoteFailure("Steam returned 429 (rate limited)");
                return null;
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                NoteFailure($"Steam redirected {Shorten(url)} to {response.Headers.Location} — endpoint moved?");
                return null;
            }
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                NoteFailure($"Steam {(int)response.StatusCode} for {Shorten(url)}");
                return null;
            }
            if (body.TrimStart().StartsWith('<'))
            {
                NoteFailure($"Steam answered HTML instead of JSON for {Shorten(url)}");
                return null;
            }
            pacer.ReportSuccess();
            return body;
        }

        private int? ToGbpPence(int minorUnits, int currency) => SteamCurrency.ToGbpPence(minorUnits, currency, usdRates());

        private static void Trim(SteamOrderBook book)
        {
            if (book.BuyLevels.Count > MaxLevelsKept) book.BuyLevels = book.BuyLevels.Take(MaxLevelsKept).ToList();
            if (book.SellLevels.Count > MaxLevelsKept) book.SellLevels = book.SellLevels.Take(MaxLevelsKept).ToList();
        }

        private void NoteFailure(string message)
        {
            LastFailureUtc = DateTime.UtcNow;
            LastError = message;
            log?.Invoke(message);
        }

        private static string Shorten(string url) => url.Length <= 140 ? url : url[..140] + "…";

        public void Dispose()
        {
            pacer.Dispose();
            http.Dispose();
        }
    }
}
