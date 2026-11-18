using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Workspace;
using Xunit.Abstractions;

namespace Omnipotent.Tests.Omniscience;

/// <summary>
/// The people directory (13 Nov 2026). Live over seven days <c>GET /omniscience/workspace/directory</c> took
/// 0.4 s while the Omniscience page stayed open, but 16–72 s for the first load of each visit (six visits),
/// with the page's two or three simultaneous calls finishing one after another. One statement computed every
/// column, two identity subqueries included, for all 45,969 people before sorting and keeping 48, so a
/// visit after a quiet spell read every person's rows from the server's slow disk.
/// </summary>
public sealed class DirectoryCostTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_directory_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;
    private readonly WorkspaceExplorer explorer;
    private readonly ITestOutputHelper output;

    // What ran through 6285052, with the order the page asks for by default.
    private const string OldPage = @"SELECT p.person_id,p.display_name,COALESCE(p.tier,'archive'),p.created_at,p.updated_at,
                COALESCE(c.message_count,0),c.first_message_at,c.last_message_at,
                (SELECT GROUP_CONCAT(platform||':'||COALESCE(platform_username,''),'|') FROM platform_identities WHERE person_id=p.person_id),
                (SELECT json_group_array(json_object('platform',platform,'username',platform_username,'display_name',display_name)) FROM platform_identities WHERE person_id=p.person_id),
                EXISTS(SELECT 1 FROM person_profile_targets t WHERE t.person_id=p.person_id AND t.enabled=1),
                EXISTS(SELECT 1 FROM workspace_pins w WHERE w.person_id=p.person_id),ps.payload_json
                FROM persons p LEFT JOIN person_message_counts c ON c.person_id=p.person_id
                LEFT JOIN workspace_pins w ON w.person_id=p.person_id
                LEFT JOIN person_statistics ps ON ps.person_id=p.person_id AND ps.module_name='deduction_summary'
                WHERE p.merged_into_person_id IS NULL ORDER BY {0} LIMIT $limit OFFSET $offset";

    public DirectoryCostTests(ITestOutputHelper output)
    {
        this.output = output;
        db = new OmniscienceDb(dbPath);
        db.Migrate();
        explorer = new WorkspaceExplorer(db);
    }

    [Fact]
    public void EveryPage_MatchesTheOldOrder_ForEverySort()
    {
        // 300 people: 240 with counts (unique count/recency), 60 without, 10 merged away; names and recency unique.
        Seed(people: 300, counted: 240, merged: 10, extraJsonBytes: 20);

        // Default order: every counted person by messages then recency, then every uncounted person once.
        var expectedCounted = Ids(string.Format(OldPage, "COALESCE(c.message_count,0) DESC, COALESCE(c.last_message_at,0) DESC"), 1000, 0)
            .Take(240 - CountedMerged()).ToList();
        var walked = new List<string>();
        for (int offset = 0; offset < 400; offset += 48)
            walked.AddRange(Page("messages", 48, offset));
        Assert.Equal(expectedCounted, walked.Take(expectedCounted.Count));
        Assert.Equal(290, walked.Count);
        Assert.Equal(290, walked.Distinct().Count());
        // A page that starts inside the uncounted people, and one past the end.
        Assert.Equal(walked.Skip(250).Take(20), Page("messages", 20, 250));
        Assert.Empty(Page("messages", 48, 290));

        foreach (var (sort, order) in new[]
                 {
                     ("name", "p.display_name COLLATE NOCASE ASC"),
                     ("recent", "COALESCE(c.last_message_at,0) DESC, p.display_name COLLATE NOCASE"),
                 })
            foreach (int offset in new[] { 0, 48, 200, 280 })
                Assert.Equal(Ids(string.Format(OldPage, order), 48, offset), Page(sort, 48, offset));

        // The rows themselves are what the old statement returned.
        var oldRows = Rows(string.Format(OldPage, "COALESCE(c.message_count,0) DESC, COALESCE(c.last_message_at,0) DESC"), 48, 0);
        var page = (JArray)explorer.Directory(null, "all", null, false, "messages", 48, 0)["items"]!;
        Assert.Equal(oldRows, page.Select(item => Canon((JObject)item)).ToList());

        // Filters still apply on the counts-index path and the sort path alike.
        var tracked = (JArray)explorer.Directory(null, "tracked", null, false, "messages", 48, 0)["items"]!;
        Assert.All(tracked, item => Assert.Equal("tracked", item.Value<string>("tier")));
        Assert.Equal(Count("SELECT COUNT(*) FROM persons WHERE tier='tracked' AND merged_into_person_id IS NULL"), tracked.Count);
        var search = explorer.Directory("person 0012", "all", null, false, "messages", 48, 0);
        Assert.InRange(((JArray)search["items"]!).Count, 8, 10); // Person 00120–00129, less any merged away
        Assert.All((JArray)search["items"]!, item => Assert.Contains("Person 0012", item.Value<string>("display_name")));
        Assert.Equal(search.Value<long>("total"), ((JArray)search["items"]!).Count);
    }

    [Fact]
    public void TheFirstPage_ReadsThePageNotEveryPerson()
    {
        // Live shape: 45,969 people, one identity each with a few hundred bytes of extra JSON, most with counts.
        Seed(people: 46_000, counted: 40_000, merged: 200, extraJsonBytes: 300);

        string oldSql = string.Format(OldPage, "COALESCE(c.message_count,0) DESC, COALESCE(c.last_message_at,0) DESC");
        int before = Pages(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = oldSql;
            cmd.Parameters.AddWithValue("$limit", 48);
            cmd.Parameters.AddWithValue("$offset", 0);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { }
        });
        int countOnly = Pages(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM persons p WHERE p.merged_into_person_id IS NULL";
            cmd.ExecuteScalar();
        });
        int after = Pages(conn => Assert.Equal(48, ((JArray)explorer.Directory(conn, null, "all", null, false, "messages", 48, 0)["items"]!).Count));
        output.WriteLine($"pages read: old page {before} (+ count {countOnly}), new page with its count {after}");

        // Warm, what the page pays every 90 s while open (live p50 425 ms).
        double oldMs = WarmMs(() => Ids(oldSql, 48, 0)), newMs = WarmMs(() => explorer.Directory(null, "all", null, false, "messages", 48, 0));
        output.WriteLine($"warm: old page {oldMs:0.0} ms, new page with its count {newMs:0.0} ms");
        Assert.True(newMs * 3 < oldMs, $"warm {newMs:0.0} ms against {oldMs:0.0} ms");

        // Measured: old page 2,361 + its count 1,173 = 3,534 pages; new page with its count 519.
        Assert.True(after * 5 < before + countOnly, $"new {after} pages against old {before} + {countOnly}");
        // The new index is used only by a query that says "tier >= ''", so the old-style
        // "merged_into_person_id IS NULL" reads elsewhere keep their plan (and the "before" above is honest).
        Assert.DoesNotContain("idx_persons_live_tier", Plan("SELECT person_id, display_name FROM persons p WHERE p.merged_into_person_id IS NULL"));
        Assert.DoesNotContain("idx_persons_live_tier", Plan("SELECT COUNT(*) FROM persons p WHERE p.merged_into_person_id IS NULL"));
        Assert.Contains("COVERING INDEX idx_persons_live_tier", Plan(
            "SELECT COUNT(*) FROM persons p WHERE p.tier >= '' AND p.merged_into_person_id IS NULL AND COALESCE(p.tier,'archive')='tracked'"));
        Assert.DoesNotContain("TEMP B-TREE", Plan(@"SELECT c.person_id FROM person_message_counts c INDEXED BY idx_person_counts_activity
                JOIN persons p ON p.person_id=c.person_id WHERE p.merged_into_person_id IS NULL ORDER BY c.message_count DESC, c.last_message_at DESC LIMIT 48"));
    }

    private static double WarmMs(Action work)
    {
        work(); // warm
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) work();
        return timer.Elapsed.TotalMilliseconds / 5;
    }

    private string Plan(string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var plan = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) plan.Add(r.GetString(3));
        return string.Join(" / ", plan);
    }

    private List<string> Page(string sort, int limit, int offset) =>
        ((JArray)explorer.Directory(null, "all", null, false, sort, limit, offset)["items"]!).Select(item => item.Value<string>("person_id")!).ToList();

    private List<string> Ids(string sql, int limit, int offset)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private List<string> Rows(string sql, int limit, int offset)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var rows = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double? completeness = r.IsDBNull(12) ? null : JObject.Parse(r.GetString(12)).Value<double?>("completeness_score");
            rows.Add(string.Join("|", r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(5),
                r.IsDBNull(7) ? "" : r.GetInt64(7).ToString(), r.IsDBNull(8) ? "" : r.GetString(8),
                JArray.Parse(r.GetString(9)).ToString(Newtonsoft.Json.Formatting.None), r.GetInt32(10), r.GetInt32(11), completeness));
        }
        return rows;
    }

    private static string Canon(JObject item) => string.Join("|", item.Value<string>("person_id"), item.Value<string>("display_name"),
        item.Value<string>("tier"), item.Value<long>("message_count"), item.Value<long?>("last_message_at")?.ToString() ?? "",
        item.Value<string>("handles"), item["identities"]!.ToString(Newtonsoft.Json.Formatting.None),
        item.Value<bool>("profile_targeted") ? 1 : 0, item.Value<bool>("pinned") ? 1 : 0, item.Value<double?>("completeness"));

    private int CountedMerged() => (int)Count(
        "SELECT COUNT(*) FROM persons p JOIN person_message_counts c ON c.person_id=p.person_id WHERE p.merged_into_person_id IS NOT NULL");

    private long Count(string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// People p0..pN with names "Person NNNNN"; the first <paramref name="counted"/> get a message count
    /// (unique count and recency, in an order unrelated to their names); every 7th is tracked, the first
    /// five pinned, tracked ones targeted with a deduction summary, and every 29th merged away.
    /// </summary>
    private void Seed(int people, int counted, int merged, int extraJsonBytes)
    {
        string extra = "{\"roles\":\"" + new string('x', Math.Max(0, extraJsonBytes - 12)) + "\"}";
        const long t0 = 1_790_000_000_000;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var person = conn.CreateCommand();
        person.Transaction = tx;
        person.CommandText = @"INSERT INTO persons(person_id,display_name,tier,created_at,updated_at,merged_into_person_id,avatar_path)
VALUES($p,$name,$tier,$t,$t,$merged,'avatars/' || $p || '/0f3c2a9e1d7b4c6a8e5f0a1b2c3d4e5f.png');
INSERT INTO platform_identities(identity_id,person_id,platform,platform_user_id,platform_username,display_name,bio,extra_json,first_seen,last_seen)
VALUES('i' || $p,$p,'discord','1' || printf('%017d', $n),'user' || $n,$name,'a short bio line for this account',$extra,$t,$t);";
        var p = person.Parameters.Add("$p", SqliteType.Text);
        var n = person.Parameters.Add("$n", SqliteType.Integer);
        var name = person.Parameters.Add("$name", SqliteType.Text);
        var tier = person.Parameters.Add("$tier", SqliteType.Text);
        var mergedInto = person.Parameters.Add("$merged", SqliteType.Text);
        person.Parameters.AddWithValue("$t", t0);
        person.Parameters.AddWithValue("$extra", extra);

        using var count = conn.CreateCommand();
        count.Transaction = tx;
        count.CommandText = "INSERT INTO person_message_counts(person_id,message_count,first_message_at,last_message_at,updated_at) VALUES($p,$c,$f,$l,$f)";
        var cp = count.Parameters.Add("$p", SqliteType.Text);
        var cc = count.Parameters.Add("$c", SqliteType.Integer);
        var cl = count.Parameters.Add("$l", SqliteType.Integer);
        count.Parameters.AddWithValue("$f", t0);

        using var extraRows = conn.CreateCommand();
        extraRows.Transaction = tx;
        extraRows.CommandText = @"INSERT INTO person_profile_targets(person_id,enabled,added_at,updated_at) VALUES($p,1,0,0);
INSERT INTO person_statistics(person_id,module_name,module_version,computed_at,payload_json) VALUES($p,'deduction_summary',1,0,$payload);";
        var ep = extraRows.Parameters.Add("$p", SqliteType.Text);
        var payload = extraRows.Parameters.Add("$payload", SqliteType.Text);

        int mergeEvery = merged > 0 ? Math.Max(2, people / merged) : int.MaxValue;
        for (int i = 0; i < people; i++)
        {
            string id = "p" + i;
            p.Value = id; n.Value = i; name.Value = $"Person {(i * 7919) % people:D5}";
            tier.Value = i % 7 == 0 ? "tracked" : "archive";
            mergedInto.Value = i % mergeEvery == mergeEvery - 1 ? "p0" : DBNull.Value;
            person.ExecuteNonQuery();
            if (i < counted)
            {
                cp.Value = id;
                cc.Value = 1 + (long)i * 104_729 % 1_000_003;
                cl.Value = t0 + (long)i * 15_485_863 % 1_000_000_007;
                count.ExecuteNonQuery();
            }
            if (i % 7 == 0 && i % mergeEvery != mergeEvery - 1 && i < 245)
            {
                ep.Value = id;
                payload.Value = "{\"completeness_score\":" + (i % 100) / 100.0 + ",\"notes\":\"" + new string('d', 2_000) + "\"}";
                extraRows.ExecuteNonQuery();
            }
        }
        tx.Commit();

        using var pins = conn.CreateCommand();
        pins.CommandText = "INSERT INTO workspace_pins(person_id,pinned_at,sort_order) VALUES('p1',0,1),('p2',0,2),('p3',0,3),('p4',0,4),('p5',0,5)";
        pins.ExecuteNonQuery();
    }

    /// <summary>Pages read (cache misses on a fresh connection) by one piece of work, schema excluded.</summary>
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(dbPath + suffix); } catch { }
    }
}
