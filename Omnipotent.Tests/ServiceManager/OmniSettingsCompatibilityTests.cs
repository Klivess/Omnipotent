using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;

namespace Omnipotent.Tests.ServiceManager;

public sealed class OmniSettingsCompatibilityTests
{
    private const string ServiceId = "legacy-service-id";
    private const string ServiceName = "Legacy service";

    [Theory]
    [InlineData("legacy-api-key")]
    [InlineData("omni-secret:v1:this-is-a-literal-existing-api-key")]
    [InlineData("omni-secret:v99:literal-future-prefix")]
    public async Task LegacyPlaintextSecret_IsPreservedThroughMigrationAndDpapiRestart(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        fixture.WriteLegacy(Setting("Legacy API Key", value));

        await fixture.Manager.InitializeAsync();
        Assert.Equal(value, await GetString(fixture.Manager, "Legacy API Key", "code-default"));
        Assert.DoesNotContain(value, File.ReadAllText(fixture.SettingsPath));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal(value, await GetString(restarted, "Legacy API Key", "code-default"));
        Assert.True(restarted.FindExistingSetting("Legacy API Key", ServiceId)!.Sensitive);
    }

    [Fact]
    public async Task LegacyLargeSecret_TrustedReadsAndUpdatesKeepHistoricalValueCapacity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        string value = "legacy-large-secret-" + new string('x', 100_000);
        fixture.WriteLegacy(Setting("Large private field", value));

