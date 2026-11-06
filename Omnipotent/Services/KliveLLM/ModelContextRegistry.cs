using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.KliveLLM;

/// <summary>What a model can take, and where that knowledge came from.</summary>
public sealed record ModelContextInfo(
    string Provider,
    string Model,
    int ContextWindowTokens,
    int MaxOutputTokens,
    string Source,
    string? MatchedCatalogId,
    long LargestSuccessfulPromptTokens,
    long? RequestByteLimit);

/// <summary>
/// Resolves each model's real context window and output ceiling dynamically, so callers fill the window
/// instead of guessing (Klives: "max context, fetched dynamically"). Resolution order:
/// <list type="number">
/// <item>an explicit operator override (setting);</item>
/// <item>a limit the provider itself stated in an overflow error (learned, persisted);</item>
/// <item>the serving provider's catalogue, when it exposes a context field (OpenRouter does; AIRouter's
/// /models lists ids only today, but any of the usual fields is read if it appears);</item>
/// <item>OpenRouter's public catalogue matched by model family (upper-bound prior for router-served models);</item>
/// <item>an operator-stated value for the provider (Klives stated AIRouter's window is 260K);</item>
/// <item>a conservative default.</item>
/// </list>
/// The window is never reported below a prompt size that has already succeeded. Request-body ceilings
/// (AIRouter's nginx 413 near 4 MB) are learned from refusals and successes. Nothing here ever sends a
/// probe request: limits are learned only from real traffic.
/// </summary>
public sealed class ModelContextRegistry
{
    public const int DefaultContextWindowTokens = 32_768;
    public const int DefaultMaxOutputTokens = 4_096;
    public const int RouterMaxOutputTokens = 16_384;
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromHours(6);

    /// <summary>One catalogue row (OpenRouter or a provider's own /models).</summary>
    public sealed record CatalogEntry(string Id, string? CanonicalSlug, int ContextWindowTokens, int? MaxOutputTokens);

    private sealed class Learned
    {
        public int? StatedLimit { get; set; }
        public long MaxOkPromptTokens { get; set; }
        public long? RefusedBytes { get; set; }
        public long MaxOkBytes { get; set; }
    }

    private readonly Func<CancellationToken, Task<IReadOnlyList<CatalogEntry>?>> fetchOpenRouterCatalog;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<CatalogEntry>?>> fetchProviderCatalog;
    private readonly Func<string, string, int?> overrideFor;
    private readonly Func<string, int?> operatorStatedWindow;
    private readonly Func<DateTime> utcNow;
    private readonly string? persistPath;
    private readonly object sync = new();
    private Dictionary<string, Learned> learned = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CatalogEntry>? openRouterCatalog;
    private DateTime openRouterFetchedUtc;
    private readonly Dictionary<string, (IReadOnlyList<CatalogEntry>? Entries, DateTime At)> providerCatalogs = new(StringComparer.OrdinalIgnoreCase);

    public ModelContextRegistry(
        Func<CancellationToken, Task<IReadOnlyList<CatalogEntry>?>> fetchOpenRouterCatalog,
        Func<string, CancellationToken, Task<IReadOnlyList<CatalogEntry>?>> fetchProviderCatalog,
        Func<string, string, int?> overrideFor,
        Func<string, int?> operatorStatedWindow,
        string? persistPath = null,
        Func<DateTime>? utcNow = null)
    {
        this.fetchOpenRouterCatalog = fetchOpenRouterCatalog;
        this.fetchProviderCatalog = fetchProviderCatalog;
        this.overrideFor = overrideFor;
        this.operatorStatedWindow = operatorStatedWindow;
        this.persistPath = persistPath;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        Load();
    }

    private static string Key(string provider, string model) => provider.Trim() + "|" + model.Trim();

