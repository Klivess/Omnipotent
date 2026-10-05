using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Diagnostics;
using Omnipotent.Service_Manager;

namespace Omnipotent.Tests.ServiceManager;

public sealed class OmniSettingsProtectorTests
{
    private static OmniSetting Identity(string name = "API Key", string parent = "service-1",
        OmniSettingType type = OmniSettingType.String, bool sensitive = true) => new()
        { Name = name, ParentServiceId = parent, Type = type, Sensitive = sensitive };

    private static OmniSettingsProtector Protector() => new(RandomNumberGenerator.GetBytes(32));

    [Theory]
    [InlineData("")]
    [InlineData("test-secret-0123456789")]
    [InlineData("private value 😺 à 漢字\n\u0000")]
    public void Protect_RoundTripsWithoutPlaintext(string secret)
    {
        using var protector = Protector();
        string encrypted = protector.Protect(secret, Identity());
        Assert.StartsWith(OmniSettingsProtector.VersionPrefix, encrypted);
        if (secret.Length > 0) Assert.DoesNotContain(secret, encrypted);
        Assert.Equal(secret, protector.Unprotect(encrypted, Identity()));
    }

    [Fact]
    public void Protect_UsesFreshNonceForEveryEncryption()
    {
        using var protector = Protector();
        var encrypted = Enumerable.Range(0, 64).Select(_ => protector.Protect("same secret", Identity())).ToList();
        Assert.Equal(64, encrypted.Distinct(StringComparer.Ordinal).Count());
        var nonces = encrypted.Select(value => Convert.ToHexString(
            Convert.FromBase64String(value[OmniSettingsProtector.VersionPrefix.Length..]).AsSpan(0, 12)));
        Assert.Equal(64, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Different name", "service-1", OmniSettingType.String, true)]
    [InlineData("API Key", "different-service", OmniSettingType.String, true)]
    [InlineData("API Key", "service-1", OmniSettingType.StringList, true)]
    [InlineData("API Key", "service-1", OmniSettingType.String, false)]
    public void Unprotect_RejectsSwappedIdentity(string name, string parent, OmniSettingType type, bool sensitive)
    {
        using var protector = Protector();
        string encrypted = protector.Protect("secret", Identity());
        Assert.Throws<CryptographicException>(() => protector.Unprotect(encrypted, Identity(name, parent, type, sensitive)));
    }

    [Fact]
    public void Identity_IsCanonicalizedToManagerKeySemantics()
    {
        using var protector = Protector();
        string encrypted = protector.Protect("secret", Identity(" API KEY ", " SERVICE-1 "));
        Assert.Equal("secret", protector.Unprotect(encrypted, Identity("api key", "service-1")));
        string global = protector.Protect("global secret", Identity(parent: " "));
        Assert.Equal("global secret", protector.Unprotect(global, Identity(parent: "0")));
    }

    [Theory]
    [InlineData(0)] // nonce
    [InlineData(12)] // tag
    [InlineData(28)] // ciphertext
    public void Unprotect_RejectsTampering(int tamperOffset)
    {
        using var protector = Protector();
        string encrypted = protector.Protect("test-secret", Identity());
        byte[] bytes = Convert.FromBase64String(encrypted[OmniSettingsProtector.VersionPrefix.Length..]);
        bytes[tamperOffset] ^= 0x80;
        string corrupted = OmniSettingsProtector.VersionPrefix + Convert.ToBase64String(bytes);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(corrupted, Identity()));
    }

    [Fact]
    public void Unprotect_RejectsWrongKey()
    {
        using var first = Protector();
        using var second = Protector();
        string encrypted = first.Protect("secret", Identity());
        Assert.Throws<CryptographicException>(() => second.Unprotect(encrypted, Identity()));
    }

    [Theory]
    [InlineData("plaintext-secret")]
    [InlineData("omni-secret:v2:unknown")]
    [InlineData("omni-secret:v1:!")]
    [InlineData("omni-secret:v1:")]
    [InlineData("omni-secret:v1:AA==")]
    public void Unprotect_RejectsMalformedOrUnsupportedEnvelope(string envelope)
    {
        using var protector = Protector();
        Assert.Throws<CryptographicException>(() => protector.Unprotect(envelope, Identity()));
    }

    [Fact]
    public void IsProtected_ReservesAllEncryptionVersions()
    {
        Assert.True(OmniSettingsProtector.IsProtected("omni-secret:v1:any"));
        Assert.True(OmniSettingsProtector.IsProtected("omni-secret:v2:any"));
        Assert.True(OmniSettingsProtector.IsProtected("omni-secret:"));
        Assert.False(OmniSettingsProtector.IsProtected("ordinary-secret"));
        Assert.False(OmniSettingsProtector.IsProtected(null));
    }

    [Fact]
    public void SizeLimits_RejectOversizedUtf8AndEnvelope()
    {
        using var protector = Protector();
        Assert.Throws<CryptographicException>(() => protector.Protect(new string('x', OmniSettingsProtector.MaxPlaintextBytes + 1), Identity()));
        Assert.Throws<CryptographicException>(() => protector.Protect(new string('漢', OmniSettingsProtector.MaxPlaintextBytes / 3 + 1), Identity()));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(
            OmniSettingsProtector.VersionPrefix + new string('A', OmniSettingsProtector.MaxEnvelopeCharacters), Identity()));
    }

