using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class OrderRepository
    {
        private readonly OmniTraderDb db;

        public OrderRepository(OmniTraderDb db) { this.db = db; }

        public Task InsertAsync(OrderIntent intent, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO orders
                (id, deployment_id, intent_id, side, type, symbol, qty, limit_price, stop_price, status, placed_utc, exchange_order_id, error)
                VALUES ($id,$d,$i,$s,$t,$sym,$q,$lp,$sp,$st,$pu,$eo,$err)";
            cmd.Parameters.AddWithValue("$id", intent.Id);
            cmd.Parameters.AddWithValue("$d", intent.DeploymentId);
            cmd.Parameters.AddWithValue("$i", intent.IntentId);
            cmd.Parameters.AddWithValue("$s", intent.Request.Side.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$t", intent.Request.Type.ToString());
            cmd.Parameters.AddWithValue("$sym", intent.Request.Symbol);
            cmd.Parameters.AddWithValue("$q", (double)intent.Request.Qty);
            cmd.Parameters.AddWithValue("$lp", (object?)(intent.Request.LimitPrice.HasValue ? (double)intent.Request.LimitPrice.Value : (double?)null) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sp", (object?)(intent.Request.StopPrice.HasValue ? (double)intent.Request.StopPrice.Value : (double?)null) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", intent.Status.ToString());
            cmd.Parameters.AddWithValue("$pu", intent.PlacedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$eo", (object?)intent.ExchangeOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)intent.Error ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task UpdateStatusAsync(string id, OrderStatus status, string? exchangeOrderId = null, string? error = null, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE orders SET status=$s, exchange_order_id=COALESCE($eo, exchange_order_id), error=COALESCE($err, error) WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", status.ToString());
            cmd.Parameters.AddWithValue("$eo", (object?)exchangeOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public async Task<List<OrderRecord>> ListByDeploymentAsync(string deploymentId, int limit = 200, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM orders WHERE deployment_id=$d ORDER BY placed_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$d", deploymentId);
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<OrderRecord>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        public async Task<List<OrderRecord>> ListOpenAsync(string deploymentId, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM orders WHERE deployment_id=$d AND status IN ('Pending','Open','PartiallyFilled')";
            cmd.Parameters.AddWithValue("$d", deploymentId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<OrderRecord>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        public async Task<bool> ExistsByIntentAsync(string deploymentId, string intentId, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM orders WHERE deployment_id=$d AND intent_id=$i LIMIT 1";
            cmd.Parameters.AddWithValue("$d", deploymentId);
            cmd.Parameters.AddWithValue("$i", intentId);
            return (await cmd.ExecuteScalarAsync(ct)) != null;
        }

        private static OrderRecord Map(SqliteDataReader r) => new OrderRecord
        {
            Id = r.GetString(r.GetOrdinal("id")),
            DeploymentId = r.GetString(r.GetOrdinal("deployment_id")),
            IntentId = r.GetString(r.GetOrdinal("intent_id")),
            Side = r.GetString(r.GetOrdinal("side")),
            Type = r.GetString(r.GetOrdinal("type")),
            Symbol = r.GetString(r.GetOrdinal("symbol")),
            Qty = (decimal)r.GetDouble(r.GetOrdinal("qty")),
            LimitPrice = r.IsDBNull(r.GetOrdinal("limit_price")) ? null : (decimal)r.GetDouble(r.GetOrdinal("limit_price")),
            StopPrice = r.IsDBNull(r.GetOrdinal("stop_price")) ? null : (decimal)r.GetDouble(r.GetOrdinal("stop_price")),
            Status = r.GetString(r.GetOrdinal("status")),
            PlacedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("placed_utc"))).ToUniversalTime(),
            ExchangeOrderId = r.IsDBNull(r.GetOrdinal("exchange_order_id")) ? null : r.GetString(r.GetOrdinal("exchange_order_id")),
            Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error"))
        };
    }

    public sealed class OrderRecord
    {
        public required string Id { get; init; }
        public required string DeploymentId { get; init; }
        public required string IntentId { get; init; }
        public required string Side { get; init; }
        public required string Type { get; init; }
        public required string Symbol { get; init; }
        public required decimal Qty { get; init; }
        public decimal? LimitPrice { get; init; }
        public decimal? StopPrice { get; init; }
        public required string Status { get; init; }
        public required DateTime PlacedUtc { get; init; }
        public string? ExchangeOrderId { get; init; }
        public string? Error { get; init; }
    }
}
