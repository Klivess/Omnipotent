using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Swear-comfort network: per conversation partner (in DMs/group DMs), how freely
    /// this person swears around them — an intimacy/comfort proxy. Someone people drop
    /// their guard around ranks high; formal relationships rank near zero.
    /// </summary>
    public class SwearComfortModule : IPersonAnalyticModule
    {
        public string Name => "swear_comfort";
        public int Version => 1;

        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("partners", new JArray())));

            // Per private conversation: this person's profanity rate + who else is in it.
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var byConv = new Dictionary<string, (int Msgs, int Profane)>();
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                if (m.ConversationKind is not ("dm" or "group_dm")) continue;
                bool profane = Word.Matches(m.Content).Any(w => ProfanityModule.Severity.ContainsKey(w.Value));
                var cur = byConv.GetValueOrDefault(m.ConversationId);
                byConv[m.ConversationId] = (cur.Msgs + 1, cur.Profane + (profane ? 1 : 0));
            }

            // Map each conversation to its other participant(s).
            var identSet = new HashSet<string>(idents);
            var partnerStats = new Dictionary<string, (int Msgs, int Profane)>();
            foreach (var (convId, stat) in byConv)
            {
                if (stat.Msgs < 20) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT DISTINCT author_identity_id FROM messages WHERE conversation_id=$c";
                cmd.Parameters.AddWithValue("$c", convId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string author = r.GetString(0);
                    if (identSet.Contains(author)) continue;
                    var cur = partnerStats.GetValueOrDefault(author);
                    partnerStats[author] = (cur.Msgs + stat.Msgs, cur.Profane + stat.Profane);
                }
            }

            var partners = new JArray();
            foreach (var kv in partnerStats.Where(p => p.Value.Msgs >= 20)
                                           .OrderByDescending(p => (double)p.Value.Profane / p.Value.Msgs).Take(20))
            {
                string display = kv.Key, otherPersonId = "";
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT pi.person_id, COALESCE(p.display_name, pi.platform_username, '')
                        FROM platform_identities pi LEFT JOIN persons p ON p.person_id=pi.person_id
                        WHERE pi.identity_id=$id";
                    cmd.Parameters.AddWithValue("$id", kv.Key);
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) { otherPersonId = r.GetString(0); display = r.GetString(1); }
                }
                partners.Add(new JObject(
                    new JProperty("person_id", otherPersonId),
                    new JProperty("display_name", display),
                    new JProperty("messages", kv.Value.Msgs),
                    new JProperty("swear_rate", Math.Round((double)kv.Value.Profane / kv.Value.Msgs, 3))));
            }
            return Task.FromResult(new JObject(new JProperty("partners", partners)));
        }
    }
}
