using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    /// <summary>
    /// Closed OHLCV bars keyed by (symbol, interval, bar-open UTC). Only closed bars are written —
    /// a forming bar would be cached mid-flight and served as final.
    /// </summary>
    public sealed class CandleCacheRepository
    {
        private readonly OmniTraderDb db;

        public CandleCacheRepository(OmniTraderDb db) { this.db = db; }

        public Task UpsertManyAsync(string symbol, TimeInterval interval, IReadOnlyList<OHLCCandle> candles, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            if (candles.Count == 0) return;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR REPLACE INTO candle_cache (symbol, interval, ts, o, h, l, c, v)
                                VALUES ($s,$i,$t,$o,$h,$l,$c,$v)";
            var pSym = cmd.Parameters.Add("$s", SqliteType.Text);
            var pInt = cmd.Parameters.Add("$i", SqliteType.Text);
            var pTs = cmd.Parameters.Add("$t", SqliteType.Text);
            var pO = cmd.Parameters.Add("$o", SqliteType.Real);
            var pH = cmd.Parameters.Add("$h", SqliteType.Real);
            var pL = cmd.Parameters.Add("$l", SqliteType.Real);
            var pC = cmd.Parameters.Add("$c", SqliteType.Real);
            var pV = cmd.Parameters.Add("$v", SqliteType.Real);

            string symU = symbol.ToUpperInvariant();
            string intS = interval.ToString();
            foreach (var k in candles)
            {
                pSym.Value = symU;
                pInt.Value = intS;
                pTs.Value = Stamp(k.Timestamp);
                pO.Value = (double)k.Open;
                pH.Value = (double)k.High;
                pL.Value = (double)k.Low;
                pC.Value = (double)k.Close;
                pV.Value = (double)k.Volume;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }, ct);

        public async Task<List<OHLCCandle>> GetLastAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, o, h, l, c, v FROM candle_cache
                                WHERE symbol=$s AND interval=$i
                                ORDER BY ts DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$s", symbol.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$i", interval.ToString());
            cmd.Parameters.AddWithValue("$n", count);
            var output = await ReadAsync(cmd, ct);
            output.Reverse();
            return output;
        }

        public async Task<List<OHLCCandle>> GetRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, o, h, l, c, v FROM candle_cache
                                WHERE symbol=$s AND interval=$i AND ts >= $f AND ts <= $t
                                ORDER BY ts ASC";
            cmd.Parameters.AddWithValue("$s", symbol.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$i", interval.ToString());
            cmd.Parameters.AddWithValue("$f", Stamp(fromUtc));
            cmd.Parameters.AddWithValue("$t", Stamp(toUtc));
            return await ReadAsync(cmd, ct);
        }

        public async Task<int> PurgeAsync(string? symbol = null, CancellationToken ct = default) => await db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = symbol == null ? "DELETE FROM candle_cache" : "DELETE FROM candle_cache WHERE symbol=$s";
            if (symbol != null) cmd.Parameters.AddWithValue("$s", symbol.ToUpperInvariant());
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public async Task<(long Rows, long Series)> StatsAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT symbol || '|' || interval) FROM candle_cache";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (0, 0);
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        private static async Task<List<OHLCCandle>> ReadAsync(SqliteCommand cmd, CancellationToken ct)
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<OHLCCandle>();
            while (await reader.ReadAsync(ct))
            {
                output.Add(new OHLCCandle(
                    DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    (decimal)reader.GetDouble(1),
                    (decimal)reader.GetDouble(2),
                    (decimal)reader.GetDouble(3),
                    (decimal)reader.GetDouble(4),
                    (decimal)reader.GetDouble(5)));
            }
            return output;
        }

        /// <summary>Fixed-width UTC stamp so lexical order is chronological order.</summary>
        private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }
}
