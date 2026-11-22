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

/// <summary>An image sent with a user turn: encoded bytes (JPEG or PNG) and their media type.</summary>
public sealed record LlmImage(byte[] Data, string MimeType);

/// <summary>One conversation turn. Roles: "user" or "assistant". Only a user turn may carry <paramref name="Images"/>
/// (Omniscience v3's captions); the caller keeps them small (AIRouter refuses bodies of about 4 MB).</summary>
public sealed record LlmTurn(string Role, string Content, IReadOnlyList<LlmImage>? Images = null);

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
    /// <summary>
    /// Every schema the calls of one prompt family answer with, in one fixed order. They go out as one tool
    /// definition, the same on every structured call of the family, with a property per schema, and
    /// <see cref="OutputSchema"/> names the one this call must answer with (<see cref="KliveLLM.GatewayTools"/>). A
    /// provider lays the tool definitions out ahead of the conversation, so a call whose tools differ from the call
    /// before it shares nothing with it past the system prompt: Omniscience's critic, asked with another schema than
    /// the reading before it, had the whole evidence prefilled a second time. Null: the call's own schema alone.
    /// </summary>
    public IReadOnlyList<LlmOutputSchema>? SchemaFamily { get; init; }
    /// <summary>
    /// With <see cref="KeepSession"/>: the session is taken back to its system prompt before this request's turns go
    /// in, and keeps its cache identity. For a caller that asks several self-contained questions over one large
    /// opening and sends that opening with each of them: the provider serves the opening from its prefix cache, the
    /// scheduler sees a continuation of the same prefix (never parked, and measured as one), and none of the earlier
    /// answers is sent back to be prefilled again.
    /// </summary>
    public bool Rewind { get; init; }
    /// <summary>Who is asking. KliveLLM routes, budgets and logs by caller; left empty, the caller is named from the
    /// file that made the call.</summary>
    public LlmCaller? Caller { get; init; }
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

    /// <summary>Context limits of the model a request would be served by right now. KliveLLM routes by caller, so
    /// with <paramref name="caller"/> this is the model that caller's calls go out to; without one it is the default
    /// route's, as it was before per-service routing.</summary>
    public async Task<ModelContextInfo> GetActiveModelContextAsync(string? modelOverride = null, CancellationToken ct = default,
        LlmCaller? caller = null)
    {
        await RefreshContextSettingsAsync().ConfigureAwait(false);
        var (provider, model) = await ServedByAsync(caller, modelOverride, ct).ConfigureAwait(false);
        if (provider.Provider == LLMProvider.AIRouter)
            model = await ResolveAIRouterModelAsync(model, provider, ct).ConfigureAwait(false);
        return await ContextRegistry.ResolveAsync(provider.Provider.ToString(), model, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The router and model a call from <paramref name="caller"/> would go out to first: its lane's route, with the
    /// model chosen exactly as the call itself chooses it. A caller whose route cannot be read (paused, over its
    /// budget, not allowed to spend, no key) is answered with the default route: the call itself reports why it
    /// cannot run, and a prompt packed for the default model is the same prompt that was packed before routing.
    /// </summary>
    internal async Task<(RemoteLLMProviderConfiguration Provider, string Model)> ServedByAsync(LlmCaller? caller, string? modelOverride, CancellationToken ct)
    {
        if (caller != null)
        {
            var decision = await ResolveRouteAsync(caller).ConfigureAwait(false);
            if (!decision.IsBlocked && decision.Primary is { IsLocal: false } router)
            {
                RemoteLLMProviderConfiguration? config;
                try { config = await BuildRouterConfigurationAsync(router, decision).ConfigureAwait(false); }
                catch (InvalidOperationException) { config = null; }
                if (config != null)
                {
                    var (served, _) = await ChooseModelAsync(new LlmPlannedRoute(router, config, decision.Model, decision.Pin, false), modelOverride, ct).ConfigureAwait(false);
                    return (config, served);
                }
            }
        }
        var byDefault = await GetRemoteProviderConfigurationAsync(false).ConfigureAwait(false);
        return (byDefault, string.IsNullOrWhiteSpace(modelOverride) ? byDefault.Model : modelOverride.Trim());
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

    /// <summary>The caller of a gateway request: the one it names, else the one its call site implies. The purpose is
    /// kept as the operation, so the call ledger can tell an extraction from a digest. It is a code-defined label,
    /// never a person or a message.</summary>
    internal static LlmCaller GatewayCaller(LlmRequest request, string? callerFile)
    {
        var who = LlmConsumerCatalog.Resolve(request.Caller, callerFile);
        return who.Operation == null && !string.IsNullOrWhiteSpace(request.Purpose) ? who with { Operation = request.Purpose } : who;
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
    public async Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct = default,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionKey);
        if (request.Turns.Count == 0 || request.Turns[^1].Role != "user")
            throw new ArgumentException("A request must end with a user turn.", nameof(request));

        // Named here and passed down: left to the entry points below, every gateway call would be filed under
        // KliveLLM itself (this file is where they are made from) and follow that lane's spending rule, not the caller's.
        LlmCaller who = GatewayCaller(request, callerFile);

        string sid = request.SessionKey;
        bool fresh = !request.KeepSession
                     || !(request.Rewind ? RewindGatewaySession(sid, request.SystemPrompt) : CanContinueGatewaySession(sid));
        if (fresh) StartToolSession(sid, request.SystemPrompt, caller: who);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string provider = "", model = request.Model ?? "";
        try
        {
            var toolSet = GatewayTools(request);
            AppendGatewayTurns(sid, toolSet?.Member == null ? request : request with { Turns = WithFamilyInstruction(request.Turns, toolSet.Member) });

            var tools = new List<HFWrapper.HFTool>();
            object? toolChoice = null;
            if (toolSet != null)
            {
                foreach (var tool in toolSet.Tools)
                    tools.Add(new HFWrapper.HFTool
                    {
                        function = new HFWrapper.HFFunctionDefinition { name = tool.Name, description = tool.Description, parameters = tool.Schema },
                    });
                toolChoice = new { type = "function", function = new { name = toolSet.Forced } };
            }

            // Without a schema there are no tools, and the key is left out: OpenAI-compatible APIs reject an empty
            // array ("tools: [] is too short"). Left out here, not in the sender, so no other caller's request changes.
            var resp = await QueryToolSessionAsync(sid, tools.Count > 0 ? tools : null!, request.MaxOutputTokens, request.Model, ct, request.OnToken,
                request.ReasoningEffort, onToolCallComplete: null, modelRoutes: null,
                compactAboveTokensOverride: 0, compactKeepRecentMessagesOverride: null, contextWindowTokensOverride: null,
                enableOpenRouterContextCompression: false, samplingParameters: request.Sampling, compactProtectPrefixMessages: 0,
                workClass: request.WorkClass, honourThinkingOnAIRouter: request.ReasoningEffort != null,
                toolChoice: toolChoice, responseFormat: null, caller: who).ConfigureAwait(false);
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
            if (request.OutputSchema is { } s && toolSet != null)
            {
                var call = resp.ToolCalls?.FirstOrDefault(c => c?.function?.name == toolSet.Forced);
                structured = call?.function?.arguments;
                if (string.IsNullOrWhiteSpace(structured)) structured = ExtractJsonObject(resp.Response);
                if (toolSet.Member != null) structured = FamilyAnswer(structured, s);
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

    /// <summary>
    /// Whether a kept gateway session can take another question on top of what it already holds: its last message is the
    /// model's answer, or the tool results this gateway records after a structured answer. A structured answer IS a tool
    /// call, so <see cref="CanContinueToolSession"/> (which wants a plain assistant message last) said no after every one
    /// of them: the session was started again, and a caller that had already sent its evidence then sent only the
    /// follow-up question. Omniscience's adjudicator and critic were judging claims without the messages they cite.
    /// A session whose last message is a user turn was left by a failed call and is started again.
    /// </summary>
    internal bool CanContinueGatewaySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (sessions)
        {
            if (!sessions.TryGetValue(sessionId, out var session) || session.structuredMessages.Count == 0) return false;
            var last = session.structuredMessages[^1];
            if (last.role == "tool") return true;
            return last.role == "assistant" && (last.tool_calls == null || last.tool_calls.Count == 0);
        }
    }

    /// <summary>The tool definitions a structured request goes out with, the one the model is made to call, and, when
    /// that one is a family's shared tool, the property of it this call's answer goes in.</summary>
    internal sealed record GatewayToolSet(IReadOnlyList<LlmOutputSchema> Tools, string Forced, string? Member);

    /// <summary>The name of the one tool every call of a schema family is made to call. Not a name any schema of a
    /// family carries (the inquiry's own answer schema is "emit_answer").</summary>
    internal const string FamilyToolName = "record_answer";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<LlmOutputSchema>, LlmOutputSchema> familyTools = new();

    /// <summary>
    /// What a structured request goes out with. A call without a family sends its own schema as the only tool and is
    /// made to call it, as every caller did before. The calls of a family all send one and the same tool
    /// (<see cref="FamilyTool"/>) and are all made to call it; which of its properties to fill is said at the end of
    /// the question.
    /// <para>From 20 to 22 Nov 2026 a family's schemas went out as separate tools, all of them on every call, with the
    /// call's own one forced. That is only the same bytes on every call if the provider shows the model every tool it
    /// is sent. AIRouter shows a call that must use one tool that tool alone: the critic's prompt came back counted
    /// about 1,100 tokens smaller than the reading's over the same evidence (the size of the claims schema), and of
    /// its 5,000 tokens some 1,400 were served from cache, the system prompt and no more. Of everything Omniscience
    /// sent a second time, 18% came from cache. One tool, forced by one name, leaves a provider nothing to choose
    /// between.</para>
    /// </summary>
    internal static GatewayToolSet? GatewayTools(LlmRequest request)
    {
        if (request.OutputSchema is not { } wanted) return null;
        var family = request.SchemaFamily;
        if (family == null || family.Count == 0) return new GatewayToolSet(new[] { wanted }, wanted.Name, null);
        // A schema its family forgot still reaches the model: the tool is then this call's own, and so is everything
        // the provider reads behind it.
        if (!family.Any(s => string.Equals(s.Name, wanted.Name, StringComparison.Ordinal)))
            return new GatewayToolSet(new[] { FamilyTool(family.Append(wanted).ToList()) }, FamilyToolName, wanted.Name);
        if (family.Count == 1) return new GatewayToolSet(new[] { wanted }, wanted.Name, null);
        return new GatewayToolSet(new[] { familyTools.GetValue(family, FamilyTool) }, FamilyToolName, wanted.Name);
    }

    /// <summary>One tool for a whole family: an object with a property per schema, named after it, in the family's order.</summary>
    internal static LlmOutputSchema FamilyTool(IReadOnlyList<LlmOutputSchema> family)
    {
        var properties = new JObject();
        foreach (var schema in family)
        {
            var member = (JObject)schema.Schema.DeepClone();
            if (member["description"] == null && !string.IsNullOrWhiteSpace(schema.Description)) member["description"] = schema.Description;
            properties[schema.Name] = member;
        }
        return new LlmOutputSchema(FamilyToolName, "Records the answer to the question that was asked. Fill in exactly one property, the one the question names, and leave the others out.",
            new JObject { ["type"] = "object", ["properties"] = properties });
    }

    /// <summary>What the end of a family call's question says about where its answer goes.</summary>
    internal static string FamilyInstruction(string member) =>
        "\n\nGive your answer by calling " + FamilyToolName + " with only its \"" + member + "\" property filled in.";

    /// <summary>The turns with the family instruction at the end of the last one: behind the question, so nothing ahead of
    /// it moves.</summary>
    private static IReadOnlyList<LlmTurn> WithFamilyInstruction(IReadOnlyList<LlmTurn> turns, string member)
    {
        var copy = new List<LlmTurn>(turns);
        copy[^1] = copy[^1] with { Content = copy[^1].Content + FamilyInstruction(member) };
        return copy;
    }

    /// <summary>
    /// A family call's own answer, taken out of the shared tool's arguments: the object under the property named after
    /// its schema. A model that answered with that object alone, without the property around it, is taken at its word.
    /// Null when the arguments hold neither (another member was filled, or nothing): the call has no structured
    /// answer. Arguments that are not whole JSON (cut off at the ceiling) are returned as they are, for the caller's
    /// own reading of a cut-off answer.
    /// </summary>
    internal static string? FamilyAnswer(string? arguments, LlmOutputSchema wanted)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return arguments;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(arguments, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return arguments;
            if (root.TryGetProperty(wanted.Name, out var member))
            {
                if (member.ValueKind == System.Text.Json.JsonValueKind.Object) return member.GetRawText();
                // Some models hand an object over as a string of JSON.
                if (member.ValueKind == System.Text.Json.JsonValueKind.String && member.GetString() is { } text && text.TrimStart().StartsWith('{')) return text;
                return null;
            }
            if (wanted.Schema["properties"] is JObject own)
                foreach (var property in root.EnumerateObject())
                    if (own[property.Name] != null) return arguments;
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return arguments;
        }
    }

    /// <summary>
    /// Takes a kept gateway session back to its system prompt, for a request that asks over the same opening again
    /// (<see cref="LlmRequest.Rewind"/>). The session object stays, and with it the cache epoch: the next call is a
    /// continuation of the prefix the provider already holds, which is what it is. Whatever the session held after the
    /// system prompt goes, an unanswered question from a failed call included. False when there is nothing to go
    /// back to (no such session, or one opened with another system prompt, which is another prefix): the caller
    /// starts the session afresh.
    /// </summary>
    internal bool RewindGatewaySession(string sessionId, string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (sessions)
        {
            if (!sessions.TryGetValue(sessionId, out var session)) return false;
            var log = session.structuredMessages;
            int head = 0;
            while (head < log.Count && log[head].role == "system") head++;
            string? held = head == 0 ? null : HFWrapper.ContentToText(log[0].content);
            string? wanted = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt + ThinkingDirective;
            if (head > 1 || !string.Equals(held, wanted, StringComparison.Ordinal)) return false;
            log.RemoveRange(head, log.Count - head);
            // What the provider can be told to keep is the system prompt; the rest of the shared opening sits inside
            // the user turn, where an automatic prefix cache finds it by itself.
            session.lastCacheMessageCount = Math.Min(session.lastCacheMessageCount, head);
            return true;
        }
    }

    /// <summary>
    /// Appends a request's turns to its session, stamped as the request says. A user turn with images goes in as one
    /// message of text and image parts, kept exactly as sent: no older image is flattened, so the prefix stays byte-identical.
    /// </summary>
    internal void AppendGatewayTurns(string sid, LlmRequest request)
    {
        foreach (var turn in request.Turns)
        {
            if (turn.Role == "assistant") { AppendAssistantMessageToToolSession(sid, turn.Content); continue; }
            if (turn.Role != "user") throw new ArgumentException($"Unsupported role '{turn.Role}'.", nameof(request));
            string asOfText = request.Stamp == LlmStamp.AsOf && request.AsOfMs is long asOf
                ? $"[as of {TemporalFormat.Stamp(DateTimeOffset.FromUnixTimeMilliseconds(asOf).UtcDateTime)}] {turn.Content}"
                : turn.Content;
            if (turn.Images is { Count: > 0 } images)
            {
                AppendUserContentToToolSession(sid, asOfText, images.Select(i => (i.Data, i.MimeType)).ToList(), keepRecentImages: int.MaxValue,
                    preservePromptPrefix: true, stampReceipt: request.Stamp == LlmStamp.Now);
                continue;
            }
            if (request.Stamp == LlmStamp.Now) AppendUserMessageToToolSession(sid, turn.Content);
            else AppendCacheStableWakeSeedToToolSession(sid, asOfText);
        }
    }

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
