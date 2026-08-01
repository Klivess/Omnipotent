using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Portfolio
{
    /// <summary>The firm view. Owned inventory and derivative notional are reported separately and
    /// summed only where that is economically meaningful.</summary>
    public sealed class FirmPortfolioView
    {
        public required string ReportingCurrency { get; init; }
        public required DateTime AsOfUtc { get; init; }

        /// <summary>Cash across all accounts, in the reporting currency.</summary>
        public decimal Cash { get; set; }
        /// <summary>Marked value of owned inventory (Kraken spot). This is an asset.</summary>
        public decimal InventoryValue { get; set; }
        /// <summary>IG account equity as the broker reports it (balance + open P&amp;L).</summary>
        public decimal DerivativeEquity { get; set; }
        /// <summary>Gross CFD notional. Deliberately NOT added to asset value — it is exposure, not
        /// something the firm owns.</summary>
        public decimal DerivativeNotional { get; set; }

        public decimal UnrealizedPnL { get; set; }
        public decimal RealizedPnLToday { get; set; }
        public decimal CostsToday { get; set; }

        public decimal GrossExposure { get; set; }
        public decimal NetExposure { get; set; }

        /// <summary>Total firm value: cash + owned inventory + broker-reported derivative equity.</summary>
        public decimal TotalValue => Cash + InventoryValue + DerivativeEquity;

        public List<PortfolioLine> Lines { get; init; } = new();
        public Dictionary<string, decimal> ExposureByAssetClass { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, decimal> ExposureByCurrency { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, decimal> ExposureByVenue { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, decimal> ExposureByStrategy { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Valuation> Valuations { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    public sealed class PortfolioLine
    {
        public required string InstrumentId { get; init; }
        public required string DisplayName { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required ExposureKind Exposure { get; init; }
        public required decimal Quantity { get; init; }
        public decimal AveragePrice { get; init; }
        public decimal? MarkPrice { get; init; }
        public decimal Notional { get; init; }
        public decimal UnrealizedPnL { get; init; }
        public decimal RealizedPnL { get; init; }
        public string? StrategyId { get; init; }
        public string NativeCurrency { get; init; } = "USD";
        public decimal? VenueQuantity { get; init; }
        public bool Disagrees { get; init; }
    }

    /// <summary>
    /// Builds the combined portfolio picture and the risk engine's view of it.
    ///
    /// Two rules shape everything here: native currency and quantity are preserved and never
    /// overwritten by the reporting-currency conversion, and leveraged CFD notional is never added to
    /// owned spot value as though both were assets.
    /// </summary>
    public sealed class PortfolioService
    {
        private readonly FirmLedger ledger;
        private readonly InstrumentMaster instruments;
        private readonly VenueRegistry venues;
        private readonly MarketDataRouter marketData;
        private readonly LedgerRepository ledgerRepo;
        private readonly AccountRepository accountRepo;
        private readonly ConcurrentDictionary<string, (decimal Rate, DateTime AsOf, string Source)> fxCache = new(StringComparer.OrdinalIgnoreCase);

        public string ReportingCurrency { get; set; } = "GBP";
        public decimal PeakEquity { get; private set; }

        public PortfolioService(FirmLedger ledger, InstrumentMaster instruments, VenueRegistry venues,
            MarketDataRouter marketData, LedgerRepository ledgerRepo, AccountRepository accountRepo)
        {
            this.ledger = ledger;
            this.instruments = instruments;
            this.venues = venues;
            this.marketData = marketData;
            this.ledgerRepo = ledgerRepo;
            this.accountRepo = accountRepo;
        }

        public async Task<FirmPortfolioView> BuildAsync(CancellationToken ct = default)
        {
            var view = new FirmPortfolioView { ReportingCurrency = ReportingCurrency, AsOfUtc = DateTime.UtcNow };

            // Refresh marks for everything we hold before measuring anything.
            var marks = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in ledger.Positions)
            {
                if (marks.ContainsKey(position.InstrumentId)) continue;
                decimal mark = await GetMarkAsync(position.InstrumentId, position.Venue, ct);
                if (mark > 0m) marks[position.InstrumentId] = mark;
            }
            ledger.ApplyMarks(marks);

            foreach (var position in ledger.Positions)
            {
                var instrument = instruments.Get(position.InstrumentId);
                string native = instrument?.QuoteCurrency ?? "USD";
                decimal rate = await GetRateAsync(native, ct);

                decimal notionalNative = position.Notional;
                decimal notionalReporting = notionalNative * rate;

                view.Lines.Add(new PortfolioLine
                {
                    InstrumentId = position.InstrumentId,
                    DisplayName = instrument?.DisplayName ?? position.InstrumentId,
                    Venue = position.Venue,
                    Environment = position.Environment,
                    Exposure = position.Exposure,
                    Quantity = position.Quantity,
                    AveragePrice = position.AveragePrice,
                    MarkPrice = position.MarkPrice,
                    Notional = notionalReporting,
                    UnrealizedPnL = position.UnrealizedPnL * rate,
                    RealizedPnL = position.RealizedPnL * rate,
                    StrategyId = position.StrategyId,
                    NativeCurrency = native,
                    VenueQuantity = position.VenueQuantity,
                    Disagrees = position.Disagrees
                });

                view.UnrealizedPnL += position.UnrealizedPnL * rate;
                view.GrossExposure += notionalReporting;
                view.NetExposure += position.SignedNotional * rate;

                if (position.Exposure == ExposureKind.Inventory) view.InventoryValue += position.SignedNotional * rate;
                else view.DerivativeNotional += notionalReporting;

                Accumulate(view.ExposureByAssetClass, (instrument?.AssetClass ?? AssetClass.Unknown).ToString(), notionalReporting);
                Accumulate(view.ExposureByCurrency, native, notionalReporting);
                Accumulate(view.ExposureByVenue, position.Venue.ToString(), notionalReporting);
                Accumulate(view.ExposureByStrategy, position.StrategyId ?? "manual", notionalReporting);

                if (position.Disagrees)
                    view.Warnings.Add($"{position.InstrumentId}: internal {position.Quantity} vs venue {position.VenueQuantity}.");
            }

            // Cash, converted but never overwritten in its native form.
            foreach (var (key, amount) in ledger.CashBalances)
            {
                string currency = key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : "USD";
                decimal rate = await GetRateAsync(currency, ct);
                view.Cash += amount * rate;
                if (amount != 0m)
                    view.Valuations.Add(new Valuation
                    {
                        Asset = currency,
                        NativeAmount = amount,
                        Rate = rate,
                        ReportingCurrency = ReportingCurrency,
                        ReportingAmount = amount * rate,
                        Source = fxCache.TryGetValue(currency, out var c) ? c.Source : "identity",
                        AsOfUtc = DateTime.UtcNow
                    });
            }

            // Broker-reported derivative equity — the CFD account's own number, not a derived one.
            foreach (var adapter in venues.All.Where(a => a.Capabilities.Exposure == ExposureKind.Derivative))
            {
                try
                {
                    var account = await adapter.GetAccountAsync(ct);
                    decimal rate = await GetRateAsync(account.BaseCurrency, ct);
                    view.DerivativeEquity += (account.Equity ?? account.Balance ?? 0m) * rate;
                }
                catch (Exception ex) { view.Warnings.Add($"{adapter.Venue} account unavailable: {ex.Message}"); }
            }

            var (realized, costs) = await ledgerRepo.SumSinceAsync(DateTime.UtcNow.Date, null, ct);
            view.RealizedPnLToday = realized;
            view.CostsToday = costs;

            if (view.TotalValue > PeakEquity) PeakEquity = view.TotalValue;
            return view;
        }

        /// <summary>The compact state the risk engine measures proposals against.</summary>
        public async Task<RiskPortfolioState> BuildRiskStateAsync(CancellationToken ct = default)
        {
            var view = await BuildAsync(ct);
            var exposureByInstrument = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in view.Lines)
                exposureByInstrument[line.InstrumentId] =
                    (exposureByInstrument.TryGetValue(line.InstrumentId, out var e) ? e : 0m)
                    + line.Quantity * (line.MarkPrice ?? line.AveragePrice);

            var byVenue = new Dictionary<VenueId, decimal>();
            foreach (var line in view.Lines)
                byVenue[line.Venue] = (byVenue.TryGetValue(line.Venue, out var v) ? v : 0m) + line.Notional;

            var freeInventory = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            decimal? availableFunds = null;
            foreach (var adapter in venues.All)
            {
                try
                {
                    var account = await adapter.GetAccountAsync(ct);
                    foreach (var (asset, qty) in account.Inventory)
                    {
                        decimal reserved = account.Reserved.TryGetValue(asset, out var r) ? r : 0m;
                        freeInventory[asset] = Math.Max(0m, qty - reserved);
                    }
                    if (account.AvailableFunds is { } funds) availableFunds = (availableFunds ?? 0m) + funds;
                }
                catch { /* an unreachable venue contributes no free inventory — which fails safe */ }
            }

            var openByStrategy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in view.Lines)
            {
                string key = line.StrategyId ?? "manual";
                openByStrategy[key] = (openByStrategy.TryGetValue(key, out var c) ? c : 0) + 1;
            }

            return new RiskPortfolioState
            {
                GrossExposure = view.GrossExposure,
                NetExposure = view.NetExposure,
                Equity = view.TotalValue,
                PeakEquity = Math.Max(PeakEquity, view.TotalValue),
                DailyRealizedPnL = view.RealizedPnLToday + view.CostsToday,
                ExposureByInstrument = exposureByInstrument,
                ExposureByVenue = byVenue,
                DailyPnLByStrategy = await ledger.DailyPnLByStrategyAsync(ct),
                OpenPositionsByStrategy = openByStrategy,
                FreeInventory = freeInventory,
                AvailableFunds = availableFunds
            };
        }

        public Task<List<(DateTime Ts, VenueId Venue, decimal Value)>> ValueSeriesAsync(DateTime? fromUtc = null, CancellationToken ct = default)
            => accountRepo.SnapshotSeriesAsync(fromUtc, ct);

        /// <summary>Mark for an instrument, preferring the venue it is held on and falling back to the
        /// shared market-data router so a venue outage does not blank the whole portfolio.</summary>
        public async Task<decimal> GetMarkAsync(string instrumentId, VenueId venue, CancellationToken ct = default)
        {
            var adapter = venues.ResolveUnambiguous(venue) ?? venues.All.FirstOrDefault(a => a.Venue == venue);
            if (adapter != null)
            {
                try
                {
                    string venueSymbol = instruments.VenueSymbolFor(instrumentId, venue);
                    decimal price = await adapter.GetLatestPriceAsync(venueSymbol, ct);
                    if (price > 0m)
                    {
                        instruments.NoteDataUpdate(instrumentId, adapter.Venue.ToString());
                        return price;
                    }
                }
                catch { }
            }
            try
            {
                decimal price = await marketData.GetLatestPriceAsync(instruments.EngineSymbolFor(instrumentId));
                if (price > 0m) instruments.NoteDataUpdate(instrumentId, "market-data-router");
                return price;
            }
            catch { return 0m; }
        }

        /// <summary>
        /// Rate from a native currency into the reporting currency. Crypto quote assets are treated as
        /// USD-equivalents; fiat crosses come from the venue's own crypto pairs so no extra data
        /// provider is required. An unresolvable rate returns 1 and is recorded as such rather than
        /// silently distorting the total.
        /// </summary>
        public async Task<decimal> GetRateAsync(string nativeCurrency, CancellationToken ct = default)
        {
            string native = Normalise(nativeCurrency);
            string reporting = Normalise(ReportingCurrency);
            if (string.Equals(native, reporting, StringComparison.OrdinalIgnoreCase)) return 1m;

            if (fxCache.TryGetValue(native, out var cached) && DateTime.UtcNow - cached.AsOf < TimeSpan.FromMinutes(30)
                && string.Equals(cached.Source, reporting, StringComparison.OrdinalIgnoreCase) == false)
                return cached.Rate;

            decimal rate = 1m;
            string source = "identity";
            try
            {
                // BTC priced in both currencies gives the cross without another data source.
                decimal nativeLeg = await marketData.GetLatestPriceAsync("BTC" + (native == "USD" ? "USDT" : native));
                decimal reportingLeg = await marketData.GetLatestPriceAsync("BTC" + (reporting == "USD" ? "USDT" : reporting));
                if (nativeLeg > 0m && reportingLeg > 0m)
                {
                    rate = nativeLeg / reportingLeg;
                    source = $"BTC cross ({native}->{reporting})";
                }
            }
            catch { }

            fxCache[native] = (rate, DateTime.UtcNow, source);
            return rate;
        }

        private static string Normalise(string currency) => currency.ToUpperInvariant() switch
        {
            "USDT" or "USDC" or "ZUSD" => "USD",
            "ZGBP" => "GBP",
            "ZEUR" => "EUR",
            var other => other
        };

        private static void Accumulate(Dictionary<string, decimal> map, string key, decimal value)
            => map[key] = (map.TryGetValue(key, out var existing) ? existing : 0m) + value;
    }
}
