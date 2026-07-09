using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience.Replica;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Owns the local MiniLM embedder and the background loop that embeds pending chunks.
    /// Shares Omniscience's <see cref="ReplicaEmbedder"/> type (and its on-disk model files, which
    /// resolve to the same shared directory), so no model is downloaded twice. Loop shape is cloned
    /// from Omniscience's MessageEmbeddingIndex: fast cadence while catching up, slow while idle.
    /// </summary>
    public sealed class RagEmbedQueue : IDisposable
    {
        private const string CurrentModel = "all-MiniLM-L6-v2";
        private const int BatchSize = 128;

        private readonly KliveRAGDb db;
        private readonly ReplicaEmbedder embedder;
        private readonly Action<string> log;
        private readonly SemaphoreSlim embedGate = new(1, 1);
        private readonly CancellationTokenSource cts = new();

        public RagEmbedQueue(KliveRAGDb db, HttpClient http, Action<string> log)
        {
            this.db = db;
            this.log = log;
            embedder = new ReplicaEmbedder(http, log);
        }

        public void Dispose()
        {
            cts.Cancel();
            embedder.Dispose();
        }

        /// <summary>True once the ONNX session has been built at least once (model present + loaded).</summary>
        public bool IsWarm { get; private set; }

        /// <summary>Warms the ONNX session (first call downloads the ~25 MB model if absent).</summary>
        public async Task EnsureReadyAsync(CancellationToken ct)
        {
            await embedder.EnsureReadyAsync(ct);
            IsWarm = true;
        }

        /// <summary>Embeds a query string for the retriever's semantic leg.</summary>
        public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
        {
            await embedder.EnsureReadyAsync(ct);
            return await embedder.EmbedAsync(query, ct);
        }

        public void StartBackgroundEmbedding()
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cts.Token); } catch { return; }
                while (!cts.IsCancellationRequested)
                {
                    int embedded = 0;
                    try { embedded = await EmbedNextBatchAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { log($"[KliveRAG] embed batch failed: {ex.Message}"); }
                    try { await Task.Delay(embedded >= BatchSize ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(5), cts.Token); }
                    catch { break; }
                }
            });
        }

        /// <summary>Embeds the next batch of pending chunks. Returns count embedded (0 = caught up).</summary>
        public async Task<int> EmbedNextBatchAsync(CancellationToken ct)
        {
            var rows = new List<(string ChunkId, string Text)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT chunk_id, text FROM rag_chunks WHERE embedded_at IS NULL LIMIT $n";
                cmd.Parameters.AddWithValue("$n", BatchSize);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
            }
            if (rows.Count == 0) return 0;

            await embedGate.WaitAsync(ct);
            try
            {
                await embedder.EnsureReadyAsync(ct);
                var vecs = await embedder.EmbedBatchAsync(rows.Select(r => r.Text).ToList(), ct);

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();

                    using var embCmd = conn.CreateCommand();
                    embCmd.Transaction = tx;
                    embCmd.CommandText = "INSERT OR REPLACE INTO rag_chunk_embeddings(chunk_id, embedding, model) VALUES($c,$e,$m)";
                    var pc = embCmd.Parameters.Add("$c", SqliteType.Text);
                    var pe = embCmd.Parameters.Add("$e", SqliteType.Blob);
                    var pm = embCmd.Parameters.Add("$m", SqliteType.Text);

                    using var markCmd = conn.CreateCommand();
                    markCmd.Transaction = tx;
                    markCmd.CommandText = "UPDATE rag_chunks SET embedded_at=$t WHERE chunk_id=$c";
                    var mt = markCmd.Parameters.Add("$t", SqliteType.Integer);
                    var mc = markCmd.Parameters.Add("$c", SqliteType.Text);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    for (int i = 0; i < rows.Count; i++)
                    {
                        pc.Value = rows[i].ChunkId;
                        pe.Value = ReplicaEmbedder.PackEmbedding(vecs[i]);
                        pm.Value = CurrentModel;
                        embCmd.ExecuteNonQuery();

                        mt.Value = now;
                        mc.Value = rows[i].ChunkId;
                        markCmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                finally { db.WriteLock.Release(); }
            }
            finally { embedGate.Release(); }

            return rows.Count;
        }

        /// <summary>Count of chunks still awaiting an embedding (for /stats).</summary>
        public int PendingCount()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rag_chunks WHERE embedded_at IS NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
}
