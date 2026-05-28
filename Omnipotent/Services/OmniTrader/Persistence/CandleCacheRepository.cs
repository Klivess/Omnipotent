using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
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
                pTs.Value = k.Timestamp.ToString("o");
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
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<OHLCCandle>();
            while (await reader.ReadAsync(ct))
            {
                output.Add(new OHLCCandle(
                    DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                    (decimal)reader.GetDouble(1),
                    (decimal)reader.GetDouble(2),
                    (decimal)reader.GetDouble(3),
                    (decimal)reader.GetDouble(4),
                    (decimal)reader.GetDouble(5)
                ));
            }
            output.Reverse();
            return output;
        }
    }
}
