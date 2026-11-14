using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>Runs only with OMNITRADER_DEPLOY_REPLAY=&lt;folder of captured kapi.py responses&gt; (never committed: live data).</summary>
    public sealed class DeployReplayFactAttribute : FactAttribute
    {
        public DeployReplayFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OMNITRADER_DEPLOY_REPLAY")))
                Skip = "Replay test; set OMNITRADER_DEPLOY_REPLAY to a folder of captured /api/omnitrader responses.";
        }
    }

    /// <summary>The deploy planner and health rules against payloads captured from the live server.</summary>
    public class DeployReplayTests
    {
        private readonly ITestOutputHelper output;
        private static string Dir => Environment.GetEnvironmentVariable("OMNITRADER_DEPLOY_REPLAY")!;

        public DeployReplayTests(ITestOutputHelper output) { this.output = output; }

        private static StrategyDescriptor StandIn(string className, bool basket) => new()
        {
            Name = className, Description = "", ClassName = className, Source = "script",
            Type = basket ? typeof(TestEtfBasketStrategy) : typeof(TestBuyOnceStrategy),
            RequiresUniverse = basket,
            Parameters = StrategyParams.For(basket ? typeof(TestEtfBasketStrategy) : typeof(TestBuyOnceStrategy)),
            Factory = basket ? () => new TestEtfBasketStrategy() : () => new TestBuyOnceStrategy(),
        };

        [DeployReplayFact]
        public void Every_captured_candidate_plans_on_the_settings_it_was_tested_on()
        {
            var files = Directory.GetFiles(Dir, "GET_api_omnitrader_alpha_candidate_id_*.json");
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                var c = JsonConvert.DeserializeObject<AlphaCandidate>(File.ReadAllText(f))!;
                bool basket = c.StrategyName.Contains("XSMOM", StringComparison.OrdinalIgnoreCase);
                var d = StandIn($"studio:{c.StrategyId}@v{c.StrategyVersion}", basket);
                var plan = DeployPlanner.Build(new DeployPlanRequest { CandidateId = c.Id }, new DeployPlanInputs
                {
                    Descriptor = d,
                    Create = p => { var s = d.Factory!(); StrategyParams.Apply(s, new Dictionary<string, object?>(p, StringComparer.OrdinalIgnoreCase)); return s; },
                    Candidate = c, EvaluationBars = 1500, KrakenConfigured = true,
                });
                output.WriteLine($"{c.Id} {c.Status} {c.Interval} [{string.Join(",", c.TestSymbols)}] -> {plan.Request.Symbol} {plan.Request.Interval} {plan.Costs.Preset} {plan.Verdict}: {plan.VerdictTitle}; "
                    + string.Join(" | ", plan.Checks.Select(x => $"{x.Level}:{x.Id}")) + $"; expect {plan.Expect.TradesPerMonth}/mo");
                Assert.Equal(c.Interval.ToString(), plan.Request.Interval);
                Assert.EndsWith($"@v{c.StrategyVersion}", plan.Request.StrategyClass);
                if (!basket) Assert.Equal(c.TestSymbols[0], plan.Request.Symbol);
                Assert.Contains(plan.Checks, x => x.Id == "lab" && x.Level == (c.Status is CandidateStatus.Promising or CandidateStatus.Promoted ? "pass" : "warn"));
            }
        }

        [DeployReplayFact]
        public void The_live_flat_deployment_reads_quiet()
        {
            var f = Directory.GetFiles(Dir, "GET_api_omnitrader_deployment_id_caa814c0*.json").Single();
            var detail = JsonConvert.DeserializeAnonymousType(File.ReadAllText(f), new { Deployment = new { Id = "", StrategyClass = "", Config = (DeploymentConfig?)null, CreatedUtc = DateTime.MinValue }, Orders = new List<object>() })!;
            var equity = JsonConvert.DeserializeObject<List<EquityPoint>>(File.ReadAllText(Directory.GetFiles(Dir, "GET_api_omnitrader_deployment_equity_id_caa814c0*.json").Single()))!;
            var row = new DeploymentRow
            {
                Id = detail.Deployment.Id, StrategyClass = detail.Deployment.StrategyClass, Config = detail.Deployment.Config!,
                Mode = SessionMode.Paper, Status = DeploymentStatus.Running, CreatedUtc = detail.Deployment.CreatedUtc, EquityInitial = 10_000, EquityCurrent = 10_000,
            };
            var last = equity[^1].Ts;
            var pulse = new SessionPulse { Kind = "single", StartedUtc = detail.Deployment.CreatedUtc, LastBarUtc = last, BarsThisRun = equity.Count, BarsTotal = equity.Count };
            var h = DeploymentHealth.Assess(row, pulse, null, false, last.AddHours(1));
            output.WriteLine($"{equity.Count} bars, {detail.Orders.Count} orders -> {h.State}: {h.Title}");
            Assert.Empty(detail.Orders);
            Assert.Equal("quiet", h.State);
        }
    }
}
