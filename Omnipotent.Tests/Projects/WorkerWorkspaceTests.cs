using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Tests.Projects;

public sealed class WorkerWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ka-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Backend remote = new();
    private readonly ProjectFileStore files;
    public WorkerWorkspaceTests()
    {
        files = new ProjectFileStore(new ProjectFileStoreOptions
        {
            VolumesRoot = Path.Combine(root, "volumes"), MetadataRoot = Path.Combine(root, "metadata"), MinimumFreeDiskBytes = 0,
        });
        files.WorkspaceBackend = _ => remote;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    private sealed class Backend : IProjectWorkspaceBackend
    {
        public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);
        public bool FailInventory;
        public bool LoseImportReply;
        public IReadOnlyList<ProjectFileEntry> List(string projectID, string directory, bool recursive)
        {
            if (FailInventory) throw new IOException("Worker temporarily unreachable");
            return Data.Select(pair => new ProjectFileEntry { FileID = pair.Key, ProjectID = projectID,
                Path = pair.Key, Kind = ProjectFileKind.File, Size = pair.Value.Length,
                CreatedUtc = DateTime.UnixEpoch, ModifiedUtc = DateTime.UnixEpoch, FileSystemModifiedUtc = DateTime.UnixEpoch }).ToList();
        }
        public FileStream ExportRead(string projectID, string path)
        {
            var stream = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
            stream.Write(Data[path]); stream.Position = 0; return stream;
        }
        public string? Version(string projectID, string path) => Data.TryGetValue(path, out var data) ? Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : null;
        public void Import(string projectID, string path, Stream contents, string? expectedVersion, string transferID)
        {
            using var buffer = new MemoryStream(); contents.CopyTo(buffer);
            var data = buffer.ToArray();
            if (Version(projectID, path) == Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()) return;
            Assert.Equal(Version(projectID, path), expectedVersion);
            Data[path] = data;
            if (LoseImportReply) { LoseImportReply = false; throw new IOException("Response lost after commit"); }
        }
        public void Mutate(string projectID, string operation, string path, string? destination = null, bool recursive = false) { }
    }

    [Fact]
    public async Task LinuxAbsolutePathsUseAuthoritativeBytes()
    {
        remote.Data["work/report.txt"] = Encoding.UTF8.GetBytes("from Linux");
        Assert.Equal("from Linux", await files.ReadTextAsync("project", "/project/work/report.txt"));
        Assert.Throws<ProjectFileException>(() => files.GetPhysicalFilePath("project", "work/report.txt"));
        Assert.False(Directory.Exists(Path.Combine(root, "volumes", "project")));
    }

    [Fact]
    public async Task RemoteWritesPreserveMetadataWithoutCreatingLocalWorkspace()
    {
        await files.WriteTextAsync("project", "/project/work/report.txt", "saved", ProjectFileActor.System);
        Assert.Equal("saved", Encoding.UTF8.GetString(remote.Data["work/report.txt"]));
        var entry = files.Stat("project", "work/report.txt", reconcile: false);
        Assert.Equal(ProjectFileActor.System, entry!.ModifiedBy);
        Assert.False(Directory.Exists(Path.Combine(root, "volumes", "project")));
    }

    [Fact]
    public async Task KeepBothRetryAfterLostReplyUsesTheOriginalDestination()
    {
        remote.Data["shared/report.txt"] = "original"u8.ToArray();
        var session = files.CreateUploadSession(ProjectUploadPurpose.ExistingProject, "project", ProjectFileActor.System);
        await files.AppendUploadChunkAsync(session.SessionID, "shared/report.txt", 0, 3, "text/plain",
            new MemoryStream("new"u8.ToArray()), ProjectFileActor.System);
        var options = new ProjectFileCommitOptions { ConflictPolicy = ProjectFileConflictPolicy.KeepBoth };
        remote.LoseImportReply = true;
        Assert.Throws<IOException>(() => files.CommitUploadSession(session.SessionID, "project", ProjectFileActor.System, options));
        var committed = files.CommitUploadSession(session.SessionID, "project", ProjectFileActor.System, options);
        Assert.Equal("shared/report (1).txt", Assert.Single(committed.Items).CommittedPath);
        Assert.Equal(2, remote.Data.Count);
        Assert.Equal("original", Encoding.UTF8.GetString(remote.Data["shared/report.txt"]));
    }

    [Fact]
    public void FailedInventoryDoesNotDeleteKnownFiles()
    {
        remote.Data["report.txt"] = [1, 2, 3];
        files.Reconcile("project");
        remote.FailInventory = true;
        Assert.Throws<IOException>(() => files.Reconcile("project"));
        Assert.NotNull(files.Stat("project", "report.txt", reconcile: false));
    }
}
