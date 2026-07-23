using Omnipotent.Services.KliveLLM;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>One canonical, secret-safe representation of remote inference failures. Timeline,
    /// checkpoint, blocker, and logs therefore retain the same provider/model/status/request facts
    /// instead of reducing an actionable 4xx response to a bare enum.</summary>
    internal static partial class ProjectProviderFailure
    {
        public const string DependencyKey = "llm-provider";

        /// <summary>
        /// Projects never permanently stop because an autonomous model route is unavailable or
        /// misconfigured. Even failures providers classify as non-retryable get a bounded
        /// automatic retry window, allowing a changed route, restored credit, or repaired
        /// configuration to recover without an agent having to unblock the project.
        /// </summary>
        public static DateTime AutomaticRetryAt(RemoteLLMException ex, DateTime? nowUtc = null)
        {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            TimeSpan delay = ex.RetryAfter is { } requested && requested > TimeSpan.Zero
                ? requested : TimeSpan.FromMinutes(15);
            return now + delay;
        }

        public static ProjectExecutionFailure ToExecutionFailure(RemoteLLMException ex, string wakeID) => new()
        {
            Category = ex.Kind switch
            {
                RemoteLLMFailureKind.RateLimited => ProjectFailureCategory.RateLimited,
                RemoteLLMFailureKind.InsufficientProviderCredit => ProjectFailureCategory.Capacity,
                RemoteLLMFailureKind.Authentication => ProjectFailureCategory.Authentication,
                RemoteLLMFailureKind.InvalidRequest or RemoteLLMFailureKind.ModelUnavailable => ProjectFailureCategory.Configuration,
                RemoteLLMFailureKind.EmptyResponse or RemoteLLMFailureKind.Network
                    or RemoteLLMFailureKind.Timeout or RemoteLLMFailureKind.ProviderUnavailable => ProjectFailureCategory.Transient,
                _ => ProjectFailureCategory.Unknown,
            },
            Code = ex.Kind.ToString(),
            Summary = Describe(ex, 1_500),
            Retryable = ex.IsRetryable,
            RetryAt = ex.RetryAfter is { } retry && retry > TimeSpan.Zero ? DateTime.UtcNow + retry : null,
            WakeID = wakeID,
            Provider = Safe(ex.Provider, 120),
            Model = Safe(ex.Model, 240),
            HttpStatus = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
            RequestedMaxTokens = ex.RequestedMaxTokens,
            AffordableMaxTokens = ex.AffordableMaxTokens,
        };

        public static string Describe(RemoteLLMException ex, int max = 1_500)
        {
            string facts = $"{ex.Kind}; provider={Safe(ex.Provider, 120)}; model={Safe(ex.Model, 240)}; " +
                $"http={(ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : "n/a")}; " +
                $"requestedMaxTokens={ex.RequestedMaxTokens?.ToString() ?? "n/a"}; " +
                $"affordableMaxTokens={ex.AffordableMaxTokens?.ToString() ?? "n/a"}; " +
                $"retryAfter={ex.RetryAfter?.ToString() ?? "n/a"}; detail={Safe(ex.Message, max)}";
            return facts.Length <= max ? facts : facts[..Math.Max(0, max - 1)] + "…";
        }

        public static string ToPayloadJson(RemoteLLMException ex) => JsonSerializer.Serialize(new
        {
            kind = ex.Kind.ToString(),
            provider = Safe(ex.Provider, 120),
            model = Safe(ex.Model, 240),
            httpStatus = ex.StatusCode.HasValue ? (int?)ex.StatusCode.Value : null,
            requestedMaxTokens = ex.RequestedMaxTokens,
            affordableMaxTokens = ex.AffordableMaxTokens,
            retryAfterSeconds = ex.RetryAfter?.TotalSeconds,
            retryable = ex.IsRetryable,
            detail = Safe(ex.Message, 2_000),
        });

        public static string SafeDetail(string? text, int max = 1_500) => Safe(text, max);

        /// <summary>Some legacy/local KliveLLM paths return Success=false instead of throwing the
        /// structured remote exception used by HTTP providers. Convert that result at the Projects
        /// boundary so the timeline never collapses back to an opaque "InvalidRequest" or generic
        /// Exception. Unknown unsuccessful responses are treated as retryable provider availability
        /// failures; explicit configuration/auth/request failures remain action-required.</summary>
        public static RemoteLLMException FromUnsuccessfulResponse(string? detail, string? model,
            int? requestedMaxTokens = null)
        {
            string message = Safe(detail, 2_000);
            string lower = message.ToLowerInvariant();
            RemoteLLMFailureKind kind = lower switch
            {
                _ when lower.Contains("402", StringComparison.Ordinal)
                    || lower.Contains("payment required", StringComparison.Ordinal)
                    || lower.Contains("insufficient credit", StringComparison.Ordinal)
                    || lower.Contains("can afford", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.InsufficientProviderCredit,
                _ when lower.Contains("429", StringComparison.Ordinal)
                    || lower.Contains("rate limit", StringComparison.Ordinal)
                    || lower.Contains("throttl", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.RateLimited,
                _ when lower.Contains("401", StringComparison.Ordinal)
                    || lower.Contains("403", StringComparison.Ordinal)
                    || lower.Contains("unauthor", StringComparison.Ordinal)
                    || lower.Contains("authentication", StringComparison.Ordinal)
                    || lower.Contains("api key", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.Authentication,
                _ when lower.Contains("empty response", StringComparison.Ordinal)
                    || lower.Contains("empty completion", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.EmptyResponse,
                _ when lower.Contains("timeout", StringComparison.Ordinal)
                    || lower.Contains("timed out", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.Timeout,
                _ when lower.Contains("model not available", StringComparison.Ordinal)
                    || lower.Contains("model unavailable", StringComparison.Ordinal)
                    || lower.Contains("model not found", StringComparison.Ordinal)
                    || lower.Contains("local model not available", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.ModelUnavailable,
                _ when lower.Contains("400", StringComparison.Ordinal)
                    || lower.Contains("bad request", StringComparison.Ordinal)
                    || lower.Contains("invalid request", StringComparison.Ordinal)
                    || lower.Contains("tool session not found", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.InvalidRequest,
                _ when lower.Contains("network", StringComparison.Ordinal)
                    || lower.Contains("dns", StringComparison.Ordinal)
                    || lower.Contains("connection", StringComparison.Ordinal)
                    => RemoteLLMFailureKind.Network,
                _ => RemoteLLMFailureKind.ProviderUnavailable,
            };
            HttpStatusCode? status = null;
            var statusMatch = Regex.Match(lower, @"(?<!\d)(?<status>[45]\d{2})(?!\d)",
                RegexOptions.CultureInvariant);
            if (statusMatch.Success && int.TryParse(statusMatch.Groups["status"].Value, out int parsed))
                status = (HttpStatusCode)parsed;
            string provider = lower.Contains("openrouter", StringComparison.Ordinal) ? "OpenRouter"
                : lower.Contains("agentrouter", StringComparison.Ordinal) ? "AgentRouter"
                : lower.Contains("huggingface", StringComparison.Ordinal) ? "HuggingFace"
                : "KliveLLM";
            return new RemoteLLMException(kind,
                message.Length == 0 ? "KliveLLM returned Success=false without an error detail." : message,
                provider, Safe(model, 240), status, requestedMaxTokens);
        }

        private static string Safe(string? text, int max)
        {
            string value = text ?? "";
            value = BearerPattern().Replace(value, "Bearer [REDACTED]");
            value = SecretAssignmentPattern().Replace(value, "$1=[REDACTED]");
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
        }

        [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+\-/=]+", RegexOptions.CultureInvariant)]
        private static partial Regex BearerPattern();

        [GeneratedRegex(@"(?i)\b(api[_-]?key|token|authorization)\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
        private static partial Regex SecretAssignmentPattern();
    }
}
