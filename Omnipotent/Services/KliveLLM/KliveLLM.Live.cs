using Omnipotent.Service_Manager;

namespace Omnipotent.Services.KliveLLM;

/// <summary>
/// The pieces a live, spoken conversation needs from KliveLLM: AIRouter's speech endpoints, a slot
/// held back from the fleet while Klives is talking, and a way to pay a session's prefill before the
/// turn that needs it.
/// </summary>
public partial class KliveLLM
{
    private AIRouterAudioClient? audio;
    private readonly object audioLock = new();

    /// <summary>AIRouter speech-to-text and text-to-speech, admitted through the shared fair-use queue.
    /// Uses AIRouterLLMToken whichever chat provider is selected.</summary>
    public AIRouterAudioClient Audio
    {
        get
        {
            lock (audioLock)
            {
                return audio ??= new AIRouterAudioClient(
                    AIRouterEndpoint,
                    async _ => await GetOmniSetting("AIRouterLLMToken", OmniSettingType.String, true, false),
                    FairUse,
                    // Whether AIRouter's 3-parallel limit counts audio is unmeasured. Until it is, the
                    // safe assumption is yes: an uncounted call can push a chat request into a 429 that
                    // penalises the whole fleet.
                    async () => await GetBoolOmniSetting("AIRouterAudioCountsTowardParallel", true),
                    message => { try { _ = ServiceLog(message); } catch { } })
                {
                    Recorder = RecordAudioCall,
                };
            }
        }
    }

    /// <summary>Speech calls go to the ledger as KliveAgent's speech lane: no tokens, but time, size and outcome.</summary>
    private void RecordAudioCall(AIRouterAudioCall call)
    {
        var ledger = Ledger;
        if (ledger == null) return;
        long ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ledger.Enqueue(new Telemetry.LlmCallRecord
        {
            Id = ledger.NewId(),
            StartedMs = ended - call.DurationMs,
            EndedMs = ended,
            Service = LlmConsumerCatalog.Callers.KliveAgentSpeech.Service,
            Lane = LlmConsumerCatalog.Callers.KliveAgentSpeech.Lane,
            Kind = call.Kind,
            WorkClass = AIRouterWorkClass.Audio.ToString(),
            Router = LlmRouterIds.AIRouter,
            RouterName = "AIRouter",
            ModelRequested = call.Model,
            ModelServed = call.Model,
            Outcome = call.Outcome,
            HttpStatus = call.Status,
            Attempts = 1,
            Billing = "flat",
            ProviderMs = call.DurationMs,
            TotalMs = call.DurationMs,
            RequestBytes = call.BytesIn,
            Error = Telemetry.LlmCallTrace.Clip(call.Error, 600),
            AttemptLog = new List<Telemetry.LlmAttempt> { new(LlmRouterIds.AIRouter, call.Outcome, call.Status, 0, call.DurationMs) },
        });
    }

    /// <summary>Hold one AIRouter slot back from background work for a live conversation, until
    /// <paramref name="ttl"/> passes without a renewal. See <see cref="AIRouterFairUseLimiter.EngageInteractiveReserve"/>.</summary>
    public void EngageInteractiveReserve(string ownerId, TimeSpan ttl) => FairUse.EngageInteractiveReserve(ownerId, ttl);

    public void ReleaseInteractiveReserve(string ownerId) => FairUse.ReleaseInteractiveReserve(ownerId);

    /// <summary>How long the provider can be expected to keep an idle prefix: the measured lifetime when
    /// the survival curve has enough evidence, otherwise the conservative default the briefed sessions use.</summary>
    public static TimeSpan EstimatedPrefixLifetime() => EffectiveBriefIdleLimit();

    /// <summary>When the tool session last completed a model turn (or was created), or null if absent.</summary>
    public DateTime? GetToolSessionLastUpdated(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (sessions)
            return sessions.TryGetValue(sessionId, out var session) ? session.lastUpdated : null;
    }

    /// <summary>
    /// True when a new user turn can be appended to the tool session without breaking the tool-call
    /// protocol: every tool call has its result, and the log ends either on a finished assistant turn or
    /// on a user turn nobody has answered yet (a conversation opened ahead of its first question).
    /// </summary>
    public bool CanAppendUserTurn(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (sessions)
        {
            if (!sessions.TryGetValue(sessionId, out var session) || session.structuredMessages.Count == 0) return false;
            if (!HasCompleteToolExchanges(session.structuredMessages)) return false;
            var last = session.structuredMessages[^1];
            return last.role == "user"
                || (last.role == "assistant" && (last.tool_calls == null || last.tool_calls.Count == 0));
        }
    }

