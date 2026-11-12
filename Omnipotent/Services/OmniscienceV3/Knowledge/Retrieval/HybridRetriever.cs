using System.Numerics.Tensors;
using System.Text;
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

        private readonly OmniscienceStores stores;
        private readonly CorpusReader corpus;
        private readonly Func<IEmbeddingEncoder?> encoder;

        public HybridRetriever(OmniscienceStores stores, CorpusReader corpus, Func<IEmbeddingEncoder?> encoder)
        {
            this.stores = stores;
            this.corpus = corpus;
            this.encoder = encoder;
        }

        public async Task<(IReadOnlyList<RetrievedMessage> Results, RetrievalCoverage Coverage)> SearchAsync(IReadOnlyList<string> queries, RetrievalScope scope, CancellationToken ct)
        {
            var clean = queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            var fused = new Dictionary<string, (double Score, int? Fts, int? Vec, double? Sim)>(StringComparer.Ordinal);
            int perQuery = Math.Clamp(scope.Limit * 3, 20, 300);
            foreach (var q in clean)
            {
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
        /// An FTS5 query from free text: quoted phrases stay phrases; every other word becomes a term, OR-joined. A word of five
        /// letters or more is a prefix term, so its inflections are found ("football" finds "footballer"). A shorter word is
        /// matched whole, with its plain plural: as a prefix it would find unrelated words ("cat" would find "Catholic").
        /// </summary>
        public static string? FtsQuery(string text)
        {
            var terms = new List<string>();
            var phrases = System.Text.RegularExpressions.Regex.Matches(text, "\"([^\"]{2,80})\"");
            foreach (System.Text.RegularExpressions.Match p in phrases) terms.Add("\"" + Clean(p.Groups[1].Value) + "\"");
            string rest = System.Text.RegularExpressions.Regex.Replace(text, "\"[^\"]*\"", " ");
            foreach (var word in rest.Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!', ';', ':', '(', ')', '[', ']', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = Clean(word);
                if (w.Length < 2) continue;
                if (w.Length >= 5) terms.Add("\"" + w + "\"*");
                else
                {
                    terms.Add("\"" + w + "\"");
                    if (w.Length >= 3 && !w.EndsWith('s')) terms.Add("\"" + w + "s\"");
                }
            }
            terms = terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            return terms.Count == 0 ? null : string.Join(" OR ", terms);
        }

        private static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '\'' || c == '-' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) sb.Append(c);
            return sb.ToString().Replace("\"", "");
        }

        private List<string> FullText(string query, RetrievalScope scope, int limit)
        {
            string? fts = FtsQuery(query);
            if (fts == null) return new List<string>();
            var where = new List<string> { "messages_fts MATCH $q" };
            var args = new List<(string, object?)> { ("$q", fts), ("$l", limit) };
            if (scope.FromMs is long f) { where.Add("m.sent_at_ms >= $f"); args.Add(("$f", f)); }
            if (scope.ToMs is long t) { where.Add("m.sent_at_ms <= $t"); args.Add(("$t", t)); }
            AddIn(where, args, "m.author_identity_id", "$a", scope.AuthorIdentityIds);
            AddIn(where, args, "m.conversation_id", "$c", scope.ConversationIds);
            AddIn(where, args, "m.message_id", "$mi", scope.MessageIds);
            string sql = $"SELECT m.message_id FROM messages_fts JOIN messages m ON m.rowid = messages_fts.rowid WHERE {string.Join(" AND ", where)} ORDER BY bm25(messages_fts) LIMIT $l";
            using var c = stores.Corpus.OpenRead();
            try
            {
                return c.Query(null, sql, r => r.GetString(0), args.ToArray());
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            {
                // A query the FTS grammar rejects (odd symbols) finds nothing by full text; vectors may still find it.
                return new List<string>();
            }
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
                foreach (var row in v.QueryIn(null, $"SELECT item_id, dims, quant, scale, vec FROM vectors WHERE space='{MessageSpace}' AND model=$model AND item_id IN ({{keys}})",
                             ids, r => (Id: r.GetString(0), Dims: r.GetInt32(1), Quant: r.GetString(2), Scale: r.GetDouble(3), Blob: (byte[])r.GetValue(4)), chunk: 400))
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
