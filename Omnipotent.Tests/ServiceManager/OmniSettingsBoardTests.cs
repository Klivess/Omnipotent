using System.Diagnostics;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Telemetry;
using Xunit.Abstractions;

namespace Omnipotent.Tests.ServiceManager;

/// <summary>
/// What the OmniSettings page reads (/OmniGlobalSettings/Board): the code's default beside the stored value,
/// when a service last read a setting and who last changed it, which settings a service is blocked on, and
/// the spans that time a save. Secrets stay out of all of it.
/// </summary>
public sealed class OmniSettingsBoardTests
{
    private const string ServiceId = "board-service";
    private const string ServiceName = "Board test service";
    private readonly ITestOutputHelper output;

    public OmniSettingsBoardTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Board_ShowsTheCodeDefault_AndWhetherTheStoredValueStillMatchesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manager = fixture.Manager;
        await manager.InitializeAsync();

        Assert.Equal(30, await manager.GetIntOmniSetting("PollSeconds", 30, parentServiceId: ServiceId, parentServiceName: ServiceName));
        var row = Row(manager, "PollSeconds");
        Assert.Equal("30", (string)row["DefaultValue"]!);
        Assert.True((bool)row["MatchesDefault"]!);
        Assert.Equal(ServiceName, (string)row["UpdatedBy"]!);
        Assert.NotNull(row["UpdatedUtc"]);

        Assert.True(await manager.SetOmniSetting("PollSeconds", "45", ServiceId, ServiceName, OmniSettingType.Int, fulfilledViaApi: true, changedBy: "Klives"));
        row = Row(manager, "PollSeconds");
        Assert.False((bool)row["MatchesDefault"]!);
        Assert.Equal("Klives", (string)row["UpdatedBy"]!);

        // A new default in code never replaces a stored value. The page is where that gap shows.
        Assert.Equal(45, await manager.GetIntOmniSetting("PollSeconds", 60, parentServiceId: ServiceId, parentServiceName: ServiceName));
        row = Row(manager, "PollSeconds");
        Assert.Equal("60", (string)row["DefaultValue"]!);
        Assert.False((bool)row["MatchesDefault"]!);
        Assert.Equal("Klives", (string)row["UpdatedBy"]!); // a read is not a change

        // A bool default arrives as "True" and the page saves "true": the same value.
        Assert.True(await manager.GetBoolOmniSetting("Enabled", true, parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.True(await manager.SetOmniSetting("Enabled", "true", ServiceId, ServiceName, OmniSettingType.Bool, fulfilledViaApi: true, changedBy: "Klives"));
        Assert.True((bool)Row(manager, "Enabled")["MatchesDefault"]!);

        // A list compares by its entries, not its spacing.
        await manager.GetStringListOmniSetting("Slots", new[] { "a", "b" }, parentServiceId: ServiceId, parentServiceName: ServiceName);
        Assert.True(await manager.SetOmniSetting("Slots", "[ \"a\", \"b\" ]", ServiceId, ServiceName, OmniSettingType.StringList, fulfilledViaApi: true));
        Assert.True((bool)Row(manager, "Slots")["MatchesDefault"]!);
        Assert.False(OmniGlobalSettingsManager.SameSettingValue(OmniSettingType.StringList, "[\"a\"]", "[\"a\",\"b\"]"));
        Assert.True(OmniGlobalSettingsManager.SameSettingValue(OmniSettingType.Int, "007", "7"));
        Assert.True(OmniGlobalSettingsManager.SameSettingValue(OmniSettingType.Dropdown, "medium", "Medium"));
        Assert.False(OmniGlobalSettingsManager.SameSettingValue(OmniSettingType.String, "Medium", "medium"));
    }

    [Fact]
    public async Task Board_NeverCarriesASecretOrItsDefault()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manager = fixture.Manager;
        await manager.InitializeAsync();

        // "API Key" makes it a secret by name, so even the code's default is stored encrypted.
        await manager.GetStringOmniSetting("Vendor API Key", "default-secret-must-not-leak", parentServiceId: ServiceId, parentServiceName: ServiceName);
        Assert.True(await manager.SetOmniSetting("Vendor API Key", "replacement-secret-must-not-leak", ServiceId, ServiceName,
            fulfilledViaApi: true, sensitive: true, changedBy: "Klives"));
        await manager.GetStringOmniSetting("Vendor API Key", "default-secret-must-not-leak", parentServiceId: ServiceId, parentServiceName: ServiceName);

        Assert.DoesNotContain("must-not-leak", manager.BuildBoardJson());
        var row = Row(manager, "Vendor API Key");
        Assert.True((bool)row["Sensitive"]!);
        Assert.Equal(OmniGlobalSettingsManager.SecretMask, (string)row["Value"]!);
        Assert.Null(row["DefaultValue"]);
        Assert.Null(row["MatchesDefault"]);
        Assert.Null(row["DropdownOptions"]);
        Assert.Equal("Klives", (string)row["UpdatedBy"]!);
        Assert.True((bool)row["ReadThisSession"]!);
    }

