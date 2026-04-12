using Newtonsoft.Json;
using Omnipotent.Logging;

namespace Omnipotent.Services.KliveAgent
{
    public enum KliveAgentMemoryType
    {
        Note,
        Preference,
        Event,
        Script,
        Insight
    }

    public sealed class KliveAgentMemoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public KliveAgentMemoryType Type { get; set; } = KliveAgentMemoryType.Note;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Source { get; set; } = "system";
        public double Importance { get; set; } = 0.5;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class KliveAgentMemorySearchResult
    {
        public KliveAgentMemoryRecord Memory { get; set; } = new();
        public double Score { get; set; }
    }

    public sealed class KliveAgentObservedEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
        public string ServiceName { get; set; } = string.Empty;
        public OmniLogging.LogType LogType { get; set; } = OmniLogging.LogType.Status;
        public string Message { get; set; } = string.Empty;
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }

        [JsonIgnore]
        public string Fingerprint => $"{ServiceName}|{LogType}|{Message}";

        public static KliveAgentObservedEvent FromLoggedMessage(OmniLogging.LoggedMessage message)
        {
            return new KliveAgentObservedEvent
            {
                ServiceName = message.serviceName ?? string.Empty,
                LogType = message.type,
                Message = message.message ?? string.Empty,
                ExceptionType = message.errorInfo?.ExceptionType,
                ExceptionMessage = message.errorInfo?.Message,
                OccurredAtUtc = message.TimeOfLog == default ? DateTime.UtcNow : message.TimeOfLog.ToUniversalTime()
            };
        }
    }

    public sealed class KliveAgentEventRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string ServiceNameContains { get; set; } = string.Empty;
        public List<string> MessageContainsAny { get; set; } = new();
        public OmniLogging.LogType MinimumLogType { get; set; } = OmniLogging.LogType.Error;
        public bool NotifyKlives { get; set; } = true;
        public string? ScriptCode { get; set; }
        public int CooldownSeconds { get; set; } = 120;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastTriggeredAtUtc { get; set; }

        public bool Matches(KliveAgentObservedEvent ev)
        {
            if (!Enabled)
            {
                return false;
            }

            if (ev.LogType < MinimumLogType)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ServiceNameContains) &&
                ev.ServiceName.Contains(ServiceNameContains, StringComparison.OrdinalIgnoreCase) == false)
            {
                return false;
            }

            if (MessageContainsAny == null || MessageContainsAny.Count == 0)
            {
                return true;
            }

            foreach (var token in MessageContainsAny)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (ev.Message.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    (ev.ExceptionMessage?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (ev.ExceptionType?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class KliveAgentScriptRunRecord
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");
        public string Trigger { get; set; } = "manual";
        public string ScriptCode { get; set; } = string.Empty;
        public string Status { get; set; } = "queued";
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAtUtc { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    public sealed class KliveAgentSaveMemoryRequest
    {
        public string Type { get; set; } = "Note";
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Source { get; set; } = "api";
        public double Importance { get; set; } = 0.6;
    }

    public sealed class KliveAgentUpsertRuleRequest
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string ServiceNameContains { get; set; } = string.Empty;
        public List<string> MessageContainsAny { get; set; } = new();
        public OmniLogging.LogType MinimumLogType { get; set; } = OmniLogging.LogType.Error;
        public bool NotifyKlives { get; set; } = true;
        public string? ScriptCode { get; set; }
        public int CooldownSeconds { get; set; } = 120;
    }

    public sealed class KliveAgentRunScriptRequest
    {
        public string ScriptCode { get; set; } = string.Empty;
        public string Trigger { get; set; } = "manual";
    }

    public sealed class KliveAgentBrainExecutionRequest
    {
        public string Goal { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public bool AllowScriptExecution { get; set; } = true;
        public bool NotifyKlivesOnCompletion { get; set; } = false;

        [JsonIgnore]
        public string RequestingProfileScope { get; set; } = string.Empty;
    }

    public sealed class KliveAgentBrainContextSnapshot
    {
        public string RequestingProfileScope { get; set; } = string.Empty;
        public string Goal { get; set; } = string.Empty;
        public string UserContext { get; set; } = string.Empty;
        public string PromptUsed { get; set; } = string.Empty;
        public List<string> MemoryEntries { get; set; } = new();
        public List<string> RecentEventEntries { get; set; } = new();
        public List<string> MatchedRuleEntries { get; set; } = new();
    }

    public sealed class KliveAgentBrainAction
    {
        public string ActionType { get; set; } = "none";
        public string Reason { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ScriptCode { get; set; } = string.Empty;
        public string MemoryType { get; set; } = "Note";
        public string MemoryTitle { get; set; } = string.Empty;
        public string MemoryContent { get; set; } = string.Empty;
        public List<string> MemoryTags { get; set; } = new();
        public double MemoryImportance { get; set; } = 0.7;
    }

    public sealed class KliveAgentBrainDecisionEnvelope
    {
        public string Summary { get; set; } = string.Empty;
        public bool ShouldAct { get; set; } = false;
        public double Confidence { get; set; } = 0.0;
        public string FinalResponse { get; set; } = string.Empty;
        public List<KliveAgentBrainAction> Actions { get; set; } = new();
    }

    public sealed class KliveAgentBrainActionResult
    {
        public string ActionType { get; set; } = "none";
        public string Status { get; set; } = "skipped";
        public string Details { get; set; } = string.Empty;
        public string? ScriptRunId { get; set; }
    }

    public sealed class KliveAgentBrainExecutionResult
    {
        public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
        public string MissionType { get; set; } = "manual-task";
        public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime CompletedAtUtc { get; set; }
        public string LlmSessionId { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string FinalResponse { get; set; } = string.Empty;
        public string RawModelOutput { get; set; } = string.Empty;
        public int ApproxOutputTokens { get; set; } = 0;
        public bool UsedFallback { get; set; } = false;
        public KliveAgentBrainContextSnapshot ContextUsed { get; set; } = new();
        public KliveAgentBrainDecisionEnvelope Decision { get; set; } = new();
        public List<KliveAgentBrainActionResult> ActionResults { get; set; } = new();
    }
}
