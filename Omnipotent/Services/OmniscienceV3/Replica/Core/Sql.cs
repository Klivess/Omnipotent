using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Parameterised IN-list queries with extra parameters (chunks stay under SQLite's variable limit).</summary>
    internal static class Sql
    {
        public static List<T> QueryIn<T>(SqliteConnection c, string sql, IReadOnlyCollection<string> keys, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] extra)
        {
            var result = new List<T>();
            foreach (var part in keys.Distinct(StringComparer.Ordinal).Chunk(500))
            {
                var names = part.Select((_, i) => "$k" + i).ToArray();
                var args = part.Select((k, i) => (names[i], (object?)k)).Concat(extra).ToArray();
                result.AddRange(c.Query(null, sql.Replace("{keys}", string.Join(",", names)), map, args));
            }
            return result;
        }
    }
}
