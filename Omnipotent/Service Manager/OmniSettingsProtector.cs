using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Omnipotent.Service_Manager;

/// <summary>
/// Authenticated encryption for sensitive OmniSettings. The master key is DPAPI CurrentUser
/// protected in a private user-profile directory, separate from settings and application backups.
/// Never fall back to plaintext or replace an unavailable key when reading protected values.
/// </summary>
internal sealed class OmniSettingsProtector : IDisposable
{
    internal const string ProtectedPrefix = "omni-secret:";
    internal const string VersionPrefix = ProtectedPrefix + "v1:";
    // Storage headroom for settings accepted by older releases; HTTP writes have a smaller limit.
    internal const int MaxPlaintextBytes = 16 * 1024 * 1024;
    internal static readonly int MaxEnvelopeCharacters = VersionPrefix.Length + ((MaxPlaintextBytes + 28 + 2) / 3 * 4);
    internal const string KeyFileName = "settings.key.dpapi";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxKeyFileBytes = 16 * 1024;
    internal const int MaxIdentityCharacters = 65536;
    private static readonly byte[] KeyHeader = Encoding.ASCII.GetBytes("OMNIKEY1");
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("Omnipotent.OmniSettings.MasterKey.v1");
    private static readonly byte[] MigrationMarker = Encoding.ASCII.GetBytes("v1");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string? keyDirectory;
    private readonly string[] ownedKeyDirectories = [];
    private readonly byte[]? testKey;
    private readonly object keyGate = new();
    private readonly HashSet<string> testMigratedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool keyEstablished;
    private bool disposed;

