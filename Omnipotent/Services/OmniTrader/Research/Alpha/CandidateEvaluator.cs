using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    /// <summary>
    /// The battery every alpha candidate faces, whoever wrote it: the same strategy across a basket of
    /// symbols at realistic costs with next-bar execution, a 2× cost stress, walk-forward on the main
    /// symbol when it has tunable inputs, and a deflated Sharpe that counts every candidate the lab has
    /// ever evaluated as a trial. The score is built so that one lucky symbol cannot carry a candidate.
    /// </summary>
    public sealed class CandidateEvaluator
    {
        private readonly StudioService studio;
        private readonly BacktestJobQueue backtests;
        private readonly BacktestJobRepository jobs;
        private readonly StudyRunner studies;
        private readonly StrategyRegistry registry;

        public CandidateEvaluator(StudioService studio, BacktestJobQueue backtests, BacktestJobRepository jobs, StudyRunner studies, StrategyRegistry registry)
        {
            this.studio = studio;
            this.backtests = backtests;
            this.jobs = jobs;
            this.studies = studies;
            this.registry = registry;
        }

        public async Task<CandidateEvaluation> EvaluateAsync(AlphaCandidate candidate, ResearchPaper? paper, AlphaSettings settings,
            IReadOnlyList<double> priorPerPeriodSharpes, CancellationToken ct = default)
        {
            var eval = new CandidateEvaluation();
            var s = await studio.GetAsync(candidate.StrategyId, ct) ?? throw new InvalidOperationException("Strategy not found.");
            if (s.Status != StudioStatus.Valid) throw new InvalidOperationException("Strategy has compile errors.");
            candidate.StrategyVersion = s.Version;
            string className = $"{s.ClassName}@v{s.Version}";
            var descriptor = registry.Resolve(className) ?? throw new InvalidOperationException("Strategy could not be compiled.");
            bool universe = descriptor.RequiresUniverse;

            var symbols = PickSymbols(candidate, paper, s, settings, universe);
            candidate.TestSymbols = symbols;
            string primary = symbols[0];
            bool crypto = !MarketDataRouter.UsesEquityFeed(primary, AssetClass.Unknown);
            var costs = crypto ? CostPresets.All.First(c => c.Id == "binance") : CostPresets.All.First(c => c.Id == "us-broker");

            var request = new StudioRunRequest
            {
                Symbol = primary,
                Interval = candidate.Interval,
                Bars = settings.EvaluationBars,
                FeeFraction = costs.Fee,
                SlippageFraction = costs.Slippage,
                Execution = ExecutionTiming.NextOpen,
                Leverage = 1m,
                Label = $"alpha:{candidate.Id}",
            };
            var baseConfig = studio.BuildConfig(s, request, "alpha");

            // 1. Breadth: every test symbol (universe strategies are one portfolio, so one run).
            if (!universe && symbols.Count > 1)
            {
                var batch = await studies.ExecuteAsync(new StudyRequest { Kind = StudyKind.Batch, Base = baseConfig, Symbols = symbols }, ct);
                eval.PerSymbol = batch.Runs;
                eval.Batch = batch.Batch;
                foreach (var r in batch.Runs.Where(r => r.Error != null)) eval.Findings.Add($"{r.Symbol}: {r.Error}");
            }

            // 2. The primary run as a real, persisted backtest job so it shows in the Lab and can back an experiment.
            string jobId = await backtests.EnqueueAsync(baseConfig, ct);
            var job = await WaitForJobAsync(jobId, TimeSpan.FromMinutes(20), ct);
            if (job?.Result == null) throw new InvalidOperationException(job?.Error ?? "Primary backtest did not finish.");
            var primaryResult = job.Result;
            eval.Primary = RunMetrics.From(primaryResult);
            eval.Findings.Add($"primary job {jobId}");
            if (universe)
            {
                eval.PerSymbol = new List<StudyRun> { new() { Symbol = primary, Metrics = eval.Primary, Objective = eval.Primary.Sharpe } };
                eval.Batch = new BatchSummary { Symbols = 1, Profitable = eval.Primary.ReturnPct > 0 ? 1 : 0, MedianSharpe = eval.Primary.Sharpe, MedianReturnPct = eval.Primary.ReturnPct, BeatBenchmarkPct = eval.Primary.ReturnPct > eval.Primary.BenchmarkReturnPct ? 100 : 0 };
            }

            // 3. Cost stress: twice the fees and slippage.
            try
            {
                var stressed = CostSensitivity.ScaleCosts(baseConfig, 2m);
                eval.PrimaryStressed = RunMetrics.From(await backtests.RunAsync(stressed, ct: ct));
            }
            catch (Exception ex) { eval.Findings.Add("cost stress failed: " + ex.Message); }

            // 4. Walk-forward over the strategy's own tunable inputs (if it has any).
            var sweep = descriptor.Parameters
                .Where(p => p.Type is "int" or "double" or "decimal" && p.Min.HasValue && p.Max.HasValue && p.Max > p.Min && p.Name != "TradeSymbol")
                .Take(2)
                .Select(p => new SweepParam
                {
                    Name = p.Name, Integer = p.Type == "int",
                    Values = new[] { 0.25, 0.5, 0.75 }.Select(f => p.Type == "int" ? Math.Round(p.Min!.Value + f * (p.Max!.Value - p.Min.Value)) : p.Min!.Value + f * (p.Max!.Value - p.Min.Value)).Distinct().ToList()
                }).ToList();
            int trialsThisCandidate = 1;
            if (sweep.Count > 0 && primaryResult.TotalCandles >= 600)
            {
                try
                {
                    int bars = primaryResult.TotalCandles;
                    var wf = await studies.ExecuteAsync(new StudyRequest
                    {
                        Kind = StudyKind.WalkForward, Base = baseConfig, Params = sweep, Objective = "sharpe", MinTrades = 3,
                        InSampleBars = Math.Max(250, bars / 2), OutOfSampleBars = Math.Max(60, bars / 8)
                    }, ct);
                    eval.OutOfSample = wf.OutOfSample;
                    eval.WalkForwardEfficiency = wf.WalkForwardEfficiency;
                    trialsThisCandidate = Math.Max(1, wf.Trials);
                }
                catch (Exception ex) { eval.Findings.Add("walk-forward failed: " + ex.Message); }
            }
            else if (sweep.Count == 0) eval.Findings.Add("no tunable inputs: walk-forward skipped (in-sample evidence only)");

            // 5. Deflated Sharpe: every candidate ever evaluated is a trial.
            var curve = primaryResult.EquityCurve;
            int trials = priorPerPeriodSharpes.Count + trialsThisCandidate;
            double bpy = Math.Max(1, primaryResult.BarsPerYear);
            eval.PerPeriodSharpe = eval.Primary.Sharpe / Math.Sqrt(bpy);
            var sharpes = priorPerPeriodSharpes.Append(eval.PerPeriodSharpe.Value).ToList();
            double sd = sharpes.Count > 1 ? Std(sharpes) : 0.5 / Math.Sqrt(bpy);
            var dsr = DeflatedSharpe.Compute(DeflatedSharpe.PeriodReturns(curve), trials, sd);
            eval.DeflatedSharpe = Math.Round(dsr.Dsr, 4);
            eval.TrialsAccounted = trials;

            eval.ScoreParts = ScoreParts(eval);
            eval.FinishedUtc = DateTime.UtcNow;
            Describe(eval);
            return eval;
        }

        public static double Score(CandidateEvaluation e) => Math.Round(Math.Clamp(e.ScoreParts.Values.Sum(), 0, 100), 1);

        private static Dictionary<string, double> ScoreParts(CandidateEvaluation e)
        {
            var parts = new Dictionary<string, double>();
            double medianSharpe = e.Batch?.MedianSharpe ?? e.Primary?.Sharpe ?? 0;
            parts["breadth sharpe"] = Math.Round(Math.Clamp(medianSharpe / 1.5, 0, 1) * 28, 2);
            double profitableShare = e.Batch is { Symbols: > 0 } b ? (double)b.Profitable / b.Symbols : (e.Primary?.ReturnPct > 0 ? 1 : 0);
            parts["breadth profitable"] = Math.Round(profitableShare * 14, 2);
            parts["beats benchmark"] = Math.Round((e.Batch?.BeatBenchmarkPct ?? 0) / 100 * 6, 2);
            double oos = e.OutOfSample?.Sharpe ?? (e.Primary?.Sharpe ?? 0) * 0.5;
            parts["out of sample"] = Math.Round(Math.Clamp(oos / 1.0, 0, 1) * 18, 2);
            parts["deflated sharpe"] = Math.Round((e.DeflatedSharpe ?? 0) * 16, 2);
            parts["cost robustness"] = e.PrimaryStressed == null ? 0 : e.PrimaryStressed.ReturnPct > 0 ? 10 : e.PrimaryStressed.ReturnPct > -5 ? 4 : 0;
            parts["trade count"] = Math.Round(Math.Min(1, (e.Primary?.Trades ?? 0) / 40.0) * 8, 2);
            double dd = e.Primary?.MaxDrawdownPct ?? 100;
            parts["drawdown"] = -Math.Round(Math.Clamp((dd - 30) / 40, 0, 1) * 12, 2);
            return parts;
        }

        private static void Describe(CandidateEvaluation e)
        {
            if (e.Primary != null) e.Findings.Insert(0, $"primary: {e.Primary.ReturnPct:F1}% return, Sharpe {e.Primary.Sharpe:F2}, max DD {e.Primary.MaxDrawdownPct:F1}%, {e.Primary.Trades} trades");
            if (e.Batch != null) e.Findings.Add($"breadth: {e.Batch.Profitable}/{e.Batch.Symbols} profitable, median Sharpe {e.Batch.MedianSharpe:F2}");
            if (e.PrimaryStressed != null) e.Findings.Add($"2× costs: {e.PrimaryStressed.ReturnPct:F1}% return");
            if (e.OutOfSample != null) e.Findings.Add($"walk-forward OOS: Sharpe {e.OutOfSample.Sharpe:F2}, return {e.OutOfSample.ReturnPct:F1}%" + (e.WalkForwardEfficiency is { } wfe ? $", efficiency {wfe:F2}" : ""));
            e.Findings.Add($"deflated Sharpe {e.DeflatedSharpe:F2} over {e.TrialsAccounted} trials");
        }

        private static List<string> PickSymbols(AlphaCandidate c, ResearchPaper? paper, StudioStrategy s, AlphaSettings settings, bool universe)
        {
            if (universe) return new List<string> { s.Blueprint?.Universe.RegimeSymbol ?? s.DefaultSymbol };
            var explicitSymbols = c.TestSymbols.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToList();
            if (explicitSymbols.Count > 0) return explicitSymbols.Take(12).ToList();
            var assets = paper?.AssetClasses ?? new List<string>();
            bool defaultCrypto = !MarketDataRouter.UsesEquityFeed(s.DefaultSymbol, AssetClass.Unknown);
            List<string> pick;
            if (assets.Contains("crypto") && !assets.Contains("equities")) pick = settings.CryptoTestSymbols;
            else if (assets.Contains("equities") && !assets.Contains("crypto")) pick = settings.EquityTestSymbols;
            else pick = defaultCrypto ? settings.CryptoTestSymbols : settings.EquityTestSymbols;
            var list = new List<string> { s.DefaultSymbol.ToUpperInvariant() };
            list.AddRange(pick.Where(p => !string.Equals(p, s.DefaultSymbol, StringComparison.OrdinalIgnoreCase)));
            return list.Take(8).ToList();
        }

        private async Task<BacktestJobRow?> WaitForJobAsync(string jobId, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var job = await jobs.GetAsync(jobId, ct);
                if (job != null && job.Status is BacktestJobStatus.Succeeded or BacktestJobStatus.Failed or BacktestJobStatus.Cancelled) return job;
                await Task.Delay(1000, ct);
            }
            return await jobs.GetAsync(jobId, ct);
        }

        private static double Std(IReadOnlyList<double> x)
        {
            if (x.Count < 2) return 0;
            double m = x.Average();
            return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / (x.Count - 1));
        }
    }
}
