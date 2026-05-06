using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// For each conversation the person speaks in, computes time gap between the
    /// previous (other-author) message and this person's reply, plus initiation rate.
    /// </summary>
    public class ResponseBehaviourModule : IPersonAnalyticModule
    {
        public string Name => "response_behaviour";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = new HashSet<string>(AnalyticHelpers.GetPersonIdentityIds(conn, personId));
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("samples", 0)));

            // Load every conversation the person spoke in.
            var convIds = new HashSet<string>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                cmd.CommandText = $"SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC})";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                using var r = cmd.ExecuteReader();
                while (r.Read()) convIds.Add(r.GetString(0));
            }

            long totalGap = 0;
            int gapSamples = 0;
            int initiations = 0;
            int totalPersonMsgs = 0;
            // Threshold: a message with no prior message in the same conversation within 30 min counts as initiation.
            const long initiationThreshMs = 30L * 60 * 1000;

            foreach (var cid in convIds)
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT author_identity_id, sent_at FROM messages WHERE conversation_id=$c ORDER BY sent_at ASC";
                cmd.Parameters.AddWithValue("$c", cid);
                using var r = cmd.ExecuteReader();
                long? lastOtherTs = null;
                long? lastAnyTs = null;
                while (r.Read())
                {
                    string author = r.GetString(0);
                    long ts = r.GetInt64(1);
                    bool mine = idents.Contains(author);
                    if (mine)
                    {
                        totalPersonMsgs++;
                        if (lastOtherTs.HasValue && lastAnyTs.HasValue && lastAnyTs == lastOtherTs)
                        {
                            totalGap += ts - lastOtherTs.Value;
                            gapSamples++;
                        }
                        if (!lastAnyTs.HasValue || (ts - lastAnyTs.Value) > initiationThreshMs)
                            initiations++;
                    }
                    else
                    {
                        lastOtherTs = ts;
                    }
                    lastAnyTs = ts;
                }
            }

            double avgSec = gapSamples == 0 ? 0 : (totalGap / (double)gapSamples) / 1000.0;
            return Task.FromResult(new JObject(
                new JProperty("samples", gapSamples),
                new JProperty("avg_response_seconds", avgSec),
                new JProperty("initiation_rate", totalPersonMsgs == 0 ? 0 : (double)initiations / totalPersonMsgs),
                new JProperty("total_person_messages", totalPersonMsgs),
                new JProperty("conversations_participated", convIds.Count)
            ));
        }
    }
}
