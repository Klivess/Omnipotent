using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using Omnipotent.Services.OmniTrader.Venues;
using System.Globalization;
using System.Net;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniTrader.Api
{
    /// <summary>
    /// HTTP surface of the terminal: market data and analytics (/terminal), the screener, saved
    /// layouts, the strategy studio (/studio), backtests and studies (/lab) and the Alpha Lab
    /// (/alpha). Live-data reads are never response-cached; stores they touch are instrumented.
    /// </summary>
    public sealed partial class TerminalRoutes
    {
        private const string Base = "/api/omnitrader";
        private readonly OmniTrader parent;
        private DataHub Hub => parent.Data;
        private readonly DocumentStore<ScreenDefinition> screens;
        private readonly DocumentStore<TerminalLayout> layouts;

        public TerminalRoutes(OmniTrader parent)
        {
            this.parent = parent;
            screens = new DocumentStore<ScreenDefinition>(parent.Db, "screens", s => s.Id,
                s => new DocumentKeys(K1: s.Universe, T1: s.UpdatedUtc));
            layouts = new DocumentStore<TerminalLayout>(parent.Db, "layouts", l => l.Id,
                l => new DocumentKeys(K1: l.Kind, K2: l.Scope, T1: l.UpdatedUtc));
        }

        public async Task RegisterAsync()
        {
            await RegisterMarketDataAsync();
            await RegisterEquitiesAsync();
            await RegisterCryptoAsync();
            await RegisterMacroAsync();
            await RegisterAnalyticsAsync();
            await RegisterScreenerAsync();
            await RegisterAlertsAsync();
            await RegisterLayoutsAsync();
            await RegisterStudioAsync();
            await RegisterLabAsync();
            await RegisterAlphaAsync();
        }

        // ── market data ──────────────────────────────────────────────────────────

        private async Task RegisterMarketDataAsync()
        {
            await Get("/terminal/quotes", OmniTraderPerms.TerminalRead, async req =>
            {
                var symbols = Csv(req, "symbols");
                if (symbols.Count == 0) { await Bad(req, "symbols required"); return; }
                Live();
                await Json(req, await Hub.GetQuotesAsync(symbols.Take(200)));
            });

            await Get("/terminal/chart", OmniTraderPerms.TerminalRead, async req =>
            {
                var r = new ChartRequest
                {
                    Symbol = req.userParameters.Get("symbol"),
                    Interval = req.userParameters.Get("interval"),
                    Count = ParseNullableInt(req.userParameters.Get("count")),
                    From = ParseUtc(req.userParameters.Get("from")),
                    To = ParseUtc(req.userParameters.Get("to")),
                    Compare = Csv(req, "compare"),
                    IncludeForming = req.userParameters.Get("forming") != "0",
                };
                try
                {
                    string? ind = req.userParameters.Get("ind");
                    if (!string.IsNullOrWhiteSpace(ind)) r.Indicators = JsonConvert.DeserializeObject<List<IndicatorRequest>>(ind);
                    string? formulas = req.userParameters.Get("formulas");
                    if (!string.IsNullOrWhiteSpace(formulas)) r.Formulas = JsonConvert.DeserializeObject<List<ChartFormula>>(formulas);
                }
                catch (Exception ex) { await Bad(req, "Bad indicator or formula list: " + ex.Message); return; }
                await ChartAsync(req, r);
            });

            await Post("/terminal/chart", OmniTraderPerms.TerminalRead, async req =>
            {
                var r = Body<ChartRequest>(req);
                if (r == null) { await Bad(req, "Body must be a chart request"); return; }
                await ChartAsync(req, r);
            });

            await Get("/terminal/indicators", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, IndicatorCatalog.All.Select(IndicatorEngine.Describe)));

            await Get("/terminal/formula/reference", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, FormulaCompiler.Reference()));

            await Post("/terminal/formula/check", OmniTraderPerms.TerminalRead, async req =>
            {
                var dto = Body<FormulaCheckDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Formula)) { await Bad(req, "formula required"); return; }
                var scope = FormulaScope.With(dto.Inputs?.Keys, dto.Variables);
                var (compiled, errors) = FormulaCompiler.Check(dto.Formula, scope);
                if (compiled == null || string.IsNullOrWhiteSpace(dto.Symbol))
                {
                    await Json(req, new
                    {
                        Ok = compiled != null, Errors = errors, compiled?.IsBoolean, compiled?.Lookback,
                        compiled?.Inputs, compiled?.Variables, compiled?.ExternalSymbols, compiled?.Functions
                    });
                    return;
                }
                Live();
                var interval = CandleClock.Parse(dto.Interval) ?? TimeInterval.OneDay;
                var candles = await parent.MarketData.GetHistoricalCandlesAsync(dto.Symbol!.Trim().ToUpperInvariant(), interval,
                    Math.Clamp(Math.Max(dto.Bars ?? 300, compiled.Lookback + 50), 50, 5000));
                if (candles.Count == 0) { await Json(req, new { Ok = true, Errors = errors, Preview = Array.Empty<object>(), Note = "No bars for symbol" }); return; }
                var bars = BarSeries.From(candles);
                var ext = await ExternalSeriesAsync(compiled.ExternalSymbols, interval, candles.Count);
                double[] values;
                try
                {
                    values = compiled.Evaluate(new FormulaContext
                    {
                        Bars = bars, Inputs = dto.Inputs, External = s => ext.TryGetValue(s, out var b) ? b : null
                    });
                }
                catch (Exception ex) { await Json(req, new { Ok = false, Errors = new[] { new { Message = ex.Message, Position = 0, Length = 1 } } }); return; }
                int take = Math.Min(20, values.Length);
                int valid = values.Count(TA.IsNum);
                await Json(req, new
                {
                    Ok = true, Errors = errors, compiled.IsBoolean, compiled.Lookback, compiled.Inputs, compiled.Variables, compiled.ExternalSymbols,
                    Bars = candles.Count, Valid = valid,
                    TrueCount = compiled.IsBoolean ? values.Count(v => v == 1) : (int?)null,
                    Last = TA.IsNum(values[^1]) ? values[^1] : (double?)null,
                    Preview = Enumerable.Range(values.Length - take, take).Select(i => new { Time = candles[i].Timestamp, Value = TA.IsNum(values[i]) ? Math.Round(values[i], 8) : (double?)null })
                });
            });

            await Get("/terminal/universes", OmniTraderPerms.TerminalRead, async req =>
            {
                var lists = parent.Firm == null ? new() : await parent.Firm.Watchlists.ListAsync();
                await Json(req, new
                {
                    Catalog = UniverseCatalog.Known,
                    Watchlists = lists.Select(w => new { Id = "watchlist:" + w.Id, w.Name, Group = "Watchlists", Size = w.InstrumentIds.Count })
                });
            });

            await Get("/terminal/universe", OmniTraderPerms.TerminalRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                Live();
                var members = await parent.Screener.ResolveUniverseAsync(new ScreenDefinition { Universe = id }, CancellationToken.None);
                await Json(req, new { Id = id, Members = members, Count = members.Count });
            });

            await Get("/terminal/monitor", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await Json(req, await Hub.GetMonitorAsync());
            });

            await Get("/terminal/breadth", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await Json(req, await Hub.GetBreadthAsync(req.userParameters.Get("universe") ?? "sp500"));
            });

            await Get("/terminal/news", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                int limit = ParseInt(req.userParameters.Get("limit"), 80, 1, 300);
                await Json(req, await Hub.GetNewsAsync(req.userParameters.Get("category"), req.userParameters.Get("symbol"),
                    req.userParameters.Get("q"), limit) ?? new());
            });

            await Get("/terminal/sources", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await Json(req, new { Sources = SourceHealth.Snapshot(), FredConfigured = !string.IsNullOrWhiteSpace(await FredKey()) });
            });

            await Get("/terminal/cost-presets", OmniTraderPerms.TerminalRead, async req => await Json(req, CostPresets.All));
        }

        private async Task ChartAsync(UserRequest req, ChartRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.Symbol)) { await Bad(req, "symbol required"); return; }
            Live();
            try { await Json(req, await BuildChartAsync(r)); }
            catch (Exception ex) { await Bad(req, ex.Message); }
        }

        /// <summary>
        /// Bars (closed plus the forming one), any catalog indicators, any formulas and comparison
        /// symbols, all aligned to the same bar index. Columnar so a 5,000-bar chart stays small.
        /// </summary>
        private async Task<object> BuildChartAsync(ChartRequest r)
        {
            string symbol = r.Symbol!.Trim().ToUpperInvariant();
            var interval = CandleClock.Parse(r.Interval) ?? TimeInterval.OneDay;
            int count = Math.Clamp(r.Count ?? 500, 20, 20_000);

            IReadOnlyList<OHLCCandle> closed;
            OHLCCandle? forming = null;
            string? source;
            if (r.From.HasValue)
            {
                closed = await parent.MarketData.GetHistoricalCandlesRangeAsync(symbol, interval, r.From.Value, r.To ?? DateTime.UtcNow);
                source = parent.MarketData.SourceFor(symbol, interval);
            }
            else
            {
                var fetch = await parent.MarketData.GetChartCandlesAsync(symbol, interval, count);
                closed = fetch.Closed;
                forming = r.IncludeForming == false ? null : fetch.Forming;
                source = fetch.Source;
            }
            var candles = forming is { } f ? closed.Append(f).ToList() : closed.ToList();

            Dictionary<string, BarSeries?> ext = new(StringComparer.OrdinalIgnoreCase);
            if (r.Formulas is { Count: > 0 } && candles.Count > 0)
            {
                var symbols = r.Formulas.Take(20).Select(x => FormulaCompiler.Check(x.Formula ?? "").Formula).Where(x => x != null).SelectMany(x => x!.ExternalSymbols);
                ext = await ExternalSeriesAsync(symbols, interval, candles.Count);
            }

            var compare = new Dictionary<string, (IReadOnlyList<OHLCCandle>? Bars, string? Error)>(StringComparer.OrdinalIgnoreCase);
            if (r.Compare is { Count: > 0 } && candles.Count > 0)
            {
                // Fetched together: each can be a provider round trip, and one after another they added up.
                async Task<(string Symbol, IReadOnlyList<OHLCCandle>? Bars, string? Error)> FetchAsync(string other)
                {
                    try
                    {
                        var bars = r.From.HasValue
                            ? await parent.MarketData.GetHistoricalCandlesRangeAsync(other, interval, r.From.Value, r.To ?? DateTime.UtcNow)
                            : (await parent.MarketData.GetChartCandlesAsync(other, interval, Math.Min(candles.Count + 50, 20_000))) is var cf
                                && cf.Forming is { } of ? cf.Closed.Append(of).ToList() : cf.Closed;
                        return (other, bars, null);
                    }
                    catch (Exception ex) { return (other, null, ex.Message); }
                }
                var others = r.Compare.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0 && c != symbol).Distinct().Take(8);
                foreach (var (other, bars, error) in await Task.WhenAll(others.Select(FetchAsync)))
                    compare[other] = (bars, error);
            }

            return ChartPayload(symbol, interval, candles, forming != null, source, r.Indicators, r.Formulas, ext, compare);
        }

        /// <summary>The chart response from bars already in hand: pure, so the e2e fixtures come from it too.</summary>
        internal static object ChartPayload(string symbol, TimeInterval interval, IReadOnlyList<OHLCCandle> candles, bool forming, string? source,
            IReadOnlyList<IndicatorRequest>? indicatorRequests, IReadOnlyList<ChartFormula>? formulaSpecs,
            IReadOnlyDictionary<string, BarSeries?>? external, IReadOnlyDictionary<string, (IReadOnlyList<OHLCCandle>? Bars, string? Error)>? compareBars)
        {
            bool equity = MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown);
            double barsPerYear = CandleClock.BarsPerYear(interval, !equity);
            var series = BarSeries.From(candles, barsPerYear);

            var indicators = indicatorRequests is { Count: > 0 } && candles.Count > 0
                ? IndicatorEngine.ComputeAll(series, indicatorRequests.Take(40))
                : new List<IndicatorResult>();

            var formulas = new List<object>();
            if (formulaSpecs is { Count: > 0 } && candles.Count > 0)
            {
                var ctx = new FormulaContext { Bars = series, External = s => external != null && external.TryGetValue(s, out var b) ? b : null };
                foreach (var spec in formulaSpecs.Take(20))
                {
                    var (formula, errors) = FormulaCompiler.Check(spec.Formula ?? "");
                    if (formula == null) { formulas.Add(new { spec.Key, spec.Formula, Errors = errors }); continue; }
                    try
                    {
                        formulas.Add(new { spec.Key, spec.Formula, formula.IsBoolean, formula.Lookback, Values = IndicatorEngine.ToNullable(formula.Evaluate(ctx)) });
                    }
                    catch (Exception ex)
                    {
                        formulas.Add(new { spec.Key, spec.Formula, Errors = new[] { new FormulaDiagnostic { Message = ex.Message } } });
                    }
                }
            }

            var compare = new List<object>();
            foreach (var (other, (bars, error)) in compareBars ?? new Dictionary<string, (IReadOnlyList<OHLCCandle>? Bars, string? Error)>())
            {
                if (bars == null) compare.Add(new { Symbol = other, Error = error ?? "no data" });
                else compare.Add(new { Symbol = other, Close = AlignCloses(candles, bars) });
            }

            return new
            {
                Symbol = symbol,
                Interval = interval.ToString(),
                Source = source,
                AssetClass = equity ? "Equity" : "Crypto",
                Streaming = MarketDataRouter.IsStreamingLive(symbol, AssetClass.Unknown),
                Forming = forming,
                BarsPerYear = barsPerYear,
                Time = candles.Select(c => Unix(c.Timestamp)),
                Open = candles.Select(c => (double)c.Open),
                High = candles.Select(c => (double)c.High),
                Low = candles.Select(c => (double)c.Low),
                Close = candles.Select(c => (double)c.Close),
                Volume = candles.Select(c => (double)c.Volume),
                Indicators = indicators,
                Formulas = formulas,
                Compare = compare,
            };
        }

        /// <summary>Another symbol's closes on this chart's bar times: the last close at or before each bar.</summary>
        internal static double?[] AlignCloses(IReadOnlyList<OHLCCandle> main, IReadOnlyList<OHLCCandle> other)
        {
            var result = new double?[main.Count];
            int j = 0;
            double? last = null;
            for (int i = 0; i < main.Count; i++)
            {
                while (j < other.Count && other[j].Timestamp <= main[i].Timestamp) { last = (double)other[j].Close; j++; }
                result[i] = last;
            }
            return result;
        }

        private async Task<Dictionary<string, BarSeries?>> ExternalSeriesAsync(IEnumerable<string> symbols, TimeInterval interval, int bars)
        {
            // Fetched together, as for chart comparisons.
            async Task<(string Symbol, BarSeries? Series)> FetchAsync(string sym)
            {
                try
                {
                    var b = await parent.MarketData.GetHistoricalCandlesAsync(sym.ToUpperInvariant(), interval, Math.Clamp(bars + 50, 50, 20_000));
                    return (sym, b.Count == 0 ? null : BarSeries.From(b));
                }
                catch { return (sym, null); }
            }
            var ext = new Dictionary<string, BarSeries?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, series) in await Task.WhenAll(symbols.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).Select(FetchAsync)))
                ext[sym] = series;
            return ext;
        }

        // ── equities ─────────────────────────────────────────────────────────────

        private async Task RegisterEquitiesAsync()
        {
            await Get("/terminal/company", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetCompanyAsync(s));
            });

            await Get("/terminal/financials", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetFinancialsAsync(s));
            });

            await Get("/terminal/sec/facts", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetSecFactsAsync(s));
            });

            await Get("/terminal/sec/filings", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetFilingsAsync(s, req.userParameters.Get("form")));
            });

            await Get("/terminal/options", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                DateTime? expiry = ParseUtc(req.userParameters.Get("expiration"));
                await JsonOrNotFound(req, await Hub.GetOptionsAsync(s, expiry));
            });

            await Get("/terminal/options/term", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetOptionTermAsync(s));
            });

            await Get("/terminal/movers", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                string screen = req.userParameters.Get("screen") ?? "day_gainers";
                int count = ParseInt(req.userParameters.Get("count"), 25, 5, 100);
                await JsonOrNotFound(req, await Hub.GetMoversAsync(screen, count));
            });

            await Get("/terminal/trending", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                var symbols = await Hub.GetTrendingAsync(req.userParameters.Get("region") ?? "US") ?? new();
                var quotes = symbols.Count == 0 ? new() : await Hub.GetQuotesAsync(symbols);
                await Json(req, new { Symbols = symbols, Quotes = quotes });
            });
        }

        // ── crypto ───────────────────────────────────────────────────────────────

        private async Task RegisterCryptoAsync()
        {
            await Get("/terminal/crypto/global", OmniTraderPerms.TerminalRead, async req => { Live(); await JsonOrNotFound(req, await Hub.GetCryptoGlobalAsync()); });

            await Get("/terminal/crypto/coins", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await Json(req, await Hub.GetTopCoinsAsync(ParseInt(req.userParameters.Get("count"), 100, 10, 250)) ?? new());
            });

            await Get("/terminal/crypto/trending", OmniTraderPerms.TerminalRead, async req => { Live(); await Json(req, await Hub.GetTrendingCoinsAsync() ?? new()); });

            await Get("/terminal/crypto/fear-greed", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await Json(req, await Hub.GetFearGreedAsync(ParseInt(req.userParameters.Get("days"), 365, 7, 2000)) ?? new());
            });

            await Get("/terminal/crypto/defi", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                var tvl = Hub.GetTvlAsync();
                var chains = Hub.GetChainsAsync();
                var stables = Hub.GetStablecoinsAsync();
                await Task.WhenAll(tvl, chains, stables);
                await Json(req, new { Tvl = tvl.Result ?? new(), Chains = chains.Result ?? new(), Stablecoins = stables.Result ?? new() });
            });

            await Get("/terminal/crypto/funding", OmniTraderPerms.TerminalRead, async req => { Live(); await Json(req, await Hub.GetFundingBoardAsync() ?? new()); });

            await Get("/terminal/crypto/derivatives", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetDerivativesAsync(s, req.userParameters.Get("period") ?? "1h",
                    ParseInt(req.userParameters.Get("limit"), 168, 10, 500)));
            });

            await Get("/terminal/crypto/dvol", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                string currency = (req.userParameters.Get("currency") ?? "BTC").ToUpperInvariant();
                await Json(req, await Hub.GetDvolAsync(currency, ParseInt(req.userParameters.Get("days"), 365, 7, 2000)) ?? new());
            });

            await Get("/terminal/crypto/options", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                await JsonOrNotFound(req, await Hub.GetCryptoOptionSurfaceAsync((req.userParameters.Get("currency") ?? "BTC").ToUpperInvariant()));
            });

            await Get("/terminal/crypto/orderbook", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } s) { await Bad(req, "symbol required"); return; }
                Live();
                int rows = ParseInt(req.userParameters.Get("rows") ?? req.userParameters.Get("depth"), 20, 5, 50);
                decimal? group = decimal.TryParse(req.userParameters.Get("group"), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0 ? g : null;
                await JsonOrNotFound(req, await Hub.GetOrderBookAsync(s, rows, group));
            });
        }

        // ── macro ────────────────────────────────────────────────────────────────

        private async Task RegisterMacroAsync()
        {
            await Get("/terminal/macro", OmniTraderPerms.TerminalRead, async req => { Live(); await Json(req, await Hub.GetMacroAsync() ?? new()); });

            await Get("/terminal/macro/curve", OmniTraderPerms.TerminalRead, async req =>
            {
                Live();
                var history = await Hub.GetCurveHistoryAsync() ?? new();
                await Json(req, new { Latest = history.LastOrDefault(), History = history });
            });

            await Get("/terminal/macro/volterm", OmniTraderPerms.TerminalRead, async req => { Live(); await JsonOrNotFound(req, await Hub.GetVolTermAsync()); });

            await Get("/terminal/macro/fred", OmniTraderPerms.TerminalRead, async req =>
            {
                string? series = req.userParameters.Get("series");
                if (string.IsNullOrWhiteSpace(series)) { await Bad(req, "series required"); return; }
                if (string.IsNullOrWhiteSpace(await FredKey())) { await Bad(req, "Set OmniTrader.FRED.ApiKey (free at fred.stlouisfed.org) to use FRED series."); return; }
                Live();
                await JsonOrNotFound(req, await Hub.GetFredAsync(series.Trim().ToUpperInvariant()));
            });
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler)
            => parent.RegisterRouteAsync(Base + path, Guard(handler), HttpMethod.Get, permission);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler)
            => parent.RegisterRouteAsync(Base + path, Guard(handler), HttpMethod.Post, permission);

        /// <summary>A handler that throws answers with its message instead of an empty 500.</summary>
        private Func<UserRequest, Task> Guard(Func<UserRequest, Task> handler) => async req =>
        {
            try { await handler(req); }
            catch (ArgumentException ex) { await Bad(req, ex.Message); }
            catch (InvalidOperationException ex) { await Bad(req, ex.Message); }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, $"OmniTrader route {req.route} failed");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Error = ex.Message }), code: HttpStatusCode.InternalServerError);
            }
        };

        /// <summary>Live data: the response must never be replayed from the response cache.</summary>
        private static void Live() => CacheDeps.MarkUncacheable("terminal-live");

        internal static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Include,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter(), new FiniteDoubleConverter() }
        };

        private static Task Json(UserRequest req, object? payload)
            => req.ReturnResponse(JsonConvert.SerializeObject(payload, JsonSettings), "application/json");

        private static Task JsonOrNotFound(UserRequest req, object? payload)
            => payload == null ? NotFound(req) : Json(req, payload);

        private static Task Bad(UserRequest req, string message)
            => req.ReturnResponse(JsonConvert.SerializeObject(new { Error = message }), "application/json", code: HttpStatusCode.BadRequest);

        private static Task NotFound(UserRequest req, string message = "Not found")
            => req.ReturnResponse(JsonConvert.SerializeObject(new { Error = message }), "application/json", code: HttpStatusCode.NotFound);

        private static T? Body<T>(UserRequest req) where T : class
        {
            try { return JsonConvert.DeserializeObject<T>(req.userMessageContent ?? "", JsonSettings); }
            catch { return null; }
        }

        private static string Actor(UserRequest req)
        {
            try
            {
                string? name = req.user?.Name;
                return string.IsNullOrWhiteSpace(name) ? "api" : name!;
            }
            catch { return "api"; }
        }

        private static string? Symbol(UserRequest req)
        {
            string? s = req.userParameters.Get("symbol");
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
        }

        private static List<string> Csv(UserRequest req, string key)
            => (req.userParameters.Get(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).Distinct().ToList();

        private static DateTime? ParseUtc(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? null
             : long.TryParse(raw, out var unix) && unix > 100_000_000 ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
             : DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

        private static int ParseInt(string? raw, int fallback, int min, int max)
            => int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;

        private static int? ParseNullableInt(string? raw) => int.TryParse(raw, out var v) ? v : null;

        private static double? ParseDouble(string? raw)
            => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static bool ParseBool(string? raw, bool fallback = false)
            => raw == null ? fallback : raw is "1" or "true" or "yes" or "on";

        private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        private async Task<string?> FredKey()
        {
            try { return await parent.GetStringOmniSetting("OmniTrader.FRED.ApiKey", sensitive: true); }
            catch { return null; }
        }
    }

    /// <summary>NaN and infinities are not JSON: they go out as null rather than as "NaN" strings.</summary>
    public sealed class FiniteDoubleConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(double) || objectType == typeof(double?);
        public override bool CanRead => false;
        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            => throw new NotSupportedException();
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is double d && double.IsFinite(d)) writer.WriteValue(d);
            else writer.WriteNull();
        }
    }

    // ── request / record types ───────────────────────────────────────────────────

    public sealed class ChartRequest
    {
        public string? Symbol { get; set; }
        public string? Interval { get; set; }
        public int? Count { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public List<IndicatorRequest>? Indicators { get; set; }
        public List<ChartFormula>? Formulas { get; set; }
        public List<string>? Compare { get; set; }
        public bool? IncludeForming { get; set; }
    }

    public sealed class ChartFormula
    {
        public string Key { get; set; } = "";
        public string? Formula { get; set; }
    }

    public sealed class FormulaCheckDto
    {
        public string Formula { get; set; } = "";
        public string? Symbol { get; set; }
        public string? Interval { get; set; }
        public int? Bars { get; set; }
        public Dictionary<string, double>? Inputs { get; set; }
        public List<string>? Variables { get; set; }
    }

    /// <summary>A saved arrangement: a chart template (indicators, formulas, style), a workspace
    /// (panels and their settings) or anything else the UI wants to persist server-side.</summary>
    public sealed class TerminalLayout
    {
        public string Id { get; set; } = "ly_" + Guid.NewGuid().ToString("N")[..12];
        public string Name { get; set; } = "Untitled";
        /// <summary>chart | workspace | screen-columns | …</summary>
        public string Kind { get; set; } = "chart";
        /// <summary>Optional narrowing, e.g. a symbol for per-symbol drawings.</summary>
        public string? Scope { get; set; }
        public JToken? Data { get; set; }
        public bool Default { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
