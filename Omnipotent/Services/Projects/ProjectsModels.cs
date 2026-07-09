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

        public DesktopAllocationMode DesktopAllocation { get; set; } = DesktopAllocationMode.SharedDesktopWithInputLock;
        /// <summary>Discord #project channel; 0 until Phase 5 creates it.</summary>
        public ulong DiscordChannelID { get; set; }
        public DateTime? CompletedAt { get; set; }
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
        public const string CommanderThought = "commander-thought";    // intermediate prose during a wake
        public const string AgentThought = "agent-thought";            // sub-agent intermediate prose during a wake
        public const string CommanderMessage = "commander-message";    // prose addressed to Klives
        public const string KlivesMessage = "klives-message";          // Klives → Commander (website chat or Discord reply)
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
        public const string WatchdogEscalation = "watchdog-escalation";
        public const string Status = "status";                         // generic progress note
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
        /// <summary>The Commander's current plan, maintained by digest rebuilds.</summary>
        public string CurrentPlan { get; set; } = "";
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
