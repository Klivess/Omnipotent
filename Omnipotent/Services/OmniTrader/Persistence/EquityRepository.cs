using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class EquityRepository
    {
        // Per-deployment: only that deployment's equity series is invalidated on a tick.
        private static string CacheKey(string deploymentId) => "omnitrader:equity:" + deploymentId;
        private readonly OmniTraderDb db;

        public EquityRepository(OmniTraderDb db) { this.db = db; }

        public Task InsertAsync(string deploymentId, EquityPoint point, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO equity_ticks (deployment_id, ts, mark_price, quote_balance, base_balance, equity)
                                VALUES ($d,$t,$mp,$q,$b,$e)";
            cmd.Parameters.AddWithValue("$d", deploymentId);
            cmd.Parameters.AddWithValue("$t", point.Ts.ToString("o"));
            cmd.Parameters.AddWithValue("$mp", (double)point.MarkPrice);
            cmd.Parameters.AddWithValue("$q", (double)point.QuoteBalance);
            cmd.Parameters.AddWithValue("$b", (double)point.BaseBalance);
            cmd.Parameters.AddWithValue("$e", (double)point.Equity);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey(deploymentId));
        }, ct);

        public async Task<List<EquityPoint>> GetSeriesAsync(string deploymentId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey(deploymentId));
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            string sql = "SELECT ts, mark_price, quote_balance, base_balance, equity FROM equity_ticks WHERE deployment_id=$d";
            if (from.HasValue) sql += " AND ts >= $from";
            if (to.HasValue) sql += " AND ts <= $to";
            sql += " ORDER BY ts ASC";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$d", deploymentId);
            if (from.HasValue) cmd.Parameters.AddWithValue("$from", from.Value.ToString("o"));
            if (to.HasValue) cmd.Parameters.AddWithValue("$to", to.Value.ToString("o"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<EquityPoint>();
            while (await reader.ReadAsync(ct))
            {
                output.Add(new EquityPoint
                {
                    Ts = DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                    MarkPrice = (decimal)reader.GetDouble(1),
                    QuoteBalance = (decimal)reader.GetDouble(2),
                    BaseBalance = (decimal)reader.GetDouble(3),
                    Equity = (decimal)reader.GetDouble(4)
                });
            }
            return output;
        }
    }
}