    /// <summary>Messages currently held by the tool session (0 when absent).</summary>
    public int GetToolSessionMessageCount(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;
        lock (sessions)
            return sessions.TryGetValue(sessionId, out var session) ? session.structuredMessages.Count : 0;
    }

    /// <summary>
    /// Sends the tool session's current messages with <c>max_tokens: 1</c>, so the provider prefills
    /// and caches the prefix the next real turn will extend, without adding anything to the session.
    ///
    /// This is not the keep-alive ping AIRouterFairUseLimiter.RefreshPingsEnabled rules out. It is
    /// only fired by the prompt OWNER for a prefix that has just been assembled and has never been
    /// sent — work the next turn would otherwise do on its critical path — so it moves a prefill
    /// earlier instead of adding one. The bytes are exactly the session's, and the next request is
    /// those same messages plus one new user turn, so what this writes is what that reads.
    ///
    /// Best-effort: returns null on any failure and never throws (other than nothing).
    /// </summary>
    public async Task<KliveLLMResponse?> WarmToolSessionPrefixAsync(string sessionId, List<HFWrapper.HFTool> tools,
        string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        LlmCaller who;
        List<HFWrapper.HFMessage> snapshot;
        string epoch;
        lock (sessions)
        {
            if (!sessions.TryGetValue(sessionId, out var session) || session.structuredMessages.Count == 0) return null;
            snapshot = new List<HFWrapper.HFMessage>(session.structuredMessages);
            epoch = session.cacheEpochID;
            who = session.caller ?? LlmConsumerCatalog.Callers.KliveAgentTalk;
        }
        using var call = BeginCall(who, "warm", sessionId, AIRouterWorkClass.Interactive, stream: false);
        try
        {
            var plan = await PlanCallAsync(who, forceFreeModel: false, toolSession: true);
            var route = plan.Primary;
            var remoteProvider = route.Config!;
            var (model, redirected) = await ChooseModelAsync(route, modelOverride, cancellationToken);
            call.Trace.ModelRequested = string.IsNullOrWhiteSpace(modelOverride) ? model : modelOverride.Trim();
            call.Trace.SetRoute(route.Router.Id, route.Router.Name, model, Telemetry.LlmCosting.BillingName(route.Router.Billing));
            if (redirected) call.Trace.Flags |= Telemetry.LlmCallFlags.Redirected;
            call.Trace.CacheEpoch = epoch;

            var payload = new HFWrapper.HFLLMInferenceRequest
            {
                model = model,
                stream = false,
                max_tokens = 1,
                tools = tools,
            };
            ApplyOpenRouterSession(ref payload, remoteProvider, sessionId);
            ApplyServiceTier(ref payload, remoteProvider);
            // No reasoning: a warm-up wants the prefill, not a thought. The reasoning setting only
            // affects what follows the prompt, so it does not change the bytes being cached.
            if (remoteProvider.Provider == LLMProvider.AIRouter)
                payload.reasoning_effort = AIRouterReasoningEffort(model, "off");
            ApplyTextualToolProtocolStops(ref payload, remoteProvider, snapshot);
            payload.BuildMessagesFromList(GemmaToolCallCompatibility.PrepareContinuationMessages(snapshot));
            ApplyPromptCaching(ref payload, remoteProvider);

            var response = await SendPayloadWithRetryAsync(remoteProvider, payload, cancellationToken,
                cachePrefixHint: $"{sessionId}#{epoch}", workClass: AIRouterWorkClass.Interactive);
            var record = call.Succeed(response);
            var result = new KliveLLMResponse
            {
                Success = true,
                SessionId = sessionId,
                Model = response.model,
                PromptTokens = response.usage?.prompt_tokens ?? 0,
                CachedPromptTokens = response.usage?.prompt_tokens_details?.cached_tokens ?? 0,
                CacheMetricsAvailable = response.usage?.prompt_tokens_details?.HasCacheReadMetrics == true,
                CompletionTokens = response.usage?.completion_tokens ?? 0,
                QueueDurationMs = response.queue_duration_ms,
                ProviderDurationMs = response.provider_duration_ms,
                CacheEpochID = epoch,
            };
            Decorate(result, call.Trace, record);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            call.Fail(new OperationCanceledException(), cancelled: true);
            return null;
        }
        catch (Exception ex)
        {
            call.Fail(ex, cancelled: false);
            try { await ServiceLog($"Prefix warm-up for {sessionId} failed (the next turn will prefill as usual): {ex.Message}"); } catch { }
            return null;
        }
    }
}
