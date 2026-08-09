using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Builds stable, semantic signatures for convergence guards. Rejected calls are tracked separately
/// from valid calls so a model cannot evade loop detection by failing before dispatch, while a fixed
/// call does not inherit the rejected call's count.
/// </summary>
internal static class ProjectToolCallConvergence
{
    public static int RegisterCall(IDictionary<string, int> recentSignatures,
        string toolName, string? argumentsJson) =>
        Register(recentSignatures, "call", toolName, argumentsJson, null);

    public static int RegisterRejectedCall(IDictionary<string, int> recentSignatures,
        string toolName, string? argumentsJson, string rejection) =>
        Register(recentSignatures, "rejected", toolName, OperationOf(argumentsJson),
            RejectionIdentity(rejection));

    private static int Register(IDictionary<string, int> recentSignatures, string kind,
        string toolName, string? argumentsJson, string? rejection)
    {
        ArgumentNullException.ThrowIfNull(recentSignatures);
        string signature = kind + "|" + toolName + "|" + NormalizeJson(argumentsJson);
        if (rejection != null) signature += "|" + rejection;
        int count = recentSignatures.TryGetValue(signature, out int previous) ? previous + 1 : 1;
        recentSignatures[signature] = count;
        return count;
    }

    /// <summary>Sorts object properties recursively so JSON key order cannot defeat convergence.</summary>
    private static string NormalizeJson(string? json)
    {
        string value = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
        try
        {
            return Sort(JToken.Parse(value)).ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>
    /// Rejected-call convergence is structural: changing an otherwise valid note, summary, or other
    /// payload value does not repair the same validation error. Keep only the folded/canonical op as
    /// the call discriminator and let the validation reason identify the actual defect. This still
    /// separates different operations and different error paths while closing the loophole where a
    /// model regenerated prose to evade the guard indefinitely.
    /// </summary>
    private static string OperationOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "{}";
        try
        {
            var root = JToken.Parse(argumentsJson);
            if (root is not JObject obj || obj["op"] is not JValue op
                || op.Type is JTokenType.Null or JTokenType.Undefined)
                return "{}";
            return new JObject { ["op"] = op.DeepClone() }.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>Contract failures already carry a stable machine-readable code and JSON path.
    /// Those identify the defect; prose can change without representing a corrected call.</summary>
    private static string RejectionIdentity(string rejection)
    {
        string value = rejection.Trim();
        const string prefix = "TOOL_ARGUMENT_ERROR ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return value;
        try
        {
            var error = JObject.Parse(value[prefix.Length..]);
            string? code = error["code"]?.Value<string>();
            string? path = error["path"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(path))
                return $"{code}|{path}";
        }
        catch (JsonException) { }
        return value;
    }

    private static JToken Sort(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, Sort(property.Value)))),
        JArray array => new JArray(array.Select(Sort)),
        _ => token.DeepClone(),
    };
}
