using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Replica;

namespace Omnipotent.Tests.Omniscience;

/// <summary>
/// Replica readiness (10 Nov 2026): six <c>GET /omniscience/replica/status</c> calls took 71–149 s, all in
/// the handler. The status counted the person's embedded messages on every call by reading every message
/// they wrote, and the panel waits for the status before showing its chats. The counts are now kept per
/// person and recounted in the background, one at a time.
/// </summary>
public sealed class ReplicaReadinessTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_replica_ready_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;

    public ReplicaReadinessTests()
    {
        db = new OmniscienceDb(dbPath);
        db.Migrate();
    }

    /// <summary>30 messages by i1 (tracked p1), i2 (tracked p2) and "other" (archive p3); every other one embedded.</summary>
    private void SeedCorpus()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder(@"INSERT INTO persons(person_id,display_name,tier,created_at,updated_at) VALUES('p1','P1','tracked',0,0),('p2','P2','tracked',0,0),('p3','P3','archive',0,0);
INSERT INTO platform_identities(identity_id,person_id,platform,platform_user_id) VALUES('i1','p1','test','u1'),('i2','p2','test','u2'),('other','p3','test','u3');");
        for (int i = 0; i < 30; i++)
        {
            string author = i % 3 == 0 ? "i1" : i % 3 == 1 ? "i2" : "other";
            sql.Append($"INSERT INTO messages(message_id,platform,platform_message_id,conversation_id,author_identity_id,sent_at,content,captured_at) VALUES('m{i}','test','m{i}','c1','{author}',{i},'text',{i});");
            if (i % 2 == 0) sql.Append($"INSERT INTO message_embeddings(message_id,embedding,embedded_at) VALUES('m{i}',x'00',0);");
            if (author != "other") sql.Append($"INSERT INTO stimulus_reply_pairs(replier_identity_id,stimulus_message_id,reply_message_id,stimulus_text,reply_text) VALUES('{author}','m{(i + 1) % 30}','r{i}','s','r');");
        }
        cmd.CommandText = sql.ToString();
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task DataHealth_AnswersAtOnce_ThenShowsWhatTheOldJoinCounted()
    {
        SeedCorpus();
        var explorer = new Omnipotent.Services.Omniscience.Workspace.WorkspaceExplorer(db);

        var first = explorer.Health()["metrics"]!;
        Assert.True(first.Value<bool>("tracked_embedding_counting"));
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, first["tracked_embedded"]!.Type);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, first["tracked_embedding_coverage_pct"]!.Type);

        await WaitUntil(() => !explorer.Health()["metrics"]!.Value<bool>("tracked_embedding_counting"));
        using var conn = db.Open();
        using var old = conn.CreateCommand();
        old.CommandText = @"SELECT COUNT(*) FROM message_embeddings e JOIN messages m ON m.message_id=e.message_id
                JOIN platform_identities i ON i.identity_id=m.author_identity_id JOIN persons p ON p.person_id=i.person_id WHERE p.tier='tracked'";
        Assert.Equal(Convert.ToInt64(old.ExecuteScalar()), explorer.Health()["metrics"]!.Value<long>("tracked_embedded"));
        Assert.Equal(10, explorer.Health()["metrics"]!.Value<long>("tracked_embedded"));
    }

    [Fact]
    public void CountEmbeddings_GivesWhatTheOldStatementGave()
    {
        SeedCorpus();
        using var read = db.Open();
        var counted = ReplicaReadinessCounts.CountEmbeddings(read, new[] { "i1", "i2" });
        using var old = read.CreateCommand();
        old.CommandText = @"SELECT
            (SELECT COUNT(*) FROM message_embeddings e JOIN messages m ON m.message_id=e.message_id WHERE m.author_identity_id IN ('i1','i2')),
            (SELECT COUNT(*) FROM stimulus_reply_pairs s JOIN message_embeddings e ON e.message_id=s.stimulus_message_id WHERE s.replier_identity_id IN ('i1','i2'))";
        using var r = old.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal((r.GetInt64(0), r.GetInt64(1)), counted);
        Assert.Equal((10L, 10L), counted); // 20 messages by i1/i2, every other one embedded; stimuli likewise
        Assert.Equal((0L, 0L), ReplicaReadinessCounts.CountEmbeddings(read, Array.Empty<string>()));
    }

    [Fact]
    public async Task AFirstCount_IsWaitedOnBriefly_ThenTheStatusSaysItIsCounting()
    {
        var gate = new TaskCompletionSource();
        int counts = 0;
        var readiness = new ReplicaReadinessCounts(_ => { Interlocked.Increment(ref counts); gate.Task.Wait(); return (5, 2); }, TimeSpan.FromMinutes(10));

        var first = await readiness.ReadAsync("p1", Short);
        Assert.Null(first.Value);
        Assert.True(first.Counting);
        // Asked again (the panel polls) while it counts: nothing new starts.
        Assert.True((await readiness.ReadAsync("p1", Short)).Counting);
        Assert.Equal(1, readiness.Running);

        gate.SetResult();
        await WaitUntil(() => readiness.Running == 0);
        var done = await readiness.ReadAsync("p1", Short);
        Assert.Equal(5, done.Value!.Embedded);
        Assert.Equal(2, done.Value.EmbeddedStimuli);
        Assert.False(done.Counting);
        Assert.Equal(1, counts);
    }

    [Fact]
    public async Task AQuickCount_IsInTheSameAnswer_AndOldCountsAreServedWhileOneRecountRuns()
    {
        DateTimeOffset now = new(2026, 11, 10, 18, 43, 0, TimeSpan.Zero);
        int version = 0;
        var gate = new TaskCompletionSource();
        var readiness = new ReplicaReadinessCounts(_ =>
        {
            if (version > 0) gate.Task.Wait();
            version++;
            return (version * 100, version);
        }, TimeSpan.FromMinutes(10), utcNow: () => now);

        Assert.Equal(100, (await readiness.ReadAsync("p1", TimeSpan.FromSeconds(5))).Value!.Embedded);
        now = now.AddMinutes(5);
        var fresh = await readiness.ReadAsync("p1", Short);
        Assert.False(fresh.Counting);
        Assert.Equal(0, readiness.Running);

        now = now.AddMinutes(6);
        var stale = await readiness.ReadAsync("p1", Short);
        await readiness.ReadAsync("p1", Short);
        Assert.Equal(100, stale.Value!.Embedded); // the old counts, at once
        Assert.True(stale.Counting);
        Assert.Equal(1, readiness.Running);

        gate.SetResult();
        await WaitUntil(() => readiness.Running == 0);
        Assert.Equal(200, (await readiness.ReadAsync("p1", Short)).Value!.Embedded);
    }

    [Fact]
    public async Task SeveralPeople_AreCountedOneAtATime_AndAFailureIsLoggedAndRetried()
    {
        int inside = 0, most = 0;
        bool fail = true;
        var errors = new List<string>();
        var readiness = new ReplicaReadinessCounts(person =>
        {
            int now = Interlocked.Increment(ref inside);
            lock (errors) most = Math.Max(most, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref inside);
            if (person == "p3" && fail) throw new InvalidOperationException("database is locked");
            return (1, 1);
        }, TimeSpan.FromMinutes(10), (person, ex) => { lock (errors) errors.Add(person + ": " + ex.Message); });

        foreach (string person in new[] { "p1", "p2", "p3", "p4" }) await readiness.ReadAsync(person, TimeSpan.Zero);
        await WaitUntil(() => readiness.Running == 0);
        Assert.Equal(1, most);
        Assert.Equal(new[] { "p3: database is locked" }, errors);

        fail = false;
        Assert.Null((await readiness.ReadAsync("p3", TimeSpan.Zero)).Value); // nothing to show yet; this read retries
        await WaitUntil(() => readiness.Running == 0);
        Assert.Equal(1, (await readiness.ReadAsync("p3", TimeSpan.Zero)).Value!.Embedded);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(dbPath + suffix); } catch { }
    }
}
