namespace Omnipotent.Services.Projects
{
    /// <summary>Lifecycle state of a Project.</summary>
    public enum ProjectStatus
    {
        Active,
        Paused,          // paused by Klives
        BudgetPaused,    // hit 100% of token budget; waiting on a budget conversation
        Completed,
        Archived,
        // NOTE: persisted as an int by ProjectStore — new values MUST be appended, never inserted.
        Planning,        // newly created: forming a Grand Plan for Klives' approval before any execution work
        Blocked,         // action-required dependency/configuration failure; resume explicitly after remediation
    }

    /// <summary>
    /// Capability tiers per design doc §6.1. A tier controls both model routing
    /// (OmniSetting maps tier → model) and tool gating (computer control requires
    /// video-tier perception; text tier gets scripts/HTTP/files only).
    /// </summary>
    public enum ProjectAgentTier
    {
        Text = 0,
        TextImage = 1,
        TextImageVideo = 2,
        TextImageVideoAudio = 3,
    }

    public enum ProjectAgentWorkStatus
    {
        Idle,
        Assigned,
        Running,
        // Historical persistence value only. It is normalized to Assigned so a worker's
        // report can request follow-up without becoming a project-stopping state.
        Blocked,
        Completed,
    }

    /// <summary>
    /// A durable instruction from Klives. Unlike ordinary timeline messages, directives are
    /// injected into every applicable wake and never depend on digest compaction or retrieval.
    /// </summary>
    public enum ProjectDirectiveKind
    {
        /// <summary>A standing constraint or preference, such as "do not use bot accounts".</summary>
        Rule,
        /// <summary>A concrete piece of work that remains visible until it is completed or revoked.</summary>
        Task,
        /// <summary>A one-off steering message. It remains visible until the Commander replies.</summary>
        Steering,
    }

    /// <summary>Which project agents must receive a directive.</summary>
    public enum ProjectDirectiveScope
    {
        Commander,
        AllAgents,
        SpecificAgents,
    }

    /// <summary>Lifecycle of a durable Klives directive.</summary>
    public enum ProjectDirectiveStatus
    {
        Active,
        Delivered,
        Acknowledged,
        Completed,
        Failed,
        Revoked,
    }

    /// <summary>
    /// Persisted project memory / command record. The event log remains the audit trail; this is
    /// the small, authoritative instruction set that survives restarts and digest rebuilds.
    /// </summary>
    public sealed class ProjectDirective
    {
        public string DirectiveID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>Optional durable correlation for a fleet broadcast.</summary>
        public string? BatchID { get; set; }
        /// <summary>Optional user-controlled stable key; rules with the same key are updated in place.</summary>
        public string? Key { get; set; }
        public ProjectDirectiveKind Kind { get; set; } = ProjectDirectiveKind.Steering;
        public ProjectDirectiveScope Scope { get; set; } = ProjectDirectiveScope.Commander;
        public List<string> TargetAgentIDs { get; set; } = new();
        public string Text { get; set; } = "";
        /// <summary>Higher values are rendered first. Human directives default to 100.</summary>
        public int Priority { get; set; } = 100;
        public ProjectDirectiveStatus Status { get; set; } = ProjectDirectiveStatus.Active;
        public List<string> ExpectedArtifactPaths { get; set; } = new();
        public string CreatedBy { get; set; } = "klives";
        public string? SourceEventID { get; set; }
        public long? SourceEventSequence { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }
        public string? DeliveredToAgentID { get; set; }
        public string? DeliveredWakeID { get; set; }
        /// <summary>Per-recipient delivery ledger; the legacy Delivered* fields mirror the latest entry.</summary>
        public List<ProjectDirectiveDelivery> Deliveries { get; set; } = new();
        public DateTime? AcknowledgedAt { get; set; }
        public string? AcknowledgedBy { get; set; }
        public string? Acknowledgement { get; set; }
        /// <summary>Bounded automatic re-delivery attempts after a wake closed without an explicit acknowledgement.</summary>
        public int RetryCount { get; set; }
        public DateTime? LastRetryAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? CompletedBy { get; set; }
        public string? CompletionSummary { get; set; }
        /// <summary>Verified project-relative deliverables recorded at completion.</summary>
        public List<string> CompletionArtifactPaths { get; set; } = new();
        public long? ReplyEventSequence { get; set; }
        public long Revision { get; set; }

