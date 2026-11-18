using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Whether the Steward execution harness may currently run work. This is deliberately separate
    /// from the Steward's strategic phase/milestones and from the legacy <see cref="StewardStatus"/>.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardExecutionDisposition
    {
        Running,
        Pausing,
        Paused,
        Waiting,
        Blocked,
        Completed,
        Archived,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardWakeLeaseStatus { Starting, Running, CancellationRequested }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardExecutionHealthStatus { Healthy, Degraded, CircuitOpen }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardCircuitStatus { Closed, Open, HalfOpen }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardFailureCategory
    {
        Unknown,
        Transient,
        RateLimited,
        Capacity,
        Authentication,
        Configuration,
        ContextLimit,
        ToolContract,
        Cancelled,
        InvariantViolation,
        ExternalDependency,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardBlockerCategory
    {
        Unknown,
        Approval,
        Budget,
        ExternalDependency,
        Capacity,
        Configuration,
        ManualIntervention,
        InvariantViolation,
    }

    [JsonConverter(typeof(DemiurgeEnumConverter))] // reads the pre-rename "ProjectFile"
    public enum StewardEvidenceKind
    {
        Event,
        Artifact,
        StewardFile,
        ToolResult,
        ExternalObservation,
        UserConfirmation,
        Other,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardArtifactValidationStatus { Unknown, Pending, Valid, Invalid }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardWakeTriggerKind
    {
        HumanMessage,
        Stimulus,
        AgentMessage,
        Resume,
        Keepalive,
        Continuation,
        Recovery,
        GateResolved,
        System,
        Other,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardWakeTriggerApplicability { Applicable, Deferred, Stale }

    /// <summary>
    /// A persisted single-flight fencing lease. WakeID plus Generation is the ownership token;
    /// an old wake cannot heartbeat, cancel or release a newer wake's lease.
    /// </summary>
    public sealed class StewardWakeLease
    {
        public string WakeID { get; set; } = "";
        public long Generation { get; set; }
        public StewardWakeLeaseStatus Status { get; set; } = StewardWakeLeaseStatus.Starting;
        public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
        public DateTime? CancellationRequestedAt { get; set; }
        public string? CancellationReason { get; set; }
    }

    public sealed class StewardEvidenceReference
    {
        public StewardEvidenceKind Kind { get; set; } = StewardEvidenceKind.Other;
        /// <summary>An event ID, artifact ID, Steward path, tool-call ID, URL, or other stable reference.</summary>
        public string Reference { get; set; } = "";
        public long? EventSequence { get; set; }
        public string? ContentHash { get; set; }
        public string? Description { get; set; }
        public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class StewardExecutionFailure
    {
        public StewardFailureCategory Category { get; set; } = StewardFailureCategory.Unknown;
        public string Code { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool Retryable { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
        public DateTime? RetryAt { get; set; }
        public string? WakeID { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public int? HttpStatus { get; set; }
        public int? RequestedMaxTokens { get; set; }
        public int? AffordableMaxTokens { get; set; }
        public List<StewardEvidenceReference> Evidence { get; set; } = new();
    }

    public sealed class StewardCircuitBreakerState
    {
        public StewardCircuitStatus Status { get; set; } = StewardCircuitStatus.Closed;
        public int FailureCount { get; set; }
        public string? ReasonCode { get; set; }
        public string? Summary { get; set; }
        public DateTime? OpenedAt { get; set; }
        public DateTime? RetryAt { get; set; }
    }

    public sealed class StewardExecutionHealth
    {
        public StewardExecutionHealthStatus Status { get; set; } = StewardExecutionHealthStatus.Healthy;
        public int ConsecutiveFailures { get; set; }
        public StewardExecutionFailure? LastFailure { get; set; }
        public DateTime? LastSuccessAt { get; set; }
        public DateTime? LastVerifiedProgressAt { get; set; }
        public long? LastVerifiedProgressSequence { get; set; }
        public StewardCircuitBreakerState Circuit { get; set; } = new();
        /// <summary>Independent live dependencies. A successful LLM wake must not make the
        /// Steward look Healthy while its desktop, mailbox bridge, filesystem, or another
        /// required subsystem is still known to be degraded.</summary>
        public Dictionary<string, StewardDependencyHealth> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Durable watchdog accounting. Keeping this in runtime state prevents recovery
        /// counters and pacing from resetting whenever Omnipotent restarts.</summary>
        public StewardWatchdogHealth Watchdog { get; set; } = new();
    }

    public sealed class StewardDependencyHealth
    {
        public string Key { get; set; } = "";
        public bool Healthy { get; set; }
        public string Code { get; set; } = "";
        public string Summary { get; set; } = "";
        public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RetryAt { get; set; }
    }

    public sealed class StewardWatchdogHealth
    {
        /// <summary>Monotonic lifetime count, used in event text so recovery numbers never regress.</summary>
        public long RecoveryCount { get; set; }
        public List<DateTime> RecoveriesUtc { get; set; } = new();
        public DateTime? LastRecoveryAt { get; set; }
        public DateTime? LastEscalationAt { get; set; }
        public List<string> RemindedGateIDs { get; set; } = new();
    }

    public sealed class StewardRuntimeBlocker
    {
        public string BlockerID { get; set; } = "";
        public StewardBlockerCategory Category { get; set; } = StewardBlockerCategory.Unknown;
        public string Code { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? Detail { get; set; }
        public bool Retryable { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? NextRetryAt { get; set; }
        public List<StewardEvidenceReference> Evidence { get; set; } = new();
    }

    /// <summary>A durable fact that is explicitly tied to evidence and can age out or be invalidated.</summary>
    public sealed class StewardVerifiedFact
    {
        public string FactID { get; set; } = "";
        public string Key { get; set; } = "";
        /// <summary>String representation of the verified value; callers may use JSON for structured values.</summary>
        public string Value { get; set; } = "";
        public string? Description { get; set; }
        public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ValidUntil { get; set; }
        public DateTime? InvalidatedAt { get; set; }
        public string? InvalidationReason { get; set; }
        public List<string> InvalidationKeys { get; set; } = new();
        public List<StewardEvidenceReference> Evidence { get; set; } = new();

        public bool IsFreshAt(DateTime utcNow) =>
            InvalidatedAt == null && (!ValidUntil.HasValue || ValidUntil.Value > utcNow);
    }

    /// <summary>
    /// A durable NEGATIVE fact: an approach that was tried and failed. The counterpart to
    /// <see cref="StewardVerifiedFact"/>, which only ever records what IS true.
    ///
    /// Without this, negative knowledge lives solely in the append-only event log and falls out of the
    /// wake seed's recent-event window, so an agent re-tries a dead end every few wakes — the single
    /// most expensive way a Steward burns budget without progressing. Health.LastFailure is NOT a
    /// substitute: it holds one failure and is cleared on the next success.
    /// </summary>
    public sealed class StewardFailedApproach
    {
        public string ApproachID { get; set; } = "";
        /// <summary>Stable identity for the thing being attempted, e.g. "ig-signup:email-domain".</summary>
        public string Key { get; set; } = "";
        /// <summary>What was tried.</summary>
        public string Approach { get; set; } = "";
        /// <summary>How it failed — the observed outcome, not a guess at the cause.</summary>
        public string Outcome { get; set; } = "";
        public int AttemptCount { get; set; }
        public DateTime FirstAttemptedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastAttemptedAt { get; set; } = DateTime.UtcNow;
        /// <summary>A known-better alternative, when one was established.</summary>
        public string? Instead { get; set; }
        /// <summary>Set for failures believed transient; the entry stops being advisory after this.</summary>
        public DateTime? RetryNotBefore { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string? ResolutionReason { get; set; }
        public List<StewardEvidenceReference> Evidence { get; set; } = new();

        /// <summary>True while this dead end should still steer the agent away from repeating it.</summary>
        public bool IsActiveAt(DateTime utcNow) =>
            ResolvedAt == null && (!RetryNotBefore.HasValue || RetryNotBefore.Value > utcNow);
    }

    /// <summary>
    /// One durable row of "we have tried this before", keyed by intent (see <see cref="StewardAttemptKey"/>)
    /// rather than by exact tool arguments.
    ///
    /// <see cref="StewardFailedApproach"/> is the curated version of this — a considered judgement, written
    /// deliberately by an agent or by the convergence guard. This is the raw automatic counterpart: every
    /// tool call updates its row, with no model cooperation required, so repetition is detectable even when
    /// the agent never thought to record anything. That matters because the in-wake convergence guard
    /// resets on every wake and matches only byte-identical arguments, which leaves cross-wake repetition
    /// completely undetected.
    /// </summary>
    public sealed class StewardAttempt
    {
        public string AttemptKey { get; set; } = "";
        public string ToolName { get; set; } = "";
        /// <summary>Audited one-line description of the call, for the seeded ledger.</summary>
        public string Summary { get; set; } = "";
        public int Count { get; set; }
        /// <summary>How many of those attempts did not succeed. Only failures are worth warning about.</summary>
        public int FailureCount { get; set; }
        public DateTime FirstAt { get; set; } = DateTime.UtcNow;
        public DateTime LastAt { get; set; } = DateTime.UtcNow;
        /// <summary>Trimmed text of the most recent result.</summary>
        public string LastOutcome { get; set; } = "";
        public bool LastSucceeded { get; set; }
        public string? LastWakeID { get; set; }
        public string? LastAgentID { get; set; }

        /// <summary>True when this intent has failed repeatedly and has not since worked.</summary>
        public bool IsRepeatedFailure(int threshold) => !LastSucceeded && FailureCount >= threshold;
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StewardStepStatus
    {
        Queued,
        Active,
        Done,
        Abandoned,
        Blocked,
    }

    /// <summary>
    /// One step on the Steward's linear path. Exactly one step is <see cref="StewardStepStatus.Active"/> at
    /// a time — that invariant is the whole point.
    ///
    /// This exists because a Steward previously had four overlapping answers to "what am I doing now":
    /// dependency-ordered Grand Plan milestones (too coarse, and only a summary line reached the seed),
    /// the digest's prose next-steps (rewritten by a utility model after every wake, so the plan drifted
    /// on its own), a single runner-set resume action, and the directive queue. With no single authority
    /// and no attempt count, each wake re-derived a plausible-looking plan and often picked something
    /// already attempted.
    /// </summary>
    public sealed class StewardStep
    {
        public string StepID { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>The Grand Plan milestone this step serves, when it serves one.</summary>
        public string? MilestoneID { get; set; }
        public StewardStepStatus Status { get; set; } = StewardStepStatus.Queued;
        public int Order { get; set; }
        /// <summary>How many tool attempts have been made while this step was active. Maintained automatically.</summary>
        public int Attempts { get; set; }
        /// <summary>Outcome of the most recent attempt, so a fresh context knows where the step actually stands.</summary>
        public string? LastAttemptOutcome { get; set; }
        /// <summary>The single next thing to do. This is what a renewed context resumes from.</summary>
        public string? NextConcreteAction { get; set; }
        public string? OwnerAgentID { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClosedAt { get; set; }
        public string? ClosureReason { get; set; }
        public List<StewardEvidenceReference> Evidence { get; set; } = new();

        public bool IsOpen => Status is StewardStepStatus.Queued or StewardStepStatus.Active or StewardStepStatus.Blocked;
    }

    /// <summary>A Steward output/input designated as canonical, with identity and validation evidence.</summary>
    public sealed class StewardCanonicalArtifact
    {
        public string CanonicalArtifactID { get; set; } = "";
        /// <summary>Stable logical role, e.g. "canonical-build", "source-dataset", or "final-report".</summary>
        public string Role { get; set; } = "";
        public string? StewardPath { get; set; }
        public string? ArtifactID { get; set; }
        public string? ContentHash { get; set; }
        public StewardArtifactValidationStatus ValidationStatus { get; set; } = StewardArtifactValidationStatus.Unknown;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ValidatedAt { get; set; }
        public List<StewardEvidenceReference> Evidence { get; set; } = new();
    }

    public class StewardActionCheckpoint
    {
        public string ActionID { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? ToolName { get; set; }
        /// <summary>Exact arguments needed to identify/repeat the action, when applicable.</summary>
        public string? ArgumentsJson { get; set; }
        /// <summary>One-way identity for convergence detection; does not persist sensitive tool arguments.</summary>
        public string? Fingerprint { get; set; }
        public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
        public string RecordedBy { get; set; } = "";
        public List<StewardEvidenceReference> Evidence { get; set; } = new();
    }

    public sealed class StewardResumeAction : StewardActionCheckpoint
    {
        public DateTime? NotBefore { get; set; }
        public List<string> Preconditions { get; set; } = new();
    }

    public sealed class StewardRuntimeCheckpoint
    {
        public long Revision { get; set; }
        public int? GrandPlanVersion { get; set; }
        public List<string> ActiveMilestoneIDs { get; set; } = new();
        public List<StewardVerifiedFact> VerifiedFacts { get; set; } = new();
        /// <summary>Bounded durable record of approaches already proven not to work. Seeded into every
        /// wake so a context reset cannot make the agent repeat a known dead end.</summary>
        public List<StewardFailedApproach> FailedApproaches { get; set; } = new();
        public List<StewardCanonicalArtifact> CanonicalArtifacts { get; set; } = new();
        /// <summary>
        /// Automatic per-intent attempt history. Written by the runners on every tool result, so repetition
        /// is visible across wakes and process restarts without the agent having to have noticed it.
        /// </summary>
        public List<StewardAttempt> Attempts { get; set; } = new();
        /// <summary>
        /// The Steward's linear path: the one active step and the queue behind it. Authoritative — unlike
        /// the digest's prose next-steps, nothing rewrites this between wakes.
        /// </summary>
        public List<StewardStep> Steps { get; set; } = new();
        /// <summary>Monotonic source for step ids (s1, s2, …) so an id is never reused after eviction.</summary>
        public int NextStepNumber { get; set; }
        public StewardActionCheckpoint? LastSuccessfulAction { get; set; }
        public Dictionary<string, StewardActionCheckpoint> AgentLastSuccessfulActions { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Bounded durable fingerprints prevent A→B→A rediscovery loops from being
        /// misclassified as fresh progress after a context rollover or process restart.</summary>
        public Dictionary<string, List<string>> RecentSuccessfulFingerprints { get; set; } = new(StringComparer.Ordinal);
        public StewardResumeAction? ResumeAction { get; set; }
        public Dictionary<string, StewardResumeAction> AgentResumeActions { get; set; } = new(StringComparer.Ordinal);
        /// <summary>
        /// A bounded verbatim excerpt of how the previous Commander wake ended — its closing status plus
        /// its last few action→result pairs. Each wake starts a fresh model session (the seed rehydrates
        /// everything else from disk), so this is what preserves the fine-grained "what was I just doing"
        /// detail that the seed's summarised event lines cannot carry.
        /// </summary>
        public string? LastWakeTail { get; set; }
        public DateTime? LastWakeTailAt { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A typed wake request. Applicability is re-evaluated when it is claimed, preventing an old
    /// phase/status-specific nudge from being delivered after Steward state has changed.
    /// </summary>
    public sealed class StewardWakeTrigger
    {
        public string TriggerID { get; set; } = "";
        public StewardWakeTriggerKind Kind { get; set; } = StewardWakeTriggerKind.Other;
        public string Payload { get; set; } = "";
        public long? SourceEventSequence { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? NotBefore { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int Priority { get; set; }
        public string? CoalescingKey { get; set; }
        public bool Durable { get; set; } = true;
        /// <summary>If true, a state/milestone mismatch makes this trigger stale instead of deferred.</summary>
        public bool DiscardWhenInapplicable { get; set; }
        /// <summary>Empty means any execution disposition.</summary>
        public List<StewardExecutionDisposition> AllowedDispositions { get; set; } = new();
        public long? ExpectedCheckpointRevision { get; set; }
        public int? ExpectedGrandPlanVersion { get; set; }
        public List<string> RequiredActiveMilestoneIDs { get; set; } = new();
        public string? ClaimedByWakeID { get; set; }
        public long? ClaimedByLeaseGeneration { get; set; }
        public DateTime? ClaimedAt { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? LastAttemptAt { get; set; }
    }

    /// <summary>
    /// Canonical persisted runtime/checkpoint projection. The append-only event log remains the
    /// audit trail; this small document is the strongly typed, always-current coordination state.
    /// </summary>
    public sealed class StewardRuntimeState
    {
        public string StewardID { get; set; } = "";
        public long Revision { get; set; }
        public StewardExecutionDisposition Disposition { get; set; } = StewardExecutionDisposition.Running;
        public StewardWakeLease? ActiveWakeLease { get; set; }
        /// <summary>Durable per-agent execution leases. Commander ownership remains in
        /// ActiveWakeLease; workers are independently fenced so they may run concurrently.</summary>
        public Dictionary<string, StewardWakeLease> ActiveAgentWakeLeases { get; set; } = new(StringComparer.Ordinal);
        public long LastWakeLeaseGeneration { get; set; }
        public StewardExecutionHealth Health { get; set; } = new();
        public StewardRuntimeBlocker? Blocker { get; set; }
        public StewardRuntimeCheckpoint Checkpoint { get; set; } = new();
        public List<StewardWakeTrigger> PendingTriggers { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Immutable projection of the runtime fields the fleet list renders. Serving those from a
    /// cached summary keeps /demiurge/list off the disk: the full state is a large document, and
    /// the list asks every Steward for it every time any Steward logs an event.
    /// </summary>
    public sealed record StewardRuntimeSummary(
        long Revision,
        StewardExecutionDisposition Disposition,
        StewardExecutionHealthStatus HealthStatus,
        string? BlockerSummary,
        DateTime? NextRetryAt,
        IReadOnlyList<string> ActiveMilestoneIDs,
        long CheckpointRevision);

    public sealed record StewardRuntimeMutationResult(bool Applied, StewardRuntimeState State, string? Reason = null);
    public sealed record StewardWakeLeaseAcquireResult(bool Acquired, StewardRuntimeState State, StewardWakeLease? Lease, string? Reason = null);
    public sealed record StewardWakeTriggerClaimResult(bool Claimed, StewardRuntimeState State, StewardWakeTrigger? Trigger, string? Reason = null);

    /// <summary>
    /// Atomic per-Steward storage and CAS-style mutation API for runtime coordination. Missing files
    /// intentionally read as an empty/default state so existing Stewards need no migration.
    ///
    /// Layout: Demiurge/RuntimeState/&lt;stewardID&gt;.runtime.json
    /// </summary>
    public sealed class StewardRuntimeStateStore
    {
        public const int MaxPendingTriggersPerSteward = 512;

        private readonly string root;
        private readonly Action<string> log;
        // Static so two store instances in the same process cannot race the same Steward file.
        private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
        // Keyed by state-file path (like FileLocks) so instances sharing a root share the projection.
        // This process is the only writer of those files, and every write refreshes the entry.
        private static readonly ConcurrentDictionary<string, StewardRuntimeSummary> SummaryCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed record MutationDecision(bool Applied, bool Changed, string? Reason = null);

        public StewardRuntimeStateStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "RuntimeState")
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        public string GetStatePath(string stewardID)
        {
            ValidateStewardID(stewardID);
            return Path.Combine(root, stewardID + ".runtime.json");
        }

        private object LockFor(string stewardID) => FileLocks.GetOrAdd(GetStatePath(stewardID), _ => new object());

        // Runtime state is read by /demiurge/list and the Steward detail GET (status, disposition,
        // blockers), so it must participate in the response cache's version model or those views
        // serve whatever the state was when the entry was first filled.
        private static string CacheKey(string stewardID) => "demiurge:runtimestate:" + stewardID;

        public StewardRuntimeState Get(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID)) return Clone(LoadLocked(stewardID));
        }

        /// <summary>
        /// The list-view fields only, served from memory once the state has been read or written
        /// once. Every mutation commits through <see cref="CommitLocked"/>, which refreshes this,
        /// so the summary is as current as <see cref="Get"/> without the read + deep clone.
        /// </summary>
        public StewardRuntimeSummary GetSummary(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            string path = GetStatePath(stewardID);
            if (SummaryCache.TryGetValue(path, out var cached)) return cached;
            lock (LockFor(stewardID))
            {
                if (SummaryCache.TryGetValue(path, out cached)) return cached;
                var summary = Summarize(LoadLocked(stewardID));
                SummaryCache[path] = summary;
                return summary;
            }
        }

        private static StewardRuntimeSummary Summarize(StewardRuntimeState state) => new(
            state.Revision,
            state.Disposition,
            state.Health.Status,
            state.Blocker?.Summary,
            state.Health.Circuit.RetryAt ?? state.Blocker?.NextRetryAt,
            state.Checkpoint.ActiveMilestoneIDs.ToArray(),
            state.Checkpoint.Revision);

        public List<StewardRuntimeState> ListWithActiveWakeLeases()
        {
            // Which files exist is not a version the model can track — a brand-new Steward's state
            // file appears without bumping any key this scan could have noted.
            CacheDeps.MarkUncacheable("runtime-state directory scan");
            var states = new List<StewardRuntimeState>();
            if (!Directory.Exists(root)) return states;
            foreach (string path in Directory.EnumerateFiles(root, "*.runtime.json"))
            {
                string stewardID = Path.GetFileName(path)[..^".runtime.json".Length];
                try
                {
                    lock (LockFor(stewardID))
                    {
                        var state = LoadLocked(stewardID);
                        if (state.ActiveWakeLease != null || state.ActiveAgentWakeLeases.Count > 0) states.Add(Clone(state));
                    }
                }
                catch (Exception ex) { log($"Runtime-state scan skipped {stewardID}: {ex.Message}"); }
            }
            return states;
        }

        // ── execution disposition + single-flight lease ──

        public StewardRuntimeMutationResult SetDisposition(string stewardID, StewardExecutionDisposition disposition,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            return Mutate(stewardID, expectedRevision, state =>
            {
                if (state.Disposition == disposition) return new(true, false);
                state.Disposition = disposition;
                return new(true, true);
            }, nowUtc);
        }

        public StewardWakeLeaseAcquireResult TryAcquireWakeLease(string stewardID, string wakeID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            lock (LockFor(stewardID))
            {
                var state = LoadLocked(stewardID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (state.ActiveWakeLease != null)
                    return new(false, Clone(state), Clone(state.ActiveWakeLease),
                        $"Wake {state.ActiveWakeLease.WakeID} generation {state.ActiveWakeLease.Generation} already owns the lease.");
                if (state.Disposition is not (StewardExecutionDisposition.Running or StewardExecutionDisposition.Waiting))
                    return new(false, Clone(state), null, $"Execution disposition is {state.Disposition}.");

                long generation = checked(state.LastWakeLeaseGeneration + 1);
                var lease = new StewardWakeLease
                {
                    WakeID = wakeID.Trim(),
                    Generation = generation,
                    Status = StewardWakeLeaseStatus.Starting,
                    AcquiredAt = now,
                    LastHeartbeatAt = now,
                };
                state.LastWakeLeaseGeneration = generation;
                state.ActiveWakeLease = lease;
                CommitLocked(state, now);
                return new(true, Clone(state), Clone(lease));
            }
        }

        public StewardRuntimeMutationResult MarkWakeRunning(string stewardID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(stewardID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == StewardWakeLeaseStatus.CancellationRequested)
                    return new(false, false, "Cancellation has already been requested.");
                if (lease.Status == StewardWakeLeaseStatus.Running) return new(true, false);
                lease.Status = StewardWakeLeaseStatus.Running;
                lease.LastHeartbeatAt = Utc(nowUtc);
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult HeartbeatWakeLease(string stewardID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(stewardID, wakeID, generation, expectedRevision, lease =>
            {
                DateTime now = Utc(nowUtc);
                if (now <= lease.LastHeartbeatAt) return new(true, false);
                lease.LastHeartbeatAt = now;
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult RequestWakeCancellation(string stewardID, string wakeID, long generation,
            string reason, long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(stewardID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == StewardWakeLeaseStatus.CancellationRequested) return new(true, false);
                lease.Status = StewardWakeLeaseStatus.CancellationRequested;
                lease.CancellationRequestedAt = Utc(nowUtc);
                lease.CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason.Trim();
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult ReleaseWakeLease(string stewardID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                if (!Owns(state.ActiveWakeLease, wakeID, generation))
                    return new(false, false, "The supplied wake ID/generation does not own the active lease.");
                state.ActiveWakeLease = null;
                return new(true, true);
            }, nowUtc);

        public StewardWakeLeaseAcquireResult TryAcquireAgentWakeLease(string stewardID, string agentID, string wakeID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            agentID = agentID.Trim();
            lock (LockFor(stewardID))
            {
                var state = LoadLocked(stewardID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (state.ActiveAgentWakeLeases.TryGetValue(agentID, out var existing))
                    return new(false, Clone(state), Clone(existing),
                        $"Agent {agentID} wake {existing.WakeID} generation {existing.Generation} already owns the lease.");
                if (state.Disposition is not (StewardExecutionDisposition.Running or StewardExecutionDisposition.Waiting))
                    return new(false, Clone(state), null, $"Execution disposition is {state.Disposition}.");

                long generation = checked(state.LastWakeLeaseGeneration + 1);
                var lease = new StewardWakeLease
                {
                    WakeID = wakeID.Trim(), Generation = generation,
                    Status = StewardWakeLeaseStatus.Starting,
                    AcquiredAt = now, LastHeartbeatAt = now,
                };
                state.LastWakeLeaseGeneration = generation;
                state.ActiveAgentWakeLeases[agentID] = lease;
                CommitLocked(state, now);
                return new(true, Clone(state), Clone(lease));
            }
        }

        public StewardRuntimeMutationResult MarkAgentWakeRunning(string stewardID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(stewardID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == StewardWakeLeaseStatus.CancellationRequested)
                    return new(false, false, "Cancellation has already been requested.");
                lease.Status = StewardWakeLeaseStatus.Running;
                lease.LastHeartbeatAt = Utc(nowUtc);
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult HeartbeatAgentWakeLease(string stewardID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(stewardID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                DateTime now = Utc(nowUtc);
                if (now <= lease.LastHeartbeatAt) return new(true, false);
                lease.LastHeartbeatAt = now;
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult RequestAgentWakeCancellation(string stewardID, string agentID, string wakeID, long generation,
            string reason, long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(stewardID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                lease.Status = StewardWakeLeaseStatus.CancellationRequested;
                lease.CancellationRequestedAt ??= Utc(nowUtc);
                lease.CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason.Trim();
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult ReleaseAgentWakeLease(string stewardID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                if (!state.ActiveAgentWakeLeases.TryGetValue(agentID, out var lease) || !Owns(lease, wakeID, generation))
                    return new(false, false, "The supplied agent/wake ID/generation does not own the active lease.");
                state.ActiveAgentWakeLeases.Remove(agentID);
                return new(true, true);
            }, nowUtc);

        private StewardRuntimeMutationResult MutateOwnedAgentLease(string stewardID, string agentID, string wakeID, long generation,
            long? expectedRevision, Func<StewardWakeLease, MutationDecision> mutation, DateTime? nowUtc) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                if (!state.ActiveAgentWakeLeases.TryGetValue(agentID, out var lease) || !Owns(lease, wakeID, generation))
                    return new(false, false, "The supplied agent/wake ID/generation does not own the active lease.");
                return mutation(lease);
            }, nowUtc);

        private StewardRuntimeMutationResult MutateOwnedLease(string stewardID, string wakeID, long generation,
            long? expectedRevision, Func<StewardWakeLease, MutationDecision> mutation, DateTime? nowUtc)
        {
            return Mutate(stewardID, expectedRevision, state =>
            {
                if (!Owns(state.ActiveWakeLease, wakeID, generation))
                    return new(false, false, "The supplied wake ID/generation does not own the active lease.");
                return mutation(state.ActiveWakeLease!);
            }, nowUtc);
        }

        // ── health, circuit and blocker ──

        public StewardRuntimeMutationResult RecordExecutionFailure(string stewardID, StewardExecutionFailure failure,
            bool openCircuit = false, DateTime? circuitRetryAt = null, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(failure);
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                failure = Clone(failure);
                failure.OccurredAt = failure.OccurredAt == default ? now : Utc(failure.OccurredAt);
                state.Health.ConsecutiveFailures++;
                state.Health.LastFailure = failure;
                state.Health.Status = openCircuit ? StewardExecutionHealthStatus.CircuitOpen : StewardExecutionHealthStatus.Degraded;
                state.Health.Circuit.FailureCount = state.Health.ConsecutiveFailures;
                if (openCircuit)
                {
                    state.Health.Circuit.Status = StewardCircuitStatus.Open;
                    state.Health.Circuit.OpenedAt = now;
                    state.Health.Circuit.RetryAt = circuitRetryAt ?? failure.RetryAt;
                    state.Health.Circuit.ReasonCode = failure.Code;
                    state.Health.Circuit.Summary = failure.Summary;
                }
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult RecordExecutionSuccess(string stewardID, long? verifiedProgressSequence = null,
            bool closeCircuit = true, long? expectedRevision = null, DateTime? nowUtc = null,
            string? providerAgentID = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                state.Health.ConsecutiveFailures = 0;
                // Failure history remains in the append-only event log. It must not survive in the
                // current wake handoff after a verified success and repeatedly look like an active fault.
                state.Health.LastFailure = null;
                state.Health.LastSuccessAt = now;
                if (verifiedProgressSequence.HasValue)
                {
                    state.Health.LastVerifiedProgressAt = now;
                    state.Health.LastVerifiedProgressSequence = verifiedProgressSequence;
                }
                if (providerAgentID != null)
                {
                    state.Health.Dependencies.Remove(StewardWakeRecovery.DependencyKey(providerAgentID));
                    // A teammate's success must not erase a newer shared Retry-After window.
                    closeCircuit &= state.Health.Circuit.Status != StewardCircuitStatus.Open
                        || state.Health.Circuit.RetryAt <= now;
                    if (closeCircuit) state.Health.Dependencies.Remove(StewardProviderFailure.DependencyKey);
                }
                if (closeCircuit) state.Health.Circuit = new StewardCircuitBreakerState();
                RecomputeHealthStatus(state);
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult OpenCircuit(string stewardID, string reasonCode, string summary,
            DateTime? retryAt = null, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                state.Health.Status = StewardExecutionHealthStatus.CircuitOpen;
                state.Health.Circuit.Status = StewardCircuitStatus.Open;
                state.Health.Circuit.OpenedAt = now;
                state.Health.Circuit.RetryAt = retryAt;
                state.Health.Circuit.ReasonCode = reasonCode;
                state.Health.Circuit.Summary = summary;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult CloseCircuit(string stewardID, bool halfOpen = false,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                state.Health.Circuit.Status = halfOpen ? StewardCircuitStatus.HalfOpen : StewardCircuitStatus.Closed;
                state.Health.Circuit.RetryAt = null;
                RecomputeHealthStatus(state);
                return new(true, true);
            }, nowUtc);

        /// <summary>Records one independently probed dependency without erasing unrelated health.
        /// Healthy dependencies remain in the map as recent evidence; stale callers can inspect
        /// CheckedAt, while aggregate status is based on the latest value for each key.</summary>
        public StewardRuntimeMutationResult RecordDependencyHealth(string stewardID, string key, bool healthy,
            string code, string summary, DateTime? retryAt = null, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("dependency key required", nameof(key));
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                string normalized = key.Trim();
                state.Health.Dependencies[normalized] = new StewardDependencyHealth
                {
                    Key = normalized,
                    Healthy = healthy,
                    Code = (code ?? "").Trim(),
                    Summary = (summary ?? "").Trim(),
                    CheckedAt = now,
                    RetryAt = retryAt,
                };
                RecomputeHealthStatus(state);
                return new(true, true);
            }, now);
        }

        /// <summary>Remember an actor denied admission during a shared outage. Its deadline survives
        /// a restart and another actor closing the shared circuit, so idle backoff cannot strand it.
        /// Repeated delivery polls are no-ops and never shorten an existing provider restriction.</summary>
        internal StewardRuntimeMutationResult DeferProviderAdmission(string stewardID, string agentID, DateTime retryAt,
            DateTime? nowUtc = null) => Mutate(stewardID, null, state =>
            {
                // A manual-only shared circuit must remain controlled by CloseCircuit; copying
                // its sentinel into an actor dependency would outlive that manual recovery.
                if (retryAt == DateTime.MaxValue) return new(true, false);
                string key = StewardWakeRecovery.DependencyKey(agentID);
                var existing = state.Health.Dependencies.GetValueOrDefault(key);
                if (existing is { Healthy: false, RetryAt: not null } && existing.RetryAt >= retryAt)
                    return new(true, false);
                state.Health.Dependencies[key] = new StewardDependencyHealth
                {
                    Key = key, Healthy = false, Code = existing is { Healthy: false } ? existing.Code : "ProviderAdmission",
                    Summary = existing is { Healthy: false } ? existing.Summary : "Assigned work is waiting for provider admission.",
                    CheckedAt = Utc(nowUtc), RetryAt = retryAt,
                };
                RecomputeHealthStatus(state);
                return new(true, true);
            }, nowUtc);

        /// <summary>
        /// Drops every provider-admission deadline on the Steward — the shared "llm-provider" entry
        /// and each actor's "llm-provider:&lt;agentID&gt;" entry alike. <see cref="CloseCircuit"/> only
        /// clears the shared circuit, but <see cref="StewardWakeRecovery.BlockedUntil"/> consults both,
        /// so a Steward resumed while an actor deadline stood had its wake refused with no visible
        /// reason until the deadline expired (up to 15 minutes, or the router's own Retry-After).
        /// Klives resuming a Steward is an explicit instruction to try the provider again now.
        /// </summary>
        public StewardRuntimeMutationResult ClearProviderAdmission(string stewardID,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                var keys = state.Health.Dependencies.Keys
                    .Where(k => string.Equals(k, StewardProviderFailure.DependencyKey, StringComparison.Ordinal)
                        || k.StartsWith(StewardProviderFailure.DependencyKey + ":", StringComparison.Ordinal))
                    .ToList();
                foreach (string key in keys) state.Health.Dependencies.Remove(key);
                if (keys.Count == 0) return new(true, false);
                RecomputeHealthStatus(state);
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult ClearDependencyHealth(string stewardID, string key,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                bool removed = state.Health.Dependencies.Remove(key);
                RecomputeHealthStatus(state);
                return new(true, removed);
            }, nowUtc);

        /// <summary>Atomically records a watchdog recovery and returns the updated durable count.</summary>
        public StewardRuntimeMutationResult RecordWatchdogRecovery(string stewardID, TimeSpan rollingWindow,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                state.Health.Watchdog.RecoveriesUtc.RemoveAll(t => now - Utc(t) > rollingWindow);
                state.Health.Watchdog.RecoveriesUtc.Add(now);
                state.Health.Watchdog.RecoveryCount = checked(state.Health.Watchdog.RecoveryCount + 1);
                state.Health.Watchdog.LastRecoveryAt = now;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult RecordWatchdogEscalation(string stewardID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                state.Health.Watchdog.LastEscalationAt = now;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult RecordGateReminder(string stewardID, string gateID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(gateID)) throw new ArgumentException("gateID required", nameof(gateID));
            return Mutate(stewardID, expectedRevision, state =>
            {
                if (state.Health.Watchdog.RemindedGateIDs.Contains(gateID, StringComparer.Ordinal))
                    return new(false, false, "Gate reminder was already recorded.");
                state.Health.Watchdog.RemindedGateIDs.Add(gateID);
                return new(true, true);
            }, nowUtc);
        }

        public StewardRuntimeMutationResult SetBlocker(string stewardID, StewardRuntimeBlocker blocker,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(blocker);
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                var b = Clone(blocker);
                if (string.IsNullOrWhiteSpace(b.BlockerID)) b.BlockerID = Guid.NewGuid().ToString("N");
                b.CreatedAt = b.CreatedAt == default ? now : Utc(b.CreatedAt);
                b.UpdatedAt = now;
                state.Blocker = b;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult ClearBlocker(string stewardID, string? expectedBlockerID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                if (state.Blocker == null) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedBlockerID) && state.Blocker.BlockerID != expectedBlockerID)
                    return new(false, false, "The active blocker does not match expectedBlockerID.");
                state.Blocker = null;
                return new(true, true);
            }, nowUtc);

        // ── typed checkpoint ──

        public StewardRuntimeMutationResult SetActiveMilestones(string stewardID, int? grandPlanVersion,
            IEnumerable<string> milestoneIDs, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            var ids = (milestoneIDs ?? Array.Empty<string>()).Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                checkpoint.GrandPlanVersion = grandPlanVersion;
                checkpoint.ActiveMilestoneIDs = ids;
                return new(true, true);
            }, nowUtc);
        }

        public StewardRuntimeMutationResult UpsertVerifiedFact(string stewardID, StewardVerifiedFact fact,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (string.IsNullOrWhiteSpace(fact.Key)) throw new ArgumentException("Verified fact key required.", nameof(fact));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(fact);
                copy.Key = copy.Key.Trim();
                copy.VerifiedAt = copy.VerifiedAt == default ? now : Utc(copy.VerifiedAt);
                int index = checkpoint.VerifiedFacts.FindIndex(x =>
                    string.Equals(x.Key, copy.Key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && string.IsNullOrWhiteSpace(copy.FactID)) copy.FactID = checkpoint.VerifiedFacts[index].FactID;
                if (string.IsNullOrWhiteSpace(copy.FactID)) copy.FactID = Guid.NewGuid().ToString("N");
                if (index >= 0) checkpoint.VerifiedFacts[index] = copy; else checkpoint.VerifiedFacts.Add(copy);
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult InvalidateVerifiedFact(string stewardID, string key, string reason,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var fact = checkpoint.VerifiedFacts.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (fact == null) return new(false, false, $"No verified fact named '{key}'.");
                fact.InvalidatedAt = now;
                fact.InvalidationReason = reason;
                return new(true, true);
            }, now);
        }

        public List<StewardVerifiedFact> GetFreshVerifiedFacts(string stewardID, DateTime? nowUtc = null)
        {
            // Freshness is a function of wall-clock time, not of any tracked version: a fact expires
            // on its own with no write to bump. Caching this would serve expired facts as fresh.
            CacheDeps.MarkUncacheable("verified-fact freshness window");
            DateTime now = Utc(nowUtc);
            return Get(stewardID).Checkpoint.VerifiedFacts.Where(f => f.IsFreshAt(now)).Select(Clone).ToList();
        }

        /// <summary>Record how the wake that just finished ended, for the next wake to open with.
        /// Passing null/blank clears it.</summary>
        public StewardRuntimeMutationResult SetLastWakeTail(string stewardID, string? tail,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                string? value = string.IsNullOrWhiteSpace(tail) ? null : tail.Trim();
                if (string.Equals(checkpoint.LastWakeTail, value, StringComparison.Ordinal)) return new(true, false);
                checkpoint.LastWakeTail = value;
                checkpoint.LastWakeTailAt = value == null ? null : now;
                return new(true, true);
            }, now);
        }

        /// <summary>Newest dead ends first; the list is bounded so a long-running Steward cannot grow it without limit.</summary>
        public const int MaxFailedApproaches = 32;

        /// <summary>
        /// Bound on the automatic attempt ledger. Larger than the curated dead-end list, but deliberately not
        /// huge: the whole runtime state file is rewritten on every tool call that touches it, so each row
        /// here is bytes on a per-tool-call write. Only 24 rows are ever seeded, and eviction keeps failures.
        /// </summary>
        public const int MaxAttempts = 96;

        /// <summary>How many failures of one intent make it worth telling the agent about.</summary>
        public const int RepeatedFailureThreshold = 2;

        /// <summary>How many failed intents the wake seed lists.</summary>
        private const int SeededAttempts = 24;

        /// <summary>
        /// Record one attempt at an intent and return the row AS IT WAS BEFORE this attempt, so the caller
        /// can tell the agent what already happened. Returns null the first time an intent is seen.
        ///
        /// Every tool call comes through here, successes included: a success is what clears an intent's
        /// "this keeps failing" state, and without recording it a tool that worked on the fourth try would
        /// keep being flagged forever.
        /// </summary>
        public StewardAttempt? RecordAttempt(string stewardID, string attemptKey, string toolName, string summary,
            bool succeeded, string? outcome, string? wakeID, string? agentID, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(attemptKey)) return null;
            DateTime now = Utc(nowUtc);
            StewardAttempt? previous = null;
            MutateCheckpoint(stewardID, null, checkpoint =>
            {
                int index = checkpoint.Attempts.FindIndex(x =>
                    string.Equals(x.AttemptKey, attemptKey, StringComparison.Ordinal));
                var entry = index >= 0 ? checkpoint.Attempts[index] : null;
                if (entry != null) previous = Clone(entry);

                entry ??= new StewardAttempt { AttemptKey = attemptKey, FirstAt = now };
                entry.ToolName = toolName ?? entry.ToolName;
                entry.Summary = string.IsNullOrWhiteSpace(summary) ? entry.Summary : Clip(summary, 160);
                entry.Count++;
                if (!succeeded) entry.FailureCount++;
                entry.LastAt = now;
                entry.LastSucceeded = succeeded;
                entry.LastOutcome = Clip(outcome, 200);
                entry.LastWakeID = wakeID;
                entry.LastAgentID = agentID;

                if (index >= 0) checkpoint.Attempts[index] = entry; else checkpoint.Attempts.Add(entry);

                // The active step's attempt count is derived from real tool activity, not from the agent
                // remembering to report it — a step that has silently been retried nine times should say so.
                ChargeActiveStep(checkpoint, outcome, succeeded, now);

                // Bound the ledger: shed intents that ended in success before ones still failing, then the
                // least recently attempted. Unresolved failures are the rows worth keeping.
                if (checkpoint.Attempts.Count > MaxAttempts)
                {
                    checkpoint.Attempts = checkpoint.Attempts
                        .OrderBy(x => x.IsRepeatedFailure(RepeatedFailureThreshold) ? 0 : 1)
                        .ThenByDescending(x => x.LastAt)
                        .Take(MaxAttempts)
                        .ToList();
                }
                return new(true, true);
            }, now);
            return previous;
        }

        // ── the step ledger: one linear path ──

        /// <summary>Bound on the ledger; closed steps are evicted oldest-first once it is exceeded.</summary>
        public const int MaxSteps = 64;

        /// <summary>How many queued and recently closed steps the wake seed shows.</summary>
        private const int SeededQueuedSteps = 8;
        private const int SeededClosedSteps = 5;

        /// <summary>Append steps to the end of the queue. Returns the created steps.</summary>
        public List<StewardStep> QueueSteps(string stewardID, IEnumerable<StewardStep> steps, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(steps);
            DateTime now = Utc(nowUtc);
            var created = new List<StewardStep>();
            MutateCheckpoint(stewardID, null, checkpoint =>
            {
                int nextOrder = checkpoint.Steps.Count == 0 ? 1 : checkpoint.Steps.Max(x => x.Order) + 1;
                foreach (var step in steps)
                {
                    if (string.IsNullOrWhiteSpace(step?.Title)) continue;
                    var copy = Clone(step!);
                    copy.StepID = $"s{++checkpoint.NextStepNumber}";
                    copy.Title = Clip(copy.Title, 200);
                    copy.NextConcreteAction = string.IsNullOrWhiteSpace(copy.NextConcreteAction) ? null : Clip(copy.NextConcreteAction, 400);
                    copy.MilestoneID = string.IsNullOrWhiteSpace(copy.MilestoneID) ? null : copy.MilestoneID!.Trim();
                    copy.Status = StewardStepStatus.Queued;
                    copy.Order = nextOrder++;
                    copy.Attempts = 0;
                    copy.CreatedAt = now;
                    copy.UpdatedAt = now;
                    copy.StartedAt = null;
                    copy.ClosedAt = null;
                    checkpoint.Steps.Add(copy);
                    created.Add(Clone(copy));
                }
                if (created.Count == 0) return new(false, false, "No steps with a title were supplied.");
                EvictClosedSteps(checkpoint);
                return new(true, true);
            }, now);
            return created;
        }

        /// <summary>
        /// Make one step the active one, demoting whatever was active back to the queue. This is where the
        /// single-active-step invariant is enforced: the model cannot hold two lines of work open by
        /// forgetting to close the first.
        /// </summary>
        public StewardRuntimeMutationResult ActivateStep(string stewardID, string stepID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, null, checkpoint =>
            {
                var target = FindStep(checkpoint, stepID);
                if (target == null) return new(false, false, $"No step '{stepID}' exists.");
                if (!target.IsOpen) return new(false, false, $"Step '{stepID}' is {target.Status} and cannot be activated; queue a new step instead.");
                if (target.Status == StewardStepStatus.Active) return new(true, false);
                foreach (var other in checkpoint.Steps.Where(x => x.Status == StewardStepStatus.Active))
                {
                    other.Status = StewardStepStatus.Queued;
                    other.UpdatedAt = now;
                }
                target.Status = StewardStepStatus.Active;
                target.StartedAt ??= now;
                target.UpdatedAt = now;
                return new(true, true);
            }, now);
        }

        /// <summary>Update the active-facing fields of a step: what to do next, and the latest outcome.</summary>
        public StewardRuntimeMutationResult UpdateStep(string stewardID, string stepID, string? nextConcreteAction,
            string? lastAttemptOutcome, string? ownerAgentID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, null, checkpoint =>
            {
                var step = FindStep(checkpoint, stepID);
                if (step == null) return new(false, false, $"No step '{stepID}' exists.");
                bool changed = false;
                if (!string.IsNullOrWhiteSpace(nextConcreteAction)) { step.NextConcreteAction = Clip(nextConcreteAction, 400); changed = true; }
                if (!string.IsNullOrWhiteSpace(lastAttemptOutcome)) { step.LastAttemptOutcome = Clip(lastAttemptOutcome, 300); changed = true; }
                if (!string.IsNullOrWhiteSpace(ownerAgentID)) { step.OwnerAgentID = ownerAgentID!.Trim(); changed = true; }
                if (!changed) return new(false, false, "Supply nextAction, outcome, and/or owner.");
                step.UpdatedAt = now;
                return new(true, true);
            }, now);
        }

        /// <summary>
        /// Close a step. Reaching <see cref="StewardStepStatus.Done"/> requires an evidence reference, the
        /// same rule the Grand Plan applies to milestones — a step is not done because the agent believes
        /// it is, but because something durable shows it.
        /// </summary>
        public StewardRuntimeMutationResult CloseStep(string stewardID, string stepID, StewardStepStatus result,
            string reason, List<StewardEvidenceReference>? evidence = null, DateTime? nowUtc = null)
        {
            if (result is not (StewardStepStatus.Done or StewardStepStatus.Abandoned or StewardStepStatus.Blocked))
                throw new ArgumentException("A step closes as Done, Abandoned or Blocked.", nameof(result));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, null, checkpoint =>
            {
                var step = FindStep(checkpoint, stepID);
                if (step == null) return new(false, false, $"No step '{stepID}' exists.");
                if (step.Status is StewardStepStatus.Done or StewardStepStatus.Abandoned)
                    return new(false, false, $"Step '{stepID}' is already {step.Status}.");
                bool hasEvidence = evidence != null && evidence.Any(e => !string.IsNullOrWhiteSpace(e.Reference));
                if (result == StewardStepStatus.Done && !hasEvidence)
                    return new(false, false, $"Closing step '{stepID}' as done requires an evidence reference (an event sequence, an artifact id, or a /project path).");
                if (string.IsNullOrWhiteSpace(reason))
                    return new(false, false, "Supply 'reason' describing the outcome.");
                step.Status = result;
                step.ClosureReason = Clip(reason, 400);
                step.ClosedAt = result == StewardStepStatus.Blocked ? null : now;
                step.UpdatedAt = now;
                if (evidence != null) step.Evidence.AddRange(evidence.Where(e => !string.IsNullOrWhiteSpace(e.Reference)));
                EvictClosedSteps(checkpoint);
                return new(true, true);
            }, now);
        }

        /// <summary>Reorder the open queue. Ids not listed keep their relative order behind the listed ones.</summary>
        public StewardRuntimeMutationResult ReorderSteps(string stewardID, IEnumerable<string> stepIDsInOrder, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            var requested = (stepIDsInOrder ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            return MutateCheckpoint(stewardID, null, checkpoint =>
            {
                if (requested.Count == 0) return new(false, false, "Supply at least one step id.");
                int order = 0;
                foreach (string id in requested)
                {
                    var step = FindStep(checkpoint, id);
                    if (step == null) return new(false, false, $"No step '{id}' exists.");
                    step.Order = ++order;
                    step.UpdatedAt = now;
                }
                foreach (var step in checkpoint.Steps
                    .Where(x => !requested.Contains(x.StepID, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => x.Order))
                    step.Order = ++order;
                return new(true, true);
            }, now);
        }

        public StewardStep? GetActiveStep(string stewardID) =>
            Get(stewardID).Checkpoint.Steps
                .Where(x => x.Status == StewardStepStatus.Active)
                .Select(Clone).FirstOrDefault();

        public List<StewardStep> ListSteps(string stewardID) =>
            Get(stewardID).Checkpoint.Steps.OrderBy(x => x.Order).Select(Clone).ToList();

        /// <summary>True while any step is open, i.e. the ledger — not the digest prose — owns the plan.</summary>
        public bool HasOpenSteps(string stewardID) =>
            Get(stewardID).Checkpoint.Steps.Any(x => x.IsOpen);

        /// <summary>
        /// Charge one attempt against whatever step is currently active. Called from the attempt ledger, so
        /// the count reflects what actually happened rather than what the agent remembered to report.
        /// </summary>
        private static void ChargeActiveStep(StewardRuntimeCheckpoint checkpoint, string? outcome, bool succeeded, DateTime now)
        {
            var active = checkpoint.Steps.FirstOrDefault(x => x.Status == StewardStepStatus.Active);
            if (active == null) return;
            active.Attempts++;
            active.LastAttemptOutcome = Clip((succeeded ? "" : "failed: ") + (outcome ?? ""), 300);
            active.UpdatedAt = now;
        }

        private static StewardStep? FindStep(StewardRuntimeCheckpoint checkpoint, string? stepID) =>
            string.IsNullOrWhiteSpace(stepID) ? null : checkpoint.Steps.FirstOrDefault(x =>
                string.Equals(x.StepID, stepID.Trim(), StringComparison.OrdinalIgnoreCase));

        private static void EvictClosedSteps(StewardRuntimeCheckpoint checkpoint)
        {
            if (checkpoint.Steps.Count <= MaxSteps) return;
            // Open steps are never evicted — losing a queued step loses real plan. Closed ones age out.
            var open = checkpoint.Steps.Where(x => x.IsOpen).ToList();
            var closed = checkpoint.Steps.Where(x => !x.IsOpen)
                .OrderByDescending(x => x.ClosedAt ?? x.UpdatedAt)
                .Take(Math.Max(0, MaxSteps - open.Count))
                .ToList();
            checkpoint.Steps = open.Concat(closed).OrderBy(x => x.Order).ToList();
        }

        /// <summary>
        /// The linear path as the agent sees it: the one thing in flight (with its attempt count, last
        /// outcome and next concrete action), the queue behind it, and what was recently closed.
        /// </summary>
        public string DescribeStepsForWake(string stewardID)
        {
            var steps = Get(stewardID).Checkpoint.Steps;
            if (steps.Count == 0) return "";
            var sb = new System.Text.StringBuilder();

            var active = steps.FirstOrDefault(x => x.Status == StewardStepStatus.Active);
            if (active != null)
            {
                sb.AppendLine("── CURRENT STEP (the ONE thing in flight) ──");
                sb.AppendLine($"{active.StepID}  {active.Title}" +
                    (string.IsNullOrWhiteSpace(active.MilestoneID) ? "" : $"  [milestone {active.MilestoneID}]"));
                sb.AppendLine($"    attempts {active.Attempts}" +
                    (active.StartedAt.HasValue ? $" · active since {Data_Handling.TemporalFormat.StampWithAge(active.StartedAt.Value)}" : "") +
                    (string.IsNullOrWhiteSpace(active.OwnerAgentID) ? "" : $" · owner {active.OwnerAgentID}"));
                if (!string.IsNullOrWhiteSpace(active.LastAttemptOutcome))
                    sb.AppendLine($"    last attempt: {active.LastAttemptOutcome}");
                sb.AppendLine(string.IsNullOrWhiteSpace(active.NextConcreteAction)
                    ? "    next concrete action: (not set — set it with checkpoint op:update_step before you finish this wake)"
                    : $"    next concrete action: {active.NextConcreteAction}");
            }

            var queued = steps.Where(x => x.Status is StewardStepStatus.Queued or StewardStepStatus.Blocked)
                .OrderBy(x => x.Order).Take(SeededQueuedSteps).ToList();
            if (queued.Count > 0)
            {
                sb.AppendLine(active == null
                    ? "── STEP QUEUE (nothing is active — activate the first one with checkpoint op:activate_step) ──"
                    : "── STEP QUEUE (work these in order; do not jump ahead) ──");
                foreach (var step in queued)
                    sb.AppendLine($"{step.StepID}  {step.Title}" +
                        (step.Status == StewardStepStatus.Blocked ? $"  [BLOCKED: {step.ClosureReason}]" : "") +
                        (string.IsNullOrWhiteSpace(step.MilestoneID) ? "" : $"  [milestone {step.MilestoneID}]"));
            }

            var closed = steps.Where(x => x.Status is StewardStepStatus.Done or StewardStepStatus.Abandoned)
                .OrderByDescending(x => x.ClosedAt ?? x.UpdatedAt).Take(SeededClosedSteps).ToList();
            if (closed.Count > 0)
            {
                sb.AppendLine("── RECENTLY CLOSED (do not re-open these without new information) ──");
                foreach (var step in closed)
                    sb.AppendLine($"{step.StepID}  {(step.Status == StewardStepStatus.Done ? "✓" : "✗ abandoned:")} {step.Title}" +
                        $" ({Data_Handling.TemporalFormat.StampWithAge(step.ClosedAt ?? step.UpdatedAt)})" +
                        (string.IsNullOrWhiteSpace(step.ClosureReason) ? "" : $" — {step.ClosureReason}"));
            }
            return sb.ToString().TrimEnd();
        }

        public StewardAttempt? GetAttempt(string stewardID, string attemptKey) =>
            Get(stewardID).Checkpoint.Attempts
                .Where(x => string.Equals(x.AttemptKey, attemptKey, StringComparison.Ordinal))
                .Select(Clone).FirstOrDefault();

        /// <summary>Intents that have failed repeatedly and have not since succeeded, worst and newest first.</summary>
        public List<StewardAttempt> GetRepeatedFailures(string stewardID) =>
            Get(stewardID).Checkpoint.Attempts
                .Where(x => x.IsRepeatedFailure(RepeatedFailureThreshold))
                .OrderByDescending(x => x.FailureCount)
                .ThenByDescending(x => x.LastAt)
                .Select(Clone).ToList();

        /// <summary>
        /// Record (or re-record) an approach that did not work. Re-recording the same <paramref name="approach"/>.Key
        /// increments its attempt count rather than adding a duplicate, so the seed shows "tried 4 times" instead of
        /// four near-identical lines.
        /// </summary>
        public StewardRuntimeMutationResult RecordFailedApproach(string stewardID, StewardFailedApproach approach,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(approach);
            if (string.IsNullOrWhiteSpace(approach.Key)) throw new ArgumentException("Failed-approach key required.", nameof(approach));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(approach);
                copy.Key = copy.Key.Trim();
                copy.LastAttemptedAt = now;

                int index = checkpoint.FailedApproaches.FindIndex(x =>
                    string.Equals(x.Key, copy.Key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var existing = checkpoint.FailedApproaches[index];
                    copy.ApproachID = string.IsNullOrWhiteSpace(copy.ApproachID) ? existing.ApproachID : copy.ApproachID;
                    copy.FirstAttemptedAt = existing.FirstAttemptedAt == default ? now : existing.FirstAttemptedAt;
                    copy.AttemptCount = Math.Max(existing.AttemptCount, 0) + Math.Max(1, approach.AttemptCount);
                    // A re-attempt reopens a previously resolved dead end: it evidently is not resolved.
                    copy.ResolvedAt = null;
                    copy.ResolutionReason = null;
                    // Keep the better alternative if this attempt didn't supply a new one.
                    if (string.IsNullOrWhiteSpace(copy.Instead)) copy.Instead = existing.Instead;
                    checkpoint.FailedApproaches[index] = copy;
                }
                else
                {
                    copy.ApproachID = string.IsNullOrWhiteSpace(copy.ApproachID) ? Guid.NewGuid().ToString("N") : copy.ApproachID;
                    copy.FirstAttemptedAt = copy.FirstAttemptedAt == default ? now : Utc(copy.FirstAttemptedAt);
                    copy.AttemptCount = Math.Max(1, copy.AttemptCount);
                    checkpoint.FailedApproaches.Add(copy);
                }

                // Bound the list: drop the least recently attempted, preferring to shed resolved entries first.
                if (checkpoint.FailedApproaches.Count > MaxFailedApproaches)
                {
                    checkpoint.FailedApproaches = checkpoint.FailedApproaches
                        .OrderBy(x => x.ResolvedAt == null ? 0 : 1)
                        .ThenByDescending(x => x.LastAttemptedAt)
                        .Take(MaxFailedApproaches)
                        .ToList();
                }
                return new(true, true);
            }, now);
        }

        /// <summary>Mark a dead end resolved — the approach now works, or is no longer relevant. Kept rather
        /// than deleted so the attempt history survives for the record.</summary>
        public StewardRuntimeMutationResult ResolveFailedApproach(string stewardID, string key, string reason,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var entry = checkpoint.FailedApproaches.FirstOrDefault(x =>
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return new(false, false, $"No recorded dead end named '{key}'.");
                entry.ResolvedAt = now;
                entry.ResolutionReason = reason;
                return new(true, true);
            }, now);
        }

        /// <summary>
        /// Resolve dead ends whose subject was a harness gap the harness has since closed. A dead end
        /// is re-seeded into every wake forever — correct for a fact about the world, wrong for one
        /// about a missing capability. Several Stewards recorded the browser's native file dialog as a
        /// permanent blocker while nothing could drive it; computer_upload_file drives it now, so those
        /// entries would otherwise keep steering agents to ask Klives for a manual VNC click.
        /// Idempotent and write-free once there is nothing left to match.
        /// </summary>
        public StewardRuntimeMutationResult RetireObsoleteDeadEnds(string stewardID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            var current = Get(stewardID);
            bool anyMatch = current.Checkpoint.FailedApproaches
                .Any(x => x.IsActiveAt(now) && StewardUploadCapability.DescribesFileDialogBlocker(
                    x.Key, x.Approach, x.Outcome, x.Instead));
            if (!anyMatch) return new StewardRuntimeMutationResult(false, current);
            return MutateCheckpoint(stewardID, null, checkpoint =>
            {
                bool changed = false;
                foreach (var entry in checkpoint.FailedApproaches)
                {
                    if (!entry.IsActiveAt(now) || !StewardUploadCapability.DescribesFileDialogBlocker(
                            entry.Key, entry.Approach, entry.Outcome, entry.Instead)) continue;
                    entry.ResolvedAt = now;
                    entry.ResolutionReason =
                        "Superseded by a harness capability: computer_upload_file drives the browser's native file " +
                        "chooser (ctrl+l, absolute container path, confirm) and otherwise attaches the file to the " +
                        "page's own file input. Uploading no longer needs a human — retry it yourself.";
                    changed = true;
                }
                return new(changed, changed);
            }, now);
        }

        public List<StewardFailedApproach> GetActiveFailedApproaches(string stewardID, DateTime? nowUtc = null)
        {
            // Same reasoning as GetFreshVerifiedFacts: RetryNotBefore expires on wall-clock alone.
            CacheDeps.MarkUncacheable("failed-approach retry window");
            DateTime now = Utc(nowUtc);
            return Get(stewardID).Checkpoint.FailedApproaches
                .Where(x => x.IsActiveAt(now))
                .OrderByDescending(x => x.LastAttemptedAt)
                .Select(Clone).ToList();
        }

        /// <summary>Compact machine-owned handoff block. Unlike the narrative digest, these facts,
        /// blockers, artifact identities and the exact resume action are never rewritten by an LLM.</summary>
        public string DescribeForWake(string stewardID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            var state = Get(stewardID);
            var sb = new System.Text.StringBuilder();

            // The linear path goes first, before health, facts or dead ends. It is the answer to the only
            // question the agent must resolve before doing anything: what is the one thing in flight.
            string stepLedger = DescribeStepsForWake(stewardID);
            if (stepLedger.Length > 0) sb.AppendLine(stepLedger);

            sb.AppendLine($"runtime revision: {state.Revision}; disposition: {state.Disposition}; health: {state.Health.Status}");
            if (state.Health.Circuit.Status != StewardCircuitStatus.Closed)
                sb.AppendLine($"provider circuit: {state.Health.Circuit.Status}; reason={state.Health.Circuit.ReasonCode ?? "unknown"}; retryAt={state.Health.Circuit.RetryAt?.ToString("O") ?? "manual"}");
            if (state.Health.LastFailure != null)
                sb.AppendLine($"last execution failure [{state.Health.LastFailure.Category}/{state.Health.LastFailure.Code}]: {state.Health.LastFailure.Summary}");
            foreach (var dependency in state.Health.Dependencies.Values.Where(x => !x.Healthy).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"dependency degraded [{dependency.Key}/{dependency.Code}]: {dependency.Summary}; checked={dependency.CheckedAt:O}" +
                    (dependency.RetryAt.HasValue ? $"; retryAt={dependency.RetryAt:O}" : ""));
            if (state.Blocker != null)
                sb.AppendLine($"blocker [{state.Blocker.Category}/{state.Blocker.Code}]: {state.Blocker.Summary}" +
                    (state.Blocker.NextRetryAt.HasValue ? $"; nextRetry={state.Blocker.NextRetryAt:O}" : ""));
            if (state.Checkpoint.GrandPlanVersion.HasValue || state.Checkpoint.ActiveMilestoneIDs.Count > 0)
                sb.AppendLine($"checkpoint: plan v{state.Checkpoint.GrandPlanVersion?.ToString() ?? "?"}; active milestones: {(state.Checkpoint.ActiveMilestoneIDs.Count == 0 ? "none" : string.Join(", ", state.Checkpoint.ActiveMilestoneIDs))}");
            if (state.Checkpoint.LastSuccessfulAction != null)
                sb.AppendLine($"last verified action: {state.Checkpoint.LastSuccessfulAction.Summary} ({state.Checkpoint.LastSuccessfulAction.RecordedAt:O})");
            // With a step active, the step's next concrete action is the authoritative "what now" and this
            // becomes context on how the last context ended. Two competing resume instructions is how an
            // agent talks itself into a third, invented one.
            if (state.Checkpoint.ResumeAction != null)
                sb.AppendLine((stepLedger.Contains("── CURRENT STEP", StringComparison.Ordinal)
                        ? "how the previous context ended (the current step above is what to resume): "
                        : "EXACT RESUME ACTION: ")
                    + state.Checkpoint.ResumeAction.Summary
                    + (state.Checkpoint.ResumeAction.NotBefore.HasValue ? $"; notBefore={state.Checkpoint.ResumeAction.NotBefore:O}" : ""));
            if (state.ActiveAgentWakeLeases.Count > 0)
                sb.AppendLine("active worker leases: " + string.Join(", ", state.ActiveAgentWakeLeases.Select(x =>
                    $"{x.Key}/{x.Value.WakeID[..Math.Min(8, x.Value.WakeID.Length)]} {x.Value.Status} heartbeat={x.Value.LastHeartbeatAt:O}")));
            if (state.Checkpoint.AgentResumeActions.Count > 0)
            {
                sb.AppendLine("worker resume actions:");
                foreach (var entry in state.Checkpoint.AgentResumeActions.Take(24))
                    sb.AppendLine($"- {entry.Key}: {entry.Value.Summary}");
            }

            AppendStewardKnowledge(sb, state.Checkpoint, now);

            if (state.Checkpoint.CanonicalArtifacts.Count > 0)
            {
                sb.AppendLine("canonical artifacts:");
                foreach (var artifact in state.Checkpoint.CanonicalArtifacts.Take(24))
                    sb.AppendLine($"- {artifact.Role}: {artifact.StewardPath ?? artifact.ArtifactID ?? "unresolved"}; validation={artifact.ValidationStatus}" +
                        $"; updated={Data_Handling.TemporalFormat.StampWithAge(artifact.UpdatedAt == default ? artifact.RegisteredAt : artifact.UpdatedAt)}" +
                        (string.IsNullOrWhiteSpace(artifact.ContentHash) ? "" : $"; hash={artifact.ContentHash}"));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The Steward's accumulated positive and negative knowledge, for a sub-agent's seed. Workers do
        /// not get the full runtime block — leases, circuits and dispositions are the Commander's concern —
        /// but they must get this, or each new worker starts by re-proving what the Steward already knows.
        /// </summary>
        public string DescribeStewardKnowledgeForWorker(string stewardID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            var sb = new System.Text.StringBuilder();
            AppendStewardKnowledge(sb, Get(stewardID).Checkpoint, now);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Verified facts, curated dead ends and the automatic attempt ledger — the three things that must
        /// reach every agent on every wake, because a fresh context has no other way to know them.
        /// </summary>
        private static void AppendStewardKnowledge(System.Text.StringBuilder sb, StewardRuntimeCheckpoint checkpoint, DateTime now)
        {
            var facts = checkpoint.VerifiedFacts.Where(f => f.IsFreshAt(now)).Take(32).ToList();
            if (facts.Count > 0)
            {
                sb.AppendLine("verified facts (fresh):");
                foreach (var fact in facts)
                    sb.AppendLine($"- {fact.Key} = {fact.Value}; verified={fact.VerifiedAt:O}" +
                        (fact.ValidUntil.HasValue ? $"; validUntil={fact.ValidUntil:O}" : "") +
                        (fact.Evidence.Count > 0 ? $"; evidence={string.Join(",", fact.Evidence.Take(3).Select(e => e.Reference))}" : ""));
            }

            // Negative knowledge. Seeded on EVERY wake precisely because a context reset would otherwise
            // let the agent re-try something it already proved does not work.
            var deadEnds = checkpoint.FailedApproaches.Where(x => x.IsActiveAt(now)).ToList();
            if (deadEnds.Count > 0)
            {
                sb.AppendLine("dead ends (already tried and failed — do not repeat without new information):");
                foreach (var dead in deadEnds.OrderByDescending(x => x.LastAttemptedAt).Take(24))
                    sb.AppendLine($"- {dead.Key}: tried {dead.Approach} → {dead.Outcome}" +
                        $"; attempts={dead.AttemptCount}; last={Data_Handling.TemporalFormat.StampWithAge(dead.LastAttemptedAt)}" +
                        (string.IsNullOrWhiteSpace(dead.Instead) ? "" : $"; instead: {dead.Instead}") +
                        (dead.Evidence.Count > 0 ? $"; evidence={string.Join(",", dead.Evidence.Take(3).Select(e => e.Reference))}" : ""));
            }

            // The automatic counterpart to the curated dead ends above. Dead ends require someone to have
            // decided an approach is finished; this row appears whether or not anybody noticed, which is
            // what catches the same intent being retried wake after wake with cosmetic argument changes.
            var repeated = checkpoint.Attempts
                .Where(x => x.IsRepeatedFailure(RepeatedFailureThreshold))
                .OrderByDescending(x => x.FailureCount)
                .ThenByDescending(x => x.LastAt)
                .Take(SeededAttempts)
                .ToList();
            if (repeated.Count > 0)
            {
                sb.AppendLine("already tried (failed attempts recorded automatically — a different approach is needed, not another go):");
                foreach (var attempt in repeated)
                    sb.AppendLine($"- {attempt.AttemptKey}: {attempt.Summary}" +
                        $"; failed {attempt.FailureCount}× of {attempt.Count}" +
                        $"; last {Data_Handling.TemporalFormat.StampWithAge(attempt.LastAt)}" +
                        $" → {attempt.LastOutcome}");
            }
        }

        public StewardRuntimeMutationResult UpsertCanonicalArtifact(string stewardID, StewardCanonicalArtifact artifact,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.Role)) throw new ArgumentException("Canonical artifact role required.", nameof(artifact));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(artifact);
                copy.Role = copy.Role.Trim();
                int index = checkpoint.CanonicalArtifacts.FindIndex(x =>
                    string.Equals(x.Role, copy.Role, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var old = checkpoint.CanonicalArtifacts[index];
                    if (string.IsNullOrWhiteSpace(copy.CanonicalArtifactID)) copy.CanonicalArtifactID = old.CanonicalArtifactID;
                    if (copy.RegisteredAt == default) copy.RegisteredAt = old.RegisteredAt;
                }
                if (string.IsNullOrWhiteSpace(copy.CanonicalArtifactID)) copy.CanonicalArtifactID = Guid.NewGuid().ToString("N");
                if (copy.RegisteredAt == default) copy.RegisteredAt = now;
                copy.UpdatedAt = now;
                if (index >= 0) checkpoint.CanonicalArtifacts[index] = copy; else checkpoint.CanonicalArtifacts.Add(copy);
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult RemoveCanonicalArtifact(string stewardID, string role,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                int removed = checkpoint.CanonicalArtifacts.RemoveAll(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase));
                return removed > 0 ? new(true, true) : new(false, false, $"No canonical artifact with role '{role}'.");
            }, nowUtc);

        public StewardRuntimeMutationResult SetLastSuccessfulAction(string stewardID, StewardActionCheckpoint action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.LastSuccessfulAction = copy;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult SetAgentLastSuccessfulAction(string stewardID, string agentID,
            StewardActionCheckpoint action, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.AgentLastSuccessfulActions[agentID.Trim()] = copy;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult TryRecordNovelSuccessfulAction(string stewardID, string actorID,
            StewardActionCheckpoint action, int historyLimit = 128, long? expectedRevision = null,
            DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(actorID)) throw new ArgumentException("actorID required", nameof(actorID));
            ArgumentNullException.ThrowIfNull(action);
            if (string.IsNullOrWhiteSpace(action.Fingerprint))
                throw new ArgumentException("A successful-action fingerprint is required.", nameof(action));
            string actor = actorID.Trim();
            historyLimit = Math.Clamp(historyLimit, 8, 512);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                if (!checkpoint.RecentSuccessfulFingerprints.TryGetValue(actor, out var recent))
                    checkpoint.RecentSuccessfulFingerprints[actor] = recent = new List<string>();
                if (recent.Contains(action.Fingerprint, StringComparer.Ordinal))
                    return new(false, false, "This successful tool outcome was already recorded in the recent durable history.");

                recent.Add(action.Fingerprint);
                if (recent.Count > historyLimit) recent.RemoveRange(0, recent.Count - historyLimit);
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                if (string.Equals(actor, "commander", StringComparison.Ordinal))
                    checkpoint.LastSuccessfulAction = copy;
                else
                    checkpoint.AgentLastSuccessfulActions[actor] = copy;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult SetResumeAction(string stewardID, StewardResumeAction action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.ResumeAction = copy;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult ClearResumeAction(string stewardID, string? expectedActionID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                if (checkpoint.ResumeAction == null) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedActionID) && checkpoint.ResumeAction.ActionID != expectedActionID)
                    return new(false, false, "The current resume action does not match expectedActionID.");
                checkpoint.ResumeAction = null;
                return new(true, true);
            }, nowUtc);

        public StewardRuntimeMutationResult SetAgentResumeAction(string stewardID, string agentID, StewardResumeAction action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.AgentResumeActions[agentID.Trim()] = copy;
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult ClearAgentResumeAction(string stewardID, string agentID, string? expectedActionID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(stewardID, expectedRevision, checkpoint =>
            {
                if (!checkpoint.AgentResumeActions.TryGetValue(agentID, out var current)) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedActionID) && current.ActionID != expectedActionID)
                    return new(false, false, "The current agent resume action does not match expectedActionID.");
                checkpoint.AgentResumeActions.Remove(agentID);
                return new(true, true);
            }, nowUtc);

        private StewardRuntimeMutationResult MutateCheckpoint(string stewardID, long? expectedRevision,
            Func<StewardRuntimeCheckpoint, MutationDecision> mutation, DateTime? nowUtc)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                var decision = mutation(state.Checkpoint);
                if (decision.Applied && decision.Changed)
                {
                    state.Checkpoint.Revision = checked(state.Checkpoint.Revision + 1);
                    state.Checkpoint.UpdatedAt = now;
                }
                return decision;
            }, now);
        }

        // ── durable typed trigger inbox ──

        public StewardRuntimeMutationResult EnqueueTrigger(string stewardID, StewardWakeTrigger trigger,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                var copy = Clone(trigger);
                if (string.IsNullOrWhiteSpace(copy.TriggerID)) copy.TriggerID = Guid.NewGuid().ToString("N");
                if (copy.CreatedAt == default) copy.CreatedAt = now;
                if (state.PendingTriggers.Any(x => x.TriggerID == copy.TriggerID)) return new(true, false);

                // Supersede only unclaimed work. A claimed trigger already belongs to a wake and must
                // be acknowledged by that wake rather than silently replaced underneath it.
                if (!string.IsNullOrWhiteSpace(copy.CoalescingKey))
                    state.PendingTriggers.RemoveAll(x => x.ClaimedByWakeID == null &&
                        string.Equals(x.CoalescingKey, copy.CoalescingKey, StringComparison.OrdinalIgnoreCase));

                PurgeStaleLocked(state, now);
                if (state.PendingTriggers.Count >= MaxPendingTriggersPerSteward)
                    return new(false, false, $"Pending trigger cap reached ({MaxPendingTriggersPerSteward}).");
                state.PendingTriggers.Add(copy);
                return new(true, true);
            }, now);
        }

        public List<StewardWakeTrigger> ListPendingTriggers(string stewardID, bool includeClaimed = true)
        {
            return Get(stewardID).PendingTriggers
                .Where(t => includeClaimed || t.ClaimedByWakeID == null)
                .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt)
                .Select(Clone).ToList();
        }

        public StewardWakeTriggerClaimResult TryClaimNextTrigger(string stewardID, string wakeID, long leaseGeneration,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            lock (LockFor(stewardID))
            {
                var state = LoadLocked(stewardID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (!Owns(state.ActiveWakeLease, wakeID, leaseGeneration))
                    return new(false, Clone(state), null, "The supplied wake ID/generation does not own the active lease.");

                bool purged = PurgeStaleLocked(state, now);
                var trigger = state.PendingTriggers
                    .Where(t => t.ClaimedByWakeID == null && EvaluateApplicability(t, state, now) == StewardWakeTriggerApplicability.Applicable)
                    .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).FirstOrDefault();
                if (trigger == null)
                {
                    if (purged) CommitLocked(state, now);
                    return new(false, Clone(state), null, "No applicable unclaimed trigger.");
                }

                trigger.ClaimedByWakeID = wakeID;
                trigger.ClaimedByLeaseGeneration = leaseGeneration;
                trigger.ClaimedAt = now;
                CommitLocked(state, now);
                return new(true, Clone(state), Clone(trigger));
            }
        }

        public StewardRuntimeMutationResult AcknowledgeTrigger(string stewardID, string triggerID, string wakeID,
            long leaseGeneration, bool succeeded, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
            {
                var trigger = state.PendingTriggers.FirstOrDefault(x => x.TriggerID == triggerID);
                if (trigger == null) return new(false, false, "No such pending trigger.");
                if (trigger.ClaimedByWakeID != wakeID || trigger.ClaimedByLeaseGeneration != leaseGeneration)
                    return new(false, false, "The trigger is not claimed by the supplied wake ID/generation.");
                if (succeeded)
                {
                    state.PendingTriggers.Remove(trigger);
                }
                else
                {
                    trigger.ClaimedByWakeID = null;
                    trigger.ClaimedByLeaseGeneration = null;
                    trigger.ClaimedAt = null;
                    trigger.AttemptCount++;
                    trigger.LastAttemptAt = now;
                }
                return new(true, true);
            }, now);
        }

        public StewardRuntimeMutationResult RemoveTrigger(string stewardID, string triggerID,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(stewardID, expectedRevision, state =>
            {
                int removed = state.PendingTriggers.RemoveAll(x => x.TriggerID == triggerID);
                return removed > 0 ? new(true, true) : new(false, false, "No such pending trigger.");
            }, nowUtc);

        public StewardRuntimeMutationResult PurgeStaleTriggers(string stewardID, long? expectedRevision = null,
            DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(stewardID, expectedRevision, state =>
                PurgeStaleLocked(state, now) ? new(true, true) : new(true, false), now);
        }

        public static StewardWakeTriggerApplicability EvaluateApplicability(StewardWakeTrigger trigger,
            StewardRuntimeState state, DateTime utcNow)
        {
            if (trigger.ExpiresAt.HasValue && trigger.ExpiresAt.Value <= utcNow)
                return StewardWakeTriggerApplicability.Stale;
            if (trigger.NotBefore.HasValue && trigger.NotBefore.Value > utcNow)
                return StewardWakeTriggerApplicability.Deferred;
            if (trigger.ExpectedCheckpointRevision.HasValue &&
                trigger.ExpectedCheckpointRevision.Value != state.Checkpoint.Revision)
                return StewardWakeTriggerApplicability.Stale;
            if (trigger.ExpectedGrandPlanVersion.HasValue &&
                trigger.ExpectedGrandPlanVersion.Value != state.Checkpoint.GrandPlanVersion)
                return StewardWakeTriggerApplicability.Stale;

            bool dispositionMatches = trigger.AllowedDispositions.Count == 0 || trigger.AllowedDispositions.Contains(state.Disposition);
            bool milestonesMatch = trigger.RequiredActiveMilestoneIDs.Count == 0 ||
                trigger.RequiredActiveMilestoneIDs.All(required =>
                    state.Checkpoint.ActiveMilestoneIDs.Contains(required, StringComparer.OrdinalIgnoreCase));
            if (dispositionMatches && milestonesMatch) return StewardWakeTriggerApplicability.Applicable;
            return trigger.DiscardWhenInapplicable
                ? StewardWakeTriggerApplicability.Stale
                : StewardWakeTriggerApplicability.Deferred;
        }

        // ── persistence internals ──

        private StewardRuntimeMutationResult Mutate(string stewardID, long? expectedRevision,
            Func<StewardRuntimeState, MutationDecision> mutation, DateTime? nowUtc)
        {
            DateTime now = Utc(nowUtc);
            lock (LockFor(stewardID))
            {
                var state = LoadLocked(stewardID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                var decision = mutation(state);
                if (decision.Applied && decision.Changed) CommitLocked(state, now);
                return new(decision.Applied, Clone(state), decision.Reason);
            }
        }

        private void CommitLocked(StewardRuntimeState state, DateTime now)
        {
            state.Revision = checked(state.Revision + 1);
            state.UpdatedAt = now;
            SaveLocked(state);
            SummaryCache[GetStatePath(state.StewardID)] = Summarize(state);
            // Single commit chokepoint: every mutator routes through Mutate → CommitLocked, so one
            // bump here covers all of them and no future mutator can forget to invalidate.
            CacheDeps.Bump(CacheKey(state.StewardID));
        }

        /// <summary>
        /// Set by Demiurge once the runners exist: true while THIS process has a live wake for the
        /// Steward. Consulted only on the corrupt-state repair path, where rebuilding a state would
        /// drop an active lease. Null (unset) is treated as "unknown", which blocks the rebuild.
        /// </summary>
        public Func<string, bool>? HasLiveWake { get; set; }

        private StewardRuntimeState LoadLocked(string stewardID)
        {
            string path = GetStatePath(stewardID);
            if (!File.Exists(path)) return NewState(stewardID);
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                // An I/O error is transient (sharing violation, disk hiccup) — it is NOT evidence the
                // document is bad, so never quarantine on it. Fail closed and let the caller retry.
                log($"StewardRuntimeStateStore: failed to read {path}: {ex.Message}");
                throw new InvalidDataException($"Steward runtime state for {stewardID} is unreadable; execution is stopped to preserve single-flight safety.", ex);
            }

            try
            {
                var state = JsonConvert.DeserializeObject<StewardRuntimeState>(text);
                if (state == null) throw new InvalidDataException("Runtime-state document was empty.");
                Normalize(state, stewardID);
                return state;
            }
            catch (Exception strictFailure)
            {
                log($"StewardRuntimeStateStore: failed to load {path}: {strictFailure.Message}");
                return RepairLocked(stewardID, path, text, strictFailure);
            }
        }

        /// <summary>
        /// Recovers a runtime state that would not deserialize, instead of bricking the Steward.
        ///
        /// Fail-closed was the right instinct — replacing a corrupt ACTIVE LEASE with an empty state
        /// could start a second wake — but throwing on every read made the Steward permanently
        /// unrunnable with no path back: the wake path, the keepalive and the watchdog all call Get,
        /// and nothing ever rewrites the file because every writer must read it first. So the Steward
        /// stops forever and the only visible symptom is that it quietly never runs again.
        ///
        /// Two tiers, most conservative first:
        ///   1. SALVAGE — re-parse tolerantly, skipping only the members that would not bind. This is
        ///      the common case (a schema/type change, one bad enum or date) and it keeps the
        ///      checkpoint, pending triggers and lease intact.
        ///   2. QUARANTINE — the document is not valid JSON at all (truncated, empty, half-written).
        ///      Nothing is recoverable from it, so move it aside for inspection and start clean.
        ///
        /// Single-flight is still honoured: tier 2 runs only when this process holds no live wake for
        /// the Steward. It cannot, in practice — every wake start goes through Mutate → LoadLocked,
        /// which was already throwing — but the check is cheap and makes the guarantee explicit rather
        /// than inferred. If a wake IS live, the original fail-closed throw stands.
        /// </summary>
        private StewardRuntimeState RepairLocked(string stewardID, string path, string text, Exception strictFailure)
        {
            // Tier 1 is only legitimate when the DOCUMENT is intact and merely a MEMBER would not
            // bind. Tolerant deserialization alone does not draw that line — handing it a truncated
            // file returns a cheerfully half-populated object, which is the one outcome fail-closed
            // exists to prevent: a state silently missing its ActiveWakeLease reads as "no wake
            // running" and lets a second wake start. So parse structurally first, and only salvage a
            // document that is genuinely well-formed JSON.
            Newtonsoft.Json.Linq.JObject? document = null;
            try { document = Newtonsoft.Json.Linq.JObject.Parse(text); }
            catch { /* structurally broken — not salvageable, fall through to quarantine */ }

            if (document != null)
            {
                try
                {
                    var salvaged = JsonConvert.DeserializeObject<StewardRuntimeState>(text, new JsonSerializerSettings
                    {
                        Error = (_, args) => args.ErrorContext.Handled = true,
                    });
                    if (salvaged != null)
                    {
                        Normalize(salvaged, stewardID);
                        // If the lease was present in the file but is not in the salvaged object, the
                        // one member we cannot afford to lose is exactly the one that was dropped.
                        bool leaseInFile = document["ActiveWakeLease"] is { } lease
                            && lease.Type != Newtonsoft.Json.Linq.JTokenType.Null;
                        if (!(leaseInFile && salvaged.ActiveWakeLease == null))
                        {
                            log($"StewardRuntimeStateStore: salvaged {stewardID} runtime state by skipping unreadable "
                                + $"members ({strictFailure.Message}). Checkpoint revision {salvaged.Checkpoint.Revision} preserved.");
                            // Rewrite immediately so the next reader takes the fast path and the damage
                            // is not re-salvaged on every single call.
                            SaveLocked(salvaged);
                            return salvaged;
                        }
                        log($"StewardRuntimeStateStore: refusing to salvage {stewardID} — the active wake lease is the "
                            + "member that failed to bind.");
                    }
                }
                catch (Exception salvageFailure)
                {
                    log($"StewardRuntimeStateStore: salvage of {stewardID} failed: {salvageFailure.Message}");
                }
            }

            bool liveWake;
            try { liveWake = HasLiveWake?.Invoke(stewardID) ?? true; }
            catch { liveWake = true; }
            if (liveWake)
            {
                throw new InvalidDataException(
                    $"Steward runtime state for {stewardID} is unreadable and a wake is currently live; execution is "
                    + "stopped to preserve single-flight safety.", strictFailure);
            }

            string quarantine = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            try { File.Move(path, quarantine, overwrite: true); }
            catch (Exception moveFailure)
            {
                log($"StewardRuntimeStateStore: could not quarantine {path}: {moveFailure.Message}");
                throw new InvalidDataException(
                    $"Steward runtime state for {stewardID} is unreadable and could not be quarantined; execution is "
                    + "stopped to preserve single-flight safety.", strictFailure);
            }

            var fresh = NewState(stewardID);
            SaveLocked(fresh);
            log($"StewardRuntimeStateStore: {stewardID} runtime state was unrecoverable ({strictFailure.Message}). "
                + $"Quarantined to {Path.GetFileName(quarantine)} and rebuilt empty — the Steward can run again, but its "
                + "checkpoint is gone and the Commander will rehydrate from the durable event log.");
            return fresh;
        }

        private void SaveLocked(StewardRuntimeState state)
        {
            Normalize(state, state.StewardID);
            string path = GetStatePath(state.StewardID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Checkpoints can include legacy/model text containing lone UTF-16 surrogates.
                // Repair only those invalid code units; strict UTF-8 must not abort a completed tool.
                byte[] payload = new System.Text.UTF8Encoding(false).GetBytes(Data_Handling.UnicodeText.RepairInvalidSurrogates(
                    JsonConvert.SerializeObject(state, Formatting.Indented)));

                // Flush to the DISK, not just the page cache, before the rename commits.
                // File.WriteAllText + File.Move is only atomic in its metadata: the rename can reach
                // the journal while the temp file's data blocks have not, so a hard kill or power
                // loss leaves the live path pointing at a truncated or all-NUL document. That is a
                // runtime state that will never deserialize again — and until the repair path above
                // existed, a Steward that silently never ran again.
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough))
                {
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush(flushToDisk: true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, path, overwrite: true); break; }
                    catch (Exception ex) when (attempt < 5 && (ex is IOException || ex is UnauthorizedAccessException))
                    {
                        Thread.Sleep(15 * (attempt + 1));
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static StewardRuntimeState NewState(string stewardID) => new()
        {
            StewardID = stewardID,
            UpdatedAt = DateTime.UtcNow,
            Checkpoint = new StewardRuntimeCheckpoint { UpdatedAt = DateTime.UtcNow },
        };

        private static void Normalize(StewardRuntimeState state, string stewardID)
        {
            state.StewardID = stewardID;
            state.Health ??= new StewardExecutionHealth();
            state.Health.Circuit ??= new StewardCircuitBreakerState();
            state.Health.Dependencies = state.Health.Dependencies == null
                ? new Dictionary<string, StewardDependencyHealth>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, StewardDependencyHealth>(state.Health.Dependencies, StringComparer.OrdinalIgnoreCase);
            state.Health.Watchdog ??= new StewardWatchdogHealth();
            state.Health.Watchdog.RecoveriesUtc ??= new();
            state.Health.Watchdog.RemindedGateIDs ??= new();
            state.Checkpoint ??= new StewardRuntimeCheckpoint();
            state.Checkpoint.ActiveMilestoneIDs ??= new();
            state.Checkpoint.VerifiedFacts ??= new();
            state.Checkpoint.CanonicalArtifacts ??= new();
            state.Checkpoint.AgentResumeActions ??= new(StringComparer.Ordinal);
            state.Checkpoint.AgentLastSuccessfulActions ??= new(StringComparer.Ordinal);
            state.Checkpoint.RecentSuccessfulFingerprints = state.Checkpoint.RecentSuccessfulFingerprints == null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : new Dictionary<string, List<string>>(state.Checkpoint.RecentSuccessfulFingerprints, StringComparer.Ordinal);
            foreach (string actor in state.Checkpoint.RecentSuccessfulFingerprints.Keys.ToList())
            {
                var recent = state.Checkpoint.RecentSuccessfulFingerprints[actor] ?? new();
                state.Checkpoint.RecentSuccessfulFingerprints[actor] = recent
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).TakeLast(128).ToList();
            }
            state.ActiveAgentWakeLeases ??= new(StringComparer.Ordinal);
            state.PendingTriggers ??= new();
            foreach (var f in state.Checkpoint.VerifiedFacts)
            {
                f.Evidence ??= new();
                f.InvalidationKeys ??= new();
            }
            foreach (var a in state.Checkpoint.CanonicalArtifacts) a.Evidence ??= new();
            foreach (var t in state.PendingTriggers)
            {
                t.AllowedDispositions ??= new();
                t.RequiredActiveMilestoneIDs ??= new();
            }
            if (state.ActiveWakeLease != null)
                state.LastWakeLeaseGeneration = Math.Max(state.LastWakeLeaseGeneration, state.ActiveWakeLease.Generation);
            if (state.ActiveAgentWakeLeases.Count > 0)
                state.LastWakeLeaseGeneration = Math.Max(state.LastWakeLeaseGeneration,
                    state.ActiveAgentWakeLeases.Values.Max(x => x.Generation));
            RecomputeHealthStatus(state);
        }

        private static void RecomputeHealthStatus(StewardRuntimeState state)
        {
            state.Health.Status = state.Health.Circuit.Status == StewardCircuitStatus.Open
                ? StewardExecutionHealthStatus.CircuitOpen
                : state.Health.Circuit.Status == StewardCircuitStatus.HalfOpen
                  || state.Health.ConsecutiveFailures > 0
                  || state.Health.Dependencies.Values.Any(x => !x.Healthy)
                    ? StewardExecutionHealthStatus.Degraded
                    : StewardExecutionHealthStatus.Healthy;
        }

        private static bool PurgeStaleLocked(StewardRuntimeState state, DateTime now)
        {
            int removed = state.PendingTriggers.RemoveAll(t => t.ClaimedByWakeID == null &&
                EvaluateApplicability(t, state, now) == StewardWakeTriggerApplicability.Stale);
            return removed > 0;
        }

        private static bool Owns(StewardWakeLease? lease, string wakeID, long generation) =>
            lease != null && lease.Generation == generation && string.Equals(lease.WakeID, wakeID, StringComparison.Ordinal);

        private static DateTime Utc(DateTime? value)
        {
            DateTime dt = value ?? DateTime.UtcNow;
            return dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            };
        }

        private static DateTime Utc(DateTime value) => Utc((DateTime?)value);

        private static T Clone<T>(T value) =>
            JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;

        /// <summary>Whitespace-collapsed and length-capped, for text destined for a wake seed.</summary>
        private static string Clip(string? value, int max)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
            return text.Length <= max ? text : Data_Handling.UnicodeText.Prefix(text, max) + "…";
        }

        private static void ValidateStewardID(string stewardID)
        {
            if (string.IsNullOrWhiteSpace(stewardID)) throw new ArgumentException("stewardID required", nameof(stewardID));
            if (stewardID.Length > 200 || stewardID is "." or ".." ||
                stewardID.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stewardID.Contains(Path.DirectorySeparatorChar) || stewardID.Contains(Path.AltDirectorySeparatorChar))
                throw new ArgumentException("stewardID contains invalid path characters", nameof(stewardID));
        }
    }
}
