using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// What the index holds, as the counters had it at <see cref="ReadAtUtc"/>. <see cref="Exact"/> is
    /// false until the rows that predate the counters have been added in; until then the numbers are
    /// only what changed since the counters were created, and are not shown to anyone.
    /// </summary>
    public sealed record RagCounts(long Documents, long Chunks, long EmbeddedChunks,
        IReadOnlyDictionary<string, long> PerSource, bool Exact, DateTime ReadAtUtc, double ReadMs);

    /// <summary>
    /// One attempt to read the counters. <see cref="Counts"/> is the newest reading there is: the one
    /// just taken, or (when <see cref="Stale"/>) an earlier one because the store did not answer in
    /// time, with <see cref="Problem"/> saying why. Null when no reading has ever succeeded.
    /// </summary>
    public sealed record RagCountsReading(RagCounts? Counts, bool Stale, string? Problem);

    /// <summary>
    /// The index's size for /kliverag/stats, read from the handful of rows SQLite triggers keep in
    /// <c>rag_counters</c> (see <c>KliveRAGDb.SchemaV2</c>) instead of by counting the tables.
    ///
    /// Counting was the route's whole cost: one of its two statements read every chunk row, and on
    /// the live server a request for the stats took up to 26 minutes. A read here is one page of
    /// one small table however large the index grows. It takes no lock that ingestion
    /// holds (a WAL reader does not wait for the writer, and <see cref="KliveRAGDb.WriteLock"/> is
    /// never taken), it runs off the caller's thread with a deadline, and when the disk is too busy
    /// to answer in time the caller gets the previous reading, marked stale, rather than a wait.
    /// </summary>
    public sealed class RagCounters
    {
        /// <summary>Everything the stats need, in one statement over one small table.</summary>
        internal const string ReadSql = "SELECT name, value FROM rag_counters";

        // The one-off count of rows that predate the counters. Each statement names a narrow index,
        // so none of them reads a chunk's text or a document's content, and a missing index stops
        // the count with an error instead of quietly turning it into a pass over the table.
        //
        // COUNT(doc_id), not COUNT(*): doc_id is never null, so the answer is the same, but SQLite
        // runs a bare COUNT(*) as a single b-tree count on an index of its own choosing, which
        // INDEXED BY does not steer and BindDeadline cannot stop part-way.
        internal const string ExistingChunksSql = "SELECT COUNT(doc_id) FROM rag_chunks INDEXED BY idx_ragchunk_doc";
        internal const string ExistingPendingSql =
            "SELECT COUNT(*) FROM rag_chunks INDEXED BY idx_ragchunk_pending WHERE embedded_at IS NULL";
        internal const string ExistingDocumentsSql =
            "SELECT source, COUNT(*) FROM rag_documents INDEXED BY idx_ragdoc_source GROUP BY source";

        private const string ChunksKey = "chunks";
        private const string EmbeddedKey = "chunks_embedded";
        private const string CountedExistingKey = "counted_existing";
        private const string DocumentsPrefix = "docs:";

        /// <summary>A read that is still stepping after this long is stopped, so an abandoned one cannot
        /// hold a connection and a pool thread for good.</summary>
        private static readonly TimeSpan ReadCeiling = TimeSpan.FromSeconds(30);

        private readonly KliveRAGDb db;
        private readonly Func<SqliteConnection, RagCounts> read;
        private readonly object gate = new();
        private Task<RagCounts>? reading;
        private volatile RagCounts? last;
        private volatile string? countingStatus;
        private volatile string? lastCountingFailure;

        public RagCounters(KliveRAGDb db) : this(db, conn => Read(conn)) { }

        /// <summary><paramref name="read"/> lets a test stand in for a store that is slow or failing.</summary>
        internal RagCounters(KliveRAGDb db, Func<SqliteConnection, RagCounts> read)
        {
            this.db = db;
            this.read = read;
        }

        /// <summary>Where the one-off count of existing rows has got to, in words, for the stats note.
        /// Null before it has been asked for and after it has finished.</summary>
        public string? CountingStatus => countingStatus;

        /// <summary>
        /// The counters, waiting no longer than <paramref name="deadline"/>. Never throws. Callers that
        /// arrive while a read is running share it, so a store that has stopped answering holds one
        /// pool thread however often the route is polled.
        /// </summary>
        public async Task<RagCountsReading> CurrentAsync(TimeSpan deadline)
        {
            Task<RagCounts> inFlight;
            lock (gate) inFlight = reading ??= Task.Run(ReadFresh);
            try
            {
                return new RagCountsReading(await inFlight.WaitAsync(deadline).ConfigureAwait(false), false, null);
            }
            catch (TimeoutException)
            {
                return new RagCountsReading(last, true, $"The store did not answer within {deadline.TotalMilliseconds:0} ms.");
            }
            catch (Exception ex)
            {
                return new RagCountsReading(last, true, "The store could not be read: " + ex.Message);
            }
        }

        private RagCounts ReadFresh()
        {
            try
            {
                using var conn = db.Open();
                using var ceiling = new CancellationTokenSource(ReadCeiling);
                using var bound = KliveRAGDb.BindDeadline(conn, ceiling.Token);
                var counts = read(conn);
                last = counts;
                return counts;
            }
            finally
            {
                lock (gate) reading = null;
            }
        }

        /// <summary>
        /// Reads the counters on <paramref name="conn"/>: the whole cost of a stats request. Values come
        /// back as stored, below zero included (possible only before the existing rows are counted):
        /// <see cref="ApplyExistingRowsAsync"/> does its arithmetic on them.
        /// </summary>
        internal static RagCounts Read(SqliteConnection conn, SqliteTransaction? tx = null)
        {
            var timer = Stopwatch.StartNew();
            long documents = 0, chunks = 0, embedded = 0;
            bool exact = false;
            var perSource = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = ReadSql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string name = r.GetString(0);
                    long value = r.GetInt64(1);
                    if (name == ChunksKey) chunks = value;
                    else if (name == EmbeddedKey) embedded = value;
                    else if (name == CountedExistingKey) exact = value != 0;
                    else if (name.StartsWith(DocumentsPrefix, StringComparison.Ordinal))
                    {
                        documents += value;
                        // A source whose documents have all gone keeps its row at zero; the stats never listed those.
                        if (value != 0) perSource[name.Substring(DocumentsPrefix.Length)] = value;
                    }
                }
            }
            return new RagCounts(documents, chunks, embedded, perSource, exact, DateTime.UtcNow, timer.Elapsed.TotalMilliseconds);
        }

        // ── rows that predate the counters ──

        /// <summary>The tables' true sizes and the counters' values, both as of one moment.</summary>
        internal sealed record ExistingRows(long Chunks, long Embedded, IReadOnlyDictionary<string, long> Documents, RagCounts CountersThen);

        private const string CountingWhat = "Counting the rows that were in the index before it kept counts (one pass, in the background)";

        /// <summary>
        /// Adds the rows that were in the index before the counters existed, once. Returns how long the
        /// pass took, or null when there was nothing to do (which is every start but the first after
        /// the upgrade, and costs one counters read). The pass reads three narrow indexes, about a
        /// fourteenth of what one stats request used to read; the stats say "counting" until it lands.
        /// <paramref name="startAfter"/> holds it back so it does not compete with start-up's own reads.
        /// To have a store recounted, set <c>counted_existing</c> back to 0: the next start redoes this,
        /// and the arithmetic in <see cref="ApplyExistingRowsAsync"/> corrects a counter whatever it held.
        /// </summary>
        public async Task<TimeSpan?> CountExistingRowsAsync(TimeSpan startAfter = default, CancellationToken ct = default)
        {
            var now = await CurrentAsync(ReadCeiling).ConfigureAwait(false);
            if (now.Counts is { Exact: true }) { countingStatus = null; return null; }

            string? earlier = lastCountingFailure == null ? null : " The attempt before this one failed: " + lastCountingFailure;
            if (startAfter > TimeSpan.Zero)
            {
                countingStatus = $"{CountingWhat} is due to start at {DateTime.UtcNow + startAfter:HH:mm:ss} UTC.{earlier}";
                await Task.Delay(startAfter, ct).ConfigureAwait(false);
            }

            countingStatus = $"{CountingWhat} started at {DateTime.UtcNow:HH:mm:ss} UTC.{earlier}";
            var timer = Stopwatch.StartNew();
            try
            {
                var existing = await Task.Run(() =>
                {
                    using var conn = db.Open();
                    using var bound = KliveRAGDb.BindDeadline(conn, ct);
                    return ReadExistingRows(conn);
                }, ct).ConfigureAwait(false);
                bool applied = await ApplyExistingRowsAsync(existing, ct).ConfigureAwait(false);
                countingStatus = null;
                lastCountingFailure = null;
                return applied ? timer.Elapsed : null;
            }
            catch (Exception ex)
            {
                lastCountingFailure = $"{ex.Message} ({DateTime.UtcNow:HH:mm:ss} UTC, after {timer.Elapsed.TotalSeconds:0.0} s)";
                countingStatus = $"{CountingWhat} failed and will be tried again: {lastCountingFailure}";
                throw;
            }
        }

        /// <summary>
        /// One read transaction, so the table counts and the counter values describe the same moment
        /// however long the pass takes and whatever ingestion writes meanwhile. Deferred: it must not
        /// take SQLite's write lock, or ingestion would stop for the length of the pass.
        /// </summary>
        internal static ExistingRows ReadExistingRows(SqliteConnection conn)
        {
            using var tx = conn.BeginTransaction(deferred: true);
            var countersThen = Read(conn, tx);
            long chunks = Convert.ToInt64(Scalar(conn, tx, ExistingChunksSql));
            long pending = Convert.ToInt64(Scalar(conn, tx, ExistingPendingSql));
            var documents = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = ExistingDocumentsSql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) documents[r.GetString(0)] = r.GetInt64(1);
            }
            tx.Commit();
            return new ExistingRows(chunks, chunks - pending, documents, countersThen);
        }

        /// <summary>
        /// Moves each counter by (true size then) - (counter then). Whatever was written between the
        /// read and now is already in the counter and stays there, so the result is exact without
        /// ever stopping ingestion. Guarded by <c>counted_existing</c> inside the same transaction,
        /// so it lands once however many times it is attempted.
        /// </summary>
        internal async Task<bool> ApplyExistingRowsAsync(ExistingRows existing, CancellationToken ct = default)
        {
            await db.WriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                if (Convert.ToInt64(Scalar(conn, tx, $"SELECT COALESCE((SELECT value FROM rag_counters WHERE name = '{CountedExistingKey}'), 0)")) != 0)
                    return false;

                var then = existing.CountersThen;
                Add(conn, tx, ChunksKey, existing.Chunks - then.Chunks);
                Add(conn, tx, EmbeddedKey, existing.Embedded - then.EmbeddedChunks);
                var sources = new HashSet<string>(existing.Documents.Keys, StringComparer.Ordinal);
                sources.UnionWith(then.PerSource.Keys);
                foreach (string source in sources)
                {
                    existing.Documents.TryGetValue(source, out long truly);
                    then.PerSource.TryGetValue(source, out long counted);
                    Add(conn, tx, DocumentsPrefix + source, truly - counted);
                }
                Add(conn, tx, CountedExistingKey, 1);
                tx.Commit();
                return true;
            }
            finally { db.WriteLock.Release(); }
        }

        private static object? Scalar(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }

        private static void Add(SqliteConnection conn, SqliteTransaction tx, string name, long difference)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rag_counters(name, value) VALUES($n, $d) ON CONFLICT(name) DO UPDATE SET value = value + $d";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", difference);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The body of /kliverag/stats. The first nine fields are the ones the route has always had; the
    /// counts are null whenever they are not known to be right, and <see cref="CountsState"/> says which
    /// case this is, so a number here is never a guess.
    /// </summary>
    public sealed class RagStats
    {
        /// <summary>Every count is exact.</summary>
        public const string Exact = "exact";
        /// <summary>The rows that predate the counters are still being counted (once, after the upgrade).</summary>
        public const string Counting = "counting";
        /// <summary>The store has not answered a read yet.</summary>
        public const string Unavailable = "unavailable";

        [JsonProperty("ready")] public bool Ready { get; init; }
        [JsonProperty("ftsAvailable")] public bool FtsAvailable { get; init; }
        [JsonProperty("embedderWarm")] public bool EmbedderWarm { get; init; }
        [JsonProperty("searxngReady")] public bool SearxngReady { get; init; }
        [JsonProperty("documents")] public long? Documents { get; init; }
        [JsonProperty("chunks")] public long? Chunks { get; init; }
        [JsonProperty("embeddedChunks")] public long? EmbeddedChunks { get; init; }
        [JsonProperty("pendingEmbeds")] public long? PendingEmbeds { get; init; }
        [JsonProperty("perSource")] public IReadOnlyDictionary<string, long> PerSource { get; init; } = new Dictionary<string, long>();

        /// <summary><see cref="Exact"/>, <see cref="Counting"/> or <see cref="Unavailable"/>.</summary>
        [JsonProperty("countsState")] public string CountsState { get; init; } = Unavailable;
        /// <summary>True when the counts are an earlier reading because the store did not answer in time.</summary>
        [JsonProperty("countsStale")] public bool CountsStale { get; init; }
        /// <summary>When the counters were read (UTC).</summary>
        [JsonProperty("countsAsOf")] public DateTime? CountsAsOf { get; init; }
        /// <summary>How long that read took: the whole cost of the request.</summary>
        [JsonProperty("countsReadMs")] public double? CountsReadMs { get; init; }
        /// <summary>Why the counts are missing or stale; null when there is nothing to say.</summary>
        [JsonProperty("countsNote")] public string? CountsNote { get; init; }

        public static RagStats Compose(RagCountsReading reading, string? countingStatus,
            bool ready, bool ftsAvailable, bool embedderWarm, bool searxngReady)
        {
            var counts = reading.Counts;
            bool exact = counts is { Exact: true };
            return new RagStats
            {
                Ready = ready,
                FtsAvailable = ftsAvailable,
                EmbedderWarm = embedderWarm,
                SearxngReady = searxngReady,
                Documents = exact ? counts!.Documents : null,
                Chunks = exact ? counts!.Chunks : null,
                EmbeddedChunks = exact ? counts!.EmbeddedChunks : null,
                PendingEmbeds = exact ? counts!.Chunks - counts.EmbeddedChunks : null,
                PerSource = exact ? counts!.PerSource : new Dictionary<string, long>(),
                CountsState = counts == null ? Unavailable : exact ? Exact : Counting,
                CountsStale = counts != null && reading.Stale,
                CountsAsOf = counts?.ReadAtUtc,
                CountsReadMs = counts == null ? null : Math.Round(counts.ReadMs, 2),
                CountsNote = reading.Problem
                    ?? (counts == null || exact ? null
                        : countingStatus ?? "The rows that were in the index before it kept counts have not been counted yet."),
            };
        }
    }
}
