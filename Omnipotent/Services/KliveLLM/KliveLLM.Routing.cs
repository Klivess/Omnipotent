using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveLLM.Telemetry;

namespace Omnipotent.Services.KliveLLM;

/// <summary>
/// Per-service routing and the call ledger, as KliveLLM's entry points use them: who is calling, which
/// router and model their call goes to (with fallbacks), and the record every call leaves behind.
/// </summary>
public partial class KliveLLM
{
    /// <summary>The routing document. Starts empty (everything routes as RemoteLLMProvider says) and is
    /// replaced by the saved one when the service starts.</summary>
    internal LlmRoutingStore Routing { get; set; } = LlmRoutingStore.InMemory();
    internal LlmPriceBook Prices { get; set; } = LlmPriceBook.InMemory();
    /// <summary>The ledger; null until the service has started it (and in most tests).</summary>
    internal LlmTelemetry? Ledger { get; set; }
    internal LlmQueryEngine? Queries { get; set; }
    internal LlmCircuitBreaker Circuits { get; } = new();
    internal LlmGates Gates { get; } = new();

    private static readonly AsyncLocal<LlmCallTrace?> currentTrace = new();

    /// <summary>The call the current flow is making, for the transport to report into.</summary>
    internal static LlmCallTrace? CurrentTrace => currentTrace.Value;

    // ── routers ──

    private Dictionary<string, LlmRouterDefinition>? routerCache;
    private long routerCacheRevision = -1;

    internal IReadOnlyDictionary<string, LlmRouterDefinition> Routers()
    {
        var doc = Routing.Peek;
        var cached = routerCache;
        if (cached != null && Interlocked.Read(ref routerCacheRevision) == doc.Revision) return cached;
        var merged = LlmRouterRegistry.Merge(doc.Routers);
        routerCache = merged;
        Interlocked.Exchange(ref routerCacheRevision, doc.Revision);
        return merged;
    }

    internal LlmRouterDefinition? RouterById(string? id)
        => id != null && Routers().TryGetValue(id, out var router) ? router : null;

    internal LlmBilling BillingOf(string routerId)
        => RouterById(routerId)?.Billing ?? (string.Equals(routerId, LlmRouterIds.AIRouter, StringComparison.OrdinalIgnoreCase) ? LlmBilling.Flat : LlmBilling.PerToken);

    internal string RouterNameOf(string routerId) => RouterById(routerId)?.Name ?? (string.IsNullOrEmpty(routerId) ? "(none)" : routerId);

    /// <summary>The router RemoteLLMProvider selects, as last read.</summary>
    internal string DefaultRouterIdNow() => LlmRouterRegistry.FromProvider((LLMProvider)cachedActiveProvider);

    // ── resolution ──

    /// <summary>Where a call from <paramref name="caller"/> goes now, using the last-read provider setting.
    /// Synchronous so per-turn budget checks can ask it.</summary>
    internal LlmRouteDecision ResolveRouteNow(LlmCaller caller, bool forceFreeModel = false)
        => LlmRouteResolver.Resolve(Routing.Peek, Routers(), DefaultRouterIdNow(), caller, forceFreeModel, DateTime.UtcNow,
            key => Ledger?.Usage(key));

    /// <summary>As <see cref="ResolveRouteNow"/>, after an authoritative read of RemoteLLMProvider.</summary>
    internal async Task<LlmRouteDecision> ResolveRouteAsync(LlmCaller caller, bool forceFreeModel = false)
    {
        await GetActiveProviderAsync();
        return ResolveRouteNow(caller, forceFreeModel);
    }

    /// <summary>One router a call may be sent to, ready to send.</summary>
    internal sealed record LlmPlannedRoute(LlmRouterDefinition Router, RemoteLLMProviderConfiguration? Config, string? PolicyModel, bool Pin, bool IsFallback);

    internal sealed class LlmCallPlan
    {
        public required LlmRouteDecision Decision { get; init; }
        public List<LlmPlannedRoute> Routes { get; } = new();
        public LlmPlannedRoute Primary => Routes[0];
        public bool IsLocal => Routes.Count > 0 && Routes[0].Router.IsLocal;
    }

