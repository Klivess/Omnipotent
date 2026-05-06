using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Counts shared-conversation-message overlap with every other person, producing a
    /// crude "social graph" \u2014 ranks the top relationships by interaction volume.
    /// </summary>
    public class SocialGraphModule : IPersonAnalyticModule
    {
        public string Name => "social_graph";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("relationships", new JArray())));

            // For every conversation this person posted in, count messages by other identities.
            string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
            var counts = new Dictionary<string, int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT m.author_identity_id, COUNT(*)
                    FROM messages m
                    WHERE m.conversation_id IN (SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC}))
                      AND m.author_identity_id NOT IN ({inC})
                    GROUP BY m.author_identity_id";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                using var r = cmd.ExecuteReader();
                while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            }

            // Resolve identities → persons + display names.
            var rels = new List<JObject>();
            foreach (var kv in counts.OrderByDescending(k => k.Value).Take(25))
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
            return Task.FromResult(new JObject(new JProperty("relationships", new JArray(rels))));
        }
    }
}
