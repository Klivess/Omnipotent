using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// The single write path into the index. Upserts documents with content-hash change
    /// detection: an unchanged document is a no-op; a changed one re-chunks and only the chunks
    /// whose per-chunk hash changed are re-inserted (and thus re-embedded). Deleted chunks cascade
    /// their embeddings + FTS rows via triggers/foreign keys. All writes serialise on the db WriteLock.
    /// </summary>
    public sealed class RagIndexWriter
    {
        private readonly KliveRAGDb db;

        public RagIndexWriter(KliveRAGDb db)
        {
            this.db = db;
        }

        /// <summary>Upserts one document. Returns true if anything changed (doc was new or content differed).</summary>
        public async Task<bool> UpsertAsync(RagDocument doc, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(doc.DocId) || string.IsNullOrEmpty(doc.Content)) return false;
            var chunks = RagChunker.Chunk(doc);
            if (chunks.Count == 0) return false;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();

                // Skip if the document content is byte-identical to what's stored.
                using (var check = conn.CreateCommand())
                {
                    check.CommandText = "SELECT content_hash FROM rag_documents WHERE doc_id=$id";
                    check.Parameters.AddWithValue("$id", doc.DocId);
                    var existing = check.ExecuteScalar() as string;
                    if (existing == doc.ContentHash) return false;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var tx = conn.BeginTransaction();

                using (var up = conn.CreateCommand())
                {
                    up.Transaction = tx;
                    up.CommandText = @"
INSERT INTO rag_documents(doc_id, source, title, uri, content, content_hash, created_at, indexed_at, expires_at, meta_json)
VALUES($id,$src,$title,$uri,$content,$hash,$created,$indexed,$expires,$meta)
ON CONFLICT(doc_id) DO UPDATE SET
    source=$src, title=$title, uri=$uri, content=$content, content_hash=$hash,
    created_at=$created, indexed_at=$indexed, expires_at=$expires, meta_json=$meta";
                    up.Parameters.AddWithValue("$id", doc.DocId);
                    up.Parameters.AddWithValue("$src", doc.Source);
                    up.Parameters.AddWithValue("$title", (object?)doc.Title ?? DBNull.Value);
                    up.Parameters.AddWithValue("$uri", (object?)doc.Uri ?? DBNull.Value);
                    up.Parameters.AddWithValue("$content", doc.Content);
                    up.Parameters.AddWithValue("$hash", doc.ContentHash);
                    up.Parameters.AddWithValue("$created", doc.CreatedAtUnixMs);
                    up.Parameters.AddWithValue("$indexed", now);
                    up.Parameters.AddWithValue("$expires", (object?)doc.ExpiresAtUnixMs ?? DBNull.Value);
                    up.Parameters.AddWithValue("$meta", (object?)doc.MetaJson ?? DBNull.Value);
                    up.ExecuteNonQuery();
                }

                // Existing chunk hashes keyed by chunk_id, so we can diff and only touch what changed.
                var existingHashes = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var ex = conn.CreateCommand())
                {
                    ex.Transaction = tx;
                    ex.CommandText = "SELECT chunk_id, content_hash FROM rag_chunks WHERE doc_id=$id";
                    ex.Parameters.AddWithValue("$id", doc.DocId);
                    using var r = ex.ExecuteReader();
                    while (r.Read()) existingHashes[r.GetString(0)] = r.GetString(1);
                }

                var newIds = new HashSet<string>(chunks.Select(c => c.ChunkId), StringComparer.Ordinal);

                // Delete chunks that no longer exist (and any whose text changed — re-inserted below).
                foreach (var kv in existingHashes)
                {
                    bool stillPresent = newIds.Contains(kv.Key);
                    bool changed = stillPresent && chunks.First(c => c.ChunkId == kv.Key).ContentHash != kv.Value;
                    if (!stillPresent || changed)
                    {
                        using var del = conn.CreateCommand();
                        del.Transaction = tx;
                        del.CommandText = "DELETE FROM rag_chunks WHERE chunk_id=$cid";
                        del.Parameters.AddWithValue("$cid", kv.Key);
                        del.ExecuteNonQuery();
                    }
                }

                // Insert new or changed chunks (embedded_at NULL → picked up by the embed queue).
                foreach (var c in chunks)
                {
                    if (existingHashes.TryGetValue(c.ChunkId, out var h) && h == c.ContentHash) continue;
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"
INSERT INTO rag_chunks(chunk_id, doc_id, seq, source, created_at, text, content_hash, token_estimate, embedded_at)
VALUES($cid,$did,$seq,$src,$created,$text,$hash,$tok,NULL)";
                    ins.Parameters.AddWithValue("$cid", c.ChunkId);
                    ins.Parameters.AddWithValue("$did", c.DocId);
                    ins.Parameters.AddWithValue("$seq", c.Seq);
                    ins.Parameters.AddWithValue("$src", c.Source);
                    ins.Parameters.AddWithValue("$created", c.CreatedAtUnixMs);
                    ins.Parameters.AddWithValue("$text", c.Text);
                    ins.Parameters.AddWithValue("$hash", c.ContentHash);
                    ins.Parameters.AddWithValue("$tok", c.TokenEstimate);
                    ins.ExecuteNonQuery();
                }

                tx.Commit();
                return true;
            }
            finally { db.WriteLock.Release(); }
        }

        /// <summary>Removes a document and all its chunks/embeddings (tombstone for deleted sources).</summary>
        public async Task DeleteAsync(string docId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(docId)) return;
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM rag_documents WHERE doc_id=$id"; // cascades to chunks + embeddings
                del.Parameters.AddWithValue("$id", docId);
                del.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        /// <summary>All document ids currently stored for a source (for tombstone reconciliation).</summary>
        public List<string> GetDocIdsForSource(string source)
        {
            var ids = new List<string>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT doc_id FROM rag_documents WHERE source=$s";
            cmd.Parameters.AddWithValue("$s", source);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        /// <summary>Reads a connector's persisted watermark (null if never run).</summary>
        public string? GetCursor(string connector)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT watermark FROM rag_cursors WHERE connector=$c";
            cmd.Parameters.AddWithValue("$c", connector);
            return cmd.ExecuteScalar() as string;
        }

        /// <summary>Persists a connector's watermark.</summary>
        public async Task SetCursorAsync(string connector, string watermark, CancellationToken ct = default)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO rag_cursors(connector, watermark, updated_at) VALUES($c,$w,$t)
ON CONFLICT(connector) DO UPDATE SET watermark=$w, updated_at=$t";
                cmd.Parameters.AddWithValue("$c", connector);
                cmd.Parameters.AddWithValue("$w", watermark);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