        public bool IsOpen => Status is ProjectDirectiveStatus.Active or ProjectDirectiveStatus.Delivered
            or ProjectDirectiveStatus.Acknowledged;
    }

    /// <summary>One agent/wake delivery attempt for a durable directive.</summary>
    public sealed class ProjectDirectiveDelivery
    {
        public string AgentID { get; set; } = "";
        public string? WakeID { get; set; }
        public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Immediate, truthful receipt returned when Klives sends a Project command.</summary>
    public sealed class ProjectCommandReceipt
    {
        public bool Accepted { get; set; }
        public string ProjectID { get; set; } = "";
        public string DirectiveID { get; set; } = "";
        public string? BatchID { get; set; }
        public string TargetAgentID { get; set; } = "commander";
        /// <summary>accepted | delivered | deferred | rejected.</summary>
        public string Status { get; set; } = "accepted";
        public string? WakeID { get; set; }
        public long? EventSequence { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<string> ExpectedArtifactPaths { get; set; } = new();
    }

    /// <summary>How desktops are allocated within a project (Commander's call, §4).</summary>
    public enum DesktopAllocationMode
    {
        PerAgentContainers,
        SharedDesktopWithInputLock,
    }

    /// <summary>
    /// A Project: a goal + a budget, pursued 24/7 by one Commander and a fleet of
    /// sub-agents. Budget/cap fields are deliberately NOT OmniSettings (design doc §8) —
    /// they are set at initialisation and only change through a Commander-negotiated
    /// approval gate.
    /// </summary>
    public class Project
    {
        public string ProjectID { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The long-horizon goal, verbatim as Klives stated it.</summary>
        public string Goal { get; set; } = "";
        public ProjectStatus Status { get; set; } = ProjectStatus.Active;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Total LLM token spend allowed, in USD.</summary>
        public double TokenBudgetUsd { get; set; }
        /// <summary>Total real-money spend allowed, in USD.</summary>
        public double MoneyBudgetUsd { get; set; }
        /// <summary>Single-action real-money spends at or below this are autonomous; above needs Discord approval. Stricter than, and independent of, the token budget (§8).</summary>
        public double MoneyAutonomousThresholdUsd { get; set; }
        /// <summary>Maximum concurrent agents (Commander + sub-agents + one-level helpers).</summary>
        public int SubAgentCap { get; set; } = 5;

        /// <summary>
        /// Every agent gets its own long-lived browser profile and desktop by default. Shared
        /// desktops remain available as an explicit resource-saving mode, but are a poor default:
        /// one worker can otherwise steal focus, cookies, tabs, or keyboard state from another.
        /// </summary>
        public DesktopAllocationMode DesktopAllocation { get; set; } = DesktopAllocationMode.PerAgentContainers;
        /// <summary>Discord #project channel; 0 until Phase 5 creates it.</summary>
        public ulong DiscordChannelID { get; set; }
        public DateTime? CompletedAt { get; set; }
        /// <summary>Machine-owned reason for an action-required block. Narrative observables must not override it.</summary>
        public string? BlockedReason { get; set; }
        public DateTime? BlockedAt { get; set; }

        /// <summary>
        /// When this project was halted by a fleet-wide halt-all, the status it held immediately
        /// beforehand — so unhalt-all restores each project to exactly where it was (Active, Planning,
        /// Paused, BudgetPaused, …) rather than blanket-resuming everything to Active. Null when the
        /// project is not currently under a global halt. Cleared whenever Klives changes the project's
        /// status individually (pause/resume/archive/unarchive/budget-resume), so a later unhalt-all
        /// never resurrects a stale state.
        /// </summary>
        public ProjectStatus? HaltedFromStatus { get; set; }
    }

