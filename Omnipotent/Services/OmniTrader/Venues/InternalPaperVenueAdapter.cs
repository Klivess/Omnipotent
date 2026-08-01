using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// The firm's own paper venue. It behaves like a broker — orders get an identifier, resolve to an
    /// outcome, and can be queried back — so the order service, ledger and reconciliation exercise the
    /// exact same code path they use for Kraken and IG. Its ledger and audit scope are entirely
    /// separate from any real environment.
    ///
    /// Fills are simulated against live market data with an explicit cost model, so paper results stay
    /// comparable to demo and live rather than being optimistic.
    /// </summary>
    public sealed class InternalPaperVenueAdapter : IVenueAdapter
    {
        private readonly MarketDataRouter marketData;
        private readonly ConcurrentDictionary<string, VenueOrderSnapshot> orders = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, decimal> inventory = new(StringComparer.OrdinalIgnoreCase);
        private readonly ChannelHealth health = new() { Channel = "internal-paper", Connected = true, LastOkUtc = DateTime.UtcNow };
        private readonly object bookLock = new();

        public decimal FeeFraction { get; set; } = 0.001m;
        public decimal SlippageFraction { get; set; } = 0.0005m;
        public decimal CashBalance { get; private set; }

        public VenueId Venue => VenueId.Internal;
        public TradingEnvironment Environment => TradingEnvironment.Paper;
        public bool IsConfigured => true;

        public VenueCapabilities Capabilities { get; } = new()
        {
            Venue = VenueId.Internal,
            DisplayName = "Internal Paper Simulator",
            Exposure = ExposureKind.Inventory,
            AssetClasses = new[] { AssetClass.Crypto, AssetClass.Index, AssetClass.Equity, AssetClass.Forex, AssetClass.Commodity },
            SupportsShort = true,
            SupportsLeverage = true,
            MaxLeverage = 10m,
            SupportsAttachedProtection = true,
            SupportsStreamingPrices = true,
            SupportsStreamingAccount = true,
            SupportsHistoricalData = true,
            SupportsDemoEnvironment = false,
            OrderTypes = new[] { OrderType.Market, OrderType.Limit, OrderType.StopLoss, OrderType.TakeProfit },
            Limitations =
            {
                ["Fills"] = "Fills are simulated at the last mark plus a configured slippage and fee; real queue position and partial liquidity are not modelled."
            }
        };

        public InternalPaperVenueAdapter(MarketDataRouter marketData, decimal startingCash = 10_000m)
        {
            this.marketData = marketData;
            CashBalance = startingCash;
        }

        public VenueHealthSnapshot Health => new()
        {
            Venue = VenueId.Internal,
            Environment = TradingEnvironment.Paper,
            Configured = true,
            Channels = new List<ChannelHealth> { health }
        };

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<VenueInstrumentDescriptor>> GetInstrumentsAsync(string? search = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VenueInstrumentDescriptor>>(Array.Empty<VenueInstrumentDescriptor>());

        public Task<VenueAccountSnapshot> GetAccountAsync(CancellationToken ct = default)
        {
            lock (bookLock)
            {
                return Task.FromResult(new VenueAccountSnapshot
                {
                    Venue = VenueId.Internal,
                    AccountId = "paper",
                    Environment = TradingEnvironment.Paper,
                    AsOfUtc = DateTime.UtcNow,
                    BaseCurrency = "USD",
                    Balance = CashBalance,
                    AvailableFunds = CashBalance,
                    Inventory = new Dictionary<string, decimal>(inventory, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        public async Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            var snapshot = inventory.ToArray();
            var list = new List<VenuePositionSnapshot>();
            foreach (var (symbol, qty) in snapshot)
            {
                if (qty == 0m) continue;
                decimal mark = 0m;
                try { mark = await marketData.GetLatestPriceAsync(symbol); } catch { }
                list.Add(new VenuePositionSnapshot
                {
                    Venue = VenueId.Internal,
                    VenueSymbol = symbol,
                    Quantity = qty,
                    Exposure = ExposureKind.Inventory,
                    MarkPrice = mark > 0 ? mark : null
                });
            }
            return list;
        }

        public Task<IReadOnlyList<VenueOrderSnapshot>> GetWorkingOrdersAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VenueOrderSnapshot>>(
                orders.Values.Where(o => o.Status is OrderStatus.Open or OrderStatus.PartiallyFilled).ToList());

        public Task<IReadOnlyList<VenueOrderSnapshot>> QueryOrdersAsync(IEnumerable<string> venueOrderIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VenueOrderSnapshot>>(
                venueOrderIds.Where(id => !string.IsNullOrWhiteSpace(id) && orders.ContainsKey(id))
                             .Select(id => orders[id]).ToList());

        public async Task<VenueSubmissionResult> SubmitOrderAsync(OrderRequest request, string clientReference, CancellationToken ct = default)
        {
            decimal mark = 0m;
            try { mark = await marketData.GetLatestPriceAsync(request.Symbol); } catch { }
            if (mark <= 0m) mark = request.LimitPrice ?? 0m;
            if (mark <= 0m) return VenueSubmissionResult.Rejected("no mark price available to simulate a fill");

            decimal fillPrice = request.Side == OrderSide.Buy
                ? mark * (1m + SlippageFraction)
                : mark * (1m - SlippageFraction);
            decimal notional = fillPrice * request.Qty;
            decimal fee = notional * FeeFraction;

            string id = "paper-" + Guid.NewGuid().ToString("N")[..12];
            lock (bookLock)
            {
                decimal signed = request.Side == OrderSide.Buy ? request.Qty : -request.Qty;
                CashBalance += request.Side == OrderSide.Buy ? -(notional + fee) : (notional - fee);
                inventory.AddOrUpdate(request.Symbol, signed, (_, existing) => existing + signed);

                orders[id] = new VenueOrderSnapshot
                {
                    Venue = VenueId.Internal,
                    VenueOrderId = id,
                    VenueSymbol = request.Symbol,
                    Side = request.Side,
                    Quantity = request.Qty,
                    FilledQuantity = request.Qty,
                    AverageFillPrice = fillPrice,
                    Fee = fee,
                    FeeCurrency = "USD",
                    Status = OrderStatus.Filled,
                    ClientReference = clientReference,
                    CreatedUtc = DateTime.UtcNow
                };
            }
            return VenueSubmissionResult.Accepted(id, clientReference);
        }

        public Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken ct = default)
        {
            if (!orders.TryGetValue(venueOrderId, out var existing)) return Task.FromResult(false);
            if (existing.Status == OrderStatus.Filled) return Task.FromResult(false);
            orders[venueOrderId] = new VenueOrderSnapshot
            {
                Venue = existing.Venue,
                VenueOrderId = existing.VenueOrderId,
                VenueSymbol = existing.VenueSymbol,
                Side = existing.Side,
                Quantity = existing.Quantity,
                FilledQuantity = existing.FilledQuantity,
                AverageFillPrice = existing.AverageFillPrice,
                Fee = existing.Fee,
                FeeCurrency = existing.FeeCurrency,
                Status = OrderStatus.Cancelled,
                ClientReference = existing.ClientReference,
                CreatedUtc = existing.CreatedUtc
            };
            return Task.FromResult(true);
        }

        public async Task<decimal> GetLatestPriceAsync(string venueSymbol, CancellationToken ct = default)
        {
            try { return await marketData.GetLatestPriceAsync(venueSymbol); } catch { return 0m; }
        }

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string venueSymbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            try { return await marketData.GetHistoricalCandlesAsync(venueSymbol, interval, count); }
            catch { return Array.Empty<OHLCCandle>(); }
        }
    }
}
