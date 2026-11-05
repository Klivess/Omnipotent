using System.Text.Json;
using KliveLink.Contracts;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveLink;

public sealed class KliveLinkV2UpdateStore
{
    private readonly string path;
    private readonly object gate = new();

    public KliveLinkV2UpdateStore(string? path = null)
    {
        this.path = path ?? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkDirectory), "updates.json");
    }

    public V2UpdateManifest? GetCurrent()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<V2UpdateManifest>(File.ReadAllText(path), V2Json.Options);
            }
            catch { return null; }
        }
    }

    public void SetCurrent(V2UpdateManifest manifest)
    {
        lock (gate)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, V2Json.Options));
        }
    }
}
