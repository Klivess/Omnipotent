using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveRAG;
using Omnipotent.Services.KliveRAG.Web;

namespace Omnipotent.Tests.KliveRAG
{
    /// <summary>
    /// What GET /kliverag/stats costs. The route counted the index on every request, and one of its
    /// two statements has no index to answer it, so it read every chunk row, text included. On the
    /// live server a request on 11 Nov 2026 had not answered after a minute, and KliveAPI's own
    /// statistics had the route's two earlier requests at 35 seconds and 26 minutes. The counts are
    /// now kept by triggers and read from one small table.
    ///
    /// Cost is counted in database pages (SQLite's page-cache misses on a fresh connection), which is
    /// what turns into disk reads on the server. A development SSD answers the old statements in a
    /// second at a million chunks; page counts show the problem at any size.
    /// </summary>
    public sealed class KliveRAGStatsCostTests : IDisposable
    {
        // The two statements KliveRAG.GetStats() ran on every request, as deployed through d2e0e83.
        private const string DeployedDocumentsSql = "SELECT source, COUNT(*) FROM rag_documents GROUP BY source";
        private const string DeployedChunksSql = "SELECT COUNT(*), COUNT(embedded_at) FROM rag_chunks";

        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "kliverag-stats-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly KliveRAGDb db;
        private readonly RagIndexWriter writer;
        private readonly RagCounters counters;
        private int filled;
        private long textBytes;

        public KliveRAGStatsCostTests()
        {
            db = new KliveRAGDb(dbPath);
            writer = new RagIndexWriter(db);
            counters = new RagCounters(db);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        /// <summary>A connection with nothing cached, so every page a statement touches is counted.</summary>
        private SqliteConnection OpenFresh()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            conn.Open();
            return conn;
        }

        private int Pages(Action<SqliteConnection> work)
        {
            using var conn = OpenFresh();
            // The schema is read once per connection whatever runs on it, so it is loaded before the count starts.
            Run(conn, "SELECT COUNT(*) FROM sqlite_master");
            SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
            work(conn);
            SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0);
            return pages;
        }

        private static void Run(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }

        private List<string> Plan(string sql)
        {
            using var conn = OpenFresh();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var plan = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
            return plan;
        }

        private long Scalar(string sql)
        {
            using var conn = OpenFresh();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Appends documents the size of a Projects event (one chunk each, about 900 characters), the
        /// way the event connector grows the index. One in twenty-five is a web page; all but the
        /// newest ten have been embedded.
        /// </summary>
        private void Fill(int count)
        {
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            using var doc = conn.CreateCommand();
            doc.Transaction = tx;
            doc.CommandText = @"INSERT INTO rag_documents(doc_id, source, title, uri, content, content_hash, created_at, indexed_at, expires_at, meta_json)
VALUES($id,$src,'AgentThought',NULL,$text,$id,$at,$at,NULL,NULL)";
            using var chunk = conn.CreateCommand();
            chunk.Transaction = tx;
            chunk.CommandText = @"INSERT INTO rag_chunks(chunk_id, doc_id, seq, source, created_at, text, content_hash, token_estimate, embedded_at)
VALUES($id || '#0',$id,0,$src,$at,$text,$id,225,$embedded)";
            foreach (var cmd in new[] { doc, chunk })
            {
                cmd.Parameters.Add("$id", SqliteType.Text);
                cmd.Parameters.Add("$src", SqliteType.Text);
                cmd.Parameters.Add("$text", SqliteType.Text);
                cmd.Parameters.Add("$at", SqliteType.Integer);
            }
            chunk.Parameters.Add("$embedded", SqliteType.Integer);

            for (int i = 0; i < count; i++, filled++)
            {
                string source = filled % 25 == 0 ? RagSource.Web : RagSource.ProjectsEvents;
                string text = string.Join(' ', Enumerable.Range(0, 150).Select(k => "w" + (filled * 31 + k * 7) % 5000));
                textBytes += text.Length;
                foreach (var cmd in new[] { doc, chunk })
                {
                    cmd.Parameters["$id"].Value = $"{source}:{filled}";
                    cmd.Parameters["$src"].Value = source;
                    cmd.Parameters["$text"].Value = text;
                    cmd.Parameters["$at"].Value = 1_794_000_000_000L + filled;
                }
                chunk.Parameters["$embedded"].Value = i >= count - 10 ? DBNull.Value : 1_794_000_000_000L + filled;
                doc.ExecuteNonQuery();
                chunk.ExecuteNonQuery();
            }
            tx.Commit();
        }

        /// <summary>The index counted the slow way, straight from the tables: the truth the counters must match.</summary>
        private (long Chunks, long Embedded, List<KeyValuePair<string, long>> PerSource) CountTheTables()
        {
            using var conn = OpenFresh();
            var perSource = new List<KeyValuePair<string, long>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = DeployedDocumentsSql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) perSource.Add(new(r.GetString(0), r.GetInt64(1)));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = DeployedChunksSql;
                using var r = cmd.ExecuteReader();
                r.Read();
                return (r.GetInt64(0), r.GetInt64(1), perSource.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
            }
        }

