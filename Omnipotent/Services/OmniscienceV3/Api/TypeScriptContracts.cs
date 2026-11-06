using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// Generates the TypeScript types of the v3 HTTP contract from the C# types, so the website and the API cannot
    /// drift (v2's UI read fields the server had renamed and silently showed blanks). Roots are every
    /// <see cref="ApiContractAttribute"/> type plus <see cref="ExtraRoots"/>; everything reachable from them is
    /// emitted. The output is deterministic, and a test keeps <c>Docs/omniscience-v3/api-contracts.ts</c> current.
    /// <para>
    /// Rules enforced here: no DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly in a contract (instants are epoch
    /// ms, durations ms: T1, F559); no open generics other than collections; no two contract types with one name.
    /// </para>
    /// </summary>
    public static class TypeScriptContracts
    {
        /// <summary>Contract types that live outside the Api namespace (kernel records returned as-is).</summary>
        public static readonly IReadOnlyList<Type> ExtraRoots = new[]
        {
            typeof(JobPage), typeof(JobRecord),
            typeof(Kernel.Tactics.TacticEstimate), typeof(Kernel.Tactics.PlaybookRunRecord), typeof(Kernel.Tactics.TacticOutput),
            typeof(Kernel.Explore.ExploreQuery), typeof(Kernel.Explore.ExploreResult), typeof(Kernel.Explore.ExploreRecordsRequest), typeof(Kernel.Explore.ExploreRecordsPage),
        };

        private static readonly JsonNamingPolicy Naming = JsonNamingPolicy.CamelCase;
        private static readonly NullabilityInfoContext Nullability = new();

        public static IReadOnlyList<Type> Roots(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
            return types
                .Where(t => t.Namespace?.StartsWith("Omnipotent.Services.OmniscienceV3", StringComparison.Ordinal) == true)
                .Where(t => t.GetCustomAttribute<ApiContractAttribute>() != null)
                .Concat(ExtraRoots)
                .Distinct()
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        public static string Generate(IEnumerable<Type> roots)
        {
            var pending = new Queue<Type>(roots);
            var seen = new Dictionary<string, Type>(StringComparer.Ordinal);
            var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var type = pending.Dequeue();
                if (seen.TryGetValue(type.Name, out var existing))
                {
                    if (existing != type) throw new InvalidOperationException($"Two contract types are named {type.Name}: {existing.FullName} and {type.FullName}.");
                    continue;
                }
                seen[type.Name] = type;
                blocks[type.Name] = type.IsEnum ? EmitEnum(type) : EmitInterface(type, pending);
            }

            var sb = new StringBuilder();
            sb.Append("// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.\n");
            sb.Append("// Instants are UTC epoch milliseconds; durations are milliseconds.\n");
            foreach (var block in blocks.Values) sb.Append('\n').Append(block);
            return sb.ToString();
        }

        private static string EmitEnum(Type type)
        {
            var names = Enum.GetNames(type).Select(n => "\"" + n + "\"");
            return $"export type {type.Name} = {string.Join(" | ", names)};\n";
        }

        private static string EmitInterface(Type type, Queue<Type> pending)
        {
            if (type.IsGenericType) throw new InvalidOperationException($"Contract type {type.FullName} is generic; use a concrete record.");
            var sb = new StringBuilder();
            sb.Append("export interface ").Append(type.Name).Append(" {\n");
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .OrderBy(p => p.MetadataToken);
            foreach (var p in props)
            {
                string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? Naming.ConvertName(p.Name);
                var nullability = Nullability.Create(p);
                bool optional = Nullable.GetUnderlyingType(p.PropertyType) != null
                                || (!p.PropertyType.IsValueType && nullability.ReadState == NullabilityState.Nullable);
                string ts = Map(p.PropertyType, nullability, pending, $"{type.Name}.{p.Name}");
                sb.Append("  ").Append(name).Append(optional ? "?: " : ": ").Append(ts).Append(optional ? " | null" : "").Append(";\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Map(Type t, NullabilityInfo? info, Queue<Type> pending, string where)
        {
            var underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null) return Map(underlying, null, pending, where);

            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(DateOnly) || t == typeof(TimeOnly))
                throw new InvalidOperationException($"{where} is a {t.Name}; contracts carry UTC epoch milliseconds and millisecond durations (T1).");
            if (t == typeof(string) || t == typeof(char) || t == typeof(Guid)) return "string";
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint)
                || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
            if (t == typeof(JsonElement) || t == typeof(JsonDocument) || t == typeof(object)) return "unknown";

            if (t.IsArray)
                return Wrap(Map(t.GetElementType()!, info?.ElementType, pending, where + "[]")) + "[]";

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    if (args[0] != typeof(string)) throw new InvalidOperationException($"{where}: dictionary keys must be strings in a contract.");
                    return $"Record<string, {Map(args[1], info?.GenericTypeArguments.ElementAtOrDefault(1), pending, where + "{}")}>";
                }
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) || def == typeof(IEnumerable<>)
                    || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(HashSet<>))
                    return Wrap(Map(args[0], info?.GenericTypeArguments.ElementAtOrDefault(0), pending, where + "[]")) + "[]";
                throw new InvalidOperationException($"{where} uses generic type {t.Name}; contracts use concrete records and standard collections.");
            }

            if (t.IsEnum || t.IsClass || (t.IsValueType && !t.IsPrimitive))
            {
                pending.Enqueue(t);
                return t.Name;
            }
            throw new InvalidOperationException($"{where}: no TypeScript mapping for {t.FullName}.");
        }

        private static string Wrap(string ts) => ts.Contains(' ') ? "(" + ts + ")" : ts;
    }
}
