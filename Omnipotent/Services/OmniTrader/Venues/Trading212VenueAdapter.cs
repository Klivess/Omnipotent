using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using System.Net;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Trading 212 (Invest / ISA): owned shares and ETFs.
    ///
    /// Two things shape this adapter. First, T212 sells you the *asset* — a share you hold, like
    /// Kraken spot — so it reports <see cref="ExposureKind.Inventory"/> and its value belongs in the
    /// firm's assets rather than as notional exposure. Second, its public API is deliberately narrow:
    /// it has no historical-bar endpoint and a hard rate limit, so charts come from the shared market
    /// data router and every capability it lacks is declared in <see cref="VenueCapabilities.Limitations"/>
    /// rather than simulated.
    ///
    /// Demo and live are separate instances with separate keys and separate base URLs, so a demo
    /// instruction can never be issued against the live account.
    /// </summary>
    public sealed class Trading212VenueAdapter : IVenueAdapter
    {
        public const string LiveBase = "https://live.trading212.com/api/v0";
        public const string DemoBase = "https://demo.trading212.com/api/v0";

        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string authorization;
        private readonly string baseUrl;
        private readonly MarketDataRouter marketData;
        private readonly VenueHealthSnapshot health;

        // T212 rate-limits aggressively (as little as one call per few seconds on some endpoints), so
        // the instrument directory — thousands of rows that change rarely — is cached in memory.
        private IReadOnlyList<VenueInstrumentDescriptor>? instrumentCache;
        private DateTime instrumentCacheUtc;
        private string? accountCurrency;
        private string? accountNumber;

        private readonly SemaphoreSlim paceLock = new(1, 1);
        private readonly Dictionary<string, DateTime> lastCallUtc = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The value of the <c>Authorization</c> header for a key, with or without a secret.</summary>
        internal static string BuildAuthorization(string apiKey, string? apiSecret)
            => string.IsNullOrWhiteSpace(apiSecret)
                ? apiKey
                : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));

        /// <summary>
        /// The documented limit for an endpoint, and the bucket it shares with its siblings.
        ///
        /// The limits are strict enough that a naive poll trips them — the instrument directory
        /// allows one call every fifty seconds — and a 429 costs a whole refresh cycle, so the
        /// adapter paces itself rather than discovering the limit by hitting it. They vary by an
        /// order of magnitude *within* a path, though: listing orders is one call per five seconds
        /// while placing a market order is fifty a minute. Matching on a prefix alone would throttle
        /// execution to the speed of a report, so each endpoint is classified on its own.
        /// </summary>
        internal static (string Bucket, TimeSpan MinInterval)? LimitFor(string path)
        {
            if (path.StartsWith("/equity/metadata/instruments", StringComparison.OrdinalIgnoreCase))
                return ("instruments", TimeSpan.FromSeconds(50));
            if (path.StartsWith("/equity/metadata/exchanges", StringComparison.OrdinalIgnoreCase))
                return ("exchanges", TimeSpan.FromSeconds(30));
            if (path.StartsWith("/equity/account/summary", StringComparison.OrdinalIgnoreCase))
                return ("summary", TimeSpan.FromSeconds(5));
            if (path.StartsWith("/equity/positions", StringComparison.OrdinalIgnoreCase))
                return ("positions", TimeSpan.FromSeconds(1));

            if (path.StartsWith("/equity/orders", StringComparison.OrdinalIgnoreCase))
            {
                string tail = path["/equity/orders".Length..].Trim('/');
                return tail switch
                {
                    // Placing an order: fast, and never queued behind a listing call.
                    "market" => ("order-place", TimeSpan.FromMilliseconds(1250)),
                    "limit" or "stop" or "stop_limit" => ("order-place", TimeSpan.FromSeconds(2)),
                    // The whole book.
                    "" => ("order-list", TimeSpan.FromSeconds(5)),
                    // A single order by id — how an ambiguous submission gets resolved, so it has
                    // to stay quick enough to walk a handful of them.
                    _ => ("order-read", TimeSpan.FromSeconds(1))
                };
            }
            return null;
        }

        /// <summary>Open while T212 keeps rejecting this key — usually a practice key pointed at the
        /// live endpoint, or the reverse. Retrying cannot fix either.</summary>
        public AuthCircuitBreaker Auth { get; } = new();

        public VenueId Venue => VenueId.Trading212;
        public TradingEnvironment Environment { get; }
        public bool IsConfigured { get; private set; }
        public VenueHealthSnapshot Health => health;

        /// <summary>
        /// Trading 212 authenticates with an API key *pair*: the key is the username and the secret is
        /// the password of an HTTP Basic header. Older keys were a single opaque token sent raw, and
        /// T212 still accepts that (their `legacyApiKeyHeader` scheme), so a configuration with no
        /// secret falls back to it rather than failing — but a key/secret pair is what the current
        /// documentation describes and what new keys are issued as.
        /// </summary>
        public Trading212VenueAdapter(string apiKey, string? apiSecret, TradingEnvironment environment, MarketDataRouter marketData)
        {
            authorization = BuildAuthorization(apiKey, apiSecret);
            this.marketData = marketData;
            Environment = environment;
            baseUrl = environment == TradingEnvironment.Live ? LiveBase : DemoBase;
            health = new VenueHealthSnapshot
            {
                Venue = VenueId.Trading212,
                Environment = environment,
                Configured = !string.IsNullOrWhiteSpace(apiKey),
                Channels =
                {
                    new ChannelHealth { Channel = "t212-rest" },
                    new ChannelHealth { Channel = "t212-orders" }
                }
            };
        }

        public VenueCapabilities Capabilities { get; } = new()
        {
            Venue = VenueId.Trading212,
            DisplayName = "Trading 212",
            // Shares bought on Invest/ISA are owned outright. This is inventory, not notional.
            Exposure = ExposureKind.Inventory,
            AssetClasses = new[] { AssetClass.Equity },
            SupportsShort = false,
            SupportsLeverage = false,
            MaxLeverage = 1m,
            SupportsAttachedProtection = false,
            SupportsStreamingPrices = false,
            SupportsStreamingAccount = false,
            SupportsHistoricalData = false,
            SupportsDemoEnvironment = true,
            OrderTypes = new[] { OrderType.Market, OrderType.Limit },
            Limitations =
            {
                ["SupportsShort"] = "Trading 212 Invest holds shares outright; it cannot take short exposure. Use IG for short CFD exposure.",
                ["SupportsLeverage"] = "Invest and ISA accounts are unleveraged.",
                ["SupportsAttachedProtection"] = "The public API does not accept attached stop-loss or take-profit on Invest orders.",
                ["SupportsHistoricalData"] = "Trading 212 publishes no historical-bar endpoint; charts come from the shared market data feed.",
                ["SupportsStreamingPrices"] = "No public price stream. Prices are polled.",
                ["OrderTypes"] = "Only market and limit orders are available through the public API."
            }
        };

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(authorization)) { IsConfigured = false; return false; }
            Auth.Reset();
            try
            {
                // The account summary is the documented identity call: it is the cheapest endpoint
                // that proves the key works *and* tells us the primary currency every figure is in.
                var info = await GetAsync("/equity/account/summary", ct);
                accountCurrency = info?["currency"]?.Value<string>() ?? "GBP";
                accountNumber = info?["id"]?.Value<string>() ?? $"t212-{Environment}".ToLowerInvariant();
                IsConfigured = true;
                MarkOk("t212-rest");
                MarkOk("t212-orders");
                return true;
            }
            catch (Exception ex)
            {
                IsConfigured = false;
                MarkFailed("t212-rest", ex.Message);
                return false;
            }
        }

        public async Task<IReadOnlyList<VenueInstrumentDescriptor>> GetInstrumentsAsync(string? search = null, CancellationToken ct = default)
        {
            if (instrumentCache == null || DateTime.UtcNow - instrumentCacheUtc > TimeSpan.FromHours(12))
            {
                var array = await GetArrayAsync("/equity/metadata/instruments", ct);
                var list = new List<VenueInstrumentDescriptor>();
                foreach (var item in array)
                {
                    string ticker = item["ticker"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(ticker)) continue;
                    list.Add(new VenueInstrumentDescriptor
                    {
                        Venue = VenueId.Trading212,
                        VenueSymbol = ticker,
                        DisplayName = item["name"]?.Value<string>() ?? ticker,
                        AssetClass = AssetClass.Equity,
                        BaseAsset = item["shortName"]?.Value<string>() ?? ticker.Split('_')[0],
                        QuoteCurrency = item["currencyCode"]?.Value<string>() ?? accountCurrency ?? "GBP",
                        // T212 publishes no minimum or step size — only a maximum. Fractional shares
                        // are supported, so the step is the smallest fraction the API will accept
                        // rather than a venue-declared figure we do not actually have.
                        QuantityStep = 0.0001m,
                        MinQuantity = 0.0001m,
                        MaxQuantity = item["maxOpenQuantity"]?.Value<decimal?>(),
                        Tradeable = true,
                        TradingStatus = item["type"]?.Value<string>()
                    });
                }
                instrumentCache = list;
                instrumentCacheUtc = DateTime.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(search)) return instrumentCache;
            return instrumentCache
                .Where(i => i.VenueSymbol.Contains(search, StringComparison.OrdinalIgnoreCase)
                         || i.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(200).ToList();
        }

        public async Task<VenueAccountSnapshot> GetAccountAsync(CancellationToken ct = default)
        {
            var summary = await GetAsync("/equity/account/summary", ct);
            var positions = await GetPositionsAsync(ct);

            var inventory = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in positions) inventory[position.VenueSymbol] = position.Quantity;

            var cash = summary?["cash"];
            var investments = summary?["investments"];

            // T212 splits cash three ways and only one of them can actually be spent. Adding them
            // into a single "balance" would overstate what an order can draw on, so the spendable
            // part is reported as available funds and the whole as the balance.
            decimal? available = cash?["availableToTrade"]?.Value<decimal?>();
            decimal? inPies = cash?["inPies"]?.Value<decimal?>();
            decimal? reserved = cash?["reservedForOrders"]?.Value<decimal?>();
            decimal? totalCash = available.HasValue || inPies.HasValue || reserved.HasValue
                ? (available ?? 0m) + (inPies ?? 0m) + (reserved ?? 0m)
                : null;

            if (!string.IsNullOrWhiteSpace(summary?["currency"]?.Value<string>()))
                accountCurrency = summary!["currency"]!.Value<string>();

            return new VenueAccountSnapshot
            {
                Venue = VenueId.Trading212,
                AccountId = accountNumber ?? $"t212-{Environment}".ToLowerInvariant(),
                Environment = Environment,
                AsOfUtc = DateTime.UtcNow,
                BaseCurrency = accountCurrency ?? "GBP",
                Balance = totalCash,
                // `totalValue` is cash plus the market value of the holdings — the whole account.
                Equity = summary?["totalValue"]?.Value<decimal?>()
                         ?? (totalCash + investments?["currentValue"]?.Value<decimal?>()),
                AvailableFunds = available,
                UnrealizedPnL = investments?["unrealizedProfitLoss"]?.Value<decimal?>(),
                Inventory = inventory
            };
        }

        public async Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            var array = await GetArrayAsync("/equity/positions", ct);
            var list = new List<VenuePositionSnapshot>();
            foreach (var item in array)
            {
                decimal quantity = item["quantity"]?.Value<decimal?>() ?? 0m;
                // The ticker moved inside `instrument` in the public API; the flat form is kept as a
                // fallback so a response from either shape still identifies the holding.
                string ticker = item["instrument"]?["ticker"]?.Value<string>()
                                ?? item["ticker"]?.Value<string>() ?? "";
                if (quantity == 0m || string.IsNullOrWhiteSpace(ticker)) continue;
                list.Add(new VenuePositionSnapshot
                {
                    Venue = VenueId.Trading212,
                    VenueSymbol = ticker,
                    Quantity = quantity,
                    Exposure = ExposureKind.Inventory,
                    // Both prices are in the *instrument's* currency, not the account's.
                    AveragePrice = item["averagePricePaid"]?.Value<decimal?>(),
                    MarkPrice = item["currentPrice"]?.Value<decimal?>(),
                    // walletImpact is the same figure converted into the account currency, which is
                    // the only one that can be added to anything else the firm holds.
                    UnrealizedPnL = item["walletImpact"]?["unrealizedProfitLoss"]?.Value<decimal?>(),
                    VenuePositionId = ticker
                });
            }
            return list;
        }

        public async Task<IReadOnlyList<VenueOrderSnapshot>> GetWorkingOrdersAsync(CancellationToken ct = default)
        {
            var array = await GetArrayAsync("/equity/orders", ct);
            return array.Select(ToOrderSnapshot).Where(o => o != null).Select(o => o!).ToList();
        }

        public async Task<IReadOnlyList<VenueOrderSnapshot>> QueryOrdersAsync(IEnumerable<string> venueOrderIds, CancellationToken ct = default)
        {
            var results = new List<VenueOrderSnapshot>();
            foreach (var id in venueOrderIds.Distinct())
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                try
                {
                    var item = await GetAsync($"/equity/orders/{Uri.EscapeDataString(id)}", ct);
                    var snapshot = item == null ? null : ToOrderSnapshot(item);
                    if (snapshot != null) results.Add(snapshot);
                }
                catch (Trading212ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    // A working order that has left the book has either filled or been cancelled;
                    // the history endpoint is the only place that can say which, and the caller
                    // treats an absent snapshot as "still unproven" rather than assuming either.
                }
            }
            return results;
        }

        public async Task<VenueSubmissionResult> SubmitOrderAsync(OrderRequest request, string clientReference, CancellationToken ct = default)
        {
            // T212 has no client-reference field, so idempotency cannot be pushed down to the venue.
            // The platform's own UNIQUE client reference still prevents a duplicate *proposal*, and an
            // ambiguous response is reported as Unknown so nothing is ever blindly retried.
            string path = request.Type == OrderType.Limit ? "/equity/orders/limit" : "/equity/orders/market";
            var body = new JObject
            {
                ["ticker"] = request.Symbol,
                ["quantity"] = request.Side == OrderSide.Buy ? request.Qty : -request.Qty
            };
            if (request.Type == OrderType.Limit)
            {
                body["limitPrice"] = request.LimitPrice ?? 0m;
                body["timeValidity"] = "DAY";
            }

            try
            {
                var response = await PostAsync(path, body, ct);
                string? id = response?["id"]?.Value<string>();
                MarkOk("t212-orders");
                return string.IsNullOrWhiteSpace(id)
                    ? VenueSubmissionResult.Unknown("Trading 212 accepted the request but returned no order id.", clientReference)
                    : VenueSubmissionResult.Accepted(id!, clientReference);
            }
            catch (Trading212ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                // The venue explicitly refused: that is a proven outcome, not an unknown one.
                MarkOk("t212-orders");
                return VenueSubmissionResult.Rejected(ex.Message);
            }
            catch (Exception ex)
            {
                MarkFailed("t212-orders", ex.Message);
                return VenueSubmissionResult.Unknown($"Trading 212 submission could not be proven: {ex.Message}", clientReference);
            }
        }

        public async Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken ct = default)
        {
            try
            {
                using var request = Build(HttpMethod.Delete, $"/equity/orders/{Uri.EscapeDataString(venueOrderId)}");
                using var response = await http.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>
        /// T212 exposes a price only as part of the portfolio payload, so an instrument the account
        /// does not hold has no venue price at all. The shared market data feed answers for those —
        /// which is also what the charts use, so a quote and its chart cannot disagree.
        /// </summary>
        public async Task<decimal> GetLatestPriceAsync(string venueSymbol, CancellationToken ct = default)
        {
            try
            {
                var positions = await GetPositionsAsync(ct);
                var held = positions.FirstOrDefault(p => string.Equals(p.VenueSymbol, venueSymbol, StringComparison.OrdinalIgnoreCase));
                if (held?.MarkPrice is > 0m) return held.MarkPrice.Value;
            }
            catch { }

            try { return await marketData.GetLatestPriceAsync(ToMarketSymbol(venueSymbol), AssetClass.Equity, ct); }
            catch { return 0m; }
        }

        public Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string venueSymbol, TimeInterval interval, int count, CancellationToken ct = default)
            => marketData.GetHistoricalCandlesAsync(ToMarketSymbol(venueSymbol), interval, count, AssetClass.Equity, ct);

        /// <summary>
        /// T212 tickers carry a venue and currency suffix (`AAPL_US_EQ`, `VUSAl_EQ`). The leading
        /// segment is the exchange ticker the market-data feed knows.
        /// </summary>
        public static string ToMarketSymbol(string venueSymbol)
        {
            if (string.IsNullOrWhiteSpace(venueSymbol)) return venueSymbol;
            string root = venueSymbol.Split('_')[0];
            // London listings carry a trailing lower-case `l` and trade in pence on Yahoo as `.L`.
            return root.Length > 1 && root[^1] == 'l' && char.IsUpper(root[0]) ? $"{root[..^1]}.L" : root;
        }

        // ── transport ─────────────────────────────────────────────────────────────

        private HttpRequestMessage Build(HttpMethod method, string path)
        {
            if (Auth.IsOpen) throw new Trading212ApiException(Auth.Reason, HttpStatusCode.Forbidden);
            var request = new HttpRequestMessage(method, baseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        /// <summary>Record what the response means for the credential before surfacing it.</summary>
        private void NoteOutcome(HttpResponseMessage response, string described)
        {
            if (response.IsSuccessStatusCode) { Auth.RecordSuccess(); return; }
            if (AuthCircuitBreaker.IsRejection(response.StatusCode)) Auth.RecordRejection(described);
        }

        private async Task<JObject?> GetAsync(string path, CancellationToken ct)
        {
            await PaceAsync(path, ct);
            using var request = Build(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            NoteOutcome(response, Describe(response.StatusCode, text));
            if (!response.IsSuccessStatusCode)
            {
                MarkFailed("t212-rest", Describe(response.StatusCode, text));
                throw new Trading212ApiException(Describe(response.StatusCode, text), response.StatusCode);
            }
            MarkOk("t212-rest");
            return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
        }

        private async Task<JArray> GetArrayAsync(string path, CancellationToken ct)
        {
            await PaceAsync(path, ct);
            using var request = Build(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            NoteOutcome(response, Describe(response.StatusCode, text));
            if (!response.IsSuccessStatusCode)
            {
                MarkFailed("t212-rest", Describe(response.StatusCode, text));
                throw new Trading212ApiException(Describe(response.StatusCode, text), response.StatusCode);
            }
            MarkOk("t212-rest");
            return string.IsNullOrWhiteSpace(text) ? new JArray() : JArray.Parse(text);
        }

        private async Task<JObject?> PostAsync(string path, JObject body, CancellationToken ct)
        {
            await PaceAsync(path, ct);
            using var request = Build(HttpMethod.Post, path);
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new Trading212ApiException(Describe(response.StatusCode, text), response.StatusCode);
            return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
        }

        private static string Describe(HttpStatusCode status, string body)
        {
            string detail = body.Length > 300 ? body[..300] : body;
            return status switch
            {
                HttpStatusCode.Unauthorized => "Trading 212 rejected the API key (401). A key only works in the environment it was generated in — switch the app to Practice mode before generating a demo key, and use an Invest/ISA key for live.",
                HttpStatusCode.Forbidden => $"Trading 212 refused the request (403) — the API key is missing a required scope. {detail}",
                HttpStatusCode.TooManyRequests => "Trading 212 rate limit hit (429). The adapter backs off rather than retrying immediately.",
                _ => $"Trading 212 request failed ({(int)status}): {detail}"
            };
        }

        internal static VenueOrderSnapshot? ToOrderSnapshot(JToken item)
        {
            string? id = item["id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(id)) return null;
            decimal quantity = item["quantity"]?.Value<decimal?>() ?? 0m;
            decimal filled = item["filledQuantity"]?.Value<decimal?>() ?? 0m;
            decimal filledValue = item["filledValue"]?.Value<decimal?>() ?? 0m;
            string status = item["status"]?.Value<string>() ?? "";
            string? side = item["side"]?.Value<string>();

            return new VenueOrderSnapshot
            {
                Venue = VenueId.Trading212,
                VenueOrderId = id!,
                VenueSymbol = item["instrument"]?["ticker"]?.Value<string>()
                              ?? item["ticker"]?.Value<string>() ?? "",
                // T212 states the side explicitly; the sign of the quantity is only the *request*
                // convention, so trust the field and fall back to the sign only if it is absent.
                Side = side != null
                    ? (side.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy)
                    : (quantity >= 0m ? OrderSide.Buy : OrderSide.Sell),
                Quantity = Math.Abs(quantity),
                FilledQuantity = Math.Abs(filled),
                // There is no average-fill field: it is the filled value over the filled quantity.
                AverageFillPrice = filled != 0m && filledValue != 0m ? Math.Abs(filledValue / filled) : null,
                Status = status.ToUpperInvariant() switch
                {
                    "FILLED" => OrderStatus.Filled,
                    "CANCELLED" or "CANCELED" => OrderStatus.Cancelled,
                    "REJECTED" => OrderStatus.Rejected,
                    "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
                    // LOCAL, UNCONFIRMED, CONFIRMED, NEW, CANCELLING, REPLACING, REPLACED are all
                    // still live on the book: the order exists and has not resolved either way.
                    _ => OrderStatus.Open
                },
                CreatedUtc = item["createdAt"]?.Value<DateTime?>(),
                Reason = status
            };
        }

        /// <summary>
        /// Hold the caller until this endpoint's documented limit allows another call. T212 counts
        /// per account rather than per key, so waiting is the only way to stay inside the limit —
        /// and a short wait is always cheaper than the 429 it prevents.
        /// </summary>
        private async Task PaceAsync(string path, CancellationToken ct)
        {
            if (LimitFor(path) is not { } limit) return;

            TimeSpan wait;
            await paceLock.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                wait = lastCallUtc.TryGetValue(limit.Bucket, out var last)
                    ? limit.MinInterval - (now - last)
                    : TimeSpan.Zero;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                // Book the slot before releasing, so two concurrent callers queue rather than collide.
                lastCallUtc[limit.Bucket] = now + wait;
            }
            finally { paceLock.Release(); }

            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }

        private void MarkOk(string channel)
        {
            var c = health.Channels.FirstOrDefault(x => x.Channel == channel);
            if (c == null) return;
            c.Connected = true;
            c.LastOkUtc = DateTime.UtcNow;
            c.ConsecutiveFailures = 0;
            c.LastError = null;
        }

        private void MarkFailed(string channel, string error)
        {
            var c = health.Channels.FirstOrDefault(x => x.Channel == channel);
            if (c == null) return;
            c.Connected = false;
            c.LastErrorUtc = DateTime.UtcNow;
            c.LastError = error;
            c.ConsecutiveFailures++;
        }
    }

    public sealed class Trading212ApiException : Exception
    {
        public HttpStatusCode Status { get; }
        public Trading212ApiException(string message, HttpStatusCode status) : base(message) => Status = status;
    }
}