    public async Task<ModelContextInfo> ResolveAsync(string provider, string model, CancellationToken ct = default)
    {
        Learned l;
        lock (sync) l = learned.TryGetValue(Key(provider, model), out var found) ? found : new Learned();

        (int window, int? output, string source, string? matched) = (DefaultContextWindowTokens, null, "default", null);

        int? over = overrideFor(provider, model);
        if (over is > 0)
        {
            (window, source) = (over.Value, "override");
        }
        else if (l.StatedLimit is > 0)
        {
            (window, source) = (l.StatedLimit.Value, "learned:provider-stated");
        }
        else
        {
            var own = await ProviderCatalogAsync(provider, ct).ConfigureAwait(false);
            var ownHit = own?.FirstOrDefault(e => string.Equals(e.Id, model, StringComparison.OrdinalIgnoreCase)) ?? (own == null ? null : MatchFamily(model, own));
            if (ownHit is { ContextWindowTokens: > 0 })
            {
                (window, output, source, matched) = (ownHit.ContextWindowTokens, ownHit.MaxOutputTokens, "provider-catalog", ownHit.Id);
            }
            else
            {
                var or = await OpenRouterCatalogAsync(ct).ConfigureAwait(false);
                var hit = or == null ? null
                    : or.FirstOrDefault(e => string.Equals(e.Id, model, StringComparison.OrdinalIgnoreCase)) ?? MatchFamily(model, or);
                int? stated = operatorStatedWindow(provider);
                if (hit is { ContextWindowTokens: > 0 })
                {
                    (window, output, source, matched) = (hit.ContextWindowTokens, hit.MaxOutputTokens, "openrouter-family", hit.Id);
                    // OpenRouter reports the upstream maximum; a router may serve less. Prefer an operator-stated value.
                    if (stated is > 0 && stated.Value < window) (window, source) = (stated.Value, "operator-stated");
                }
                else if (stated is > 0)
                {
                    (window, source) = (stated.Value, "operator-stated");
                }
            }
        }

        // Never report less than something that already worked.
        int outCap = output is > 0 ? output.Value : (source == "default" ? DefaultMaxOutputTokens : RouterMaxOutputTokens);
        outCap = Math.Min(outCap, Math.Max(256, window / 2));
        if (l.MaxOkPromptTokens > 0 && l.MaxOkPromptTokens + outCap > window && source != "override")
            window = (int)Math.Min(int.MaxValue, l.MaxOkPromptTokens + outCap);

        long? byteLimit = l.RefusedBytes is > 0 ? Math.Max(l.MaxOkBytes, (long)(l.RefusedBytes.Value * 0.9)) : null;
        return new ModelContextInfo(provider, model, window, outCap, source, matched, l.MaxOkPromptTokens, byteLimit);
    }

    /// <summary>Learns a provider-stated window from an overflow error message. Returns the parsed limit.</summary>
    public int? RecordOverflow(string provider, string model, string? errorMessage)
    {
        int? stated = ParseStatedLimit(errorMessage);
        if (stated == null) return null;
        lock (sync)
        {
            var l = Get(provider, model);
            l.StatedLimit = l.StatedLimit is > 0 ? Math.Min(l.StatedLimit.Value, stated.Value) : stated.Value;
        }
        Save();
        return stated;
    }

    public void RecordSuccess(string provider, string model, long promptTokens, long requestBytes)
    {
        bool changed = false;
        lock (sync)
        {
            var l = Get(provider, model);
            if (promptTokens > l.MaxOkPromptTokens) { l.MaxOkPromptTokens = promptTokens; changed = true; }
            if (requestBytes > l.MaxOkBytes) { l.MaxOkBytes = requestBytes; changed = true; }
        }
        if (changed) Save();
    }

    public void RecordRequestTooLarge(string provider, string model, long requestBytes)
    {
        if (requestBytes <= 0) return;
        lock (sync)
        {
            var l = Get(provider, model);
            l.RefusedBytes = l.RefusedBytes is > 0 ? Math.Min(l.RefusedBytes.Value, requestBytes) : requestBytes;
        }
        Save();
    }

    private Learned Get(string provider, string model)
    {
        string k = Key(provider, model);
        if (!learned.TryGetValue(k, out var l)) learned[k] = l = new Learned();
        return l;
    }

    // ── parsing & matching ───────────────────────────────────────────────────