        private RagCounts ReadCounters()
        {
            using var conn = OpenFresh();
            return RagCounters.Read(conn);
        }

        private void AssertCountersMatchTheTables(string after)
        {
            var truth = CountTheTables();
            var counts = ReadCounters();
            Assert.True(counts.Exact, after);
            Assert.True(truth.Chunks == counts.Chunks, $"{after}: {truth.Chunks} chunks in the table, counter says {counts.Chunks}");
            Assert.True(truth.Embedded == counts.EmbeddedChunks, $"{after}: {truth.Embedded} embedded chunks in the table, counter says {counts.EmbeddedChunks}");
            Assert.Equal(truth.PerSource, counts.PerSource.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
            Assert.Equal(truth.PerSource.Sum(p => p.Value), counts.Documents);
        }

        private static RagDocument Doc(string id, string content, string source, long? expiresAt = null, bool single = true) => new()
        {
            DocId = id,
            Source = source,
            Title = id,
            Content = content,
            ContentHash = RagChunker.Hash(content),
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = expiresAt,
            SingleChunk = single,
        };

        // ── the cause ──

        [Fact]
        public void CountingTheTables_AsDeployed_ReadsEveryChunkRow_AndTwiceAsMuchWhenTheIndexDoubles()
        {
            // The slow request, reproduced. COUNT(embedded_at) needs a column no index carries, so
            // SQLite walks the chunk table itself, and each row holds the chunk's text.
            db.Migrate();
            Fill(3000);
            int chunksSmall = Pages(conn => Run(conn, DeployedChunksSql));
            int documentsSmall = Pages(conn => Run(conn, DeployedDocumentsSql));
            long textSmall = textBytes;

            Fill(3000);
            int chunksBig = Pages(conn => Run(conn, DeployedChunksSql));
            int documentsBig = Pages(conn => Run(conn, DeployedDocumentsSql));
            long pageSize = Scalar("PRAGMA page_size");

            var plan = Plan(DeployedChunksSql);
            string detail = $"chunks statement {chunksSmall} -> {chunksBig} pages, documents statement {documentsSmall} -> {documentsBig} pages; "
                + $"chunk text {textSmall} -> {textBytes} bytes in {pageSize}-byte pages. Plan: {string.Join(" / ", plan)}";
            // If the planner ever answers this from an index by itself, the cause has gone and this test should go with it.
            Assert.Equal(new[] { "SCAN rag_chunks" }, plan);
            // It reads at least the text it is not interested in...
            Assert.True(chunksSmall * pageSize > textSmall, detail);
            Assert.True(chunksBig * pageSize > textBytes, detail);
            // ...so its cost is the size of the index, and the documents statement (a whole index) grows with it.
            Assert.True(chunksBig > chunksSmall * 1.8, detail);
            Assert.True(documentsBig > documentsSmall * 1.5, detail);
        }

        // ── the fix ──

        [Fact]
        public void Stats_ReadOnePageOfCounters_HoweverLargeTheIndexHasGrown()
        {
            db.Migrate();
            Fill(3000);
            RagCounts? small = null;
            int smallPages = Pages(conn => small = RagCounters.Read(conn));
            long smallDatabase = Scalar("PRAGMA page_count");

            Fill(3000);
            RagCounts? big = null;
            int bigPages = Pages(conn => big = RagCounters.Read(conn));
            long bigDatabase = Scalar("PRAGMA page_count");
            int deployedPages = Pages(conn => { Run(conn, DeployedDocumentsSql); Run(conn, DeployedChunksSql); });

            string detail = $"counters read {smallPages} pages of {smallDatabase}, then {bigPages} of {bigDatabase}; counting the tables read {deployedPages}";
            Assert.True(bigDatabase > smallDatabase * 1.8, detail);
            // One small table that fits a page (two leaves room for a store with hundreds of sources),
            // and the same page at twice the size.
            Assert.True(bigPages <= 2, detail);
            Assert.True(bigPages <= smallPages, detail);
            Assert.True(bigPages * 100 < deployedPages, detail);

            // And the answer is the one counting gave.
            Assert.Equal((3000L, 2990L, 3000L), (small!.Chunks, small.EmbeddedChunks, small.Documents));
            Assert.Equal((6000L, 5980L, 6000L), (big!.Chunks, big.EmbeddedChunks, big.Documents));
            AssertCountersMatchTheTables("after two fills");
        }

        [Fact]
        public void Stats_TouchOnlyTheCountersTable()
        {
            db.Migrate();
            var plan = Plan(RagCounters.ReadSql);
            Assert.All(plan, step => Assert.Contains("rag_counters", step));
            Assert.DoesNotContain(plan, step => step.Contains("rag_chunks") || step.Contains("rag_documents"));
        }

        [Fact]
        public async Task Counters_MatchTheTables_AfterEveryKindOfWrite()
        {
            db.Migrate();
            AssertCountersMatchTheTables("on an empty index");

            string longText = string.Concat(Enumerable.Repeat("Sentence about foxes and hounds in the field. ", 150));
            await writer.UpsertAsync(Doc("repodoc:long", longText, RagSource.RepoDocs, single: false));
            await writer.UpsertAsync(Doc("projevt:p:1", "the commander decided to ship the build today", RagSource.ProjectsEvents));
            await writer.UpsertAsync(Doc("projevt:p:2", "the agent reported the upload had finished", RagSource.ProjectsEvents));
            await writer.UpsertAsync(Doc("web:a", "a fetched page about sqlite counters", RagSource.Web, expiresAt: 1));
            await writer.UpsertAsync(Doc("web:b", "a fetched page that is still fresh", RagSource.Web, expiresAt: long.MaxValue));
            Assert.True(Scalar("SELECT COUNT(*) FROM rag_chunks WHERE doc_id = 'repodoc:long'") > 1);
            AssertCountersMatchTheTables("after new documents");

            // The embed queue's statement, on some chunks.
            Execute("UPDATE rag_chunks SET embedded_at = 5 WHERE chunk_id IN (SELECT chunk_id FROM rag_chunks ORDER BY chunk_id LIMIT 4)");
            AssertCountersMatchTheTables("after embedding some chunks");
            Assert.True(ReadCounters().EmbeddedChunks == 4);

            // Re-embedding an embedded chunk changes nothing; a chunk going back to pending does.
            Execute("UPDATE rag_chunks SET embedded_at = 6 WHERE embedded_at = 5");
            AssertCountersMatchTheTables("after re-embedding");
            Execute("UPDATE rag_chunks SET embedded_at = NULL WHERE chunk_id = (SELECT chunk_id FROM rag_chunks WHERE embedded_at IS NOT NULL LIMIT 1)");
            AssertCountersMatchTheTables("after a chunk went back to pending");

            // Changed content: the writer deletes and re-inserts the chunks that differ, embedded or not.
            Execute("UPDATE rag_chunks SET embedded_at = 7");
            await writer.UpsertAsync(Doc("repodoc:long", longText.Replace("foxes", "badgers"), RagSource.RepoDocs, single: false));
            await writer.UpsertAsync(Doc("projevt:p:1", "the commander decided to hold the build back", RagSource.ProjectsEvents));
            AssertCountersMatchTheTables("after documents changed");

            // The same document filed under another source.
            await writer.UpsertAsync(Doc("projevt:p:2", "the agent reported the upload had finished twice", RagSource.AgentMemories));
            AssertCountersMatchTheTables("after a document moved source");
            Assert.Equal(1, ReadCounters().PerSource[RagSource.AgentMemories]);

            // A delete cascades to the document's chunks; the counters must follow the cascade.
            await writer.DeleteAsync("repodoc:long");
            AssertCountersMatchTheTables("after a delete");
            Assert.DoesNotContain(RagSource.RepoDocs, ReadCounters().PerSource.Keys);

            // Nightly eviction deletes by expiry, many documents in one statement.
            await new WebIngestPipeline(db, writer, new WebFetcher(new HttpClient()), _ => { }).EvictExpiredAsync(CancellationToken.None);
            AssertCountersMatchTheTables("after eviction");
            Assert.Equal(1, ReadCounters().PerSource[RagSource.Web]);

            await writer.DeleteAsync("projevt:p:1");
            await writer.DeleteAsync("projevt:p:2");
            await writer.DeleteAsync("web:b");
            AssertCountersMatchTheTables("after everything was deleted");
            var empty = ReadCounters();
            Assert.Equal((0L, 0L, 0L), (empty.Documents, empty.Chunks, empty.EmbeddedChunks));
            Assert.Empty(empty.PerSource);
        }

        // ── a store that predates the counters ──

        [Fact]
        public async Task RowsThatPredateTheCounters_AreAddedOnce_WhileIngestionCarriesOn()
        {
            // The live store: built by the deployed schema, then upgraded.
            db.Migrate(1);
            Fill(500);
            await writer.UpsertAsync(Doc("repodoc:only", "the one document from the repository docs", RagSource.RepoDocs));
            await writer.UpsertAsync(Doc("agentmem:only", "the one memory the agent kept from that week", RagSource.AgentMemories));
            db.Migrate();

            // Until the old rows are counted the counters hold only what has changed since, so no number is shown.
            var before = RagStats.Compose(await counters.CurrentAsync(TimeSpan.FromSeconds(30)), counters.CountingStatus, true, true, false, false);
            Assert.Equal(RagStats.Counting, before.CountsState);
            Assert.Null(before.Documents);
            Assert.Null(before.Chunks);
            Assert.Null(before.EmbeddedChunks);
            Assert.Null(before.PendingEmbeds);
            Assert.Empty(before.PerSource);
            Assert.NotNull(before.CountsNote);

            // Writes between the upgrade and the count, including a delete from a source nothing has been added to since.
            await writer.UpsertAsync(Doc("projevt:new:1", "an event that arrived after the upgrade", RagSource.ProjectsEvents));
            await writer.DeleteAsync("repodoc:only");
            await writer.DeleteAsync(RagSource.ProjectsEvents + ":7");
            Execute("UPDATE rag_chunks SET embedded_at = 9 WHERE chunk_id = 'projevt:new:1#0'");

            RagCounters.ExistingRows existing;
            using (var conn = db.Open()) existing = RagCounters.ReadExistingRows(conn);

            // Writes that land while the count is being taken: after its snapshot, before it is applied.
            await writer.UpsertAsync(Doc("projevt:new:2", "an event that arrived during the count", RagSource.ProjectsEvents));
            await writer.UpsertAsync(Doc("omnilog:new:1", "an error that was logged during the count", RagSource.OmniLogs));
            await writer.DeleteAsync("agentmem:only");
            await writer.DeleteAsync(RagSource.Web + ":25");
            Execute("UPDATE rag_chunks SET embedded_at = 9 WHERE chunk_id = 'projevt:new:2#0'");

            Assert.True(await counters.ApplyExistingRowsAsync(existing));
            AssertCountersMatchTheTables("after the existing rows were counted");
            var after = RagStats.Compose(await counters.CurrentAsync(TimeSpan.FromSeconds(30)), counters.CountingStatus, true, true, false, false);
            Assert.Equal(RagStats.Exact, after.CountsState);
            Assert.Equal(500 + 2 + 3 - 4, after.Documents);
            Assert.Equal(after.Chunks - after.EmbeddedChunks, after.PendingEmbeds);
            Assert.Null(after.CountsNote);

            // A second attempt with the same snapshot must change nothing, and neither must the public entry point.
            Assert.False(await counters.ApplyExistingRowsAsync(existing));
            Assert.Null(await counters.CountExistingRowsAsync());
            AssertCountersMatchTheTables("after counting was attempted again");

            // Ingestion goes on being counted.
            Fill(40);
            await writer.DeleteAsync(RagSource.ProjectsEvents + ":8");
            AssertCountersMatchTheTables("after more ingestion");
        }

        [Fact]
        public async Task CountExistingRows_BringsAnUpgradedStoreToExact_InOneCall()
        {
            db.Migrate(1);
            Fill(300);
            db.Migrate();
            Assert.False(ReadCounters().Exact);

            Assert.NotNull(await counters.CountExistingRowsAsync());

            AssertCountersMatchTheTables("after the one-off count");
            Assert.Null(counters.CountingStatus);
            Assert.Equal(300, ReadCounters().Chunks);
        }

        [Fact]
        public async Task CountingExistingRows_WaitsItsTurnOnAnUpgradedStore_AndNotAtAllOnACountedOne()
        {
            db.Migrate(1);
            Fill(20);
            db.Migrate();

            // Held back after a start, so it does not compete with every service reading its store cold;
            // meanwhile the stats say when it is due.
            using var stop = new CancellationTokenSource();
            var heldBack = counters.CountExistingRowsAsync(TimeSpan.FromHours(1), stop.Token);
            Assert.True(SpinWait.SpinUntil(() => counters.CountingStatus != null, TimeSpan.FromSeconds(30)));
            var shown = RagStats.Compose(await counters.CurrentAsync(TimeSpan.FromSeconds(30)), counters.CountingStatus, true, true, false, false);
            Assert.Equal(RagStats.Counting, shown.CountsState);
            Assert.Contains("is due to start at", shown.CountsNote);
            Assert.False(ReadCounters().Exact);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heldBack);

            Assert.NotNull(await counters.CountExistingRowsAsync());
            // Every later start finds the store counted and returns without waiting, whatever delay it was given.
            Assert.Null(await counters.CountExistingRowsAsync(TimeSpan.FromHours(1)));
            AssertCountersMatchTheTables("after the count");
        }

