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
    /// Counts shared-conversation-message overlap with every other person, producing a
    /// crude "social graph" — ranks the top relationships by interaction volume.
    /// Also emits a recent-90-day ranking so growing/fading friendships are visible.
    /// </summary>
    public class SocialGraphModule : IPersonAnalyticModule
    {
        public string Name => "social_graph";
        public int Version => 2;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return Task.FromResult(new JObject(
                new JProperty("relationships", new JArray()),
                new JProperty("relationships_recent_90d", new JArray())));

            long cutoff90 = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();

            // For every conversation this person posted in, count messages by other identities
            // (lifetime + last 90 days in one scan).
            string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
            var counts = new Dictionary<string, int>();
            var countsRecent = new Dictionary<string, int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT m.author_identity_id, COUNT(*),
                        SUM(CASE WHEN m.sent_at >= $cutoff THEN 1 ELSE 0 END)
                    FROM messages m
                    WHERE m.conversation_id IN (SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC}))
                      AND m.author_identity_id NOT IN ({inC})
                    GROUP BY m.author_identity_id";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                cmd.Parameters.AddWithValue("$cutoff", cutoff90);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    counts[r.GetString(0)] = r.GetInt32(1);
                    int recent = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    if (recent > 0) countsRecent[r.GetString(0)] = recent;
                }
            }

            var rels = ResolveRanked(conn, counts, 25);
            var relsRecent = ResolveRanked(conn, countsRecent, 15);
            return Task.FromResult(new JObject(
                new JProperty("relationships", new JArray(rels)),
                new JProperty("relationships_recent_90d", new JArray(relsRecent))));
        }

        // Resolve identities → persons + display names, ranked by interaction volume.
        private static List<JObject> ResolveRanked(SqliteConnection conn, Dictionary<string, int> counts, int take)
        {
            var rels = new List<JObject>();
            foreach (var kv in counts.OrderByDescending(k => k.Value).Take(take))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT pi.person_id, p.display_name, pi.platform_username, pi.platform
                    FROM platform_identities pi LEFT JOIN persons p ON pi.person_id = p.person_id
                    WHERE pi.identity_id=$id";
                cmd.Parameters.AddWithValue("$id", kv.Key);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) continue;
                rels.Add(new JObject(
                    new JProperty("person_id", r.GetString(0)),
                    new JProperty("display_name", r.IsDBNull(1) ? "" : r.GetString(1)),
                    new JProperty("platform_username", r.IsDBNull(2) ? "" : r.GetString(2)),
                    new JProperty("platform", r.IsDBNull(3) ? "" : r.GetString(3)),
                    new JProperty("interaction_messages", kv.Value)
                ));
            }
            return rels;
        }
    }
}
