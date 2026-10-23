using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.Omniscience.Replica;

namespace Omnipotent.Tests.Omniscience;

public sealed class ReplicaTemporalTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omniscience_temporal_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly OmniscienceDb db;

    public ReplicaTemporalTests()
    {
        db = new OmniscienceDb(dbPath);
        db.Migrate();
    }

    [Fact]
    public void EraProfiles_SeparateChangingStyleAcrossYears()
    {
        var rows = new List<ReplicaTemporalMessage>
        {
            new("a", At(2022, 3, 1), "lol yeah", "dm"),
            new("b", At(2022, 4, 1), "lol nah", "dm"),
            new("c", At(2023, 3, 1), "Indeed. That is a splendid idea.", "guild_channel"),
            new("d", At(2023, 5, 1), "I agree entirely.", "guild_channel"),
        };

        var eras = ReplicaTemporalAnalyzer.BuildEraProfiles(rows);

        Assert.Equal(2, eras.Count);
        Assert.Equal("2023", eras[0].Era);
        Assert.Equal(2, eras[0].MessageCount);
        Assert.Equal("2022", eras[1].Era);
        Assert.Equal("lol", eras[1].Style.DominantLaughFamily);
        Assert.True(eras[0].Style.AverageLength > eras[1].Style.AverageLength);
        Assert.True(eras[0].Style.TerminalPeriodRate > eras[1].Style.TerminalPeriodRate);
    }

    [Fact]
    public async Task ExactStyle_UsesCutoff_ContextAndExclusions()
    {
        Seed("p1", "i1", "c1", "dm");
        Insert("m1", "i1", "c1", At(2023, 2, 1), "lol lowercase");
        Insert("m2", "i1", "c1", At(2023, 3, 1), "THIS IS EMPHATIC!!!");
        Insert("future", "i1", "c1", At(2024, 1, 1), "future vocabulary");
        var analyzer = new ReplicaTemporalAnalyzer(db);

        var style = await analyzer.GetStyleAsync("p1", At(2023, 12, 31), "dm", CancellationToken.None,
            new HashSet<string> { "m1" });

        Assert.Equal(1, style.SampleSize);
        Assert.Equal(1, style.CapsRate);
        Assert.DoesNotContain("future", style.SignatureTokens);
        Assert.Equal("dm", style.ContextKind);

        var eras = await analyzer.GetErasAsync("p1", true, CancellationToken.None);
        Assert.Equal(2, eras.Count);
        Assert.Equal(2, eras.Single(e => e.Era == "2023").MessageCount);
    }

    [Fact]
    public void RetrievalScore_PrefersSameEraAndContextWithoutZeroingOldEvidence()
    {
        long cutoff = At(2024, 1, 1);
        float sameEra = ReplicaTemporalAnalyzer.RetrievalScore(0.8f, At(2023, 11, 1), cutoff, "dm", "dm");
        float old = ReplicaTemporalAnalyzer.RetrievalScore(0.8f, At(2018, 1, 1), cutoff, "dm", "dm");
        float wrongContext = ReplicaTemporalAnalyzer.RetrievalScore(0.8f, At(2023, 11, 1), cutoff, "guild_channel", "dm");

        Assert.True(sameEra > old);
        Assert.True(old > 0);
        Assert.True(sameEra > wrongContext);
    }

    [Fact]
    public void PromptContract_DisclosesSimulationAndTreatsArchiveAsData()
    {
        Assert.Contains("AI simulation", ReplicaPromptBuilder.ChatSystemPromptTemplate);
        Assert.Contains("NOT {DISPLAY_NAME} or a human", ReplicaPromptBuilder.ChatSystemPromptTemplate);
        Assert.Contains("untrusted quoted evidence", ReplicaPromptBuilder.ChatSystemPromptTemplate);
        Assert.DoesNotContain("You are not an AI", ReplicaPromptBuilder.ChatSystemPromptTemplate);
    }

    [Fact]
    public void MemoryPrompt_PreservesOldSummaryAndSimulationBoundary()
    {
        string prompt = ReplicaChatMemory.BuildSummaryPrompt("unresolved: project",
            new List<(long Id, string Role, string Content)> { (2, "user", "what about that project?") });

        Assert.Contains("unresolved: project", prompt);
        Assert.Contains("what about that project?", prompt);
        Assert.Contains("not real-world evidence", prompt);
    }

    private void Seed(string person, string identity, string conversation, string kind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO persons(person_id,display_name,created_at,updated_at) VALUES($p,$p,0,0);
INSERT INTO platform_identities(identity_id,person_id,platform,platform_user_id) VALUES($i,$p,'test',$i);
INSERT INTO conversations(conversation_id,platform,kind,channel_id) VALUES($c,'test',$kind,$c);";
        cmd.Parameters.AddWithValue("$p", person);
        cmd.Parameters.AddWithValue("$i", identity);
        cmd.Parameters.AddWithValue("$c", conversation);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.ExecuteNonQuery();
    }

    private void Insert(string id, string identity, string conversation, long sentAt, string content)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO messages(message_id,platform,platform_message_id,conversation_id,author_identity_id,sent_at,content,captured_at)
VALUES($id,'test',$id,$c,$i,$at,$content,$at)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", conversation);
        cmd.Parameters.AddWithValue("$i", identity);
        cmd.Parameters.AddWithValue("$at", sentAt);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.ExecuteNonQuery();
    }

    private static long At(int year, int month, int day) => new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { }
    }
}