    /// <summary>
    /// The routers a call will try, in order, each with its configuration. A router whose circuit is
    /// open is skipped while something else is left to try. Refusals (paused, budget, spending) throw a
    /// structured <see cref="RemoteLLMException"/>; a router that cannot be configured (no key, no
    /// endpoint) throws the same exception it always did when there is nothing else to fall back on.
    /// </summary>
    internal async Task<LlmCallPlan> PlanCallAsync(LlmCaller caller, bool forceFreeModel, bool toolSession)
    {
        var decision = await ResolveRouteAsync(caller, forceFreeModel);
        var trace = CurrentTrace;
        if (trace != null)
        {
            trace.RoutingRevision = decision.Revision;
            trace.CaptureContent = decision.Capture;
        }
        if (decision.IsBlocked)
            throw new RemoteLLMException(decision.Block!.Value, decision.BlockReason ?? "Refused by the KliveLLM routing policy.",
                decision.Primary?.Name ?? "KliveLLM", decision.Model ?? "", retryAfter: decision.BlockRetryAfter);

        var plan = new LlmCallPlan { Decision = decision };
        var wanted = new List<(LlmRouterDefinition Router, string? Model, bool Pin, bool Fallback)> { (decision.Primary!, decision.Model, decision.Pin, false) };
        wanted.AddRange(decision.Fallbacks.Select(f => (f.Router, f.Model, f.Model != null, true)));
        Exception? firstError = null;
        foreach (var (router, model, pin, fallback) in wanted)
        {
            if (router.IsLocal)
            {
                if (toolSession)
                {
                    firstError ??= new InvalidOperationException("Remote provider configuration was requested while RemoteLLMProvider is set to Local.");
                    continue;
                }
                plan.Routes.Add(new LlmPlannedRoute(router, null, model, pin, fallback));
                continue;
            }
            try
            {
                var config = await BuildRouterConfigurationAsync(router, decision);
                if (config == null && string.Equals(router.Id, LlmRouterIds.OpenRouterFree, StringComparison.OrdinalIgnoreCase))
                {
                    // The free model is switched off. As before per-service routing: the call still runs
                    // when the default route costs nothing per token, and stops with a reason when it would bill.
                    var substitute = RouterById(Routing.Peek.Default.Router ?? DefaultRouterIdNow());
                    string freeModel = await GetStringOmniSetting("FreeOpenRouterModelID", DefaultFreeOpenRouterModel, false, true);
                    if (substitute != null && !substitute.BillsPerToken && !substitute.IsLocal)
                    {
                        decision.Chain.Add($"The free model is switched off (FreeOpenRouterModelID = '{freeModel}'): {substitute.Name} costs nothing per token, so it is used.");
                        plan.Routes.Add(new LlmPlannedRoute(substitute, await BuildRouterConfigurationAsync(substitute, decision), null, false, fallback));
                        continue;
                    }
                    firstError ??= new InvalidOperationException(
                        $"The free model is switched off (FreeOpenRouterModelID = '{freeModel}') and the active provider bills per token, "
                        + "so free-model work is not run. Set FreeOpenRouterModelID to an OpenRouter free model (e.g. openrouter/free) to resume it.");
                    continue;
                }
                if (config != null) plan.Routes.Add(new LlmPlannedRoute(router, config, model, pin, fallback));
            }
            catch (InvalidOperationException ex)
            {
                firstError ??= ex;
                decision.Chain.Add($"{router.Name} cannot be used: {ex.Message}");
            }
        }
        if (plan.Routes.Count == 0)
            throw firstError ?? new RemoteLLMException(RemoteLLMFailureKind.RouterUnavailable,
                $"No router {caller.Service} may use is available.", "KliveLLM", decision.Model ?? "");

        // Skip routers that just failed this lane, as long as something else is left to try.
        if (plan.Routes.Count > 1)
        {
            var open = plan.Routes.Where(r => Circuits.IsOpen(caller.Key, r.Router.Id)).ToList();
            if (open.Count > 0 && open.Count < plan.Routes.Count)
            {
                foreach (var skipped in open)
                {
                    plan.Routes.Remove(skipped);
                    decision.Chain.Add($"{skipped.Router.Name} failed {caller.Service} recently: skipped until it is tried again.");
                }
            }
        }
        return plan;
    }

    /// <summary>
    /// The configuration to send a call to one router with. Built-ins read the settings they always
    /// have, so their requests are byte-for-byte what they were; added routers read their key from
    /// KliveLLM_RouterKey_{id}. Returns null only for the free route when the free model is switched off.
    /// </summary>
    /// <summary>Test seam: builds router configurations without the settings service (null = not configured).</summary>
    internal Func<LlmRouterDefinition, RemoteLLMProviderConfiguration?>? RouterConfigurationOverride { get; set; }

