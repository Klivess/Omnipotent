using System.Reflection;
using System.Text;
using Omnipotent.Service_Manager;

namespace Omnipotent.Services.ServiceTools;

/// <summary>One service as the agent sees it: its key, what it is, and every op it can perform.</summary>
public sealed class OmniServiceSurface
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Summary { get; init; }
    public required Type ServiceType { get; init; }

    /// <summary>True when the service carries [OmniServiceTools]. Annotated services get dedicated
    /// generated tools; unannotated ones are reachable through the universal omniservice tool only.</summary>
    public required bool Annotated { get; init; }

    public required IReadOnlyList<OmniOperation> Operations { get; init; }

    /// <summary>The dedicated tools generated for this service (empty when not annotated).</summary>
    public IReadOnlyList<OmniToolGroup> Groups { get; internal set; } = Array.Empty<OmniToolGroup>();
}

/// <summary>One generated tool: a service, optionally narrowed to a group, with its ops as an op enum.</summary>
public sealed class OmniToolGroup
{
    public required string ToolName { get; init; }
    public required string Summary { get; init; }
    public required OmniServiceSurface Service { get; init; }
    public required IReadOnlyList<OmniOperation> Operations { get; init; }
}

/// <summary>
/// The catalogue of everything KliveAgent can do to an OmniService without writing C#.
///
/// Built once from assembly metadata - NOT from live instances - so it does not depend on service
/// startup order and can be constructed before the service graph is up. Instances are resolved
/// lazily at call time by <see cref="OmniToolInvoker"/>.
///
/// Two tiers:
///   verified   - methods carrying [OmniTool]. Curated description, real schema, may write.
///   reflective - derived from an unannotated service's public read methods, so a service is useful
///                the day it is written and long before anyone annotates it. Read-only by
///                construction: only read-prefixed methods are surfaced, and the invoker refuses
///                anything else, because nothing has classified whether it writes.
/// </summary>
public sealed class OmniToolRegistry
{
    /// <summary>Method-name prefixes treated as reads. A reflective op must match one of these; that
    /// is the whole safety story for unannotated services, so keep it conservative.</summary>
    private static readonly string[] ReadPrefixes =
    {
        "Get", "List", "Find", "Search", "Read", "Describe", "Count", "Query", "Preview", "Compose"
    };

    /// <summary>Never surfaced reflectively, whatever their name says. These either restart the world,
    /// leak the service graph, or return something the model cannot use.</summary>
    private static readonly HashSet<string> ReflectiveDenyList = new(StringComparer.Ordinal)
    {
        "GetType", "GetHashCode", "GetActiveServices", "GetServiceByName", "GetServicesByType",
        "GetServiceObject", "GetServiceMonitor", "GetLoggerService", "GetTimeManagerService",
        "GetDataHandler", "GetSeleniumManager", "GetOmniGlobalSettingsManager", "GetThread",
        "GetServiceUptime", "GetManagerUptime", "GetName", "GetThreadAnteriority",
    };

    private readonly Dictionary<string, OmniToolGroup> toolsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OmniServiceSurface> servicesByKey = new(StringComparer.OrdinalIgnoreCase);

    private OmniToolRegistry(IReadOnlyList<OmniServiceSurface> services)
    {
        Services = services;
        Operations = services.SelectMany(s => s.Operations).ToList();
        foreach (var s in services)
        {
            servicesByKey[s.Key] = s;
            foreach (var g in s.Groups) toolsByName[g.ToolName] = g;
        }
    }

    /// <summary>The process-wide catalogue. Built from assembly metadata and immutable, so one
    /// instance is the truth for every caller and the static tool-definition builders can reach it
    /// without threading a parameter through KliveAgentBrain's static surface.</summary>
    public static OmniToolRegistry Shared => SharedLazy.Value;