    /// <summary>One agent in a project's org chart (Commander included, with a null parent).</summary>
    public class ProjectAgentRecord
    {
        public string AgentID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>Null for the Commander; the Commander's ID for its spawns; a sub-agent's ID for one-level-deep helpers.</summary>
        public string? ParentAgentID { get; set; }
        public ProjectAgentTier Tier { get; set; } = ProjectAgentTier.Text;
        /// <summary>Free-text role, e.g. "commander", "market-researcher".</summary>
        public string Role { get; set; } = "";
        public string Objective { get; set; } = "";
        public ProjectAgentWorkStatus WorkStatus { get; set; } = ProjectAgentWorkStatus.Idle;
        public List<string> ActiveMilestoneIDs { get; set; } = new();
        public List<string> DeliverablePaths { get; set; } = new();
        public string? LastReport { get; set; }
        public DateTime? LastReportAt { get; set; }
        public bool Retired { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RetiredAt { get; set; }
    }

    /// <summary>
    /// Event types in the per-project append-only log — the single source of truth (§7).
    /// Open-ended strings so new types don't break older clients or stored logs
    /// (same convention as StratumTimelineEventTypes).
    /// </summary>
    public static class ProjectEventTypes
    {
        public const string Stimulus = "stimulus";                     // confirmed stimulus delivered to an agent
        public const string CommanderWake = "commander-wake";          // a wake began (heartbeat for the watchdog)
        public const string AgentWake = "agent-wake";                  // a sub-agent wake began
        public const string WakeCompleted = "wake-completed";
        public const string WakeFailed = "wake-failed";
        public const string WakeCancelled = "wake-cancelled";          // deliberate pause/archive/recovery cancellation; not a provider failure
        public const string WakeDeferred = "wake-deferred";            // retryable infrastructure circuit is open until a typed retry time
        public const string CommanderThought = "commander-thought";    // intermediate prose during a wake
        public const string AgentThought = "agent-thought";            // sub-agent intermediate prose during a wake
        public const string CommanderMessage = "commander-message";    // prose addressed to Klives
        public const string KlivesMessage = "klives-message";          // Klives → Commander (website chat or Discord reply)
        public const string HumanAssistanceRequested = "human-assistance-requested"; // one durable, de-duplicated human-only request
        public const string AgentMessage = "agent-message";            // inter-agent message riding the bus
        public const string ToolCall = "tool-call";
        public const string ToolResult = "tool-result";
        public const string AgentSpawned = "agent-spawned";
        public const string AgentRetired = "agent-retired";
        public const string ApprovalRequested = "approval-requested";
        public const string ApprovalResolved = "approval-resolved";
        public const string BudgetWarning = "budget-warning";          // ~80% burn
        public const string BudgetPaused = "budget-paused";            // 100% — project paused
        public const string MoneySpent = "money-spent";                // real-money ledger entry
        public const string ArtifactAdded = "artifact-added";
        public const string DigestRebuilt = "digest-rebuilt";
        public const string HookChanged = "hook-changed";              // stimulus hook CRUD is itself an event (§5.1)
        public const string ObservableChanged = "observable-changed";  // a named live value shown to Klives was created/updated/deleted
        public const string WatchdogRecovery = "watchdog-recovery";     // an automatic recovery was actually initiated
        public const string WatchdogReminder = "watchdog-reminder";     // one durable reminder for an aged approval gate
        public const string WatchdogEscalation = "watchdog-escalation"; // automatic recovery was exhausted / human attention requested
        public const string CouncilConvened = "council-convened";      // Commander raised an adversarial council on a topic
        public const string CouncilStatement = "council-statement";    // one panelist's opening/rebuttal, or the Chair's synthesis
        public const string CouncilVerdict = "council-verdict";        // the council's synthesized recommendation
        public const string GrandPlanSubmitted = "grand-plan-submitted";            // Commander submitted a Grand Plan version for approval
        public const string GrandPlanApproved = "grand-plan-approved";              // Klives approved a Grand Plan version
        public const string GrandPlanRevisionRequested = "grand-plan-revision-requested"; // Klives denied/deferred; revise & resubmit
        public const string GrandPlanAmended = "grand-plan-amended";                // a non-material amendment applied without a gate
        public const string GrandPlanProgress = "grand-plan-progress";              // a milestone status / success-criterion tick (live progress, no gate)
        public const string AccountChanged = "account-changed";        // a shared-registry account was registered/updated (metadata only, never secrets)
        public const string ProjectFileChanged = "project-file-changed"; // shared project-volume upload/write/move/delete/metadata batch
        public const string Status = "status";                         // generic progress note
        public const string ProjectBlocked = "project-blocked";        // action-required blocker changed lifecycle to Blocked
        public const string ProjectUnblocked = "project-unblocked";    // Klives resumed after remediation
        public const string CheckpointChanged = "checkpoint-changed";  // typed execution checkpoint/fact/artifact/blocker mutation
        public const string DirectiveCreated = "directive-created";    // Klives created/updated durable project memory
        public const string DirectiveDelivered = "directive-delivered"; // directive entered a specific agent wake
        public const string DirectiveAcknowledged = "directive-acknowledged";
        public const string DirectiveCompleted = "directive-completed";
        public const string DirectiveRevoked = "directive-revoked";
    }

