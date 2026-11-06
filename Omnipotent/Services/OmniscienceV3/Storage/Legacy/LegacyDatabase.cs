using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>
    /// Read-only access to an Omniscience v2 database. The file is only ever opened read-only, so v2 keeps running
    /// beside v3 and its bytes are never changed (plan F-Storage accept). Columns are probed per table, because
    /// v2 databases in the field sit at different schema versions (v10–v12) with columns added over time.
    /// </summary>
    public sealed class LegacyDatabase : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly Dictionary<string, HashSet<string>> columns = new(StringComparer.OrdinalIgnoreCase);

        private LegacyDatabase(string path, SqliteConnection connection, int userVersion)
        {
            Path = path;
            this.connection = connection;
            UserVersion = userVersion;
        }

        public string Path { get; }

        /// <summary>v2's <c>PRAGMA user_version</c> (its migration number).</summary>
        public int UserVersion { get; }

        public static LegacyDatabase OpenReadOnly(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The Omniscience v2 database was not found.", path);
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 30,
            }.ToString());
            conn.Open();
            try
            {
                conn.Exec(null, "PRAGMA query_only=ON; PRAGMA busy_timeout=30000; PRAGMA temp_store=MEMORY;");
                int version = Convert.ToInt32(conn.Scalar<long>(null, "PRAGMA user_version"));
                return new LegacyDatabase(path, conn, version);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        public SqliteConnection Connection => connection;

        public bool HasTable(string table) => Columns(table).Count > 0;

        public bool HasColumn(string table, string column) => Columns(table).Contains(column);

        public IReadOnlySet<string> Columns(string table)
        {
            if (columns.TryGetValue(table, out var known)) return known;
            var set = new HashSet<string>(connection.Query(null, $"SELECT name FROM pragma_table_info($t)", r => r.GetString(0), ("$t", table)),
                StringComparer.OrdinalIgnoreCase);
            columns[table] = set;
            return set;
        }

        /// <summary>The column expression when present, else <c>NULL AS column</c>: lets one query read every schema version.</summary>
        public string ColumnOrNull(string table, string column) => HasColumn(table, column) ? column : "NULL AS " + column;

        public long MaxRowid(string table) => HasTable(table) ? connection.Scalar<long?>(null, $"SELECT MAX(rowid) FROM {table}") ?? 0 : 0;

        public long Count(string table) => HasTable(table) ? connection.Scalar<long>(null, $"SELECT COUNT(*) FROM {table}") : 0;

        /// <summary>
        /// Copies the live v2 database into <paramref name="directory"/> with SQLite's online backup API, which
        /// includes committed WAL content and never blocks v2's writer for long. Checks free space first (twice the
        /// database plus its WAL, plan F-Storage). Returns the snapshot's path.
        /// </summary>
        public static async Task<string> SnapshotAsync(string livePath, string directory, IClock clock, CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            long need = 2 * (FileLength(livePath) + FileLength(livePath + "-wal"));
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory))!);
            if (drive.AvailableFreeSpace < need)
                throw new IOException($"Not enough free disk space to snapshot the v2 database: need {need / (1024 * 1024)} MB, have {drive.AvailableFreeSpace / (1024 * 1024)} MB.");

            string target = System.IO.Path.Combine(directory, $"omniscience-v2-{Instant.ToUtcDateTime(clock.NowMs):yyyyMMdd'T'HHmmss'Z'}.db");
            string partial = target + ".partial";
            if (File.Exists(partial)) File.Delete(partial);
            await Task.Run(() =>
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = livePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 60,
                }.ToString());
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = partial, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
                }.ToString());
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                destination.Exec(null, "PRAGMA journal_mode=DELETE;");
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            SqliteConnection.ClearAllPools();
            File.Move(partial, target, overwrite: true);
            return target;
        }

        private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        public void Dispose()
        {
            connection.Dispose();
        }
    }

    /// <summary>A bounded batch read from one v2 table in rowid order.</summary>
    public sealed record LegacyBatch<T>(IReadOnlyList<(long Rowid, T Row)> Rows)
    {
        public long LastRowid => Rows.Count == 0 ? 0 : Rows[^1].Rowid;
    }

    internal static class LegacyReads
    {
        public static LegacyBatch<T> Batch<T>(this LegacyDatabase db, string table, string columnsSql, long afterRowid, int limit, Func<SqliteDataReader, T> map)
        {
            var rows = db.Connection.Query(null, $"SELECT rowid, {columnsSql} FROM {table} WHERE rowid > $after ORDER BY rowid LIMIT $limit",
                r => (r.GetInt64(0), map(r)), ("$after", afterRowid), ("$limit", limit));
            return new LegacyBatch<T>(rows);
        }

        /// <summary>A raw column value as stored (v2 columns are dynamically typed).</summary>
        public static object? Raw(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetValue(ordinal);

        public static string? Text(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Convert.ToString(r.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        public static long? Number(this SqliteDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal)) return null;
            object v = r.GetValue(ordinal);
            return v switch
            {
                long l => l,
                int i => i,
                double d when double.IsFinite(d) => (long)Math.Round(d),
                string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long p) => p,
                _ => null,
            };
        }
    }
}
