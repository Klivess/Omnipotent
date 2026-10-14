using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniTrader.Backtesting.Studies
{
    public enum StudyKind { Sweep, WalkForward, MonteCarlo, Batch }
    public enum StudyStatus { Queued, Running, Done, Failed, Cancelled }

    public sealed class SweepParam
    {
        public required string Name { get; set; }
        /// <summary>Explicit values; when empty, Min..Max by Step.</summary>
        public List<double> Values { get; set; } = new();
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }
        public bool Integer { get; set; }

        public List<double> Expand(int cap = 50)
        {
            if (Values.Count > 0) return Values.Take(cap).ToList();
            if (!Min.HasValue || !Max.HasValue || Max < Min) return new List<double>();
            double step = Step is > 0 ? Step.Value : (Max.Value - Min.Value) / 4;
            if (step <= 0) return new List<double> { Min.Value };
            var list = new List<double>();
            for (double v = Min.Value; v <= Max.Value + step * 1e-9 && list.Count < cap; v += step)
                list.Add(Integer ? Math.Round(v) : Math.Round(v, 10));
            return list.Distinct().ToList();
        }
    }

    public sealed class StudyRequest
    {
        public StudyKind Kind { get; set; } = StudyKind.Sweep;
        public required BacktestConfig Base { get; set; }
        public List<SweepParam> Params { get; set; } = new();
        /// <summary>sharpe | sortino | return | cagr | calmar | profitfactor | expectancy</summary>
        public string Objective { get; set; } = "sharpe";
        /// <summary>grid | random</summary>
        public string Method { get; set; } = "grid";
        public int MaxRuns { get; set; } = 150;
        public int Seed { get; set; } = 7;
        public int MinTrades { get; set; } = 5;
        // walk-forward
        public int InSampleBars { get; set; } = 500;
        public int OutOfSampleBars { get; set; } = 120;
        // monte carlo
        public string? SourceJobId { get; set; }
        public int Iterations { get; set; } = 1000;
        public double RuinDrawdownPct { get; set; } = 50;
        // batch
        public List<string> Symbols { get; set; } = new();
        public string? Universe { get; set; }
        public string? Label { get; set; }
    }

    public sealed class RunMetrics
    {
        public double ReturnPct { get; set; }
        public double CagrPct { get; set; }
        public double Sharpe { get; set; }
        public double Sortino { get; set; }
        public double MaxDrawdownPct { get; set; }
        public double Calmar { get; set; }
        public double ProfitFactor { get; set; }
        public double WinRatePct { get; set; }
        public double ExpectancyPct { get; set; }
        public int Trades { get; set; }
        public double ExposurePct { get; set; }
        public double BenchmarkReturnPct { get; set; }

        public static RunMetrics From(BacktestResult r)
        {
            var a = r.Analysis;
            return new RunMetrics
            {
                ReturnPct = Math.Round((double)r.TotalPnLPercent, 4),
                CagrPct = Math.Round(a?.CagrPct ?? (double)r.AnnualizedReturnPercent, 4),
                Sharpe = Math.Round(a?.Sharpe ?? (double)r.SharpeRatio, 4),
                Sortino = Math.Round(a?.Sortino ?? 0, 4),
                MaxDrawdownPct = Math.Round((double)r.MaxDrawdownPercent, 4),
                Calmar = Math.Round(a?.Calmar ?? 0, 4),
                ProfitFactor = Math.Round(Math.Min(999, a?.ProfitFactor ?? (double)r.ProfitFactor), 4),
                WinRatePct = Math.Round((double)r.WinRate, 2),
                ExpectancyPct = Math.Round(a?.ExpectancyPct ?? 0, 4),
                Trades = r.TotalTrades,
                ExposurePct = Math.Round(a?.ExposurePct ?? 0, 2),
                BenchmarkReturnPct = Math.Round(a?.BenchmarkReturnPct ?? (double)r.BuyAndHoldPnLPercent, 4),
            };
        }

        public double Objective(string objective) => objective switch
        {
            "return" => ReturnPct,
            "cagr" => CagrPct,
            "sortino" => Sortino,
            "calmar" => Calmar,
            "profitfactor" => ProfitFactor,
            "expectancy" => ExpectancyPct,
            _ => Sharpe
        };
    }

    public sealed class StudyRun
    {
        public Dictionary<string, double> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Symbol { get; set; }
        public RunMetrics? Metrics { get; set; }
        public double Objective { get; set; }
        public string? Error { get; set; }
        public List<SeriesPoint>? Equity { get; set; }
    }

    public sealed class WalkForwardFold
    {
        public int Index { get; set; }
        public DateTime InSampleFrom { get; set; }
        public DateTime InSampleTo { get; set; }
        public DateTime OutOfSampleFrom { get; set; }
        public DateTime OutOfSampleTo { get; set; }
        public Dictionary<string, double> BestParams { get; set; } = new();
        public double InSampleObjective { get; set; }
        public RunMetrics? InSample { get; set; }
        public RunMetrics? OutOfSample { get; set; }
    }

    public sealed class StudyResult
    {
        public List<StudyRun> Runs { get; set; } = new();
        public StudyRun? Best { get; set; }
        public int Trials { get; set; }
        public double? DeflatedSharpe { get; set; }
        public double? ExpectedMaxSharpe { get; set; }
        /// <summary>For two-parameter sweeps: X values, Y values and objective[y][x].</summary>
        public HeatmapData? Heatmap { get; set; }
        /// <summary>Spread of the objective across each parameter's values (larger = more sensitive).</summary>
        public Dictionary<string, double> Sensitivity { get; set; } = new();
        public List<WalkForwardFold> Folds { get; set; } = new();
        public RunMetrics? OutOfSample { get; set; }
        public List<SeriesPoint> OutOfSampleEquity { get; set; } = new();
        public double? WalkForwardEfficiency { get; set; }
        public MonteCarloResult? MonteCarlo { get; set; }
        public BatchSummary? Batch { get; set; }
    }

    public sealed class HeatmapData
    {
        public required string X { get; init; }
        public required string Y { get; init; }
        public List<double> XValues { get; init; } = new();
        public List<double> YValues { get; init; } = new();
        public List<List<double?>> Z { get; init; } = new();
    }

    public sealed class MonteCarloResult
    {
        public int Iterations { get; set; }
        public int Trades { get; set; }
        public Dictionary<string, double> FinalReturnPct { get; set; } = new();
        public Dictionary<string, double> MaxDrawdownPct { get; set; } = new();
        public double ProbabilityOfLossPct { get; set; }
        public double ProbabilityOfRuinPct { get; set; }
        public double RuinThresholdPct { get; set; }
        public List<HistogramBin> ReturnHistogram { get; set; } = new();
        public List<HistogramBin> DrawdownHistogram { get; set; } = new();
        public List<List<double>> SamplePaths { get; set; } = new();
        public double ActualReturnPct { get; set; }
        public double ActualMaxDrawdownPct { get; set; }
    }

    public sealed class BatchSummary
    {
        public int Symbols { get; set; }
        public int Profitable { get; set; }
        public double MedianSharpe { get; set; }
        public double MedianReturnPct { get; set; }
        public double BeatBenchmarkPct { get; set; }
    }

    public sealed class StudyRecord
    {
        public string Id { get; set; } = "sd_" + Guid.NewGuid().ToString("N")[..12];
        public StudyKind Kind { get; set; }
        public StudyStatus Status { get; set; } = StudyStatus.Queued;
        public string StrategyClass { get; set; } = "";
        public string? Label { get; set; }
        public string? Origin { get; set; }
        public required StudyRequest Request { get; set; }
        public StudyResult? Result { get; set; }
        public double Progress { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public string? Error { get; set; }
        public bool CancelRequested { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
    }

    /// <summary>
    /// Runs studies — many backtests of one idea — on one worker, fanning each study's runs out across
    /// cores over a single shared data fetch. Everything uses the one backtest engine.
    /// </summary>
    public sealed class StudyRunner
    {
        private readonly DocumentStore<StudyRecord> store;
        private readonly BacktestJobQueue backtests;
        private readonly BacktestJobRepository jobs;
        private readonly Func<string, CancellationToken, Task<List<string>>> universeResolver;
        private readonly Action<string, Exception?> err;
        private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
        private Task? worker;
        public int Parallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        public event Func<StudyRecord, Task>? Completed;

        public StudyRunner(OmniTraderDb db, BacktestJobQueue backtests, BacktestJobRepository jobs,
            Func<string, CancellationToken, Task<List<string>>> universeResolver, Action<string, Exception?> err)
        {
            store = new DocumentStore<StudyRecord>(db, "studies", s => s.Id,
                s => new DocumentKeys(K1: s.StrategyClass, K2: s.Status.ToString(), T1: s.CreatedUtc));
            this.backtests = backtests;
            this.jobs = jobs;
            this.universeResolver = universeResolver;
            this.err = err;
        }

        public void Start(CancellationToken ct)
        {
            if (worker != null) return;
            worker = Task.Run(async () =>
            {
                await foreach (var id in queue.Reader.ReadAllAsync(ct))
                {
                    try { await RunAsync(id, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { err($"study {id} failed", ex); }
                }
            }, ct);
        }

        public async Task RestoreAsync(CancellationToken ct = default)
        {
            foreach (var s in await store.QueryAsync(new DocumentQuery { K2In = new[] { "Queued", "Running" }, Limit = 200 }, ct))
            {
                if (s.Status == StudyStatus.Running) { s.Status = StudyStatus.Failed; s.Error = "interrupted by restart"; s.FinishedUtc = DateTime.UtcNow; await store.UpsertAsync(s, ct); }
                else queue.Writer.TryWrite(s.Id);
            }
        }

        public async Task<StudyRecord> EnqueueAsync(StudyRequest request, string origin, CancellationToken ct = default)
        {
            var record = new StudyRecord
            {
                Kind = request.Kind, Request = request, StrategyClass = request.Base.StrategyClass,
                Label = request.Label ?? request.Base.Label, Origin = origin
            };
            await store.UpsertAsync(record, ct);
            queue.Writer.TryWrite(record.Id);
            return record;
        }

        public Task<StudyRecord?> GetAsync(string id, CancellationToken ct = default) => store.GetAsync(id, ct);

        public Task<List<StudyRecord>> ListAsync(string? strategyClass = null, int limit = 100, CancellationToken ct = default)
            => store.QueryAsync(new DocumentQuery { K1 = strategyClass, OrderBy = "t1", Limit = limit }, ct);

        public async Task<bool> CancelAsync(string id, CancellationToken ct = default)
        {
            var s = await store.GetAsync(id, ct);
            if (s == null) return false;
            s.CancelRequested = true;
            if (s.Status == StudyStatus.Queued) { s.Status = StudyStatus.Cancelled; s.FinishedUtc = DateTime.UtcNow; }
            await store.UpsertAsync(s, ct);
            if (running.TryGetValue(id, out var cts)) cts.Cancel();
            return true;
        }

        private async Task RunAsync(string id, CancellationToken ct)
        {
            var s = await store.GetAsync(id, ct);
            if (s == null || s.Status != StudyStatus.Queued || s.CancelRequested) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            running[id] = cts;
            s.Status = StudyStatus.Running;
            s.StartedUtc = DateTime.UtcNow;
            await store.UpsertAsync(s, ct);
            var lastSave = DateTime.UtcNow;
            async Task Progress(int done, int total)
            {
                s.Completed = done;
                s.Total = total;
                s.Progress = total == 0 ? 0 : Math.Round(done * 100.0 / total, 1);
                if (DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(2))
                {
                    lastSave = DateTime.UtcNow;
                    try { await store.UpsertAsync(s, CancellationToken.None); } catch { }
                }
            }

            try
            {
                s.Result = s.Kind switch
                {
                    StudyKind.Sweep => await SweepAsync(s.Request, Progress, cts.Token),
                    StudyKind.WalkForward => await WalkForwardAsync(s.Request, Progress, cts.Token),
                    StudyKind.MonteCarlo => await MonteCarloAsync(s.Request, Progress, cts.Token),
                    StudyKind.Batch => await BatchAsync(s.Request, Progress, cts.Token),
                    _ => throw new NotSupportedException(s.Kind.ToString())
                };
                s.Status = StudyStatus.Done;
                s.Progress = 100;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                s.Status = StudyStatus.Cancelled;
            }
            catch (Exception ex)
            {
                s.Status = StudyStatus.Failed;
                s.Error = ex.Message;
            }
            finally
            {
                running.TryRemove(id, out _);
                s.FinishedUtc = DateTime.UtcNow;
                await store.UpsertAsync(s, CancellationToken.None);
            }
            if (Completed != null)
                foreach (Func<StudyRecord, Task> h in Completed.GetInvocationList())
                {
                    try { await h(s); } catch (Exception ex) { err("study completion handler failed", ex); }
                }
        }

        /// <summary>Run a study inline (no record, no queue) — for internal callers such as the alpha evaluator.</summary>
        public Task<StudyResult> ExecuteAsync(StudyRequest req, CancellationToken ct = default)
        {
            Task NoProgress(int done, int total) => Task.CompletedTask;
            return req.Kind switch
            {
                StudyKind.Sweep => SweepAsync(req, NoProgress, ct),
                StudyKind.WalkForward => WalkForwardAsync(req, NoProgress, ct),
                StudyKind.MonteCarlo => MonteCarloAsync(req, NoProgress, ct),
                StudyKind.Batch => BatchAsync(req, NoProgress, ct),
                _ => throw new NotSupportedException(req.Kind.ToString())
            };
        }

        // ── parameter sets ───────────────────────────────────────────────────────

        public static List<Dictionary<string, double>> Combinations(StudyRequest req)
        {
            var axes = req.Params.Select(p => (p.Name, Values: p.Expand())).Where(a => a.Values.Count > 0).ToList();
            if (axes.Count == 0) return new List<Dictionary<string, double>> { new() };
            long total = axes.Aggregate(1L, (acc, a) => acc * a.Values.Count);
            int cap = Math.Clamp(req.MaxRuns, 1, 2000);
            var combos = new List<Dictionary<string, double>>();
            if (req.Method == "random" || total > cap)
            {
                var rng = new Random(req.Seed);
                var seen = new HashSet<string>();
                int attempts = 0;
                while (combos.Count < Math.Min(cap, total) && attempts++ < cap * 20)
                {
                    var c = axes.ToDictionary(a => a.Name, a => a.Values[rng.Next(a.Values.Count)], StringComparer.OrdinalIgnoreCase);
                    if (seen.Add(string.Join("|", c.Select(kv => kv.Value.ToString("R", CultureInfo.InvariantCulture))))) combos.Add(c);
                }
                return combos;
            }
            combos.Add(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
            foreach (var (name, values) in axes)
                combos = combos.SelectMany(c => values.Select(v => new Dictionary<string, double>(c, StringComparer.OrdinalIgnoreCase) { [name] = v })).ToList();
            return combos;
        }

        private static BacktestConfig WithParams(BacktestConfig c, IReadOnlyDictionary<string, double> overrides, string? symbol = null,
            DateTime? from = null, DateTime? to = null)
        {
            var pars = new Dictionary<string, object?>(c.Parameters ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in overrides) pars[k] = v;
            if (symbol != null) pars["TradeSymbol"] = symbol;
            return new BacktestConfig
            {
                StrategyClass = c.StrategyClass, Coin = symbol ?? c.Coin, Currency = symbol != null ? "" : c.Currency, Interval = c.Interval,
                CandleCount = c.CandleCount, FromUtc = from ?? c.FromUtc, ToUtc = to ?? c.ToUtc,
                InitialQuoteBalance = c.InitialQuoteBalance, InitialBaseBalance = c.InitialBaseBalance,
                FeeFraction = c.FeeFraction, SlippageFraction = c.SlippageFraction, Margin = c.Margin,
                Execution = c.Execution, Benchmark = c.Benchmark, Parameters = pars, Label = c.Label, Origin = "study"
            };
        }

        private static PreparedData Slice(PreparedData data, DateTime from, DateTime to) => new()
        {
            Candles = data.Candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<OHLCCandle>)kv.Value.Where(c => c.Timestamp >= from && c.Timestamp <= to).ToList(), StringComparer.OrdinalIgnoreCase),
            RegimeSymbol = data.RegimeSymbol,
            IsUniverse = data.IsUniverse,
            PrimarySymbol = data.PrimarySymbol
        };

        private async Task<List<StudyRun>> RunManyAsync(BacktestConfig baseConfig, PreparedData data, List<Dictionary<string, double>> combos,
            string objective, int minTrades, Func<int, int, Task> progress, CancellationToken ct, bool keepEquity = false)
        {
            var results = new StudyRun[combos.Count];
            int done = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, combos.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (i, token) =>
            {
                var cfg = WithParams(baseConfig, combos[i]);
                var run = new StudyRun { Params = combos[i] };
                try
                {
                    var r = await backtests.RunPreparedAsync(cfg, data, null, null, null, token);
                    run.Metrics = RunMetrics.From(r);
                    run.Objective = run.Metrics.Trades >= minTrades ? run.Metrics.Objective(objective) : double.NegativeInfinity;
                    if (keepEquity) run.Equity = BacktestAnalyzer.Downsample(r.EquityCurve.Select(p => p.Ts).ToArray(), r.EquityCurve.Select(p => (double)p.Equity).ToArray(), 300);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { run.Error = ex.Message; run.Objective = double.NegativeInfinity; }
                results[i] = run;
                await progress(Interlocked.Increment(ref done), combos.Count);
            });
            return results.ToList();
        }

        // ── sweep ────────────────────────────────────────────────────────────────

        private async Task<StudyResult> SweepAsync(StudyRequest req, Func<int, int, Task> progress, CancellationToken ct)
        {
            var data = await backtests.PrepareAsync(req.Base, null, ct);
            var combos = Combinations(req);
            var runs = await RunManyAsync(req.Base, data, combos, req.Objective, req.MinTrades, progress, ct);
            var result = new StudyResult { Runs = runs.OrderByDescending(r => r.Objective).ToList(), Trials = runs.Count };
            var valid = runs.Where(r => r.Metrics != null && double.IsFinite(r.Objective)).ToList();
            result.Best = valid.OrderByDescending(r => r.Objective).FirstOrDefault();

            if (result.Best != null)
            {
                // Re-run the winner for its equity curve and the deflated Sharpe across all trials.
                var best = await backtests.RunPreparedAsync(WithParams(req.Base, result.Best.Params), data, null, null, null, ct);
                result.Best.Equity = BacktestAnalyzer.Downsample(best.EquityCurve.Select(p => p.Ts).ToArray(), best.EquityCurve.Select(p => (double)p.Equity).ToArray(), 600);
                var sharpes = valid.Select(v => v.Metrics!.Sharpe / Math.Sqrt(Math.Max(1, best.BarsPerYear))).ToList();
                double sd = sharpes.Count > 1 ? Math.Sqrt(sharpes.Sum(x => Math.Pow(x - sharpes.Average(), 2)) / (sharpes.Count - 1)) : 0;
                var dsr = DeflatedSharpe.Compute(DeflatedSharpe.PeriodReturns(best.EquityCurve), Math.Max(1, valid.Count), sd);
                result.DeflatedSharpe = Math.Round(dsr.Dsr, 4);
                result.ExpectedMaxSharpe = Math.Round(dsr.ExpectedMaxSharpe * Math.Sqrt(Math.Max(1, best.BarsPerYear)), 4);
            }

            foreach (var p in req.Params)
            {
                var groups = valid.GroupBy(r => r.Params.TryGetValue(p.Name, out var v) ? v : double.NaN)
                    .Where(g => !double.IsNaN(g.Key)).Select(g => g.Average(r => r.Objective)).ToList();
                if (groups.Count > 1) result.Sensitivity[p.Name] = Math.Round(groups.Max() - groups.Min(), 4);
            }

            if (req.Params.Count >= 2)
            {
                string x = req.Params[0].Name, y = req.Params[1].Name;
                var xs = runs.Select(r => r.Params.GetValueOrDefault(x)).Distinct().OrderBy(v => v).ToList();
                var ys = runs.Select(r => r.Params.GetValueOrDefault(y)).Distinct().OrderBy(v => v).ToList();
                var z = ys.Select(yv => xs.Select(xv =>
                {
                    var cell = valid.Where(r => r.Params.GetValueOrDefault(x) == xv && r.Params.GetValueOrDefault(y) == yv).ToList();
                    return cell.Count == 0 ? (double?)null : Math.Round(cell.Average(r => r.Objective), 4);
                }).ToList()).ToList();
                result.Heatmap = new HeatmapData { X = x, Y = y, XValues = xs, YValues = ys, Z = z };
            }
            foreach (var r in result.Runs.Where(r => !double.IsFinite(r.Objective))) r.Objective = 0;
            return result;
        }

        // ── walk-forward ─────────────────────────────────────────────────────────

        private async Task<StudyResult> WalkForwardAsync(StudyRequest req, Func<int, int, Task> progress, CancellationToken ct)
        {
            var data = await backtests.PrepareAsync(req.Base, null, ct);
            var timeline = data.Candles.TryGetValue(data.PrimarySymbol, out var primary)
                ? primary.Select(c => c.Timestamp).ToList()
                : data.Candles.Values.SelectMany(v => v.Select(c => c.Timestamp)).Distinct().OrderBy(t => t).ToList();
            int isBars = Math.Max(50, req.InSampleBars), oosBars = Math.Max(10, req.OutOfSampleBars);
            if (timeline.Count < isBars + oosBars) throw new InvalidOperationException($"Need {isBars + oosBars} bars, have {timeline.Count}.");

            var combos = Combinations(req);
            var folds = new List<WalkForwardFold>();
            int foldCount = (timeline.Count - isBars) / oosBars;
            int total = foldCount * (combos.Count + 1), done = 0;
            var stitched = new List<SeriesPoint>();
            double oosEquity = (double)req.Base.InitialQuoteBalance;
            var oosReturns = new List<double>();
            double isAnnual = 0, oosAnnual = 0;

            for (int f = 0; f < foldCount; f++)
            {
                ct.ThrowIfCancellationRequested();
                int isStart = f * oosBars, isEnd = isStart + isBars - 1, oosEnd = Math.Min(timeline.Count - 1, isEnd + oosBars);
                var isFrom = timeline[isStart]; var isTo = timeline[isEnd];
                var oosFrom = timeline[isEnd + 1]; var oosTo = timeline[oosEnd];
                var isData = Slice(data, isFrom, isTo);

                var runs = await RunManyAsync(req.Base, isData, combos, req.Objective, req.MinTrades,
                    async (d, _) => await progress(done + d, total), ct);
                done += combos.Count;
                var best = runs.Where(r => r.Metrics != null && double.IsFinite(r.Objective)).OrderByDescending(r => r.Objective).FirstOrDefault()
                           ?? runs.First();

                // The out-of-sample window keeps the in-sample history as warm-up, but only its own bars count.
                var oosData = Slice(data, isFrom, oosTo);
                var oosRun = await backtests.RunPreparedAsync(WithParams(req.Base, best.Params), oosData, null, null, null, ct);
                var oosCurve = oosRun.EquityCurve.Where(p => p.Ts >= oosFrom).ToList();
                double startEq = oosCurve.Count > 0 ? (double)oosCurve[0].Equity : 1;
                RunMetrics oosMetrics = OosMetrics(oosCurve, oosRun);
                for (int k = 1; k < oosCurve.Count; k++)
                {
                    double r = (double)oosCurve[k - 1].Equity > 0 ? (double)oosCurve[k].Equity / (double)oosCurve[k - 1].Equity - 1 : 0;
                    oosReturns.Add(r);
                    oosEquity *= 1 + r;
                    stitched.Add(new SeriesPoint(oosCurve[k].Ts, Math.Round(oosEquity, 2)));
                }
                folds.Add(new WalkForwardFold
                {
                    Index = f + 1, InSampleFrom = isFrom, InSampleTo = isTo, OutOfSampleFrom = oosFrom, OutOfSampleTo = oosTo,
                    BestParams = best.Params, InSampleObjective = double.IsFinite(best.Objective) ? Math.Round(best.Objective, 4) : 0,
                    InSample = best.Metrics, OutOfSample = oosMetrics
                });
                isAnnual += best.Metrics?.CagrPct ?? 0;
                oosAnnual += oosMetrics.CagrPct;
                await progress(++done, total);
            }

            double bpy = Math.Max(1, MarketData.CandleClock.BarsPerYear(req.Base.Interval, !MarketData.MarketDataRouter.UsesEquityFeed(data.PrimarySymbol, Venues.AssetClass.Unknown)));
            double mean = oosReturns.Count > 0 ? oosReturns.Average() : 0;
            double sd = oosReturns.Count > 1 ? Math.Sqrt(oosReturns.Sum(r => (r - mean) * (r - mean)) / (oosReturns.Count - 1)) : 0;
            double startCapital = (double)req.Base.InitialQuoteBalance;
            double oosYears = stitched.Count > 1 ? Math.Max(1e-9, (stitched[^1].T - stitched[0].T).TotalDays / 365.25) : 1;
            double oosCagr = oosEquity > 0 && startCapital > 0 ? (Math.Pow(oosEquity / startCapital, 1 / oosYears) - 1) * 100 : -100;
            double oosDd = MaxDd(stitched.Select(p => p.V));
            var aggregate = new RunMetrics
            {
                ReturnPct = Math.Round((oosEquity / startCapital - 1) * 100, 4),
                CagrPct = Math.Round(oosCagr, 4),
                Sharpe = sd > 0 ? Math.Round(mean / sd * Math.Sqrt(bpy), 4) : 0,
                Sortino = Math.Round(Sortino(oosReturns, bpy), 4),
                MaxDrawdownPct = Math.Round(oosDd, 4),
                Calmar = oosDd > 0 ? Math.Round(oosCagr / oosDd, 4) : 0,
                Trades = folds.Sum(f => f.OutOfSample?.Trades ?? 0),
            };
            return new StudyResult
            {
                Folds = folds,
                OutOfSample = aggregate,
                OutOfSampleEquity = BacktestAnalyzer.Downsample(stitched.Select(p => p.T).ToArray(), stitched.Select(p => p.V).ToArray(), 800),
                WalkForwardEfficiency = isAnnual != 0 ? Math.Round(oosAnnual / isAnnual, 4) : null,
                Trials = combos.Count
            };
        }

        private static RunMetrics OosMetrics(List<EquityPoint> curve, BacktestResult run)
        {
            if (curve.Count < 2) return new RunMetrics();
            double start = (double)curve[0].Equity, end = (double)curve[^1].Equity;
            double years = Math.Max(1e-9, (curve[^1].Ts - curve[0].Ts).TotalDays / 365.25);
            var rets = new List<double>();
            for (int k = 1; k < curve.Count; k++) if (curve[k - 1].Equity > 0) rets.Add((double)(curve[k].Equity / curve[k - 1].Equity) - 1);
            double m = rets.Count > 0 ? rets.Average() : 0;
            double s = rets.Count > 1 ? Math.Sqrt(rets.Sum(r => (r - m) * (r - m)) / (rets.Count - 1)) : 0;
            var from = curve[0].Ts;
            double cagr = start > 0 && end > 0 ? (Math.Pow(end / start, 1 / years) - 1) * 100 : -100;
            double dd = MaxDd(curve.Select(p => (double)p.Equity));
            // Trades active in the window: a position carried in from the warm-up counts, it is OOS exposure.
            var active = run.Trades.Where(t => t.ExitTime >= from).ToList();
            double gains = active.Where(t => t.RealizedPnL > 0).Sum(t => (double)t.RealizedPnL);
            double losses = -active.Where(t => t.RealizedPnL < 0).Sum(t => (double)t.RealizedPnL);
            return new RunMetrics
            {
                ReturnPct = Math.Round((end / start - 1) * 100, 4),
                CagrPct = Math.Round(cagr, 4),
                Sharpe = s > 0 ? Math.Round(m / s * Math.Sqrt(Math.Max(1, run.BarsPerYear)), 4) : 0,
                Sortino = Math.Round(Sortino(rets, Math.Max(1, run.BarsPerYear)), 4),
                MaxDrawdownPct = Math.Round(dd, 4),
                Calmar = dd > 0 ? Math.Round(cagr / dd, 4) : 0,
                ProfitFactor = losses > 0 ? Math.Round(gains / losses, 4) : gains > 0 ? 99 : 0,
                WinRatePct = active.Count > 0 ? Math.Round(active.Count(t => t.RealizedPnL > 0) * 100.0 / active.Count, 2) : 0,
                Trades = active.Count,
            };
        }

        private static double Sortino(IReadOnlyList<double> returns, double barsPerYear)
        {
            if (returns.Count < 2) return 0;
            double mean = returns.Average();
            double downside = Math.Sqrt(returns.Sum(r => r < 0 ? r * r : 0) / returns.Count);
            return downside > 0 ? mean / downside * Math.Sqrt(barsPerYear) : 0;
        }

        private static double MaxDd(IEnumerable<double> equity)
        {
            double peak = double.MinValue, dd = 0;
            foreach (var e in equity)
            {
                if (e > peak) peak = e;
                if (peak > 0) dd = Math.Max(dd, (peak - e) / peak * 100);
            }
            return dd;
        }

        // ── Monte Carlo ──────────────────────────────────────────────────────────

        private async Task<StudyResult> MonteCarloAsync(StudyRequest req, Func<int, int, Task> progress, CancellationToken ct)
        {
            BacktestResult source;
            if (!string.IsNullOrWhiteSpace(req.SourceJobId))
            {
                var job = await jobs.GetAsync(req.SourceJobId!, ct) ?? throw new ArgumentException("Unknown backtest job");
                source = job.Result ?? throw new InvalidOperationException("That backtest has no result.");
            }
            else source = await backtests.RunAsync(req.Base, ct: ct);
            var mc = Simulate(source, Math.Clamp(req.Iterations, 100, 20_000), req.Seed, req.RuinDrawdownPct);
            await progress(1, 1);
            return new StudyResult { MonteCarlo = mc, Trials = mc.Iterations };
        }

        /// <summary>
        /// Resample the run's trades (as % returns on the equity at entry) with replacement, N times,
        /// and report the spread of final return and maximum drawdown. Order-dependent risk (drawdown)
        /// varies; the sum of trade outcomes is what is being stress-tested.
        /// </summary>
        public static MonteCarloResult Simulate(BacktestResult source, int iterations, int seed, double ruinPct)
        {
            var closed = source.Trades.Where(t => t.ExitReason != "open").ToList();
            var equityAt = source.EquityCurve.Select(p => (p.Ts, E: (double)p.Equity)).ToList();
            double EquityBefore(DateTime t)
            {
                double e = (double)source.InitialEquity;
                foreach (var p in equityAt) { if (p.Ts > t) break; e = p.E; }
                return e;
            }
            var returns = closed.Select(t => { double e = EquityBefore(t.EntryTime); return e > 0 ? (double)t.RealizedPnL / e : 0; }).ToList();
            var result = new MonteCarloResult
            {
                Iterations = iterations, Trades = returns.Count, RuinThresholdPct = ruinPct,
                ActualReturnPct = Math.Round((double)source.TotalPnLPercent, 4), ActualMaxDrawdownPct = Math.Round((double)source.MaxDrawdownPercent, 4)
            };
            if (returns.Count < 3) return result;

            var rng = new Random(seed);
            var finals = new double[iterations];
            var dds = new double[iterations];
            for (int it = 0; it < iterations; it++)
            {
                double eq = 1, peak = 1, dd = 0;
                List<double>? path = it < 25 ? new List<double>(returns.Count + 1) { 100 } : null;
                for (int k = 0; k < returns.Count; k++)
                {
                    eq *= 1 + returns[rng.Next(returns.Count)];
                    if (eq > peak) peak = eq;
                    dd = Math.Max(dd, (peak - eq) / peak);
                    path?.Add(Math.Round(eq * 100, 3));
                }
                finals[it] = (eq - 1) * 100;
                dds[it] = dd * 100;
                if (path != null) result.SamplePaths.Add(path);
            }
            Array.Sort(finals);
            Array.Sort(dds);
            foreach (var p in new[] { 5, 25, 50, 75, 95 })
            {
                result.FinalReturnPct[$"p{p}"] = Math.Round(BacktestAnalyzer.Percentile(finals, p / 100.0), 3);
                result.MaxDrawdownPct[$"p{p}"] = Math.Round(BacktestAnalyzer.Percentile(dds, p / 100.0), 3);
            }
            result.ProbabilityOfLossPct = Math.Round(finals.Count(f => f < 0) * 100.0 / iterations, 2);
            result.ProbabilityOfRuinPct = Math.Round(dds.Count(d => d >= ruinPct) * 100.0 / iterations, 2);
            result.ReturnHistogram = BacktestAnalyzer.Histogram(finals, 40);
            result.DrawdownHistogram = BacktestAnalyzer.Histogram(dds, 30);
            return result;
        }

        // ── batch ────────────────────────────────────────────────────────────────

        private async Task<StudyResult> BatchAsync(StudyRequest req, Func<int, int, Task> progress, CancellationToken ct)
        {
            var symbols = req.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToList();
            if (!string.IsNullOrWhiteSpace(req.Universe)) symbols.AddRange(await universeResolver(req.Universe!, ct));
            symbols = symbols.Distinct().Take(300).ToList();
            if (symbols.Count == 0) throw new ArgumentException("No symbols.");
            var runs = new StudyRun[symbols.Count];
            int done = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, symbols.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Parallelism, 6), CancellationToken = ct }, async (i, token) =>
            {
                var run = new StudyRun { Symbol = symbols[i] };
                try
                {
                    var cfg = WithParams(req.Base, new Dictionary<string, double>(), symbols[i]);
                    var r = await backtests.RunAsync(cfg, ct: token);
                    run.Metrics = RunMetrics.From(r);
                    run.Objective = run.Metrics.Objective(req.Objective);
                    run.Equity = BacktestAnalyzer.Downsample(r.EquityCurve.Select(p => p.Ts).ToArray(), r.EquityCurve.Select(p => (double)p.Equity).ToArray(), 200);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { run.Error = ex.Message; }
                runs[i] = run;
                await progress(Interlocked.Increment(ref done), symbols.Count);
            });
            var ok = runs.Where(r => r.Metrics != null).ToList();
            double Median(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
            return new StudyResult
            {
                Runs = runs.OrderByDescending(r => r.Objective).ToList(),
                Trials = runs.Length,
                Batch = new BatchSummary
                {
                    Symbols = runs.Length,
                    Profitable = ok.Count(r => r.Metrics!.ReturnPct > 0),
                    MedianSharpe = Math.Round(Median(ok.Select(r => r.Metrics!.Sharpe)), 4),
                    MedianReturnPct = Math.Round(Median(ok.Select(r => r.Metrics!.ReturnPct)), 4),
                    BeatBenchmarkPct = ok.Count == 0 ? 0 : Math.Round(ok.Count(r => r.Metrics!.ReturnPct > r.Metrics.BenchmarkReturnPct) * 100.0 / ok.Count, 1)
                }
            };
        }
    }

    /// <summary>Fee and slippage presets matching each venue's published retail schedule.</summary>
    public static class CostPresets
    {
        public sealed record Preset(string Id, string Name, decimal Fee, decimal Slippage, string Note);

        public static readonly Preset[] All =
        {
            new("binance", "Binance spot", 0.0010m, 0.0005m, "0.10% taker"),
            new("binance-bnb", "Binance spot (BNB fees)", 0.00075m, 0.0005m, "0.075% with BNB discount"),
            new("kraken", "Kraken spot", 0.0026m, 0.0005m, "0.26% taker (entry tier)"),
            new("kraken-maker", "Kraken spot (maker)", 0.0016m, 0.0002m, "0.16% maker, limit orders"),
            new("coinbase", "Coinbase Advanced", 0.0060m, 0.0005m, "0.60% taker (entry tier)"),
            new("ig-cfd", "IG CFD (indices/FX)", 0.0000m, 0.0004m, "spread-only, ~4 bps"),
            new("t212", "Trading 212 Invest", 0.0000m, 0.0015m, "no commission; 0.15% FX on non-GBP"),
            new("us-broker", "US broker (zero commission)", 0.0000m, 0.0003m, "~3 bps spread/impact"),
            new("institutional", "Institutional", 0.0002m, 0.0002m, "2 bps fee, 2 bps impact"),
            new("zero", "Frictionless", 0.0000m, 0.0000m, "no costs — research only"),
        };
    }
}
