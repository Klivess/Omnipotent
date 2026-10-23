using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Analytics;

namespace Omnipotent.Tests.Omniscience;

public sealed class ActivitySummaryStoreTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_counts_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;

    public ActivitySummaryStoreTests()
    {
        db = new OmniscienceDb(dbPath);
        db.Migrate();
    }

    [Fact]
    public async Task Counts_AreIncremental_AndLegacyBackfillIsExact()
    {
        SeedPerson("p1", "i1");
        SeedConversation("c1");
        InsertMessage("m1", "c1", "i1", 1_000, withSummary: true);
        InsertMessage("m2", "c1", "i1", 2_000, withSummary: true);
        InsertMessage("legacy", "c1", "i1", 500, withSummary: false);

        Assert.Equal(2, ScalarLong("SELECT message_count FROM person_message_counts WHERE person_id='p1'"));
        Assert.Equal(2, ScalarLong("SELECT message_count FROM conversation_message_counts WHERE conversation_id='c1'"));

        var summaries = new ActivitySummaryStore(db);
        await summaries.EnsureBackfilledAsync();

        Assert.Equal(3, ScalarLong("SELECT message_count FROM person_message_counts WHERE person_id='p1'"));
        Assert.Equal(3, ScalarLong("SELECT message_count FROM conversation_message_counts WHERE conversation_id='c1'"));
        Assert.Equal(500, ScalarLong("SELECT first_message_at FROM person_message_counts WHERE person_id='p1'"));
        Assert.Equal(2_000, ScalarLong("SELECT last_message_at FROM person_message_counts WHERE person_id='p1'"));
        Assert.True(summaries.GetStatus().Ready);
        Assert.False(summaries.GetStatus().Running);
    }

    [Fact]
    public async Task RebuildPerson_RepairsCountsAfterIdentityMerge()
    {
        SeedPerson("keep", "i1");
        SeedPerson("drop", "i2");
        SeedConversation("c1");
        InsertMessage("m1", "c1", "i1", 1_000, withSummary: false);
        InsertMessage("m2", "c1", "i2", 2_000, withSummary: false);
        await new ActivitySummaryStore(db).EnsureBackfilledAsync();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var move = conn.CreateCommand())
        {
            move.Transaction = tx;
            move.CommandText = "UPDATE platform_identities SET person_id='keep' WHERE person_id='drop'";
            move.ExecuteNonQuery();
        }
        ActivitySummaryStore.RebuildPerson(conn, tx, "keep", 3_000);
        ActivitySummaryStore.RebuildPerson(conn, tx, "drop", 3_000);
        tx.Commit();

        Assert.Equal(2, ScalarLong("SELECT message_count FROM person_message_counts WHERE person_id='keep'"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM person_message_counts WHERE person_id='drop'"));
    }

    private void SeedPerson(string personId, string identityId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO persons(person_id, display_name, created_at, updated_at) VALUES($p,$p,0,0);
INSERT INTO platform_identities(identity_id, person_id, platform, platform_user_id) VALUES($i,$p,'test',$i);";
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$i", identityId);
        cmd.ExecuteNonQuery();
    }

    private void SeedConversation(string conversationId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations(conversation_id, platform, kind, channel_id) VALUES($c,'test','dm',$c)";
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.ExecuteNonQuery();
    }

    private void InsertMessage(string id, string conversationId, string identityId, long sentAt, bool withSummary)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, captured_at)
VALUES($id,'test',$id,$c,$i,$sent,$sent)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$c", conversationId);
            cmd.Parameters.AddWithValue("$i", identityId);
            cmd.Parameters.AddWithValue("$sent", sentAt);
            cmd.ExecuteNonQuery();
        }
        if (withSummary) ActivitySummaryStore.RecordMessage(conn, tx, identityId, conversationId, sentAt, sentAt);
        tx.Commit();
    }

    private long ScalarLong(string sql)
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
    }
}
