using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Analytics;
using Omnipotent.Services.Omniscience.Workspace;

namespace Omnipotent.Tests.Omniscience;

public sealed class WorkspaceExplorerTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_workspace_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;
    private readonly WorkspaceExplorer explorer;
    private readonly WorkspaceStore store;
    private readonly long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public WorkspaceExplorerTests()
    {
        db = new OmniscienceDb(dbPath);
        db.Migrate();
        explorer = new WorkspaceExplorer(db);
        store = new WorkspaceStore(db);
        SeedPerson("p1", "Alice", "i1", "alice");
        SeedPerson("p2", "Bob", "i2", "bob");
        SeedConversation("c1", "dm", "Private chat");
        InsertMessage("m1", "i1", "c1", now - 1000, "alpha one");
        InsertMessage("m2", "i1", "c1", now - 2000, "alpha two");
        InsertMessage("m3", "i2", "c1", now - 3000, "beta reply");
        InsertMessage("old", "i1", "c1", now - 10_000, "older alpha");
        SeedKnowledge();
    }

    [Fact]
    public void DirectorySearchPinsAndSavedQueries_WorkTogether()
    {
        store.SetPin("p1", true, -2);
        store.SaveNote(null, "p1", "Remember the alpha lead", "investigation");
        string queryId = store.SaveQuery(null, "Alpha investigation", new JObject { ["q"] = "alpha", ["personId"] = "p1" });
        store.UseQuery(queryId, "alpha", new JObject { ["personId"] = "p1" });

        var directory = explorer.Directory("Alice", null, null, true, "messages", 20, 0);
        Assert.Equal(1, directory.Value<int>("total"));
        var person = Assert.Single((JArray)directory["items"]!);
        Assert.True(person.Value<bool>("pinned"));
        Assert.Equal(3, person.Value<long>("message_count"));

        var search = explorer.CommandSearch("alpha", 20);
        var kinds = ((JArray)search["results"]!).Select(x => x.Value<string>("kind")).ToList();
        Assert.Contains("note", kinds);
        Assert.Contains("saved_query", kinds);
        Assert.Contains("fact", ((JArray)explorer.CommandSearch("Leeds", 20)["results"]!).Select(x => x.Value<string>("kind")).ToList());
        Assert.Equal(1, store.ListHistory().Count);
        Assert.Equal(1, ((JObject)store.ListQueries()[0]!).Value<int>("use_count"));
    }

    [Fact]
    public void ScopedMessageSearchTimelineAndExport_RespectScopeAndProvenance()
    {
        Assert.Throws<ArgumentException>(() => explorer.SearchMessages(new JObject { ["q"] = "alpha" }, 20, 0));
        var filters = new JObject { ["q"] = "alpha", ["personId"] = "p1", ["from"] = now - 5000 };
        var result = explorer.SearchMessages(filters, 20, 0);
        Assert.Equal(2, ((JArray)result["items"]!).Count);
        Assert.All((JArray)result["items"]!, item => Assert.Equal("p1", item.Value<string>("person_id")));
        Assert.Contains("alpha", explorer.MessagesCsv(filters));

        store.SaveNote(null, "p1", "timeline annotation", "test");
        var timeline = explorer.Timeline("p1", null, null, 50, null);
        var types = ((JArray)timeline["events"]!).Select(x => x.Value<string>("type")).ToHashSet();
        Assert.Contains("message", types);
        Assert.Contains("fact", types);
        Assert.Contains("identity", types);
        Assert.Contains("note", types);

        string exported = explorer.ExportPerson("p1", "json").Value<string>("content")!;
        Assert.Contains("Lives in Leeds", exported);
        Assert.Contains("evidence_message_ids", exported);
        Assert.DoesNotContain("replica_chat_messages", exported);
    }

    [Fact]
    public void ActivityComparisonGraphAndHealth_AreComputedFromSmallSummaries()
    {
        var activity = explorer.Activity("p1", 30);
        Assert.Equal(3, activity.Value<long>("current_messages"));
        Assert.Equal(24, ((JArray)activity["hourly"]!).Count);
        Assert.NotEmpty((JArray)activity["daily"]!);

        var comparison = explorer.Compare("p1", "p2");
        Assert.Equal(1, comparison.Value<int>("shared_conversation_count"));
        Assert.NotEmpty((JArray)comparison["relationship_links"]!);

        var graph = explorer.Graph("p1", 1, 20);
        Assert.Contains((JArray)graph["nodes"]!, n => n.Value<string>("id") == "p2");
        Assert.NotEmpty((JArray)graph["edges"]!);

        var health = explorer.Health();
        Assert.True(health["metrics"]!.Value<long>("messages") >= 4);
        Assert.NotNull(health["activity_counts"]);
        Assert.NotEmpty(explorer.Capabilities()["features"]!);
    }

    private void SeedKnowledge()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO person_facts(fact_id,person_id,category,fact_text,confidence,status,source_context,first_evidence_at,last_evidence_at,evidence_message_ids_json,extracted_by,created_at,updated_at)
VALUES('f1','p1','location','Lives in Leeds',0.9,'active','dm',$at,$at,'[""m1""]','manual',$at,$at);
INSERT INTO entity_relationships(owner_person_id,related_person_id,rel_type,confidence,evidence_count,status,created_at,updated_at)
VALUES('p1','p2','friend',0.85,3,'active',$at,$at);
INSERT INTO identity_history(identity_id,field,old_value,new_value,changed_at) VALUES('i1','display_name',NULL,'Alice',$at);
INSERT INTO conversation_participants(conversation_id,identity_id) VALUES('c1','i1'),('c1','i2');";
        cmd.Parameters.AddWithValue("$at", now);
        cmd.ExecuteNonQuery();
    }

    private void SeedPerson(string personId, string name, string identityId, string username)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO persons(person_id,display_name,tier,created_at,updated_at) VALUES($p,$name,'tracked',$at,$at);
INSERT INTO platform_identities(identity_id,person_id,platform,platform_user_id,platform_username,display_name)
VALUES($i,$p,'test',$i,$username,$name);";
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$i", identityId);
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$at", now);
        cmd.ExecuteNonQuery();
    }

    private void SeedConversation(string id, string kind, string title)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations(conversation_id,platform,kind,channel_id,title,first_seen,last_seen) VALUES($id,'test',$kind,$id,$title,$at,$at)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$at", now);
        cmd.ExecuteNonQuery();
    }

    private void InsertMessage(string id, string identity, string conversation, long sentAt, string content)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO messages(message_id,platform,platform_message_id,conversation_id,author_identity_id,sent_at,content,captured_at)
VALUES($id,'test',$id,$c,$i,$at,$content,$at)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$i", identity);
            cmd.Parameters.AddWithValue("$at", sentAt);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.ExecuteNonQuery();
        }
        ActivitySummaryStore.RecordMessage(conn, tx, identity, conversation, sentAt, sentAt);
        tx.Commit();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { }
    }
}
