using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Research
{
    public enum ExperimentStatus { Draft = 0, Running = 1, Complete = 2, Abandoned = 3 }

    /// <summary>
    /// A recorded hypothesis and everything needed to reproduce its test: the strategy version, the
    /// parameters, the dataset window and the results. Without this, a good backtest result is an
    /// anecdote rather than evidence.
    /// </summary>
    public sealed class Experiment
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string StrategyClass { get; init; }
        public string Hypothesis { get; set; } = "";
        public ExperimentStatus Status { get; set; } = ExperimentStatus.Draft;
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Backtest job ids that belong to this experiment.</summary>
        public List<string> JobIds { get; init; } = new();
        public Dictionary<string, object?> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? DatasetDescription { get; set; }
        public DateTime? WindowFromUtc { get; set; }
        public DateTime? WindowToUtc { get; set; }
        public string? Notes { get; set; }

        /// <summary>Headline results, kept flat so comparisons across experiments are trivial.</summary>
        public Dictionary<string, double> Results { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An immutable record of one strategy configuration and the authority it has earned.</summary>
    public sealed class StrategyVersionRecord
    {
        public required string Id { get; init; }
        public required string StrategyClass { get; init; }
        public required int Version { get; init; }
        /// <summary>draft | validated | promoted | retired</summary>
        public string Status { get; set; } = "draft";
        public ExecutionAuthority Authority { get; set; } = ExecutionAuthority.Observe;
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedUtc { get; set; }
        public Dictionary<string, object?> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Notes { get; set; }
        /// <summary>Evidence backing the current authority — experiment ids and their headline metrics.</summary>
        public List<string> EvidenceExperimentIds { get; init; } = new();
    }

    /// <summary>The evidence a version must produce before it may hold more execution authority.</summary>
    public sealed class PromotionRequirement
    {
        public required string Name { get; init; }
        public required bool Met { get; init; }
        public required string Detail { get; init; }
    }

    public sealed class PromotionAssessment
    {
        public required string StrategyClass { get; init; }
        public required int Version { get; init; }
        public required ExecutionAuthority CurrentAuthority { get; init; }
        public required ExecutionAuthority RequestedAuthority { get; init; }
        public required bool Eligible { get; init; }
        public List<PromotionRequirement> Requirements { get; init; } = new();
        public string Summary => Eligible
            ? "All promotion requirements met."
            : string.Join("; ", Requirements.Where(r => !r.Met).Select(r => r.Detail));
    }

    /// <summary>
    /// Owns the experiment log, strategy versioning and the promotion gate.
    ///
    /// Promotion is deliberately a *gate*, not a suggestion: moving a version up the authority ladder
    /// requires documented evidence at each step, and the assessment names exactly which evidence is
    /// missing rather than returning a bare "no".
    /// </summary>
    public sealed class ExperimentRegistry
    {
        private readonly ExperimentRepository repo;
        private readonly StrategyVersionRepository versions;
        private readonly BacktestJobRepository backtests;

        public ExperimentRegistry(ExperimentRepository repo, StrategyVersionRepository versions, BacktestJobRepository backtests)
        {
            this.repo = repo;
            this.versions = versions;
            this.backtests = backtests;
        }

        // ── experiments ───────────────────────────────────────────────────────────

        public Task<List<Experiment>> ListAsync(int limit = 200, CancellationToken ct = default) => repo.ListAsync(limit, ct);
        public Task<Experiment?> GetAsync(string id, CancellationToken ct = default) => repo.GetAsync(id, ct);

        public async Task<Experiment> CreateAsync(string name, string strategyClass, string hypothesis,
            Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var experiment = new Experiment
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                StrategyClass = strategyClass,
                Hypothesis = hypothesis
            };
            if (parameters != null)
                foreach (var (k, v) in parameters) experiment.Parameters[k] = v;
            await repo.UpsertAsync(experiment, ct);
            return experiment;
        }

        /// <summary>Attach a backtest job and fold its headline metrics into the experiment's results.</summary>
        public async Task<Experiment?> AttachJobAsync(string experimentId, string jobId, CancellationToken ct = default)
        {
            var experiment = await repo.GetAsync(experimentId, ct);
            if (experiment == null) return null;
            if (!experiment.JobIds.Contains(jobId)) experiment.JobIds.Add(jobId);

            var job = await backtests.GetAsync(jobId, ct);
            if (job?.Result is { } result)
            {
                experiment.Results["TotalPnLPercent"] = (double)result.TotalPnLPercent;
                experiment.Results["SharpeRatio"] = (double)result.SharpeRatio;
                experiment.Results["MaxDrawdownPercent"] = (double)result.MaxDrawdownPercent;
                experiment.Results["WinRate"] = (double)result.WinRate;
                experiment.Results["ProfitFactor"] = (double)result.ProfitFactor;
                experiment.Results["TotalTrades"] = result.TotalTrades;
                experiment.Results["AlphaVsBuyAndHoldPercent"] = (double)result.AlphaVsBuyAndHoldPercent;
                experiment.Status = ExperimentStatus.Complete;
                experiment.WindowFromUtc ??= job.Config.FromUtc;
                experiment.WindowToUtc ??= job.Config.ToUtc;
                experiment.DatasetDescription ??= $"{job.Config.Coin}{job.Config.Currency} {job.Config.Interval}, {job.Config.CandleCount} bars";
            }
            await repo.UpsertAsync(experiment, ct);
            return experiment;
        }

        public async Task<Experiment?> UpdateAsync(string id, string? name, string? hypothesis, string? notes,
            ExperimentStatus? status, CancellationToken ct = default)
        {
            var experiment = await repo.GetAsync(id, ct);
            if (experiment == null) return null;
            if (!string.IsNullOrWhiteSpace(name)) experiment.Name = name!;
            if (hypothesis != null) experiment.Hypothesis = hypothesis;
            if (notes != null) experiment.Notes = notes;
            if (status.HasValue) experiment.Status = status.Value;
            await repo.UpsertAsync(experiment, ct);
            return experiment;
        }

        // ── strategy versions ─────────────────────────────────────────────────────

        public Task<List<StrategyVersionRecord>> ListVersionsAsync(string? strategyClass = null, CancellationToken ct = default)
            => versions.ListAsync(strategyClass, ct);

        public async Task<StrategyVersionRecord> CreateVersionAsync(string strategyClass,
            Dictionary<string, object?>? parameters, string? notes, CancellationToken ct = default)
        {
            int next = await versions.NextVersionAsync(strategyClass, ct);
            var record = new StrategyVersionRecord
            {
                Id = $"{strategyClass}:v{next}",
                StrategyClass = strategyClass,
                Version = next,
                Notes = notes
            };
            if (parameters != null)
                foreach (var (k, v) in parameters) record.Parameters[k] = v;
            await versions.UpsertAsync(record, ct);
            return record;
        }

        /// <summary>
        /// Assess whether a version has earned the requested authority. The ladder is
        /// observe → paper → demo → approval-required → automated, and each rung has its own evidence
        /// bar. A version may only ever move up one rung at a time.
        /// </summary>
        public async Task<PromotionAssessment> AssessPromotionAsync(string strategyClass, int version,
            ExecutionAuthority requested, CancellationToken ct = default)
        {
            var all = await versions.ListAsync(strategyClass, ct);
            var record = all.FirstOrDefault(v => v.Version == version);
            var current = record?.Authority ?? ExecutionAuthority.Observe;
            var requirements = new List<PromotionRequirement>();

            requirements.Add(new PromotionRequirement
            {
                Name = "version.exists",
                Met = record != null,
                Detail = record != null ? $"Version {version} is registered." : $"No registered version {version} of {strategyClass}."
            });

            bool oneRung = (int)requested <= (int)current + 1;
            requirements.Add(new PromotionRequirement
            {
                Name = "ladder.single_step",
                Met = oneRung,
                Detail = oneRung
                    ? $"{current} → {requested} is a single step."
                    : $"{current} → {requested} skips a rung; promote one level at a time."
            });

            var experiments = (await repo.ListAsync(500, ct))
                .Where(e => string.Equals(e.StrategyClass, strategyClass, StringComparison.OrdinalIgnoreCase)
                         && e.Status == ExperimentStatus.Complete)
                .ToList();

            bool hasEvidence = experiments.Count > 0;
            requirements.Add(new PromotionRequirement
            {
                Name = "evidence.experiment",
                Met = hasEvidence,
                Detail = hasEvidence
                    ? $"{experiments.Count} completed experiment(s) on record."
                    : "No completed experiment documents this strategy's behaviour."
            });

            // Anything touching real money needs measured results, not merely a completed run.
            if (requested >= ExecutionAuthority.Demo)
            {
                var best = experiments
                    .Where(e => e.Results.ContainsKey("TotalTrades"))
                    .OrderByDescending(e => e.Results.GetValueOrDefault("SharpeRatio"))
                    .FirstOrDefault();

                double trades = best?.Results.GetValueOrDefault("TotalTrades") ?? 0;
                double sharpe = best?.Results.GetValueOrDefault("SharpeRatio") ?? 0;
                double drawdown = best?.Results.GetValueOrDefault("MaxDrawdownPercent") ?? 100;

                requirements.Add(new PromotionRequirement
                {
                    Name = "evidence.sample_size",
                    Met = trades >= 30,
                    Detail = trades >= 30
                        ? $"{trades:F0} trades in the strongest experiment."
                        : $"Only {trades:F0} trades on record; at least 30 are needed for the result to mean anything."
                });
                requirements.Add(new PromotionRequirement
                {
                    Name = "evidence.risk_adjusted_return",
                    Met = sharpe > 0,
                    Detail = sharpe > 0 ? $"Sharpe {sharpe:F2}." : $"Sharpe {sharpe:F2} is not positive."
                });
                requirements.Add(new PromotionRequirement
                {
                    Name = "evidence.drawdown",
                    Met = drawdown < 50,
                    Detail = drawdown < 50 ? $"Max drawdown {drawdown:F1}%." : $"Max drawdown {drawdown:F1}% is too deep to promote."
                });
            }

            if (requested == ExecutionAuthority.Automated)
            {
                bool wasApprovalRequired = current == ExecutionAuthority.ApprovalRequired;
                requirements.Add(new PromotionRequirement
                {
                    Name = "ladder.approval_served",
                    Met = wasApprovalRequired,
                    Detail = wasApprovalRequired
                        ? "Version has operated under approval-required authority."
                        : "A version must run under approval-required authority before it is allowed to trade unattended."
                });
            }

            return new PromotionAssessment
            {
                StrategyClass = strategyClass,
                Version = version,
                CurrentAuthority = current,
                RequestedAuthority = requested,
                Eligible = requirements.All(r => r.Met),
                Requirements = requirements
            };
        }

        /// <summary>Promote a version. Refuses when the gate is not met — the assessment is returned
        /// either way so the caller can show exactly what is missing.</summary>
        public async Task<(bool Promoted, PromotionAssessment Assessment)> PromoteAsync(string strategyClass, int version,
            ExecutionAuthority requested, string approvedBy, CancellationToken ct = default)
        {
            var assessment = await AssessPromotionAsync(strategyClass, version, requested, ct);
            if (!assessment.Eligible) return (false, assessment);

            var all = await versions.ListAsync(strategyClass, ct);
            var record = all.First(v => v.Version == version);
            record.Authority = requested;
            record.Status = requested >= ExecutionAuthority.ApprovalRequired ? "promoted" : "validated";
            record.ApprovedBy = approvedBy;
            record.ApprovedUtc = DateTime.UtcNow;
            await versions.UpsertAsync(record, ct);
            return (true, assessment);
        }
    }
}
