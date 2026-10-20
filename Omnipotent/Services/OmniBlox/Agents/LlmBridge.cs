using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using KliveLlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Services.OmniBlox.Agents
{
    public sealed class ToolDefinition
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        /// <summary>JSON schema of the arguments object.</summary>
        public required JObject Parameters { get; init; }
    }

    public sealed class LlmToolCall
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string ArgumentsJson { get; init; } = "{}";
    }

    public sealed class LlmTurn
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string Text { get; init; } = "";
        public List<LlmToolCall> ToolCalls { get; init; } = new();
        public long PromptTokens { get; init; }
        public long CachedTokens { get; init; }
        public long CompletionTokens { get; init; }
        public string? Model { get; init; }
    }

    public sealed class LlmCallOptions
    {
        public int MaxTokens { get; init; } = 4096;
        public string? Thinking { get; init; } = "medium";
        public AIRouterWorkClass WorkClass { get; init; } = AIRouterWorkClass.Agent;
    }

    /// <summary>
    /// The only door OmniBlox uses to reach a language model. The live implementation is KliveLLM's tool
    /// sessions; tests substitute a scripted fake.
    /// </summary>
    public interface IOmniBloxLlm
    {
        /// <summary>False (with the reason) unless KliveLLM is up AND the active provider is flat-fee AIRouter.</summary>
        Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct);

        /// <summary>True when the shared AIRouter queue is idle enough for background work (the asset labeller).</summary>
        bool HasSpareCapacity();

        void StartSession(string sessionId, string systemPrompt);
        void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null);
        void AppendToolResult(string sessionId, string toolCallId, string toolName, string content);
        Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct);
        void EndSession(string sessionId);
    }

    /// <summary>KliveLLM-backed bridge. History is append-only (preservePromptPrefix) so AIRouter's prefix cache keeps matching.</summary>
    public sealed class KliveLlmBridge : IOmniBloxLlm
    {
        private readonly Func<KliveLlmService?> resolve;

        public KliveLlmBridge(Func<KliveLlmService?> resolve) => this.resolve = resolve;

        public async Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct)
        {
            var llm = resolve();
            if (llm == null || !llm.IsServiceActive()) return (false, "KliveLLM is not running.");
            bool flatFee;
            try { flatFee = await llm.IsFlatFeeProviderAsync(); }
            catch (Exception ex) { return (false, "Could not read the active model provider: " + ex.Message); }
            if (!flatFee)
                return (false, "OmniBlox only runs on AIRouter (flat fee), and RemoteLLMProvider is set to something else. It never spends money on a per-token provider.");
            return (true, null);
        }

        public bool HasSpareCapacity()
        {
            var llm = resolve();
            if (llm == null) return false;
            var s = llm.FairUse.Describe();
            return s.Waiting == 0 && s.InFlight <= 1 && s.PenaltyRemaining <= TimeSpan.Zero;
        }

        private KliveLlmService Llm => resolve() ?? throw new InvalidOperationException("KliveLLM is not running.");

        public void StartSession(string sessionId, string systemPrompt) => Llm.StartToolSession(sessionId, systemPrompt);

        public void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null)
        {
            if (pngImages is { Count: > 0 })
                Llm.AppendUserContentToToolSession(sessionId, text, pngImages.Select(b => (b, "image/png")).ToList(),
                    keepRecentImages: Math.Max(1, pngImages.Count), preservePromptPrefix: true);
            else
                Llm.AppendUserMessageToToolSession(sessionId, text);
        }

        public void AppendToolResult(string sessionId, string toolCallId, string toolName, string content) =>
            Llm.AppendToolResult(sessionId, toolCallId, toolName, content, preservePromptPrefix: true);

        public async Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
        {
            var hfTools = tools.Count == 0 ? null! : tools.Select(t => new HFWrapper.HFTool
            {
                function = new HFWrapper.HFFunctionDefinition { name = t.Name, description = t.Description, parameters = t.Parameters },
            }).ToList();
            var response = await Llm.QueryToolSessionAsync(sessionId, hfTools,
                maxTokensOverride: options.MaxTokens,
                cancellationToken: ct,
                thinkingOverride: options.Thinking,
                // Cycles are short and history is append-only; the runner enforces its own budget, so the
                // generic compactor (which rewrites already-sent history) stays off.
                compactAboveTokensOverride: 0,
                workClass: options.WorkClass);
            return new LlmTurn
            {
                Success = response.Success,
                Error = response.Success ? null : response.ErrorMessage,
                Text = response.Response ?? "",
                ToolCalls = (response.ToolCalls ?? new List<HFWrapper.HFToolCall>())
                    .Select(c => new LlmToolCall { Id = c.id ?? "", Name = c.function?.name ?? "", ArgumentsJson = c.function?.arguments ?? "{}" })
                    .ToList(),
                PromptTokens = response.PromptTokens,
                CachedTokens = response.CachedPromptTokens,
                CompletionTokens = response.CompletionTokens,
                Model = response.Model,
            };
        }

        public void EndSession(string sessionId)
        {
            try { resolve()?.ResetSession(sessionId); } catch { }
        }
    }
}
