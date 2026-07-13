using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects;

/// <summary>A machine-readable explanation of why a model-authored tool call was rejected.</summary>
public sealed record ProjectToolContractError(
    string Code,
    string Path,
    string Message,
    string? Suggestion = null)
{
    /// <summary>Compact text suitable for returning as the tool result without dispatching the call.</summary>
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

/// <summary>The validated, normalized arguments or the error that must be returned to the model.</summary>
public sealed class ProjectToolContractResult
{
    private ProjectToolContractResult(string? normalizedArgumentsJson, ProjectToolContractError? error,
        IReadOnlyList<string>? warnings = null)
    {
        NormalizedArgumentsJson = normalizedArgumentsJson;
        Error = error;
        Warnings = warnings ?? Array.Empty<string>();
    }

    public bool IsValid => Error == null;
    public string? NormalizedArgumentsJson { get; }
    public ProjectToolContractError? Error { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string? ErrorText => Error?.ToToolResult();

    internal static ProjectToolContractResult Valid(string json, IReadOnlyList<string>? warnings = null) => new(json, null, warnings);
    internal static ProjectToolContractResult Invalid(ProjectToolContractError error) => new(null, error);
}

/// <summary>
/// Validates model-authored tool arguments against the JSON-schema subset used by project tools.
/// The validator is deliberately independent of dispatch: callers validate first and only invoke a
/// handler when <see cref="ProjectToolContractResult.IsValid"/> is true.
/// </summary>
public static class ProjectToolContract
{
    public const string ToolNotOffered = "tool_not_offered";
    public const string InvalidJson = "invalid_json";
    public const string ExpectedObject = "expected_object";
    public const string InvalidSchema = "invalid_schema";
    public const string UnknownProperty = "unknown_property";
    public const string MissingRequired = "missing_required";
    public const string TypeMismatch = "type_mismatch";
    public const string EnumMismatch = "enum_mismatch";
    public const string AliasConflict = "alias_conflict";

    public static CommanderToolResult AttachWarnings(ProjectToolContractResult contract, CommanderToolResult result)
    {
        if (contract.Warnings.Count == 0) return result;
        string prefix = "TOOL_ARGUMENT_NORMALIZED: " + string.Join(" ", contract.Warnings) + "\n";
        return result with
        {
            ResultText = prefix + result.ResultText,
            AuditText = prefix + (result.AuditText ?? result.ResultText),
        };
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LegacyAliases =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["run_bash"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "script",
                ["code"] = "script",
            },
            ["run_powershell"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "script",
                ["code"] = "script",
            },
            ["computer_terminal"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["script"] = "command",
                ["code"] = "command",
            },
        };

    /// <summary>Finds <paramref name="toolName"/> in the tools actually offered to the model, then validates its arguments.</summary>
    public static ProjectToolContractResult ValidateAndNormalize(
        string toolName,
        string? argumentsJson,
        IEnumerable<HFWrapper.HFTool> offeredTools)
    {
        ArgumentNullException.ThrowIfNull(offeredTools);
        var tool = offeredTools.FirstOrDefault(t =>
            string.Equals(t?.function?.name, toolName, StringComparison.Ordinal));
        if (tool == null)
            return Fail(ToolNotOffered, "$", $"Tool '{Truncate(toolName, 80)}' was not offered for this turn.");
        return ValidateAndNormalize(tool, argumentsJson);
    }

    /// <summary>Validates arguments against one offered tool definition.</summary>
    public static ProjectToolContractResult ValidateAndNormalize(HFWrapper.HFTool offeredTool, string? argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(offeredTool);
        string toolName = offeredTool.function?.name ?? "";

        JObject arguments;
        try
        {
            var token = ParseJson(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson!);
            if (token is not JObject obj)
                return Fail(ExpectedObject, "$", "Tool arguments must be a JSON object.");
            arguments = obj;
        }
        catch (JsonException ex)
        {
            return Fail(InvalidJson, "$", "Tool arguments are not valid JSON: " + Truncate(FirstLine(ex.Message), 140));
        }

        JObject schema;
        try
        {
            if (offeredTool.function?.parameters == null)
                return Fail(InvalidSchema, "$", $"Tool '{Truncate(toolName, 80)}' has no parameter schema.");
            schema = offeredTool.function.parameters is JObject jo
                ? (JObject)jo.DeepClone()
                : JObject.Parse(JsonConvert.SerializeObject(offeredTool.function.parameters));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Fail(InvalidSchema, "$", "The offered tool has an invalid parameter schema.");
        }

        var aliasError = ApplyLegacyAliases(toolName, arguments, schema);
        if (aliasError != null) return ProjectToolContractResult.Invalid(aliasError);

        var warnings = new List<string>();
        ApplyToolSpecificNormalizations(toolName, arguments, warnings);
        NormalizeSafeScalarTypes(arguments, schema);

        var error = ValidateToken(arguments, schema, "$", schemaIsToolRoot: true);
        return error == null
            ? ProjectToolContractResult.Valid(arguments.ToString(Formatting.None), warnings)
            : ProjectToolContractResult.Invalid(error);
    }

    private static ProjectToolContractError? ApplyLegacyAliases(string toolName, JObject arguments, JObject schema)
    {
        if (!LegacyAliases.TryGetValue(toolName, out var aliases)) return null;
        var schemaProperties = schema["properties"] as JObject;
        foreach (var (legacy, canonical) in aliases)
        {
            var legacyProperty = arguments.Property(legacy, StringComparison.Ordinal);
            if (legacyProperty == null || schemaProperties?.Property(canonical, StringComparison.Ordinal) == null) continue;
            if (arguments.Property(canonical, StringComparison.Ordinal) != null)
            {
                var canonicalProperty = arguments.Property(canonical, StringComparison.Ordinal)!;
                if (JToken.DeepEquals(canonicalProperty.Value, legacyProperty.Value))
                {
                    legacyProperty.Remove();
                    continue;
                }
                return new ProjectToolContractError(
                    AliasConflict,
                    AppendPropertyPath("$", legacy),
                    $"Both legacy argument '{legacy}' and canonical argument '{canonical}' were provided; use only '{canonical}'.",
                    canonical);
            }

            // Preserve the value exactly while emitting only the canonical field to the dispatcher.
            arguments.Add(canonical, legacyProperty.Value.DeepClone());
            legacyProperty.Remove();
        }
        return null;
    }

    /// <summary>
    /// Repairs common, unambiguous model serialization variants before schema validation. This is
    /// deliberately narrow: it never invents business data or guesses between conflicting values.
    /// </summary>
    private static void ApplyToolSpecificNormalizations(string toolName, JObject arguments, List<string> warnings)
    {
        if (toolName == "read_file" && arguments.Property("run_as_cwd", StringComparison.Ordinal) is { } misplacedExecution)
        {
            misplacedExecution.Remove();
            warnings.Add("Ignored legacy read_file argument 'run_as_cwd': read_file never executes a file. Use run_bash or computer_terminal as a separate tool call if execution is actually intended.");
        }

        if (toolName == "update_observable" && arguments.Property("op", StringComparison.Ordinal) == null &&
            (arguments.Property("value", StringComparison.Ordinal) != null ||
             arguments.Property("textValue", StringComparison.Ordinal) != null))
            arguments["op"] = "set";

        if (toolName != "update_plan" || arguments["nextSteps"] is not { } supplied) return;

        if (supplied is JObject oneObject)
            arguments["nextSteps"] = new JArray(NormalizePlanStep(oneObject));
        else if (supplied is JArray array)
        {
            var normalized = new JArray();
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String) normalized.Add(item.DeepClone());
                else if (item is JObject obj) normalized.Add(NormalizePlanStep(obj));
                else normalized.Add(item.DeepClone()); // validator will explain genuinely invalid data
            }
            arguments["nextSteps"] = normalized;
        }
        else if (supplied.Type == JTokenType.String)
        {
            string text = supplied.Value<string>() ?? "";
            if (TryParseStringArray(text, out var parsed)) arguments["nextSteps"] = parsed;
            else
            {
                var steps = Regex.Split(text, "\\r?\\n")
                    .Select(line => Regex.Replace(line.Trim(), @"^(?:[-*]\s+|\[?\d+[\].):]?\s*)", ""))
                    .Where(line => !string.IsNullOrWhiteSpace(line));
                arguments["nextSteps"] = new JArray(steps);
            }
        }
    }

    private static JToken NormalizePlanStep(JObject item)
    {
        foreach (string key in new[] { "step", "text", "description", "title" })
            if (item[key]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(item[key]!.Value<string>()))
                return new JValue(item[key]!.Value<string>()!.Trim());
        // Unknown object shapes remain objects so schema validation rejects them explicitly. Do
        // not guess that an arbitrary owner/status/value string is the intended plan step.
        return item.DeepClone();
    }

    private static bool TryParseStringArray(string text, out JArray array)
    {
        array = new JArray();
        if (!text.TrimStart().StartsWith("[", StringComparison.Ordinal)) return false;
        try
        {
            var parsed = JArray.Parse(text);
            if (parsed.All(x => x.Type == JTokenType.String))
            {
                array = parsed;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Tool providers frequently serialize primitive values as JSON strings. Coerce only
    /// lossless integer/number/boolean strings when the offered schema explicitly asks for that
    /// primitive; arbitrary strings and objects remain strict.</summary>
    private static void NormalizeSafeScalarTypes(JToken token, JObject schema)
    {
        if (token is JObject obj && schema["properties"] is JObject properties)
        {
            foreach (var property in obj.Properties().ToList())
                if (properties.Property(property.Name, StringComparison.Ordinal)?.Value is JObject childSchema)
                {
                    var replacement = CoerceScalar(property.Value, childSchema);
                    if (!ReferenceEquals(replacement, property.Value)) property.Value = replacement;
                    NormalizeSafeScalarTypes(property.Value, childSchema);
                }
        }
        else if (token is JArray array && schema["items"] is JObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++)
            {
                var replacement = CoerceScalar(array[i]!, itemSchema);
                if (!ReferenceEquals(replacement, array[i])) array[i] = replacement;
                NormalizeSafeScalarTypes(array[i]!, itemSchema);
            }
        }
    }

    private static JToken CoerceScalar(JToken token, JObject schema)
    {
        if (token.Type != JTokenType.String) return token;
        string value = token.Value<string>()?.Trim() ?? "";
        var types = ReadDeclaredTypes(schema);
        if (types.Contains("integer", StringComparer.Ordinal) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            return new JValue(integer);
        if (types.Contains("number", StringComparer.Ordinal) &&
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            return new JValue(number);
        if (types.Contains("boolean", StringComparer.Ordinal))
        {
            if (bool.TryParse(value, out bool boolean)) return new JValue(boolean);
            if (value is "1" or "yes" or "on") return new JValue(true);
            if (value is "0" or "no" or "off") return new JValue(false);
        }
        return token;
    }

    private static ProjectToolContractError? ValidateToken(JToken token, JObject schema, string path, bool schemaIsToolRoot = false)
    {
        var declaredTypes = ReadDeclaredTypes(schema);
        if (declaredTypes.Count > 0 && !declaredTypes.Any(t => MatchesType(token, t)))
        {
            string expected = string.Join(" or ", declaredTypes);
            return new ProjectToolContractError(
                schemaIsToolRoot && declaredTypes.Contains("object", StringComparer.Ordinal) ? ExpectedObject : TypeMismatch,
                path,
                $"Expected {expected} at '{path}', received {DescribeType(token)}.");
        }

        if (schema["enum"] is JArray choices && !choices.Any(choice => JsonValuesEqual(choice, token)))
        {
            string allowed = string.Join(", ", choices.Take(8).Select(DescribeEnumValue));
            if (choices.Count > 8) allowed += ", …";
            return new ProjectToolContractError(EnumMismatch, path,
                $"Value at '{path}' must be one of: {allowed}.");
        }

        if (token is JObject obj)
        {
            var properties = schema["properties"] as JObject;
            var additional = schema["additionalProperties"];

            // Unknown names are checked before required names. For the common misspelling case this
            // returns the useful suggestion instead of only saying that the canonical field is absent.
            foreach (var supplied in obj.Properties())
            {
                var childSchema = properties?.Property(supplied.Name, StringComparison.Ordinal)?.Value as JObject;
                if (childSchema != null) continue;

                bool explicitlyAllowed = additional?.Type == JTokenType.Boolean && additional.Value<bool>();
                bool openObject = properties == null && additional == null;
                if (additional is JObject additionalSchema)
                {
                    var additionalError = ValidateToken(supplied.Value, additionalSchema,
                        AppendPropertyPath(path, supplied.Name));
                    if (additionalError != null) return additionalError;
                    continue;
                }
                if (explicitlyAllowed || openObject) continue;

                var names = properties?.Properties().Select(p => p.Name).ToList() ?? new List<string>();
                string? suggestion = NearestName(supplied.Name, names);
                string message = $"Unknown argument '{supplied.Name}' at '{path}'.";
                if (suggestion != null) message += $" Did you mean '{suggestion}'?";
                else if (names.Count > 0)
                    message += " Allowed arguments: " + string.Join(", ", names.Take(10)) + (names.Count > 10 ? ", …" : "") + ".";
                return new ProjectToolContractError(UnknownProperty,
                    AppendPropertyPath(path, supplied.Name), message, suggestion);
            }

            if (schema["required"] is JArray required)
            {
                var missing = required.Values<string>()
                    .Where(name => !string.IsNullOrEmpty(name) && obj.Property(name!, StringComparison.Ordinal) == null)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (missing.Count > 0)
                    return new ProjectToolContractError(MissingRequired, path,
                        "Missing required argument" + (missing.Count == 1 ? "" : "s") + ": " + string.Join(", ", missing) + ".");
            }

            if (properties != null)
            {
                foreach (var supplied in obj.Properties())
                {
                    if (properties.Property(supplied.Name, StringComparison.Ordinal)?.Value is not JObject childSchema) continue;
                    var childError = ValidateToken(supplied.Value, childSchema, AppendPropertyPath(path, supplied.Name));
                    if (childError != null) return childError;
                }
            }
        }
        else if (token is JArray array && schema["items"] is JObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++)
            {
                var itemError = ValidateToken(array[i]!, itemSchema, $"{path}[{i}]");
                if (itemError != null) return itemError;
            }
        }

        return null;
    }

    private static List<string> ReadDeclaredTypes(JObject schema)
    {
        if (schema["type"] is JValue { Type: JTokenType.String } single)
            return [single.Value<string>() ?? ""];
        if (schema["type"] is JArray many)
            return many.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList()!;
        // A properties/required declaration unambiguously describes an object even if the author
        // omitted the redundant type field.
        if (schema["properties"] != null || schema["required"] != null) return ["object"];
        return [];
    }

    private static bool MatchesType(JToken token, string type) => type switch
    {
        "object" => token.Type == JTokenType.Object,
        "array" => token.Type == JTokenType.Array,
        "string" => token.Type == JTokenType.String,
        "boolean" => token.Type == JTokenType.Boolean,
        "number" => token.Type is JTokenType.Integer or JTokenType.Float,
        // JSON Schema defines integer mathematically, so 2.0 and 2e0 are integers too.
        "integer" => token.Type == JTokenType.Integer ||
                     (token.Type == JTokenType.Float && IsMathematicalInteger(token)),
        "null" => token.Type == JTokenType.Null,
        _ => false,
    };

    private static bool IsMathematicalInteger(JToken token)
    {
        string value = token.ToString(Formatting.None);
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            return decimal.Truncate(number) == number;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double wide)
               && double.IsFinite(wide) && Math.Truncate(wide) == wide;
    }

    private static bool JsonValuesEqual(JToken first, JToken second)
    {
        if (JToken.DeepEquals(first, second)) return true;
        if (first.Type is JTokenType.Integer or JTokenType.Float &&
            second.Type is JTokenType.Integer or JTokenType.Float)
        {
            return decimal.TryParse(first.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                   && decimal.TryParse(second.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                   && a == b;
        }
        return false;
    }

    private static JToken ParseJson(string json)
    {
        try { return ParseJsonCore(json, DuplicatePropertyNameHandling.Error); }
        catch (JsonException)
        {
            // Native tool providers occasionally add one closing delimiter, omit one final
            // delimiter, or repeat the exact same property. Repair only those mechanically
            // provable envelope defects. Conflicting duplicate values and incomplete strings
            // remain hard errors so dispatch never guesses business data.
            foreach (string candidate in SafeEnvelopeRepairs(json))
            {
                try
                {
                    var firstWins = ParseJsonCore(candidate, DuplicatePropertyNameHandling.Ignore);
                    var lastWins = ParseJsonCore(candidate, DuplicatePropertyNameHandling.Replace);
                    if (JToken.DeepEquals(firstWins, lastWins)) return lastWins;
                }
                catch (JsonException) { }
            }
            throw;
        }
    }

    private static JToken ParseJsonCore(string json, DuplicatePropertyNameHandling duplicateHandling)
    {
        using var stringReader = new StringReader(json);
        using var reader = new JsonTextReader(stringReader)
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
            SupportMultipleContent = false,
        };
        var token = JToken.ReadFrom(reader, new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            DuplicatePropertyNameHandling = duplicateHandling,
        });
        while (reader.Read())
            if (reader.TokenType != JsonToken.Comment)
                throw new JsonReaderException("Additional content follows the JSON value.");
        return token;
    }

    private static IEnumerable<string> SafeEnvelopeRepairs(string json)
    {
        string text = json.Trim();
        // The original is useful for accepting equal duplicate properties.
        yield return text;

        string withoutExtra = text;
        for (int i = 0; i < 2 && withoutExtra.Length > 0
             && withoutExtra[^1] is '}' or ']'; i++)
        {
            withoutExtra = withoutExtra[..^1].TrimEnd();
            yield return withoutExtra;
        }

        var stack = new Stack<char>();
        bool inString = false, escaped = false, invalidClose = false;
        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c is '{' or '[') { stack.Push(c); continue; }
            if (c is not ('}' or ']')) continue;
            if (stack.Count == 0 || c == '}' && stack.Peek() != '{' || c == ']' && stack.Peek() != '[')
            {
                invalidClose = true;
                break;
            }
            stack.Pop();
        }
        if (inString || escaped || invalidClose || stack.Count is 0 or > 2) yield break;
        var suffix = new string(stack.Select(open => open == '{' ? '}' : ']').ToArray());
        yield return text + suffix;
    }

    private static string DescribeType(JToken token) => token.Type switch
    {
        JTokenType.Object => "object",
        JTokenType.Array => "array",
        JTokenType.String => "string",
        JTokenType.Boolean => "boolean",
        JTokenType.Integer => "integer",
        JTokenType.Float => "number",
        JTokenType.Null => "null",
        _ => token.Type.ToString().ToLowerInvariant(),
    };

    private static string DescribeEnumValue(JToken token)
    {
        string value = token.Type == JTokenType.String ? $"'{token.Value<string>()}'" : token.ToString(Formatting.None);
        return Truncate(value, 40);
    }

    private static string? NearestName(string supplied, IReadOnlyCollection<string> candidates)
    {
        if (candidates.Count == 0) return null;
        var nearest = candidates.Select(name => (name, distance: Levenshtein(supplied, name)))
            .OrderBy(x => x.distance).ThenBy(x => x.name, StringComparer.Ordinal).First();
        int threshold = Math.Clamp(Math.Max(supplied.Length, nearest.name.Length) / 3, 1, 4);
        return nearest.distance <= threshold ? nearest.name : null;
    }

    private static int Levenshtein(string first, string second)
    {
        first = first.ToLowerInvariant();
        second = second.ToLowerInvariant();
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];
        for (int i = 1; i <= first.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= second.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[second.Length];
    }

    private static string AppendPropertyPath(string path, string property)
    {
        bool simple = property.Length > 0 && (char.IsLetter(property[0]) || property[0] == '_') &&
                      property.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
        return simple ? path + "." + property : path + "['" + property.Replace("'", "\\'") + "']";
    }

    private static ProjectToolContractResult Fail(string code, string path, string message, string? suggestion = null) =>
        ProjectToolContractResult.Invalid(new ProjectToolContractError(code, path, message, suggestion));

    private static string FirstLine(string value)
    {
        int newline = value.IndexOfAny(['\r', '\n']);
        return newline < 0 ? value : value[..newline];
    }

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value)
        ? ""
        : value.Length <= max ? value : value[..max] + "…";
}
