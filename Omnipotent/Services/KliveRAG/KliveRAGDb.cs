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

        public void Migrate()
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
                };

                foreach (var (version, sql) in migrations)
                {
                    if (currentVersion >= version) continue;
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