        [Fact]
        public async Task CountingExistingRows_FailsLoudly_AndLandsOnTheRetry_WhenAnIndexItReadsIsMissing()
        {
            db.Migrate(1);
            Fill(50);
            db.Migrate();
            // Without the index the count could only be a pass over the chunk table: it must refuse, not do that quietly.
            Execute("DROP INDEX idx_ragchunk_doc");

            await Assert.ThrowsAsync<SqliteException>(() => counters.CountExistingRowsAsync());
            Assert.Contains("failed and will be tried again", counters.CountingStatus);
            Assert.False(ReadCounters().Exact);

            Execute("CREATE INDEX idx_ragchunk_doc ON rag_chunks(doc_id)");
            Assert.NotNull(await counters.CountExistingRowsAsync());
            AssertCountersMatchTheTables("after the retry");
            Assert.Null(counters.CountingStatus);
        }

        [Fact]
        public void AStoreCreatedWithTheCounters_IsExactFromTheStart()
        {
            db.Migrate();
            Assert.True(ReadCounters().Exact);
            Fill(20);
            AssertCountersMatchTheTables("after the first rows of a new store");
        }

        [Fact]
        public void CountingTheExistingRows_ReadsNarrowIndexes_NeverTheChunkTable()
        {
            db.Migrate(1);
            Fill(3000);
            db.Migrate();

            foreach (var (sql, index) in new[]
            {
                (RagCounters.ExistingChunksSql, "idx_ragchunk_doc"),
                (RagCounters.ExistingPendingSql, "idx_ragchunk_pending"),
                (RagCounters.ExistingDocumentsSql, "idx_ragdoc_source"),
            })
            {
                var plan = Plan(sql);
                Assert.True(plan.All(step => step.Contains("USING COVERING INDEX " + index, StringComparison.Ordinal)), $"{sql}: {string.Join(" / ", plan)}");
            }

            int onePass = Pages(conn => RagCounters.ReadExistingRows(conn));
            int deployedRequest = Pages(conn => { Run(conn, DeployedDocumentsSql); Run(conn, DeployedChunksSql); });
            // The whole one-off pass costs a fraction of what every stats request used to.
            Assert.True(onePass * 5 < deployedRequest, $"the one-off pass read {onePass} pages; one stats request as deployed read {deployedRequest}");
        }

