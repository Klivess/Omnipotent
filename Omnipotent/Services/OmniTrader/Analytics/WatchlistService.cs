using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Analytics
{
    public sealed class Watchlist
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public List<string> InstrumentIds { get; init; } = new();
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>One instrument's full analytical picture, as rendered on the Markets page.</summary>
    public sealed class MarketRow
    {
        public required string InstrumentId { get; init; }
        public required string DisplayName { get; init; }
        public required AssetClass AssetClass { get; init; }
        public decimal Price { get; init; }
        public decimal ChangePercent24h { get; init; }
        public MarketRegime Regime { get; init; }
        public double RegimeConfidence { get; init; }
        public decimal MomentumScore { get; init; }
        public int BreakoutDirection { get; init; }
        public double BreakoutQuality { get; init; }
        public double AlignmentScore { get; init; }
        public decimal AnnualizedVolatility { get; init; }
        public decimal AverageQuoteVolume { get; init; }
        public decimal EstimatedSpreadPercent { get; init; }
        public List<string> TradableOn { get; init; } = new();
        public DateTime? DataAsOfUtc { get; init; }
        public bool Stale { get; init; }
        /// <summary>The feed is healthy but the exchange is shut. A separate fact from staleness —
        /// nothing is wrong, and nothing is blocked.</summary>
        public bool MarketClosed { get; init; }
        public string? DataIssue { get; init; }
        /// <summary>Downsampled recent closes for the row's sparkline. A momentum score says how
        /// strong the move is; only the shape says whether it is one clean trend or a whipsaw that
        /// happens to end high.</summary>
        public List<decimal> Spark { get; init; } = new();
    }

    /// <summary>
    /// Turns watchlists into evaluated market rows by running the shared analytics over normalized
    /// candles. Every row carries its own data-freshness verdict, so a stale instrument is visibly
    /// stale on screen rather than quietly presented as current.
    /// </summary>
    public sealed class WatchlistService
    {
        private readonly WatchlistRepository repo;
        private readonly InstrumentMaster instruments;
        private readonly MarketDataRouter marketData;

        public WatchlistService(WatchlistRepository repo, InstrumentMaster instruments, MarketDataRouter marketData)
        {
            this.repo = repo;
            this.instruments = instruments;
            this.marketData = marketData;
        }

        public Task<List<Watchlist>> ListAsync(CancellationToken ct = default) => repo.ListAsync(ct);
        public Task SaveAsync(Watchlist watchlist, CancellationToken ct = default) => repo.UpsertAsync(watchlist, ct);
        public Task DeleteAsync(string id, CancellationToken ct = default) => repo.DeleteAsync(id, ct);

        /// <summary>
        /// Add or remove one instrument, read-modify-write on the server. Returns the saved list, or
        /// null when the id names no list. Adding something already present is a no-op rather than an
        /// error — the gesture's intent is "this should be on the list", and repeating it is harmless.
        /// </summary>
        public async Task<Watchlist?> ToggleInstrumentAsync(string? watchlistId, string instrumentId,
            bool remove, CancellationToken ct = default)
        {
            var lists = await repo.ListAsync(ct);
            var target = string.IsNullOrWhiteSpace(watchlistId)
                ? lists.FirstOrDefault()
                : lists.FirstOrDefault(w => w.Id == watchlistId);
            if (target == null) return null;

            // Store the canonical id when the firm knows the instrument, so the same thing added by
            // ticker and by id does not end up on the list twice.
            string id = instruments.Resolve(instrumentId)?.Id ?? instrumentId.Trim();
            if (id.Length == 0) return target;

            bool present = target.InstrumentIds.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (remove)
            {
                if (!present) return target;
                target.InstrumentIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (present) return target;
                target.InstrumentIds.Add(id);
            }

            await repo.UpsertAsync(target, ct);
            return target;
        }

        /// <summary>Create the default watchlist the first time the platform runs, so the Markets page
        /// is never an empty shell.</summary>
        public async Task EnsureDefaultAsync(CancellationToken ct = default)
        {
            var existing = await repo.ListAsync(ct);
            if (existing.Count > 0) return;
            await repo.UpsertAsync(new Watchlist
            {
                Id = "default",
                Name = "Core",
                InstrumentIds = { "crypto:BTC/USD", "crypto:ETH/USD", "crypto:SOL/USD", "crypto:XRP/USD", "crypto:LINK/USD" }
            }, ct);
        }

        /// <summary>
        /// Evaluate a set of instruments. <paramref name="interval"/> is the primary timeframe; the
        /// alignment reading additionally pulls a higher timeframe so "aligned" means something across
        /// horizons rather than within one.
        /// </summary>
        public async Task<List<MarketRow>> EvaluateAsync(IEnumerable<string> instrumentIds,
            TimeInterval interval = TimeInterval.OneHour, CancellationToken ct = default)
        {
            var rows = new List<MarketRow>();
            foreach (var instrumentId in instrumentIds.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                var instrument = instruments.Resolve(instrumentId);
                string engineSymbol = instrument != null ? instruments.EngineSymbolFor(instrument.Id) : instrumentId;

                IReadOnlyList<OHLCCandle> candles;
                try { candles = await marketData.GetHistoricalCandlesAsync(engineSymbol, interval, 250); }
                catch { candles = Array.Empty<OHLCCandle>(); }

                if (candles.Count == 0)
                {
                    rows.Add(new MarketRow
                    {
                        InstrumentId = instrument?.Id ?? instrumentId,
                        DisplayName = instrument?.DisplayName ?? instrumentId,
                        AssetClass = instrument?.AssetClass ?? AssetClass.Unknown,
                        Stale = true,
                        DataIssue = "no candles available for this instrument",
                        TradableOn = instrument?.Venues.Select(v => v.Venue.ToString()).ToList() ?? new List<string>()
                    });
                    continue;
                }

                // The bar's own stamp, plus the cadence it arrives at. Without the cadence an hourly
                // series was measured against a 15-minute threshold and every instrument on the page
                // was permanently "stale" — which the risk engine treats as a hard block.
                instruments.NoteDataUpdate(instrument?.Id ?? instrumentId, "market-data-router",
                    dataUtc: candles[^1].Timestamp,
                    cadence: TimeSpan.FromMinutes((int)interval),
                    continuousMarket: TradesContinuously(instrument?.AssetClass, engineSymbol));

                var regime = MarketAnalytics.ClassifyRegime(candles);
                var breakout = MarketAnalytics.AnalyseBreakout(candles);
                var liquidity = MarketAnalytics.AnalyseLiquidity(candles);

                double alignment = 0;
                try
                {
                    var higher = await marketData.GetHistoricalCandlesAsync(engineSymbol, HigherTimeframe(interval), 150);
                    alignment = MarketAnalytics.AnalyseAlignment(new Dictionary<string, IReadOnlyList<OHLCCandle>>
                    {
                        [interval.ToString()] = candles,
                        [HigherTimeframe(interval).ToString()] = higher
                    }).Score;
                }
                catch { }

                decimal change24h = ComputeChange(candles, BarsPerDay(interval));
                var freshness = instruments.GetFreshness(instrument?.Id ?? instrumentId);

                rows.Add(new MarketRow
                {
                    InstrumentId = instrument?.Id ?? instrumentId,
                    DisplayName = instrument?.DisplayName ?? instrumentId,
                    AssetClass = instrument?.AssetClass ?? AssetClass.Unknown,
                    Price = candles[^1].Close,
                    ChangePercent24h = change24h,
                    Regime = regime.Regime,
                    RegimeConfidence = regime.Confidence,
                    MomentumScore = MarketAnalytics.MomentumScore(candles),
                    BreakoutDirection = breakout.Direction,
                    BreakoutQuality = breakout.Quality,
                    AlignmentScore = alignment,
                    AnnualizedVolatility = regime.AnnualizedVolatility,
                    AverageQuoteVolume = liquidity.AverageQuoteVolume,
                    EstimatedSpreadPercent = liquidity.EstimatedSpreadPercent,
                    TradableOn = instrument?.Venues.Where(v => v.Tradeable).Select(v => v.Venue.ToString()).ToList()
                                 ?? new List<string>(),
                    DataAsOfUtc = candles[^1].Timestamp,
                    Stale = freshness.Stale,
                    MarketClosed = freshness.MarketLikelyClosed,
                    DataIssue = freshness.Issue,
                    Spark = Downsample(candles.Select(c => c.Close).ToList(), 48)
                });
            }
            return rows;
        }

        /// <summary>Breadth over an evaluated set — the participation measure for the Markets page.</summary>
        public async Task<BreadthReading> BreadthAsync(IEnumerable<string> instrumentIds,
            TimeInterval interval = TimeInterval.OneDay, CancellationToken ct = default)
        {
            var series = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var instrumentId in instrumentIds.Distinct())
            {
                try
                {
                    string engineSymbol = instruments.EngineSymbolFor(instrumentId);
                    series[instrumentId] = await marketData.GetHistoricalCandlesAsync(engineSymbol, interval, 120);
                }
                catch { }
            }
            return MarketAnalytics.AnalyseBreadth(series);
        }

        /// <summary>
        /// Reduce a series to at most <paramref name="target"/> points for display. Each output point
        /// is the extreme of its bucket in the direction the bucket moved, so a spike survives
        /// downsampling rather than being averaged into a flat line the operator never sees.
        /// </summary>
        public static List<decimal> Downsample(IReadOnlyList<decimal> series, int target)
        {
            if (target < 2 || series.Count <= target) return series.ToList();

            var output = new List<decimal>(target);
            double step = (double)series.Count / target;
            for (int i = 0; i < target; i++)
            {
                int start = (int)(i * step);
                int end = Math.Min(series.Count, (int)((i + 1) * step));
                if (end <= start) end = start + 1;

                decimal first = series[start], last = series[end - 1];
                decimal min = first, max = first;
                for (int j = start; j < end; j++)
                {
                    if (series[j] < min) min = series[j];
                    if (series[j] > max) max = series[j];
                }
                output.Add(last >= first ? max : min);
            }
            // The final point is the latest price and must be exact — it is read as "the mark".
            output[^1] = series[^1];
            return output;
        }

        private static decimal ComputeChange(IReadOnlyList<OHLCCandle> candles, int bars)
        {
            if (candles.Count < 2) return 0m;
            int index = Math.Max(0, candles.Count - 1 - bars);
            decimal from = candles[index].Close;
            return from <= 0m ? 0m : (candles[^1].Close - from) / from * 100m;
        }

        private static int BarsPerDay(TimeInterval interval)
            => Math.Max(1, 1440 / (int)interval);

        /// <summary>
        /// Whether this market produces bars around the clock. Crypto does; an exchange listing stops
        /// overnight and at weekends, and calling that a stale feed would block trading every evening
        /// for a feed that is working. An unrecognised symbol is judged by the feed it routes to.
        /// </summary>
        private static bool TradesContinuously(AssetClass? assetClass, string engineSymbol)
            => assetClass switch
            {
                AssetClass.Crypto => true,
                null or AssetClass.Unknown => !MarketDataRouter.UsesEquityFeed(engineSymbol, AssetClass.Unknown),
                _ => false
            };

        private static TimeInterval HigherTimeframe(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => TimeInterval.FifteenMinute,
            TimeInterval.FiveMinute => TimeInterval.OneHour,
            TimeInterval.FifteenMinute => TimeInterval.FourHour,
            TimeInterval.ThirtyMinute => TimeInterval.FourHour,
            TimeInterval.OneHour => TimeInterval.OneDay,
            TimeInterval.FourHour => TimeInterval.OneDay,
            _ => TimeInterval.OneWeek
        };
    }
}
