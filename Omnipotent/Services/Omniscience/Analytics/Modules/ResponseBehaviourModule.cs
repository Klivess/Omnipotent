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
    /// Accumulates per-window and per-facet buckets in the same streaming pass so
    /// "replies fast in DMs, slow in servers" is visible.
    /// </summary>
    public class ResponseBehaviourModule : IPersonAnalyticModule
    {
        public string Name => "response_behaviour";
        public int Version => 2;

        private class Bucket
        {
            public long TotalGap;
            public int GapSamples;
            public int Initiations;
            public int TotalPersonMsgs;

            public JObject ToJson() => new(
                new JProperty("samples", GapSamples),
                new JProperty("avg_response_seconds", GapSamples == 0 ? 0 : (TotalGap / (double)GapSamples) / 1000.0),
                new JProperty("initiation_rate", TotalPersonMsgs == 0 ? 0 : (double)Initiations / TotalPersonMsgs),
                new JProperty("total_person_messages", TotalPersonMsgs));
        }

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = new HashSet<string>(AnalyticHelpers.GetPersonIdentityIds(conn, personId));
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("samples", 0)));

            // Load every conversation the person spoke in, with its facet context.
            var convFacets = new Dictionary<string, string>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                cmd.CommandText = $@"SELECT DISTINCT m.conversation_id, c.kind, c.guild_id, c.guild_name
                    FROM messages m LEFT JOIN conversations c ON m.conversation_id = c.conversation_id
                    WHERE m.author_identity_id IN ({inC})";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string kind = r.IsDBNull(1) ? "" : r.GetString(1);
                    string facet = kind switch
                    {
                        "dm" => "dm",
                        "group_dm" => "group_dm",
                        _ => "server:" + (r.IsDBNull(3) ? (r.IsDBNull(2) ? "unknown" : r.GetString(2)) : r.GetString(3)),
                    };
                    convFacets[r.GetString(0)] = facet;
                }
            }

            var lifetime = new Bucket();
            var windows = AnalyticSplits.Windows.ToDictionary(w => w.Name, _ => new Bucket());
            var windowCutoffs = AnalyticSplits.Windows.ToDictionary(
                w => w.Name, w => DateTimeOffset.UtcNow.AddDays(-w.Days).ToUnixTimeMilliseconds());
            var facets = new Dictionary<string, Bucket>();

            // Threshold: a message with no prior message in the same conversation within 30 min counts as initiation.
            const long initiationThreshMs = 30L * 60 * 1000;

            foreach (var (cid, facetKey) in convFacets)
            {
                ct.ThrowIfCancellationRequested();
                if (!facets.TryGetValue(facetKey, out var facetBucket))
                    facets[facetKey] = facetBucket = new Bucket();

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
                        bool repliedToOther = lastOtherTs.HasValue && lastAnyTs.HasValue && lastAnyTs == lastOtherTs;
                        long gap = repliedToOther ? ts - lastOtherTs!.Value : 0;
                        bool initiated = !lastAnyTs.HasValue || (ts - lastAnyTs.Value) > initiationThreshMs;

                        void Add(Bucket b)
                        {
                            b.TotalPersonMsgs++;
                            if (repliedToOther) { b.TotalGap += gap; b.GapSamples++; }
                            if (initiated) b.Initiations++;
                        }
                        Add(lifetime);
                        Add(facetBucket);
                        foreach (var (name, _) in AnalyticSplits.Windows)
                            if (ts >= windowCutoffs[name]) Add(windows[name]);
                    }
                    else
                    {
                        lastOtherTs = ts;
                    }
                    lastAnyTs = ts;
                }
            }

            var payload = lifetime.ToJson();
            payload["conversations_participated"] = convFacets.Count;
            var winObj = new JObject();
            foreach (var (name, _) in AnalyticSplits.Windows) winObj[name] = windows[name].ToJson();
            payload["windows"] = winObj;
            var facObj = new JObject();
            foreach (var kv in facets.Where(f => f.Value.TotalPersonMsgs >= AnalyticSplits.MinFacetMessages)
                                     .OrderByDescending(f => f.Value.TotalPersonMsgs)
                                     .Take(AnalyticSplits.MaxFacets))
                facObj[kv.Key] = kv.Value.ToJson();
            payload["facets"] = facObj;
            return Task.FromResult(payload);
        }
    }
}
