using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Classification
{
    /// <summary>
    /// The A2 classification layer (plan A1–A6, R1, R2): every message is embedded once by the shared encoder, labelled
    /// for every task by light heads on that embedding plus context heads, the LLM teacher/adjudicator where it earns its
    /// cost, gold corrections and person priors, fused by a calibrated stacker with abstention.
    /// <list type="bullet">
    /// <item><b>drain</b> (CPU): consumes message inserts/edits from the corpus change log in seq order (so late backfill is
    /// processed like live data, T5), embeds them (vectors.db "message" space, by message version) and labels them under
    /// the active label set (and a building one). A large backlog first runs the priority pass (R2: tracked people →
    /// Klives's DMs → watch tier → everything else).</item>
    /// <item><b>teacher</b> (LLM background): stratified samples, disagreements and high-impact messages are labelled by the
    /// LLM in context, batched to fill the window under one byte-stable taxonomy prefix, with a daily call budget (R1).</item>
    /// <item><b>train</b> (CPU): heads are trained from teacher + gold labels, calibrated, gated against the active heads
    /// (regression gate) and published as a new <i>label set</i>; a background relabel fills it while the old label set keeps
    /// serving, then it becomes active (charts never mix versions, A3).</item>
    /// <item><b>drift</b>: weekly label distributions per task with a PSI alert (F-ML acceptance), measured by the rollups
    /// module on its daily label counts.</item>
    /// </list>
    /// Per-message results are packed rows in <c>classify_message</c>; detailed records (LLM labels with rationale,
    /// adjudications, concept hits) use the frozen <c>labels</c> table. Read them through <see cref="ClassificationReader"/>.
    /// </summary>
    public sealed partial class ClassificationModule : IntelModuleBase
    {
        public const string ModuleId = "classify";
        public const string HealthModel = "classify.health";
        public const string TeacherVersion = "llm.teacher.v1";
        public const string AdjudicationVersion = "llm.adjudicate.v1";
        public const string TeacherPromptVersion = "teacher.v1";

        // Settings (namespaced classify.*). Encoder settings are read once at startup by SharedEncoder.
        public const string TeacherCallsPerDaySetting = "classify.teacher_calls_per_day";
        public const string TeacherTargetSetting = "classify.teacher_target";
        public const string TeacherBatchSetting = "classify.teacher_batch";
        public const string PriorityBacklogSetting = "classify.priority_backlog";
        public const string ContextGapMinutesSetting = "classify.context_gap_minutes";
        public const string IvfThresholdSetting = "classify.ivf_threshold";

        private readonly IReadOnlyList<ITactic> tactics;

        public ClassificationModule()
        {
            tactics = new ITactic[] { new TeachConceptTactic(this) };
        }

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Classification (A2 ensembles)",
            Category = "analytics",
            Description = "Embeds every message once with the shared encoder and labels it for sentiment, emotion, toxicity, dialogue act, humour, politeness, " +
                          "formality, self-disclosure, information density, conflict, support, modality, language and references to Klives: heads on the " +
                          "embedding plus context heads, the LLM teacher where it earns its cost, gold corrections, fused and calibrated with abstention.",
            SubscribedChangeKinds = new[] { ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete },
            Schedules = new[]
            {
                new ScheduleTrigger("drain", Every: TimeSpan.FromSeconds(30)),
                new ScheduleTrigger("teacher", Every: TimeSpan.FromMinutes(10)),
                new ScheduleTrigger("train-check", Every: TimeSpan.FromMinutes(30)),
                new ScheduleTrigger("train", DailyAtLocal: new TimeOnly(4, 10)),
            },
            DefaultLane = Lane.Cpu,
            Settings = new[]
            {
                new SettingDescriptor(SharedEncoder.VariantSetting, SettingTypes.String, "\"int8\"", "Encoder build: int8 (default, measured against fp32) or fp32. Applies at the next start."),
                new SettingDescriptor(SharedEncoder.ThreadsSetting, SettingTypes.Int, "2", "ONNX intra-op threads for the shared encoder. Applies at the next start.", 1, 16),
                new SettingDescriptor(SharedEncoder.CpuPauseSetting, SettingTypes.Number, "0.85", "Background encoding pauses while host CPU load is above this fraction (bounded).", 0.3, 1.0),
                new SettingDescriptor(TeacherCallsPerDaySetting, SettingTypes.Int, "80", "LLM teacher/adjudication calls per day (Klives's zone day). Each labels a full batch.", 0, 2000),
                new SettingDescriptor(TeacherTargetSetting, SettingTypes.Int, "6000", "Teacher-labelled sample size the heads are trained on (plan R1: 5–10K).", 500, 50000),
                new SettingDescriptor(TeacherBatchSetting, SettingTypes.Int, "120", "Most target messages per teacher call (fewer when the window is smaller).", 10, 600),
                new SettingDescriptor(PriorityBacklogSetting, SettingTypes.Int, "20000", "A backlog above this many changes runs the priority pass first (tracked → Klives's DMs → watch).", 0, 10000000),
                new SettingDescriptor(ContextGapMinutesSetting, SettingTypes.Int, "360", "A previous message older than this is not context for the context heads.", 1, 10080),
                new SettingDescriptor(IvfThresholdSetting, SettingTypes.Int, "50000", "Message vectors above which the IVF index is built (retrained when the space grows 4×).", 1000, 100000000),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(HealthModel, "Classification health", "omniscience.overview.view", 1, 5 * 60_000,
                    "Encoder, coverage, teacher sample, per-task head metrics, label sets and drift."),
            },
        };

        public override IReadOnlyList<ITactic> Tactics => tactics;

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "classification tables", """
CREATE TABLE classify_cursors(
  name TEXT PRIMARY KEY,
  seq INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE classify_state(
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE classify_message(
  message_id TEXT NOT NULL,
  labelset INTEGER NOT NULL,
  item_version INTEGER NOT NULL,
  text_hash TEXT NOT NULL,
  codes TEXT NOT NULL,
  gate TEXT,
  as_of_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL,
  PRIMARY KEY(message_id, labelset)
) WITHOUT ROWID;
CREATE INDEX ix_classify_message_labelset ON classify_message(labelset, message_id);
CREATE TABLE classify_text_authors(
  text_hash TEXT NOT NULL,
  author_identity_id TEXT NOT NULL,
  PRIMARY KEY(text_hash, author_identity_id)
) WITHOUT ROWID;
CREATE TABLE classify_author_lang(
  author_identity_id TEXT NOT NULL,
  lang TEXT NOT NULL,
  n INTEGER NOT NULL,
  PRIMARY KEY(author_identity_id, lang)
) WITHOUT ROWID;
CREATE TABLE classify_heads(
  task TEXT NOT NULL,
  kind TEXT NOT NULL,
  version INTEGER NOT NULL,
  task_version INTEGER NOT NULL,
  encoder TEXT NOT NULL,
  labels_json TEXT NOT NULL,
  dims INTEGER NOT NULL,
  weights BLOB NOT NULL,
  bias BLOB NOT NULL,
  temperature REAL NOT NULL,
  metrics_json TEXT NOT NULL,
  gold_metrics_json TEXT,
  train_items INTEGER NOT NULL,
  state TEXT NOT NULL,
  trained_at_ms INTEGER NOT NULL,
  PRIMARY KEY(task, kind, version)
);
CREATE TABLE classify_labelsets(
  labelset INTEGER PRIMARY KEY,
  state TEXT NOT NULL,
  encoder TEXT NOT NULL,
  heads_json TEXT NOT NULL,
  fusion_json TEXT NOT NULL,
  cursor_item TEXT,
  items_done INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL,
  completed_at_ms INTEGER
);
CREATE TABLE classify_teacher_queue(
  message_id TEXT PRIMARY KEY,
  reason TEXT NOT NULL,
  priority INTEGER NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  state TEXT NOT NULL,
  text_hash TEXT,
  enqueued_at_ms INTEGER NOT NULL,
  done_at_ms INTEGER,
  note TEXT
);
CREATE INDEX ix_classify_teacher_queue_hash ON classify_teacher_queue(text_hash) WHERE text_hash IS NOT NULL;
CREATE INDEX ix_classify_teacher_queue_ready ON classify_teacher_queue(state, priority DESC, enqueued_at_ms);
CREATE TABLE classify_teacher_calls(
  call_day TEXT NOT NULL,
  calls INTEGER NOT NULL,
  PRIMARY KEY(call_day)
);
CREATE TABLE classify_concepts(
  concept_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  examples_json TEXT NOT NULL,
  state TEXT NOT NULL,
  head_version INTEGER,
  threshold REAL,
  metrics_json TEXT,
  hits INTEGER NOT NULL DEFAULT 0,
  run_id TEXT,
  created_by TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
"""),
            // Label changes of the active label set, for consumers that aggregate labels (rollups): written with the labels.
            new Migration(ModuleId, 2, "label outbox", """
CREATE TABLE classify_outbox(
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  message_id TEXT NOT NULL,
  labelset INTEGER NOT NULL,
  at_ms INTEGER NOT NULL
);
CREATE INDEX ix_classify_outbox_time ON classify_outbox(at_ms);
"""),
        };

        // ── kernel hooks ─────────────────────────────────────────────────────────

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Count == 0 ? Array.Empty<DirtySubject>() : new[] { new DirtySubject("stream", "messages") };

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            bool drainDue = context.Dirty.Count > 0 || context.DueSchedules.Any(s => s.Name == "drain") || context.VersionChanged;
            if (drainDue && Context.Encoder != null && Context.Vectors != null) jobs.Add(Job("drain", "stream", "messages", priority: 120));
            if (context.DueSchedules.Any(s => s.Name == "teacher"))
            {
                jobs.Add(Job("teacher-sample", "stream", "teacher", priority: 90));
                if (Context.Llm != null) jobs.Add(Job("teacher", "stream", "teacher", priority: 80, lane: Lane.LlmBackground));
            }
            if (context.DueSchedules.Any(s => s.Name == "train"))
                jobs.Add(Job("train", "stream", "heads", priority: 100));
            else if (context.DueSchedules.Any(s => s.Name == "train-check") && TrainingWanted())
                jobs.Add(Job("train", "stream", "heads", priority: 100));
            if (BuildingLabelset() is { } building) jobs.Add(Job("relabel", "labelset", building.ToString(System.Globalization.CultureInfo.InvariantCulture), priority: 110));
            return jobs;
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            "drain" => await DrainAsync(job).ConfigureAwait(false),
            "teacher-sample" => await TeacherSampleAsync(job).ConfigureAwait(false),
            "teacher" => await TeacherAsync(job).ConfigureAwait(false),
            "train" => await TrainAsync(job).ConfigureAwait(false),
            "relabel" => await RelabelAsync(job).ConfigureAwait(false),
            "prune" => await PruneAsync(job).ConfigureAwait(false),
            "ivf" => await IvfAsync(job).ConfigureAwait(false),
            "concept-train" => await ConceptTrainAsync(job).ConfigureAwait(false),
            "concept-apply" => await ConceptApplyAsync(job).ConfigureAwait(false),
            "refuse" => await RefuseAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown classify job kind '{job.Job.Kind}'."),
        };

        // ── corrections (gold) ───────────────────────────────────────────────────

        internal sealed record RefusePayload(IReadOnlyList<string> Ids);

        /// <summary>
        /// Klives corrects one label (X69 → gold): recorded in operator.db by natural key, applied to that message under the
        /// live label sets by a queued re-fusion, and used by the next training run (heads, LLM reliability, stacker).
        /// </summary>
        public async Task CorrectAsync(string messageId, string task, string label, string actor, string? note, CancellationToken ct)
        {
            await ClassificationReader.RecordGoldAsync(Context.Stores, Context.Clock, actor, messageId, task, label, note, ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ModuleId, Kind = "refuse", SubjectKind = "message", SubjectId = messageId, Lane = Lane.Cpu, Priority = 300,
                PayloadJson = JsonSerializer.Serialize(new RefusePayload(new[] { messageId }), KernelJson.Options),
            }, ct).ConfigureAwait(false);
        }

        private async Task<string?> RefuseAsync(JobContext job)
        {
            var ids = job.Payload<RefusePayload>()?.Ids ?? Array.Empty<string>();
            var labelsets = LiveLabelsets();
            if (ids.Count == 0 || labelsets.Count == 0) return null;
            DrainContext dc;
            using (var corpus = Context.Stores.Corpus.OpenRead()) dc = NewDrainContext(corpus);
            int n = await LabelAsync(ids, labelsets, dc, job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { refused = n });
        }

        // ── small state helpers ──────────────────────────────────────────────────

        internal IEmbeddingEncoder Encoder => Context.Encoder ?? throw new JobFailureException(ErrorClass.Permanent, "No shared encoder is configured in this runtime.");
        internal VectorStore Vectors => Context.Vectors ?? throw new JobFailureException(ErrorClass.Permanent, "No vector store is configured in this runtime.");
        internal ModuleContext Ctx => Context;

        internal long Cursor(string name)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT seq FROM classify_cursors WHERE name=$n", ("$n", name)) ?? 0;
        }

        internal Task SetCursorAsync(string name, long seq, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO classify_cursors(name, seq, updated_at_ms) VALUES($n,$s,$t)
                ON CONFLICT(name) DO UPDATE SET seq=MAX(classify_cursors.seq, excluded.seq), updated_at_ms=excluded.updated_at_ms
                """, ("$n", name), ("$s", seq), ("$t", Context.Clock.NowMs)), ct);

        internal T? State<T>(string key)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT value_json FROM classify_state WHERE key=$k", ("$k", key));
            return json == null ? default : JsonSerializer.Deserialize<T>(json, KernelJson.Options);
        }

        internal Task SetStateAsync<T>(string key, T value, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO classify_state(key, value_json, updated_at_ms) VALUES($k,$v,$t)
                ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at_ms=excluded.updated_at_ms
                """, ("$k", key), ("$v", JsonSerializer.Serialize(value, KernelJson.Options)), ("$t", Context.Clock.NowMs)), ct);

        internal int Setting(string key, int fallback) => Context.Settings.Get(key, fallback);

        /// <summary>The label set that is being filled in the background, if any.</summary>
        internal int? BuildingLabelset()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return (int?)c.Scalar<long?>(null, "SELECT labelset FROM classify_labelsets WHERE state='building' ORDER BY labelset DESC LIMIT 1");
        }

        internal int? ActiveLabelset()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return ClassificationReader.ActiveLabelset(c);
        }

        /// <summary>Cold start: no heads yet but enough teacher labels for at least one task.</summary>
        private bool TrainingWanted()
        {
            using var c = Context.Stores.Intel.OpenRead();
            bool hasHeads = c.Scalar<long>(null, "SELECT COUNT(*) FROM classify_heads WHERE state='active'") > 0;
            long teacher = c.Scalar<long>(null, "SELECT COUNT(DISTINCT item_id) FROM labels WHERE item_kind='message' AND model_version=$v AND task='sentiment'", ("$v", TeacherVersion));
            var last = State<TrainMarker>("train.last");
            if (!hasHeads) return teacher >= HeadTrainer.MinExamples;
            return last != null && teacher >= last.TeacherLabels * 1.25 + 100;
        }

        internal sealed record TrainMarker(long TeacherLabels, long GoldLabels, long AtMs);

        // ── label sets ───────────────────────────────────────────────────────────

        /// <summary>A label set: the heads and fusion settings one consistent generation of labels was computed with.</summary>
        internal sealed class Labelset
        {
            public required int Id { get; init; }
            public required string State { get; init; }
            public required string EncoderVersion { get; init; }
            public required Dictionary<string, LinearHead> Single { get; init; }
            public required Dictionary<string, LinearHead> ContextHeads { get; init; }
            public required Dictionary<string, FusionConfig> Fusion { get; init; }
            public required Dictionary<string, double> LlmReliability { get; init; }
            public string? CursorItem { get; init; }
        }

        internal sealed record LabelsetHeads(Dictionary<string, int> Single, Dictionary<string, int> Context);

        internal sealed record LabelsetFusion(Dictionary<string, FusionConfigDto> Tasks, Dictionary<string, double> LlmReliability);

        internal sealed record FusionConfigDto(Dictionary<string, double> Weights, double AbstainBelow, double MinMargin, int GoldExamples, string Source)
        {
            public FusionConfig ToConfig() => new(Weights, AbstainBelow, MinMargin, GoldExamples, Source);
            public static FusionConfigDto From(FusionConfig f) => new(f.Weights.ToDictionary(k => k.Key, k => k.Value), f.AbstainBelow, f.MinMargin, f.GoldExamples, f.Source);
        }

        private readonly Dictionary<int, Labelset> labelsetCache = new();
        private readonly object labelsetLock = new();

        /// <summary>The label sets new messages must be written under: the active one and a building one.</summary>
        internal IReadOnlyList<Labelset> LiveLabelsets()
        {
            List<(int Id, string State)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, "SELECT labelset, state FROM classify_labelsets WHERE state IN ('active','building') ORDER BY labelset",
                    r => ((int)r.GetInt64(0), r.GetString(1)));
            return rows.Select(r => LoadLabelset(r.Id)).OfType<Labelset>().Where(l => l.EncoderVersion == Encoder.ModelVersion).ToList();
        }

        internal Labelset? LoadLabelset(int id)
        {
            lock (labelsetLock)
            {
                if (labelsetCache.TryGetValue(id, out var cached))
                {
                    // State and cursor move; heads never change within a label set.
                    using var c0 = Context.Stores.Intel.OpenRead();
                    var st = c0.Query(null, "SELECT state, cursor_item FROM classify_labelsets WHERE labelset=$l", r => (r.GetString(0), r.GetStringOrNull(1)), ("$l", id)).FirstOrDefault();
                    if (st == default) { labelsetCache.Remove(id); return null; }
                    return new Labelset
                    {
                        Id = cached.Id, State = st.Item1, CursorItem = st.Item2, EncoderVersion = cached.EncoderVersion, Single = cached.Single,
                        ContextHeads = cached.ContextHeads, Fusion = cached.Fusion, LlmReliability = cached.LlmReliability,
                    };
                }
            }
            using var c = Context.Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT state, encoder, heads_json, fusion_json, cursor_item FROM classify_labelsets WHERE labelset=$l",
                r => (State: r.GetString(0), Encoder: r.GetString(1), Heads: r.GetString(2), Fusion: r.GetString(3), Cursor: r.GetStringOrNull(4)), ("$l", id)).FirstOrDefault();
            if (row == default) return null;
            var heads = JsonSerializer.Deserialize<LabelsetHeads>(row.Heads, KernelJson.Options)!;
            var fusion = JsonSerializer.Deserialize<LabelsetFusion>(row.Fusion, KernelJson.Options)!;
            var set = new Labelset
            {
                Id = id,
                State = row.State,
                EncoderVersion = row.Encoder,
                CursorItem = row.Cursor,
                Single = heads.Single.Select(kv => LoadHead(c, kv.Key, HeadKind.Single, kv.Value)).OfType<LinearHead>().ToDictionary(h => h.Task),
                ContextHeads = heads.Context.Select(kv => LoadHead(c, kv.Key, HeadKind.Context, kv.Value)).OfType<LinearHead>().ToDictionary(h => h.Task),
                Fusion = fusion.Tasks.ToDictionary(kv => kv.Key, kv => kv.Value.ToConfig()),
                LlmReliability = fusion.LlmReliability,
            };
            lock (labelsetLock) labelsetCache[id] = set;
            return set;
        }

        internal static LinearHead? LoadHead(SqliteConnection c, string task, HeadKind kind, int version) =>
            c.Query(null, "SELECT encoder, labels_json, dims, weights, bias, temperature FROM classify_heads WHERE task=$t AND kind=$k AND version=$v",
                r => new LinearHead(task, kind, version, r.GetString(0), JsonSerializer.Deserialize<List<string>>(r.GetString(1))!, r.GetInt32(2),
                    VectorMath.Floats((byte[])r[3]), VectorMath.Floats((byte[])r[4]), (float)r.GetDouble(5)),
                ("$t", task), ("$k", kind.ToString().ToLowerInvariant()), ("$v", version)).FirstOrDefault();

        // ── health read model ────────────────────────────────────────────────────

        internal sealed record TaskHealthDto(string Task, string Title, int? SingleVersion, int? ContextVersion, double? MacroF1, double? MacroF1Low, double? Ece,
            double? Coverage, double? GoldMacroF1, int TeacherLabels, int GoldLabels, string FusionSource, double? LastPsi);

        internal sealed record HealthDto(string Encoder, string EncoderState, long Embedded, long Labelled, int? ActiveLabelset, int? BuildingLabelset, long BuildingDone,
            long DrainCursor, long CorpusSeq, long Backlog, long TeacherQueued, long TeacherDone, int TeacherCallsToday, int TeacherCallsPerDay,
            IReadOnlyList<TaskHealthDto> Tasks, object? EncoderStats, object? Ivf, long AsOfMs);

        internal async Task RefreshHealthAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            long corpusSeq;
            using (var cr = Context.Stores.Corpus.OpenRead()) corpusSeq = ChangeLog.MaxSeq(cr);
            using var c = Context.Stores.Intel.OpenRead();
            int? active = ClassificationReader.ActiveLabelset(c);
            var building = c.Query(null, "SELECT labelset, items_done FROM classify_labelsets WHERE state='building' ORDER BY labelset DESC LIMIT 1",
                r => ((int)r.GetInt64(0), r.GetInt64(1))).FirstOrDefault();
            long labelled = active == null ? 0 : c.Scalar<long>(null, "SELECT COUNT(*) FROM classify_message WHERE labelset=$l", ("$l", active.Value));
            long drain = c.Scalar<long?>(null, "SELECT seq FROM classify_cursors WHERE name='drain'") ?? 0;
            var queue = c.Query(null, "SELECT state, COUNT(*) FROM classify_teacher_queue GROUP BY state", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
            string today = TeacherDay(now);
            int callsToday = (int)(c.Scalar<long?>(null, "SELECT calls FROM classify_teacher_calls WHERE call_day=$d", ("$d", today)) ?? 0);
            var teacherCounts = c.Query(null, "SELECT task, COUNT(*) FROM labels WHERE item_kind='message' AND model_version IN ($a,$b) GROUP BY task",
                r => (r.GetString(0), (int)r.GetInt64(1)), ("$a", TeacherVersion), ("$b", AdjudicationVersion)).ToDictionary(x => x.Item1, x => x.Item2);
            var gold = ClassificationReader.GoldCounts(Context.Stores);
            var heads = c.Query(null, "SELECT task, kind, version, metrics_json, gold_metrics_json FROM classify_heads WHERE state='active'",
                r => (Task: r.GetString(0), Kind: r.GetString(1), Version: r.GetInt32(2), Metrics: r.GetString(3), Gold: r.GetStringOrNull(4))).ToList();
            var activeSet = active == null ? null : LoadLabelset(active.Value);
            // Drift is measured by the rollups module on daily label counts (its table exists once it is installed).
            bool driftTable = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_drift'") > 0;
            var psi = !driftTable ? new Dictionary<string, double?>() : c.Query(null,
                "SELECT task, psi FROM rollup_drift WHERE (task, week) IN (SELECT task, MAX(week) FROM rollup_drift GROUP BY task)",
                r => (r.GetString(0), r.GetDoubleOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2);
            var tasks = ClassificationTasks.All.Select(t =>
            {
                var single = heads.FirstOrDefault(h => h.Task == t.Id && h.Kind == "single");
                var context = heads.FirstOrDefault(h => h.Task == t.Id && h.Kind == "context");
                ClassifierMetrics? m = single.Metrics == null ? null : JsonSerializer.Deserialize<ClassifierMetrics>(single.Metrics, KernelJson.Options);
                ClassifierMetrics? g = single.Gold == null ? null : JsonSerializer.Deserialize<ClassifierMetrics>(single.Gold, KernelJson.Options);
                return new TaskHealthDto(t.Id, t.Title, single.Metrics == null ? null : single.Version, context.Metrics == null ? null : context.Version,
                    m?.MacroF1, m?.MacroF1Low, m?.Ece, m?.Coverage, g?.MacroF1, teacherCounts.GetValueOrDefault(t.Id), gold.GetValueOrDefault(t.Id),
                    activeSet?.Fusion.GetValueOrDefault(t.Id)?.Source ?? "none", psi.GetValueOrDefault(t.Id));
            }).ToList();
            var encoder = Context.Encoder;
            object? stats = encoder is IEncoderDiagnostics d ? d.Stats() : null;
            object? ivf = encoder == null || Context.Vectors == null ? null : Context.Vectors.Status(VectorSpaces.Message, encoder.ModelVersion);
            long embedded = encoder == null || Context.Vectors == null ? 0 : Context.Vectors.Count(VectorSpaces.Message, encoder.ModelVersion);
            var dto = new HealthDto(encoder?.ModelVersion ?? "none", encoder == null ? "missing" : encoder is LazyE5Encoder lazy && !lazy.Loaded ? "not loaded yet" : "ready",
                embedded, labelled, active, building == default ? null : building.Item1, building == default ? 0 : building.Item2, drain, corpusSeq,
                Math.Max(0, corpusSeq - drain), queue.GetValueOrDefault("queued"), queue.GetValueOrDefault("done"), callsToday,
                Setting(TeacherCallsPerDaySetting, 80), tasks, stats, ivf, now);
            await Context.ReadModels.PutAsync(HealthModel, "global", 1, now, drain, dto, ct).ConfigureAwait(false);
        }

        /// <summary>The teacher budget day: Klives's local calendar day (plan T4).</summary>
        internal string TeacherDay(long nowMs) =>
            Temporal.Zones.LocalDate(nowMs, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
