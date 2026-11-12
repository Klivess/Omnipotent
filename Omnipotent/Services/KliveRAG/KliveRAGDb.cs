using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;
using System;
using System.Threading;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Owns the SQLite file for KliveRAG (documents + chunks + embeddings + FTS mirror +
    /// connector cursors + web cache). Mirrors <c>OmniscienceDb</c>: private cache + WAL for
    /// concurrent readers with a single serialised writer (<see cref="WriteLock"/>), and
    /// PRAGMA user_version migrations.
    ///
    /// The FTS5 lexical leg is created inside a try/catch: if the shipped SQLite build lacks
    /// FTS5 the virtual table + triggers are skipped and <see cref="FtsAvailable"/> stays false,
    /// so <c>HybridRetriever</c> falls back to an in-memory BM25 leg over the vector candidates.
    /// </summary>
    public class KliveRAGDb
    {
        public string DbPath { get; }
        public string ConnectionString { get; }

        /// <summary>SQLite is single-writer; every write goes through this to avoid SQLITE_BUSY chains.</summary>
        public readonly SemaphoreSlim WriteLock = new(1, 1);

        /// <summary>Whether the FTS5 virtual table + sync triggers were created successfully.</summary>
        public bool FtsAvailable { get; private set; }

        public KliveRAGDb() : this(null) { }

        /// <summary><paramref name="dbPathOverride"/> lets tests run migrations against a temp file.</summary>
        public KliveRAGDb(string? dbPathOverride)
        {
            if (dbPathOverride == null)
            {
                System.IO.Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveRAGDirectory));
                DbPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveRAGDbFile);
            }
            else
            {
                DbPath = dbPathOverride;
            }
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        /// <summary>VM steps between deadline checks: a few microseconds of work, so a deadline lands
        /// within milliseconds while the managed callback stays a negligible cost.</summary>
        private const int DeadlineCheckSteps = 1000;

        /// <summary>
        /// Bounds every statement run on <paramref name="conn"/> by <paramref name="ct"/>: SQLite polls
        /// a progress handler as it steps and aborts the statement (SQLITE_INTERRUPT, surfaced as a
        /// <see cref="SqliteException"/>) once the token is cancelled. Microsoft.Data.Sqlite does not
        /// stop a statement that is already stepping, which is how one full-text query held KliveAgent's
        /// prompt build for minutes. A one-off sqlite3_interrupt would also miss a statement that starts
        /// just after the deadline; the handler cannot. Dispose before the connection goes back to the pool.
        /// </summary>
        public static IDisposable BindDeadline(SqliteConnection conn, CancellationToken ct)
        {
            var handle = conn.Handle;
            if (handle == null || !ct.CanBeCanceled) return NoDeadline.Instance;
            SQLitePCL.raw.sqlite3_progress_handler(handle, DeadlineCheckSteps,
                static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, ct);
            return new StatementDeadline(handle);
        }

        private sealed class StatementDeadline : IDisposable
        {
            private SQLitePCL.sqlite3? handle;
            public StatementDeadline(SQLitePCL.sqlite3 handle) => this.handle = handle;

            public void Dispose()
            {
                var h = Interlocked.Exchange(ref handle, null);
                if (h != null) SQLitePCL.raw.sqlite3_progress_handler(h, 0, null, null);
            }
        }

        private sealed class NoDeadline : IDisposable
        {
            public static readonly NoDeadline Instance = new();
            public void Dispose() { }
        }

        public void Migrate() => Migrate(int.MaxValue);

        /// <summary>Migrates no further than <paramref name="throughVersion"/>, so a test can stand up a
        /// store as an earlier build left it and then bring it forward.</summary>
        internal void Migrate(int throughVersion)
        {
            using var conn = Open();

            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
            }

            int currentVersion;
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "PRAGMA user_version;";
                    currentVersion = Convert.ToInt32(cmd.ExecuteScalar());
                }

                var migrations = new (int Version, string Sql)[]
                {
                    (1, SchemaV1),
                    (2, SchemaV2),
                };

                foreach (var (version, sql) in migrations)
                {
                    if (currentVersion >= version || version > throughVersion) continue;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = sql;
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = $"PRAGMA user_version = {version};";
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }

            // FTS5 mirror is created outside the migration transaction and guarded: a SQLite
            // build without FTS5 must degrade, not crash the whole service on boot.
            TrySetupFts(conn);
        }

        private void TrySetupFts(SqliteConnection conn)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = FtsSchema;
                cmd.ExecuteNonQuery();
                FtsAvailable = true;
            }
            catch
            {
                FtsAvailable = false;
            }
        }

        private const string SchemaV1 = @"
