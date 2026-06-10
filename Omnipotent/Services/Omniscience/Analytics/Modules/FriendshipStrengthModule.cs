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
    /// Friendship strength + trend per relationship: interaction volume × DM closeness ×
    /// recency, with a growing/fading flag from comparing the last 90 days against the 90
    /// before that. Feeds the target-suggestion engine and the briefing's
    /// "friendships changing" section.
    /// </summary>
    public class FriendshipStrengthModule : IPersonAnalyticModule
    {
        public string Name => "friendship_strength";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("relationships", new JArray())));

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long cut90 = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
            long cut180 = DateTimeOffset.UtcNow.AddDays(-180).ToUnixTimeMilliseconds();

            // Other identities sharing conversations with this person, split into
            // lifetime / last 90d / previous 90d volumes, plus DM-only volume.
            var stats = new Dictionary<string, Rel>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT m.author_identity_id,
                        COUNT(*),
                        SUM(CASE WHEN m.sent_at >= $c90 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN m.sent_at >= $c180 AND m.sent_at < $c90 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN c.kind IN ('dm','group_dm') THEN 1 ELSE 0 END),
                        MAX(m.sent_at)
                    FROM messages m
                    LEFT JOIN conversations c ON m.conversation_id = c.conversation_id
                    WHERE m.conversation_id IN (SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC}))
                      AND m.author_identity_id NOT IN ({inC})
                    GROUP BY m.author_identity_id";
                cmd.Parameters.AddWithValue("$c90", cut90);
                cmd.Parameters.AddWithValue("$c180", cut180);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    stats[r.GetString(0)] = new Rel
                    {
                        IdentityId = r.GetString(0),
                        Lifetime = r.GetInt32(1),
                        Last90 = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                        Prev90 = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        DmMessages = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                        LastInteractionAt = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    };
                }
            }

            var ranked = new List<(Rel rel, double strength, string trend)>();
            foreach (var rel in stats.Values)
            {
                if (rel.Lifetime < 10) continue;
                // Strength: log-volume × DM weight × recency decay on the last interaction.
                double recency = Math.Max(0.1, Math.Pow(0.5, (now - rel.LastInteractionAt) / (90.0 * 86_400_000)));
                double dmWeight = 1 + 2.0 * rel.DmMessages / Math.Max(1, rel.Lifetime);
                double strength = Math.Log10(1 + rel.Lifetime) * dmWeight * recency;

                string trend;
                if (rel.Last90 >= 20 && rel.Prev90 < rel.Last90 / 2) trend = "growing";
                else if (rel.Prev90 >= 20 && rel.Last90 < rel.Prev90 / 2) trend = "fading";
                else trend = "stable";

                ranked.Add((rel, strength, trend));
            }

            var arr = new JArray();
            foreach (var (rel, strength, trend) in ranked.OrderByDescending(x => x.strength).Take(25))
            {
                string personIdOther = "", display = "";
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT pi.person_id, COALESCE(p.display_name, pi.platform_username, '')
                        FROM platform_identities pi LEFT JOIN persons p ON pi.person_id=p.person_id
                        WHERE pi.identity_id=$id";
                    cmd.Parameters.AddWithValue("$id", rel.IdentityId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) { personIdOther = r.GetString(0); display = r.GetString(1); }
                }
                arr.Add(new JObject(
                    new JProperty("person_id", personIdOther),
                    new JProperty("display_name", display),
                    new JProperty("strength", Math.Round(strength, 3)),
                    new JProperty("trend", trend),
                    new JProperty("lifetime_messages", rel.Lifetime),
                    new JProperty("last_90d_messages", rel.Last90),
                    new JProperty("previous_90d_messages", rel.Prev90),
                    new JProperty("dm_messages", rel.DmMessages),
                    new JProperty("last_interaction_at", DateTimeOffset.FromUnixTimeMilliseconds(rel.LastInteractionAt).UtcDateTime.ToString("o"))));
            }

            return Task.FromResult(new JObject(
                new JProperty("relationships", arr),
                new JProperty("growing_count", ranked.Count(x => x.trend == "growing")),
                new JProperty("fading_count", ranked.Count(x => x.trend == "fading"))
            ));
        }

        private class Rel
        {
            public string IdentityId = "";
            public int Lifetime;
            public int Last90;
            public int Prev90;
            public int DmMessages;
            public long LastInteractionAt;
        }
    }
}
