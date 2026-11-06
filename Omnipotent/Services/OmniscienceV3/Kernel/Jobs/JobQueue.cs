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
    /// <item><b>Ordering:</b> highest priority first, then earliest eligible, then oldest. Queued jobs gain
    /// priority as they age (<see cref="AgePriorities"/>), so nothing starves.</item>
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

        /// <summary>Leases the best eligible job in <paramref name="lane"/>, or null when none is ready.</summary>
        public Task<JobRecord?> LeaseNextAsync(Lane lane, string owner, TimeSpan leaseDuration, CancellationToken ct = default) =>
            intel.WriteAsync<JobRecord?>((c, tx) =>
            {
                long now = clock.NowMs;
                long? id = c.Scalar<long?>(tx, """
                    SELECT job_id FROM jobs
                    WHERE lane=$l AND state='queued' AND not_before_ms <= $now
                    ORDER BY priority DESC, not_before_ms, job_id
                    LIMIT 1
                    """, ("$l", lane.ToString()), ("$now", now));
                if (id == null) return null;
                c.Exec(tx, """
                    UPDATE jobs SET state='leased', lease_owner=$o, lease_expires_ms=$exp, heartbeat_ms=$now,
                                    attempts=attempts+1, started_at_ms=COALESCE(started_at_ms, $now)
                    WHERE job_id=$id
                    """, ("$o", owner), ("$exp", now + (long)leaseDuration.TotalMilliseconds), ("$now", now), ("$id", id.Value));
                return Read(c, tx, id.Value);
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

        public IReadOnlyList<JobCount> Counts()
        {
            using var c = intel.OpenRead();
            return c.Query(null, "SELECT lane, state, COUNT(*) FROM jobs GROUP BY lane, state",
                r => new JobCount(Enum.Parse<Lane>(r.GetString(0)), r.GetString(1), r.GetInt64(2)));
        }

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
