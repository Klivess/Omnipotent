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
        private readonly ConcurrentDictionary<string, Observation> lastSeen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One successful read of an instrument's data. <c>DataUtc</c> and <c>ObservedUtc</c> are
        /// deliberately separate: a feed that answers instantly with an hour-old bar is healthy if
        /// the bars are hourly, and broken if they are meant to be by the minute. Collapsing the two
        /// into one timestamp is what made every instrument permanently stale.
        /// </summary>
        private sealed record Observation(DateTime DataUtc, DateTime ObservedUtc, string Source,
            TimeSpan? Cadence, bool? ContinuousMarket);
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

        /// <summary>
        /// Record that data was successfully observed for an instrument. Freshness is what the risk
        /// engine's data-integrity layer blocks on, so it is tracked centrally rather than per caller.
        ///
        /// <paramref name="dataUtc"/> is the timestamp *of the data* (a bar's stamp); omit it for a
        /// live tick. <paramref name="cadence"/> is how often new data is expected — pass the bar
        /// interval when reading candles, so an hourly series is not judged against a threshold meant
        /// for ticks. <paramref name="continuousMarket"/> says whether this market trades around the
        /// clock; a session-bound market that stops producing bars overnight is shut, not broken.
        /// </summary>
        public void NoteDataUpdate(string instrumentId, string source, DateTime? dataUtc = null,
            TimeSpan? cadence = null, bool? continuousMarket = null)
        {
            var now = DateTime.UtcNow;
            lastSeen[instrumentId] = new Observation(dataUtc ?? now, now, source, cadence, continuousMarket);
            if (byId.TryGetValue(instrumentId, out var instrument))
            {
                instrument.LastUpdatedUtc = dataUtc ?? now;
                instrument.DataSource = source;
            }
        }

        /// <summary>
        /// How late data may be before it is stale, given how often it arrives. Two intervals of
        /// slack: providers disagree about whether a bar carries its open or its close time, so one
        /// interval of the difference is a convention rather than lateness, and the second covers the
        /// bar that is still forming. Past that, a bar has genuinely been missed.
        /// </summary>
        internal static TimeSpan ToleranceFor(TimeSpan cadence)
            => cadence + cadence + TimeSpan.FromMinutes(2);

        /// <summary>
        /// The freshness verdict, as a pure decision so it can be reasoned about and tested on its
        /// own — it is a hard block in the risk engine, so getting it wrong stops all trading.
        ///
        /// Two distinct failures, and conflating them is what made every instrument permanently
        /// stale: <paramref name="observationAge"/> says whether the feed is reachable at all, which
        /// is always a fault; <paramref name="dataAge"/> says how old the newest data is, which is
        /// only a fault when newer data was actually due. On a bar series the newest bar is at least
        /// one bar old by definition, so it is meaningless without <paramref name="cadence"/>.
        /// </summary>
        internal static (bool Stale, bool MarketClosed, bool FeedSilent, bool DataOld) Judge(
            TimeSpan dataAge, TimeSpan observationAge, TimeSpan? cadence, bool continuous,
            TimeSpan feedThreshold)
        {
            var dataThreshold = cadence is { } c ? ToleranceFor(c) : feedThreshold;
            bool feedSilent = observationAge > feedThreshold;
            bool dataOld = dataAge > dataThreshold;

            // A session-bound market that has stopped producing bars is shut, not broken. Calling
            // that stale would block trading every evening and every weekend on a healthy feed.
            bool stale = feedSilent || (dataOld && continuous);
            return (stale, dataOld && !continuous && !feedSilent, feedSilent, dataOld);
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
                    ObservationAge = TimeSpan.MaxValue,
                    Stale = true,
                    Issue = "no data observed for this instrument in this process"
                };
            }

            var now = DateTime.UtcNow;
            var raw = now - seen.DataUtc;

            // A bar can be stamped ahead of the clock — providers stamp the *forming* bar with its
            // close time, so a 4-hour candle arrives with a timestamp up to four hours in the future.
            // Left signed, that produced a nonsensical "-171.3 min" on screen and, far worse, made
            // `age > threshold` permanently false: nothing on this instrument could ever be judged
            // stale, and the data-integrity layer had nothing to block on.
            bool aheadOfClock = raw < TimeSpan.Zero;
            var age = aheadOfClock ? TimeSpan.Zero : raw;
            var observationAge = now - seen.ObservedUtc;
            if (observationAge < TimeSpan.Zero) observationAge = TimeSpan.Zero;

            var feedThreshold = instrument?.FreshnessThreshold ?? DefaultFreshnessThreshold;
            bool continuous = seen.ContinuousMarket ?? true;
            var (stale, marketClosed, feedSilent, dataOld) =
                Judge(age, observationAge, seen.Cadence, continuous, feedThreshold);

            return new DataFreshness
            {
                InstrumentId = instrumentId,
                LastUpdateUtc = seen.DataUtc,
                Age = age,
                ObservationAge = observationAge,
                Cadence = seen.Cadence,
                Stale = stale,
                MarketLikelyClosed = marketClosed,
                Source = seen.Source,
                Issue = feedSilent
                    ? $"no successful read for {observationAge.TotalMinutes:F0} min "
                      + $"(expected every {feedThreshold.TotalMinutes:F0} min)"
                    : dataOld && continuous
                        ? $"newest data is {age.TotalMinutes:F1} min old — a "
                          + $"{Describe(seen.Cadence, feedThreshold)} series should not be that far behind"
                        : dataOld
                            ? $"no new bars for {age.TotalHours:F1} h — the market is closed or between bars"
                            : aheadOfClock
                                ? $"bar is stamped {(-raw).TotalMinutes:F0} min ahead of the clock — a forming bar, or a clock difference"
                                : null
            };
        }

        /// <summary>Default for instruments that never declared one — the right threshold for a live
        /// tick, and the fallback when a caller did not say how often its data arrives.</summary>
        public static readonly TimeSpan DefaultFreshnessThreshold = TimeSpan.FromMinutes(15);

        private static string Describe(TimeSpan? cadence, TimeSpan fallback)
        {
            var span = cadence ?? fallback;
            return span >= TimeSpan.FromDays(1) ? $"{span.TotalDays:F0}-day"
                 : span >= TimeSpan.FromHours(1) ? $"{span.TotalHours:F0}-hour"
                 : $"{span.TotalMinutes:F0}-minute";
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
