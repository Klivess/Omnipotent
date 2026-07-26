using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.Projects;

public class ProjectContextWindowPolicyTests
{
    [Fact]
    public void Policy_ReservesOutputAndRollsWorkSliceBeforeModelLimit()
    {
        var limits = new OpenRouterRouteContextLimits(
            ContextWindowTokens: 32_768,
            MaxCompletionTokens: 4_096,
            AllRoutesResolved: true,
            Routes: Array.Empty<OpenRouterModelContextLimit>());

        var policy = ProjectsContextBudget.CreateContextWindowPolicy(
            limits,
            requestedMaxOutputTokens: 8_192,
            configuredWorkSliceTokenBudget: 180_000);

        Assert.Equal(32_768, policy.ContextWindowTokens);
        Assert.Equal(4_096, policy.MaxOutputTokens);
        Assert.Equal(26_214, policy.WorkSliceTokenBudget);
        Assert.InRange(policy.CompactionTriggerTokens, 1, 32_767);
        Assert.True(policy.CatalogComplete);
    }

    [Fact]
    public void Policy_PreservesSmallerConfiguredWorkSlice()
    {
        var limits = new OpenRouterRouteContextLimits(
            128_000,
            16_000,
            true,
            Array.Empty<OpenRouterModelContextLimit>());

        var policy = ProjectsContextBudget.CreateContextWindowPolicy(
            limits,
            requestedMaxOutputTokens: 8_000,
            configuredWorkSliceTokenBudget: 20_000);

        Assert.Equal(20_000, policy.WorkSliceTokenBudget);
        Assert.Equal(8_000, policy.MaxOutputTokens);
    }

    [Fact]
    public void ToolSessionBudget_AccountsForOutputToolsAndSafetyMargin()
    {
        var tools = new List<HFWrapper.HFTool>
        {
            new()
            {
                function = new HFWrapper.HFFunctionDefinition
                {
                    name = "large_tool",
                    description = new string('x', 3_000),
                    parameters = new { type = "object", properties = new { value = new { type = "string" } } }
                }
            }
        };

        int withoutTools = LlmService.CalculateToolSessionMessageBudget(32_768, 4_096, Array.Empty<HFWrapper.HFTool>());
        int withTools = LlmService.CalculateToolSessionMessageBudget(32_768, 4_096, tools);

        Assert.True(withTools < withoutTools);
        Assert.True(withTools + 4_096 + LlmService.EstimateToolDefinitionTokens(tools) < 32_768);
    }

    [Fact]
    public void Compactor_FitsOversizedWakeSeedAndPreservesBothEnds()
    {
        using var http = new HttpClient(new NoopHandler());
        var llm = new LlmService(http);
        var session = new LlmService.KliveLLMSession(llm, false)
        {
            structuredMessages =
            {
                new() { role = "system", content = "fixed system rules" },
                new()
                {
                    role = "user",
                    content = "BEGIN-CRITICAL\n" + new string('x', 30_000) + "\nLATEST-TRIGGER"
                }
            }
        };

        bool compacted = LlmService.CompactToolSessionIfNeeded(session, aboveTokens: 1_500, keepRecent: 28);

        Assert.True(compacted);
        Assert.Equal("fixed system rules", session.structuredMessages[0].content);
        string fitted = Assert.IsType<string>(session.structuredMessages[1].content);
        Assert.Contains("BEGIN-CRITICAL", fitted);
        Assert.Contains("LATEST-TRIGGER", fitted);
        Assert.True(LlmService.EstimateToolSessionTokens(session.structuredMessages) <= 1_500);
    }

    [Fact]
    public void Compactor_KeepsRecentAssistantToolResultPairIntact()
    {
        using var http = new HttpClient(new NoopHandler());
        var llm = new LlmService(http);
        var session = new LlmService.KliveLLMSession(llm, false);
        session.structuredMessages.Add(new() { role = "system", content = "rules" });
        session.structuredMessages.Add(new() { role = "user", content = new string('a', 8_000) });
        session.structuredMessages.Add(AssistantToolCall("old-call", "old_tool"));
        session.structuredMessages.Add(new()
        {
            role = "tool",
            tool_call_id = "old-call",
            name = "old_tool",
            content = new string('b', 8_000)
        });
        session.structuredMessages.Add(AssistantToolCall("recent-call", "recent_tool"));
        session.structuredMessages.Add(new()
        {
            role = "tool",
            tool_call_id = "recent-call",
            name = "recent_tool",
            content = new string('c', 8_000)
        });

        bool compacted = LlmService.CompactToolSessionIfNeeded(
            session,
            aboveTokens: 1_800,
            keepRecent: 5);

        Assert.True(compacted);
        int recentAssistant = session.structuredMessages.FindIndex(m =>
            m.role == "assistant" && m.tool_calls?.Any(tc => tc.id == "recent-call") == true);
        int recentResult = session.structuredMessages.FindIndex(m =>
            m.role == "tool" && m.tool_call_id == "recent-call");
        Assert.True(recentAssistant >= 0);
        Assert.Equal(recentAssistant + 1, recentResult);
        Assert.NotEqual("tool", session.structuredMessages[1].role);
        Assert.True(LlmService.EstimateToolSessionTokens(session.structuredMessages) <= 1_800);
    }

    [Fact]
    public void OpenRouterCompressionPlugin_IsProviderGated()
    {
        var openRouterPayload = new HFWrapper.HFLLMInferenceRequest();
        LlmService.ApplyContextCompression(
            ref openRouterPayload,
            Provider(LlmService.LLMProvider.OpenRouter),
            enabled: true);
        Assert.Equal("context-compression", Assert.Single(openRouterPayload.plugins).id);
        Assert.True(openRouterPayload.plugins[0].enabled);

        var customPayload = new HFWrapper.HFLLMInferenceRequest();
        LlmService.ApplyContextCompression(
            ref customPayload,
            Provider(LlmService.LLMProvider.CustomOpenAI),
            enabled: true);
        Assert.Null(customPayload.plugins);
    }

    private static LlmService.RemoteLLMProviderConfiguration Provider(LlmService.LLMProvider provider) =>
        new(provider, provider.ToString(), "https://provider.test/v1/chat/completions", "token", "vendor/model");

    private static HFWrapper.HFMessage AssistantToolCall(string id, string name) =>
        new()
        {
            role = "assistant",
            content = "",
            tool_calls =
            [
                new HFWrapper.HFToolCall
                {
                    id = id,
                    type = "function",
                    function = new HFWrapper.HFFunctionCall
                    {
                        name = name,
                        arguments = "{}"
                    }
                }
            ]
        };

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
