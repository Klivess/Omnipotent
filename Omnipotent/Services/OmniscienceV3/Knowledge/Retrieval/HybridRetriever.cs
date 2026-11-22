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
        /// <summary>How many of the scope's most recent messages the vector pass compares against (bounded work per query).
        /// Each one costs a row read and its vector in memory; full-text search still covers the whole scope.</summary>
        public int VectorCandidates { get; init; } = 12_000;

        /// <summary>The same messages in scope: same people, conversations, messages and period. What a scope's row set and
        /// vector candidates are kept under while an inquiry asks its many questions of the one scope.</summary>
        internal string Signature()
        {
            static string Ids(IReadOnlyCollection<string>? ids) => ids == null ? "*" : Store.NaturalKeys.Sha256Hex(string.Join("\n", ids.OrderBy(x => x, StringComparer.Ordinal)))[..24];
            return string.Join("|", Ids(AuthorIdentityIds), Ids(ConversationIds), Ids(MessageIds), FromMs, ToMs);
        }
    }

    /// <summary>A set of message rows (by rowid) as one bit each: the whole corpus is under a megabyte, and asking whether a
    /// row is in scope reads no page of the database.</summary>
    internal sealed class RowSet
    {
        private readonly System.Collections.BitArray bits;

        public RowSet(long maxRowId) => bits = new System.Collections.BitArray((int)Math.Min(int.MaxValue - 64, Math.Max(0, maxRowId) + 1));

        public int Count { get; private set; }

        public void Add(long rowId)
        {
            if (rowId < 0 || rowId >= bits.Length || bits[(int)rowId]) return;
            bits[(int)rowId] = true;
            Count++;
        }

        public bool Contains(long rowId) => rowId >= 0 && rowId < bits.Length && bits[(int)rowId];

        public void IntersectWith(RowSet other)
        {
            bits.And(other.bits);
            int n = 0;
            for (int i = 0; i < bits.Length; i++) if (bits[i]) n++;
            Count = n;
        }
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

        /// <summary>The matches of a full-text query, best first, read from the full-text index alone.</summary>
        internal const string RankedMatches = "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q ORDER BY rank";

        private const long ScopeTtlMs = 15 * 60_000;
        private readonly ConcurrentDictionary<string, (RowSet Rows, bool PeriodApplied, long AtTick)> scopeRows = new(StringComparer.Ordinal);
        private sealed record KeptVectors(string Key, List<string> Ids, List<float[]> Vectors, long AtTick);
        /// <summary>The vector candidates of the scopes searched lately. An inquiry goes round three scopes for every facet
        /// (the subject's own messages, their conversations, what they reacted to); with room for one, each search read its
        /// thousands of rows and vectors again, because the search before it had been of another scope.</summary>
        private readonly ConcurrentDictionary<string, KeptVectors> vectorCandidates = new(StringComparer.Ordinal);
        /// <summary>How many scopes' candidates are kept, and how many vectors in all (a vector is its dimensions × 4 bytes).</summary>
        internal const int KeptScopes = 6, KeptVectorLimit = 40_000;
        /// <summary>How many times candidate vectors were read from the stores (not served from what is kept).</summary>
        internal int CandidateLoads => Volatile.Read(ref candidateLoads);
        private int candidateLoads;

        /// <summary>
        /// The best matches of a full-text query within a scope, reading a message's row only for the few that are returned.
        /// <para>The matches come best first from the full-text index. Which rows are in scope is known beforehand as a set of
        /// row numbers, read from the index that covers the scope (author and time, or conversation and time), so a match is
        /// kept or passed over without touching the messages table. The statement this replaces joined every match to its
        /// message row to test the scope and then sorted: a query of two dozen words, each in up to one message in a hundred,
        /// was over a million row reads in a 15 GB table, and an inquiry asks dozens of such queries of each scope. On the
        /// server two tactic runs spent an hour in this stage three times over and were given up on (20 Nov 2026). Before
        /// that the planner walked the scope instead and ran the match per message (18 Nov 2026).</para>
        /// </summary>
        private List<string> FullText(string query, RetrievalScope scope, int limit)
        {
            using var c = stores.Corpus.OpenRead();
            long cap = CommonCap(c);
            string? fts = FtsQuery(query, term => TooCommon(c, term, cap));
            if (fts == null) return new List<string>();
            try
            {
                var (rows, periodApplied) = ScopeRows(c, scope);
                if (rows is { Count: 0 }) return new List<string>();
                // Where the row set could not carry the period (no scope, or chosen messages), it is checked on the rows that
                // are read at the end, and a few more are taken to allow for the ones it turns away.
                bool checkPeriod = !periodApplied && (scope.FromMs != null || scope.ToMs != null);
                int want = checkPeriod ? limit * 3 : limit;
                var hits = new List<long>(want);
                c.Scan(null, RankedMatches, r =>
                {
                    long row = r.GetInt64(0);
                    if (rows == null || rows.Contains(row)) hits.Add(row);
                    return hits.Count < want;
                }, ("$q", fts));
                if (hits.Count == 0) return new List<string>();
                var found = new Dictionary<long, (string Id, long SentAtMs)>(hits.Count);
                foreach (var chunk in hits.Chunk(500))
                    foreach (var (row, id, at) in c.Query(null, $"SELECT rowid, message_id, sent_at_ms FROM messages WHERE rowid IN ({string.Join(",", chunk)})",
                                 r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2))))
                        found[row] = (id, at);
                return hits.Where(found.ContainsKey).Select(h => found[h])
                    .Where(m => !checkPeriod || ((scope.FromMs == null || m.SentAtMs >= scope.FromMs) && (scope.ToMs == null || m.SentAtMs <= scope.ToMs)))
                    .Select(m => m.Id).Take(limit).ToList();
            }
            catch (SqliteException ex) when (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            {
                // A query the FTS grammar rejects (odd symbols) finds nothing by full text; vectors may still find it.
                return new List<string>();
            }
        }

        /// <summary>The statement that reads the rows of one part of a scope (its authors, or its conversations) within the
        /// scope's period, from the index that covers it: no row of the messages table is read.</summary>
        internal static string ScopeRowsStatement(string column, string keys, RetrievalScope scope) =>
            $"SELECT rowid FROM messages INDEXED BY {(column == "author_identity_id" ? "ix_messages_author_time" : "ix_messages_conv_time")} WHERE {column} IN ({keys})"
            + (scope.FromMs != null ? " AND sent_at_ms >= $f" : "") + (scope.ToMs != null ? " AND sent_at_ms <= $t" : "");

        /// <summary>The rows a scope covers (null: every row), and whether the scope's period is already applied to them.
        /// Kept for a few minutes: an inquiry asks every one of its questions of the same few scopes.</summary>
        private (RowSet? Rows, bool PeriodApplied) ScopeRows(SqliteConnection c, RetrievalScope scope)
        {
            if (scope.AuthorIdentityIds == null && scope.ConversationIds == null && scope.MessageIds == null) return (null, false);
            string key = scope.Signature();
            long now = Environment.TickCount64;
            if (scopeRows.TryGetValue(key, out var kept) && now - kept.AtTick < ScopeTtlMs) return (kept.Rows, kept.PeriodApplied);

            long max = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            RowSet? rows = null;
            bool periodApplied = false;
            var period = new List<(string, object?)>();
            if (scope.FromMs is long f) period.Add(("$f", f));
            if (scope.ToMs is long t) period.Add(("$t", t));
            void Narrow(RowSet part)
            {
                if (rows == null) rows = part;
                else rows.IntersectWith(part);
            }
            void ByIndex(string column, IReadOnlyCollection<string> ids)
            {
                var part = new RowSet(max);
                foreach (var chunk in ids.Take(200).Chunk(100))
                {
                    var args = chunk.Select((id, i) => ("$k" + i, (object?)id)).Concat(period).ToArray();
                    c.Scan(null, ScopeRowsStatement(column, string.Join(",", chunk.Select((_, i) => "$k" + i)), scope), r => { part.Add(r.GetInt64(0)); return true; }, args);
                }
                Narrow(part);
                periodApplied = true;
            }
            if (scope.AuthorIdentityIds != null) ByIndex("author_identity_id", scope.AuthorIdentityIds);
            if (scope.ConversationIds != null) ByIndex("conversation_id", scope.ConversationIds);
            if (scope.MessageIds != null)
            {
                var part = new RowSet(max);
                foreach (long row in c.QueryIn(null, "SELECT rowid FROM messages WHERE message_id IN ({keys})", scope.MessageIds.Take(800).ToList(), r => r.GetInt64(0))) part.Add(row);
                Narrow(part);
            }
            if (scopeRows.Count >= 8) scopeRows.Clear();
            scopeRows[key] = (rows!, periodApplied, now);
            return (rows, periodApplied);
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

        /// <summary>
        /// The scope's most recent messages that have a vector, with their vectors. Kept for a few minutes under the scope:
        /// an inquiry searches the same scope once per facet and again for counter-evidence, and each search used to read
        /// the candidates' rows and vectors anew (40,000 row reads a time).
        /// </summary>
        private (List<string> Ids, List<float[]> Vectors) LoadCandidateVectors(IEmbeddingEncoder enc, RetrievalScope scope)
        {
            if (scope.AuthorIdentityIds == null && scope.ConversationIds == null && scope.MessageIds == null) return (new List<string>(), new List<float[]>()); // never scan the whole corpus per query
            string key = scope.Signature() + "|" + scope.VectorCandidates + "|" + enc.ModelVersion;
            long now = Environment.TickCount64;
            if (vectorCandidates.TryGetValue(key, out var kept) && now - kept.AtTick < ScopeTtlMs) return (kept.Ids, kept.Vectors);
            Interlocked.Increment(ref candidateLoads);

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
                if (scope.MessageIds != null)
                    ids = c.Query(null, $"SELECT m.message_id FROM messages m WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT $l", r => r.GetString(0), args.ToArray());
                else if (scope.AuthorIdentityIds == null || scope.ConversationIds == null)
                {
                    // One list of accounts, or one of conversations: the newest of each come straight off the end of its
                    // run in the index, and the newest of them all are picked from those. Asked for in one statement
                    // ("in any of these sixty conversations, newest first"), every index entry of all sixty was read and
                    // sorted to find a few thousand.
                    bool byAuthor = scope.AuthorIdentityIds != null;
                    var newest = new List<(long Row, long At)>();
                    foreach (var one in (byAuthor ? scope.AuthorIdentityIds! : scope.ConversationIds!).Take(200))
                    {
                        var own = new List<(string, object?)> { ("$k", one), ("$l", scope.VectorCandidates) };
                        if (scope.FromMs is long from) own.Add(("$f", from));
                        if (scope.ToMs is long to) own.Add(("$t", to));
                        newest.AddRange(c.Query(null, NewestOfOneStatement(byAuthor ? "author_identity_id" : "conversation_id", scope), r => (r.GetInt64(0), r.GetInt64(1)), own.ToArray()));
                    }
                    foreach (var chunk in newest.OrderByDescending(n => n.At).ThenByDescending(n => n.Row).Take(scope.VectorCandidates).Select(n => n.Row).Chunk(500))
                        ids.AddRange(c.Query(null, $"SELECT message_id FROM messages WHERE rowid IN ({string.Join(",", chunk)})", r => r.GetString(0)));
                }
                else
                {
                    // Which rows are the newest is settled in the index (it holds the time and the row number), and only
                    // those rows are then read for their ids. Asked for ids outright, with several accounts or conversations
                    // in scope, the statement read every row in scope to sort them.
                    string index = scope.AuthorIdentityIds != null ? "ix_messages_author_time" : "ix_messages_conv_time";
                    var newest = c.Query(null, $"SELECT m.rowid FROM messages m INDEXED BY {index} WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT $l", r => r.GetInt64(0), args.ToArray());
                    foreach (var chunk in newest.Chunk(500))
                        ids.AddRange(c.Query(null, $"SELECT message_id FROM messages WHERE rowid IN ({string.Join(",", chunk)})", r => r.GetString(0)));
                }
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
            var keptVectors = keptIds.Select(id => vectors[id]).ToList();
            vectorCandidates[key] = new KeptVectors(key, keptIds, keptVectors, now);
            // The oldest go first, until what is kept is within both limits (the scope just read always stays).
            foreach (var old in vectorCandidates.Values.OrderBy(k => k.AtTick).ToList())
            {
                bool stale = now - old.AtTick >= ScopeTtlMs;
                if (!stale && vectorCandidates.Count <= KeptScopes && vectorCandidates.Values.Sum(k => (long)k.Vectors.Count) <= KeptVectorLimit) break;
                if (old.Key != key) vectorCandidates.TryRemove(old.Key, out _);
            }
            return (keptIds, keptVectors);
        }

        /// <summary>The statement that reads the newest rows of one account or one conversation within the scope's period,
        /// newest first, from the index alone: the entries are met in that order, so nothing is sorted and no row is read.</summary>
        internal static string NewestOfOneStatement(string column, RetrievalScope scope) =>
            $"SELECT rowid, sent_at_ms FROM messages INDEXED BY {(column == "author_identity_id" ? "ix_messages_author_time" : "ix_messages_conv_time")} WHERE {column}=$k"
            + (scope.FromMs != null ? " AND sent_at_ms >= $f" : "") + (scope.ToMs != null ? " AND sent_at_ms <= $t" : "") + " ORDER BY sent_at_ms DESC LIMIT $l";

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
