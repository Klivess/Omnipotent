using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects.Computers;

/// <summary>Explicit exports are disposable copies. All writes go to the authoritative worker.</summary>
public sealed class WorkerWorkspaceBackend(WorkerClient client) : IProjectWorkspaceBackend
{
    private static string Route(string projectID, string suffix, string path) =>
        $"/workspaces/{WorkerClient.Segment(projectID)}/{suffix}?path={Uri.EscapeDataString(ProjectWorkspaceLocator.NormalizeRelative(path))}";

    public IReadOnlyList<ProjectFileEntry> List(string projectID, string directory, bool recursive)
    {
        var rows = (JArray)client.SendAsync(HttpMethod.Get, Route(projectID, "list", directory)).GetAwaiter().GetResult();
        var entries = new List<ProjectFileEntry>();
        foreach (var item in rows)
        {
            if (item.Value<bool?>("symlink") == true) continue;
            string path = item.Value<string>("path")!;
            DateTime modified = DateTimeOffset.FromUnixTimeMilliseconds((long)((item.Value<double?>("modified") ?? 0) * 1000)).UtcDateTime;
            bool isDirectory = item.Value<bool?>("directory") == true;
            entries.Add(new ProjectFileEntry
            {
                FileID = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectID + "/" + path))).ToLowerInvariant(),
                ProjectID = projectID, Path = path, Kind = isDirectory ? ProjectFileKind.Directory : ProjectFileKind.File,
                Size = item.Value<long?>("bytes") ?? 0, CreatedUtc = modified, ModifiedUtc = modified,
                FileSystemModifiedUtc = modified, Origin = ProjectFileOrigin.Filesystem,
            });
            if (recursive && isDirectory) entries.AddRange(List(projectID, path, true));
        }
        return entries;
    }

    public string? Version(string projectID, string path)
    {
        try { return client.SendAsync(HttpMethod.Get, Route(projectID, "file", path) + "&count=1").GetAwaiter().GetResult().Value<string>("version"); }
        catch (KeyNotFoundException) { return null; }
    }

    public FileStream ExportRead(string projectID, string path)
    {
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "ka-export-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.DeleteOnClose);
        try
        {
            long offset = 0;
            string? version = null;
            while (true)
            {
                var chunk = client.SendAsync(HttpMethod.Get, Route(projectID, "file", path) + "&offset=" + offset).GetAwaiter().GetResult();
                string current = chunk.Value<string>("version")!;
                if (version != null && version != current) throw new IOException("Workspace file changed during export; retry the read.");
                version = current;
                byte[] data = Convert.FromBase64String(chunk.Value<string>("data")!);
                stream.Write(data);
                offset += data.Length;
                if (offset >= chunk.Value<long>("bytes")) break;
                if (data.Length == 0) throw new IOException("Incomplete workspace export");
            }
            stream.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != version) throw new IOException("Workspace export checksum mismatch");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Import(string projectID, string path, Stream contents, string? expectedVersion, string transferID)
    {
        if (!contents.CanSeek) throw new ArgumentException("Import requires a seekable staged stream");
        contents.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
        contents.Position = 0;
        byte[] buffer = new byte[1024 * 1024];
        long offset = 0;
        do
        {
            int read = contents.Read(buffer, 0, buffer.Length);
            client.SendAsync(HttpMethod.Put, Route(projectID, "file", path), new
            {
                transferID, offset, data = Convert.ToBase64String(buffer, 0, read), commit = contents.Position == contents.Length,
                expectedVersion, sha256 = hash,
            }).GetAwaiter().GetResult();
            offset += read;
        } while (offset < contents.Length);
    }

    public void Mutate(string projectID, string operation, string path, string? destination = null, bool recursive = false) =>
        client.SendAsync(HttpMethod.Post, Route(projectID, "mutate", path),
            new { operation, destination, recursive, operationID = Guid.NewGuid().ToString("N") }).GetAwaiter().GetResult();
}
