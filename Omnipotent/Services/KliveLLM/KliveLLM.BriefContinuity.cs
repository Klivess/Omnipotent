namespace Omnipotent.Services.KliveLLM;

public partial class KliveLLM
{
    public sealed record BriefSessionStart(bool Continued, string Reason, int AppendedTokens, int FullBriefTokens);

    // A retained transcript costs attention and cached-input money too. Do not keep growing it
    // merely to inflate a hit percentage, or resend it cold after a long idle period.
    internal const int MaxBriefSessionTokens = 96_000;
    internal const int MaxRetainedHistoryTokens = 24_000;
    internal static readonly TimeSpan BriefSessionIdleLimit = TimeSpan.FromMinutes(10);

    internal async Task<string> GetBriefProviderKeyAsync()
    {
        var provider = await GetRemoteProviderConfigurationAsync();
        string value = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            provider.Provider, provider.ChatCompletionsEndpoint, provider.Model, provider.ServiceTier, thinkingType,
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    public BriefSessionStart StartOrContinueBriefedToolSession(string sessionId, string systemPrompt,
        IReadOnlyList<ToolSessionBriefSection> sections, string compatibilityKey, int messageBudget,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        if (sections.Select(section => section.Key).Distinct(StringComparer.Ordinal).Count() != sections.Count)
            throw new ArgumentException("Brief section keys must be unique.", nameof(sections));
        DateTime now = nowUtc ?? DateTime.UtcNow;
        string system = systemPrompt + ThinkingDirective;
        string full = ToolSessionBriefState.FullText(sections);
        int fullTokens = (full.Length + 3) / 4;
        int ceiling = Math.Min(Math.Max(1, messageBudget), MaxBriefSessionTokens);
        lock (sessions)
        {
            string reason = "cold-start";
            sessions.TryGetValue(sessionId, out var session);
            var state = session?.briefState;
            if (session != null && state != null)
            {
                string delta = state.Delta(sections);
                // Reserve headroom for the next response/tool batch. The caller additionally reserves
                // tools and completion tokens using the actual route's context window.
                long projected = Math.Max(EstimateToolSessionTokens(session.structuredMessages),
                    (long)session.lastPromptTokens + session.lastCompletionTokens) + (delta.Length + 3) / 4;
                long retainedHistory = EstimateToolSessionTokens(session.structuredMessages)
                    - fullTokens - (system.Length + 3) / 4;
                if (!string.Equals(state.CompatibilityKey, compatibilityKey, StringComparison.Ordinal)
                    || !string.Equals(HFWrapper.ContentToText(session.structuredMessages.FirstOrDefault()?.content), system, StringComparison.Ordinal))
                    reason = "configuration-changed";
                else if (now - session.lastUpdated > BriefSessionIdleLimit)
                    reason = "idle-expired";
                else if (!HasCompleteToolExchanges(session.structuredMessages))
                    reason = "incomplete-tool-batch";
                else if (projected > ceiling * 0.85 || session.structuredMessages.Count > 1500)
                    reason = "context-limit";
                else if (retainedHistory > MaxRetainedHistoryTokens)
                    reason = "history-cost-limit";
                else
                {
                    var message = new HFWrapper.HFMessage { role = "user", content = delta };
                    session.structuredMessages.Add(message);
                    state.Remember(sections, message);
                    return new(true, "continued", (delta.Length + 3) / 4, fullTokens);
                }
            }

            // Only remove expired brief sessions. Ordinary chat sessions and active tool loops have
            // their own lifecycle. A generous grace period avoids reclaiming a long-running tool.
            foreach (var stale in sessions.Where(pair => pair.Key != sessionId && pair.Value.briefState != null
                && now - pair.Value.lastUpdated > TimeSpan.FromHours(2)
                && pair.Value.structuredMessages.LastOrDefault() is { role: "assistant", tool_calls: null or { Count: 0 } })
                .Select(pair => pair.Key).ToArray())
                sessions.Remove(stale);

            StartToolSession(sessionId, systemPrompt);
            session = sessions[sessionId];
            session.lastUpdated = now;
            var seed = new HFWrapper.HFMessage { role = "user", content = full };
            session.structuredMessages.Add(seed);
            session.briefState = new ToolSessionBriefState { CompatibilityKey = compatibilityKey };
            session.briefState.Remember(sections, seed);
            return new(false, reason, fullTokens, fullTokens);
        }
    }

    /// <summary>A completed batch may end in tool results, not an assistant final. Reject the entire
    /// session if any call is unmatched, duplicated or interleaved with a new user/assistant turn.</summary>
    internal static bool HasCompleteToolExchanges(IReadOnlyList<HFWrapper.HFMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.role == "tool")
            {
                if (string.IsNullOrEmpty(message.tool_call_id) || !pending.Remove(message.tool_call_id)) return false;
                continue;
            }
            if (pending.Count != 0) return false;
            if (message.role != "assistant") continue;
            foreach (var call in message.tool_calls ?? new())
                if (string.IsNullOrEmpty(call.id) || !pending.Add(call.id)) return false;
        }
        return pending.Count == 0;
    }

    // Compaction is an intentional cache reset. Materialise CURRENT state before summarising,
    // otherwise the old protected seed survives while later directive/approval updates are lost.
    internal static void MaterializeBriefForCompaction(KliveLLMSession session)
    {
        var state = session.briefState;
        if (state == null) return;
        session.structuredMessages.RemoveAll(message => state.BriefMessages.Contains(message));
        int insertAt = session.structuredMessages.TakeWhile(message => message.role == "system").Count();
        var seed = new HFWrapper.HFMessage { role = "user", content = ToolSessionBriefState.FullText(state.Sections) };
        session.structuredMessages.Insert(insertAt, seed);
        state.BriefMessages.Clear();
        state.JournalEntries.Clear();
        state.Remember(state.Sections, seed);
    }
}
