using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// Renders whatever an OmniService method returned into text a model can read.
///
/// Services return wildly different shapes - a JSON string already built by a route payload builder,
/// a JObject, a List of DTOs, a bare int. All of them arrive here and leave as one compact string
/// inside the agent's per-tool token budget. Oversized collections are cut with an explicit
/// "N more" marker rather than silently truncated, so the agent knows to narrow its query instead
/// of assuming it saw everything.
/// </summary>
public static class OmniToolResultFormatter
{
    /// <summary>Items kept per array before the rest are summarised away.</summary>
    private const int MaxArrayItems = 40;

    /// <summary>Nesting depth kept before a subtree collapses to a placeholder.</summary>
    private const int MaxDepth = 6;

    /// <summary>Property names whose values never reach the model. Deliberately narrower than a
    /// blanket "key" match, which would redact harmless things like cacheKey or sortKey.</summary>
    private static readonly string[] SecretMarkers =
    {
        "password", "passwd", "secret", "token", "apikey", "api_key", "credential",
        "privatekey", "private_key", "authorization", "auth_header", "encrypted",
    };

    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    };

    /// <summary>Formats a return value and clips it to <paramref name="maxTokens"/>.</summary>
    public static string Format(object? value, int maxTokens)
    {
        var text = Render(value);
        if (string.IsNullOrWhiteSpace(text)) return "(no result)";
        return KliveAgentContextBudget.TruncateToTokens(text, maxTokens);
    }

    private static string Render(object? value)
    {
        switch (value)
        {
            case null:
                return "(no result)";

            // Route payload builders already hand back JSON. Pass it through rather than
            // re-serialising it into an escaped string-inside-a-string.
            case string s:
                return s.Length == 0 ? "(empty)" : s;

            case bool b:
                return b ? "true" : "false";

            case JToken token:
                return Compact(token);
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is DateTimeOffset
            || value is TimeSpan || value is Guid)
            return value.ToString() ?? "(no result)";

        try
        {
            return Compact(JToken.FromObject(value, JsonSerializer.CreateDefault(Settings)));
        }
        catch (Exception ex)
        {
            // A type that will not serialise is still worth reporting honestly - the agent can then
            // fall back to execute_csharp rather than believing the call returned nothing.
            return $"(result of type {OmniToolSchema.FriendlyTypeName(type)} could not be serialised: {ex.Message})";
        }
    }

    private static string Compact(JToken token)
    {
        var pruned = Prune(token, 0);
        return pruned.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean
            ? pruned.ToString()
            : pruned.ToString(Formatting.None);
    }

    private static JToken Prune(JToken token, int depth)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
            {
                if (depth >= MaxDepth) return JValue.CreateString("{...}");
                var src = (JObject)token;
                var dst = new JObject();
                foreach (var prop in src.Properties())
                {
                    dst[prop.Name] = IsSecret(prop.Name)
                        ? JValue.CreateString("[redacted]")
                        : Prune(prop.Value, depth + 1);
                }
                return dst;
            }

            case JTokenType.Array:
            {
                if (depth >= MaxDepth) return JValue.CreateString("[...]");
                var src = (JArray)token;
                var dst = new JArray();
                foreach (var item in src.Take(MaxArrayItems)) dst.Add(Prune(item, depth + 1));
                if (src.Count > MaxArrayItems)
                    dst.Add(JValue.CreateString($"... {src.Count - MaxArrayItems} more items not shown - narrow the query or raise the limit"));
                return dst;
            }

            default:
                return token.DeepClone();
        }
    }

    private static bool IsSecret(string propertyName)
    {
        var lowered = propertyName.ToLowerInvariant();
        foreach (var marker in SecretMarkers)
            if (lowered.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Redacts a model-supplied argument object for the audit trail. Same rules as the result
    /// path, so a secret passed IN is no more visible than one returned.</summary>
    public static string RedactArguments(JObject? args)
    {
        if (args == null || args.Count == 0) return "{}";
        var redacted = new JObject();
        foreach (var prop in args.Properties())
        {
            redacted[prop.Name] = IsSecret(prop.Name)
                ? JValue.CreateString("[redacted]")
                : Truncate(prop.Value);
        }
        return redacted.ToString(Formatting.None);
    }

    private static JToken Truncate(JToken value)
    {
        if (value.Type != JTokenType.String) return value;
        var s = value.Value<string>() ?? "";
        return s.Length <= 200 ? value : JValue.CreateString(s[..200] + $"... (+{s.Length - 200} chars)");
    }
}
