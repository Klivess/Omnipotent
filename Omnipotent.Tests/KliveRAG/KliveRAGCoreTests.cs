using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveRAG;

namespace Omnipotent.Tests.KliveRAG
{
    /// <summary>
    /// Phase-1 core: chunker determinism, migration + FTS5 availability, writer change-detection,
    /// and the lexical retrieval leg (embeddings excluded — those need the ONNX model download and
    /// are exercised at runtime, not in a unit test).
    /// </summary>
    public class KliveRAGCoreTests : IDisposable
    {
        private readonly string dbPath;
        private readonly KliveRAGDb db;
        private readonly RagIndexWriter writer;

        public KliveRAGCoreTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "kliverag_test_" + Guid.NewGuid().ToString("N") + ".db");
            db = new KliveRAGDb(dbPath);
            db.Migrate();
            writer = new RagIndexWriter(db);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }

        private static RagDocument Doc(string id, string content, string source = RagSource.RepoDocs, bool single = false, bool md = false) => new()
        {
            DocId = id,
            Source = source,
            Title = id,
            Content = content,
            ContentHash = RagChunker.Hash(content),
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SingleChunk = single,
            IsMarkdown = md,
        };

        [Fact]
        public void Migrate_ReachesV1_AndFtsAvailable()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
            // The shipped Microsoft.Data.Sqlite bundle includes FTS5.
            Assert.True(db.FtsAvailable);
        }

        [Theory]
        [InlineData("rag_documents")]
        [InlineData("rag_chunks")]
        [InlineData("rag_chunk_embeddings")]
        [InlineData("rag_cursors")]
        [InlineData("rag_web_cache")]
        [InlineData("rag_chunks_fts")]
        public void Migrate_CreatesTable(string table)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE name=$t";
            cmd.Parameters.AddWithValue("$t", table);
            Assert.NotNull(cmd.ExecuteScalar());
        }

        [Fact]
        public void Chunker_IsDeterministic_SameIdsAndHashes()
        {
            var doc = Doc("repodoc:x", string.Concat(Enumerable.Repeat("The quick brown fox jumps. ", 200)));
            var a = RagChunker.Chunk(doc);
            var b = RagChunker.Chunk(doc);
            Assert.True(a.Count > 1);
            Assert.Equal(a.Select(c => c.ChunkId), b.Select(c => c.ChunkId));
            Assert.Equal(a.Select(c => c.ContentHash), b.Select(c => c.ContentHash));
        }

        [Fact]
        public void Chunker_SingleChunk_YieldsOneChunk()
        {
            var doc = Doc("projevt:p:1", string.Concat(Enumerable.Repeat("word ", 500)), RagSource.ProjectsEvents, single: true);
            Assert.Single(RagChunker.Chunk(doc));
        }

        [Fact]
        public async Task Writer_UnchangedDoc_IsNoOp()
        {
            var doc = Doc("repodoc:a", "hello world alpha beta gamma");
            Assert.True(await writer.UpsertAsync(doc));
            Assert.False(await writer.UpsertAsync(doc)); // identical hash → skipped
        }

        [Fact]
        public async Task Writer_GrownDoc_OnlyReembedsChangedChunks()
        {
            string body = string.Concat(Enumerable.Repeat("Sentence about foxes and hounds. ", 120));
            var v1 = Doc("repodoc:grow", body);
            await writer.UpsertAsync(v1);
            int chunksV1 = CountChunks("repodoc:grow");
            var pendingBefore = CountPending();

            // Simulate an append-only growth (a conversation gaining a turn): earlier chunk hashes are stable.
            var v2 = Doc("repodoc:grow", body + "\n\nA brand new trailing paragraph about eagles.");
            v2.ContentHash = RagChunker.Hash(v2.Content);
            Assert.True(await writer.UpsertAsync(v2));

            Assert.True(CountChunks("repodoc:grow") >= chunksV1);
            Assert.True(CountPending() > 0); // new/changed chunks are queued for embedding
        }

        [Fact]
        public async Task Writer_Delete_RemovesDocAndChunks()
        {
            await writer.UpsertAsync(Doc("repodoc:del", "content to be tombstoned"));
            Assert.True(CountChunks("repodoc:del") > 0);
            await writer.DeleteAsync("repodoc:del");
            Assert.Equal(0, CountChunks("repodoc:del"));
        }

        [Fact]
        public async Task Cursor_RoundTrips()
        {
            Assert.Null(writer.GetCursor("repo-docs"));
            await writer.SetCursorAsync("repo-docs", "12345");
            Assert.Equal("12345", writer.GetCursor("repo-docs"));
            await writer.SetCursorAsync("repo-docs", "67890");
            Assert.Equal("67890", writer.GetCursor("repo-docs"));
        }

        [Fact]
        public async Task Retriever_LexicalLeg_FindsDocumentByKeyword()
        {
            await writer.UpsertAsync(Doc("repodoc:trader", "The OmniTrader backtester runs a single multi-asset BacktestSession."));
            await writer.UpsertAsync(Doc("repodoc:agent", "KliveAgent is a runtime service orchestrator for Klives."));

            var retriever = new HybridRetriever(db, new RagEmbedQueue(db, new HttpClient(), _ => { }));
            // Cold embedder → vector leg returns empty; the FTS lexical leg must still find the match.
            var hits = await retriever.SearchAsync("OmniTrader backtester", new RagSearchOptions { MaxResults = 5 });

            Assert.NotEmpty(hits);
            Assert.Equal("repodoc:trader", hits[0].DocId);
            Assert.True(hits[0].LexicalRank >= 0);
        }

        [Fact]
        public async Task Retriever_ExcludeProjectId_DropsOwnProjectEvents()
        {
            await writer.UpsertAsync(Doc("projevt:P1:5", "decision to use SearXNG for web search", RagSource.ProjectsEvents, single: true));
            await writer.UpsertAsync(Doc("projevt:P2:9", "another project also discussed SearXNG web search", RagSource.ProjectsEvents, single: true));

            var retriever = new HybridRetriever(db, new RagEmbedQueue(db, new HttpClient(), _ => { }));
            var hits = await retriever.SearchAsync("SearXNG web search", new RagSearchOptions { MaxResults = 5, ExcludeProjectId = "P1" });

            Assert.NotEmpty(hits);
            Assert.DoesNotContain(hits, h => h.DocId.StartsWith("projevt:P1:"));
            Assert.Contains(hits, h => h.DocId.StartsWith("projevt:P2:"));
        }

        [Fact]
        public async Task PreChunks_AppendOnlyGrowth_OnlyNewChunkReembeds()
        {
            // A conversation with two turn-pairs.
            var v1 = Doc("agentconv:c1", "");
            v1.PreChunks = new List<string> { "User: hi\n\nAgent: hello there", "User: how are you\n\nAgent: doing well" };
            v1.Content = string.Join("\n\n", v1.PreChunks);
            v1.ContentHash = RagChunker.Hash(v1.Content);
            await writer.UpsertAsync(v1);

            var idsAfterV1 = ChunkIds("agentconv:c1");
            Assert.Equal(2, idsAfterV1.Count);
            // Clear the pending flag to simulate the embed queue having caught up.
            MarkAllEmbedded();
            Assert.Equal(0, CountPending());

            // Add a third turn-pair (append-only). Earlier chunk ids + hashes are unchanged.
            var v2 = Doc("agentconv:c1", "");
            v2.PreChunks = new List<string>(v1.PreChunks) { "User: bye\n\nAgent: see you" };
            v2.Content = string.Join("\n\n", v2.PreChunks);
            v2.ContentHash = RagChunker.Hash(v2.Content);
            Assert.True(await writer.UpsertAsync(v2));

            var idsAfterV2 = ChunkIds("agentconv:c1");
            Assert.Equal(3, idsAfterV2.Count);
            Assert.Equal(new[] { "agentconv:c1#0", "agentconv:c1#1" }, idsAfterV1);
            Assert.Equal(1, CountPending()); // only the new third turn awaits embedding
        }

        private List<string> ChunkIds(string docId)
        {
            var ids = new List<string>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT chunk_id FROM rag_chunks WHERE doc_id=$d ORDER BY seq";
            cmd.Parameters.AddWithValue("$d", docId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        private void MarkAllEmbedded()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE rag_chunks SET embedded_at=1 WHERE embedded_at IS NULL";
            cmd.ExecuteNonQuery();
        }

        private int CountChunks(string docId)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rag_chunks WHERE doc_id=$d";
            cmd.Parameters.AddWithValue("$d", docId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private int CountPending()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rag_chunks WHERE embedded_at IS NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
}
