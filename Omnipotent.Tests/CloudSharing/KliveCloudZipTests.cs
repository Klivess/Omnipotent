using System.IO.Compression;
using System.Text;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveCloud;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using CloudService = Omnipotent.Services.KliveCloud.KliveCloud;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.CloudSharing;

/// <summary>
/// "Download all" on a shared folder: which entries the archive holds, what they are called,
/// and that the bytes survive being written to a stream that can't seek (the HTTP response).
/// </summary>
public sealed class KliveCloudZipTests : IDisposable
{
    private readonly string profilesDir = Path.Combine(Path.GetTempPath(), "kc-zip-" + Guid.NewGuid().ToString("N"));
    // A private corner of the storage directory (under the test binaries), removed afterwards.
    private readonly string area = "zip-test-" + Guid.NewGuid().ToString("N");
    private readonly string storage = Path.GetFullPath(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory));
    private readonly CloudService cloud = new();
    private readonly KMProfileManager manager = new();
    private readonly KMProfile sharer;
    private readonly CloudService.ShareLink link;

    private static readonly DateTime BeachTaken = new(2026, 7, 14, 9, 30, 0);
    private readonly byte[] beach = RandomBytes(300_000);
    private readonly byte[] cover = RandomBytes(40_000);
    private readonly string notes = string.Concat(Enumerable.Repeat("Swim at nine, lunch at the harbour.\n", 400));

    /// <summary>
    /// trip                (shared: sharer is a Viewer)
    ///   day1/beach.jpg, day1/notes.txt
    ///   empty/
    ///   hidden/secret.txt (doesn't inherit: the sharer can't see it, so the link mustn't serve it)
    ///   cover.png
    ///   missing.txt       (in the metadata, gone from disk)
    /// outside.txt         (not in the share)
    /// </summary>
    public KliveCloudZipTests()
    {
        sharer = Profile(ProfileRank.Manager);
        manager.InitializeForTests(new[] { sharer }, profilesDir);
        cloud.UseProfileManager(manager);

        cloud.SetItemsForTests(new List<CloudItem>
        {
            Folder("trip", null, new CloudAccess { Entries = { new CloudAccessEntry { ProfileId = sharer.UserID, Level = CloudAccessLevel.Viewer } } }),
            Folder("day1", "trip"),
            File("beach.jpg", "day1", beach, BeachTaken),
            File("notes.txt", "day1", Encoding.UTF8.GetBytes(notes)),
            Folder("empty", "trip"),
            Folder("hidden", "trip", new CloudAccess { Inherit = false }),
            File("secret.txt", "hidden", Encoding.UTF8.GetBytes("not for the link")),
            File("cover.png", "trip", cover),
            File("missing.txt", "trip", null),
            File("outside.txt", null, Encoding.UTF8.GetBytes("elsewhere")),
        });
        link = new CloudService.ShareLink { ShareCode = "zip", ItemID = "trip", CreatedByUserID = sharer.UserID };
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(storage, area), true); } catch { }
        try { Directory.Delete(profilesDir, true); } catch { }
    }

    private readonly Dictionary<string, string> paths = new();

    private CloudItem Folder(string id, string? parent, CloudAccess? access = null)
    {
        string relative = parent == null ? Path.Combine(area, id) : Path.Combine(paths[parent], id);
        paths[id] = relative;
        Directory.CreateDirectory(Path.Combine(storage, relative));
        return new CloudItem
        {
            ItemID = id, Name = id, RelativePath = relative, ParentFolderID = parent ?? "",
            ItemType = CloudItem.CloudItemType.Folder, Access = access ?? new CloudAccess(),
        };
    }

    /// <summary>A file item; null content leaves it out of the storage directory.</summary>
    private CloudItem File(string id, string? parent, byte[]? content, DateTime modified = default)
    {
        string relative = parent == null ? Path.Combine(area, id) : Path.Combine(paths[parent], id);
        paths[id] = relative;
        if (content != null)
        {
            string full = Path.Combine(storage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, content);
        }
        return new CloudItem
        {
            ItemID = id, Name = id, RelativePath = relative, ParentFolderID = parent ?? "",
            ItemType = CloudItem.CloudItemType.File, FileSizeBytes = content?.Length ?? 0,
            ModifiedDate = modified, Access = new CloudAccess(),
        };
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private CloudItem Get(string id) => cloud.GetItemByID(id);

    private static List<string> Names(IEnumerable<CloudService.ZipEntrySource> plan) => plan.Select(e => e.EntryName).ToList();

    [Fact]
    public void WholeFolder_HoldsWhatTheLinkServes_FoldersFirst_NamedBelowTheFolder()
    {
        var plan = cloud.PlanShareZip(link, Get("trip"));

        Assert.Equal(new[] { "day1/", "day1/beach.jpg", "day1/notes.txt", "empty/", "cover.png" }, Names(plan));
    }

    [Fact]
    public void Selection_ItemInsideASelectedFolder_IsOnlyAddedOnce()
    {
        var plan = cloud.PlanShareZip(link, Get("trip"), new[] { Get("day1"), Get("beach.jpg"), Get("cover.png") });

        Assert.Equal(new[] { "day1/", "day1/beach.jpg", "day1/notes.txt", "cover.png" }, Names(plan));
    }

    [Fact]
    public void Subfolder_NamesAreRelativeToIt()
    {
        var plan = cloud.PlanShareZip(link, Get("day1"));

        Assert.Equal(new[] { "beach.jpg", "notes.txt" }, Names(plan));
    }

    [Fact]
    public void Selection_NeverReachesOutsideTheShare()
    {
        var plan = cloud.PlanShareZip(link, Get("trip"), new[] { Get("outside.txt"), Get("secret.txt"), Get("cover.png") });

        Assert.Equal(new[] { "cover.png" }, Names(plan));
    }

    [Fact]
    public async Task Archive_WrittenToANonSeekableStream_ReadsBackIntact()
    {
        var plan = cloud.PlanShareZip(link, Get("trip"));
        var response = new ResponseLikeStream();

        await CloudService.WriteZipAsync(response, plan);

        using var zip = new ZipArchive(new MemoryStream(response.Written.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(Names(plan), zip.Entries.Select(e => e.FullName).ToList());
        Assert.Equal(beach, ReadAll(zip.GetEntry("day1/beach.jpg")!));
        Assert.Equal(cover, ReadAll(zip.GetEntry("cover.png")!));
        Assert.Equal(notes, Encoding.UTF8.GetString(ReadAll(zip.GetEntry("day1/notes.txt")!)));
        Assert.Equal(0, zip.GetEntry("empty/")!.Length);

        // Photos are stored as they are; text is deflated.
        var photo = zip.GetEntry("day1/beach.jpg")!;
        Assert.Equal(photo.Length, photo.CompressedLength);
        var text = zip.GetEntry("day1/notes.txt")!;
        Assert.True(text.CompressedLength < text.Length / 4, $"notes.txt: {text.CompressedLength} of {text.Length} bytes");

        // The photo keeps its date; an item without one gets the earliest date a ZIP can hold.
        Assert.Equal(BeachTaken, photo.LastWriteTime.DateTime);
        Assert.Equal(1980, zip.GetEntry("cover.png")!.LastWriteTime.Year);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Write-only and unseekable, with no Position: what an HttpListener response stream offers.</summary>
    private sealed class ResponseLikeStream : Stream
    {
        public readonly MemoryStream Written = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
