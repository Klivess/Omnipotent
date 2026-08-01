using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Kraken **spot** venue adapter. Spot balances are owned inventory, not leveraged positions, so
    /// this adapter never reports margin/liquidation/short fields — the common model would otherwise
    /// invent them. Withdrawal permission is deliberately outside the trading key's scope.
    /// </summary>
    public sealed class KrakenVenueAdapter : IVenueAdapter
    {
        private readonly KrakenOrderRouter router;
        private readonly MarketDataRouter marketData;
        private readonly ChannelHealth restHealth = new() { Channel = "kraken-rest" };
        private readonly ChannelHealth privateHealth = new() { Channel = "kraken-private-rest" };
        private readonly ChannelHealth publicStreamHealth = new() { Channel = "kraken-public-stream" };
        private readonly SemaphoreSlim instrumentLock = new(1, 1);
        private List<VenueInstrumentDescriptor>? instrumentCache;
        private DateTime instrumentCacheUtc = DateTime.MinValue;

        public VenueId Venue => VenueId.Kraken;
        public TradingEnvironment Environment => TradingEnvironment.Live;
        public bool IsConfigured { get; private set; }

        public VenueCapabilities Capabilities { get; } = new()
        {
            Venue = VenueId.Kraken,
            DisplayName = "Kraken (Spot)",
            Exposure = ExposureKind.Inventory,
            AssetClasses = new[] { AssetClass.Crypto },
            SupportsShort = false,
            SupportsLeverage = false,
            MaxLeverage = 1m,
            SupportsAttachedProtection = true,
            SupportsStreamingPrices = true,
            SupportsStreamingAccount = false,
            SupportsHistoricalData = true,
            SupportsDemoEnvironment = false,
            OrderTypes = new[] { OrderType.Market, OrderType.Limit, OrderType.StopLoss, OrderType.TakeProfit },
            Limitations =
            {
                ["SupportsShort"] = "UK spot baseline — a sell cannot exceed free inventory. Short exposure is not available through spot.",
                ["SupportsLeverage"] = "Kraken margin/derivatives are out of scope for the UK baseline.",
                ["SupportsDemoEnvironment"] = "Kraken has no demo account; simulation runs on the internal paper venue instead.",
                ["SupportsStreamingAccount"] = "Private account streaming is not wired; account state is reconciled over REST on a schedule."
            }
        };

        public KrakenVenueAdapter(KrakenOrderRouter router, MarketDataRouter marketData)
        {
            this.router = router;
            this.marketData = marketData;
            IsConfigured = true;
        }

        public VenueHealthSnapshot Health => new()
        {
            Venue = VenueId.Kraken,
            Environment = TradingEnvironment.Live,
            Configured = IsConfigured,
            Channels = new List<ChannelHealth> { restHealth, privateHealth, publicStreamHealth }
        };

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await router.QueryPrivateAsync("/0/private/Balance", null, ct);
                bool ok = (resp["error"] as JArray)?.Count is null or 0;
                MarkChannel(privateHealth, ok, ok ? null : string.Join(", ", (JArray?)resp["error"] ?? new JArray()));
                IsConfigured = ok;
                return ok;
            }
            catch (Exception ex)
            {
                MarkChannel(privateHealth, false, ex.Message);
                return false;
            }
        }

        public async Task<IReadOnlyList<VenueInstrumentDescriptor>> GetInstrumentsAsync(string? search = null, CancellationToken ct = default)
        {
            var all = await LoadInstrumentsAsync(ct);
            if (string.IsNullOrWhiteSpace(search)) return all;
            return all.Where(i => i.VenueSymbol.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || i.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private async Task<List<VenueInstrumentDescriptor>> LoadInstrumentsAsync(CancellationToken ct)
        {
            if (instrumentCache != null && DateTime.UtcNow - instrumentCacheUtc < TimeSpan.FromHours(6))
                return instrumentCache;

            await instrumentLock.WaitAsync(ct);
            try
            {
                if (instrumentCache != null && DateTime.UtcNow - instrumentCacheUtc < TimeSpan.FromHours(6))
                    return instrumentCache;

                var list = new List<VenueInstrumentDescriptor>();
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    string body = await http.GetStringAsync("https://api.kraken.com/0/public/AssetPairs", ct);
                    var parsed = JObject.Parse(body);
                    if (parsed["result"] is JObject result)
                    {
                        foreach (var prop in result.Properties())
                        {
                            if (prop.Value is not JObject o) continue;
                            string wsname = (string?)o["wsname"] ?? prop.Name;
                            string baseAsset = (string?)o["base"] ?? "";
                            string quote = (string?)o["quote"] ?? "";
                            int pairDecimals = (int?)o["pair_decimals"] ?? 5;
                            int lotDecimals = (int?)o["lot_decimals"] ?? 8;
                            decimal ordermin = ParseDec(o["ordermin"]);
                            string status = (string?)o["status"] ?? "online";

                            list.Add(new VenueInstrumentDescriptor
                            {
                                Venue = VenueId.Kraken,
                                VenueSymbol = prop.Name,
                                DisplayName = wsname,
                                AssetClass = AssetClass.Crypto,
                                BaseAsset = NormalizeAsset(baseAsset),
                                QuoteCurrency = NormalizeAsset(quote),
                                TickSize = Pow10Neg(pairDecimals),
                                QuantityStep = Pow10Neg(lotDecimals),
                                MinQuantity = ordermin,
                                ContractMultiplier = 1m,
                                Tradeable = string.Equals(status, "online", StringComparison.OrdinalIgnoreCase),
                                TradingStatus = status,
                                TradingHours = "24/7"
                            });
                        }
                    }
                    MarkChannel(restHealth, true, null);
                }
                catch (Exception ex)
                {
                    MarkChannel(restHealth, false, ex.Message);
                    return instrumentCache ?? new List<VenueInstrumentDescriptor>();
                }

                instrumentCache = list;
                instrumentCacheUtc = DateTime.UtcNow;
                return list;
            }
            finally { instrumentLock.Release(); }
        }

        public async Task<VenueAccountSnapshot> GetAccountAsync(CancellationToken ct = default)
        {
            var inventory = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var reserved = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            decimal? cash = null;

            try
            {
                var balances = await router.QueryPrivateAsync("/0/private/Balance", null, ct);
                if (balances["result"] is JObject result)
                {
                    foreach (var p in result.Properties())
                    {
                        decimal amount = ParseDec(p.Value);
                        if (amount == 0m) continue;
                        string asset = NormalizeAsset(p.Name);
                        inventory[asset] = amount;
                        if (IsCashAsset(asset)) cash = (cash ?? 0m) + amount;
                    }
                }
                MarkChannel(privateHealth, true, null);
            }
            catch (Exception ex)
            {
                MarkChannel(privateHealth, false, ex.Message);
                throw;
            }

            // Inventory committed to resting sell orders is not free to sell again.
            try
            {
                var open = await router.QueryPrivateAsync("/0/private/OpenOrders", null, ct);
                if (open["result"]?["open"] is JObject orders)
                {
                    foreach (var p in orders.Properties())
                    {
                        if (p.Value is not JObject o) continue;
                        var descr = o["descr"] as JObject;
                        string type = (string?)descr?["type"] ?? "buy";
                        if (!string.Equals(type, "sell", StringComparison.OrdinalIgnoreCase)) continue;
                        string pair = (string?)descr?["pair"] ?? "";
                        decimal remaining = ParseDec(o["vol"]) - ParseDec(o["vol_exec"]);
                        if (remaining <= 0m) continue;
                        string asset = NormalizeAsset(GuessBaseAsset(pair));
                        reserved[asset] = reserved.TryGetValue(asset, out var r) ? r + remaining : remaining;
                    }
                }
            }
            catch { /* reserved is best-effort; a failure here must not hide the balance read */ }

            return new VenueAccountSnapshot
            {
                Venue = VenueId.Kraken,
                AccountId = "kraken-spot",
                Environment = TradingEnvironment.Live,
                AsOfUtc = DateTime.UtcNow,
                BaseCurrency = "USD",
                Balance = cash,
                AvailableFunds = cash,
                // Deliberately null: spot has no equity/margin concept in this baseline.
                Equity = null,
                MarginUsed = null,
                Inventory = inventory,
                Reserved = reserved
            };
        }

        /// <summary>Spot "positions" are owned inventory marked to the current price. They are reported
        /// as <see cref="ExposureKind.Inventory"/> so the portfolio never mixes them with CFD notional.</summary>
        public async Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            var account = await GetAccountAsync(ct);
            var list = new List<VenuePositionSnapshot>();
            foreach (var (asset, qty) in account.Inventory)
            {
                if (IsCashAsset(asset) || qty == 0m) continue;
                decimal mark = 0m;
                try { mark = await marketData.GetLatestPriceAsync(asset + "USDT"); } catch { }
                list.Add(new VenuePositionSnapshot
                {
                    Venue = VenueId.Kraken,
                    VenueSymbol = asset + "USD",
                    Quantity = qty,
                    Exposure = ExposureKind.Inventory,
                    MarkPrice = mark > 0 ? mark : null,
                    Leverage = null
                });
            }
            return list;
        }

        public async Task<IReadOnlyList<VenueOrderSnapshot>> GetWorkingOrdersAsync(CancellationToken ct = default)
        {
            var list = new List<VenueOrderSnapshot>();
            try
            {
                var open = await router.QueryPrivateAsync("/0/private/OpenOrders", null, ct);
                if (open["result"]?["open"] is JObject orders)
                    foreach (var p in orders.Properties())
                        if (p.Value is JObject o) list.Add(ToSnapshot(p.Name, o));
                MarkChannel(privateHealth, true, null);
            }
            catch (Exception ex) { MarkChannel(privateHealth, false, ex.Message); throw; }
            return list;
        }

        public async Task<IReadOnlyList<VenueOrderSnapshot>> QueryOrdersAsync(IEnumerable<string> venueOrderIds, CancellationToken ct = default)
        {
            var ids = venueOrderIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (ids.Count == 0) return Array.Empty<VenueOrderSnapshot>();
            var list = new List<VenueOrderSnapshot>();
            try
            {
                var resp = await router.QueryOrdersAsync(ids, ct);
                if (resp?["result"] is JObject result)
                    foreach (var p in result.Properties())
                        if (p.Value is JObject o) list.Add(ToSnapshot(p.Name, o));
                MarkChannel(privateHealth, true, null);
            }
            catch (Exception ex) { MarkChannel(privateHealth, false, ex.Message); throw; }
            return list;
        }

        public async Task<VenueSubmissionResult> SubmitOrderAsync(OrderRequest request, string clientReference, CancellationToken ct = default)
        {
            // The client reference IS the idempotency key: Kraken stores it as cl_ord_id, so an
            // ambiguous submission can be resolved by looking the reference up rather than retrying.
            var withRef = new OrderRequest
            {
                IntentId = clientReference,
                Side = request.Side,
                Type = request.Type,
                Symbol = request.Symbol,
                Qty = request.Qty,
                LimitPrice = request.LimitPrice,
                StopPrice = request.StopPrice,
                Leverage = 1m, // spot baseline — never inject leverage on this venue
                TakeProfitPrice = request.TakeProfitPrice,
                StopLossPrice = request.StopLossPrice
            };

            try
            {
                var intent = await router.PlaceOrderAsync("firm", withRef, ct);
                MarkChannel(privateHealth, true, null);
                if (intent.Status == OrderStatus.Rejected)
                    return VenueSubmissionResult.Rejected(intent.Error ?? "Kraken rejected the order");
                if (string.IsNullOrWhiteSpace(intent.ExchangeOrderId))
                    return VenueSubmissionResult.Unknown("Kraken accepted the request but returned no txid", clientReference);
                return VenueSubmissionResult.Accepted(intent.ExchangeOrderId!, clientReference);
            }
            catch (Exception ex)
            {
                // Transport failure — we cannot prove the order was not accepted.
                MarkChannel(privateHealth, false, ex.Message);
                return VenueSubmissionResult.Unknown(ex.Message, clientReference);
            }
        }

        public async Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken ct = default)
        {
            try
            {
                var resp = await router.QueryPrivateAsync("/0/private/CancelOrder",
                    new Dictionary<string, string> { ["txid"] = venueOrderId }, ct);
                bool ok = (resp["error"] as JArray)?.Count is null or 0;
                MarkChannel(privateHealth, ok, ok ? null : "cancel rejected");
                return ok;
            }
            catch (Exception ex) { MarkChannel(privateHealth, false, ex.Message); return false; }
        }

        public async Task<decimal> GetLatestPriceAsync(string venueSymbol, CancellationToken ct = default)
        {
            try
            {
                decimal p = await marketData.GetLatestPriceAsync(venueSymbol);
                MarkChannel(publicStreamHealth, p > 0, p > 0 ? null : "no price");
                return p;
            }
            catch (Exception ex) { MarkChannel(publicStreamHealth, false, ex.Message); return 0m; }
        }

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string venueSymbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            try
            {
                var candles = await marketData.GetHistoricalCandlesAsync(venueSymbol, interval, count);
                MarkChannel(restHealth, true, null);
                return candles;
            }
            catch (Exception ex) { MarkChannel(restHealth, false, ex.Message); return Array.Empty<OHLCCandle>(); }
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private static VenueOrderSnapshot ToSnapshot(string txid, JObject o)
        {
            var descr = o["descr"] as JObject;
            string type = (string?)descr?["type"] ?? "buy";
            decimal vol = ParseDec(o["vol"]);
            decimal volExec = ParseDec(o["vol_exec"]);
            decimal cost = ParseDec(o["cost"]);
            string status = (string?)o["status"] ?? "";
            return new VenueOrderSnapshot
            {
                Venue = VenueId.Kraken,
                VenueOrderId = txid,
                VenueSymbol = (string?)descr?["pair"] ?? "",
                Side = string.Equals(type, "sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
                Quantity = vol,
                FilledQuantity = volExec,
                AverageFillPrice = volExec > 0 ? cost / volExec : null,
                Fee = ParseDec(o["fee"]),
                FeeCurrency = "USD",
                Status = MapStatus(status, vol, volExec),
                ClientReference = (string?)o["cl_ord_id"] ?? (string?)o["userref"],
                CreatedUtc = ParseKrakenTime(o["opentm"]),
                Reason = (string?)o["reason"]
            };
        }

        private static OrderStatus MapStatus(string krakenStatus, decimal vol, decimal volExec) => krakenStatus switch
        {
            "pending" => OrderStatus.Pending,
            "open" => volExec > 0m && volExec < vol ? OrderStatus.PartiallyFilled : OrderStatus.Open,
            "closed" => OrderStatus.Filled,
            "canceled" => volExec > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Cancelled,
            "expired" => OrderStatus.Cancelled,
            _ => OrderStatus.Open
        };

        private static DateTime? ParseKrakenTime(JToken? t)
        {
            double seconds = t?.Type is JTokenType.Float or JTokenType.Integer ? (double)t : 0;
            return seconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime : null;
        }

        private static decimal ParseDec(JToken? t)
            => t == null ? 0m
             : decimal.TryParse((string?)t, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

        private static decimal Pow10Neg(int decimals)
        {
            decimal v = 1m;
            for (int i = 0; i < Math.Clamp(decimals, 0, 12); i++) v /= 10m;
            return v;
        }

        /// <summary>Kraken prefixes legacy assets (XXBT, ZUSD). Strip to the common ticker.</summary>
        public static string NormalizeAsset(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string a = raw.ToUpperInvariant();
            if (a.Length > 3 && (a[0] == 'X' || a[0] == 'Z') && a is not ("XRP" or "XLM" or "XTZ" or "XDG"))
                a = a[1..];
            return a switch { "XBT" => "BTC", "XDG" => "DOGE", _ => a };
        }

        private static bool IsCashAsset(string asset)
            => asset is "USD" or "GBP" or "EUR" or "USDT" or "USDC" or "ZUSD" or "ZGBP" or "ZEUR";

        private static string GuessBaseAsset(string pair)
        {
            string p = pair.ToUpperInvariant();
            foreach (var q in new[] { "USDT", "USDC", "ZUSD", "USD", "ZGBP", "GBP", "ZEUR", "EUR", "XBT", "BTC" })
                if (p.EndsWith(q, StringComparison.Ordinal) && p.Length > q.Length) return p[..^q.Length];
            return p;
        }

        private static void MarkChannel(ChannelHealth h, bool ok, string? error)
        {
            h.Connected = ok;
            if (ok) { h.LastOkUtc = DateTime.UtcNow; h.ConsecutiveFailures = 0; h.LastError = null; }
            else { h.LastErrorUtc = DateTime.UtcNow; h.LastError = error; h.ConsecutiveFailures++; }
        }
    }
}
