using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Portfolio
{
    /// <summary>
    /// One side of the book — real money or simulated. Every value figure the platform reports is
    /// one of these, never a mixture, because a paper balance added to a real one is not a number
    /// anybody can act on.
    /// </summary>
    public sealed class PortfolioTotals
    {
        public decimal Cash { get; set; }
        public decimal InventoryValue { get; set; }
        public decimal DerivativeEquity { get; set; }
        public decimal DerivativeNotional { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal GrossExposure { get; set; }
        public decimal NetExposure { get; set; }
        public int Positions { get; set; }

        public decimal TotalValue => Cash + InventoryValue + DerivativeEquity;
    }

    /// <summary>The firm view. Owned inventory and derivative notional are reported separately and
    /// summed only where that is economically meaningful.</summary>
    public sealed class FirmPortfolioView
    {
        public required string ReportingCurrency { get; init; }
        public required DateTime AsOfUtc { get; init; }

        /// <summary>Real money: live broker accounts only.</summary>
        public PortfolioTotals Real { get; init; } = new();
        /// <summary>The paper simulator and broker demo accounts. Never added to anything above.</summary>
        public PortfolioTotals Simulated { get; init; } = new();

        // The headline fields are the *real* ones. The internal paper trader holds no money, so
        // counting it toward firm value would report wealth that does not exist.
        public decimal Cash => Real.Cash;
        public decimal InventoryValue => Real.InventoryValue;
        public decimal DerivativeEquity => Real.DerivativeEquity;
        public decimal DerivativeNotional => Real.DerivativeNotional;
        public decimal UnrealizedPnL => Real.UnrealizedPnL;
        public decimal GrossExposure => Real.GrossExposure;
        public decimal NetExposure => Real.NetExposure;

        public decimal RealizedPnLToday { get; set; }
        public decimal CostsToday { get; set; }
        public decimal SimulatedRealizedPnLToday { get; set; }

        /// <summary>Total firm value: cash + owned inventory + broker-reported derivative equity,
        /// across live accounts only.</summary>
        public decimal TotalValue => Real.TotalValue;
        public decimal SimulatedTotalValue => Simulated.TotalValue;
        /// <summary>True when nothing real is configured — the UI says "simulated only" rather than
        /// presenting a £0 firm as a loss.</summary>
        public bool HasRealAccounts { get; set; }

        /// <summary>
        /// False when a live venue could not be reached, so part of the firm's money is unaccounted
        /// for in these totals. The figure is still shown (with its warning), but it must never be
        /// written to the value history: a broker outage would otherwise be recorded as a crash.
        /// </summary>
        public bool ValuationComplete { get; set; } = true;

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
        private readonly FirmValueRepository valueRepo;
        private readonly ConcurrentDictionary<string, (decimal Rate, DateTime AsOf, string Source)> fxCache = new(StringComparer.OrdinalIgnoreCase);

        public string ReportingCurrency { get; set; } = "GBP";
        public decimal PeakEquity { get; private set; }

        public PortfolioService(FirmLedger ledger, InstrumentMaster instruments, VenueRegistry venues,
            MarketDataRouter marketData, LedgerRepository ledgerRepo, AccountRepository accountRepo,
            FirmValueRepository valueRepo)
        {
            this.ledger = ledger;
            this.instruments = instruments;
            this.venues = venues;
            this.marketData = marketData;
            this.ledgerRepo = ledgerRepo;
            this.accountRepo = accountRepo;
            this.valueRepo = valueRepo;
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
                var totals = IsRealMoney(position.Environment) ? view.Real : view.Simulated;

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

                totals.UnrealizedPnL += position.UnrealizedPnL * rate;
                totals.GrossExposure += notionalReporting;
                totals.NetExposure += position.SignedNotional * rate;
                totals.Positions++;

                if (position.Exposure == ExposureKind.Inventory) totals.InventoryValue += position.SignedNotional * rate;
                else totals.DerivativeNotional += notionalReporting;

                Accumulate(view.ExposureByAssetClass, (instrument?.AssetClass ?? AssetClass.Unknown).ToString(), notionalReporting);
                Accumulate(view.ExposureByCurrency, native, notionalReporting);
                Accumulate(view.ExposureByVenue, position.Venue.ToString(), notionalReporting);
                Accumulate(view.ExposureByStrategy, position.StrategyId ?? "manual", notionalReporting);

                if (position.Disagrees)
                    view.Warnings.Add($"{position.InstrumentId}: internal {position.Quantity} vs venue {position.VenueQuantity}.");
            }

            // Cash, converted but never overwritten in its native form. The account id prefix carries
            // the environment, so simulated cash lands on the simulated side.
            foreach (var (key, amount) in ledger.CashBalances)
            {
                string currency = key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : "USD";
                string account = key.Contains('|') ? key[..key.IndexOf('|')] : key;
                decimal rate = await GetRateAsync(currency, ct);
                (IsRealMoneyAccount(account) ? view.Real : view.Simulated).Cash += amount * rate;
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
                    (IsRealMoney(adapter.Environment) ? view.Real : view.Simulated).DerivativeEquity
                        += (account.Equity ?? account.Balance ?? 0m) * rate;
                }
                catch (Exception ex)
                {
                    view.Warnings.Add($"{adapter.Venue} account unavailable: {ex.Message}");
                    if (IsRealMoney(adapter.Environment)) view.ValuationComplete = false;
                }
            }

            view.HasRealAccounts = venues.All.Any(a => IsRealMoney(a.Environment) && a.IsConfigured);

            var (realized, costs) = await ledgerRepo.SumSinceAsync(DateTime.UtcNow.Date, null, RealEnvironments, ct);
            view.RealizedPnLToday = realized;
            view.CostsToday = costs;
            var (simRealized, _) = await ledgerRepo.SumSinceAsync(DateTime.UtcNow.Date, null, SimulatedEnvironments, ct);
            view.SimulatedRealizedPnLToday = simRealized;

            if (view.TotalValue > PeakEquity) PeakEquity = view.TotalValue;
            return view;
        }

        /// <summary>
        /// Only live broker accounts hold real money. The built-in paper trader and broker demo
        /// accounts are simulations: useful for measuring a strategy, worthless as a statement of
        /// what the firm is worth, and never summed into one.
        /// </summary>
        public static bool IsRealMoney(TradingEnvironment environment) => environment == TradingEnvironment.Live;

        private static readonly string[] RealEnvironments = { nameof(TradingEnvironment.Live) };
        private static readonly string[] SimulatedEnvironments =
            { nameof(TradingEnvironment.Paper), nameof(TradingEnvironment.Demo), nameof(TradingEnvironment.Historical) };

        /// <summary>Account ids are `{venue}-{environment}`, so the environment is recoverable without
        /// a second lookup. An unparseable id is treated as simulated — the safe direction to fail.</summary>
        private static bool IsRealMoneyAccount(string accountId)
            => accountId.EndsWith($"-{nameof(TradingEnvironment.Live)}", StringComparison.OrdinalIgnoreCase);

        /// <summary>The compact state the risk engine measures proposals against.</summary>
        public async Task<RiskPortfolioState> BuildRiskStateAsync(CancellationToken ct = default)
            => await BuildRiskStateAsync(await BuildAsync(ct), ct);

        /// <summary>Overload for callers that have already built the portfolio — valuing the whole
        /// firm hits every venue, and doing it twice in one sweep is pure latency.</summary>
        public async Task<RiskPortfolioState> BuildRiskStateAsync(FirmPortfolioView view, CancellationToken ct = default)
        {
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

        /// <summary>
        /// Firm value over time — real money only, in the reporting currency, one point per
        /// valuation. Recorded by <see cref="RecordValuePointAsync"/> rather than derived from broker
        /// balances, because "what is the firm worth" is a whole-firm question and no single account
        /// snapshot answers it.
        /// </summary>
        public Task<List<FirmValuePoint>> ValueSeriesAsync(DateTime? fromUtc = null, CancellationToken ct = default)
            => valueRepo.SeriesAsync(fromUtc, ct);

        /// <summary>
        /// Write one point of firm value history. An incomplete valuation is skipped rather than
        /// recorded: a venue that failed to answer means money is missing from the total, and a dip
        /// caused by an outage is indistinguishable from a real one once it is in the chart.
        /// </summary>
        public async Task<bool> RecordValuePointAsync(FirmPortfolioView view, CancellationToken ct = default)
        {
            if (!view.ValuationComplete) return false;
            await valueRepo.RecordAsync(ToValuePoint(view), ct);
            return true;
        }

        /// <summary>
        /// The one place a portfolio view becomes a value point. Every money field is taken from the
        /// <em>real</em> side; the simulated total rides along in its own field so research can see it
        /// without any arithmetic being able to pull it into firm value.
        /// </summary>
        public static FirmValuePoint ToValuePoint(FirmPortfolioView view) => new()
        {
            Ts = view.AsOfUtc,
            Currency = view.ReportingCurrency,
            TotalValue = view.TotalValue,
            Cash = view.Cash,
            InventoryValue = view.InventoryValue,
            DerivativeEquity = view.DerivativeEquity,
            DerivativeNotional = view.DerivativeNotional,
            GrossExposure = view.GrossExposure,
            UnrealizedPnL = view.UnrealizedPnL,
            RealizedPnLToday = view.RealizedPnLToday,
            Positions = view.Real.Positions,
            HasRealAccounts = view.HasRealAccounts,
            SimulatedValue = view.SimulatedTotalValue
        };

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
