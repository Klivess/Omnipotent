using System.Diagnostics;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// Calls Omnipotent's own HTTP routes over the loopback interface, authenticated as Klives.
///
/// Why HTTP to ourselves rather than an in-process call: a route handler is a
/// Func&lt;UserRequest, Task&gt; where UserRequest is a struct wrapping a live HttpListenerContext and
/// writes its own response into it. There is no way to invoke one without a real request, short of
/// refactoring the response sink out of UserRequest — which every one of the ~425 routes depends on.
/// A loopback request costs one localhost round trip and reaches all of them today, and it inherits
/// the whole pipeline for free: the permission ladder, the OmniDefence gate, and the response cache.
///
/// This is the only place in the process that calls its own API, and it is deliberate. The typed
/// registry stays the preferred path; this is the escape hatch for capability that exists only as a
/// route. Public-internet fetches are NOT this tool's job — that is web_fetch.
/// </summary>
public sealed class OmniApiClient
{
    // One shared client: the loopback listener is the same endpoint every time, and a per-call client
    // would burn ports. Generous timeout — some routes rebuild a cold cache entry.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // Never route a loopback call through a corporate/system proxy.
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "DELETE", "PATCH"
    };

    private readonly Func<List<OmniService>> resolveServices;
    private readonly OmniToolAudit audit;
    private readonly Action<string>? log;

    public OmniApiClient(Func<List<OmniService>> resolveServices, OmniToolAudit audit, Action<string>? log = null)
    {
        this.resolveServices = resolveServices;
        this.audit = audit;
        this.log = log;
    }

    /// <summary>Response body budget, in tokens.</summary>
    public int ResultTokenBudget { get; set; } = 1200;

    public async Task<OmniToolInvocation> ExecuteAsync(string? argumentsJson, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        JObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);
        }
        catch (Exception ex)
        {
            return new OmniToolInvocation(false, new ToolArgumentError(ToolArgumentContract.InvalidJson, "$",
                $"Arguments were not valid JSON: {ex.Message}").ToToolResult());
        }

        var method = (args["method"]?.Value<string>() ?? "GET").Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
            return new OmniToolInvocation(false, new ToolArgumentError(ToolArgumentContract.EnumMismatch, "$.method",
                $"'{method}' is not a supported HTTP method.",
                "Use one of: GET | POST | PUT | DELETE | PATCH.").ToToolResult());

        var path = args["path"]?.Value<string>()?.Trim() ?? "";
        var pathError = ValidatePath(path);
        if (pathError != null)
            return new OmniToolInvocation(false, pathError);

        var url = BuildUrl(path, args["query"] as JObject);

        var (password, authError) = ResolveKlivesCredential();
        if (authError != null)
            return new OmniToolInvocation(false, authError);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.TryAddWithoutValidation("Authorization", password);

        var body = args["body"];
        if (body != null && body.Type is not (JTokenType.Null or JTokenType.Undefined)
            && method is not "GET" and not "DELETE")
        {
            var payload = body.Type == JTokenType.String ? body.Value<string>() ?? "" : body.ToString(Newtonsoft.Json.Formatting.None);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        bool mutating = method != "GET";
        var redacted = OmniToolResultFormatter.RedactArguments(args);

        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            bool ok = response.IsSuccessStatusCode;
            var rendered = Render(response.StatusCode, text, response);

            audit.Record(new OmniToolAuditEntry(DateTime.UtcNow, OmniToolCatalog.UniversalApiTool,
                $"{method} {path}", "KliveAPI", mutating, ok, stopwatch.ElapsedMilliseconds, redacted,
                ok ? null : $"HTTP {(int)response.StatusCode}"));

            return new OmniToolInvocation(ok, rendered, false, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var message = $"{method} {path} failed: {ex.GetType().Name}: {ex.Message}";
            log?.Invoke($"[ServiceTools] omni_api {message}");
            audit.Record(new OmniToolAuditEntry(DateTime.UtcNow, OmniToolCatalog.UniversalApiTool,
                $"{method} {path}", "KliveAPI", mutating, false, stopwatch.ElapsedMilliseconds, redacted, ex.Message));
            return new OmniToolInvocation(false, message, false, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>Server-relative paths only. An absolute URL here would turn an internal, Klives-
    /// authenticated tool into an arbitrary outbound request that leaks the credential header.</summary>
    private static string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ToolArgumentError(ToolArgumentContract.MissingRequired, "$.path",
                "'path' is required.", "A server-relative route path, e.g. \"/omniscience/stats/overview\".").ToToolResult();

        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "omni_api only calls this server's own routes. Give a server-relative path starting with '/' "
                 + "(e.g. \"/omniscience/persons\"). To fetch a page from the internet, use web_fetch.";

        if (!path.StartsWith('/'))
            return $"Path must start with '/' — got \"{path}\".";

        return null;
    }

    private static string BuildUrl(string path, JObject? query)
    {
        var sb = new StringBuilder();
        sb.Append("http://127.0.0.1:").Append(Services.KliveAPI.KliveAPI.apiHTTPPORT).Append(path);

        if (query == null || query.Count == 0) return sb.ToString();

        sb.Append(path.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        bool first = true;
        foreach (var prop in query.Properties())
        {
            if (prop.Value.Type is JTokenType.Null or JTokenType.Undefined) continue;
            if (!first) sb.Append('&');
            first = false;
            var value = prop.Value.Type == JTokenType.String
                ? prop.Value.Value<string>() ?? ""
                : prop.Value.ToString(Newtonsoft.Json.Formatting.None);
            sb.Append(WebUtility.UrlEncode(prop.Name)).Append('=').Append(WebUtility.UrlEncode(value));
        }
        return sb.ToString();
    }

    /// <summary>Reads the Klives-rank profile's password out of the live profile manager. The agent
    /// already runs with Klives' authority, so this stores no new secret and grants no new access —
    /// it just presents the credential the pipeline expects.</summary>
    private (string? password, string? error) ResolveKlivesCredential()
    {
        KMProfileManager? profiles;
        try
        {
            profiles = resolveServices()?.OfType<KMProfileManager>().FirstOrDefault();
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach the profile manager: {ex.Message}");
        }

        if (profiles?.Profiles == null)
            return (null, "The profile manager has not loaded yet, so omni_api cannot authenticate. Try again shortly.");

        var klives = profiles.Profiles.FirstOrDefault(p =>
            p != null && p.KlivesManagementRank == KMProfileManager.KMPermissions.Klives
            && !string.IsNullOrEmpty(p.Password));

        return klives == null
            ? (null, "No Klives-rank profile with a password is loaded, so omni_api cannot authenticate.")
            : (klives.Password, null);
    }

    private string Render(HttpStatusCode status, string body, HttpResponseMessage response)
    {
        var header = new StringBuilder();
        header.Append("HTTP ").Append((int)status).Append(' ').Append(status);

        // The cache header is worth surfacing: a HIT explains an instant response, and a BYPASS
        // explains a slow one.
        if (response.Headers.TryGetValues("X-KliveAPI-Cache", out var cacheValues))
            header.Append("  [cache: ").Append(string.Join(",", cacheValues)).Append(']');

        if (string.IsNullOrWhiteSpace(body))
            return header.Append("  (empty response body)").ToString();

        string rendered;
        try
        {
            // Compact and prune through the same formatter the typed path uses, so a big list is cut
            // with an explicit marker instead of a silent truncation mid-token.
            rendered = OmniToolResultFormatter.Format(JToken.Parse(body), ResultTokenBudget);
        }
        catch
        {
            rendered = KliveAgentContextBudget.TruncateToTokens(body, ResultTokenBudget);
        }

        return header.Append('\n').Append(rendered).ToString();
    }
}
