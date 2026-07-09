using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveRAG;
using Omnipotent.Services.KliveRAG.Web;

namespace Omnipotent.Tests.KliveRAG
{
    /// <summary>
    /// Phase-4 web layer: the pure HTML→text extractor and the web-cache TTL eviction.
    /// The SearXNG container + live fetch are exercised at runtime (they need Docker/network).
    /// </summary>
    public class KliveRAGWebTests : IDisposable
    {
        private readonly string dbPath;
        private readonly KliveRAGDb db;
        private readonly RagIndexWriter writer;

        public KliveRAGWebTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "kliverag_web_" + Guid.NewGuid().ToString("N") + ".db");
            db = new KliveRAGDb(dbPath);
            db.Migrate();
            writer = new RagIndexWriter(db);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }

        [Fact]
        public void HtmlExtractor_StripsScriptsAndTags_KeepsText()
        {
            string html = "<html><head><title>My Page</title><style>.x{color:red}</style></head>" +
                          "<body><nav>menu junk</nav><h1>Hello</h1><p>First para &amp; more.</p>" +
                          "<script>alert('x')</script><p>Second para.</p></body></html>";
            string text = HtmlTextExtractor.ExtractText(html);

            Assert.Equal("My Page", HtmlTextExtractor.ExtractTitle(html));
            Assert.Contains("Hello", text);
            Assert.Contains("First para & more.", text); // entity decoded
            Assert.Contains("Second para.", text);
            Assert.DoesNotContain("alert", text);   // script dropped
            Assert.DoesNotContain("color:red", text); // style dropped
            Assert.DoesNotContain("menu junk", text); // nav dropped
        }

        [Fact]
        public async Task WebIngest_EvictExpired_RemovesExpiredWebDocsOnly()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // One expired web doc, one still-fresh web doc.
            await writer.UpsertAsync(new RagDocument
            {
                DocId = "web:expired", Source = RagSource.Web, Title = "old", Content = "expired page content here",
                ContentHash = RagChunker.Hash("expired page content here"),
                CreatedAtUnixMs = now - 1000, ExpiresAtUnixMs = now - 1, SingleChunk = true,
            });
            await writer.UpsertAsync(new RagDocument
            {
                DocId = "web:fresh", Source = RagSource.Web, Title = "new", Content = "fresh page content here",
                ContentHash = RagChunker.Hash("fresh page content here"),
                CreatedAtUnixMs = now, ExpiresAtUnixMs = now + 1_000_000, SingleChunk = true,
            });
            InsertCacheRow("hash_expired", now - 1);
            InsertCacheRow("hash_fresh", now + 1_000_000);

            var pipeline = new WebIngestPipeline(db, writer, new WebFetcher(new HttpClient()), _ => { });
            await pipeline.EvictExpiredAsync(CancellationToken.None);

            Assert.False(DocExists("web:expired"));
            Assert.True(DocExists("web:fresh"));
            Assert.Equal(0, ChunkCount("web:expired")); // chunks cascaded
            Assert.False(CacheRowExists("hash_expired"));
            Assert.True(CacheRowExists("hash_fresh"));
        }

        private void InsertCacheRow(string urlHash, long expiresAt)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO rag_web_cache(url_hash,url,fetched_at,expires_at,status,doc_id) VALUES($h,'http://x',0,$e,200,NULL)";
            cmd.Parameters.AddWithValue("$h", urlHash);
            cmd.Parameters.AddWithValue("$e", expiresAt);
            cmd.ExecuteNonQuery();
        }

        private bool DocExists(string docId) => Scalar("SELECT COUNT(*) FROM rag_documents WHERE doc_id=$v", docId) > 0;
        private bool CacheRowExists(string hash) => Scalar("SELECT COUNT(*) FROM rag_web_cache WHERE url_hash=$v", hash) > 0;
        private int ChunkCount(string docId) => Scalar("SELECT COUNT(*) FROM rag_chunks WHERE doc_id=$v", docId);

        private int Scalar(string sql, string val)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$v", val);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
}
