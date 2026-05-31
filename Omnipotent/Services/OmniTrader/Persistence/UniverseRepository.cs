using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    /// <summary>One coin-day of point-in-time universe data.</summary>
    public readonly record struct UniverseDailyPoint(DateTime Date, decimal Price, decimal MarketCap, decimal VolumeUsd);

    /// <summary>Coin identity + flags (denylist/shortable) and the span of data we hold.</summary>
    public sealed class CoinMeta
    {
        public required string CoinId { get; init; }
        public required string Symbol { get; init; }
        public string? Name { get; init; }
        public bool Denylisted { get; init; }
        public bool Shortable { get; init; } = true;
        public DateTime? FirstDate { get; init; }
        public DateTime? LastDate { get; init; }
    }

    /// <summary>
    /// Persists and serves the point-in-time universe (daily price / market cap / USD volume per coin,
    /// including coins that later delisted). Backs Section 3 universe construction and the Section 11
    /// survivorship audit. Mirrors the storage pattern of <see cref="CandleCacheRepository"/>.
    /// </summary>
    public sealed class UniverseRepository
    {
        private readonly OmniTraderDb db;

        public UniverseRepository(OmniTraderDb db) { this.db = db; }

        public Task UpsertCoinMetaAsync(CoinMeta meta, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO coin_meta (coin_id, symbol, name, denylisted, shortable, first_date, last_date)
                                VALUES ($id,$sym,$name,$deny,$short,$first,$last)
                                ON CONFLICT(coin_id) DO UPDATE SET
                                    symbol=excluded.symbol, name=excluded.name, denylisted=excluded.denylisted,
                                    shortable=excluded.shortable, first_date=excluded.first_date, last_date=excluded.last_date";
            cmd.Parameters.AddWithValue("$id", meta.CoinId);
            cmd.Parameters.AddWithValue("$sym", meta.Symbol.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$name", (object?)meta.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deny", meta.Denylisted ? 1 : 0);
            cmd.Parameters.AddWithValue("$short", meta.Shortable ? 1 : 0);
            cmd.Parameters.AddWithValue("$first", (object?)meta.FirstDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last", (object?)meta.LastDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task UpsertDailyAsync(string coinId, IReadOnlyList<UniverseDailyPoint> points, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            if (points.Count == 0) return;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR REPLACE INTO universe_daily (coin_id, date, price, market_cap, volume_usd)
                                VALUES ($id,$d,$p,$m,$v)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pD = cmd.Parameters.Add("$d", SqliteType.Text);
            var pP = cmd.Parameters.Add("$p", SqliteType.Real);
            var pM = cmd.Parameters.Add("$m", SqliteType.Real);
            var pV = cmd.Parameters.Add("$v", SqliteType.Real);
            foreach (var pt in points)
            {
                pId.Value = coinId;
                pD.Value = pt.Date.ToString("yyyy-MM-dd");
                pP.Value = (double)pt.Price;
                pM.Value = (double)pt.MarketCap;
                pV.Value = (double)pt.VolumeUsd;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }, ct);

        public async Task<List<CoinMeta>> ListCoinsAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT coin_id, symbol, name, denylisted, shortable, first_date, last_date FROM coin_meta";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<CoinMeta>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(new CoinMeta
                {
                    CoinId = reader.GetString(0),
                    Symbol = reader.GetString(1),
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Denylisted = reader.GetInt32(3) != 0,
                    Shortable = reader.GetInt32(4) != 0,
                    FirstDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
                    LastDate = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
                });
            }
            return list;
        }

        /// <summary>
        /// Load every coin's daily series in [from, to]. Returns coin_id → ordered points. Coins with
        /// no rows in the window are omitted. Used to assemble a portfolio backtest's inputs.
        /// </summary>
        public async Task<Dictionary<string, List<UniverseDailyPoint>>> LoadWindowAsync(DateTime from, DateTime to, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT coin_id, date, price, market_cap, volume_usd FROM universe_daily
                                WHERE date >= $from AND date <= $to
                                ORDER BY coin_id, date";
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new Dictionary<string, List<UniverseDailyPoint>>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(ct))
            {
                string id = reader.GetString(0);
                if (!output.TryGetValue(id, out var list)) { list = new List<UniverseDailyPoint>(); output[id] = list; }
                list.Add(new UniverseDailyPoint(
                    DateTime.SpecifyKind(DateTime.Parse(reader.GetString(1)), DateTimeKind.Utc),
                    (decimal)reader.GetDouble(2), (decimal)reader.GetDouble(3), (decimal)reader.GetDouble(4)));
            }
            return output;
        }

        /// <summary>Distinct coin count and total coin-day rows currently stored (used by the survivorship audit).</summary>
        public async Task<(int coins, long rows, DateTime? minDate, DateTime? maxDate)> StatsAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT coin_id), COUNT(*), MIN(date), MAX(date) FROM universe_daily";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (0, 0, null, null);
            int coins = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            long rows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            DateTime? min = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)).ToUniversalTime();
            DateTime? max = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)).ToUniversalTime();
            return (coins, rows, min, max);
        }
    }
}
