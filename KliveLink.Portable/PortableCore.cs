using System.Runtime.InteropServices;

namespace KliveLink.Agent.V2;

internal static class PortablePaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KliveLink")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support", "KliveLink")
                : Path.Combine(Home, ".local", "share", "klivelink");

    public static string ConfigDirectory =>
        OperatingSystem.IsWindows()
            ? DataDirectory
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support", "KliveLink")
                : Path.Combine(Home, ".config", "klivelink");

    public static string LogDirectory =>
        OperatingSystem.IsWindows()
            ? DataDirectory
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Logs", "KliveLink")
                : Path.Combine(Home, ".local", "state", "klivelink");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "klive.config.json");
    public static string IdentityPath => Path.Combine(DataDirectory, "agent.id");
    public static string LogPath => Path.Combine(LogDirectory, "agent.log");
}

public sealed class V2Config
{
    public string? Server { get; set; }
    public string? Token { get; set; }
    public string? AgentId { get; set; }
    public string? PinSha256 { get; set; }
    public string? AutoTier { get; set; }

    public static string DefaultPath => PortablePaths.ConfigPath;

    public static V2Config? TryLoad(string? explicitPath = null)
    {
        foreach (string path in Candidates(explicitPath))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return System.Text.Json.JsonSerializer.Deserialize<V2Config>(File.ReadAllText(path), options);
            }
            catch { }
        }
        return null;
    }

    public static IEnumerable<string> Candidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
            yield return Path.Combine(Path.GetDirectoryName(exe)!, "klive.config.json");
        yield return DefaultPath;
        if (!OperatingSystem.IsWindows())
            yield return "/etc/klivelink/klive.config.json";
    }
}

public static class V2Identity
{
    public static string GetOrCreate(string? overrideId = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideId)) return overrideId;
        try
        {
            Directory.CreateDirectory(PortablePaths.DataDirectory);
            string path = PortablePaths.IdentityPath;
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16) return existing;
            }
            string id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
            return id;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }
}

public static class V2Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(PortablePaths.LogDirectory);
                string path = PortablePaths.LogPath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_048_576)
                {
                    string old = path + ".1";
                    try { if (File.Exists(old)) File.Delete(old); File.Move(path, old); } catch { }
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
