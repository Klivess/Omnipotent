using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Disk-backed FTS5 retrieval over each project's complete event log. JSONL remains the
    /// authoritative audit log; this SQLite database is a rebuildable search projection. Keeping
    /// the projection on disk avoids retaining one object and term dictionary per event forever.
    /// A contiguous per-project cursor makes out-of-order push delivery safe.
    /// </summary>
    public class ProjectRetrievalIndex
    {
        private const int SnippetChars = 400;
        private readonly ProjectEventLogStore eventLog;
        private readonly string connectionString;
        private readonly object gate = new();

        public record RetrievalHit(long Sequence, string EventID, string Type, DateTime Timestamp, string Snippet, double Score);

        public ProjectRetrievalIndex(ProjectEventLogStore eventLog)
        {
            this.eventLog = eventLog;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "retrieval.sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
            EnsureSchema();
        }

        public void Ingest(ProjectEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.ProjectID) || evt.Sequence <= 0) return;
            lock (gate)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                Insert(conn, tx, evt);
                AdvanceContiguousCursor(conn, tx, evt.ProjectID);
                tx.Commit();
            }
        }

        public void EnsureFresh(string projectID)
        {
            lock (gate)
            {
                while (true)
                {
                    long cursor;
                    using (var conn = Open()) cursor = GetCursor(conn, projectID);
                    var newer = eventLog.ReadSince(projectID, cursor, max: 2000);
                    if (newer.Count == 0) break;

                    using var write = Open();
                    using var tx = write.BeginTransaction();
                    foreach (var evt in newer) Insert(write, tx, evt);
                    AdvanceContiguousCursor(write, tx, projectID);
                    tx.Commit();
                    if (newer.Count < 2000) break;
                }
            }
        }

        public List<RetrievalHit> Search(string projectID, string query, int topK = 12)
        {
            EnsureFresh(projectID);
            var terms = Tokenize(query).Distinct(StringComparer.Ordinal).Take(24).ToList();
            if (terms.Count == 0) return new();
            string ftsQuery = string.Join(" OR ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));

            lock (gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT e.sequence, e.event_id, e.type, e.timestamp_utc, e.snippet, bm25(project_event_fts) AS rank
FROM project_event_fts
JOIN project_events e ON e.id = project_event_fts.rowid
WHERE project_event_fts MATCH $query AND e.project_id = $project
ORDER BY rank
LIMIT $limit;";
                cmd.Parameters.AddWithValue("$query", ftsQuery);
                cmd.Parameters.AddWithValue("$project", projectID);
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
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS project_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    event_id TEXT NOT NULL,
    type TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    body TEXT NOT NULL,
    snippet TEXT NOT NULL,
    UNIQUE(project_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_project_events_sequence ON project_events(project_id, sequence);
CREATE TABLE IF NOT EXISTS project_retrieval_cursors (
    project_id TEXT PRIMARY KEY,
    contiguous_sequence INTEGER NOT NULL DEFAULT 0
);
CREATE VIRTUAL TABLE IF NOT EXISTS project_event_fts USING fts5(body, content='project_events', content_rowid='id');
CREATE TRIGGER IF NOT EXISTS project_events_ai AFTER INSERT ON project_events BEGIN
    INSERT INTO project_event_fts(rowid, body) VALUES (new.id, new.body);
END;
CREATE TRIGGER IF NOT EXISTS project_events_ad AFTER DELETE ON project_events BEGIN
    INSERT INTO project_event_fts(project_event_fts, rowid, body) VALUES ('delete', old.id, old.body);
END;";
                cmd.ExecuteNonQuery();
            }
        }

        private static void Insert(SqliteConnection conn, SqliteTransaction tx, ProjectEvent evt)
        {
            string body = ComposeText(evt);
            string snippet = body.Length <= SnippetChars ? body : body[..SnippetChars] + "…";
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR IGNORE INTO project_events(project_id, sequence, event_id, type, timestamp_utc, body, snippet)
VALUES($project, $sequence, $event, $type, $timestamp, $body, $snippet);";
            cmd.Parameters.AddWithValue("$project", evt.ProjectID);
            cmd.Parameters.AddWithValue("$sequence", evt.Sequence);
            cmd.Parameters.AddWithValue("$event", string.IsNullOrWhiteSpace(evt.EventID) ? $"seq-{evt.Sequence}" : evt.EventID);
            cmd.Parameters.AddWithValue("$type", evt.Type ?? "");
            cmd.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$body", body);
            cmd.Parameters.AddWithValue("$snippet", snippet);
            cmd.ExecuteNonQuery();
        }

        private static long GetCursor(SqliteConnection conn, string projectID)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT contiguous_sequence FROM project_retrieval_cursors WHERE project_id=$project;";
            cmd.Parameters.AddWithValue("$project", projectID);
            return cmd.ExecuteScalar() is long value ? value : 0;
        }

        private static void AdvanceContiguousCursor(SqliteConnection conn, SqliteTransaction tx, string projectID)
        {
            long cursor;
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT contiguous_sequence FROM project_retrieval_cursors WHERE project_id=$project;";
                get.Parameters.AddWithValue("$project", projectID);
                cursor = get.ExecuteScalar() is long value ? value : 0;
            }
            while (true)
            {
                using var exists = conn.CreateCommand();
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM project_events WHERE project_id=$project AND sequence=$sequence LIMIT 1;";
                exists.Parameters.AddWithValue("$project", projectID);
                exists.Parameters.AddWithValue("$sequence", cursor + 1);
                if (exists.ExecuteScalar() == null) break;
                cursor++;
            }
            using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = @"
INSERT INTO project_retrieval_cursors(project_id, contiguous_sequence) VALUES($project, $cursor)
ON CONFLICT(project_id) DO UPDATE SET contiguous_sequence=excluded.contiguous_sequence;";
            upsert.Parameters.AddWithValue("$project", projectID);
            upsert.Parameters.AddWithValue("$cursor", cursor);
            upsert.ExecuteNonQuery();
        }

        private static string ComposeText(ProjectEvent evt)
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
