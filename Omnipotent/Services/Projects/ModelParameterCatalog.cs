using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects;

public enum ModelParameterKind
{
    Number,
    Integer,
    Enum,
}

/// <summary>
/// One request parameter Klives may pin on a route, with everything the UI needs to render a control
/// for it. The catalog decides WHICH of these a given route may set (the live OpenRouter
/// <c>supported_parameters</c>); this record describes HOW each one is edited and validated.
/// </summary>
public sealed record ModelParameterDefinition(
    string Name,
    string Label,
    ModelParameterKind Kind,
    double? Min,
    double? Max,
    double? Step,
    string Description,
    string DefaultHint,
    bool OpenRouterOnly,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// The tunable request parameters Projects exposes per LLM route, and the validation that keeps a
/// stored value inside its legal range.
///
/// SCOPE: sampling/decoding knobs only. Protocol-owned fields OpenRouter also lists under
/// <c>supported_parameters</c> — tools, tool_choice, response_format, structured_outputs, logprobs,
/// include_reasoning, stop — are deliberately absent: the agent loop owns those, and letting a saved
/// setting overwrite them would break tool calling rather than tune it. <c>max_tokens</c> is likewise
/// excluded because the per-role *MaxOutputTokens settings already govern it (and are clamped against
/// the resolved context window at dispatch).
///
/// Which of these appear for a route is NOT decided here — see
/// <see cref="OpenRouterContextWindowResolver.ResolveParametersAsync"/>. A model absent from the live
/// catalog reports "unknown", and every parameter stays offered: OpenRouter ignores a parameter a model
/// does not implement, so offering one is never an error, whereas hiding one on a failed fetch would
/// silently strip Klives' configuration.
/// </summary>
public static class ModelParameterCatalog
{
    /// <summary>The reasoning-effort levels, matching KliveLLM's ThinkingType ladder. A route's value is
    /// still clamped by the global ThinkingType ceiling at dispatch — a route can ask for less thinking
    /// than the global setting, never more.</summary>
    public static readonly string[] ReasoningLevels = ["off", "low", "medium", "high"];

    public static readonly IReadOnlyList<ModelParameterDefinition> Definitions =
    [
        new("temperature", "Temperature", ModelParameterKind.Number, 0, 2, 0.05,
            "Randomness of token selection. Lower is more deterministic and repeatable; higher is more varied.",
            "provider default (usually 1)", OpenRouterOnly: false),
        new("top_p", "Top P", ModelParameterKind.Number, 0, 1, 0.01,
            "Nucleus sampling: consider only the most probable tokens whose cumulative probability reaches this value.",
            "provider default (usually 1)", OpenRouterOnly: false),
        new("top_k", "Top K", ModelParameterKind.Integer, 0, 200, 1,
            "Consider only the K most probable tokens. 0 disables the limit. Not implemented by OpenAI models.",
            "0 (disabled)", OpenRouterOnly: true),
        new("frequency_penalty", "Frequency penalty", ModelParameterKind.Number, -2, 2, 0.05,
            "Penalises tokens in proportion to how often they have already appeared. Positive values reduce verbatim repetition.",
            "0", OpenRouterOnly: false),
        new("presence_penalty", "Presence penalty", ModelParameterKind.Number, -2, 2, 0.05,
            "Penalises tokens that have appeared at all, regardless of count. Positive values push toward new topics.",
            "0", OpenRouterOnly: false),
        new("repetition_penalty", "Repetition penalty", ModelParameterKind.Number, 0.01, 2, 0.01,
            "Multiplicative penalty on already-seen tokens. 1 disables it; above 1 discourages repetition.",
            "1 (disabled)", OpenRouterOnly: true),
        new("min_p", "Min P", ModelParameterKind.Number, 0, 1, 0.01,
            "Drops tokens whose probability is below this fraction of the most likely token's probability.",
            "0 (disabled)", OpenRouterOnly: true),
        new("top_a", "Top A", ModelParameterKind.Number, 0, 1, 0.01,
            "Dynamic filter that tightens as the top token's confidence rises. 0 disables it.",
            "0 (disabled)", OpenRouterOnly: true),
        new("seed", "Seed", ModelParameterKind.Integer, 0, int.MaxValue, 1,
            "Requests deterministic sampling for identical inputs. Best-effort — most providers do not fully guarantee it.",
            "unset (nondeterministic)", OpenRouterOnly: false),
        new("reasoning", "Reasoning effort", ModelParameterKind.Enum, null, null, null,
            "How hard a reasoning-capable model thinks before answering. Clamped by the global ThinkingType ceiling.",
            "the global ThinkingType setting", OpenRouterOnly: true, Options: ReasoningLevels),
    ];

    private static readonly Dictionary<string, ModelParameterDefinition> byName =
        Definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static ModelParameterDefinition? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out var d) ? d : null;

    public static bool IsKnown(string? name) => Find(name) != null;

    /// <summary>
    /// Validates and normalizes one parameter value, clamping numbers into range and matching enum
    /// options case-insensitively. Returns false for an unknown parameter or an unusable value, so a
    /// bad entry is dropped rather than persisted and later rejected by the provider mid-wake.
    /// </summary>
    public static bool TryNormalize(string name, JToken? value, out string normalizedName, out JToken normalizedValue)
    {
        normalizedName = string.Empty;
        normalizedValue = JValue.CreateNull();
        var definition = Find(name);
        if (definition == null || value == null || value.Type is JTokenType.Null or JTokenType.Undefined) return false;
        normalizedName = definition.Name;

        if (definition.Kind == ModelParameterKind.Enum)
        {
            string text = (value.Type == JTokenType.String ? value.Value<string>() : value.ToString()) ?? string.Empty;
            string? match = definition.Options?.FirstOrDefault(o => string.Equals(o, text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            normalizedValue = new JValue(match);
            return true;
        }

        double parsed;
        if (value.Type is JTokenType.Integer or JTokenType.Float)
            parsed = value.Value<double>();
        else if (!double.TryParse(
                     (value.Type == JTokenType.String ? value.Value<string>() : value.ToString()),
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out parsed))
            return false;

        if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
        if (definition.Min.HasValue) parsed = Math.Max(definition.Min.Value, parsed);
        if (definition.Max.HasValue) parsed = Math.Min(definition.Max.Value, parsed);

        if (definition.Kind == ModelParameterKind.Integer)
            normalizedValue = new JValue((long)Math.Round(parsed, MidpointRounding.AwayFromZero));
        else
            normalizedValue = new JValue(Math.Round(parsed, 4));
        return true;
    }

    /// <summary>Normalizes a whole parameter object, silently dropping unknown or unusable entries.
    /// Ordering follows <see cref="Definitions"/> so a saved settings file stays diff-stable.</summary>
    public static Dictionary<string, JToken> Normalize(IEnumerable<KeyValuePair<string, JToken>>? values)
    {
        var result = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values ?? [])
            if (TryNormalize(kv.Key, kv.Value, out string name, out JToken value))
                result[name] = value;

        return Definitions
            .Where(d => result.ContainsKey(d.Name))
            .ToDictionary(d => d.Name, d => result[d.Name], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Projects a stored parameter object onto the typed payload KliveLLM sends. Only values
    /// Klives explicitly pinned are carried; everything else stays absent from the request so the
    /// provider's own default applies.</summary>
    public static ModelSamplingParameters? ToSamplingParameters(IReadOnlyDictionary<string, JToken>? values)
    {
        if (values == null || values.Count == 0) return null;

        double? Number(string name) =>
            values.TryGetValue(name, out var v) && v.Type is JTokenType.Integer or JTokenType.Float
                ? v.Value<double>() : null;
        int? Integer(string name) =>
            values.TryGetValue(name, out var v) && v.Type is JTokenType.Integer or JTokenType.Float
                ? (int)Math.Clamp(v.Value<double>(), int.MinValue, int.MaxValue) : null;
        string? Text(string name) =>
            values.TryGetValue(name, out var v) && v.Type == JTokenType.String ? v.Value<string>() : null;

        var parameters = new ModelSamplingParameters(
            Temperature: Number("temperature"),
            TopP: Number("top_p"),
            TopK: Integer("top_k"),
            FrequencyPenalty: Number("frequency_penalty"),
            PresencePenalty: Number("presence_penalty"),
            RepetitionPenalty: Number("repetition_penalty"),
            MinP: Number("min_p"),
            TopA: Number("top_a"),
            Seed: Integer("seed"));
        return parameters.IsEmpty ? null : parameters;
    }

    /// <summary>The reasoning effort a route pins, or null to keep the global ThinkingType setting.
    /// Carried separately from <see cref="ToSamplingParameters"/> because KliveLLM already owns
    /// reasoning through its thinking-override path, where it is clamped against the global ceiling.</summary>
    public static string? ReasoningEffort(IReadOnlyDictionary<string, JToken>? values) =>
        values != null && values.TryGetValue("reasoning", out var v) && v.Type == JTokenType.String
            ? v.Value<string>() : null;
}
