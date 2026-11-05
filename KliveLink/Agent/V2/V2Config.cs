using System.Text.Json;

namespace KliveLink.Agent.V2;

public sealed class V2Config
{
    public string? Server { get; set; }
    public string? Token { get; set; }
    public string? AgentId { get; set; }
    public string? PinSha256 { get; set; }

    public static V2Config? TryLoad()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return null;
            string path = Path.Combine(Path.GetDirectoryName(exe)!, "klive.config.json");
            if (!File.Exists(path)) return null;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<V2Config>(File.ReadAllText(path), options);
        }
        catch { return null; }
    }
}
