using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel
{
    public sealed record KernelOptions
    {
        public IReadOnlyList<LaneOptions> Lanes { get; init; } = DefaultLanes(Environment.ProcessorCount);

        /// <summary>
        /// The worker pools for a host with <paramref name="processors"/> cores. CPU work gets half the cores, between two
        /// and four workers: the first formula (cores minus four) left the four-core server with a single worker for
        /// 450,000 queued jobs, and whatever that one worker was doing, nothing else in the lane ran. Each lane also says
        /// how long a job may go without a sign of progress before it is stopped. The limits are set for a hang, not for
        /// slowness: a replica's index stage took fifty minutes on the loaded server without reporting anything, and that
        /// is work, so CPU jobs get two hours; I/O that goes quiet for half an hour is waiting on something that will not
        /// come (on the server it was four workers, for four hours).
        /// </summary>
        public static IReadOnlyList<LaneOptions> DefaultLanes(int processors) => new[]
        {
            new LaneOptions(Lane.Cpu, Math.Clamp(processors / 2, 2, 4), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2), StallAfter: TimeSpan.FromHours(2)),
            new LaneOptions(Lane.Io, 4, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2), StallAfter: TimeSpan.FromMinutes(30)),
            new LaneOptions(Lane.LlmBackground, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), StallAfter: TimeSpan.FromMinutes(60)),
            // Klives started these himself and watches them: two of the same kind may run side by side.
            new LaneOptions(Lane.LlmInteractive, 2, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(500), StallAfter: TimeSpan.FromMinutes(60), MaxPerModule: 2),
            new LaneOptions(Lane.Coordinator, 4, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), StallAfter: TimeSpan.FromHours(24)),
        };
        public TimeSpan LoopInterval { get; init; } = TimeSpan.FromSeconds(1);
        public int ChangeBatch { get; init; } = 5_000;
        public int PlanBatch { get; init; } = 500;
        public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(15);
        /// <summary>Zero (the default): queued jobs are not aged. Workers take turns between modules, so no module can be
        /// starved by another's priorities, and ageing rewrote every queued row every ten minutes (650,000 rows on the
        /// server, a 300 MB write-ahead log). A positive value brings the old behaviour back.</summary>
        public TimeSpan PriorityAgeAfter { get; init; } = TimeSpan.Zero;
        /// <summary>How often each store's write-ahead log is checkpointed by maintenance.</summary>
        public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(2);
        public int PriorityCap { get; init; } = 1_000;
        public TimeSpan StreamRetention { get; init; } = TimeSpan.FromDays(7);
        public TimeSpan SucceededJobRetention { get; init; } = TimeSpan.FromDays(14);
        public TimeSpan DeadJobRetention { get; init; } = TimeSpan.FromDays(90);
        public string? OwnerPrefix { get; init; }
        /// <summary>When false, no worker pools start (tests drive the queue directly; shadow startup).</summary>
        public bool StartWorkers { get; init; } = true;
        /// <summary>When false, the pump/plan/maintenance loop does not run (tests call the *OnceAsync steps).</summary>
        public bool StartLoop { get; init; } = true;
    }

    /// <summary>Per-module health for the Modules page.</summary>
    public sealed record ModuleHealth(
        string ModuleId, string Title, int Version, bool Enabled, long Watermark, long Backlog, long DirtySubjects,
        long Queued, long Leased, long Dead, long? LastSucceededMs, long? LastFailedMs, string? LastError);

    /// <summary>
    /// Hosts every <see cref="IIntelModule"/>: validates the dependency graph, applies module migrations,
    /// pumps the corpus change log into per-module dirty sets (atomically with each module's watermark),
    /// plans jobs in dependency order (consuming dirty rows and enqueueing jobs in one intel transaction),
    /// fires DST-safe schedules (a missed run fires once, late), runs maintenance (lease reclamation,
    /// priority aging, retention) and dispatches jobs to modules.
    /// </summary>
    public sealed class ModuleHost : IJobDispatcher, IAsyncDisposable
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly KernelOptions options;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> logError;
        private readonly ILaneGate? gate;
        private readonly Dictionary<string, IIntelModule> modules = new(StringComparer.Ordinal);
        private List<IIntelModule> ordered = new();
        private readonly SemaphoreSlim kick = new(0, int.MaxValue);
        /// <summary>Serialises pump and plan passes: two concurrent passes could both read the same dirty rows
        /// or version flag and plan twice.</summary>
        private readonly SemaphoreSlim passGate = new(1, 1);
        private readonly CancellationTokenSource stopping = new();
        private LaneWorkers? workers;
        private Task? loop;
        private long lastMaintenanceMs;
        private long lastAgingMs;
        private long lastPruneMs;
        private long lastCheckpointMs;
        private int started;

        public ModuleHost(OmniscienceStores stores, IClock clock, KernelOptions options, Action<string> log,
            Action<string, Exception?> logError, ILaneGate? gate = null, KernelServices? services = null)
        {
            this.stores = stores;
            this.clock = clock;
            this.options = options;
            this.log = log;
            this.logError = logError;
            this.gate = gate;
            Jobs = new JobQueue(stores.Intel, clock);
            Events = new EventHub(stores.Intel, clock, logError);
            Settings = new KernelSettings(stores.Operator, clock);
            ReadModels = new ReadModels.ReadModelStore(stores.Intel, clock, Events);
            Policy = services?.Policy ?? new Knowledge.Policy.OperatorPolicyOracle(stores);
            Services = services ?? new KernelServices();
        }

        public JobQueue Jobs { get; }
        public EventHub Events { get; }
        public KernelSettings Settings { get; }
        public ReadModels.ReadModelStore ReadModels { get; }
        public Knowledge.Policy.IPolicyOracle Policy { get; }
        public KernelServices Services { get; }
        public IReadOnlyList<IIntelModule> Modules => ordered.Count > 0 ? ordered : modules.Values.ToList();

        /// <summary>The context modules receive (also used by the tactic runner to run tactics).</summary>
        public ModuleContext Context { get; private set; } = null!;

        public void Register(IIntelModule module)
        {
            if (Volatile.Read(ref started) != 0) throw new InvalidOperationException("Register modules before StartAsync.");
            var m = module.Manifest;
            ArgumentException.ThrowIfNullOrWhiteSpace(m.Id);
            if (!modules.TryAdd(m.Id, module)) throw new InvalidOperationException($"Module '{m.Id}' is registered twice.");
            foreach (var s in m.Schedules) s.Validate();
            foreach (var d in m.Datasets) d.Validate();
            foreach (var s in m.Settings)
            {
                // Namespaced keys: two modules can never collide, and the Settings page can group by owner.
                if (!s.Key.StartsWith(m.Id + ".", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Module '{m.Id}' setting '{s.Key}' must start with '{m.Id}.'.");
                string? problem;
                try
                {
                    using var parsed = JsonDocument.Parse(s.DefaultJson);
                    problem = KernelSettings.Validate(s, parsed.RootElement);
                }
                catch (JsonException)
                {
                    problem = "is not valid JSON";
                }
                if (problem != null)
                    throw new InvalidOperationException($"Module '{m.Id}' setting '{s.Key}' default {s.DefaultJson} {problem}.");
            }
            foreach (var mig in module.IntelMigrations)
                if (mig.ModuleId != m.Id) throw new InvalidOperationException($"Module '{m.Id}' ships a migration for '{mig.ModuleId}'.");
        }

        /// <summary>Topological order of the registered modules; throws on unknown dependencies or cycles.</summary>
        public static List<IIntelModule> OrderByDependencies(IReadOnlyDictionary<string, IIntelModule> all)
        {
            var inDegree = all.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
            var dependents = all.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var (id, mod) in all)
            {
                foreach (var dep in mod.Manifest.DependsOn)
                {
                    if (!all.ContainsKey(dep)) throw new InvalidOperationException($"Module '{id}' depends on unknown module '{dep}'.");
                    inDegree[id]++;
                    dependents[dep].Add(id);
                }
            }
            var ready = new SortedSet<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
            var result = new List<IIntelModule>();
            while (ready.Count > 0)
            {
                string next = ready.Min!;
                ready.Remove(next);
                result.Add(all[next]);
                foreach (var d in dependents[next])
                    if (--inDegree[d] == 0) ready.Add(d);
            }
            if (result.Count != all.Count)
            {
                var cycle = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(x => x);
                throw new InvalidOperationException("Module dependency cycle among: " + string.Join(", ", cycle));
            }
            return result;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Kernel already started.");
            ordered = OrderByDependencies(modules);

            await stores.Intel.ApplyMigrationsAsync(ordered.SelectMany(m => m.IntelMigrations), ct).ConfigureAwait(false);
            await EnsureModuleStateAsync(ct).ConfigureAwait(false);

            var context = new ModuleContext
            {
                Stores = stores, Clock = clock, Jobs = Jobs, Events = Events, Settings = Settings, Log = log, LogError = logError,
                ReadModels = ReadModels, Policy = Policy, Llm = Services.Llm, Models = Services.Models,
                Encoder = Services.Encoder, Vectors = Services.Vectors,
            };
            Context = context;
            foreach (var m in ordered) await m.InitializeAsync(context, ct).ConfigureAwait(false);

            // Crash recovery first: the instance lock means anything still leased belongs to a previous process,
            // so it is reclaimed before workers start instead of waiting for its lease to run out.
            int reclaimed = await Jobs.ReclaimOrphanedAsync(ct).ConfigureAwait(false);
            if (reclaimed > 0) log($"[OmniscienceV3] reclaimed {reclaimed} job(s) left leased by a previous run.");

            if (options.StartWorkers)
            {
                foreach (var lane in Enum.GetValues<Lane>().Where(l => !options.Lanes.Any(o => o.Lane == l && o.Concurrency > 0)))
                    logError($"[OmniscienceV3] no workers are configured for the {lane} lane: its jobs will wait until some are.", null);
                workers = new LaneWorkers(Jobs, this, clock, options.Lanes, logError, gate, options.OwnerPrefix);
                workers.Start();
            }
            if (options.StartLoop)
                loop = Task.Factory.StartNew(RunLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            log($"[OmniscienceV3] kernel online with {ordered.Count} module(s).");
        }

        /// <summary>Wakes the kernel loop now (ingest calls this after committing a batch).</summary>
        public void Kick()
        {
            if (kick.CurrentCount < 4) kick.Release();
        }

        public JobHandler? Resolve(string moduleId, string kind) =>
            modules.TryGetValue(moduleId, out var m) ? ctx => m.ExecuteAsync(ctx) : null;

        private async Task RunLoopAsync()
        {
            var token = stopping.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PumpChangesOnceAsync(token).ConfigureAwait(false);
                    await PlanOnceAsync(token).ConfigureAwait(false);
                    await RunMaintenanceIfDueAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { logError("[OmniscienceV3] kernel loop iteration failed; retrying next tick.", ex); }

                try { await kick.WaitAsync(options.LoopInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ── module state ─────────────────────────────────────────────────────────

        private sealed class StateDoc
        {
            public Dictionary<string, long> NextDue { get; set; } = new();
            public bool VersionChanged { get; set; }
        }

        private Task EnsureModuleStateAsync(CancellationToken ct) => stores.Intel.WriteAsync((c, tx) =>
        {
            long now = clock.NowMs;
            foreach (var m in ordered)
            {
                var row = c.Query(tx, "SELECT version, state_json FROM module_state WHERE module_id=$m",
                    r => (Version: r.GetInt32(0), State: r.GetStringOrNull(1)), ("$m", m.Manifest.Id)).FirstOrDefault();
                if (row == default)
                {
                    var doc = new StateDoc { VersionChanged = true };
                    c.Exec(tx, "INSERT INTO module_state(module_id, version, enabled, watermark_seq, state_json, updated_at_ms) VALUES($m,$v,1,0,$s,$t)",
                        ("$m", m.Manifest.Id), ("$v", m.Manifest.Version), ("$s", JsonSerializer.Serialize(doc, KernelJson.Options)), ("$t", now));
                }
                else if (row.Version != m.Manifest.Version)
                {
                    var doc = ParseState(row.State);
                    doc.VersionChanged = true;
                    c.Exec(tx, "UPDATE module_state SET version=$v, state_json=$s, updated_at_ms=$t WHERE module_id=$m",
                        ("$v", m.Manifest.Version), ("$s", JsonSerializer.Serialize(doc, KernelJson.Options)), ("$t", now), ("$m", m.Manifest.Id));
                    log($"[OmniscienceV3] module {m.Manifest.Id} version {row.Version} → {m.Manifest.Version}: full re-plan scheduled; old outputs keep serving.");
                }
            }
        }, ct);

        private static StateDoc ParseState(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new StateDoc();
            try { return JsonSerializer.Deserialize<StateDoc>(json, KernelJson.Options) ?? new StateDoc(); }
            catch (JsonException) { return new StateDoc(); }
        }

        public async Task SetEnabledAsync(string moduleId, bool enabled, CancellationToken ct = default)
        {
            if (!modules.ContainsKey(moduleId)) throw new KeyNotFoundException(moduleId);
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE module_state SET enabled=$e, updated_at_ms=$t WHERE module_id=$m",
                ("$e", enabled ? 1 : 0), ("$t", clock.NowMs), ("$m", moduleId)), ct).ConfigureAwait(false);
            if (!enabled) await Jobs.CancelModuleAsync(moduleId, ct).ConfigureAwait(false);
        }

        private Dictionary<string, (bool Enabled, long Watermark, StateDoc State)> ReadModuleStates()
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, "SELECT module_id, enabled, watermark_seq, state_json FROM module_state",
                    r => (Id: r.GetString(0), Enabled: r.GetInt32(1) == 1, Watermark: r.GetInt64(2), State: ParseState(r.GetStringOrNull(3))))
                .ToDictionary(x => x.Id, x => (x.Enabled, x.Watermark, x.State), StringComparer.Ordinal);
        }

        // ── change feed ──────────────────────────────────────────────────────────

        /// <summary>Moves every subscribed module's watermark forward by up to one batch. Returns rows consumed.</summary>
        public async Task<int> PumpChangesOnceAsync(CancellationToken ct = default)
        {
            await passGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await PumpChangesCoreAsync(ct).ConfigureAwait(false); }
            finally { passGate.Release(); }
        }

        private async Task<int> PumpChangesCoreAsync(CancellationToken ct)
        {
            long maxSeq;
            using (var cr = stores.Corpus.OpenRead()) maxSeq = ChangeLog.MaxSeq(cr);
            var states = ReadModuleStates();
            int consumed = 0;
            foreach (var m in ordered)
            {
                if (!states.TryGetValue(m.Manifest.Id, out var st) || !st.Enabled) continue;
                if (st.Watermark >= maxSeq) continue;
                long upper = Math.Min(maxSeq, st.Watermark + options.ChangeBatch);
                var kinds = m.Manifest.SubscribedChangeKinds;
                IReadOnlyList<ChangeRecord> changes = Array.Empty<ChangeRecord>();
                if (kinds.Count > 0)
                {
                    using var cr = stores.Corpus.OpenRead();
                    changes = ReadWindow(cr, st.Watermark, upper, kinds);
                }
                List<DirtySubject> dirty;
                try { dirty = changes.Count == 0 ? new List<DirtySubject>() : m.MapChanges(changes).Distinct().ToList(); }
                catch (Exception ex)
                {
                    // A mapping bug must not advance the watermark: the window is retried next tick.
                    logError($"[OmniscienceV3] module {m.Manifest.Id} failed to map changes ({st.Watermark}, {upper}]; watermark held.", ex);
                    continue;
                }
                long now = clock.NowMs;
                string id = m.Manifest.Id;
                await stores.Intel.WriteAsync((c, tx) =>
                {
                    foreach (var d in dirty)
                    {
                        c.Exec(tx, """
                            INSERT INTO module_dirty(module_id, subject_kind, subject_id, dirty_since_seq, dirty_at_ms) VALUES($m,$k,$i,$s,$t)
                            ON CONFLICT(module_id, subject_kind, subject_id) DO UPDATE SET dirty_since_seq=MAX(dirty_since_seq, excluded.dirty_since_seq)
                            """, ("$m", id), ("$k", d.Kind), ("$i", d.Id), ("$s", upper), ("$t", now));
                    }
                    c.Exec(tx, "UPDATE module_state SET watermark_seq=$w, updated_at_ms=$t WHERE module_id=$m AND watermark_seq < $w",
                        ("$w", upper), ("$t", now), ("$m", id));
                }, ct).ConfigureAwait(false);
                consumed += changes.Count;
            }
            return consumed;
        }

        private static IReadOnlyList<ChangeRecord> ReadWindow(SqliteConnection c, long afterSeq, long upToSeq, IReadOnlyList<string> kinds)
        {
            var parameters = new List<(string, object?)> { ("$a", afterSeq), ("$b", upToSeq) };
            var names = new List<string>();
            for (int i = 0; i < kinds.Count; i++) { names.Add("$k" + i); parameters.Add(("$k" + i, kinds[i])); }
            return c.Query(null,
                $"SELECT seq, at_ms, kind, entity_id, person_id, conversation_id, source_kind FROM change_log WHERE seq > $a AND seq <= $b AND kind IN ({string.Join(",", names)}) ORDER BY seq",
                r => new ChangeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetString(6)),
                parameters.ToArray());
        }

        // ── planner ──────────────────────────────────────────────────────────────

        /// <summary>One planning pass over every enabled module. Returns jobs enqueued (including deduped).</summary>
        public async Task<int> PlanOnceAsync(CancellationToken ct = default)
        {
            await passGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await PlanCoreAsync(ct).ConfigureAwait(false); }
            finally { passGate.Release(); }
        }

        private async Task<int> PlanCoreAsync(CancellationToken ct)
        {
            var states = ReadModuleStates();
            var tz = Settings.KlivesZone;
            int planned = 0;
            foreach (var m in ordered)
            {
                string id = m.Manifest.Id;
                if (!states.TryGetValue(id, out var st) || !st.Enabled) continue;
                long now = clock.NowMs;

                var due = new List<(string Name, TimeSpan Lateness)>();
                var nextDue = new Dictionary<string, long>(st.State.NextDue, StringComparer.Ordinal);
                foreach (var s in m.Manifest.Schedules)
                {
                    if (!nextDue.TryGetValue(s.Name, out long next))
                    {
                        nextDue[s.Name] = s.DailyAtLocal is { } at
                            ? Schedules.NextDailyOccurrence(now, at, tz)
                            : now + (long)s.Every!.Value.TotalMilliseconds;
                        continue;
                    }
                    if (now < next) continue;
                    due.Add((s.Name, TimeSpan.FromMilliseconds(now - next)));
                    nextDue[s.Name] = s.DailyAtLocal is { } at2
                        ? Schedules.NextDailyOccurrence(now, at2, tz)
                        : now + (long)s.Every!.Value.TotalMilliseconds;
                }

                List<(string Kind, string Id, long Seq)> dirtyRows;
                using (var c = stores.Intel.OpenRead())
                {
                    dirtyRows = c.Query(null,
                        "SELECT subject_kind, subject_id, dirty_since_seq FROM module_dirty WHERE module_id=$m ORDER BY dirty_at_ms LIMIT $l",
                        r => (r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$m", id), ("$l", options.PlanBatch));
                }

                bool scheduleStateChanged = !nextDue.OrderBy(k => k.Key).SequenceEqual(st.State.NextDue.OrderBy(k => k.Key));
                if (dirtyRows.Count == 0 && due.Count == 0 && !st.State.VersionChanged)
                {
                    if (scheduleStateChanged) await SaveStateAsync(id, new StateDoc { NextDue = nextDue, VersionChanged = false }, ct).ConfigureAwait(false);
                    continue;
                }

                List<JobRequest> jobs;
                try
                {
                    jobs = m.Plan(new PlanContext
                    {
                        Dirty = dirtyRows.Select(d => new DirtySubject(d.Kind, d.Id)).ToList(),
                        DueSchedules = due,
                        NowMs = now,
                        ChangeWatermark = st.Watermark,
                        VersionChanged = st.State.VersionChanged,
                    }).ToList();
                }
                catch (Exception ex)
                {
                    // Dirty rows stay put; the pass is retried next tick.
                    logError($"[OmniscienceV3] module {id} failed to plan; dirty set retained.", ex);
                    continue;
                }
                foreach (var j in jobs)
                    if (j.ModuleId != id) throw new InvalidOperationException($"Module '{id}' planned a job for '{j.ModuleId}'.");

                var newState = new StateDoc { NextDue = nextDue, VersionChanged = false };
                int count = await stores.Intel.WriteAsync((c, tx) =>
                {
                    foreach (var d in dirtyRows)
                        c.Exec(tx, "DELETE FROM module_dirty WHERE module_id=$m AND subject_kind=$k AND subject_id=$i AND dirty_since_seq <= $s",
                            ("$m", id), ("$k", d.Kind), ("$i", d.Id), ("$s", d.Seq));
                    foreach (var j in jobs) Jobs.EnqueueCore(c, tx, j);
                    c.Exec(tx, "UPDATE module_state SET state_json=$s, updated_at_ms=$t WHERE module_id=$m",
                        ("$s", JsonSerializer.Serialize(newState, KernelJson.Options)), ("$t", now), ("$m", id));
                    return jobs.Count;
                }, ct).ConfigureAwait(false);
                if (count > 0) Kick();
                planned += count;
            }
            return planned;
        }

        private Task SaveStateAsync(string moduleId, StateDoc doc, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE module_state SET state_json=$s, updated_at_ms=$t WHERE module_id=$m",
                ("$s", JsonSerializer.Serialize(doc, KernelJson.Options)), ("$t", clock.NowMs), ("$m", moduleId)), ct);

        // ── maintenance ──────────────────────────────────────────────────────────

        private async Task RunMaintenanceIfDueAsync(CancellationToken ct)
        {
            long now = clock.NowMs;
            if (now - lastMaintenanceMs < (long)options.MaintenanceInterval.TotalMilliseconds) return;
            lastMaintenanceMs = now;
            await RunMaintenanceOnceAsync(ct).ConfigureAwait(false);
        }

        public async Task RunMaintenanceOnceAsync(CancellationToken ct = default)
        {
            long now = clock.NowMs;
            int reclaimed = await Jobs.ReclaimExpiredAsync(ct).ConfigureAwait(false);
            if (reclaimed > 0) log($"[OmniscienceV3] reclaimed {reclaimed} expired job lease(s).");
            if (options.PriorityAgeAfter > TimeSpan.Zero && now - lastAgingMs >= (long)options.PriorityAgeAfter.TotalMilliseconds)
            {
                lastAgingMs = now;
                await Jobs.AgePriorities(options.PriorityAgeAfter, options.PriorityCap, ct).ConfigureAwait(false);
            }
            if (options.CheckpointInterval > TimeSpan.Zero && now - lastCheckpointMs >= (long)options.CheckpointInterval.TotalMilliseconds)
            {
                lastCheckpointMs = now;
                CheckpointStores();
            }
            if (now - lastPruneMs >= (long)TimeSpan.FromHours(6).TotalMilliseconds)
            {
                lastPruneMs = now;
                await Events.PruneAsync(options.StreamRetention, ct).ConfigureAwait(false);
                await stores.Intel.WriteAsync((c, tx) =>
                {
                    c.Exec(tx, "DELETE FROM jobs WHERE state IN ('succeeded','cancelled') AND finished_at_ms < $cut",
                        ("$cut", now - (long)options.SucceededJobRetention.TotalMilliseconds));
                    c.Exec(tx, "DELETE FROM jobs WHERE state='dead' AND finished_at_ms < $cut",
                        ("$cut", now - (long)options.DeadJobRetention.TotalMilliseconds));
                }, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copies each store's write-ahead log back into its file. SQLite does this by itself only when a commit finds no
        /// reader in the way; on the server intel.db's log had grown to 309 MB, and every read pays for a long log. A
        /// passive checkpoint never blocks a reader or the writer, and a store that cannot be checkpointed now is tried
        /// again on the next round.
        /// </summary>
        private void CheckpointStores()
        {
            foreach (var store in stores.All)
            {
                try { store.CheckpointPassive(); }
                catch (SqliteException ex) { logError($"[OmniscienceV3] could not checkpoint {store.Name}.db; it is tried again shortly.", ex); }
            }
        }

        // ── health ───────────────────────────────────────────────────────────────

        /// <summary>Jobs stopped by the stall watch since this engine started.</summary>
        public long StalledJobs => workers?.Stalled ?? 0;

        /// <summary>
        /// Per-module health. Everything here is a read of a small table: the job figures come from the counters the
        /// triggers keep (<see cref="JobQueue.ModuleStats"/>). It used to count and sort the jobs table once per module
        /// on every call, which took minutes with a million jobs in it and was what the status page waited for.
        /// </summary>
        public IReadOnlyList<ModuleHealth> Health()
        {
            long maxSeq;
            using (var cr = stores.Corpus.OpenRead()) maxSeq = ChangeLog.MaxSeq(cr);
            var states = ReadModuleStates();
            var jobs = Jobs.ModuleStats();
            Dictionary<string, long> dirty;
            using (var c = stores.Intel.OpenRead())
                dirty = c.Query(null, "SELECT module_id, COUNT(*) FROM module_dirty GROUP BY module_id", r => (r.GetString(0), r.GetInt64(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            var result = new List<ModuleHealth>();
            foreach (var m in ordered)
            {
                string id = m.Manifest.Id;
                states.TryGetValue(id, out var st);
                var j = jobs.GetValueOrDefault(id) ?? new ModuleJobStats();
                bool hasSubs = m.Manifest.SubscribedChangeKinds.Count > 0;
                result.Add(new ModuleHealth(id, m.Manifest.Title, m.Manifest.Version, st.Enabled, st.Watermark,
                    hasSubs ? Math.Max(0, maxSeq - st.Watermark) : 0, dirty.GetValueOrDefault(id),
                    j.Queued, j.Leased, j.Dead, j.LastSucceededMs, j.LastFailedMs, j.LastError));
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (!stopping.IsCancellationRequested) stopping.Cancel();
            if (loop != null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (Exception ex) { logError("[OmniscienceV3] kernel loop ended with an error during shutdown.", ex); }
            }
            if (workers != null) await workers.DisposeAsync().ConfigureAwait(false);
            // Modules that hold something of their own (a listener, a loop, a connection) let go of it once no job
            // can use it any more, in reverse start order.
            foreach (var module in ordered.AsEnumerable().Reverse())
            {
                try
                {
                    if (module is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (module is IDisposable disposable) disposable.Dispose();
                }
                catch (Exception ex)
                {
                    logError($"[OmniscienceV3] module '{module.Manifest.Id}' failed to shut down cleanly.", ex);
                }
            }
            stopping.Dispose();
            kick.Dispose();
            passGate.Dispose();
        }
    }
}
