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
    /// diversity cap stops one long document from monopolising the results.
    ///
    /// Both legs run on the thread pool and every SQLite statement is bound to the search deadline
    /// (<see cref="RagSearchOptions.Deadline"/>), because the work underneath is synchronous: an FTS
    /// bm25 sort over every chunk containing a common word, or a cosine scan of every embedding. With
    /// the Projects event log in the index that is millions of rows, and an unbounded lexical leg on
    /// the caller's thread is what held KliveAgent at "preparing context" for 5-12 minutes.
    /// </summary>
    public sealed class HybridRetriever
    {
        private const int LegLimit = 50;      // candidates kept per leg
        private const int RrfK = 60;          // RRF damping
        private const int MaxPerDoc = 2;      // diversity cap
        /// <summary>Vector candidates kept before source filters run, so excluded sources can drop out
        /// without starving the leg.</summary>
        private const int VectorPoolLimit = LegLimit * 4;
        /// <summary>Distinct terms one lexical query may carry; the longest are kept.</summary>
        internal const int MaxQueryTerms = 8;
        /// <summary>How long the search waits past its deadline for a leg to hand over what it has.</summary>
        private static readonly TimeSpan LegGrace = TimeSpan.FromMilliseconds(250);

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

            TimeSpan? deadline = opts.Deadline is { } d && d > TimeSpan.Zero ? d : null;
            int window = Math.Max(0, opts.RecentWindow);
            using var legs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (deadline.HasValue) legs.CancelAfter(deadline.Value);
            var legToken = legs.Token;

            var vectorTask = Task.Run(() => VectorLegAsync(query, window, sourceFilter, excludeSources, legToken));
            var lexicalTask = Task.Run(() => LexicalLeg(query, window, sourceFilter, excludeSources, legToken));
            var both = Task.WhenAll(vectorTask, lexicalTask);
            _ = both.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            // Bounded by the deadline even if a leg is stuck somewhere SQLite's handler cannot reach
            // (opening the file, the embedder): a leg that misses it simply contributes nothing.
            try { await both.WaitAsync(deadline.HasValue ? deadline.Value + LegGrace : Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            var vector = vectorTask.IsCompletedSuccessfully ? vectorTask.Result : new List<Candidate>();
            var lexical = lexicalTask.IsCompletedSuccessfully ? lexicalTask.Result : new List<Candidate>();

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

        /// <summary>
        /// Brute-force cosine, newest embeddings first, so a deadline or <paramref name="window"/> cuts off
        /// the oldest material and a scan that is stopped early still returns the best of what it read.
        /// Without an include filter only the embeddings table is walked (one sequential pass); chunk
        /// metadata is then read for the survivors alone.
        /// </summary>
        private async Task<List<Candidate>> VectorLegAsync(string query, int window, HashSet<string>? sourceFilter, HashSet<string>? excludeSources, CancellationToken ct)
        {
            float[] qv;
            try { qv = await embed.EmbedQueryAsync(query, ct).ConfigureAwait(false); }
            catch { return new List<Candidate>(); }

            int poolLimit = sourceFilter == null ? VectorPoolLimit : LegLimit;
            var pool = new List<(string ChunkId, float Score)>();
            float worst = float.MinValue;
            try
            {
                using var conn = db.Open();
                using var deadline = KliveRAGDb.BindDeadline(conn, ct);
                using var cmd = conn.CreateCommand();
                // The include filter needs each row's source; a correlated primary-key lookup keeps the
                // embeddings table as the only scanned table, so rows still stream newest-first with no sort.
                cmd.CommandText = sourceFilter == null
                    ? "SELECT chunk_id, embedding FROM rag_chunk_embeddings ORDER BY rowid DESC"
                    : "SELECT e.chunk_id, e.embedding, (SELECT c.source FROM rag_chunks c WHERE c.chunk_id = e.chunk_id) "
                      + "FROM rag_chunk_embeddings e ORDER BY e.rowid DESC";
                using var r = cmd.ExecuteReader();
                int scanned = 0;
                while (!ct.IsCancellationRequested && r.Read())
                {
                    if (window > 0 && ++scanned > window) break;
                    if (sourceFilter != null && (r.IsDBNull(2) || !Allowed(r.GetString(2), sourceFilter, excludeSources))) continue;
                    var v = ReplicaEmbedder.UnpackEmbedding((byte[])r.GetValue(1));
                    if (v.Length != qv.Length) continue;
                    float score = ReplicaEmbedder.CosineSimilarity(qv, v);
                    if (pool.Count < poolLimit)
                    {
                        pool.Add((r.GetString(0), score));
                        if (pool.Count == poolLimit) worst = pool.Min(t => t.Score);
                    }
                    else if (score > worst)
                    {
                        int wi = 0; float w = float.MaxValue;
                        for (int i = 0; i < pool.Count; i++) if (pool[i].Score < w) { w = pool[i].Score; wi = i; }
                        pool[wi] = (r.GetString(0), score);
                        worst = pool.Min(t => t.Score);
                    }
                }
            }
            catch { /* interrupted at the deadline, or a read failed: keep what was scored */ }
            if (pool.Count == 0) return new List<Candidate>();

            try { return ResolveCandidates(pool, sourceFilter, excludeSources); }
            catch { return new List<Candidate>(); }
        }

        /// <summary>Chunk metadata for the scored survivors, best first, with source filters applied.</summary>
        private List<Candidate> ResolveCandidates(List<(string ChunkId, float Score)> pool, HashSet<string>? sourceFilter, HashSet<string>? excludeSources)
        {
            var meta = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            using (var conn = db.Open())
            using (var cmd = conn.CreateCommand())
            {
                var names = new List<string>(pool.Count);
                for (int i = 0; i < pool.Count; i++)
                {
                    names.Add("$c" + i);
                    cmd.Parameters.AddWithValue("$c" + i, pool[i].ChunkId);
                }
                cmd.CommandText = $"SELECT chunk_id, doc_id, source, created_at FROM rag_chunks WHERE chunk_id IN ({string.Join(",", names)})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    meta[r.GetString(0)] = new Candidate(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3));
            }

            var result = new List<Candidate>();
            foreach (var (chunkId, _) in pool.OrderByDescending(p => p.Score))
            {
                if (!meta.TryGetValue(chunkId, out var cand) || !Allowed(cand.Source, sourceFilter, excludeSources)) continue;
                result.Add(cand);
                if (result.Count >= LegLimit) break;
            }
            return result;
        }

        private List<Candidate> LexicalLeg(string query, int window, HashSet<string>? sourceFilter, HashSet<string>? excludeSources, CancellationToken ct)
        {
            try
            {
                return db.FtsAvailable
                    ? FtsLeg(query, window, sourceFilter, excludeSources, ct)
                    : FallbackLexicalLeg(query, window, sourceFilter, excludeSources, ct);
            }
            catch { return new List<Candidate>(); }
        }

        // FTS5 bm25() — lower is better, so results come back best-first already. The sort has to
        // score every matching row before it yields one, so its cost is the match count: stopwords are
        // dropped, the term count capped, and the window (when set) limits it to the newest rows.
        private List<Candidate> FtsLeg(string query, int window, HashSet<string>? sourceFilter, HashSet<string>? excludeSources, CancellationToken ct)
        {
            string match = BuildFtsMatch(query);
            if (match.Length == 0) return new List<Candidate>();
            var result = new List<Candidate>();
            try
            {
                using var conn = db.Open();
                using var deadline = KliveRAGDb.BindDeadline(conn, ct);
                long floor = window > 0 ? RowidFloor(conn, window) : 0;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT c.chunk_id, c.doc_id, c.source, c.created_at
FROM rag_chunks_fts f JOIN rag_chunks c ON c.rowid = f.rowid
WHERE rag_chunks_fts MATCH $q" + (floor > 0 ? " AND f.rowid > $floor" : "") + @"
ORDER BY bm25(rag_chunks_fts) LIMIT $n";
                cmd.Parameters.AddWithValue("$q", match);
                if (floor > 0) cmd.Parameters.AddWithValue("$floor", floor);
                cmd.Parameters.AddWithValue("$n", LegLimit * 2); // over-fetch, source-filter in memory
                using var r = cmd.ExecuteReader();
                while (r.Read() && result.Count < LegLimit)
                {
                    string source = r.GetString(2);
                    if (!Allowed(source, sourceFilter, excludeSources)) continue;
                    result.Add(new Candidate(r.GetString(0), r.GetString(1), source, r.GetInt64(3)));
                }
            }
            catch { /* malformed MATCH, or interrupted at the deadline — the lexical leg is best-effort */ }
            return result;
        }

        /// <summary>The lowest rowid inside the newest <paramref name="window"/> chunks (0 = no floor).</summary>
        private static long RowidFloor(SqliteConnection conn, int window)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT max(rowid) FROM rag_chunks";
            return cmd.ExecuteScalar() is long max ? Math.Max(0, max - window) : 0;
        }

        // Fallback when FTS5 is absent: term-overlap scan over the newest rows, bounded by a candidate cap.
        private List<Candidate> FallbackLexicalLeg(string query, int window, HashSet<string>? sourceFilter, HashSet<string>? excludeSources, CancellationToken ct)
        {
            var terms = QueryTerms(query);
            if (terms.Count == 0) return new List<Candidate>();
            var scored = new List<(Candidate Cand, int Overlap)>();
            try
            {
                using var conn = db.Open();
                using var deadline = KliveRAGDb.BindDeadline(conn, ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT chunk_id, doc_id, source, created_at, text FROM rag_chunks ORDER BY rowid DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", window > 0 ? Math.Min(window, 20000) : 20000);
                using var r = cmd.ExecuteReader();
                while (!ct.IsCancellationRequested && r.Read())
                {
                    string source = r.GetString(2);
                    if (!Allowed(source, sourceFilter, excludeSources)) continue;
                    string text = r.GetString(4).ToLowerInvariant();
                    int overlap = terms.Count(t => text.Contains(t));
                    if (overlap > 0)
                        scored.Add((new Candidate(r.GetString(0), r.GetString(1), source, r.GetInt64(3)), overlap));
                }
            }
            catch { /* interrupted at the deadline: rank what was read */ }
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

        internal static string BuildFtsMatch(string query)
        {
            var terms = QueryTerms(query);
            if (terms.Count == 0) return "";
            // Quote each term (escaping embedded quotes) and OR them — tolerant recall, ranked by bm25.
            return string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
        }

        /// <summary>
        /// The words of a query worth matching on. Function words ("the", "that", "your", "then") occur
        /// in nearly every chunk, so OR-ing them in made bm25 score the whole corpus for no ranking gain.
        /// The longest remaining words are kept: in an English request they are usually the rare ones.
        /// </summary>
        internal static List<string> QueryTerms(string query) =>
            Tokenize(query).Where(t => !StopWords.Contains(t)).Distinct(StringComparer.Ordinal)
                .OrderByDescending(t => t.Length).Take(MaxQueryTerms).ToList();

        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "about", "above", "after", "again", "against", "all", "also", "and", "any", "are", "because", "been",
            "before", "being", "below", "between", "both", "but", "can", "cannot", "could", "did", "does", "doing",
            "done", "down", "during", "each", "even", "every", "few", "for", "from", "further", "get", "gets", "getting",
            "got", "had", "has", "have", "having", "her", "here", "hers", "him", "his", "how", "into", "its", "itself",
            "just", "let", "lets", "like", "made", "make", "makes", "many", "may", "might", "more", "most", "much",
            "must", "need", "needs", "nor", "not", "now", "off", "once", "one", "only", "other", "our", "ours", "out",
            "over", "own", "please", "same", "she", "should", "some", "still", "such", "than", "that", "thats", "the",
            "their", "theirs", "them", "then", "there", "these", "they", "this", "those", "through", "too", "under",
            "until", "upon", "use", "used", "uses", "using", "very", "via", "want", "wants", "was", "way", "were",
            "what", "whats", "when", "where", "which", "while", "who", "whom", "why", "will", "with", "would", "yes",
            "yet", "you", "your", "yours", "yourself",
        };

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
