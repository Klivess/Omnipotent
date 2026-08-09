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
        Register(recentSignatures, "rejected", toolName, argumentsJson, rejection.Trim());

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

    private static JToken Sort(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, Sort(property.Value)))),
        JArray array => new JArray(array.Select(Sort)),
        _ => token.DeepClone(),
    };
}
