using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Data_Handling;

/// <summary>Crash-safe creation and conservative recovery for encryption root-key files.</summary>
internal static class AtomicSecretRootKey
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static byte[] LoadOrCreate(string path, int expectedBytes, Func<bool> hasProtectedData,
        Action<string> hardenPermissions, Action<string> log, string label)
    {
        string fullPath = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath))
            {
                byte[] existing = File.ReadAllBytes(fullPath);
                if (existing.Length == expectedBytes)
                {
                    hardenPermissions(fullPath);
                    return existing;
                }

                // Regenerating over ciphertext would make every existing secret unrecoverable.
                // If inspection itself fails, propagate that failure and preserve the bad key.
                if (hasProtectedData())
                    throw new InvalidOperationException(
                        $"{label} root key is corrupt ({existing.Length} bytes) and encrypted data exists. " +
                        "The key was preserved; restore it from backup before using protected data.");

                string quarantine = fullPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                File.Move(fullPath, quarantine, overwrite: false);
                log($"{label}: quarantined an unusable {existing.Length}-byte root key at {quarantine}; no encrypted data existed.");
            }

            byte[] created = RandomNumberGenerator.GetBytes(expectedBytes);
            string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 4096, FileOptions.WriteThrough))
                {
                    fs.Write(created, 0, created.Length);
                    fs.Flush(flushToDisk: true);
                }
                try { File.Move(temp, fullPath, overwrite: false); }
                catch (IOException) when (File.Exists(fullPath))
                {
                    // Another Omnipotent process won the atomic-create race. Use only a complete
                    // winner; never overwrite or silently accept a partial key.
                    byte[] winner = File.ReadAllBytes(fullPath);
                    if (winner.Length != expectedBytes)
                        throw new InvalidOperationException(
                            $"{label} root key creation raced an unusable {winner.Length}-byte file; it was preserved for recovery.");
                    CryptographicOperations.ZeroMemory(created);
                    hardenPermissions(fullPath);
                    return winner;
                }
                hardenPermissions(fullPath);
                log($"{label}: generated a new root key atomically.");
                return created;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
