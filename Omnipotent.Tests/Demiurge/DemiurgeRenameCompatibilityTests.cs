using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.AccountRegistry;
using Omnipotent.Services.Demiurge;
using Omnipotent.Services.Demiurge.Containers;
using Omnipotent.Services.Demiurge.Discord;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.KliveLLM.Telemetry;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Demiurge;

/// <summary>
/// Projects became Demiurge (and a project a Steward) in November 2026. Everything the live
/// system stored under the old names must keep working after the deploy: records, logs, folders,
/// databases, grants, Docker labels, Discord categories and the names agents remember.
/// </summary>
public sealed class DemiurgeRenameCompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "demiurge-rename-" + Guid.NewGuid().ToString("N"));

    public DemiurgeRenameCompatibilityTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    // ── stored JSON ──

    [Fact]
    public void ARecordSavedBeforeTheRename_LoadsUnderTheNewNames_AndSavesOnlyThem()
    {
        var steward = JsonConvert.DeserializeObject<Steward>("""{"ProjectID":"p-1","Name":"Old","Goal":"Ship it"}""")!;
        Assert.Equal("p-1", steward.StewardID);

        string saved = JsonConvert.SerializeObject(steward);
        Assert.Contains("\"StewardID\":\"p-1\"", saved);
        Assert.DoesNotContain("Project", saved);
    }

    [Fact]
    public void AnEventLoggedBeforeTheRename_KeepsItsSteward_AndReadsItsTypeAsToday()
    {
        var evt = JsonConvert.DeserializeObject<StewardEvent>("""{"Sequence":7,"ProjectID":"p-1","Type":"project-blocked","Text":"x"}""")!;
        Assert.Equal("p-1", evt.StewardID);
        Assert.Equal(StewardEventTypes.StewardBlocked, evt.Type);
        Assert.Equal(StewardEventTypes.StewardFileChanged, DemiurgeLegacyNames.CurrentEventType("project-file-changed"));
        Assert.Contains("project-unblocked", DemiurgeLegacyNames.StoredEventTypes(StewardEventTypes.StewardUnblocked));
    }

    [Fact]
    public void AnEnumStoredByItsPreRenameName_StillReads()
    {
        var evidence = JsonConvert.DeserializeObject<StewardEvidenceReference>("""{"Kind":"ProjectFile","Reference":"a.txt"}""")!;
        Assert.Equal(StewardEvidenceKind.StewardFile, evidence.Kind);
        Assert.Contains("\"StewardFile\"", JsonConvert.SerializeObject(evidence));
    }

    [Fact]
    public void TypesOutsideDemiurge_AreResolvedExactlyAsBefore()
    {
        Assert.False(DemiurgeLegacyNameResolver.Applies(typeof(RegisteredAccount)));
        Assert.True(DemiurgeLegacyNameResolver.Applies(typeof(DesktopContainerRecord)));
        Assert.Null(DemiurgeLegacyNames.LegacyName("Name"));
        Assert.Equal("ProjectPath", DemiurgeLegacyNames.LegacyName("StewardPath"));
    }

    [Fact]
    public void ALifecycleRecordedBeforeTheRename_StillReadsAsAStatusChange()
    {
        var paused = new StewardEvent { Type = StewardEventTypes.Status, Author = "system", Text = "Project paused by Klives." };
        Assert.True(StewardLifecycleEvents.TryReadToStatus(paused, out var status));
        Assert.Equal(StewardStatus.Paused, status);

        var renamed = new StewardEvent { Type = StewardEventTypes.Status, Author = "system", Text = "Steward paused by Klives." };
        Assert.True(StewardLifecycleEvents.TryReadToStatus(renamed, out status));
        Assert.Equal(StewardStatus.Paused, status);

        var structured = new StewardEvent
        {
            Type = StewardEventTypes.Status, Author = "system", Text = "anything",
            PayloadJson = """{"schemaVersion":1,"kind":"project-status-transition","fromStatus":"Active","toStatus":"Archived","reason":"x"}""",
        };
        Assert.True(StewardLifecycleEvents.TryReadToStatus(structured, out status));
        Assert.Equal(StewardStatus.Archived, status);
    }

    // ── the data folder ──

    private (string Legacy, string Current) Folders() => (Path.Combine(root, "SavedData", "Projects"), Path.Combine(root, "SavedData", "Demiurge"));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task TheOldFolder_MovesWithItsFiles_AfterTheDesktopsAreRemoved()
    {
        var (legacy, current) = Folders();
        Write(Path.Combine(legacy, "projects.json"), "[{\"ProjectID\":\"p-1\"}]");
        Write(Path.Combine(legacy, "FileMetadata", "project-files.db"), "db");
        Write(Path.Combine(legacy, "FileMetadata", "project-files.db-wal"), "wal");
        Write(Path.Combine(legacy, "containers.json"), "[{\"ContainerID\":\"abc\"}]");
        Write(Path.Combine(legacy, "Volumes", "p-1", "notes.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(current, "EventLog")); // an empty skeleton made earlier
        int removals = 0;

        var outcome = await DemiurgeStorageMigration.RunAsync(legacy, current,
            _ => { removals++; return Task.FromResult(3); }, _ => { }, retryStep: TimeSpan.Zero);

        Assert.True(outcome.Moved);
        Assert.False(outcome.UsingLegacyFolder);
        Assert.Equal(3, outcome.ContainersRemoved);
        Assert.Equal(1, removals);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("[{\"ProjectID\":\"p-1\"}]", File.ReadAllText(Path.Combine(current, "stewards.json")));
        Assert.True(File.Exists(Path.Combine(current, "FileMetadata", "steward-files.db")));
        Assert.True(File.Exists(Path.Combine(current, "FileMetadata", "steward-files.db-wal")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(current, "Volumes", "p-1", "notes.txt")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(current, "containers.json")));
        Assert.True(Directory.Exists(Path.Combine(current, "Councils")));
    }

    [Fact]
    public async Task WhenDockerCannotBeAsked_TheFolderStillMoves()
    {
        var (legacy, current) = Folders();
        Write(Path.Combine(legacy, "projects.json"), "[]");
        string? logged = null;

        var outcome = await DemiurgeStorageMigration.RunAsync(legacy, current,
            _ => throw new InvalidOperationException("no daemon"), message => logged = message, retryStep: TimeSpan.Zero);

        Assert.True(outcome.Moved);
        Assert.Contains("no daemon", logged);
    }

    [Fact]
    public async Task AFolderThatCannotMove_StaysUsable_UnderTodaysFileNames()
    {
        if (!OperatingSystem.IsWindows()) return; // an open handle only blocks a directory rename on Windows
        var (legacy, current) = Folders();
        Write(Path.Combine(legacy, "projects.json"), "[]");
        Write(Path.Combine(legacy, "EventLog", "p-1.log.jsonl"), "{}");

        DemiurgeStorageMigration.Outcome outcome;
        using (new FileStream(Path.Combine(legacy, "EventLog", "p-1.log.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = await DemiurgeStorageMigration.RunAsync(legacy, current, _ => Task.FromResult(0), _ => { }, retryStep: TimeSpan.Zero);

        Assert.False(outcome.Moved);
        Assert.True(outcome.UsingLegacyFolder);
        Assert.Equal(legacy, outcome.Root);
        Assert.NotNull(outcome.Problem);
        Assert.True(File.Exists(Path.Combine(legacy, "stewards.json")));
        Assert.False(File.Exists(Path.Combine(legacy, "projects.json")));
    }

    [Fact]
    public async Task AFolderAlreadyInUse_IsKept_AndTheOldOneLeftAlone()
    {
        var (legacy, current) = Folders();
        Write(Path.Combine(legacy, "projects.json"), "[\"old\"]");
        Write(Path.Combine(current, "stewards.json"), "[\"new\"]");
        bool asked = false;

        var outcome = await DemiurgeStorageMigration.RunAsync(legacy, current,
            _ => { asked = true; return Task.FromResult(0); }, _ => { }, retryStep: TimeSpan.Zero);

        Assert.False(outcome.Moved);
        Assert.Equal(current, outcome.Root);
        Assert.False(asked);
        Assert.Equal("[\"old\"]", File.ReadAllText(Path.Combine(legacy, "projects.json")));
    }

    [Fact]
    public async Task AFreshHost_GetsTheNewFolderLaidOut()
    {
        var (legacy, current) = Folders();
        var outcome = await DemiurgeStorageMigration.RunAsync(legacy, current, _ => Task.FromResult(0), _ => { });
        Assert.Equal(current, outcome.Root);
        Assert.True(Directory.Exists(Path.Combine(current, "Volumes")));
        Assert.False(Directory.Exists(legacy));
    }

    [Theory]
    [InlineData(@"C:\serverBuild\SavedData\Projects\Volumes\p-1", true)]
    [InlineData("/run/desktop/mnt/host/c/serverBuild/SavedData/Projects/Volumes/p-1/.klive/agent-runtime/shared", true)]
    [InlineData(@"C:\serverBuild\SavedData\Demiurge\Volumes\p-1", false)]
    [InlineData(@"C:\Projects\Software\Omnipotent\notes", false)]
    public void ADesktopMountingTheOldFolder_IsRecognised(string source, bool expected)
        => Assert.Equal(expected, DemiurgeStorageMigration.MountsLegacyFolder(new[] { source }));

    // ── Docker ──

    [Fact]
    public void DesktopsAndImagesLabelledBeforeTheRename_AreStillOurs()
    {
        var legacy = new Dictionary<string, string>
        {
            [ContainerLabels.Owner] = ContainerLabels.LegacyOwnerValue,
            [ContainerLabels.LegacyStewardID] = "p-1",
            [ContainerLabels.LegacyContextHash] = "abc",
        };
        Assert.True(ContainerLabels.IsOwnedByDemiurge(legacy));
        Assert.True(ContainerLabels.TryGet(legacy, ContainerLabels.StewardID, out var id));
        Assert.Equal("p-1", id);
        Assert.True(ContainerLabels.TryGet(legacy, ContainerLabels.ContextHash, out var hash));
        Assert.Equal("abc", hash);
        Assert.False(ContainerLabels.IsOwnedByDemiurge(new Dictionary<string, string> { [ContainerLabels.Owner] = "kliverag-searxng" }));
    }

    [Fact]
    public void SettingsThatNameTheOldDesktopImage_UseTheCurrentTag()
    {
        var settings = JsonConvert.DeserializeObject<StewardSettings>("""{"DesktopImage":"omnipotent/projects-desktop:latest"}""")!;
        Assert.Equal(StewardSettings.Defaults.DesktopImage, settings.DesktopImage);
        Assert.Equal(StewardSettings.Defaults.LegacyDesktopImage, StewardSettings.Defaults.LegacyDesktopImageFor(StewardSettings.Defaults.DesktopImage));
        settings.DesktopImage = "custom/image:1";
        Assert.Equal("custom/image:1", settings.DesktopImage);
    }

    [Fact]
    public void AHostConfiguredWithTheOldVariableNames_KeepsItsSetting()
    {
        string suffix = "RENAME_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        try
        {
            Environment.SetEnvironmentVariable("PROJECTS_" + suffix, "1");
            Assert.Equal("1", DemiurgeLegacyNames.Environment(suffix));
            Environment.SetEnvironmentVariable("DEMIURGE_" + suffix, "2");
            Assert.Equal("2", DemiurgeLegacyNames.Environment(suffix));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROJECTS_" + suffix, null);
            Environment.SetEnvironmentVariable("DEMIURGE_" + suffix, null);
        }
    }

    // ── what agents remember ──

    [Theory]
    [InlineData("update_project", "update_steward")]
    [InlineData("cross_project_search", "cross_steward_search")]
    [InlineData("list_project_directives", "list_steward_directives")]
    public void AToolNamedAsBeforeTheRename_ReachesTodaysTool(string remembered, string canonical)
        => Assert.Equal(canonical, StewardToolFacade.Unfold(remembered, "{}").ToolName);

    // ── access ──

    [Fact]
    public void AGrantUnderTheOldServiceKey_MovesToTheNewOne()
    {
        var profile = new KMProfile
        {
            UserID = "u1", Name = "Helper", Rank = ProfileRank.Admin, CanLogin = true,
            SchemaVersion = KMProfileManager.CurrentSchemaVersion,
            Grants = new List<PermissionGrant>
            {
                new() { Key = "projects.details.read", GrantedUtc = DateTime.UtcNow },
                new() { Key = "projects.overview.view", GrantedUtc = DateTime.UtcNow },
                new() { Key = "demiurge.overview.view", GrantedUtc = DateTime.UtcNow },
                new() { Key = "stratum.projects.read", GrantedUtc = DateTime.UtcNow },
            },
        };

        var (migrated, changed, note, _) = KMProfileManager.MigrateProfile(profile, PermissionCatalog.All, null, DateTime.UtcNow);

        Assert.True(changed);
        Assert.Contains("projects.details.read", note);
        Assert.Equal(new[] { "demiurge.details.read", "demiurge.overview.view", "stratum.projects.read" },
            migrated.Grants.Select(g => g.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(AccessEvaluator.Can(migrated, DemiurgePerms.DetailsRead));
        Assert.False(KMProfileManager.MigrateProfile(migrated, PermissionCatalog.All, null, DateTime.UtcNow).Changed);
    }

    [Fact]
    public void AnAccountOwnedAsAProject_IsOwnedByTheSameSteward()
    {
        string index = Path.Combine(root, "accounts", "accounts.json");
        Write(index, JsonConvert.SerializeObject(new[]
        {
            new RegisteredAccount { AccountID = "a1", ServiceKey = "github.com", CreatedBy = "project:p-1", Owners = new() { "project:p-1", "KliveAgent" } },
        }));
        var store = new AccountRegistryStore(_ => { }, index, Path.Combine(root, "accounts", "root.key"));

        var account = store.Get("a1")!;
        Assert.Equal("steward:p-1", account.CreatedBy);
        Assert.Equal(new[] { "steward:p-1", "KliveAgent" }, account.Owners);
        Assert.False(store.AddOwner("a1", "steward:p-1"));
    }

    // ── databases ──

    [Fact]
    public void ASharedFileDatabaseFromBeforeTheRename_IsRenamedInPlace()
    {
        string path = Path.Combine(root, "steward-files.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Exec(conn, """
                CREATE TABLE file_entries (file_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, path TEXT NOT NULL, UNIQUE(project_id,path));
                CREATE INDEX idx_project_files_path ON file_entries(project_id,path);
                CREATE TABLE file_events (event_id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL);
                CREATE TABLE upload_sessions (session_id TEXT PRIMARY KEY, project_id TEXT NULL);
                INSERT INTO file_entries VALUES ('f1','p-1','notes.txt');
                """);
            StewardFileStore.RenameLegacySchema(conn);
            StewardFileStore.RenameLegacySchema(conn); // idempotent

            Assert.Equal("notes.txt", Scalar(conn, "SELECT path FROM file_entries WHERE steward_id='p-1'"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE name='idx_project_files_path'"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('upload_sessions') WHERE name='project_id'"));
        }
    }

    [Fact]
    public void ARetrievalIndexFromBeforeTheRename_KeepsItsRows_AndSearchesThem()
    {
        string path = Path.Combine(root, "retrieval.sqlite");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Exec(conn, """
                CREATE TABLE project_events (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                    event_id TEXT NOT NULL, type TEXT NOT NULL, timestamp_utc TEXT NOT NULL, body TEXT NOT NULL, snippet TEXT NOT NULL,
                    UNIQUE(project_id, sequence));
                CREATE INDEX ix_project_events_sequence ON project_events(project_id, sequence);
                CREATE TABLE project_retrieval_cursors (project_id TEXT PRIMARY KEY, contiguous_sequence INTEGER NOT NULL DEFAULT 0);
                CREATE VIRTUAL TABLE project_event_fts USING fts5(body, content='project_events', content_rowid='id');
                CREATE TRIGGER project_events_ai AFTER INSERT ON project_events BEGIN
                    INSERT INTO project_event_fts(rowid, body) VALUES (new.id, new.body);
                END;
                INSERT INTO project_events(project_id, sequence, event_id, type, timestamp_utc, body, snippet)
                    VALUES ('legacy-steward', 1, 'e1', 'status', '2026-11-01T00:00:00.0000000Z', 'quarterly zebra migration report', 'zebra');
                INSERT INTO project_retrieval_cursors VALUES ('legacy-steward', 1);
                """);
        }

        var index = new StewardRetrievalIndex(new StewardEventLogStore(_ => { }), path);
        var hits = index.Search("legacy-steward", "zebra", 5);

        Assert.Single(hits);
        Assert.Equal("e1", hits[0].EventID);
    }

    [Fact]
    public void TheLedger_MovesARenamedServicesHistoryAcross()
    {
        var db = new LlmTelemetryDb(Path.Combine(root, "ledger.db"));
        db.Migrate();
        using (var conn = db.Open())
        {
            Exec(conn, """
                PRAGMA user_version = 1;
                INSERT INTO calls (started_ms, ended_ms, service, lane, kind, router, stream, outcome, attempts, prompt, cached, cache_write,
                    completion, reasoning, cache_metrics, billing, queue_ms, provider_ms, total_ms, request_bytes, tool_calls, flags, epoch_turn, revision)
                    VALUES (1, 2, 'Projects', 'commander', 'chat', 'airouter', 0, 'ok', 1, 10, 0, 0, 5, 0, 0, 'flat', 0, 1, 1, 10, 0, 0, 0, 0);
                """);
        }
        db.Migrate();
        using var check = db.Open();
        Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM calls WHERE service='Demiurge'"));
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM calls WHERE service='Projects'"));
        Assert.Equal(2L, Scalar(check, "PRAGMA user_version"));
    }

    [Fact]
    public void RoutingPoliciesSavedForTheOldService_MoveToTheNewName_WithoutOverwritingNewOnes()
    {
        var doc = new LlmRoutingDocument();
        doc.Services["Projects"] = new LlmRoutePolicy { Router = "openrouter" };
        doc.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = "airouter" };
        doc.Lanes["Projects/council"] = new LlmRoutePolicy { Router = "openrouter" };
        doc.Lanes["Demiurge/council"] = new LlmRoutePolicy { Router = "airouter" };

        doc.Normalize();

        Assert.Equal("openrouter", doc.ServicePolicy("Demiurge")!.Router);
        Assert.Equal("airouter", doc.LanePolicy("Demiurge", "commander")!.Router);
        Assert.Equal("airouter", doc.LanePolicy("Demiurge", "council")!.Router);
        Assert.Null(doc.ServicePolicy("Projects"));
    }

    // ── Discord ──

    [Fact]
    public void EveryStatusCategory_KnowsTheNameItHadBefore()
    {
        Assert.Equal("Active Projects", StewardDiscordManager.LegacyCategoryNameFor(StewardDiscordManager.ActiveCategoryName));
        Assert.Equal("Paused Projects", StewardDiscordManager.LegacyCategoryNameFor(StewardDiscordManager.PausedCategoryName));
        Assert.Equal("Archived Projects", StewardDiscordManager.LegacyCategoryNameFor(StewardDiscordManager.ArchivedCategoryName));
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
