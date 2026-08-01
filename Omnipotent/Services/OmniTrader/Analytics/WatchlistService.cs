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

                instruments.NoteDataUpdate(instrument?.Id ?? instrumentId, "market-data-router", candles[^1].Timestamp);

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
