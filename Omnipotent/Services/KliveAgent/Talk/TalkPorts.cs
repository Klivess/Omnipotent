using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>The receipt for a spoken turn handed to the agent.</summary>
internal sealed record TalkTurnReceipt(string? RequestId, bool Steering, bool Accepted, string? Error);

/// <summary>What a voice session needs from KliveAgent. Lets the session be exercised without a live agent.</summary>
internal interface ITalkBackend
{
    KliveAgentTalkHub Hub { get; }
    Task<TalkTurnReceipt> QueueTurnAsync(string conversationId, string text, string clientMessageId);
    bool CancelRun(string requestId);
    IReadOnlyList<AgentSteeringMessage>? GetSteering(string requestId);
    Task EnsureWarmAsync(string conversationId, ITalkListener listener, CancellationToken cancellationToken);
    Task<string?> PrepareContextAsync(string text);
    /// <summary>The conversation's messages, oldest first, for the voice to start with.</summary>
    IReadOnlyList<(bool User, string Text)> RecentHistory(string conversationId, int max);
    void RecordStage(string stage, long ms);
    void LogError(Exception ex, string message);
}

/// <summary>What a voice session needs from KliveLLM: GPT-Live on the OpenAI key, the AIRouter slot
/// reserve for the agent behind it, and the ledger.</summary>
internal interface ITalkVoice
{
    /// <summary>Null when GPT-Live can be used; otherwise why not (the OpenAI router is switched off).</summary>
    string? Unavailable { get; }
    Task<OpenAILiveCreated> CreateSessionAsync(JObject session, string offerSdp, CancellationToken cancellationToken);
    Task<IOpenAILiveSideband> AttachAsync(string sessionId, CancellationToken cancellationToken);
    Task WarmAsync(CancellationToken cancellationToken);
    void EngageReserve(string ownerId, TimeSpan ttl);
    void ReleaseReserve(string ownerId);
    void RecordSession(string sessionId, string model, DateTimeOffset started, DateTimeOffset ended,
        OpenAILiveUsage? usage, bool usageFinal, double activePerMinute, double standbyPerMinute, string outcome, string? error);
}

/// <summary>The live KliveAgent behind a voice session.</summary>
internal sealed class KliveAgentTalkBackend : ITalkBackend
{
    private readonly KliveAgent agent;

    internal KliveAgentTalkBackend(KliveAgent agent) => this.agent = agent;

    public KliveAgentTalkHub Hub => agent.Talk;

    public async Task<TalkTurnReceipt> QueueTurnAsync(string conversationId, string text, string clientMessageId)
    {
        var response = await agent.QueueIncomingApiMessageAsync(text, conversationId, "Klives", clientMessageId);
        bool accepted = response.Success || response.IsPending;
        return new TalkTurnReceipt(response.PendingRequestId, response.WasSteering, accepted,
            accepted ? null : response.ErrorMessage ?? response.Response);
    }

    public bool CancelRun(string requestId) => agent.CancelPendingApiResponse(requestId);

    public IReadOnlyList<AgentSteeringMessage>? GetSteering(string requestId) =>
        agent.GetPendingApiResponse(requestId)?.SteeringMessages;

    public Task EnsureWarmAsync(string conversationId, ITalkListener listener, CancellationToken cancellationToken) =>
        agent.EnsureTalkSessionAsync(conversationId, listener, cancellationToken);

    public Task<string?> PrepareContextAsync(string text) => agent.PrepareTalkTurnContextAsync(text);

    public IReadOnlyList<(bool User, string Text)> RecentHistory(string conversationId, int max) =>
        agent.RecentTalkHistory(conversationId, max);

    public void RecordStage(string stage, long ms)
    {
        try { agent.Stats?.RecordStageSample(stage, ms); } catch { }
    }

    public void LogError(Exception ex, string message) => _ = agent.ServiceLogError(ex, message, false);
}

/// <summary>GPT-Live through KliveLLM's OpenAI router, plus the AIRouter slot reserve and connection warm-up
/// for the agent that answers.</summary>
internal sealed class KliveLLMTalkVoice : ITalkVoice
{
    private readonly KliveLLM.KliveLLM llm;

    internal KliveLLMTalkVoice(KliveLLM.KliveLLM llm) => this.llm = llm;

    public string? Unavailable => llm.OpenAIEnabled ? null : "The OpenAI router is switched off on the KliveLLM page.";

    public Task<OpenAILiveCreated> CreateSessionAsync(JObject session, string offerSdp, CancellationToken cancellationToken) =>
        llm.OpenAILive.CreateWebRtcSessionAsync(session, offerSdp, cancellationToken);

    public async Task<IOpenAILiveSideband> AttachAsync(string sessionId, CancellationToken cancellationToken) =>
        await llm.OpenAILive.AttachAsync(sessionId, cancellationToken);

    public Task WarmAsync(CancellationToken cancellationToken) =>
        llm.WarmUpConnectionAsync(cancellationToken, LlmConsumerCatalog.Callers.KliveAgentTalk);

    public void EngageReserve(string ownerId, TimeSpan ttl) => llm.EngageInteractiveReserve(ownerId, ttl);

    public void ReleaseReserve(string ownerId) => llm.ReleaseInteractiveReserve(ownerId);

    public void RecordSession(string sessionId, string model, DateTimeOffset started, DateTimeOffset ended,
        OpenAILiveUsage? usage, bool usageFinal, double activePerMinute, double standbyPerMinute, string outcome, string? error) =>
        llm.RecordLiveSession(sessionId, model, started, ended, usage, usageFinal, activePerMinute, standbyPerMinute, outcome, error);
}
