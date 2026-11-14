using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Writes the Deploy page's e2e fixtures (deploy-*.json) from the real planner, catalog and health rules,
    /// so the website is tested against engine-shaped payloads. Synthetic strategies and numbers only.
    /// <c>OMNITRADER_FIXTURES=&lt;website&gt;/tests/e2e/fixtures/omnitrader dotnet test --filter "FullyQualifiedName~DeployFixtureExport"</c>
    /// </summary>
    public class DeployFixtureExport
    {
        private static readonly DateTime Now = new(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        private readonly string outDir = Environment.GetEnvironmentVariable("OMNITRADER_FIXTURES") ?? Path.Combine(Path.GetTempPath(), "ot-fixtures");

        private void Write(string name, object payload, bool terminal = true)
        {
            Directory.CreateDirectory(outDir);
            string json = terminal ? JsonConvert.SerializeObject(payload, Formatting.None, TerminalRoutes.JsonSettings) : JsonConvert.SerializeObject(payload);
            File.WriteAllText(Path.Combine(outDir, name + ".json"), json);
        }

        private static StrategyDescriptor Describe<T>(string className, string name) where T : TradingStrategy, new() => new()
        {
            Name = name, Description = "", ClassName = className, Type = typeof(T), Source = "script",
            RequiresUniverse = typeof(Omnipotent.Services.OmniTrader.Scripting.ScriptUniverseStrategy).IsAssignableFrom(typeof(T)),
            Parameters = StrategyParams.For(typeof(T)), Factory = () => new T(),
        };

        private static AlphaCandidate Candidate(string id, string strategyId, string name, CandidateStatus status, double score, TimeInterval interval, double sharpe, double ret, double dd, int trades, params string[] symbols) => new()
        {
            Id = id, StrategyId = strategyId, StrategyName = name, StrategyVersion = 2, Status = status, Score = score, Interval = interval,
            Hypothesis = "A synthetic fixture strategy: trend entries filtered by a volatility regime.",
            PaperTitle = "Fixture paper", PaperId = "fixture:1", PaperIds = new() { "fixture:1" },
            TestSymbols = symbols.ToList(), CreatedUtc = Now.AddDays(-20), UpdatedUtc = Now.AddDays(-20),
            Evaluation = new CandidateEvaluation
            {
                Primary = new RunMetrics { ReturnPct = ret, CagrPct = ret / 4, Sharpe = sharpe, MaxDrawdownPct = dd, Trades = trades, ExposurePct = 44, WinRatePct = 54 },
                PrimaryStressed = new RunMetrics { ReturnPct = ret * 0.4 - 3, Sharpe = sharpe * 0.6 },
                OutOfSample = new RunMetrics { Sharpe = status == CandidateStatus.Rejected ? -0.3 : sharpe * 1.2 },
                PerSymbol = symbols.Select((s, i) => new StudyRun { Symbol = s, Metrics = new RunMetrics { Sharpe = Math.Round(sharpe + 0.12 * (i == 2 ? 1 : -i), 3), ReturnPct = ret + 3 * i, MaxDrawdownPct = dd + i, Trades = trades } }).ToList(),
                Batch = new BatchSummary { Symbols = symbols.Length, Profitable = symbols.Length, MedianSharpe = sharpe },
                FinishedUtc = Now.AddDays(-20),
                Findings = { $"primary: {ret:F1}% return, Sharpe {sharpe:F2}" },
            },
        };

        private DeployPlan Plan(StrategyDescriptor d, AlphaCandidate? c, StudioStrategy? s, DeployPlanRequest r, IReadOnlyList<DeploymentRow> existing) => DeployPlanner.Build(r, new DeployPlanInputs
        {
            Descriptor = d,
            Create = p => { var x = d.Factory!(); StrategyParams.Apply(x, new Dictionary<string, object?>(p, StringComparer.OrdinalIgnoreCase)); return x; },
            Candidate = c, Studio = s, EvaluationBars = 1500, Existing = existing, KrakenConfigured = true, NowUtc = Now,
        });

        private static object ListRow(DeploymentRow d, SessionPulse? pulse, OrderSummary? orders, string name) => new
        {
            d.Id, d.StrategyClass, StrategyName = d.Config.Origin?.StrategyName ?? name, Label = d.Config.Origin?.Label, d.Config.Symbol,
            Interval = d.Config.Interval.ToString(), Mode = d.Mode.ToString(), Status = d.Status.ToString(), Armed = false,
            d.EquityInitial, d.EquityCurrent, PnLPercent = (d.EquityCurrent - d.EquityInitial) / d.EquityInitial * 100m,
            d.CreatedUtc, d.ArmedLiveUtc, d.PausedUtc, d.Error, Shape = d.Config.Origin?.Shape ?? (d.StrategyClass.Contains("etf") ? "basket" : null),
            Origin = d.Config.Origin == null ? null : new
            {
                d.Config.Origin.Source, d.Config.Origin.CandidateId, d.Config.Origin.CandidateStatus, d.Config.Origin.Score, d.Config.Origin.StrategyId,
                d.Config.Origin.StrategyVersion, d.Config.Origin.BarsPerTrade,
                Evaluated = d.Config.Origin.Evaluated == null ? null : new
                {
                    Interval = d.Config.Origin.Evaluated.Interval.ToString(), d.Config.Origin.Evaluated.ReturnPct, d.Config.Origin.Evaluated.CagrPct,
                    d.Config.Origin.Evaluated.Sharpe, d.Config.Origin.Evaluated.MaxDrawdownPct, d.Config.Origin.Evaluated.Trades, d.Config.Origin.Evaluated.Bars,
                },
            },
            Health = DeploymentHealth.Assess(d, pulse, orders, false, Now),
        };

        [OmniTraderFixtureFact]
        public void Export_deploy_fixtures()
        {
            var trendD = Describe<TestBuyOnceStrategy>("studio:st_trend@v2", "Vol-gated trend");
            var goldD = Describe<TestBuyOnceStrategy>("studio:st_gold@v2", "Gold MACD exits");
            var rejD = Describe<TestBuyOnceStrategy>("studio:st_fade@v2", "Gap fade");
            var etfD = Describe<TestEtfBasketStrategy>("studio:st_etf", "ETF momentum basket");

            var trend = Candidate("ac_trend", "st_trend", "Vol-gated trend", CandidateStatus.Promising, 73.1, TimeInterval.FourHour, 0.87, 12.4, 14.2, 41, "ETHUSDT", "BTCUSDT", "SOLUSDT");
            var gold = Candidate("ac_gold", "st_gold", "Gold MACD exits", CandidateStatus.Promoted, 78.3, TimeInterval.OneDay, 0.85, 51.2, 14.9, 25, "GC=F");
            var fade = Candidate("ac_fade", "st_fade", "Gap fade", CandidateStatus.Rejected, 31.0, TimeInterval.OneDay, 0.12, 2.1, 38.0, 8, "SPY");
            var studios = new List<StudioStrategy>
            {
                new() { Id = "st_trend", Name = "Vol-gated trend", Version = 2, Status = StudioStatus.Valid, DefaultSymbol = "ETHUSDT", DefaultInterval = TimeInterval.FourHour },
                new() { Id = "st_gold", Name = "Gold MACD exits", Version = 3, Status = StudioStatus.Valid, DefaultSymbol = "GC=F", DefaultInterval = TimeInterval.OneDay },
                new() { Id = "st_fade", Name = "Gap fade", Version = 2, Status = StudioStatus.Valid, DefaultSymbol = "SPY", DefaultInterval = TimeInterval.OneDay },
                new() { Id = "st_etf", Name = "ETF momentum basket", Version = 1, Status = StudioStatus.Valid, DefaultSymbol = "SPY", DefaultInterval = TimeInterval.OneDay, Description = "Twelve-month momentum across three ETFs, rebalanced monthly." },
            };

            // Deployments: the shape of the live one that never traded, one healthy one from a plan, one stopped.
            var trendPlan = Plan(trendD, trend, studios[0], new DeployPlanRequest { CandidateId = "ac_trend" }, Array.Empty<DeploymentRow>());
            var quiet = new DeploymentRow
            {
                Id = "a1b2c3d4e5f60718293a4b5c6d7e8f90", StrategyClass = "studio:st_etf", Mode = SessionMode.Paper, Status = DeploymentStatus.Running,
                CreatedUtc = Now.AddDays(-25), EquityInitial = 10_000, EquityCurrent = 10_000,
                Config = new DeploymentConfig { StrategyClass = "studio:st_etf", Symbol = "SPY", Interval = TimeInterval.OneHour, Mode = SessionMode.Paper },
            };
            var running = new DeploymentRow
            {
                Id = "0f1e2d3c4b5a69788796a5b4c3d2e1f0", StrategyClass = "studio:st_trend@v2", Mode = SessionMode.Paper, Status = DeploymentStatus.Running,
                CreatedUtc = Now.AddDays(-30), EquityInitial = 10_000, EquityCurrent = 10_240,
                Config = new DeploymentConfig
                {
                    StrategyClass = "studio:st_trend@v2", Symbol = "ETHUSDT", Interval = TimeInterval.FourHour, Mode = SessionMode.Paper,
                    FeeFraction = 0.0026m, SlippageFraction = 0.0005m, Parameters = new() { ["TradeSymbol"] = "ETHUSDT", ["SlowLength"] = 50 },
                    Execution = ExecutionTiming.SignalClose, HistoryBars = 500, Origin = trendPlan.Request.Origin,
                },
            };
            var stopped = new DeploymentRow
            {
                Id = "99887766554433221100ffeeddccbbaa", StrategyClass = "IBSMeanReversionStrategy", Mode = SessionMode.Paper, Status = DeploymentStatus.Stopped,
                CreatedUtc = Now.AddDays(-160), EquityInitial = 10_000, EquityCurrent = 9_808.63m,
                Config = new DeploymentConfig { StrategyClass = "IBSMeanReversionStrategy", Symbol = "BTCUSDT", Interval = TimeInterval.FifteenMinute, Mode = SessionMode.Paper },
            };
            var existing = new[] { quiet, running, stopped };
            var quietPulse = new SessionPulse { Kind = "single", StartedUtc = Now.AddHours(-13), LastBarUtc = Now.AddHours(-1), LastBarSeenUtc = Now.AddMinutes(-30), BarsThisRun = 143, BarsTotal = 143, SymbolsTracked = 1 };
            var runPulse = new SessionPulse { Kind = "single", StartedUtc = Now.AddHours(-13), LastBarUtc = Now.AddHours(-4), LastBarSeenUtc = Now.AddMinutes(-10), BarsThisRun = 3, BarsTotal = 180, SymbolsTracked = 1 };
            var runOrders = new OrderSummary { Orders = 6, Filled = 6, LastPlacedUtc = Now.AddDays(-2) };

            Write("deploy-deployments", new[]
            {
                ListRow(quiet, quietPulse, null, "ETF momentum basket"),
                ListRow(running, runPulse, runOrders, "Vol-gated trend"),
                ListRow(stopped, null, new OrderSummary { Orders = 100, Filled = 100, LastPlacedUtc = Now.AddDays(-31) }, "IBS mean reversion"),
            }, terminal: false);

            // Detail and equity for the healthy one.
            var orders = Enumerable.Range(0, 6).Select(i => new OrderRecord
            {
                Id = $"o{i}", DeploymentId = running.Id, IntentId = $"i{i}", Side = i % 2 == 0 ? "buy" : "sell", Type = "Market", Symbol = "ETHUSDT",
                Qty = 1.25m, Status = "Filled", PlacedUtc = Now.AddDays(-2 - i * 4),
            }).ToList();
            var fills = orders.Select((o, i) => new FillRecord { Id = i, OrderId = o.Id, Qty = 1.25m, Price = 3_100m + i * 40, Fee = 10.1m, FeeCurrency = "USDT", FilledUtc = o.PlacedUtc }).ToList();
            Write("deploy-detail-trend", new
            {
                Deployment = new { running.Id, running.StrategyClass, running.Config, Mode = "Paper", Status = "Running", running.EquityInitial, running.EquityCurrent, running.CreatedUtc, running.ArmedLiveUtc, running.PausedUtc, running.Error },
                StrategyName = "Vol-gated trend", Interval = "FourHour", Execution = "SignalClose", Armed = false,
                Health = DeploymentHealth.Assess(running, runPulse, runOrders, false, Now), Pulse = runPulse,
                Book = new { Quote = 6_350.0m, UpdatedUtc = Now.AddHours(-4), Fees = 60.6m, Holdings = new[] { new PaperBookPosition { Symbol = "ETHUSDT", Qty = 1.25m, AveragePrice = 3_100m, OpenedUtc = Now.AddDays(-2) } } },
                Trades = OmniTraderRoutes.DeploymentTrades(orders, fills), Orders = orders, Fills = fills,
            }, terminal: false);
            var rng = new Random(7);
            double eq = 10_000;
            var equity = Enumerable.Range(0, 180).Select(i =>
            {
                eq *= 1 + (rng.NextDouble() - 0.47) * 0.006;
                return new EquityPoint { Ts = Now.AddDays(-30).AddHours(4 * i), MarkPrice = 3_100, QuoteBalance = 6_350, BaseBalance = 1.25m, Equity = (decimal)Math.Round(eq, 2) };
            }).ToList();
            Write("deploy-equity-trend", equity, terminal: false);

            // Plans, including the overrides the spec exercises.
            Write("deploy-plan-ac_trend", trendPlan);
            Write("deploy-plan-ac_trend-OneHour", Plan(trendD, trend, studios[0], new DeployPlanRequest { CandidateId = "ac_trend", Interval = "OneHour" }, existing));
            Write("deploy-plan-ac_trend-SOLUSDT", Plan(trendD, trend, studios[0], new DeployPlanRequest { CandidateId = "ac_trend", Symbol = "SOLUSDT" }, existing));
            Write("deploy-plan-ac_trend-Live", Plan(trendD, trend, studios[0], new DeployPlanRequest { CandidateId = "ac_trend", Mode = "Live" }, existing));
            Write("deploy-plan-ac_gold", Plan(goldD, gold, studios[1], new DeployPlanRequest { CandidateId = "ac_gold" }, existing));
            Write("deploy-plan-ac_gold-Live", Plan(goldD, gold, studios[1], new DeployPlanRequest { CandidateId = "ac_gold", Mode = "Live" }, existing));
            Write("deploy-plan-ac_fade", Plan(rejD, fade, studios[2], new DeployPlanRequest { CandidateId = "ac_fade" }, existing));
            Write("deploy-plan-st_etf", Plan(etfD, null, studios[3], new DeployPlanRequest { StrategyClass = "studio:st_etf" }, existing));

            var registry = new StrategyRegistry();
            registry.RegisterDynamic(Describe<TestBuyOnceStrategy>("studio:st_trend", "Vol-gated trend"));
            registry.RegisterDynamic(Describe<TestBuyOnceStrategy>("studio:st_gold", "Gold MACD exits"));
            registry.RegisterDynamic(Describe<TestBuyOnceStrategy>("studio:st_fade", "Gap fade"));
            registry.RegisterDynamic(Describe<TestEtfBasketStrategy>("studio:st_etf", "ETF momentum basket"));
            var rows = DeployCatalog.Build(new[] { trend, gold, fade }, studios, registry)
                .Where(r => r.Source != "builtin")
                .Select(r => r with { Running = r.Key == "ac_trend" || r.Key == "studio:st_etf" ? 1 : 0, Deployed = r.Key == "ac_trend" || r.Key == "studio:st_etf" ? 1 : 0 })
                .ToList();
            Write("deploy-catalog", new
            {
                Rows = rows,
                Counts = new { Total = rows.Count, Recommended = rows.Count(r => r.Recommended), Alpha = rows.Count(r => r.Source == "alpha"), Studio = rows.Count(r => r.Source == "studio"), Builtin = 0, Deployed = rows.Count(r => r.Running > 0) },
                BuiltUtc = Now.AddMinutes(-1), BuildMs = 412.5,
            });
            Write("deploy-meta", new { GeneratedUtc = Now });
        }
    }
}
