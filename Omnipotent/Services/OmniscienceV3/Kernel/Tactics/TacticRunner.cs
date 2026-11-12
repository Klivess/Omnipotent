using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Tactics
{
    /// <summary>Filters for listing runs; all optional, combined with AND. <see cref="ToMs"/> is exclusive.</summary>
    public sealed record TacticRunQuery
    {
        public string? TacticKey { get; init; }
        public string? State { get; init; }
        public string? SubjectKind { get; init; }
        public string? SubjectId { get; init; }
        public string? PlaybookRunId { get; init; }
        public long? FromMs { get; init; }
        public long? ToMs { get; init; }
        /// <summary>Keyset cursor: the created time and id of the last row seen, as returned in <c>NextCursor</c>.</summary>
        public string? Cursor { get; init; }
        public int Limit { get; init; } = 50;
    }

    public sealed record TacticRunPage(IReadOnlyList<TacticRunRecord> Items, string? NextCursor);

    /// <summary>
    /// The X-engine runtime (plan "Runs are durable jobs"). A run is a row in <c>tactic_runs</c> plus a kernel job:
    /// it survives restarts (stages already finished are not redone), can be cancelled, paused and resumed, re-run
    /// with new parameters or a different as-of, and records who ran what with which inputs and outputs. Progress and
    /// results stream over the live stream (<c>tactic.progress</c>, <c>tactic.finished</c>). Runs are snapshots as
    /// known at run time: one whose subjects changed afterwards reports itself stale.
    /// </summary>
    public sealed class TacticRunner : IntelModuleBase
    {
        public const string ModuleId = "core.tactics";
        public const string RunKind = "run";
        public const string PlaybookKind = "playbook";
        public const string PlaybookTickKind = "playbook-tick";
        public const int MaxSubjectsRecorded = 10_000;

        private readonly Func<IEnumerable<ITactic>> catalog;
        private Dictionary<string, ITactic>? tactics;
        /// <summary>Runs executing in this process, so pause and cancel stop them at once instead of at the next heartbeat.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> live = new(StringComparer.Ordinal);

        public TacticRunner(Func<IEnumerable<ITactic>> catalog)
        {
            this.catalog = catalog;
            Playbooks = new PlaybookEngine(this);
        }

        public PlaybookEngine Playbooks { get; }

        internal ModuleContext ContextForPlaybooks => Context;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Tactics and playbooks",
            Category = "core",
            Description = "Runs tactics as durable, resumable jobs and fires playbooks on their schedules and events.",
            DefaultLane = Lane.Cpu,
            Schedules = new[] { new ScheduleTrigger("playbooks", Every: TimeSpan.FromMinutes(1)) },
        };

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations { get; } = new[]
        {
            new Storage.Sqlite.Migration(ModuleId, 1, "tactic runs and playbook runs", """
CREATE TABLE tactic_runs(
  run_id TEXT PRIMARY KEY,
  tactic_key TEXT NOT NULL,
  tactic_version INTEGER NOT NULL,
  actor TEXT NOT NULL,
  state TEXT NOT NULL,
  input_json TEXT NOT NULL,
  as_of_ms INTEGER,
  watermark_seq INTEGER NOT NULL,
  stages_json TEXT,
  current_stage TEXT,
  progress_json TEXT,
  outputs_json TEXT,
  error TEXT,
  job_id INTEGER,
  parent_run_id TEXT,
  playbook_run_id TEXT,
  created_at_ms INTEGER NOT NULL,
  started_at_ms INTEGER,
  finished_at_ms INTEGER
);
CREATE INDEX ix_tactic_runs_created ON tactic_runs(created_at_ms, run_id);
CREATE INDEX ix_tactic_runs_tactic ON tactic_runs(tactic_key, created_at_ms);
CREATE INDEX ix_tactic_runs_live ON tactic_runs(state) WHERE state IN ('queued','running','paused');
CREATE INDEX ix_tactic_runs_playbook ON tactic_runs(playbook_run_id) WHERE playbook_run_id IS NOT NULL;
CREATE TABLE tactic_run_subjects(
  run_id TEXT NOT NULL,
  subject_kind TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  PRIMARY KEY(run_id, subject_kind, subject_id)
);
CREATE INDEX ix_tactic_run_subjects ON tactic_run_subjects(subject_kind, subject_id);
CREATE TABLE playbook_runs(
  playbook_run_id TEXT PRIMARY KEY,
  playbook_id TEXT NOT NULL,
  trigger_json TEXT,
  state TEXT NOT NULL,
  steps_json TEXT,
  error TEXT,
  job_id INTEGER,
  created_at_ms INTEGER NOT NULL,
  started_at_ms INTEGER,
  finished_at_ms INTEGER
);
CREATE INDEX ix_playbook_runs_playbook ON playbook_runs(playbook_id, created_at_ms);
CREATE TABLE tactics_cursors(
  name TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
"""),
        };

        private Dictionary<string, ITactic> Tactics_
        {
            get
            {
                if (tactics != null) return tactics;
                var map = new Dictionary<string, ITactic>(StringComparer.Ordinal);
                foreach (var t in catalog())
                {
                    if (!map.TryAdd(t.Descriptor.Key, t))
                        throw new InvalidOperationException($"Two tactics share the key '{t.Descriptor.Key}'.");
                }
                return tactics = map;
            }
        }

        public IReadOnlyList<TacticDescriptor> Catalog(IReadOnlyCollection<SelectionKind>? accepts = null) =>
            Tactics_.Values.Select(t => t.Descriptor)
                .Where(d => accepts == null || accepts.Count == 0 || accepts.All(k => d.Accepts.Contains(k)))
                .OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Key, StringComparer.Ordinal)
                .ToList();

        public ITactic? Find(string key) => Tactics_.GetValueOrDefault(key);

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            // Event-triggered playbooks react to everything published from now on (never to history replayed at first start).
            long latest = context.Events.Bounds().LatestEver;
            await context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT OR IGNORE INTO tactics_cursors(name, value) VALUES('playbook-events',$v)", ("$v", latest)), ct).ConfigureAwait(false);
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context) =>
            context.DueSchedules.Any(s => s.Name == "playbooks")
                ? new[] { Job(PlaybookTickKind) }
                : Array.Empty<JobRequest>();

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            RunKind => await ExecuteRunAsync(job).ConfigureAwait(false),
            PlaybookKind => await Playbooks.ExecuteRunAsync(job).ConfigureAwait(false),
            PlaybookTickKind => await Playbooks.TickAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'."),
        };

        // ── starting runs ────────────────────────────────────────────────────────

        /// <summary>
        /// Validates and queues a run. Throws <see cref="TacticRefusedException"/> with the reason when the input does
        /// not fit the tactic or policy forbids it (a minor subject for a restricted tactic).
        /// </summary>
        public async Task<string> StartAsync(string tacticKey, TacticInput input, string actor, string? parentRunId = null, string? playbookRunId = null,
            CancellationToken ct = default)
        {
            var tactic = Find(tacticKey) ?? throw new KeyNotFoundException($"No tactic '{tacticKey}'.");
            var d = tactic.Descriptor;
            string? problem = CheckShape(d, input) ?? tactic.Validate(input) ?? CheckMinors(d, input) ?? CheckAnalysisOff(d, input);
            if (problem != null) throw new TacticRefusedException(problem);

            long now = Context.Clock.NowMs;
            long watermark;
            using (var cr = Context.Stores.Corpus.OpenRead()) watermark = ChangeLog.MaxSeq(cr);
            string runId = NewRunId(now);
            string inputJson = JsonSerializer.Serialize(input, KernelJson.Options);
            var subjects = input.Selections.SelectMany(s => s.Items.Select(i => (Kind: s.Kind.ToString(), i.Id)))
                .Distinct().Take(MaxSubjectsRecorded).ToList();

            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO tactic_runs(run_id, tactic_key, tactic_version, actor, state, input_json, as_of_ms, watermark_seq, parent_run_id, playbook_run_id, created_at_ms)
                    VALUES($id,$t,$v,$a,'queued',$in,$asof,$w,$parent,$pb,$now)
                    """, ("$id", runId), ("$t", d.Key), ("$v", d.Version), ("$a", actor), ("$in", inputJson), ("$asof", input.AsOfMs), ("$w", watermark),
                    ("$parent", parentRunId), ("$pb", playbookRunId), ("$now", now));
                using var subject = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO tactic_run_subjects(run_id, subject_kind, subject_id) VALUES($r,$k,$i)", "$r", "$k", "$i");
                foreach (var (kind, id) in subjects) subject.Run(runId, kind, id);
            }, ct).ConfigureAwait(false);
            await EnqueueRunJobAsync(runId, d, ct).ConfigureAwait(false);
            await PublishAsync(Topics.TacticProgress, runId, d.Key, new { state = TacticRunStates.Queued }, ct).ConfigureAwait(false);
            return runId;
        }

        private async Task EnqueueRunJobAsync(string runId, TacticDescriptor d, CancellationToken ct)
        {
            long jobId = await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ModuleId, Kind = RunKind, SubjectKind = "tactic_run", SubjectId = runId,
                DedupeKey = JobRequest.KeyFor(ModuleId, RunKind, "tactic_run", runId),
                Lane = d.Lane, Priority = 200, MaxAttempts = 3, RunId = runId,
            }, ct).ConfigureAwait(false);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE tactic_runs SET job_id=$j WHERE run_id=$r", ("$j", jobId), ("$r", runId)), ct).ConfigureAwait(false);
        }

        internal static string NewRunId(long nowMs) =>
            "run_" + nowMs.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..10];

        /// <summary>The descriptor's shape rules: how many selections, of which kinds.</summary>
        public static string? CheckShape(TacticDescriptor d, TacticInput input)
        {
            int n = input.Selections.Count;
            if (n < d.MinSelections) return $"{d.Title} needs at least {d.MinSelections} selection(s); got {n}.";
            if (n > d.MaxSelections) return $"{d.Title} takes at most {d.MaxSelections} selection(s); got {n}.";
            foreach (var s in input.Selections)
                if (!d.Accepts.Contains(s.Kind))
                    return $"{d.Title} does not accept a {s.Kind} selection (accepts: {string.Join(", ", d.Accepts)}).";
            return null;
        }

        /// <summary>Analysis switched off (N20): no tactic runs on a person Klives asked Omniscience to stop analysing.</summary>
        private string? CheckAnalysisOff(TacticDescriptor d, TacticInput input)
        {
            var persons = input.Selections.Where(s => s.Kind == SelectionKind.Person).SelectMany(s => s.Items).ToList();
            if (persons.Count == 0) return null;
            var off = AnalysisOff.Load(Context.Stores);
            if (!off.Any) return null;
            foreach (var item in persons)
                if (off.Person(item.Id) || off.Person(Context.Policy.Canonical(item.Id)))
                    return $"{d.Title} is not available for {item.Label ?? item.Id}: analysis is switched off for them. It can be switched back on under Data controls.";
            return null;
        }

        /// <summary>MinorGuard: a restricted tactic is refused, with the reason, when any person in the input is or may be a minor.</summary>
        private string? CheckMinors(TacticDescriptor d, TacticInput input)
        {
            if (d.MinorRestriction is not { } capability) return null;
            long at = input.AsOfMs ?? Context.Clock.NowMs;
            foreach (var s in input.Selections.Where(s => s.Kind == SelectionKind.Person))
                foreach (var item in s.Items)
                {
                    var band = Context.Policy.BandOf(item.Id, at);
                    if (!MinorGuard.Allows(capability, band))
                        return $"{d.Title} is not available for {item.Label ?? item.Id}: they are, or may be, under 18 ({band}). Minors are excluded from {capability}.";
                }
            return null;
        }

        // ── executing runs ───────────────────────────────────────────────────────

        private async Task<string?> ExecuteRunAsync(JobContext job)
        {
            string runId = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Run job without a run id.");
            var run = Get(runId) ?? throw new JobFailureException(ErrorClass.Permanent, $"Run {runId} no longer exists.");
            if (TacticRunStates.IsTerminal(run.State) || run.State == TacticRunStates.Paused) return null; // cancelled or paused before it started
            var tactic = Find(run.TacticKey);
            if (tactic == null)
            {
                await FinishAsync(runId, run.TacticKey, TacticRunStates.Failed, null, $"The tactic '{run.TacticKey}' is no longer installed.", job.Cancellation).ConfigureAwait(false);
                return null;
            }
            var input = JsonSerializer.Deserialize<TacticInput>(run.InputJson, KernelJson.Options)!;
            long now = Context.Clock.NowMs;
            int claimed = await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE tactic_runs SET state='running', started_at_ms=COALESCE(started_at_ms,$n) WHERE run_id=$r AND state IN ('queued','running')",
                ("$n", now), ("$r", runId)), job.Cancellation).ConfigureAwait(false);
            if (claimed == 0) return null; // paused or cancelled between the lease and here
            await PublishAsync(Topics.TacticProgress, runId, run.TacticKey, new { state = TacticRunStates.Running }, job.Cancellation).ConfigureAwait(false);

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation);
            live[runId] = runCts;
            var context = new TacticRunContext(runId, input, run.Actor, run.AsOfMs ?? run.CreatedAtMs, Context, Context.Policy.Canonical, runCts.Token,
                stage => Task.FromResult(LoadStage(runId, stage)),
                (stage, json) => SaveStageAsync(runId, stage, json, job.Cancellation),
                partial => ProgressAsync(runId, run.TacticKey, partial, job));
            try
            {
                var outputs = await tactic.RunAsync(context).ConfigureAwait(false);
                await FinishAsync(runId, run.TacticKey, TacticRunStates.Succeeded, outputs, null, job.Cancellation).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { runId, outputs = outputs.Count }, KernelJson.Options);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested && !job.Cancellation.IsCancellationRequested)
            {
                // Paused or cancelled through the API: the run row already records it; the job has nothing left to do.
                return null;
            }
            catch (TacticRefusedException ex)
            {
                await FinishAsync(runId, run.TacticKey, TacticRunStates.Failed, null, ex.Message, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (JobFailureException ex) when (ex.Class == ErrorClass.Permanent || job.Job.Attempts >= job.Job.MaxAttempts)
            {
                await FinishAsync(runId, run.TacticKey, TacticRunStates.Failed, null, ex.Message, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && job.Job.Attempts >= job.Job.MaxAttempts)
            {
                await FinishAsync(runId, run.TacticKey, TacticRunStates.Failed, null, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            finally
            {
                live.TryRemove(runId, out _);
            }
        }

        private void StopLive(string runId)
        {
            if (!live.TryGetValue(runId, out var cts)) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the run finished meanwhile */ return; }
        }

        private string? LoadStage(string runId, string stage)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT stages_json FROM tactic_runs WHERE run_id=$r", ("$r", runId));
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(stage, out var v) ? v.GetRawText() : null;
        }

        private Task SaveStageAsync(string runId, string stage, string resultJson, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE tactic_runs SET stages_json=json_set(COALESCE(stages_json,'{}'), $path, json($v)), current_stage=$s WHERE run_id=$r",
                    ("$path", "$." + JsonPathKey(stage)), ("$v", resultJson), ("$s", stage), ("$r", runId));
            }, ct);

        /// <summary>Quotes a stage name for a JSON path (stage names may contain dots or spaces).</summary>
        private static string JsonPathKey(string stage) => "\"" + stage.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private async Task ProgressAsync(string runId, string tacticKey, object partial, JobContext job)
        {
            string json = JsonSerializer.Serialize(partial, KernelJson.Options);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE tactic_runs SET progress_json=$p WHERE run_id=$r", ("$p", json), ("$r", runId)), job.Cancellation).ConfigureAwait(false);
            await PublishAsync(Topics.TacticProgress, runId, tacticKey, partial, job.Cancellation).ConfigureAwait(false);
            await job.ReportProgressAsync(partial).ConfigureAwait(false);
        }

        private async Task FinishAsync(string runId, string tacticKey, string state, IReadOnlyList<TacticOutput>? outputs, string? error, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string? outputsJson = outputs == null ? null : JsonSerializer.Serialize(outputs, KernelJson.Options);
            bool changed = await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE tactic_runs SET state=$s, outputs_json=COALESCE($o, outputs_json), error=$e, finished_at_ms=$n
                WHERE run_id=$r AND state NOT IN ('succeeded','failed','cancelled')
                """, ("$s", state), ("$o", outputsJson), ("$e", error), ("$n", now), ("$r", runId)) == 1, ct).ConfigureAwait(false);
            if (changed) await PublishAsync(Topics.TacticFinished, runId, tacticKey, new { state, error, outputs = outputs?.Count ?? 0 }, ct).ConfigureAwait(false);
        }

        private Task PublishAsync(string topic, string runId, string tacticKey, object detail, CancellationToken ct) =>
            Context.Events.PublishAsync(topic, new { runId, tactic = tacticKey, detail }, ct);

        // ── controlling runs ─────────────────────────────────────────────────────

        public async Task<bool> CancelAsync(string runId, string actor, CancellationToken ct = default)
        {
            var run = Get(runId) ?? throw new KeyNotFoundException(runId);
            if (TacticRunStates.IsTerminal(run.State)) return false;
            await FinishAsync(runId, run.TacticKey, TacticRunStates.Cancelled, null, $"Cancelled by {actor}.", ct).ConfigureAwait(false);
            if (run.JobId is long job) await Context.Jobs.CancelAsync(job, ct).ConfigureAwait(false);
            StopLive(runId);
            return true;
        }

        /// <summary>Stops a run but keeps its finished stages; <see cref="ResumeAsync"/> continues from there.</summary>
        public async Task<bool> PauseAsync(string runId, CancellationToken ct = default)
        {
            var run = Get(runId) ?? throw new KeyNotFoundException(runId);
            if (run.State is not (TacticRunStates.Queued or TacticRunStates.Running)) return false;
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE tactic_runs SET state='paused' WHERE run_id=$r AND state IN ('queued','running')", ("$r", runId)), ct).ConfigureAwait(false);
            if (run.JobId is long job) await Context.Jobs.CancelAsync(job, ct).ConfigureAwait(false);
            StopLive(runId);
            await PublishAsync(Topics.TacticProgress, runId, run.TacticKey, new { state = TacticRunStates.Paused }, ct).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> ResumeAsync(string runId, CancellationToken ct = default)
        {
            var run = Get(runId) ?? throw new KeyNotFoundException(runId);
            if (run.State != TacticRunStates.Paused) return false;
            var tactic = Find(run.TacticKey) ?? throw new TacticRefusedException($"The tactic '{run.TacticKey}' is no longer installed.");
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE tactic_runs SET state='queued' WHERE run_id=$r AND state='paused'", ("$r", runId)), ct).ConfigureAwait(false);
            await EnqueueRunJobAsync(runId, tactic.Descriptor, ct).ConfigureAwait(false);
            await PublishAsync(Topics.TacticProgress, runId, run.TacticKey, new { state = TacticRunStates.Queued, resumed = true }, ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>A new run of the same tactic and selections, optionally with new parameters or a different as-of.</summary>
        public Task<string> RerunAsync(string runId, string actor, JsonElement? parameters = null, long? asOfMs = null, bool clearAsOf = false, CancellationToken ct = default)
        {
            var run = Get(runId) ?? throw new KeyNotFoundException(runId);
            var input = JsonSerializer.Deserialize<TacticInput>(run.InputJson, KernelJson.Options)!;
            var next = input with
            {
                Parameters = parameters ?? input.Parameters,
                AsOfMs = clearAsOf ? null : asOfMs ?? input.AsOfMs,
            };
            return StartAsync(run.TacticKey, next, actor, parentRunId: runId, ct: ct);
        }

        // ── reading runs ─────────────────────────────────────────────────────────

        private const string RunColumns =
            "run_id, tactic_key, tactic_version, actor, state, input_json, as_of_ms, watermark_seq, current_stage, progress_json, outputs_json, error, " +
            "job_id, parent_run_id, playbook_run_id, created_at_ms, started_at_ms, finished_at_ms";

        private static TacticRunRecord MapRun(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64OrNull(6), r.GetInt64(7),
            r.GetStringOrNull(8), r.GetStringOrNull(9), r.GetStringOrNull(10), r.GetStringOrNull(11), r.GetInt64OrNull(12), r.GetStringOrNull(13),
            r.GetStringOrNull(14), r.GetInt64(15), r.GetInt64OrNull(16), r.GetInt64OrNull(17));

        public TacticRunRecord? Get(string runId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, $"SELECT {RunColumns} FROM tactic_runs WHERE run_id=$r", MapRun, ("$r", runId)).FirstOrDefault();
        }

        public TacticRunPage List(TacticRunQuery q)
        {
            int limit = Math.Clamp(q.Limit, 1, 500);
            var where = new List<string>();
            var p = new List<(string, object?)>();
            if (q.TacticKey != null) { where.Add("tactic_key=$t"); p.Add(("$t", q.TacticKey)); }
            if (q.State != null) { where.Add("state=$s"); p.Add(("$s", q.State)); }
            if (q.PlaybookRunId != null) { where.Add("playbook_run_id=$pb"); p.Add(("$pb", q.PlaybookRunId)); }
            if (q.FromMs is long f) { where.Add("created_at_ms >= $f"); p.Add(("$f", f)); }
            if (q.ToMs is long t) { where.Add("created_at_ms < $to"); p.Add(("$to", t)); }
            if (q.SubjectId != null)
            {
                where.Add("run_id IN (SELECT run_id FROM tactic_run_subjects WHERE subject_id=$sid AND ($sk IS NULL OR subject_kind=$sk))");
                p.Add(("$sid", q.SubjectId));
                p.Add(("$sk", q.SubjectKind));
            }
            if (q.Cursor != null)
            {
                int bar = q.Cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(q.Cursor.AsSpan(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out long cAt))
                    throw new ArgumentException("Malformed cursor.");
                where.Add("(created_at_ms < $cat OR (created_at_ms = $cat AND run_id < $cid))");
                p.Add(("$cat", cAt));
                p.Add(("$cid", q.Cursor[(bar + 1)..]));
            }
            p.Add(("$lim", limit + 1));
            using var c = Context.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"SELECT {RunColumns} FROM tactic_runs {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY created_at_ms DESC, run_id DESC LIMIT $lim",
                MapRun, p.ToArray());
            if (rows.Count <= limit) return new TacticRunPage(rows, null);
            var page = rows.Take(limit).ToList();
            return new TacticRunPage(page, page[^1].CreatedAtMs.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].RunId);
        }

        /// <summary>
        /// True when anything the run read has changed since it ran (an edit, deletion, merge or new evidence about
        /// its subjects): the run viewer then shows "stale — re-run" (plan X-engine).
        /// </summary>
        public bool IsStale(TacticRunRecord run)
        {
            if (!TacticRunStates.IsTerminal(run.State)) return false;
            List<string> subjects;
            using (var c = Context.Stores.Intel.OpenRead())
                subjects = c.Query(null, "SELECT subject_id FROM tactic_run_subjects WHERE run_id=$r LIMIT 2000", r => r.GetString(0), ("$r", run.RunId));
            if (subjects.Count == 0) return false;
            using var corpus = Context.Stores.Corpus.OpenRead();
            foreach (var chunk in subjects.Chunk(500))
            {
                var names = chunk.Select((_, i) => "$k" + i).ToArray();
                var args = chunk.Select((id, i) => (names[i], (object?)id)).Append(("$w", run.WatermarkSeq)).ToArray();
                if (corpus.Scalar<long?>(null, $"SELECT 1 FROM change_log WHERE entity_id IN ({string.Join(",", names)}) AND seq > $w LIMIT 1", args) != null)
                    return true;
                var persons = chunk.Select((_, i) => names[i]);
                if (corpus.Scalar<long?>(null, $"SELECT 1 FROM change_log WHERE person_id IN ({string.Join(",", persons)}) AND seq > $w LIMIT 1", args) != null)
                    return true;
            }
            return false;
        }
    }
}
