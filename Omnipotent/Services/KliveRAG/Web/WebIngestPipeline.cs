using System;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Web
{
    /// <summary>
    /// Turns a URL into an indexed <c>web</c> document: fetch → extract text → upsert (with a TTL) →
    /// its chunks get embedded by the background queue like any other source. Successful pages are
    /// cached for 7 days (1 day for time-ranged/news queries); failures are negative-cached for 1 hour
    /// so a dead link isn't re-hammered. No LLM summarisation in v1 — raw extracted text only (zero cost).
    /// </summary>
    public sealed class WebIngestPipeline
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan FreshTtl = TimeSpan.FromDays(1);
        private static readonly TimeSpan FailTtl = TimeSpan.FromHours(1);

        private readonly KliveRAGDb db;
        private readonly RagIndexWriter writer;
        private readonly WebFetcher fetcher;
        private readonly Action<string> log;

        public WebIngestPipeline(KliveRAGDb db, RagIndexWriter writer, WebFetcher fetcher, Action<string> log)
        {
            this.db = db;
            this.writer = writer;
            this.fetcher = fetcher;
            this.log = log ?? (_ => { });
        }

        public sealed class IngestResult
        {
            public string Url = "";
            public string? DocId;
            public string? Title;
            public string? Text;
            public string? Error;
            public bool FromCache;
            public bool Ok => Error == null && DocId != null;
        }

        /// <summary>Fetches+indexes a URL (or serves it from cache). <paramref name="freshOnly"/> shortens the TTL.</summary>
        public async Task<IngestResult> IngestAsync(string url, bool freshOnly, CancellationToken ct)
        {
            var result = new IngestResult { Url = url };
            string norm = NormalizeUrl(url);
            string urlHash = RagChunker.Hash(norm);
            long now = RagTime.Now;

            // Cache hit?
            var cached = ReadCache(urlHash);
            if (cached != null && cached.Value.ExpiresAt > now)
            {
                if (cached.Value.DocId != null)
                {
                    result.DocId = cached.Value.DocId;
                    result.FromCache = true;
                    result.Text = writer_GetDocText(cached.Value.DocId);
                    return result;
                }
                result.Error = $"Recently failed to fetch (cached): HTTP {cached.Value.Status}.";
                result.FromCache = true;
                return result;
            }

            var fetch = await fetcher.FetchAsync(url, ct);
            if (!fetch.Ok)
            {
                await WriteCacheAsync(urlHash, norm, fetch.Status, now + (long)FailTtl.TotalMilliseconds, null, ct);
                result.Error = fetch.Error ?? "Fetch failed.";
                return result;
            }

            string title = HtmlTextExtractor.ExtractTitle(fetch.Html!);
            string text = HtmlTextExtractor.ExtractText(fetch.Html!);
            if (text.Length < 40)
            {
                await WriteCacheAsync(urlHash, norm, fetch.Status, now + (long)FailTtl.TotalMilliseconds, null, ct);
                result.Error = "Page had no extractable text.";
                return result;
            }

            long ttlMs = (long)(freshOnly ? FreshTtl : DefaultTtl).TotalMilliseconds;
            string docId = $"web:{RagChunker.ShortHash(norm)}";
            var doc = new RagDocument
            {
                DocId = docId,
                Source = RagSource.Web,
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Uri = url,
                Content = text,
                ContentHash = RagChunker.Hash(text),
                CreatedAtUnixMs = now,
                ExpiresAtUnixMs = now + ttlMs,
                MetaJson = $"{{\"url\":{Newtonsoft.Json.JsonConvert.ToString(url)}}}",
            };
            await writer.UpsertAsync(doc, ct);
            await WriteCacheAsync(urlHash, norm, fetch.Status, now + ttlMs, docId, ct);

            result.DocId = docId;
            result.Title = doc.Title;
            result.Text = text;
            return result;
        }

        // ── cache table ──

        private (long ExpiresAt, int Status, string? DocId)? ReadCache(string urlHash)
        {
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT expires_at, status, doc_id FROM rag_web_cache WHERE url_hash=$h";
                cmd.Parameters.AddWithValue("$h", urlHash);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return (r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
            catch { return null; }
        }

        private async Task WriteCacheAsync(string urlHash, string url, int status, long expiresAt, string? docId, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO rag_web_cache(url_hash, url, fetched_at, expires_at, status, doc_id) VALUES($h,$u,$f,$e,$s,$d)
ON CONFLICT(url_hash) DO UPDATE SET url=$u, fetched_at=$f, expires_at=$e, status=$s, doc_id=$d";
                cmd.Parameters.AddWithValue("$h", urlHash);
                cmd.Parameters.AddWithValue("$u", url);
                cmd.Parameters.AddWithValue("$f", RagTime.Now);
                cmd.Parameters.AddWithValue("$e", expiresAt);
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$d", (object?)docId ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { log($"[KliveRAG] web cache write failed: {ex.Message}"); }
            finally { db.WriteLock.Release(); }
        }

        private string? writer_GetDocText(string docId)
        {
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT content FROM rag_documents WHERE doc_id=$id";
                cmd.Parameters.AddWithValue("$id", docId);
                return cmd.ExecuteScalar() as string;
            }
            catch { return null; }
        }

        private static string NormalizeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().TrimEnd('/');
            string path = uri.AbsolutePath.TrimEnd('/');
            return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{path}{uri.Query}".TrimEnd('/');
        }

        /// <summary>Nightly TTL eviction: drop expired web docs (chunks + embeddings cascade) and stale cache rows.</summary>
        public async Task EvictExpiredAsync(CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                long now = RagTime.Now;
                using var conn = db.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM rag_documents WHERE source=$s AND expires_at IS NOT NULL AND expires_at < $now";
                    cmd.Parameters.AddWithValue("$s", RagSource.Web);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM rag_web_cache WHERE expires_at < $now";
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { log($"[KliveRAG] web TTL eviction failed: {ex.Message}"); }
            finally { db.WriteLock.Release(); }
        }
    }
}
