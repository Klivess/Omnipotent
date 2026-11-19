namespace Omnipotent.Services.OmniscienceV3.Kernel.Jobs
{
    /// <summary>Execution lanes. Each lane has its own worker pool and concurrency budget (plan R1/R2).</summary>
    public enum Lane
    {
        /// <summary>CPU-bound work: rollups, local model inference, clustering, projections.</summary>
        Cpu,
        /// <summary>Network and disk I/O: downloads, backfills, imports.</summary>
        Io,
        /// <summary>LLM work nobody is waiting for. Runs in AIRouter's lowest class and yields to everything else.</summary>
        LlmBackground,
        /// <summary>LLM work Klives is waiting for (Ask, inquiries, replica replies, tactics).</summary>
        LlmInteractive,
        /// <summary>Jobs that only orchestrate other jobs and wait for them (playbook runs). They get their own workers,
        /// so a coordinator waiting for a step can never hold the worker that step needs (starvation deadlock).</summary>
        Coordinator,
    }

    /// <summary>Job lifecycle states as stored in intel.db.</summary>
    public static class JobStates
    {
        public const string Queued = "queued";
        public const string Leased = "leased";
        public const string Succeeded = "succeeded";
        public const string Dead = "dead";
        public const string Cancelled = "cancelled";

        public static bool IsTerminal(string state) => state is Succeeded or Dead or Cancelled;
    }

    /// <summary>How a failure should be treated (replaces v2's silent catch-alls, F008).</summary>
    public enum ErrorClass
    {
        /// <summary>Retry with exponential backoff (timeouts, 429/5xx, locked resources).</summary>
        Transient,
        /// <summary>Retrying cannot help (bad input, missing data, policy refusal): dead-letter immediately.</summary>
        Permanent,
    }

    /// <summary>Raise from a job handler to classify a failure explicitly.</summary>
    public sealed class JobFailureException : Exception
    {
        public JobFailureException(ErrorClass errorClass, string message, Exception? inner = null) : base(message, inner) => Class = errorClass;

        public ErrorClass Class { get; }
    }

    /// <summary>A request to run one unit of module work.</summary>
    public sealed record JobRequest
    {
        public required string ModuleId { get; init; }
        public required string Kind { get; init; }
        public string? SubjectKind { get; init; }
        public string? SubjectId { get; init; }
        /// <summary>At most one queued-or-leased job per key (single-flight per module × subject).</summary>
        public string? DedupeKey { get; init; }
        public int Priority { get; init; } = 100;
        public Lane Lane { get; init; } = Lane.Cpu;
        public int MaxAttempts { get; init; } = 5;
        public long NotBeforeMs { get; init; }
        public string? PayloadJson { get; init; }
        public long? ParentJobId { get; init; }
        /// <summary>Groups jobs that belong to one tactic run or one batch.</summary>
        public string? RunId { get; init; }

        public static string KeyFor(string moduleId, string kind, string? subjectKind, string? subjectId) =>
            $"{moduleId}|{kind}|{subjectKind}|{subjectId}";
    }

    /// <summary>A stored job as seen by workers and the UI.</summary>
    public sealed record JobRecord(
        long JobId,
        string ModuleId,
        string Kind,
        string? SubjectKind,
        string? SubjectId,
        string? DedupeKey,
        int Priority,
        Lane Lane,
        string State,
        int Attempts,
        int MaxAttempts,
        long NotBeforeMs,
        string? LeaseOwner,
        long? LeaseExpiresMs,
        string? PayloadJson,
        string? ProgressJson,
        string? ResultJson,
        string? ErrorClass,
        string? ErrorMessage,
        long CreatedAtMs,
        long? StartedAtMs,
        long? FinishedAtMs,
        long? ParentJobId,
        string? RunId);

    /// <summary>Counts per (lane, state) for health pages.</summary>
    public sealed record JobCount(Lane Lane, string State, long Count);

    /// <summary>One module's jobs by state, with when it last succeeded and last dead-lettered.</summary>
    public sealed record ModuleJobStats(long Queued = 0, long Leased = 0, long Dead = 0, long Succeeded = 0,
        long? LastSucceededMs = null, long? LastFailedMs = null, string? LastError = null);

    /// <summary>Filters for <see cref="JobQueue.Query"/>; all optional, combined with AND. Times are UTC epoch ms;
    /// <see cref="CreatedToMs"/> is exclusive.</summary>
    public sealed record JobQuery
    {
        public string? State { get; init; }
        public string? ModuleId { get; init; }
        public Lane? Lane { get; init; }
        public string? Kind { get; init; }
        public string? SubjectKind { get; init; }
        public string? SubjectId { get; init; }
        public string? RunId { get; init; }
        public long? ParentJobId { get; init; }
        public string? ErrorClass { get; init; }
        public long? CreatedFromMs { get; init; }
        public long? CreatedToMs { get; init; }
        /// <summary>Keyset cursor: only jobs with a smaller id (the previous page's <see cref="JobPage.NextCursor"/>).</summary>
        public long? BeforeJobId { get; init; }
        public int Limit { get; init; } = 100;
    }

    /// <summary>One page of jobs, newest first. <see cref="NextCursor"/> is null on the last page.</summary>
    public sealed record JobPage(IReadOnlyList<JobRecord> Items, long? NextCursor);
}
