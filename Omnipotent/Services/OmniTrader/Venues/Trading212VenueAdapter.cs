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
        private readonly string apiKey;
        private readonly string baseUrl;
        private readonly MarketDataRouter marketData;
        private readonly VenueHealthSnapshot health;

        // T212 rate-limits aggressively (as little as one call per few seconds on some endpoints), so
        // the instrument directory — thousands of rows that change rarely — is cached in memory.
        private IReadOnlyList<VenueInstrumentDescriptor>? instrumentCache;
        private DateTime instrumentCacheUtc;
        private string? accountCurrency;
        private string? accountNumber;

        /// <summary>Open while T212 keeps rejecting this key — usually a practice key pointed at the
        /// live endpoint, or the reverse. Retrying cannot fix either.</summary>
        public AuthCircuitBreaker Auth { get; } = new();

        public VenueId Venue => VenueId.Trading212;
        public TradingEnvironment Environment { get; }
        public bool IsConfigured { get; private set; }
        public VenueHealthSnapshot Health => health;

        public Trading212VenueAdapter(string apiKey, TradingEnvironment environment, MarketDataRouter marketData)
        {
            this.apiKey = apiKey;
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
            if (string.IsNullOrWhiteSpace(apiKey)) { IsConfigured = false; return false; }
            Auth.Reset();
            try
            {
                var info = await GetAsync("/equity/account/info", ct);
                accountCurrency = info?["currencyCode"]?.Value<string>() ?? "GBP";
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
                        QuantityStep = item["minTradeQuantity"]?.Value<decimal?>() ?? 0.0001m,
                        MinQuantity = item["minTradeQuantity"]?.Value<decimal?>() ?? 0.0001m,
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
            var cash = await GetAsync("/equity/account/cash", ct);
            var positions = await GetPositionsAsync(ct);

            var inventory = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in positions) inventory[position.VenueSymbol] = position.Quantity;

            return new VenueAccountSnapshot
            {
                Venue = VenueId.Trading212,
                AccountId = accountNumber ?? $"t212-{Environment}".ToLowerInvariant(),
                Environment = Environment,
                AsOfUtc = DateTime.UtcNow,
                BaseCurrency = accountCurrency ?? "GBP",
                // `free` is uninvested cash; `total` includes the value of the shares, so the two are
                // reported in their own fields rather than conflated into one "balance".
                Balance = cash?["free"]?.Value<decimal?>(),
                Equity = cash?["total"]?.Value<decimal?>(),
                AvailableFunds = cash?["free"]?.Value<decimal?>(),
                UnrealizedPnL = cash?["ppl"]?.Value<decimal?>(),
                Inventory = inventory
            };
        }

        public async Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            var array = await GetArrayAsync("/equity/portfolio", ct);
            var list = new List<VenuePositionSnapshot>();
            foreach (var item in array)
            {
                decimal quantity = item["quantity"]?.Value<decimal?>() ?? 0m;
                if (quantity == 0m) continue;
                list.Add(new VenuePositionSnapshot
                {
                    Venue = VenueId.Trading212,
                    VenueSymbol = item["ticker"]?.Value<string>() ?? "",
                    Quantity = quantity,
                    Exposure = ExposureKind.Inventory,
                    AveragePrice = item["averagePrice"]?.Value<decimal?>(),
                    MarkPrice = item["currentPrice"]?.Value<decimal?>(),
                    UnrealizedPnL = item["ppl"]?.Value<decimal?>(),
                    VenuePositionId = item["ticker"]?.Value<string>()
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
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);
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

        private static VenueOrderSnapshot? ToOrderSnapshot(JToken item)
        {
            string? id = item["id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(id)) return null;
            decimal quantity = item["quantity"]?.Value<decimal?>() ?? 0m;
            decimal filled = item["filledQuantity"]?.Value<decimal?>() ?? 0m;
            string status = item["status"]?.Value<string>() ?? "";

            return new VenueOrderSnapshot
            {
                Venue = VenueId.Trading212,
                VenueOrderId = id!,
                VenueSymbol = item["ticker"]?.Value<string>() ?? "",
                Side = quantity >= 0m ? OrderSide.Buy : OrderSide.Sell,
                Quantity = Math.Abs(quantity),
                FilledQuantity = Math.Abs(filled),
                AverageFillPrice = item["fillPrice"]?.Value<decimal?>(),
                Status = status.ToUpperInvariant() switch
                {
                    "FILLED" => OrderStatus.Filled,
                    "CANCELLED" or "CANCELED" => OrderStatus.Cancelled,
                    "REJECTED" => OrderStatus.Rejected,
                    "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
                    _ => OrderStatus.Open
                },
                CreatedUtc = item["dateCreated"]?.Value<DateTime?>(),
                Reason = item["status"]?.Value<string>()
            };
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
