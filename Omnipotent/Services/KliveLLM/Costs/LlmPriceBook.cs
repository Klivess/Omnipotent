using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;
using System.Globalization;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>Per-million-token prices for one model on one router.</summary>
    public sealed class LlmPriceEntry
    {
        /// <summary>A router id, or "*" for a price that applies on any router.</summary>
        public string Router { get; set; } = "*";
        public string Model { get; set; } = "";
        public double? InputPerMTok { get; set; }
        /// <summary>Prompt tokens served from the provider's cache. Blank bills them as input.</summary>
        public double? CachedInputPerMTok { get; set; }
        /// <summary>Fresh prompt tokens written into the cache (a subset of input, never an addition). Blank bills them as input.</summary>
        public double? CacheWritePerMTok { get; set; }
        public double? OutputPerMTok { get; set; }
        /// <summary>manual or openrouter (read from OpenRouter's catalogue, refreshed every few hours).</summary>
        public string Source { get; set; } = "manual";
        public DateTime UpdatedUtc { get; set; }

        [JsonIgnore] public bool HasPrice => InputPerMTok.HasValue || OutputPerMTok.HasValue;

        /// <summary>USD for a token split under these prices. Cache writes are part of the fresh input.</summary>
        public double Cost(long prompt, long cached, long cacheWrite, long completion)
        {
            long cachedClamped = Math.Clamp(cached, 0, Math.Max(0, prompt));
            long fresh = Math.Max(0, prompt - cachedClamped);
            long write = Math.Clamp(cacheWrite, 0, fresh);
            double input = InputPerMTok ?? 0;
            return ((fresh - write) * input
                    + write * (CacheWritePerMTok ?? input)
                    + cachedClamped * (CachedInputPerMTok ?? input)
                    + Math.Max(0, completion) * (OutputPerMTok ?? 0)) / 1_000_000.0;
        }
    }

    /// <summary>
    /// The prices KliveLLM costs calls with: what Klives typed in, and OpenRouter's catalogue prices
    /// for every OpenRouter model. Used for three things: an estimate where a router reports no cost,
    /// the list-price equivalent of flat-fee traffic, and the defaults the simulator offers.
    /// </summary>
    public sealed class LlmPriceBook
    {
        public const string CacheKey = "klivellm:prices";

        private readonly string? path;
        private readonly object sync = new();
        private Dictionary<string, LlmPriceEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> log;

        private LlmPriceBook(string? path, Action<string>? log)
        {
            this.path = path;
            this.log = log ?? (_ => { });
            Load();
        }

        public static LlmPriceBook InMemory() => new(null, null);
        public static LlmPriceBook Open(string directory, Action<string>? log)
        {
            Directory.CreateDirectory(directory);
            return new LlmPriceBook(Path.Combine(directory, "prices.json"), log);
        }

        private static string Key(string router, string model) => (router ?? "*").Trim() + "|" + (model ?? "").Trim();

        public IReadOnlyList<LlmPriceEntry> All()
        {
            CacheDeps.NoteRead(CacheKey);
            lock (sync) return entries.Values.OrderBy(e => e.Router).ThenBy(e => e.Model).ToList();
        }

        public int Count { get { lock (sync) return entries.Count; } }

        /// <summary>The price for a model on a router: its own entry, then an any-router entry, then
        /// OpenRouter's catalogue entry for the same model id.</summary>
        public LlmPriceEntry? Find(string? router, string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            string m = model.Trim();
            lock (sync)
            {
                if (router != null && entries.TryGetValue(Key(router, m), out var own) && own.HasPrice) return own;
                if (entries.TryGetValue(Key("*", m), out var any) && any.HasPrice) return any;
                if (entries.TryGetValue(Key(LlmRouterIds.OpenRouter, m), out var catalogue) && catalogue.HasPrice) return catalogue;
                // OpenRouter answers with a dated variant of the slug it was sent ("…-20250514").
                int dash = m.LastIndexOf('-');
                if (dash > 0 && m.Length - dash == 9 && m[(dash + 1)..].All(char.IsDigit)
                    && entries.TryGetValue(Key(LlmRouterIds.OpenRouter, m[..dash]), out var undated) && undated.HasPrice) return undated;
                return null;
            }
        }

        /// <summary>Adds or replaces entries Klives entered. A blank entry removes the price.</summary>
        public void Save(IEnumerable<LlmPriceEntry> changes)
        {
            lock (sync)
            {
                foreach (var change in changes)
                {
                    if (change == null || string.IsNullOrWhiteSpace(change.Model)) continue;
                    string router = string.IsNullOrWhiteSpace(change.Router) ? "*" : change.Router.Trim();
                    string key = Key(router, change.Model);
                    if (!change.HasPrice && change.CachedInputPerMTok == null && change.CacheWritePerMTok == null)
                    {
                        entries.Remove(key);
                        continue;
                    }
                    entries[key] = new LlmPriceEntry
                    {
                        Router = router,
                        Model = change.Model.Trim(),
                        InputPerMTok = Price(change.InputPerMTok),
                        CachedInputPerMTok = Price(change.CachedInputPerMTok),
                        CacheWritePerMTok = Price(change.CacheWritePerMTok),
                        OutputPerMTok = Price(change.OutputPerMTok),
                        Source = "manual",
                        UpdatedUtc = DateTime.UtcNow,
                    };
                }
                Persist();
            }
            CacheDeps.Bump(CacheKey);
        }

        /// <summary>Reads OpenRouter's /models "data" array. Manual entries are never overwritten.</summary>
        public int ImportOpenRouterCatalogue(JArray? models)
        {
            if (models == null) return 0;
            int n = 0;
            lock (sync)
            {
                foreach (var model in models.OfType<JObject>())
                {
                    string? id = (string?)model["id"];
                    var pricing = model["pricing"] as JObject;
                    if (string.IsNullOrWhiteSpace(id) || pricing == null) continue;
                    string key = Key(LlmRouterIds.OpenRouter, id);
                    if (entries.TryGetValue(key, out var existing) && existing.Source == "manual") continue;
                    double? perToken(string field) => double.TryParse((string?)pricing[field], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0
                        ? Math.Round(v * 1_000_000, 6) : null;
                    var entry = new LlmPriceEntry
                    {
                        Router = LlmRouterIds.OpenRouter,
                        Model = id,
                        InputPerMTok = perToken("prompt"),
                        CachedInputPerMTok = perToken("input_cache_read"),
                        CacheWritePerMTok = perToken("input_cache_write"),
                        OutputPerMTok = perToken("completion"),
                        Source = "openrouter",
                        UpdatedUtc = DateTime.UtcNow,
                    };
                    if (!entry.HasPrice) continue;
                    entries[key] = entry;
                    n++;
                }
                Persist();
            }
            CacheDeps.Bump(CacheKey);
            return n;
        }

        private static double? Price(double? value) => value is { } v && double.IsFinite(v) && v >= 0 && v < 100_000 ? v : null;

        private void Load()
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<LlmPriceEntry>>(File.ReadAllText(path)) ?? new();
                entries = list.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Model))
                    .GroupBy(e => Key(e.Router, e.Model), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { log($"KliveLLM prices.json could not be read: {ex.Message}"); }
        }

        private void Persist()
        {
            if (path == null) return;
            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(entries.Values.OrderBy(e => e.Router).ThenBy(e => e.Model), Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) { log($"KliveLLM prices.json could not be saved: {ex.Message}"); }
        }
    }
}
