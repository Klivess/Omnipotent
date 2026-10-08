using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveRAG;

namespace Omnipotent.Tests.KliveRAG
{
    /// <summary>
    /// The knowledge search sits on KliveAgent's and every Projects wake's critical path, so it has to
    /// end on time however large the index grows. An unbounded FTS bm25 sort over every chunk that
    /// contains "the" or "account" held KliveAgent at "preparing context" for 5-12 minutes, while a
    /// "??" nudge (no searchable words) sailed straight through.
    /// </summary>
    public class KliveRAGBoundedSearchTests : IDisposable
    {
        private readonly string dbPath;
        private readonly KliveRAGDb db;
        private readonly RagIndexWriter writer;

        public KliveRAGBoundedSearchTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "kliverag_bounded_" + Guid.NewGuid().ToString("N") + ".db");
            db = new KliveRAGDb(dbPath);
            db.Migrate();
            writer = new RagIndexWriter(db);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }

        private static RagDocument Doc(string id, string content, string source = RagSource.RepoDocs) => new()
        {
            DocId = id,
            Source = source,
            Title = id,
            Content = content,
            ContentHash = RagChunker.Hash(content),
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SingleChunk = true,
        };

        // A statement that runs for minutes unless something stops it.
        private const string EndlessQuery =
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 5000000000) SELECT count(*) FROM n";

        [Fact]
        public void BindDeadline_AbortsAStatementThatIsAlreadyRunning()
        {
            using var conn = db.Open();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var timer = Stopwatch.StartNew();
            using (KliveRAGDb.BindDeadline(conn, deadline.Token))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = EndlessQuery;
                Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
            }
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"aborted after {timer.Elapsed}");

            // Unbound again: the same connection runs ordinary statements to completion.
            using var after = conn.CreateCommand();
            after.CommandText = "SELECT 41 + 1";
            Assert.Equal(42L, after.ExecuteScalar());
        }

        [Fact]
        public void BindDeadline_StopsAStatementStartedAfterTheDeadlinePassed()
        {
            // A one-off interrupt fired before the statement starts is a no-op in SQLite; the progress
            // handler has no such gap.
            using var conn = db.Open();
            using var expired = new CancellationTokenSource();
            expired.Cancel();
            using (KliveRAGDb.BindDeadline(conn, expired.Token))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = EndlessQuery;
                Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
            }
        }

        [Theory]
        [InlineData("use your computer to make a tumblr account using a KliveMail email address, then create API keys for that tumblr account, then send me the login details and the api keys for that tumblr account.",
            new[] { "klivemail", "tumblr", "account" }, new[] { "use", "your", "then", "that", "using", "make", "the", "and" })]
        [InlineData("i did the captcha, get me those keys", new[] { "captcha", "keys" }, new[] { "did", "the", "get", "those" })]
        public void QueryTerms_KeepTheTellingWords_AndDropTheOnesEveryChunkContains(string query, string[] kept, string[] dropped)
        {
            var terms = HybridRetriever.QueryTerms(query);
            foreach (var word in kept) Assert.Contains(word, terms);
            foreach (var word in dropped) Assert.DoesNotContain(word, terms);
            Assert.True(terms.Count <= HybridRetriever.MaxQueryTerms);
        }

        [Fact]
        public void QueryTerms_ForANudgeWithNoWords_IsEmpty()
        {
            Assert.Empty(HybridRetriever.QueryTerms("??"));
            Assert.Equal("", HybridRetriever.BuildFtsMatch("??"));
            Assert.Equal("", HybridRetriever.BuildFtsMatch("is it on the way?"));
        }

        [Fact]
        public async Task RecentWindow_LimitsTheLexicalLegToTheNewestChunks()
        {
            await writer.UpsertAsync(Doc("repodoc:old", "The quokka migration notes from long ago."));
            for (int i = 0; i < 10; i++)
                await writer.UpsertAsync(Doc($"repodoc:new{i}", $"Fresh operational note number {i} about the scheduler."));
            var retriever = new HybridRetriever(db, new RagEmbedQueue(db, new HttpClient(), _ => { }));

            var windowed = await retriever.SearchAsync("quokka migration",
                new RagSearchOptions { MaxResults = 5, RecentWindow = 5, Deadline = TimeSpan.FromSeconds(5) });
            Assert.DoesNotContain(windowed, h => h.DocId == "repodoc:old");

            var whole = await retriever.SearchAsync("quokka migration",
                new RagSearchOptions { MaxResults = 5, Deadline = TimeSpan.FromSeconds(5) });
            Assert.Contains(whole, h => h.DocId == "repodoc:old");
        }

        [Fact]
        public async Task Search_WithADeadline_ReturnsOnTime_WithWhateverTheLegsFound()
        {
            await writer.UpsertAsync(Doc("repodoc:trader", "The OmniTrader backtester runs a single multi-asset BacktestSession."));
            // The embedder is cold here (it would load or download the model), so the vector leg cannot
            // finish inside the deadline; the search must not wait for it.
            var retriever = new HybridRetriever(db, new RagEmbedQueue(db, new HttpClient(), _ => { }));

            var timer = Stopwatch.StartNew();
            var hits = await retriever.SearchAsync("OmniTrader backtester",
                new RagSearchOptions { MaxResults = 5, Deadline = TimeSpan.FromMilliseconds(300) });

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"search took {timer.Elapsed}");
            Assert.Contains(hits, h => h.DocId == "repodoc:trader");
        }
    }
}
