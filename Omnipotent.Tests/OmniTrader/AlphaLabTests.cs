using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Tests.OmniTrader
{
    public class PaperTriageTests
    {
        private static ResearchPaper Paper(string title, string abs, params string[] cats)
            => new() { Id = "t:" + Guid.NewGuid().ToString("N")[..6], Title = title, Abstract = abs, Categories = cats.ToList() };

        [Fact]
        public void Trading_Paper_Scores_High_And_Is_Tagged()
        {
            var p = Paper("Time-series momentum and trend following in cryptocurrency markets",
                "We study time-series momentum in bitcoin and the largest cryptocurrencies. A trend-following strategy that goes long when the " +
                "trailing return is positive earns excess returns with a Sharpe ratio above one after transaction costs. We backtest the rule " +
                "out-of-sample across 2018-2025, compare it to buy-and-hold, and show the profitability survives realistic trading frictions. " +
                "Moving average variants and volatility targeting improve the risk-adjusted returns further.", "q-fin.TR");
            PaperScoring.Score(p);
            Assert.True(p.Relevance >= 60, $"relevance {p.Relevance}");
            Assert.Contains("momentum", p.Families);
            Assert.Contains("crypto", p.AssetClasses);
            Assert.Contains("prices", p.DataNeeds);
            Assert.True(p.Feasibility >= 80, $"feasibility {p.Feasibility}");
        }

        [Fact]
        public void Theory_Paper_Scores_Low()
        {
            var p = Paper("Existence and uniqueness of equilibria in a mean-field game of optimal execution",
                "We prove a theorem establishing existence and uniqueness of solutions to a class of stochastic differential equations arising in a " +
                "mean-field game. The Hamilton-Jacobi-Bellman system is analysed with martingale methods and we characterise the equilibrium. " +
                "Our results extend the general equilibrium literature and the PDE theory for controlled diffusions in insurance markets.");
            PaperScoring.Score(p);
            Assert.True(p.Relevance < 20, $"relevance {p.Relevance}");
        }

        [Fact]
        public void Data_We_Lack_Lowers_Feasibility()
        {
            var p = Paper("Order book imbalance predicts short-horizon returns",
                "Using limit order book data we show the order book imbalance and order flow predict short-horizon returns. A trading strategy " +
                "based on the imbalance signal earns a high Sharpe ratio before costs. We backtest on intraday high-frequency quote data across " +
                "liquid stocks and discuss the role of bid-ask spreads and transaction costs in profitability.");
            PaperScoring.Score(p);
            Assert.Contains("order-book", p.DataNeeds);
            Assert.True(p.Feasibility <= 30, $"feasibility {p.Feasibility}");
        }

        [Fact]
        public void Arxiv_Atom_Parses_Ids_Categories_And_Links()
        {
            const string xml = """
                <feed xmlns="http://www.w3.org/2005/Atom" xmlns:arxiv="http://arxiv.org/schemas/atom">
                  <entry>
                    <id>http://arxiv.org/abs/2410.01234v2</id>
                    <published>2024-10-02T17:00:00Z</published>
                    <updated>2024-10-05T10:00:00Z</updated>
                    <title>  Momentum   Everywhere:
                      A Study </title>
                    <summary>We document momentum.</summary>
                    <author><name>Ada Lovelace</name></author>
                    <author><name>Alan Turing</name></author>
                    <arxiv:primary_category term="q-fin.TR"/>
                    <category term="q-fin.TR"/><category term="q-fin.PM"/>
                  </entry>
                  <entry>
                    <id>http://arxiv.org/abs/solv-int/9901001v1</id>
                    <published>1999-01-01T00:00:00Z</published>
                    <title>Legacy id</title>
                    <summary>Old.</summary>
                  </entry>
                </feed>
                """;
            var papers = PaperSources.ParseArxiv(xml);
            Assert.Equal(2, papers.Count);
            var p = papers[0];
            Assert.Equal("arxiv:2410.01234", p.Id);
            Assert.Equal("Momentum Everywhere: A Study", p.Title);
            Assert.Equal(new[] { "Ada Lovelace", "Alan Turing" }, p.Authors);
            Assert.Equal("https://arxiv.org/pdf/2410.01234", p.PdfUrl);
            Assert.Equal("q-fin.TR", p.Categories[0]);
            Assert.Equal(new DateTime(2024, 10, 2, 17, 0, 0, DateTimeKind.Utc), p.PublishedUtc);
            Assert.Equal("arxiv:solv-int/9901001", papers[1].Id);
        }

        [Fact]
        public void OpenAlex_Rebuilds_Abstract_From_Inverted_Index()
        {
            var json = JObject.Parse("""
                {"results":[{"id":"https://openalex.org/W42","title":"Carry in crypto","doi":"https://doi.org/10.1/abc",
                  "abstract_inverted_index":{"Funding":[0],"rates":[1],"predict":[2],"returns.":[3]},
                  "publication_date":"2025-03-04","cited_by_count":7,
                  "authorships":[{"author":{"display_name":"B. Author"}}],
                  "primary_location":{"landing_page_url":"https://example.org/p","source":{"display_name":"J. Finance"}},
                  "primary_topic":{"display_name":"Crypto markets","subfield":{"display_name":"Finance"}}}]}
                """);
            var p = Assert.Single(PaperSources.ParseOpenAlex(json));
            Assert.Equal("openalex:W42", p.Id);
            Assert.Equal("Funding rates predict returns.", p.Abstract);
            Assert.Equal("10.1/abc", p.Doi);
            Assert.Equal(7, p.Citations);
            Assert.Equal("J. Finance", p.Venue);
            Assert.Equal("https://example.org/p", p.Url);
        }

        [Fact]
        public void Score_Rewards_Breadth_And_Punishes_Drawdown()
        {
            CandidateEvaluation Eval(double sharpe, double dd, int profitable) => new()
            {
                Primary = new RunMetrics { Sharpe = sharpe, ReturnPct = sharpe * 20, MaxDrawdownPct = dd, Trades = 60 },
                PrimaryStressed = new RunMetrics { ReturnPct = sharpe * 10 },
                OutOfSample = new RunMetrics { Sharpe = sharpe * 0.8 },
                Batch = new BatchSummary { Symbols = 6, Profitable = profitable, MedianSharpe = sharpe, BeatBenchmarkPct = profitable / 6.0 * 100 },
                DeflatedSharpe = Math.Clamp(sharpe / 2, 0, 1),
            };
            static double ScoreOf(CandidateEvaluation e)
            {
                var parts = typeof(CandidateEvaluator).GetMethod("ScoreParts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                e.ScoreParts = (Dictionary<string, double>)parts.Invoke(null, new object[] { e })!;
                return CandidateEvaluator.Score(e);
            }
            double good = ScoreOf(Eval(1.6, 20, 6));
            double narrow = ScoreOf(Eval(1.6, 20, 1));
            double deep = ScoreOf(Eval(1.6, 75, 6));
            double bad = ScoreOf(Eval(-0.5, 60, 0));
            Assert.True(good > narrow, $"{good} vs {narrow}");
            Assert.True(good > deep, $"{good} vs {deep}");
            Assert.True(bad < 20, $"bad {bad}");
            Assert.InRange(good, 60, 100);
        }

        [Fact]
        public void Agent_Tool_Surface_Matches_The_Brief()
        {
            var log = new List<string>();
            var registry = OmniToolRegistry.Build(typeof(Omnipotent.Services.OmniTrader.OmniTrader).Assembly, log.Add);
            var svc = registry.GetService("omnitrader");
            Assert.NotNull(svc);
            Assert.True(svc!.Annotated);
            Assert.DoesNotContain(log, l => l.Contains("OmniTraderAgentTools") && l.Contains("skipping"));
            Assert.DoesNotContain(log, l => l.Contains("duplicate op") && l.Contains("omnitrader"));

            var tools = svc.Groups.ToDictionary(g => g.ToolName, g => g.Operations.Select(o => o.Op).ToHashSet());
            Assert.Equal(new[] { "omnitrader_data", "omnitrader_lab", "omnitrader_research", "omnitrader_studio" }, tools.Keys.OrderBy(k => k));
            foreach (var op in new[] { "papers", "paper_get", "paper_verdict", "paper_add", "alpha_submit", "candidates", "candidate_get", "ingest_now" })
                Assert.Contains(op, tools["omnitrader_research"]);
            foreach (var op in new[] { "reference", "templates", "list", "get", "create", "update", "validate", "formula_check" })
                Assert.Contains(op, tools["omnitrader_studio"]);
            foreach (var op in new[] { "strategies", "quick_backtest", "backtest", "backtest_get", "sweep", "study_get" })
                Assert.Contains(op, tools["omnitrader_lab"]);
            foreach (var op in new[] { "quote", "candles", "screen", "company", "derivatives", "macro", "news", "alert_set", "alerts" })
                Assert.Contains(op, tools["omnitrader_data"]);

            // Every tool the brief tells the agent to call must exist under that exact name.
            var brief = typeof(AlphaLab).GetMethod("Brief", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.NotNull(brief);
            foreach (var (tool, op) in new[] { ("omnitrader_research", "paper_get"), ("omnitrader_research", "paper_verdict"), ("omnitrader_research", "alpha_submit"),
                         ("omnitrader_studio", "reference"), ("omnitrader_studio", "create"), ("omnitrader_studio", "validate"), ("omnitrader_studio", "update"),
                         ("omnitrader_lab", "quick_backtest") })
                Assert.NotNull(registry.FindOnTool(tool, op));
        }

        [Fact]
        public void Studio_Reference_Lists_Every_Indicator()
        {
            string text = Omnipotent.Services.OmniTrader.Agent.StudioReference.Text;
            foreach (var spec in Omnipotent.Services.OmniTrader.Technicals.IndicatorCatalog.All)
                Assert.Contains($"- {spec.Id}(", text);
            Assert.Contains("ScriptUniverseStrategy", text);
            Assert.Contains("crosses_above", text);
        }
    }

    /// <summary>
    /// The Alpha Lab loop offline: a paper is dispatched to a stand-in agent that implements it through
    /// the same calls the real agent's tools make, and the candidate goes through the whole battery on
    /// seeded bars.
    /// </summary>
    public class AlphaLabFlowTests : IDisposable
    {
        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly StrategyRegistry registry = new();
        private readonly MarketDataRouter router;
        private readonly BacktestJobRepository jobs;
        private readonly BacktestJobQueue queue;
        private readonly StudioService studio;
        private readonly StudyRunner runner;
        private readonly CancellationTokenSource cts = new();
        private readonly List<string> notifications = new();

        public AlphaLabFlowTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-alpha-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            var cache = new CandleCacheRepository(db);
            router = new MarketDataRouter(cache);
            jobs = new BacktestJobRepository(db);
            queue = new BacktestJobQueue(jobs, router, registry, new UniverseRepository(db), _ => { }, (_, _) => { });
            studio = new StudioService(db, registry, queue, router, _ => { });
            runner = new StudyRunner(db, queue, jobs, (_, _) => Task.FromResult(new List<string>()), (_, _) => { });
            queue.Start();
            runner.Start(cts.Token);
            foreach (var (sym, seed) in new[] { ("AAAUSDT", 11), ("BBBUSDT", 12), ("CCCUSDT", 13) })
                Seed(cache, sym, seed).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            cts.Cancel();
            db.Dispose();
            try { File.Delete(dbPath); } catch { }
        }

        private static async Task Seed(CandleCacheRepository cache, string symbol, int seed, int n = 900)
        {
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, TimeInterval.OneDay);
            var rng = new Random(seed);
            double price = 100;
            var bars = new List<OHLCCandle>(n);
            for (int i = 0; i < n; i++)
            {
                double drift = Math.Sin(i / 60.0 + seed) * 0.004;
                double next = Math.Max(5, price * (1 + drift + (rng.NextDouble() - 0.5) * 0.03));
                bars.Add(new OHLCCandle(last.AddDays(i - (n - 1)), (decimal)price, (decimal)(Math.Max(price, next) * 1.01), (decimal)(Math.Min(price, next) * 0.99), (decimal)next, 1000));
                price = next;
            }
            await cache.UpsertManyAsync(symbol, TimeInterval.OneDay, bars);
        }

        [Fact]
        public async Task Dispatched_Paper_Becomes_An_Evaluated_Candidate()
        {
            AlphaLab? lab = null;
            string? strategyId = null;
            string? briefSeen = null;
            var evaluator = new CandidateEvaluator(studio, queue, jobs, runner, registry);
            lab = new AlphaLab(db, studio, evaluator,
                async (brief, conversation, ct) =>
                {
                    // The stand-in agent: implement the paper with a template, submit, give a verdict.
                    briefSeen = brief;
                    var s = await studio.CreateFromTemplateAsync("ema-cross", "kliveagent", "Paper EMA");
                    await studio.UpdateAsync(s.Id, new StudioDraft { DefaultSymbol = "AAAUSDT" }, "kliveagent");
                    strategyId = s.Id;
                    var paperId = (await lab!.ListPapersAsync()).Single().Id;
                    await lab.SubmitAsync(paperId, s.Id, "Trend persists in crypto", new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" }, TimeInterval.OneDay, "kliveagent");
                    await lab.SetVerdictAsync(paperId, "implemented", "EMA 20/50 crossover as in the paper", "kliveagent");
                    return "Implemented 1 paper.";
                },
                m => { lock (notifications) notifications.Add(m); return Task.CompletedTask; },
                _ => { }, (_, _) => { });
            await lab.LoadAsync();
            var settings = lab.Settings;
            settings.EvaluationBars = 800;
            settings.UseArxiv = false;
            settings.UseOpenAlex = false;
            await lab.SaveSettingsAsync(settings);
            lab.Start(cts.Token);

            var paper = await lab.AddPaperAsync("Trend following in cryptocurrency markets",
                "We backtest a trend-following moving average crossover on bitcoin with transaction costs and report the Sharpe ratio out-of-sample.",
                "https://example.org/paper", "klives");
            var run = await lab.DispatchAsync("manual", default, new[] { paper.Id });
            Assert.NotNull(run);

            AlphaCandidate? candidate = null;
            for (int i = 0; i < 1800 && (candidate == null || candidate.Status is CandidateStatus.Proposed or CandidateStatus.Evaluating); i++)
            {
                await Task.Delay(100);
                candidate = (await lab.ListCandidatesAsync()).FirstOrDefault();
            }
            Assert.NotNull(candidate);
            Assert.NotNull(briefSeen);
            Assert.Contains(paper.Id, briefSeen);
            Assert.Contains("omnitrader_research op=alpha_submit", briefSeen);

            Assert.True(candidate!.Status is CandidateStatus.Promising or CandidateStatus.Weak or CandidateStatus.Rejected,
                $"status {candidate.Status}: {candidate.Evaluation?.Error}");
            var e = candidate.Evaluation!;
            Assert.Null(e.Error);
            Assert.NotNull(e.Primary);
            Assert.Equal(3, e.Batch!.Symbols);
            Assert.NotNull(e.PrimaryStressed);
            Assert.NotNull(e.DeflatedSharpe);
            Assert.NotNull(e.PerPeriodSharpe);
            Assert.True(e.TrialsAccounted >= 1);
            Assert.NotNull(candidate.Score);
            Assert.Contains(e.Findings, f => f.StartsWith("primary job"));
            // Walk-forward ran over the template's two inputs on 800 bars.
            Assert.NotNull(e.OutOfSample);

            var stored = await lab.GetPaperAsync(paper.Id);
            Assert.Equal(PaperStatus.Implemented, stored!.Status);
            Assert.Contains(candidate.Id, stored.CandidateIds);
            var strategy = await studio.GetAsync(strategyId!);
            Assert.Equal("paper:" + paper.Id, strategy!.Origin);

            // The persisted primary job is a normal lab backtest, findable by its label.
            var (rows, _) = await jobs.QuerySummariesAsync(new BacktestJobFilter { Origin = "alpha" });
            Assert.Contains(rows, r => r.Config.Label == $"alpha:{candidate.Id}" && r.Status == BacktestJobStatus.Succeeded);

            for (int i = 0; i < 50 && (await lab.ListRunsAsync("analyse")).First().Status == "running"; i++) await Task.Delay(100);
            Assert.Equal("done", (await lab.ListRunsAsync("analyse")).First().Status);
        }

        [Fact]
        public async Task Market_Alert_Fires_On_Edge_And_Once_Disables()
        {
            var sent = new List<string>();
            var alerts = new MarketAlertService(db, router, m => { sent.Add(m); return Task.CompletedTask; }, (_, _) => { });
            Assert.NotEmpty(MarketAlertService.Validate(new MarketAlert { Symbol = "AAAUSDT", Condition = "sma(close, 20)" }));
            Assert.NotEmpty(MarketAlertService.Validate(new MarketAlert { Symbol = "AAAUSDT", Condition = "close >" }));

            var always = await alerts.SaveAsync(new MarketAlert { Symbol = "aaausdt", Interval = TimeInterval.OneDay, Condition = "close > 0", Mode = AlertMode.Once });
            var never = await alerts.SaveAsync(new MarketAlert { Symbol = "AAAUSDT", Interval = TimeInterval.OneDay, Condition = "close < 0", Mode = AlertMode.EveryBar });
            var edge = await alerts.SaveAsync(new MarketAlert { Symbol = "AAAUSDT", Interval = TimeInterval.OneDay, Condition = "close > 0", Mode = AlertMode.OnChange });

            var test = await alerts.TestAsync(always);
            Assert.True(test.Value);
            Assert.Equal(test.Bars, test.TrueBars);

            await alerts.TickAsync();
            var a = await alerts.GetAsync(always.Id);
            Assert.False(a!.Enabled);
            Assert.Equal(1, a.TriggerCount);
            Assert.Equal("AAAUSDT", a.Symbol);
            Assert.Equal(0, (await alerts.GetAsync(never.Id))!.TriggerCount);
            // Always-true has no false→true edge in the data, so OnChange stays quiet.
            Assert.Equal(0, (await alerts.GetAsync(edge.Id))!.TriggerCount);
            Assert.Single(sent);
            Assert.Single(await alerts.EventsAsync(always.Id));

            // Same bar again: nothing re-fires.
            await alerts.TickAsync();
            Assert.Single(sent);
        }
    }
}
