using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Whether the project execution harness may currently run work. This is deliberately separate
    /// from the project's strategic phase/milestones and from the legacy <see cref="ProjectStatus"/>.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectExecutionDisposition
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
    public enum ProjectWakeLeaseStatus { Starting, Running, CancellationRequested }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectExecutionHealthStatus { Healthy, Degraded, CircuitOpen }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectCircuitStatus { Closed, Open, HalfOpen }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectFailureCategory
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
    public enum ProjectBlockerCategory
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

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectEvidenceKind
    {
        Event,
        Artifact,
        ProjectFile,
        ToolResult,
        ExternalObservation,
        UserConfirmation,
        Other,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectArtifactValidationStatus { Unknown, Pending, Valid, Invalid }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectWakeTriggerKind
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
    public enum ProjectWakeTriggerApplicability { Applicable, Deferred, Stale }

    /// <summary>
    /// A persisted single-flight fencing lease. WakeID plus Generation is the ownership token;
    /// an old wake cannot heartbeat, cancel or release a newer wake's lease.
    /// </summary>
    public sealed class ProjectWakeLease
    {
        public string WakeID { get; set; } = "";
        public long Generation { get; set; }
        public ProjectWakeLeaseStatus Status { get; set; } = ProjectWakeLeaseStatus.Starting;
        public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
        public DateTime? CancellationRequestedAt { get; set; }
        public string? CancellationReason { get; set; }
    }

    public sealed class ProjectEvidenceReference
    {
        public ProjectEvidenceKind Kind { get; set; } = ProjectEvidenceKind.Other;
        /// <summary>An event ID, artifact ID, project path, tool-call ID, URL, or other stable reference.</summary>
        public string Reference { get; set; } = "";
        public long? EventSequence { get; set; }
        public string? ContentHash { get; set; }
        public string? Description { get; set; }
        public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class ProjectExecutionFailure
    {
        public ProjectFailureCategory Category { get; set; } = ProjectFailureCategory.Unknown;
        public string Code { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool Retryable { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
        public DateTime? RetryAt { get; set; }
        public string? WakeID { get; set; }
        public List<ProjectEvidenceReference> Evidence { get; set; } = new();
    }

    public sealed class ProjectCircuitBreakerState
    {
        public ProjectCircuitStatus Status { get; set; } = ProjectCircuitStatus.Closed;
        public int FailureCount { get; set; }
        public string? ReasonCode { get; set; }
        public string? Summary { get; set; }
        public DateTime? OpenedAt { get; set; }
        public DateTime? RetryAt { get; set; }
    }

    public sealed class ProjectExecutionHealth
    {
        public ProjectExecutionHealthStatus Status { get; set; } = ProjectExecutionHealthStatus.Healthy;
        public int ConsecutiveFailures { get; set; }
        public ProjectExecutionFailure? LastFailure { get; set; }
        public DateTime? LastSuccessAt { get; set; }
        public DateTime? LastVerifiedProgressAt { get; set; }
        public long? LastVerifiedProgressSequence { get; set; }
        public ProjectCircuitBreakerState Circuit { get; set; } = new();
    }

    public sealed class ProjectRuntimeBlocker
    {
        public string BlockerID { get; set; } = "";
        public ProjectBlockerCategory Category { get; set; } = ProjectBlockerCategory.Unknown;
        public string Code { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? Detail { get; set; }
        public bool Retryable { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? NextRetryAt { get; set; }
        public List<ProjectEvidenceReference> Evidence { get; set; } = new();
    }

    /// <summary>A durable fact that is explicitly tied to evidence and can age out or be invalidated.</summary>
    public sealed class ProjectVerifiedFact
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
        public List<ProjectEvidenceReference> Evidence { get; set; } = new();

        public bool IsFreshAt(DateTime utcNow) =>
            InvalidatedAt == null && (!ValidUntil.HasValue || ValidUntil.Value > utcNow);
    }

    /// <summary>A project output/input designated as canonical, with identity and validation evidence.</summary>
    public sealed class ProjectCanonicalArtifact
    {
        public string CanonicalArtifactID { get; set; } = "";
        /// <summary>Stable logical role, e.g. "canonical-build", "source-dataset", or "final-report".</summary>
        public string Role { get; set; } = "";
        public string? ProjectPath { get; set; }
        public string? ArtifactID { get; set; }
        public string? ContentHash { get; set; }
        public ProjectArtifactValidationStatus ValidationStatus { get; set; } = ProjectArtifactValidationStatus.Unknown;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ValidatedAt { get; set; }
        public List<ProjectEvidenceReference> Evidence { get; set; } = new();
    }

    public class ProjectActionCheckpoint
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
        public List<ProjectEvidenceReference> Evidence { get; set; } = new();
    }

    public sealed class ProjectResumeAction : ProjectActionCheckpoint
    {
        public DateTime? NotBefore { get; set; }
        public List<string> Preconditions { get; set; } = new();
    }

    public sealed class ProjectRuntimeCheckpoint
    {
        public long Revision { get; set; }
        public int? GrandPlanVersion { get; set; }
        public List<string> ActiveMilestoneIDs { get; set; } = new();
        public List<ProjectVerifiedFact> VerifiedFacts { get; set; } = new();
        public List<ProjectCanonicalArtifact> CanonicalArtifacts { get; set; } = new();
        public ProjectActionCheckpoint? LastSuccessfulAction { get; set; }
        public Dictionary<string, ProjectActionCheckpoint> AgentLastSuccessfulActions { get; set; } = new(StringComparer.Ordinal);
        public ProjectResumeAction? ResumeAction { get; set; }
        public Dictionary<string, ProjectResumeAction> AgentResumeActions { get; set; } = new(StringComparer.Ordinal);
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A typed wake request. Applicability is re-evaluated when it is claimed, preventing an old
    /// phase/status-specific nudge from being delivered after project state has changed.
    /// </summary>
    public sealed class ProjectWakeTrigger
    {
        public string TriggerID { get; set; } = "";
        public ProjectWakeTriggerKind Kind { get; set; } = ProjectWakeTriggerKind.Other;
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
        public List<ProjectExecutionDisposition> AllowedDispositions { get; set; } = new();
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
    public sealed class ProjectRuntimeState
    {
        public string ProjectID { get; set; } = "";
        public long Revision { get; set; }
        public ProjectExecutionDisposition Disposition { get; set; } = ProjectExecutionDisposition.Running;
        public ProjectWakeLease? ActiveWakeLease { get; set; }
        /// <summary>Durable per-agent execution leases. Commander ownership remains in
        /// ActiveWakeLease; workers are independently fenced so they may run concurrently.</summary>
        public Dictionary<string, ProjectWakeLease> ActiveAgentWakeLeases { get; set; } = new(StringComparer.Ordinal);
        public long LastWakeLeaseGeneration { get; set; }
        public ProjectExecutionHealth Health { get; set; } = new();
        public ProjectRuntimeBlocker? Blocker { get; set; }
        public ProjectRuntimeCheckpoint Checkpoint { get; set; } = new();
        public List<ProjectWakeTrigger> PendingTriggers { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed record ProjectRuntimeMutationResult(bool Applied, ProjectRuntimeState State, string? Reason = null);
    public sealed record ProjectWakeLeaseAcquireResult(bool Acquired, ProjectRuntimeState State, ProjectWakeLease? Lease, string? Reason = null);
    public sealed record ProjectWakeTriggerClaimResult(bool Claimed, ProjectRuntimeState State, ProjectWakeTrigger? Trigger, string? Reason = null);

    /// <summary>
    /// Atomic per-project storage and CAS-style mutation API for runtime coordination. Missing files
    /// intentionally read as an empty/default state so existing projects need no migration.
    ///
    /// Layout: Projects/RuntimeState/&lt;projectID&gt;.runtime.json
    /// </summary>
    public sealed class ProjectRuntimeStateStore
    {
        public const int MaxPendingTriggersPerProject = 512;

        private readonly string root;
        private readonly Action<string> log;
        // Static so two store instances in the same process cannot race the same project file.
        private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);

        private sealed record MutationDecision(bool Applied, bool Changed, string? Reason = null);

        public ProjectRuntimeStateStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "RuntimeState")
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        public string GetStatePath(string projectID)
        {
            ValidateProjectID(projectID);
            return Path.Combine(root, projectID + ".runtime.json");
        }

        private object LockFor(string projectID) => FileLocks.GetOrAdd(GetStatePath(projectID), _ => new object());

        public ProjectRuntimeState Get(string projectID)
        {
            lock (LockFor(projectID)) return Clone(LoadLocked(projectID));
        }

        public List<ProjectRuntimeState> ListWithActiveWakeLeases()
        {
            var states = new List<ProjectRuntimeState>();
            if (!Directory.Exists(root)) return states;
            foreach (string path in Directory.EnumerateFiles(root, "*.runtime.json"))
            {
                string projectID = Path.GetFileName(path)[..^".runtime.json".Length];
                try
                {
                    lock (LockFor(projectID))
                    {
                        var state = LoadLocked(projectID);
                        if (state.ActiveWakeLease != null || state.ActiveAgentWakeLeases.Count > 0) states.Add(Clone(state));
                    }
                }
                catch (Exception ex) { log($"Runtime-state scan skipped {projectID}: {ex.Message}"); }
            }
            return states;
        }

        // ── execution disposition + single-flight lease ──

        public ProjectRuntimeMutationResult SetDisposition(string projectID, ProjectExecutionDisposition disposition,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            return Mutate(projectID, expectedRevision, state =>
            {
                if (state.Disposition == disposition) return new(true, false);
                state.Disposition = disposition;
                return new(true, true);
            }, nowUtc);
        }

        public ProjectWakeLeaseAcquireResult TryAcquireWakeLease(string projectID, string wakeID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            lock (LockFor(projectID))
            {
                var state = LoadLocked(projectID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (state.ActiveWakeLease != null)
                    return new(false, Clone(state), Clone(state.ActiveWakeLease),
                        $"Wake {state.ActiveWakeLease.WakeID} generation {state.ActiveWakeLease.Generation} already owns the lease.");
                if (state.Disposition is not (ProjectExecutionDisposition.Running or ProjectExecutionDisposition.Waiting))
                    return new(false, Clone(state), null, $"Execution disposition is {state.Disposition}.");

                long generation = checked(state.LastWakeLeaseGeneration + 1);
                var lease = new ProjectWakeLease
                {
                    WakeID = wakeID.Trim(),
                    Generation = generation,
                    Status = ProjectWakeLeaseStatus.Starting,
                    AcquiredAt = now,
                    LastHeartbeatAt = now,
                };
                state.LastWakeLeaseGeneration = generation;
                state.ActiveWakeLease = lease;
                CommitLocked(state, now);
                return new(true, Clone(state), Clone(lease));
            }
        }

        public ProjectRuntimeMutationResult MarkWakeRunning(string projectID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(projectID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == ProjectWakeLeaseStatus.CancellationRequested)
                    return new(false, false, "Cancellation has already been requested.");
                if (lease.Status == ProjectWakeLeaseStatus.Running) return new(true, false);
                lease.Status = ProjectWakeLeaseStatus.Running;
                lease.LastHeartbeatAt = Utc(nowUtc);
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult HeartbeatWakeLease(string projectID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(projectID, wakeID, generation, expectedRevision, lease =>
            {
                DateTime now = Utc(nowUtc);
                if (now <= lease.LastHeartbeatAt) return new(true, false);
                lease.LastHeartbeatAt = now;
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult RequestWakeCancellation(string projectID, string wakeID, long generation,
            string reason, long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedLease(projectID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == ProjectWakeLeaseStatus.CancellationRequested) return new(true, false);
                lease.Status = ProjectWakeLeaseStatus.CancellationRequested;
                lease.CancellationRequestedAt = Utc(nowUtc);
                lease.CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason.Trim();
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult ReleaseWakeLease(string projectID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(projectID, expectedRevision, state =>
            {
                if (!Owns(state.ActiveWakeLease, wakeID, generation))
                    return new(false, false, "The supplied wake ID/generation does not own the active lease.");
                state.ActiveWakeLease = null;
                return new(true, true);
            }, nowUtc);

        public ProjectWakeLeaseAcquireResult TryAcquireAgentWakeLease(string projectID, string agentID, string wakeID,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            agentID = agentID.Trim();
            lock (LockFor(projectID))
            {
                var state = LoadLocked(projectID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (state.ActiveAgentWakeLeases.TryGetValue(agentID, out var existing))
                    return new(false, Clone(state), Clone(existing),
                        $"Agent {agentID} wake {existing.WakeID} generation {existing.Generation} already owns the lease.");
                if (state.Disposition is not (ProjectExecutionDisposition.Running or ProjectExecutionDisposition.Waiting))
                    return new(false, Clone(state), null, $"Execution disposition is {state.Disposition}.");

                long generation = checked(state.LastWakeLeaseGeneration + 1);
                var lease = new ProjectWakeLease
                {
                    WakeID = wakeID.Trim(), Generation = generation,
                    Status = ProjectWakeLeaseStatus.Starting,
                    AcquiredAt = now, LastHeartbeatAt = now,
                };
                state.LastWakeLeaseGeneration = generation;
                state.ActiveAgentWakeLeases[agentID] = lease;
                CommitLocked(state, now);
                return new(true, Clone(state), Clone(lease));
            }
        }

        public ProjectRuntimeMutationResult MarkAgentWakeRunning(string projectID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(projectID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                if (lease.Status == ProjectWakeLeaseStatus.CancellationRequested)
                    return new(false, false, "Cancellation has already been requested.");
                lease.Status = ProjectWakeLeaseStatus.Running;
                lease.LastHeartbeatAt = Utc(nowUtc);
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult HeartbeatAgentWakeLease(string projectID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(projectID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                DateTime now = Utc(nowUtc);
                if (now <= lease.LastHeartbeatAt) return new(true, false);
                lease.LastHeartbeatAt = now;
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult RequestAgentWakeCancellation(string projectID, string agentID, string wakeID, long generation,
            string reason, long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateOwnedAgentLease(projectID, agentID, wakeID, generation, expectedRevision, lease =>
            {
                lease.Status = ProjectWakeLeaseStatus.CancellationRequested;
                lease.CancellationRequestedAt ??= Utc(nowUtc);
                lease.CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason.Trim();
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult ReleaseAgentWakeLease(string projectID, string agentID, string wakeID, long generation,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(projectID, expectedRevision, state =>
            {
                if (!state.ActiveAgentWakeLeases.TryGetValue(agentID, out var lease) || !Owns(lease, wakeID, generation))
                    return new(false, false, "The supplied agent/wake ID/generation does not own the active lease.");
                state.ActiveAgentWakeLeases.Remove(agentID);
                return new(true, true);
            }, nowUtc);

        private ProjectRuntimeMutationResult MutateOwnedAgentLease(string projectID, string agentID, string wakeID, long generation,
            long? expectedRevision, Func<ProjectWakeLease, MutationDecision> mutation, DateTime? nowUtc) =>
            Mutate(projectID, expectedRevision, state =>
            {
                if (!state.ActiveAgentWakeLeases.TryGetValue(agentID, out var lease) || !Owns(lease, wakeID, generation))
                    return new(false, false, "The supplied agent/wake ID/generation does not own the active lease.");
                return mutation(lease);
            }, nowUtc);

        private ProjectRuntimeMutationResult MutateOwnedLease(string projectID, string wakeID, long generation,
            long? expectedRevision, Func<ProjectWakeLease, MutationDecision> mutation, DateTime? nowUtc)
        {
            return Mutate(projectID, expectedRevision, state =>
            {
                if (!Owns(state.ActiveWakeLease, wakeID, generation))
                    return new(false, false, "The supplied wake ID/generation does not own the active lease.");
                return mutation(state.ActiveWakeLease!);
            }, nowUtc);
        }

        // ── health, circuit and blocker ──

        public ProjectRuntimeMutationResult RecordExecutionFailure(string projectID, ProjectExecutionFailure failure,
            bool openCircuit = false, DateTime? circuitRetryAt = null, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(failure);
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
            {
                failure = Clone(failure);
                failure.OccurredAt = failure.OccurredAt == default ? now : Utc(failure.OccurredAt);
                state.Health.ConsecutiveFailures++;
                state.Health.LastFailure = failure;
                state.Health.Status = openCircuit ? ProjectExecutionHealthStatus.CircuitOpen : ProjectExecutionHealthStatus.Degraded;
                state.Health.Circuit.FailureCount = state.Health.ConsecutiveFailures;
                if (openCircuit)
                {
                    state.Health.Circuit.Status = ProjectCircuitStatus.Open;
                    state.Health.Circuit.OpenedAt = now;
                    state.Health.Circuit.RetryAt = circuitRetryAt ?? failure.RetryAt;
                    state.Health.Circuit.ReasonCode = failure.Code;
                    state.Health.Circuit.Summary = failure.Summary;
                }
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult RecordExecutionSuccess(string projectID, long? verifiedProgressSequence = null,
            bool closeCircuit = true, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
            {
                state.Health.Status = ProjectExecutionHealthStatus.Healthy;
                state.Health.ConsecutiveFailures = 0;
                state.Health.LastSuccessAt = now;
                if (verifiedProgressSequence.HasValue)
                {
                    state.Health.LastVerifiedProgressAt = now;
                    state.Health.LastVerifiedProgressSequence = verifiedProgressSequence;
                }
                if (closeCircuit) state.Health.Circuit = new ProjectCircuitBreakerState();
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult OpenCircuit(string projectID, string reasonCode, string summary,
            DateTime? retryAt = null, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
            {
                state.Health.Status = ProjectExecutionHealthStatus.CircuitOpen;
                state.Health.Circuit.Status = ProjectCircuitStatus.Open;
                state.Health.Circuit.OpenedAt = now;
                state.Health.Circuit.RetryAt = retryAt;
                state.Health.Circuit.ReasonCode = reasonCode;
                state.Health.Circuit.Summary = summary;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult CloseCircuit(string projectID, bool halfOpen = false,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(projectID, expectedRevision, state =>
            {
                state.Health.Circuit.Status = halfOpen ? ProjectCircuitStatus.HalfOpen : ProjectCircuitStatus.Closed;
                state.Health.Circuit.RetryAt = null;
                state.Health.Status = halfOpen ? ProjectExecutionHealthStatus.Degraded : ProjectExecutionHealthStatus.Healthy;
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult SetBlocker(string projectID, ProjectRuntimeBlocker blocker,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(blocker);
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
            {
                var b = Clone(blocker);
                if (string.IsNullOrWhiteSpace(b.BlockerID)) b.BlockerID = Guid.NewGuid().ToString("N");
                b.CreatedAt = b.CreatedAt == default ? now : Utc(b.CreatedAt);
                b.UpdatedAt = now;
                state.Blocker = b;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult ClearBlocker(string projectID, string? expectedBlockerID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(projectID, expectedRevision, state =>
            {
                if (state.Blocker == null) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedBlockerID) && state.Blocker.BlockerID != expectedBlockerID)
                    return new(false, false, "The active blocker does not match expectedBlockerID.");
                state.Blocker = null;
                return new(true, true);
            }, nowUtc);

        // ── typed checkpoint ──

        public ProjectRuntimeMutationResult SetActiveMilestones(string projectID, int? grandPlanVersion,
            IEnumerable<string> milestoneIDs, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            var ids = (milestoneIDs ?? Array.Empty<string>()).Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                checkpoint.GrandPlanVersion = grandPlanVersion;
                checkpoint.ActiveMilestoneIDs = ids;
                return new(true, true);
            }, nowUtc);
        }

        public ProjectRuntimeMutationResult UpsertVerifiedFact(string projectID, ProjectVerifiedFact fact,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (string.IsNullOrWhiteSpace(fact.Key)) throw new ArgumentException("Verified fact key required.", nameof(fact));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
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

        public ProjectRuntimeMutationResult InvalidateVerifiedFact(string projectID, string key, string reason,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                var fact = checkpoint.VerifiedFacts.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (fact == null) return new(false, false, $"No verified fact named '{key}'.");
                fact.InvalidatedAt = now;
                fact.InvalidationReason = reason;
                return new(true, true);
            }, now);
        }

        public List<ProjectVerifiedFact> GetFreshVerifiedFacts(string projectID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Get(projectID).Checkpoint.VerifiedFacts.Where(f => f.IsFreshAt(now)).Select(Clone).ToList();
        }

        /// <summary>Compact machine-owned handoff block. Unlike the narrative digest, these facts,
        /// blockers, artifact identities and the exact resume action are never rewritten by an LLM.</summary>
        public string DescribeForWake(string projectID, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            var state = Get(projectID);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"runtime revision: {state.Revision}; disposition: {state.Disposition}; health: {state.Health.Status}");
            if (state.Health.Circuit.Status != ProjectCircuitStatus.Closed)
                sb.AppendLine($"provider circuit: {state.Health.Circuit.Status}; reason={state.Health.Circuit.ReasonCode ?? "unknown"}; retryAt={state.Health.Circuit.RetryAt?.ToString("O") ?? "manual"}");
            if (state.Blocker != null)
                sb.AppendLine($"blocker [{state.Blocker.Category}/{state.Blocker.Code}]: {state.Blocker.Summary}" +
                    (state.Blocker.NextRetryAt.HasValue ? $"; nextRetry={state.Blocker.NextRetryAt:O}" : ""));
            if (state.Checkpoint.GrandPlanVersion.HasValue || state.Checkpoint.ActiveMilestoneIDs.Count > 0)
                sb.AppendLine($"checkpoint: plan v{state.Checkpoint.GrandPlanVersion?.ToString() ?? "?"}; active milestones: {(state.Checkpoint.ActiveMilestoneIDs.Count == 0 ? "none" : string.Join(", ", state.Checkpoint.ActiveMilestoneIDs))}");
            if (state.Checkpoint.LastSuccessfulAction != null)
                sb.AppendLine($"last verified action: {state.Checkpoint.LastSuccessfulAction.Summary} ({state.Checkpoint.LastSuccessfulAction.RecordedAt:O})");
            if (state.Checkpoint.ResumeAction != null)
                sb.AppendLine($"EXACT RESUME ACTION: {state.Checkpoint.ResumeAction.Summary}" +
                    (state.Checkpoint.ResumeAction.NotBefore.HasValue ? $"; notBefore={state.Checkpoint.ResumeAction.NotBefore:O}" : ""));
            if (state.ActiveAgentWakeLeases.Count > 0)
                sb.AppendLine("active worker leases: " + string.Join(", ", state.ActiveAgentWakeLeases.Select(x =>
                    $"{x.Key}/{x.Value.WakeID[..Math.Min(8, x.Value.WakeID.Length)]} {x.Value.Status} heartbeat={x.Value.LastHeartbeatAt:O}")));
            if (state.Checkpoint.AgentResumeActions.Count > 0)
            {
                sb.AppendLine("worker resume actions:");
                foreach (var entry in state.Checkpoint.AgentResumeActions.Take(24))
                    sb.AppendLine($"- {entry.Key}: {entry.Value.Summary}");
            }

            var facts = state.Checkpoint.VerifiedFacts.Where(f => f.IsFreshAt(now)).Take(32).ToList();
            if (facts.Count > 0)
            {
                sb.AppendLine("verified facts (fresh):");
                foreach (var fact in facts)
                    sb.AppendLine($"- {fact.Key} = {fact.Value}; verified={fact.VerifiedAt:O}" +
                        (fact.ValidUntil.HasValue ? $"; validUntil={fact.ValidUntil:O}" : "") +
                        (fact.Evidence.Count > 0 ? $"; evidence={string.Join(",", fact.Evidence.Take(3).Select(e => e.Reference))}" : ""));
            }

            if (state.Checkpoint.CanonicalArtifacts.Count > 0)
            {
                sb.AppendLine("canonical artifacts:");
                foreach (var artifact in state.Checkpoint.CanonicalArtifacts.Take(24))
                    sb.AppendLine($"- {artifact.Role}: {artifact.ProjectPath ?? artifact.ArtifactID ?? "unresolved"}; validation={artifact.ValidationStatus}" +
                        $"; updated={Data_Handling.TemporalFormat.StampWithAge(artifact.UpdatedAt == default ? artifact.RegisteredAt : artifact.UpdatedAt)}" +
                        (string.IsNullOrWhiteSpace(artifact.ContentHash) ? "" : $"; hash={artifact.ContentHash}"));
            }
            return sb.ToString().TrimEnd();
        }

        public ProjectRuntimeMutationResult UpsertCanonicalArtifact(string projectID, ProjectCanonicalArtifact artifact,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.Role)) throw new ArgumentException("Canonical artifact role required.", nameof(artifact));
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
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

        public ProjectRuntimeMutationResult RemoveCanonicalArtifact(string projectID, string role,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                int removed = checkpoint.CanonicalArtifacts.RemoveAll(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase));
                return removed > 0 ? new(true, true) : new(false, false, $"No canonical artifact with role '{role}'.");
            }, nowUtc);

        public ProjectRuntimeMutationResult SetLastSuccessfulAction(string projectID, ProjectActionCheckpoint action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.LastSuccessfulAction = copy;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult SetAgentLastSuccessfulAction(string projectID, string agentID,
            ProjectActionCheckpoint action, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.AgentLastSuccessfulActions[agentID.Trim()] = copy;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult SetResumeAction(string projectID, ProjectResumeAction action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.ResumeAction = copy;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult ClearResumeAction(string projectID, string? expectedActionID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                if (checkpoint.ResumeAction == null) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedActionID) && checkpoint.ResumeAction.ActionID != expectedActionID)
                    return new(false, false, "The current resume action does not match expectedActionID.");
                checkpoint.ResumeAction = null;
                return new(true, true);
            }, nowUtc);

        public ProjectRuntimeMutationResult SetAgentResumeAction(string projectID, string agentID, ProjectResumeAction action,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(agentID)) throw new ArgumentException("agentID required", nameof(agentID));
            ArgumentNullException.ThrowIfNull(action);
            DateTime now = Utc(nowUtc);
            return MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                var copy = Clone(action);
                if (string.IsNullOrWhiteSpace(copy.ActionID)) copy.ActionID = Guid.NewGuid().ToString("N");
                if (copy.RecordedAt == default) copy.RecordedAt = now;
                checkpoint.AgentResumeActions[agentID.Trim()] = copy;
                return new(true, true);
            }, now);
        }

        public ProjectRuntimeMutationResult ClearAgentResumeAction(string projectID, string agentID, string? expectedActionID = null,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            MutateCheckpoint(projectID, expectedRevision, checkpoint =>
            {
                if (!checkpoint.AgentResumeActions.TryGetValue(agentID, out var current)) return new(true, false);
                if (!string.IsNullOrWhiteSpace(expectedActionID) && current.ActionID != expectedActionID)
                    return new(false, false, "The current agent resume action does not match expectedActionID.");
                checkpoint.AgentResumeActions.Remove(agentID);
                return new(true, true);
            }, nowUtc);

        private ProjectRuntimeMutationResult MutateCheckpoint(string projectID, long? expectedRevision,
            Func<ProjectRuntimeCheckpoint, MutationDecision> mutation, DateTime? nowUtc)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
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

        public ProjectRuntimeMutationResult EnqueueTrigger(string projectID, ProjectWakeTrigger trigger,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
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
                if (state.PendingTriggers.Count >= MaxPendingTriggersPerProject)
                    return new(false, false, $"Pending trigger cap reached ({MaxPendingTriggersPerProject}).");
                state.PendingTriggers.Add(copy);
                return new(true, true);
            }, now);
        }

        public List<ProjectWakeTrigger> ListPendingTriggers(string projectID, bool includeClaimed = true)
        {
            return Get(projectID).PendingTriggers
                .Where(t => includeClaimed || t.ClaimedByWakeID == null)
                .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt)
                .Select(Clone).ToList();
        }

        public ProjectWakeTriggerClaimResult TryClaimNextTrigger(string projectID, string wakeID, long leaseGeneration,
            long? expectedRevision = null, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) throw new ArgumentException("wakeID required", nameof(wakeID));
            DateTime now = Utc(nowUtc);
            lock (LockFor(projectID))
            {
                var state = LoadLocked(projectID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), null, $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                if (!Owns(state.ActiveWakeLease, wakeID, leaseGeneration))
                    return new(false, Clone(state), null, "The supplied wake ID/generation does not own the active lease.");

                bool purged = PurgeStaleLocked(state, now);
                var trigger = state.PendingTriggers
                    .Where(t => t.ClaimedByWakeID == null && EvaluateApplicability(t, state, now) == ProjectWakeTriggerApplicability.Applicable)
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

        public ProjectRuntimeMutationResult AcknowledgeTrigger(string projectID, string triggerID, string wakeID,
            long leaseGeneration, bool succeeded, long? expectedRevision = null, DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
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

        public ProjectRuntimeMutationResult RemoveTrigger(string projectID, string triggerID,
            long? expectedRevision = null, DateTime? nowUtc = null) =>
            Mutate(projectID, expectedRevision, state =>
            {
                int removed = state.PendingTriggers.RemoveAll(x => x.TriggerID == triggerID);
                return removed > 0 ? new(true, true) : new(false, false, "No such pending trigger.");
            }, nowUtc);

        public ProjectRuntimeMutationResult PurgeStaleTriggers(string projectID, long? expectedRevision = null,
            DateTime? nowUtc = null)
        {
            DateTime now = Utc(nowUtc);
            return Mutate(projectID, expectedRevision, state =>
                PurgeStaleLocked(state, now) ? new(true, true) : new(true, false), now);
        }

        public static ProjectWakeTriggerApplicability EvaluateApplicability(ProjectWakeTrigger trigger,
            ProjectRuntimeState state, DateTime utcNow)
        {
            if (trigger.ExpiresAt.HasValue && trigger.ExpiresAt.Value <= utcNow)
                return ProjectWakeTriggerApplicability.Stale;
            if (trigger.NotBefore.HasValue && trigger.NotBefore.Value > utcNow)
                return ProjectWakeTriggerApplicability.Deferred;
            if (trigger.ExpectedCheckpointRevision.HasValue &&
                trigger.ExpectedCheckpointRevision.Value != state.Checkpoint.Revision)
                return ProjectWakeTriggerApplicability.Stale;
            if (trigger.ExpectedGrandPlanVersion.HasValue &&
                trigger.ExpectedGrandPlanVersion.Value != state.Checkpoint.GrandPlanVersion)
                return ProjectWakeTriggerApplicability.Stale;

            bool dispositionMatches = trigger.AllowedDispositions.Count == 0 || trigger.AllowedDispositions.Contains(state.Disposition);
            bool milestonesMatch = trigger.RequiredActiveMilestoneIDs.Count == 0 ||
                trigger.RequiredActiveMilestoneIDs.All(required =>
                    state.Checkpoint.ActiveMilestoneIDs.Contains(required, StringComparer.OrdinalIgnoreCase));
            if (dispositionMatches && milestonesMatch) return ProjectWakeTriggerApplicability.Applicable;
            return trigger.DiscardWhenInapplicable
                ? ProjectWakeTriggerApplicability.Stale
                : ProjectWakeTriggerApplicability.Deferred;
        }

        // ── persistence internals ──

        private ProjectRuntimeMutationResult Mutate(string projectID, long? expectedRevision,
            Func<ProjectRuntimeState, MutationDecision> mutation, DateTime? nowUtc)
        {
            DateTime now = Utc(nowUtc);
            lock (LockFor(projectID))
            {
                var state = LoadLocked(projectID);
                if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                    return new(false, Clone(state), $"Revision mismatch: expected {expectedRevision}, current {state.Revision}.");
                var decision = mutation(state);
                if (decision.Applied && decision.Changed) CommitLocked(state, now);
                return new(decision.Applied, Clone(state), decision.Reason);
            }
        }

        private void CommitLocked(ProjectRuntimeState state, DateTime now)
        {
            state.Revision = checked(state.Revision + 1);
            state.UpdatedAt = now;
            SaveLocked(state);
        }

        private ProjectRuntimeState LoadLocked(string projectID)
        {
            string path = GetStatePath(projectID);
            if (!File.Exists(path)) return NewState(projectID);
            try
            {
                var state = JsonConvert.DeserializeObject<ProjectRuntimeState>(File.ReadAllText(path));
                if (state == null) throw new InvalidDataException("Runtime-state document was empty.");
                Normalize(state, projectID);
                return state;
            }
            catch (Exception ex)
            {
                // Runtime coordination must fail closed: silently replacing a corrupt active lease
                // with an empty state could start a second wake.
                log($"ProjectRuntimeStateStore: failed to load {path}: {ex.Message}");
                throw new InvalidDataException($"Project runtime state for {projectID} is unreadable; execution is stopped to preserve single-flight safety.", ex);
            }
        }

        private void SaveLocked(ProjectRuntimeState state)
        {
            Normalize(state, state.ProjectID);
            string path = GetStatePath(state.ProjectID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented));
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

        private static ProjectRuntimeState NewState(string projectID) => new()
        {
            ProjectID = projectID,
            UpdatedAt = DateTime.UtcNow,
            Checkpoint = new ProjectRuntimeCheckpoint { UpdatedAt = DateTime.UtcNow },
        };

        private static void Normalize(ProjectRuntimeState state, string projectID)
        {
            state.ProjectID = projectID;
            state.Health ??= new ProjectExecutionHealth();
            state.Health.Circuit ??= new ProjectCircuitBreakerState();
            state.Checkpoint ??= new ProjectRuntimeCheckpoint();
            state.Checkpoint.ActiveMilestoneIDs ??= new();
            state.Checkpoint.VerifiedFacts ??= new();
            state.Checkpoint.CanonicalArtifacts ??= new();
            state.Checkpoint.AgentResumeActions ??= new(StringComparer.Ordinal);
            state.Checkpoint.AgentLastSuccessfulActions ??= new(StringComparer.Ordinal);
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
        }

        private static bool PurgeStaleLocked(ProjectRuntimeState state, DateTime now)
        {
            int removed = state.PendingTriggers.RemoveAll(t => t.ClaimedByWakeID == null &&
                EvaluateApplicability(t, state, now) == ProjectWakeTriggerApplicability.Stale);
            return removed > 0;
        }

        private static bool Owns(ProjectWakeLease? lease, string wakeID, long generation) =>
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

        private static void ValidateProjectID(string projectID)
        {
            if (string.IsNullOrWhiteSpace(projectID)) throw new ArgumentException("projectID required", nameof(projectID));
            if (projectID.Length > 200 || projectID is "." or ".." ||
                projectID.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                projectID.Contains(Path.DirectorySeparatorChar) || projectID.Contains(Path.AltDirectorySeparatorChar))
                throw new ArgumentException("projectID contains invalid path characters", nameof(projectID));
        }
    }
}
