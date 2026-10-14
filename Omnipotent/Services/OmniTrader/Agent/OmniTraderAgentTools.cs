using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using Omnipotent.Services.ServiceTools;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Agent
{
    /// <summary>
    /// OmniTrader's first-class KliveAgent tools: research papers and alpha candidates, the strategy
    /// studio, backtests and studies, and market data. Exposed on the service as
    /// <see cref="OmniTrader.Agent"/>, so the tool registry discovers it.
    /// </summary>
    public sealed class OmniTraderAgentTools
    {
        private readonly OmniTrader trader;
        private const string Author = "kliveagent";

        public OmniTraderAgentTools(OmniTrader trader) { this.trader = trader; }

        private AlphaLab Lab => trader.AlphaLab ?? throw new InvalidOperationException("The Alpha Lab is not running.");
        private StudioService Studio => trader.Studio ?? throw new InvalidOperationException("The Studio is not running.");

        // ── research ─────────────────────────────────────────────────────────────

        [OmniTool("papers", "List research papers found by the Alpha Lab, best first. Each row: id, title, relevance and feasibility (0-100), families, assets, status.", Group = "research")]
        public async Task<object> PapersAsync(
            [OmniParam("Filter by status.", Values = new[] { "New", "Queued", "Analysing", "Analysed", "Implemented", "Rejected" })] string? status = null,
            [OmniParam("Text to search titles/abstracts for.")] string? query = null,
            [OmniParam("Minimum relevance 0-100.")] double minRelevance = 0,
            [OmniParam("Rows to return (max 50).")] int limit = 15)
        {
            var list = await Lab.ListPapersAsync(status, query, minRelevance > 0 ? minRelevance : null, "n1", Math.Clamp(limit, 1, 50));
            return list.Select(p => new
            {
                p.Id, p.Title, Published = p.PublishedUtc?.ToString("yyyy-MM-dd"), p.Relevance, p.Feasibility,
                p.Families, p.AssetClasses, p.DataNeeds, Status = p.Status.ToString(), p.Verdict
            });
        }

        [OmniTool("paper_get", "Full record of one paper: abstract, authors, links (abs page + PDF), tags, prior verdict and candidates.", Group = "research")]
        public async Task<object?> PaperGetAsync([OmniParam("Paper id, e.g. arxiv:2410.01234 or openalex:W123.")] string id)
            => await Lab.GetPaperAsync(id) is { } p ? new
            {
                p.Id, p.Title, p.Authors, Published = p.PublishedUtc?.ToString("yyyy-MM-dd"), p.Venue, p.Url, p.PdfUrl, p.Doi,
                p.Abstract, p.Relevance, p.Feasibility, p.Families, p.AssetClasses, p.DataNeeds, Status = p.Status.ToString(),
                p.Verdict, p.Notes, p.CandidateIds
            } : null;

        [OmniTool("paper_verdict", "Record the outcome of analysing a paper: implemented (a candidate was submitted), rejected (no testable alpha on our data), or analysed. Notes should state the rule and anything not reproducible.", Group = "research", Mutating = true)]
        public async Task<string> PaperVerdictAsync(
            [OmniParam("Paper id.")] string id,
            [OmniParam("Outcome.", Values = new[] { "implemented", "rejected", "analysed" })] string verdict,
            [OmniParam("Short analysis.")] string notes)
        {
            var p = await Lab.SetVerdictAsync(id, verdict, notes, Author);
            return p == null ? $"No paper {id}." : $"{p.Id} → {p.Status}.";
        }

        [OmniTool("paper_add", "Add a paper by hand (title, abstract, URL) and queue it for analysis.", Group = "research", Mutating = true)]
        public async Task<string> PaperAddAsync(string title, string? abstractText = null, string? url = null)
        {
            var p = await Lab.AddPaperAsync(title, abstractText, url, Author);
            return $"Added {p.Id} (relevance {p.Relevance}).";
        }

        [OmniTool("alpha_submit", "Submit a Studio strategy as the implementation of a paper's idea. OmniTrader evaluates it automatically (multi-symbol, 2x costs, walk-forward, deflated Sharpe) and ranks it.", Group = "research", Mutating = true)]
        public async Task<string> AlphaSubmitAsync(
            [OmniParam("Studio strategy id (st_...).")] string strategyId,
            [OmniParam("One-sentence hypothesis being tested.")] string hypothesis,
            [OmniParam("Paper id the idea came from, if any.")] string? paperId = null,
            [OmniParam("Comma-separated symbols to test on (optional; defaults by asset class).")] string? testSymbols = null,
            [OmniParam("Bar interval.", Values = new[] { "OneHour", "FourHour", "OneDay", "OneWeek" })] string? interval = null,
            string? notes = null)
        {
            var c = await Lab.SubmitAsync(paperId, strategyId, hypothesis,
                testSymbols?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                CandleClock.Parse(interval), Author, notes);
            return $"Candidate {c.Id} queued for evaluation ({c.StrategyName} v{c.StrategyVersion}). Check with op=candidate_get.";
        }

        [OmniTool("candidates", "Alpha candidates ranked by evaluation score.", Group = "research")]
        public async Task<object> CandidatesAsync(
            [OmniParam("Filter by status.", Values = new[] { "Proposed", "Evaluating", "Promising", "Weak", "Rejected", "Failed", "Promoted" })] string? status = null,
            int limit = 20)
        {
            var list = await Lab.ListCandidatesAsync(status, null, Math.Clamp(limit, 1, 100));
            return list.Select(c => new
            {
                c.Id, c.StrategyId, c.StrategyName, c.PaperTitle, Status = c.Status.ToString(), c.Score, c.Hypothesis,
                Sharpe = c.Evaluation?.Primary?.Sharpe, ReturnPct = c.Evaluation?.Primary?.ReturnPct, Dsr = c.Evaluation?.DeflatedSharpe
            });
        }

        [OmniTool("candidate_get", "One candidate's evaluation: per-symbol metrics, cost stress, walk-forward, deflated Sharpe, score breakdown and findings.", Group = "research")]
        public async Task<object?> CandidateGetAsync(string id) => await Lab.GetCandidateAsync(id);

        [OmniTool("ingest_now", "Fetch new papers from arXiv and OpenAlex now.", Group = "research", Mutating = true)]
        public async Task<string> IngestNowAsync()
        {
            var run = await Lab.IngestAsync("kliveagent");
            return run.Summary ?? run.Error ?? run.Status;
        }

        // ── studio ───────────────────────────────────────────────────────────────

        [OmniTool("reference", "Everything needed to write a strategy: the blueprint JSON schema with examples, the C# script API, the formula language and every indicator. Read once before writing.", Group = "studio")]
        public string Reference() => StudioReference.Text;

        [OmniTool("templates", "Starter strategies (blueprints and scripts) with their ids.", Group = "studio")]
        public object Templates() => Studio.Templates.Select(t => new { t.Id, t.Name, Kind = t.Kind.ToString(), t.Category, t.Description });

        [OmniTool("list", "Studio strategies (blueprints and scripts), newest first.", Group = "studio")]
        public async Task<object> ListAsync(string? query = null, [OmniParam("klives | kliveagent | alpha-lab")] string? author = null)
        {
            var list = await Studio.ListAsync(author, null, query, false, 100);
            return list.Select(s => new
            {
                s.Id, s.Name, Kind = s.Kind.ToString(), Status = s.Status.ToString(), s.Version, s.Author, s.DefaultSymbol,
                Interval = s.DefaultInterval.ToString(), s.LastMetrics, s.Origin
            });
        }

        [OmniTool("get", "A studio strategy in full: blueprint JSON or C# source, diagnostics, compiled formulas.", Group = "studio")]
        public async Task<object?> GetAsync(string id) => await Studio.GetAsync(id);

        [OmniTool("create", "Create a strategy. kind=blueprint takes the blueprint JSON as definition; kind=script takes C# source. Returns the id and any compile diagnostics.", Group = "studio", Mutating = true)]
        public async Task<object> CreateAsync(
            string name,
            [OmniParam("blueprint | script", Values = new[] { "blueprint", "script" })] string kind,
            [OmniParam("Blueprint JSON (kind=blueprint) or complete C# source (kind=script).")] string definition,
            [OmniParam("Default symbol, e.g. BTCUSDT or SPY.")] string? symbol = null,
            [OmniParam("Default interval.", Values = new[] { "OneHour", "FourHour", "OneDay", "OneWeek" })] string? interval = null,
            string? description = null,
            [OmniParam("Comma-separated tags.")] string? tags = null,
            [OmniParam("Where the idea came from, e.g. paper:arxiv:2410.01234.")] string? origin = null)
        {
            var draft = Draft(kind, definition);
            draft.Name = name;
            draft.Description = description;
            draft.DefaultSymbol = symbol;
            draft.DefaultInterval = CandleClock.Parse(interval);
            draft.Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            draft.Origin = origin;
            var s = await Studio.CreateAsync(draft, Author);
            return Summary(s);
        }

        [OmniTool("update", "Change a strategy's definition (blueprint JSON or C# source) or metadata. A logic change creates a new version.", Group = "studio", Mutating = true)]
        public async Task<object?> UpdateAsync(string id, string? definition = null, string? name = null, string? description = null,
            [OmniParam("What changed and why.")] string? note = null, string? symbol = null, string? interval = null)
        {
            var existing = await Studio.GetAsync(id);
            if (existing == null) return $"No strategy {id}.";
            var draft = definition == null ? new StudioDraft() : Draft(existing.Kind == StudioKind.Script ? "script" : "blueprint", definition);
            draft.Name = name;
            draft.Description = description;
            draft.Note = note;
            draft.DefaultSymbol = symbol;
            draft.DefaultInterval = CandleClock.Parse(interval);
            var s = await Studio.UpdateAsync(id, draft, Author);
            return s == null ? null : Summary(s);
        }

        [OmniTool("validate", "Compile a definition without saving it. Returns diagnostics (with line numbers for scripts, rule paths for blueprints) and the compiled formulas.", Group = "studio")]
        public object Validate(
            [OmniParam("blueprint | script", Values = new[] { "blueprint", "script" })] string kind,
            string definition)
        {
            var draft = Draft(kind, definition);
            var check = Studio.Check(draft.Kind!.Value, draft.Blueprint, draft.Source, "agent_check_" + Guid.NewGuid().ToString("N")[..8]);
            return new { check.Ok, check.Diagnostics, check.Compiled, Parameters = check.Parameters.Select(p => new { p.Name, p.Type, p.Default, p.Min, p.Max }) };
        }

        [OmniTool("formula_check", "Check a formula (indicator language) and evaluate it on a symbol's latest bars.", Group = "studio")]
        public async Task<object> FormulaCheckAsync(string formula, string symbol = "BTCUSDT", string interval = "OneDay")
        {
            var (compiled, errors) = FormulaCompiler.Check(formula);
            if (compiled == null) return new { Ok = false, Errors = errors };
            var iv = CandleClock.Parse(interval) ?? TimeInterval.OneDay;
            var candles = await trader.MarketData.GetHistoricalCandlesAsync(symbol, iv, Math.Max(300, compiled.Lookback + 50));
            var values = compiled.Evaluate(new FormulaContext { Bars = BarSeries.From(candles), External = s => ExternalBars(s, iv) });
            return new
            {
                Ok = true, compiled.IsBoolean, compiled.Lookback, Bars = candles.Count,
                Last = values.Skip(Math.Max(0, values.Length - 10)).Select((v, i) => new { T = candles[Math.Max(0, candles.Count - 10) + i].Timestamp, V = TA.IsNum(v) ? Math.Round(v, 6) : (double?)null })
            };
        }

        private BarSeries? ExternalBars(string symbol, TimeInterval interval)
        {
            var bars = trader.MarketData.GetHistoricalCandlesAsync(symbol, interval, 600).GetAwaiter().GetResult();
            return bars.Count == 0 ? null : BarSeries.From(bars);
        }

        private static StudioDraft Draft(string kind, string definition)
        {
            if (string.Equals(kind, "script", StringComparison.OrdinalIgnoreCase))
                return new StudioDraft { Kind = StudioKind.Script, Source = definition };
            Blueprint? bp;
            try { bp = JsonConvert.DeserializeObject<Blueprint>(definition, Persistence.DocumentStore<StudioStrategy>.Json); }
            catch (Exception ex) { throw new ArgumentException("Blueprint JSON did not parse: " + ex.Message); }
            return new StudioDraft { Kind = StudioKind.Blueprint, Blueprint = bp ?? new Blueprint() };
        }

        private static object Summary(StudioStrategy s) => new
        {
            s.Id, s.Name, Kind = s.Kind.ToString(), Status = s.Status.ToString(), s.Version,
            Errors = s.Diagnostics.Where(d => d.Severity == "error").Select(d => new { d.Message, d.Line, d.Column, d.Path }),
            Warnings = s.Diagnostics.Where(d => d.Severity != "error").Select(d => d.Message),
            s.Compiled
        };

        // ── lab ──────────────────────────────────────────────────────────────────

        [OmniTool("strategies", "Every runnable strategy: built-in classes and studio strategies, with parameters.", Group = "lab")]
        public object Strategies() => trader.StrategyRegistry.All.OrderBy(d => d.Source).ThenBy(d => d.Name).Select(d => new
        {
            d.ClassName, d.Name, d.Source, d.StudioId, d.RequiresUniverse,
            Parameters = d.Parameters.Select(p => new { p.Name, p.Type, p.Default, p.Min, p.Max })
        });

        [OmniTool("quick_backtest", "Run a studio strategy inline (≤5,000 bars, ≤90 s) and return the headline metrics and the last trades. Next-bar execution, Binance-like costs unless told otherwise.", Group = "lab", Mutating = true)]
        public async Task<object> QuickBacktestAsync(
            [OmniParam("Studio strategy id (st_...).")] string strategyId,
            string? symbol = null,
            [OmniParam("Interval.", Values = new[] { "FifteenMinute", "OneHour", "FourHour", "OneDay", "OneWeek" })] string? interval = null,
            int bars = 1500,
            [OmniParam("JSON object of parameter overrides, e.g. {\"fast\":10}.")] string? parameters = null,
            double fee = 0.001, double slippage = 0.0005, double leverage = 1)
        {
            var s = await Studio.GetAsync(strategyId) ?? throw new ArgumentException($"No strategy {strategyId}.");
            var (check, result, error) = await Studio.QuickTestAsync(
                new StudioDraft { Kind = s.Kind, Blueprint = s.Blueprint, Source = s.Source, DefaultSymbol = s.DefaultSymbol, DefaultInterval = s.DefaultInterval, Name = s.Name },
                new StudioRunRequest
                {
                    Symbol = symbol, Interval = CandleClock.Parse(interval), Bars = bars, Parameters = ParseParams(parameters),
                    FeeFraction = (decimal)fee, SlippageFraction = (decimal)slippage, Leverage = (decimal)leverage
                });
            if (result == null) return new { Ok = false, Error = error, check.Diagnostics };
            return BacktestSummary(result);
        }

        [OmniTool("backtest", "Queue a full backtest job of any strategy (class name or studio id). Returns the job id; read it with op=backtest_get.", Group = "lab", Mutating = true)]
        public async Task<string> BacktestAsync(
            [OmniParam("Studio id (st_...) or strategy class name.")] string strategy,
            string symbol,
            string interval = "OneDay",
            int bars = 2000,
            [OmniParam("Optional ISO start date; overrides bars.")] string? from = null,
            string? to = null,
            string? parameters = null,
            double fee = 0.001, double slippage = 0.0005, double leverage = 1,
            [OmniParam("Run cost sensitivity + walk-forward validation too.")] bool validate = false)
        {
            DateTime? f = DateTime.TryParse(from, out var fd) ? DateTime.SpecifyKind(fd, DateTimeKind.Utc) : null;
            DateTime? t = DateTime.TryParse(to, out var td) ? DateTime.SpecifyKind(td, DateTimeKind.Utc) : null;
            if (strategy.StartsWith("st_", StringComparison.OrdinalIgnoreCase))
            {
                string id = await Studio.QueueBacktestAsync(strategy, new StudioRunRequest
                {
                    Symbol = symbol, Interval = CandleClock.Parse(interval), Bars = bars, FromUtc = f, ToUtc = t, Parameters = ParseParams(parameters),
                    FeeFraction = (decimal)fee, SlippageFraction = (decimal)slippage, Leverage = (decimal)leverage, Validate = validate
                }, "kliveagent");
                return $"Job {id} queued.";
            }
            var pars = ParseParams(parameters) ?? new();
            pars["TradeSymbol"] = symbol.ToUpperInvariant();
            string job = await trader.BacktestQueue.EnqueueAsync(new BacktestConfig
            {
                StrategyClass = strategy, Coin = symbol.ToUpperInvariant(), Currency = "", Interval = CandleClock.Parse(interval) ?? TimeInterval.OneDay,
                CandleCount = Math.Clamp(bars, 50, 50_000), FromUtc = f, ToUtc = f.HasValue ? (t ?? DateTime.UtcNow) : null,
                FeeFraction = (decimal)fee, SlippageFraction = (decimal)slippage, Margin = new MarginSettings { Leverage = (decimal)Math.Clamp(leverage, 1, 10) },
                Execution = ExecutionTiming.NextOpen, Parameters = pars, Validation = validate ? new ValidationSettings() : null, Origin = "kliveagent"
            });
            return $"Job {job} queued.";
        }

        [OmniTool("backtest_get", "A backtest job's status and, when done, its summary metrics and last trades (no full arrays).", Group = "lab")]
        public async Task<object?> BacktestGetAsync(string jobId)
        {
            var job = await trader.BacktestJobRepo.GetAsync(jobId);
            if (job == null) return null;
            return new
            {
                job.Id, job.StrategyClass, Status = job.Status.ToString(), job.ProgressPct, job.Error,
                Summary = job.Result == null ? null : BacktestSummary(job.Result)
            };
        }

        [OmniTool("sweep", "Queue a parameter sweep: grid over the given ranges, ranked by an objective, with a deflated Sharpe for the winner. Spec JSON: [{\"name\":\"fast\",\"min\":5,\"max\":50,\"step\":5}] or explicit values.", Group = "lab", Mutating = true)]
        public async Task<string> SweepAsync(string strategy, string symbol, string spec, string interval = "OneDay", int bars = 2000,
            [OmniParam("Objective.", Values = new[] { "sharpe", "sortino", "return", "cagr", "calmar", "profitfactor" })] string objective = "sharpe",
            [OmniParam("sweep | walkforward", Values = new[] { "sweep", "walkforward" })] string kind = "sweep")
        {
            var parameters = JsonConvert.DeserializeObject<List<SweepParam>>(spec) ?? new();
            string className = strategy;
            if (strategy.StartsWith("st_", StringComparison.OrdinalIgnoreCase))
            {
                var s = await Studio.GetAsync(strategy) ?? throw new ArgumentException($"No strategy {strategy}.");
                className = $"{s.ClassName}@v{s.Version}";
            }
            var record = await trader.Studies.EnqueueAsync(new StudyRequest
            {
                Kind = kind == "walkforward" ? StudyKind.WalkForward : StudyKind.Sweep,
                Base = new BacktestConfig
                {
                    StrategyClass = className, Coin = symbol.ToUpperInvariant(), Currency = "", Interval = CandleClock.Parse(interval) ?? TimeInterval.OneDay,
                    CandleCount = Math.Clamp(bars, 100, 50_000), Execution = ExecutionTiming.NextOpen,
                    Parameters = new() { ["TradeSymbol"] = symbol.ToUpperInvariant() }
                },
                Params = parameters, Objective = objective, Label = $"agent {kind} {strategy} {symbol}"
            }, "kliveagent");
            return $"Study {record.Id} queued ({record.Kind}).";
        }

        [OmniTool("study_get", "A study's status and results (best run, top runs, heatmap, deflated Sharpe, walk-forward folds).", Group = "lab")]
        public async Task<object?> StudyGetAsync(string id)
        {
            var s = await trader.Studies.GetAsync(id);
            if (s == null) return null;
            return new
            {
                s.Id, Kind = s.Kind.ToString(), Status = s.Status.ToString(), s.Progress, s.Error,
                Best = s.Result?.Best, Top = s.Result?.Runs.Take(10), s.Result?.DeflatedSharpe, s.Result?.Sensitivity,
                Folds = s.Result?.Folds, s.Result?.OutOfSample, s.Result?.WalkForwardEfficiency, s.Result?.MonteCarlo, s.Result?.Batch
            };
        }

        private static Dictionary<string, object?>? ParseParams(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var obj = JObject.Parse(json);
            return obj.Properties().ToDictionary(p => p.Name, p => (object?)(p.Value is JValue v ? v.Value : p.Value.ToString()), StringComparer.OrdinalIgnoreCase);
        }

        public static object BacktestSummary(BacktestResult r)
        {
            var a = r.Analysis;
            return new
            {
                ReturnPct = Math.Round((double)r.TotalPnLPercent, 2),
                BenchmarkPct = Math.Round(a?.BenchmarkReturnPct ?? (double)r.BuyAndHoldPnLPercent, 2),
                CagrPct = a?.CagrPct, Sharpe = a?.Sharpe ?? (double)r.SharpeRatio, a?.Sortino, MaxDrawdownPct = Math.Round((double)r.MaxDrawdownPercent, 2),
                a?.Calmar, r.TotalTrades, WinRatePct = Math.Round((double)r.WinRate, 1), ProfitFactor = a?.ProfitFactor, a?.ExposurePct,
                a?.Alpha, a?.Beta, Bars = r.TotalCandles, From = r.StartTime, To = r.EndTime, r.StrategyErrors, r.FirstStrategyError,
                LastTrades = r.Trades.TakeLast(8).Select(t => new { t.Symbol, t.EntryTime, t.ExitTime, t.EntryPrice, t.ExitPrice, PnL = Math.Round(t.RealizedPnL, 2), t.ExitReason, t.IsShort })
            };
        }

        // ── data ─────────────────────────────────────────────────────────────────

        [OmniTool("quote", "Live quotes for comma-separated symbols (crypto pairs like BTCUSDT, stocks, ETFs, ^indices, FX like EURUSD=X, futures like GC=F).", Group = "data")]
        public async Task<object> QuoteAsync(string symbols)
            => await trader.Data.GetQuotesAsync(symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        [OmniTool("candles", "Recent closed OHLCV bars for any symbol, newest last, as [time, open, high, low, close, volume].", Group = "data")]
        public async Task<object> CandlesAsync(string symbol, string interval = "OneDay", int count = 60)
        {
            var bars = await trader.MarketData.GetHistoricalCandlesAsync(symbol, CandleClock.Parse(interval) ?? TimeInterval.OneDay, Math.Clamp(count, 1, 500));
            return bars.Select(b => new object[] { b.Timestamp.ToString("yyyy-MM-ddTHH:mm"), b.Open, b.High, b.Low, b.Close, b.Volume });
        }

        [OmniTool("screen", "Run a screen over a universe with formula filters (AND-combined, one per line or ';'-separated). Universes: sp500, ftse100, megacaps, sectors, factors, crypto-top50, crypto-top100, crypto-mcap100, world-indices, fx, commodities.", Group = "data")]
        public async Task<object> ScreenAsync(string universe, string filters, string? sort = null, int limit = 25, string interval = "OneDay")
        {
            var def = new ScreenDefinition
            {
                Universe = universe, Interval = CandleClock.Parse(interval) ?? TimeInterval.OneDay, Limit = Math.Clamp(limit, 1, 100),
                Filters = filters.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                SortBy = sort
            };
            var errors = ScreenerEngine.Validate(def);
            if (errors.Count > 0) return new { Ok = false, Errors = errors };
            var result = await trader.Screener.RunAsync(def);
            return new
            {
                result.Universe, result.Passed, result.ElapsedMs,
                Rows = result.Rows.Select(r => new { r.Symbol, r.Name, r.Sector, Values = r.Values.Where(v => v.Value.HasValue).ToDictionary(v => v.Key, v => Math.Round(v.Value!.Value, 4)) })
            };
        }

        [OmniTool("company", "Company profile and key statistics for an equity (sector, valuation, margins, growth, ownership, analyst targets, next earnings).", Group = "data")]
        public async Task<object?> CompanyAsync(string symbol) => await trader.Data.GetCompanyAsync(symbol);

        [OmniTool("derivatives", "Crypto perpetual data: funding (current, history, cross-venue), open interest, long/short ratios, taker flow.", Group = "data")]
        public async Task<object?> DerivativesAsync(string symbol, string period = "1h", int limit = 48)
        {
            var d = await trader.Data.GetDerivativesAsync(symbol, period, Math.Clamp(limit, 10, 200));
            if (d == null) return null;
            return new
            {
                d.Symbol, d.Current, d.Venues,
                Funding = d.Funding.TakeLast(12), OpenInterest = d.OpenInterestValue.TakeLast(12),
                LongShort = d.LongShortAccounts.TakeLast(12), TopTraders = d.TopTraderLongShort.TakeLast(12), Taker = d.TakerBuySell.TakeLast(12)
            };
        }

        [OmniTool("macro", "Macro dashboard: yield curve, CPI, unemployment, payrolls, policy rates, GDP, VIX term structure.", Group = "data")]
        public async Task<object> MacroAsync()
        {
            var series = await trader.Data.GetMacroAsync() ?? new();
            var curve = await trader.Data.GetLatestCurveAsync();
            var vol = await trader.Data.GetVolTermAsync();
            return new
            {
                Series = series.Select(s => new { s.Info.Id, s.Info.Name, s.Latest, s.Previous, Date = s.LatestDate?.ToString("yyyy-MM-dd"), s.Error }),
                Curve = curve == null ? null : new { curve.Date, curve.Points, curve.Spread2s10s, curve.Spread3m10y },
                Vol = vol?.Latest
            };
        }

        [OmniTool("alert_set", "Watch a true/false formula on a symbol's closed bars and message Klives on Discord when it fires, e.g. condition=\"crossover(close, sma(close, 200))\".", Group = "data", Mutating = true)]
        public async Task<object> AlertSetAsync(string symbol, string condition,
            [OmniParam("Bar interval.", Values = new[] { "FiveMinute", "FifteenMinute", "OneHour", "FourHour", "OneDay", "OneWeek" })] string interval = "OneHour",
            [OmniParam("once = fire then disable; onchange = each time it turns true; everybar = every true bar.", Values = new[] { "once", "onchange", "everybar" })] string mode = "onchange",
            string? name = null, string? message = null, [OmniParam("Existing alert id to replace.")] string? id = null)
        {
            var alert = id != null ? await trader.MarketAlerts.GetAsync(id) ?? new Screening.MarketAlert { Id = id } : new Screening.MarketAlert();
            alert.Symbol = symbol;
            alert.Condition = condition;
            alert.Interval = CandleClock.Parse(interval) ?? TimeInterval.OneHour;
            alert.Mode = mode.ToLowerInvariant() switch { "once" => Screening.AlertMode.Once, "everybar" => Screening.AlertMode.EveryBar, _ => Screening.AlertMode.OnChange };
            alert.Name = name ?? "";
            alert.Message = message;
            alert.Enabled = true;
            alert.CreatedBy = Author;
            var saved = await trader.MarketAlerts.SaveAsync(alert);
            var test = await trader.MarketAlerts.TestAsync(saved);
            return new { saved.Id, saved.Name, Interval = saved.Interval.ToString(), Mode = saved.Mode.ToString(), TrueNow = test.Value, TrueBarsInHistory = test.TrueBars, test.Bars };
        }

        [OmniTool("alerts", "Market alerts and their recent triggers.", Group = "data")]
        public async Task<object> AlertsAsync(string? symbol = null)
        {
            var list = await trader.MarketAlerts.ListAsync(symbol?.ToUpperInvariant());
            var events = await trader.MarketAlerts.EventsAsync(null, 20);
            return new
            {
                Alerts = list.Select(a => new { a.Id, a.Name, a.Symbol, Interval = a.Interval.ToString(), a.Condition, Mode = a.Mode.ToString(), a.Enabled, a.LastValue, a.LastTriggeredUtc, a.TriggerCount, a.LastError }),
                Recent = events.Select(e => new { e.AlertName, e.Symbol, e.BarUtc, e.Close })
            };
        }

        [OmniTool("news", "Latest headlines: markets, crypto, macro desks, a symbol's own feed, or a search query.", Group = "data")]
        public async Task<object> NewsAsync(string? symbol = null, string? query = null,
            [OmniParam("Desk.", Values = new[] { "markets", "crypto", "macro", "regulatory", "all" })] string? category = null, int limit = 15)
        {
            var items = await trader.Data.GetNewsAsync(category, symbol, query, Math.Clamp(limit, 1, 50)) ?? new();
            return items.Select(i => new { i.Title, i.Source, i.PublishedUtc, i.Url });
        }
    }

    /// <summary>The reference text the agent reads before writing a strategy.</summary>
    public static class StudioReference
    {
        public static string Text => text.Value;
        private static readonly Lazy<string> text = new(Build);

        private static string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# OmniTrader strategy reference");
            sb.AppendLine();
            sb.AppendLine("## Blueprint (kind=blueprint) — JSON");
            sb.AppendLine("Fields: inputs[] {name,label,type:int|double|bool,default,min,max,step} · universe {mode:single|basket|universe, symbol, symbols[], topN, quoteAsset, regimeSymbol} · direction long|short|both · rules {entryLong, exitLong, entryShort, exitShort, filter} (each a ConditionGroup) · sizing {mode:percent|quote|risk|voltarget, value, maxPositions, maxPositionPct} · risk {stopLoss, takeProfit, trailing: {mode:none|percent|atr|r, value, atrLength}, maxBarsInTrade, cooldownBars, exitOnFilterOff} · ranking {score (formula), top, bottom, rebalanceBars, weighting:equal|inverse-vol|score, grossExposure, minDollarVolume}.");
            sb.AppendLine("ConditionGroup {logic:all|any, negate, conditions[]}. Condition {kind:compare|group|formula, left, op, right, right2, bars, group, formula}. ops: > >= < <= == != crosses_above crosses_below rising falling between outside. bars>1 on a comparison = held for N bars.");
            sb.AppendLine("Operand {kind:indicator|price|value|input|formula, indicator, params{input:value or input name}, output, field, value, input, formula, offset (bars ago ≥0), scale}.");
            sb.AppendLine("Sizing: percent = % of equity per position; risk = % of equity lost at the stop (needs stopLoss); voltarget = target annual vol % per position. filter in basket/universe mode is evaluated on regimeSymbol. Shorts only execute with leverage > 1.");
            sb.AppendLine("Example single-symbol: {\"inputs\":[{\"name\":\"fast\",\"type\":\"int\",\"default\":20,\"min\":5,\"max\":100}],\"universe\":{\"mode\":\"single\",\"symbol\":\"BTCUSDT\"},\"rules\":{\"entryLong\":{\"logic\":\"all\",\"conditions\":[{\"kind\":\"compare\",\"left\":{\"kind\":\"indicator\",\"indicator\":\"ema\",\"params\":{\"length\":\"fast\"}},\"op\":\"crosses_above\",\"right\":{\"kind\":\"indicator\",\"indicator\":\"ema\",\"params\":{\"length\":50}}}]},\"exitLong\":{\"logic\":\"all\",\"conditions\":[{\"kind\":\"formula\",\"formula\":\"crossunder(ema(close, fast), ema(close, 50))\"}]}},\"sizing\":{\"mode\":\"percent\",\"value\":95},\"risk\":{\"stopLoss\":{\"mode\":\"atr\",\"value\":3,\"atrLength\":14}}}");
            sb.AppendLine("Example rotation: {\"universe\":{\"mode\":\"universe\",\"topN\":30,\"regimeSymbol\":\"BTCUSDT\"},\"rules\":{\"filter\":{\"conditions\":[{\"kind\":\"formula\",\"formula\":\"close > sma(close, 100)\"}]}},\"ranking\":{\"score\":\"roc(close, 30)\",\"top\":5,\"rebalanceBars\":7,\"weighting\":\"inverse-vol\",\"grossExposure\":0.95}}");
            sb.AppendLine();
            sb.AppendLine("## Script (kind=script) — C#");
            sb.AppendLine("Derive from ScriptStrategy (one symbol: override void OnBar()) or ScriptUniverseStrategy (many: override void OnBar(UniverseView u), call SetWeights(dict of signed weights)). Optional [TradingStrategy(\"Name\",\"Description\")] on the class; [Param(\"Label\", Min=, Max=, Step=)] on public settable properties makes them inputs. Usings for System, Linq, Collections, the trading API are implicit.");
            sb.AppendLine("ScriptStrategy: Close/Open/High/Low/Volume, C(ago)/O/H/L/V, Time, CurrentBar; indicators Sma/Ema/Wma/Hma(n,ago), Rsi, Atr, Natr, Adx, Cci, Mfi, WilliamsR, Roc, StdDev, ZScore, PercentRank, Highest, Lowest, Vwap, Obv, HistVol, Ibs, Macd(f,s,sig)→(Line,Signal,Hist), Bollinger(n,k)→(Upper,Mid,Lower), Keltner, Donchian, Stoch→(K,D), Supertrend→(Line,Direction), F(\"any formula\", ago), CrossAbove(\"a\",\"b\"), CrossBelow; position PositionSize, IsLong, IsShort, IsFlat, EntryPrice, BarsInTrade, OpenProfitPct, Cash, AccountEquity, MaxLeverage, HasPendingOrders; orders Buy(qty, sl, tp), Sell, BuyPercent(pct, sl, tp), ShortPercent, SetTargetPercent(signed pct), ExitLong(), ExitShort(), Exit(), LimitBuy/LimitSell(qty, price), StopBuy/StopSell(qty, stop), SetStopLoss(price), SetTakeProfit(price); Plot(name, value); Print(msg); override Initialize() and OnFill(FillEvent).");
            sb.AppendLine("ScriptUniverseStrategy: properties UniverseSize, RegimeSymbol, BarNumber; override Symbols for a fixed basket; UniverseView u: u.Symbols, u.Time, u.Close(sym, ago), u.Volume(sym, ago), u.F(sym, formula, ago), u.Bars(sym); Holdings, AccountEquity, MaxLeverage, Plot.");
            sb.AppendLine("Sandbox: no I/O, network, reflection, threads/Task.Run, processes, Environment, Console, DateTime.Now (use Time), unsafe/stackalloc/goto/finalizers, nothing in Omnipotent outside the trading API. Loops and recursion are budgeted per callback. Orders queued in OnBar submit after it returns; market orders fill next bar's open in evaluations.");
            sb.AppendLine();
            sb.AppendLine("## Formula language (blueprint formulas, ranking scores, F(), screens)");
            sb.AppendLine("Series: open high low close volume hl2 hlc3 ohlc4 hlcc4 bar_index time. Operators: + - * / % ^, > >= < <= == !=, and or not, cond ? a : b, x[n] (n bars ago, n ≥ 0), f(...).output. Booleans are 1/0; na during warm-up.");
            sb.AppendLine("Functions: " + string.Join("; ", FormulaBinder.Builtins.Select(b => b.Value.Signature)));
            sb.AppendLine("Indicators (source can be omitted: rsi(14) = rsi(close, 14); named args allowed: bb(close, 20, mult=2.5)):");
            foreach (var spec in IndicatorCatalog.All)
            {
                string args = string.Join(", ", spec.Inputs.Select(i => i.Kind == InputKind.Source ? "src" : $"{i.Name}={i.Default}"));
                string outs = spec.Outputs.Count > 1 ? " → ." + string.Join(" .", spec.Outputs.Select(o => o.Name)) : "";
                sb.AppendLine($"- {spec.Id}({args}){outs} — {spec.Name}");
            }
            return sb.ToString();
        }
    }
}
