using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Analytics;
using Xunit.Abstractions;

namespace Omnipotent.Tests.Omniscience;

/// <summary>
/// The overview snapshot (13 Nov 2026) rebuilds every 30 s around the clock; each rebuild counted the 5.7M
/// live messages by walking a whole index. Its total now comes from the per-conversation counts that every
/// insert keeps, once their one-off backfill has run, and its person counts from idx_persons_live_tier.
/// </summary>
public sealed class OverviewCountsTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_overview_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;
    private readonly ITestOutputHelper output;
    private const long Now = 1_794_600_000_000;

    public OverviewCountsTests(ITestOutputHelper output)
    {
        this.output = output;
        db = new OmniscienceDb(dbPath);
        db.Migrate();
    }

    [Fact]
    public async Task TheCounters_AreWhatTheOldStatementsGave_BeforeAndAfterTheBackfill()
    {
        Seed(messages: 2_000, conversations: 12, countAsInserted: false);
        // Before the backfill the conversation counts are empty: the total must still be exact.
        Assert.Equal(OldCounts(), Counters(OverviewNow()));

        await new ActivitySummaryStore(db).EnsureBackfilledAsync();
        Seed(messages: 300, conversations: 12, countAsInserted: true, firstId: 2_000); // live ingest after it
        var counters = Counters(OverviewNow());
        Assert.Equal(OldCounts(), counters);
        Assert.Equal(2_300, counters["messages"]);
        Assert.Equal(Scalar("SELECT COUNT(*) FROM persons WHERE tier='tracked' AND merged_into_person_id IS NULL"), counters["tracked"]);
    }

    [Fact]
    public async Task TheMessageTotal_ReadsTheConversationCounts_NotTheMessages()
    {
        Seed(messages: 20_000, conversations: 40, countAsInserted: false);
        await new ActivitySummaryStore(db).EnsureBackfilledAsync();

        int before = Pages(conn => { using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM messages"; cmd.ExecuteScalar(); });
        int after = Pages(conn => Assert.Equal(20_000, JObject.Parse(OmniscienceRoutes.BuildOverviewPayload(conn, Now)).Value<long>("messages")));
        output.WriteLine($"pages read: old message count {before}, whole new overview {after}");
        // Measured: 192 pages for the old message count alone, 11 for the whole new overview. The old count
        // grows with the table: about 55,000 pages for the live 5.7M, every 30 s.
        Assert.True(after * 4 < before, $"new overview {after} pages against {before} for the old message count");
    }

    private string OverviewNow()
    {
        using var conn = db.Open();
        return OmniscienceRoutes.BuildOverviewPayload(conn, Now);
    }

    private static Dictionary<string, long> Counters(string payload) =>
        JObject.Parse(payload).Properties().ToDictionary(p => p.Name, p => p.Value.Value<long>());

    private Dictionary<string, long> OldCounts() => new()
    {
        ["persons"] = Scalar("SELECT COUNT(*) FROM persons WHERE merged_into_person_id IS NULL"),
        ["identities"] = Scalar("SELECT COUNT(*) FROM platform_identities"),
        ["conversations"] = Scalar("SELECT COUNT(*) FROM conversations"),
        ["messages"] = Scalar("SELECT COUNT(*) FROM messages"),
        ["attachments"] = Scalar("SELECT COUNT(*) FROM attachments"),
        ["sources"] = Scalar("SELECT COUNT(*) FROM harvest_sources"),
        ["personality_profiles"] = Scalar("SELECT COUNT(DISTINCT person_id) FROM personality_profiles"),
        ["tracked"] = Scalar("SELECT COUNT(*) FROM persons WHERE tier='tracked' AND merged_into_person_id IS NULL"),
        ["facts_active"] = Scalar("SELECT COUNT(*) FROM person_facts WHERE status='active'"),
        ["relationships"] = Scalar("SELECT COUNT(*) FROM entity_relationships WHERE status='active'"),
        ["radar_alerts_24h"] = Scalar($"SELECT COUNT(*) FROM radar_alerts WHERE occurred_at >= {Now - 86_400_000L}"),
    };

    private void Seed(int messages, int conversations, bool countAsInserted, int firstId = 0)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var setup = conn.CreateCommand())
        {
            setup.Transaction = tx;
            var sql = new System.Text.StringBuilder();
            for (int p = 0; p < 30; p++)
                sql.Append($"INSERT OR IGNORE INTO persons(person_id,display_name,tier,created_at,updated_at,merged_into_person_id) VALUES('p{p}','P{p}','{(p % 4 == 0 ? "tracked" : "archive")}',0,0,{(p % 9 == 8 ? "'p0'" : "NULL")});" +
                           $"INSERT OR IGNORE INTO platform_identities(identity_id,person_id,platform,platform_user_id) VALUES('i{p}','p{p}','discord','u{p}');");
            for (int c = 0; c < conversations; c++)
                sql.Append($"INSERT OR IGNORE INTO conversations(conversation_id,platform,kind,channel_id) VALUES('c{c}','discord','dm','c{c}');");
            setup.CommandText = sql.ToString();
            setup.ExecuteNonQuery();
        }
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = @"INSERT INTO messages(message_id,platform,platform_message_id,conversation_id,author_identity_id,sent_at,content,raw_json,captured_at)
VALUES($id,'discord',$id,$c,$a,$t,'hello there','{""id"":""123456789012345678""}',$t)";
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var conv = insert.Parameters.Add("$c", SqliteType.Text);
        var author = insert.Parameters.Add("$a", SqliteType.Text);
        var at = insert.Parameters.Add("$t", SqliteType.Integer);
        for (int i = firstId; i < firstId + messages; i++)
        {
            id.Value = "discord:" + (100_000_000_000_000_000L + i);
            conv.Value = "c" + i % conversations;
            author.Value = "i" + i % 30;
            at.Value = Now - 1_000_000 + i;
            insert.ExecuteNonQuery();
            if (countAsInserted) ActivitySummaryStore.RecordMessage(conn, tx, (string)author.Value, (string)conv.Value, (long)at.Value, (long)at.Value);
        }
        tx.Commit();
    }

    private int Pages(Action<SqliteConnection> work)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using (var schema = conn.CreateCommand()) { schema.CommandText = "SELECT COUNT(*) FROM sqlite_schema"; schema.ExecuteScalar(); }
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
        work(conn);
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0);
        return pages;
    }

    private long Scalar(string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(dbPath + suffix); } catch { }
    }
}
