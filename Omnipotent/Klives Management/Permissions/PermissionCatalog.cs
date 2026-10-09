using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Omnipotent.Profiles.Permissions
{
    /// <summary>
    /// Every permission the system knows about. Static keys come from <see cref="PermissionSetAttribute"/>
    /// classes; dynamic keys (one per KliveTools tool) are registered at runtime. Keys are unique — a
    /// duplicate throws at type initialisation so it fails at startup and in tests, never silently.
    /// </summary>
    public static class PermissionCatalog
    {
        private static readonly ConcurrentDictionary<string, PermissionDef> byKey = new(StringComparer.Ordinal);
        private static readonly object loadLock = new();
        private static volatile bool loaded;
        private static long version;

        /// <summary>Lower-case dot-separated segments; dashes allowed inside a segment.</summary>
        private static readonly Regex KeyFormat = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*){2,}$", RegexOptions.Compiled);

        /// <summary>Raised after a key is added (including dynamic ones registered after startup).</summary>
        public static event Action<PermissionDef>? Registered;

        /// <summary>Bumped whenever a key is added, so cached catalog views can tell they are stale.</summary>
        public static long Version => Interlocked.Read(ref version);

        internal static void Add(PermissionDef def)
        {
            if (def.IsStandard && !KeyFormat.IsMatch(def.Key))
                throw new InvalidOperationException($"Permission key '{def.Key}' is not in service.area.action form.");
            if (!byKey.TryAdd(def.Key, def))
            {
                if (byKey.TryGetValue(def.Key, out var existing) && ReferenceEquals(existing, def)) return;
                throw new InvalidOperationException($"Duplicate permission key '{def.Key}'.");
            }
            Interlocked.Increment(ref version);
            try { Registered?.Invoke(def); } catch { /* listeners must never break registration */ }
        }

        /// <summary>
        /// Runs the static constructor of every <see cref="PermissionSetAttribute"/> class in this
        /// assembly, so the catalog is complete before anything enumerates it.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (loaded) return;
            lock (loadLock)
            {
                if (loaded) return;
                Type[] types;
                try { types = typeof(PermissionCatalog).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                foreach (var type in types)
                {
                    if (type.GetCustomAttribute<PermissionSetAttribute>() == null) continue;
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                }
                loaded = true;
            }
        }

        public static PermissionDef? Get(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            EnsureLoaded();
            return byKey.TryGetValue(key, out var def) ? def : null;
        }

        /// <summary>All grantable (standard) keys, ordered by service, area, tier and key.</summary>
        public static IReadOnlyList<PermissionDef> All
        {
            get
            {
                EnsureLoaded();
                return byKey.Values
                    .Where(d => d.IsStandard)
                    .OrderBy(d => d.Service, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Area, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => (int)d.Tier)
                    .ThenBy(d => d.Key, StringComparer.Ordinal)
                    .ToList();
            }
        }

        public static bool IsKnown(string key) => Get(key)?.IsStandard == true;

        /// <summary>
        /// Registers a key discovered at runtime (one per KliveTools tool). Registering the same key
        /// again returns the existing definition, so a restarted service doesn't fail.
        /// </summary>
        public static PermissionDef RegisterDynamic(string key, string service, string serviceKey, string area,
            PermissionTier tier, string title, string description, ProfileRank legacyRank, bool sensitive = false)
        {
            EnsureLoaded();
            if (byKey.TryGetValue(key, out var existing)) return existing;
            var def = new PermissionDef(key, service, serviceKey, area, tier, title, description, legacyRank,
                PermissionKind.Standard, Array.Empty<string>(), Array.Empty<string>(), sensitive);
            try { Add(def); }
            catch (InvalidOperationException) when (byKey.TryGetValue(key, out var raced)) { return raced; }
            return def;
        }

        /// <summary>
        /// Expands a set of granted keys with everything they imply (transitively). Unknown keys are
        /// kept — a grant for a tool that hasn't registered yet must not vanish.
        /// </summary>
        public static HashSet<string> Expand(IEnumerable<string> keys)
        {
            EnsureLoaded();
            var result = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(keys);
            while (stack.Count > 0)
            {
                string key = stack.Pop();
                if (!result.Add(key)) continue;
                if (byKey.TryGetValue(key, out var def))
                {
                    foreach (var implied in def.Implies) stack.Push(implied);
                }
            }
            return result;
        }

        /// <summary>For tests: verifies every <see cref="PermissionDef.Implies"/> target exists.</summary>
        public static IReadOnlyList<string> FindDanglingImplications()
        {
            EnsureLoaded();
            return byKey.Values.SelectMany(d => d.Implies.Where(i => !byKey.ContainsKey(i)).Select(i => $"{d.Key} -> {i}")).ToList();
        }
    }
}
