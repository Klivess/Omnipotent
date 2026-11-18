using System.Net;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveCloud;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using CloudService = Omnipotent.Services.KliveCloud.KliveCloud;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.CloudSharing;

/// <summary>
/// What the rebuilt KliveCloud page leans on, each pinned to the live failure behind it (Nov 2026):
/// downloads that couldn't resume and were logged as 500s when the visitor cancelled, share links
/// that couldn't open but were listed as working, tickets that let a browser element open a private
/// file, renaming, and the thumbnail memory.
/// </summary>
public sealed class KliveCloudConsoleBackendTests : IDisposable
{
    private readonly string profilesDir = Path.Combine(Path.GetTempPath(), "kc-console-" + Guid.NewGuid().ToString("N"));
    private readonly string area = "console-test-" + Guid.NewGuid().ToString("N");
    private readonly string storage = Path.GetFullPath(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory));
    private readonly CloudService cloud = new();
    private readonly KMProfileManager manager = new();
    private readonly KMProfile owner, editor, viewer;
    private readonly List<(string Path, string Json)> saves = new();

    private static readonly DateTime Written = new(2026, 11, 8, 2, 17, 53, 412, DateTimeKind.Utc);

    public KliveCloudConsoleBackendTests()
    {
        owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        editor = Profile(ProfileRank.Manager, "klivecloud.files.download", "klivecloud.sharing.manage");
        viewer = Profile(ProfileRank.Guest, "klivecloud.files.download");
        manager.InitializeForTests(new[] { owner, editor, viewer }, profilesDir);
        cloud.UseProfileManager(manager);
        cloud.PersistForTests = (path, json) => { lock (saves) saves.Add((path, json)); return Task.CompletedTask; };
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(storage, area), true); } catch { }
        try { Directory.Delete(profilesDir, true); } catch { }
    }

    // ───────────────────────────── ranges and resuming ─────────────────────────────

    private static readonly string Tag = KliveCloudTransfer.StrongETag(1_000, Written);

    private static KliveCloudTransfer.RangePlan Plan(string? range, string? ifRange = null, long length = 1_000)
        => KliveCloudTransfer.PlanRange(length, range, ifRange, Tag, Written);

    [Fact]
    public void Range_NoneOrForeign_SendsTheWholeFile()
    {
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan(null).Kind);
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan("items=0-10").Kind);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=900-", 900, 999)]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=-5000", 0, 999)]   // a suffix longer than the file is the whole file
    [InlineData("bytes=990-5000", 990, 999)] // past the end is cut to the end (RFC 9110), not refused
    public void Range_Satisfiable_PlansThoseBytes(string range, long start, long end)
    {
        var plan = Plan(range);
        Assert.Equal(KliveCloudTransfer.RangeKind.Partial, plan.Kind);
        Assert.Equal(start, plan.Start);
        Assert.Equal(end, plan.End);
        Assert.Equal(end - start + 1, plan.Length);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=50-10")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("bytes=-0")]
    public void Range_Unsatisfiable_Is416(string range)
        => Assert.Equal(KliveCloudTransfer.RangeKind.Unsatisfiable, Plan(range).Kind);

    [Fact]
    public void IfRange_ResumesOnlyTheSameVersion()
    {
        // The browser resumes with the validator it was given: same file → the rest of it.
        Assert.Equal(KliveCloudTransfer.RangeKind.Partial, Plan("bytes=500-", Tag).Kind);
        Assert.Equal(KliveCloudTransfer.RangeKind.Partial, Plan("bytes=500-", KliveCloudTransfer.HttpDate(Written)).Kind);

        // Anything else → the whole file again, never a splice of two versions.
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan("bytes=500-", KliveCloudTransfer.StrongETag(1_001, Written)).Kind);
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan("bytes=500-", "W/" + Tag).Kind);
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan("bytes=500-", KliveCloudTransfer.HttpDate(Written.AddMinutes(-1))).Kind);
        Assert.Equal(KliveCloudTransfer.RangeKind.Full, Plan("bytes=500-", "not a validator").Kind);
    }

    [Fact]
    public void ETag_ChangesWithSizeAndWriteTime()
    {
        Assert.NotEqual(Tag, KliveCloudTransfer.StrongETag(1_000, Written.AddTicks(1)));
        Assert.NotEqual(Tag, KliveCloudTransfer.StrongETag(999, Written));
        Assert.StartsWith("\"", Tag);
        Assert.Equal("Sun, 08 Nov 2026 02:17:53 GMT", KliveCloudTransfer.HttpDate(Written));
    }

    // ───────────────────────────── a client hanging up ─────────────────────────────

    /// <summary>A response stream whose connection drops after <paramref name="allow"/> bytes, as when a visitor cancels a download.</summary>
    private sealed class DroppingStream(long allow, Exception failure) : Stream
    {
        public long Received;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Received;
        public override long Position { get => Received; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Received + buffer.Length > allow) throw failure;
            Received += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSource : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The device is not ready.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static IEnumerable<object[]> HangUps() => new[]
    {
        new object[] { new HttpListenerException(1229, "An operation was attempted on a nonexistent network connection") },
        new object[] { new IOException("Unable to write data to the transport connection") },
        new object[] { new ObjectDisposedException("HttpListenerResponse") },
        new object[] { new InvalidOperationException("wrapped", new HttpListenerException(64)) },
    };

    [Theory]
    [MemberData(nameof(HangUps))]
    public async Task Pump_ClientHangingUp_EndsQuietly_WithWhatWasSent(Exception failure)
    {
        // 8 Nov: five cancelled downloads of one 241 MB file were each recorded as a server 500.
        var source = new MemoryStream(new byte[3 * KliveCloudTransfer.BufferBytes]);
        var output = new DroppingStream(allow: 2 * KliveCloudTransfer.BufferBytes, failure);

        var result = await KliveCloudTransfer.PumpAsync(source, output, -1);

        Assert.Equal(KliveCloudTransfer.PumpOutcome.ClientGone, result.Outcome);
        Assert.Equal(2 * KliveCloudTransfer.BufferBytes, result.BytesSent);
        Assert.True(KliveCloudTransfer.IsClientGone(failure));
    }

    [Fact]
    public async Task Pump_ReadFailure_IsTheServersFault_NotTheClients()
    {
        var result = await KliveCloudTransfer.PumpAsync(new FailingSource(), new MemoryStream(), -1);
        Assert.Equal(KliveCloudTransfer.PumpOutcome.SourceFailed, result.Outcome);
        Assert.IsType<IOException>(result.Error);
        Assert.False(KliveCloudTransfer.IsClientGone(new InvalidOperationException("a bug")));
    }

    [Fact]
    public async Task Pump_SendsExactlyTheRangeAsked()
    {
        var bytes = Enumerable.Range(0, 1_000_000).Select(i => (byte)(i % 251)).ToArray();
        var source = new MemoryStream(bytes);
        source.Seek(123_456, SeekOrigin.Begin);
        var output = new MemoryStream();

        var result = await KliveCloudTransfer.PumpAsync(source, output, 600_000);

        Assert.Equal(KliveCloudTransfer.PumpOutcome.Completed, result.Outcome);
        Assert.Equal(600_000, result.BytesSent);
        Assert.Equal(bytes.Skip(123_456).Take(600_000), output.ToArray());
    }

    [Fact]
    public void Disposition_KeepsNonAsciiNames_AndNeverBreaksTheHeader()
    {
        string header = KliveCloudTransfer.Disposition("Ünïcode \"quote\"\\name.pdf", inline: true);
        Assert.StartsWith("inline; filename=\"", header);
        Assert.DoesNotContain("\\", header.Split(';')[1]);
        Assert.Contains("filename*=UTF-8''%C3%9Cn%C3%AFcode%20%22quote%22%5Cname.pdf", header);
        Assert.StartsWith("attachment;", KliveCloudTransfer.Disposition("a.bin", inline: false));
    }

    [Fact]
    public async Task CountingStream_CountsWhatWentOut()
    {
        var inner = new MemoryStream();
        var counted = new KliveCloudTransfer.CountingStream(inner);
        await counted.WriteAsync(new byte[1234]);
        counted.Write(new byte[66], 0, 66);
        Assert.Equal(1300, counted.BytesWritten);
        Assert.Equal(1300, inner.Length);
    }

    // ───────────────────────────── share links ─────────────────────────────

    private static CloudItem Item(string id, string? parent, CloudItem.CloudItemType type = CloudItem.CloudItemType.File, CloudAccess? access = null) => new()
    {
        ItemID = id,
        Name = id,
        RelativePath = parent == null ? id : $"{parent}/{id}",
        ParentFolderID = parent ?? "",
        ItemType = type,
        FileSizeBytes = type == CloudItem.CloudItemType.File ? 1234 : 0,
        Access = access ?? new CloudAccess(),
    };

    private static CloudService.ShareLink Link(string code, string itemId, string? creator, DateTime? expires = null) => new()
    {
        ShareCode = code,
        ItemID = itemId,
        CreatedByUserID = creator!,
        CreatedDate = DateTime.Now.AddDays(-30),
        ExpirationDate = expires,
    };

    /// <summary>
    /// public/ (everyone: Viewer)   notes.txt
    /// private/ (editor: Editor)    plan.txt
    /// </summary>
    private void Drive()
    {
        cloud.SetItemsForTests(new List<CloudItem>
        {
            Item("public", null, CloudItem.CloudItemType.Folder, new CloudAccess { Everyone = CloudAccessLevel.Viewer }),
            Item("notes.txt", "public"),
            Item("private", null, CloudItem.CloudItemType.Folder, new CloudAccess { Entries = { new CloudAccessEntry { ProfileId = editor.UserID, Level = CloudAccessLevel.Editor } } }),
            Item("plan.txt", "private"),
        });
    }

    [Fact]
    public void Diagnose_NamesWhyALinkCantOpen()
    {
        Drive();
        var now = DateTime.Now;
        Assert.Equal(CloudService.ShareLinkProblem.None, cloud.DiagnoseShareLink(Link("ok", "notes.txt", owner.UserID), now));
        // Eight live links (19–22 Sep 2026) were created with no creator: they 404ed while listed as working.
        Assert.Equal(CloudService.ShareLinkProblem.NoOwner, cloud.DiagnoseShareLink(Link("anon", "notes.txt", ""), now));
        Assert.Equal(CloudService.ShareLinkProblem.NoOwner, cloud.DiagnoseShareLink(Link("gone", "notes.txt", "deleted-profile"), now));
        Assert.Equal(CloudService.ShareLinkProblem.ItemDeleted, cloud.DiagnoseShareLink(Link("dead", "no-such-item", owner.UserID), now));
        Assert.Equal(CloudService.ShareLinkProblem.Expired, cloud.DiagnoseShareLink(Link("old", "notes.txt", owner.UserID, now.AddHours(-1)), now));
        // The viewer can't see the private folder, so a link they made to it can't serve it.
        Assert.Equal(CloudService.ShareLinkProblem.OwnerLostAccess, cloud.DiagnoseShareLink(Link("lost", "plan.txt", viewer.UserID), now));
    }

    [Fact]
    public async Task Adopting_ALinkWithNoOwner_MakesItOpen_AtTheSameAddress()
    {
        Drive();
        cloud.ShareLinks = new List<CloudService.ShareLink> { Link("anon", "notes.txt", "") };
        Assert.False(cloud.LinkCanServe(cloud.ShareLinks[0], cloud.GetItemByID("notes.txt")));

        var adopted = await cloud.UpdateShareLink("anon", mode: null, expiry: null, clearExpiry: false, newOwnerId: owner.UserID);

        Assert.NotNull(adopted);
        Assert.Equal("anon", adopted!.ShareCode);
        Assert.Equal(owner.UserID, cloud.GetShareLinkByCode("anon").CreatedByUserID);
        Assert.Equal(CloudService.ShareLinkProblem.None, cloud.DiagnoseShareLink(cloud.GetShareLinkByCode("anon"), DateTime.Now));
        Assert.Single(saves);
    }

    [Fact]
    public async Task UpdateShareLink_ChangesOnlyWhatItIsGiven()
    {
        Drive();
        var expires = DateTime.Now.AddDays(3);
        cloud.ShareLinks = new List<CloudService.ShareLink> { Link("f", "public", owner.UserID, expires) };

        await cloud.UpdateShareLink("f", CloudService.SharePermissionMode.Write, null, false, null);
        var link = cloud.GetShareLinkByCode("f");
        Assert.Equal(CloudService.SharePermissionMode.Write, link.PermissionMode);
        Assert.Equal(expires, link.ExpirationDate);
        Assert.Equal(owner.UserID, link.CreatedByUserID);

        var later = DateTime.Now.AddDays(30);
        await cloud.UpdateShareLink("f", null, later, false, null);
        Assert.Equal(later, cloud.GetShareLinkByCode("f").ExpirationDate);
        Assert.Equal(CloudService.SharePermissionMode.Write, cloud.GetShareLinkByCode("f").PermissionMode);

        await cloud.UpdateShareLink("f", null, null, clearExpiry: true, null);
        Assert.Null(cloud.GetShareLinkByCode("f").ExpirationDate);

        Assert.Null(await cloud.UpdateShareLink("missing", null, null, false, null));
    }

    [Fact]
    public async Task CreateShareLink_RefusesALinkNobodyOwns()
    {
        Drive();
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.CreateShareLink("notes.txt", "", null));
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.CreateShareLink("notes.txt", "  ", null));
        var made = await cloud.CreateShareLink("notes.txt", owner.UserID, null);
        Assert.Equal(CloudService.ShareLinkProblem.None, cloud.DiagnoseShareLink(made, DateTime.Now));
    }

    // ───────────────────────────── usage ─────────────────────────────

    [Fact]
    public void Usage_CountsAVisitorOncePerWindow_AndDownloadsAndBytes()
    {
        var stats = new KliveCloudShareStats(null);
        var t = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);

        stats.NoteOpen("abc", "1.2.3.4|Firefox", t);
        stats.NoteOpen("abc", "1.2.3.4|Firefox", t.AddMinutes(5));     // the tab regaining focus re-reads it
        stats.NoteOpen("abc", "5.6.7.8|Chrome", t.AddMinutes(6));
        stats.NoteOpen("abc", "1.2.3.4|Firefox", t.AddMinutes(40));    // a new visit
        stats.NoteDownload("abc", t.AddMinutes(41));
        stats.NoteBytes("abc", 241_821_414);
        stats.NoteBytes("abc", 0);

        var usage = stats.Get("abc")!;
        Assert.Equal(3, usage.Opens);
        Assert.Equal(1, usage.Downloads);
        Assert.Equal(241_821_414, usage.BytesServed);
        Assert.Equal(t, usage.FirstOpenedUtc);
        Assert.Equal(t.AddMinutes(40), usage.LastOpenedUtc);
        Assert.Null(stats.Get("never-opened"));

        stats.Forget("abc");
        Assert.Null(stats.Get("abc"));
    }

    [Fact]
    public void Usage_SurvivesARestart_AndKeepsNoAddresses()
    {
        string path = Path.Combine(profilesDir, "share_link_stats.json");
        Directory.CreateDirectory(profilesDir);
        var stats = new KliveCloudShareStats(path);
        stats.NoteOpen("abc", "203.0.113.9|Agent", DateTime.UtcNow);
        stats.NoteBytes("abc", 42);
        Assert.True(stats.FlushIfDirty());
        Assert.False(stats.FlushIfDirty()); // nothing new

        string written = File.ReadAllText(path);
        Assert.DoesNotContain("203.0.113.9", written);

        var reloaded = new KliveCloudShareStats(path);
        reloaded.Load();
        Assert.Equal(1, reloaded.Get("abc")!.Opens);
        Assert.Equal(42, reloaded.Get("abc")!.BytesServed);
    }

    // ───────────────────────────── tickets ─────────────────────────────

    [Fact]
    public void Tickets_AreUnguessable_ItemBound_AndExpire()
    {
        var tickets = new KliveCloudTickets();
        var now = DateTime.UtcNow;
        var a = tickets.Issue("item-1", owner.UserID, now);
        var b = tickets.Issue("item-1", owner.UserID, now);

        Assert.NotEqual(a.Token, b.Token);
        Assert.True(a.Token.Length >= 40);
        Assert.DoesNotContain('+', a.Token);
        Assert.DoesNotContain('/', a.Token);
        Assert.Equal("item-1", tickets.Resolve(a.Token, now.AddMinutes(30))!.ItemId);
        Assert.Null(tickets.Resolve(a.Token, now + KliveCloudTickets.Lifetime));
        Assert.Null(tickets.Resolve("guess", now));
        Assert.Null(tickets.Resolve(null, now));
        Assert.Throws<ArgumentException>(() => tickets.Issue("item-1", "", now));
    }

    [Fact]
    public void Tickets_StayBounded()
    {
        var tickets = new KliveCloudTickets();
        var now = DateTime.UtcNow;
        for (int i = 0; i < KliveCloudTickets.MaxLive + 50; i++) tickets.Issue("item", owner.UserID, now.AddMilliseconds(i));
        Assert.True(tickets.Count <= KliveCloudTickets.MaxLive + 1);
    }

    [Fact]
    public void InlineTypes_NeverIncludeAnythingThatRunsAsAPage()
    {
        CloudItem File(string name) => new() { ItemID = name, Name = name, ItemType = CloudItem.CloudItemType.File };
        Assert.Equal("image/jpeg", cloud.InlineMimeType(File("a.JPG")));
        Assert.Equal("video/mp4", cloud.InlineMimeType(File("a.mp4")));
        Assert.Equal("application/pdf", cloud.InlineMimeType(File("a.pdf")));
        // An uploaded web page or script opens as text, never as HTML.
        Assert.Equal("text/plain; charset=utf-8", cloud.InlineMimeType(File("page.html")));
        Assert.Equal("text/plain; charset=utf-8", cloud.InlineMimeType(File("x.js")));
        Assert.Null(cloud.InlineMimeType(File("setup.exe")));
        Assert.Null(cloud.InlineMimeType(File("archive.zip")));
        Assert.Null(cloud.InlineMimeType(new CloudItem { ItemID = "f", Name = "folder", ItemType = CloudItem.CloudItemType.Folder }));
    }

    // ───────────────────────────── renaming ─────────────────────────────

    /// <summary>The test's own corner of the storage directory, as a top-level folder of the drive.</summary>
    private void Corner()
    {
        Directory.CreateDirectory(Path.Combine(storage, area));
        cloud.SetItemsForTests(new List<CloudItem>
        {
            new() { ItemID = area, Name = area, RelativePath = area, ParentFolderID = "", ItemType = CloudItem.CloudItemType.Folder, Access = new CloudAccess() },
        });
    }

    private CloudItem OnDisk(string id, string name, string? parentId, CloudItem.CloudItemType type, byte[]? bytes = null)
    {
        parentId ??= area;
        string parentPath = cloud.GetItemByID(parentId).RelativePath;
        string relative = Path.Combine(parentPath, name);
        string full = Path.Combine(storage, relative);
        if (type == CloudItem.CloudItemType.Folder) Directory.CreateDirectory(full);
        else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); System.IO.File.WriteAllBytes(full, bytes ?? new byte[] { 1, 2, 3 }); }
        var item = new CloudItem
        {
            ItemID = id, Name = name, RelativePath = relative, ParentFolderID = parentId, ItemType = type,
            FileSizeBytes = bytes?.Length ?? 3, Access = new CloudAccess(),
        };
        cloud.SetItemsForTests(cloud.CloudItems.Append(item).ToList());
        return item;
    }

    // ───────────────────────────── thumbnails of files that are gone ─────────────────────────────

    /// <summary>
    /// 13 Nov 2026: a delete and two moves while the page listed the old folder left 6 thumbnail
    /// requests in one second answered 500 in 0.3 ms (the source was simply not on disk). That is
    /// "missing", which the route now answers 404, not a failure to make a thumbnail.
    /// </summary>
    [Fact]
    public async Task Preview_OfAFileNoLongerOnDisk_IsMissing_NotAFailure()
    {
        Corner();
        var photo = OnDisk("gone", "screenshot.png", null, CloudItem.CloudItemType.File);
        System.IO.File.Delete(Path.Combine(storage, photo.RelativePath));

        var preview = await cloud.GetPreviewAsync(photo, 320, 320);

        Assert.Null(preview.Data);
        Assert.Equal(CloudService.PreviewSource.Missing, preview.Source);
    }

    [Fact]
    public async Task Preview_OfAFileWithNoPicture_IsUnsupported_WithoutQueueing()
    {
        Corner();
        var notes = OnDisk("notes", "notes.txt", null, CloudItem.CloudItemType.File);

        var preview = await cloud.GetPreviewAsync(notes, 320, 320);

        Assert.Null(preview.Data);
        Assert.Equal(CloudService.PreviewSource.Unsupported, preview.Source);
    }

    [Fact]
    public async Task Rename_MovesTheFolderOnDisk_AndEveryPathInside()
    {
        Corner();
        OnDisk("trip", "Trip", null, CloudItem.CloudItemType.Folder);
        OnDisk("day1", "Day 1", "trip", CloudItem.CloudItemType.Folder);
        OnDisk("photo", "beach.jpg", "day1", CloudItem.CloudItemType.File, new byte[] { 9, 9, 9, 9 });

        var renamed = await cloud.RenameItem("trip", "Summer 2026", "test");

        Assert.NotNull(renamed);
        Assert.Equal("Summer 2026", cloud.GetItemByID("trip").Name);
        string photo = cloud.GetItemByID("photo").RelativePath;
        Assert.Equal(Path.Combine(area, "Summer 2026", "Day 1", "beach.jpg"), photo);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, System.IO.File.ReadAllBytes(Path.Combine(storage, photo)));
        Assert.False(Directory.Exists(Path.Combine(storage, area, "Trip")));
        Assert.Single(saves);
    }

    [Fact]
    public async Task Rename_ChangingOnlyTheCase_Works_OnWindowsToo()
    {
        Corner();
        OnDisk("doc", "report.pdf", null, CloudItem.CloudItemType.File);

        await cloud.RenameItem("doc", "Report.pdf", "test");

        Assert.Equal("Report.pdf", cloud.GetItemByID("doc").Name);
        string[] names = Directory.GetFiles(Path.Combine(storage, area)).Select(Path.GetFileName).ToArray()!;
        Assert.Equal(new[] { "Report.pdf" }, names);
    }

    [Fact]
    public async Task Rename_RefusesATakenName_AndBadNames()
    {
        Corner();
        OnDisk("a", "a.txt", null, CloudItem.CloudItemType.File);
        OnDisk("b", "b.txt", null, CloudItem.CloudItemType.File);

        await Assert.ThrowsAsync<IOException>(() => cloud.RenameItem("b", "A.TXT", "test"));
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.RenameItem("b", "../escape.txt", "test"));
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.RenameItem("b", "   ", "test"));
        Assert.Null(await cloud.RenameItem("nope", "c.txt", "test"));
        Assert.Equal("b.txt", cloud.GetItemByID("b").Name);
        Assert.Empty(saves);
    }

    // ───────────────────────────── thumbnails ─────────────────────────────

    [Fact]
    public void ThumbnailMemory_ServesTheSameVersion_AndStaysWithinItsBudget()
    {
        var memory = new CloudService.ThumbnailMemory(maxBytes: 10_000);
        memory.Put("a_300x300", 1234, new byte[1_000]);
        Assert.NotNull(memory.Get("a_300x300", 1234));
        Assert.Null(memory.Get("a_300x300", 999)); // a different version of the file
        Assert.Null(memory.Get("a_300x300", 1234)); // ...which also dropped the stale one

        for (int i = 0; i < 30; i++) memory.Put($"f{i}_300x300", 1, new byte[1_000]);
        Assert.True(memory.Bytes <= 10_000);
        Assert.NotNull(memory.Get("f29_300x300", 1)); // the newest stay
        Assert.Null(memory.Get("f0_300x300", 1));

        memory.Put("x_300x300", 1, new byte[500]);
        memory.Put("x_800x800", 1, new byte[500]);
        memory.Forget("x");
        Assert.Null(memory.Get("x_300x300", 1));
        Assert.Null(memory.Get("x_800x800", 1));
    }
}