        await fixture.Manager.InitializeAsync();
        Assert.Equal(value, await GetString(fixture.Manager, "Large private field", "code-default"));
        Assert.DoesNotContain(value, File.ReadAllText(fixture.SettingsPath));
        string updatedValue = value + "-updated-by-trusted-service";
        Assert.True(await fixture.Manager.SetOmniSetting("Large private field", updatedValue, ServiceId, ServiceName));
        Assert.Equal(updatedValue, await GetString(fixture.Manager, "Large private field", "code-default"));
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal(updatedValue, await GetString(restarted, "Large private field", "code-default"));
    }

    [Fact]
    public async Task LegacyNormalizedDuplicateIdentities_PreserveLastSavedValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var earlier = Setting(" Private field ", "earlier-secret");
        earlier.ParentServiceId = " LEGACY-SERVICE-ID ";
        var latest = Setting("private FIELD", "last-saved-secret");
        fixture.WriteLegacy(earlier, latest);

        await fixture.Manager.InitializeAsync();
        Assert.Equal("last-saved-secret", await GetString(fixture.Manager, "Private field", "code-default"));
        Assert.Single(ReadRows(fixture.SettingsPath));
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("last-saved-secret", await GetString(restarted, "Private field", "code-default"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task LegacyDuplicateWithLastValueCleared_DoesNotResurrectEarlierCredential(string? latestValue)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteLegacy(Setting("Private field", "obsolete-cleared-secret"), Setting("Private field", latestValue));

        await fixture.Manager.InitializeAsync();
        Assert.Equal("code-default", await GetString(fixture.Manager, "Private field", "code-default"));
        var row = Assert.Single(ReadRows(fixture.SettingsPath));
        Assert.False(fixture.Manager.FindExistingSetting("Private field", ServiceId)!.HasValue);
        Assert.True(string.IsNullOrEmpty(fixture.ReadPayload(row)["Value"]!.Value<string>()));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("code-default", await GetString(restarted, "Private field", "code-default"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("new-code-default")]
    public async Task ChangedServiceId_UsesSameNamedOwnersSavedCredentialBeforeOtherOwnersOrDefaults(string defaultValue)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var own = Setting("Private field", "saved-owner-secret");
        var other = Setting("Private field", "other-service-secret");
        other.ParentServiceId = "other-service-id";
        other.ParentServiceName = "Other service";
        fixture.WriteLegacy(own, other);
        await fixture.Manager.InitializeAsync();

        Assert.Equal("saved-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", defaultValue,
            parentServiceId: "new-service-id", parentServiceName: ServiceName));
        Assert.Equal("other-service-secret", await fixture.Manager.GetStringOmniSetting("Private field", "other-default",
            parentServiceId: "other-service-id", parentServiceName: "Other service"));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("saved-owner-secret", await restarted.GetStringOmniSetting("Private field", defaultValue,
            parentServiceId: "another-new-service-id", parentServiceName: ServiceName));
    }

    [Fact]
    public async Task ChangedServiceId_TrustedUpdateTargetsThePreviouslyResolvedOwnerWithoutCreatingDuplicate()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var own = Setting("Private field", "original-owner-secret");
        var other = Setting("Private field", "other-service-secret");
        other.ParentServiceId = "other-service-id";
        other.ParentServiceName = "Other service";
        fixture.WriteLegacy(own, other);
        await fixture.Manager.InitializeAsync();

        Assert.Equal("original-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        Assert.True(await fixture.Manager.SetOmniSetting("Private field", "updated-owner-secret", "new-runtime-id", ServiceName));
        Assert.Equal("updated-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        AssertOwnerRows();

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("updated-owner-secret", await restarted.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "next-runtime-id", parentServiceName: ServiceName));
        Assert.Equal("other-service-secret", await restarted.GetStringOmniSetting("Private field", "other-default",
            parentServiceId: "other-service-id", parentServiceName: "Other service"));
        AssertOwnerRows();

        void AssertOwnerRows()
        {
            var rows = ReadRows(fixture.SettingsPath);
            Assert.Equal(2, rows.Count);
            var persistedOwner = Assert.Single(rows.Where(row => row.ParentServiceName == ServiceName));
            Assert.Equal("updated-owner-secret", fixture.ReadPayload(persistedOwner)["Value"]!.Value<string>());
            var persistedOther = Assert.Single(rows.Where(row => row.ParentServiceName == "Other service"));
            Assert.Equal("other-service-secret", fixture.ReadPayload(persistedOther)["Value"]!.Value<string>());
        }
    }

    [Fact]
    public async Task ChangedServiceId_TrustedUpdateSynchronizesEquivalentHistoricalOwnerCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var first = Setting("Private field", "original-owner-secret");
        var second = Setting("Private field", "original-owner-secret");
        second.ParentServiceId = "historical-owner-id";
        fixture.WriteLegacy(first, second);
        await fixture.Manager.InitializeAsync();

        Assert.Equal("original-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        Assert.True(await fixture.Manager.SetOmniSetting("Private field", "updated-owner-secret", "new-runtime-id", ServiceName));
        AssertHistoricalCopies();

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("updated-owner-secret", await restarted.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "next-runtime-id", parentServiceName: ServiceName));
        AssertHistoricalCopies();

        void AssertHistoricalCopies()
        {
            var rows = ReadRows(fixture.SettingsPath);
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "historical-owner-id", ServiceId }, rows.Select(row => row.ParentServiceId).Order(StringComparer.Ordinal));
            Assert.All(rows, row => Assert.Equal("updated-owner-secret", fixture.ReadPayload(row)["Value"]!.Value<string>()));
        }
    }

    [Fact]
    public async Task ChangedServiceId_AliasUpdateRejectsDivergedHistoricalCopiesWithoutWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var first = Setting("Private field", "original-owner-secret");
        var second = Setting("Private field", "original-owner-secret");
        second.ParentServiceId = "historical-owner-id";
        fixture.WriteLegacy(first, second);
        await fixture.Manager.InitializeAsync();
        Assert.Equal("original-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));

        Assert.True(await fixture.Manager.SetOmniSetting("Private field", "explicitly-changed-scope", ServiceId, ServiceName, fulfilledViaApi: true));
        byte[] committed = File.ReadAllBytes(fixture.SettingsPath);
        int notifications = 0;
        fixture.Manager.OnSettingsChanged += (_, _) => notifications++;

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Manager.SetOmniSetting("Private field", "rejected-alias-update", "new-runtime-id", ServiceName));

        Assert.Equal(committed, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(0, notifications);
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);
        Assert.Equal("explicitly-changed-scope", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal("original-owner-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "historical-owner-id", parentServiceName: ServiceName));
        Assert.Equal(committed, File.ReadAllBytes(fixture.SettingsPath));
    }

    [Fact]
    public async Task ApiScopedUpdate_IgnoresTrustedRuntimeAliasAndPreservesHistoricalScope()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteLegacy(Setting("Private field", "historical-scope-secret"));
        await fixture.Manager.InitializeAsync();
        Assert.Equal("historical-scope-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));

        Assert.True(await fixture.Manager.SetOmniSetting("Private field", "new-explicit-scope-secret", "new-runtime-id", ServiceName, fulfilledViaApi: true));
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);
        Assert.Equal("historical-scope-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal("new-explicit-scope-secret", await fixture.Manager.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("historical-scope-secret", await restarted.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal("new-explicit-scope-secret", await restarted.GetStringOmniSetting("Private field", "code-default",
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);
    }

    [Fact]
    public async Task ChangedServiceId_AppendingStringListUpdatesThePreviouslyResolvedOwnerAndSurvivesRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var own = Setting("Private list", "[\"saved-owner-entry\"]", OmniSettingType.StringList);
        var other = Setting("Private list", "[\"other-service-entry\"]", OmniSettingType.StringList);
        other.ParentServiceId = "other-service-id";
        other.ParentServiceName = "Other service";
        fixture.WriteLegacy(own, other);
        await fixture.Manager.InitializeAsync();

        Assert.Equal(new[] { "saved-owner-entry" }, await fixture.Manager.GetStringListOmniSetting("Private list", Array.Empty<string>(),
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        Assert.True(await fixture.Manager.AddToStringListOmniSetting("Private list", "appended-owner-entry", "new-runtime-id", ServiceName));
        Assert.Equal(new[] { "saved-owner-entry", "appended-owner-entry" }, await fixture.Manager.GetStringListOmniSetting("Private list", Array.Empty<string>(),
            parentServiceId: "new-runtime-id", parentServiceName: ServiceName));
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);
        Assert.Single(ReadRows(fixture.SettingsPath).Where(row => row.ParentServiceName == ServiceName));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal(new[] { "saved-owner-entry", "appended-owner-entry" }, await restarted.GetStringListOmniSetting("Private list", Array.Empty<string>(),
            parentServiceId: "next-runtime-id", parentServiceName: ServiceName));
        Assert.Equal(new[] { "other-service-entry" }, await restarted.GetStringListOmniSetting("Private list", Array.Empty<string>(),
            parentServiceId: "other-service-id", parentServiceName: "Other service"));
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);
        Assert.Single(ReadRows(fixture.SettingsPath).Where(row => row.ParentServiceName == ServiceName));
    }

    [Fact]
    public async Task SharedStringList_EmptyDefaultOnNewRegistrationRetainsSavedEntries()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        string[] entries = ["saved-list-entry-a", "saved-list-entry-b", "saved-list-entry-a"];
        var list = Setting("Shared private list", JsonConvert.SerializeObject(entries), OmniSettingType.StringList);
        fixture.WriteLegacy(list);
        await fixture.Manager.InitializeAsync();

        Assert.Equal(entries, await fixture.Manager.GetStringListOmniSetting("Shared private list", Array.Empty<string>(),
            parentServiceId: "new-list-service", parentServiceName: "New list consumer"));
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal(entries, await restarted.GetStringListOmniSetting("Shared private list", Array.Empty<string>(),
            parentServiceId: "next-list-service", parentServiceName: "Next list consumer"));
    }

    [Fact]
    public async Task UnknownCaller_IdenticalSharedSecretsExposeRedactedMetadataWhileDeletionRequiresOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var first = Setting("Shared private field", "same-saved-secret");
        var second = Setting("Shared private field", "same-saved-secret");
        second.ParentServiceId = "second-service-id";
        second.ParentServiceName = "Second service";
        fixture.WriteLegacy(first, second);
        await fixture.Manager.InitializeAsync();

        Assert.Equal("same-saved-secret", await fixture.Manager.GetStringOmniSetting("Shared private field", "code-default",
            parentServiceId: "0", parentServiceName: "UnknownService"));
        var snapshot = fixture.Manager.FindExistingSetting("Shared private field")!;
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, snapshot.Value);
        Assert.True(snapshot.Sensitive);
        Assert.True(snapshot.HasValue);
        Assert.Empty(snapshot.DropdownOptions);
        Assert.DoesNotContain("same-saved-secret", JsonConvert.SerializeObject(snapshot));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Manager.DeleteOmniSetting("Shared private field"));
    }

    [Theory]
    [InlineData(OmniSettingType.String)]
    [InlineData(OmniSettingType.StringList)]
    public async Task ExistingEmptyStringRegistration_RecoversPreviousOwnersValueAndPreservesOtherService(OmniSettingType type)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        string saved = type == OmniSettingType.String ? "saved-owner-secret" : "[\"saved-owner-entry\"]";
        string otherSaved = type == OmniSettingType.String ? "other-service-secret" : "[\"other-service-entry\"]";
        var previous = Setting("Shared private field", saved, type);
        var current = Setting("Shared private field", "", type);
        current.ParentServiceId = "current-service-id";
        var other = Setting("Shared private field", otherSaved, type);
        other.ParentServiceId = "other-service-id";
        other.ParentServiceName = "Other service";
        fixture.WriteLegacy(previous, current, other);
        await fixture.Manager.InitializeAsync();

        await AssertCurrentValue(fixture.Manager);
        Assert.Equal(saved, ReadValueFor(ServiceId));
        Assert.Equal(saved, ReadValueFor("current-service-id"));
        Assert.Equal(otherSaved, ReadValueFor("other-service-id"));
        Assert.Equal(3, ReadRows(fixture.SettingsPath).Count);

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        await AssertCurrentValue(restarted);
        Assert.Equal(saved, ReadValueFor(ServiceId));
        Assert.Equal(otherSaved, ReadValueFor("other-service-id"));

        string ReadValueFor(string serviceId) => fixture.ReadPayload(ReadRows(fixture.SettingsPath)
            .Single(row => row.ParentServiceId == serviceId))["Value"]!.Value<string>()!;

        async Task AssertCurrentValue(OmniGlobalSettingsManager manager)
        {
            if (type == OmniSettingType.String)
                Assert.Equal(saved, await manager.GetStringOmniSetting("Shared private field", "code-default",
                    parentServiceId: "current-service-id", parentServiceName: ServiceName));
            else
                Assert.Equal(new[] { "saved-owner-entry" }, await manager.GetStringListOmniSetting("Shared private field", new[] { "code-default" },
                    parentServiceId: "current-service-id", parentServiceName: ServiceName));
        }
    }

    [Fact]
    public async Task PersistedEmptyStringList_RemainsClearedDespitePopulatedSiblingAndNonemptyDefault()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var previous = Setting("Shared private list", "[\"previous-owner-entry\"]", OmniSettingType.StringList);
        var cleared = Setting("Shared private list", "[]", OmniSettingType.StringList);
        cleared.ParentServiceId = "current-service-id";
        fixture.WriteLegacy(previous, cleared);

        await fixture.Manager.InitializeAsync();
        Assert.Equal("[]", ReadCurrentValue());
        Assert.Empty(await fixture.Manager.GetStringListOmniSetting("Shared private list", new[] { "code-default" },
            parentServiceId: "current-service-id", parentServiceName: ServiceName));
        Assert.Equal("[]", ReadCurrentValue());

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Empty(await restarted.GetStringListOmniSetting("Shared private list", new[] { "code-default" },
            parentServiceId: "current-service-id", parentServiceName: ServiceName));
        Assert.Equal("[]", ReadCurrentValue());
        Assert.Equal("[\"previous-owner-entry\"]", fixture.ReadPayload(ReadRows(fixture.SettingsPath)
            .Single(row => row.ParentServiceId == ServiceId))["Value"]!.Value<string>());

        string ReadCurrentValue() => fixture.ReadPayload(ReadRows(fixture.SettingsPath)
            .Single(row => row.ParentServiceId == "current-service-id"))["Value"]!.Value<string>()!;
    }

    [Fact]
    public async Task LegacyLongIdentityAndLargeDropdownOptions_RetainSavedChoiceOnMigrationAndRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        string name = "Legacy private choice " + new string('n', 3000);
        string serviceId = "legacy-long-id-" + new string('i', 3000);
        string[] options = Enumerable.Range(0, 1200).Select(index => $"private-option-{index:D4}-" + new string('o', 80)).ToArray();
        var dropdown = Setting(name, options[1151], OmniSettingType.Dropdown);
        dropdown.ParentServiceId = serviceId;
        dropdown.DropdownOptions = options.ToList();
        fixture.WriteLegacy(dropdown);

        await fixture.Manager.InitializeAsync();
        Assert.Equal(options, fixture.ReadPayload(Assert.Single(ReadRows(fixture.SettingsPath)))["DropdownOptions"]!.ToObject<string[]>());
        Assert.Equal(options[1151], await fixture.Manager.GetDropdownOmniSetting(name, options[0], options,
            parentServiceId: serviceId, parentServiceName: ServiceName));
        Assert.DoesNotContain(options[1151], File.ReadAllText(fixture.SettingsPath));

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal(options[1151], await restarted.GetDropdownOmniSetting(name, options[0], options,
            parentServiceId: serviceId, parentServiceName: ServiceName));
        Assert.Equal(options, fixture.ReadPayload(Assert.Single(ReadRows(fixture.SettingsPath)))["DropdownOptions"]!.ToObject<string[]>());
    }

    [Fact]
    public async Task EveryLegacySensitiveType_PreservesSavedValuesInsteadOfNewDefaultsOnMigrationAndRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        const string exactString = "  saved private string\nwith unicode \u03bb  ";
        const string exactListJson = "[\"saved-a\",\"saved-b\",\"saved-a\"]";
        string[] options = ["Code default", "Saved option"];
        var dropdown = Setting("Private choice", options[1], OmniSettingType.Dropdown);
        dropdown.DropdownOptions = options.ToList();
        fixture.WriteLegacy(
            Setting("Private text", exactString),
            Setting("Private flag", "False", OmniSettingType.Bool),
            Setting("Private number", "-12345", OmniSettingType.Int),
            dropdown,
            Setting("Private list", exactListJson, OmniSettingType.StringList));

        await fixture.Manager.InitializeAsync();
        Assert.Equal(exactString, fixture.ReadValue("Private text"));
        Assert.Equal("False", fixture.ReadValue("Private flag"));
        Assert.Equal("-12345", fixture.ReadValue("Private number"));
        Assert.Equal("Saved option", fixture.ReadValue("Private choice"));
        Assert.Equal(exactListJson, fixture.ReadValue("Private list"));
        await AssertTypedValues(fixture.Manager);

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        await AssertTypedValues(restarted);
        Assert.All(ReadRows(fixture.SettingsPath), row => Assert.True(OmniSettingsProtector.IsProtected(row.Value)));
        Assert.DoesNotContain("saved private string", File.ReadAllText(fixture.SettingsPath));
        Assert.DoesNotContain("saved-a", File.ReadAllText(fixture.SettingsPath));

        async Task AssertTypedValues(OmniGlobalSettingsManager manager)
        {
            Assert.Equal(exactString, await GetString(manager, "Private text", "new text default"));
            Assert.False(await manager.GetBoolOmniSetting("Private flag", true, parentServiceId: ServiceId, parentServiceName: ServiceName));
            Assert.Equal(-12345, await manager.GetIntOmniSetting("Private number", 98765, parentServiceId: ServiceId, parentServiceName: ServiceName));
            Assert.Equal("Saved option", await manager.GetDropdownOmniSetting("Private choice", options[0], options,
                parentServiceId: ServiceId, parentServiceName: ServiceName));
            Assert.Equal(new[] { "saved-a", "saved-b", "saved-a" }, await manager.GetStringListOmniSetting("Private list", new[] { "new-list-default" },
                parentServiceId: ServiceId, parentServiceName: ServiceName));
        }
    }

    [Fact]
    public async Task LegacyDropdownWithoutOptions_MigrationPreservesTheSavedValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var dropdown = Setting("Private choice", "saved-choice-before-options-registration", OmniSettingType.Dropdown);
        dropdown.DropdownOptions = null!;
        fixture.WriteLegacy(dropdown);

        await fixture.Manager.InitializeAsync();
        Assert.Equal("saved-choice-before-options-registration", fixture.ReadValue("Private choice"));
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("saved-choice-before-options-registration", fixture.ReadValue("Private choice"));
        Assert.Equal("saved-choice-before-options-registration", await restarted.GetDropdownOmniSetting("Private choice", "code-default",
            new[] { "code-default", "saved-choice-before-options-registration" }, parentServiceId: ServiceId, parentServiceName: ServiceName));
    }

    [Fact]
    public async Task LegacyMissingOptionalFieldsAndNamelessRows_DoNotBlockOtherCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var legacy = new JArray(
            new JObject { ["Name"] = "Usable private field", ["Value"] = "saved-usable-secret", ["Sensitive"] = true },
            new JObject { ["Name"] = "Empty private field", ["Value"] = null, ["Sensitive"] = true,
                ["ParentServiceId"] = null, ["ParentServiceName"] = null, ["DropdownOptions"] = null },
            new JObject { ["Value"] = "nameless ignored entry" },
            new JObject { ["Name"] = null, ["Value"] = "null name ignored entry" },
            new JObject { ["Name"] = "  ", ["Value"] = "blank name ignored entry" },
            JValue.CreateNull());
        File.WriteAllText(fixture.SettingsPath, legacy.ToString());

        await fixture.Manager.InitializeAsync();
        Assert.Equal("saved-usable-secret", await fixture.Manager.GetStringOmniSetting("Usable private field", "code-default",
            parentServiceId: "0", parentServiceName: "UnknownService"));
        Assert.Equal("code-default", await fixture.Manager.GetStringOmniSetting("Empty private field", "code-default",
            parentServiceId: "0", parentServiceName: "UnknownService"));
        var empty = fixture.Manager.FindExistingSetting("Empty private field", "0")!;
        Assert.Equal("UnknownService", empty.ParentServiceName);
        Assert.Empty(empty.DropdownOptions);
        Assert.False(empty.HasValue);
        Assert.Equal(2, ReadRows(fixture.SettingsPath).Count);

        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("saved-usable-secret", await restarted.GetStringOmniSetting("Usable private field", "code-default",
            parentServiceId: "0", parentServiceName: "UnknownService"));
    }

    [Fact]
    public async Task FailedMigrationWrite_PreservesTheOriginalLegacyFileAndAllowsRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(useDpapi: true);
        fixture.WriteLegacy(Setting("Private field", "saved-secret-before-failed-migration"));
        byte[] original = File.ReadAllBytes(fixture.SettingsPath);

        using (new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.InitializeAsync());

        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(fixture.KeyDirectory, "settings-*.v1"));
        Assert.Null(fixture.Manager.FindExistingSetting("Private field", ServiceId));
        var restarted = fixture.NewManager();
        await restarted.InitializeAsync();
        Assert.Equal("saved-secret-before-failed-migration", await GetString(restarted, "Private field", "code-default"));
        Assert.DoesNotContain("saved-secret-before-failed-migration", File.ReadAllText(fixture.SettingsPath));
    }

    private static OmniSetting Setting(string name, string? value, OmniSettingType type = OmniSettingType.String) => new()
    {
        Name = name, Value = value!, Type = type, Sensitive = true, ParentServiceId = ServiceId, ParentServiceName = ServiceName
    };

    private static Task<string> GetString(OmniGlobalSettingsManager manager, string name, string defaultValue) =>
        manager.GetStringOmniSetting(name, defaultValue, parentServiceId: ServiceId, parentServiceName: ServiceName);

    private static List<OmniSetting> ReadRows(string path) => JObject.Parse(File.ReadAllText(path))["Settings"]!.ToObject<List<OmniSetting>>()!;

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omnisettings-compatibility-" + Guid.NewGuid().ToString("N"));
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        private readonly List<OmniSettingsProtector> protectors = new();
        private readonly bool useDpapi;
        internal string SettingsDirectory => Path.Combine(root, "settings");
        internal string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        internal string KeyDirectory => Path.Combine(root, "private-keys");
        internal OmniGlobalSettingsManager Manager { get; }

        internal Fixture(bool useDpapi = false)
        {
            this.useDpapi = useDpapi;
            Directory.CreateDirectory(SettingsDirectory);
            Manager = NewManager();
        }

        internal OmniGlobalSettingsManager NewManager()
        {
            var protector = useDpapi ? new OmniSettingsProtector(KeyDirectory) : new OmniSettingsProtector(key);
            protectors.Add(protector);
            return new OmniGlobalSettingsManager(SettingsDirectory, protector);
        }

        internal void WriteLegacy(params OmniSetting[] rows) => File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(rows));
        internal JObject ReadPayload(OmniSetting row) => JObject.Parse(protectors[^1].Unprotect(row.Value, row));
        internal string ReadValue(string name) => ReadPayload(ReadRows(SettingsPath).Single(row => row.Name == name))["Value"]!.Value<string>()!;

        public void Dispose()
        {
            foreach (var protector in protectors) protector.Dispose();
            CryptographicOperations.ZeroMemory(key);
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
