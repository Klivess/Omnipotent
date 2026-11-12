using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Quality
{
    /// <summary>One measured value with its interval and sample size.</summary>
    public sealed record QualityMetric(string Scope, string Metric, double? Value, double? CiLow, double? CiHigh, int N, string? PromptVersion = null, string? Model = null);

    public sealed record GateDecision(string Subject, string Candidate, string? Baseline, string Decision, IReadOnlyList<string> Reasons, long DecidedAtMs);

    /// <summary>
    /// The regression gate (N18: "blocks a prompt/model version whose precision drops beyond the CI"). A candidate passes when,
    /// for every category with enough decisions on both sides, its precision is not below the baseline's lower confidence
    /// bound, and when the absolute invariants hold (no restricted facts for minors, no hallucinated references, no sensitive
    /// inferences; literal self-disclosures ≥ 0.95 and overall ≥ 0.85 on the planted corpus).
    /// </summary>
    public static class PromptGate
    {
        public const int MinimumN = 20;

        public static GateDecision Decide(string subject, string candidate, string? baseline, IReadOnlyList<QualityMetric> candidateMetrics, IReadOnlyList<QualityMetric> baselineMetrics, long nowMs)
        {
            var reasons = new List<string>();
            foreach (var c in candidateMetrics.Where(m => m.Metric == "precision" && m.N >= MinimumN && m.Value != null))
            {
                var b = baselineMetrics.FirstOrDefault(x => x.Metric == "precision" && x.Scope == c.Scope && x.N >= MinimumN && x.CiLow != null);
                if (b != null && c.Value < b.CiLow) reasons.Add($"{c.Scope}: precision {c.Value:0.000} is below the baseline's lower bound {b.CiLow:0.000} (n={c.N} vs {b.N}).");
            }
            foreach (var m in candidateMetrics)
            {
                if (m.Metric is "restricted_for_minors" or "hallucinated_refs" or "sensitive_inferences" && m.Value > 0) reasons.Add($"{m.Metric} = {m.Value} (must be 0).");
                if (m.Scope == "planted" && m.Metric == "precision_literal_self" && m.Value < DeductionEvaluation.LiteralSelfTarget) reasons.Add($"literal self-disclosure precision {m.Value:0.000} < 0.95.");
                if (m.Scope == "planted" && m.Metric == "precision" && m.Value < DeductionEvaluation.OverallTarget) reasons.Add($"overall precision {m.Value:0.000} < 0.85.");
            }
            return new GateDecision(subject, candidate, baseline, reasons.Count == 0 ? "pass" : "block", reasons, nowMs);
        }
    }

    /// <summary>
    /// <c>knowledge.quality</c> — the Quality Lab (N18). Daily it measures, from Klives's decisions (gold) and the engine's own
    /// records: per-category precision of facts per prompt version (with Wilson intervals), calibration of the probabilities
    /// that were shown when he decided (ECE, Brier, reliability), inquiry calibration (and refits the inquiry curve from his
    /// "was this right?" feedback), extraction rejection reasons and refusal rates, and classifier label drift per task (the
    /// A2 labels written by the classification workstream). On demand it runs the extraction pipeline on the synthetic
    /// planted-fact corpus in an isolated scratch store with the real model, scores it, and records a gate decision for that
    /// prompt version. Other workstreams record their own metrics (replica fidelity, classifier evaluations) through
    /// <see cref="RecordAsync"/>. Gold examples are never used as few-shot prompts by the extractor, so evaluation cannot leak.
    /// </summary>
    public sealed class QualityModule : IntelModuleBase
    {
        public const string Id = KnowledgeSchema.QualityModule;
        public const string LabKind = "lab";
        public const string PlantedKind = "planted";
        public const string ReadModel = "knowledge.quality";

        private KnowledgeKit kit = null!;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = 1,
            Title = "Quality Lab",
            Category = "knowledge",
            Description = "Precision, recall and calibration from Klives's decisions and a planted-fact corpus; inquiry calibration; label drift; the regression gate for prompt and model versions.",
            DefaultLane = Lane.Cpu,
            DependsOn = new[] { KnowledgeSchema.DeductionModule },
            Schedules = new[] { new ScheduleTrigger("lab", DailyAtLocal: new TimeOnly(5, 0)) },
            Datasets = new[] { KnowledgeSchema.QualityDataset },
            ReadModels = new[] { new ReadModelDescriptor(ReadModel, "Quality Lab", "omniscience.overview.view", 1, 86_400_000, "Latest precision, calibration, drift and gate decisions.") },
        };

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Quality;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context) =>
            context.DueSchedules.Any(s => s.Name == "lab") ? new[] { Job(LabKind, priority: 30) } : Array.Empty<JobRequest>();

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            LabKind => await LabAsync(job).ConfigureAwait(false),
            PlantedKind => await PlantedAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'."),
        };

        public Task<long> StartPlantedEvaluationAsync(CancellationToken ct) => Context.Jobs.EnqueueAsync(Job(PlantedKind, lane: Lane.LlmBackground, priority: 50), ct);

        public Task<long> StartLabAsync(CancellationToken ct) => Context.Jobs.EnqueueAsync(Job(LabKind, priority: 200), ct);

        // ── daily lab ────────────────────────────────────────────────────────────

        private async Task<string?> LabAsync(JobContext job)
        {
            long now = Context.Clock.NowMs;
            var metrics = new List<QualityMetric>();
            metrics.AddRange(GoldPrecision());
            metrics.AddRange(await InquiryCalibrationAsync(job.Cancellation).ConfigureAwait(false));
            metrics.AddRange(ExtractionHealth(now));
            metrics.AddRange(LabelDrift(now));
            string runId = await RecordAsync("lab", "daily", ExtractionPrompt.Version, null, metrics, job.Cancellation).ConfigureAwait(false);
            await WriteReadModelAsync(job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { runId, metrics = metrics.Count }, KernelJson.Options);
        }

        /// <summary>Precision of facts per category and prompt version from Klives's decisions, and the calibration of what he was shown.</summary>
        public IReadOnlyList<QualityMetric> GoldPrecision()
        {
            var labels = kit.Gold.Active(GoldKinds.Assertion).Values.Where(l => l.Verdict is GoldVerdicts.Confirm or GoldVerdicts.Reject or GoldVerdicts.Correct).ToList();
            var rows = new List<(string Category, string Prompt, double P, bool Correct)>();
            using (var c = Context.Stores.Intel.OpenRead())
            {
                foreach (var label in labels)
                {
                    string key = GoldLabelStore.Recanonicalize(label.NaturalKey, kit.Corpus.Canonical);
                    var shown = c.Query(null, """
                        SELECT d.category, COALESCE(a.prompt_version,''), a.probability FROM assertions a JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id
                        WHERE a.natural_key=$k AND a.recorded_at_ms <= $t AND a.status NOT IN ('confirmed','rejected') ORDER BY a.recorded_at_ms DESC LIMIT 1
                        """, r => (r.GetString(0), r.GetString(1), r.GetDoubleOrNull(2) ?? 0.5), ("$k", key), ("$t", label.DecidedAtMs)).FirstOrDefault();
                    if (shown.Item1 == null) continue;
                    // A correction means the fact was right in substance (the attribute and subject), wrong in detail: it counts as correct.
                    rows.Add((shown.Item1, shown.Item2, shown.Item3, label.Verdict != GoldVerdicts.Reject));
                }
            }
            var metrics = new List<QualityMetric>();
            foreach (var g in rows.GroupBy(r => (r.Category, r.Prompt)))
            {
                var p = Proportion.Of(g.Count(x => x.Correct), g.Count());
                metrics.Add(new QualityMetric("category:" + g.Key.Category, "precision", p.Value, p.Low, p.High, p.Trials, g.Key.Prompt));
            }
            foreach (var g in rows.GroupBy(r => r.Prompt))
            {
                var p = Proportion.Of(g.Count(x => x.Correct), g.Count());
                var samples = g.Select(x => (x.P, x.Correct)).ToList();
                metrics.Add(new QualityMetric("gold", "precision", p.Value, p.Low, p.High, p.Trials, g.Key));
                metrics.Add(new QualityMetric("gold", "ece", Metrics.ExpectedCalibrationError(samples), null, null, samples.Count, g.Key));
                metrics.Add(new QualityMetric("gold", "brier", Metrics.Brier(samples), null, null, samples.Count, g.Key));
            }
            return metrics;
        }

        /// <summary>Inquiry calibration from Klives's feedback; refits the inquiry curve the engine applies to new answers.</summary>
        private async Task<IReadOnlyList<QualityMetric>> InquiryCalibrationAsync(CancellationToken ct)
        {
            List<(double P, string Feedback, string Prompt)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                bool hasTable = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='kn_inquiries'") > 0;
                rows = !hasTable ? new() : c.Query(null, """
                    SELECT probability_raw, feedback, COALESCE(prompt_version,'') FROM kn_inquiries
                    WHERE feedback IN ('yes','no','correct','incorrect') AND probability_raw IS NOT NULL AND answer_type IN ('Binary','Prediction')
                    """, r => (r.GetDouble(0), r.GetString(1), r.GetString(2)));
            }
            var metrics = new List<QualityMetric>();
            if (rows.Count == 0) return metrics;
            // The raw probability is P(yes). Feedback either states the truth ("yes"/"no") or judges the verdict
            // ("correct"/"incorrect"), which gives the truth through the verdict's side of 0.5.
            var samples = rows.Select(r => (r.P, Truth: r.Feedback switch { "yes" => true, "no" => false, "correct" => r.P >= 0.5, _ => r.P < 0.5 })).ToList();
            var verdictRight = rows.Select(r => r.Feedback switch { "yes" => r.P >= 0.5, "no" => r.P < 0.5, "correct" => true, _ => false }).ToList();
            metrics.Add(new QualityMetric("inquiry", "ece_raw", Metrics.ExpectedCalibrationError(samples), null, null, samples.Count));
            metrics.Add(new QualityMetric("inquiry", "brier_raw", Metrics.Brier(samples), null, null, samples.Count));
            var accuracy = Proportion.Of(verdictRight.Count(x => x), verdictRight.Count);
            metrics.Add(new QualityMetric("inquiry", "accuracy", accuracy.Value, accuracy.Low, accuracy.High, accuracy.Trials));
            if (rows.Count >= Isotonic.MinimumGold)
                await kit.Calibration.FitAsync(CalibrationStore.InquiryScope, samples, ct).ConfigureAwait(false);
            return metrics;
        }

        /// <summary>Extraction health over the last 7 days: rejection share per reason, refusals and LLM calls per run.</summary>
        private IReadOnlyList<QualityMetric> ExtractionHealth(long now)
        {
            var metrics = new List<QualityMetric>();
            using var c = Context.Stores.Intel.OpenRead();
            var since = now - 7 * 86_400_000L;
            var reasons = c.Query(null, "SELECT COALESCE(reason,'verified'), COUNT(*) FROM kn_claims WHERE recorded_at_ms >= $s GROUP BY COALESCE(reason,'verified')",
                r => (r.GetString(0), r.GetInt64(1)), ("$s", since));
            long total = reasons.Sum(r => r.Item2);
            foreach (var (reason, count) in reasons)
            {
                var p = Proportion.Of((int)count, (int)total);
                metrics.Add(new QualityMetric("extraction:reason:" + reason, "share", p.Value, p.Low, p.High, p.Trials, ExtractionPrompt.Version));
            }
            var runs = c.Query(null, "SELECT rejections_json, llm_calls FROM kn_extraction_runs WHERE started_at_ms >= $s AND finished_at_ms IS NOT NULL", r => (r.GetStringOrNull(0), r.GetInt32(1)), ("$s", since));
            int refusals = runs.Sum(r => r.Item1 == null ? 0 : JsonDocument.Parse(r.Item1).RootElement.TryGetProperty("refusals", out var x) && x.TryGetInt32(out int v) ? v : 0);
            metrics.Add(new QualityMetric("extraction", "refusals", refusals, null, null, runs.Count, ExtractionPrompt.Version));
            metrics.Add(new QualityMetric("extraction", "llm_calls_per_run", runs.Count == 0 ? null : runs.Average(r => r.Item2), null, null, runs.Count, ExtractionPrompt.Version));
            return metrics;
        }

        /// <summary>Classifier drift: week-over-week Jensen–Shannon divergence of each task's label distribution (A2 labels).</summary>
        private IReadOnlyList<QualityMetric> LabelDrift(long now)
        {
            var metrics = new List<QualityMetric>();
            using var c = Context.Stores.Intel.OpenRead();
            long week = 7 * 86_400_000L;
            var rows = c.Query(null, "SELECT task, label, (as_of_ms >= $a) AS recent, COUNT(*) FROM labels WHERE as_of_ms >= $b AND abstained=0 AND label IS NOT NULL GROUP BY task, label, recent",
                r => (r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, r.GetInt64(3)), ("$a", now - week), ("$b", now - 2 * week));
            foreach (var task in rows.GroupBy(r => r.Item1))
            {
                var recent = task.Where(r => r.Item3).ToDictionary(r => r.Item2, r => (double)r.Item4, StringComparer.Ordinal);
                var previous = task.Where(r => !r.Item3).ToDictionary(r => r.Item2, r => (double)r.Item4, StringComparer.Ordinal);
                if (recent.Count == 0 || previous.Count == 0) continue;
                metrics.Add(new QualityMetric("labels:" + task.Key, "drift_js", Metrics.JensenShannon(recent, previous), null, null, (int)(recent.Values.Sum() + previous.Values.Sum())));
            }
            return metrics;
        }

        // ── planted-fact evaluation (real model, isolated scratch store) ──────────

        private async Task<string?> PlantedAsync(JobContext job)
        {
            if (Context.Llm == null) throw new JobFailureException(ErrorClass.Permanent, "The planted-fact evaluation needs the LLM gateway.");
            string root = Path.Combine(Context.Stores.Paths.Root, "quality-lab", "planted-" + job.Job.JobId.ToString(CultureInfo.InvariantCulture));
            var evaluation = await PlantedEvaluation.RunAsync(root, Context, job).ConfigureAwait(false);
            var metrics = PlantedEvaluation.ToMetrics(evaluation, ExtractionPrompt.Version);
            string runId = await RecordAsync("planted", "deduction", ExtractionPrompt.Version, null, metrics, job.Cancellation).ConfigureAwait(false);
            var baseline = LatestMetrics("planted", exceptRun: runId);
            var decision = PromptGate.Decide("deduction.extract", ExtractionPrompt.Version, baseline.Count == 0 ? null : "previous run", metrics, baseline, Context.Clock.NowMs);
            await SaveGateAsync(decision, runId, job.Cancellation).ConfigureAwait(false);
            await WriteReadModelAsync(job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { runId, decision.Decision, decision.Reasons, evaluation.Precision, evaluation.LiteralSelfPrecision, evaluation.Recall }, KernelJson.Options);
        }

        // ── persistence ──────────────────────────────────────────────────────────

        /// <summary>Records a set of metrics as one Quality Lab run (other workstreams use this for replica fidelity and classifier evals).</summary>
        public async Task<string> RecordAsync(string kind, string subject, string? promptVersion, string? model, IReadOnlyList<QualityMetric> metrics, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string runId = NaturalKeys.Id("qr", kind + "|" + subject + "|" + now.ToString(CultureInfo.InvariantCulture) + "|" + Guid.NewGuid().ToString("N"), 20);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO kn_quality_runs(run_id, kind, subject, prompt_version, model, n_items, metrics_json, as_of_ms, created_at_ms) VALUES($r,$k,$s,$pv,$m,$n,$j,$t,$t)",
                    ("$r", runId), ("$k", kind), ("$s", subject), ("$pv", promptVersion), ("$m", model), ("$n", metrics.Count), ("$j", JsonSerializer.Serialize(metrics, KernelJson.Options)), ("$t", now));
                foreach (var m in metrics)
                    c.Exec(tx, """
                        INSERT OR REPLACE INTO kn_quality_metrics(run_id, scope, metric, value, ci_low, ci_high, n, prompt_version, model, created_at_ms)
                        VALUES($r,$s,$m,$v,$lo,$hi,$n,$pv,$model,$t)
                        """, ("$r", runId), ("$s", m.Scope), ("$m", m.Metric), ("$v", m.Value is double v && double.IsFinite(v) ? v : null), ("$lo", m.CiLow), ("$hi", m.CiHigh),
                        ("$n", m.N), ("$pv", m.PromptVersion ?? promptVersion), ("$model", m.Model ?? model), ("$t", now));
            }, ct).ConfigureAwait(false);
            return runId;
        }

        private IReadOnlyList<QualityMetric> LatestMetrics(string kind, string? exceptRun)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? run = c.Scalar<string>(null, "SELECT run_id FROM kn_quality_runs WHERE kind=$k AND ($x IS NULL OR run_id <> $x) ORDER BY created_at_ms DESC LIMIT 1", ("$k", kind), ("$x", exceptRun));
            if (run == null) return Array.Empty<QualityMetric>();
            return c.Query(null, "SELECT scope, metric, value, ci_low, ci_high, n, prompt_version, model FROM kn_quality_metrics WHERE run_id=$r",
                r => new QualityMetric(r.GetString(0), r.GetString(1), r.GetDoubleOrNull(2), r.GetDoubleOrNull(3), r.GetDoubleOrNull(4), r.GetInt32(5), r.GetStringOrNull(6), r.GetStringOrNull(7)),
                ("$r", run));
        }

        private Task SaveGateAsync(GateDecision d, string runId, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_gate_decisions(gate_id, subject, candidate, baseline, decision, reasons_json, run_id, decided_at_ms) VALUES($id,$s,$c,$b,$d,$r,$run,$t)
                """, ("$id", NaturalKeys.Id("gt", d.Subject + "|" + d.Candidate + "|" + runId, 20)), ("$s", d.Subject), ("$c", d.Candidate), ("$b", d.Baseline), ("$d", d.Decision),
                ("$r", JsonSerializer.Serialize(d.Reasons, KernelJson.Options)), ("$run", runId), ("$t", d.DecidedAtMs)), ct);

        /// <summary>The latest gate decision for a candidate (null when it was never evaluated: the bootstrap baseline is allowed).</summary>
        public static GateDecision? LatestGate(Storage.OmniscienceStores stores, string subject, string candidate)
        {
            using var c = stores.Intel.OpenRead();
            bool hasTable = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='kn_gate_decisions'") > 0;
            if (!hasTable) return null;
            return c.Query(null, "SELECT subject, candidate, baseline, decision, reasons_json, decided_at_ms FROM kn_gate_decisions WHERE subject=$s AND candidate=$c ORDER BY decided_at_ms DESC LIMIT 1",
                r => new GateDecision(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), JsonSerializer.Deserialize<List<string>>(r.GetString(4), KernelJson.Options) ?? new(), r.GetInt64(5)),
                ("$s", subject), ("$c", candidate)).FirstOrDefault();
        }

        public async Task WriteReadModelAsync(CancellationToken ct)
        {
            var lab = LatestMetrics("lab", null);
            var planted = LatestMetrics("planted", null);
            var gate = LatestGate(Context.Stores, "deduction.extract", ExtractionPrompt.Version);
            long now = Context.Clock.NowMs;
            await Context.ReadModels.PutAsync(ReadModel, "lab", 1, now, kit.Corpus.MaxSeq(), new
            {
                asOfMs = now,
                promptVersion = ExtractionPrompt.Version,
                gold = lab,
                planted,
                gate,
            }, ct).ConfigureAwait(false);
        }
    }
}
