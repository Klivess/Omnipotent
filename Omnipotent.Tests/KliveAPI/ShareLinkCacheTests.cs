using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveCloud;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// Share links and cloud items live in memory and are read straight out of those lists
/// by anonymous route handlers. The DataUtil file-write behind SaveShareLinks bumps only
/// <c>file:{share_links.json}</c> — a key those reads never note — so before this
/// instrumentation a cached <c>/KliveCloud/DownloadShared</c> response depended solely on
/// the content file and therefore survived revocation of the very link that authorised
/// it. These tests pin the read→dep→invalidate chain that closes that.
/// </summary>
public sealed class ShareLinkCacheTests
{
    private static KliveCloud NewCloud()
    {
        // Default OmniService ctor only creates a CancellationTokenSource, so the service
        // can be built without starting its thread. Populate the in-memory lists directly.
        var cloud = new KliveCloud
        {
            ShareLinks = new List<KliveCloud.ShareLink>(),
            CloudItems = new List<CloudItem>()
        };
        return cloud;
    }

    private static byte[] Body() => System.Text.Encoding.UTF8.GetBytes("file-bytes");

    [Fact]
    public void ReadingAShareLink_NotesTheShareLinkDependency()
    {
        var cloud = NewCloud();
        cloud.ShareLinks.Add(new KliveCloud.ShareLink { ShareCode = "abc", ItemID = "item1" });

        var scope = CacheDeps.OpenScope();
        cloud.GetShareLinkByCode("abc");
        CacheDeps.Seal(scope);

        Assert.Contains(KliveCloud.ShareLinksCacheKey, scope.SnapshotReads().Keys);
    }

    [Fact]
    public void ReadingAnItem_NotesTheItemsDependency()
    {
        var cloud = NewCloud();
        cloud.CloudItems.Add(new CloudItem { ItemID = "item1" });

        var scope = CacheDeps.OpenScope();
        cloud.GetItemByID("item1");
        CacheDeps.Seal(scope);

        Assert.Contains(KliveCloud.ItemsCacheKey, scope.SnapshotReads().Keys);
    }

    [Fact]
    public void RevokingAShareLink_InvalidatesACachedShareResponse()
    {
        var cache = new ResponseCache();
        var cloud = NewCloud();
        cloud.ShareLinks.Add(new KliveCloud.ShareLink { ShareCode = "abc", ItemID = "item1" });
        cloud.CloudItems.Add(new CloudItem { ItemID = "item1" });

        // Fill shaped like the real handler: resolve the share, then read the file.
        var scope = CacheDeps.OpenScope();
        cloud.GetShareLinkByCode("abc");
        cloud.GetItemByID("item1");
        CacheDeps.NoteRead("file:C:/cloud/item1.bin");   // what DownloadFile notes
        var rec = new ResponseRecording();
        rec.Record(200, "application/octet-stream", null, Body(), true);
        CacheDeps.Seal(scope);

        string key = ResponseCache.BuildKey("/KliveCloud/DownloadShared", null, null);
        Assert.True(cache.TryStoreFromRecording(key, rec, scope));
        Assert.NotNull(cache.TryGetValid(key));

        // Revocation writes the share-link file; the bump rides on SaveShareLinks.
        CacheDeps.Bump(KliveCloud.ShareLinksCacheKey);

        Assert.Null(cache.TryGetValid(key));
    }

    [Fact]
    public void ContentFileChange_StillInvalidates_SoTheItemsKeyDidNotReplaceIt()
    {
        var cache = new ResponseCache();
        var cloud = NewCloud();
        cloud.ShareLinks.Add(new KliveCloud.ShareLink { ShareCode = "abc", ItemID = "item1" });

        var scope = CacheDeps.OpenScope();
        cloud.GetShareLinkByCode("abc");
        CacheDeps.NoteRead("file:C:/cloud/item1.bin");
        var rec = new ResponseRecording();
        rec.Record(200, "application/octet-stream", null, Body(), true);
        CacheDeps.Seal(scope);

        string key = ResponseCache.BuildKey("/KliveCloud/DownloadShared", null, null);
        Assert.True(cache.TryStoreFromRecording(key, rec, scope));

        CacheDeps.Bump("file:C:/cloud/item1.bin");

        Assert.Null(cache.TryGetValid(key));
    }
}
