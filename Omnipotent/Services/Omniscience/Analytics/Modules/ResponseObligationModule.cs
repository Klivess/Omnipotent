using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Response-obligation map: in DM conversations, who this person reliably answers
    /// vs leaves on read — a status-hierarchy proxy. For each DM partner: the share of
    /// the partner's messages that got a reply within an hour.
    /// </summary>
    public class ResponseObligationModule : IPersonAnalyticModule
    {
        public string Name => "response_obligation";
        public int Version => 1;

        private const long ReplyWindowMs = 60 * 60_000;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = new HashSet<string>(AnalyticHelpers.GetPersonIdentityIds(conn, personId));
            if (idents.Count == 0) return Task.FromResult(new JObject(new JProperty("partners", new JArray())));

            // DM conversations this person participates in.
            var dmConvs = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents.ToList());
                cmd.CommandText = $@"SELECT DISTINCT m.conversation_id FROM messages m
                    JOIN conversations c ON c.conversation_id = m.conversation_id
                    WHERE m.author_identity_id IN ({inC}) AND c.kind='dm'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) dmConvs.Add(r.GetString(0));
            }

            var stats = new Dictionary<string, (int Got, int Answered, long TotalGapMs)>();
            foreach (var convId in dmConvs)
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT author_identity_id, sent_at FROM messages WHERE conversation_id=$c ORDER BY sent_at ASC";
                cmd.Parameters.AddWithValue("$c", convId);
                using var r = cmd.ExecuteReader();
                string? pendingPartner = null;
                long pendingAt = 0;
                while (r.Read())
                {
                    string author = r.GetString(0);
                    long ts = r.GetInt64(1);
                    if (idents.Contains(author))
                    {
                        if (pendingPartner != null)
                        {
                            var cur = stats.GetValueOrDefault(pendingPartner);
                            bool answered = ts - pendingAt <= ReplyWindowMs;
                            stats[pendingPartner] = (cur.Got, cur.Answered + (answered ? 1 : 0), cur.TotalGapMs + Math.Min(ts - pendingAt, ReplyWindowMs));
                            pendingPartner = null;
                        }
                    }
                    else
                    {
                        if (pendingPartner == null)
                        {
                            var cur = stats.GetValueOrDefault(author);
                            stats[author] = (cur.Got + 1, cur.Answered, cur.TotalGapMs);
                            pendingPartner = author;
                            pendingAt = ts;
                        }
                    }
                }
            }

            var partners = new JArray();
            foreach (var kv in stats.Where(s => s.Value.Got >= 10).OrderByDescending(s => s.Value.Got).Take(20))
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
                double answerRate = (double)kv.Value.Answered / kv.Value.Got;
                partners.Add(new JObject(
                    new JProperty("person_id", otherPersonId),
                    new JProperty("display_name", display),
                    new JProperty("messages_received", kv.Value.Got),
                    new JProperty("answer_rate_within_1h", Math.Round(answerRate, 3)),
                    new JProperty("avg_response_minutes", Math.Round(kv.Value.TotalGapMs / Math.Max(1.0, kv.Value.Got) / 60_000.0, 1)),
                    new JProperty("obligation", answerRate >= 0.75 ? "always_answers" : answerRate >= 0.4 ? "usually_answers" : "often_leaves_on_read")));
            }
            return Task.FromResult(new JObject(new JProperty("partners", partners)));
        }
    }
}
