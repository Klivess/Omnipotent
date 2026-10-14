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
            foreach (var op in new[] { "papers", "paper_get", "paper_verdict", "paper_add", "idea_add", "ideas", "alpha_submit", "candidates", "candidate_get", "ingest_now" })
                Assert.Contains(op, tools["omnitrader_research"]);
            foreach (var op in new[] { "reference", "templates", "template_get", "list", "get", "create", "update", "validate", "formula_check" })
                Assert.Contains(op, tools["omnitrader_studio"]);
            foreach (var op in new[] { "strategies", "quick_backtest", "backtest", "backtest_get", "sweep", "study_get" })
                Assert.Contains(op, tools["omnitrader_lab"]);
            foreach (var op in new[] { "quote", "candles", "screen", "company", "derivatives", "macro", "news", "alert_set", "alerts" })
                Assert.Contains(op, tools["omnitrader_data"]);

            // Every tool the brief tells the agent to call must exist under that exact name.
            var brief = typeof(AlphaLab).GetMethod("Brief", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.NotNull(brief);
            foreach (var (tool, op) in new[] { ("omnitrader_research", "paper_get"), ("omnitrader_research", "paper_verdict"), ("omnitrader_research", "alpha_submit"),
                         ("omnitrader_research", "idea_add"), ("omnitrader_research", "ideas"),
                         ("omnitrader_studio", "reference"), ("omnitrader_studio", "create"), ("omnitrader_studio", "validate"), ("omnitrader_studio", "update"),
                         ("omnitrader_studio", "template_get"), ("omnitrader_lab", "quick_backtest") })
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
            // C# leads, with complete worked scripts the agent can copy.
            Assert.True(text.IndexOf("## Script (kind=script)", StringComparison.Ordinal) < text.IndexOf("## Blueprint (kind=blueprint)", StringComparison.Ordinal));
            Assert.Contains("```csharp", text);
            Assert.Contains("class KeltnerBreakout : ScriptStrategy", text);
            Assert.Contains("class CrossSectionalMomentum : ScriptUniverseStrategy", text);
            Assert.Contains("class TrendCrashFilterVolTarget", text);
        }

        [Fact]
        public void Synthesis_Is_Compared_With_Its_Strongest_Part_On_Shared_Symbols()
        {
            static StudyRun Run(string symbol, double sharpe) => new() { Symbol = symbol, Metrics = new RunMetrics { Sharpe = sharpe } };
            var synthesis = new AlphaCandidate { Id = "ac_syn", StrategyId = "st_syn", PaperIds = { "p1", "p2" }, TestSymbols = { "AAA", "BBB", "CCC" } };
            var eval = new CandidateEvaluation
            {
                Primary = new RunMetrics { Sharpe = 1.1 },
                PerSymbol = { Run("AAA", 1.2), Run("BBB", 0.9), Run("CCC", 1.0) },
            };
            var strong = new AlphaCandidate
            {
                Id = "ac_a", StrategyId = "st_a", StrategyName = "Momentum alone", PaperId = "p1", TestSymbols = { "AAA", "BBB", "DDD" },
                Evaluation = new CandidateEvaluation { Primary = new RunMetrics { Sharpe = 0.8 }, PerSymbol = { Run("AAA", 0.8), Run("BBB", 0.6), Run("DDD", 2.0) } }
            };
            var weak = new AlphaCandidate
            {
                Id = "ac_b", StrategyId = "st_b", StrategyName = "Filter alone", PaperId = "p2", TestSymbols = { "ZZZ" },
                Evaluation = new CandidateEvaluation { Primary = new RunMetrics { Sharpe = 0.2 } }
            };
            var otherInterval = new AlphaCandidate
            {
                Id = "ac_c", StrategyId = "st_c", PaperId = "p1", Interval = TimeInterval.OneHour,
                Evaluation = new CandidateEvaluation { Primary = new RunMetrics { Sharpe = 3 } }
            };

            var parts = CandidateEvaluator.CompareConstituents(synthesis, eval, new[] { weak, strong, otherInterval });
            Assert.Equal(new[] { "ac_a", "ac_b" }, parts.Select(p => p.CandidateId));
            // Like for like: AAA and BBB only (DDD's 2.0 is not on the synthesis's basket).
            Assert.Equal(2, parts[0].SharedSymbols);
            Assert.Equal(0.7, parts[0].Sharpe, 3);
            Assert.Equal(1.05, parts[0].SynthesisSharpe, 3);
            Assert.Equal(0.35, parts[0].Delta, 3);
            // No shared symbols: primary against primary.
            Assert.Equal(0, parts[1].SharedSymbols);
            Assert.Equal(0.9, parts[1].Delta, 3);

            eval.Constituents = parts;
            var scoreParts = (Dictionary<string, double>)typeof(CandidateEvaluator).GetMethod("ScoreParts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { eval })!;
            Assert.Equal(5, scoreParts["vs parts"]);
            eval.Constituents[0].Delta = -0.3;
            scoreParts = (Dictionary<string, double>)typeof(CandidateEvaluator).GetMethod("ScoreParts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { eval })!;
            Assert.Equal(-8, scoreParts["vs parts"]);
        }

        [Fact]
        public void Old_Single_Paper_Candidates_Read_As_One_Source()
        {
            var json = """{"Id":"ac_old","PaperId":"arxiv:1","PaperTitle":"Old","StrategyId":"st_1","Status":"Weak"}""";
            var c = Newtonsoft.Json.JsonConvert.DeserializeObject<AlphaCandidate>(json, DocumentStore<AlphaCandidate>.Json)!;
            Assert.Equal(new[] { "arxiv:1" }, c.SourcePaperIds());
            Assert.False(c.IsSynthesis());
            Assert.Empty(c.IdeaIds);
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

        private static async Task<AlphaCandidate> WaitEvaluatedAsync(AlphaLab lab, string id)
        {
            AlphaCandidate? c = null;
            for (int i = 0; i < 2400; i++)
            {
                c = await lab.GetCandidateAsync(id);
                if (c != null && c.Status is not (CandidateStatus.Proposed or CandidateStatus.Evaluating)) return c;
                await Task.Delay(100);
            }
            throw new TimeoutException($"candidate {id} stayed {c?.Status}");
        }

        [Fact]
        public async Task Reference_Synthesis_Example_Compiles_And_Trades()
        {
            var (check, result, error) = await studio.QuickTestAsync(
                new StudioDraft { Kind = StudioKind.Script, Source = Omnipotent.Services.OmniTrader.Agent.StudioReference.SynthesisExample, Name = "synthesis example", DefaultSymbol = "AAAUSDT", DefaultInterval = TimeInterval.OneDay },
                new StudioRunRequest { Symbol = "AAAUSDT", Interval = TimeInterval.OneDay, Bars = 900 });
            Assert.True(check.Ok, string.Join("; ", check.Diagnostics.Select(d => d.Message)));
            Assert.Null(error);
            Assert.NotNull(result);
            Assert.Equal(0, result!.StrategyErrors);
            Assert.True(result.TotalTrades > 0, "the example never traded");
            Assert.Contains(check.Parameters, p => p.Name == "TargetVol" && p.Min == 5 && p.Max == 80);
        }

        [Fact]
        public async Task Synthesis_Connects_Ideas_From_Two_Papers_And_Is_Measured_Against_Its_Parts()
        {
            AlphaLab? lab = null;
            string? synthesisBrief = null;
            string? submittedId = null;
            var ideaIds = new List<string>();
            string p1 = "", p2 = "";
            var evaluator = new CandidateEvaluator(studio, queue, jobs, runner, registry);
            lab = new AlphaLab(db, studio, evaluator,
                async (brief, conversation, ct) =>
                {
                    // The stand-in agent writes the reference's synthesis example in C# and cites both papers.
                    synthesisBrief = brief;
                    var s = await studio.CreateAsync(new StudioDraft
                    {
                        Kind = StudioKind.Script, Source = Omnipotent.Services.OmniTrader.Agent.StudioReference.SynthesisExample,
                        Name = "Trend + crash filter + vol target", DefaultSymbol = "AAAUSDT", DefaultInterval = TimeInterval.OneDay
                    }, "kliveagent");
                    Assert.Equal(StudioStatus.Valid, s.Status);
                    var c = await lab!.SubmitAsync(new CandidateSubmission
                    {
                        StrategyId = s.Id, Hypothesis = "Trend works when volatility is calm; vol-targeting evens the risk.",
                        PaperIds = new[] { p1 }, IdeaIds = ideaIds, TestSymbols = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" }, Interval = TimeInterval.OneDay
                    });
                    submittedId = c.Id;
                    return "Submitted one synthesis.";
                },
                _ => Task.CompletedTask, _ => { }, (_, _) => { });
            await lab.LoadAsync();
            var settings = lab.Settings;
            settings.EvaluationBars = 800;
            settings.UseArxiv = settings.UseOpenAlex = false;
            await lab.SaveSettingsAsync(settings);
            lab.Start(cts.Token);

            p1 = (await lab.AddPaperAsync("Time-series momentum in cryptocurrency markets", "Trailing twelve-month returns predict the next month's return in crypto.", "https://example.org/a", "klives")).Id;
            p2 = (await lab.AddPaperAsync("Volatility-managed portfolios", "Scaling exposure by inverse realised volatility raises Sharpe ratios; momentum crashes cluster in high volatility.", "https://example.org/b", "klives")).Id;

            // What analysis records as it reads the papers.
            ideaIds.Add((await lab.AddIdeaAsync(new AlphaIdea { PaperId = p1, Role = "signal", Name = "12-month time-series momentum", Summary = "Long while the trailing 252-day return is positive.", Formula = "roc(close, 252) > 0" })).Id);
            ideaIds.Add((await lab.AddIdeaAsync(new AlphaIdea { PaperId = p2, Role = "regime", Name = "High-volatility stand-aside", Summary = "Stay out when 20-day realised vol is in its top decile.", Formula = "percentrank(hv(20), 252) < 90" })).Id);
            ideaIds.Add((await lab.AddIdeaAsync(new AlphaIdea { PaperId = p2, Role = "sizing", Name = "Volatility target", Summary = "Exposure = target vol / realised vol, capped at 100%." })).Id);
            await Assert.ThrowsAsync<ArgumentException>(() => lab.AddIdeaAsync(new AlphaIdea { PaperId = p2, Role = "signal", Name = "Broken", Summary = "x", Formula = "close >" }));
            await Assert.ThrowsAsync<ArgumentException>(() => lab.AddIdeaAsync(new AlphaIdea { PaperId = p2, Role = "astrology", Name = "Moon", Summary = "x" }));
            // The same name again updates rather than duplicating.
            var again = await lab.AddIdeaAsync(new AlphaIdea { PaperId = p2, Role = "sizing", Name = "volatility target", Summary = "Exposure = 25% / realised vol, at most 100%.", Parameters = "target 25%" });
            Assert.Equal(ideaIds[2], again.Id);
            Assert.Equal(3, (await lab.ListIdeasAsync(limit: 50)).Count);
            Assert.Equal("filter", (await lab.GetIdeaAsync(ideaIds[1]))!.Role); // "regime" normalises to filter
            Assert.Equal(2, (await lab.GetPaperAsync(p2))!.IdeaIds.Count);

            // A single-paper replication of the signal exists first, so the synthesis has a part to beat.
            var ema = await studio.CreateFromTemplateAsync("ema-cross", "kliveagent", "Momentum alone");
            await studio.UpdateAsync(ema.Id, new StudioDraft { DefaultSymbol = "AAAUSDT" }, "kliveagent");
            var part = await lab.SubmitAsync(new CandidateSubmission { StrategyId = ema.Id, Hypothesis = "Trend persists", PaperIds = new[] { p1 }, IdeaIds = new[] { ideaIds[0] }, TestSymbols = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" } });
            Assert.False(part.IsSynthesis());
            part = await WaitEvaluatedAsync(lab, part.Id);
            Assert.NotNull(part.Evaluation?.Primary);

            // One paper's ideas are not enough to synthesise from.
            Assert.Null(await lab.SynthesiseAsync("manual", default, new[] { p1 }));

            var run = await lab.SynthesiseAsync("manual", default, new[] { p1, p2 }, null, "Keep it to three components.");
            Assert.NotNull(run);
            Assert.Equal("synthesise", run!.Kind);
            for (int i = 0; i < 600 && submittedId == null; i++) await Task.Delay(100);
            Assert.NotNull(submittedId);
            Assert.NotNull(synthesisBrief);
            Assert.Contains(p1, synthesisBrief);
            Assert.Contains(p2, synthesisBrief);
            Assert.Contains("kind=script", synthesisBrief);
            Assert.Contains("alpha_submit paperIds=", synthesisBrief);
            Assert.Contains("Keep it to three components.", synthesisBrief);
            Assert.Contains(ideaIds[1], synthesisBrief);

            var synth = await WaitEvaluatedAsync(lab, submittedId!);
            Assert.True(synth.IsSynthesis());
            Assert.Equal(new[] { p1, p2 }, synth.PaperIds); // p2 joined through its ideas
            Assert.Equal("Script", synth.StrategyKind);
            Assert.Equal(2, synth.Sources.Count);
            Assert.Contains("sizing: volatility target", synth.Sources.Single(x => x.PaperId == p2).Contribution);
            var e = synth.Evaluation!;
            Assert.Null(e.Error);
            Assert.Contains(e.Constituents, k => k.CandidateId == part.Id);
            Assert.Contains("vs parts", e.ScoreParts.Keys);
            Assert.Contains(e.Findings, f => f.StartsWith("vs parts:"));

            // Every paper it draws on lists it; every idea it used knows it and its score.
            Assert.Contains(await lab.ListCandidatesAsync(null, p2), c => c.Id == synth.Id);
            Assert.Equal(2, (await lab.ListCandidatesAsync(null, p1)).Count);
            Assert.Contains(synth.Id, (await lab.GetPaperAsync(p2))!.CandidateIds);
            var usedIdea = (await lab.GetIdeaAsync(ideaIds[1]))!;
            Assert.Contains(synth.Id, usedIdea.CandidateIds);
            // The idea's score is written just after the candidate's.
            for (int i = 0; i < 50 && usedIdea.BestScore == null; i++) { await Task.Delay(100); usedIdea = (await lab.GetIdeaAsync(ideaIds[1]))!; }
            Assert.Equal(synth.Score, usedIdea.BestScore);
            Assert.Equal($"papers:{p1}+{p2}", (await studio.GetAsync(synth.StrategyId))!.Origin);

            for (int i = 0; i < 50 && (await lab.ListRunsAsync("synthesise")).First().Status == "running"; i++) await Task.Delay(100);
            Assert.Equal("done", (await lab.ListRunsAsync("synthesise")).First().Status);
        }

        [Fact]
        public async Task Briefs_Ask_For_Ideas_And_CSharp_And_Name_Only_Real_Tools()
        {
            var lab = new AlphaLab(db, studio, new CandidateEvaluator(studio, queue, jobs, runner, registry),
                (_, _, _) => Task.FromResult<string?>("ok"), _ => Task.CompletedTask, _ => { }, (_, _) => { });
            await lab.LoadAsync();
            var paper = await lab.AddPaperAsync("Carry in crypto", "Funding rates predict returns.", "https://example.org/c", "klives");
            var idea = await lab.AddIdeaAsync(new AlphaIdea { PaperId = paper.Id, Role = "signal", Name = "Funding carry", Summary = "Long low funding, short high funding." });
            string analyse = lab.Brief(new[] { paper }, 1);
            string synthesis = lab.SynthesisBrief(new[] { idea }, Array.Empty<ResearchPaper>(), Array.Empty<AlphaIdea>(), Array.Empty<AlphaCandidate>(), null);

            Assert.Contains("op=idea_add", analyse);
            Assert.Contains("C#", analyse);
            Assert.Contains("kind=script", analyse);
            Assert.Contains("paperIds=", analyse);
            Assert.Contains(idea.Id, synthesis);
            Assert.Contains("[signal]", synthesis);

            var registry2 = OmniToolRegistry.Build(typeof(Omnipotent.Services.OmniTrader.OmniTrader).Assembly, _ => { });
            var svc = registry2.GetService("omnitrader")!;
            var allOps = svc.Groups.SelectMany(g => g.Operations.Select(o => o.Op)).ToHashSet();
            foreach (var text in new[] { analyse, synthesis })
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(omnitrader_\w+) op=(\w+)"))
                    Assert.True(registry2.FindOnTool(m.Groups[1].Value, m.Groups[2].Value) != null, $"{m.Value} is not a tool");
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?<![\w_])op=(\w+)"))
                    Assert.True(allOps.Contains(m.Groups[1].Value), $"op={m.Groups[1].Value} is not an omnitrader op");
            }
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
