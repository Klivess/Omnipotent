using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>The receipt for a spoken turn handed to the agent.</summary>
internal sealed record TalkTurnReceipt(string? RequestId, bool Steering, bool Accepted, string? Error);

/// <summary>What a talk session needs from KliveAgent. Lets the session be exercised without a live agent.</summary>
internal interface ITalkBackend
{
    KliveAgentTalkHub Hub { get; }
    Task<TalkTurnReceipt> QueueTurnAsync(string conversationId, string text, string clientMessageId);
    bool CancelRun(string requestId);
    IReadOnlyList<AgentSteeringMessage>? GetSteering(string requestId);
    Task EnsureWarmAsync(string conversationId, ITalkListener listener, CancellationToken cancellationToken);
    Task<string?> PrepareContextAsync(string text);
    void RecordStage(string stage, long ms);
    void LogError(Exception ex, string message);
}

/// <summary>What a talk session needs from KliveLLM: speech in, speech out, and the slot reserve.</summary>
internal interface ITalkAudio
{
    Task<AIRouterTranscript> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancellationToken);
    Task<(byte[] Audio, string Format)> SynthesizeAsync(string text, string voice, double speed, string format, CancellationToken cancellationToken);
    Task<IReadOnlyList<AIRouterVoice>> ListVoicesAsync(CancellationToken cancellationToken);
    Task WarmAsync(CancellationToken cancellationToken);
    void EngageReserve(string ownerId, TimeSpan ttl);
    void ReleaseReserve(string ownerId);
}

/// <summary>The live KliveAgent behind a talk session.</summary>
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

    public void RecordStage(string stage, long ms)
    {
        try { agent.Stats?.RecordStageSample(stage, ms); } catch { }
    }

    public void LogError(Exception ex, string message) => _ = agent.ServiceLogError(ex, message, false);
}

/// <summary>AIRouter speech plus the KliveLLM slot reserve and connection warm-up.</summary>
internal sealed class KliveLLMTalkAudio : ITalkAudio
{
    private readonly KliveLLM.KliveLLM llm;

    internal KliveLLMTalkAudio(KliveLLM.KliveLLM llm) => this.llm = llm;

    public Task<AIRouterTranscript> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancellationToken) =>
        llm.Audio.TranscribeAsync(wav, "speech.wav", "audio/wav", language, prompt, cancellationToken);

    public Task<(byte[] Audio, string Format)> SynthesizeAsync(string text, string voice, double speed, string format,
        CancellationToken cancellationToken) =>
        llm.Audio.SynthesizeAsync(text, voice, speed, format, cancellationToken);

    public Task<IReadOnlyList<AIRouterVoice>> ListVoicesAsync(CancellationToken cancellationToken) =>
        llm.Audio.ListVoicesAsync(cancellationToken);

    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        _ = llm.WarmUpConnectionAsync(cancellationToken);
        await llm.Audio.WarmAsync(cancellationToken);
    }

    public void EngageReserve(string ownerId, TimeSpan ttl) => llm.EngageInteractiveReserve(ownerId, ttl);

    public void ReleaseReserve(string ownerId) => llm.ReleaseInteractiveReserve(ownerId);
}
