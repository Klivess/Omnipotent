using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;

namespace Omnipotent.Tests.Profiles;

/// <summary>Who may manage whom ("who is above whom") and which keys they may hand out.</summary>
public sealed class DelegationTests
{
    [Fact]
    public void Managers_OnlyManageProfilesRankedBelowThem()
    {
        var admin = Profile(ProfileRank.Admin);
        var associate = Profile(ProfileRank.Associate);
        var otherAdmin = Profile(ProfileRank.Admin);
        var owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;

        Assert.True(KMProfileManager.CanManage(admin, associate));
        Assert.False(KMProfileManager.CanManage(associate, admin));
        Assert.False(KMProfileManager.CanManage(admin, otherAdmin)); // equal rank is not "below"
        Assert.False(KMProfileManager.CanManage(admin, admin));      // never yourself
        Assert.False(KMProfileManager.CanManage(admin, owner));
        Assert.True(KMProfileManager.CanManage(owner, admin));
        Assert.False(KMProfileManager.CanManage(owner, owner));
    }

    [Fact]
    public void AssignableRanks_ClosesTheOldEscalations()
    {
        // The old CreateProfile allowed rank <= actor + 1 (an Admin could mint a Klives profile),
        // and ChangeProfileRank only checked the target's *current* rank.
        var admin = Profile(ProfileRank.Admin);
        var ranks = KMProfileManager.AssignableRanks(admin);
        Assert.DoesNotContain(ProfileRank.Klives, ranks);
        Assert.DoesNotContain(ProfileRank.Admin, ranks);
        Assert.Contains(ProfileRank.Associate, ranks);

        var owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        Assert.DoesNotContain(ProfileRank.Klives, KMProfileManager.AssignableRanks(owner)); // owner rank is unique
        Assert.Contains(ProfileRank.Admin, KMProfileManager.AssignableRanks(owner));
    }

    [Fact]
    public void Delegation_OnlyOwnKeys_NeverOwnerOnlyKeys()
    {
        var now = DateTime.UtcNow;
        var manager = Profile(ProfileRank.Admin, ProfilesPerms.PermissionsGrant.Key, OmniTraderPerms.StatusView.Key, ProfilesPerms.AccessControl.Key);
        Assert.Null(KMProfileManager.CannotDelegate(manager, OmniTraderPerms.StatusView.Key, now));
        Assert.NotNull(KMProfileManager.CannotDelegate(manager, OmniTraderPerms.OrdersPlace.Key, now));      // doesn't hold it
        Assert.NotNull(KMProfileManager.CannotDelegate(manager, ProfilesPerms.AccessControl.Key, now));      // owner-only, even though held
        Assert.NotNull(KMProfileManager.CannotDelegate(manager, ProfilesPerms.PermissionsGrant.Key, now));

        var owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        Assert.Null(KMProfileManager.CannotDelegate(owner, ProfilesPerms.PermissionsGrant.Key, now));
    }
}