CREATE TABLE IF NOT EXISTS rag_documents (
    doc_id       TEXT PRIMARY KEY,
    source       TEXT NOT NULL,
    title        TEXT,
    uri          TEXT,
    content      TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    created_at   INTEGER NOT NULL,
    indexed_at   INTEGER NOT NULL,
    expires_at   INTEGER,
    meta_json    TEXT
);
CREATE INDEX IF NOT EXISTS idx_ragdoc_source ON rag_documents(source, indexed_at);
CREATE INDEX IF NOT EXISTS idx_ragdoc_expiry ON rag_documents(expires_at) WHERE expires_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS rag_chunks (
    chunk_id       TEXT PRIMARY KEY,
    doc_id         TEXT NOT NULL REFERENCES rag_documents(doc_id) ON DELETE CASCADE,
    seq            INTEGER NOT NULL,
    source         TEXT NOT NULL,
    created_at     INTEGER NOT NULL,
    text           TEXT NOT NULL,
    content_hash   TEXT NOT NULL,
    token_estimate INTEGER NOT NULL,
    embedded_at    INTEGER
);
CREATE INDEX IF NOT EXISTS idx_ragchunk_doc ON rag_chunks(doc_id);
CREATE INDEX IF NOT EXISTS idx_ragchunk_pending ON rag_chunks(embedded_at) WHERE embedded_at IS NULL;

CREATE TABLE IF NOT EXISTS rag_chunk_embeddings (
    chunk_id  TEXT PRIMARY KEY REFERENCES rag_chunks(chunk_id) ON DELETE CASCADE,
    embedding BLOB NOT NULL,
    model     TEXT NOT NULL DEFAULT 'all-MiniLM-L6-v2'
);

