using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Conversation-killer score: how often this person's message is the last word —
    /// nobody replies within the dead-air window. High in DMs = people disengage on
    /// them (or they naturally close conversations); compared per facet.
    /// </summary>
    public class ConversationKillerModule : IPersonAnalyticModule
    {
        public string Name => "conversation_killer";
        public int Version => 1;

        private const long DeadAirMs = 2 * 60 * 60_000;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = new HashSet<string>(AnalyticHelpers.GetPersonIdentityIds(conn, personId));
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("messages_analysed", 0)));

            var convs = new Dictionary<string, string>(); // conversation → facet
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents.ToList());
                cmd.CommandText = $@"SELECT DISTINCT m.conversation_id, c.kind, c.guild_name, c.guild_id
                    FROM messages m LEFT JOIN conversations c ON c.conversation_id = m.conversation_id
                    WHERE m.author_identity_id IN ({inC})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string kind = r.IsDBNull(1) ? "" : r.GetString(1);
                    convs[r.GetString(0)] = kind switch
                    {
                        "dm" => "dm",
                        "group_dm" => "group_dm",
                        _ => "server:" + (r.IsDBNull(2) ? (r.IsDBNull(3) ? "unknown" : r.GetString(3)) : r.GetString(2)),
                    };
                }
            }

            int total = 0, killed = 0;
            var byFacet = new Dictionary<string, (int Total, int Killed)>();
            foreach (var (convId, facet) in convs)
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT author_identity_id, sent_at FROM messages WHERE conversation_id=$c ORDER BY sent_at ASC";
                cmd.Parameters.AddWithValue("$c", convId);
                using var r = cmd.ExecuteReader();
                string? prevAuthor = null;
                long prevTs = 0;
                while (r.Read())
                {
                    string author = r.GetString(0);
                    long ts = r.GetInt64(1);
                    if (prevAuthor != null && idents.Contains(prevAuthor) && !idents.Contains(author))
                    {
                        // Someone replied to them — count whether it took dead-air long.
                        total++;
                        bool died = ts - prevTs > DeadAirMs;
                        if (died) killed++;
                        var cur = byFacet.GetValueOrDefault(facet);
                        byFacet[facet] = (cur.Total + 1, cur.Killed + (died ? 1 : 0));
                    }
                    prevAuthor = author;
                    prevTs = ts;
                }
                // A conversation that ENDS on their message is the strongest kill signal.
                if (prevAuthor != null && idents.Contains(prevAuthor))
                {
                    total++; killed++;
                    var cur = byFacet.GetValueOrDefault(facet);
                    byFacet[facet] = (cur.Total + 1, cur.Killed + 1);
                }
            }

            var facets = new JObject();
            foreach (var kv in byFacet.Where(f => f.Value.Total >= 20).OrderByDescending(f => f.Value.Total).Take(8))
                facets[kv.Key] = new JObject(
                    new JProperty("samples", kv.Value.Total),
                    new JProperty("killer_score", Math.Round((double)kv.Value.Killed / kv.Value.Total, 3)));

            return Task.FromResult(new JObject(
                new JProperty("messages_analysed", total),
                new JProperty("killer_score", total == 0 ? 0 : Math.Round((double)killed / total, 3)),
                new JProperty("facets", facets)
            ));
        }
    }
}
