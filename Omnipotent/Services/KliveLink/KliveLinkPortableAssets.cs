using System.Reflection;
using System.Text.Json;

namespace Omnipotent.Services.KliveLink;

internal static class KliveLinkPortableAssets
{
    internal sealed class Asset
    {
        public string rid { get; set; } = "";
        public string archive { get; set; } = "";
        public string sha256 { get; set; } = "";
        public long bytes { get; set; }
    }

    private sealed class Document
    {
        public string version { get; set; } = "";
        public string releaseTag { get; set; } = "";
        public string githubRepository { get; set; } = "";
        public List<Asset> assets { get; set; } = new();
    }

    private static readonly Lazy<Document?> Manifest = new(Load);

    private static Document? Load()
    {
        try
        {
            string json = ReadText("KliveLink.PortableAssets");
            return JsonSerializer.Deserialize<Document>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public static Asset? Find(string rid)
        => Manifest.Value?.assets.FirstOrDefault(a =>
            string.Equals(a.rid, rid, StringComparison.OrdinalIgnoreCase));

    public static string? GithubUrl(Asset asset)
    {
        Document? doc = Manifest.Value;
        if (doc == null || string.IsNullOrWhiteSpace(doc.githubRepository) || string.IsNullOrWhiteSpace(doc.releaseTag))
            return null;
        return $"https://github.com/{doc.githubRepository}/releases/download/{doc.releaseTag}/{asset.archive}";
    }

    public static string ReleaseVersion => Manifest.Value?.version ?? "0.0.0";

    public static string ReadText(string resourceName)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) throw new InvalidOperationException("Missing embedded resource " + resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
