using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.KliveLLM
{
    public sealed class LlmFallbackTarget
    {
        public string Router { get; set; } = "";
        /// <summary>The model to send on this router; null uses the caller's own or the router's default.</summary>
        public string? Model { get; set; }
    }

    public sealed class LlmBudget
    {
        public double? DailyUsd { get; set; }
        public double? MonthlyUsd { get; set; }
        public long? DailyTokens { get; set; }
        public long? MonthlyTokens { get; set; }
        /// <summary>warn (flag only), block (refuse calls) or fallback (move to a free or flat-fee fallback).</summary>
        public string Action { get; set; } = "warn";

        [JsonIgnore] public bool IsEmpty => DailyUsd is null && MonthlyUsd is null && DailyTokens is null && MonthlyTokens is null;
    }

    /// <summary>
    /// How one level (a lane, a service, or the default) wants its calls routed. Every field is
    /// optional: null inherits from the less specific level, and a document of nulls routes every call
    /// exactly as KliveLLM always has.
    /// </summary>
    public sealed class LlmRoutePolicy
    {
        public string? Router { get; set; }
        public string? Model { get; set; }
        /// <summary>Send <see cref="Model"/> even when the service chose its own.</summary>
        public bool? Pin { get; set; }
        public List<LlmFallbackTarget>? Fallbacks { get; set; }
        /// <summary>Reasoning ceiling for this level: off, low, medium or high.</summary>
        public string? Thinking { get; set; }
        /// <summary>OpenRouter service tier: default, flex or priority.</summary>
        public string? ServiceTier { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public LlmSpending? Spending { get; set; }
        public bool? Paused { get; set; }
        public string? PausedReason { get; set; }
        public DateTime? PausedUtc { get; set; }
        public int? MaxParallel { get; set; }
        public LlmBudget? Budget { get; set; }
        public string? Note { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Router == null && Model == null && Pin == null && (Fallbacks == null || Fallbacks.Count == 0)
            && Thinking == null && ServiceTier == null && Spending == null && Paused != true && MaxParallel == null
            && (Budget == null || Budget.IsEmpty) && string.IsNullOrWhiteSpace(Note);
    }

    /// <summary>"Everything to this router until then": for an outage, set in one click and self-expiring.</summary>
    public sealed class LlmEmergencyOverride
    {
        public string Router { get; set; } = "";
        public string? Model { get; set; }
        public DateTime UntilUtc { get; set; }
        public string? Reason { get; set; }
        public string? SetBy { get; set; }
        /// <summary>Only these services; empty means every service.</summary>
        public List<string>? Services { get; set; }

        public bool AppliesTo(string service, DateTime nowUtc)
            => UntilUtc > nowUtc && !string.IsNullOrWhiteSpace(Router)
               && (Services == null || Services.Count == 0 || Services.Contains(service, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A window in which a service's (or lane's) prompt and reply text is kept.</summary>
    public sealed class LlmCaptureWindow
    {
        public DateTime StartedUtc { get; set; }
        public DateTime UntilUtc { get; set; }
        public string? SetBy { get; set; }
    }

    public sealed class LlmRoutingDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public LlmRoutePolicy Default { get; set; } = new();
        /// <summary>Keyed by service name.</summary>
        public Dictionary<string, LlmRoutePolicy> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Keyed by "Service/lane".</summary>
        public Dictionary<string, LlmRoutePolicy> Lanes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public LlmEmergencyOverride? Override { get; set; }
        /// <summary>Added routers, and the parts of built-ins Klives changed.</summary>
        public List<LlmRouterDefinition> Routers { get; set; } = new();
        /// <summary>Keyed by "Service" or "Service/lane".</summary>
        public Dictionary<string, LlmCaptureWindow> Capture { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public LlmRoutePolicy? ServicePolicy(string service) => Services.TryGetValue(service, out var p) ? p : null;
        public LlmRoutePolicy? LanePolicy(string service, string lane) => Lanes.TryGetValue(service + "/" + lane, out var p) ? p : null;

        private static readonly JsonSerializerSettings CloneSettings = new() { NullValueHandling = NullValueHandling.Ignore };

        public LlmRoutingDocument Clone()
        {
            var copy = JsonConvert.DeserializeObject<LlmRoutingDocument>(JsonConvert.SerializeObject(this, CloneSettings)) ?? new();
            copy.Normalize();
            return copy;
        }

        /// <summary>Drops empty entries, trims text, removes duplicate and self-referencing fallbacks,
        /// clamps numbers and restores case-insensitive keys after deserialisation.</summary>
        public void Normalize()
        {
            Default ??= new();
            Clean(Default);
            Services = Rebuild(Services);
            Lanes = Rebuild(Lanes);
            Routers = (Routers ?? new()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id))
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
            Capture = new Dictionary<string, LlmCaptureWindow>(
                (Capture ?? new()).Where(kv => kv.Value != null && !string.IsNullOrWhiteSpace(kv.Key)),
                StringComparer.OrdinalIgnoreCase);
            if (Override != null && string.IsNullOrWhiteSpace(Override.Router)) Override = null;
        }

        private static Dictionary<string, LlmRoutePolicy> Rebuild(Dictionary<string, LlmRoutePolicy>? source)
        {
            var result = new Dictionary<string, LlmRoutePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, policy) in source ?? new())
            {
                if (string.IsNullOrWhiteSpace(key) || policy == null) continue;
                Clean(policy);
                if (!policy.IsEmpty) result[key.Trim()] = policy;
            }
            return result;
        }

        private static void Clean(LlmRoutePolicy p)
        {
            p.Router = Blank(p.Router);
            p.Model = Blank(p.Model);
            p.Thinking = Blank(p.Thinking)?.ToLowerInvariant();
            if (p.Thinking is not (null or "off" or "low" or "medium" or "high")) p.Thinking = null;
            p.ServiceTier = Blank(p.ServiceTier)?.ToLowerInvariant();
            if (p.ServiceTier is not (null or "default" or "flex" or "priority")) p.ServiceTier = null;
            if (p.Model == null) p.Pin = null;
            if (p.Paused != true) { p.Paused = null; p.PausedReason = null; p.PausedUtc = null; }
            if (p.MaxParallel is { } mp) p.MaxParallel = mp <= 0 ? null : Math.Min(mp, 64);
            if (p.Budget != null)
            {
                if (p.Budget.DailyUsd is <= 0) p.Budget.DailyUsd = null;
                if (p.Budget.MonthlyUsd is <= 0) p.Budget.MonthlyUsd = null;
                if (p.Budget.DailyTokens is <= 0) p.Budget.DailyTokens = null;
                if (p.Budget.MonthlyTokens is <= 0) p.Budget.MonthlyTokens = null;
                p.Budget.Action = p.Budget.Action?.ToLowerInvariant() is "block" or "fallback" ? p.Budget.Action.ToLowerInvariant() : "warn";
                if (p.Budget.IsEmpty) p.Budget = null;
            }
            if (p.Fallbacks != null)
            {
                p.Fallbacks = p.Fallbacks
                    .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Router))
                    .Select(f => new LlmFallbackTarget { Router = f.Router.Trim(), Model = Blank(f.Model) })
                    .Where(f => !string.Equals(f.Router, p.Router, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(f => f.Router + "|" + f.Model, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .Take(4).ToList();
                if (p.Fallbacks.Count == 0) p.Fallbacks = null;
            }
            p.Note = Blank(p.Note);
            if (p.Note?.Length > 500) p.Note = p.Note[..500];
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class LlmRoutingHistoryEntry
    {
        public long Revision { get; set; }
        public DateTime AtUtc { get; set; }
        public string? By { get; set; }
        public string? Note { get; set; }
        public List<string> Changes { get; set; } = new();
        public LlmRoutingDocument? Before { get; set; }
        public LlmRoutingDocument? After { get; set; }
    }

    /// <summary>
    /// The routing document on disk, with every change kept as a history entry that can be reverted.
    /// One writer at a time; readers get the current document, which is never mutated in place (every
    /// change works on a clone and swaps the reference), so a read needs no lock.
    /// </summary>
    public sealed class LlmRoutingStore
    {
        public const string CacheKey = "klivellm:routing";
        private const int MaxHistoryEntries = 400;
        private const long MaxHistoryBytes = 4 * 1024 * 1024;

        private readonly string? path;
        private readonly string? historyPath;
        private readonly Action<string> log;
        private readonly object sync = new();
        private volatile LlmRoutingDocument current;
        private readonly List<LlmRoutingHistoryEntry> memoryHistory = new();

        private LlmRoutingStore(string? directory, Action<string>? log)
        {
            this.log = log ?? (_ => { });
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, "routing.json");
                historyPath = Path.Combine(directory, "routing-history.jsonl");
            }
            current = ReadDocument() ?? new LlmRoutingDocument();
        }

        /// <summary>A store with no file: tests, and the moments before KliveLLM has started.</summary>
        public static LlmRoutingStore InMemory(LlmRoutingDocument? document = null)
        {
            var store = new LlmRoutingStore(null, null);
            if (document != null) { document.Normalize(); store.current = document; }
            return store;
        }

        public static LlmRoutingStore Load(string directory, Action<string>? log) => new(directory, log);

        /// <summary>The live document. Treat it as read-only.</summary>
        public LlmRoutingDocument Current
        {
            get
            {
                CacheDeps.NoteRead(CacheKey);
                return current;
            }
        }

        /// <summary>The document without registering a cache dependency (hot paths that are never cached).</summary>
        internal LlmRoutingDocument Peek => current;

        public long Revision => current.Revision;

        /// <summary>Raised after a change is saved, with the new document.</summary>
        public event Action<LlmRoutingDocument, LlmRoutingHistoryEntry>? Changed;

        /// <summary>
        /// Replaces the document. <paramref name="baseRevision"/> is the revision the editor started from:
        /// when someone else saved in between, nothing is written and the caller gets a conflict.
        /// </summary>
        public (bool Ok, string? Error, bool Conflict, LlmRoutingDocument Document, LlmRoutingHistoryEntry? Entry) Apply(
            LlmRoutingDocument next, long? baseRevision, string? by, string? note, Func<LlmRoutingDocument, string?>? validate = null)
        {
            if (next == null) return (false, "No routing document.", false, current, null);
            lock (sync)
            {
                var before = current;
                if (baseRevision.HasValue && baseRevision.Value != before.Revision)
                    return (false, $"The routing changed since you loaded it (revision {before.Revision}, you started from {baseRevision}). Reload and apply again.", true, before, null);

                var candidate = next.Clone();
                string? error = validate?.Invoke(candidate);
                if (error != null) return (false, error, false, before, null);

                var changes = Diff(before, candidate);
                if (changes.Count == 0 && string.IsNullOrWhiteSpace(note)) return (true, null, false, before, null);

                candidate.Revision = before.Revision + 1;
                candidate.UpdatedUtc = DateTime.UtcNow;
                candidate.UpdatedBy = Clip(by, 80);
                var entry = new LlmRoutingHistoryEntry
                {
                    Revision = candidate.Revision,
                    AtUtc = candidate.UpdatedUtc.Value,
                    By = candidate.UpdatedBy,
                    Note = Clip(note, 300),
                    Changes = changes,
                    Before = before,
                    After = candidate,
                };
                try
                {
                    WriteDocument(candidate);
                    AppendHistory(entry);
                }
                catch (Exception ex)
                {
                    log($"KliveLLM routing could not be saved: {ex.Message}");
                    return (false, "The routing could not be saved: " + ex.Message, false, before, null);
                }
                current = candidate;
                CacheDeps.Bump(CacheKey);
                try { Changed?.Invoke(candidate, entry); } catch (Exception ex) { log($"KliveLLM routing change handler failed: {ex.Message}"); }
                return (true, null, false, candidate, entry);
            }
        }

        /// <summary>Applies one edit to a copy of the current document and saves it.</summary>
        public (bool Ok, string? Error, bool Conflict, LlmRoutingDocument Document, LlmRoutingHistoryEntry? Entry) Mutate(
            Func<LlmRoutingDocument, string?> change, string? by, string? note, Func<LlmRoutingDocument, string?>? validate = null)
        {
            lock (sync)
            {
                var copy = current.Clone();
                string? error = change(copy);
                if (error != null) return (false, error, false, current, null);
                return Apply(copy, current.Revision, by, note, validate);
            }
        }

        public IReadOnlyList<LlmRoutingHistoryEntry> History(int limit = 50, bool includeDocuments = false)
        {
            limit = Math.Clamp(limit, 1, MaxHistoryEntries);
            List<LlmRoutingHistoryEntry> entries;
            lock (sync) entries = ReadHistory();
            return entries.OrderByDescending(e => e.Revision).Take(limit)
                .Select(e => includeDocuments ? e : new LlmRoutingHistoryEntry
                {
                    Revision = e.Revision, AtUtc = e.AtUtc, By = e.By, Note = e.Note, Changes = e.Changes,
                }).ToList();
        }

        public LlmRoutingHistoryEntry? HistoryEntry(long revision)
        {
            lock (sync) return ReadHistory().LastOrDefault(e => e.Revision == revision);
        }

        // ── persistence ──

        private LlmRoutingDocument? ReadDocument()
        {
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var doc = JsonConvert.DeserializeObject<LlmRoutingDocument>(File.ReadAllText(path));
                doc?.Normalize();
                return doc;
            }
            catch (Exception ex)
            {
                // A corrupt file must not stop model calls: route as if nothing were configured, keep the
                // file for inspection and say so.
                try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); } catch { }
                log($"KliveLLM routing.json could not be read ({ex.Message}); routing as configured by RemoteLLMProvider until it is saved again.");
                return null;
            }
        }

        private void WriteDocument(LlmRoutingDocument doc)
        {
            if (path == null) return;
            string json = JsonConvert.SerializeObject(doc, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            string temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        private void AppendHistory(LlmRoutingHistoryEntry entry)
        {
            if (historyPath == null)
            {
                memoryHistory.Add(entry);
                if (memoryHistory.Count > MaxHistoryEntries) memoryHistory.RemoveAt(0);
                return;
            }
            string line = JsonConvert.SerializeObject(entry, Formatting.None,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            File.AppendAllText(historyPath, line + Environment.NewLine);
            try
            {
                if (new FileInfo(historyPath).Length > MaxHistoryBytes)
                {
                    var keep = ReadHistory().OrderBy(e => e.Revision).TakeLast(MaxHistoryEntries / 2).ToList();
                    string temp = historyPath + ".tmp";
                    File.WriteAllLines(temp, keep.Select(e => JsonConvert.SerializeObject(e, Formatting.None,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })));
                    File.Move(temp, historyPath, overwrite: true);
                }
            }
            catch (Exception ex) { log($"KliveLLM routing history could not be trimmed: {ex.Message}"); }
        }

        private List<LlmRoutingHistoryEntry> ReadHistory()
        {
            if (historyPath == null) return memoryHistory.ToList();
            var result = new List<LlmRoutingHistoryEntry>();
            if (!File.Exists(historyPath)) return result;
            foreach (string line in File.ReadLines(historyPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonConvert.DeserializeObject<LlmRoutingHistoryEntry>(line);
                    if (entry != null) result.Add(entry);
                }
                catch { /* one bad line must not hide the rest */ }
            }
            return result;
        }

        // ── what changed ──

        /// <summary>One plain line per changed field, for the history list and the confirmation dialog.</summary>
        public static List<string> Diff(LlmRoutingDocument before, LlmRoutingDocument after)
        {
            var a = Flatten(before);
            var b = Flatten(after);
            var lines = new List<string>();
            foreach (string key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                a.TryGetValue(key, out string? old);
                b.TryGetValue(key, out string? now);
                if (string.Equals(old, now, StringComparison.Ordinal)) continue;
                string label = Label(key);
                if (old == null) lines.Add($"{label}: set to {now}");
                else if (now == null) lines.Add($"{label}: cleared (was {old})");
                else lines.Add($"{label}: {old} → {now}");
            }
            return lines;
        }

        private static Dictionary<string, string> Flatten(LlmRoutingDocument doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var json = JObject.FromObject(doc, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            foreach (string skip in new[] { "Revision", "UpdatedUtc", "UpdatedBy", "SchemaVersion" }) json.Remove(skip);
            void Walk(JToken token, string prefix)
            {
                switch (token)
                {
                    case JObject obj:
                        foreach (var prop in obj.Properties()) Walk(prop.Value, prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name);
                        break;
                    case JArray array:
                        result[prefix] = string.Join(", ", array.Select(item => item is JObject o
                            ? string.Join(" ", o.Properties().Where(p => p.Value.Type != JTokenType.Null).Select(p => p.Value.ToString()))
                            : item.ToString()));
                        break;
                    default:
                        if (token.Type != JTokenType.Null) result[prefix] = token.ToString();
                        break;
                }
            }
            // Routers are listed one per id so a change names the router, not its position.
            var routers = json["Routers"] as JArray;
            json.Remove("Routers");
            Walk(json, "");
            foreach (var router in routers?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string id = router.Value<string>("Id") ?? "?";
                foreach (string skip in new[] { "Id", "CreatedUtc", "UpdatedUtc", "BuiltIn", "Kind" }) router.Remove(skip);
                Walk(router, "Router " + id);
            }
            return result;
        }

        private static string Label(string key)
        {
            // "Lanes.Projects/commander.Router" → "Projects › Commander: router"
            string[] parts = key.Split('.');
            string field(int i) => string.Join(" ", parts.Skip(i)).ToLowerInvariant();
            if (parts[0] == "Lanes" && parts.Length >= 3)
            {
                var lane = parts[1].Split('/');
                var desc = lane.Length == 2 ? LlmConsumerCatalog.Find(lane[0], lane[1]) : null;
                return $"{(lane.Length == 2 ? lane[0] : parts[1])} › {desc?.Label ?? (lane.Length == 2 ? lane[1] : "")} {field(2)}";
            }
            if (parts[0] == "Services" && parts.Length >= 3) return $"{parts[1]} {field(2)}";
            if (parts[0] == "Default") return $"Default {field(1)}";
            if (parts[0] == "Override") return $"Emergency override {field(1)}";
            if (parts[0] == "Capture" && parts.Length >= 3) return $"Text capture for {parts[1]} {field(2)}";
            return key;
        }

        private static string? Clip(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string clean = value.Trim();
            return clean.Length <= max ? clean : clean[..max];
        }
    }
}
