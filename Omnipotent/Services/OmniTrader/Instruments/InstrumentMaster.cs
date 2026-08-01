using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Instruments
{
    /// <summary>
    /// Owns canonical instrument identity across venues. It folds each venue's directory into shared
    /// records so a strategy asking for <c>crypto:BTC/USD</c> gets the right Kraken pair, IG epic and
    /// Binance symbol without knowing any of them.
    ///
    /// It does not own price history — that stays with the market-data service.
    /// </summary>
    public sealed class InstrumentMaster
    {
        private readonly InstrumentRepository repo;
        private readonly VenueRegistry venues;
        private readonly ConcurrentDictionary<string, Instrument> byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> venueSymbolToId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (DateTime Ts, string Source)> lastSeen = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim refreshLock = new(1, 1);

        public DateTime? LastRefreshUtc { get; private set; }
        public int Count => byId.Count;

        public InstrumentMaster(InstrumentRepository repo, VenueRegistry venues)
        {
            this.repo = repo;
            this.venues = venues;
        }

        public async Task LoadAsync(CancellationToken ct = default)
        {
            foreach (var instrument in await repo.ListAllAsync(ct)) Index(instrument);
            SeedDefaults();
        }

        public IReadOnlyList<Instrument> All => byId.Values.OrderBy(i => i.DisplayName).ToList();

        public Instrument? Get(string instrumentId)
            => byId.TryGetValue(instrumentId, out var i) ? i : null;

        /// <summary>Resolve any identifier a caller might hold — canonical id, venue symbol, or a bare
        /// engine symbol like <c>BTCUSDT</c> — to the canonical instrument.</summary>
        public Instrument? Resolve(string identifier, VenueId? venue = null)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return null;
            if (byId.TryGetValue(identifier, out var direct)) return direct;

            if (venue.HasValue && venueSymbolToId.TryGetValue($"{venue.Value}:{identifier}", out var mapped))
                return Get(mapped);

            foreach (var v in Enum.GetValues<VenueId>())
                if (venueSymbolToId.TryGetValue($"{v}:{identifier}", out var any))
                    return Get(any);

            // Last resort: interpret it as an engine pair (BTCUSDT) so legacy call sites keep working.
            var (baseAsset, quote) = SplitEnginePair(identifier);
            return string.IsNullOrEmpty(baseAsset) ? null : Get(Instrument.MakeId(AssetClass.Crypto, baseAsset, quote));
        }

        public IReadOnlyList<Instrument> Search(string term, int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(term)) return All.Take(limit).ToList();
            return byId.Values
                .Where(i => i.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                         || i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                         || i.Venues.Any(v => v.VenueSymbol.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(i => i.DisplayName)
                .Take(limit)
                .ToList();
        }

        /// <summary>Pull every registered venue's directory and fold it into the master.</summary>
        public async Task<int> RefreshFromVenuesAsync(CancellationToken ct = default)
        {
            await refreshLock.WaitAsync(ct);
            try
            {
                int added = 0;
                foreach (var adapter in venues.All)
                {
                    IReadOnlyList<VenueInstrumentDescriptor> descriptors;
                    try { descriptors = await adapter.GetInstrumentsAsync(null, ct); }
                    catch { continue; }

                    foreach (var d in descriptors)
                        if (Upsert(d, adapter.Capabilities.Exposure)) added++;
                }
                LastRefreshUtc = DateTime.UtcNow;
                await PersistAsync(ct);
                return added;
            }
            finally { refreshLock.Release(); }
        }

        /// <summary>Fold one venue descriptor into the master, creating or extending the canonical
        /// record. Returns true when a new canonical instrument was created.</summary>
        public bool Upsert(VenueInstrumentDescriptor descriptor, ExposureKind exposure)
        {
            string baseAsset = string.IsNullOrWhiteSpace(descriptor.BaseAsset) ? descriptor.VenueSymbol : descriptor.BaseAsset;
            string quote = string.IsNullOrWhiteSpace(descriptor.QuoteCurrency) ? "USD" : descriptor.QuoteCurrency;
            // Stablecoin quotes all collapse onto USD for identity purposes; the venue mapping keeps
            // the real quote asset so orders are still sized in the right currency.
            string identityQuote = quote is "USDT" or "USDC" or "ZUSD" ? "USD" : quote;
            string id = Instrument.MakeId(descriptor.AssetClass, baseAsset, identityQuote);

            var mapping = new VenueMapping
            {
                Venue = descriptor.Venue,
                VenueSymbol = descriptor.VenueSymbol,
                TickSize = descriptor.TickSize,
                QuantityStep = descriptor.QuantityStep,
                MinQuantity = descriptor.MinQuantity,
                MaxQuantity = descriptor.MaxQuantity,
                ContractMultiplier = descriptor.ContractMultiplier,
                MarginFactor = descriptor.MarginFactor,
                Tradeable = descriptor.Tradeable,
                TradingStatus = descriptor.TradingStatus,
                TradingHours = descriptor.TradingHours
            };

            bool created = false;
            var instrument = byId.GetOrAdd(id, _ =>
            {
                created = true;
                return new Instrument
                {
                    Id = id,
                    DisplayName = descriptor.DisplayName,
                    AssetClass = descriptor.AssetClass,
                    BaseAsset = baseAsset,
                    QuoteCurrency = identityQuote,
                    ContractMultiplier = descriptor.ContractMultiplier,
                    Exposure = exposure
                };
            });

            lock (instrument.Venues)
            {
                instrument.Venues.RemoveAll(v => v.Venue == descriptor.Venue);
                instrument.Venues.Add(mapping);
            }
            venueSymbolToId[$"{descriptor.Venue}:{descriptor.VenueSymbol}"] = id;
            return created;
        }

        /// <summary>Record that fresh data was observed for an instrument. Freshness is what the risk
        /// engine's data-integrity layer blocks on, so it is tracked centrally rather than per caller.</summary>
        public void NoteDataUpdate(string instrumentId, string source, DateTime? tsUtc = null)
        {
            lastSeen[instrumentId] = (tsUtc ?? DateTime.UtcNow, source);
            if (byId.TryGetValue(instrumentId, out var instrument))
            {
                instrument.LastUpdatedUtc = tsUtc ?? DateTime.UtcNow;
                instrument.DataSource = source;
            }
        }

        public DataFreshness GetFreshness(string instrumentId)
        {
            var instrument = Get(instrumentId);
            if (!lastSeen.TryGetValue(instrumentId, out var seen))
            {
                return new DataFreshness
                {
                    InstrumentId = instrumentId,
                    Age = TimeSpan.MaxValue,
                    Stale = true,
                    Issue = "no data observed for this instrument in this process"
                };
            }
            var raw = DateTime.UtcNow - seen.Ts;

            // A bar can be stamped ahead of the clock — providers stamp the *forming* bar with its
            // close time, so a 4-hour candle arrives with a timestamp up to four hours in the future.
            // Left signed, that produced a nonsensical "-171.3 min" on screen and, far worse, made
            // `age > threshold` permanently false: nothing on this instrument could ever be judged
            // stale, and the data-integrity layer had nothing to block on.
            bool aheadOfClock = raw < TimeSpan.Zero;
            var age = aheadOfClock ? TimeSpan.Zero : raw;

            var threshold = instrument?.FreshnessThreshold ?? TimeSpan.FromMinutes(15);
            bool stale = age > threshold;
            return new DataFreshness
            {
                InstrumentId = instrumentId,
                LastUpdateUtc = seen.Ts,
                Age = age,
                Stale = stale,
                Source = seen.Source,
                Issue = stale
                    ? $"last update {age.TotalMinutes:F1} min ago exceeds {threshold.TotalMinutes:F0} min threshold"
                    : aheadOfClock
                        ? $"bar is stamped {(-raw).TotalMinutes:F0} min ahead of the clock — a forming bar, or a clock difference"
                        : null
            };
        }

        public IReadOnlyList<DataFreshness> AllFreshness()
            => lastSeen.Keys.Select(GetFreshness).OrderByDescending(f => f.Age).ToList();

        public async Task PersistAsync(CancellationToken ct = default)
            => await repo.UpsertManyAsync(byId.Values.ToList(), ct);

        private void Index(Instrument instrument)
        {
            byId[instrument.Id] = instrument;
            foreach (var v in instrument.Venues)
                venueSymbolToId[$"{v.Venue}:{v.VenueSymbol}"] = instrument.Id;
        }

        /// <summary>A small built-in crypto set so the platform is usable before any venue directory
        /// has been pulled. Real venue data overwrites these mappings on first refresh.</summary>
        private void SeedDefaults()
        {
            foreach (var asset in new[] { "BTC", "ETH", "SOL", "XRP", "ADA", "DOGE", "LINK", "AVAX", "DOT", "LTC" })
            {
                string id = Instrument.MakeId(AssetClass.Crypto, asset, "USD");
                if (byId.ContainsKey(id)) continue;
                var instrument = new Instrument
                {
                    Id = id,
                    DisplayName = $"{asset}/USD",
                    AssetClass = AssetClass.Crypto,
                    BaseAsset = asset,
                    QuoteCurrency = "USD",
                    Exposure = ExposureKind.Inventory,
                    Venues =
                    {
                        new VenueMapping { Venue = VenueId.Binance, VenueSymbol = asset + "USDT", QuantityStep = 0.00001m, MinQuantity = 0.0001m },
                        new VenueMapping { Venue = VenueId.Kraken, VenueSymbol = Execution.KrakenSymbolMap.ToKrakenPair(asset + "USD"), QuantityStep = 0.00001m, MinQuantity = 0.0001m }
                    }
                };
                Index(instrument);
            }
        }

        /// <summary>The venue symbol to use for an instrument on a venue, falling back to the engine
        /// pair so existing single-symbol strategies keep resolving.</summary>
        public string VenueSymbolFor(string instrumentId, VenueId venue)
        {
            var instrument = Get(instrumentId);
            var mapping = instrument?.MappingFor(venue);
            if (mapping != null) return mapping.VenueSymbol;
            if (instrument == null) return instrumentId;

            // Only crypto venues take a concatenated pair. A share on Trading 212 or an epic on IG
            // is a ticker, and "AAPLUSDT" is not a thing anyone can trade.
            if (instrument.AssetClass != AssetClass.Crypto) return instrument.BaseAsset;

            return venue == VenueId.Kraken
                ? Execution.KrakenSymbolMap.ToKrakenPair(instrument.BaseAsset + instrument.QuoteCurrency)
                : instrument.BaseAsset + (instrument.QuoteCurrency == "USD" ? "USDT" : instrument.QuoteCurrency);
        }

        /// <summary>
        /// The symbol the market-data feed knows this instrument by: a Binance-style pair for crypto,
        /// an exchange ticker for everything else.
        ///
        /// The asset class matters here. Building a crypto pair for a share produces `AAPLUSDT`,
        /// which the equities feed cannot resolve and the crypto feed does not have — so the chart
        /// silently comes back empty.
        /// </summary>
        public string EngineSymbolFor(string instrumentId)
        {
            var instrument = Get(instrumentId);
            if (instrument == null) return instrumentId;

            var binance = instrument.MappingFor(VenueId.Binance);
            if (binance != null) return binance.VenueSymbol;

            if (instrument.AssetClass != AssetClass.Crypto)
            {
                // Trading 212 tickers carry a venue/currency suffix that the data feed does not use.
                var t212 = instrument.MappingFor(VenueId.Trading212);
                if (t212 != null) return Venues.Trading212VenueAdapter.ToMarketSymbol(t212.VenueSymbol);
                return instrument.BaseAsset;
            }

            return instrument.BaseAsset + (instrument.QuoteCurrency == "USD" ? "USDT" : instrument.QuoteCurrency);
        }

        public static (string BaseAsset, string Quote) SplitEnginePair(string symbol)
        {
            string s = symbol.ToUpperInvariant().Replace("/", "");
            foreach (var suffix in new[] { "USDT", "USDC", "USD", "GBP", "EUR", "BTC" })
                if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length)
                    return (s[..^suffix.Length], suffix is "USDT" or "USDC" ? "USD" : suffix);
            return ("", "USD");
        }
    }
}
