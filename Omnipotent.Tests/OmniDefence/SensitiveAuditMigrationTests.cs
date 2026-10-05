using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence;

public sealed class SensitiveAuditMigrationTests
{
    [Fact]
    public async Task Startup_ScrubsMultipleBatchesAndKeepsUnrelatedAuditRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sensitive-audit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "audit.db");
        var initial = new OmniDefenceStore(path);
        OmniDefenceStore? migrated = null;
        OmniDefenceStore? repeated = null;
        try
        {
            await initial.InitializeAsync();
            using (var insert = initial.Connection.CreateCommand())
            {
                insert.CommandText = @"WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n<503)
                    INSERT INTO requests (utc_ts, route, query, body_text, body_hash, body_length, body_truncated,
                        headers_json, user_agent, client_page, ip, profile_id, status_code)
                    SELECT 1, '/omniGLOBALsettings/Set', 'secret-query', 'secret-body', 'secret-hash', 11, 1,
                        'secret-header', 'secret-agent', 'secret-page', '127.0.0.1', 'admin', 200 FROM rows";
                await insert.ExecuteNonQueryAsync();
            }
            await initial.InsertRequestAsync(new RequestRow { Route = "/ordinary", BodyText = "keep", BodyHash = "keep-hash", BodyLength = 4 });
            await initial.ShutdownAsync();
            await initial.Connection.DisposeAsync();

            migrated = new OmniDefenceStore(path);
            await migrated.InitializeAsync();
            Assert.True(migrated.SensitiveAuditMigrationChangedRows);
            var rows = await migrated.QueryAsync("SELECT * FROM requests ORDER BY id", new());
            Assert.Equal(504, rows.Count);
            foreach (var row in rows.Take(503))
            {
                AssertPayloadRemoved(row);
                Assert.Equal("admin", row["profile_id"]);
                Assert.Equal("127.0.0.1", row["ip"]);
                Assert.Equal(200L, row["status_code"]);
            }
            Assert.Equal("keep", rows.Last()["body_text"]);
            Assert.Equal("keep-hash", rows.Last()["body_hash"]);
            await migrated.ShutdownAsync();
            await migrated.Connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Assert.DoesNotContain("secret-body", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            if (File.Exists(path + "-wal")) Assert.Equal(0, new FileInfo(path + "-wal").Length);

            repeated = new OmniDefenceStore(path);
            await repeated.InitializeAsync();
            Assert.False(repeated.SensitiveAuditMigrationChangedRows);
        }
        finally
        {
            await CloseAsync(repeated);
            await CloseAsync(migrated);
            await CloseAsync(initial);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("[{\"path\":\"/OmniGlobalSettings/List?revealSensitive=true\"}]")]
    [InlineData("[{\"path\":\"\\/\\u004fmniGlobalSettings\\/Get?name=secret\"}]")]
    [InlineData("[{\"path\":\"/batch\",\"nested\":[\"OmniGlobalSettings/List\"]}]")]
    [InlineData("[{\"path\":\"/Omni")]
    public async Task Startup_ScrubsLegacyNestedEncodedOrTruncatedSettingsBatchBodies(string body)
    {
        string directory = Path.Combine(Path.GetTempPath(), "batch-audit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "audit.db");
        var initial = new OmniDefenceStore(path);
        OmniDefenceStore? migrated = null;
        try
        {
            await initial.InitializeAsync();
            using (var insert = initial.Connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO requests (utc_ts,route,body_text,body_hash,body_length,query,headers_json) VALUES (1,'/batch',$body,'hash',12,'query','headers')";
                insert.Parameters.AddWithValue("$body", body);
                await insert.ExecuteNonQueryAsync();
            }
            await initial.InsertRequestAsync(new RequestRow { Route = "/batch", BodyText = "[{\"path\":\"/ordinary\"}]", BodyHash = "keep" });
            await initial.ShutdownAsync();
            await initial.Connection.DisposeAsync();

            migrated = new OmniDefenceStore(path);
            await migrated.InitializeAsync();
            var rows = await migrated.QueryAsync("SELECT * FROM requests ORDER BY id", new());
            AssertPayloadRemoved(rows[0]);
            Assert.Equal("[{\"path\":\"/ordinary\"}]", rows[1]["body_text"]);
            Assert.Equal("keep", rows[1]["body_hash"]);
        }
        finally
        {
            await CloseAsync(migrated);
            await CloseAsync(initial);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SnapshotCleanup_RemovesLegacyGeneratedFilesAndKeepsCurrentAndUnrelatedFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "snapshot-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "requests-old.json"), "{\"Json\":\"secret\"}");
            File.WriteAllText(Path.Combine(directory, "ip-old.json.random.tmp"), "secret");
            File.WriteAllText(Path.Combine(directory, "requests-current.json"), "{\"SecurityVersion\":1,\"Json\":\"safe\"}");
            File.WriteAllText(Path.Combine(directory, "unrelated.json"), "preserve");
            var cache = new OmniDefenceReadSnapshotCache(directory);

            cache.RemoveLegacyAuditSnapshots();

            Assert.False(File.Exists(Path.Combine(directory, "requests-old.json")));
            Assert.False(File.Exists(Path.Combine(directory, "ip-old.json.random.tmp")));
            Assert.True(File.Exists(Path.Combine(directory, "requests-current.json")));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(directory, "unrelated.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertPayloadRemoved(Dictionary<string, object?> row)
    {
        foreach (string key in new[] { "query", "body_text", "body_hash", "headers_json", "user_agent", "client_page" })
            Assert.Null(row[key]);
        Assert.Equal(0L, row["body_length"]);
        Assert.Equal(0L, row["body_truncated"]);
    }

    private static async Task CloseAsync(OmniDefenceStore? store)
    {
        if (store == null) return;
        await store.ShutdownAsync();
        await store.Connection.DisposeAsync();
    }
}
