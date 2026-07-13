using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public sealed class ProjectFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-project-files-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider clock = new(DateTimeOffset.Parse("2026-07-11T10:00:00Z"));
    private readonly ProjectFileStore store;
    private readonly ProjectFileActor klives = new(ProjectFileActorType.User, "user-1", "Klives");
    private const string ProjectID = "project-test";

    public ProjectFileStoreTests()
    {
        store = new ProjectFileStore(new ProjectFileStoreOptions
        {
            VolumesRoot = Path.Combine(root, "volumes"),
            MetadataRoot = Path.Combine(root, "metadata"),
            MaxFileBytes = 1024 * 1024,
            MaxChunkBytes = 4,
            MinimumFreeDiskBytes = 0,
            UploadTimeToLive = TimeSpan.FromHours(24),
            TimeProvider = clock,
        });
        store.EnsureProjectScaffold(ProjectID);
    }

    [Fact]
    public async Task InitialUpload_CommitsUnderInputs_WithHashAndProvenance()
    {
        byte[] bytes = "brandkit"u8.ToArray();
        var session = store.CreateUploadSession(ProjectUploadPurpose.Initial, null, klives);
        await store.AppendUploadChunkAsync(session.SessionID, "brand/logo.txt", 0, bytes.Length,
            "text/plain", new MemoryStream(bytes[..4]), klives);
        await store.AppendUploadChunkAsync(session.SessionID, "brand/logo.txt", 4, bytes.Length,
            "text/plain", new MemoryStream(bytes[4..]), klives);

        var result = store.CommitUploadSession(session.SessionID, ProjectID, klives);

        var entry = Assert.Single(result.Items).Entry!;
        Assert.Equal("inputs/brand/logo.txt", entry.Path);
        Assert.Equal(ProjectFileOrigin.InitialUpload, entry.Origin);
        Assert.Equal(klives, entry.CreatedBy);
        Assert.Equal(klives, store.Stat(ProjectID, "inputs/brand", reconcile: false)!.CreatedBy);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Sha256);
        Assert.Equal("brandkit", await store.ReadTextAsync(ProjectID, entry.Path));
        Assert.True(File.Exists(Path.Combine(root, "volumes", ProjectID, ".klive", "manifest.json")));
        Assert.Contains(store.ListAudit(ProjectID), x => x.Operation == ProjectFileOperation.Upload && x.Actor == klives);
    }

    [Fact]
    public async Task Listing_IsPagedSearchableAndGlobFiltered()
    {
        var actor = new ProjectFileActor(ProjectFileActorType.Commander, "commander", "Commander");
        for (int i = 0; i < 13; i++)
            await store.WriteTextAsync(ProjectID, $"shared/item-{i:00}.md", $"item {i}", actor);
        await store.WriteTextAsync(ProjectID, "shared/image.png", "not-really-an-image", actor);

        var first = store.List(ProjectID, new ProjectFileListRequest
        {
            Directory = "shared", Limit = 5, Recursive = false,
        });
        var second = store.List(ProjectID, new ProjectFileListRequest
        {
            Directory = "shared", Limit = 5, Offset = 5, Recursive = false,
        });
        var filtered = store.List(ProjectID, new ProjectFileListRequest
        {
            Directory = "shared", Recursive = true, Search = "item-1", Glob = "**/*.md", Limit = 100,
        });

        Assert.Equal(14, first.Total);
        Assert.Equal(5, first.Entries.Count);
        Assert.Empty(first.Entries.Select(x => x.Path).Intersect(second.Entries.Select(x => x.Path)));
        Assert.All(filtered.Entries, x => Assert.EndsWith(".md", x.Path));
        Assert.Contains(filtered.Entries, x => x.Path.EndsWith("item-10.md"));
    }

    [Fact]
    public async Task Commit_RequiresExplicitConflictPolicy_AndKeepBothIsNonDestructive()
    {
        await store.WriteTextAsync(ProjectID, "shared/brief.txt", "original", klives);
        var session = store.CreateUploadSession(ProjectUploadPurpose.ExistingProject, ProjectID, klives);
        await store.AppendUploadChunkAsync(session.SessionID, "shared/brief.txt", 0, 3,
            "text/plain", new MemoryStream("new"u8.ToArray()), klives);

        Assert.Throws<ProjectFileConflictException>(() =>
            store.CommitUploadSession(session.SessionID, ProjectID, klives));
        Assert.Equal("original", await store.ReadTextAsync(ProjectID, "shared/brief.txt"));

        var result = store.CommitUploadSession(session.SessionID, ProjectID, klives,
            new ProjectFileCommitOptions { ConflictPolicy = ProjectFileConflictPolicy.KeepBoth });
        Assert.Equal("shared/brief (2).txt", Assert.Single(result.Items).CommittedPath);
        Assert.Equal("new", await store.ReadTextAsync(ProjectID, "shared/brief (2).txt"));
        Assert.Equal("original", await store.ReadTextAsync(ProjectID, "shared/brief.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData(".klive/manifest.json")]
    [InlineData("shared//empty.txt")]
    public async Task ManagedPaths_RejectTraversalAbsoluteAndReservedMetadata(string path)
    {
        await Assert.ThrowsAsync<ProjectFileException>(() =>
            store.WriteTextAsync(ProjectID, path, "blocked", klives));
    }

    [Fact]
    public async Task ContainerAndLegacyProjectPaths_MapToOneWorkspace_AndScriptsUseLf()
    {
        await store.WriteTextAsync(ProjectID, "/project/work/job.py", "#!/usr/bin/env python3\r\nprint('ok')\r\n", klives);

        Assert.Equal("#!/usr/bin/env python3\nprint('ok')\n",
            await store.ReadTextAsync(ProjectID, "D:/project/work/job.py"));
        string exactHostPath = Path.Combine(root, "volumes", ProjectID, "work", "job.py");
        Assert.Equal("#!/usr/bin/env python3\nprint('ok')\n",
            await store.ReadTextAsync(ProjectID, exactHostPath));
        Assert.Equal("work/job.py", store.Stat(ProjectID, exactHostPath)!.Path);
        Assert.Contains(store.List(ProjectID, new ProjectFileListRequest
        {
            Directory = Path.GetDirectoryName(exactHostPath),
        }).Entries, entry => entry.Path == "work/job.py");
        Assert.Equal("work/job.py", store.NormalizeRelativePath("/project/work/job.py"));
    }

    [Fact]
    public async Task ManagedMetadata_IsVisibleAndReadable_ButRemainsImmutable()
    {
        var listed = store.List(ProjectID, new ProjectFileListRequest { Directory = ".klive", Recursive = true });

        Assert.Contains(listed.Entries, x => x.Path == ".klive/manifest.json");
        Assert.Contains("Derived metadata", await store.ReadTextAsync(ProjectID, "/project/.klive/manifest.json"));
        await Assert.ThrowsAsync<ProjectFileException>(() =>
            store.WriteTextAsync(ProjectID, "/project/.klive/manifest.json", "tampered", klives));
    }

    [Fact]
    public async Task ManagedOperations_PreserveAudit_AndDirectWritesBecomeUnknown()
    {
        var commander = new ProjectFileActor(ProjectFileActorType.Commander, "commander", "Commander");
        await store.WriteTextAsync(ProjectID, "shared/kit/readme.md", "kit", commander);
        var marked = store.SetMetadata(ProjectID, "shared/kit/readme.md", true, "Reusable brand kit", commander);
        Assert.True(marked.Important);
        Assert.Equal("Reusable brand kit", marked.Description);

        store.Copy(ProjectID, "shared/kit", "work/kit-copy", commander);
        store.Move(ProjectID, "work/kit-copy/readme.md", "outputs/readme.md", commander);
        Assert.True(store.Delete(ProjectID, "work/kit-copy", recursive: true, commander));
        var cleanReconcile = store.Reconcile(ProjectID);
        Assert.Equal(0, cleanReconcile.Created);
        Assert.Equal(0, cleanReconcile.Modified);
        Assert.Equal(0, cleanReconcile.Deleted);

        string direct = Path.Combine(root, "volumes", ProjectID, "shared", "from-cli.txt");
        await File.WriteAllTextAsync(direct, "direct");
        var reconciled = store.Stat(ProjectID, "shared/from-cli.txt")!;
        Assert.Equal(ProjectFileActorType.Unknown, reconciled.CreatedBy.Type);
        Assert.Equal(ProjectFileOrigin.Filesystem, reconciled.Origin);

        var operations = store.ListAudit(ProjectID, 100).Select(x => x.Operation).ToHashSet();
        Assert.Contains(ProjectFileOperation.Write, operations);
        Assert.Contains(ProjectFileOperation.Metadata, operations);
        Assert.Contains(ProjectFileOperation.Copy, operations);
        Assert.Contains(ProjectFileOperation.Move, operations);
        Assert.Contains(ProjectFileOperation.Delete, operations);
        Assert.Contains(ProjectFileOperation.ReconcileCreate, operations);

        var firstAuditPage = store.ListAudit(ProjectID, 3);
        var secondAuditPage = store.ListAudit(ProjectID, 3, firstAuditPage[^1].EventID);
        Assert.Equal(3, firstAuditPage.Count);
        Assert.DoesNotContain(secondAuditPage, later => firstAuditPage.Any(first => first.EventID == later.EventID));
    }

    [Fact]
    public async Task ChunkFailures_RollBackStagedLength_AndRemainResumable()
    {
        var session = store.CreateUploadSession(ProjectUploadPurpose.ExistingProject, ProjectID, klives);
        await Assert.ThrowsAsync<ProjectFileException>(() => store.AppendUploadChunkAsync(
            session.SessionID, "shared/data.bin", 0, 6, "application/octet-stream",
            new MemoryStream([1, 2, 3, 4, 5]), klives));
        Assert.Empty(store.ListUploadItems(session.SessionID));

        var first = await store.AppendUploadChunkAsync(session.SessionID, "shared/data.bin", 0, 6,
            "application/octet-stream", new MemoryStream([1, 2, 3, 4]), klives);
        Assert.Equal(4, first.ReceivedSize);
        await Assert.ThrowsAsync<ProjectFileException>(() => store.AppendUploadChunkAsync(
            session.SessionID, "shared/data.bin", 3, 6, "application/octet-stream",
            new MemoryStream([5, 6]), klives));
        var complete = await store.AppendUploadChunkAsync(session.SessionID, "shared/data.bin", 4, 6,
            "application/octet-stream", new MemoryStream([5, 6]), klives);
        Assert.Equal(6, complete.ReceivedSize);
    }

    [Fact]
    public async Task OneSession_AcceptsThreeDifferentFilesConcurrently()
    {
        var session = store.CreateUploadSession(ProjectUploadPurpose.ExistingProject, ProjectID, klives);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        void Signal()
        {
            if (Interlocked.Increment(ref entered) == 3) allEntered.TrySetResult();
        }
        Task[] uploads = Enumerable.Range(0, 3).Select(i => store.AppendUploadChunkAsync(
            session.SessionID, $"shared/parallel-{i}.bin", 0, 1, "application/octet-stream",
            new CoordinatedByteStream((byte)i, Signal, release.Task), klives)).ToArray();

        Task first = await Task.WhenAny(allEntered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        release.TrySetResult();
        await Task.WhenAll(uploads);

        Assert.Same(allEntered.Task, first);
        Assert.Equal(3, store.ListUploadItems(session.SessionID).Count);
        Assert.True(store.CancelUploadSession(session.SessionID, klives));
    }

    [Fact]
    public void ExpiredSessions_AreClosedAndStagedItemsRemoved()
    {
        var session = store.CreateUploadSession(ProjectUploadPurpose.Initial, null, klives);
        clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(1, store.CleanupExpiredUploads());
        Assert.Equal(ProjectUploadStatus.Expired, store.GetUploadSession(session.SessionID)!.Status);
        Assert.Empty(store.ListUploadItems(session.SessionID));
    }

    [Fact]
    public void ToolAudit_OmitsWriteContentsButRetainsHashAndSize()
    {
        string payload = ProjectCommanderTools.AuditPayload("write_file", "{\"path\":\"shared/a.txt\",\"content\":\"secret contents\"}")!;
        Assert.DoesNotContain("secret contents", payload);
        Assert.Contains("sha256=", payload);
        Assert.Contains("15 chars", payload);
        Assert.Contains("shared/a.txt", payload);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;
        public MutableTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan by) => utcNow += by;
    }

    private sealed class CoordinatedByteStream : Stream
    {
        private readonly byte value;
        private readonly Action signal;
        private readonly Task release;
        private bool sent;

        public CoordinatedByteStream(byte value, Action signal, Task release)
        {
            this.value = value;
            this.signal = signal;
            this.release = release;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent) return 0;
            sent = true;
            signal();
            await release.WaitAsync(cancellationToken);
            buffer.Span[0] = value;
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get => sent ? 1 : 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
