using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// How long a search may go on, counted from when this was made. The archive is on a drive where a page that is not
    /// in memory takes 25 to 150 ms to read, so what a search costs is decided by what happens to be in memory, not by
    /// the question: the same evidence was found in seconds on one run and not in an hour on the one before
    /// (22 Nov 2026, four inquiries stopped in this stage after 60 minutes, three attempts each). Whoever is waiting for
    /// an answer gives the search a budget instead. When it runs out, the search ends with what it has and says what it
    /// left out (<see cref="RetrievalCoverage.Note"/>); it never ends with nothing, because every part of it takes one
    /// step whatever the clock says. The vector half stops first (<see cref="VectorShare"/>): it costs a row read per
    /// candidate, where full text covers the whole scope from the index.
    /// </summary>
    public sealed class RetrievalBudget
    {
        private readonly long began = Stopwatch.GetTimestamp();

        public RetrievalBudget(TimeSpan total, double vectorShare = 0.5)
        {
            Total = total < TimeSpan.Zero ? TimeSpan.Zero : total;
            VectorShare = Math.Clamp(vectorShare, 0, 1);
        }

        public TimeSpan Total { get; }
        /// <summary>The share of <see cref="Total"/> after which no more message vectors are read.</summary>
        public double VectorShare { get; }
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(began);
        /// <summary>No time is left: what has been found is what there is.</summary>
        public bool Spent => Elapsed >= Total;
        /// <summary>No time is left for reading message vectors; full text goes on.</summary>
        public bool VectorsSpent => Elapsed >= Total * VectorShare;
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
    /// <para>What it reads is kept small on purpose, because on the server every page that is not in memory is a wait:
    /// a word's matches are read once from the full-text index and kept; which of them are in a scope is asked of a set
    /// of row numbers in memory; matches are ranked here, by how many of the question's words they hold and how rare
    /// those are; and a message's row is read only for the few results that are returned. Several scopes asked the same
    /// question (<see cref="SearchScopesAsync"/>) share all of it.</para>
    /// </summary>
    public sealed class HybridRetriever
    {
        public const string MessageSpace = "message";
        private const int RrfK = 60;
        /// <summary>A word found in more than this share of all messages is left out of full-text queries (with at least
        /// <see cref="CommonTermFloor"/> messages): it tells nothing apart, and a query costs as much as its commonest term.</summary>
        internal const double CommonShare = 0.01;
        private const long CommonTermTtlMs = 10 * 60_000;
        private const int CommonTermCacheLimit = 8_192;
        /// <summary>How many row numbers the kept matches of all words may add up to (eight bytes each) before they are dropped.</summary>
        private const long KeptTermRows = 4_000_000;
        private const int KeptQueryVectors = 512;

        private readonly OmniscienceStores stores;
        private readonly CorpusReader corpus;
        private readonly Func<IEmbeddingEncoder?> encoder;
        private CommonCapReading? commonCap;

        private sealed record CommonCapReading(long Cap, long Messages, long AtTick);

        public HybridRetriever(OmniscienceStores stores, CorpusReader corpus, Func<IEmbeddingEncoder?> encoder)
        {
            this.stores = stores;
            this.corpus = corpus;
            this.encoder = encoder;
        }

        /// <summary>The fewest messages a word must appear in before it can count as too common (a small corpus prunes nothing).</summary>
        internal long CommonTermFloor { get; init; } = 2_000;

        /// <summary>How many of an account's or a conversation's newest messages are listed before the next one's are begun
        /// (the rest of each follows, while there is time): a search that runs out of time has then looked at the recent
        /// part of everything in scope, not at all of the first conversation and none of the last.</summary>
        internal int FirstPassRows { get; init; } = 5_000;

        public async Task<(IReadOnlyList<RetrievedMessage> Results, RetrievalCoverage Coverage)> SearchAsync(IReadOnlyList<string> queries, RetrievalScope scope, CancellationToken ct) =>
            (await SearchScopesAsync(queries, new[] { scope }, ct).ConfigureAwait(false))[0];

        /// <summary>
        /// Asks the same questions of several scopes at once and returns each scope's own ranking, in the order given.
        /// <para>An inquiry asks every facet's questions of three scopes: what the subject wrote, what was said in the
        /// conversations they are in, and what they reacted to. Asked one scope at a time, each question was run against
        /// the whole full-text index three times and its text encoded three times, to be filtered three different ways.
        /// Here the index is read once per word and the questions are encoded once.</para>
        /// </summary>
        /// <param name="budget">How long the search may go on (null: until it is done). See <see cref="RetrievalBudget"/>.</param>
        public async Task<IReadOnlyList<(IReadOnlyList<RetrievedMessage> Results, RetrievalCoverage Coverage)>> SearchScopesAsync(
            IReadOnlyList<string> queries, IReadOnlyList<RetrievalScope> scopes, CancellationToken ct, RetrievalBudget? budget = null)
        {
            var clean = queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            var asked = scopes.Select(s => new Asked(s)).ToList();
            var enc = encoder();
            if (clean.Count > 0)
            {
                using (var c = stores.Corpus.OpenRead())
                {
                    foreach (var a in asked) a.Enter(ScopeOf(c, a.Scope, budget));
                    FullText(c, clean, asked, budget, ct);
                }
                if (enc != null) await VectorsAsync(enc, clean, asked, budget, ct).ConfigureAwait(false);
            }
            if (enc == null) foreach (var a in asked) a.Say("The shared encoder is not installed; full-text search only.");
            var results = new List<(IReadOnlyList<RetrievedMessage>, RetrievalCoverage)>(asked.Count);
            foreach (var a in asked)
                results.Add((Resolve(a), new RetrievalCoverage(true, enc != null && a.Candidates > 0, enc?.ModelId, clean.Count, a.Candidates, a.Notes.Count == 0 ? null : string.Join(" ", a.Notes))));
            return results;
        }

        /// <summary>One scope of a search while it is being answered: what is in it, and what has been found in it so far.</summary>
        private sealed class Asked
        {
            public Asked(RetrievalScope scope)
            {
                Scope = scope;
                PerQuery = Math.Clamp(scope.Limit * 3, 20, 300);
            }

            public RetrievalScope Scope { get; }
            /// <summary>How many of one question's best matches go into the fused ranking.</summary>
            public int PerQuery { get; }
            public KeptScope Kept { get; private set; } = null!;
            /// <summary>The scope's period could not be put into its row set (no scope at all, or chosen messages): it is
            /// checked on the rows read at the end, and a few more matches are taken to allow for the ones it turns away.</summary>
            public bool CheckPeriod { get; private set; }
            public int Want => CheckPeriod ? PerQuery * 3 : PerQuery;
            /// <summary>By row number: no message's row has been read yet.</summary>
            public Dictionary<long, (double Score, int? Fts, int? Vec, double? Sim)> Fused { get; } = new();
            /// <summary>Rows whose message id is already known (the vector candidates carry theirs).</summary>
            public Dictionary<long, string> KnownIds { get; } = new();
            public int Candidates { get; set; }
            public List<string> Notes { get; } = new();

            public void Enter(KeptScope kept)
            {
                Kept = kept;
                CheckPeriod = !kept.PeriodApplied && (Scope.FromMs != null || Scope.ToMs != null);
                if (kept.Partial) Say("The time allowed for finding evidence ran out while this scope's messages were being listed: only the newest of them were searched.");
            }

            public void Say(string note)
            {
                if (!Notes.Contains(note)) Notes.Add(note);
            }

            /// <summary>Adds one question's ranking (best first) to the fused one.</summary>
            public void Rank(IReadOnlyList<long> rows, bool fullText, IReadOnlyList<float>? similarity = null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ref var cur = ref CollectionsMarshal.GetValueRefOrAddDefault(Fused, rows[i], out _);
                    cur.Score += 1.0 / (RrfK + i + 1);
                    if (fullText) cur.Fts = cur.Fts == null ? i + 1 : Math.Min(cur.Fts.Value, i + 1);
                    else
                    {
                        cur.Vec = cur.Vec == null ? i + 1 : Math.Min(cur.Vec.Value, i + 1);
                        cur.Sim = Math.Max(cur.Sim ?? double.MinValue, similarity![i]);
                    }
                }
            }
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
            var terms = FtsTerms(text, tooCommon);
            return terms.Count == 0 ? null : string.Join(" OR ", terms);
        }

        /// <summary>The terms of <see cref="FtsQuery(string, Func{string, bool}?)"/>, one by one: each is a full-text query of
        /// its own (a word, a word with its inflections, a phrase). A search looks them up separately, so that each is read
        /// from the index once however many questions hold it.</summary>
        internal static List<string> FtsTerms(string text, Func<string, bool>? tooCommon)
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
            return terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        }

        /// <summary>Where words end: whitespace and punctuation, and the apostrophes and hyphens unicode61 also splits on.</summary>
        private static readonly char[] WordSeparators = { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '(', ')', '[', ']', '/', '\\', '\'', '-', '\u2019' };

        private static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '\'' || c == '-' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) sb.Append(c);
            return sb.ToString().Replace("\"", "");
        }

        // ── full text ────────────────────────────────────────────────────────────

        /// <summary>Every match of one full-text term, in row order, read from the full-text index alone: no message's row,
        /// and none of the index's per-message sizes. The limit is the most a word may match before it is too common.</summary>
        internal const string TermMatches = "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q LIMIT $l";

        /// <summary>The matches of a term among the messages stored after a given row: what has arrived since its matches were kept.</summary>
        internal const string TermMatchesAfter = "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q AND rowid > $after LIMIT $l";

        /// <param name="UpTo">The newest message there was when the rows were read: later ones are not among them yet.</param>
        private sealed record TermRows(long[]? Rows, long UpTo, long AtTick);
        /// <summary>The matches of the words searched lately (null: the word is too common to search for).</summary>
        private readonly ConcurrentDictionary<string, TermRows> termRows = new(StringComparer.OrdinalIgnoreCase);
        private long termRowsKept;

        /// <summary>How many times a word's matches were read from the full-text index (not served from what is kept).</summary>
        internal int TermReads => Volatile.Read(ref termReads);
        private int termReads;

        /// <summary>
        /// The best full-text matches of each question within each scope, as row numbers.
        /// <para>A question's words are looked up one at a time, each once: its matches come off the full-text index as
        /// row numbers and are kept for ten minutes, so the paraphrases of a facet and the facets of an inquiry, which
        /// share most of their words, read each word once between them. Whether a match is in a scope is a bit in memory
        /// (<see cref="RowSet"/>). A match scores the rarity of every word of the question it holds, so one holding three
        /// of them stands above one holding the commonest; among equals the newer comes first.</para>
        /// <para>The statement this replaces asked the index for a question's matches best first (<c>ORDER BY rank</c>).
        /// To put them in order SQLite scored every match in the archive, and the score needs each matching message's
        /// length, which is a lookup in a table of its own: a question of two dozen words was up to a million and a half
        /// lookups scattered over that table, repeated for each of the three scopes, to return twenty to forty rows.
        /// Then each of those rows was read from the 15 GB messages table for its id, though only a third survive the
        /// fusion. On the server one inquiry spent over an hour in this, three times (22 Nov 2026).</para>
        /// </summary>
        private void FullText(SqliteConnection c, IReadOnlyList<string> queries, List<Asked> asked, RetrievalBudget? budget, CancellationToken ct)
        {
            var live = asked.Where(a => a.Kept.Rows is not { Count: 0 }).ToList();
            if (live.Count == 0) return;
            var common = Common(c);
            // The newest message there is now: what is kept of a word's matches is brought up to it before it is used.
            long newest = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            bool first = true;
            foreach (var q in queries)
            {
                ct.ThrowIfCancellationRequested();
                // One wording is always tried; the rest only while there is time.
                if (!first && budget is { Spent: true })
                {
                    foreach (var a in live) a.Say("The time allowed for finding evidence ran out before every wording of the search had been tried.");
                    break;
                }
                first = false;
                var terms = FtsTerms(q, term => RowsOf(c, term, common.Cap, newest) == null);
                if (terms.Count == 0) continue;
                var tallies = new Dictionary<long, double>[live.Count];
                for (int s = 0; s < live.Count; s++) tallies[s] = new Dictionary<long, double>();
                foreach (var term in terms)
                {
                    var rows = RowsOf(c, term, common.Cap, newest);
                    if (rows == null || rows.Length == 0) continue;
                    double n = rows.Length, all = Math.Max(common.Messages, rows.Length);
                    double weight = Math.Log(1 + (all - n + 0.5) / (n + 0.5));
                    for (int s = 0; s < live.Count; s++)
                    {
                        var inScope = live[s].Kept.Rows;
                        var tally = tallies[s];
                        foreach (long row in rows)
                            if (inScope == null || inScope.Contains(row))
                                CollectionsMarshal.GetValueRefOrAddDefault(tally, row, out _) += weight;
                    }
                }
                for (int s = 0; s < live.Count; s++) live[s].Rank(Best(tallies[s], live[s].Want), fullText: true);
            }
        }

        /// <summary>The <paramref name="k"/> best-scored rows, best first; among equal scores the higher row (the newer message).</summary>
        private static List<long> Best(Dictionary<long, double> tally, int k)
        {
            if (tally.Count <= k)
                return tally.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).Select(kv => kv.Key).ToList();
            // The k best of many, without sorting the many: the worst of the best is at the head and is dropped when a better one comes.
            var best = new PriorityQueue<long, (double Score, long Row)>(k + 1);
            foreach (var (row, score) in tally)
            {
                best.Enqueue(row, (score, row));
                if (best.Count > k) best.Dequeue();
            }
            var rows = new List<long>(best.Count);
            while (best.Count > 0) rows.Add(best.Dequeue());
            rows.Reverse();
            return rows;
        }

        /// <summary>
        /// Every message a full-text term matches, as row numbers in ascending order, or null when it matches more than
        /// <paramref name="cap"/> of them (too common to search for). Kept for ten minutes: the read stops one past the cap,
        /// so a word costs at most that many index entries, once.
        /// <para>What is kept is never behind the archive: before it is used, the matches among the messages that arrived
        /// since it was read are added (<paramref name="newest"/> is the newest message there is now). A standing question
        /// is asked of the messages of the last minutes, and found none of them in matches read before they came. An edit
        /// or a deletion is seen when the ten minutes are up, as it was for which words are too common.</para>
        /// </summary>
        private long[]? RowsOf(SqliteConnection c, string term, long cap, long newest)
        {
            long now = Environment.TickCount64;
            try
            {
                if (termRows.TryGetValue(term, out var known) && now - known.AtTick < CommonTermTtlMs)
                {
                    if (known.Rows == null || known.UpTo >= newest) return known.Rows;
                    var arrived = new List<long>();
                    // After the last row already kept as well: a message stored while the word was being read is in both.
                    long after = known.Rows.Length > 0 ? Math.Max(known.UpTo, known.Rows[^1]) : known.UpTo;
                    c.Scan(null, TermMatchesAfter, r => { arrived.Add(r.GetInt64(0)); return true; }, ("$q", term), ("$after", after), ("$l", cap + 1 - known.Rows.Length));
                    long[]? brought = arrived.Count == 0 ? known.Rows : known.Rows.Length + arrived.Count > cap ? null : known.Rows.Concat(arrived).ToArray();
                    // Its age is kept: the whole word is read again when the ten minutes are up.
                    termRows[term] = new TermRows(brought, newest, known.AtTick);
                    Interlocked.Add(ref termRowsKept, (brought?.Length ?? 0) - known.Rows.Length);
                    return brought;
                }
                var found = new List<long>();
                Interlocked.Increment(ref termReads);
                c.Scan(null, TermMatches, r => { found.Add(r.GetInt64(0)); return true; }, ("$q", term), ("$l", cap + 1));
                long[]? rows = found.Count > cap ? null : found.ToArray();
                if (termRows.Count >= CommonTermCacheLimit || Interlocked.Read(ref termRowsKept) > KeptTermRows)
                {
                    termRows.Clear();
                    Interlocked.Exchange(ref termRowsKept, 0);
                }
                termRows[term] = new TermRows(rows, newest, now);
                Interlocked.Add(ref termRowsKept, rows?.Length ?? 0);
                return rows;
            }
            catch (SqliteException ex) when (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            {
                // A term the FTS grammar rejects (odd symbols) finds nothing by full text; vectors may still find it.
                termRows[term] = new TermRows(Array.Empty<long>(), long.MaxValue, now);
                return Array.Empty<long>();
            }
        }

        /// <summary>How many messages a word may appear in before it is too common to search for (<see cref="CommonShare"/> of
        /// the corpus, at least <see cref="CommonTermFloor"/>), and how many messages there are. Read again every ten minutes.</summary>
        private CommonCapReading Common(SqliteConnection c)
        {
            long now = Environment.TickCount64;
            var known = Volatile.Read(ref commonCap);
            if (known != null && now - known.AtTick < CommonTermTtlMs) return known;
            // MAX(rowid) reads one page; it counts every message ever stored, which is near enough for a share.
            long messages = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            var reading = new CommonCapReading(Math.Max(CommonTermFloor, (long)(messages * CommonShare)), messages, now);
            Volatile.Write(ref commonCap, reading);
            return reading;
        }

        // ── scopes ───────────────────────────────────────────────────────────────

        private const long ScopeTtlMs = 15 * 60_000;

        /// <summary>What is kept of a scope between searches: which rows it holds, and its vector candidates once read.</summary>
        private sealed class KeptScope
        {
            public required string Key { get; init; }
            /// <summary>The rows the scope covers (null: every row).</summary>
            public RowSet? Rows { get; set; }
            /// <summary>Whether the scope's period is already applied to <see cref="Rows"/>.</summary>
            public bool PeriodApplied { get; set; }
            /// <summary>The time ran out while the rows were being listed: the newest are here, older ones may be missing.</summary>
            public bool Partial { get; set; }
            /// <summary>For a scope of chosen messages: their rows and ids, as read from the index of message ids.</summary>
            public (long Row, string Id)[]? Chosen { get; set; }
            public KeptVectors? Vectors { get; set; }
            public long UsedTick;
        }

        private sealed record KeptVectors(string Key, long[] Rows, string[] Ids, float[][] Vectors, bool Partial);

        /// <summary>The scopes searched lately. Kept while they are being used (the fifteen minutes count from the last
        /// search, not the first: a gather that took longer than that read its scopes again in the middle).</summary>
        private readonly ConcurrentDictionary<string, KeptScope> keptScopes = new(StringComparer.Ordinal);
        /// <summary>How many scopes are kept, and how many vectors in all (a vector is its dimensions × 4 bytes).</summary>
        internal const int KeptScopes = 12, KeptVectorLimit = 40_000;
        /// <summary>How many times candidate vectors were read from the stores (not served from what is kept).</summary>
        internal int CandidateLoads => Volatile.Read(ref candidateLoads);
        private int candidateLoads;

        /// <summary>
        /// The rows a scope covers, read once and kept. They come from the index that covers the scope (author and time,
        /// or conversation and time), newest first, one account or conversation at a time: no row of the messages table
        /// is read. With a budget, each one's newest <see cref="FirstPassRows"/> are listed first and the older rest
        /// after, so that running out of time leaves the recent part of everything rather than all of some and none of
        /// others; what is then missing is said (<see cref="KeptScope.Partial"/>) and read again by the next search that
        /// has time.
        /// </summary>
        private KeptScope ScopeOf(SqliteConnection c, RetrievalScope scope, RetrievalBudget? budget)
        {
            string key = scope.Signature();
            long now = Environment.TickCount64;
            if (keptScopes.TryGetValue(key, out var kept) && now - Volatile.Read(ref kept.UsedTick) < ScopeTtlMs && (!kept.Partial || budget is { Spent: true }))
            {
                Volatile.Write(ref kept.UsedTick, now);
                return kept;
            }
            var built = new KeptScope { Key = key, UsedTick = now };
            if (scope.AuthorIdentityIds != null || scope.ConversationIds != null || scope.MessageIds != null)
            {
                long max = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
                RowSet? rows = null;
                void Narrow(RowSet part)
                {
                    if (rows == null) rows = part;
                    else rows.IntersectWith(part);
                }
                RowSet ByIndex(string column, IReadOnlyCollection<string> ids)
                {
                    var part = new RowSet(max);
                    var older = new List<(string Key, long Before)>();
                    bool stopped = false;
                    // Returns false when the time ran out in the middle of this one.
                    bool Read(string one, RetrievalScope within, int limit, out int read, out long oldest)
                    {
                        var args = new List<(string, object?)> { ("$k", one), ("$l", limit) };
                        if (within.FromMs is long f) args.Add(("$f", f));
                        if (within.ToMs is long t) args.Add(("$t", t));
                        int n = 0;
                        long last = long.MaxValue;
                        bool cut = false;
                        c.Scan(null, NewestOfOneStatement(column, within), r =>
                        {
                            part.Add(r.GetInt64(0));
                            last = r.GetInt64(1);
                            if ((++n & 2047) == 0 && budget is { Spent: true }) { cut = true; return false; }
                            return true;
                        }, args.ToArray());
                        (read, oldest) = (n, last);
                        return !cut;
                    }
                    bool any = false;
                    foreach (var one in ids.Take(200))
                    {
                        // The first is always begun: a search never ends with nothing because the clock had already run out.
                        if (any && budget is { Spent: true }) { stopped = true; break; }
                        any = true;
                        if (!Read(one, scope, FirstPassRows, out int read, out long oldest)) { stopped = true; break; }
                        if (read >= FirstPassRows) older.Add((one, oldest));
                    }
                    foreach (var (one, before) in older)
                    {
                        if (stopped || budget is { Spent: true }) { stopped = true; break; }
                        // The rest of this one: everything up to the oldest already listed (the row at that instant is listed twice, which a set does not mind).
                        if (!Read(one, scope with { ToMs = before }, int.MaxValue, out _, out _)) stopped = true;
                    }
                    if (stopped) built.Partial = true;
                    built.PeriodApplied = true;
                    return part;
                }
                if (scope.AuthorIdentityIds != null) Narrow(ByIndex("author_identity_id", scope.AuthorIdentityIds));
                if (scope.ConversationIds != null) Narrow(ByIndex("conversation_id", scope.ConversationIds));
                if (scope.MessageIds != null)
                {
                    // The index of message ids holds the row number: the ids' rows are found without reading a message.
                    var chosen = c.QueryIn(null, "SELECT rowid, message_id FROM messages WHERE message_id IN ({keys})", scope.MessageIds.Take(800).ToList(), r => (Row: r.GetInt64(0), Id: r.GetString(1)));
                    var part = new RowSet(max);
                    foreach (var (row, _) in chosen) part.Add(row);
                    Narrow(part);
                    built.Chosen = chosen.Where(x => rows!.Contains(x.Row)).ToArray();
                }
                built.Rows = rows;
            }
            keptScopes[key] = built;
            Trim(key);
            return built;
        }

        /// <summary>Lets go of what has not been used for a while, the longest unused first, until what is kept is within its
        /// limits. A scope over the vector limit only loses its vectors (its rows are a megabyte, its vectors can be sixty).</summary>
        private void Trim(string justUsed)
        {
            long now = Environment.TickCount64;
            foreach (var old in keptScopes.Values.OrderBy(k => Volatile.Read(ref k.UsedTick)).ToList())
            {
                bool stale = now - Volatile.Read(ref old.UsedTick) >= ScopeTtlMs;
                bool tooMany = keptScopes.Count > KeptScopes;
                bool tooManyVectors = keptScopes.Values.Sum(k => (long)(k.Vectors?.Rows.Length ?? 0)) > KeptVectorLimit;
                if (!stale && !tooMany && !tooManyVectors) break;
                if (old.Key == justUsed) continue;
                if (stale || tooMany) keptScopes.TryRemove(old.Key, out _);
                else old.Vectors = null;
            }
        }

        private static void AddIn(List<string> where, List<(string, object?)> args, string column, string prefix, IReadOnlyCollection<string>? values)
        {
            if (values == null) return;
            if (values.Count == 0) { where.Add("0"); return; }
            var names = values.Take(200).Select((v, i) => (Name: prefix + i, Value: v)).ToList();
            where.Add($"{column} IN ({string.Join(",", names.Select(n => n.Name))})");
            foreach (var (name, value) in names) args.Add((name, value));
        }

        // ── vectors ──────────────────────────────────────────────────────────────

        private readonly ConcurrentDictionary<string, float[]> queryVectors = new(StringComparer.Ordinal);

        /// <summary>How many times question texts were handed to the encoder (not served from what is kept).</summary>
        internal int QueryEncodings => Volatile.Read(ref queryEncodings);
        private int queryEncodings;

        /// <summary>The vector half: each question against each scope's candidates, by cosine similarity. The questions are
        /// encoded once for all the scopes, and a question asked before is not encoded again.</summary>
        private async Task VectorsAsync(IEmbeddingEncoder enc, IReadOnlyList<string> queries, List<Asked> asked, RetrievalBudget? budget, CancellationToken ct)
        {
            float[][]? encoded = null;
            foreach (var a in asked)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = CandidatesOf(enc, a, budget);
                if (candidates == null)
                {
                    a.Say("The time allowed for finding evidence ran out before this scope's message vectors were read: it was searched by full text only.");
                    continue;
                }
                a.Candidates = candidates.Rows.Length;
                if (candidates.Rows.Length == 0)
                {
                    a.Say("No message vectors exist for this scope yet; full-text search only.");
                    continue;
                }
                if (candidates.Partial) a.Say("The time allowed for finding evidence ran out while this scope's message vectors were being read: only its newest messages were compared by meaning.");
                encoded ??= await EncodeAsync(enc, queries, ct).ConfigureAwait(false);
                foreach (var qv in encoded)
                {
                    var best = new PriorityQueue<int, (float Sim, int Order)>(a.PerQuery + 1);
                    for (int i = 0; i < candidates.Rows.Length; i++)
                    {
                        // Among equal similarities the earlier candidate (the newer message) is kept.
                        best.Enqueue(i, (TensorPrimitives.Dot(qv.AsSpan(), candidates.Vectors[i].AsSpan()), -i));
                        if (best.Count > a.PerQuery) best.Dequeue();
                    }
                    var rows = new List<long>(best.Count);
                    var sims = new List<float>(best.Count);
                    while (best.TryDequeue(out int index, out var score))
                    {
                        rows.Add(candidates.Rows[index]);
                        sims.Add(score.Sim);
                        a.KnownIds[candidates.Rows[index]] = candidates.Ids[index];
                    }
                    rows.Reverse();
                    sims.Reverse();
                    a.Rank(rows, fullText: false, sims);
                }
            }
        }

        private async Task<float[][]> EncodeAsync(IEmbeddingEncoder enc, IReadOnlyList<string> queries, CancellationToken ct)
        {
            string under = enc.ModelVersion + "\n";
            var have = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (var q in queries)
            {
                if (queryVectors.TryGetValue(under + q, out var v)) have[q] = v;
                else missing.Add(q);
            }
            if (missing.Count > 0)
            {
                Interlocked.Increment(ref queryEncodings);
                var made = await enc.EmbedAsync(missing, ct).ConfigureAwait(false);
                if (queryVectors.Count + missing.Count > KeptQueryVectors) queryVectors.Clear();
                for (int i = 0; i < missing.Count; i++)
                {
                    have[missing[i]] = made[i];
                    queryVectors[under + missing[i]] = made[i];
                }
            }
            return queries.Select(q => have[q]).ToArray();
        }

        /// <summary>
        /// The scope's most recent messages that have a vector, with their rows, ids and vectors, kept with the scope: an
        /// inquiry searches the same scope once per facet and again for counter-evidence. Null when there is no time left
        /// to read them and none are kept.
        /// <para>Which rows are the newest is settled in the index. Their ids, which the vectors are filed under, are on
        /// the rows themselves: one read of the messages table for each candidate, the costliest part of a search on a
        /// cold archive. They are read newest first, as far as the time allows.</para>
        /// </summary>
        private KeptVectors? CandidatesOf(IEmbeddingEncoder enc, Asked asked, RetrievalBudget? budget)
        {
            var scope = asked.Scope;
            if (scope.AuthorIdentityIds == null && scope.ConversationIds == null && scope.MessageIds == null)
                return new KeptVectors("", Array.Empty<long>(), Array.Empty<string>(), Array.Empty<float[]>(), false); // never scan the whole corpus per query
            string key = scope.VectorCandidates + "|" + enc.ModelVersion;
            var have = asked.Kept.Vectors;
            bool outOfTime = budget is { VectorsSpent: true };
            if (have != null && have.Key == key && (!have.Partial || outOfTime)) return have;
            if (outOfTime) return null;
            Interlocked.Increment(ref candidateLoads);

            var pairs = new List<(long Row, string Id)>();
            bool partial = false;
            if (scope.MessageIds != null)
            {
                // The chosen messages are the candidates, and their rows and ids are already known.
                pairs.AddRange(asked.Kept.Chosen!.Take(scope.VectorCandidates));
            }
            else
            {
                using var c = stores.Corpus.OpenRead();
                List<long> newest;
                if (scope.AuthorIdentityIds == null || scope.ConversationIds == null)
                {
                    // One list of accounts, or one of conversations: the newest of each come straight off the end of its
                    // run in the index, and the newest of them all are picked from those. Asked for in one statement
                    // ("in any of these sixty conversations, newest first"), every index entry of all sixty was read and
                    // sorted to find a few thousand.
                    bool byAuthor = scope.AuthorIdentityIds != null;
                    var each = new List<(long Row, long At)>();
                    foreach (var one in (byAuthor ? scope.AuthorIdentityIds! : scope.ConversationIds!).Take(200))
                    {
                        var own = new List<(string, object?)> { ("$k", one), ("$l", scope.VectorCandidates) };
                        if (scope.FromMs is long from) own.Add(("$f", from));
                        if (scope.ToMs is long to) own.Add(("$t", to));
                        each.AddRange(c.Query(null, NewestOfOneStatement(byAuthor ? "author_identity_id" : "conversation_id", scope), r => (r.GetInt64(0), r.GetInt64(1)), own.ToArray()));
                    }
                    newest = each.OrderByDescending(n => n.At).ThenByDescending(n => n.Row).Take(scope.VectorCandidates).Select(n => n.Row).ToList();
                }
                else
                {
                    // Accounts and conversations together: which rows are the newest is still settled in one index (it
                    // holds the time and the row number), and only those rows are then read.
                    var where = new List<string> { "1=1" };
                    var args = new List<(string, object?)> { ("$l", scope.VectorCandidates) };
                    if (scope.FromMs is long f) { where.Add("m.sent_at_ms >= $f"); args.Add(("$f", f)); }
                    if (scope.ToMs is long t) { where.Add("m.sent_at_ms <= $t"); args.Add(("$t", t)); }
                    AddIn(where, args, "m.author_identity_id", "$a", scope.AuthorIdentityIds);
                    AddIn(where, args, "m.conversation_id", "$c", scope.ConversationIds);
                    newest = c.Query(null, $"SELECT m.rowid FROM messages m INDEXED BY ix_messages_author_time WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT $l", r => r.GetInt64(0), args.ToArray());
                }
                foreach (var chunk in newest.Chunk(500))
                {
                    // The newest five hundred are always read; the older ones while there is time.
                    if (pairs.Count > 0 && budget is { VectorsSpent: true }) { partial = true; break; }
                    pairs.AddRange(c.Query(null, $"SELECT rowid, message_id FROM messages WHERE rowid IN ({string.Join(",", chunk)}) ORDER BY rowid DESC", r => (r.GetInt64(0), r.GetString(1))));
                }
            }
            var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            using (var v = stores.Vectors.OpenRead())
            {
                // Message vectors are filed under the encoder's model VERSION (the classifier writes them so, and a new build of a
                // model never mixes with the old one's vectors), not its model id. Both parameters are bound on every chunk: the
                // model was once left unbound, and every inquiry with the encoder installed failed here (18 Nov 2026).
                foreach (var row in v.QueryIn(null, "SELECT item_id, dims, quant, scale, vec FROM vectors WHERE space=$space AND model=$model AND item_id IN ({keys})",
                             pairs.Select(p => p.Id).ToList(), r => (Id: r.GetString(0), Dims: r.GetInt32(1), Quant: r.GetString(2), Scale: r.GetDouble(3), Blob: (byte[])r.GetValue(4)), 400,
                             ("$space", MessageSpace), ("$model", enc.ModelVersion)))
                {
                    var decoded = Decode(row.Dims, row.Quant, row.Scale, row.Blob);
                    if (decoded != null && decoded.Length == enc.Dimensions) vectors[row.Id] = decoded;
                }
            }
            var with = pairs.Where(p => vectors.ContainsKey(p.Id)).ToList();
            var read = new KeptVectors(key, with.Select(p => p.Row).ToArray(), with.Select(p => p.Id).ToArray(), with.Select(p => vectors[p.Id]).ToArray(), partial);
            asked.Kept.Vectors = read;
            Trim(asked.Kept.Key);
            return read;
        }

        // ── results ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The scope's results: the best of the fused ranking, as messages. Only here is a message's row read, and only for
        /// what is returned (a candidate found by its vector already carries its id): where the period could not be part
        /// of the scope's row set, it is checked on these rows and the next best are taken in place of those turned away.
        /// </summary>
        private List<RetrievedMessage> Resolve(Asked a)
        {
            int limit = Math.Max(0, a.Scope.Limit);
            var results = new List<RetrievedMessage>(Math.Min(limit, a.Fused.Count));
            if (limit == 0 || a.Fused.Count == 0) return results;
            var ordered = a.Fused.OrderByDescending(kv => kv.Value.Score).ThenByDescending(kv => kv.Key).ToList();
            // As many at a time as are wanted: where every row passes (the usual case) no row is read that is not returned.
            int step = limit;
            SqliteConnection? c = null;
            try
            {
                for (int from = 0; from < ordered.Count && results.Count < limit; from += step)
                {
                    var some = ordered.Skip(from).Take(step).ToList();
                    var unread = some.Where(kv => a.CheckPeriod || !a.KnownIds.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
                    var rows = new Dictionary<long, (string Id, long SentAtMs)>(unread.Count);
                    if (unread.Count > 0)
                    {
                        c ??= stores.Corpus.OpenRead();
                        foreach (var (row, id, at) in c.Query(null, $"SELECT rowid, message_id, sent_at_ms FROM messages WHERE rowid IN ({string.Join(",", unread)})",
                                     r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2))))
                            rows[row] = (id, at);
                    }
                    foreach (var (row, f) in some)
                    {
                        string id;
                        if (rows.TryGetValue(row, out var m))
                        {
                            if (a.CheckPeriod && ((a.Scope.FromMs is long fromMs && m.SentAtMs < fromMs) || (a.Scope.ToMs is long toMs && m.SentAtMs > toMs))) continue;
                            id = m.Id;
                        }
                        else if (!a.CheckPeriod && a.KnownIds.TryGetValue(row, out var known)) id = known;
                        else continue; // the message was deleted since it was indexed
                        results.Add(new RetrievedMessage(id, f.Score, f.Fts, f.Vec, f.Sim));
                        if (results.Count == limit) break;
                    }
                }
            }
            finally
            {
                c?.Dispose();
            }
            return results;
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
