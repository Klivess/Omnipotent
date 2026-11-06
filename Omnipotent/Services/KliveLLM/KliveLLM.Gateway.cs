using System.Globalization;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveLLM;

/// <summary>How the current time is (or is not) shown to the model.</summary>
public enum LlmStamp
{
    /// <summary>No stamp: byte-stable prompts (cache-safe) and no "now" leaking into historical tasks.</summary>
    None,
    /// <summary>Stamp the turn with an explicit as-of instant (replicas and historical inquiries).</summary>
    AsOf,
    /// <summary>The legacy receipt stamp (current UTC time).</summary>
    Now,
}

/// <summary>A JSON schema the model must answer with (forced single tool call).</summary>
public sealed record LlmOutputSchema(string Name, string Description, JObject Schema);

/// <summary>One conversation turn. Roles: "user" or "assistant".</summary>
public sealed record LlmTurn(string Role, string Content);

/// <summary>A gateway request. Stateless by default; <see cref="KeepSession"/> enables "prefill once, ask
/// many": later calls with the same <see cref="SessionKey"/> append to the same prefix.</summary>
public sealed record LlmRequest
{
    public required string Purpose { get; init; }
    /// <summary>Stable session key; also the AIRouter cache-prefix identity. Use "omniscience-bg-…" for
    /// background work and "omniscience-ia-…" for interactive work.</summary>
    public required string SessionKey { get; init; }
    public string? SystemPrompt { get; init; }
    public required IReadOnlyList<LlmTurn> Turns { get; init; }
    public LlmStamp Stamp { get; init; } = LlmStamp.None;
    public long? AsOfMs { get; init; }
    public int? MaxOutputTokens { get; init; }
    public ModelSamplingParameters? Sampling { get; init; }
    public AIRouterWorkClass WorkClass { get; init; } = AIRouterWorkClass.Background;
    public string? Model { get; init; }
    public LlmOutputSchema? OutputSchema { get; init; }
    /// <summary>Provider reasoning effort ("low"/"medium"/"high"); null keeps the provider default.</summary>
    public string? ReasoningEffort { get; init; }
    public Action<string>? OnToken { get; init; }
    public bool KeepSession { get; init; }
}

public enum LlmErrorKind
{
    None,
    ContextOverflow,
    RequestTooLarge,
    RateLimited,
    Unauthorized,
    Refused,
    NoStructuredOutput,
    Truncated,
    Cancelled,
    Other,
}

/// <summary>Everything a caller needs to log, verify and calibrate a call.</summary>
public sealed record LlmResult(
    bool Success,
    string? Text,
    string? StructuredJson,
    LlmErrorKind ErrorKind,
    string? ErrorMessage,
    string? FinishReason,
    string? Model,
    string? Provider,
    int PromptTokens,
    int CachedTokens,
    int CompletionTokens,
    long RequestBytes,
    long LatencyMs);

public partial class KliveLLM
{
    private ModelContextRegistry? contextRegistry;
    private readonly object contextRegistryLock = new();
    private volatile Dictionary<string, int> modelContextOverrides = new(StringComparer.OrdinalIgnoreCase);
    private volatile int aiRouterStatedWindow;

