using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Jobs
{
    /// <summary>
    /// The durable job queue in intel.db. It replaces every fire-and-forget path in v2: replica training
    /// that wedged forever on a crashed job (F011/F012), runs whose state lived only in memory (F187/F512),
    /// and a nightly monolith that couldn't finish in 24 h (F510).
    ///
    /// Guarantees:
    /// <list type="bullet">
    /// <item><b>Single-flight:</b> at most one queued-or-leased job per dedupe key (enqueue returns the live one).</item>
    /// <item><b>Leases:</b> a leased job must heartbeat. An expired lease is reclaimed, so nothing stays "running"
    /// after a crash.</item>
    /// <item><b>Classified failure:</b> transient errors retry with exponential backoff and jitter; permanent
    /// errors and exhausted retries go to the dead-letter state with their last error, retryable from the UI.</item>
    /// <item><b>Ordering:</b> highest priority first, then earliest eligible, then oldest; a job counts as higher the
    /// longer it has waited, and a module with a backlog cannot crowd out the others (<see cref="LeaseNextAsync"/>).</item>
    /// </list>
    /// All mutations go through intel.db's single writer, so lease/complete races resolve in one place.
    /// </summary>
    public sealed class JobQueue
    {
        private readonly SqliteStore intel;
        private readonly IClock clock;
        private readonly Random jitter = new();
        private readonly object jitterLock = new();

        public JobQueue(SqliteStore intel, IClock clock)
        {
            this.intel = intel;
            this.clock = clock;
        }

        /// <summary>Raised after a job is enqueued so idle workers in that lane wake immediately.</summary>
        public event Action<Lane>? JobEnqueued;

        public async Task<long> EnqueueAsync(JobRequest request, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ModuleId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
            if (request.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(request), "MaxAttempts must be at least 1.");
            long id = await intel.WriteAsync((c, tx) => EnqueueCore(c, tx, request), ct).ConfigureAwait(false);
            JobEnqueued?.Invoke(request.Lane);
            return id;
        }

        /// <summary>Enqueue inside an existing intel transaction (used by the planner to make dirty-set
        /// consumption and job creation atomic).</summary>
        internal long EnqueueCore(SqliteConnection c, SqliteTransaction tx, JobRequest r)
        {
            if (r.DedupeKey != null)
            {
                long? existing = c.Scalar<long?>(tx,
                    "SELECT job_id FROM jobs WHERE dedupe_key=$k AND state IN ('queued','leased') LIMIT 1",
                    ("$k", r.DedupeKey));
                if (existing != null) return existing.Value;
            }
            long now = clock.NowMs;
            c.Exec(tx, """
                INSERT INTO jobs(module_id, kind, subject_kind, subject_id, dedupe_key, priority, lane, state, attempts,
                                 max_attempts, not_before_ms, payload_json, created_at_ms, parent_job_id, run_id)
                VALUES($m,$k,$sk,$si,$d,$p,$l,'queued',0,$ma,$nb,$pl,$now,$par,$run)
                """,
                ("$m", r.ModuleId), ("$k", r.Kind), ("$sk", r.SubjectKind), ("$si", r.SubjectId), ("$d", r.DedupeKey),
                ("$p", r.Priority), ("$l", r.Lane.ToString()), ("$ma", r.MaxAttempts), ("$nb", Math.Max(r.NotBeforeMs, 0)),
                ("$pl", r.PayloadJson), ("$now", now), ("$par", r.ParentJobId), ("$run", r.RunId));
            return c.Scalar<long>(tx, "SELECT last_insert_rowid()");
        }

        /// <summary>A module with more jobs than this queued in one lane is working through a backlog.</summary>
        public const int BacklogJobs = 1_000;

        /// <summary>While other modules have work, a backlog still gets one lease in this many.</summary>
        public const int BacklogShare = 4;

        /// <summary>A waiting job counts as one priority step higher for every ten minutes it has waited.</summary>
        private const long AgeStepMs = 10 * 60 * 1000;

        private const int AgeCap = 1_000;

        /// <summary>Per lane: the backlog module served last, and how many leases went by since a backlog was served.</summary>
        private readonly Dictionary<Lane, (string? LastBacklog, int SinceBacklog)> rotation = new();

        private readonly record struct Candidate(string Module, long JobId, int Priority, long NotBeforeMs, long CreatedAtMs);

        /// <summary>
        /// Leases the next job in <paramref name="lane"/>, or null when none is ready.
        /// <para>Among modules with an ordinary amount of work the order is what it always was: highest priority first
        /// (a job gains a step for every ten minutes it has waited), then earliest eligible, then oldest. A module with
        /// more than <see cref="BacklogJobs"/> queued is a backlog, and a backlog does not compete on priority: it gets
        /// the lane whenever nobody else has a job ready, and one lease in <see cref="BacklogShare"/> when somebody
        /// does; several backlogs take turns. On the server 650,000 media jobs outranked everything else in two lanes,
        /// so the deduction sweep, the projections and the tail of v2's messages waited behind all of them.</para>
        /// <para><paramref name="maxPerModule"/> is how many jobs one module may hold leased in this lane at once, so a
        /// module whose jobs are slow or stuck cannot occupy every worker.</para>
        /// </summary>
        public Task<JobRecord?> LeaseNextAsync(Lane lane, string owner, TimeSpan leaseDuration, CancellationToken ct = default, int maxPerModule = int.MaxValue) =>
            intel.WriteAsync<JobRecord?>((c, tx) =>
            {
                long now = clock.NowMs;
                string laneName = lane.ToString();
                // job_counts is kept by triggers: a few rows per module, read in place of a scan of the queue.
                var modules = c.Query(tx, """
                    SELECT module_id, SUM(CASE WHEN state='queued' THEN n ELSE 0 END), SUM(CASE WHEN state='leased' THEN n ELSE 0 END)
                    FROM job_counts WHERE lane=$l AND state IN ('queued','leased') GROUP BY module_id ORDER BY module_id
                    """, r => (Module: r.GetString(0), Queued: r.GetInt64(1), Leased: r.GetInt64(2)), ("$l", laneName))
                    .Where(m => m.Queued > 0 && m.Leased < maxPerModule).ToList();
                if (modules.Count == 0) return null;

                Candidate? Best(string module) => c.Query(tx, """
                    SELECT job_id, priority, not_before_ms, created_at_ms FROM jobs INDEXED BY ix_jobs_fair
                    WHERE lane=$l AND state='queued' AND module_id=$m AND not_before_ms <= $now
                    ORDER BY priority DESC, not_before_ms, job_id
                    LIMIT 1
                    """, r => (Candidate?)new Candidate(module, r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)),
                    ("$l", laneName), ("$m", module), ("$now", now)).FirstOrDefault();

                // The ordinary modules' best ready jobs, compared as one queue.
                Candidate? ordinary = null;
                long ordinaryRank = long.MinValue;
                foreach (var m in modules.Where(m => m.Queued <= BacklogJobs))
                {
                    if (Best(m.Module) is not { } candidate) continue; // everything it has queued is still backing off
                    long rank = candidate.Priority + Math.Min(AgeCap, Math.Max(0, now - candidate.CreatedAtMs) / AgeStepMs);
                    bool better = ordinary is not { } held || rank > ordinaryRank
                                  || (rank == ordinaryRank && (candidate.NotBeforeMs < held.NotBeforeMs
                                                              || (candidate.NotBeforeMs == held.NotBeforeMs && candidate.JobId < held.JobId)));
                    if (better) (ordinary, ordinaryRank) = (candidate, rank);
                }

                // The backlogs, starting after the one served last.
                Candidate? NextBacklog(string? last)
                {
                    var backlogs = modules.Where(m => m.Queued > BacklogJobs).Select(m => m.Module).ToList();
                    if (backlogs.Count == 0) return null;
                    int from = last == null ? 0 : backlogs.FindIndex(b => string.CompareOrdinal(b, last) > 0);
                    if (from < 0) from = 0;
                    for (int i = 0; i < backlogs.Count; i++)
                        if (Best(backlogs[(from + i) % backlogs.Count]) is { } candidate) return candidate;
                    return null;
                }

                var state = rotation.GetValueOrDefault(lane);
                Candidate? chosen;
                bool fromBacklog;
                if (ordinary != null && state.SinceBacklog < BacklogShare - 1) (chosen, fromBacklog) = (ordinary, false);
                else
                {
                    var backlog = NextBacklog(state.LastBacklog);
                    (chosen, fromBacklog) = backlog != null ? (backlog, true) : (ordinary, false);
                }
                if (chosen is not { } job) return null;
                rotation[lane] = fromBacklog ? (job.Module, 0) : (state.LastBacklog, state.SinceBacklog + 1);

                c.Exec(tx, """
                    UPDATE jobs SET state='leased', lease_owner=$o, lease_expires_ms=$exp, heartbeat_ms=$now,
                                    attempts=attempts+1, started_at_ms=COALESCE(started_at_ms, $now)
                    WHERE job_id=$id
                    """, ("$o", owner), ("$exp", now + (long)leaseDuration.TotalMilliseconds), ("$now", now), ("$id", job.JobId));
                return Read(c, tx, job.JobId);
            }, ct);

        /// <summary>Extends the lease (and optionally records progress). False when the lease was lost.</summary>
        public Task<bool> HeartbeatAsync(long jobId, string owner, TimeSpan leaseDuration, string? progressJson = null, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                int n = c.Exec(tx, """
                    UPDATE jobs SET lease_expires_ms=$exp, heartbeat_ms=$now, progress_json=COALESCE($p, progress_json)
                    WHERE job_id=$id AND state='leased' AND lease_owner=$o
                    """, ("$exp", now + (long)leaseDuration.TotalMilliseconds), ("$now", now), ("$p", progressJson), ("$id", jobId), ("$o", owner));
                return n == 1;
            }, ct);

        public Task<bool> CompleteAsync(long jobId, string owner, string? resultJson, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                int n = c.Exec(tx, """
                    UPDATE jobs SET state='succeeded', result_json=$r, finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class=NULL, error_message=NULL
                    WHERE job_id=$id AND state='leased' AND lease_owner=$o
                    """, ("$r", resultJson), ("$now", clock.NowMs), ("$id", jobId), ("$o", owner));
                return n == 1;
            }, ct);

        /// <summary>Records a failure. Transient failures with attempts left go back to the queue after an
        /// exponential backoff with jitter; everything else is dead-lettered. Returns the resulting state.</summary>
        public Task<string?> FailAsync(long jobId, string owner, ErrorClass errorClass, string message, CancellationToken ct = default) =>
            intel.WriteAsync<string?>((c, tx) =>
            {
                var row = c.Query(tx, "SELECT attempts, max_attempts FROM jobs WHERE job_id=$id AND state='leased' AND lease_owner=$o",
                    r => (Attempts: r.GetInt32(0), Max: r.GetInt32(1)), ("$id", jobId), ("$o", owner)).FirstOrDefault();
                if (row == default) return null; // lease lost: someone else owns the outcome now
                long now = clock.NowMs;
                bool retry = errorClass == ErrorClass.Transient && row.Attempts < row.Max;
                if (retry)
                {
                    c.Exec(tx, """
                        UPDATE jobs SET state='queued', not_before_ms=$nb, lease_owner=NULL, lease_expires_ms=NULL,
                                        error_class=$ec, error_message=$em
                        WHERE job_id=$id
                        """, ("$nb", now + BackoffMs(row.Attempts)), ("$ec", errorClass.ToString()), ("$em", Truncate(message)), ("$id", jobId));
                    return JobStates.Queued;
                }
                c.Exec(tx, """
                    UPDATE jobs SET state='dead', finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class=$ec, error_message=$em
                    WHERE job_id=$id
                    """, ("$now", now), ("$ec", errorClass.ToString()), ("$em", Truncate(message)), ("$id", jobId));
                return JobStates.Dead;
            }, ct);

        /// <summary>Gives a leased job back without counting the attempt (graceful shutdown mid-job).</summary>
        public Task<bool> ReleaseAsync(long jobId, string owner, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE jobs SET state='queued', attempts=MAX(attempts-1,0), lease_owner=NULL, lease_expires_ms=NULL
                WHERE job_id=$id AND state='leased' AND lease_owner=$o
                """, ("$id", jobId), ("$o", owner)) == 1, ct);

        /// <summary>Requeues (or dead-letters, when out of attempts) every lease that expired: the crash-recovery path.
        /// Returns the number reclaimed.</summary>
        public Task<int> ReclaimExpiredAsync(CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                int dead = c.Exec(tx, """
                    UPDATE jobs SET state='dead', finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class='Transient', error_message='Lease expired on the final attempt (worker crashed or hung).'
                    WHERE state='leased' AND lease_expires_ms < $now AND attempts >= max_attempts
                    """, ("$now", now));
                int requeued = c.Exec(tx, """
                    UPDATE jobs SET state='queued', not_before_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class='Transient', error_message='Lease expired (worker crashed or hung); requeued.'
                    WHERE state='leased' AND lease_expires_ms < $now
                    """, ("$now", now));
                return dead + requeued;
            }, ct);

        /// <summary>
        /// Startup recovery. The store's <see cref="Storage.InstanceLock"/> guarantees no other kernel works this queue,
        /// so every job still marked leased belongs to a process that is gone, however much of its lease is left.
        /// Requeue them now rather than waiting out the lease (up to the lane's lease duration). The interrupted run
        /// counts as an attempt (the job may be what crashed the process), so a poison job still dead-letters.
        /// </summary>
        public Task<int> ReclaimOrphanedAsync(CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                int dead = c.Exec(tx, """
                    UPDATE jobs SET state='dead', finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class='Transient', error_message='The service stopped during the final attempt.'
                    WHERE state='leased' AND attempts >= max_attempts
                    """, ("$now", now));
                int requeued = c.Exec(tx, """
                    UPDATE jobs SET state='queued', not_before_ms=$now, lease_owner=NULL, lease_expires_ms=NULL,
                                    error_class='Transient', error_message='The service stopped while this job ran; requeued.'
                    WHERE state='leased'
                    """, ("$now", now));
                return dead + requeued;
            }, ct);

        public Task<bool> CancelAsync(long jobId, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE jobs SET state='cancelled', finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL
                WHERE job_id=$id AND state IN ('queued','leased')
                """, ("$now", clock.NowMs), ("$id", jobId)) == 1, ct);

        /// <summary>Cancels every live job of a module (module disabled, plan rule F-Kernel).</summary>
        public Task<int> CancelModuleAsync(string moduleId, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE jobs SET state='cancelled', finished_at_ms=$now, lease_owner=NULL, lease_expires_ms=NULL
                WHERE module_id=$m AND state IN ('queued','leased')
                """, ("$now", clock.NowMs), ("$m", moduleId)), ct);

        /// <summary>Puts a dead-lettered job back in the queue with a fresh attempt budget (UI "retry").</summary>
        public Task<bool> RetryDeadAsync(long jobId, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE jobs SET state='queued', attempts=0, not_before_ms=$now, finished_at_ms=NULL
                WHERE job_id=$id AND state='dead'
                """, ("$now", clock.NowMs), ("$id", jobId)) == 1, ct);

        /// <summary>Raises the priority of jobs queued for longer than <paramref name="olderThan"/> by one step,
        /// up to <paramref name="cap"/>. Run periodically so low-priority work cannot starve.</summary>
        public Task<int> AgePriorities(TimeSpan olderThan, int cap, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE jobs SET priority = priority + 1
                WHERE state='queued' AND created_at_ms < $cut AND priority < $cap
                """, ("$cut", clock.NowMs - (long)olderThan.TotalMilliseconds), ("$cap", cap)), ct);

        public JobRecord? Get(long jobId)
        {
            using var c = intel.OpenRead();
            return Read(c, null, jobId);
        }

        /// <summary>Jobs per lane and state, from the counters the triggers keep (never a scan of the jobs table).</summary>
        public IReadOnlyList<JobCount> Counts()
        {
            using var c = intel.OpenRead();
            return c.Query(null, "SELECT lane, state, SUM(n) FROM job_counts GROUP BY lane, state HAVING SUM(n) > 0",
                r => new JobCount(Enum.Parse<Lane>(r.GetString(0)), r.GetString(1), r.GetInt64(2)));
        }

        /// <summary>Jobs per module and state, and each module's last success and last dead-lettered failure, from the
        /// same counters.</summary>
        public IReadOnlyDictionary<string, ModuleJobStats> ModuleStats()
        {
            using var c = intel.OpenRead();
            var stats = new Dictionary<string, ModuleJobStats>(StringComparer.Ordinal);
            foreach (var (module, state, n) in c.Query(null, "SELECT module_id, state, SUM(n) FROM job_counts GROUP BY module_id, state",
                         r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
            {
                var s = stats.GetValueOrDefault(module) ?? new ModuleJobStats();
                stats[module] = state switch
                {
                    JobStates.Queued => s with { Queued = n },
                    JobStates.Leased => s with { Leased = n },
                    JobStates.Dead => s with { Dead = n },
                    JobStates.Succeeded => s with { Succeeded = n },
                    _ => s,
                };
            }
            foreach (var row in c.Query(null, "SELECT module_id, last_ok_ms, last_fail_ms, last_error FROM job_module_last",
                         r => (Module: r.GetString(0), Ok: r.GetInt64OrNull(1), Fail: r.GetInt64OrNull(2), Error: r.GetStringOrNull(3))))
            {
                var s = stats.GetValueOrDefault(row.Module) ?? new ModuleJobStats();
                stats[row.Module] = s with { LastSucceededMs = row.Ok, LastFailedMs = row.Fail, LastError = row.Error };
            }
            return stats;
        }

        /// <summary>
        /// For every module with a job ready to run: since when its next job (the one a worker would take first) has been
        /// ready. A job waiting for its start time or backing off is not ready. One index seek per module with queued
        /// work, so this costs the same whether ten jobs are queued or a million.
        /// </summary>
        public IReadOnlyList<(Lane Lane, string Module, long ReadySinceMs)> WaitingHeads()
        {
            long now = clock.NowMs;
            using var c = intel.OpenRead();
            var result = new List<(Lane, string, long)>();
            foreach (var (lane, module) in c.Query(null, "SELECT lane, module_id FROM job_counts WHERE state='queued' AND n > 0", r => (r.GetString(0), r.GetString(1))))
            {
                var head = c.Query(null, """
                    SELECT created_at_ms, not_before_ms FROM jobs INDEXED BY ix_jobs_fair
                    WHERE lane=$l AND state='queued' AND module_id=$m AND not_before_ms <= $now
                    ORDER BY priority DESC, not_before_ms, job_id
                    LIMIT 1
                    """, r => (Found: true, Created: r.GetInt64(0), NotBefore: r.GetInt64(1)), ("$l", lane), ("$m", module), ("$now", now)).FirstOrDefault();
                if (head.Found) result.Add((Enum.Parse<Lane>(lane), module, Math.Max(head.Created, head.NotBefore)));
            }
            return result;
        }

        /// <summary>Recounts <c>job_counts</c> from the jobs table. The triggers keep it exact; this is the repair for a
        /// database edited by hand, and what the tests compare the triggers against.</summary>
        public Task RecountAsync(CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM job_counts");
                c.Exec(tx, "INSERT INTO job_counts(lane, module_id, state, n) SELECT lane, module_id, state, COUNT(*) FROM jobs GROUP BY lane, module_id, state");
            }, ct);

        public IReadOnlyList<JobRecord> List(string? state, string? moduleId, int limit) =>
            Query(new JobQuery { State = state, ModuleId = moduleId, Limit = limit }).Items;

        /// <summary>
        /// Filtered, keyset-paged job listing, newest first. Every filter is optional and they combine with AND;
        /// <see cref="JobQuery.BeforeJobId"/> is the cursor returned as <c>NextCursor</c> by the previous page.
        /// </summary>
        public JobPage Query(JobQuery q)
        {
            int limit = Math.Clamp(q.Limit, 1, 1000);
            var where = new List<string>();
            var p = new List<(string, object?)>();
            void Eq(string column, string name, object? value)
            {
                if (value == null) return;
                where.Add($"{column}={name}");
                p.Add((name, value));
            }
            Eq("state", "$state", q.State);
            Eq("module_id", "$module", q.ModuleId);
            Eq("lane", "$lane", q.Lane?.ToString());
            Eq("kind", "$kind", q.Kind);
            Eq("subject_kind", "$sk", q.SubjectKind);
            Eq("subject_id", "$si", q.SubjectId);
            Eq("run_id", "$run", q.RunId);
            Eq("parent_job_id", "$parent", q.ParentJobId);
            Eq("error_class", "$ec", q.ErrorClass);
            if (q.CreatedFromMs is long from) { where.Add("created_at_ms >= $from"); p.Add(("$from", from)); }
            if (q.CreatedToMs is long to) { where.Add("created_at_ms < $to"); p.Add(("$to", to)); }
            if (q.BeforeJobId is long before) { where.Add("job_id < $before"); p.Add(("$before", before)); }
            p.Add(("$lim", limit + 1));
            string sql = $"SELECT {Columns} FROM jobs {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY job_id DESC LIMIT $lim";

            using var c = intel.OpenRead();
            var rows = c.Query(null, sql, Map, p.ToArray());
            if (rows.Count <= limit) return new JobPage(rows, null);
            var page = rows.Take(limit).ToList();
            return new JobPage(page, page[^1].JobId);
        }

        /// <summary>Exponential backoff: 2 s, 4 s, 8 s … capped at 15 min, with ±20% jitter.</summary>
        internal long BackoffMs(int attempts)
        {
            double baseMs = Math.Min(2000 * Math.Pow(2, Math.Max(0, attempts - 1)), 15 * 60 * 1000);
            double factor;
            lock (jitterLock) factor = 0.8 + jitter.NextDouble() * 0.4;
            return (long)(baseMs * factor);
        }

        private static string Truncate(string s) => s.Length <= 4000 ? s : s[..4000] + "…";

        private const string Columns =
            "job_id, module_id, kind, subject_kind, subject_id, dedupe_key, priority, lane, state, attempts, max_attempts, " +
            "not_before_ms, lease_owner, lease_expires_ms, payload_json, progress_json, result_json, error_class, error_message, " +
            "created_at_ms, started_at_ms, finished_at_ms, parent_job_id, run_id";

        private static JobRecord? Read(SqliteConnection c, SqliteTransaction? tx, long jobId) =>
            c.Query(tx, $"SELECT {Columns} FROM jobs WHERE job_id=$id", Map, ("$id", jobId)).FirstOrDefault();

        private static JobRecord Map(SqliteDataReader r) => new(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5),
            r.GetInt32(6), Enum.Parse<Lane>(r.GetString(7)), r.GetString(8), r.GetInt32(9), r.GetInt32(10), r.GetInt64(11),
            r.GetStringOrNull(12), r.GetInt64OrNull(13), r.GetStringOrNull(14), r.GetStringOrNull(15), r.GetStringOrNull(16),
            r.GetStringOrNull(17), r.GetStringOrNull(18), r.GetInt64(19), r.GetInt64OrNull(20), r.GetInt64OrNull(21),
            r.GetInt64OrNull(22), r.GetStringOrNull(23));
    }
}