    private static readonly Lazy<OmniToolRegistry> SharedLazy =
        new(() => Build(), LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<OmniServiceSurface> Services { get; }
    public IReadOnlyList<OmniOperation> Operations { get; }

    /// <summary>Every dedicated tool the registry can generate, in service then group order.</summary>
    public IEnumerable<OmniToolGroup> Tools => Services.SelectMany(s => s.Groups);

    /// <summary>True if the name is a generated service tool. Load-bearing in KliveAgentBrain: any tool
    /// name it does not recognise is routed to execute_csharp, which would compile the arguments JSON
    /// as C# and fail every time.</summary>
    public bool IsServiceTool(string? toolName)
        => !string.IsNullOrEmpty(toolName) && toolsByName.ContainsKey(toolName);

    public OmniToolGroup? GetTool(string toolName)
        => toolsByName.TryGetValue(toolName, out var t) ? t : null;

    public OmniServiceSurface? GetService(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey)) return null;
        if (servicesByKey.TryGetValue(serviceKey, out var s)) return s;
        // Tolerate a group tool name ("omniscience_people") or a display name ("Klives Management ...").
        var trimmed = serviceKey.Trim();
        if (toolsByName.TryGetValue(trimmed, out var tool)) return tool.Service;
        return Services.FirstOrDefault(x =>
            string.Equals(x.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.ServiceType.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a call on a generated tool: the op must belong to that tool.</summary>
    public OmniOperation? FindOnTool(string toolName, string op)
    {
        var tool = GetTool(toolName);
        return tool?.Operations.FirstOrDefault(o => string.Equals(o.Op, op, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a call routed through the universal omniservice tool, where the model names a
    /// service and an op rather than a generated tool.</summary>
    public OmniOperation? FindOnService(string serviceKey, string op)
    {
        var svc = GetService(serviceKey);
        return svc?.Operations.FirstOrDefault(o => string.Equals(o.Op, op, StringComparison.OrdinalIgnoreCase));
    }

    // -- Construction --

    public static OmniToolRegistry Build(Action<string>? log = null)
        => Build(typeof(OmniToolRegistry).Assembly, log);

    public static OmniToolRegistry Build(Assembly assembly, Action<string>? log = null)
    {
        var types = SafeGetTypes(assembly, log);

        var serviceTypes = types
            .Where(t => t.IsClass && !t.IsAbstract && typeof(OmniService).IsAssignableFrom(t) && t != typeof(OmniService))
            .ToList();

        // Every type that declares at least one [OmniTool] method - the service itself, or a store or
        // engine the service owns.
        var providerTypes = types
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Any(m => m.GetCustomAttribute<OmniToolAttribute>() != null))
            .ToList();

        var surfaces = new List<OmniServiceSurface>();

        foreach (var serviceType in serviceTypes.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var meta = serviceType.GetCustomAttribute<OmniServiceToolsAttribute>();
            var key = Sanitize(meta?.ToolName ?? serviceType.Name);
            if (string.IsNullOrEmpty(key)) continue;

            var groupSummaries = serviceType.GetCustomAttributes<OmniToolGroupAttribute>()
                .ToDictionary(g => g.Group, g => g.Summary, StringComparer.OrdinalIgnoreCase);

            var display = DisplayNameFor(serviceType);
            var summary = meta?.Summary ?? $"The {display} service.";
            var ops = new List<OmniOperation>();

            if (meta != null)
            {
                // Annotated: collect [OmniTool] methods on the service and on anything it exposes.
                foreach (var provider in providerTypes)
                {
                    var accessor = ResolveAccessor(serviceType, provider);
                    if (accessor == null) continue;

                    foreach (var m in provider.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var attr = m.GetCustomAttribute<OmniToolAttribute>();
                        if (attr == null) continue;

                        var op = BuildVerifiedOperation(serviceType, display, key, provider, m, attr,
                            accessor.IsSelf ? null : accessor.Getter, log);
                        if (op != null) ops.Add(op);
                    }
                }

                if (ops.Count == 0)
                    log?.Invoke($"[ServiceTools] {display} carries [OmniServiceTools] but declares no [OmniTool] methods.");
            }
            else
            {
                ops.AddRange(BuildReflectiveOperations(serviceType, display, key));
            }

            if (ops.Count == 0) continue;

            // Two ops that collide on (tool, op) would make one silently unreachable.
            var deduped = new List<OmniOperation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in ops)
            {
                if (seen.Add($"{op.ToolName} {op.Op}")) { deduped.Add(op); continue; }
                log?.Invoke($"[ServiceTools] duplicate op '{op.Op}' on tool '{op.ToolName}' ({op.Method.DeclaringType?.Name}.{op.Method.Name}) - ignored.");
            }

            var surface = new OmniServiceSurface
            {
                Key = key,
                DisplayName = display,
                Summary = summary,
                ServiceType = serviceType,
                Annotated = meta != null,
                Operations = deduped,
            };

            if (meta != null)
            {
                surface.Groups = deduped
                    .GroupBy(o => o.ToolName, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var groupName = g.First().Group;
                        var groupSummary = groupName != null && groupSummaries.TryGetValue(groupName, out var gs)
                            ? gs
                            : summary;
                        return new OmniToolGroup
                        {
                            ToolName = g.Key,
                            Summary = groupSummary,
                            Service = surface,
                            Operations = g.ToList(),
                        };
                    })
                    .ToList();
            }

            surfaces.Add(surface);
        }

        var registry = new OmniToolRegistry(surfaces);
        log?.Invoke($"[ServiceTools] catalogued {registry.Operations.Count} operations across {surfaces.Count} services "
                  + $"({surfaces.Count(s => s.Annotated)} annotated, {registry.Tools.Count()} dedicated tools).");
        return registry;
    }

    private static OmniOperation? BuildVerifiedOperation(
        Type serviceType, string display, string serviceKey, Type provider, MethodInfo method,
        OmniToolAttribute attr, Func<object, object?>? accessor, Action<string>? log)
    {
        foreach (var p in method.GetParameters())
        {
            if (OmniToolSchema.IsInjected(p.ParameterType)) continue;
            if (!OmniToolSchema.IsRepresentable(p.ParameterType))
            {
                log?.Invoke($"[ServiceTools] skipping {provider.Name}.{method.Name}: parameter '{p.Name}' "
                          + $"({p.ParameterType.Name}) cannot be expressed as JSON Schema.");
                return null;
            }
        }

        var group = string.IsNullOrWhiteSpace(attr.Group) ? null : Sanitize(attr.Group!);
        var parameters = method.GetParameters();

        return new OmniOperation
        {
            ToolName = group == null ? serviceKey : $"{serviceKey}_{group}",
            ServiceToolBase = serviceKey,
            Group = group,
            Op = attr.Op,
            Description = attr.Description,
            ParameterSchema = OmniToolSchema.BuildParameterSchema(method),
            Method = method,
            DeclaringType = provider,
            ServiceType = serviceType,
            ServiceDisplayName = display,
            Mutating = attr.Mutating || attr.Destructive,
            Destructive = attr.Destructive,
            Verified = true,
            Parameters = parameters,
            CancellationParameter = parameters.FirstOrDefault(p => p.ParameterType == typeof(CancellationToken)),
            InstanceAccessor = accessor,
        };
    }

    private static IEnumerable<OmniOperation> BuildReflectiveOperations(Type serviceType, string display, string key)
    {
        foreach (var m in serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.DeclaringType == typeof(OmniService) || m.DeclaringType == typeof(object)) continue;
            if (m.IsSpecialName || m.IsGenericMethod) continue;
            if (ReflectiveDenyList.Contains(m.Name)) continue;
            if (!ReadPrefixes.Any(pfx => m.Name.StartsWith(pfx, StringComparison.Ordinal))) continue;

            // A read that returns nothing tells the agent nothing.
            if (m.ReturnType == typeof(void) || m.ReturnType == typeof(Task)) continue;

            var parameters = m.GetParameters();
            if (parameters.Any(p => !OmniToolSchema.IsInjected(p.ParameterType)
                                 && !OmniToolSchema.IsRepresentable(p.ParameterType))) continue;

            yield return new OmniOperation
            {
                ToolName = key,
                ServiceToolBase = key,
                Group = null,
                Op = ToSnakeCase(m.Name),
                Description = $"{display}: {Humanise(m.Name)}. (Reflective, unverified - derived from the "
                            + "method signature, not a written description. Read-only.)",
                ParameterSchema = OmniToolSchema.BuildParameterSchema(m),
                Method = m,
                DeclaringType = serviceType,
                ServiceType = serviceType,
                ServiceDisplayName = display,
                Mutating = false,
                Destructive = false,
                Verified = false,
                Parameters = parameters,
                CancellationParameter = parameters.FirstOrDefault(p => p.ParameterType == typeof(CancellationToken)),
                InstanceAccessor = null,
            };
        }
    }

    /// <summary>Finds how to get from a live service instance to an object of <paramref name="provider"/>.
    /// Either the service IS one, or it exposes one through a public property or field.</summary>
    private static ServiceAccessor? ResolveAccessor(Type serviceType, Type provider)
    {
        if (provider == serviceType || provider.IsAssignableFrom(serviceType))
            return new ServiceAccessor(true, _ => null);

        foreach (var p in serviceType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (!provider.IsAssignableFrom(p.PropertyType)) continue;
            var captured = p;
            return new ServiceAccessor(false, instance => captured.GetValue(instance));
        }

        foreach (var f in serviceType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!provider.IsAssignableFrom(f.FieldType)) continue;
            var captured = f;
            return new ServiceAccessor(false, instance => captured.GetValue(instance));
        }

        return null;
    }

