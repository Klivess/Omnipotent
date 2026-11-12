using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>
    /// Prompt and reply text for the calls of lanes whose text capture is switched on, for debugging.
    /// Off everywhere unless Klives starts a capture window, which always ends on its own. Its own file,
    /// gzipped, pictures and audio replaced by a note of their size, a size cap per call and in total,
    /// and a short retention. API keys travel in headers and never appear in what is kept.
    /// </summary>
    public sealed partial class LlmContentStore
    {
        public const int MaxBytesPerCall = 2 * 1024 * 1024;
        private readonly string path;
        private readonly string connectionString;
        private readonly object sync = new();

        public LlmContentStore(string path)
        {
            this.path = path;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, DefaultTimeout = 30 }.ToString();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS content (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, service TEXT NOT NULL, lane TEXT NOT NULL,
    request BLOB, response BLOB, request_chars INTEGER NOT NULL, response_chars INTEGER NOT NULL, stored_bytes INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_content_ts ON content(ts);";
            cmd.ExecuteNonQuery();
        }

        public int RetentionDays { get; set; } = 3;
        public long MaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        public void Write(LlmCallRecord record)
        {
            string? request = Scrub(record.CapturedRequest);
            string? response = Scrub(record.CapturedResponse);
            if (request == null && response == null) return;
            byte[]? req = request == null ? null : Gzip(Clip(request));
            byte[]? resp = response == null ? null : Gzip(Clip(response));
            lock (sync)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO content(id, ts, service, lane, request, response, request_chars, response_chars, stored_bytes) VALUES ($id, $ts, $s, $l, $req, $resp, $rc, $pc, $b);";
                cmd.Parameters.AddWithValue("$id", record.Id);
                cmd.Parameters.AddWithValue("$ts", record.EndedMs);
                cmd.Parameters.AddWithValue("$s", record.Service);
                cmd.Parameters.AddWithValue("$l", record.Lane);
                cmd.Parameters.AddWithValue("$req", (object?)req ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$resp", (object?)resp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rc", request?.Length ?? 0);
                cmd.Parameters.AddWithValue("$pc", response?.Length ?? 0);
                cmd.Parameters.AddWithValue("$b", (req?.Length ?? 0) + (resp?.Length ?? 0));
                cmd.ExecuteNonQuery();
            }
            // The text was only needed to get here.
            record.CapturedRequest = null;
            record.CapturedResponse = null;
        }

        public (string? Request, string? Response, long Ts)? Read(long id)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT request, response, ts FROM content WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            string? req = reader.IsDBNull(0) ? null : Gunzip((byte[])reader.GetValue(0));
            string? resp = reader.IsDBNull(1) ? null : Gunzip((byte[])reader.GetValue(1));
            return (req, resp, reader.GetInt64(2));
        }

        public void Prune()
        {
            lock (sync)
            {
                using var conn = Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM content WHERE ts < $cut;";
                    cmd.Parameters.AddWithValue("$cut", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Math.Clamp(RetentionDays, 1, 14) * 86_400_000L);
                    cmd.ExecuteNonQuery();
                }
                // Over the total cap: drop the oldest until under it.
                long total;
                using (var sum = conn.CreateCommand()) { sum.CommandText = "SELECT COALESCE(SUM(stored_bytes), 0) FROM content;"; total = Convert.ToInt64(sum.ExecuteScalar()); }
                if (total <= MaxTotalBytes) return;
                using var trim = conn.CreateCommand();
                trim.CommandText = "DELETE FROM content WHERE id IN (SELECT id FROM content ORDER BY ts LIMIT 500);";
                while (total > MaxTotalBytes && trim.ExecuteNonQuery() > 0)
                {
                    using var again = conn.CreateCommand();
                    again.CommandText = "SELECT COALESCE(SUM(stored_bytes), 0) FROM content;";
                    total = Convert.ToInt64(again.ExecuteScalar());
                }
            }
        }

        public long FileBytes
        {
            get
            {
                long n = 0;
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                {
                    try { var fi = new FileInfo(path + suffix); if (fi.Exists) n += fi.Length; } catch { }
                }
                return n;
            }
        }

        /// <summary>Replaces base64 pictures, audio and files with a note of what was there.</summary>
        internal static string? Scrub(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = DataUri().Replace(text, m => $"[{m.Groups[1].Value} removed, {m.Length * 3 / 4 / 1024:N0} KB]");
            text = Base64Field().Replace(text, m => $"\"{m.Groups[1].Value}\":\"[base64 removed, {m.Groups[2].Length * 3 / 4 / 1024:N0} KB]\"");
            return text;
        }

        private static string Clip(string text) => text.Length <= MaxBytesPerCall ? text : text[..MaxBytesPerCall] + "\n[clipped]";

        private static byte[] Gzip(string text)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                gz.Write(bytes, 0, bytes.Length);
            }
            return ms.ToArray();
        }

        private static string Gunzip(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        [GeneratedRegex(@"data:([a-zA-Z]+/[a-zA-Z0-9.+-]+);base64,[A-Za-z0-9+/=]{64,}", RegexOptions.CultureInvariant)]
        private static partial Regex DataUri();

        [GeneratedRegex(@"""(data|file_data)""\s*:\s*""([A-Za-z0-9+/=]{200,})""", RegexOptions.CultureInvariant)]
        private static partial Regex Base64Field();
    }
}