    [Fact]
    public void Dispose_PreventsFurtherCrypto()
    {
        var protector = Protector();
        string encrypted = protector.Protect("secret", Identity());
        protector.Dispose();
        Assert.Throws<ObjectDisposedException>(() => protector.Protect("secret", Identity()));
        Assert.Throws<ObjectDisposedException>(() => protector.Unprotect(encrypted, Identity()));
    }

    [Fact]
    public void PersistedKey_IsDpapiWrappedAndReusableAcrossInstances()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        string encrypted;
        using (var first = new OmniSettingsProtector(directory.Path)) encrypted = first.Protect("persisted-secret", Identity());
        string keyPath = System.IO.Path.Combine(directory.Path, OmniSettingsProtector.KeyFileName);
        Assert.True(new FileInfo(keyPath).Length > 32);
        Assert.DoesNotContain("persisted-secret", File.ReadAllText(keyPath));
        using var second = new OmniSettingsProtector(directory.Path);
        Assert.Equal("persisted-secret", second.Unprotect(encrypted, Identity()));
        AssertPrivateAcl(new DirectoryInfo(directory.Path).GetAccessControl());
        AssertPrivateAcl(new FileInfo(keyPath).GetAccessControl());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void MissingKey_DecryptAndSubsequentEncryptNeverRegenerateIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        string encrypted;
        using (var first = new OmniSettingsProtector(directory.Path)) encrypted = first.Protect("secret", Identity());
        string keyPath = System.IO.Path.Combine(directory.Path, OmniSettingsProtector.KeyFileName);
        File.Delete(keyPath);
        using var second = new OmniSettingsProtector(directory.Path);
        Assert.Throws<CryptographicException>(() => second.Unprotect(encrypted, Identity()));
        Assert.False(File.Exists(keyPath));
        Assert.Throws<CryptographicException>(() => second.Protect("replacement secret", Identity()));
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void ExistingInstance_RefusesKeyReplacementAfterDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        using var protector = new OmniSettingsProtector(directory.Path);
        protector.Protect("secret", Identity());
        string keyPath = System.IO.Path.Combine(directory.Path, OmniSettingsProtector.KeyFileName);
        File.Delete(keyPath);
        Assert.Throws<CryptographicException>(() => protector.Protect("second secret", Identity()));
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void CorruptedKey_IsNeverReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        string encrypted;
        using (var first = new OmniSettingsProtector(directory.Path)) encrypted = first.Protect("secret", Identity());
        string keyPath = System.IO.Path.Combine(directory.Path, OmniSettingsProtector.KeyFileName);
        byte[] damaged = [1, 2, 3];
        File.WriteAllBytes(keyPath, damaged);
        using var second = new OmniSettingsProtector(directory.Path);
        Assert.Throws<CryptographicException>(() => second.Unprotect(encrypted, Identity()));
        Assert.Throws<CryptographicException>(() => second.Protect("second secret", Identity()));
        Assert.Equal(damaged, File.ReadAllBytes(keyPath));
    }

    [Fact]
    public void HardenFile_RemovesExistingBroadAccessRules()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        Directory.CreateDirectory(directory.Path);
        string path = System.IO.Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "test fixture");
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        OmniSettingsProtector.HardenDirectory(directory.Path);
        OmniSettingsProtector.HardenFile(path);
        AssertPrivateAcl(new FileInfo(path).GetAccessControl());
        AssertPrivateAcl(new DirectoryInfo(directory.Path).GetAccessControl());
    }

    [Theory]
    [InlineData(@"\\server\share\settings.json")]
    [InlineData(@"C:\settings.json:alternate")]
    public void ValidatePath_RejectsNetworkPathsAndAlternateStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<IOException>(() => OmniSettingsProtector.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_RejectsDirectoryJunctionAndItsDescendants()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        Directory.CreateDirectory(directory.Path);
        string target = System.IO.Path.Combine(directory.Path, "target");
        string junction = System.IO.Path.Combine(directory.Path, "junction");
        Directory.CreateDirectory(target);
        try
        {
            // Directory junction creation is available to ordinary Windows users, unlike
            // symlink creation, which can require developer mode or elevation.
            var start = new ProcessStartInfo(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10000), "Directory junction creation timed out.");
            Assert.Equal(0, process.ExitCode);
            Assert.Throws<IOException>(() => OmniSettingsProtector.ValidatePath(junction));
            Assert.Throws<IOException>(() => OmniSettingsProtector.ValidatePath(System.IO.Path.Combine(junction, "settings.json")));
            Assert.Throws<IOException>(() => OmniSettingsProtector.HardenDirectory(junction));
        }
        finally
        {
            // Remove the link itself and the empty target separately; never recurse through it.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(target);
        }
    }

    [Fact]
    public void MigrationMarkers_ArePathSpecificAndPersistentWithoutCreatingCryptoKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        string settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        using (var first = new OmniSettingsProtector(directory.Path))
        {
            Assert.False(first.WasMigrated(settingsPath));
            first.MarkMigrated(settingsPath);
            first.MarkMigrated(settingsPath);
            Assert.True(first.WasMigrated(settingsPath));
            Assert.False(first.WasMigrated(System.IO.Path.Combine(directory.Path, "other-settings.json")));
        }
        using var second = new OmniSettingsProtector(directory.Path);
        Assert.True(second.WasMigrated(settingsPath));
        string marker = Assert.Single(Directory.GetFiles(directory.Path, "settings-*.v1"));
        Assert.Equal("v1", File.ReadAllText(marker));
        Assert.DoesNotContain(settingsPath, System.IO.Path.GetFileName(marker), StringComparison.OrdinalIgnoreCase);
        AssertPrivateAcl(new FileInfo(marker).GetAccessControl());
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, OmniSettingsProtector.KeyFileName)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void CorruptedMigrationMarker_FailsClosedWithoutOverwriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new PrivateTestDirectory();
        string settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        using var protector = new OmniSettingsProtector(directory.Path);
        protector.MarkMigrated(settingsPath);
        string marker = Assert.Single(Directory.GetFiles(directory.Path, "settings-*.v1"));
        File.WriteAllText(marker, "corrupted");
        Assert.Throws<CryptographicException>(() => protector.WasMigrated(settingsPath));
        Assert.Throws<CryptographicException>(() => protector.MarkMigrated(settingsPath));
        Assert.Equal("corrupted", File.ReadAllText(marker));
    }

    [Fact]
    public void InjectedKeyMigrationMarkers_ArePathSpecificAndHaveNoFilesystemSideEffects()
    {
        using var protector = Protector();
        using var directory = new PrivateTestDirectory();
        string first = System.IO.Path.Combine(directory.Path, "settings.json");
        string second = System.IO.Path.Combine(directory.Path, "other-settings.json");
        Assert.False(protector.WasMigrated(first));
        protector.MarkMigrated(first);
        Assert.True(protector.WasMigrated(first));
        Assert.False(protector.WasMigrated(second));
        Assert.False(Directory.Exists(directory.Path));
    }

    private static void AssertPrivateAcl(FileSystemSecurity security)
    {
        using var current = WindowsIdentity.GetCurrent();
        string owner = current.User!.Value;
        string system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(owner, Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier))).Value);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Contains(rule.IdentityReference.Value, new[] { owner, system });
        });
        Assert.Contains(rules, rule => rule.IdentityReference.Value == owner);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == system);
    }

    private sealed class PrivateTestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnisettings-protection-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            // This fixture creates no child directories. Avoid recursive cleanup of any
            // unexpected path or link introduced while a filesystem test is running.
            OmniSettingsProtector.ValidatePath(Path);
            foreach (string file in Directory.GetFiles(Path))
            {
                OmniSettingsProtector.ValidatePath(file);
                File.Delete(file);
            }
            Directory.Delete(Path);
        }
    }
}
