using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.ServiceTools;

/// <summary>A machine-readable explanation of why a model-authored tool call was rejected. Mirrors the
/// envelope Projects already returns (TOOL_ARGUMENT_ERROR + a JSON body), so the agent sees one error
/// format across both tool systems and can learn a single recovery habit.</summary>
public sealed record ToolArgumentError(string Code, string Path, string Message, string? Suggestion = null)
{
    public string ToToolResult()
    {
        var payload = new JObject
        {
            ["code"] = Code,
            ["path"] = Path,
            ["message"] = Message,
        };
        if (!string.IsNullOrWhiteSpace(Suggestion)) payload["suggestion"] = Suggestion;
        return "TOOL_ARGUMENT_ERROR " + payload.ToString(Formatting.None);
    }
}

/// <summary>Validated arguments, or the error to return to the model instead of dispatching.</summary>
public sealed class ToolArgumentResult
{
    private ToolArgumentResult(JObject? normalized, ToolArgumentError? error, IReadOnlyList<string>? warnings)
    {
        Normalized = normalized;
        Error = error;
        Warnings = warnings ?? Array.Empty<string>();
    }

    public bool IsValid => Error == null;
    public JObject? Normalized { get; }
    public ToolArgumentError? Error { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string? ErrorText => Error?.ToToolResult();

    internal static ToolArgumentResult Valid(JObject normalized, IReadOnlyList<string>? warnings = null)
        => new(normalized, null, warnings);
    internal static ToolArgumentResult Invalid(ToolArgumentError error) => new(null, error, null);
}

/// <summary>
/// Validates model-authored arguments against the JSON-Schema subset that <see cref="OmniToolSchema"/>
/// generates: an object of typed properties, with enums, arrays and one level of nested objects.
///
/// Deliberately separate from Projects' ProjectToolContract. That validator carries repair rules for
/// Projects' hand-written tool surface (run_bash aliases, wait-condition and plan-step shapes) which do
/// not apply here, and this module must not depend on Projects - ServiceTools is the shared layer both
/// agents sit on. What the two share is the error envelope, so the model sees one format.
///
/// Validation runs before dispatch: a caller invokes only when <see cref="ToolArgumentResult.IsValid"/>.
/// </summary>
public static class ToolArgumentContract
{
    public const string InvalidJson = "invalid_json";
    public const string ExpectedObject = "expected_object";
    public const string UnknownProperty = "unknown_property";
    public const string MissingRequired = "missing_required";
    public const string TypeMismatch = "type_mismatch";
    public const string EnumMismatch = "enum_mismatch";

    /// <summary>Validates and normalizes arguments against an operation's schema. Coerces the scalar
    /// shapes providers habitually get wrong ("5" for an integer, "true" for a boolean, 3 for a number)
    /// and records each coercion as a warning rather than failing the call.</summary>
    public static ToolArgumentResult ValidateAndNormalize(JObject schema, string? argumentsJson)
    {
        JObject arguments;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            arguments = new JObject();
        }
        else
        {
            JToken parsed;
            try
            {
                parsed = JToken.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return ToolArgumentResult.Invalid(new ToolArgumentError(InvalidJson, "$",
                    $"Arguments were not valid JSON: {FirstLine(ex.Message)}",
                    "Send the arguments as a single JSON object."));
            }

            // A model that has been told to send an object occasionally sends the object encoded as a
            // JSON string. Unwrap that once before giving up on it.
            if (parsed.Type == JTokenType.String)
            {
                try { parsed = JToken.Parse(parsed.Value<string>() ?? ""); }
                catch { /* genuinely a string - the object check below reports it */ }
            }

            if (parsed is not JObject obj)
                return ToolArgumentResult.Invalid(new ToolArgumentError(ExpectedObject, "$",
                    $"Arguments must be a JSON object, got {DescribeType(parsed)}.",
                    "Wrap the values in an object, e.g. {\"personId\":\"...\"}."));

            arguments = obj;
        }

        var properties = schema["properties"] as JObject ?? new JObject();
        var required = (schema["required"] as JArray)?.Select(t => t.Value<string>() ?? "").ToList() ?? new List<string>();
        var warnings = new List<string>();
        var normalized = new JObject();

        // Unknown properties are rejected rather than ignored: a silently dropped argument produces a
        // call that looks like it succeeded but did something else.
        foreach (var prop in arguments.Properties())
        {
            var declared = MatchProperty(properties, prop.Name);
            if (declared == null)
            {
                var nearest = NearestName(prop.Name, properties.Properties().Select(p => p.Name).ToList());
                return ToolArgumentResult.Invalid(new ToolArgumentError(UnknownProperty,
                    "$." + prop.Name,
                    $"'{prop.Name}' is not an argument of this operation.",
                    nearest != null
                        ? $"Did you mean '{nearest}'?"
                        : properties.Count == 0
                            ? "This operation takes no arguments."
                            : $"Valid arguments: {string.Join(", ", properties.Properties().Select(p => p.Name))}."));
            }

            if (!string.Equals(declared, prop.Name, StringComparison.Ordinal))
                warnings.Add($"'{prop.Name}' read as '{declared}'.");

            var propSchema = properties[declared] as JObject ?? new JObject();
            var value = prop.Value;

            if (IsAbsent(value))
            {
                // An explicit null for an optional argument means "not supplied".
                continue;
            }

            var coerced = Coerce(value, propSchema, "$." + declared, warnings, out var error);
            if (error != null) return ToolArgumentResult.Invalid(error);

            normalized[declared] = coerced;
        }

