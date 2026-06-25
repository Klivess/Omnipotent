using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Threading;

namespace Omnipotent.Services.KliveAgent.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentMessageRole
    {
        User,
        Agent,
        System,
        Script
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentSourceChannel
    {
        API,
        Discord
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentTaskStatus
    {
        Running,
        Completed,
        Cancelled,
        Failed
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentCapabilityPermissionTier
    {
        Safe,
        Moderate,
        Dangerous
    }

    public class AgentCapabilityParameterDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = "string";

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("required")]
        public bool Required { get; set; }

        [JsonProperty("defaultValue")]
        public object? DefaultValue { get; set; }
    }

    public class AgentCapabilityDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("permissionTier")]
        public AgentCapabilityPermissionTier PermissionTier { get; set; }

        [JsonProperty("requiresConfirmation")]
        public bool RequiresConfirmation { get; set; }

        [JsonProperty("confirmationMessage")]
        public string? ConfirmationMessage { get; set; }

        [JsonProperty("parameters")]
        public List<AgentCapabilityParameterDefinition> Parameters { get; set; } = new();

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonProperty("exampleInvocation")]
        public string? ExampleInvocation { get; set; }
    }

    public class AgentCapabilityInvocationRequest
    {
        [JsonProperty("capability")]
        public string Capability { get; set; } = string.Empty;

        [JsonProperty("arguments")]
        public Dictionary<string, object?> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("confirmed")]
        public bool Confirmed { get; set; }
    }

    public class AgentCapabilityExecutionRequest : AgentCapabilityInvocationRequest
    {
        [JsonProperty("capabilityName")]
        public string CapabilityName
        {
            get => Capability;
            set => Capability = value;
        }
    }

    public class AgentCapabilityExecutionContext
    {
        [JsonProperty("conversationId")]
        public string? ConversationId { get; set; }

        [JsonProperty("senderName")]
        public string? SenderName { get; set; }

        [JsonProperty("sourceChannel")]
        public AgentSourceChannel SourceChannel { get; set; } = AgentSourceChannel.API;

        [JsonProperty("confirmed")]
        public bool Confirmed { get; set; }

        [JsonProperty("hasElevatedPermissions")]
        public bool HasElevatedPermissions { get; set; }
    }

    public class AgentCapabilityInvocationResult
    {
        [JsonProperty("capability")]
        public string Capability { get; set; } = string.Empty;

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonProperty("permissionTier")]
        public AgentCapabilityPermissionTier PermissionTier { get; set; }

        [JsonProperty("requiresConfirmation")]
        public bool RequiresConfirmation { get; set; }

        [JsonProperty("confirmationMessage")]
        public string? ConfirmationMessage { get; set; }

        [JsonProperty("data")]
        public object? Data { get; set; }

        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }
    }

    public class AgentCapabilityExecutionResult : AgentCapabilityInvocationResult
    {
    }

    public class AgentCapabilityInvocationContext
    {
        [JsonProperty("conversationId")]
        public string? ConversationId { get; set; }

        [JsonProperty("senderName")]
        public string? SenderName { get; set; }

        [JsonProperty("sourceChannel")]
        public AgentSourceChannel SourceChannel { get; set; }

        [JsonProperty("confirmed")]
        public bool Confirmed { get; set; }

        [JsonProperty("hasElevatedPermissions")]
        public bool HasElevatedPermissions { get; set; }
    }

    public class AgentServiceStatusSummary
    {
        [JsonProperty("serviceName")]
        public string ServiceName { get; set; } = string.Empty;

        [JsonProperty("serviceType")]
        public string ServiceType { get; set; } = string.Empty;

        [JsonProperty("isActive")]
        public bool IsActive { get; set; }

        [JsonProperty("uptime")]
        public TimeSpan Uptime { get; set; }

        [JsonProperty("uptimeHumanized")]
        public string UptimeHumanized { get; set; } = string.Empty;
    }

    public class AgentMessage
    {
        [JsonProperty("role")]
        public AgentMessageRole Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Legacy single-script slot. Kept for back-compat deserialization of older
        /// conversation files; new turns populate <see cref="ScriptResults"/> instead.</summary>
        [JsonProperty("scriptResult")]
        public AgentScriptResult ScriptResult { get; set; }

        /// <summary>Every script the agent wrote+ran while producing this turn (in order), with
        /// their outputs/errors. Lets the agent see its own prior code+outputs on later turns and
        /// lets the UI replay them when a past conversation is reloaded.</summary>
        [JsonProperty("scriptResults")]
        public List<AgentScriptResult> ScriptResults { get; set; }

        [JsonProperty("senderName")]
        public string SenderName { get; set; }
    }

    public class AgentConversation
    {
        [JsonProperty("conversationId")]
        public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("messages")]
        public List<AgentMessage> Messages { get; set; } = new();

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonProperty("sourceChannel")]
        public AgentSourceChannel SourceChannel { get; set; }
    }

    public class AgentScriptResult
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("output")]
        public string Output { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("executionTimeMs")]
        public long ExecutionTimeMs { get; set; }
    }

    /// <summary>
    /// Flat, scalar-only snapshot of KliveAgent's own run statistics. Every field is a primitive, so it
    /// serializes cleanly at any JSON depth (no object cycles / depth-exhaustion, unlike the nested
    /// GetAgentStats() shape) and can be dot-accessed directly. Returned by GetAgentStatsSummary().
    /// </summary>
    public class AgentStatsSummary
    {
        public string TodayUtcDate { get; set; } = "";
        public long LifetimeScriptsRun { get; set; }
        public long LifetimeScriptFailures { get; set; }
        public double LifetimeScriptFailureRatePct { get; set; }
        public double LifetimeScriptSuccessRatePct { get; set; }
        public long TodayScriptsRun { get; set; }
        public long TodayScriptFailures { get; set; }
        public long LifetimeMessages { get; set; }
        public long LifetimeIterations { get; set; }
        public double AvgIterationsPerMessage { get; set; }
        public long LifetimePromptTokens { get; set; }
        public long LifetimeCompletionTokens { get; set; }
        public double EstimatedCostUsd { get; set; }
        public long CapabilityCalls { get; set; }
        public double CapabilitySuccessRatePct { get; set; }
        public long MemorySaves { get; set; }
        public long MemoryRecalls { get; set; }
    }

    /// <summary>Flat failure breakdown for the agent's own script runs. TopErrorCodes is a shallow list
    /// of {Code, Count} pairs (depth 2), safe to JSON-serialize. Returned by GetScriptFailureBreakdown().</summary>
    public class AgentScriptFailureBreakdown
    {
        public long TotalScripts { get; set; }
        public long TotalFailures { get; set; }
        public double FailureRatePct { get; set; }
        public long CompileFailures { get; set; }
        public long RuntimeFailures { get; set; }
        public List<ErrorCodeCount> TopErrorCodes { get; set; } = new();
    }

    public class ErrorCodeCount
    {
        public string Code { get; set; } = "";
        public long Count { get; set; }
    }

    public class AgentTypeSchema
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("fullName")]
        public string? FullName { get; set; }

        [JsonProperty("baseType")]
        public string? BaseType { get; set; }

        [JsonProperty("interfaces")]
        public List<string> Interfaces { get; set; } = new();

        [JsonProperty("methods")]
        public List<AgentTypeMethodSchema> Methods { get; set; } = new();

        [JsonProperty("properties")]
        public List<AgentTypePropertySchema> Properties { get; set; } = new();

        [JsonProperty("fields")]
        public List<AgentTypeFieldSchema> Fields { get; set; } = new();
    }

    public class AgentTypeMethodSchema
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("declaringType")]
        public string DeclaringType { get; set; } = string.Empty;

        [JsonProperty("returnType")]
        public string ReturnType { get; set; } = string.Empty;

        /// <summary>Full, ready-to-call signature, e.g.
        /// "SendMessageToChannel(ulong guildId, ulong channelId, DiscordMessageBuilder builder) -> Task&lt;DiscordMessage&gt;".
        /// Reachable directly from GetTypeSchema so callers never need a per-method GetMethodDocumentation round-trip.</summary>
        [JsonProperty("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonProperty("isStatic")]
        public bool IsStatic { get; set; }

        [JsonProperty("parameters")]
        public List<AgentTypeParameterSchema> Parameters { get; set; } = new();
    }

    public class AgentTypeParameterSchema
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Alias for <see cref="Type"/>. Lets scripts that naturally reach for
        /// <c>p.ParameterType</c> compile (a common, previously-fatal guess). Not serialized.</summary>
        [JsonIgnore]
        public string ParameterType => Type;

        [JsonProperty("hasDefaultValue")]
        public bool HasDefaultValue { get; set; }

        [JsonProperty("defaultValue")]
        public string? DefaultValue { get; set; }
    }

    public class AgentTypePropertySchema
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("declaringType")]
        public string DeclaringType { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("canRead")]
        public bool CanRead { get; set; }

        [JsonProperty("canWrite")]
        public bool CanWrite { get; set; }

        [JsonProperty("isStatic")]
        public bool IsStatic { get; set; }
    }

    public class AgentTypeFieldSchema
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("declaringType")]
        public string DeclaringType { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("isStatic")]
        public bool IsStatic { get; set; }
    }

    /// <summary>A single member (field/property/method) of a live object, returned by
    /// GetObjectMembers so scripts can LINQ over it inline (filter, pick, call) without
    /// JSON-serializing-and-splitting a string blob.</summary>
    public class AgentObjectMember
    {
        /// <summary>"field" | "property" | "method"</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;

        /// <summary>"public" | "private"</summary>
        [JsonProperty("visibility")]
        public string Visibility { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Field/property type, or a method's return type.</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>For methods: the full callable signature. Null for fields/properties.</summary>
        [JsonProperty("signature")]
        public string? Signature { get; set; }

        [JsonProperty("isStatic")]
        public bool IsStatic { get; set; }

        /// <summary>For FIELDS read off a live object: whether the current value is null. Null = not probed
        /// (methods, and properties — whose getters are skipped to avoid side effects).</summary>
        [JsonProperty("isNull")]
        public bool? IsNull { get; set; }

        /// <summary>For FIELDS with a non-null live value whose runtime type differs from the declared type:
        /// the actual runtime type name. Null otherwise.</summary>
        [JsonProperty("runtimeType")]
        public string? RuntimeType { get; set; }

        public override string ToString()
        {
            if (Signature != null) return Signature;
            var state = IsNull == true ? " = null" : (RuntimeType != null ? $" = {RuntimeType}" : string.Empty);
            return $"{Kind} {Type} {Name}{state}";
        }
    }

    /// <summary>A single entry from ListDataDirectory — a file or folder in a runtime data directory.
    /// Scalars only, safe to serialize.</summary>
    public class AgentFileEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastModifiedUtc { get; set; }

        public override string ToString() =>
            IsDirectory ? $"[DIR]  {Name}/" : $"[FILE] {Name} ({SizeBytes:N0} bytes, {LastModifiedUtc:yyyy-MM-dd})";
    }

    public class ServiceInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonProperty("uptime")]
        public string Uptime { get; set; } = string.Empty;

        public override string ToString() => $"{TypeName} (\"{Name}\", uptime: {Uptime})";
    }

    public class ProjectClassInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [JsonProperty("namespace")]
        public string Namespace { get; set; } = string.Empty;

        [JsonProperty("relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonProperty("lineNumber")]
        public int LineNumber { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; } = "class";

        [JsonProperty("summary")]
        public string? Summary { get; set; }

        public override string ToString() => $"{FullName} ({RelativePath}:{LineNumber})";
    }

    public class ProjectParameterDocumentation
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("hasDefaultValue")]
        public bool HasDefaultValue { get; set; }

        [JsonProperty("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonProperty("documentation")]
        public string? Documentation { get; set; }
    }

    public class ProjectMethodDocumentation
    {
        [JsonProperty("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonProperty("methodName")]
        public string MethodName { get; set; } = string.Empty;

        [JsonProperty("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonProperty("summary")]
        public string? Summary { get; set; }

        [JsonProperty("returns")]
        public string? Returns { get; set; }

        [JsonProperty("relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonProperty("lineNumber")]
        public int LineNumber { get; set; }

        [JsonProperty("parameters")]
        public List<ProjectParameterDocumentation> Parameters { get; set; } = new();

        public override string ToString() => Signature;
    }

    public class AgentBackgroundTaskInfo
    {
        [JsonProperty("taskId")]
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("status")]
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("completedAt")]
        public DateTime? CompletedAt { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }
    }

    public class AgentMemoryEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("content")]
        public string Content { get; set; }

        /// <summary>"general" (default) or "shortcut" (reusable how-to recipe)</summary>
        [JsonProperty("memoryType")]
        public string MemoryType { get; set; } = "general";

        /// <summary>Short human-readable title, mainly used for shortcuts.</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("source")]
        public string Source { get; set; } // "agent" or "user"

        [JsonProperty("importance")]
        public int Importance { get; set; } = 1; // 1-5 scale
    }

    public class AgentChatRequest
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("conversationId")]
        public string ConversationId { get; set; }
    }

    public class AgentChatResponse
    {
        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("conversationId")]
        public string ConversationId { get; set; }

        [JsonProperty("scriptsExecuted")]
        public List<AgentScriptResult> ScriptsExecuted { get; set; } = new();

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("completionTokens")]
        public int CompletionTokens { get; set; }

        [JsonProperty("iterations")]
        public int Iterations { get; set; }

        [JsonProperty("isPending")]
        public bool IsPending { get; set; }

        [JsonProperty("pendingRequestId")]
        public string PendingRequestId { get; set; }
    }

    public class AgentPendingChatResponse
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("conversationId")]
        public string ConversationId { get; set; }

        [JsonProperty("status")]
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;

        /// <summary>The user's original message for this turn. Carried here so a page that reloads
        /// mid-run can render the prompt+streaming-answer pair before the turn is persisted to the
        /// conversation file.</summary>
        [JsonProperty("userMessage")]
        public string UserMessage { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }

        /// <summary>Scripts run so far this turn, streamed live so the UI shows code as it executes
        /// (before the final response is ready).</summary>
        [JsonProperty("scriptsExecuted")]
        public List<AgentScriptResult> ScriptsExecuted { get; set; } = new();

        // ── Live transparency (streamed to the UI between polls) ──

        /// <summary>1-based index of the agent's current Think→Script→Observe iteration.</summary>
        [JsonProperty("iteration")]
        public int Iteration { get; set; }

        /// <summary>Coarse phase label: "thinking" | "running" | "observing" | "final".</summary>
        [JsonProperty("phase")]
        public string Phase { get; set; }

        /// <summary>Short human-readable note about what the agent is doing right now.</summary>
        [JsonProperty("statusNote")]
        public string StatusNote { get; set; }

        /// <summary>Running prompt-token total for the turn so far.</summary>
        [JsonProperty("promptTokens")]
        public int PromptTokens { get; set; }

        /// <summary>Running completion-token total for the turn so far.</summary>
        [JsonProperty("completionTokens")]
        public int CompletionTokens { get; set; }

        /// <summary>Append-only timeline of what the agent has done this turn (for the UI activity log).</summary>
        [JsonProperty("activity")]
        public List<AgentActivityEvent> Activity { get; set; } = new();

        /// <summary>Latest annotated screenshot from a computer-use action (base64 JPEG), streamed to the
        /// website so the page can render a live video of what the agent is doing on the host machine.</summary>
        [JsonProperty("latestFrame")]
        public string LatestFrame { get; set; }

        /// <summary>Set while a computer-use action is blocked awaiting Klive's approval (the website renders
        /// an inline approve/deny card with the target screenshot). Null when nothing is pending.</summary>
        [JsonProperty("pendingApproval")]
        public PendingApproval PendingApproval { get; set; }

        [JsonProperty("finalResponse")]
        public AgentChatResponse FinalResponse { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("completedAt")]
        public DateTime? CompletedAt { get; set; }

        /// <summary>Last moment the run made real progress (LLM reply, token, iteration, or script
        /// result). The stall watchdog cancels a run only when this stops advancing — so a slow but
        /// progressing task is never aborted, while a truly hung one is.</summary>
        [JsonIgnore]
        public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;

        /// <summary>Cancellation source for this run. A manual Stop or the stall watchdog cancels it,
        /// which unwinds the LLM HTTP call, the agent loop, and any running script.</summary>
        [JsonIgnore]
        public CancellationTokenSource CancellationSource { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }
    }

    /// <summary>A human-in-the-loop approval request for an irreversible computer-use action. Surfaced to
    /// the website (inline approve/deny card with the target screenshot) and resolved via the
    /// /kliveagent/chat/approve route. The action blocks until Status leaves "pending".</summary>
    public class PendingApproval
    {
        [JsonProperty("approvalId")]
        public string ApprovalId { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>Annotated screenshot of exactly what is about to happen (base64 JPEG), or null.</summary>
        [JsonProperty("frameBase64")]
        public string FrameBase64 { get; set; }

        /// <summary>"pending" | "approved" | "denied".</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = "pending";

        /// <summary>"approval" (irreversible-action approve/deny) or "intervention" (human takeover: captcha
        /// /login/2FA). The website renders an approve/deny card for the former and an "Open Remote Desktop"
        /// launcher for the latter.</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; } = "approval";

        /// <summary>For an intervention: the token-scoped remote-desktop solve URL the operator opens to take
        /// over. Null for a plain approval.</summary>
        [JsonProperty("solveUrl")]
        public string SolveUrl { get; set; }
    }

    /// <summary>One entry in a run's live activity timeline.</summary>
    public class AgentActivityEvent
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonProperty("iteration")]
        public int Iteration { get; set; }

        /// <summary>Kind of event: "think" | "script" | "tool" | "observe" | "final" | "error".</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }
    }

    /// <summary>Structured progress carrier the brain pushes to its host (replaces the old
    /// (text, scripts) callback) so the UI can show iteration, phase, running token counts and a
    /// per-step activity timeline — not just accumulated prose.</summary>
    public class AgentProgressUpdate
    {
        public string Text { get; set; }
        public List<AgentScriptResult> Scripts { get; set; }
        public int Iteration { get; set; }
        public string Phase { get; set; }
        public string StatusNote { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        /// <summary>A new activity event to append, if this update introduces one.</summary>
        public AgentActivityEvent NewActivity { get; set; }

        /// <summary>Latest annotated computer-use frame (JPEG bytes) for the website video stream, if any.</summary>
        public byte[] Frame { get; set; }

        /// <summary>A computer-use approval request/resolution to surface to the website, if any.</summary>
        public PendingApproval Approval { get; set; }
    }
}
