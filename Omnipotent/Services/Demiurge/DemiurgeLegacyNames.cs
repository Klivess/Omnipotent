using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Everything Demiurge needs to read what it wrote before Projects became Demiurge
    /// (November 2026), when a Steward was called a project.
    /// </summary>
    public static class DemiurgeLegacyNames
    {
        /// <summary>A Steward member's name as it was spelled before the rename, or null when the
        /// rename did not touch it. StewardID → ProjectID, PausedStewards → PausedProjects.</summary>
        public static string? LegacyName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string legacy = name.Replace("Steward", "Project", StringComparison.Ordinal)
                .Replace("steward", "project", StringComparison.Ordinal)
                .Replace("STEWARD", "PROJECT", StringComparison.Ordinal);
            return legacy == name ? null : legacy;
        }

        /// <summary>The current spelling of a name saved before the rename (the inverse of <see cref="LegacyName"/>).</summary>
        public static string CurrentName(string name) =>
            name.Replace("Project", "Steward", StringComparison.Ordinal)
                .Replace("project", "steward", StringComparison.Ordinal)
                .Replace("PROJECT", "STEWARD", StringComparison.Ordinal);

        /// <summary>Event types whose stored value predates the rename, old → current.</summary>
        private static readonly Dictionary<string, string> LegacyEventTypes = new(StringComparer.Ordinal)
        {
            ["project-file-changed"] = StewardEventTypes.StewardFileChanged,
            ["project-blocked"] = StewardEventTypes.StewardBlocked,
            ["project-unblocked"] = StewardEventTypes.StewardUnblocked,
        };

        /// <summary>An event type as the code knows it today, whatever spelling it was stored with.</summary>
        public static string CurrentEventType(string? type) =>
            type != null && LegacyEventTypes.TryGetValue(type, out var current) ? current : type ?? "";

        /// <summary>The old spellings of an event type, for queries that filter stored logs by type.</summary>
        public static IEnumerable<string> StoredEventTypes(string type)
        {
            yield return type;
            foreach (var (legacy, current) in LegacyEventTypes)
                if (current == type) yield return legacy;
        }

        /// <summary>The environment variable <c>DEMIURGE_<paramref name="suffix"/></c>, else the
        /// <c>PROJECTS_</c> name it had before the rename, so a host configured earlier keeps its setting.</summary>
        public static string? Environment(string suffix)
            => System.Environment.GetEnvironmentVariable("DEMIURGE_" + suffix)
               ?? System.Environment.GetEnvironmentVariable("PROJECTS_" + suffix);
    }

    /// <summary>
    /// Opts a type outside the Demiurge namespace into <see cref="DemiurgeLegacyNameResolver"/>,
    /// for records other services keep about Stewards.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class LegacyStewardNamesAttribute : Attribute { }

    /// <summary>
    /// Stored JSON from before the rename says "ProjectID", "ProjectPath", "PausedProjects"; the classes
    /// now say StewardID, StewardPath, PausedStewards. This resolver gives every renamed property of a
    /// Demiurge type a write-never alias under its old spelling, so old records — append-only logs
    /// included — load in place and take the new spelling the next time they are saved. Nothing is
    /// migrated on disk and nothing is ever written under an old name.
    ///
    /// It is installed as Json.NET's default resolver when the assembly loads, which covers every
    /// JsonConvert / ToObject call that does not bring its own resolver (all of Demiurge's stores).
    /// Types outside Demiurge are resolved exactly as before.
    /// </summary>
    public sealed class DemiurgeLegacyNameResolver : DefaultContractResolver
    {
        public static readonly DemiurgeLegacyNameResolver Instance = new();
        private static readonly JsonSerializerSettings DefaultSettings = new() { ContractResolver = Instance };
        private const string DemiurgeNamespace = "Omnipotent.Services.Demiurge";

        [ModuleInitializer]
        internal static void Install()
        {
            if (JsonConvert.DefaultSettings == null) JsonConvert.DefaultSettings = () => DefaultSettings;
        }

        internal static bool Applies(Type type)
        {
            string? ns = type.Namespace;
            if (ns != null && (ns == DemiurgeNamespace || ns.StartsWith(DemiurgeNamespace + ".", StringComparison.Ordinal)))
                return true;
            return type.GetCustomAttribute<LegacyStewardNamesAttribute>() != null;
        }

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var properties = base.CreateProperties(type, memberSerialization);
            if (!Applies(type)) return properties;
            var taken = new HashSet<string>(properties.Select(p => p.PropertyName ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.ToList())
            {
                if (property.Ignored || !property.Writable) continue;
                string? legacy = DemiurgeLegacyNames.LegacyName(property.PropertyName);
                if (legacy == null || !taken.Add(legacy)) continue;
                properties.Add(new JsonProperty
                {
                    PropertyName = legacy,
                    UnderlyingName = property.UnderlyingName,
                    PropertyType = property.PropertyType,
                    DeclaringType = property.DeclaringType,
                    ValueProvider = property.ValueProvider,
                    AttributeProvider = property.AttributeProvider,
                    Converter = property.Converter,
                    ItemConverter = property.ItemConverter,
                    ObjectCreationHandling = property.ObjectCreationHandling,
                    NullValueHandling = property.NullValueHandling,
                    DefaultValueHandling = property.DefaultValueHandling,
                    TypeNameHandling = property.TypeNameHandling,
                    Readable = false,
                    Writable = true,
                    ShouldSerialize = _ => false,
                });
            }
            return properties;
        }
    }

    /// <summary>
    /// String enum converter for Demiurge enums persisted by name: a member renamed with the rest of
    /// Demiurge (ProjectFile → StewardFile) still reads from its stored, pre-rename name.
    /// </summary>
    public sealed class DemiurgeEnumConverter : StringEnumConverter
    {
        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String && reader.Value is string text)
            {
                Type enumType = Nullable.GetUnderlyingType(objectType) ?? objectType;
                if (!Enum.TryParse(enumType, text, ignoreCase: true, out _)
                    && Enum.TryParse(enumType, DemiurgeLegacyNames.CurrentName(text), ignoreCase: true, out var renamed))
                    return renamed;
            }
            return base.ReadJson(reader, objectType, existingValue, serializer);
        }
    }
}
