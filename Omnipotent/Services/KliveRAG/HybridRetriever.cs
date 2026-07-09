using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience.Replica;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Hybrid retrieval: a semantic leg (brute-force cosine over MiniLM embeddings) and a lexical
    /// leg (SQLite FTS5 bm25, or an in-memory term-overlap fallback when FTS is unavailable), fused
    /// by Reciprocal Rank Fusion. A mild recency boost lifts fresh operational sources; a per-document
    /// diversity cap stops one long document from monopolising the results. Everything is bounded and
    /// fails soft — a query that can't run returns nothing rather than throwing into a prompt build.
    /// </summary>
    public sealed class HybridRetriever
    {
        private const int LegLimit = 50;      // candidates kept per leg
        private const int RrfK = 60;          // RRF damping
        private const int MaxPerDoc = 2;      // diversity cap

        private readonly KliveRAGDb db;
        private readonly RagEmbedQueue embed;

        public HybridRetriever(KliveRAGDb db, RagEmbedQueue embed)
        {
            this.db = db;
            this.embed = embed;
        }

        public async Task<List<RagHit>> SearchAsync(string query, RagSearchOptions opts, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<RagHit>();
            var sourceFilter = opts.Sources != null && opts.Sources.Count > 0
                ? new HashSet<string>(opts.Sources, StringComparer.Ordinal)
                : null;
            var excludeSources = opts.ExcludeSources != null && opts.ExcludeSources.Count > 0
                ? new HashSet<string>(opts.ExcludeSources, StringComparer.Ordinal)
                : null;

            var vectorTask = VectorLegAsync(query, sourceFilter, excludeSources, ct);
            var lexical = LexicalLeg(query, sourceFilter, excludeSources);
            var vector = await vectorTask;

            // RRF fuse: each leg contributes 1/(k + rank) for chunks it ranked.
            var fused = new Dictionary<string, RagHit>(StringComparer.Ordinal);
            void Fuse(List<Candidate> leg, bool isVector)
            {
                for (int i = 0; i < leg.Count; i++)
                {
                    var c = leg[i];
                    if (!fused.TryGetValue(c.ChunkId, out var hit))
                    {
                        hit = new RagHit
                        {
                            ChunkId = c.ChunkId,
                            DocId = c.DocId,
                            Source = c.Source,
                            CreatedAtUnixMs = c.CreatedAt,
                        };
                        fused[c.ChunkId] = hit;
                    }
                    hit.Score += 1.0 / (RrfK + i + 1);
                    if (isVector) hit.VectorRank = i; else hit.LexicalRank = i;
                }
            }
            Fuse(vector, true);
            Fuse(lexical, false);

            if (fused.Count == 0) return new List<RagHit>();

            // Exclude a project's own events/digests (the caller already has that log leg).
            if (!string.IsNullOrEmpty(opts.ExcludeProjectId))
            {
                string p1 = $"projevt:{opts.ExcludeProjectId}:";
                string p2 = $"projdigest:{opts.ExcludeProjectId}";
                foreach (var key in fused.Keys.ToList())
                    if (fused[key].DocId.StartsWith(p1, StringComparison.Ordinal) ||
                        fused[key].DocId.StartsWith(p2, StringComparison.Ordinal))
                        fused.Remove(key);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var hit in fused.Values)
            {
                if (hit.Source == RagSource.ProjectsEvents || hit.Source == RagSource.AgentConversations)
                {
                    double ageDays = Math.Max(0, (now - hit.CreatedAtUnixMs) / 86_400_000.0);
                    hit.Score += 0.08 * Math.Exp(-ageDays / 21.0);
                }
            }

            // Diversity cap, then take the caller's requested number of hits.
            var ranked = fused.Values.OrderByDescending(h => h.Score).ToList();
            var perDoc = new Dictionary<string, int>(StringComparer.Ordinal);
            var kept = new List<RagHit>();
            foreach (var h in ranked)
            {
                perDoc.TryGetValue(h.DocId, out int n);
                if (n >= MaxPerDoc) continue;
                perDoc[h.DocId] = n + 1;
                kept.Add(h);
                if (kept.Count >= Math.Max(1, opts.MaxResults)) break;
            }

            Hydrate(kept);
            return kept;
        }

        private sealed record Candidate(string ChunkId, string DocId, string Source, long CreatedAt);

        private static bool Allowed(string source, HashSet<string>? include, HashSet<string>? exclude)
            => (include == null || include.Contains(source)) && (exclude == null || !exclude.Contains(source));

        // Brute-force cosine over all embeddings, streamed, top-K kept. Source filter prunes in SQL.
        private async Task<List<Candidate>> VectorLegAsync(string query, HashSet<string>? sourceFilter, HashSet<string>? excludeSources, CancellationToken ct)
        {
            float[] qv;
            try { qv = await embed.EmbedQueryAsync(query, ct); }
            catch { return new List<Candidate>(); }

            var top = new List<(Candidate Cand, float Score)>();
            float worst = float.MinValue;

            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT c.chunk_id, c.doc_id, c.source, c.created_at, e.embedding
FROM rag_chunk_embeddings e JOIN rag_chunks c ON c.chunk_id = e.chunk_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                string source = r.GetString(2);
                if (!Allowed(source, sourceFilter, excludeSources)) continue;
                var v = ReplicaEmbedder.UnpackEmbedding((byte[])r.GetValue(4));
                if (v.Length != qv.Length) continue;
                float score = ReplicaEmbedder.CosineSimilarity(qv, v);
                var cand = new Candidate(r.GetString(0), r.GetString(1), source, r.GetInt64(3));
                if (top.Count < LegLimit)
                {
                    top.Add((cand, score));
                    if (top.Count == LegLimit) worst = top.Min(t => t.Score);
                }
                else if (score > worst)
                {
                    int wi = 0; float w = float.MaxValue;
                    for (int i = 0; i < top.Count; i++) if (top[i].Score < w) { w = top[i].Score; wi = i; }
                    top[wi] = (cand, score);
                    worst = top.Min(t => t.Score);
                }
            }
            return top.OrderByDescending(t => t.Score).Select(t => t.Cand).ToList();
        }

        private List<Candidate> LexicalLeg(string query, HashSet<string>? sourceFilter, HashSet<string>? excludeSources)
        {
            return db.FtsAvailable ? FtsLeg(query, sourceFilter, excludeSources) : FallbackLexicalLeg(query, sourceFilter, excludeSources);
        }

        // FTS5 bm25() — lower is better, so results come back best-first already.
        private List<Candidate> FtsLeg(string query, HashSet<string>? sourceFilter, HashSet<string>? excludeSources)
        {
            string match = BuildFtsMatch(query);
            if (match.Length == 0) return new List<Candidate>();
            var result = new List<Candidate>();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT c.chunk_id, c.doc_id, c.source, c.created_at
FROM rag_chunks_fts f JOIN rag_chunks c ON c.rowid = f.rowid
WHERE rag_chunks_fts MATCH $q
ORDER BY bm25(rag_chunks_fts) LIMIT $n";
                cmd.Parameters.AddWithValue("$q", match);
                cmd.Parameters.AddWithValue("$n", LegLimit * 2); // over-fetch, source-filter in memory
                using var r = cmd.ExecuteReader();
                while (r.Read() && result.Count < LegLimit)
                {
                    string source = r.GetString(2);
                    if (!Allowed(source, sourceFilter, excludeSources)) continue;
                    result.Add(new Candidate(r.GetString(0), r.GetString(1), source, r.GetInt64(3)));
                }
            }
            catch { /* malformed MATCH etc. — lexical leg is best-effort */ }
            return result;
        }

        // Fallback when FTS5 is absent: term-overlap scan bounded by a candidate cap.
        private List<Candidate> FallbackLexicalLeg(string query, HashSet<string>? sourceFilter, HashSet<string>? excludeSources)
        {
            var terms = Tokenize(query).Distinct().ToList();
            if (terms.Count == 0) return new List<Candidate>();
            var scored = new List<(Candidate Cand, int Overlap)>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT chunk_id, doc_id, source, created_at, text FROM rag_chunks LIMIT 20000";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string source = r.GetString(2);
                if (!Allowed(source, sourceFilter, excludeSources)) continue;
                string text = r.GetString(4).ToLowerInvariant();
                int overlap = terms.Count(t => text.Contains(t));
                if (overlap > 0)
                    scored.Add((new Candidate(r.GetString(0), r.GetString(1), source, r.GetInt64(3)), overlap));
            }
            return scored.OrderByDescending(s => s.Overlap).Take(LegLimit).Select(s => s.Cand).ToList();
        }

        private void Hydrate(List<RagHit> hits)
        {
            if (hits.Count == 0) return;
            using var conn = db.Open();
            foreach (var h in hits)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT c.text, d.title, d.uri FROM rag_chunks c
JOIN rag_documents d ON d.doc_id = c.doc_id WHERE c.chunk_id=$c";
                cmd.Parameters.AddWithValue("$c", h.ChunkId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    h.Text = r.GetString(0);
                    h.Title = r.IsDBNull(1) ? null : r.GetString(1);
                    h.Uri = r.IsDBNull(2) ? null : r.GetString(2);
                }
            }
        }

        // ── formatting ──

        /// <summary>Budget-fitted, citation-tagged block for automatic prompt injection.</summary>
        public static string FormatForPrompt(List<RagHit> hits, int maxTokens, string header)
        {
            if (hits.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine(header);
            int used = RagChunker.EstimateTokens(header);
            foreach (var h in hits)
            {
                string line = $"- [{h.Source}{(string.IsNullOrEmpty(h.Title) ? "" : " · " + h.Title)} · {Stamp(h.CreatedAtUnixMs)}] {Clip(h.Text, 320)} (doc:{h.DocId})";
                int cost = RagChunker.EstimateTokens(line);
                if (used + cost > maxTokens) break;
                sb.AppendLine(line);
                used += cost;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Numbered result list for the search_knowledge tool, fitted to a token cap.</summary>
        public static string FormatForTool(List<RagHit> hits, int maxTokens)
        {
            if (hits.Count == 0) return "No matching knowledge found.";
            var sb = new StringBuilder();
            int used = 0, n = 1;
            foreach (var h in hits)
            {
                string block = $"{n}. [{h.Source}] {(string.IsNullOrEmpty(h.Title) ? "" : h.Title + " — ")}{Clip(h.Text, 600)}\n   doc:{h.DocId}";
                int cost = RagChunker.EstimateTokens(block);
                if (used + cost > maxTokens && n > 1) break;
                sb.AppendLine(block);
                used += cost;
                n++;
            }
            return sb.ToString().TrimEnd();
        }

        public static List<KnowledgeHit> ToKnowledgeHits(List<RagHit> hits) =>
            hits.Select(h => new KnowledgeHit(h.Source, h.Title ?? "", h.Text, h.DocId, h.CreatedAtUnixMs, h.Score)).ToList();

        // ── helpers ──

        private static string BuildFtsMatch(string query)
        {
            var terms = Tokenize(query).Distinct().Take(24).ToList();
            if (terms.Count == 0) return "";
            // Quote each term (escaping embedded quotes) and OR them — tolerant recall, ranked by bm25.
            return string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            var cur = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) cur.Append(char.ToLowerInvariant(c));
                else if (cur.Length > 0) { if (cur.Length > 2) tokens.Add(cur.ToString()); cur.Clear(); }
            }
            if (cur.Length > 2) tokens.Add(cur.ToString());
            return tokens;
        }

        private static string Stamp(long unixMs) =>
            DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("MM-dd");

        private static string Clip(string s, int chars)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= chars ? s : s.Substring(0, chars) + "…";
        }
    }
}