    /// <summary>
    /// One sequence-numbered entry in a project's event log. Persisted as JSONL, append-only.
    /// Media (clips/screenshots) are referenced by artifact ID, never inlined — the 48h raw
    /// retention policy (§7) operates on the referenced files, not the log.
    /// </summary>
    public class ProjectEvent
    {
        public long Sequence { get; set; }
        public string EventID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>The Commander wake (or sub-agent run) this event belongs to; null for external events.</summary>
        public string? WakeID { get; set; }
        /// <summary>Which agent authored/received this event; null for system/Klives events.</summary>
        public string? AgentID { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>One of <see cref="ProjectEventTypes"/>.</summary>
        public string Type { get; set; } = "";
        /// <summary>"commander" | "agent" | "klives" | "system" | "stimulus".</summary>
        public string Author { get; set; } = "system";
        public string Text { get; set; } = "";
        public string? ToolName { get; set; }
        public string? ToolCallId { get; set; }
        /// <summary>Tool args / results / gate proposals / stimulus payloads as raw JSON, truncated to 32 KB at write time.</summary>
        public string? PayloadJson { get; set; }
        public List<string> ArtifactIDs { get; set; } = new();
        public string? GateID { get; set; }
        /// <summary>ID of the stimulus envelope that produced this event, when applicable.</summary>
        public string? StimulusID { get; set; }
    }

    /// <summary>
    /// The standing digest — the compact always-current picture of a project used to seed
    /// every Commander wake (§7): goal, current plan, org chart, budget state, open threads.
    /// Rewritten in place (small doc), while the event log stays append-only.
    /// Also carries the per-project mutable coordination state (active wake, compaction
    /// watermark) that Stratum kept in its conversation meta.
    /// </summary>
    public class ProjectDigest
    {
        public string ProjectID { get; set; } = "";
        /// <summary>The Commander's current plan, maintained by digest rebuilds. Legacy free-text; prefer CurrentFocus + NextSteps.</summary>
        public string CurrentPlan { get; set; } = "";
        /// <summary>The Commander's one-line current focus (tactical), shown in Klives' side rail.</summary>
        public string CurrentFocus { get; set; } = "";
        /// <summary>The near-term concrete next steps (tactical), shown in Klives' side rail.</summary>
        public List<string> NextSteps { get; set; } = new();
        /// <summary>Who exists, their tiers/roles, what each is doing.</summary>
        public string OrgChart { get; set; } = "";
        /// <summary>Spend vs budget, burn rate — refreshed from the ledger at rebuild time.</summary>
        public string BudgetState { get; set; } = "";
        /// <summary>Unresolved questions, pending approvals, blockers.</summary>
        public string OpenThreads { get; set; } = "";
        /// <summary>Compact narrative of everything older than the recent verbatim window.</summary>
        public string RollingSummary { get; set; } = "";
        /// <summary>Highest sequence number folded into this digest.</summary>
        public long LastDigestedSequence { get; set; }
        /// <summary>WakeID currently executing; null when the Commander is asleep. One wake at a time per project.</summary>
        public string? ActiveWakeID { get; set; }
        /// <summary>Stuck-loop-guard trips during recent wakes — a watchdog signal (P7).</summary>
        public int RecentStuckLoopTrips { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
