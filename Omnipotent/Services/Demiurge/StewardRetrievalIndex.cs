using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Disk-backed FTS5 retrieval over each Steward's complete event log. JSONL remains the
    /// authoritative audit log; this SQLite database is a rebuildable search projection. Keeping
    /// the projection on disk avoids retaining one object and term dictionary per event forever.
    /// A contiguous per-Steward cursor makes out-of-order push delivery safe.
    /// </summary>
    public class StewardRetrievalIndex
    {
        private const int SnippetChars = 400;
        private readonly StewardEventLogStore eventLog;
        private readonly string connectionString;
        private readonly object gate = new();

        public record RetrievalHit(long Sequence, string EventID, string Type, DateTime Timestamp, string Snippet, double Score);

        public record SearchHit(long Sequence, string EventID, string Type, DateTime Timestamp, string Snippet, double Score, string StewardID);

        public StewardRetrievalIndex(StewardEventLogStore eventLog)
            : this(eventLog, Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "retrieval.sqlite")) { }

        internal StewardRetrievalIndex(StewardEventLogStore eventLog, string path)
        {
            this.eventLog = eventLog;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Private cache: shared mode's table-level locks made searches wait out ingest writes.
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
            }.ToString();
            try { EnsureSchema(); }
            catch (SqliteException) when (File.Exists(path))
            {
                // The index is derived from the event logs: an index that cannot be opened or migrated
                // is set aside and rebuilt, rather than stopping Demiurge from starting.
                SqliteConnection.ClearAllPools();
                string aside = path + $".unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, aside);
                foreach (string suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix);
                EnsureSchema();
            }
        }

        public void Ingest(StewardEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.StewardID) || evt.Sequence <= 0) return;
            lock (gate)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                Insert(conn, tx, evt);
                AdvanceContiguousCursor(conn, tx, evt.StewardID);
                tx.Commit();
            }
        }

        public void EnsureFresh(string stewardID)
        {
            while (true)
            {
                long cursor;
                lock (gate)
                {
                    using (var conn = Open()) cursor = GetCursor(conn, stewardID);
                }
                // Append publishes Ingest while holding the event-log lock. Never acquire that
                // lock while holding gate, or a refresh and an append can deadlock the fleet.
                var newer = eventLog.ReadSince(stewardID, cursor, max: 2000);
                if (newer.Count == 0) break;

                lock (gate)
                {
                    using var write = Open();
                    using var tx = write.BeginTransaction();
                    // A concurrent Ingest/refresh may already have inserted these rows. Inserts
                    // are idempotent and the cursor is recalculated from the committed rows.
                    foreach (var evt in newer) Insert(write, tx, evt);
                    AdvanceContiguousCursor(write, tx, stewardID);
                    tx.Commit();
                }
                if (newer.Count < 2000) break;
            }
        }

        public List<RetrievalHit> Search(string stewardID, string query, int topK = 12)
        {
            EnsureFresh(stewardID);
            var terms = Tokenize(query).Distinct(StringComparer.Ordinal).Take(24).ToList();
            if (terms.Count == 0) return new();
            string ftsQuery = string.Join(" OR ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));

            lock (gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT e.sequence, e.event_id, e.type, e.timestamp_utc, e.snippet, bm25(steward_event_fts) AS rank
FROM steward_event_fts
JOIN steward_events e ON e.id = steward_event_fts.rowid
WHERE steward_event_fts MATCH $query AND e.steward_id = $steward
ORDER BY rank
LIMIT $limit;";
                cmd.Parameters.AddWithValue("$query", ftsQuery);
                cmd.Parameters.AddWithValue("$steward", stewardID);
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(topK, 1, 100));
                using var reader = cmd.ExecuteReader();
                var hits = new List<RetrievalHit>();
                while (reader.Read())
                {
                    double rank = reader.IsDBNull(5) ? 0 : reader.GetDouble(5);
                    hits.Add(new RetrievalHit(
                        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        reader.GetString(4), -rank));
                }
                return hits;
            }
        }

        /// <summary>Bring the shared index up to date for every Steward that has an event log (per-Steward cursors are preserved).</summary>
        public void EnsureAllFresh()
        {
            foreach (var stewardID in eventLog.AllStewardIDsWithLogs())
                EnsureFresh(stewardID);
        }

        /// <summary>Cross-Steward FTS/BM25 search over the whole shared index: the same query as Search but without the per-Steward filter, each hit annotated with its Steward id. Timestamps are ISO-8601 UTC, so the optional window filters by string comparison.</summary>
        public List<SearchHit> SearchAll(string query, int topK = 20, DateTime? sinceUtc = null)
        {
            EnsureAllFresh();
            var terms = Tokenize(query).Distinct(StringComparer.Ordinal).Take(24).ToList();
            if (terms.Count == 0) return new();
            string ftsQuery = string.Join(" OR ", terms.Select(t => $"""{t.Replace("\"", "\"\"")}"""));
            lock (gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT e.sequence, e.event_id, e.type, e.timestamp_utc, e.snippet, e.steward_id, bm25(steward_event_fts) AS rank
FROM steward_event_fts
JOIN steward_events e ON e.id = steward_event_fts.rowid
WHERE steward_event_fts MATCH $query AND (e.timestamp_utc >= $since OR $since = '')
ORDER BY rank
LIMIT $limit;";
                cmd.Parameters.AddWithValue("$query", ftsQuery);
                cmd.Parameters.AddWithValue("$since", sinceUtc.HasValue ? sinceUtc.Value.ToUniversalTime().ToString("O") : "");
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(topK, 1, 100));
                using var reader = cmd.ExecuteReader();
                var hits = new List<SearchHit>();
                while (reader.Read())
                {
                    double rank = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
                    hits.Add(new SearchHit(
                        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        reader.GetString(4), -rank, reader.GetString(5)));
                }
                return hits;
            }
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        }

        private void EnsureSchema()
        {
            lock (gate)
            {
                using var conn = Open();
                bool rebuildFullText = RenameLegacySchema(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS steward_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    steward_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    event_id TEXT NOT NULL,
    type TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    body TEXT NOT NULL,
    snippet TEXT NOT NULL,
    UNIQUE(steward_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_steward_events_sequence ON steward_events(steward_id, sequence);
CREATE TABLE IF NOT EXISTS steward_retrieval_cursors (
    steward_id TEXT PRIMARY KEY,
    contiguous_sequence INTEGER NOT NULL DEFAULT 0
);
CREATE VIRTUAL TABLE IF NOT EXISTS steward_event_fts USING fts5(body, content='steward_events', content_rowid='id');
CREATE TRIGGER IF NOT EXISTS steward_events_ai AFTER INSERT ON steward_events BEGIN
    INSERT INTO steward_event_fts(rowid, body) VALUES (new.id, new.body);
END;
CREATE TRIGGER IF NOT EXISTS steward_events_ad AFTER DELETE ON steward_events BEGIN
    INSERT INTO steward_event_fts(steward_event_fts, rowid, body) VALUES ('delete', old.id, old.body);
END;";
                cmd.ExecuteNonQuery();
                if (rebuildFullText)
                {
                    using var rebuild = conn.CreateCommand();
                    rebuild.CommandText = "INSERT INTO steward_event_fts(steward_event_fts) VALUES('rebuild');";
                    rebuild.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// An index built before Projects became Demiurge holds project_events (owner column
        /// project_id), project_retrieval_cursors and a project_event_fts full-text table. Its rows are
        /// kept: the triggers, the full-text table and the old index are dropped, the tables and
        /// columns renamed, all in one transaction; the caller recreates the full-text table and
        /// rebuilds it from the kept rows. Returns true when that rebuild is needed.
        /// </summary>
        internal static bool RenameLegacySchema(SqliteConnection conn)
        {
            bool Exists(SqliteTransaction tx, string type, string name)
            {
                using var probe = conn.CreateCommand();
                probe.Transaction = tx;
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name";
                probe.Parameters.AddWithValue("$type", type);
                probe.Parameters.AddWithValue("$name", name);
                return Convert.ToInt64(probe.ExecuteScalar()) > 0;
            }
            void Run(SqliteTransaction tx, string sql)
            {
                using var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using var transaction = conn.BeginTransaction();
            if (!Exists(transaction, "table", "project_events") || Exists(transaction, "table", "steward_events"))
                return false;
            Run(transaction, "DROP TRIGGER IF EXISTS project_events_ai");
            Run(transaction, "DROP TRIGGER IF EXISTS project_events_ad");
            Run(transaction, "DROP TABLE IF EXISTS project_event_fts");
            Run(transaction, "DROP INDEX IF EXISTS ix_project_events_sequence");
            Run(transaction, "ALTER TABLE project_events RENAME TO steward_events");
            Run(transaction, "ALTER TABLE steward_events RENAME COLUMN project_id TO steward_id");
            if (Exists(transaction, "table", "project_retrieval_cursors") && !Exists(transaction, "table", "steward_retrieval_cursors"))
            {
                Run(transaction, "ALTER TABLE project_retrieval_cursors RENAME TO steward_retrieval_cursors");
                Run(transaction, "ALTER TABLE steward_retrieval_cursors RENAME COLUMN project_id TO steward_id");
            }
            transaction.Commit();
            return true;
        }

        private static void Insert(SqliteConnection conn, SqliteTransaction tx, StewardEvent evt)
        {
            string body = ComposeText(evt);
            string snippet = body.Length <= SnippetChars ? body : body[..SnippetChars] + "…";
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR IGNORE INTO steward_events(steward_id, sequence, event_id, type, timestamp_utc, body, snippet)
VALUES($steward, $sequence, $event, $type, $timestamp, $body, $snippet);";
            cmd.Parameters.AddWithValue("$steward", evt.StewardID);
            cmd.Parameters.AddWithValue("$sequence", evt.Sequence);
            cmd.Parameters.AddWithValue("$event", string.IsNullOrWhiteSpace(evt.EventID) ? $"seq-{evt.Sequence}" : evt.EventID);
            cmd.Parameters.AddWithValue("$type", evt.Type ?? "");
            cmd.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$body", body);
            cmd.Parameters.AddWithValue("$snippet", snippet);
            cmd.ExecuteNonQuery();
        }

        private static long GetCursor(SqliteConnection conn, string stewardID)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT contiguous_sequence FROM steward_retrieval_cursors WHERE steward_id=$steward;";
            cmd.Parameters.AddWithValue("$steward", stewardID);
            return cmd.ExecuteScalar() is long value ? value : 0;
        }

        private static void AdvanceContiguousCursor(SqliteConnection conn, SqliteTransaction tx, string stewardID)
        {
            long cursor;
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT contiguous_sequence FROM steward_retrieval_cursors WHERE steward_id=$steward;";
                get.Parameters.AddWithValue("$steward", stewardID);
                cursor = get.ExecuteScalar() is long value ? value : 0;
            }
            while (true)
            {
                using var exists = conn.CreateCommand();
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM steward_events WHERE steward_id=$steward AND sequence=$sequence LIMIT 1;";
                exists.Parameters.AddWithValue("$steward", stewardID);
                exists.Parameters.AddWithValue("$sequence", cursor + 1);
                if (exists.ExecuteScalar() == null) break;
                cursor++;
            }
            using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = @"
INSERT INTO steward_retrieval_cursors(steward_id, contiguous_sequence) VALUES($steward, $cursor)
ON CONFLICT(steward_id) DO UPDATE SET contiguous_sequence=excluded.contiguous_sequence;";
            upsert.Parameters.AddWithValue("$steward", stewardID);
            upsert.Parameters.AddWithValue("$cursor", cursor);
            upsert.ExecuteNonQuery();
        }

        private static string ComposeText(StewardEvent evt)
        {
            var parts = new List<string> { evt.Type, evt.Author, evt.Text };
            if (!string.IsNullOrWhiteSpace(evt.ToolName)) parts.Add(evt.ToolName);
            if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
                parts.Add(evt.PayloadJson!.Length > 600 ? evt.PayloadJson[..600] : evt.PayloadJson);
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            var current = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) current.Append(char.ToLowerInvariant(c));
                else Flush();
            }
            Flush();
            return tokens;

            void Flush()
            {
                if (current.Length > 2) tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }
}