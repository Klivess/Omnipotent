using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

/// <summary>Context limits for one configured OpenRouter route. <see cref="FromCatalog"/> is false
/// only when the live catalog was unavailable or did not contain the route, in which case deliberately
/// conservative fail-safe limits are returned.</summary>
public sealed record OpenRouterModelContextLimit(
    string RequestedModel,
    string ResolvedModel,
    int ContextWindowTokens,
    int MaxCompletionTokens,
    bool FromCatalog);

/// <summary>The limiting context values for an ordered OpenRouter model/fallback set.</summary>
public sealed record OpenRouterRouteContextLimits(
    int ContextWindowTokens,
    int MaxCompletionTokens,
    bool AllRoutesResolved,
    IReadOnlyList<OpenRouterModelContextLimit> Routes);

/// <summary>The request parameters one configured route advertises as settable. <see cref="FromCatalog"/>
/// is false when the live catalog was unavailable or did not contain the model, in which case
/// <see cref="SupportedParameters"/> is empty and the caller must not claim the model rejects anything.</summary>
public sealed record OpenRouterModelParameterSupport(
    string RequestedModel,
    string ResolvedModel,
    IReadOnlyList<string> SupportedParameters,
    bool FromCatalog);

/// <summary>
/// Fetches OpenRouter's live model catalog and resolves the context constraints for the complete
/// model fallback set used by a Projects request. The smallest route window is authoritative: until
/// OpenRouter returns a response, any configured fallback can be the model that actually serves it.
///
/// Catalog failures fail safe instead of disabling context management. OpenRouter's explicit
/// context-compression plugin remains the exact-token backstop at dispatch time, while these fallback
/// limits make local compaction intentionally early.
/// </summary>
public sealed class OpenRouterContextWindowResolver
{
    public const int FallbackContextWindowTokens = 8_192;
    public const int FallbackMaxCompletionTokens = 2_048;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly Func<Task<string?>> tokenProvider;
    private readonly Action<string> log;
    private readonly HttpClient http;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private Dictionary<string, CatalogEntry>? cachedCatalog;
    private DateTime cachedAtUtc;

    internal sealed record CatalogEntry(
        string Model,
        int ContextWindowTokens,
        int MaxCompletionTokens,
        IReadOnlyList<string>? SupportedParameters = null);

