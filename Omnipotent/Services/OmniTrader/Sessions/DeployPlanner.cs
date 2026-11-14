using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>What the person asked for; anything left null is filled from the evidence.</summary>
    public sealed class DeployPlanRequest
    {
        public string? CandidateId { get; init; }
        public string? StrategyClass { get; init; }
        public string? Mode { get; init; }
        public string? Symbol { get; init; }
        public string? Interval { get; init; }
        public decimal? Capital { get; init; }
    }

    /// <summary>Everything the planner reads, fetched by the route so the planner itself stays pure.</summary>
    public sealed class DeployPlanInputs
    {
        /// <summary>Resolved strategy, pinned (studio:id@vN) when it comes from a candidate.</summary>
        public required StrategyDescriptor Descriptor { get; init; }
        /// <summary>A fresh instance, used only to read what it declares it trades.</summary>
        public required Func<IDictionary<string, object?>, TradingStrategy> Create { get; init; }
        public StudioStrategy? Studio { get; init; }
        public AlphaCandidate? Candidate { get; init; }
        public int EvaluationBars { get; init; } = 1500;
        public IReadOnlyList<DeploymentRow> Existing { get; init; } = Array.Empty<DeploymentRow>();
        public bool KrakenConfigured { get; init; }
        public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class DeployCheck
    {
        public required string Id { get; init; }
        /// <summary>pass | info | warn | block</summary>
        public required string Level { get; init; }
        public required string Title { get; init; }
        public string? Detail { get; init; }
    }

    public sealed class DeploySymbolChoice
    {
        public required string Symbol { get; init; }
        public bool Primary { get; init; }
        public bool Tested { get; init; }
        public double? Sharpe { get; init; }
        public double? ReturnPct { get; init; }
        public double? MaxDrawdownPct { get; init; }
        public int? Trades { get; init; }
    }

    public sealed class DeployModeChoice
    {
        public required string Mode { get; init; }
        public bool Allowed { get; init; }
        public string? Reason { get; init; }
    }

    public sealed class DeployCostChoice
    {
        public required string Preset { get; init; }
        public required string Name { get; init; }
        public decimal Fee { get; init; }
        public decimal Slippage { get; init; }
        public string? Note { get; init; }
        public string? Why { get; init; }
        public string? EvaluatedPreset { get; init; }
        public decimal? EvaluatedFee { get; init; }
        public decimal? EvaluatedSlippage { get; init; }
    }

    public sealed class DeployExpectation
    {
        public double? BarsPerTrade { get; init; }
        public double? TradesPerMonth { get; init; }
        public int? WarmupBars { get; init; }
        public int HistoryBars { get; init; }
        public double? ExposurePct { get; init; }
        public double? MaxDrawdownPct { get; init; }
        public double? CagrPct { get; init; }
        public List<string> Lines { get; init; } = new();
    }

    /// <summary>The live caps a Live deployment gets (RiskGate). Shown, editable, sent with the request.</summary>
    public sealed class DeployRisk
    {
        public decimal MaxPositionQuoteUsd { get; init; }
        public decimal MaxDailyLossUsd { get; init; }
        public int MaxOrdersPerHour { get; init; }
        public List<string> AllowedSymbols { get; init; } = new();
    }

    /// <summary>A body for POST /deployment/create, exactly as the plan proposes it.</summary>
    public sealed class DeployRequestBody
    {
        public required string StrategyClass { get; init; }
        public required string Symbol { get; init; }
        public required string Interval { get; init; }
        public required string Mode { get; init; }
        public decimal InitialQuoteBalance { get; init; }
        public decimal FeeFraction { get; init; }
        public decimal SlippageFraction { get; init; }
        public decimal Leverage { get; init; } = 1m;
        public decimal? MaxPositionQuoteUsd { get; init; }
        public decimal? MaxDailyLossUsd { get; init; }
        public int? MaxOrdersPerHour { get; init; }
        public List<string>? AllowedSymbols { get; init; }
        public Dictionary<string, object?> Parameters { get; init; } = new();
        public string? Execution { get; init; }
        public int? HistoryBars { get; init; }
        public required DeploymentOrigin Origin { get; init; }
    }

    public sealed class DeployPlan
    {
        public required string StrategyClass { get; init; }
        public required string StrategyName { get; init; }
        public string? Description { get; init; }
        /// <summary>alpha | studio | builtin</summary>
        public required string Source { get; init; }
        /// <summary>builtin | blueprint | script</summary>
        public required string Kind { get; init; }
        /// <summary>single | basket | universe</summary>
        public required string Shape { get; init; }
        public List<string> Basket { get; init; } = new();
        public int? StrategyVersion { get; init; }
        public int? CurrentVersion { get; init; }
        public object? Candidate { get; init; }
        public DeploymentEvidence? Evaluated { get; init; }
        public required string Symbol { get; init; }
        public required string AssetClass { get; init; }
        public required string Feed { get; init; }
        public List<DeploySymbolChoice> SymbolChoices { get; init; } = new();
        public required string Interval { get; init; }
        public string? EvaluatedInterval { get; init; }
        public List<string> IntervalChoices { get; init; } = new();
        public required string Mode { get; init; }
        public List<DeployModeChoice> Modes { get; init; } = new();
        public decimal Capital { get; init; }
        public List<decimal> CapitalChoices { get; init; } = new();
        public required DeployCostChoice Costs { get; init; }
        public required string Execution { get; init; }
        public DeployRisk? Risk { get; init; }
        public List<ParamDescriptor> ParameterSpecs { get; init; } = new();
        public required DeployExpectation Expect { get; init; }
        public List<DeployCheck> Checks { get; init; } = new();
        /// <summary>ready | caution | blocked</summary>
        public required string Verdict { get; init; }
        public required string VerdictTitle { get; init; }
        public required string Label { get; init; }
        public required DeployRequestBody Request { get; init; }
    }

    /// <summary>
    /// Builds a fully filled-in deployment from the evidence a strategy carries: the Alpha Lab's evaluation
    /// when it came from a candidate (interval, tested symbols, costs, execution, the exact version), else
    /// the Studio strategy's defaults, else the engine defaults; then checks it. The Deploy page shows the
    /// plan and posts <see cref="DeployPlan.Request"/>. Before it, the page offered one 210-item dropdown and
    /// BTCUSDT / 1h / Binance costs for every strategy, though 156 of 206 Studio strategies are daily.
    /// </summary>
    public static class DeployPlanner
    {
        public static readonly string[] IntervalChoices = { "FifteenMinute", "OneHour", "FourHour", "OneDay" };
        public static readonly decimal[] CapitalChoices = { 1_000m, 10_000m, 50_000m, 100_000m };
        // Live caps scale with the capital (Klives, 13 Nov 2026): one order may be as large as the capital,
        // the day may lose 5 % of it. The fixed 100 / 50 the DTO defaults to refused every full-size order
        // of any deployment with more than 100 behind it.
        public const decimal LiveDailyLossFraction = 0.05m;
        public const int LiveMaxOrdersPerHour = 30;
        public static decimal LiveMaxOrderFor(decimal capital) => Math.Round(capital, 2);
        public static decimal LiveDailyLossFor(decimal capital) => Math.Max(1m, Math.Round(capital * LiveDailyLossFraction, 2));

        private static readonly Regex LookbackName = new(
            "lookback|length|period|window|slow|long|bars|days|span|lag|formation|history|horizon|crash|trend|ma$|sma|ema|momentum|mom",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> HiddenParams = new(StringComparer.OrdinalIgnoreCase) { "TradeSymbol", "UniverseSize", "RegimeSymbol" };

        public static DeployPlan Build(DeployPlanRequest req, DeployPlanInputs inp)
        {
            var d = inp.Descriptor;
            var cand = inp.Candidate;
            var eval = cand?.Evaluation;
            var studio = inp.Studio;
            string source = cand != null ? "alpha" : studio != null || d.Source != "builtin" ? "studio" : "builtin";

            // ── interval ─────────────────────────────────────────────────────────
            TimeInterval? evaluatedInterval = cand?.Interval ?? studio?.DefaultInterval;
            TimeInterval interval = ParseInterval(req.Interval) ?? evaluatedInterval ?? TimeInterval.OneHour;

            // ── what it trades ───────────────────────────────────────────────────
            var defaults = DefaultParameters(d);
            var probe = inp.Create(new Dictionary<string, object?>(defaults, StringComparer.OrdinalIgnoreCase));
            var declared = probe.DeclareSymbols();
            var kind = SessionManager.KindOf(declared);
            string shape = SessionManager.ShapeName(kind);
            var basket = kind == SessionKind.Basket ? declared.Fixed!.ToList() : new List<string>();

            var tested = (cand?.TestSymbols ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList();
            string defaultSymbol = kind switch
            {
                SessionKind.Basket => declared.Primary,
                SessionKind.Universe => declared.Universe!.RegimeSymbol,
                _ => tested.FirstOrDefault() ?? studio?.DefaultSymbol ?? declared.Primary,
            };
            string symbol = kind == SessionKind.Single && !string.IsNullOrWhiteSpace(req.Symbol) ? req.Symbol.Trim().ToUpperInvariant() : defaultSymbol;
            var symbols = kind == SessionKind.Basket ? basket : new List<string> { symbol };
            string asset = AssetClassOf(symbols);
            bool crypto = asset == "crypto";

            // ── evidence ─────────────────────────────────────────────────────────
            var perSymbol = (eval?.PerSymbol ?? new List<StudyRun>())
                .Where(r => r.Error == null && r.Metrics != null && !string.IsNullOrEmpty(r.Symbol))
                .GroupBy(r => r.Symbol!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Metrics!, StringComparer.OrdinalIgnoreCase);
            RunMetrics? headline = eval?.Primary;
            if (kind == SessionKind.Single && headline != null && tested.Count > 0 && !symbol.Equals(tested[0], StringComparison.OrdinalIgnoreCase)
                && perSymbol.TryGetValue(symbol, out var other))
                headline = other;

            var evalCost = cand == null ? null
                : CostPresets.All.First(c => c.Id == (MarketDataRouter.UsesEquityFeed(tested.FirstOrDefault() ?? symbol, Venues.AssetClass.Unknown) ? "us-broker" : "binance"));
            DeploymentEvidence? evidence = null;
            if (headline != null)
            {
                evidence = new DeploymentEvidence
                {
                    Interval = cand!.Interval,
                    Symbols = tested,
                    Bars = inp.EvaluationBars,
                    ReturnPct = Round(headline.ReturnPct), CagrPct = Round(headline.CagrPct), Sharpe = Round(headline.Sharpe, 3),
                    MaxDrawdownPct = Round(headline.MaxDrawdownPct), Trades = headline.Trades, WinRatePct = Round(headline.WinRatePct),
                    ExposurePct = Round(headline.ExposurePct),
                    OosSharpe = eval?.OutOfSample?.Sharpe is double oos ? Round(oos, 3) : null,
                    StressedReturnPct = eval?.PrimaryStressed?.ReturnPct is double st ? Round(st) : null,
                    DeflatedSharpe = eval?.DeflatedSharpe,
                    CostPreset = evalCost?.Id, FeeFraction = evalCost?.Fee, SlippageFraction = evalCost?.Slippage,
                    EvaluatedUtc = eval?.FinishedUtc,
                };
            }
            else if (studio is { LastMetrics.Count: > 0 } && source == "studio")
            {
                var m = studio.LastMetrics;
                evidence = new DeploymentEvidence
                {
                    Interval = studio.DefaultInterval,
                    Symbols = new List<string> { studio.DefaultSymbol },
                    ReturnPct = Get(m, "ReturnPct"), Sharpe = Get(m, "Sharpe", 3), MaxDrawdownPct = Get(m, "MaxDrawdownPct"),
                    Trades = Get(m, "Trades") is double t ? (int)t : null, WinRatePct = Get(m, "WinRatePct"),
                };
            }

            // ── costs: where it would actually trade ─────────────────────────────
            var preset = crypto ? CostPresets.All.First(c => c.Id == "kraken")
                : asset is "fx" or "futures" or "index" ? CostPresets.All.First(c => c.Id == "ig-cfd")
                : CostPresets.All.First(c => c.Id == "us-broker");
            var costs = new DeployCostChoice
            {
                Preset = preset.Id, Name = preset.Name, Fee = preset.Fee, Slippage = preset.Slippage, Note = preset.Note,
                Why = crypto ? "Live crypto orders go to Kraken spot, so paper is charged Kraken's fees."
                    : asset == "equity" ? "Shares and ETFs at a zero-commission broker's spread."
                    : "Spread-only, as an IG CFD.",
                EvaluatedPreset = evalCost?.Id, EvaluatedFee = evalCost?.Fee, EvaluatedSlippage = evalCost?.Slippage,
            };

            // ── mode ─────────────────────────────────────────────────────────────
            string? liveBlock = !inp.KrakenConfigured ? "Kraken is not configured, so nothing can trade live."
                : !crypto ? $"Live strategy trading routes to Kraken spot only; {string.Join(", ", symbols.Where(s => MarketDataRouter.UsesEquityFeed(s, Venues.AssetClass.Unknown)).Take(3))} {(symbols.Count(s => MarketDataRouter.UsesEquityFeed(s, Venues.AssetClass.Unknown)) == 1 ? "is not" : "are not")} a Kraken pair. Trade it by ticket from Execution on IG or Trading 212."
                : kind == SessionKind.Universe ? "A live universe would have every order except its benchmark's refused by the symbol allow-list. Not offered until that is settled."
                : null;
            var modes = new List<DeployModeChoice>
            {
                new() { Mode = "Paper", Allowed = true },
                new() { Mode = "Live", Allowed = liveBlock == null, Reason = liveBlock },
            };
            string mode = string.Equals(req.Mode, "Live", StringComparison.OrdinalIgnoreCase) ? "Live" : "Paper";
            decimal capital = req.Capital is > 0 ? Math.Round(req.Capital.Value, 2) : mode == "Live" ? 500m : 10_000m;

            // ── expectations ─────────────────────────────────────────────────────
            int? warmup = WarmupFor(d, defaults);
            int defaultHistory = kind == SessionKind.Single ? PaperSession.PreloadCandles : MultiAssetSession.PreloadBars;
            int history = warmup is int w && w + 20 > defaultHistory ? Math.Min(3000, w + 50) : defaultHistory;
            double barsPerMonth = CandleClock.BarsPerYear(interval, crypto) / 12.0;
            double? barsPerTrade = evidence?.Trades is > 0 && evidence.Bars is > 0 ? Math.Round(evidence.Bars.Value / (double)evidence.Trades.Value, 1) : null;
            double? tradesPerMonth = barsPerTrade is > 0 ? Math.Round(barsPerMonth / barsPerTrade.Value, 1) : null;
            var lines = new List<string>();
            if (tradesPerMonth is double tpm)
                lines.Add(tpm >= 1 ? $"About {Fmt(tpm)} trade{(tpm >= 1.5 ? "s" : "")} a month (the backtest traded every {Fmt(barsPerTrade!.Value)} bars)."
                                   : $"About one trade every {Fmt(1 / tpm)} months (the backtest traded every {Fmt(barsPerTrade!.Value)} bars).");
            if (evidence?.ExposurePct is double ex) lines.Add($"In the market about {Fmt(ex)} % of the time.");
            if (evidence?.MaxDrawdownPct is double dd) lines.Add($"Its worst fall in testing was {Fmt(dd)} % from a peak; expect one like it.");
            if (warmup is int wu)
                lines.Add(wu <= history ? $"Needs about {wu} bars of history; {history} are loaded at start, so it can act on the first new bar."
                                        : $"Needs about {wu} bars of history; at most {history} load at start.");
            if (asset == "equity" && interval < TimeInterval.OneDay) lines.Add("Share prices only move in market hours: about 7 one-hour bars a trading day.");

            // ── checks ───────────────────────────────────────────────────────────
            var checks = new List<DeployCheck>();
            if (cand != null)
            {
                string score = cand.Score is double sc ? $" (score {sc:0})" : "";
                checks.Add(cand.Status switch
                {
                    CandidateStatus.Promoted or CandidateStatus.Promising => new DeployCheck { Id = "lab", Level = "pass", Title = $"The Alpha Lab rated it {cand.Status}{score}" },
                    CandidateStatus.Weak => new DeployCheck { Id = "lab", Level = "warn", Title = $"The Alpha Lab rated it Weak{score}", Detail = "Its evidence was not strong enough to recommend it." },
                    CandidateStatus.Rejected or CandidateStatus.Failed => new DeployCheck { Id = "lab", Level = "warn", Title = $"The Alpha Lab {(cand.Status == CandidateStatus.Failed ? "could not evaluate it" : "rejected it")}{score}", Detail = eval?.Findings.FirstOrDefault() },
                    _ => new DeployCheck { Id = "lab", Level = "warn", Title = "Not evaluated yet", Detail = "There is no backtest evidence for it yet." },
                });
                if (eval?.OutOfSample is { } oosRun)
                    checks.Add(oosRun.Sharpe > 0
                        ? new DeployCheck { Id = "oos", Level = "pass", Title = $"Held up out of sample (Sharpe {oosRun.Sharpe:0.00})" }
                        : new DeployCheck { Id = "oos", Level = "warn", Title = $"Did not hold up out of sample (Sharpe {oosRun.Sharpe:0.00})", Detail = "Walk-forward on data it was not tuned on lost money." });
                if (eval?.PrimaryStressed is { } stress)
                    checks.Add(stress.ReturnPct > 0
                        ? new DeployCheck { Id = "costs", Level = "pass", Title = $"Still profitable at twice the costs ({stress.ReturnPct:+0.0;-0.0} %)" }
                        : new DeployCheck { Id = "costs", Level = "warn", Title = $"Loses money at twice the costs ({stress.ReturnPct:+0.0;-0.0} %)", Detail = "It is sensitive to fees; small edges vanish in live trading." });
                if (evalCost != null && costs.Fee > evalCost.Fee * 1.5m)
                    checks.Add(new DeployCheck { Id = "fees", Level = "info", Title = $"Fees here are {costs.Fee * 100:0.##} % against {evalCost.Fee * 100:0.##} % in testing", Detail = $"{costs.Name}: {costs.Note}. Expect results a little below the backtest's." });
                if (evaluatedInterval is TimeInterval ei && ei != interval)
                    checks.Add(new DeployCheck { Id = "interval", Level = "warn", Title = $"Tested on {DeploymentHealth.Short(ei)} bars, set to {DeploymentHealth.Short(interval)}", Detail = "Its inputs count bars, so every lookback now covers a different stretch of time than it was tested on." });
                if (kind == SessionKind.Single && tested.Count > 0 && !tested.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                    checks.Add(new DeployCheck { Id = "symbol", Level = "warn", Title = $"Never tested on {symbol}", Detail = $"It was tested on {string.Join(", ", tested.Take(6))}." });
                if (studio != null && studio.Version != cand.StrategyVersion)
                    checks.Add(new DeployCheck { Id = "version", Level = "info", Title = $"Runs v{cand.StrategyVersion}, the version that was tested", Detail = $"The strategy is at v{studio.Version} now; that version has not been evaluated." });
                else if (cand.StrategyVersion > 0)
                    checks.Add(new DeployCheck { Id = "version", Level = "pass", Title = $"Pinned to v{cand.StrategyVersion} as tested", Detail = "Later edits in the Studio won't change what this deployment runs." });
            }
            else
            {
                // Nothing has tested it out of sample: worth knowing before money (even paper) rides on it.
                checks.Add(new DeployCheck
                {
                    Id = "lab", Level = "warn",
                    Title = source == "builtin" ? "A built-in strategy the Alpha Lab has not evaluated" : "Not evaluated by the Alpha Lab",
                    Detail = evidence != null ? $"Its last Studio backtest: {Fmt(evidence.ReturnPct)} % return, Sharpe {Fmt(evidence.Sharpe)}." : "Backtest it in the Lab first if you can.",
                });
                if (studio != null && studio.DefaultInterval != interval)
                    checks.Add(new DeployCheck { Id = "interval", Level = "warn", Title = $"Written for {DeploymentHealth.Short(studio.DefaultInterval)} bars, set to {DeploymentHealth.Short(interval)}" });
            }

            if (evidence?.MaxDrawdownPct is > 30)
                checks.Add(new DeployCheck { Id = "drawdown", Level = "warn", Title = $"Fell {evidence.MaxDrawdownPct:0} % from a peak in testing", Detail = "Only commit money you can watch fall that far." });
            if (evidence != null && (evidence.Trades ?? 0) < 10)
                checks.Add(new DeployCheck { Id = "trades", Level = "warn", Title = $"Only {evidence.Trades ?? 0} trades in testing", Detail = "Too few to tell skill from luck." });

            string baseClass = BaseClass(d.ClassName);
            var dup = inp.Existing.FirstOrDefault(e => e.Status is DeploymentStatus.Running or DeploymentStatus.Paused
                && BaseClass(e.StrategyClass).Equals(baseClass, StringComparison.OrdinalIgnoreCase)
                && e.Config.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && e.Config.Interval == interval);
            if (dup != null)
                checks.Add(new DeployCheck { Id = "duplicate", Level = "warn", Title = $"Already {(dup.Status == DeploymentStatus.Running ? "running" : "paused")} on {symbol} · {DeploymentHealth.Short(interval)}", Detail = $"Deployment {dup.Id[..8]}, since {dup.CreatedUtc:d MMM}." });

            if (warmup is int need && need > history)
                checks.Add(new DeployCheck { Id = "warmup", Level = "warn", Title = $"Needs about {need} bars of history; {history} can be loaded", Detail = $"Its first signal waits about {need - history} more {DeploymentHealth.Short(interval)} bars." });
            else if (warmup is int need2 && need2 > defaultHistory)
                checks.Add(new DeployCheck { Id = "warmup", Level = "pass", Title = $"Loads {history} bars of history at start", Detail = $"Its inputs look back about {need2} bars, more than the usual {defaultHistory}." });

            if (kind == SessionKind.Basket)
                checks.Add(new DeployCheck { Id = "basket", Level = "info", Title = $"Trades a fixed basket of {basket.Count} symbols", Detail = string.Join(" · ", basket) });
            if (kind == SessionKind.Universe)
                checks.Add(new DeployCheck { Id = "universe", Level = "info", Title = $"Trades the top {declared.Universe!.TopN} coins by volume", Detail = $"Re-picked every 6 hours; {declared.Universe.RegimeSymbol} sets its regime." });

            if (mode == "Live")
            {
                if (liveBlock != null) checks.Add(new DeployCheck { Id = "live", Level = "block", Title = "Can't run live", Detail = liveBlock });
                else checks.Add(new DeployCheck { Id = "live", Level = "info", Title = "Starts disarmed", Detail = $"Nothing reaches Kraken until you arm it. Caps: {LiveMaxOrderFor(capital):0.##} per order (the capital), {LiveDailyLossFor(capital):0.##} daily loss (5 %), {LiveMaxOrdersPerHour} orders an hour. Raise the per-order cap under Advanced as the account grows." });
            }
            if (mode == "Live")
                checks.Add(new DeployCheck { Id = "fills", Level = "info", Title = "Live orders go to Kraken as each bar closes", Detail = "The lab filled at the next bar's open; on a fast market the two can differ by a few ticks." });

            string verdict = checks.Any(c => c.Level == "block") ? "blocked" : checks.Any(c => c.Level == "warn") ? "caution" : "ready";
            string verdictTitle = verdict switch
            {
                "blocked" => checks.First(c => c.Level == "block").Title,
                "caution" => $"{checks.Count(c => c.Level == "warn")} thing{(checks.Count(c => c.Level == "warn") == 1 ? "" : "s")} to know before deploying",
                _ => cand != null ? "Ready to deploy as tested" : "Ready to deploy",
            };

            // ── request ──────────────────────────────────────────────────────────
            string name = cand?.StrategyName is { Length: > 0 } cn ? cn : studio?.Name ?? d.Name;
            string where = kind == SessionKind.Basket ? $"{basket.Count} symbols" : kind == SessionKind.Universe ? $"top {declared.Universe!.TopN}" : symbol;
            string label = $"{name} · {where} · {DeploymentHealth.Short(interval)}";
            var parameters = new Dictionary<string, object?>(defaults, StringComparer.OrdinalIgnoreCase);
            if (kind == SessionKind.Single && d.Parameters.Any(p => p.Name == "TradeSymbol")) parameters["TradeSymbol"] = symbol;
            // Paper fills at the next bar's open, as the lab tested (both paper sessions honour it). Live orders
            // go to Kraken when the bar closes, so a live plan says so instead.
            var execution = mode == "Live" ? ExecutionTiming.SignalClose : ExecutionTiming.NextOpen;
            var origin = new DeploymentOrigin
            {
                Source = source,
                CandidateId = cand?.Id,
                CandidateStatus = cand?.Status.ToString(),
                Score = cand?.Score,
                StrategyId = cand?.StrategyId ?? studio?.Id ?? d.StudioId,
                StrategyVersion = cand?.StrategyVersion is > 0 ? cand.StrategyVersion : studio?.Version ?? d.Version,
                StrategyName = name,
                Shape = shape,
                Label = label,
                Evaluated = evidence,
                BarsPerTrade = barsPerTrade,
                WarmupBars = warmup,
                AcceptedWarnings = checks.Where(c => c.Level == "warn").Select(c => c.Id).ToList(),
                PlannedUtc = inp.NowUtc,
            };
            var risk = mode == "Live"
                ? new DeployRisk { MaxPositionQuoteUsd = LiveMaxOrderFor(capital), MaxDailyLossUsd = LiveDailyLossFor(capital), MaxOrdersPerHour = LiveMaxOrdersPerHour, AllowedSymbols = symbols.ToList() }
                : null;
            var body = new DeployRequestBody
            {
                StrategyClass = d.ClassName,
                Symbol = symbol,
                Interval = interval.ToString(),
                Mode = mode,
                InitialQuoteBalance = capital,
                FeeFraction = costs.Fee,
                SlippageFraction = costs.Slippage,
                Leverage = 1m,
                MaxPositionQuoteUsd = risk?.MaxPositionQuoteUsd,
                MaxDailyLossUsd = risk?.MaxDailyLossUsd,
                MaxOrdersPerHour = risk?.MaxOrdersPerHour,
                AllowedSymbols = risk?.AllowedSymbols,
                Parameters = parameters,
                Execution = execution.ToString(),
                HistoryBars = history,
                Origin = origin,
            };

            var intervals = IntervalChoices.ToList();
            if (evaluatedInterval is TimeInterval evi && !intervals.Contains(evi.ToString())) intervals.Add(evi.ToString());

            return new DeployPlan
            {
                StrategyClass = d.ClassName,
                StrategyName = name,
                Description = string.IsNullOrWhiteSpace(d.Description) ? studio?.Description : d.Description,
                Source = source,
                Kind = d.Source,
                Shape = shape,
                Basket = basket,
                StrategyVersion = origin.StrategyVersion,
                CurrentVersion = studio?.Version,
                Candidate = cand == null ? null : new
                {
                    cand.Id, Status = cand.Status.ToString(), cand.Score, cand.Hypothesis, cand.PaperTitle,
                    Synthesis = cand.IsSynthesis(), Papers = cand.SourcePaperIds().Count, cand.StrategyId, cand.StrategyVersion,
                    Findings = eval?.Findings.Take(8).ToList() ?? new List<string>(),
                },
                Evaluated = evidence,
                Symbol = symbol,
                AssetClass = asset,
                Feed = crypto ? "Binance spot, streamed" : "Yahoo Finance, polled",
                SymbolChoices = SymbolChoices(kind, symbol, tested, perSymbol),
                Interval = interval.ToString(),
                EvaluatedInterval = evaluatedInterval?.ToString(),
                IntervalChoices = intervals,
                Mode = mode,
                Modes = modes,
                Capital = capital,
                CapitalChoices = CapitalChoices.ToList(),
                Costs = costs,
                Execution = execution.ToString(),
                Risk = risk,
                ParameterSpecs = d.Parameters.Where(p => !HiddenParams.Contains(p.Name)).ToList(),
                Expect = new DeployExpectation
                {
                    BarsPerTrade = barsPerTrade, TradesPerMonth = tradesPerMonth, WarmupBars = warmup, HistoryBars = history,
                    ExposurePct = evidence?.ExposurePct, MaxDrawdownPct = evidence?.MaxDrawdownPct, CagrPct = evidence?.CagrPct,
                    Lines = lines,
                },
                Checks = checks.OrderBy(c => c.Level switch { "block" => 0, "warn" => 1, "pass" => 2, _ => 3 }).ToList(),
                Verdict = verdict,
                VerdictTitle = verdictTitle,
                Label = label,
                Request = body,
            };
        }

        private static List<DeploySymbolChoice> SymbolChoices(SessionKind kind, string chosen, List<string> tested, Dictionary<string, RunMetrics> perSymbol)
        {
            if (kind != SessionKind.Single) return new List<DeploySymbolChoice>();
            var list = new List<DeploySymbolChoice>();
            foreach (var s in tested)
            {
                perSymbol.TryGetValue(s, out var m);
                list.Add(new DeploySymbolChoice
                {
                    Symbol = s, Primary = list.Count == 0, Tested = true,
                    Sharpe = m == null ? null : Round(m.Sharpe, 3), ReturnPct = m == null ? null : Round(m.ReturnPct),
                    MaxDrawdownPct = m == null ? null : Round(m.MaxDrawdownPct), Trades = m?.Trades,
                });
            }
            if (!list.Any(c => c.Symbol.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
                list.Add(new DeploySymbolChoice { Symbol = chosen, Primary = list.Count == 0, Tested = false });
            return list;
        }

        /// <summary>The strategy's declared defaults, as the lab ran it (it never tunes before scoring).</summary>
        public static Dictionary<string, object?> DefaultParameters(StrategyDescriptor d)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in d.Parameters) map[p.Name] = p.Default;
            return map;
        }

        /// <summary>
        /// The longest lookback the inputs imply: the largest whole-number default among inputs named like a
        /// lookback, plus two. A heuristic, labelled as one; null when no input looks like a lookback.
        /// </summary>
        public static int? WarmupFor(StrategyDescriptor d, IReadOnlyDictionary<string, object?> values)
        {
            int best = 0;
            foreach (var p in d.Parameters)
            {
                if (p.Type is not ("int" or "double" or "decimal")) continue;
                if (!LookbackName.IsMatch(p.Name) && !LookbackName.IsMatch(p.Label)) continue;
                if (!values.TryGetValue(p.Name, out var raw) || raw == null) continue;
                double v;
                try { v = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture); } catch { continue; }
                if (p.Type != "int" && (v != Math.Floor(v) || v < 5)) continue;
                if (v > best && v <= 5000) best = (int)v;
            }
            return best >= 5 ? best + 2 : null;
        }

        public static string AssetClassOf(IReadOnlyList<string> symbols)
        {
            var kinds = symbols.Select(AssetClassOf).GroupBy(k => k).OrderByDescending(g => g.Count()).ToList();
            return kinds.Count == 0 ? "crypto" : kinds.Count > 1 && kinds.Any(k => k.Key == "crypto") && kinds.Any(k => k.Key != "crypto") ? "mixed" : kinds[0].Key;
        }

        public static string AssetClassOf(string symbol)
        {
            if (!MarketDataRouter.UsesEquityFeed(symbol, Venues.AssetClass.Unknown)) return "crypto";
            if (symbol.EndsWith("=X", StringComparison.OrdinalIgnoreCase)) return "fx";
            if (symbol.EndsWith("=F", StringComparison.OrdinalIgnoreCase)) return "futures";
            if (symbol.StartsWith('^')) return "index";
            return "equity";
        }

        public static string BaseClass(string className)
        {
            int at = className.IndexOf('@');
            return at < 0 ? className : className[..at];
        }

        public static TimeInterval? ParseInterval(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (Enum.TryParse<TimeInterval>(raw, true, out var i) && Enum.IsDefined(i)) return i;
            return raw.Trim().ToLowerInvariant() switch
            {
                "1m" => TimeInterval.OneMinute, "5m" => TimeInterval.FiveMinute, "15m" => TimeInterval.FifteenMinute,
                "30m" => TimeInterval.ThirtyMinute, "1h" => TimeInterval.OneHour, "2h" => TimeInterval.TwoHour,
                "4h" => TimeInterval.FourHour, "12h" => TimeInterval.TwelveHour, "1d" => TimeInterval.OneDay, "1w" => TimeInterval.OneWeek,
                _ => null,
            };
        }

        private static double Round(double v, int digits = 2) => Math.Round(v, digits);
        private static double? Get(Dictionary<string, double> m, string key, int digits = 2) => m.TryGetValue(key, out var v) && double.IsFinite(v) ? Math.Round(v, digits) : null;
        private static string Fmt(double? v) => v is double x ? (Math.Abs(x) >= 100 ? x.ToString("0") : Math.Abs(x) >= 10 ? x.ToString("0.#") : x.ToString("0.##")) : "—";
    }
}
