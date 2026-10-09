using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Profiles.Permissions
{
    /// <summary>
    /// How much information or power a permission hands out. Keys inside one service are split by
    /// tier so a profile can be given, say, a service's summaries without its raw records, or its
    /// records without the ability to change anything.
    /// </summary>
    public enum PermissionTier
    {
        /// <summary>Status, counts and summaries.</summary>
        Glance = 1,
        /// <summary>Full records, history and content.</summary>
        Read = 2,
        /// <summary>Routine, mostly reversible operations.</summary>
        Act = 3,
        /// <summary>Creating, deleting and configuring.</summary>
        Manage = 4,
        /// <summary>Live money, host control, secrets and other profiles.</summary>
        Critical = 5,
    }

    public enum PermissionKind
    {
        /// <summary>Granted per profile.</summary>
        Standard = 0,
        /// <summary>No authentication at all (the old <c>Anybody</c>).</summary>
        Public = 1,
        /// <summary>Any enabled, authenticated profile. Exempt from read-only and tier checks.</summary>
        SignedIn = 2,
    }

    /// <summary>
    /// Marks a static class whose <see cref="PermissionDef"/> fields belong in the catalog. The
    /// catalog runs these classes' static constructors on first use so every key is known before
    /// profiles are migrated or the console lists them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PermissionSetAttribute : Attribute { }

    /// <summary>
    /// One grantable permission. Every API route is registered with exactly one of these; a
    /// permission covers one or more routes. Instances are created through a
    /// <see cref="PermissionGroup"/> (or <see cref="PermissionCatalog.RegisterDynamic"/>) and are
    /// registered in <see cref="PermissionCatalog"/> as they are created.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PermissionDef
    {
        internal PermissionDef(string key, string service, string serviceKey, string area, PermissionTier tier,
            string title, string description, ProfileRank legacyRank, PermissionKind kind,
            IReadOnlyList<string> implies, IReadOnlyList<string> refines, bool sensitive)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A permission needs a key.", nameof(key));
            Key = key;
            Service = service;
            ServiceKey = serviceKey;
            Area = area;
            Tier = tier;
            Title = title;
            Description = description;
            LegacyRank = legacyRank;
            Kind = kind;
            Implies = implies;
            Refines = refines;
            Sensitive = sensitive;
        }

        /// <summary>Stable identifier, <c>service.area.action</c> (e.g. <c>omnitrader.orders.place</c>).</summary>
        [JsonProperty("key")] public string Key { get; }

        /// <summary>Display name of the owning service, e.g. "OmniTrader".</summary>
        [JsonProperty("service")] public string Service { get; }

        /// <summary>The key prefix shared by the service's permissions, e.g. "omnitrader".</summary>
        [JsonProperty("serviceKey")] public string ServiceKey { get; }

        /// <summary>Display name of the area inside the service, e.g. "Orders".</summary>
        [JsonProperty("area")] public string Area { get; }

        [JsonProperty("tier")] public PermissionTier Tier { get; }

        [JsonProperty("title")] public string Title { get; }

        [JsonProperty("description")] public string Description { get; }

        /// <summary>
        /// Raw private data, money or credentials. The console flags these so they are never granted
        /// by accident.
        /// </summary>
        [JsonProperty("sensitive")] public bool Sensitive { get; }

        [JsonProperty("kind"), JsonConverter(typeof(StringEnumConverter))] public PermissionKind Kind { get; }

        /// <summary>Keys this one implies. Granting a Manage key also gives its matching Read key.</summary>
        [JsonProperty("implies")] public IReadOnlyList<string> Implies { get; }

        /// <summary>
        /// Routes that are gated by another key but consult this one inside their handler (for
        /// example "reveal secrets" on a list route). Shown beside the key's own routes.
        /// </summary>
        [JsonProperty("refines")] public IReadOnlyList<string> Refines { get; }

        /// <summary>
        /// The lowest retired rank that could use this key's routes. Migration grants the key to
        /// every profile at or above it so converting ranks to permissions preserves access exactly.
        /// <see cref="ProfileRank.Klives"/> means owner-only.
        /// </summary>
        [JsonProperty("legacyRank")] public ProfileRank LegacyRank { get; }

        public bool IsStandard => Kind == PermissionKind.Standard;

        /// <summary>
        /// Keys only the owner may hand out: Tier-5 profile administration. A non-owner holding them
        /// still cannot delegate them, so control over other profiles can never spread.
        /// </summary>
        [JsonProperty("ownerGrantOnly")]
        public bool OwnerGrantOnly => Tier == PermissionTier.Critical && ServiceKey == "profiles";

        public override string ToString() => Key;
    }

    /// <summary>
    /// Builds the keys of one service. Keeps the per-service permission files short:
    /// <code>
    /// static readonly PermissionGroup G = new("omnitrader", "OmniTrader");
    /// public static readonly PermissionDef StatusView = G.Define("status.view", "Status", PermissionTier.Glance, ...);
    /// </code>
    /// </summary>
    public sealed class PermissionGroup
    {
        public PermissionGroup(string serviceKey, string serviceName)
        {
            ServiceKey = serviceKey;
            ServiceName = serviceName;
        }

        public string ServiceKey { get; }
        public string ServiceName { get; }

        /// <param name="suffix">The part after the service key, e.g. <c>orders.place</c>.</param>
        /// <param name="legacyRank">Lowest retired rank whose routes this key now covers.</param>
        /// <param name="implies">Other keys of this service (suffixes, e.g. <c>orders.read</c>) this one implies.</param>
        /// <param name="refines">Routes that check this key inside their handler.</param>
        public PermissionDef Define(string suffix, string area, PermissionTier tier, string title, string description,
            ProfileRank legacyRank, string[]? implies = null, string[]? refines = null, bool sensitive = false)
        {
            string key = ServiceKey + "." + suffix;
            var def = new PermissionDef(key, ServiceName, ServiceKey, area, tier, title, description, legacyRank,
                PermissionKind.Standard, Qualify(implies), refines ?? Array.Empty<string>(), sensitive);
            PermissionCatalog.Add(def);
            return def;
        }

        private string[] Qualify(string[]? implies)
        {
            if (implies == null || implies.Length == 0) return Array.Empty<string>();
            return implies.Select(k => k.StartsWith(ServiceKey + ".", StringComparison.Ordinal) ? k : ServiceKey + "." + k).ToArray();
        }
    }
}
