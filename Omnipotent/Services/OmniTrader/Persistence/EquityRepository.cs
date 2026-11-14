using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveAPI.Telemetry;
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

        /// <summary>
        /// One deployment's equity series, oldest first. Every row is read (the v8 covering index keeps a
        /// deployment's rows together, where the table interleaves every deployment's ticks), then thinned to
        /// at most <paramref name="maxPoints"/> keeping each bucket's low and high so drawdowns survive.
        /// </summary>
        public async Task<List<EquityPoint>> GetSeriesAsync(string deploymentId, DateTime? from = null, DateTime? to = null,
            CancellationToken ct = default, int? maxPoints = null)
        {
            CacheDeps.NoteRead(CacheKey(deploymentId));
            var output = new List<EquityPoint>();
            using (RequestTrace.AmbientSpan("equity-read"))
            {
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
            }
            return maxPoints is int max ? Thin(output, max) : output;
        }

        /// <summary>
        /// Thin a series to about <paramref name="max"/> points: the first and last are always kept, and each
        /// bucket in between contributes its lowest and highest equity in time order.
        /// </summary>
        public static List<EquityPoint> Thin(List<EquityPoint> points, int max)
        {
            max = Math.Max(4, max);
            if (points.Count <= max) return points;
            int buckets = Math.Max(1, (max - 2) / 2);
            double size = (points.Count - 2) / (double)buckets;
            var result = new List<EquityPoint>(max) { points[0] };
            for (int b = 0; b < buckets; b++)
            {
                int start = 1 + (int)Math.Floor(b * size);
                int end = Math.Min(points.Count - 1, 1 + (int)Math.Floor((b + 1) * size));
                if (start >= end) continue;
                int lo = start, hi = start;
                for (int i = start; i < end; i++)
                {
                    if (points[i].Equity < points[lo].Equity) lo = i;
                    if (points[i].Equity > points[hi].Equity) hi = i;
                }
                if (lo == hi) result.Add(points[lo]);
                else if (lo < hi) { result.Add(points[lo]); result.Add(points[hi]); }
                else { result.Add(points[hi]); result.Add(points[lo]); }
            }
            result.Add(points[^1]);
            return result;
        }
    }
}
