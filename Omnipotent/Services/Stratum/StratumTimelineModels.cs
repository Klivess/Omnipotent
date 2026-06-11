namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Event types in the unified per-project conversation timeline. Open-ended strings so
    /// new types don't break older clients or stored logs.
    /// </summary>
    public static class StratumTimelineEventTypes
    {
        public const string UserMessage = "user-message";
        public const string AgentMessage = "agent-message";   // final assistant prose for a turn
        public const string Thought = "thought";              // intermediate assistant prose
        public const string ToolCall = "tool-call";
        public const string ToolResult = "tool-result";
        public const string Image = "image";                  // render attached to the conversation (artifact-backed)
        public const string GateOpened = "gate-opened";
        public const string GateResolved = "gate-resolved";
        public const string ArtifactAdded = "artifact-added";
        public const string TurnStarted = "turn-started";
        public const string TurnCompleted = "turn-completed";
        public const string TurnFailed = "turn-failed";
        public const string TurnCancelled = "turn-cancelled";
        public const string Status = "status";                // progress note during long tools
    }

    /// <summary>
    /// One sequence-numbered entry in a project's conversation timeline. Persisted as JSONL
    /// (append-only) — never as a rewritten whole-file JSON, since a single Engineer turn can
    /// produce hundreds of entries. Images are referenced by artifact ID, never inlined.
    /// </summary>
    public class StratumTimelineEvent
    {
        public long Sequence { get; set; }
        public string EventID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>RunID of the Engineer turn this event belongs to; null for user messages.</summary>
        public string? TurnID { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>One of <see cref="StratumTimelineEventTypes"/>.</summary>
        public string Type { get; set; } = "";
        /// <summary>"user" | "agent" | "system".</summary>
        public string Author { get; set; } = "agent";
        public string Text { get; set; } = "";
        /// <summary>Tool name for tool-call / tool-result events.</summary>
        public string? ToolName { get; set; }
        /// <summary>Provider tool_call id linking a tool-result to its tool-call.</summary>
        public string? ToolCallId { get; set; }
        /// <summary>Tool args / results / gate proposal as raw JSON, truncated to 32 KB at write time.</summary>
        public string? PayloadJson { get; set; }
        public List<string> ArtifactIDs { get; set; } = new();
        public string? GateID { get; set; }
    }

    /// <summary>
    /// Per-project conversation metadata: the rolling summary used to seed each turn, and the
    /// currently active turn (one at a time per project).
    /// </summary>
    public class StratumConversationMeta
    {
        public string ProjectID { get; set; } = "";
        /// <summary>Compact summary of everything older than the recent verbatim window.</summary>
        public string RollingSummary { get; set; } = "";
        /// <summary>Highest sequence number folded into RollingSummary.</summary>
        public long LastCompactedSequence { get; set; }
        /// <summary>RunID of the Engineer turn currently executing; null when idle.</summary>
        public string? ActiveTurnID { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
