using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// IG **CFD** venue adapter. CFD positions are leveraged derivative exposure — notional, never an
    /// owned asset — so this adapter reports <see cref="ExposureKind.Derivative"/> and surfaces margin
    /// fields the spot venue leaves null.
    ///
    /// Every submission resolves through IG's deal-confirmation endpoint: <c>POST /positions/otc</c>
    /// returns only a deal *reference*, and the platform must query <c>/confirms/{ref}</c> to learn
    /// whether the deal was ACCEPTED or REJECTED. A confirmation we cannot read is reported as
    /// <see cref="SubmissionOutcome.Unknown"/> — not retried.
    /// </summary>
    public sealed class IGVenueAdapter : IVenueAdapter
    {
        private readonly IGRestClient client;
        private readonly ChannelHealth restHealth = new() { Channel = "ig-rest" };
        private readonly ChannelHealth sessionHealth = new() { Channel = "ig-session" };
        // Lightstreamer is not implemented, so this channel is *unsupported*, not broken. Reporting a
        // feature the platform has never built as an outage is noise an operator can never clear.
        private readonly ChannelHealth streamHealth = new() { Channel = "ig-lightstreamer", Unsupported = true };
        private readonly SemaphoreSlim instrumentLock = new(1, 1);
        private readonly Dictionary<string, VenueInstrumentDescriptor> instrumentCache = new(StringComparer.OrdinalIgnoreCase);

        public VenueId Venue => VenueId.IG;
        public TradingEnvironment Environment { get; }
        public bool IsConfigured { get; private set; }
        public string? AccountId => client.CurrentAccountId;

        public VenueCapabilities Capabilities { get; }

        public IGVenueAdapter(IGRestClient client)
        {
            this.client = client;
            Environment = client.Environment;
            Capabilities = new VenueCapabilities
            {
                Venue = VenueId.IG,
                DisplayName = Environment == TradingEnvironment.Demo ? "IG (CFD — Demo)" : "IG (CFD — Live)",
                Exposure = ExposureKind.Derivative,
                AssetClasses = new[] { AssetClass.Index, AssetClass.Equity, AssetClass.Forex, AssetClass.Commodity, AssetClass.Crypto },
                SupportsShort = true,
                SupportsLeverage = true,
                MaxLeverage = 30m,
                SupportsAttachedProtection = true,
                SupportsStreamingPrices = false,
                SupportsStreamingAccount = false,
                SupportsHistoricalData = true,
                SupportsDemoEnvironment = true,
                OrderTypes = new[] { OrderType.Market, OrderType.Limit, OrderType.StopLoss, OrderType.TakeProfit },
                Limitations =
                {
                    ["SupportsStreamingPrices"] = "Lightstreamer subscriptions are not yet implemented; prices are polled over REST and freshness is reported per instrument.",
                    ["SupportsStreamingAccount"] = "Account and trade updates are reconciled over REST on a schedule rather than streamed.",
                    ["MaxLeverage"] = "Effective leverage is set by IG's per-instrument margin factor, not by this platform."
                }
            };
            // Streaming is honestly reported as an unavailable channel rather than faked as healthy.
            streamHealth.Connected = false;
            streamHealth.LastError = "Lightstreamer not implemented — REST polling in use";
        }

        public VenueHealthSnapshot Health
        {
            get
            {
                sessionHealth.Connected = client.HasSession;
                restHealth.QuotaRemaining = client.HistoricalAllowanceTotal is > 0
                    ? (double)(client.HistoricalAllowanceRemaining ?? 0) / client.HistoricalAllowanceTotal.Value
                    : null;
                return new VenueHealthSnapshot
                {
                    Venue = VenueId.IG,
                    Environment = Environment,
                    Configured = IsConfigured,
                    Channels = new List<ChannelHealth> { restHealth, sessionHealth, streamHealth }
                };
            }
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            // Reconnecting is the one action that can change a rejection's outcome, because it
            // follows the operator fixing the credential. Give it a clean attempt.
            client.Auth.Reset();
            try
            {
                bool ok = await client.LoginAsync(force: true, ct: ct);
                Mark(sessionHealth, ok, ok ? null : "login returned no tokens");
                IsConfigured = ok;
                return ok;
            }
            catch (Exception ex)
            {
                Mark(sessionHealth, false, ex.Message);
                IsConfigured = false;
                return false;
            }
        }

        // ── instrument directory ──────────────────────────────────────────────────

        public async Task<IReadOnlyList<VenueInstrumentDescriptor>> GetInstrumentsAsync(string? search = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                await instrumentLock.WaitAsync(ct);
                try { return instrumentCache.Values.ToList(); }
                finally { instrumentLock.Release(); }
            }

            try
            {
                var resp = await client.GetAsync($"/markets?searchTerm={Uri.EscapeDataString(search)}", "1", ct);
                Mark(restHealth, true, null);
                var list = new List<VenueInstrumentDescriptor>();
                if (resp["markets"] is JArray markets)
                {
                    foreach (var m in markets.OfType<JObject>())
                    {
                        string epic = (string?)m["epic"] ?? "";
                        if (string.IsNullOrWhiteSpace(epic)) continue;
                        list.Add(new VenueInstrumentDescriptor
                        {
                            Venue = VenueId.IG,
                            VenueSymbol = epic,
                            DisplayName = (string?)m["instrumentName"] ?? epic,
                            AssetClass = MapAssetClass((string?)m["instrumentType"]),
                            BaseAsset = (string?)m["instrumentName"] ?? epic,
                            QuoteCurrency = "GBP",
                            Tradeable = string.Equals((string?)m["marketStatus"], "TRADEABLE", StringComparison.OrdinalIgnoreCase),
                            TradingStatus = (string?)m["marketStatus"],
                            ContractMultiplier = 1m
                        });
                    }
                }
                return list;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); throw; }
        }

        /// <summary>Full dealing rules for one epic — the data the order ticket and risk engine need to
        /// validate size, precision and stop distances before anything is submitted.</summary>
        public async Task<VenueInstrumentDescriptor?> GetInstrumentDetailAsync(string epic, CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync($"/markets/{Uri.EscapeDataString(epic)}", "3", ct);
                Mark(restHealth, true, null);

                var instrument = resp["instrument"] as JObject;
                var rules = resp["dealingRules"] as JObject;
                var snapshot = resp["snapshot"] as JObject;
                if (instrument == null) return null;

                decimal minDeal = Dec(rules?["minDealSize"]?["value"]);
                decimal step = Dec(rules?["minStepDistance"]?["value"]);
                var descriptor = new VenueInstrumentDescriptor
                {
                    Venue = VenueId.IG,
                    VenueSymbol = epic,
                    DisplayName = (string?)instrument["name"] ?? epic,
                    AssetClass = MapAssetClass((string?)instrument["type"]),
                    BaseAsset = (string?)instrument["name"] ?? epic,
                    QuoteCurrency = (string?)(instrument["currencies"] as JArray)?.OfType<JObject>().FirstOrDefault()?["code"] ?? "GBP",
                    ContractMultiplier = Dec(instrument["contractSize"]) is var cs && cs > 0 ? cs : 1m,
                    MinQuantity = minDeal,
                    QuantityStep = step > 0 ? step : minDeal,
                    TickSize = Dec(instrument["onePipMeans"]) is var pip && pip > 0 ? pip : 0m,
                    MarginFactor = Dec(instrument["marginFactor"]) is var mf && mf > 0 ? mf : null,
                    Tradeable = string.Equals((string?)snapshot?["marketStatus"], "TRADEABLE", StringComparison.OrdinalIgnoreCase),
                    TradingStatus = (string?)snapshot?["marketStatus"],
                    TradingHours = (instrument["openingHours"] ?? instrument["marketId"])?.ToString()
                };

                await instrumentLock.WaitAsync(ct);
                try { instrumentCache[epic] = descriptor; }
                finally { instrumentLock.Release(); }
                return descriptor;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); return null; }
        }

        // ── account & positions ───────────────────────────────────────────────────

        public async Task<VenueAccountSnapshot> GetAccountAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync("/accounts", "1", ct);
                Mark(restHealth, true, null);

                JObject? account = null;
                if (resp["accounts"] is JArray accounts)
                {
                    account = accounts.OfType<JObject>()
                        .FirstOrDefault(a => string.Equals((string?)a["accountId"], client.CurrentAccountId, StringComparison.OrdinalIgnoreCase))
                        ?? accounts.OfType<JObject>().FirstOrDefault();
                }

                var balance = account?["balance"] as JObject;
                return new VenueAccountSnapshot
                {
                    Venue = VenueId.IG,
                    AccountId = (string?)account?["accountId"] ?? client.CurrentAccountId ?? "ig",
                    Environment = Environment,
                    AsOfUtc = DateTime.UtcNow,
                    BaseCurrency = (string?)account?["currency"] ?? "GBP",
                    Balance = Dec(balance?["balance"]),
                    Equity = Dec(balance?["balance"]) + Dec(balance?["profitLoss"]),
                    AvailableFunds = Dec(balance?["available"]),
                    MarginUsed = Dec(balance?["deposit"]),
                    UnrealizedPnL = Dec(balance?["profitLoss"])
                    // Inventory intentionally empty: a CFD account owns nothing.
                };
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); throw; }
        }

        public async Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync("/positions", "2", ct);
                Mark(restHealth, true, null);

                var list = new List<VenuePositionSnapshot>();
                if (resp["positions"] is JArray positions)
                {
                    foreach (var p in positions.OfType<JObject>())
                    {
                        var pos = p["position"] as JObject;
                        var market = p["market"] as JObject;
                        if (pos == null) continue;

                        decimal size = Dec(pos["size"]);
                        bool isSell = string.Equals((string?)pos["direction"], "SELL", StringComparison.OrdinalIgnoreCase);
                        decimal signed = isSell ? -size : size;
                        decimal open = Dec(pos["level"]);
                        // Mark a long against the bid (what you would sell into) and a short against the offer.
                        decimal mark = isSell ? Dec(market?["offer"]) : Dec(market?["bid"]);
                        decimal contract = Dec(pos["contractSize"]) is var c && c > 0 ? c : 1m;

                        list.Add(new VenuePositionSnapshot
                        {
                            Venue = VenueId.IG,
                            VenueSymbol = (string?)market?["epic"] ?? "",
                            Quantity = signed,
                            Exposure = ExposureKind.Derivative,
                            AveragePrice = open,
                            MarkPrice = mark > 0 ? mark : null,
                            UnrealizedPnL = mark > 0 ? (mark - open) * signed * contract : null,
                            VenuePositionId = (string?)pos["dealId"]
                        });
                    }
                }
                return list;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); throw; }
        }

        public async Task<IReadOnlyList<VenueOrderSnapshot>> GetWorkingOrdersAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync("/workingorders", "2", ct);
                Mark(restHealth, true, null);

                var list = new List<VenueOrderSnapshot>();
                if (resp["workingOrders"] is JArray orders)
                {
                    foreach (var w in orders.OfType<JObject>())
                    {
                        var data = w["workingOrderData"] as JObject;
                        var market = w["marketData"] as JObject;
                        if (data == null) continue;
                        bool isSell = string.Equals((string?)data["direction"], "SELL", StringComparison.OrdinalIgnoreCase);
                        list.Add(new VenueOrderSnapshot
                        {
                            Venue = VenueId.IG,
                            VenueOrderId = (string?)data["dealId"] ?? "",
                            VenueSymbol = (string?)data["epic"] ?? (string?)market?["epic"] ?? "",
                            Side = isSell ? OrderSide.Sell : OrderSide.Buy,
                            Quantity = Dec(data["orderSize"]),
                            FilledQuantity = 0m,
                            Status = OrderStatus.Open,
                            ClientReference = (string?)data["dealReference"],
                            CreatedUtc = ParseIgTime((string?)data["createdDateUTC"])
                        });
                    }
                }
                return list;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); throw; }
        }

        /// <summary>Resolve deal references (our idempotency keys) through IG's confirmation endpoint.
        /// This is how an ambiguous submission is settled without ever re-sending it.</summary>
        public async Task<IReadOnlyList<VenueOrderSnapshot>> QueryOrdersAsync(IEnumerable<string> venueOrderIds, CancellationToken ct = default)
        {
            var list = new List<VenueOrderSnapshot>();
            foreach (var reference in venueOrderIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                var snapshot = await GetConfirmationAsync(reference, ct);
                if (snapshot != null) list.Add(snapshot);
            }
            return list;
        }

        public async Task<VenueOrderSnapshot?> GetConfirmationAsync(string dealReference, CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync($"/confirms/{Uri.EscapeDataString(dealReference)}", "1", ct);
                Mark(restHealth, true, null);

                string dealStatus = (string?)resp["dealStatus"] ?? "";
                string status = (string?)resp["status"] ?? "";
                bool accepted = string.Equals(dealStatus, "ACCEPTED", StringComparison.OrdinalIgnoreCase);
                decimal size = Dec(resp["size"]);
                bool isSell = string.Equals((string?)resp["direction"], "SELL", StringComparison.OrdinalIgnoreCase);

                return new VenueOrderSnapshot
                {
                    Venue = VenueId.IG,
                    VenueOrderId = (string?)resp["dealId"] ?? dealReference,
                    VenueSymbol = (string?)resp["epic"] ?? "",
                    Side = isSell ? OrderSide.Sell : OrderSide.Buy,
                    Quantity = size,
                    FilledQuantity = accepted ? size : 0m,
                    AverageFillPrice = Dec(resp["level"]) is var lvl && lvl > 0 ? lvl : null,
                    Status = accepted
                        ? (string.Equals(status, "DELETED", StringComparison.OrdinalIgnoreCase) ? OrderStatus.Cancelled : OrderStatus.Filled)
                        : OrderStatus.Rejected,
                    ClientReference = dealReference,
                    Reason = (string?)resp["reason"],
                    CreatedUtc = ParseIgTime((string?)resp["date"])
                };
            }
            catch (IGApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // IG keeps confirmations for a limited window; absence is not proof of rejection.
                return null;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); return null; }
        }

        // ── execution ─────────────────────────────────────────────────────────────

        public async Task<VenueSubmissionResult> SubmitOrderAsync(OrderRequest request, string clientReference, CancellationToken ct = default)
        {
            // IG deal references are alphanumeric, max 30 chars — derive one deterministically from
            // our idempotency key so the same intent always maps to the same reference.
            string dealReference = SanitiseReference(clientReference);

            // `currencyCode` and `expiry` are both required, and both are properties of the
            // instrument rather than of the order. Guessing them is how you earn
            // CONTACT_SUPPORT_INSTRUMENT_ERROR, so ask the venue what this epic actually deals in.
            var detail = await GetDealingTermsAsync(request.Symbol, ct);

            var body = new JObject
            {
                ["epic"] = request.Symbol,
                ["expiry"] = detail.Expiry,
                ["currencyCode"] = detail.Currency,
                ["direction"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
                ["size"] = request.Qty.ToString(CultureInfo.InvariantCulture),
                ["orderType"] = request.Type switch
                {
                    OrderType.Limit => "LIMIT",
                    OrderType.Market => "MARKET",
                    _ => "MARKET"
                },
                ["guaranteedStop"] = false,
                // forceOpen is required by IG whenever a stop or limit is attached to the deal.
                ["forceOpen"] = request.StopLossPrice.HasValue || request.TakeProfitPrice.HasValue,
                ["timeInForce"] = request.Type == OrderType.Limit ? "FILL_OR_KILL" : "EXECUTE_AND_ELIMINATE",
                ["dealReference"] = dealReference
            };
            if (request.Type == OrderType.Limit && request.LimitPrice.HasValue)
                body["level"] = request.LimitPrice.Value.ToString(CultureInfo.InvariantCulture);
            if (request.StopLossPrice.HasValue)
                body["stopLevel"] = request.StopLossPrice.Value.ToString(CultureInfo.InvariantCulture);
            if (request.TakeProfitPrice.HasValue)
                body["limitLevel"] = request.TakeProfitPrice.Value.ToString(CultureInfo.InvariantCulture);

            string? returnedReference;
            try
            {
                var resp = await client.PostAsync("/positions/otc", body, "2", ct);
                Mark(restHealth, true, null);
                returnedReference = (string?)resp["dealReference"] ?? dealReference;
            }
            catch (Exception ex)
            {
                Mark(restHealth, false, ex.Message);
                // Transport or gateway failure: the deal may or may not exist. Try to resolve it by
                // reference; if that fails too, the outcome stays Unknown and automation is blocked.
                var probe = await GetConfirmationAsync(dealReference, ct);
                if (probe == null) return VenueSubmissionResult.Unknown(ex.Message, dealReference);
                return probe.Status == OrderStatus.Rejected
                    ? VenueSubmissionResult.Rejected(probe.Reason ?? "rejected")
                    : VenueSubmissionResult.Accepted(probe.VenueOrderId, dealReference);
            }

            // A deal reference is not an accepted deal — confirm it.
            var confirmation = await GetConfirmationAsync(returnedReference!, ct);
            if (confirmation == null)
                return VenueSubmissionResult.Unknown("deal reference returned but confirmation unavailable", returnedReference);
            if (confirmation.Status == OrderStatus.Rejected)
                return VenueSubmissionResult.Rejected(confirmation.Reason ?? "IG rejected the deal");
            return VenueSubmissionResult.Accepted(confirmation.VenueOrderId, returnedReference);
        }

        /// <summary>
        /// The dealing currency and expiry for an epic, which IG requires on every order.
        ///
        /// Both come from the market's own details: the currency is the instrument's default (an epic
        /// may quote in several, and dealing in the wrong one is rejected), and the expiry is `DFB`
        /// for a daily funded bet, a contract month for a future, or `-` where there is none. Falling
        /// back to the account currency and `-` keeps an order possible when the lookup fails, which
        /// is the same pair the adapter used to hard-code.
        /// </summary>
        private async Task<(string Currency, string Expiry)> GetDealingTermsAsync(string epic, CancellationToken ct)
        {
            try
            {
                var resp = await client.GetAsync($"/markets/{Uri.EscapeDataString(epic)}", "3", ct);
                Mark(restHealth, true, null);
                var instrument = resp["instrument"] as JObject;
                var currencies = instrument?["currencies"] as JArray;

                string? currency = currencies?.OfType<JObject>()
                                       .FirstOrDefault(c => (bool?)c["isDefault"] == true)?["code"]?.Value<string>()
                                   ?? currencies?.OfType<JObject>().FirstOrDefault()?["code"]?.Value<string>();
                string? expiry = (string?)instrument?["expiry"];

                return (string.IsNullOrWhiteSpace(currency) ? "GBP" : currency!,
                        string.IsNullOrWhiteSpace(expiry) ? "-" : expiry!);
            }
            catch (Exception ex)
            {
                Mark(restHealth, false, ex.Message);
                return ("GBP", "-");
            }
        }

        public async Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken ct = default)
        {
            try
            {
                await client.DeleteAsync($"/workingorders/otc/{Uri.EscapeDataString(venueOrderId)}", new JObject(), "2", ct);
                Mark(restHealth, true, null);
                return true;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); return false; }
        }

        /// <summary>Close an open CFD position by deal id. Separate from cancellation because closing
        /// changes exposure, while cancelling only removes an unfilled instruction.</summary>
        public async Task<VenueSubmissionResult> ClosePositionAsync(string dealId, OrderSide closingSide, decimal size, CancellationToken ct = default)
        {
            var body = new JObject
            {
                ["dealId"] = dealId,
                ["direction"] = closingSide == OrderSide.Buy ? "BUY" : "SELL",
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
                ["orderType"] = "MARKET"
            };
            try
            {
                var resp = await client.DeleteAsync("/positions/otc", body, "1", ct);
                Mark(restHealth, true, null);
                string? reference = (string?)resp["dealReference"];
                if (string.IsNullOrWhiteSpace(reference))
                    return VenueSubmissionResult.Unknown("close accepted with no deal reference");
                var confirmation = await GetConfirmationAsync(reference!, ct);
                if (confirmation == null) return VenueSubmissionResult.Unknown("close confirmation unavailable", reference);
                return confirmation.Status == OrderStatus.Rejected
                    ? VenueSubmissionResult.Rejected(confirmation.Reason ?? "close rejected")
                    : VenueSubmissionResult.Accepted(confirmation.VenueOrderId, reference);
            }
            catch (Exception ex)
            {
                Mark(restHealth, false, ex.Message);
                return VenueSubmissionResult.Unknown(ex.Message);
            }
        }

        // ── market data ───────────────────────────────────────────────────────────

        public async Task<decimal> GetLatestPriceAsync(string venueSymbol, CancellationToken ct = default)
        {
            try
            {
                var resp = await client.GetAsync($"/markets/{Uri.EscapeDataString(venueSymbol)}", "3", ct);
                Mark(restHealth, true, null);
                var snapshot = resp["snapshot"] as JObject;
                decimal bid = Dec(snapshot?["bid"]), offer = Dec(snapshot?["offer"]);
                // Derived mid — kept distinct from the bid/ask the ledger uses for execution costs.
                return bid > 0 && offer > 0 ? (bid + offer) / 2m : Math.Max(bid, offer);
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); return 0m; }
        }

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string venueSymbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            string resolution = interval switch
            {
                TimeInterval.OneMinute => "MINUTE",
                TimeInterval.FiveMinute => "MINUTE_5",
                TimeInterval.FifteenMinute => "MINUTE_15",
                TimeInterval.ThirtyMinute => "MINUTE_30",
                TimeInterval.OneHour => "HOUR",
                TimeInterval.FourHour => "HOUR_4",
                TimeInterval.OneDay => "DAY",
                TimeInterval.OneWeek => "WEEK",
                _ => "HOUR"
            };

            try
            {
                var resp = await client.GetAsync(
                    $"/prices/{Uri.EscapeDataString(venueSymbol)}?resolution={resolution}&max={Math.Clamp(count, 1, 1000)}&pageSize=0", "3", ct);
                Mark(restHealth, true, null);

                var candles = new List<OHLCCandle>();
                if (resp["prices"] is JArray prices)
                {
                    foreach (var p in prices.OfType<JObject>())
                    {
                        // Bid and ask are both published; the engine's bar uses mid so a backtest and a
                        // live mark agree, while the cost model applies the spread separately.
                        decimal Mid(string field)
                        {
                            var node = p[field] as JObject;
                            decimal bid = Dec(node?["bid"]), ask = Dec(node?["ask"]);
                            return bid > 0 && ask > 0 ? (bid + ask) / 2m : Math.Max(bid, ask);
                        }
                        var ts = ParseIgTime((string?)p["snapshotTimeUTC"] ?? (string?)p["snapshotTime"]);
                        if (ts == null) continue;
                        candles.Add(new OHLCCandle(ts.Value, Mid("openPrice"), Mid("highPrice"), Mid("lowPrice"), Mid("closePrice"),
                                                   Dec(p["lastTradedVolume"])));
                    }
                }
                candles.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                return candles;
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); return Array.Empty<OHLCCandle>(); }
        }

        /// <summary>External/manual account activity, imported into the ledger with its origin marked.</summary>
        public async Task<IReadOnlyList<(DateTime Ts, string Description, string? DealId, string? Epic)>> GetActivityAsync(DateTime fromUtc, CancellationToken ct = default)
        {
            var list = new List<(DateTime, string, string?, string?)>();
            try
            {
                var resp = await client.GetAsync($"/history/activity?from={fromUtc:yyyy-MM-ddTHH:mm:ss}&detailed=true&pageSize=100", "3", ct);
                Mark(restHealth, true, null);
                if (resp["activities"] is JArray activities)
                {
                    foreach (var a in activities.OfType<JObject>())
                    {
                        var ts = ParseIgTime((string?)a["date"]);
                        if (ts == null) continue;
                        list.Add((ts.Value,
                                  $"{(string?)a["type"]} {(string?)a["status"]} {(string?)a["description"]}".Trim(),
                                  (string?)a["dealId"], (string?)a["epic"]));
                    }
                }
            }
            catch (Exception ex) { Mark(restHealth, false, ex.Message); }
            return list;
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private static AssetClass MapAssetClass(string? igType) => (igType ?? "").ToUpperInvariant() switch
        {
            "SHARES" => AssetClass.Equity,
            "INDICES" => AssetClass.Index,
            "CURRENCIES" => AssetClass.Forex,
            "COMMODITIES" => AssetClass.Commodity,
            "CRYPTOCURRENCY" => AssetClass.Crypto,
            _ => AssetClass.Unknown
        };

        /// <summary>IG deal references allow [A-Za-z0-9_-] up to 30 characters.</summary>
        public static string SanitiseReference(string raw)
        {
            var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            string s = new string(chars);
            if (s.Length == 0) s = Guid.NewGuid().ToString("N");
            return s.Length <= 30 ? s : s[..30];
        }

        private static decimal Dec(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0m;
            if (t.Type is JTokenType.Float or JTokenType.Integer) return (decimal)t;
            return decimal.TryParse((string?)t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        private static DateTime? ParseIgTime(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] formats =
            {
                "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff",
                "yyyy/MM/dd HH:mm:ss:fff", "dd/MM/yy HH:mm:ss", "yyyy-MM-dd"
            };
            if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
                return exact;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose))
                return loose;
            return null;
        }

        private static void Mark(ChannelHealth h, bool ok, string? error)
        {
            h.Connected = ok;
            if (ok) { h.LastOkUtc = DateTime.UtcNow; h.ConsecutiveFailures = 0; h.LastError = null; }
            else { h.LastErrorUtc = DateTime.UtcNow; h.LastError = error; h.ConsecutiveFailures++; }
        }
    }
}
