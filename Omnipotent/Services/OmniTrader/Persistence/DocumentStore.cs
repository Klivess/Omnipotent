using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    /// <summary>Indexed projection of a document: two text keys, a number and a timestamp, all queryable.</summary>
    public readonly record struct DocumentKeys(string? K1 = null, string? K2 = null, double? N1 = null, DateTime? T1 = null);

    public sealed class DocumentQuery
    {
        public string? K1 { get; init; }
        public string? K2 { get; init; }
        public IReadOnlyCollection<string>? K2In { get; init; }
        public double? MinN1 { get; init; }
        public DateTime? T1From { get; init; }
        /// <summary>Case-insensitive substring over the stored JSON.</summary>
        public string? Text { get; init; }
        /// <summary>updated | n1 | t1 (all descending unless <see cref="Ascending"/>).</summary>
        public string OrderBy { get; init; } = "updated";
        public bool Ascending { get; init; }
        public int Limit { get; init; } = 200;
        public int Offset { get; init; }
    }

    /// <summary>
    /// JSON documents in one SQLite table, partitioned by collection. Every feature added in the
    /// terminal rewrite (screens, studio strategies and versions, research papers, alpha candidates,
    /// agent runs, studies, alerts) stores here. Reads note and writes bump the response-cache key
    /// <c>omnitrader:doc:{collection}</c>, so a cached route can never serve a stale document.
    /// </summary>
    public sealed class DocumentStore<T> where T : class
    {
        private readonly OmniTraderDb db;
        private readonly string collection;
        private readonly Func<T, string> id;
        private readonly Func<T, DocumentKeys> keys;
        private readonly string cacheKey;

        public static readonly JsonSerializerSettings Json = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
        };

        public DocumentStore(OmniTraderDb db, string collection, Func<T, string> id, Func<T, DocumentKeys>? keys = null)
        {
            this.db = db;
            this.collection = collection;
            this.id = id;
            this.keys = keys ?? (_ => default);
            cacheKey = $"omnitrader:doc:{collection}";
        }

        public string Collection => collection;

        public Task UpsertAsync(T doc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await UpsertCoreAsync(conn, null, doc, ct);
            CacheDeps.Bump(cacheKey);
        }, ct);

        public Task UpsertManyAsync(IEnumerable<T> docs, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var d in docs) await UpsertCoreAsync(conn, tx, d, ct);
            await tx.CommitAsync(ct);
            CacheDeps.Bump(cacheKey);
        }, ct);

        /// <summary>Insert unless a document with the same K2 already exists (de-duplication key). True when inserted.</summary>
        public Task<bool> InsertIfK2AbsentAsync(T doc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            var k = keys(doc);
            if (k.K2 != null)
            {
                await using var check = conn.CreateCommand();
                check.CommandText = "SELECT 1 FROM documents WHERE collection=$c AND k2=$k2 LIMIT 1";
                check.Parameters.AddWithValue("$c", collection);
                check.Parameters.AddWithValue("$k2", k.K2);
                if (await check.ExecuteScalarAsync(ct) != null) return false;
            }
            await UpsertCoreAsync(conn, null, doc, ct);
            CacheDeps.Bump(cacheKey);
            return true;
        }, ct);

        private async Task UpsertCoreAsync(SqliteConnection conn, SqliteTransaction? tx, T doc, CancellationToken ct)
        {
            var k = keys(doc);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO documents (collection, id, k1, k2, n1, t1, updated_utc, json)
                VALUES ($c,$id,$k1,$k2,$n1,$t1,$u,$j)
                ON CONFLICT(collection, id) DO UPDATE SET k1=$k1, k2=$k2, n1=$n1, t1=$t1, updated_utc=$u, json=$j";
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id(doc));
            cmd.Parameters.AddWithValue("$k1", (object?)k.K1 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k2", (object?)k.K2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n1", k.N1.HasValue && double.IsFinite(k.N1.Value) ? k.N1.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$t1", k.T1.HasValue ? Stamp(k.T1.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$u", Stamp(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$j", JsonConvert.SerializeObject(doc, Json));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<T?> GetAsync(string docId, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(cacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM documents WHERE collection=$c AND id=$id";
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", docId);
            return await cmd.ExecuteScalarAsync(ct) is string json ? Read(json) : null;
        }

        public async Task<bool> DeleteAsync(string docId, CancellationToken ct = default) => await db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE collection=$c AND id=$id";
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", docId);
            int n = await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(cacheKey);
            return n > 0;
        }, ct);

        public async Task<List<T>> QueryAsync(DocumentQuery? query = null, CancellationToken ct = default)
        {
            query ??= new DocumentQuery();
            CacheDeps.NoteRead(cacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            var where = new List<string> { "collection=$c" };
            cmd.Parameters.AddWithValue("$c", collection);
            Filter(query, where, cmd);
            string order = query.OrderBy switch
            {
                "n1" => "n1",
                "t1" => "t1",
                _ => "updated_utc"
            };
            cmd.CommandText = $"SELECT json FROM documents WHERE {string.Join(" AND ", where)} "
                + $"ORDER BY {order} {(query.Ascending ? "ASC" : "DESC")} NULLS LAST, updated_utc DESC LIMIT $lim OFFSET $off";
            cmd.Parameters.AddWithValue("$lim", Math.Clamp(query.Limit, 1, 10_000));
            cmd.Parameters.AddWithValue("$off", Math.Max(0, query.Offset));
            var list = new List<T>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (Read(reader.GetString(0)) is { } d) list.Add(d);
            return list;
        }

        public async Task<int> CountAsync(DocumentQuery? query = null, CancellationToken ct = default)
        {
            query ??= new DocumentQuery();
            CacheDeps.NoteRead(cacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            var where = new List<string> { "collection=$c" };
            cmd.Parameters.AddWithValue("$c", collection);
            Filter(query, where, cmd);
            cmd.CommandText = $"SELECT COUNT(*) FROM documents WHERE {string.Join(" AND ", where)}";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        /// <summary>Counts grouped by K2 (typically a status).</summary>
        public async Task<Dictionary<string, int>> CountByK2Async(string? k1 = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(cacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(k2,''), COUNT(*) FROM documents WHERE collection=$c" + (k1 != null ? " AND k1=$k1" : "") + " GROUP BY k2";
            cmd.Parameters.AddWithValue("$c", collection);
            if (k1 != null) cmd.Parameters.AddWithValue("$k1", k1);
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }

        private static void Filter(DocumentQuery q, List<string> where, SqliteCommand cmd)
        {
            if (q.K1 != null) { where.Add("k1=$k1"); cmd.Parameters.AddWithValue("$k1", q.K1); }
            if (q.K2 != null) { where.Add("k2=$k2"); cmd.Parameters.AddWithValue("$k2", q.K2); }
            if (q.K2In is { Count: > 0 })
            {
                var names = q.K2In.Select((v, i) => { cmd.Parameters.AddWithValue($"$k2i{i}", v); return $"$k2i{i}"; });
                where.Add($"k2 IN ({string.Join(",", names)})");
            }
            if (q.MinN1.HasValue) { where.Add("n1 >= $n1"); cmd.Parameters.AddWithValue("$n1", q.MinN1.Value); }
            if (q.T1From.HasValue) { where.Add("t1 >= $t1"); cmd.Parameters.AddWithValue("$t1", Stamp(q.T1From.Value)); }
            if (!string.IsNullOrWhiteSpace(q.Text)) { where.Add("json LIKE $q ESCAPE '\\'"); cmd.Parameters.AddWithValue("$q", "%" + Escape(q.Text!) + "%"); }
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private static T? Read(string json)
        {
            try { return JsonConvert.DeserializeObject<T>(json, Json); }
            catch { return null; }
        }

        private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }
}
