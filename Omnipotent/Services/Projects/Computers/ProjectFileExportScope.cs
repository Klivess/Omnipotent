namespace Omnipotent.Services.Projects.Computers;

/// <summary>Bounded-lifetime materialization for host APIs that require filenames, not streams.</summary>
internal sealed class ProjectFileExportScope(ProjectFileStore? files, string projectID) : IDisposable
{
    private readonly List<string> exports = [];
    public string Resolve(string relative)
    {
        if (files == null) throw new InvalidOperationException("Project files unavailable");
        if (!files.IsRemote(projectID)) return files.GetPhysicalFilePath(projectID, relative);
        string normalized = files.NormalizeProjectPath(projectID, relative, allowManagedMetadata: true);
        string directory = Path.Combine(Path.GetTempPath(), "ka-materialized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Path.GetFileName(normalized));
        exports.Add(path);
        using var source = files.OpenRead(projectID, normalized);
        using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        source.CopyTo(destination);
        return path;
    }
    public void Dispose()
    {
        foreach (string path in exports)
        {
            try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); } catch { }
        }
    }
}
