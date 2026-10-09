using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Profiles;

public sealed class AccessEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    internal static KMProfile Profile(ProfileRank rank = ProfileRank.Guest, params string[] keys) => new()
    {
        UserID = Guid.NewGuid().ToString("N")[..8],
        Name = "Test",
        Rank = rank,
        CanLogin = true,
        SchemaVersion = KMProfileManager.CurrentSchemaVersion,
        Grants = keys.Select(k => new PermissionGrant { Key = k, GrantedUtc = Now.AddDays(-1) }).ToList(),
    };

    [Fact]
    public void Public_IsAllowedWithoutAProfile()
    {
        Assert.True(AccessEvaluator.Evaluate(null, Perms.Public).Allowed);
    }

    [Fact]
    public void NoProfile_ReportsTheCredentialFailure()
    {
        var none = AccessEvaluator.Evaluate(null, OmniTraderPerms.StatusView);
        Assert.Equal(AccessDenyReason.NoCredential, none.Reason);
        Assert.True(none.IsAuthenticationFailure);

        var revoked = AccessEvaluator.Evaluate(null, OmniTraderPerms.StatusView, AccessDenyReason.SessionRevoked);
        Assert.Equal(AccessDenyReason.SessionRevoked, revoked.Reason);
        Assert.True(revoked.IsAuthenticationFailure);
    }

    [Fact]
    public void Grant_AllowsExactlyItsKey()
    {
        var p = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        Assert.True(AccessEvaluator.Evaluate(p, OmniTraderPerms.StatusView, nowUtc: Now).Allowed);
        var denied = AccessEvaluator.Evaluate(p, OmniTraderPerms.OrdersPlace, nowUtc: Now);
        Assert.False(denied.Allowed);
        Assert.Equal(AccessDenyReason.MissingPermission, denied.Reason);
        Assert.False(denied.IsAuthenticationFailure);
        Assert.Same(OmniTraderPerms.OrdersPlace, denied.Permission);
    }

    [Fact]
    public void Rank_NeverGrantsAccess()
    {
        var admin = Profile(ProfileRank.Admin);
        Assert.False(AccessEvaluator.Evaluate(admin, OmniTraderPerms.StatusView, nowUtc: Now).Allowed);
        var guest = Profile(ProfileRank.Guest, OmniTraderPerms.OrdersPlace.Key);
        Assert.True(AccessEvaluator.Evaluate(guest, OmniTraderPerms.OrdersPlace, nowUtc: Now).Allowed);
    }

    [Fact]
    public void Implied_KeysAreHeld()
    {
        var p = Profile(ProfileRank.Guest, KliveCloudPerms.FilesDelete.Key);
        Assert.True(AccessEvaluator.Evaluate(p, KliveCloudPerms.FilesBrowse, nowUtc: Now).Allowed);
        Assert.True(AccessEvaluator.Evaluate(p, KliveCloudPerms.DriveView, nowUtc: Now).Allowed);
        Assert.False(AccessEvaluator.Evaluate(p, KliveCloudPerms.FilesUpload, nowUtc: Now).Allowed);
    }

    [Fact]
    public void Owner_HoldsEverything_IncludingFutureKeys()
    {
        var owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        Assert.True(AccessEvaluator.Evaluate(owner, ProfilesPerms.PermissionsGrant, nowUtc: Now).Allowed);
        var future = KliveToolsPerms.ForTool("Owner Future Tool", "", ProfileRank.Klives);
        Assert.True(AccessEvaluator.Evaluate(owner, future, nowUtc: Now).Allowed);
    }

    [Fact]
    public void Disabled_IsAnAuthenticationFailure_EvenForSignedInRoutes()
    {
        var p = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        p.CanLogin = false;
        var d = AccessEvaluator.Evaluate(p, Perms.SignedIn, nowUtc: Now);
        Assert.Equal(AccessDenyReason.ProfileDisabled, d.Reason);
        Assert.True(d.IsAuthenticationFailure);
    }

    [Fact]
    public void Suspended_BlocksEverythingButSignedInRoutes()
    {
        var p = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        p.SuspendedUntilUtc = Now.AddHours(1);
        var d = AccessEvaluator.Evaluate(p, OmniTraderPerms.StatusView, nowUtc: Now);
        Assert.Equal(AccessDenyReason.Suspended, d.Reason);
        Assert.False(d.IsAuthenticationFailure);
        Assert.True(AccessEvaluator.Evaluate(p, Perms.SignedIn, nowUtc: Now).Allowed);
        // ...and lapses on its own.
        Assert.True(AccessEvaluator.Evaluate(p, OmniTraderPerms.StatusView, nowUtc: Now.AddHours(2)).Allowed);
    }

    [Fact]
    public void ReadOnly_BlocksActTierAndAbove_ButNotReads()
    {
        var p = Profile(ProfileRank.Guest, OmniTraderPerms.BacktestsRun.Key);
        p.ReadOnly = true;
        Assert.True(AccessEvaluator.Evaluate(p, OmniTraderPerms.BacktestsRead, nowUtc: Now).Allowed); // implied Read tier
        var d = AccessEvaluator.Evaluate(p, OmniTraderPerms.BacktestsRun, nowUtc: Now);
        Assert.Equal(AccessDenyReason.ReadOnly, d.Reason);
    }

    [Fact]
    public void TemporaryGrant_ExpiresAtEvaluationTime()
    {
        var p = Profile(ProfileRank.Guest);
        p.Grants.Add(new PermissionGrant { Key = OmniTraderPerms.StatusView.Key, GrantedUtc = Now, ExpiresUtc = Now.AddMinutes(30) });
        Assert.True(AccessEvaluator.Evaluate(p, OmniTraderPerms.StatusView, nowUtc: Now.AddMinutes(29)).Allowed);
        Assert.False(AccessEvaluator.Evaluate(p, OmniTraderPerms.StatusView, nowUtc: Now.AddMinutes(31)).Allowed);
    }
}