        [Fact]
        public void CountingTheExistingRows_CanBeStoppedPartWay()
        {
            db.Migrate(1);
            Fill(3000);
            db.Migrate();

            // The pass may take minutes on a cold disk, so the service's stop signal has to reach it.
            using var conn = db.Open();
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            using (KliveRAGDb.BindDeadline(conn, stop.Token))
            {
                Assert.Throws<SqliteException>(() => RagCounters.ReadExistingRows(conn));
            }

            // Stopped, not broken: the same connection takes the count once it is left to finish.
            Assert.Equal(3000, RagCounters.ReadExistingRows(conn).Chunks);
            Assert.False(ReadCounters().Exact);
        }

        // ── never behind ingestion ──

        [Fact]
        public async Task Stats_AnswerWhileIngestionHoldsItsLockAndAnOpenTransaction()
        {
            db.Migrate();
            Fill(50);

            // Ingestion mid-write: the writer's lock taken, a transaction open with rows not yet committed.
            await db.WriteLock.WaitAsync();
            using var ingest = db.Open();
            using var tx = ingest.BeginTransaction();
            using (var cmd = ingest.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO rag_documents(doc_id, source, title, uri, content, content_hash, created_at, indexed_at, expires_at, meta_json)
VALUES('projevt:open:1','projects-events','t',NULL,'uncommitted','h',1,1,NULL,NULL)";
                cmd.ExecuteNonQuery();
            }

            // A reading that had to wait for either would still be waiting when this deadline passed, and come back stale.
            var during = await counters.CurrentAsync(TimeSpan.FromSeconds(20));
            Assert.False(during.Stale, during.Problem);
            Assert.Equal(50, during.Counts!.Documents);

            tx.Commit();
            db.WriteLock.Release();
            var after = await counters.CurrentAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(51, after.Counts!.Documents);
        }

