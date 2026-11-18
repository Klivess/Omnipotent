using Omnipotent.Data_Handling;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Demiurge;

/// <summary>One canonical mapping for the persistent Steward workspace in every execution environment.</summary>
public static class StewardWorkspaceLocator
{
    public const string ContainerRoot = "/project";

    public static string HostRoot(string stewardID)
    {
        ValidateStewardID(stewardID);
        return Path.GetFullPath(Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeVolumesDirectory), stewardID));
    }

    public static string NormalizeRelative(string? path)
    {
        path = (path ?? "").Trim().Replace('\\', '/');
        var driveSteward = Regex.Match(path, "^[A-Za-z]:/project(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (driveSteward.Success) path = driveSteward.Groups[1].Success ? driveSteward.Groups[1].Value : "";
        if (path.Equals(ContainerRoot, StringComparison.OrdinalIgnoreCase)) return "";
        if (path.StartsWith(ContainerRoot + "/", StringComparison.OrdinalIgnoreCase))
            path = path[(ContainerRoot.Length + 1)..];
        if (path == ".") return "";
        if (Path.IsPathRooted(path) || path.StartsWith('/'))
            throw new InvalidOperationException("Use a Steward-relative path or /project/...; arbitrary absolute paths are not in the shared workspace.");

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new InvalidOperationException(
                "Path escapes the Steward volume: Steward paths cannot contain '.' or '..' segments.");
        return string.Join('/', parts);
    }

    /// <summary>Also accepts the real host path for this exact Steward's volume. No other host
    /// absolute path is translated, so callers get convenience without cross-Steward access.</summary>
    public static string NormalizeRelative(string stewardID, string? path)
    {
        string supplied = (path ?? "").Trim();
        if (Path.IsPathRooted(supplied))
        {
            string root = HostRoot(stewardID);
            string full = Path.GetFullPath(supplied);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(root, full).Replace('\\', '/');
        }
        return NormalizeRelative(supplied);
    }

    public static string HostPath(string stewardID, string? path)
    {
        string root = HostRoot(stewardID);
        string relative = NormalizeRelative(stewardID, path);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved path escapes the Steward workspace.");
        return full;
    }

    public static string ContainerPath(string? path)
    {
        string relative = NormalizeRelative(path);
        return relative.Length == 0 ? ContainerRoot : ContainerRoot + "/" + relative;
    }

    private static void ValidateStewardID(string stewardID)
    {
        if (string.IsNullOrWhiteSpace(stewardID)
            || stewardID.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid Steward ID.", nameof(stewardID));
    }
}
