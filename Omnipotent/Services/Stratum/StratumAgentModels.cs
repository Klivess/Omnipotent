using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Type of agent within Stratum's mechatronics pipeline. Phase 2 ships with Planning only;
    /// later phases will add Mechanical, Electronics, Firmware, Simulation.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum StratumAgentType
    {
        Planning,
        Mechanical,
        Electronics,
        Firmware,
        Simulation
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StratumRunStatus
    {
        Pending,
        Running,
        AwaitingApproval,
        Completed,
        Rejected,
        Failed,
        // Set on service startup for any run that was mid-flight when Omnipotent restarted.
        // The in-memory gate TCS is gone; user must start a fresh run.
        Interrupted
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StratumGateStatus
    {
        Awaiting,
        Approved,
        Rejected
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StratumGateDecision
    {
        Approve,
        Reject
    }

    /// <summary>
    /// Event types streamed from agent → UI. Kept open-ended (string) so adding
    /// new event types in later phases doesn't break older clients/snapshots.
    /// </summary>
    public static class StratumEventTypes
    {
        public const string Status = "status";        // run status changed
        public const string Thought = "thought";       // agent's reasoning text
        public const string Output = "output";        // structured output produced
        public const string GateOpened = "gate-opened"; // human approval required
        public const string GateResolved = "gate-resolved"; // user approved/rejected
        public const string ArtifactAdded = "artifact-added"; // a revision artifact was created
        public const string Error = "error";
        public const string Completed = "completed";   // run finished successfully
    }

    /// <summary>
    /// One human-in-the-loop approval gate. The agent emits a gate, persists it, and suspends
    /// until the user approves/rejects via the resolve route. Per paper Eq. (3) P_{n+1} = F(P_n, R_n)
    /// rejection comments feed back into the next agent iteration.
    /// </summary>
    public class StratumApprovalGate
    {
        public string GateID { get; set; } = "";
        public string RunID { get; set; } = "";
        public DateTime OpenedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public StratumAgentType AgentType { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Short rationale the agent wants the user to see before deciding.</summary>
        public string AgentRationale { get; set; } = "";
        /// <summary>JSON blob of the proposal (e.g. plan tree, parameter table). UI renders it.</summary>
        public string ProposalJson { get; set; } = "";
        /// <summary>Optional artifact IDs the gate is asking the user to inspect (mesh previews, etc.).</summary>
        public List<string> ProposalArtifactIDs { get; set; } = new();
        public StratumGateStatus Status { get; set; } = StratumGateStatus.Awaiting;
        public StratumGateDecision? Decision { get; set; }
        public string UserComment { get; set; } = "";
        public string ResolvedByUserID { get; set; } = "";
    }

    public class StratumAgentRun
    {
        public string RunID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        public StratumAgentType AgentType { get; set; }
        public string OwnerUserID { get; set; } = "";
        public StratumRunStatus Status { get; set; } = StratumRunStatus.Pending;
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string UserPrompt { get; set; } = "";
        public List<string> AttachmentIDs { get; set; } = new();
        public string ErrorMessage { get; set; } = "";
        /// <summary>RevisionID this run is producing artifacts into (defaults to the project's latest revision).</summary>
        public string TargetRevisionID { get; set; } = "";
        /// <summary>The currently open gate, if any. Cleared when resolved.</summary>
        public string? CurrentGateID { get; set; }
        /// <summary>How many planner iterations the agent has done so far (refinement loop counter).</summary>
        public int Iteration { get; set; } = 0;

        /// <summary>
        /// When non-empty, the agent must only (re)design the listed subtasks; everything else is
        /// reused from prior approved artifacts. Set by chat-spawned amendment runs so unrelated
        /// parts aren't re-generated. Subtask titles match <c>StratumPlannerSubtask.Title</c>.
        /// </summary>
        public List<string> RestrictToSubtasks { get; set; } = new();
    }

    /// <summary>
    /// Append-only event entry for a run. Persisted as JSONL beside the run JSON.
    /// </summary>
    public class StratumAgentEvent
    {
        public long Sequence { get; set; }
        public string RunID { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "";
        /// <summary>Free-form payload (already JSON-serialised) — kept as raw JSON to avoid double-encoding.</summary>
        public object? Payload { get; set; }
    }

    /// <summary>
    /// Strict output schema returned by the Planning Agent. Nullable strings are tolerated;
    /// arrays default to empty so missing sections don't blow up downstream parsing.
    /// </summary>
    public class StratumPlannerOutput
    {
        public string DeviceConcept { get; set; } = "";
        public List<StratumPlannerSubtask> MechanicalSubtasks { get; set; } = new();
        public List<StratumPlannerSubtask> ElectronicsSubtasks { get; set; } = new();
        public List<StratumPlannerSubtask> FirmwareSubtasks { get; set; } = new();
        public List<StratumPlannerSubtask> SimulationSubtasks { get; set; } = new();
        public List<string> OpenQuestions { get; set; } = new();
        public List<string> Assumptions { get; set; } = new();
    }

    public class StratumPlannerSubtask
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Optional ordered dependency titles within the same plan.</summary>
        public List<string> DependsOn { get; set; } = new();
    }
}