    [Fact]
    public async Task ReadThisSession_CountsServiceReads_NotSavesFromThePage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manager = fixture.Manager;
        await manager.InitializeAsync();

        Assert.True(await manager.SetOmniSetting("Fresh", "typed-by-hand", ServiceId, ServiceName, fulfilledViaApi: true, changedBy: "Klives"));
        var row = Row(manager, "Fresh");
        Assert.False((bool)row["ReadThisSession"]!);
        Assert.Null(row["LastReadUtc"]);
        Assert.Null(row["DefaultValue"]);

        await manager.GetStringOmniSetting("Fresh", "from-code", parentServiceId: ServiceId, parentServiceName: ServiceName);
        row = Row(manager, "Fresh");
        Assert.True((bool)row["ReadThisSession"]!);
        Assert.NotNull(row["LastReadUtc"]);
        Assert.Equal("from-code", (string)row["DefaultValue"]!);
        Assert.False((bool)row["MatchesDefault"]!);
    }

    [Fact]
    public async Task ReadTimes_AreWrittenCoarsely_AndOutliveARestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var t0 = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var manager = fixture.Manager;
        manager.UtcNow = () => clock;
        manager.LastReadFlushDelay = TimeSpan.FromDays(1); // flushed by hand below
        await manager.InitializeAsync();

        // Created by its first read: the stamp goes out with that write.
        await manager.GetIntOmniSetting("Interval", 5, parentServiceId: ServiceId, parentServiceName: ServiceName);
        Assert.Equal(t0, StoredOnDisk(fixture, "Interval")["LastReadUtc"]!.Value<DateTime>());

        // Read again within the interval: the page sees it at once; the file is not rewritten.
        clock = t0.AddHours(1);
        await manager.GetIntOmniSetting("Interval", 5, parentServiceId: ServiceId, parentServiceName: ServiceName);
        Assert.Equal(clock, Row(manager, "Interval")["LastReadUtc"]!.Value<DateTime>());
        Assert.Equal(t0, StoredOnDisk(fixture, "Interval")["LastReadUtc"]!.Value<DateTime>());

        // Past the interval: stamped in memory, written in one batch.
        clock = t0.AddHours(7);
        await manager.GetIntOmniSetting("Interval", 5, parentServiceId: ServiceId, parentServiceName: ServiceName);
        Assert.Equal(t0, StoredOnDisk(fixture, "Interval")["LastReadUtc"]!.Value<DateTime>());
        Assert.True(await manager.FlushLastReadsAsync());
        Assert.Equal(t0.AddHours(7), StoredOnDisk(fixture, "Interval")["LastReadUtc"]!.Value<DateTime>());

        // After a restart nothing has read it yet, but its last read is still known, and so is when tracking began.
        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        var row = Row(reloaded, "Interval");
        Assert.False((bool)row["ReadThisSession"]!);
        Assert.Equal(t0.AddHours(7), row["LastReadUtc"]!.Value<DateTime>());
        Assert.Equal(t0, Board(reloaded)["ReadTrackingSinceUtc"]!.Value<DateTime>());
    }

    [Fact]
    public async Task AServiceWaitingForASecret_IsShownUntilTheValueArrives()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manager = fixture.Manager;
        var notices = new List<string>();
        manager.SensitiveNoticeOverride = message => { lock (notices) notices.Add(message); return Task.CompletedTask; };
        await manager.InitializeAsync();

        var waiting = manager.GetStringOmniSetting("Broker Secret", null, sensitive: true, askKlivesForFulfillment: true,
            parentServiceId: ServiceId, parentServiceName: ServiceName);
        await WaitUntil(() => FindRow(manager, "Broker Secret")?["Awaiting"]?.Value<bool>() == true);
        Assert.False(waiting.IsCompleted);
        Assert.Single(notices);
        Assert.NotNull(Row(manager, "Broker Secret")["AwaitingSinceUtc"]);

        Assert.True(await manager.SetOmniSetting("Broker Secret", "supplied-secret-value", ServiceId, ServiceName,
            fulfilledViaApi: true, sensitive: true, changedBy: "Klives"));
        Assert.Equal("supplied-secret-value", await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(Row(manager, "Broker Secret")["Awaiting"]);
        Assert.DoesNotContain("supplied-secret-value", manager.BuildBoardJson());
    }

    [Fact]
    public async Task ChangeStamps_ArePersistedBesideProtectedValues_WithoutTouchingTheirEncryption()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manager = fixture.Manager;
        await manager.InitializeAsync();

        Assert.True(await manager.SetOmniSetting("Signing Key", "stamped-secret-value", ServiceId, ServiceName,
            fulfilledViaApi: true, sensitive: true, changedBy: "Klives"));
        Assert.DoesNotContain("stamped-secret-value", File.ReadAllText(fixture.SettingsPath));
        var stored = StoredOnDisk(fixture, "Signing Key");
        Assert.Equal("Klives", (string)stored["UpdatedBy"]!);
        Assert.True(OmniSettingsProtector.IsProtected((string)stored["Value"]!));

        var reloaded = fixture.NewManager();
        await reloaded.InitializeAsync();
        Assert.Equal("stamped-secret-value", await reloaded.GetStringOmniSetting("Signing Key", parentServiceId: ServiceId, parentServiceName: ServiceName));
        Assert.Equal("Klives", (string)Row(reloaded, "Signing Key")["UpdatedBy"]!);
        Assert.NotNull(Row(reloaded, "Signing Key")["UpdatedUtc"]);

        // A setting written by an earlier build has no stamps: the board leaves them out rather than inventing them.
        var legacyRow = Row(await LegacyManagerWith(fixture, "Old Setting"), "Old Setting");
        Assert.Null(legacyRow["UpdatedUtc"]);
        Assert.Null(legacyRow["UpdatedBy"]);
        Assert.Null(legacyRow["LastReadUtc"]);
    }

    [Fact]
    public async Task ASave_IsTimedStepByStepOnItsRequestTrace()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The real key directory and ACLs, as on the server.
        using var fixture = new Fixture(useDpapi: true);
        var manager = fixture.Manager;
        await manager.InitializeAsync();
        // A store the size of the live one: 264 settings, about one in six secret.
        for (int i = 0; i < 264; i++)
            Assert.True(await manager.SetOmniSetting($"Setting{i:000}", $"value-{i}", ServiceId, ServiceName, sensitive: i % 6 == 0));

        var saves = new List<double>();
        var perStep = new Dictionary<string, double>();
        for (int run = 0; run < 10; run++)
        {
            var trace = RequestTrace.StartNow(TelemetryStage.Handler);
            RequestTrace.Current = trace;
            var clock = Stopwatch.StartNew();
            Assert.True(await manager.SetOmniSetting("Setting001", $"changed-{run}", ServiceId, ServiceName, fulfilledViaApi: true, changedBy: "Klives"));
            saves.Add(clock.Elapsed.TotalMilliseconds);
            RequestTrace.Current = null;
            for (int i = 0; i < trace.SpanCount; i++)
                perStep[trace.SpanName(i)] = perStep.GetValueOrDefault(trace.SpanName(i)) + RequestTrace.TicksToMs(trace.SpanTicks(i));
        }

        foreach (string step in new[] { "settings-gate", "settings-marker", "settings-io-wait", "settings-acl", "settings-serialize", "settings-write", "settings-replace" })
            Assert.Contains(step, perStep.Keys);
        var sorted = saves.Order().ToList();
        output.WriteLine($"save: median {sorted[sorted.Count / 2]:F1} ms, max {sorted[^1]:F1} ms (this machine, not the server)");
        foreach (var (step, total) in perStep.OrderByDescending(entry => entry.Value))
            output.WriteLine($"  {step,-20} {total / saves.Count,8:F2} ms a save");
    }

    // ── helpers ──

    private static JObject Board(OmniGlobalSettingsManager manager) => JObject.Parse(manager.BuildBoardJson());

    private static JObject Row(OmniGlobalSettingsManager manager, string name) =>
        FindRow(manager, name) ?? throw new KeyNotFoundException(name);

    private static JObject? FindRow(OmniGlobalSettingsManager manager, string name) =>
        Board(manager)["Settings"]!.Cast<JObject>().SingleOrDefault(row => (string)row["Name"]! == name);

    private static JObject StoredOnDisk(Fixture fixture, string name) =>
        JObject.Parse(File.ReadAllText(fixture.SettingsPath))["Settings"]!.Cast<JObject>().Single(row => (string)row["Name"]! == name);

    /// <summary>A store written before change stamps existed: the earlier array format, no metadata.</summary>
    private static async Task<OmniGlobalSettingsManager> LegacyManagerWith(Fixture fixture, string name)
    {
        var legacy = new Fixture();
        fixture.Adopt(legacy);
        legacy.WriteLegacy(new OmniSetting { Name = name, Type = OmniSettingType.String, Value = "kept", ParentServiceId = ServiceId, ParentServiceName = ServiceName });
        await legacy.Manager.InitializeAsync();
        return legacy.Manager;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omnisettings-board-" + Guid.NewGuid().ToString("N"));
        private readonly OmniSettingsProtector protector;
        private readonly List<Fixture> adopted = new();
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
        internal void WriteLegacy(params OmniSetting[] settings) => File.WriteAllText(SettingsPath, Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        /// <summary>Disposes another fixture along with this one.</summary>
        internal void Adopt(Fixture other) => adopted.Add(other);

        public void Dispose()
        {
            foreach (var other in adopted) other.Dispose();
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
