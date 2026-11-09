using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules
{
    /// <summary>Query helpers shared by the WS-C modules (parameterised, chunked under SQLite's variable limit).</summary>
    public static class SqlHelpers
    {
        /// <summary>Like <see cref="SqliteExtensions.QueryIn"/> but with extra named parameters bound on every chunk.</summary>
        public static List<T> QueryInWith<T>(this SqliteConnection conn, string sql, IReadOnlyCollection<string> keys, Func<SqliteDataReader, T> map,
            params (string Name, object? Value)[] args)
        {
            var result = new List<T>();
            if (keys.Count == 0) return result;
            foreach (var part in keys.Distinct(StringComparer.Ordinal).Chunk(400))
            {
                var names = part.Select((_, i) => "$k" + i).ToArray();
                var all = part.Select((k, i) => (names[i], (object?)k)).Concat(args).ToArray();
                result.AddRange(conn.Query(null, sql.Replace("{keys}", string.Join(",", names)), map, all));
            }
            return result;
        }

        /// <summary>A positional "?..." free IN list: <c>$p0,$p1…</c> names and their bindings.</summary>
        public static (string List, (string, object?)[] Args) InList(IReadOnlyList<string> values, string prefix = "$v")
        {
            var names = values.Select((_, i) => prefix + i).ToArray();
            return (string.Join(",", names), values.Select((v, i) => (names[i], (object?)v)).ToArray());
        }
    }
}