    public OpenRouterContextWindowResolver(
        Func<Task<string?>> tokenProvider,
        Action<string> log,
        HttpClient? http = null)
    {
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.log = log ?? (_ => { });
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<OpenRouterRouteContextLimits> ResolveAsync(
        IReadOnlyList<string> modelRoutes,
        CancellationToken ct = default)
    {
        var routes = (modelRoutes ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (routes.Count == 0)
            routes.Add("unknown");

        Dictionary<string, CatalogEntry>? catalog = await GetCatalogAsync(ct);
        var resolved = new List<OpenRouterModelContextLimit>(routes.Count);
        foreach (string route in routes)
        {
            if (catalog != null && catalog.TryGetValue(route, out var entry))
            {
                resolved.Add(new OpenRouterModelContextLimit(
                    route,
                    entry.Model,
                    entry.ContextWindowTokens,
                    entry.MaxCompletionTokens,
                    FromCatalog: true));
            }
            else
            {
                resolved.Add(new OpenRouterModelContextLimit(
                    route,
                    route,
                    FallbackContextWindowTokens,
                    FallbackMaxCompletionTokens,
                    FromCatalog: false));
            }
        }

        return new OpenRouterRouteContextLimits(
            resolved.Min(x => x.ContextWindowTokens),
            resolved.Min(x => x.MaxCompletionTokens),
            resolved.All(x => x.FromCatalog),
            resolved);
    }

    /// <summary>
    /// Resolves which request parameters each model in an ordered route set advertises as settable
    /// (OpenRouter's per-model <c>supported_parameters</c>). A model missing from the catalog — or a
    /// catalog that could not be fetched — reports FromCatalog=false with no parameters, which callers
    /// must read as "unknown", never as "supports nothing": OpenRouter silently ignores a parameter a
    /// model doesn't implement, so an unverified parameter is harmless rather than a 400.
    /// </summary>
    public async Task<IReadOnlyList<OpenRouterModelParameterSupport>> ResolveParametersAsync(
        IReadOnlyList<string> modelRoutes,
        CancellationToken ct = default)
    {
        var routes = (modelRoutes ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (routes.Count == 0) return Array.Empty<OpenRouterModelParameterSupport>();

        Dictionary<string, CatalogEntry>? catalog = await GetCatalogAsync(ct);
        var resolved = new List<OpenRouterModelParameterSupport>(routes.Count);
        foreach (string route in routes)
        {
            if (catalog != null && catalog.TryGetValue(route, out var entry) && entry.SupportedParameters is { Count: > 0 })
                resolved.Add(new OpenRouterModelParameterSupport(
                    route, entry.Model, entry.SupportedParameters, FromCatalog: true));
            else
                resolved.Add(new OpenRouterModelParameterSupport(
                    route, route, Array.Empty<string>(), FromCatalog: false));
        }
        return resolved;
    }

    private async Task<Dictionary<string, CatalogEntry>?> GetCatalogAsync(CancellationToken ct)
    {
        if (cachedCatalog != null && DateTime.UtcNow - cachedAtUtc < CacheTtl)
            return cachedCatalog;

        await refreshGate.WaitAsync(ct);
        try
        {
            if (cachedCatalog != null && DateTime.UtcNow - cachedAtUtc < CacheTtl)
                return cachedCatalog;

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://openrouter.ai/api/v1/models");
                string? token = await tokenProvider();
                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    log($"OpenRouter context catalog returned HTTP {(int)response.StatusCode}; using fail-safe limits.");
                    return null;
                }

                string body = await response.Content.ReadAsStringAsync(ct);
                var parsed = ParseCatalog(body);
                if (parsed.Count == 0)
                {
                    log("OpenRouter context catalog was empty; using fail-safe limits.");
                    return null;
                }

                cachedCatalog = parsed;
                cachedAtUtc = DateTime.UtcNow;
                return cachedCatalog;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"OpenRouter context catalog fetch failed ({ex.Message}); using fail-safe limits.");
                return null;
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    internal static Dictionary<string, CatalogEntry> ParseCatalog(string body)
    {
        var root = JObject.Parse(body);
        var result = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (JToken item in (root["data"] as JArray) ?? new JArray())
        {
            string id = ((string?)item["id"] ?? string.Empty).Trim();
            if (id.Length == 0) continue;

            int modelContext = PositiveInt(item["context_length"]);
            int providerContext = PositiveInt(item["top_provider"]?["context_length"]);
            int contextWindow = modelContext > 0 && providerContext > 0
                ? Math.Min(modelContext, providerContext)
                : Math.Max(modelContext, providerContext);
            if (contextWindow <= 0) continue;

            int advertisedCompletion = PositiveInt(item["top_provider"]?["max_completion_tokens"]);
            int conservativeCompletion = Math.Max(512, contextWindow / 4);
            int maxCompletion = advertisedCompletion > 0
                ? Math.Min(advertisedCompletion, contextWindow)
                : conservativeCompletion;

            var supported = (item["supported_parameters"] as JArray)?
                .Select(x => ((string?)x ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            result[id] = new CatalogEntry(id, contextWindow, maxCompletion, supported);

            // A canonical slug can differ from the request alias. Preserve both lookup keys when
            // OpenRouter includes it in the list response.
            string canonical = ((string?)item["canonical_slug"] ?? string.Empty).Trim();
            if (canonical.Length > 0)
                result.TryAdd(canonical, result[id]);
        }
        return result;

        static int PositiveInt(JToken? token)
        {
            long value = token?.Value<long?>() ?? 0;
            return value is > 0 and <= int.MaxValue ? (int)value : 0;
        }
    }
}
