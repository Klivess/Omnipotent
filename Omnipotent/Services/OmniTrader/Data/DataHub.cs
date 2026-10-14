using Omnipotent.Services.OmniTrader.Data.Crypto;
using Omnipotent.Services.OmniTrader.Data.Equities;
using Omnipotent.Services.OmniTrader.Data.Macro;
using Omnipotent.Services.OmniTrader.Data.News;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Data
{
    public sealed class MonitorSection
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public List<QuoteRow> Rows { get; init; } = new();
        public string? Error { get; init; }
    }

    public sealed class MarketMonitor
    {
        public DateTime AsOfUtc { get; init; }
        public List<MonitorSection> Sections { get; init; } = new();
        public CryptoGlobal? CryptoGlobal { get; init; }
        public FearGreedPoint? FearGreed { get; init; }
        public List<FearGreedPoint> FearGreedHistory { get; init; } = new();
        public List<FundingSnapshot> Funding { get; init; } = new();
        public Dictionary<string, double?> Dvol { get; init; } = new();
        public YieldCurve? Curve { get; init; }
        public YieldCurve? CurveMonthAgo { get; init; }
        public YieldCurve? CurveYearAgo { get; init; }
        public VolTerm? VolTerm { get; init; }
        public Breadth? Breadth { get; init; }
        public List<string> Errors { get; init; } = new();
    }

    public sealed record SectorStat(string Sector, double ChangePct, int Count);

    public sealed class Breadth
    {
        public required string Universe { get; init; }
        public int Members { get; init; }
        public int Advancers { get; init; }
        public int Decliners { get; init; }
        public double PctAbove50d { get; init; }
        public double PctAbove200d { get; init; }
        public double PctNear52wHigh { get; init; }
        public double PctNear52wLow { get; init; }
        public double MedianChangePct { get; init; }
        public List<SectorStat> Sectors { get; init; } = new();
        public DateTime AsOfUtc { get; init; }
    }

    /// <summary>
    /// The terminal's data layer: every free source behind one cached façade. Each method has a TTL
    /// suited to how fast its data changes, concurrent identical requests share one upstream call, and
    /// a failed refresh keeps serving the last good answer.
    /// </summary>
    public sealed class DataHub
    {
        private readonly MarketDataRouter marketData;
        private readonly TtlCache cache = new(8000);

        public YahooData Yahoo { get; }
        public SecEdgar Sec { get; } = new();
        public CryptoData Crypto { get; } = new();
        public MacroData Macro { get; } = new();
        public NewsData News { get; } = new();
        public UniverseCatalog Universes { get; }

        public DataHub(MarketDataRouter marketData, Func<Task<string?>>? fredKey = null)
        {
            this.marketData = marketData;
            Yahoo = new YahooData(marketData.Yahoo.Session);
            Macro.FredKey = fredKey;
            Universes = new UniverseCatalog(CryptoByVolumeAsync, CryptoByMarketCapAsync);
        }

        private Task<T?> Cached<T>(string key, TimeSpan ttl, Func<Task<T?>> factory) where T : class
            => cache.GetOrAddAsync(key, ttl, factory);

        // ── quotes (any symbol) ───────────────────────────────────────────────────

        /// <summary>Quotes for a mixed list: crypto pairs from Binance 24h tickers, everything else from Yahoo.</summary>
        public async Task<List<QuoteRow>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
        {
            var list = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var crypto = list.Where(s => !MarketDataRouter.UsesEquityFeed(s, AssetClass.Unknown)).Select(s => s.ToUpperInvariant()).ToList();
            var equity = list.Except(crypto, StringComparer.OrdinalIgnoreCase).ToList();
            var rows = new List<QuoteRow>();

            if (crypto.Count > 0)
            {
                string key = "q:c:" + string.Join(",", crypto.OrderBy(s => s));
                var tickers = await Cached(key, TimeSpan.FromSeconds(8), async () =>
                {
                    try { return await Crypto.GetTickersAsync(crypto, ct); }
                    catch
                    {
                        // One unknown symbol fails Binance's whole batch; retry individually.
                        var single = new List<CryptoTicker>();
                        foreach (var s in crypto)
                            try { single.AddRange(await Crypto.GetTickersAsync(new[] { s }, ct)); } catch { }
                        return single;
                    }
                }) ?? new List<CryptoTicker>();
                rows.AddRange(tickers.Select(t => new QuoteRow
                {
                    Symbol = t.Symbol, Name = t.Symbol, QuoteType = "CRYPTOCURRENCY", Exchange = "Binance", Currency = QuoteOf(t.Symbol),
                    MarketState = "REGULAR", Price = t.Price, ChangePct = t.ChangePct,
                    Change = t.Price - t.Price / (1 + t.ChangePct / 100), DayHigh = t.High, DayLow = t.Low,
                    Volume = t.Volume, MarketCap = null, AsOfUtc = DateTime.UtcNow, Source = "binance"
                }));
            }
            if (equity.Count > 0)
            {
                string key = "q:e:" + string.Join(",", equity.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                var quotes = await Cached(key, TimeSpan.FromSeconds(15), () => Yahoo.GetQuotesAsync(equity, ct)!) ?? new List<QuoteRow>();
                rows.AddRange(quotes);
            }
            // Preserve the caller's order.
            var order = list.Select((s, i) => (s, i)).ToDictionary(x => x.s.ToUpperInvariant(), x => x.i);
            return rows.OrderBy(r => order.TryGetValue(r.Symbol.ToUpperInvariant(), out var i) ? i : int.MaxValue).ToList();
        }

        private static string QuoteOf(string pair)
        {
            foreach (var q in new[] { "USDT", "USDC", "FDUSD", "BUSD", "USD", "EUR", "GBP", "BTC", "ETH" })
                if (pair.EndsWith(q, StringComparison.OrdinalIgnoreCase)) return q;
            return "USD";
        }

        // ── equities ──────────────────────────────────────────────────────────────

        public Task<CompanySnapshot?> GetCompanyAsync(string symbol, CancellationToken ct = default)
            => Cached($"company:{symbol.ToUpperInvariant()}", TimeSpan.FromHours(6), () => Yahoo.GetCompanyAsync(symbol, ct));

        public Task<FinancialStatements?> GetFinancialsAsync(string symbol, CancellationToken ct = default)
            => Cached($"fin:{symbol.ToUpperInvariant()}", TimeSpan.FromHours(12), async () => (FinancialStatements?)await Yahoo.GetFinancialsAsync(symbol, ct));

        public Task<SecFacts?> GetSecFactsAsync(string symbol, CancellationToken ct = default)
            => Cached($"secfacts:{symbol.ToUpperInvariant()}", TimeSpan.FromHours(12), () => Sec.GetFactsAsync(symbol, ct));

        public Task<List<SecFiling>?> GetFilingsAsync(string symbol, string? form = null, CancellationToken ct = default)
            => Cached($"filings:{symbol.ToUpperInvariant()}:{form}", TimeSpan.FromHours(1), async () => (List<SecFiling>?)await Sec.GetFilingsAsync(symbol, 40, form, ct));

        public async Task<OptionChain?> GetOptionsAsync(string symbol, DateTime? expiration = null, CancellationToken ct = default)
        {
            double rf = await RiskFreeRateAsync(ct);
            return await Cached($"opt:{symbol.ToUpperInvariant()}:{expiration:yyyyMMdd}", TimeSpan.FromMinutes(2),
                () => Yahoo.GetOptionsAsync(symbol, expiration, rf, ct));
        }

        public Task<List<Equities.TermPoint>?> GetOptionTermAsync(string symbol, CancellationToken ct = default)
            => Cached($"optterm:{symbol.ToUpperInvariant()}", TimeSpan.FromMinutes(10), async () => (List<Equities.TermPoint>?)await Yahoo.GetTermStructureAsync(symbol, 8, ct));

        public Task<MoverList?> GetMoversAsync(string screen, int count = 25, CancellationToken ct = default)
            => Cached($"movers:{screen}:{count}", TimeSpan.FromMinutes(2), async () => (MoverList?)await Yahoo.GetPredefinedScreenAsync(screen, count, ct));

        public Task<List<string>?> GetTrendingAsync(string region = "US", CancellationToken ct = default)
            => Cached($"trending:{region}", TimeSpan.FromMinutes(5), async () => (List<string>?)await Yahoo.GetTrendingAsync(region, 20, ct));

        /// <summary>3-month T-bill yield as the options risk-free rate; 4% when unavailable.</summary>
        public async Task<double> RiskFreeRateAsync(CancellationToken ct = default)
        {
            try
            {
                var curve = await GetLatestCurveAsync(ct);
                return curve?.Get("3 Mo") ?? 4.0;
            }
            catch { return 4.0; }
        }

        // ── crypto ────────────────────────────────────────────────────────────────

        public Task<List<FundingSnapshot>?> GetFundingBoardAsync(CancellationToken ct = default)
            => Cached("funding-board", TimeSpan.FromMinutes(1), async () => (List<FundingSnapshot>?)await Crypto.GetFundingBoardAsync(ct));

        public Task<DerivativesView?> GetDerivativesAsync(string symbol, string period = "1h", int limit = 168, CancellationToken ct = default)
            => Cached($"deriv:{symbol.ToUpperInvariant()}:{period}:{limit}", TimeSpan.FromMinutes(2), async () => (DerivativesView?)await Crypto.GetDerivativesAsync(symbol, period, limit, ct));

        public Task<List<TimePoint>?> GetDvolAsync(string currency, int days = 180, CancellationToken ct = default)
            => Cached($"dvol:{currency.ToUpperInvariant()}:{days}", TimeSpan.FromMinutes(10), async () => (List<TimePoint>?)await Crypto.GetDvolAsync(currency, days, "1D", ct));

        public Task<OptionSurface?> GetCryptoOptionSurfaceAsync(string currency, CancellationToken ct = default)
            => Cached($"cryptoopt:{currency.ToUpperInvariant()}", TimeSpan.FromMinutes(5), async () => (OptionSurface?)await Crypto.GetOptionSurfaceAsync(currency, ct));

        public Task<CryptoGlobal?> GetCryptoGlobalAsync(CancellationToken ct = default)
            => Cached("crypto-global", TimeSpan.FromMinutes(5), async () => (CryptoGlobal?)await Crypto.GetGlobalAsync(ct));

        public Task<List<CoinRow>?> GetTopCoinsAsync(int count = 100, CancellationToken ct = default)
            => Cached($"coins:{count}", TimeSpan.FromMinutes(5), async () =>
            {
                var coins = await Crypto.GetTopCoinsAsync(count, ct);
                foreach (var c in coins) c.BinanceSymbol = c.Symbol + "USDT";
                return (List<CoinRow>?)coins;
            });

        public Task<List<object>?> GetTrendingCoinsAsync(CancellationToken ct = default)
            => Cached("coins-trending", TimeSpan.FromMinutes(10), async () => (List<object>?)await Crypto.GetTrendingCoinsAsync(ct));

        public Task<List<FearGreedPoint>?> GetFearGreedAsync(int days = 365, CancellationToken ct = default)
            => Cached($"fng:{days}", TimeSpan.FromHours(1), async () => (List<FearGreedPoint>?)await Crypto.GetFearGreedAsync(days, ct));

        public Task<List<TimePoint>?> GetTvlAsync(CancellationToken ct = default)
            => Cached("tvl", TimeSpan.FromHours(6), async () => (List<TimePoint>?)await Crypto.GetTotalTvlAsync(ct));

        public Task<List<object>?> GetChainsAsync(CancellationToken ct = default)
            => Cached("chains", TimeSpan.FromHours(1), async () => (List<object>?)await Crypto.GetChainsAsync(25, ct));

        public Task<List<TimePoint>?> GetStablecoinsAsync(CancellationToken ct = default)
            => Cached("stables", TimeSpan.FromHours(6), async () => (List<TimePoint>?)await Crypto.GetStablecoinSupplyAsync(ct));

        public Task<object?> GetOrderBookAsync(string symbol, int depth = 50, CancellationToken ct = default)
            => Cached($"book:{symbol.ToUpperInvariant()}:{depth}", TimeSpan.FromSeconds(2), () => Crypto.GetOrderBookAsync(symbol, depth, ct)!);

        private async Task<List<UniverseMember>> CryptoByVolumeAsync(int n, CancellationToken ct)
        {
            var tickers = await Crypto.GetTickersAsync(null, ct);
            string[] stable = { "USDCUSDT", "FDUSDUSDT", "TUSDUSDT", "USDPUSDT", "DAIUSDT", "EURUSDT", "BUSDUSDT", "USD1USDT", "PAXGUSDT", "XUSDUSDT" };
            return tickers.Where(t => t.Symbol.EndsWith("USDT") && !stable.Contains(t.Symbol) && t.Price > 0)
                .OrderByDescending(t => t.QuoteVolume).Take(n)
                .Select(t => new UniverseMember { Symbol = t.Symbol, Name = t.Symbol[..^4], AssetClass = "Crypto" }).ToList();
        }

        private async Task<List<UniverseMember>> CryptoByMarketCapAsync(int n, CancellationToken ct)
        {
            var coins = await GetTopCoinsAsync(Math.Max(n, 100), ct) ?? new List<CoinRow>();
            string[] stable = { "USDT", "USDC", "DAI", "FDUSD", "TUSD", "USDE", "USDS", "PYUSD", "USD1", "BUSD" };
            return coins.Where(c => !stable.Contains(c.Symbol)).Take(n)
                .Select(c => new UniverseMember { Symbol = c.Symbol + "USDT", Name = c.Name, AssetClass = "Crypto" }).ToList();
        }

        // ── macro ─────────────────────────────────────────────────────────────────

        public async Task<YieldCurve?> GetLatestCurveAsync(CancellationToken ct = default)
            => (await GetCurveHistoryAsync(ct))?.FirstOrDefault();

        /// <summary>Daily curves for this year and last, newest first.</summary>
        public Task<List<YieldCurve>?> GetCurveHistoryAsync(CancellationToken ct = default)
            => Cached("curves", TimeSpan.FromHours(1), async () =>
            {
                int year = DateTime.UtcNow.Year;
                var all = new List<YieldCurve>();
                foreach (var y in new[] { year, year - 1 })
                {
                    try { all.AddRange(await Macro.GetYieldCurvesAsync(y, ct)); } catch { }
                }
                return all.Count == 0 ? null : all.OrderByDescending(c => c.Date).ToList();
            });

        public Task<List<MacroSeries>?> GetMacroAsync(CancellationToken ct = default)
            => Cached("macro-catalog", TimeSpan.FromHours(6), async () => (List<MacroSeries>?)await Macro.GetCatalogSeriesAsync(MacroData.Catalog, ct));

        public Task<VolTermResult?> GetVolTermAsync(CancellationToken ct = default)
            => Cached("volterm", TimeSpan.FromMinutes(30), async () =>
            {
                var (latest, history) = await Macro.GetVolTermAsync(365, ct);
                return new VolTermResult { Latest = latest, History = history };
            });

        public sealed class VolTermResult
        {
            public required VolTerm Latest { get; init; }
            public required Dictionary<string, List<TimePoint>> History { get; init; }
        }

        public Task<List<TimePoint>?> GetFredAsync(string seriesId, CancellationToken ct = default)
            => Cached($"fred:{seriesId}", TimeSpan.FromHours(6), () => Macro.GetFredAsync(seriesId, ct));

        // ── news ──────────────────────────────────────────────────────────────────

        public Task<List<NewsItem>?> GetNewsAsync(string? category = null, string? symbol = null, string? query = null, int limit = 80,
            CancellationToken ct = default)
        {
            var feeds = new List<NewsFeed>();
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                bool crypto = !MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown);
                if (crypto) feeds.Add(NewsData.SearchFeed($"{CryptoData.BaseAsset(symbol)} crypto"));
                else feeds.Add(NewsData.SymbolFeed(symbol));
            }
            if (!string.IsNullOrWhiteSpace(query)) feeds.Add(NewsData.SearchFeed(query));
            if (feeds.Count == 0)
                feeds.AddRange(NewsData.Feeds.Where(f => category == null || category == "all" || string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase)));
            string key = $"news:{category}:{symbol}:{query}:{limit}";
            return Cached(key, TimeSpan.FromMinutes(3), async () => (List<NewsItem>?)await News.GetManyAsync(feeds, limit, ct));
        }

        // ── composites ────────────────────────────────────────────────────────────

        public Task<MarketMonitor?> GetMonitorAsync(CancellationToken ct = default)
            => Cached("monitor", TimeSpan.FromSeconds(20), async () => (MarketMonitor?)await BuildMonitorAsync(ct));

        private async Task<MarketMonitor> BuildMonitorAsync(CancellationToken ct)
        {
            var errors = new List<string>();
            async Task<MonitorSection> Section(string id, string title, List<UniverseMember> members)
            {
                try
                {
                    var rows = await GetQuotesAsync(members.Select(m => m.Symbol), ct);
                    var names = members.ToDictionary(m => m.Symbol, m => m.Name, StringComparer.OrdinalIgnoreCase);
                    return new MonitorSection
                    {
                        Id = id, Title = title,
                        Rows = rows.Select(r => names.TryGetValue(r.Symbol, out var n) && n != null ? With(r, n) : r).ToList()
                    };
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"{title}: {ex.Message}");
                    return new MonitorSection { Id = id, Title = title, Error = ex.Message };
                }
            }

            var sections = new[]
            {
                Section("indices", "Indices", UniverseCatalog.WorldIndices),
                Section("fx", "FX", UniverseCatalog.Fx),
                Section("commodities", "Commodities", UniverseCatalog.Commodities),
                Section("rates", "Rates", UniverseCatalog.Rates),
                Section("vol", "Volatility", UniverseCatalog.Volatility),
                Section("crypto", "Crypto", UniverseCatalog.CryptoMajors),
                Section("sectors", "Sectors", UniverseCatalog.SectorEtfs),
            };

            async Task<T?> Try<T>(string label, Func<Task<T?>> f) where T : class
            {
                try { return await f(); }
                catch (Exception ex) { lock (errors) errors.Add($"{label}: {ex.Message}"); return null; }
            }

            var globalTask = Try("Crypto market", () => GetCryptoGlobalAsync(ct));
            var fngTask = Try("Fear & Greed", () => GetFearGreedAsync(30, ct));
            var fundingTask = Try("Funding", () => GetFundingBoardAsync(ct));
            var curvesTask = Try("Yield curve", () => GetCurveHistoryAsync(ct));
            var volTask = Try("VIX term", () => GetVolTermAsync(ct));
            var btcDvol = Try("DVOL", () => GetDvolAsync("BTC", 7, ct));
            var ethDvol = Try("DVOL", () => GetDvolAsync("ETH", 7, ct));

            await Task.WhenAll(sections);
            var curves = await curvesTask;
            var fng = await fngTask ?? new List<FearGreedPoint>();
            var funding = await fundingTask ?? new List<FundingSnapshot>();
            var majors = UniverseCatalog.CryptoMajors.Select(m => m.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new MarketMonitor
            {
                AsOfUtc = DateTime.UtcNow,
                Sections = sections.Select(s => s.Result).ToList(),
                CryptoGlobal = await globalTask,
                FearGreed = fng.LastOrDefault(),
                FearGreedHistory = fng,
                Funding = funding.Where(f => majors.Contains(f.Symbol)).ToList(),
                Dvol = new Dictionary<string, double?>
                {
                    ["BTC"] = (await btcDvol)?.LastOrDefault()?.V,
                    ["ETH"] = (await ethDvol)?.LastOrDefault()?.V,
                },
                Curve = curves?.FirstOrDefault(),
                CurveMonthAgo = curves?.FirstOrDefault(c => c.Date <= DateTime.UtcNow.Date.AddDays(-30)),
                CurveYearAgo = curves?.FirstOrDefault(c => c.Date <= DateTime.UtcNow.Date.AddDays(-365)),
                VolTerm = (await volTask)?.Latest,
                Errors = errors
            };
        }

        private static QuoteRow With(QuoteRow r, string name) => new()
        {
            Symbol = r.Symbol, Name = name, QuoteType = r.QuoteType, Exchange = r.Exchange, Currency = r.Currency, MarketState = r.MarketState,
            Price = r.Price, Change = r.Change, ChangePct = r.ChangePct, PreviousClose = r.PreviousClose, Open = r.Open, DayHigh = r.DayHigh,
            DayLow = r.DayLow, Volume = r.Volume, AvgVolume3M = r.AvgVolume3M, MarketCap = r.MarketCap, TrailingPE = r.TrailingPE,
            ForwardPE = r.ForwardPE, Eps = r.Eps, DividendYieldPct = r.DividendYieldPct, High52w = r.High52w, Low52w = r.Low52w,
            Change52wPct = r.Change52wPct, Avg50d = r.Avg50d, Avg200d = r.Avg200d, PriceToBook = r.PriceToBook,
            PreMarketPrice = r.PreMarketPrice, PreMarketChangePct = r.PreMarketChangePct, PostMarketPrice = r.PostMarketPrice,
            PostMarketChangePct = r.PostMarketChangePct, AsOfUtc = r.AsOfUtc, Source = r.Source
        };

        /// <summary>Market internals for an equity universe from one batch of quotes: advancers, MA breadth, 52w extremes, sectors.</summary>
        public Task<Breadth?> GetBreadthAsync(string universe = "sp500", CancellationToken ct = default)
            => Cached($"breadth:{universe}", TimeSpan.FromMinutes(10), async () =>
            {
                var members = await Universes.GetAsync(universe, ct);
                if (members.Count == 0) return null;
                var quotes = await GetQuotesAsync(members.Select(m => m.Symbol), ct);
                var sectorOf = members.ToDictionary(m => m.Symbol, m => m.Sector ?? "Other", StringComparer.OrdinalIgnoreCase);
                var valid = quotes.Where(q => q.Price is > 0).ToList();
                if (valid.Count == 0) return null;
                double Pct(Func<QuoteRow, bool> f) => Math.Round(valid.Count(f) * 100.0 / valid.Count, 1);
                var changes = valid.Where(q => q.ChangePct.HasValue).Select(q => q.ChangePct!.Value).OrderBy(x => x).ToList();
                return new Breadth
                {
                    Universe = universe,
                    Members = valid.Count,
                    Advancers = valid.Count(q => q.ChangePct > 0),
                    Decliners = valid.Count(q => q.ChangePct < 0),
                    PctAbove50d = Pct(q => q.Avg50d.HasValue && q.Price > q.Avg50d),
                    PctAbove200d = Pct(q => q.Avg200d.HasValue && q.Price > q.Avg200d),
                    PctNear52wHigh = Pct(q => q.High52w is > 0 && q.Price >= q.High52w * 0.98),
                    PctNear52wLow = Pct(q => q.Low52w is > 0 && q.Price <= q.Low52w * 1.02),
                    MedianChangePct = changes.Count > 0 ? changes[changes.Count / 2] : 0,
                    Sectors = valid.GroupBy(q => sectorOf.TryGetValue(q.Symbol, out var s) ? s : "Other")
                        .Select(g => new SectorStat(g.Key, Math.Round(g.Where(q => q.ChangePct.HasValue).Select(q => q.ChangePct!.Value).DefaultIfEmpty(0).Average(), 2), g.Count()))
                        .OrderByDescending(x => x.ChangePct).ToList(),
                    AsOfUtc = DateTime.UtcNow
                };
            });

        public void Invalidate(string prefix) => cache.Invalidate(prefix);
    }
}