    private static readonly Regex[] LimitPatterns =
    {
        new(@"maximum context length is\s+([\d,_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"context(?:\s+window)?\s+(?:length|size|limit)\s+(?:of|is)\s+([\d,_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:max(?:imum)?|limit)\s+(?:of\s+)?([\d,_]+)\s+tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"max_model_len\D{0,20}([\d,_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>Extracts the model's stated window from an overflow error, e.g.
    /// "This model's maximum context length is 262144 tokens. However, you requested 300000 tokens".</summary>
    public static int? ParseStatedLimit(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        foreach (var re in LimitPatterns)
        {
            var m = re.Match(message);
            if (!m.Success) continue;
            string digits = m.Groups[1].Value.Replace(",", "").Replace("_", "");
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1024) return n;
        }
        return null;
    }

    /// <summary>True when an error message describes a context/prompt-length overflow.</summary>
    public static bool LooksLikeContextOverflow(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && Regex.IsMatch(message, @"context(?:\s+window)?\s+length|maximum context|too many tokens|prompt is too long|reduce the length|max_model_len|context_length_exceeded", RegexOptions.IgnoreCase);

    public static string NormalizeFamily(string modelId)
    {
        string s = modelId.Trim().ToLowerInvariant();
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        return Regex.Replace(s, @"[\s_]+", "-");
    }

    /// <summary>Matches a router's model id (e.g. "Qwen3.8") to OpenRouter entries ("qwen/qwen3.8",
    /// "qwen/qwen3.8-instruct"). Exact family match first; otherwise entries whose family starts with the
    /// model's, taking the SMALLEST window among them (conservative).</summary>
    public static CatalogEntry? MatchFamily(string model, IReadOnlyList<CatalogEntry> catalog)
    {
        string fam = NormalizeFamily(model);
        if (fam.Length < 3) return null;
        var exact = catalog.Where(e => NormalizeFamily(e.Id) == fam || (e.CanonicalSlug != null && NormalizeFamily(e.CanonicalSlug) == fam)).ToList();
        if (exact.Count > 0) return exact.OrderBy(e => e.ContextWindowTokens).First();
        var prefixed = catalog.Where(e =>
        {
            string f = NormalizeFamily(e.Id);
            return f.StartsWith(fam + "-", StringComparison.Ordinal) || f.StartsWith(fam + ".", StringComparison.Ordinal);
        }).ToList();
        return prefixed.Count > 0 ? prefixed.OrderBy(e => e.ContextWindowTokens).First() : null;
    }

    /// <summary>Parses an OpenAI/OpenRouter-style /models body, reading any of the usual context fields.</summary>
    public static IReadOnlyList<CatalogEntry> ParseCatalog(string json)
    {
        var list = new List<CatalogEntry>();
        var root = JToken.Parse(json);
        var data = root is JObject o ? o["data"] as JArray ?? o["models"] as JArray : root as JArray;
        if (data == null) return list;
        foreach (var e in data.OfType<JObject>())
        {
            string? id = (string?)e["id"];
            if (string.IsNullOrWhiteSpace(id)) continue;
            int? ctx = FirstInt(e, "context_length", "max_model_len", "context_window", "max_context_length", "max_input_tokens")
                       ?? FirstInt(e["top_provider"] as JObject, "context_length")
                       ?? FirstInt(e["limits"] as JObject, "context", "context_length");
            int? topCtx = FirstInt(e["top_provider"] as JObject, "context_length");
            if (ctx is > 0 && topCtx is > 0) ctx = Math.Min(ctx.Value, topCtx.Value);
            int? output = FirstInt(e["top_provider"] as JObject, "max_completion_tokens")
                          ?? FirstInt(e, "max_output_tokens", "max_completion_tokens")
                          ?? FirstInt(e["limits"] as JObject, "output", "max_output_tokens");
            list.Add(new CatalogEntry(id!, (string?)e["canonical_slug"], ctx ?? 0, output is > 0 ? output : null));
        }
        return list;
    }

    private static int? FirstInt(JObject? o, params string[] names)
    {
        if (o == null) return null;
        foreach (var n in names)
        {
            var t = o[n];
            if (t == null || t.Type == JTokenType.Null) continue;
            if (t.Type == JTokenType.Integer) return (int)Math.Min(int.MaxValue, (long)t);
            if (t.Type == JTokenType.Float) return (int)Math.Min(int.MaxValue, (double)t);
            if (t.Type == JTokenType.String && int.TryParse((string?)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        }
        return null;
    }

    // ── catalogue caches ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CatalogEntry>?> OpenRouterCatalogAsync(CancellationToken ct)
    {
        lock (sync)
            if (openRouterCatalog != null && utcNow() - openRouterFetchedUtc < CatalogTtl) return openRouterCatalog;
        var fetched = await fetchOpenRouterCatalog(ct).ConfigureAwait(false);
        if (fetched == null) return openRouterCatalog; // keep a stale copy rather than nothing
        lock (sync) { openRouterCatalog = fetched; openRouterFetchedUtc = utcNow(); }
        return fetched;
    }

    private async Task<IReadOnlyList<CatalogEntry>?> ProviderCatalogAsync(string provider, CancellationToken ct)
    {
        lock (sync)
            if (providerCatalogs.TryGetValue(provider, out var c) && utcNow() - c.At < CatalogTtl) return c.Entries;
        var fetched = await fetchProviderCatalog(provider, ct).ConfigureAwait(false);
        lock (sync) providerCatalogs[provider] = (fetched, utcNow());
        return fetched;
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        if (persistPath == null || !File.Exists(persistPath)) return;
        try
        {
            var data = JsonConvert.DeserializeObject<Dictionary<string, Learned>>(File.ReadAllText(persistPath));
            if (data != null) learned = new Dictionary<string, Learned>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { learned = new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { learned = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        if (persistPath == null) return;
        string json;
        lock (sync) json = JsonConvert.SerializeObject(learned, Formatting.Indented);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
            string tmp = persistPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, persistPath, overwrite: true);
        }
        catch (IOException) { /* learned limits are an optimisation; they are re-learned from traffic */ return; }
    }
}
