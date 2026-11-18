using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Demiurge.Stimulus
{
    /// <summary>
    /// CRUD store for stimulus hooks (§5.1), per Steward. Every mutation appends a HookChanged
    /// event to the log so hook changes are themselves part of the timeline. One JSON file per
    /// Steward, atomic rewrite.
    /// </summary>
    public class StimulusHookStore
    {
        private readonly StewardEventLogStore eventLog;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public StimulusHookStore(StewardEventLogStore eventLog)
        {
            this.eventLog = eventLog;
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeStimulusDirectory), "Hooks");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string HooksPath(string stewardID) => Path.Combine(dir, stewardID + ".hooks.json");

        public StimulusHookRecord Create(StimulusHookRecord hook)
        {
            Validate(hook);
            if (string.IsNullOrWhiteSpace(hook.HookID)) hook.HookID = Guid.NewGuid().ToString("N");
            EnsureIngressToken(hook);
            lock (LockFor(hook.StewardID))
            {
                var hooks = LoadLocked(hook.StewardID);
                hooks.Add(hook);
                SaveLocked(hook.StewardID, hooks);
            }
            LogChange(hook.StewardID, $"Hook created: {hook.SourceKind} → {hook.DestinationAgentID} ({hook.HookID}).");
            return hook;
        }

        public bool Update(StimulusHookRecord hook)
        {
            Validate(hook);
            lock (LockFor(hook.StewardID))
            {
                var hooks = LoadLocked(hook.StewardID);
                int i = hooks.FindIndex(h => h.HookID == hook.HookID);
                if (i < 0) return false;
                hooks[i] = hook;
                SaveLocked(hook.StewardID, hooks);
            }
            LogChange(hook.StewardID, $"Hook updated: {hook.HookID}.");
            return true;
        }

        private static void Validate(StimulusHookRecord hook)
        {
            if (!hook.SourceKind.Equals("timer", StringComparison.OrdinalIgnoreCase)) return;

            JObject spec;
            try
            {
                spec = JObject.Parse(string.IsNullOrWhiteSpace(hook.SourceSpecJson) ? "{}" : hook.SourceSpecJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Timer source spec must be a valid JSON object.", ex);
            }

            if (spec["firstRunUtc"] is not JToken first) return;
            if (first.Type != JTokenType.String
                || !DateTimeOffset.TryParse(first.Value<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                throw new InvalidOperationException(
                    "Timer firstRunUtc must be an ISO-8601 timestamp, e.g. 2026-07-14T09:00:00Z.");
        }

        public bool Delete(string stewardID, string hookID)
        {
            bool removed;
            lock (LockFor(stewardID))
            {
                var hooks = LoadLocked(stewardID);
                removed = hooks.RemoveAll(h => h.HookID == hookID) > 0;
                if (removed) SaveLocked(stewardID, hooks);
            }
            if (removed) LogChange(stewardID, $"Hook deleted: {hookID}.");
            return removed;
        }

        public List<StimulusHookRecord> List(string stewardID)
        {
            lock (LockFor(stewardID)) return LoadLocked(stewardID);
        }

        public StimulusHookRecord? Get(string stewardID, string hookID)
        {
            lock (LockFor(stewardID)) return LoadLocked(stewardID).FirstOrDefault(h => h.HookID == hookID);
        }

        /// <summary>All hooks across all Stewards (for the adapters to arm on boot).</summary>
        public List<StimulusHookRecord> AllHooks()
        {
            var all = new List<StimulusHookRecord>();
            if (!Directory.Exists(dir)) return all;
            foreach (var f in Directory.EnumerateFiles(dir, "*.hooks.json"))
            {
                try { all.AddRange(JsonConvert.DeserializeObject<List<StimulusHookRecord>>(File.ReadAllText(f)) ?? new()); }
                catch { }
            }
            return all;
        }

        private void LogChange(string stewardID, string text) =>
            eventLog.Append(new StewardEvent { StewardID = stewardID, Type = StewardEventTypes.HookChanged, Author = "commander", Text = text });

        private List<StimulusHookRecord> LoadLocked(string stewardID)
        {
            string path = HooksPath(stewardID);
            if (!File.Exists(path)) return new();
            try
            {
                var hooks = JsonConvert.DeserializeObject<List<StimulusHookRecord>>(File.ReadAllText(path)) ?? new();
                bool changed = false;
                foreach (var hook in hooks)
                    if (hook.SourceKind == "webhook" && string.IsNullOrWhiteSpace(hook.IngressToken))
                    {
                        EnsureIngressToken(hook);
                        changed = true;
                    }
                if (changed) SaveLocked(stewardID, hooks);
                return hooks;
            }
            catch { return new(); }
        }

        public string RotateIngressToken(string stewardID, string hookID)
        {
            lock (LockFor(stewardID))
            {
                var hooks = LoadLocked(stewardID);
                var hook = hooks.FirstOrDefault(h => h.HookID == hookID && h.SourceKind == "webhook")
                    ?? throw new InvalidOperationException("webhook hook not found");
                hook.IngressToken = NewIngressToken();
                SaveLocked(stewardID, hooks);
                return hook.IngressToken;
            }
        }

        private static void EnsureIngressToken(StimulusHookRecord hook)
        {
            if (hook.SourceKind == "webhook" && string.IsNullOrWhiteSpace(hook.IngressToken))
                hook.IngressToken = NewIngressToken();
        }

        private static string NewIngressToken() =>
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        private void SaveLocked(string stewardID, List<StimulusHookRecord> hooks)
        {
            string path = HooksPath(stewardID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(hooks, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
