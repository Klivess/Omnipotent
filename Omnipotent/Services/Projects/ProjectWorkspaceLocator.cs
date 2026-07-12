using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects;

/// <summary>One canonical mapping for the persistent project workspace in every execution environment.</summary>
public static class ProjectWorkspaceLocator
{
    public const string ContainerRoot = "/project";

    public static string HostRoot(string projectID)
    {
        ValidateProjectID(projectID);
        return Path.GetFullPath(Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsVolumesDirectory), projectID));
    }

    public static string NormalizeRelative(string? path)
    {
        path = (path ?? "").Trim().Replace('\\', '/');
        if (path.Equals(ContainerRoot, StringComparison.Ordinal)) return "";
        if (path.StartsWith(ContainerRoot + "/", StringComparison.Ordinal))
            path = path[(ContainerRoot.Length + 1)..];
        if (path == ".") return "";
        if (Path.IsPathRooted(path) || path.StartsWith('/'))
            throw new InvalidOperationException("Use a project-relative path or /project/...; arbitrary absolute paths are not in the shared workspace.");

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new InvalidOperationException("Project paths cannot contain '.' or '..' segments.");
        return string.Join('/', parts);
    }

    public static string HostPath(string projectID, string? path)
    {
        string root = HostRoot(projectID);
        string relative = NormalizeRelative(path);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved path escapes the project workspace.");
        return full;
    }

    public static string ContainerPath(string? path)
    {
        string relative = NormalizeRelative(path);
        return relative.Length == 0 ? ContainerRoot : ContainerRoot + "/" + relative;
    }

    private static void ValidateProjectID(string projectID)
    {
        if (string.IsNullOrWhiteSpace(projectID)
            || projectID.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid project ID.", nameof(projectID));
    }
}
