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
    /// Reaction affinity: who the person reacts to, who reacts to them, and with what.
    /// Reactions are emotional currency — a cheap, honest signal of who pays attention
    /// to whom. Data comes from reaction_events (live capture + raw_json backfill).
    /// </summary>
    public class ReactionAffinityModule : IPersonAnalyticModule
    {
        public string Name => "reaction_affinity";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var userIds = AnalyticHelpers.GetPersonPlatformUserIds(conn, personId);
            if (userIds.Count == 0)
                return Task.FromResult(new JObject(new JProperty("reactions_given", 0), new JProperty("reactions_received", 0)));

            // Reactions GIVEN: this person reacting to other people's messages.
            var givenTo = new Dictionary<string, (int count, Dictionary<string, int> emoji)>();
            int totalGiven = 0;
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "u", userIds);
                cmd.CommandText = $@"SELECT m.author_identity_id, r.emoji, COUNT(*)
                    FROM reaction_events r
                    JOIN messages m ON m.platform_message_id = r.platform_message_id AND m.platform = r.platform
                    WHERE r.reactor_platform_user_id IN ({inC}) AND r.action != 'remove'
                    GROUP BY m.author_identity_id, r.emoji";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string target = r.GetString(0);
                    string emoji = r.GetString(1);
                    int n = r.GetInt32(2);
                    totalGiven += n;
                    if (!givenTo.TryGetValue(target, out var cur)) cur = (0, new Dictionary<string, int>());
                    cur.count += n;
                    cur.emoji.TryGetValue(emoji, out int ec); cur.emoji[emoji] = ec + n;
                    givenTo[target] = cur;
                }
            }

            // Reactions RECEIVED: others reacting to this person's messages.
            var receivedFrom = new Dictionary<string, (int count, Dictionary<string, int> emoji)>();
            int totalReceived = 0;
            using (var cmd = conn.CreateCommand())
            {
                var identIds = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (identIds.Count > 0)
                {
                    string inI = AnalyticHelpers.BindInClause(cmd, "i", identIds);
                    cmd.CommandText = $@"SELECT r.reactor_platform_user_id, r.emoji, SUM(r.count)
                        FROM reaction_events r
                        JOIN messages m ON m.platform_message_id = r.platform_message_id AND m.platform = r.platform
                        WHERE m.author_identity_id IN ({inI}) AND r.action != 'remove'
                        GROUP BY r.reactor_platform_user_id, r.emoji";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string reactor = r.GetString(0);
                        if (userIds.Contains(reactor)) continue; // self-reactions
                        string emoji = r.GetString(1);
                        int n = r.GetInt32(2);
                        totalReceived += n;
                        // Snapshot rows (raw_json backfill) have no reactor identity — they
                        // count toward totals but can't rank who reacted.
                        if (string.IsNullOrEmpty(reactor)) continue;
                        if (!receivedFrom.TryGetValue(reactor, out var cur)) cur = (0, new Dictionary<string, int>());
                        cur.count += n;
                        cur.emoji.TryGetValue(emoji, out int ec); cur.emoji[emoji] = ec + n;
                        receivedFrom[reactor] = cur;
                    }
                }
            }

            return Task.FromResult(new JObject(
                new JProperty("reactions_given", totalGiven),
                new JProperty("reactions_received", totalReceived),
                new JProperty("top_reacted_to", RankByIdentity(conn, givenTo, byIdentityId: true)),
                new JProperty("top_reactors", RankByIdentity(conn, receivedFrom, byIdentityId: false))
            ));
        }

        private static JArray RankByIdentity(SqliteConnection conn,
            Dictionary<string, (int count, Dictionary<string, int> emoji)> data, bool byIdentityId)
        {
            var arr = new JArray();
            foreach (var kv in data.OrderByDescending(k => k.Value.count).Take(15))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = byIdentityId
                    ? @"SELECT pi.person_id, COALESCE(p.display_name, pi.platform_username, '')
                        FROM platform_identities pi LEFT JOIN persons p ON pi.person_id=p.person_id
                        WHERE pi.identity_id=$x"
                    : @"SELECT pi.person_id, COALESCE(p.display_name, pi.platform_username, '')
                        FROM platform_identities pi LEFT JOIN persons p ON pi.person_id=p.person_id
                        WHERE pi.platform='discord' AND pi.platform_user_id=$x";
                cmd.Parameters.AddWithValue("$x", kv.Key);
                string personId = "", display = kv.Key;
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read()) { personId = r.GetString(0); display = r.GetString(1); }
                }
                arr.Add(new JObject(
                    new JProperty("person_id", personId),
                    new JProperty("display_name", display),
                    new JProperty("count", kv.Value.count),
                    new JProperty("top_emoji", new JArray(kv.Value.emoji
                        .OrderByDescending(e => e.Value).Take(5)
                        .Select(e => new JObject(new JProperty("emoji", e.Key), new JProperty("count", e.Value)))))));
            }
            return arr;
        }
    }
}
