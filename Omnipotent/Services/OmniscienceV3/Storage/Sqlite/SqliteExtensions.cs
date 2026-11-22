using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>Small ADO helpers so every command is bound to the right transaction and parameterised
    /// (legacy defect F029: SQL built by string interpolation).</summary>
    public static class SqliteExtensions
    {
        public static SqliteCommand Cmd(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        public static int Exec(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
        {
            using var cmd = conn.Cmd(tx, sql, args);
            return cmd.ExecuteNonQuery();
        }

        public static T? Scalar<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
        {
            // A read made by work that has been cancelled does not start, and one that is cut short says so as a
            // cancellation (see AmbientCancellation). Writes run on the writer's own thread, outside any such work.
            // A job's read also waits here while a request is being answered (ForegroundIo).
            if (tx == null) BeforeRead();
            try
            {
                using var cmd = conn.Cmd(tx, sql, args);
                object? v = cmd.ExecuteScalar();
                if (v == null || v is DBNull) return default;
                var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (SqliteException ex) when (AmbientCancellation.IsInterrupt(ex))
            {
                throw new OperationCanceledException("The read was stopped because the work it belongs to was cancelled.", ex, AmbientCancellation.Token);
            }
        }

        private static void BeforeRead()
        {
            AmbientCancellation.ThrowIfCancelled();
            ForegroundIo.YieldIfBackground();
        }

        public static List<T> Query<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
        {
            if (tx == null) BeforeRead();
            try
            {
                using var cmd = conn.Cmd(tx, sql, args);
                using var reader = cmd.ExecuteReader();
                var list = new List<T>();
                while (reader.Read()) list.Add(map(reader));
                return list;
            }
            catch (SqliteException ex) when (AmbientCancellation.IsInterrupt(ex))
            {
                throw new OperationCanceledException("The read was stopped because the work it belongs to was cancelled.", ex, AmbientCancellation.Token);
            }
        }

        /// <summary>Reads rows one at a time and stops as soon as <paramref name="row"/> returns false, without fetching the
        /// rest (a ranked list of which only the first few that pass a test are wanted). Returns the rows read.</summary>
        public static int Scan(this SqliteConnection conn, SqliteTransaction? tx, string sql, Func<SqliteDataReader, bool> row, params (string Name, object? Value)[] args)
        {
            if (tx == null) BeforeRead();
            try
            {
                using var cmd = conn.Cmd(tx, sql, args);
                using var reader = cmd.ExecuteReader();
                int read = 0;
                while (reader.Read())
                {
                    read++;
                    if (!row(reader)) break;
                }
                return read;
            }
            catch (SqliteException ex) when (AmbientCancellation.IsInterrupt(ex))
            {
                throw new OperationCanceledException("The read was stopped because the work it belongs to was cancelled.", ex, AmbientCancellation.Token);
            }
        }

        public static string? GetStringOrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

        public static long? GetInt64OrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);

        public static double? GetDoubleOrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);

        /// <summary>Runs <paramref name="sql"/> once per chunk of <paramref name="keys"/> with the chunk bound as
        /// <c>$k0..$kN</c> in place of the <c>{keys}</c> token (keeps IN lists under SQLite's variable limit).
        /// Any other parameter the statement names must be passed in <paramref name="extra"/>, which is bound on every chunk:
        /// SQLite refuses a statement with an unbound parameter ("Must add values for the following parameters").</summary>
        public static List<T> QueryIn<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, IReadOnlyCollection<string> keys,
            Func<SqliteDataReader, T> map, int chunk = 500, params (string Name, object? Value)[] extra)
        {
            var result = new List<T>();
            foreach (var part in keys.Chunk(Math.Clamp(chunk, 1, 900)))
            {
                var names = part.Select((_, i) => "$k" + i).ToArray();
                var args = part.Select((k, i) => (names[i], (object?)k)).Concat(extra).ToArray();
                result.AddRange(conn.Query(tx, sql.Replace("{keys}", string.Join(",", names)), map, args));
            }
            return result;
        }
    }

    /// <summary>
    /// A statement prepared once and executed many times inside one transaction: the hot path of bulk imports,
    /// where re-preparing every statement for every row would dominate the cost.
    /// </summary>
    public sealed class PreparedStatement : IDisposable
    {
        private readonly SqliteCommand cmd;
        private readonly SqliteParameter[] parameters;

        public PreparedStatement(SqliteConnection conn, SqliteTransaction? tx, string sql, params string[] parameterNames)
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            parameters = new SqliteParameter[parameterNames.Length];
            for (int i = 0; i < parameterNames.Length; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = parameterNames[i];
                p.Value = DBNull.Value;
                cmd.Parameters.Add(p);
                parameters[i] = p;
            }
            cmd.Prepare();
        }

        public int Run(params object?[] values)
        {
            Bind(values);
            return cmd.ExecuteNonQuery();
        }

        public object? ScalarRaw(params object?[] values)
        {
            Bind(values);
            object? v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        /// <summary>Runs the statement as a query. Dispose the reader before running the statement again.</summary>
        public SqliteDataReader Reader(params object?[] values)
        {
            Bind(values);
            return cmd.ExecuteReader();
        }

        private void Bind(object?[] values)
        {
            if (values.Length != parameters.Length)
                throw new ArgumentException($"Expected {parameters.Length} values, got {values.Length}.", nameof(values));
            for (int i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
        }

        public void Dispose() => cmd.Dispose();
    }
}
