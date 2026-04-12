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
}