        foreach (var name in required)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (normalized.ContainsKey(name)) continue;
            var propSchema = properties[name] as JObject;
            var hint = propSchema?["description"]?.Value<string>();
            return ToolArgumentResult.Invalid(new ToolArgumentError(MissingRequired, "$." + name,
                $"Required argument '{name}' was not supplied.",
                string.IsNullOrWhiteSpace(hint) ? null : hint));
        }

        return ToolArgumentResult.Valid(normalized, warnings);
    }

    private static JToken Coerce(JToken value, JObject schema, string path, List<string> warnings, out ToolArgumentError? error)
    {
        error = null;
        var declaredType = schema["type"]?.Value<string>();

        if (schema["enum"] is JArray enumValues && enumValues.Count > 0)
        {
            var supplied = value.Type == JTokenType.String ? value.Value<string>() ?? "" : value.ToString(Formatting.None);
            var match = enumValues.FirstOrDefault(v =>
                string.Equals(v.Value<string>(), supplied, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                error = new ToolArgumentError(EnumMismatch, path,
                    $"'{Truncate(supplied, 60)}' is not an accepted value.",
                    $"Use one of: {string.Join(" | ", enumValues.Select(v => v.Value<string>()))}.");
                return value;
            }
            var canonical = match.Value<string>() ?? supplied;
            if (!string.Equals(canonical, supplied, StringComparison.Ordinal))
                warnings.Add($"{path} normalized to '{canonical}'.");
            return JValue.CreateString(canonical);
        }

        switch (declaredType)
        {
            case "string":
                if (value.Type == JTokenType.String) return value;
                if (value.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Boolean or JTokenType.Date)
                {
                    warnings.Add($"{path} read as text.");
                    return JValue.CreateString(value.ToString());
                }
                error = TypeError(path, "a string", value);
                return value;

            case "integer":
                if (value.Type == JTokenType.Integer) return value;
                if (value.Type == JTokenType.Float)
                {
                    var d = value.Value<double>();
                    if (Math.Abs(d - Math.Round(d)) < double.Epsilon)
                    {
                        warnings.Add($"{path} read as a whole number.");
                        return new JValue((long)Math.Round(d));
                    }
                    error = TypeError(path, "a whole number", value);
                    return value;
                }
                if (value.Type == JTokenType.String && long.TryParse(value.Value<string>(), out var parsedLong))
                {
                    warnings.Add($"{path} read as a number.");
                    return new JValue(parsedLong);
                }
                error = TypeError(path, "an integer", value);
                return value;

            case "number":
                if (value.Type is JTokenType.Integer or JTokenType.Float) return value;
                if (value.Type == JTokenType.String
                    && double.TryParse(value.Value<string>(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var parsedDouble))
                {
                    warnings.Add($"{path} read as a number.");
                    return new JValue(parsedDouble);
                }
                error = TypeError(path, "a number", value);
                return value;

            case "boolean":
                if (value.Type == JTokenType.Boolean) return value;
                if (value.Type == JTokenType.String && bool.TryParse(value.Value<string>(), out var parsedBool))
                {
                    warnings.Add($"{path} read as a boolean.");
                    return new JValue(parsedBool);
                }
                error = TypeError(path, "true or false", value);
                return value;

            case "array":
                if (value.Type == JTokenType.Array) return value;
                // A single value where a list was wanted is a near-universal provider habit.
                warnings.Add($"{path} wrapped into a single-item list.");
                return new JArray(value);

            case "object":
                if (value.Type == JTokenType.Object) return value;
                if (value.Type == JTokenType.String)
                {
                    try
                    {
                        var reparsed = JToken.Parse(value.Value<string>() ?? "");
                        if (reparsed.Type == JTokenType.Object)
                        {
                            warnings.Add($"{path} decoded from a JSON string.");
                            return reparsed;
                        }
                    }
                    catch { /* fall through to the type error */ }
                }
                error = TypeError(path, "an object", value);
                return value;

            default:
                return value;
        }
    }

    private static ToolArgumentError TypeError(string path, string expected, JToken actual)
        => new(TypeMismatch, path, $"Expected {expected}, got {DescribeType(actual)}.");

    private static bool IsAbsent(JToken token)
        => token.Type is JTokenType.Null or JTokenType.Undefined;

    /// <summary>Matches a supplied property name to a declared one, exactly first then case-insensitively.</summary>
    private static string? MatchProperty(JObject properties, string supplied)
    {
        if (properties[supplied] != null) return supplied;
        foreach (var p in properties.Properties())
            if (string.Equals(p.Name, supplied, StringComparison.OrdinalIgnoreCase)) return p.Name;
        return null;
    }

    private static string DescribeType(JToken token) => token.Type switch
    {
        JTokenType.Object => "an object",
        JTokenType.Array => "a list",
        JTokenType.String => "a string",
        JTokenType.Integer => "an integer",
        JTokenType.Float => "a number",
        JTokenType.Boolean => "a boolean",
        JTokenType.Null or JTokenType.Undefined => "null",
        _ => token.Type.ToString().ToLowerInvariant(),
    };

    /// <summary>Nearest declared name within one or two edits, so a typo gets a pointed suggestion
    /// instead of a bare list.</summary>
    private static string? NearestName(string supplied, IReadOnlyCollection<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Levenshtein(supplied.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        var threshold = supplied.Length <= 4 ? 1 : 2;
        return bestDistance <= threshold ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static string FirstLine(string value)
    {
        var index = value.IndexOf('\n');
        return index < 0 ? value : value[..index].TrimEnd('\r');
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "...";
}
