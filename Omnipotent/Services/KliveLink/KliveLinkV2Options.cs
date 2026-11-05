using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KliveLink.Contracts;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveLink;

public sealed class KliveLinkV2Options
{
    public string ListenPrefix { get; init; } = "http://127.0.0.1:5101/";
    public string DataDirectory { get; init; } = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkDirectory);
    public string DbPath { get; init; } = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2DbFile);
    public string KeyPath { get; init; } = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2KeyFile);
    public string TokenFilePath { get; init; } = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2TokenFile);
    public string CollectionDirectory { get; init; } = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkCollectionDirectory);
    public HashSet<string> TokenHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowDevToken { get; init; }

    public static KliveLinkV2Options Load()
    {
        string dataDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkDirectory);
        Directory.CreateDirectory(dataDir);
        string tokenFile = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2TokenFile);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(tokenFile))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
                if (doc.RootElement.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement token in tokens.EnumerateArray())
                    {
                        string? value = token.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) hashes.Add(NormalizeHash(value));
                    }
                }
            }
            catch
            {
                // A malformed token file is treated as no tokens; the dev-token fallback keeps local runs alive.
            }
        }

        string? env = Environment.GetEnvironmentVariable("KLIVELINK_V2_TOKEN_HASHES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (string token in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                hashes.Add(NormalizeHash(token));
        }

        string? allowDev = Environment.GetEnvironmentVariable("KLIVELINK_V2_ALLOW_DEV");
        bool allow = allowDev is null ? hashes.Count == 0 : allowDev != "0";
        string prefix = Environment.GetEnvironmentVariable("KLIVELINK_V2_LISTEN") ?? "http://127.0.0.1:5101/";

        return new KliveLinkV2Options
        {
            ListenPrefix = prefix,
            DataDirectory = dataDir,
            DbPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2DbFile),
            KeyPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkV2KeyFile),
            TokenFilePath = tokenFile,
            CollectionDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLinkCollectionDirectory),
            TokenHashes = hashes,
            AllowDevToken = allow,
        };
    }

    public bool IsTokenValid(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string hash = NormalizeHash(token);
        if (TokenHashes.Contains(hash)) return true;
        return AllowDevToken && string.Equals(token, "dev-token", StringComparison.Ordinal);
    }

    public static string NormalizeHash(string value)
    {
        value = value.Trim();
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : V2Proof.Sha256Hex(value);
    }
}

public sealed class KliveLinkV2SecretBox
{
    private readonly byte[] key;

    public KliveLinkV2SecretBox(string keyPath)
    {
        string? dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(keyPath))
        {
            key = File.ReadAllBytes(keyPath);
            if (key.Length != 32) throw new InvalidDataException("KliveLink v2 key must be 32 bytes.");
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, key);
        }
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[data.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        byte[] blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText)) return "";
        try
        {
            byte[] blob = Convert.FromBase64String(protectedText);
            return Encoding.UTF8.GetString(UnprotectBytes(blob));
        }
        catch
        {
            return "";
        }
    }

    public byte[] ProtectBytes(byte[] data)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[data.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        byte[] blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        return blob;
    }

    public byte[] UnprotectBytes(byte[] blob)
    {
        if (blob.Length < 29) return Array.Empty<byte>();
        byte[] nonce = blob.AsSpan(0, 12).ToArray();
        byte[] tag = blob.AsSpan(12, 16).ToArray();
        byte[] cipher = blob.AsSpan(28).ToArray();
        byte[] data = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, data);
        return data;
    }
}