    /// <summary>The shared model-limit registry (Omniscience v3, OmniBlox and Projects can all use it).</summary>
    public ModelContextRegistry ContextRegistry
    {
        get
        {
            lock (contextRegistryLock)
            {
                return contextRegistry ??= new ModelContextRegistry(
                    FetchOpenRouterPublicCatalogAsync,
                    FetchProviderCatalogAsync,
                    (provider, model) => modelContextOverrides.TryGetValue(model, out int v) ? v : null,
                    provider => provider == LLMProvider.AIRouter.ToString() && aiRouterStatedWindow > 0 ? aiRouterStatedWindow : null,
                    Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMDirectory), "model-limits.json"));
            }
        }
    }

    /// <summary>Context limits of the model a request would be served by right now.</summary>
    public async Task<ModelContextInfo> GetActiveModelContextAsync(string? modelOverride = null, CancellationToken ct = default)
    {
        await RefreshContextSettingsAsync().ConfigureAwait(false);
        var provider = await GetRemoteProviderConfigurationAsync(false).ConfigureAwait(false);
        string model = string.IsNullOrWhiteSpace(modelOverride) ? provider.Model : modelOverride.Trim();
        if (provider.Provider == LLMProvider.AIRouter)
            model = await ResolveAIRouterModelAsync(model, provider, ct).ConfigureAwait(false);
        return await ContextRegistry.ResolveAsync(provider.Provider.ToString(), model, ct).ConfigureAwait(false);
    }

    private async Task RefreshContextSettingsAsync()
    {
        // "model=tokens;model2=tokens". Empty default (an unset key is harmless if persisted).
        string raw = await GetStringOmniSetting("KliveLLM_ModelContextOverrides", "", false, false).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq > 0 && int.TryParse(part[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
                map[part[..eq].Trim()] = v;
        }
        modelContextOverrides = map;
        // Klives stated AIRouter's window as 260K (Oct 24 2026); the registry still prefers anything learned.
        aiRouterStatedWindow = await GetIntOmniSetting("KliveLLM_AIRouterContextTokens", 260_000).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ModelContextRegistry.CatalogEntry>?> FetchOpenRouterPublicCatalogAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            using var resp = await capabilitiesHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return ModelContextRegistry.ParseCatalog(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private async Task<IReadOnlyList<ModelContextRegistry.CatalogEntry>?> FetchProviderCatalogAsync(string provider, CancellationToken ct)
    {
        try
        {
            var cfg = await GetRemoteProviderConfigurationAsync(false).ConfigureAwait(false);
            if (cfg.Provider.ToString() != provider) return null;
            string? url = cfg.Provider switch
            {
                LLMProvider.AIRouter => AIRouterEndpoint.TrimEnd('/') + "/models",
                LLMProvider.OpenRouter => "https://openrouter.ai/api/v1/models",
                _ => null,
            };
            if (url == null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            using var resp = await aiRouterCatalogHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return ModelContextRegistry.ParseCatalog(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The structured gateway Omniscience v3 builds on (plan "LLM gateway"). Unlike <see cref="QueryLLM"/>:
    /// <list type="bullet">
    /// <item>it carries an explicit AIRouter work class and a stable cache-prefix identity (no anonymous OneShot);</item>
    /// <item>it never stamps "now" unless asked (byte-stable prefixes, no present-day leak into historical work);</item>
    /// <item>it can force a JSON-schema answer through a single tool call;</item>
    /// <item>it disables the lossy session compactor: the caller packs the prompt and a provider overflow is
    /// reported (and learned) instead of evidence being silently dropped;</item>
    /// <item>it never leaks sessions: stateless calls reset in finally; KeepSession sessions are the caller's to end.</item>
    /// </list>
    /// </summary>
    public async Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionKey);
        if (request.Turns.Count == 0 || request.Turns[^1].Role != "user")
            throw new ArgumentException("A request must end with a user turn.", nameof(request));

        string sid = request.SessionKey;
        bool fresh = !request.KeepSession || !CanContinueToolSession(sid);
        if (fresh) StartToolSession(sid, request.SystemPrompt);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string provider = "", model = request.Model ?? "";
        try
        {
            foreach (var turn in request.Turns)
            {
                if (turn.Role == "assistant") { AppendAssistantMessageToToolSession(sid, turn.Content); continue; }
                if (turn.Role != "user") throw new ArgumentException($"Unsupported role '{turn.Role}'.", nameof(request));
                switch (request.Stamp)
                {
                    case LlmStamp.Now: AppendUserMessageToToolSession(sid, turn.Content); break;
                    case LlmStamp.AsOf when request.AsOfMs is long asOf:
                        var at = DateTimeOffset.FromUnixTimeMilliseconds(asOf).UtcDateTime;
                        AppendCacheStableWakeSeedToToolSession(sid, $"[as of {TemporalFormat.Stamp(at)}] {turn.Content}");
                        break;
                    default: AppendCacheStableWakeSeedToToolSession(sid, turn.Content); break;
                }
            }

            var tools = new List<HFWrapper.HFTool>();
            object? toolChoice = null;
            if (request.OutputSchema is { } schema)
            {
                tools.Add(new HFWrapper.HFTool
                {
                    function = new HFWrapper.HFFunctionDefinition { name = schema.Name, description = schema.Description, parameters = schema.Schema },
                });
                toolChoice = new { type = "function", function = new { name = schema.Name } };
            }

            var resp = await QueryToolSessionAsync(sid, tools, request.MaxOutputTokens, request.Model, ct, request.OnToken,
                request.ReasoningEffort, onToolCallComplete: null, modelRoutes: null,
                compactAboveTokensOverride: 0, compactKeepRecentMessagesOverride: null, contextWindowTokensOverride: null,
                enableOpenRouterContextCompression: false, samplingParameters: request.Sampling, compactProtectPrefixMessages: 0,
                workClass: request.WorkClass, honourThinkingOnAIRouter: request.ReasoningEffort != null,
                toolChoice: toolChoice, responseFormat: null).ConfigureAwait(false);
            sw.Stop();
            provider = resp.Provider ?? "";
            model = resp.Model ?? model;

            if (!resp.Success)
            {
                var kind = ClassifyGatewayError(resp.ErrorMessage);
                if (kind == LlmErrorKind.ContextOverflow) ContextRegistry.RecordOverflow(provider, model, resp.ErrorMessage);
                if (kind == LlmErrorKind.RequestTooLarge) ContextRegistry.RecordRequestTooLarge(provider, model, resp.RequestBytes);
                return Fail(kind, resp.ErrorMessage, resp, sw.ElapsedMilliseconds);
            }

            ContextRegistry.RecordSuccess(provider, model, resp.PromptTokens, resp.RequestBytes);

            // A kept session that just answered with tool calls must carry matching tool results before the
            // next user turn (OpenAI-compatible APIs reject an assistant tool_call followed by a user message).
            // Appended without pruning so the cached prefix stays byte-identical.
            if (request.KeepSession && resp.ToolCalls is { Count: > 0 })
                foreach (var call in resp.ToolCalls.Where(c => c?.id != null))
                    AppendToolResult(sid, call.id, call.function?.name ?? "result", "{\"recorded\":true}", preservePromptPrefix: true);

            string? structured = null;
            if (request.OutputSchema is { } s)
            {
                var call = resp.ToolCalls?.FirstOrDefault(c => c?.function?.name == s.Name);
                structured = call?.function?.arguments;
                if (string.IsNullOrWhiteSpace(structured)) structured = ExtractJsonObject(resp.Response);
                if (string.IsNullOrWhiteSpace(structured))
                    return Fail(LooksLikeRefusal(resp.Response) ? LlmErrorKind.Refused : LlmErrorKind.NoStructuredOutput,
                        "The model did not return the requested structured output.", resp, sw.ElapsedMilliseconds);
            }
            if (resp.FinishReason == "length")
                return Fail(LlmErrorKind.Truncated, "The answer hit the output ceiling (finish_reason=length).", resp, sw.ElapsedMilliseconds, structured);

            return new LlmResult(true, resp.Response, structured, LlmErrorKind.None, null, resp.FinishReason, resp.Model, resp.Provider,
                resp.PromptTokens, resp.CachedPromptTokens, resp.CompletionTokens, resp.RequestBytes, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LlmResult(false, null, null, LlmErrorKind.Cancelled, "Cancelled.", null, model, provider, 0, 0, 0, 0, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            var kind = ClassifyGatewayError(ex.Message);
            if (kind == LlmErrorKind.ContextOverflow && provider.Length > 0) ContextRegistry.RecordOverflow(provider, model, ex.Message);
            return new LlmResult(false, null, null, kind, ex.Message, null, model, provider, 0, 0, 0, 0, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (!request.KeepSession) ResetSession(sid);
        }
    }

    /// <summary>Ends a KeepSession conversation (releases its message log).</summary>
    public void EndGatewaySession(string sessionKey) => ResetSession(sessionKey);

    private static LlmResult Fail(LlmErrorKind kind, string? message, KliveLLMResponse r, long latencyMs, string? structured = null) =>
        new(false, r.Response, structured, kind, message, r.FinishReason, r.Model, r.Provider,
            r.PromptTokens, r.CachedPromptTokens, r.CompletionTokens, r.RequestBytes, latencyMs);

    internal static LlmErrorKind ClassifyGatewayError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return LlmErrorKind.Other;
        if (ModelContextRegistry.LooksLikeContextOverflow(message)) return LlmErrorKind.ContextOverflow;
        if (message.Contains("413", StringComparison.Ordinal) || message.Contains("Request Entity Too Large", StringComparison.OrdinalIgnoreCase)) return LlmErrorKind.RequestTooLarge;
        if (message.Contains("429", StringComparison.Ordinal) || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return LlmErrorKind.RateLimited;
        if (message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal) || message.Contains("unauthor", StringComparison.OrdinalIgnoreCase)) return LlmErrorKind.Unauthorized;
        return LlmErrorKind.Other;
    }

    private static bool LooksLikeRefusal(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length < 600
        && System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(I can(?:not|'t)|I'm (?:not able|unable)|I won't|I am unable)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The first balanced top-level JSON object in a text, if any.</summary>
    internal static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    string candidate = text.Substring(start, i - start + 1);
                    try { JToken.Parse(candidate); return candidate; }
                    catch (Newtonsoft.Json.JsonReaderException) { break; }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }
}
