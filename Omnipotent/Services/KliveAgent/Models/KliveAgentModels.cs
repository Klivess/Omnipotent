using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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

        [JsonProperty("scriptResult")]
        public AgentScriptResult ScriptResult { get; set; }

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

    public class DiscordGuildInfo
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        public override string ToString() => $"{Name} ({Id})";
    }

    public class DiscordChannelInfo
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }

        [JsonProperty("guildId")]
        public ulong GuildId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("position")]
        public int Position { get; set; }

        public override string ToString() => $"#{Name} ({Id}) [{Type}]";
    }

    public class DiscordMessageInfo
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }

        [JsonProperty("channelId")]
        public ulong ChannelId { get; set; }

        [JsonProperty("authorId")]
        public ulong AuthorId { get; set; }

        [JsonProperty("authorName")]
        public string AuthorName { get; set; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;

        [JsonProperty("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        public override string ToString() => $"{AuthorName}: {Content}";
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
    }
}
