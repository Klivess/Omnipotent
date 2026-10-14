using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.Agent;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class TerminalRoutes
    {
        private async Task RegisterLabAsync()
        {
            // Every runnable strategy, built-in and studio, with parameter schemas.
            await Get("/lab/strategies", OmniTraderPerms.StrategiesRead, async req =>
                await Json(req, parent.StrategyRegistry.All
                    .OrderBy(d => d.Source == "builtin" ? 1 : 0).ThenBy(d => d.Name)
                    .Select(d => new { d.Name, d.ClassName, d.Description, d.RequiresUniverse, d.Parameters, d.Source, d.StudioId, d.Version, d.Author })));

            await Get("/lab/backtests", OmniTraderPerms.BacktestsRead, async req =>
            {
                Enum.TryParse<BacktestJobStatus>(req.userParameters.Get("status"), true, out var status);
                var filter = new BacktestJobFilter
                {
                    StrategyClass = req.userParameters.Get("strategy"),
                    Origin = req.userParameters.Get("origin"),
                    Status = req.userParameters.Get("status") == null ? null : status,
                    Symbol = req.userParameters.Get("symbol"),
                    Text = req.userParameters.Get("q"),
                    Limit = ParseInt(req.userParameters.Get("limit"), 100, 1, 1000),
                    Offset = ParseInt(req.userParameters.Get("offset"), 0, 0, 1_000_000)
                };
                var (rows, total) = await parent.BacktestJobRepo.QuerySummariesAsync(filter);
                await Json(req, new
                {
                    Total = total,
                    Running = parent.BacktestQueue.Running,
                    Workers = parent.BacktestQueue.Workers,
                    Rows = rows.Select(LabRow)
                });
            });

            // The job in full with enums as names (the legacy /backtest route writes ordinals).
            await Get("/lab/backtest", OmniTraderPerms.BacktestsRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var job = await parent.BacktestJobRepo.GetAsync(id);
                if (job == null) { await NotFound(req); return; }
                await Json(req, JobDetail(job));
            });

            await Post("/lab/backtest", OmniTraderPerms.BacktestsRun, async req =>
            {
                var dto = Body<LabBacktestDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Strategy)) { await Bad(req, "strategy required"); return; }
                string jobId;
                if (IsStudioId(dto.Strategy))
                    jobId = await StudioOrThrow.QueueBacktestAsync(StudioIdOf(dto.Strategy), ToRunRequest(dto), "lab");
                else
                    jobId = await parent.BacktestQueue.EnqueueAsync(await BuildConfigAsync(dto, "lab"));
                await Json(req, new { JobId = jobId });
            });

            await Post("/lab/compare", OmniTraderPerms.BacktestsRead, async req =>
            {
                var dto = Body<LabCompareDto>(req);
                if (dto == null || dto.JobIds.Count == 0) { await Bad(req, "jobIds required"); return; }
                var output = new List<object>();
                foreach (var id in dto.JobIds.Distinct().Take(12))
                {
                    var job = await parent.BacktestJobRepo.GetAsync(id);
                    if (job?.Result == null) { output.Add(new { Id = id, Error = job == null ? "not found" : "no result" }); continue; }
                    var r = job.Result;
                    decimal start = r.EquityCurve.Count > 0 ? r.EquityCurve[0].Equity : r.InitialEquity;
                    var curve = Downsample(r.EquityCurve, 800).Select(p => new
                    {
                        T = Unix(p.Ts),
                        V = start == 0 ? 0 : Math.Round((double)(p.Equity / start) * 100, 4)
                    });
                    output.Add(new
                    {
                        job.Id, job.StrategyClass, job.Config.Label, Symbol = job.Config.Coin, Interval = job.Config.Interval.ToString(),
                        Summary = OmniTraderAgentTools.BacktestSummary(r),
                        Equity = curve,
                        Drawdown = r.Analysis?.Drawdown is { Count: > 0 } dd ? DownsampleSeries(dd, 800).Select(p => new { T = Unix(p.T), p.V }) : null,
                        Monthly = r.Analysis?.Monthly
                    });
                }
                await Json(req, output);
            });

            await Get("/lab/studies", OmniTraderPerms.BacktestsRead, async req =>
            {
                var list = await parent.Studies.ListAsync(req.userParameters.Get("strategy"), ParseInt(req.userParameters.Get("limit"), 100, 1, 500));
                await Json(req, list.Select(StudyRow));
            });

            await Get("/lab/study", OmniTraderPerms.BacktestsRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await JsonOrNotFound(req, await parent.Studies.GetAsync(id));
            });

            await Post("/lab/study/create", OmniTraderPerms.BacktestsRun, async req =>
            {
                var dto = Body<LabStudyDto>(req);
                if (dto?.Base == null || string.IsNullOrWhiteSpace(dto.Base.Strategy)) { await Bad(req, "base.strategy required"); return; }
                if (dto.Kind is StudyKind.Sweep or StudyKind.WalkForward && dto.Params.Count == 0) { await Bad(req, "params required for a sweep or walk-forward"); return; }
                if (dto.Kind == StudyKind.MonteCarlo && string.IsNullOrWhiteSpace(dto.SourceJobId)) { await Bad(req, "sourceJobId required for Monte Carlo"); return; }
                if (dto.Kind == StudyKind.Batch && dto.Symbols.Count == 0 && string.IsNullOrWhiteSpace(dto.Universe)) { await Bad(req, "symbols or universe required for a batch"); return; }

                BacktestConfig baseConfig;
                if (dto.Kind == StudyKind.MonteCarlo)
                {
                    var source = await parent.BacktestJobRepo.GetAsync(dto.SourceJobId!);
                    if (source == null) { await Bad(req, "source job not found"); return; }
                    baseConfig = source.Config;
                }
                else baseConfig = await BuildConfigAsync(dto.Base, "lab-study", pinStudio: true);

                var record = await parent.Studies.EnqueueAsync(new StudyRequest
                {
                    Kind = dto.Kind, Base = baseConfig, Params = dto.Params, Objective = dto.Objective, Method = dto.Method,
                    MaxRuns = Math.Clamp(dto.MaxRuns, 1, 2000), Seed = dto.Seed, MinTrades = Math.Max(0, dto.MinTrades),
                    InSampleBars = Math.Max(50, dto.InSampleBars), OutOfSampleBars = Math.Max(10, dto.OutOfSampleBars),
                    SourceJobId = dto.SourceJobId, Iterations = Math.Clamp(dto.Iterations, 100, 20_000),
                    RuinDrawdownPct = Math.Clamp(dto.RuinDrawdownPct, 5, 100),
                    Symbols = dto.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().Take(200).ToList(),
                    Universe = dto.Universe, Label = dto.Label
                }, Actor(req));
                await Json(req, new { record.Id, Kind = record.Kind.ToString(), Status = record.Status.ToString() });
            });

            await Post("/lab/study/cancel", OmniTraderPerms.BacktestsRun, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Requested = await parent.Studies.CancelAsync(id) });
            });
        }

        internal static object LabRow(BacktestJobSummaryRow j) => new
        {
            j.Id, j.StrategyClass, j.Config.Coin, Interval = j.Config.Interval.ToString(), j.Config.CandleCount,
            j.Config.FromUtc, j.Config.ToUtc, j.Config.Label, j.Config.Origin, Execution = j.Config.Execution.ToString(),
            j.Config.FeeFraction, j.Config.SlippageFraction, Leverage = j.Config.Margin.Leverage,
            Status = j.Status.ToString(), j.ProgressPct, j.QueuedUtc, j.StartedUtc, j.FinishedUtc, j.Error,
            j.TotalPnLPercent, j.WinRate, j.SharpeRatio, j.MaxDrawdownPercent, j.TotalTrades,
            j.CagrPct, j.Sortino, j.Calmar, j.ProfitFactor, j.ExposurePct, j.BenchmarkReturnPct, j.Alpha, j.StrategyErrors
        };

        internal static object JobDetail(BacktestJobRow job) => new
        {
            job.Id, job.StrategyClass, job.Config, Status = job.Status.ToString(), job.ProgressPct, job.CandlesTotal, job.CandlesDone,
            job.QueuedUtc, job.StartedUtc, job.FinishedUtc, job.CancellationRequested, job.Error, job.Result
        };

        internal static object StudyRow(StudyRecord s) => new
        {
            s.Id, Kind = s.Kind.ToString(), Status = s.Status.ToString(), s.StrategyClass, s.Label, s.Origin, s.Progress, s.Completed, s.Total,
            s.Error, s.CreatedUtc, s.StartedUtc, s.FinishedUtc,
            Symbol = s.Request.Base.Coin, Interval = s.Request.Base.Interval.ToString(), s.Request.Objective,
            Best = s.Result?.Best == null ? null : new { s.Result.Best.Params, s.Result.Best.Objective, s.Result.Best.Metrics, s.Result.Best.Symbol },
            s.Result?.DeflatedSharpe, s.Result?.WalkForwardEfficiency, s.Result?.Trials, s.Result?.Batch
        };

        private static bool IsStudioId(string strategy)
            => strategy.StartsWith("st_", StringComparison.OrdinalIgnoreCase) || strategy.StartsWith("studio:", StringComparison.OrdinalIgnoreCase);

        private static string StudioIdOf(string strategy)
        {
            string s = strategy.StartsWith("studio:", StringComparison.OrdinalIgnoreCase) ? strategy["studio:".Length..] : strategy;
            int at = s.IndexOf('@');
            return at >= 0 ? s[..at] : s;
        }

        private static StudioRunRequest ToRunRequest(LabBacktestDto dto)
        {
            var (fee, slip) = Costs(dto);
            int? version = dto.Version;
            if (version == null && dto.Strategy.Contains("@v", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(dto.Strategy[(dto.Strategy.LastIndexOf("@v", StringComparison.OrdinalIgnoreCase) + 2)..], out var v)) version = v;
            return new StudioRunRequest
            {
                Symbol = dto.Symbol, Interval = CandleClock.Parse(dto.Interval), Bars = dto.Bars, FromUtc = dto.From, ToUtc = dto.To,
                Capital = dto.Capital, FeeFraction = fee, SlippageFraction = slip, Leverage = dto.Leverage, Execution = dto.Execution,
                Benchmark = dto.Benchmark, Parameters = dto.Parameters, Validate = dto.Validate, Version = version, Label = dto.Label
            };
        }

        private static (decimal Fee, decimal Slippage) Costs(LabBacktestDto dto)
        {
            var preset = string.IsNullOrWhiteSpace(dto.CostPreset) ? null : CostPresets.All.FirstOrDefault(p => p.Id == dto.CostPreset);
            return (dto.Fee ?? preset?.Fee ?? 0.001m, dto.Slippage ?? preset?.Slippage ?? 0.0005m);
        }

        /// <summary>A lab request as an engine config. Studio strategies are pinned to a version so a
        /// later edit cannot change what a queued job or study runs.</summary>
        private async Task<BacktestConfig> BuildConfigAsync(LabBacktestDto dto, string origin, bool pinStudio = true)
        {
            if (IsStudioId(dto.Strategy))
            {
                var s = await StudioOrThrow.GetAsync(StudioIdOf(dto.Strategy)) ?? throw new ArgumentException("Unknown studio strategy");
                return StudioOrThrow.BuildConfig(s, ToRunRequest(dto), origin);
            }
            var descriptor = parent.StrategyRegistry.Resolve(dto.Strategy) ?? throw new ArgumentException($"Unknown strategy '{dto.Strategy}'");
            var (fee, slip) = Costs(dto);
            string symbol = string.IsNullOrWhiteSpace(dto.Symbol) ? "BTCUSDT" : dto.Symbol!.Trim().ToUpperInvariant();
            var pars = new Dictionary<string, object?>(dto.Parameters ?? new(), StringComparer.OrdinalIgnoreCase);
            if (!descriptor.RequiresUniverse) pars["TradeSymbol"] = symbol;
            return new BacktestConfig
            {
                StrategyClass = descriptor.ClassName,
                Coin = dto.Momentum?.RegimeSymbol ?? symbol,
                Currency = "",
                Interval = dto.Momentum != null ? TimeInterval.OneDay : CandleClock.Parse(dto.Interval) ?? TimeInterval.OneDay,
                CandleCount = dto.Momentum != null ? Math.Max(1, (int)(dto.Momentum.ToUtc - dto.Momentum.FromUtc).TotalDays) : Math.Clamp(dto.Bars, 50, 50_000),
                FromUtc = dto.From,
                ToUtc = dto.From.HasValue ? dto.To ?? DateTime.UtcNow : null,
                InitialQuoteBalance = dto.Capital,
                FeeFraction = fee,
                SlippageFraction = slip,
                Margin = new MarginSettings { Leverage = Math.Clamp(dto.Leverage, 1m, 10m) },
                Momentum = dto.Momentum,
                Validation = dto.Validate ? new ValidationSettings() : null,
                Parameters = pars,
                Execution = dto.Execution ?? ExecutionTiming.NextOpen,
                Benchmark = string.IsNullOrWhiteSpace(dto.Benchmark) ? null : dto.Benchmark!.Trim().ToUpperInvariant(),
                Label = dto.Label,
                Origin = origin
            };
        }

        private static List<EquityPoint> Downsample(List<EquityPoint> points, int max)
        {
            if (points.Count <= max) return points;
            int stride = (int)Math.Ceiling(points.Count / (double)max);
            var result = new List<EquityPoint>();
            for (int i = 0; i < points.Count; i += stride) result.Add(points[i]);
            if (result[^1] != points[^1]) result.Add(points[^1]);
            return result;
        }

        private static List<SeriesPoint> DownsampleSeries(List<SeriesPoint> points, int max)
        {
            if (points.Count <= max) return points;
            int stride = (int)Math.Ceiling(points.Count / (double)max);
            var result = new List<SeriesPoint>();
            for (int i = 0; i < points.Count; i += stride) result.Add(points[i]);
            if (result[^1] != points[^1]) result.Add(points[^1]);
            return result;
        }
    }

    public sealed class LabBacktestDto
    {
        /// <summary>Class name, studio id (st_…), or a pinned studio class (studio:st_…@v3).</summary>
        public string Strategy { get; set; } = "";
        public int? Version { get; set; }
        public string? Symbol { get; set; }
        public string? Interval { get; set; } = "OneDay";
        public int Bars { get; set; } = 2000;
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public decimal Capital { get; set; } = 10_000m;
        public string? CostPreset { get; set; }
        public decimal? Fee { get; set; }
        public decimal? Slippage { get; set; }
        public decimal Leverage { get; set; } = 1m;
        public ExecutionTiming? Execution { get; set; }
        public string? Benchmark { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
        public bool Validate { get; set; }
        public string? Label { get; set; }
        public MomentumBacktestSettings? Momentum { get; set; }
    }

    public sealed class LabStudyDto
    {
        public StudyKind Kind { get; set; } = StudyKind.Sweep;
        public LabBacktestDto Base { get; set; } = new();
        public List<SweepParam> Params { get; set; } = new();
        public string Objective { get; set; } = "sharpe";
        public string Method { get; set; } = "grid";
        public int MaxRuns { get; set; } = 150;
        public int Seed { get; set; } = 7;
        public int MinTrades { get; set; } = 5;
        public int InSampleBars { get; set; } = 500;
        public int OutOfSampleBars { get; set; } = 120;
        public string? SourceJobId { get; set; }
        public int Iterations { get; set; } = 1000;
        public double RuinDrawdownPct { get; set; } = 50;
        public List<string> Symbols { get; set; } = new();
        public string? Universe { get; set; }
        public string? Label { get; set; }
    }

    public sealed class LabCompareDto
    {
        public List<string> JobIds { get; set; } = new();
    }
}
