using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Permissions.Legacy;
using Omnipotent.Services.KliveCloud;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.CloudSharing;

public sealed class KliveCloudAccessTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "kc-access-" + Guid.NewGuid().ToString("N"));
    private readonly Omnipotent.Services.KliveCloud.KliveCloud cloud = new();
    private readonly KMProfileManager manager = new();
    private readonly KMProfile owner, philip, oliver, guest;

    public KliveCloudAccessTests()
    {
        owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        philip = Profile(ProfileRank.Manager);
        oliver = Profile(ProfileRank.Admin);
        guest = Profile(ProfileRank.Guest);
        manager.InitializeForTests(new[] { owner, philip, oliver, guest }, dir);
        cloud.UseProfileManager(manager);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private static CloudItem Item(string id, string? parent, CloudItem.CloudItemType type = CloudItem.CloudItemType.Folder, CloudAccess? access = null) => new()
    {
        ItemID = id,
        Name = id,
        RelativePath = id,
        ParentFolderID = parent ?? "",
        ItemType = type,
        Access = access ?? new CloudAccess(),
    };

    private static CloudAccessEntry Entry(KMProfile p, CloudAccessLevel level) => new() { ProfileId = p.UserID, Level = level };

    /// <summary>
    /// shared (everyone: Viewer)
    ///   team (philip: Editor)
    ///     secret (does not inherit; oliver: Viewer)
    ///   readme.txt
    /// private (nobody)
    ///   gift.txt (guest: Viewer)
    /// </summary>
    private void Tree()
    {
        cloud.SetItemsForTests(new List<CloudItem>
        {
            Item("shared", null, access: new CloudAccess { Everyone = CloudAccessLevel.Viewer }),
            Item("team", "shared", access: new CloudAccess { Entries = { Entry(philip, CloudAccessLevel.Editor) } }),
            Item("secret", "team", access: new CloudAccess { Inherit = false, Entries = { Entry(oliver, CloudAccessLevel.Viewer) } }),
            Item("readme.txt", "shared", CloudItem.CloudItemType.File),
            Item("private", null),
            Item("gift.txt", "private", CloudItem.CloudItemType.File, new CloudAccess { Entries = { Entry(guest, CloudAccessLevel.Viewer) } }),
        });
    }

    private CloudAccessLevel Level(string id, KMProfile p, bool all = false) => cloud.EffectiveLevel(cloud.GetItemByID(id), p, all);

    [Fact]
    public void Lists_InheritDownTheTree_UntilAnItemStopsInheriting()
    {
        Tree();
        Assert.Equal(CloudAccessLevel.Viewer, Level("shared", philip));
        Assert.Equal(CloudAccessLevel.Editor, Level("team", philip));
        Assert.Equal(CloudAccessLevel.None, Level("secret", philip));     // stops inheriting
        Assert.Equal(CloudAccessLevel.Viewer, Level("secret", oliver));
        Assert.Equal(CloudAccessLevel.Viewer, Level("readme.txt", guest)); // everyone, inherited
        Assert.Equal(CloudAccessLevel.None, Level("private", philip));
    }

    [Fact]
    public void Owner_AndFilesAll_SeeEverything()
    {
        Tree();
        Assert.Equal(CloudAccessLevel.Editor, Level("secret", owner));
        Assert.Equal(CloudAccessLevel.Editor, Level("private", guest, all: true));
    }

    [Fact]
    public void SharedWithMe_ListsItemsWhoseFolderIsHidden()
    {
        Tree();
        var shared = cloud.GetSharedWithMe(guest, false).Select(s => s.Item.ItemID).ToList();
        Assert.Equal(new[] { "gift.txt" }, shared);
        Assert.DoesNotContain(cloud.GetVisibleChildren(null, guest, false), c => c.Item.ItemID == "private");
        // ...and the path shown never names the hidden folder above it.
        Assert.Equal(new[] { "gift.txt" }, cloud.VisibleAncestry(cloud.GetItemByID("gift.txt"), guest, false).Select(i => i.ItemID));
    }

    [Fact]
    public void Folders_CantBeDeletedOrSharedOverItemsYouCantEdit()
    {
        Tree();
        var blocker = cloud.FirstNonEditableDescendant(cloud.GetItemByID("team"), philip, false);
        Assert.Equal("secret", blocker?.ItemID);
        Assert.Null(cloud.FirstNonEditableDescendant(cloud.GetItemByID("team"), owner, false));
    }

    [Fact]
    public void ShareLinks_OnlyServeWhatTheirCreatorCanStillSee()
    {
        Tree();
        var link = new Omnipotent.Services.KliveCloud.KliveCloud.ShareLink { ShareCode = "c1", ItemID = "shared", CreatedByUserID = philip.UserID };
        Assert.True(cloud.LinkCanServe(link, cloud.GetItemByID("team")));
        Assert.True(cloud.LinkCanServe(link, cloud.GetItemByID("readme.txt")));
        Assert.False(cloud.LinkCanServe(link, cloud.GetItemByID("secret")));   // the old hole: hidden children leaked
        Assert.False(cloud.LinkCanServe(link, cloud.GetItemByID("gift.txt"))); // outside the share

        var orphan = new Omnipotent.Services.KliveCloud.KliveCloud.ShareLink { ShareCode = "c2", ItemID = "shared", CreatedByUserID = "deleted-profile" };
        Assert.False(cloud.LinkCanServe(orphan, cloud.GetItemByID("shared")));
    }

    [Fact]
    public void Migration_PreservesExactlyWhoCouldSeeEachItem()
    {
        var items = new List<CloudItem>
        {
            Item("root", null), Item("adminOnly", "root"), Item("insideAdmin", "adminOnly"), Item("managers", "root"),
        };
        items[0].LegacyMinimumPermissionLevel = LegacyRank.Guest;
        items[1].LegacyMinimumPermissionLevel = LegacyRank.Admin;
        items[2].LegacyMinimumPermissionLevel = LegacyRank.Anybody; // floored by its parent: effectively Admin
        items[3].LegacyMinimumPermissionLevel = LegacyRank.Manager;
        var byId = items.ToDictionary(i => i.ItemID);
        var oldFloor = new Dictionary<string, int> { ["root"] = 1, ["adminOnly"] = 4, ["insideAdmin"] = 4, ["managers"] = 2 };

        Omnipotent.Services.KliveCloud.KliveCloud.ApplyLegacyAccessMigration(items, manager.Profiles, id => byId.GetValueOrDefault(id), DateTime.UtcNow);
        cloud.SetItemsForTests(items);

        foreach (var p in new[] { philip, oliver, guest })
        {
            foreach (var item in items)
            {
                bool before = (int)p.Rank >= oldFloor[item.ItemID];
                bool after = cloud.CanView(item, p);
                Assert.True(before == after, $"{p.Rank} on {item.ItemID}: before {before}, after {after}");
            }
        }
        Assert.Equal(CloudAccessLevel.Editor, items[0].Access.Everyone);
        Assert.False(items[1].Access.Inherit);  // narrower than its parent: its own list
        Assert.True(items[2].Access.Inherit);   // same audience as its parent: just inherits
        Assert.All(items, i => Assert.Null(i.LegacyMinimumPermissionLevel));
    }
}