    internal async Task<RemoteLLMProviderConfiguration?> BuildRouterConfigurationAsync(LlmRouterDefinition router, LlmRouteDecision? decision = null)
    {
        RemoteLLMProviderConfiguration? config;
        if (RouterConfigurationOverride is { } overrideFor)
        {
            config = overrideFor(router) ?? throw new InvalidOperationException($"{router.Name} is not configured.");
            config.RouterId = router.Id;
            config.Billing = router.Billing;
            config.ThinkingCeiling = decision?.Thinking;
            if (!router.BuiltIn) { config.MaxParallel = router.MaxParallel; config.RequestsPerMinute = router.RequestsPerMinute; }
            return config;
        }
        switch (router.Id.ToLowerInvariant())
        {
            case LlmRouterIds.OpenRouterFree:
                {
                    string freeModel = await GetStringOmniSetting("FreeOpenRouterModelID", DefaultFreeOpenRouterModel, false, true);
                    if (IsSwitchedOffModelID(freeModel)) return null;
                    string freeToken = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
                    if (string.IsNullOrWhiteSpace(freeToken))
                        throw new InvalidOperationException("OpenRouterLLMToken is missing (required to use the free OpenRouter model).");
                    string freeServiceTier = await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
                    config = new RemoteLLMProviderConfiguration(LLMProvider.OpenRouter, "OpenRouter (free)",
                        "https://openrouter.ai/api/v1/chat/completions", freeToken, freeModel, decision?.ServiceTier ?? freeServiceTier);
                    break;
                }
            case LlmRouterIds.OpenRouter:
                {
                    string token = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
                    string model = await GetStringOmniSetting("OpenRouterModelID", DefaultOpenRouterModel, false, true);
                    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("OpenRouterLLMToken is missing.");
                    string tier = await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
                    config = new RemoteLLMProviderConfiguration(LLMProvider.OpenRouter, "OpenRouter",
                        "https://openrouter.ai/api/v1/chat/completions", token, model, decision?.ServiceTier ?? tier);
                    break;
                }
            case LlmRouterIds.Custom:
                {
                    string token = await GetOmniSetting("CustomOpenAILLMToken", OmniSettingType.String, true, false);
                    string endpoint = ResolveChatCompletionsEndpoint(await GetStringOmniSetting("CustomOpenAIEndpointURL", DefaultCustomOpenAIEndpoint, false, true));
                    string model = await GetStringOmniSetting("CustomOpenAIModelID", DefaultCustomOpenAIModel, false, true);
                    if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("CustomOpenAIEndpointURL is missing.");
                    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("CustomOpenAILLMToken is missing.");
                    config = new RemoteLLMProviderConfiguration(LLMProvider.CustomOpenAI, CustomOpenAIProviderOption, endpoint, token, model);
                    break;
                }
            case LlmRouterIds.AIRouter:
                {
                    string token = await GetOmniSetting("AIRouterLLMToken", OmniSettingType.String, true, false);
                    string model = await GetStringOmniSetting("AIRouterModelID", DefaultAIRouterModel, false, true);
                    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("AIRouterLLMToken is missing.");
                    config = new RemoteLLMProviderConfiguration(LLMProvider.AIRouter, AIRouterProviderOption,
                        ResolveChatCompletionsEndpoint(AIRouterEndpoint), token, model);
                    break;
                }
            case LlmRouterIds.HuggingFace:
                {
                    string model = await GetStringOmniSetting("HuggingFaceModelID", DefaultHuggingFaceModel, false, true);
                    string key = await GetOmniSetting("HuggingFaceLLMToken", OmniSettingType.String, true, false);
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("HuggingFaceLLMToken is missing.");
                    config = new RemoteLLMProviderConfiguration(LLMProvider.HuggingFace, "HuggingFace",
                        "https://router.huggingface.co/v1/chat/completions", key, model);
                    break;
                }
            case LlmRouterIds.Local:
                throw new InvalidOperationException("Remote provider configuration was requested while RemoteLLMProvider is set to Local.");
            default:
                {
                    string key = await GetStringOmniSetting(router.KeySetting, defaultValue: null, sensitive: true, askKlivesForFulfillment: false) ?? "";
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"{router.Name} has no key yet: add it on the KliveLLM page.");
                    string endpoint = ResolveChatCompletionsEndpoint(router.BaseUrl);
                    if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException($"{router.Name} has no base URL.");
                    config = new RemoteLLMProviderConfiguration(LLMProvider.CustomOpenAI, router.Name, endpoint, key, router.DefaultModel ?? "");
                    break;
                }
        }
        config.RouterId = router.Id;
        config.Billing = router.Billing;
        config.ThinkingCeiling = decision?.Thinking;
        if (!router.BuiltIn)
        {
            config.MaxParallel = router.MaxParallel;
            config.RequestsPerMinute = router.RequestsPerMinute;
        }
        return config;
    }

    /// <summary>
    /// The model to send on one route: a pinned policy model; otherwise the caller's own when the router
    /// serves it; otherwise the policy model; otherwise the router's default. "Serves it" is AIRouter's
    /// catalogue for AIRouter, an OpenRouter-style slug for OpenRouter, and the last probe's model list for
    /// an added router; anything else is passed through as it always was.
    /// </summary>
    internal async Task<(string Model, bool Redirected)> ChooseModelAsync(LlmPlannedRoute route, string? requested, CancellationToken cancellationToken)
    {
        var config = route.Config!;
        string fallback = string.IsNullOrWhiteSpace(route.PolicyModel) ? config.Model : route.PolicyModel!;
        string want = (requested ?? string.Empty).Trim();
        if (route.Pin && !string.IsNullOrWhiteSpace(route.PolicyModel))
            return (route.PolicyModel!, want.Length > 0 && !string.Equals(want, route.PolicyModel, StringComparison.OrdinalIgnoreCase));
        if (want.Length == 0) return (fallback, false);
        switch (config.Provider)
        {
            case LLMProvider.AIRouter:
                {
                    string model = await ResolveAIRouterModelAsync(want, config, cancellationToken, fallback);
                    return (model, !string.Equals(model, want, StringComparison.OrdinalIgnoreCase));
                }
            case LLMProvider.OpenRouter:
                // OpenRouter ids are always vendor/model; a bare name ("Qwen3.8") belongs to another router.
                if (!want.Contains('/', StringComparison.Ordinal))
                {
                    try { await ServiceLog($"OpenRouter does not serve '{want}'; using '{fallback}' instead."); } catch { }
                    return (fallback, true);
                }
                return (want, false);
            default:
                if (!LlmRouterIds.IsBuiltIn(route.Router.Id) && Ledger?.Health.Models(route.Router.Id) is { Count: > 0 } models && !models.Contains(want))
                    return (fallback, true);
                return (want, false);
        }
    }

    // ── recording ──

    /// <summary>One call in flight: makes the transport report into its trace and records it at the end.</summary>
    internal sealed class LlmCallScope : IDisposable
    {
        private readonly KliveLLM owner;
        private readonly LlmCallTrace? previous;
        private bool done;

        public LlmCallScope(KliveLLM owner, LlmCallTrace trace)
        {
            this.owner = owner;
            Trace = trace;
            previous = currentTrace.Value;
            currentTrace.Value = trace;
        }

        public LlmCallTrace Trace { get; }

        public LlmCallRecord? Succeed(HFWrapper.HFLLMInferenceResponse? response, string outcome = LlmOutcome.Ok, string? error = null)
        {
            if (done) return null;
            done = true;
            var record = Trace.Finish(outcome, response ?? Trace.LastResponse);
            if (error != null) record.Error = LlmCallTrace.Clip(error, 600);
            owner.RecordCall(Trace, record);
            return record;
        }

        public LlmCallRecord? Fail(Exception error, bool cancelled)
        {
            if (done) return null;
            done = true;
            var record = Trace.Finish(LlmOutcome.Of(error, cancelled), Trace.LastResponse, error);
            owner.RecordCall(Trace, record);
            return record;
        }

        /// <summary>A call refused before it was sent (a local check, not a provider).</summary>
        public LlmCallRecord? Refuse(string reason)
        {
            if (done) return null;
            done = true;
            var record = Trace.Finish(LlmOutcome.Blocked, null);
            record.Error = LlmCallTrace.Clip(reason, 600);
            record.Attempts = 0;
            owner.RecordCall(Trace, record);
            return record;
        }

        public void Dispose()
        {
            currentTrace.Value = previous;
            if (!done)
            {
                done = true;
                owner.Ledger?.Abandon(Trace);
            }
        }
    }

    internal LlmCallScope BeginCall(LlmCaller caller, string kind, string? sessionId, AIRouterWorkClass? workClass, bool stream)
    {
        var trace = Ledger?.Begin(caller, kind) ?? new LlmCallTrace(0, caller, kind);
        trace.SessionId = sessionId;
        trace.WorkClass = (workClass ?? (kind == "chat" ? AIRouterWorkClass.OneShot : AIRouterWorkClassifier.Classify(sessionId))).ToString();
        trace.Stream = stream;
        trace.RoutingRevision = Routing.Peek.Revision;
        return new LlmCallScope(this, trace);
    }

    private void RecordCall(LlmCallTrace trace, LlmCallRecord record)
    {
        var ledger = Ledger;
        if (ledger == null) return;
        if (record.Id == 0) record.Id = ledger.NewId();
        ledger.End(trace, record);
    }

    /// <summary>Copies what the ledger knows about a call onto the response its caller gets.</summary>
    private static void Decorate(KliveLLMResponse response, LlmCallTrace trace, LlmCallRecord? record)
    {
        response.CallId = record?.Id ?? trace.Id;
        response.RouterId = trace.Router;
        response.RouterName = trace.RouterName;
        response.Billing = trace.Billing;
        response.FlatFee = trace.Billing != "per_token";
        response.Attempts = trace.AttemptCount;
        response.FallbackFrom = trace.FallbackFrom;
        response.TimeToFirstTokenMs = trace.TtftMs;
        if (record != null) response.ReasoningTokens = (int)Math.Min(int.MaxValue, record.ReasoningTokens);
    }

    /// <summary>An added router's own parallel and per-minute limits, held for one HTTP exchange.</summary>
    private async Task<IDisposable?> EnterRouterGateAsync(RemoteLLMProviderConfiguration config, LlmCallTrace? trace, CancellationToken cancellationToken)
    {
        if (config.MaxParallel is not > 0 && config.RequestsPerMinute is not > 0) return null;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        trace?.SetState(LlmCallState.Queued);
        var gate = await Gates.EnterAsync("router:" + config.RouterId, config.MaxParallel, cancellationToken);
        try { await Gates.WaitForRateAsync("router:" + config.RouterId, config.RequestsPerMinute, cancellationToken); }
        catch { gate?.Dispose(); throw; }
        trace?.AddQueue(waited.ElapsedMilliseconds);
        return gate;
    }

    /// <summary>True when the exception is the router's failure, so another router might succeed.</summary>
    internal static bool IsFallbackable(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (ex is KliveLLMStreamStalledException { ProducedOutput: true }) return false;
        return ex switch
        {
            RemoteLLMException remote => remote.Kind is RemoteLLMFailureKind.Network or RemoteLLMFailureKind.Timeout
                or RemoteLLMFailureKind.ProviderUnavailable or RemoteLLMFailureKind.EmptyResponse
                or RemoteLLMFailureKind.InsufficientProviderCredit or RemoteLLMFailureKind.Authentication
                or RemoteLLMFailureKind.ModelUnavailable or RemoteLLMFailureKind.RateLimited,
            KliveLLMStreamStalledException => true,
            HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500 || (int)http.StatusCode is 401 or 402 or 403 or 404 or 408 or 429,
            TaskCanceledException or TimeoutException or IOException => true,
            _ => false,
        };
    }

    // ── public questions callers ask ──

    /// <summary>
    /// Can <paramref name="caller"/> make model calls right now, and if not, why? Used by services that
    /// refuse to start work they could not finish (OmniBlox never spends per token).
    /// </summary>
    public async Task<(bool Ok, string? Reason)> CheckRouteAsync(LlmCaller caller)
    {
        var decision = await ResolveRouteAsync(caller);
        if (decision.IsBlocked) return (false, decision.BlockReason);
        try
        {
            if (decision.Primary is { IsLocal: false } router) await BuildRouterConfigurationAsync(router, decision);
        }
        catch (InvalidOperationException ex) { return (false, ex.Message); }
        return (true, null);
    }

    /// <summary>The router a caller's calls go to now (for display and per-service decisions).</summary>
    public string RouterFor(LlmCaller caller) => ResolveRouteNow(caller).Primary?.Id ?? DefaultRouterIdNow();

    /// <summary>Tool sessions held per lane: how many, how many are still warm, and which router served each last.</summary>
    internal Dictionary<string, List<(string? Router, bool Warm, DateTime LastUpdated)>> SessionsByLane()
    {
        var limit = EffectiveBriefIdleLimit();
        var now = DateTime.UtcNow;
        var result = new Dictionary<string, List<(string?, bool, DateTime)>>(StringComparer.OrdinalIgnoreCase);
        lock (sessions)
        {
            foreach (var s in sessions.Values)
            {
                if (s.caller == null || s.structuredMessages.Count == 0) continue;
                if (!result.TryGetValue(s.caller.Key, out var list)) result[s.caller.Key] = list = new();
                list.Add((s.lastRouterId, now - s.lastUpdated <= limit, s.lastUpdated));
            }
        }
        return result;
    }
}
