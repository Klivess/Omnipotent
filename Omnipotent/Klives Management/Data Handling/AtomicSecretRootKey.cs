using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Data_Handling;

/// <summary>Crash-safe creation and conservative recovery for encryption root-key files.</summary>
internal static class AtomicSecretRootKey
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="quarantineProtectedData">
    /// Optional recovery hook invoked when the key is unusable but ciphertext exists. It must move
    /// the undecryptable payloads aside (preserving them for offline recovery) and return a short
    /// human description of what it parked. Supplying it converts a permanently bricked store into
    /// a self-healing one: the ciphertext was already unrecoverable without the key, so refusing to
    /// continue only blocks all future writes as well. Omit it to keep the strict, throwing
    /// behaviour for stores where a stale-but-present key may still be restorable from backup.
    /// </param>
    public static byte[] LoadOrCreate(string path, int expectedBytes, Func<bool> hasProtectedData,
        Action<string> hardenPermissions, Action<string> log, string label,
        Func<string>? quarantineProtectedData = null)
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

                // Regenerating over ciphertext would make every existing secret unrecoverable, so
                // the ciphertext is parked first and only then is a fresh key minted. Without a
                // recovery hook the store stays strict and refuses rather than guessing.
                if (hasProtectedData())
                {
                    if (quarantineProtectedData == null)
                        throw new InvalidOperationException(
                            $"{label} root key is corrupt ({existing.Length} bytes) and encrypted data exists. " +
                            "The key was preserved; restore it from backup before using protected data.");

                    string parked = quarantineProtectedData();
                    log($"{label}: root key was unusable ({existing.Length} bytes) and its ciphertext could " +
                        $"never be decrypted again. Parked {parked} and re-keyed so new secrets can be stored.");
                }

                string quarantine = fullPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                File.Move(fullPath, quarantine, overwrite: false);
                log($"{label}: quarantined an unusable {existing.Length}-byte root key at {quarantine}.");
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
