using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Tactics
{
    /// <summary>Where a step's selections come from: the trigger, an earlier step's output, or fixed selections.</summary>
    public sealed record PlaybookStepInput(string From, int? Step = null, int? Output = null, IReadOnlyList<Selection>? Selections = null)
    {
        public const string FromTrigger = "trigger";
        public const string FromStep = "step";
        public const string FromLiteral = "literal";
    }

    public sealed record PlaybookStep(string Tactic, JsonElement? Parameters = null, PlaybookStepInput? Input = null);

    public sealed record PlaybookDefinition(IReadOnlyList<PlaybookStep> Steps);

    public enum PlaybookTriggerKind { Manual, Daily, Weekly, Every, Event }

    /// <summary>
    /// When a playbook fires. Daily/Weekly times are wall-clock times in Klives's zone (DST-safe; a run missed while the
    /// service was down fires once, late). Event triggers match a live-stream topic prefix and payload fields.
    /// </summary>
    public sealed record PlaybookTrigger(
        PlaybookTriggerKind Kind,
        string? At = null,
        IReadOnlyList<DayOfWeek>? Days = null,
        long? EveryMs = null,
        string? Topic = null,
        IReadOnlyDictionary<string, string>? Match = null,
        long? MinIntervalMs = null);

    public sealed record Playbook(string Id, string Name, PlaybookDefinition Definition, PlaybookTrigger Trigger, bool Enabled, long? LastRunMs, long CreatedAtMs, long UpdatedAtMs);

    public sealed record PlaybookRunRecord(string PlaybookRunId, string PlaybookId, string? TriggerJson, string State, string? StepsJson, string? Error, long CreatedAtMs, long? StartedAtMs, long? FinishedAtMs);

    /// <summary>
    /// Playbooks (plan X-engine): saved chains of tactics. Definitions are Klives-authored (operator.db); runs are
    /// derived (intel.db). A playbook run is a resumable job that starts each step as an ordinary tactic run (so lanes,
    /// MinorGuard, audit and the run viewer all apply), waits for it and feeds its output into the next step.
    /// </summary>
    public sealed class PlaybookEngine
    {
        public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMinutes(1);
        private static readonly string[] ForbiddenEventTopics = { "tactic.", "readmodel.", "stream." };
        private const string EventCursor = "playbook-events";

        private readonly TacticRunner runner;

        internal PlaybookEngine(TacticRunner runner) => this.runner = runner;

        private Modules.ModuleContext Context => runner.ContextForPlaybooks;

        // ── definitions ──────────────────────────────────────────────────────────

        /// <summary>Every problem with a definition and trigger (empty when valid).</summary>
        public IReadOnlyList<string> Validate(PlaybookDefinition definition, PlaybookTrigger trigger)
        {
            var problems = new List<string>();
            if (definition.Steps.Count == 0) problems.Add("A playbook needs at least one step.");
            for (int i = 0; i < definition.Steps.Count; i++)
            {
                var step = definition.Steps[i];
                var tactic = runner.Find(step.Tactic);
                if (tactic == null) { problems.Add($"Step {i + 1}: no tactic '{step.Tactic}'."); continue; }
                if (tactic.Descriptor.Outbound) problems.Add($"Step {i + 1}: {tactic.Descriptor.Title} acts on the outside world and needs a confirmation each time; it cannot run unattended.");
                var input = step.Input;
                if (input == null) continue;
                switch (input.From)
                {
                    case PlaybookStepInput.FromTrigger:
                        break;
                    case PlaybookStepInput.FromStep:
                        if (input.Step is not int s || s < 0 || s >= i) problems.Add($"Step {i + 1}: input must come from an earlier step.");
                        break;
                    case PlaybookStepInput.FromLiteral:
                        if (input.Selections == null || input.Selections.Count == 0) problems.Add($"Step {i + 1}: literal input has no selections.");
                        break;
                    default:
                        problems.Add($"Step {i + 1}: unknown input source '{input.From}'.");
                        break;
                }
            }
            switch (trigger.Kind)
            {
                case PlaybookTriggerKind.Daily or PlaybookTriggerKind.Weekly:
                    if (!TimeOnly.TryParseExact(trigger.At ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        problems.Add("A daily or weekly trigger needs 'at' as HH:mm (Klives's local time).");
                    if (trigger.Kind == PlaybookTriggerKind.Weekly && (trigger.Days == null || trigger.Days.Count == 0))
                        problems.Add("A weekly trigger needs at least one day.");
                    break;
                case PlaybookTriggerKind.Every:
                    if (trigger.EveryMs is not long every || every < 60_000) problems.Add("An interval trigger must be at least one minute.");
                    break;
                case PlaybookTriggerKind.Event:
                    if (string.IsNullOrWhiteSpace(trigger.Topic)) problems.Add("An event trigger needs a topic.");
                    else if (ForbiddenEventTopics.Any(t => trigger.Topic.StartsWith(t, StringComparison.Ordinal)))
                        problems.Add($"Event triggers cannot listen to '{trigger.Topic}': playbooks would trigger themselves.");
                    break;
            }
            return problems;
        }

        public async Task<Playbook> SaveAsync(string? id, string name, PlaybookDefinition definition, PlaybookTrigger trigger, bool enabled, string actor, CancellationToken ct = default)
        {
            var problems = Validate(definition, trigger);
            if (problems.Count > 0) throw new TacticRefusedException(string.Join(" ", problems));
            if (string.IsNullOrWhiteSpace(name)) throw new TacticRefusedException("A playbook needs a name.");
            long now = Context.Clock.NowMs;
            string playbookId = string.IsNullOrWhiteSpace(id) ? "pb_" + Guid.NewGuid().ToString("N")[..12] : id;
            string def = JsonSerializer.Serialize(definition, KernelJson.Options);
            string trig = JsonSerializer.Serialize(trigger, KernelJson.Options);
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO playbooks(playbook_id, name, definition_json, trigger_json, enabled, created_at_ms, updated_at_ms) VALUES($id,$n,$d,$t,$e,$now,$now)
                    ON CONFLICT(playbook_id) DO UPDATE SET name=excluded.name, definition_json=excluded.definition_json, trigger_json=excluded.trigger_json,
                        enabled=excluded.enabled, updated_at_ms=excluded.updated_at_ms
                    """, ("$id", playbookId), ("$n", name.Trim()), ("$d", def), ("$t", trig), ("$e", enabled ? 1 : 0), ("$now", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'playbook.save',$id,$d)",
                    ("$t", now), ("$a", actor), ("$id", playbookId), ("$d", JsonSerializer.Serialize(new { name, enabled, trigger.Kind }, KernelJson.Options)));
            }, ct).ConfigureAwait(false);
            return Get(playbookId)!;
        }

        public IReadOnlyList<Playbook> List()
        {
            using var c = Context.Stores.Operator.OpenRead();
            return c.Query(null, "SELECT playbook_id, name, definition_json, trigger_json, enabled, last_run_ms, created_at_ms, updated_at_ms FROM playbooks ORDER BY name", Map);
        }

        public Playbook? Get(string playbookId)
        {
            using var c = Context.Stores.Operator.OpenRead();
            return c.Query(null, "SELECT playbook_id, name, definition_json, trigger_json, enabled, last_run_ms, created_at_ms, updated_at_ms FROM playbooks WHERE playbook_id=$id",
                Map, ("$id", playbookId)).FirstOrDefault();
        }

        private static Playbook Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1),
            JsonSerializer.Deserialize<PlaybookDefinition>(r.GetString(2), KernelJson.Options)!,
            JsonSerializer.Deserialize<PlaybookTrigger>(r.GetString(3), KernelJson.Options)!,
            r.GetInt32(4) == 1, r.GetInt64OrNull(5), r.GetInt64(6), r.GetInt64(7));

        // ── runs ─────────────────────────────────────────────────────────────────

        /// <summary>Starts a run of a playbook (manual starts and fired triggers alike).</summary>
        public async Task<string> StartAsync(string playbookId, string actor, JsonElement? triggerPayload = null, CancellationToken ct = default)
        {
            var playbook = Get(playbookId) ?? throw new KeyNotFoundException($"No playbook '{playbookId}'.");
            long now = Context.Clock.NowMs;
            string runId = "pbr_" + now.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
            string trigger = JsonSerializer.Serialize(new { actor, payload = triggerPayload }, KernelJson.Options);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "INSERT INTO playbook_runs(playbook_run_id, playbook_id, trigger_json, state, steps_json, created_at_ms) VALUES($r,$p,$t,'queued','[]',$n)",
                ("$r", runId), ("$p", playbook.Id), ("$t", trigger), ("$n", now)), ct).ConfigureAwait(false);
            long job = await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = TacticRunner.ModuleId, Kind = TacticRunner.PlaybookKind, SubjectKind = "playbook_run", SubjectId = runId,
                DedupeKey = JobRequest.KeyFor(TacticRunner.ModuleId, TacticRunner.PlaybookKind, "playbook_run", runId), Priority = 150, MaxAttempts = 3,
                // It waits for its steps: on its own lane it can never hold the worker a step needs.
                Lane = Lane.Coordinator,
            }, ct).ConfigureAwait(false);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE playbook_runs SET job_id=$j WHERE playbook_run_id=$r", ("$j", job), ("$r", runId)), ct).ConfigureAwait(false);
            return runId;
        }

        public PlaybookRunRecord? GetRun(string playbookRunId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT playbook_run_id, playbook_id, trigger_json, state, steps_json, error, created_at_ms, started_at_ms, finished_at_ms FROM playbook_runs WHERE playbook_run_id=$r",
                r => new PlaybookRunRecord(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6),
                    r.GetInt64OrNull(7), r.GetInt64OrNull(8)), ("$r", playbookRunId)).FirstOrDefault();
        }

        public IReadOnlyList<PlaybookRunRecord> Runs(string playbookId, int limit = 50)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT playbook_run_id, playbook_id, trigger_json, state, steps_json, error, created_at_ms, started_at_ms, finished_at_ms FROM playbook_runs WHERE playbook_id=$p ORDER BY created_at_ms DESC LIMIT $l",
                r => new PlaybookRunRecord(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6),
                    r.GetInt64OrNull(7), r.GetInt64OrNull(8)), ("$p", playbookId), ("$l", Math.Clamp(limit, 1, 500)));
        }

        private sealed record StepState(int Step, string RunId);

        internal async Task<string?> ExecuteRunAsync(JobContext job)
        {
            string runId = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Playbook job without a run id.");
            var run = GetRun(runId) ?? throw new JobFailureException(ErrorClass.Permanent, $"Playbook run {runId} no longer exists.");
            if (run.State is "succeeded" or "failed" or "cancelled") return null;
            var playbook = Get(run.PlaybookId);
            if (playbook == null)
            {
                await FinishAsync(runId, "failed", "The playbook was deleted.", job.Cancellation).ConfigureAwait(false);
                return null;
            }
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE playbook_runs SET state='running', started_at_ms=COALESCE(started_at_ms,$n) WHERE playbook_run_id=$r",
                ("$n", Context.Clock.NowMs), ("$r", runId)), job.Cancellation).ConfigureAwait(false);

            var steps = JsonSerializer.Deserialize<List<StepState>>(run.StepsJson ?? "[]", KernelJson.Options) ?? new List<StepState>();
            JsonElement? triggerPayload = null;
            if (run.TriggerJson != null)
            {
                using var doc = JsonDocument.Parse(run.TriggerJson);
                if (doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null) triggerPayload = p.Clone();
            }
            var outputs = new List<IReadOnlyList<TacticOutput>>();
            for (int i = 0; i < playbook.Definition.Steps.Count; i++)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                var step = playbook.Definition.Steps[i];
                string tacticRun;
                var existing = steps.FirstOrDefault(s => s.Step == i);
                if (existing != null) tacticRun = existing.RunId; // resumed after a restart: re-attach, never start twice
                else
                {
                    var input = new TacticInput(ResolveInput(step.Input, triggerPayload, outputs), step.Parameters);
                    try
                    {
                        tacticRun = await runner.StartAsync(step.Tactic, input, $"playbook:{playbook.Id}", playbookRunId: runId, ct: job.Cancellation).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TacticRefusedException or KeyNotFoundException)
                    {
                        await FinishAsync(runId, "failed", $"Step {i + 1} ({step.Tactic}) was refused: {ex.Message}", job.Cancellation).ConfigureAwait(false);
                        return null;
                    }
                    steps.Add(new StepState(i, tacticRun));
                    string stepsJson = JsonSerializer.Serialize(steps, KernelJson.Options);
                    await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE playbook_runs SET steps_json=$s WHERE playbook_run_id=$r", ("$s", stepsJson), ("$r", runId)),
                        job.Cancellation).ConfigureAwait(false);
                }
                var finished = await WaitAsync(tacticRun, job).ConfigureAwait(false);
                if (finished.State != TacticRunStates.Succeeded)
                {
                    await FinishAsync(runId, "failed", $"Step {i + 1} ({step.Tactic}) ended {finished.State}: {finished.Error}", job.Cancellation).ConfigureAwait(false);
                    return null;
                }
                outputs.Add(JsonSerializer.Deserialize<List<TacticOutput>>(finished.OutputsJson ?? "[]", KernelJson.Options) ?? new List<TacticOutput>());
            }
            await FinishAsync(runId, "succeeded", null, job.Cancellation).ConfigureAwait(false);
            await Context.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "UPDATE playbooks SET last_run_ms=MAX(COALESCE(last_run_ms,0),$n) WHERE playbook_id=$p",
                ("$n", run.CreatedAtMs), ("$p", playbook.Id)), job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { runId, steps = steps.Count }, KernelJson.Options);
        }

        private static IReadOnlyList<Selection> ResolveInput(PlaybookStepInput? input, JsonElement? trigger, List<IReadOnlyList<TacticOutput>> outputs)
        {
            if (input == null) return Array.Empty<Selection>();
            switch (input.From)
            {
                case PlaybookStepInput.FromLiteral:
                    return input.Selections ?? (IReadOnlyList<Selection>)Array.Empty<Selection>();
                case PlaybookStepInput.FromStep:
                    var produced = outputs[input.Step!.Value];
                    var selection = produced.Where(o => o.Selection != null).Skip(input.Output ?? 0).Select(o => o.Selection!).FirstOrDefault();
                    return selection == null ? Array.Empty<Selection>() : new[] { selection };
                default:
                    return SelectionsFromTrigger(trigger);
            }
        }

        /// <summary>Turns an event payload into selections: explicit <c>selections</c>, or the ids it names.</summary>
        internal static IReadOnlyList<Selection> SelectionsFromTrigger(JsonElement? payload)
        {
            if (payload is not { ValueKind: JsonValueKind.Object } p) return Array.Empty<Selection>();
            if (p.TryGetProperty("selections", out var s) && s.ValueKind == JsonValueKind.Array)
                return s.Deserialize<List<Selection>>(KernelJson.Options) ?? new List<Selection>();
            var result = new List<Selection>();
            foreach (var (prop, kind) in new[] { ("personId", SelectionKind.Person), ("messageId", SelectionKind.Message), ("conversationId", SelectionKind.Conversation) })
                if (p.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } id)
                    result.Add(Selection.Of(kind, new[] { id }, "trigger"));
            return result;
        }

        private async Task<TacticRunRecord> WaitAsync(string tacticRunId, JobContext job)
        {
            string? reported = null;
            while (true)
            {
                var run = runner.Get(tacticRunId) ?? throw new JobFailureException(ErrorClass.Permanent, $"Step run {tacticRunId} disappeared.");
                if (TacticRunStates.IsTerminal(run.State)) return run;
                string now = run.State + "|" + run.CurrentStage;
                if (now != reported)
                {
                    // Only on change: the lease is kept alive by the worker's own heartbeat.
                    await job.ReportProgressAsync(new { waitingOn = tacticRunId, run.State, run.CurrentStage }).ConfigureAwait(false);
                    reported = now;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), job.Cancellation).ConfigureAwait(false);
            }
        }

        private Task FinishAsync(string runId, string state, string? error, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE playbook_runs SET state=$s, error=$e, finished_at_ms=$n WHERE playbook_run_id=$r AND state NOT IN ('succeeded','failed','cancelled')",
                ("$s", state), ("$e", error), ("$n", Context.Clock.NowMs), ("$r", runId)), ct);

        // ── triggers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires due playbooks: schedules (in Klives's zone, one late run for any missed while down) and events (read
        /// from the durable live-stream log after a cursor, so no event is missed across restarts).
        /// </summary>
        internal async Task<string?> TickAsync(JobContext job)
        {
            long now = Context.Clock.NowMs;
            var zone = Context.Settings.KlivesZone;
            var playbooks = List().Where(p => p.Enabled).ToList();
            int started = 0;
            foreach (var p in playbooks.Where(p => p.Trigger.Kind is PlaybookTriggerKind.Daily or PlaybookTriggerKind.Weekly or PlaybookTriggerKind.Every))
            {
                long? due = NextDue(p, zone);
                if (due is long d && d <= now)
                {
                    await StartAsync(p.Id, "schedule", null, job.Cancellation).ConfigureAwait(false);
                    await MarkRunAsync(p.Id, now, job.Cancellation).ConfigureAwait(false);
                    started++;
                }
            }

            var eventPlaybooks = playbooks.Where(p => p.Trigger.Kind == PlaybookTriggerKind.Event).ToList();
            long cursor;
            using (var c = Context.Stores.Intel.OpenRead())
                cursor = c.Scalar<long?>(null, "SELECT value FROM tactics_cursors WHERE name=$n", ("$n", EventCursor)) ?? Context.Events.Bounds().LatestEver;
            var events = Context.Events.Replay(cursor, 1000);
            var lastRun = eventPlaybooks.ToDictionary(p => p.Id, p => p.LastRunMs ?? 0, StringComparer.Ordinal);
            foreach (var ev in events)
            {
                foreach (var p in eventPlaybooks)
                {
                    var t = p.Trigger;
                    if (t.Topic == null || !ev.Topic.StartsWith(t.Topic, StringComparison.Ordinal)) continue;
                    long minInterval = t.MinIntervalMs ?? (long)DefaultMinInterval.TotalMilliseconds;
                    if (now - lastRun[p.Id] < minInterval) continue;
                    using var doc = JsonDocument.Parse(ev.PayloadJson);
                    if (!Matches(doc.RootElement, t.Match)) continue;
                    await StartAsync(p.Id, $"event:{ev.EventId}", doc.RootElement.Clone(), job.Cancellation).ConfigureAwait(false);
                    await MarkRunAsync(p.Id, now, job.Cancellation).ConfigureAwait(false);
                    lastRun[p.Id] = now;
                    started++;
                }
                cursor = ev.EventId;
            }
            long newCursor = events.Count > 0 ? cursor : Math.Max(cursor, 0);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "INSERT INTO tactics_cursors(name, value) VALUES($n,$v) ON CONFLICT(name) DO UPDATE SET value=MAX(value, excluded.value)",
                ("$n", EventCursor), ("$v", newCursor)), job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { started }, KernelJson.Options);
        }

        private Task MarkRunAsync(string playbookId, long now, CancellationToken ct) =>
            Context.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "UPDATE playbooks SET last_run_ms=$n WHERE playbook_id=$p", ("$n", now), ("$p", playbookId)), ct);

        /// <summary>The next instant a scheduled playbook is due after its last run (or its creation).</summary>
        public static long? NextDue(Playbook p, TimeZoneInfo zone)
        {
            long after = p.LastRunMs ?? p.CreatedAtMs;
            var t = p.Trigger;
            switch (t.Kind)
            {
                case PlaybookTriggerKind.Every:
                    return t.EveryMs is long every ? after + every : null;
                case PlaybookTriggerKind.Daily or PlaybookTriggerKind.Weekly:
                    if (!TimeOnly.TryParseExact(t.At ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
                    long candidate = Schedules.NextDailyOccurrence(after, at, zone);
                    if (t.Kind == PlaybookTriggerKind.Daily) return candidate;
                    for (int i = 0; i < 8; i++)
                    {
                        if (t.Days!.Contains(Zones.ToLocal(candidate, zone).DayOfWeek)) return candidate;
                        candidate = Schedules.NextDailyOccurrence(candidate, at, zone);
                    }
                    return null;
                default:
                    return null;
            }
        }

        private static bool Matches(JsonElement payload, IReadOnlyDictionary<string, string>? match)
        {
            if (match == null || match.Count == 0) return true;
            if (payload.ValueKind != JsonValueKind.Object) return false;
            foreach (var (key, expected) in match)
            {
                if (!payload.TryGetProperty(key, out var v)) return false;
                string? actual = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                if (!string.Equals(actual, expected, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
