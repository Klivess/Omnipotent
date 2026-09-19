namespace Omnipotent.Services.Projects.Computers;

public interface IProjectWorkspaceBackend
{
    IReadOnlyList<ProjectFileEntry> List(string projectID, string directory, bool recursive);
    FileStream ExportRead(string projectID, string path);
    void Import(string projectID, string path, Stream contents, string? expectedVersion, string transferID);
    string? Version(string projectID, string path);
    void Mutate(string projectID, string operation, string path, string? destination = null, bool recursive = false);
}
