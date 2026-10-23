using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Omniscience.Workspace
{
    /// <summary>Operator-owned workspace data. These records never become person evidence.</summary>
    public sealed class WorkspaceStore
    {
        private readonly OmniscienceDb db;
        public WorkspaceStore(OmniscienceDb db) { this.db = db; }

        public JArray ListQueries()
        {
            var result = new JArray();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT query_id,name,query_json,created_at,updated_at,last_used_at,use_count
                FROM workspace_saved_queries ORDER BY COALESCE(last_used_at,0) DESC, updated_at DESC LIMIT 100";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                JObject query;
                try { query = JObject.Parse(r.GetString(2)); } catch { query = new JObject { ["q"] = r.GetString(2) }; }
                result.Add(new JObject
                {
                    ["query_id"] = r.GetString(0), ["name"] = r.GetString(1), ["query"] = query,
                    ["created_at"] = r.GetInt64(3), ["updated_at"] = r.GetInt64(4),
                    ["last_used_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5), ["use_count"] = r.GetInt32(6),
                });
            }
            return result;
        }

        public string SaveQuery(string? queryId, string name, JObject query)
        {
            name = Required(name, "Query name", 120);
            string id = string.IsNullOrWhiteSpace(queryId) ? Guid.NewGuid().ToString("N") : queryId;
            long now = Now();
            Write(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO workspace_saved_queries(query_id,name,query_json,created_at,updated_at)
                    VALUES($id,$name,$query,$now,$now)
                    ON CONFLICT(query_id) DO UPDATE SET name=excluded.name,query_json=excluded.query_json,updated_at=excluded.updated_at";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$query", query.ToString(Formatting.None));
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            });
            return id;
        }

        public void DeleteQuery(string queryId) => Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM workspace_saved_queries WHERE query_id=$id";
            cmd.Parameters.AddWithValue("$id", queryId);
            cmd.ExecuteNonQuery();
        });

        public void UseQuery(string queryId, string queryText, JObject scope)
        {
            long now = Now();
            Write(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE workspace_saved_queries SET last_used_at=$now,use_count=use_count+1 WHERE query_id=$id;
INSERT INTO workspace_query_history(query_text,scope_json,created_at) VALUES($text,$scope,$now);";
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", queryId);
                cmd.Parameters.AddWithValue("$text", Required(queryText, "Query", 500));
                cmd.Parameters.AddWithValue("$scope", scope.ToString(Formatting.None));
                cmd.ExecuteNonQuery();
            });
        }

        public void RecordHistory(string queryText, JObject scope)
        {
            long now = Now();
            Write(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO workspace_query_history(query_text,scope_json,created_at) VALUES($text,$scope,$now)";
                cmd.Parameters.AddWithValue("$text", Required(queryText, "Query", 500));
                cmd.Parameters.AddWithValue("$scope", scope.ToString(Formatting.None));
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            });
        }

        public JArray ListHistory()
        {
            var result = new JArray();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT history_id,query_text,scope_json,created_at FROM workspace_query_history ORDER BY history_id DESC LIMIT 30";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                JObject scope;
                try { scope = JObject.Parse(r.IsDBNull(2) ? "{}" : r.GetString(2)); } catch { scope = new(); }
                result.Add(new JObject
                {
                    ["history_id"] = r.GetInt64(0), ["query_text"] = r.GetString(1),
                    ["scope"] = scope, ["created_at"] = r.GetInt64(3),
                });
            }
            return result;
        }

        public void ClearHistory() => Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM workspace_query_history";
            cmd.ExecuteNonQuery();
        });

        public void SetPin(string personId, bool pinned, int sortOrder)
        {
            Write(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = pinned
                    ? @"INSERT INTO workspace_pins(person_id,pinned_at,sort_order) VALUES($p,$now,$sort)
                        ON CONFLICT(person_id) DO UPDATE SET sort_order=excluded.sort_order"
                    : "DELETE FROM workspace_pins WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", Required(personId, "personId", 100));
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$sort", Math.Clamp(sortOrder, -10000, 10000));
                cmd.ExecuteNonQuery();
            });
        }

        public JArray ListPins()
        {
            var result = new JArray();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT w.person_id,p.display_name,w.pinned_at,w.sort_order,COALESCE(c.message_count,0),c.last_message_at
                FROM workspace_pins w JOIN persons p ON p.person_id=w.person_id
                LEFT JOIN person_message_counts c ON c.person_id=w.person_id
                WHERE p.merged_into_person_id IS NULL ORDER BY w.sort_order,w.pinned_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new JObject
                {
                    ["person_id"] = r.GetString(0), ["display_name"] = r.GetString(1),
                    ["pinned_at"] = r.GetInt64(2), ["sort_order"] = r.GetInt32(3),
                    ["message_count"] = r.GetInt64(4), ["last_message_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                });
            return result;
        }

        public JArray ListNotes(string personId)
        {
            var result = new JArray();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT note_id,person_id,content,tags,created_at,updated_at FROM workspace_notes WHERE person_id=$p ORDER BY updated_at DESC LIMIT 200";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new JObject
                {
                    ["note_id"] = r.GetString(0), ["person_id"] = r.GetString(1), ["content"] = r.GetString(2),
                    ["tags"] = r.IsDBNull(3) ? "" : r.GetString(3), ["created_at"] = r.GetInt64(4), ["updated_at"] = r.GetInt64(5),
                });
            return result;
        }

        public string SaveNote(string? noteId, string personId, string content, string? tags)
        {
            string id = string.IsNullOrWhiteSpace(noteId) ? Guid.NewGuid().ToString("N") : noteId;
            long now = Now();
            Write(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO workspace_notes(note_id,person_id,content,tags,created_at,updated_at)
                    VALUES($id,$p,$content,$tags,$now,$now)
                    ON CONFLICT(note_id) DO UPDATE SET content=excluded.content,tags=excluded.tags,updated_at=excluded.updated_at";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$p", Required(personId, "personId", 100));
                cmd.Parameters.AddWithValue("$content", Required(content, "Note", 12000));
                cmd.Parameters.AddWithValue("$tags", (object?)Limit(tags, 300) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            });
            return id;
        }

        public void DeleteNote(string noteId) => Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM workspace_notes WHERE note_id=$id";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.ExecuteNonQuery();
        });

        private void Write(Action<Microsoft.Data.Sqlite.SqliteConnection> action)
        {
            db.WriteLock.Wait();
            try { using var conn = db.Open(); action(conn); }
            finally { db.WriteLock.Release(); }
        }
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static string Required(string? value, string label, int max)
        {
            string result = (value ?? "").Trim();
            if (result.Length == 0) throw new ArgumentException(label + " is required.");
            return result.Length <= max ? result : result[..max];
        }
        private static string? Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    }
}
