using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience.Analytics;
using Omnipotent.Services.Omniscience.Replica;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Search
{
    /// <summary>
    /// Corpus-wide semantic layer over message_embeddings (local MiniLM, 384-dim).
    /// A background job incrementally embeds every message authored by Tracked-tier
    /// persons; consumers are semantic search, person Q&amp;A retrieval, deduction
    /// hypothesis watchers and replica stimulus-matching. Brute-force cosine at query
    /// time — fine at expected scale, and person-scoped queries prune hard.
    /// </summary>
    public class MessageEmbeddingIndex : IDisposable
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly ReplicaEmbedder embedder;
        private readonly SemaphoreSlim embedBatchLock = new(1, 1);
        private readonly CancellationTokenSource cts = new();

        private const int BatchSize = 256;
        private const int MinContentChars = 8;

        public MessageEmbeddingIndex(Omniscience service, OmniscienceDb db, HttpClient http)
        {
            this.service = service;
            this.db = db;
            embedder = new ReplicaEmbedder(http, msg => _ = service.ServiceLog(msg));
        }

        public void Dispose()
        {
            cts.Cancel();
            embedder.Dispose();
        }

        public void StartBackgroundIndexing()
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMinutes(3), cts.Token); } catch { return; }
                while (!cts.IsCancellationRequested)
                {
                    int embedded = 0;
                    try { embedded = await EmbedNextBatchAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Embedding index batch failed"); }
                    try { await Task.Delay(embedded >= BatchSize ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(10), cts.Token); }
                    catch { break; }
                }
            });
        }

        /// <summary>
        /// Embeds the next batch of unembedded messages from Tracked-tier persons.
        /// Returns how many were embedded (0 = caught up).
        /// </summary>
        public async Task<int> EmbedNextBatchAsync(CancellationToken ct)
        {
            var rows = new List<(string MessageId, string Content)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT m.message_id, m.content
                    FROM messages m
                    JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                    JOIN persons p ON p.person_id = pi.person_id
                    WHERE p.tier = 'tracked'
                      AND m.content IS NOT NULL AND length(m.content) >= $min
                      AND NOT EXISTS (SELECT 1 FROM message_embeddings e WHERE e.message_id = m.message_id)
                    LIMIT $n";
                cmd.Parameters.AddWithValue("$min", MinContentChars);
                cmd.Parameters.AddWithValue("$n", BatchSize);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));

                // Second source: stimulus messages of tracked persons' reply pairs. Their
                // authors are usually OTHER people (not tracked), but replica
                // stimulus-matching needs them in the index.
                if (rows.Count < BatchSize)
                {
                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = @"SELECT DISTINCT m.message_id, m.content
                        FROM stimulus_reply_pairs srp
                        JOIN messages m ON m.message_id = srp.stimulus_message_id
                        WHERE m.content IS NOT NULL AND length(m.content) >= $min
                          AND NOT EXISTS (SELECT 1 FROM message_embeddings e WHERE e.message_id = m.message_id)
                        LIMIT $n";
                    cmd2.Parameters.AddWithValue("$min", MinContentChars);
                    cmd2.Parameters.AddWithValue("$n", BatchSize - rows.Count);
                    using var r2 = cmd2.ExecuteReader();
                    while (r2.Read()) rows.Add((r2.GetString(0), r2.GetString(1)));
                }
            }
            if (rows.Count == 0) return 0;
            await EmbedAndStoreAsync(rows, ct);
            return rows.Count;
        }

        /// <summary>
        /// Ensures every message by this person is embedded (used by replica training and
        /// tier promotion). Reports progress via the optional callback (done, total).
        /// </summary>
        public async Task<int> EnsurePersonEmbeddedAsync(string personId, Func<int, int, Task>? progress, CancellationToken ct)
        {
            var rows = new List<(string MessageId, string Content)>();
            using (var conn = db.Open())
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return 0;
                using var cmd = conn.CreateCommand();
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT m.message_id, m.content FROM messages m
                    WHERE m.author_identity_id IN ({inC})
                      AND m.content IS NOT NULL AND length(m.content) >= $min
                      AND NOT EXISTS (SELECT 1 FROM message_embeddings e WHERE e.message_id = m.message_id)";
                cmd.Parameters.AddWithValue("$min", MinContentChars);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
            }

            int done = 0;
            foreach (var chunk in rows.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                await EmbedAndStoreAsync(chunk.ToList(), ct);
                done += chunk.Length;
                if (progress != null) await progress(done, rows.Count);
            }
            return done;
        }

        private async Task EmbedAndStoreAsync(List<(string MessageId, string Content)> rows, CancellationToken ct)
        {
            await embedBatchLock.WaitAsync(ct);
            try
            {
                await embedder.EnsureReadyAsync(ct);
                var vecs = await embedder.EmbedBatchAsync(rows.Select(r => r.Content).ToList(), ct);

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO message_embeddings(message_id, embedding, embedded_at) VALUES($m,$e,$t)";
                    var pm = cmd.Parameters.Add("$m", SqliteType.Text);
                    var pe = cmd.Parameters.Add("$e", SqliteType.Blob);
                    var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        pm.Value = rows[i].MessageId;
                        pe.Value = ReplicaEmbedder.PackEmbedding(vecs[i]);
                        pt.Value = now;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                finally { db.WriteLock.Release(); }
            }
            finally { embedBatchLock.Release(); }
        }

        /// <summary>Embeds an arbitrary query string (also used by hypothesis watchers).</summary>
        public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
        {
            await embedder.EnsureReadyAsync(ct);
            return await embedder.EmbedAsync(query, ct);
        }

        /// <summary>
        /// Semantic search: top-K messages by cosine similarity. personId scopes the scan
        /// to one person's messages; null scans the whole index (streamed, top-K kept).
        /// </summary>
        public async Task<List<(string MessageId, float Score)>> SearchAsync(string query, string? personId, int limit, CancellationToken ct)
        {
            var queryVec = await EmbedQueryAsync(query, ct);
            var top = new List<(string MessageId, float Score)>();
            float worstKept = float.MinValue;

            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            if (!string.IsNullOrEmpty(personId))
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return top;
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT e.message_id, e.embedding FROM message_embeddings e
                    JOIN messages m ON m.message_id = e.message_id
                    WHERE m.author_identity_id IN ({inC})";
            }
            else
            {
                cmd.CommandText = "SELECT message_id, embedding FROM message_embeddings";
            }

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var v = ReplicaEmbedder.UnpackEmbedding((byte[])r.GetValue(1));
                if (v.Length != queryVec.Length) continue;
                float score = ReplicaEmbedder.CosineSimilarity(queryVec, v);
                if (top.Count < limit)
                {
                    top.Add((r.GetString(0), score));
                    if (top.Count == limit) worstKept = top.Min(t => t.Score);
                }
                else if (score > worstKept)
                {
                    int worstIdx = 0; float worst = float.MaxValue;
                    for (int i = 0; i < top.Count; i++)
                        if (top[i].Score < worst) { worst = top[i].Score; worstIdx = i; }
                    top[worstIdx] = (r.GetString(0), score);
                    worstKept = top.Min(t => t.Score);
                }
            }
            return top.OrderByDescending(t => t.Score).ToList();
        }
    }
}
