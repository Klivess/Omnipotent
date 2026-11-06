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
            using var cmd = conn.Cmd(tx, sql, args);
            object? v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return default;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static List<T> Query<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
        {
            using var cmd = conn.Cmd(tx, sql, args);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read()) list.Add(map(reader));
            return list;
        }

        public static string? GetStringOrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

        public static long? GetInt64OrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);

        public static double? GetDoubleOrNull(this SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);
    }
}
