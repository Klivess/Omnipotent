using System.Security.Cryptography;

namespace Omnipotent.Services.Projects.Containers;

/// <summary>
/// Moves an authenticated Chromium profile between desktops in the same Project without ever
/// serialising cookie or session values into an agent result, API response, log, or project file.
/// Chromium is stopped by <see cref="ContainerDesktopManager"/> before this class is called; this
/// class only performs the local, atomic-ish profile replacement on the host-mounted project data.
/// </summary>
internal static class BrowserProfileHandoff
{
    private const string ProfilesDirectoryName = "browser-profiles";

    /// <summary>Maps an agent ID to the stable directory segment used by its browser profile.</summary>
    internal static string ProfileSegment(string? agentID)
    {
        string segment = string.Concat((agentID ?? "shared")
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return string.IsNullOrWhiteSpace(segment) ? "shared" : segment;
    }

    internal static string ProfilePath(string projectID, string? agentID) =>
        Path.Combine(ProjectWorkspaceLocator.HostRoot(projectID), ".klive", ProfilesDirectoryName,
            ProfileSegment(agentID));

    /// <summary>
    /// Copies the complete Chromium profile into <paramref name="destinationProfile"/>. This is
    /// intentionally opaque: the only result is metadata about the handoff, never the profile's
    /// contents. The source profile remains unchanged, while the destination's previous profile is
    /// replaced only after a complete staging copy succeeds.
    /// </summary>
    internal static BrowserProfileHandoffResult Copy(string sourceProfile, string destinationProfile)
    {
        string source = Path.GetFullPath(sourceProfile);
        string destination = Path.GetFullPath(destinationProfile);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return BrowserProfileHandoffResult.AlreadyShared;
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("The source agent has no saved browser profile yet.");

        string parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Browser-profile destination has no parent directory.");
        Directory.CreateDirectory(parent);
        string name = Path.GetFileName(destination);
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string staging = Path.Combine(parent, "." + name + ".handoff-" + nonce);
        string backup = Path.Combine(parent, "." + name + ".replaced-" + nonce);

        try
        {
            CopyDirectory(source, staging);
            RemoveRuntimeArtifacts(staging);

            bool destinationExisted = Directory.Exists(destination);
            if (destinationExisted) Directory.Move(destination, backup);
            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (destinationExisted && !Directory.Exists(destination) && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }

            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return new BrowserProfileHandoffResult(true, destinationExisted);
        }
        finally
        {
            // Staging/backup only survive a catastrophic process termination. Ordinary errors leave
            // the previous destination intact and do not accumulate secret-bearing profile copies.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            // A Chromium profile should not contain links. Do not follow one if a compromised page
            // or extension managed to create it: a handoff must stay within the profile directory.
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

            string target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    private static void RemoveRuntimeArtifacts(string profile)
    {
        foreach (string name in new[]
        {
            "SingletonLock", "SingletonSocket", "SingletonCookie", ".omnipotent-launch.lock",
        })
        {
            string path = Path.Combine(profile, name);
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}

/// <summary>Deliberately content-free browser-session handoff outcome.</summary>
internal readonly record struct BrowserProfileHandoffResult(bool ReplacedDestination, bool DestinationPreviouslyExisted)
{
    internal static BrowserProfileHandoffResult AlreadyShared => new(false, false);
}
