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
        key = LoadOrCreateKey(keyPath);
    }

    private static byte[] LoadOrCreateKey(string keyPath)
    {
        string? dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(keyPath))
        {
            try
            {
                byte[] raw = File.ReadAllBytes(keyPath);
                if (raw.Length == 32)
                {
                    return raw;
                }

                // Check text encodings: hex, base64, trimmed ASCII/UTF-8, or passphrase
                string text = Encoding.UTF8.GetString(raw).Trim();

                // 64-character hex string (32 bytes in hex)
                if (text.Length == 64 && text.All(Uri.IsHexDigit))
                {
                    return Convert.FromHexString(text);
                }

                // Base64 encoded 32-byte key (standard base64 length is 44 characters)
                if (text.Length == 44)
                {
                    try
                    {
                        byte[] decoded = Convert.FromBase64String(text);
                        if (decoded.Length == 32) return decoded;
                    }
                    catch { }
                }

                // If trimmed text bytes are exactly 32 bytes (e.g. 32-char string with stripped newlines)
                byte[] textBytes = Encoding.UTF8.GetBytes(text);
                if (textBytes.Length == 32)
                {
                    return textBytes;
                }

                // If non-empty passphrase / text, hash with SHA256 to derive a stable 32-byte key
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return SHA256.HashData(textBytes);
                }

                // 0-byte or whitespace-only file: quarantine unusable file and regenerate
                string quarantine = keyPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try { File.Move(keyPath, quarantine, overwrite: true); } catch { }
            }
            catch
            {
                // Fall through to fresh key generation
            }
        }

        byte[] freshKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllBytes(keyPath, freshKey);
        }
        catch { }
        return freshKey;
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