    internal OmniSettingsProtector()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new CryptographicException("The private OmniSettings key directory is unavailable.");
        string appDirectory = Path.Combine(localData, "Omnipotent");
        string securityDirectory = Path.Combine(appDirectory, "Security");
        keyDirectory = Path.Combine(securityDirectory, "OmniSettings");
        ownedKeyDirectories = [appDirectory, securityDirectory, keyDirectory];
    }

    /// <summary>Uses an isolated private directory for persistence tests.</summary>
    internal OmniSettingsProtector(string keyDirectory)
    {
        this.keyDirectory = Path.GetFullPath(keyDirectory ?? throw new ArgumentNullException(nameof(keyDirectory)));
        ownedKeyDirectories = [this.keyDirectory];
    }

    /// <summary>In-memory key injection for crypto tests; production must use the profile key store.</summary>
    internal OmniSettingsProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes) throw new ArgumentException("An AES-256 key is required.", nameof(key));
        testKey = key.ToArray();
    }

    // Versioned documents recognize the entire reserved namespace and reject unknown envelopes.
    // The original array format instead migrates these strings as literal plaintext values.
    internal static bool IsProtected(string? value) => value?.StartsWith(ProtectedPrefix, StringComparison.Ordinal) == true;

    /// <summary>A private, path-specific marker makes plaintext migration a one-time operation.</summary>
    internal bool WasMigrated(string settingsFilePath)
    {
        lock (keyGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string canonicalPath = CanonicalSettingsPath(settingsFilePath);
            if (testKey != null) return testMigratedPaths.Contains(canonicalPath);
            EnsurePrivateKeyDirectories();
            string markerPath = MigrationMarkerPath(canonicalPath);
            ValidatePath(markerPath);
            if (!File.Exists(markerPath)) return false;
            ReadMigrationMarker(markerPath);
            return true;
        }
    }

    internal void MarkMigrated(string settingsFilePath)
    {
        lock (keyGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string canonicalPath = CanonicalSettingsPath(settingsFilePath);
            if (testKey != null) { testMigratedPaths.Add(canonicalPath); return; }
            EnsurePrivateKeyDirectories();
            string markerPath = MigrationMarkerPath(canonicalPath);
            ValidatePath(markerPath);
            if (File.Exists(markerPath)) { ReadMigrationMarker(markerPath); return; }
            string temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidatePath(temporaryPath);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(MigrationMarker);
                    stream.Flush(flushToDisk: true);
                }
                HardenFile(temporaryPath);
                ValidatePath(markerPath);
                try { File.Move(temporaryPath, markerPath, overwrite: false); }
                catch (IOException) when (File.Exists(markerPath)) { ValidatePath(markerPath); }
                ReadMigrationMarker(markerPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    ValidatePath(temporaryPath);
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static string CanonicalSettingsPath(string path)
    {
        ValidatePath(path);
        return Path.GetFullPath(path).ToUpperInvariant();
    }

    private string MigrationMarkerPath(string canonicalPath) => Path.Combine(keyDirectory!,
        "settings-" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(canonicalPath))) + ".v1");

    private static void ReadMigrationMarker(string path)
    {
        HardenFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != MigrationMarker.Length)
            throw new CryptographicException("The OmniSettings migration marker is invalid.");
        Span<byte> content = stackalloc byte[MigrationMarker.Length];
        stream.ReadExactly(content);
        if (!content.SequenceEqual(MigrationMarker))
            throw new CryptographicException("The OmniSettings migration marker is invalid.");
    }

    private void EnsurePrivateKeyDirectories()
    {
        RequireWindows();
        foreach (string directory in ownedKeyDirectories) HardenDirectory(directory);
    }

    internal string Protect(string? value, OmniSetting identity)
    {
        byte[] aad = AssociatedData(identity);
        byte[]? key = null;
        byte[]? plain = null;
        try
        {
            value ??= string.Empty;
            int plainLength = Utf8.GetByteCount(value);
            if (plainLength > MaxPlaintextBytes)
                throw new CryptographicException("The sensitive setting exceeds the supported size.");
            plain = Utf8.GetBytes(value);
            key = LoadKey(allowCreate: true);
            byte[] envelope = new byte[NonceBytes + TagBytes + plain.Length];
            Span<byte> nonce = envelope.AsSpan(0, NonceBytes);
            Span<byte> tag = envelope.AsSpan(NonceBytes, TagBytes);
            Span<byte> cipher = envelope.AsSpan(NonceBytes + TagBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, cipher, tag, aad);
            return VersionPrefix + Convert.ToBase64String(envelope);
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    internal string Unprotect(string value, OmniSetting identity)
    {
        byte[]? envelope = null;
        byte[]? plain = null;
        byte[]? key = null;
        byte[] aad = AssociatedData(identity);
        try
        {
            if (value == null || value.Length > MaxEnvelopeCharacters || !value.StartsWith(VersionPrefix, StringComparison.Ordinal))
                throw new CryptographicException("The sensitive setting has an unsupported or invalid encrypted format.");
            try { envelope = Convert.FromBase64String(value[VersionPrefix.Length..]); }
            catch (FormatException) { throw new CryptographicException("The sensitive setting has an invalid encrypted format."); }
            if (envelope.Length < NonceBytes + TagBytes || envelope.Length > MaxPlaintextBytes + NonceBytes + TagBytes)
                throw new CryptographicException("The sensitive setting has an invalid encrypted format.");

            plain = new byte[envelope.Length - NonceBytes - TagBytes];
            key = LoadKey(allowCreate: false);
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(envelope.AsSpan(0, NonceBytes), envelope.AsSpan(NonceBytes + TagBytes),
                envelope.AsSpan(NonceBytes, TagBytes), plain, aad);
            return Utf8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("The sensitive setting failed authentication.");
        }
        catch (DecoderFallbackException)
        {
            throw new CryptographicException("The sensitive setting contains invalid encrypted data.");
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (envelope != null) CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static byte[] AssociatedData(OmniSetting identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string name = (identity.Name ?? string.Empty).Trim();
        string parent = string.IsNullOrWhiteSpace(identity.ParentServiceId) ? "0" : identity.ParentServiceId.Trim();
        if (name.Length == 0 || name.Length > MaxIdentityCharacters || parent.Length > MaxIdentityCharacters
            || !Enum.IsDefined(identity.Type))
            throw new CryptographicException("The sensitive setting identity is invalid.");
        // Length prefixes prevent ambiguous names (e.g. embedded separators). Match the
        // manager's case-insensitive, trimmed key semantics without allowing cross-setting swaps.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            writer.Write(VersionPrefix);
            writer.Write(name.ToUpperInvariant());
            writer.Write(parent.ToUpperInvariant());
            writer.Write((int)identity.Type);
            writer.Write(identity.Sensitive);
        }
        return stream.ToArray();
    }

    private byte[] LoadKey(bool allowCreate)
    {
        lock (keyGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (testKey != null) return testKey.ToArray();
            EnsurePrivateKeyDirectories();
            string keyPath = Path.Combine(keyDirectory!, KeyFileName);
            ValidatePath(keyPath);
            if (!allowCreate) keyEstablished = true; // A failed decrypt can never be followed by regeneration.
            if (!File.Exists(keyPath))
            {
                if (!allowCreate || keyEstablished)
                    throw new CryptographicException("The OmniSettings encryption key is missing. Restore the original protected key for this Windows account.");
                CreateKeyFile(keyPath);
            }
            HardenFile(keyPath);
            byte[] stored = ReadProtectedKeyFile(keyPath);
            byte[]? wrapped = null;
            byte[]? key = null;
            try
            {
                if (stored.Length < KeyHeader.Length + 1 || stored.Length > MaxKeyFileBytes
                    || !stored.AsSpan(0, KeyHeader.Length).SequenceEqual(KeyHeader))
                    throw new CryptographicException("The OmniSettings encryption key file is invalid.");
                wrapped = stored[KeyHeader.Length..];
                key = ProtectedData.Unprotect(wrapped, KeyEntropy, DataProtectionScope.CurrentUser);
                if (key.Length != KeyBytes)
                    throw new CryptographicException("The OmniSettings encryption key is invalid.");
                keyEstablished = true;
                byte[] result = key;
                key = null;
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
                if (wrapped != null) CryptographicOperations.ZeroMemory(wrapped);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static byte[] ReadProtectedKeyFile(string keyPath)
    {
        ValidatePath(keyPath);
        // Keep writers/deleters out while enforcing the limit and reading, so a size-check
        // race cannot turn a small protected-key read into an unbounded allocation.
        using var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < KeyHeader.Length + 1 || stream.Length > MaxKeyFileBytes)
            throw new CryptographicException("The OmniSettings encryption key file is invalid.");
        byte[] stored = new byte[checked((int)stream.Length)];
        stream.ReadExactly(stored);
        return stored;
    }

    private static void CreateKeyFile(string keyPath)
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);
        byte[]? wrapped = null;
        string tempPath = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            wrapped = ProtectedData.Protect(key, KeyEntropy, DataProtectionScope.CurrentUser);
            ValidatePath(tempPath);
            // The parent has already been hardened before any key material is written.
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(KeyHeader);
                stream.Write(wrapped);
                stream.Flush(flushToDisk: true);
            }
            HardenFile(tempPath);
            ValidatePath(keyPath);
            try { File.Move(tempPath, keyPath, overwrite: false); }
            catch (IOException) when (File.Exists(keyPath))
            {
                // Another process won atomic creation. Read its key rather than overwrite it.
                ValidatePath(keyPath);
            }
            HardenFile(keyPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (wrapped != null) CryptographicOperations.ZeroMemory(wrapped);
            if (File.Exists(tempPath))
            {
                ValidatePath(tempPath);
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Rejects symlinks/junctions in every existing component, including the leaf.</summary>
    internal static void ValidatePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            || fullPath.IndexOf(':', fullPath.IndexOf(':') + 1) >= 0))
            throw new IOException("OmniSettings storage must use a local path without alternate data streams.");
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("OmniSettings storage cannot use symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>ACL failures propagate: secrets must never be written with broader access.</summary>
    internal static void HardenDirectory(string path)
    {
        RequireWindows();
        ValidatePath(path);
        Directory.CreateDirectory(path);
        ValidatePath(path);
        SecurityIdentifier owner = CurrentUser();
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddPrivateRules(security, owner, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    internal static void HardenFile(string path)
    {
        RequireWindows();
        ValidatePath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The OmniSettings storage file is missing.");
        SecurityIdentifier owner = CurrentUser();
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddPrivateRules(security, owner, InheritanceFlags.None);
        new FileInfo(path).SetAccessControl(security);
    }

    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The Windows user identity is unavailable.");
    }

    private static void AddPrivateRules(FileSystemSecurity security, SecurityIdentifier owner, InheritanceFlags inheritance)
    {
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!owner.Equals(system))
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected OmniSettings storage requires Windows DPAPI and filesystem ACLs.");
    }

    public void Dispose()
    {
        lock (keyGate)
        {
            if (disposed) return;
            disposed = true;
            if (testKey != null) CryptographicOperations.ZeroMemory(testKey);
            testMigratedPaths.Clear();
        }
    }
}
