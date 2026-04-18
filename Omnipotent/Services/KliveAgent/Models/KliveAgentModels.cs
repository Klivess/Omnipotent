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
    }
}
