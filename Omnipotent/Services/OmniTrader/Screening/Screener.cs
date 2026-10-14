using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Data.Equities;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.OmniTrader.Screening
{
    public sealed class ScreenColumn
    {
        public required string Key { get; set; }
        public string? Label { get; set; }
        /// <summary>A built-in field name (see <see cref="ScreenerEngine.Fields"/>) or a formula.</summary>
        public required string Expr { get; set; }
        /// <summary>number | pct | price | compact | bool</summary>
        public string Format { get; set; } = "number";
    }

    public sealed class ScreenDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Untitled screen";
        public string? Description { get; set; }
        /// <summary>A universe id from the catalog, "watchlist:{id}", or empty when <see cref="Symbols"/> is set.</summary>
        public string Universe { get; set; } = "megacaps";
        public List<string> Symbols { get; set; } = new();
        public TimeInterval Interval { get; set; } = TimeInterval.OneDay;
        /// <summary>Formulas combined with AND; each must be true on the latest bar.</summary>
        public List<string> Filters { get; set; } = new();
        public List<ScreenColumn> Columns { get; set; } = new();
        public string? SortBy { get; set; }
        public bool Descending { get; set; } = true;
        public int Limit { get; set; } = 100;
        public bool IncludeForming { get; set; } = true;
        public Dictionary<string, double> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Preset { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class ScreenRow
    {
        public required string Symbol { get; init; }
        public string? Name { get; init; }
        public string? Sector { get; init; }
        public string AssetClass { get; init; } = "Equity";
        public bool Passed { get; init; }
        public Dictionary<string, double?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public double[] Spark { get; init; } = Array.Empty<double>();
        public string? Error { get; init; }
    }

    public sealed class ScreenResult
    {
        public required ScreenDefinition Definition { get; init; }
        public List<ScreenRow> Rows { get; init; } = new();
        public int Universe { get; init; }
        public int Evaluated { get; init; }
        public int Passed { get; init; }
        public int Failed { get; init; }
        public List<string> Errors { get; init; } = new();
        public double ElapsedMs { get; init; }
        public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;
        public List<ScreenColumn> Columns { get; init; } = new();
    }

    /// <summary>
    /// Runs a screen: resolve the universe, fetch bars per symbol (cache-first, bounded parallelism),
    /// compute the built-in fields, apply formula filters on the latest bar and compute the columns.
    /// Equity universes are enriched with one batch quote call (market cap, P/E, yield).
    /// </summary>
    public sealed class ScreenerEngine
    {
        private readonly MarketDataRouter marketData;
        private readonly DataHub hub;
        private readonly Func<string, CancellationToken, Task<List<string>>>? watchlistResolver;

        public ScreenerEngine(MarketDataRouter marketData, DataHub hub, Func<string, CancellationToken, Task<List<string>>>? watchlistResolver = null)
        {
            this.marketData = marketData;
            this.hub = hub;
            this.watchlistResolver = watchlistResolver;
        }

        /// <summary>Built-in per-symbol fields, available as columns and as scalar names inside filters.</summary>
        public static readonly (string Key, string Label, string Format)[] Fields =
        {
            ("price", "Price", "price"), ("chg1d", "1D %", "pct"), ("chg5d", "5D %", "pct"), ("chg1m", "1M %", "pct"),
            ("chg3m", "3M %", "pct"), ("chg6m", "6M %", "pct"), ("chg1y", "1Y %", "pct"), ("ytd", "YTD %", "pct"),
            ("volume", "Volume", "compact"), ("relvol", "Rel vol", "number"), ("dollarvol", "$ vol (20d)", "compact"),
            ("rsi14", "RSI 14", "number"), ("atrpct", "ATR %", "pct"), ("vol20", "Vol 20 (ann.)", "pct"),
            ("dist52h", "From 52w high", "pct"), ("dist52l", "From 52w low", "pct"), ("sma50dist", "vs SMA50", "pct"),
            ("sma200dist", "vs SMA200", "pct"), ("adx14", "ADX 14", "number"), ("trend", "Trend", "number"), ("gap", "Gap %", "pct"),
            ("mcap", "Mkt cap", "compact"), ("pe", "P/E", "number"), ("fpe", "Fwd P/E", "number"), ("divyield", "Div yield", "pct"),
        };

        public static readonly HashSet<string> FieldKeys = Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public static List<ScreenColumn> DefaultColumns() => new[] { "price", "chg1d", "chg1m", "chg6m", "rsi14", "relvol", "atrpct", "dist52h" }
            .Select(k => { var f = Fields.First(x => x.Key == k); return new ScreenColumn { Key = k, Label = f.Label, Expr = k, Format = f.Format }; }).ToList();

        /// <summary>Validate every formula without running anything.</summary>
        public static List<string> Validate(ScreenDefinition def)
        {
            var scope = Scope(def);
            var errors = new List<string>();
            foreach (var f in def.Filters.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                var (_, errs) = FormulaCompiler.Check(f, scope);
                errors.AddRange(errs.Select(e => $"Filter \"{f}\": {e.Message}"));
            }
            foreach (var c in def.Columns.Where(c => !FieldKeys.Contains(c.Expr)))
            {
                var (_, errs) = FormulaCompiler.Check(c.Expr, scope);
                errors.AddRange(errs.Select(e => $"Column {c.Key}: {e.Message}"));
            }
            return errors;
        }

        private static FormulaScope Scope(ScreenDefinition def)
            => FormulaScope.With(inputs: FieldKeys.Concat(def.Inputs.Keys), variables: null);

        public async Task<List<UniverseMember>> ResolveUniverseAsync(ScreenDefinition def, CancellationToken ct)
        {
            if (def.Symbols.Count > 0)
                return def.Symbols.Select(s => new UniverseMember
                {
                    Symbol = s.Trim(),
                    AssetClass = MarketDataRouter.UsesEquityFeed(s, AssetClass.Unknown) ? "Equity" : "Crypto"
                }).ToList();
            if (def.Universe.StartsWith("watchlist:", StringComparison.OrdinalIgnoreCase) && watchlistResolver != null)
            {
                var symbols = await watchlistResolver(def.Universe["watchlist:".Length..], ct);
                return symbols.Select(s => new UniverseMember
                {
                    Symbol = s,
                    AssetClass = MarketDataRouter.UsesEquityFeed(s, AssetClass.Unknown) ? "Equity" : "Crypto"
                }).ToList();
            }
            return await hub.Universes.GetAsync(def.Universe, ct);
        }

        public async Task<ScreenResult> RunAsync(ScreenDefinition def, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var scope = Scope(def);
            var filters = def.Filters.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => FormulaCompiler.Compile(f, scope)).ToList();
            var columns = def.Columns.Count > 0 ? def.Columns : DefaultColumns();
            var columnFormulas = columns.Where(c => !FieldKeys.Contains(c.Expr))
                .ToDictionary(c => c.Key, c => FormulaCompiler.Compile(c.Expr, scope), StringComparer.OrdinalIgnoreCase);

            var members = (await ResolveUniverseAsync(def, ct)).GroupBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            int lookback = filters.Select(f => f.Lookback).Concat(columnFormulas.Values.Select(f => f.Lookback)).DefaultIfEmpty(0).Max();
            int bars = Math.Clamp(Math.Max(lookback + 30, def.Interval == TimeInterval.OneDay ? 270 : 220), 60, 1500);

            // One batch quote for fundamentals on equity members.
            var equitySymbols = members.Where(m => m.AssetClass != "Crypto").Select(m => m.Symbol).ToList();
            var quotes = new Dictionary<string, QuoteRow>(StringComparer.OrdinalIgnoreCase);
            if (equitySymbols.Count > 0)
            {
                try { foreach (var q in await hub.Yahoo.GetQuotesAsync(equitySymbols, ct)) quotes[q.Symbol] = q; } catch { }
            }

            var rows = new ConcurrentBag<ScreenRow>();
            var errors = new ConcurrentBag<string>();
            int evaluated = 0, failed = 0;
            using var gate = new SemaphoreSlim(8, 8);
            await Task.WhenAll(members.Select(async m =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var row = await EvaluateAsync(m, def, bars, filters, columns, columnFormulas, quotes.GetValueOrDefault(m.Symbol), ct);
                    Interlocked.Increment(ref evaluated);
                    if (row.Error != null) { Interlocked.Increment(ref failed); errors.Add($"{m.Symbol}: {row.Error}"); }
                    else if (row.Passed) rows.Add(row);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Interlocked.Increment(ref failed); errors.Add($"{m.Symbol}: {ex.Message}"); }
                finally { gate.Release(); }
            }));

            string sortKey = def.SortBy ?? columns.FirstOrDefault(c => c.Key != "price")?.Key ?? "chg1d";
            var sorted = (def.Descending
                    ? rows.OrderByDescending(r => r.Values.GetValueOrDefault(sortKey) ?? double.MinValue)
                    : rows.OrderBy(r => r.Values.GetValueOrDefault(sortKey) ?? double.MaxValue))
                .Take(Math.Clamp(def.Limit, 1, 2000)).ToList();

            return new ScreenResult
            {
                Definition = def,
                Rows = sorted,
                Universe = members.Count,
                Evaluated = evaluated,
                Passed = rows.Count,
                Failed = failed,
                Errors = errors.Take(25).ToList(),
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Columns = columns.ToList()
            };
        }

        private async Task<ScreenRow> EvaluateAsync(UniverseMember m, ScreenDefinition def, int barCount,
            List<CompiledFormula> filters, List<ScreenColumn> columns, Dictionary<string, CompiledFormula> columnFormulas,
            QuoteRow? quote, CancellationToken ct)
        {
            var assetClass = m.AssetClass == "Crypto" ? AssetClass.Crypto : AssetClass.Unknown;
            IReadOnlyList<OHLCCandle> candles;
            if (def.IncludeForming)
            {
                var fetch = await marketData.GetChartCandlesAsync(m.Symbol, def.Interval, barCount, assetClass, ct);
                candles = fetch.Forming is { } f ? fetch.Closed.Append(f).ToList() : fetch.Closed;
            }
            else candles = await marketData.GetHistoricalCandlesAsync(m.Symbol, def.Interval, barCount, assetClass, ct);
            if (candles.Count < 30) return new ScreenRow { Symbol = m.Symbol, Name = m.Name, Error = $"only {candles.Count} bars" };

            bool continuous = !MarketDataRouter.UsesEquityFeed(m.Symbol, assetClass);
            var bars = BarSeries.From(candles, CandleClock.BarsPerYear(def.Interval, continuous));
            var fields = ComputeFields(bars, def.Interval, continuous, quote);
            var inputs = new Dictionary<string, double>(def.Inputs, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fields) inputs[k] = v ?? double.NaN;
            var ctx = new FormulaContext { Bars = bars, Inputs = inputs };

            foreach (var filter in filters)
            {
                var v = filter.EvaluateLast(ctx);
                if (v is not { } value || value == 0)
                    return new ScreenRow { Symbol = m.Symbol, Passed = false };
            }

            var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fields) values[k] = v;
            foreach (var c in columns)
                values[c.Key] = FieldKeys.Contains(c.Expr) ? fields.GetValueOrDefault(c.Expr) : columnFormulas[c.Key].EvaluateLast(ctx);

            return new ScreenRow
            {
                Symbol = m.Symbol,
                Name = m.Name ?? quote?.Name,
                Sector = m.Sector,
                AssetClass = continuous ? "Crypto" : m.AssetClass,
                Passed = true,
                Values = values,
                Spark = Spark(bars.Close, 40)
            };
        }

        public static Dictionary<string, double?> ComputeFields(BarSeries bars, TimeInterval interval, bool continuous, QuoteRow? quote)
        {
            var c = bars.Close;
            int n = c.Length;
            double last = c[^1];
            double? Chg(int back) => n > back && c[n - 1 - back] > 0 ? (last / c[n - 1 - back] - 1) * 100 : null;
            double? Last(double[] s) => s.Length > 0 && TA.IsNum(s[^1]) ? s[^1] : null;
            int perDay = interval switch
            {
                TimeInterval.OneDay => 1,
                TimeInterval.OneWeek => 0,
                TimeInterval.OneMonth => 0,
                _ => continuous ? Math.Max(1, 1440 / (int)interval) : Math.Max(1, (int)Math.Ceiling(390.0 / (int)interval))
            };
            int Days(int d) => perDay == 0 ? Math.Max(1, interval == TimeInterval.OneWeek ? d / 5 : d / 21) : d * perDay;

            var avgVol = TA.Sma(TA.Shift(bars.Volume, 1), 20);
            var hi52 = TA.Highest(bars.High, Math.Min(n, Days(continuous ? 365 : 252)));
            var lo52 = TA.Lowest(bars.Low, Math.Min(n, Days(continuous ? 365 : 252)));
            var sma50 = TA.Sma(c, 50);
            var sma200 = TA.Sma(c, 200);
            double? ytd = null;
            var yearStart = bars.Time.Select((t, i) => (t, i)).FirstOrDefault(x => x.t.Year == bars.Time[^1].Year);
            if (yearStart.i > 0 && c[yearStart.i - 1] > 0) ytd = (last / c[yearStart.i - 1] - 1) * 100;

            var f = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
            {
                ["price"] = last,
                ["chg1d"] = Chg(Days(1)),
                ["chg5d"] = Chg(Days(5)),
                ["chg1m"] = Chg(Days(21)),
                ["chg3m"] = Chg(Days(63)),
                ["chg6m"] = Chg(Days(126)),
                ["chg1y"] = Chg(Days(252)),
                ["ytd"] = ytd,
                ["volume"] = bars.Volume[^1],
                ["relvol"] = Last(avgVol) is > 0 ? bars.Volume[^1] / Last(avgVol) : null,
                ["dollarvol"] = Last(TA.DollarVolume(c, bars.Volume, 20)),
                ["rsi14"] = Last(TA.Rsi(c, 14)),
                ["atrpct"] = Last(TA.Natr(bars.High, bars.Low, c, 14)),
                ["vol20"] = Last(TA.HistoricalVolatility(c, 20, bars.BarsPerYear)),
                ["dist52h"] = Last(hi52) is > 0 ? (last / Last(hi52) - 1) * 100 : null,
                ["dist52l"] = Last(lo52) is > 0 ? (last / Last(lo52) - 1) * 100 : null,
                ["sma50dist"] = Last(sma50) is > 0 ? (last / Last(sma50) - 1) * 100 : null,
                ["sma200dist"] = Last(sma200) is > 0 ? (last / Last(sma200) - 1) * 100 : null,
                ["adx14"] = Last(TA.Adx(bars.High, bars.Low, c, 14).Adx),
                ["trend"] = Last(TA.TrendScore(c)),
                ["gap"] = Last(TA.Gap(bars.Open, c)),
                ["mcap"] = quote?.MarketCap,
                ["pe"] = quote?.TrailingPE,
                ["fpe"] = quote?.ForwardPE,
                ["divyield"] = quote?.DividendYieldPct,
            };
            // A live quote is fresher than the last bar for price and the day's change.
            if (quote?.Price is > 0)
            {
                f["price"] = quote.Price;
                if (interval == TimeInterval.OneDay && quote.ChangePct.HasValue) f["chg1d"] = quote.ChangePct;
            }
            return f;
        }

        private static double[] Spark(double[] close, int points)
        {
            if (close.Length <= points) return close.Select(v => Math.Round(v, 6)).ToArray();
            var r = new double[points];
            double step = (close.Length - 1) / (double)(points - 1);
            for (int i = 0; i < points; i++) r[i] = Math.Round(close[(int)Math.Round(i * step)], 6);
            return r;
        }

        // ── presets ───────────────────────────────────────────────────────────────

        public static List<ScreenDefinition> Presets() => new()
        {
            Preset("oversold-uptrend", "Oversold in uptrend", "sp500", "RSI(2) below 10 above a rising 200-day average.",
                new() { "rsi(close, 2) < 10", "close > sma(close, 200)", "sma(close, 200) > sma(close, 200)[20]" }, sort: "rsi2", desc: false,
                extra: new() { new() { Key = "rsi2", Label = "RSI 2", Expr = "rsi(close, 2)" } }),
            Preset("new-highs", "52-week highs", "sp500", "Within 1% of the 52-week high on above-average volume.",
                new() { "dist52h > -1", "relvol > 1.2" }, sort: "relvol"),
            Preset("golden-cross", "Golden cross (10 bars)", "sp500", "50-day crossed above 200-day in the last 10 sessions.",
                new() { "barssince(crossover(sma(close, 50), sma(close, 200))) <= 10" }, sort: "chg1m"),
            Preset("volume-surge", "Volume surge", "sp500", "Volume ≥ 3× its 20-day average.", new() { "relvol >= 3" }, sort: "relvol"),
            Preset("squeeze", "Volatility squeeze", "sp500", "Bollinger bands inside Keltner channels.", new() { "squeeze(20) == 1" }, sort: "atrpct", desc: false),
            Preset("momentum-leaders", "Momentum leaders", "sp500", "Top 6-month return, above the 50-day, ADX trending.",
                new() { "close > sma(close, 50)", "adx(14).adx > 20" }, sort: "chg6m"),
            Preset("crypto-momentum", "Crypto momentum", "crypto-top100", "Liquid coins above both averages with rising 30-day momentum.",
                new() { "close > ema(close, 20)", "ema(close, 20) > ema(close, 50)", "roc(close, 30) > 0" }, sort: "chg1m"),
            Preset("crypto-oversold", "Crypto oversold", "crypto-top100", "RSI(14) under 30.", new() { "rsi(close, 14) < 30" }, sort: "rsi14", desc: false),
            Preset("crypto-breakout", "Crypto 20-day breakout", "crypto-top100", "Close above the prior 20-day high.",
                new() { "close > highest(high, 20)[1]" }, sort: "relvol"),
            Preset("value-quality", "Cheap and profitable", "sp500", "P/E between 0 and 15 with positive 1-year return.",
                new() { "pe > 0", "pe < 15", "chg1y > 0" }, sort: "pe", desc: false,
                extra: new() { new() { Key = "pe", Label = "P/E", Expr = "pe" }, new() { Key = "mcap", Label = "Mkt cap", Expr = "mcap", Format = "compact" } }),
            Preset("ftse-movers", "FTSE 100 movers", "ftse100", "All FTSE 100 constituents by today's move.", new(), sort: "chg1d"),
        };

        private static ScreenDefinition Preset(string id, string name, string universe, string description, List<string> filters,
            string sort, bool desc = true, List<ScreenColumn>? extra = null)
        {
            var cols = DefaultColumns();
            if (extra != null) foreach (var c in extra) if (cols.All(x => x.Key != c.Key)) cols.Add(c);
            return new ScreenDefinition
            {
                Id = "preset:" + id, Name = name, Universe = universe, Description = description, Filters = filters,
                Columns = cols, SortBy = sort, Descending = desc, Preset = true
            };
        }
    }
}