CREATE TABLE IF NOT EXISTS rag_cursors (
    connector  TEXT PRIMARY KEY,
    watermark  TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS rag_web_cache (
    url_hash   TEXT PRIMARY KEY,
    url        TEXT NOT NULL,
    fetched_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    status     INTEGER NOT NULL,
    doc_id     TEXT
);
";

        // What the index holds, kept as it changes (read by RagCounters). /kliverag/stats used to count
        // on every call: SELECT COUNT(*), COUNT(embedded_at) FROM rag_chunks has no index to answer it,
        // so it read every chunk row, text included, and the whole table is gigabytes once the Projects
        // event log is in the index.
        //
        // The triggers do the counting, so no write path can forget to: cascaded deletes (a document's
        // chunks), TTL eviction and the embed queue all pass through them. Each one adds a difference,
        // never a total, which is what lets rows that predate the counters be added in later
        // (RagCounters.CountExistingRowsAsync, the only other writer); 'counted_existing' is 1 once that
        // has happened, and an empty store has nothing to add. Every statement here is a primary-key
        // lookup, so migrating a large store is instant.
        //
        // Two things to keep true. A row in either table must not be replaced with INSERT OR REPLACE:
        // SQLite removes the old row without firing its delete trigger (recursive_triggers is off), so
        // RagIndexWriter upserts with ON CONFLICT DO UPDATE. And the bodies below create a missing
        // counter with INSERT ... WHERE NOT EXISTS rather than OR IGNORE, because a conflict policy on
        // the statement that fires a trigger replaces the trigger's own.
        private const string SchemaV2 = @"
CREATE TABLE IF NOT EXISTS rag_counters (
    name  TEXT PRIMARY KEY,
    value INTEGER NOT NULL
) WITHOUT ROWID;

INSERT OR IGNORE INTO rag_counters(name, value) VALUES ('chunks', 0), ('chunks_embedded', 0);
INSERT OR IGNORE INTO rag_counters(name, value)
    SELECT 'counted_existing', NOT (EXISTS (SELECT 1 FROM rag_documents) OR EXISTS (SELECT 1 FROM rag_chunks));

CREATE TRIGGER IF NOT EXISTS rag_documents_count_ai AFTER INSERT ON rag_documents BEGIN
    INSERT INTO rag_counters(name, value) SELECT 'docs:' || new.source, 0
        WHERE NOT EXISTS (SELECT 1 FROM rag_counters WHERE name = 'docs:' || new.source);
    UPDATE rag_counters SET value = value + 1 WHERE name = 'docs:' || new.source;
END;
CREATE TRIGGER IF NOT EXISTS rag_documents_count_ad AFTER DELETE ON rag_documents BEGIN
    INSERT INTO rag_counters(name, value) SELECT 'docs:' || old.source, 0
        WHERE NOT EXISTS (SELECT 1 FROM rag_counters WHERE name = 'docs:' || old.source);
    UPDATE rag_counters SET value = value - 1 WHERE name = 'docs:' || old.source;
END;
CREATE TRIGGER IF NOT EXISTS rag_documents_count_au AFTER UPDATE OF source ON rag_documents
WHEN old.source <> new.source BEGIN
    INSERT INTO rag_counters(name, value) SELECT 'docs:' || old.source, 0
        WHERE NOT EXISTS (SELECT 1 FROM rag_counters WHERE name = 'docs:' || old.source);
    INSERT INTO rag_counters(name, value) SELECT 'docs:' || new.source, 0
        WHERE NOT EXISTS (SELECT 1 FROM rag_counters WHERE name = 'docs:' || new.source);
    UPDATE rag_counters SET value = value - 1 WHERE name = 'docs:' || old.source;
    UPDATE rag_counters SET value = value + 1 WHERE name = 'docs:' || new.source;
END;

CREATE TRIGGER IF NOT EXISTS rag_chunks_count_ai AFTER INSERT ON rag_chunks BEGIN
    UPDATE rag_counters SET value = value + 1 WHERE name = 'chunks';
    UPDATE rag_counters SET value = value + 1 WHERE name = 'chunks_embedded' AND new.embedded_at IS NOT NULL;
END;
CREATE TRIGGER IF NOT EXISTS rag_chunks_count_ad AFTER DELETE ON rag_chunks BEGIN
    UPDATE rag_counters SET value = value - 1 WHERE name = 'chunks';
    UPDATE rag_counters SET value = value - 1 WHERE name = 'chunks_embedded' AND old.embedded_at IS NOT NULL;
END;
CREATE TRIGGER IF NOT EXISTS rag_chunks_count_au AFTER UPDATE OF embedded_at ON rag_chunks
WHEN (old.embedded_at IS NULL) <> (new.embedded_at IS NULL) BEGIN
    UPDATE rag_counters SET value = value + (CASE WHEN new.embedded_at IS NULL THEN -1 ELSE 1 END)
        WHERE name = 'chunks_embedded';
END;
";

        // External-content FTS5 mirror over rag_chunks.text, kept in sync by triggers. The
        // 'delete' command rows are the documented external-content upkeep pattern.
        private const string FtsSchema = @"
CREATE VIRTUAL TABLE IF NOT EXISTS rag_chunks_fts USING fts5(
    text,
    content='rag_chunks',
    content_rowid='rowid',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER IF NOT EXISTS rag_chunks_ai AFTER INSERT ON rag_chunks BEGIN
    INSERT INTO rag_chunks_fts(rowid, text) VALUES (new.rowid, new.text);
END;
CREATE TRIGGER IF NOT EXISTS rag_chunks_ad AFTER DELETE ON rag_chunks BEGIN
    INSERT INTO rag_chunks_fts(rag_chunks_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
END;
CREATE TRIGGER IF NOT EXISTS rag_chunks_au AFTER UPDATE OF text ON rag_chunks BEGIN
    INSERT INTO rag_chunks_fts(rag_chunks_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
    INSERT INTO rag_chunks_fts(rowid, text) VALUES (new.rowid, new.text);
END;
";
    }
}
