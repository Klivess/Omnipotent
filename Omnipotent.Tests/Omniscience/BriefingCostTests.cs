using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Scheduling;
using Xunit.Abstractions;

namespace Omnipotent.Tests.Omniscience;

/// <summary>
/// The briefing preview (13 Nov 2026). Live over seven days <c>GET /omniscience/briefing/preview</c>
/// answered 503 "warming" to 480 of 499 calls with no error logged. Its background snapshot counted the
/// day's messages with <c>WHERE captured_at &gt;= $since</c>, which no index carries, so every refresh read
/// the whole 5.7M-row messages table; the build outlived the snapshot's 15-minute life, and the store
/// refuses a snapshot older than that.
/// </summary>
public sealed class BriefingCostTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_briefing_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;
    private readonly ITestOutputHelper output;

    public BriefingCostTests(ITestOutputHelper output)
    {
        this.output = output;
        db = new OmniscienceDb(dbPath);
        db.Migrate();
    }

    [Fact]
    public void CountAddedSince_MatchesAPlainCount_AtEveryEdge()
    {
        using (var conn = db.Open())
            Assert.Equal(0, DailyBriefing.CountAddedSince(conn, "messages", "captured_at", 0));

        SeedMessages(500, rawJsonBytes: 0, capturedAt: i => 1_000L * i);
        // Gaps where rows were deleted, including both ends of the table.
        Execute("DELETE FROM messages WHERE rowid IN (1, 2, 50, 51, 52, 250, 499, 500) OR rowid BETWEEN 300 AND 340");

        using var read = db.Open();
        foreach (long since in new long[] { long.MinValue, -1, 0, 1, 999, 1_000, 1_001, 49_000, 50_000, 250_500,
                     299_000, 300_000, 341_000, 497_000, 498_000, 498_001, 499_000, 600_000, long.MaxValue })
        {
            long expected = Scalar($"SELECT COUNT(*) FROM messages WHERE captured_at >= {since}");
            Assert.Equal(expected, DailyBriefing.CountAddedSince(read, "messages", "captured_at", since));
        }
    }

    [Fact]
    public void TheDaysCount_ReadsTheRecentRows_NotTheTable()
    {
        const long now = 1_794_600_000_000;
        const long day = 86_400_000;
        // 20,000 messages with a ~1 KB raw payload, the last 100 inside the day (live: 5.7M of them).
        SeedMessages(20_000, rawJsonBytes: 1_000, capturedAt: i => i < 19_900 ? now - 30 * day + i : now - 1_000 + (i - 19_900));
        long since = now - day;

        int before = Pages(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE captured_at >= $since";
            cmd.Parameters.AddWithValue("$since", since);
            Assert.Equal(100L, Convert.ToInt64(cmd.ExecuteScalar()));
        });
        int after = Pages(conn => Assert.Equal(100, DailyBriefing.CountAddedSince(conn, "messages", "captured_at", since)));
        output.WriteLine($"pages read: plain count {before}, CountAddedSince {after}");

        // Measured: 6,683 pages for the plain count (the whole table), 51 for CountAddedSince. A first
        // version read MIN and MAX in one SELECT and took 145: that form scans the smallest index.

        Assert.True(before > 5_000, $"the old count read {before} pages");
        Assert.True(after * 50 < before, $"the new count read {after} pages against {before}");
        Assert.True(after < 120, $"the new count read {after} pages");
    }

    [Fact]
    public void Compose_StillCountsTheDaysMessagesAndAnswers()
    {
        const long now = 1_794_600_000_000;
        const long hour = 3_600_000;
        SeedMessages(6, rawJsonBytes: 10, capturedAt: i => now - (30 - i * 5) * hour); // 30h, 25h, 20h, 15h, 10h, 5h ago
        for (int i = 0; i < 4; i++)
            Execute($"INSERT INTO qa_pairs(question, answer, extracted_at) VALUES('q{i}', 'a{i}', {now - (26 - i * 8) * hour})"); // 26h, 18h, 10h, 2h ago

        string markdown = new DailyBriefing(null!, db).Compose(now);

        Assert.Contains("4 messages ingested", markdown);
        Assert.Contains("3 Q&A pairs mined", markdown);
    }

    [Fact]
    public async Task ABuildSlowerThanItsMaxAge_IsReported_AtMostHourly()
    {
        DateTimeOffset clock = new(2026, 11, 13, 2, 0, 0, TimeSpan.Zero);
        var errors = new List<string>();
        using var snapshots = new OmniscienceBackgroundSnapshots(
            Path.Combine(Path.GetTempPath(), "omniscience_snapshots_" + Guid.NewGuid().ToString("N")),
            (key, ex) => errors.Add(ex.Message), () => clock);
        TimeSpan buildTakes = TimeSpan.FromMinutes(16);
        snapshots.Register("briefing/preview", "briefing-preview.json", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15),
            () => { clock += buildTakes; return "{\"markdown\":\"x\"}"; }, startWorker: false);

        await snapshots.RefreshNowAsync("briefing/preview");
        Assert.Null(snapshots.TryGet("briefing/preview"));
        Assert.Single(errors);
        Assert.Contains("16.0 min", errors[0]);

        await snapshots.RefreshNowAsync("briefing/preview");
        Assert.Single(errors); // still slow, but already said this hour

        buildTakes = TimeSpan.FromSeconds(2);
        await snapshots.RefreshNowAsync("briefing/preview");
        Assert.Equal("{\"markdown\":\"x\"}", snapshots.TryGet("briefing/preview"));
        Assert.Single(errors);

        clock += TimeSpan.FromHours(1);
        buildTakes = TimeSpan.FromMinutes(20);
        await snapshots.RefreshNowAsync("briefing/preview");
        Assert.Equal(2, errors.Count);
    }

    private void SeedMessages(int count, int rawJsonBytes, Func<int, long> capturedAt)
    {
        string raw = "{\"payload\":\"" + new string('r', Math.Max(0, rawJsonBytes - 14)) + "\"}";
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, raw_json, captured_at)
VALUES($id, 'discord', $id, 'c1', 'i1', $at, 'hello there', $raw, $at)";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var at = cmd.Parameters.Add("$at", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$raw", raw);
        for (int i = 0; i < count; i++)
        {
            id.Value = "m" + i;
            at.Value = capturedAt(i);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Pages read (cache misses on a fresh connection) by one piece of work.</summary>
    private int Pages(Action<SqliteConnection> work)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        // Load the schema first: Migrate() creates dozens of tables, and that read is no part of the query.
        using (var schema = conn.CreateCommand()) { schema.CommandText = "SELECT COUNT(*) FROM sqlite_schema"; schema.ExecuteScalar(); }
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
        work(conn);
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0);
        return pages;
    }

    private void Execute(string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
        try { File.Delete(dbPath); } catch { }
        try { File.Delete(dbPath + "-wal"); } catch { }
        try { File.Delete(dbPath + "-shm"); } catch { }
    }
}
