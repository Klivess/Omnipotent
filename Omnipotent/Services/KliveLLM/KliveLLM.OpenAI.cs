using Omnipotent.Service_Manager;

namespace Omnipotent.Services.KliveLLM;

/// <summary>
/// OpenAI as a KliveLLM provider: the key and base URL its router and GPT-Live share, the GPT-Live
/// client KliveAgent's voice uses, and the ledger record a voice session leaves behind.
/// </summary>
public partial class KliveLLM
{
    private OpenAILiveClient? openAILive;

    /// <summary>Test seam: answers the OpenAI key and base URL without the settings service.</summary>
    internal Func<(string Key, string BaseUrl)>? OpenAICredentialsOverride { get; set; }

    /// <summary>The OpenAI key (OpenAILLMToken, set on the KliveLLM page's OpenAI router) and base URL
    /// (OpenAIBaseURL). Throws with the setting to fix when the key is missing.</summary>
    internal async Task<(string Key, string BaseUrl)> GetOpenAICredentialsAsync()
    {
        if (OpenAICredentialsOverride is { } forced) return forced();
        string key = await GetOmniSetting("OpenAILLMToken", OmniSettingType.String, true, false);
        string baseUrl = await GetStringOmniSetting("OpenAIBaseURL", DefaultOpenAIBaseUrl, false, true);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAILLMToken is missing: add the OpenAI key on the KliveLLM page (Routers → OpenAI → Set key).");
        return (key.Trim(), string.IsNullOrWhiteSpace(baseUrl) ? DefaultOpenAIBaseUrl : baseUrl.Trim());
    }

    /// <summary>GPT-Live (OpenAI's realtime voice model), on the OpenAI router's key.</summary>
    public OpenAILiveClient OpenAILive =>
        LazyInitializer.EnsureInitialized(ref openAILive, () => new OpenAILiveClient(async _ => await GetOpenAICredentialsAsync()))!;

    /// <summary>Whether the OpenAI router is switched on (the KliveLLM page can switch a built-in off).</summary>
    public bool OpenAIEnabled => RouterById(LlmRouterIds.OpenAI)?.Enabled ?? true;

    /// <summary>
    /// One GPT-Live voice session in the ledger, as KliveAgent's voice lane: billed by the minute, so the
    /// cost is reported directly rather than priced from tokens. When OpenAI's final usage never
    /// arrived (the connection dropped first), the wall-clock duration stands in as an estimate.
    /// </summary>
    internal void RecordLiveSession(string sessionId, string model, DateTimeOffset started, DateTimeOffset ended,
        OpenAILiveUsage? usage, bool usageFinal, double activePerMinute, double standbyPerMinute, string outcome, string? error)
    {
        var ledger = Ledger;
        if (ledger == null) return;
        long startedMs = started.ToUnixTimeMilliseconds();
        long endedMs = Math.Max(startedMs, ended.ToUnixTimeMilliseconds());
        var basis = usage ?? new OpenAILiveUsage((endedMs - startedMs) / 1000.0, null, null, null);
        double cost = Math.Round(basis.CostUsd(activePerMinute, standbyPerMinute), 6);
        var caller = LlmConsumerCatalog.Callers.KliveAgentVoice;
        ledger.Enqueue(new Telemetry.LlmCallRecord
        {
            Id = ledger.NewId(),
            StartedMs = startedMs,
            EndedMs = endedMs,
            Service = caller.Service,
            Lane = caller.Lane,
            SessionId = sessionId,
            Kind = "voice",
            WorkClass = AIRouterWorkClass.Interactive.ToString(),
            Router = LlmRouterIds.OpenAI,
            RouterName = "OpenAI",
            Provider = "OpenAI",
            ModelRequested = model,
            ModelServed = model,
            Stream = true,
            Outcome = outcome,
            Attempts = 1,
            Billing = "per_token",
            CostUsd = usageFinal ? cost : null,
            EstimatedCostUsd = cost,
            ProviderMs = (long)(basis.Seconds * 1000),
            TotalMs = endedMs - startedMs,
            Error = Telemetry.LlmCallTrace.Clip(error, 600),
            AttemptLog = new List<Telemetry.LlmAttempt> { new(LlmRouterIds.OpenAI, outcome, null, 0, endedMs - startedMs) },
        });
    }
}
