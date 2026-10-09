using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Frozen;

namespace Omnipotent.Profiles.Permissions
{
    /// <summary>One permission held by a profile. Grants can be temporary.</summary>
    public sealed class PermissionGrant
    {
        public string Key { get; set; } = "";
        public DateTime GrantedUtc { get; set; }
        public string? GrantedById { get; set; }
        /// <summary>Null = permanent.</summary>
        public DateTime? ExpiresUtc { get; set; }
        public string? Note { get; set; }

        public bool IsActive(DateTime nowUtc) => ExpiresUtc == null || ExpiresUtc.Value > nowUtc;

        public PermissionGrant Clone() => (PermissionGrant)MemberwiseClone();
    }

    /// <summary>Why a request was refused. Values are stable: they are sent to the website.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AccessDenyReason
    {
        None = 0,
        /// <summary>No credential was sent.</summary>
        NoCredential,
        /// <summary>The credential matches no session or profile (or the profile was deleted).</summary>
        InvalidCredential,
        SessionRevoked,
        SessionExpired,
        /// <summary>Login is turned off for the profile. Ends the session.</summary>
        ProfileDisabled,
        /// <summary>Temporarily suspended. The session stays, everything but SignedIn routes is refused.</summary>
        Suspended,
        /// <summary>Read-only lockdown: every key of tier Act or above is refused.</summary>
        ReadOnly,
        /// <summary>Signed in, but the profile does not hold the route's permission.</summary>
        MissingPermission,
    }

    public readonly struct AccessDecision
    {
        public AccessDecision(bool allowed, AccessDenyReason reason, PermissionDef permission)
        {
            Allowed = allowed;
            Reason = reason;
            Permission = permission;
        }

        public bool Allowed { get; }
        public AccessDenyReason Reason { get; }
        public PermissionDef Permission { get; }

        /// <summary>
        /// The caller is not (or no longer) authenticated: answered with 401 and the website signs out.
        /// Everything else is a 403 the website shows in place, without signing out.
        /// </summary>
        public bool IsAuthenticationFailure => AccessEvaluator.IsAuthenticationFailure(Reason);
    }

    /// <summary>
    /// The single access decision used by the HTTP pipeline, /batch, WebSocket upgrades and in-handler
    /// checks. Pure and O(1): it reads only the in-memory profile snapshot.
    /// </summary>
    public static class AccessEvaluator
    {
        public static bool IsAuthenticationFailure(AccessDenyReason reason) => reason is
            AccessDenyReason.NoCredential or AccessDenyReason.InvalidCredential or AccessDenyReason.SessionRevoked
            or AccessDenyReason.SessionExpired or AccessDenyReason.ProfileDisabled;

        /// <param name="profile">The resolved profile, or null when no valid credential was presented.</param>
        /// <param name="credentialFailure">Why credential resolution failed (when <paramref name="profile"/> is null).</param>
        public static AccessDecision Evaluate(KMProfileManager.KMProfile? profile, PermissionDef permission,
            AccessDenyReason credentialFailure = AccessDenyReason.None, DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(permission);
            if (permission.Kind == PermissionKind.Public) return Allow(permission);

            if (profile == null)
            {
                var reason = credentialFailure == AccessDenyReason.None ? AccessDenyReason.NoCredential : credentialFailure;
                return Deny(reason, permission);
            }

            DateTime now = nowUtc ?? DateTime.UtcNow;
            if (!profile.CanLogin) return Deny(AccessDenyReason.ProfileDisabled, permission);

            // SignedIn routes stay open to suspended and read-only profiles: they are how the site
            // learns *why* everything else is locked.
            if (permission.Kind == PermissionKind.SignedIn) return Allow(permission);

            if (profile.IsOwner) return Allow(permission);
            if (profile.IsSuspended(now)) return Deny(AccessDenyReason.Suspended, permission);
            if (profile.ReadOnly && permission.Tier >= PermissionTier.Act) return Deny(AccessDenyReason.ReadOnly, permission);

            return profile.HoldsPermission(permission.Key, now)
                ? Allow(permission)
                : Deny(AccessDenyReason.MissingPermission, permission);
        }

        /// <summary>Convenience for handlers: may this profile use the key right now?</summary>
        public static bool Can(KMProfileManager.KMProfile? profile, PermissionDef permission, DateTime? nowUtc = null)
            => Evaluate(profile, permission, AccessDenyReason.None, nowUtc).Allowed;

        private static AccessDecision Allow(PermissionDef p) => new(true, AccessDenyReason.None, p);
        private static AccessDecision Deny(AccessDenyReason r, PermissionDef p) => new(false, r, p);
    }

    /// <summary>
    /// The precomputed, immutable view of a profile's grants: the effective key set (with implied
    /// keys expanded) and the earliest moment a temporary grant lapses, after which it is rebuilt.
    /// </summary>
    internal sealed class ProfileAccessSnapshot
    {
        public ProfileAccessSnapshot(FrozenSet<string> effective, DateTime? rebuildAfterUtc)
        {
            Effective = effective;
            RebuildAfterUtc = rebuildAfterUtc;
        }

        public FrozenSet<string> Effective { get; }
        public DateTime? RebuildAfterUtc { get; }

        public static ProfileAccessSnapshot Build(IEnumerable<PermissionGrant> grants, DateTime nowUtc)
        {
            var active = new List<string>();
            DateTime? nextExpiry = null;
            foreach (var g in grants)
            {
                if (g == null || string.IsNullOrWhiteSpace(g.Key)) continue;
                if (!g.IsActive(nowUtc)) continue;
                active.Add(g.Key);
                if (g.ExpiresUtc is DateTime exp && (nextExpiry == null || exp < nextExpiry)) nextExpiry = exp;
            }
            return new ProfileAccessSnapshot(PermissionCatalog.Expand(active).ToFrozenSet(StringComparer.Ordinal), nextExpiry);
        }
    }
}
