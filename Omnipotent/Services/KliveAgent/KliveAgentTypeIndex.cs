using System.Reflection;
using System.Reflection.Metadata;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Public type names across the script reference set, by simple name. Two uses: the script repair adds
/// the one <c>using</c> a name needs ("BindingFlags" lives only in System.Reflection), and a compile
/// error for an unknown type can name the closest real ones and their namespaces instead of leaving the
/// model to guess again. Read straight from the same metadata the compiler sees, so no assembly is
/// loaded to build it. Built once, lazily; KliveAgent's warm-up builds it in the background.
/// </summary>
internal sealed class KliveAgentTypeIndex
{
    public static KliveAgentTypeIndex Shared { get; } = new();

    private readonly object gate = new();
    private Dictionary<string, string[]>? byName;

    /// <summary>A small index for tests: names mapped to their namespaces, no assemblies read.</summary>
    internal static KliveAgentTypeIndex FromMap(IReadOnlyDictionary<string, string[]> map) =>
        new() { byName = new Dictionary<string, string[]>(map, StringComparer.Ordinal) };

    public void EnsureBuilt() => _ = Map;

    private Dictionary<string, string[]> Map
    {
        get
        {
            var map = Volatile.Read(ref byName);
            if (map != null) return map;
            lock (gate)
            {
                byName ??= Build();
                return byName;
            }
        }
    }

    /// <summary>Namespaces that declare a public top-level type with this simple name (arity ignored).</summary>
    public IReadOnlyList<string> NamespacesOf(string simpleName)
    {
        string key = StripArity(simpleName);
        return Map.TryGetValue(key, out var namespaces) ? namespaces : Array.Empty<string>();
    }

    /// <summary>
    /// The one namespace a bare type name most plausibly means, or null when that is not clear. A name
    /// declared once is that one. Declared several times, the framework's copy wins when it is the only
    /// framework one (Timer is not clear: System.Threading and System.Timers both have it).
    /// </summary>
    public string? PreferredNamespaceOf(string simpleName)
    {
        var namespaces = NamespacesOf(simpleName);
        if (namespaces.Count == 0) return null;
        if (namespaces.Count == 1) return namespaces[0];
        var framework = namespaces.Where(IsFrameworkNamespace).ToList();
        if (framework.Count == 1) return framework[0];
        var own = namespaces.Where(n => n.StartsWith("Omnipotent", StringComparison.Ordinal)).ToList();
        return framework.Count == 0 && own.Count == 1 ? own[0] : null;
    }

    private static bool IsFrameworkNamespace(string ns) =>
        ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>The closest public type names to <paramref name="name"/>, as (name, namespace).</summary>
    public IReadOnlyList<(string Name, string Namespace)> Similar(string name, int max = 4)
    {
        string target = StripArity(name);
        if (target.Length < 3) return Array.Empty<(string, string)>();
        string lower = target.ToLowerInvariant();
        var scored = new List<(double Score, string Name, string Namespace)>();
        foreach (var (candidate, namespaces) in Map)
        {
            if (Math.Abs(candidate.Length - target.Length) > 4) continue;
            double score = KliveAgentNames.Similarity(lower, candidate.ToLowerInvariant());
            if (score <= 0) continue;
            foreach (var ns in namespaces.Take(2)) scored.Add((score, candidate, ns));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Namespace.Length)
            .Take(max).Select(x => (x.Name, x.Namespace)).ToList();
    }

    private static Dictionary<string, string[]> Build()
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var assembly in OmniScriptReferences.Metadata)
        {
            try
            {
                var reader = assembly.GetModules()[0].GetMetadataReader();
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    // Top-level public types only: a nested type is reached through its parent.
                    if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
                    if (type.Namespace.IsNil) continue;
                    string name = StripArity(reader.GetString(type.Name));
                    if (name.Length == 0 || name[0] == '<' || name.StartsWith("__", StringComparison.Ordinal)) continue;
                    string ns = reader.GetString(type.Namespace);
                    if (ns.Length == 0) continue;
                    if (!sets.TryGetValue(name, out var namespaces)) sets[name] = namespaces = new HashSet<string>(StringComparer.Ordinal);
                    namespaces.Add(ns);
                }
            }
            catch { /* a damaged image contributes nothing */ }
        }
        return sets.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    internal static string StripArity(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int cut = name.IndexOfAny(new[] { '`', '<' });
        return (cut >= 0 ? name[..cut] : name).Trim();
    }
}

/// <summary>Name similarity shared by the type index, the script diagnostics and the dynamic proxy's suggestions.</summary>
internal static class KliveAgentNames
{
    /// <summary>0 when not close; otherwise higher is closer. Substrings beat edit distance.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 100;
        if (b.Contains(a) || a.Contains(b)) return 10.0 + 1.0 / (1 + Math.Abs(a.Length - b.Length));
        int max = Math.Max(a.Length, b.Length);
        double ratio = 1.0 - (double)Levenshtein(a, b) / max;
        return ratio >= 0.5 ? ratio : 0;
    }

    internal static List<string> Closest(string target, IEnumerable<string> candidates, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(target)) return new List<string>();
        string lower = target.ToLowerInvariant();
        return candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.Ordinal)
            .Select(c => (c, score: Similarity(lower, c.ToLowerInvariant())))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(max)
            .Select(x => x.c)
            .ToList();
    }

    private static int Levenshtein(string s, string t)
    {
        var previous = new int[t.Length + 1];
        var current = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) previous[j] = j;
        for (int i = 1; i <= s.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[t.Length];
    }
}
