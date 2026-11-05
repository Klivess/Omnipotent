namespace Omnipotent.Services.KliveLink;

/// <summary>
/// Finds generated KliveLink deployment artifacts without assuming the server runs
/// from the repository root. Deployment tools commonly place them beside the exe,
/// in serverBuild, or in the campaign output folder.
/// </summary>
public static class KliveLinkAssetLocator
{
    public static string? Find(string fileName)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "serverBuild", fileName),
            Path.Combine(baseDir, "..", fileName),
            Path.Combine(baseDir, "..", "serverBuild", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-live", "setup", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-live", "agent", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-live", "stub", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-test", "setup", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-test", "agent", fileName),
            Path.Combine(baseDir, "artifacts", "campaign-test", "stub", fileName),
        };
        foreach (string path in candidates)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
