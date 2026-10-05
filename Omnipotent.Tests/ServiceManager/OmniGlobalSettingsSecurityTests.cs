using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;

namespace Omnipotent.Tests.ServiceManager;

public sealed class OmniGlobalSettingsSecurityTests
{
    private const string ServiceId = "test-service";
    private const string ServiceName = "Security test service";

    [Fact]
    public async Task LegacySecretsAndCredentialNames_AreEncryptedBeforeBecomingAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteLegacy(
            Setting("Explicitly private", "legacy-explicit-secret", sensitive: true),
            Setting("Vendor API Key", "legacy-auto-secret", sensitive: false),
            Setting("Display name", "Public display label", sensitive: false));

        await fixture.Manager.InitializeAsync();
        string disk = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain("legacy-explicit-secret", disk);
        Assert.DoesNotContain("legacy-auto-secret", disk);
        Assert.Contains("Public display label", disk);
        var document = JObject.Parse(disk);
        Assert.Equal(1, document["FormatVersion"]!.Value<int>());
        var rows = document["Settings"]!.ToObject<List<OmniSetting>>()!;
        Assert.All(rows.Where(s => s.Name != "Display name"), row =>
        {
            Assert.True(row.Sensitive);
            Assert.True(OmniSettingsProtector.IsProtected(row.Value));
        });
        Assert.Equal("legacy-explicit-secret", await GetString(fixture.Manager, "Explicitly private"));
        Assert.Equal("legacy-auto-secret", await GetString(fixture.Manager, "Vendor API Key"));
        Assert.DoesNotContain("legacy-auto-secret", JsonConvert.SerializeObject(StoredRecords(fixture.Manager)));
    }

    [Fact]
    public async Task LegacySensitivity_PropagatesAcrossSameNameServiceRegistrations()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var first = Setting("CustomAuthValue", "private-sibling-a", sensitive: true);
        first.ParentServiceId = "service-a";
        var second = Setting("CustomAuthValue", "private-sibling-b", sensitive: false);
        second.ParentServiceId = "service-b";
        fixture.WriteLegacy(first, second);
        await fixture.Manager.InitializeAsync();
        string disk = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain("private-sibling-a", disk);
        Assert.DoesNotContain("private-sibling-b", disk);
        Assert.All(StoredRecords(fixture.Manager), row => Assert.True(row.Sensitive));
        var secondView = fixture.Manager.FindExistingSetting("CustomAuthValue", "service-b")!;
        Assert.True(secondView.Sensitive);
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, secondView.Value);
        Assert.Equal("private-sibling-b", await fixture.Manager.GetStringOmniSetting("CustomAuthValue",
            parentServiceId: "service-b", parentServiceName: ServiceName));
    }

    [Fact]
    public async Task PromotingSensitivity_EncryptsPreviouslyPublicSameNameSiblingsAtomically()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await fixture.Manager.SetOmniSetting("CustomAuthValue", "sibling-original-a", "service-a", ServiceName));
        Assert.True(await fixture.Manager.SetOmniSetting("CustomAuthValue", "sibling-original-b", "service-b", ServiceName));
        Assert.True(await fixture.Manager.SetOmniSetting("CustomAuthValue", "private-updated-a", "service-a", ServiceName, sensitive: true));
        string disk = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain("sibling-original-b", disk);
        Assert.DoesNotContain("private-updated-a", disk);
        Assert.All(StoredRecords(fixture.Manager), row =>
        {
            Assert.True(row.Sensitive);
            Assert.True(OmniSettingsProtector.IsProtected(row.Value));
        });
        Assert.Equal("sibling-original-b", await fixture.Manager.GetStringOmniSetting("CustomAuthValue",
            parentServiceId: "service-b", parentServiceName: ServiceName));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("Name")]
    [InlineData("ParentServiceId")]
    [InlineData("ParentServiceName")]
    [InlineData("Type")]
    [InlineData("Sensitive")]
    [InlineData("plaintext")]
    [InlineData("DropdownOptions")]
    public async Task TamperedVersionedDocument_FailsStartupAndGettersWithoutOverwriting(string modification)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "original-private-secret", sensitive: true));
        var document = JObject.Parse(File.ReadAllText(fixture.SettingsPath));
        var row = (JObject)document["Settings"]![0]!;
        switch (modification)
        {
            case "ciphertext":
                byte[] encrypted = Convert.FromBase64String(row["Value"]!.Value<string>()![OmniSettingsProtector.VersionPrefix.Length..]);
                encrypted[^1] ^= 0x80;
                row["Value"] = OmniSettingsProtector.VersionPrefix + Convert.ToBase64String(encrypted);
                break;
            case "Name": row["Name"] = "different setting"; break;
            case "ParentServiceId": row["ParentServiceId"] = "different-service"; break;
            case "ParentServiceName": row["ParentServiceName"] = "different owner"; break;
            case "Type": row["Type"] = (int)OmniSettingType.Bool; break;
            case "Sensitive": row["Sensitive"] = false; break;
            case "plaintext": row["Value"] = "attacker-private-secret"; break;
            case "DropdownOptions": row["DropdownOptions"] = new JArray("injected plaintext choice"); break;
        }
        File.WriteAllText(fixture.SettingsPath, document.ToString());
        byte[] originalBytes = File.ReadAllBytes(fixture.SettingsPath);
        var reloaded = fixture.NewManager();

        var startup = await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.InitializeAsync());
        Assert.DoesNotContain("original-private-secret", startup.ToString());
        Assert.DoesNotContain("attacker-private-secret", startup.ToString());
        await Assert.ThrowsAsync<InvalidDataException>(() => GetString(reloaded, "Private field"));
        await Assert.ThrowsAsync<InvalidDataException>(() => SetString(reloaded, "Private field", "replacement", sensitive: true));
        Assert.Null(reloaded.FindExistingSetting("Private field", ServiceId));
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Fact]
    public async Task EarlierVersionOnePayload_WithoutOwnerBindingRemainsReadableAndIsUpgraded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var record = Setting("Private field", "", sensitive: true);
        record.Value = fixture.Protector.Protect(JsonConvert.SerializeObject(new
        {
            Value = "previous-v1-private-value", DropdownOptions = Array.Empty<string>()
        }), record);
        File.WriteAllText(fixture.SettingsPath, JsonConvert.SerializeObject(new { FormatVersion = 1, Settings = new[] { record } }));

        await fixture.Manager.InitializeAsync();
        Assert.Equal("previous-v1-private-value", await GetString(fixture.Manager, "Private field"));
        var saved = JObject.Parse(File.ReadAllText(fixture.SettingsPath))["Settings"]![0]!.ToObject<OmniSetting>()!;
        var payload = JObject.Parse(fixture.Protector.Unprotect(saved.Value, saved));
        Assert.Equal(ServiceName, payload["ParentServiceName"]!.Value<string>());
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("previous-v1-private-value", await GetString(restarted, "Private field"));
    }

    [Fact]
    public async Task VersionedSensitivePlaintext_IsRejectedInsteadOfMigrated()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SettingsPath, JsonConvert.SerializeObject(new
        {
            FormatVersion = 1,
            Settings = new[] { Setting("Private field", "unprotected-secret", sensitive: true) }
        }));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => GetString(fixture.Manager, "Private field"));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Fact]
    public async Task VersionedCredentialNamedPlaintext_CannotDowngradeSensitivity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SettingsPath, JsonConvert.SerializeObject(new
        {
            FormatVersion = 1,
            Settings = new[] { Setting("Vendor API Key", "unprotected-credential", sensitive: false) }
        }));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.InitializeAsync());
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("string")]
    [InlineData("future")]
    public async Task VersionedDocument_RequiresExplicitSupportedIntegerVersion(string version)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var document = new JObject { ["Settings"] = new JArray() };
        if (version == "string") document["FormatVersion"] = "1";
        if (version == "future") document["FormatVersion"] = 2;
        File.WriteAllText(fixture.SettingsPath, document.ToString());
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.InitializeAsync());
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Fact]
    public async Task Sensitivity_RemainsStickyAcrossDefaultGettersAndSetters()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "first-private-value", sensitive: true));
        Assert.Equal("first-private-value", await GetString(fixture.Manager, "Private field"));
        Assert.True(await SetString(fixture.Manager, "Private field", "second-private-value", sensitive: false));
        Assert.Equal("second-private-value", await GetString(fixture.Manager, "Private field"));
        Assert.True(fixture.Manager.FindExistingSetting("Private field", ServiceId)!.Sensitive);
        string disk = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain("first-private-value", disk);
        Assert.DoesNotContain("second-private-value", disk);
        Assert.True(OmniSettingsProtector.IsProtected(StoredRecords(fixture.Manager).Single().Value));
    }

    [Fact]
    public async Task EverySettingType_RoundTripsWithSensitiveDropdownOptionsEncrypted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.Equal("typed-string-secret", await fixture.Manager.GetStringOmniSetting("Private string", "typed-string-secret", sensitive: true,
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.True(await fixture.Manager.GetBoolOmniSetting("Private flag", true, sensitive: true, parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal(123456789, await fixture.Manager.GetIntOmniSetting("Private number", 123456789, sensitive: true,
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        string[] options = ["private-dropdown-option-a", "private-dropdown-option-b"];
        Assert.Equal(options[1], await fixture.Manager.GetDropdownOmniSetting("Private choice", options[1], options, sensitive: true,
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        string[] entries = ["private-list-entry-a", "private-list-entry-b"];
        Assert.Equal(entries, await fixture.Manager.GetStringListOmniSetting("Private list", entries, sensitive: true,
            parentServiceId: ServiceId, parentServiceName: ServiceName));

        string disk = File.ReadAllText(fixture.SettingsPath);
        Assert.All(new[] { "typed-string-secret", "123456789" }.Concat(options).Concat(entries), secret => Assert.DoesNotContain(secret, disk));
        Assert.All(StoredRecords(fixture.Manager), row =>
        {
            Assert.True(row.Sensitive);
            Assert.True(OmniSettingsProtector.IsProtected(row.Value));
            Assert.Empty(row.DropdownOptions);
        });
        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        Assert.Equal("typed-string-secret", await GetString(reloaded, "Private string"));
        Assert.True(await reloaded.GetBoolOmniSetting("Private flag", parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal(123456789, await reloaded.GetIntOmniSetting("Private number", parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal(options[1], await reloaded.GetDropdownOmniSetting("Private choice", options[0], options,
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal(entries, await reloaded.GetStringListOmniSetting("Private list", parentServiceId: ServiceId, parentServiceName: ServiceName));
    }

    [Fact]
    public async Task FindAndEvents_RedactCurrentAndPreviousValuesAndReturnIsolatedSnapshots()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        var events = new List<OmniSettingsChangedEventArgs>();
        fixture.Manager.OnSettingsChanged += (_, change) => events.Add(change);
        Assert.True(await SetString(fixture.Manager, "Private field", "short", sensitive: true));
        Assert.True(await SetString(fixture.Manager, "Private field", "a-much-longer-private-secret"));
        Assert.True(await SetString(fixture.Manager, "Private field", "a-much-longer-private-secret"));
        Assert.Equal(3, events.Count);
        Assert.True(events[0].IsNewSetting);
        Assert.True(events[0].ValueChanged);
        Assert.Equal("", events[0].PreviousValue);
        Assert.False(events[1].IsNewSetting);
        Assert.True(events[1].ValueChanged);
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, events[1].PreviousValue);
        Assert.False(events[2].ValueChanged);
        Assert.All(events, change => Assert.Equal(OmniGlobalSettingsManager.SecretMask, change.Setting.Value));
        string serializedEvents = JsonConvert.SerializeObject(events);
        Assert.DoesNotContain("short", serializedEvents);
        Assert.DoesNotContain("a-much-longer-private-secret", serializedEvents);

        var view = fixture.Manager.FindExistingSetting("Private field", ServiceId)!;
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, view.Value);
        view.Name = "mutated external snapshot";
        view.Value = "external replacement";
        view.Sensitive = false;
        events[1].Setting.Value = "event replacement";
        Assert.Equal("a-much-longer-private-secret", await GetString(fixture.Manager, "Private field"));
        Assert.True(fixture.Manager.FindExistingSetting("Private field", ServiceId)!.Sensitive);
    }

    [Fact]
    public async Task FailedPersistence_ReturnsFailureAndPreservesCommittedStateAndFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "committed-private-value", sensitive: true));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        int notifications = 0;
        fixture.Manager.OnSettingsChanged += (_, _) => notifications++;
        using (new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(await SetString(fixture.Manager, "Private field", "uncommitted-private-value"));
        Assert.Equal("committed-private-value", await GetString(fixture.Manager, "Private field"));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ApiView_UsesConstantMaskWithoutSecretLengthSuffixOrDropdownChoices()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        string[] secrets = ["x", "this-is-a-very-long-private-value-with-an-identifying-suffix"];
        for (int i = 0; i < secrets.Length; i++)
            Assert.True(await SetString(fixture.Manager, $"Private field {i}", secrets[i], sensitive: true));
        var method = typeof(OmniGlobalSettingsManager).GetMethod("ApiView", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.All(StoredRecords(fixture.Manager), stored =>
        {
            var api = JObject.FromObject(method.Invoke(null, new object[] { stored })!);
            Assert.Equal(OmniGlobalSettingsManager.SecretMask, api["Value"]!.Value<string>());
            Assert.Empty((JArray)api["DropdownOptions"]!);
            Assert.True(api["HasValue"]!.Value<bool>());
            Assert.DoesNotContain(secrets[1], api.ToString());
        });
    }

    [Fact]
    public async Task Resolver_RejectsUnscopedAmbiguityAndHonorsExplicitServiceIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await fixture.Manager.SetOmniSetting("Shared private field", "service-a-secret", "service-a", ServiceName, sensitive: true));
        Assert.True(await fixture.Manager.SetOmniSetting("Shared private field", "service-b-secret", "service-b", ServiceName, sensitive: true));
        Assert.Throws<ArgumentException>(() => fixture.Manager.FindExistingSetting("Shared private field"));
        Assert.Null(fixture.Manager.FindExistingSetting("Shared private field", "nonexistent-service"));
        Assert.Equal("service-a-secret", await fixture.Manager.GetStringOmniSetting("Shared private field", parentServiceId: "service-a", parentServiceName: ServiceName));
        Assert.Equal("service-b-secret", await fixture.Manager.GetStringOmniSetting("Shared private field", parentServiceId: "service-b", parentServiceName: ServiceName));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Manager.DeleteOmniSetting("Shared private field"));
        Assert.True(await fixture.Manager.DeleteOmniSetting("Shared private field", "service-a"));
        Assert.Equal("service-b", fixture.Manager.FindExistingSetting("Shared private field")!.ParentServiceId);
    }

    [Fact]
    public async Task ExplicitGlobalApiScope_DoesNotOverwriteUniqueServiceSetting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "service-private-value", sensitive: true));
        Assert.True(await fixture.Manager.SetOmniSetting("Private field", "global-private-value", "0", "API/Global",
            fulfilledViaApi: true, sensitive: true));
        Assert.Equal("service-private-value", await GetString(fixture.Manager, "Private field"));
        Assert.Equal("global-private-value", await fixture.Manager.GetStringOmniSetting("Private field",
            parentServiceId: "0", parentServiceName: "API/Global"));
    }

    [Theory]
    [InlineData(OmniSettingType.Bool, "True", "not-a-boolean")]
    [InlineData(OmniSettingType.Int, "42", "not-an-integer")]
    [InlineData(OmniSettingType.Dropdown, "choice-a", "not-a-choice")]
    [InlineData(OmniSettingType.StringList, "[\"value-a\"]", "[1]")]
    [InlineData(OmniSettingType.StringList, "[\"value-a\"]", "{\"value\":\"a\"}")]
    [InlineData(OmniSettingType.String, "original-value", "********")]
    public async Task InvalidApiReplacement_PreservesCommittedValueAndDisk(OmniSettingType type, string originalValue, string invalid)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await fixture.Manager.SetOmniSetting("Private field", originalValue, ServiceId, ServiceName, type,
            dropdownOptions: new[] { "choice-a", "choice-b" }, sensitive: true));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        int notifications = 0;
        fixture.Manager.OnSettingsChanged += (_, _) => notifications++;
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Manager.SetOmniSetting("Private field", invalid,
            ServiceId, ServiceName, type, fulfilledViaApi: true));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(0, notifications);
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, fixture.Manager.FindExistingSetting("Private field", ServiceId)!.Value);
    }

    [Fact]
    public async Task LegacyArrayDowngrade_IsRejectedAfterMigrationAcrossProtectorInstances()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        fixture.WriteLegacy(Setting("Private field", "legacy-private-secret", sensitive: true));
        byte[] legacy = File.ReadAllBytes(fixture.SettingsPath);
        await fixture.Manager.InitializeAsync();
        Assert.DoesNotContain("legacy-private-secret", File.ReadAllText(fixture.SettingsPath));
        Assert.Single(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
        File.WriteAllBytes(fixture.SettingsPath, legacy);
        using var restartedProtector = new OmniSettingsProtector(fixture.KeyDirectory);
        var restarted = new OmniGlobalSettingsManager(fixture.SettingsDirectory, restartedProtector);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => GetString(restarted, "Private field"));
        Assert.Equal(legacy, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableMigrationMarker_PreventsFailedWriteFromChangingDiskOrCommittedState(bool corrupted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "committed-private-value", sensitive: true));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        string marker = Assert.Single(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
        int notifications = 0;
        fixture.Manager.OnSettingsChanged += (_, _) => notifications++;
        if (corrupted)
        {
            File.WriteAllText(marker, "corrupted");
            Assert.False(await SetString(fixture.Manager, "Private field", "uncommitted-private-value"));
        }
        else
        {
            using var locked = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.False(await SetString(fixture.Manager, "Private field", "uncommitted-private-value"));
        }
        Assert.Equal("committed-private-value", await GetString(fixture.Manager, "Private field"));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
        if (corrupted)
        {
            var reloaded = fixture.NewManager();
            await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.InitializeAsync());
            Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
            Assert.Equal("corrupted", File.ReadAllText(marker));
        }
    }

    [Theory]
    [InlineData("omni-secret:")]
    [InlineData("omni-secret:v1:invalid")]
    [InlineData("omni-secret:v9:future")]
    public async Task NonsensitiveReservedPrefix_IsRejectedBeforeAnyInvalidDocumentCanBeCommitted(string reserved)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Ordinary field", "ordinary public value"));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        await Assert.ThrowsAsync<ArgumentException>(() => SetString(fixture.Manager, "Ordinary field", reserved));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.GetStringOmniSetting("Ordinary default", reserved,
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Null(fixture.Manager.FindExistingSetting("Ordinary default", ServiceId));
        Assert.Equal("ordinary public value", await GetString(fixture.Manager, "Ordinary field"));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        Assert.Equal("ordinary public value", await GetString(reloaded, "Ordinary field"));
    }

    [Fact]
    public async Task SensitiveReservedPrefix_IsSafelyWrappedAndRoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.Manager.InitializeAsync();
        const string literal = "omni-secret:v1:a-sensitive-literal-value";
        Assert.True(await SetString(fixture.Manager, "Private field", literal, sensitive: true));
        Assert.DoesNotContain(literal, File.ReadAllText(fixture.SettingsPath));
        Assert.Equal(literal, await GetString(fixture.Manager, "Private field"));
        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        Assert.Equal(literal, await GetString(reloaded, "Private field"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratedSettingsFileDeletion_CannotResetStoreOrRegenerateMissingKey(bool deleteKey)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "committed-private-value", sensitive: true));
        string keyPath = Path.Combine(fixture.KeyDirectory, OmniSettingsProtector.KeyFileName);
        File.Delete(fixture.SettingsPath);
        if (deleteKey) File.Delete(keyPath);
        Assert.False(await SetString(fixture.Manager, "Private field", "replacement-private-value"));
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.Equal(!deleteKey, File.Exists(keyPath));
        if (!deleteKey) Assert.Equal("committed-private-value", await GetString(fixture.Manager, "Private field"));

        using var restartedProtector = new OmniSettingsProtector(fixture.KeyDirectory);
        var restarted = new OmniGlobalSettingsManager(fixture.SettingsDirectory, restartedProtector);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => GetString(restarted, "Private field"));
        await Assert.ThrowsAsync<InvalidDataException>(() => SetString(restarted, "Private field", "new-private-value", sensitive: true));
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.Equal(!deleteKey, File.Exists(keyPath));
        Assert.Single(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
    }

    [Fact]
    public async Task FreshStore_IsPersistedAsEmptyProtectedDocumentBeforeAvailability()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        await fixture.Manager.InitializeAsync();
        var document = JObject.Parse(File.ReadAllText(fixture.SettingsPath));
        Assert.Equal(1, document["FormatVersion"]!.Value<int>());
        Assert.Empty((JArray)document["Settings"]!);
        Assert.Single(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
        Assert.False(File.Exists(Path.Combine(fixture.KeyDirectory, OmniSettingsProtector.KeyFileName)));
        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        Assert.Empty(StoredRecords(reloaded));
    }

    [Fact]
    public async Task LostDpapiKey_FailsStartupWithoutReplacingKeyOrSettings()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        await fixture.Manager.InitializeAsync();
        Assert.True(await SetString(fixture.Manager, "Private field", "private-persisted-secret", sensitive: true));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);
        string keyPath = Path.Combine(fixture.KeyDirectory, OmniSettingsProtector.KeyFileName);
        File.Delete(keyPath);
        using var missingKeyProtector = new OmniSettingsProtector(fixture.KeyDirectory);
        var reloaded = new OmniGlobalSettingsManager(fixture.SettingsDirectory, missingKeyProtector);
        await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => GetString(reloaded, "Private field"));
        Assert.False(File.Exists(keyPath));
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Fact]
    public async Task InterruptedLegacyFirstSave_IsRecoveredAndEncryptedBeforeStagingIsRemoved()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        fixture.WriteLegacy(Setting("Recovered API Key", "recovered-private-value", sensitive: true));
        string staging = fixture.SettingsPath + ".tmp";
        File.Move(fixture.SettingsPath, staging);

        await fixture.Manager.InitializeAsync();

        Assert.Equal("recovered-private-value", await GetString(fixture.Manager, "Recovered API Key"));
        Assert.DoesNotContain("recovered-private-value", File.ReadAllText(fixture.SettingsPath));
        Assert.False(File.Exists(staging));
        Assert.Single(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
    }

    [Fact]
    public async Task CorruptLegacyStaging_IsPreservedWithoutCreatingEmptySettingsOrMarker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        string staging = fixture.SettingsPath + ".tmp";
        const string partial = "[{\"Name\":\"APIKey\",\"Value\":\"partial-private-value";
        File.WriteAllText(staging, partial);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.InitializeAsync());

        Assert.DoesNotContain("partial-private-value", error.ToString());
        Assert.Equal(partial, File.ReadAllText(staging));
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.Empty(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
    }

    private static OmniSetting Setting(string name, string value, bool sensitive) => new()
    {
        Name = name, Value = value, Type = OmniSettingType.String, Sensitive = sensitive,
        ParentServiceId = ServiceId, ParentServiceName = ServiceName
    };

    private static Task<string> GetString(OmniGlobalSettingsManager manager, string name) => manager.GetStringOmniSetting(
        name, parentServiceId: ServiceId, parentServiceName: ServiceName);

    private static Task<bool> SetString(OmniGlobalSettingsManager manager, string name, string value, bool sensitive = false) => manager.SetOmniSetting(
        name, value, ServiceId, ServiceName, sensitive: sensitive);

    private static IEnumerable<OmniSetting> StoredRecords(OmniGlobalSettingsManager manager) =>
        ((ConcurrentDictionary<string, OmniSetting>)typeof(OmniGlobalSettingsManager)
            .GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!).Values;

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omnisettings-manager-security-" + Guid.NewGuid().ToString("N"));
        private readonly OmniSettingsProtector protector;
        internal string SettingsDirectory => Path.Combine(root, "settings");
        internal string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        internal string KeyDirectory => Path.Combine(root, "private-keys");
        internal OmniGlobalSettingsManager Manager { get; }

        internal Fixture(bool useDpapi = false)
        {
            Directory.CreateDirectory(SettingsDirectory);
            protector = useDpapi ? new OmniSettingsProtector(KeyDirectory) : new OmniSettingsProtector(RandomNumberGenerator.GetBytes(32));
            Manager = NewManager();
        }

        internal OmniGlobalSettingsManager NewManager() => new(SettingsDirectory, protector);
        internal OmniSettingsProtector Protector => protector;
        internal void WriteLegacy(params OmniSetting[] settings) => File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings));

        public void Dispose()
        {
            protector.Dispose();
            foreach (string directory in new[] { SettingsDirectory, KeyDirectory })
            {
                if (!Directory.Exists(directory)) continue;
                OmniSettingsProtector.ValidatePath(directory);
                foreach (string file in Directory.GetFiles(directory))
                {
                    OmniSettingsProtector.ValidatePath(file);
                    File.Delete(file);
                }
                Directory.Delete(directory);
            }
            Directory.Delete(root);
        }
    }
}
