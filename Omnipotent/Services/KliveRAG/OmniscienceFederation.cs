using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Query-time federation into Omniscience's raw-message semantic index. Rather than copying
    /// millions of messages into KliveRAG's store, a message-scoped query is forwarded to
    /// Omniscience's existing <c>MessageEmbeddingIndex</c> and the top hits are hydrated on the fly.
    /// Used only on the explicit tool path (opt-in via <c>includeMessages</c>), never on the latency-
    /// sensitive injection path.
    /// </summary>
    public sealed class OmniscienceFederation
    {
        private readonly Func<Task<Omniscience.Omniscience?>> resolveOmniscience;
        private readonly Action<string> log;

        public OmniscienceFederation(Func<Task<Omniscience.Omniscience?>> resolveOmniscience, Action<string> log)
        {
            this.resolveOmniscience = resolveOmniscience;
            this.log = log;
        }

        public async Task<List<RagHit>> SearchAsync(string query, int limit, CancellationToken ct)
        {
            var hits = new List<RagHit>();
            Omniscience.Omniscience? omni;
            try { omni = await resolveOmniscience(); } catch { return hits; }
            if (omni?.SearchIndex == null || omni.Db == null) return hits;

            List<(string MessageId, float Score)> matches;
            try { matches = await omni.SearchIndex.SearchAsync(query, null, limit, ct); }
            catch (Exception ex) { log($"[KliveRAG] omniscience federation failed: {ex.Message}"); return hits; }
            if (matches.Count == 0) return hits;

            try
            {
                using var conn = omni.Db.Open();
                foreach (var (messageId, score) in matches)
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT m.content, m.sent_at, COALESCE(p.display_name, pi.display_name, pi.platform_username)
                        FROM messages m
                        LEFT JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                        LEFT JOIN persons p ON p.person_id = pi.person_id
                        WHERE m.message_id=$id";
                    cmd.Parameters.AddWithValue("$id", messageId);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read() || r.IsDBNull(0)) continue;
                    string content = r.GetString(0);
                    long sentAt = r.IsDBNull(1) ? RagTime.Now : r.GetInt64(1);
                    string author = r.IsDBNull(2) ? "someone" : r.GetString(2);
                    hits.Add(new RagHit
                    {
                        ChunkId = $"omnimsg:{messageId}",
                        DocId = $"omnimsg:{messageId}",
                        Source = RagSource.Omniscience,
                        Title = $"Message · {author}",
                        Text = content,
                        CreatedAtUnixMs = sentAt,
                        Score = score,
                    });
                }
            }
            catch (Exception ex) { log($"[KliveRAG] omniscience federation hydrate failed: {ex.Message}"); }
            return hits;
        }
    }
}
