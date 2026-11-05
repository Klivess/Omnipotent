namespace KliveLink.Agent.V2;

public static class V2Identity
{
    public static string GetOrCreate(string? overrideId = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideId)) return overrideId;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Caches");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "k.dat");
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16) return existing;
            }
            string id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            try { File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System); } catch { }
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
