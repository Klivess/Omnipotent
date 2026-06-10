using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Burst pattern: do they fire several short messages in a row, or compose one
    /// block? Measured as runs of consecutive same-author messages within a short gap.
    /// Feeds replica realism (whether the replica should send multi-message bursts).
    /// </summary>
    public class MessageBurstModule : IPersonAnalyticModule
    {
        public string Name => "message_burst";
        public int Version => 1;

        private const long BurstGapMs = 60_000; // consecutive sends within 60s = same burst

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = new HashSet<string>(AnalyticHelpers.GetPersonIdentityIds(conn, personId));
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("bursts", 0)));

            var convs = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents.ToList());
                cmd.CommandText = $"SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC})";
                using var r = cmd.ExecuteReader();
                while (r.Read()) convs.Add(r.GetString(0));
            }

            int bursts = 0;
            long totalBurstMessages = 0;
            int maxBurst = 0;
            var burstLengthCounts = new Dictionary<int, int>();
            foreach (var convId in convs)
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT author_identity_id, sent_at FROM messages WHERE conversation_id=$c ORDER BY sent_at ASC";
                cmd.Parameters.AddWithValue("$c", convId);
                using var r = cmd.ExecuteReader();
                int currentRun = 0;
                long lastTs = 0;
                bool lastWasMine = false;
                void Close()
                {
                    if (currentRun <= 0) return;
                    bursts++;
                    totalBurstMessages += currentRun;
                    maxBurst = Math.Max(maxBurst, currentRun);
                    int bucket = Math.Min(currentRun, 6);
                    burstLengthCounts.TryGetValue(bucket, out int n);
                    burstLengthCounts[bucket] = n + 1;
                }
                while (r.Read())
                {
                    bool mine = idents.Contains(r.GetString(0));
                    long ts = r.GetInt64(1);
                    if (mine && lastWasMine && ts - lastTs <= BurstGapMs)
                    {
                        currentRun++;
                    }
                    else
                    {
                        if (lastWasMine) Close();
                        currentRun = mine ? 1 : 0;
                    }
                    lastWasMine = mine;
                    lastTs = ts;
                }
                if (lastWasMine) Close();
            }

            double avgBurst = bursts == 0 ? 0 : (double)totalBurstMessages / bursts;
            return Task.FromResult(new JObject(
                new JProperty("bursts", bursts),
                new JProperty("avg_burst_length", Math.Round(avgBurst, 2)),
                new JProperty("max_burst_length", maxBurst),
                new JProperty("multi_message_burst_share", bursts == 0 ? 0 :
                    Math.Round((double)burstLengthCounts.Where(kv => kv.Key >= 2).Sum(kv => kv.Value) / bursts, 3)),
                new JProperty("is_rapid_fire", avgBurst >= 1.8),
                new JProperty("burst_length_distribution", new JArray(
                    burstLengthCounts.OrderBy(kv => kv.Key).Select(kv => new JObject(
                        new JProperty("length", kv.Key == 6 ? "6+" : kv.Key.ToString()),
                        new JProperty("count", kv.Value)))))
            ));
        }
    }
}
