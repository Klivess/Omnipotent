using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class FillRepository
    {
        private const string CacheKey = "omnitrader:fills";
        private readonly OmniTraderDb db;

        public FillRepository(OmniTraderDb db) { this.db = db; }

        public Task InsertAsync(FillEvent fill, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO fills (order_id, qty, price, fee, fee_currency, filled_utc)
                                VALUES ($o,$q,$p,$f,$fc,$u)";
            cmd.Parameters.AddWithValue("$o", fill.OrderId);
            cmd.Parameters.AddWithValue("$q", (double)fill.Qty);
            cmd.Parameters.AddWithValue("$p", (double)fill.Price);
            cmd.Parameters.AddWithValue("$f", (double)fill.Fee);
            cmd.Parameters.AddWithValue("$fc", fill.FeeCurrency);
            cmd.Parameters.AddWithValue("$u", fill.FilledUtc.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<FillRecord>> ListByOrderAsync(string orderId, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM fills WHERE order_id=$o ORDER BY filled_utc ASC";
            cmd.Parameters.AddWithValue("$o", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<FillRecord>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        public async Task<List<FillRecord>> ListByDeploymentAsync(string deploymentId, int limit = 500, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT f.* FROM fills f
                                INNER JOIN orders o ON o.id = f.order_id
                                WHERE o.deployment_id=$d
                                ORDER BY f.filled_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$d", deploymentId);
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<FillRecord>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        private static FillRecord Map(SqliteDataReader r) => new FillRecord
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            OrderId = r.GetString(r.GetOrdinal("order_id")),
            Qty = (decimal)r.GetDouble(r.GetOrdinal("qty")),
            Price = (decimal)r.GetDouble(r.GetOrdinal("price")),
            Fee = (decimal)r.GetDouble(r.GetOrdinal("fee")),
            FeeCurrency = r.GetString(r.GetOrdinal("fee_currency")),
            FilledUtc = DateTime.Parse(r.GetString(r.GetOrdinal("filled_utc"))).ToUniversalTime()
        };
    }

    public sealed class FillRecord
    {
        public required long Id { get; init; }
        public required string OrderId { get; init; }
        public required decimal Qty { get; init; }
        public required decimal Price { get; init; }
        public required decimal Fee { get; init; }
        public required string FeeCurrency { get; init; }
        public required DateTime FilledUtc { get; init; }
    }
}