        [Fact]
        public async Task Stats_GiveThePreviousReading_MarkedStale_WhenTheStoreDoesNotAnswerInTime()
        {
            db.Migrate();
            Fill(30);
            using var storeIsStuck = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int reads = 0;
            var slow = new RagCounters(db, conn =>
            {
                Interlocked.Increment(ref reads);
                if (storeIsStuck.IsSet) release.Wait(TimeSpan.FromSeconds(30));
                return RagCounters.Read(conn);
            });

            var first = await slow.CurrentAsync(TimeSpan.FromSeconds(20));
            Assert.False(first.Stale);
            Assert.Equal(30, first.Counts!.Chunks);

            // The disk stops answering. Every request during that time comes back at its deadline with the
            // previous reading, and they share the one read that is stuck instead of each starting another.
            storeIsStuck.Set();
            Fill(5);
            var stale = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => slow.CurrentAsync(TimeSpan.FromMilliseconds(100))));
            Assert.All(stale, reading =>
            {
                Assert.True(reading.Stale);
                Assert.Equal(30, reading.Counts!.Chunks);
                Assert.NotNull(reading.Problem);
            });
            Assert.InRange(Volatile.Read(ref reads), 1, 2);

            var shown = RagStats.Compose(stale[0], null, true, true, true, false);
            Assert.Equal(RagStats.Exact, shown.CountsState);
            Assert.True(shown.CountsStale);
            Assert.Equal(30, shown.Chunks);
            Assert.Equal(stale[0].Problem, shown.CountsNote);

            // The store answers again: the stuck read lands, and the next request is fresh.
            storeIsStuck.Reset();
            release.Set();
            var fresh = await slow.CurrentAsync(TimeSpan.FromSeconds(20));
            Assert.False(fresh.Stale);
            Assert.Equal(35, fresh.Counts!.Chunks);
        }

        [Fact]
        public async Task Stats_SayUnavailable_WhenTheStoreHasNeverAnswered()
        {
            db.Migrate();
            var broken = new RagCounters(db, _ => throw new InvalidOperationException("disk gone"));

            var reading = await broken.CurrentAsync(TimeSpan.FromSeconds(20));
            var shown = RagStats.Compose(reading, null, true, true, false, false);

            Assert.Null(reading.Counts);
            Assert.Equal(RagStats.Unavailable, shown.CountsState);
            Assert.False(shown.CountsStale);
            Assert.Null(shown.Documents);
            Assert.Contains("disk gone", shown.CountsNote);
        }

        // ── the response ──

        [Fact]
        public async Task TheResponse_KeepsTheFieldsItAlwaysHad_AndSaysHowGoodTheCountsAre()
        {
            db.Migrate();
            Fill(30);
            var stats = RagStats.Compose(await counters.CurrentAsync(TimeSpan.FromSeconds(20)), null,
                ready: true, ftsAvailable: true, embedderWarm: false, searxngReady: false);
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(stats));

            Assert.Equal(new[]
            {
                "ready", "ftsAvailable", "embedderWarm", "searxngReady", "documents", "chunks", "embeddedChunks",
                "pendingEmbeds", "perSource", "countsState", "countsStale", "countsAsOf", "countsReadMs", "countsNote"
            }, json.Properties().Select(p => p.Name));
            Assert.True(json.Value<bool>("ready"));
            Assert.True(json.Value<bool>("ftsAvailable"));
            Assert.False(json.Value<bool>("embedderWarm"));
            Assert.Equal(30, json.Value<long>("documents"));
            Assert.Equal(30, json.Value<long>("chunks"));
            Assert.Equal(20, json.Value<long>("embeddedChunks"));
            Assert.Equal(10, json.Value<long>("pendingEmbeds"));
            Assert.Equal(28, json["perSource"]!.Value<long>(RagSource.ProjectsEvents));
            Assert.Equal(2, json["perSource"]!.Value<long>(RagSource.Web));
            Assert.Equal("exact", json.Value<string>("countsState"));
            Assert.False(json.Value<bool>("countsStale"));
            Assert.Equal(JTokenType.Null, json["countsNote"]!.Type);
        }
    }
}
