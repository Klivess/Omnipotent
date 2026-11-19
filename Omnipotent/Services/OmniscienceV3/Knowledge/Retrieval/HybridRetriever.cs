using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Retrieval
{
    /// <summary>Where to search. Every field is optional; together they AND.</summary>
    public sealed record RetrievalScope
    {
        /// <summary>Only messages authored by these identities.</summary>
        public IReadOnlyCollection<string>? AuthorIdentityIds { get; init; }
        /// <summary>Only messages in these conversations.</summary>
        public IReadOnlyCollection<string>? ConversationIds { get; init; }
        /// <summary>Only these messages (e.g. everything the subject reacted to). At most 800 are honoured per search.</summary>
        public IReadOnlyCollection<string>? MessageIds { get; init; }
        public long? FromMs { get; init; }
        /// <summary>Upper bound on sent time (an inquiry's as-of: nothing after it is evidence).</summary>
        public long? ToMs { get; init; }
        public int Limit { get; init; } = 40;
        /// <summary>How many of the scope's most recent messages the vector pass compares against (bounded work per query).</summary>
        public int VectorCandidates { get; init; } = 40_000;
    }

    public sealed record RetrievedMessage(string MessageId, double Score, int? FtsRank, int? VectorRank, double? Similarity);

    /// <summary>What a search covered, reported with every result (plan "coverage").</summary>
    public sealed record RetrievalCoverage(bool FullText, bool Vectors, string? VectorModel, int Queries, int Candidates, string? Note);

    /// <summary>
    /// Hybrid retrieval over the corpus (the KliveRAG pattern): FTS5 (<c>unicode61 remove_diacritics 2</c>) plus the shared
    /// encoder's message vectors (vectors.db, int8 or float), fused by reciprocal-rank fusion. Several queries (multilingual
    /// paraphrases from the planner) fuse into one ranking. Without an encoder it runs on full text alone and says so.
    /// </summary>
    public sealed class HybridRetriever
    {
        public const string MessageSpace = "message";
        private const int RrfK = 60;
        /// <summary>A word found in more than this share of all messages is left out of full-text queries (with at least
        /// <see cref="CommonTermFloor"/> messages): it tells nothing apart, and an OR query costs as much as its commonest term.</summary>
        internal const double CommonShare = 0.01;
        private const long CommonTermTtlMs = 10 * 60_000;
        private const int CommonTermCacheLimit = 8_192;

        private readonly OmniscienceStores stores;
        private readonly CorpusReader corpus;
        private readonly Func<IEmbeddingEncoder?> encoder;
        private readonly ConcurrentDictionary<string, (bool Common, long AtTick)> commonTerms = new(StringComparer.OrdinalIgnoreCase);
        private CommonCapReading? commonCap;

        private sealed record CommonCapReading(long Cap, long AtTick);

        public HybridRetriever(OmniscienceStores stores, CorpusReader corpus, Func<IEmbeddingEncoder?> encoder)
        {
            this.stores = stores;
            this.corpus = corpus;
            this.encoder = encoder;
        }

        /// <summary>The fewest messages a word must appear in before it can count as too common (a small corpus prunes nothing).</summary>
        internal long CommonTermFloor { get; init; } = 2_000;

        public async Task<(IReadOnlyList<RetrievedMessage> Results, RetrievalCoverage Coverage)> SearchAsync(IReadOnlyList<string> queries, RetrievalScope scope, CancellationToken ct)
        {
            var clean = queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            var fused = new Dictionary<string, (double Score, int? Fts, int? Vec, double? Sim)>(StringComparer.Ordinal);
            int perQuery = Math.Clamp(scope.Limit * 3, 20, 300);
            foreach (var q in clean)
            {
                ct.ThrowIfCancellationRequested();
                var hits = FullText(q, scope, perQuery);
                for (int i = 0; i < hits.Count; i++)
                {
                    var cur = fused.GetValueOrDefault(hits[i]);
                    fused[hits[i]] = (cur.Score + 1.0 / (RrfK + i + 1), cur.Fts == null ? i + 1 : Math.Min(cur.Fts.Value, i + 1), cur.Vec, cur.Sim);
                }
            }
            var enc = encoder();
            int candidates = 0;
            string? note = null;
            if (enc != null && clean.Count > 0)
            {
                var (ids, vectors) = LoadCandidateVectors(enc, scope);
                candidates = ids.Count;
                if (ids.Count == 0) note = "No message vectors exist for this scope yet; full-text search only.";
                else
                {
                    var queryVectors = await enc.EmbedAsync(clean, ct).ConfigureAwait(false);
                    foreach (var qv in queryVectors)
                    {
                        var scored = new List<(int Index, float Sim)>(ids.Count);
                        for (int i = 0; i < ids.Count; i++)
                            scored.Add((i, TensorPrimitives.Dot(qv.AsSpan(), vectors[i].AsSpan())));
                        var top = scored.OrderByDescending(s => s.Sim).Take(perQuery).ToList();
                        for (int r = 0; r < top.Count; r++)
                        {
                            string id = ids[top[r].Index];
                            var cur = fused.GetValueOrDefault(id);
                            fused[id] = (cur.Score + 1.0 / (RrfK + r + 1), cur.Fts, cur.Vec == null ? r + 1 : Math.Min(cur.Vec.Value, r + 1),
                                Math.Max(cur.Sim ?? double.MinValue, top[r].Sim));
                        }
                    }
                }
            }
            else if (enc == null) note = "The shared encoder is not installed; full-text search only.";
            var results = fused.OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(scope.Limit)
                .Select(kv => new RetrievedMessage(kv.Key, kv.Value.Score, kv.Value.Fts, kv.Value.Vec, kv.Value.Sim)).ToList();
            return (results, new RetrievalCoverage(true, enc != null && candidates > 0, enc?.ModelId, clean.Count, candidates, note));
        }

        /// <summary>
        /// An FTS5 query from free text: quoted phrases stay phrases; every other word becomes a term, OR-joined. Words are split
        /// where the index's tokenizer splits them, apostrophes and hyphens included, so "don't" is the word "don" and never a
        /// phrase ending in a one-letter prefix (t*, every word starting with t). A word of five letters or more is a prefix
        /// term, so its inflections are found ("football" finds "footballer"). A shorter word is matched whole, with its plain
        /// plural: as a prefix it would find unrelated words ("cat" would find "Catholic").
        /// </summary>
        public static string? FtsQuery(string text) => FtsQuery(text, null);

        /// <summary>As <see cref="FtsQuery(string)"/>, leaving out every word (and its plural) that <paramref name="tooCommon"/>
        /// says is in too many messages: "is", "my", "lol" match a large share of a chat corpus.</summary>
        internal static string? FtsQuery(string text, Func<string, bool>? tooCommon)
        {
            var terms = new List<string>();
            var phrases = System.Text.RegularExpressions.Regex.Matches(text, "\"([^\"]{2,80})\"");
            foreach (System.Text.RegularExpressions.Match p in phrases)
            {
                // Word by word, spaces kept: cleaned whole, "the end" became the one word "theend" and no phrase ever matched.
                string phrase = string.Join(" ", p.Groups[1].Value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(w => w.Length > 0));
                if (phrase.Length > 0) terms.Add("\"" + phrase + "\"");
            }
            string rest = System.Text.RegularExpressions.Regex.Replace(text, "\"[^\"]*\"", " ");
            foreach (var word in rest.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = Clean(word);
                if (w.Length < 2) continue;
                string term = w.Length >= 5 ? "\"" + w + "\"*" : "\"" + w + "\"";
                if (terms.Contains(term, StringComparer.OrdinalIgnoreCase) || (tooCommon != null && tooCommon(term))) continue;
                terms.Add(term);
                if (w.Length is >= 3 and < 5 && !w.EndsWith('s')) terms.Add("\"" + w + "s\"");
            }
            terms = terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            return terms.Count == 0 ? null : string.Join(" OR ", terms);
        }

        /// <summary>Where words end: whitespace and punctuation, and the apostrophes and hyphens unicode61 also splits on.</summary>
        private static readonly char[] WordSeparators = { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '(', ')', '[', ']', '/', '\\', '\'', '-', '\u2019' };

        private static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '\'' || c == '-' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) sb.Append(c);
            return sb.ToString().Replace("\"", "");
        }

        private List<string> FullText(string query, RetrievalScope scope, int limit)
        {
            using var c = stores.Corpus.OpenRead();
            long cap = CommonCap(c);
            string? fts = FtsQuery(query, term => TooCommon(c, term, cap));
            if (fts == null) return new List<string>();
            var (sql, args) = FullTextStatement(fts, scope, limit);
            try
            {
                return c.Query(null, sql, r => r.GetString(0), args);
            }
            catch (SqliteException ex) when (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            {
                // A query the FTS grammar rejects (odd symbols) finds nothing by full text; vectors may still find it.
                return new List<string>();
            }
        }

        /// <summary>
        /// The statement <see cref="FullText"/> runs. The full-text index drives it (CROSS JOIN fixes the join order): left to
        /// choose, the planner, which cannot know that one person or conversation holds hundreds of thousands of messages, walked
        /// every message in scope and ran the match again for each one. For Klives's own messages that was about three minutes
        /// per query on two million messages, and an inquiry runs dozens (an x01 run failed after 23 minutes on 18 Nov 2026).
        /// </summary>
        internal static (string Sql, (string Name, object? Value)[] Args) FullTextStatement(string fts, RetrievalScope scope, int limit)
        {
            var where = new List<string> { "messages_fts MATCH $q" };
            var args = new List<(string, object?)> { ("$q", fts), ("$l", limit) };
            if (scope.FromMs is long f) { where.Add("m.sent_at_ms >= $f"); args.Add(("$f", f)); }
            if (scope.ToMs is long t) { where.Add("m.sent_at_ms <= $t"); args.Add(("$t", t)); }
            AddIn(where, args, "m.author_identity_id", "$a", scope.AuthorIdentityIds);
            AddIn(where, args, "m.conversation_id", "$c", scope.ConversationIds);
            AddIn(where, args, "m.message_id", "$mi", scope.MessageIds);
            string sql = $"SELECT m.message_id FROM messages_fts CROSS JOIN messages m ON m.rowid = messages_fts.rowid WHERE {string.Join(" AND ", where)} ORDER BY bm25(messages_fts) LIMIT $l";
            return (sql, args.ToArray());
        }

        /// <summary>How many messages a word may appear in before it is too common to search for: <see cref="CommonShare"/> of
        /// the corpus, at least <see cref="CommonTermFloor"/>. Read again every ten minutes.</summary>
        private long CommonCap(SqliteConnection c)
        {
            long now = Environment.TickCount64;
            var known = Volatile.Read(ref commonCap);
            if (known != null && now - known.AtTick < CommonTermTtlMs) return known.Cap;
            // MAX(rowid) reads one page; it counts every message ever stored, which is near enough for a share.
            long messages = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            long cap = Math.Max(CommonTermFloor, (long)(messages * CommonShare));
            Volatile.Write(ref commonCap, new CommonCapReading(cap, now));
            return cap;
        }

        /// <summary>Whether more than <paramref name="cap"/> messages contain <paramref name="term"/>. The count stops at cap + 1,
        /// so it reads at most that many index entries; answers are kept for ten minutes.</summary>
        private bool TooCommon(SqliteConnection c, string term, long cap)
        {
            long now = Environment.TickCount64;
            if (commonTerms.TryGetValue(term, out var known) && now - known.AtTick < CommonTermTtlMs) return known.Common;
            bool common;
            try
            {
                common = c.Scalar<long>(null, "SELECT COUNT(*) FROM (SELECT 1 FROM messages_fts WHERE messages_fts MATCH $t LIMIT $l)", ("$t", term), ("$l", cap + 1)) > cap;
            }
            catch (SqliteException)
            {
                return false; // a term the FTS grammar rejects: the whole query then finds nothing by full text, as before
            }
            if (commonTerms.Count >= CommonTermCacheLimit) commonTerms.Clear();
            commonTerms[term] = (common, now);
            return common;
        }

        private static void AddIn(List<string> where, List<(string, object?)> args, string column, string prefix, IReadOnlyCollection<string>? values)
        {
            if (values == null) return;
            if (values.Count == 0) { where.Add("0"); return; }
            var names = values.Take(prefix == "$mi" ? 800 : 200).Select((v, i) => (Name: prefix + i, Value: v)).ToList();
            where.Add($"{column} IN ({string.Join(",", names.Select(n => n.Name))})");
            foreach (var (name, value) in names) args.Add((name, value));
        }

        private (List<string> Ids, List<float[]> Vectors) LoadCandidateVectors(IEmbeddingEncoder enc, RetrievalScope scope)
        {
            var ids = new List<string>();
            using (var c = stores.Corpus.OpenRead())
            {
                var where = new List<string> { "1=1" };
                var args = new List<(string, object?)> { ("$l", scope.VectorCandidates) };
                if (scope.FromMs is long f) { where.Add("m.sent_at_ms >= $f"); args.Add(("$f", f)); }
                if (scope.ToMs is long t) { where.Add("m.sent_at_ms <= $t"); args.Add(("$t", t)); }
                AddIn(where, args, "m.author_identity_id", "$a", scope.AuthorIdentityIds);
                AddIn(where, args, "m.conversation_id", "$c", scope.ConversationIds);
                AddIn(where, args, "m.message_id", "$mi", scope.MessageIds);
                if (scope.AuthorIdentityIds == null && scope.ConversationIds == null && scope.MessageIds == null) return (ids, new List<float[]>()); // never scan the whole corpus per query
                ids = c.Query(null, $"SELECT m.message_id FROM messages m WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT $l", r => r.GetString(0), args.ToArray());
            }
            var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            using (var v = stores.Vectors.OpenRead())
            {
                // Message vectors are filed under the encoder's model VERSION (the classifier writes them so, and a new build of a
                // model never mixes with the old one's vectors), not its model id. Both parameters are bound on every chunk: the
                // model was once left unbound, and every inquiry with the encoder installed failed here (18 Nov 2026).
                foreach (var row in v.QueryIn(null, "SELECT item_id, dims, quant, scale, vec FROM vectors WHERE space=$space AND model=$model AND item_id IN ({keys})",
                             ids, r => (Id: r.GetString(0), Dims: r.GetInt32(1), Quant: r.GetString(2), Scale: r.GetDouble(3), Blob: (byte[])r.GetValue(4)), 400,
                             ("$space", MessageSpace), ("$model", enc.ModelVersion)))
                {
                    var decoded = Decode(row.Dims, row.Quant, row.Scale, row.Blob);
                    if (decoded != null && decoded.Length == enc.Dimensions) vectors[row.Id] = decoded;
                }
            }
            var keptIds = ids.Where(vectors.ContainsKey).ToList();
            return (keptIds, keptIds.Select(id => vectors[id]).ToList());
        }

        /// <summary>Decodes a stored vector (contract: vectors.db quant "i8" = int8 × scale, "f32" = little-endian floats).</summary>
        public static float[]? Decode(int dims, string quant, double scale, byte[] blob)
        {
            if (quant == "i8")
            {
                if (blob.Length != dims) return null;
                var v = new float[dims];
                for (int i = 0; i < dims; i++) v[i] = (sbyte)blob[i] * (float)scale;
                return Normalize(v);
            }
            if (quant == "f32")
            {
                if (blob.Length != dims * 4) return null;
                var v = new float[dims];
                Buffer.BlockCopy(blob, 0, v, 0, blob.Length);
                return Normalize(v);
            }
            return null;
        }

        private static float[] Normalize(float[] v)
        {
            float norm = TensorPrimitives.Norm(v);
            if (norm > 0) TensorPrimitives.Divide(v, norm, v);
            return v;
        }

        /// <summary>Cosine similarity of two texts with the shared encoder (null when no encoder is installed).</summary>
        public async Task<double?> SimilarityAsync(string a, string b, CancellationToken ct)
        {
            var enc = encoder();
            if (enc == null) return null;
            var vs = await enc.EmbedAsync(new[] { a, b }, ct).ConfigureAwait(false);
            return TensorPrimitives.CosineSimilarity(vs[0].AsSpan(), vs[1].AsSpan());
        }
    }
}