    private sealed record ServiceAccessor(bool IsSelf, Func<object, object?> Getter);

    private static List<Type> SafeGetTypes(Assembly assembly, Action<string>? log)
    {
        try { return assembly.GetTypes().ToList(); }
        catch (ReflectionTypeLoadException ex)
        {
            log?.Invoke($"[ServiceTools] {ex.LoaderExceptions.Length} type(s) failed to load; cataloguing the rest.");
            return ex.Types.Where(t => t != null).Cast<Type>().ToList();
        }
    }

    // -- Naming --

    /// <summary>Lowercases and strips anything a tool name may not contain. Tool names must match
    /// ^[a-zA-Z0-9_-]+$ for the provider APIs.</summary>
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '_' or '-') sb.Append(c);
            else if (c is ' ' or '.') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }

    /// <summary>GetPersonDossier -> get_person_dossier.</summary>
    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // Break before an uppercase run's final letter too, so "RunOCRPass" -> "run_ocr_pass".
                bool boundary = i > 0 && (!char.IsUpper(name[i - 1])
                                          || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (boundary) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Humanise(string name) => ToSnakeCase(name).Replace('_', ' ');

    private static string DisplayNameFor(Type serviceType)
    {
        // The instance's runtime name is the friendly one ("Klives Management Profile Manager"), but the
        // registry is built from metadata with no instance, so fall back to the type name.
        return serviceType.Name;
    }
}
