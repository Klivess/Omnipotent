using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Editing + deletion behaviour from message_edits / message_deletes: how often they
    /// edit, how drastically, and how often they delete. Heavy self-editing = image
    /// management; quick deletions = impulse-then-regret.
    /// </summary>
    public class EditingBehaviourModule : IPersonAnalyticModule
    {
        public string Name => "editing_behaviour";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return Task.FromResult(Empty());

            long totalMessages = 0;
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $"SELECT COUNT(*) FROM messages WHERE author_identity_id IN ({inC})";
                totalMessages = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            if (totalMessages == 0) return Task.FromResult(Empty());

            int edits = 0;
            double totalChangeRatio = 0;
            var samples = new List<JObject>();
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT e.old_content, e.new_content FROM message_edits e
                    JOIN messages m ON m.message_id = e.message_id
                    WHERE m.author_identity_id IN ({inC})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    edits++;
                    string oldC = r.IsDBNull(0) ? "" : r.GetString(0);
                    string newC = r.IsDBNull(1) ? "" : r.GetString(1);
                    int dist = Math.Abs(oldC.Length - newC.Length) + CharDiff(oldC, newC);
                    double ratio = Math.Min(1.0, dist / Math.Max(1.0, oldC.Length));
                    totalChangeRatio += ratio;
                    if (ratio > 0.4 && samples.Count < 5 && oldC.Length is > 5 and < 200 && newC.Length < 200)
                        samples.Add(new JObject(new JProperty("before", oldC), new JProperty("after", newC)));
                }
            }

            int deletes = 0;
            using (var cmd = conn.CreateCommand())
            {
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $"SELECT COUNT(*) FROM message_deletes WHERE author_identity_id IN ({inC})";
                deletes = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }

            return Task.FromResult(new JObject(
                new JProperty("total_messages", totalMessages),
                new JProperty("edits", edits),
                new JProperty("edit_rate", (double)edits / totalMessages),
                new JProperty("avg_edit_change_ratio", edits == 0 ? 0 : Math.Round(totalChangeRatio / edits, 3)),
                new JProperty("deletes", deletes),
                new JProperty("delete_rate", (double)deletes / totalMessages),
                new JProperty("drastic_edit_samples", new JArray(samples))
            ));
        }

        // Cheap prefix/suffix-trimmed difference (full edit distance is overkill here).
        private static int CharDiff(string a, string b)
        {
            int start = 0;
            while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
            int endA = a.Length - 1, endB = b.Length - 1;
            while (endA >= start && endB >= start && a[endA] == b[endB]) { endA--; endB--; }
            return Math.Max(endA - start + 1, endB - start + 1);
        }

        private static JObject Empty() => new(
            new JProperty("total_messages", 0),
            new JProperty("edits", 0),
            new JProperty("edit_rate", 0.0),
            new JProperty("deletes", 0),
            new JProperty("delete_rate", 0.0));
    }
}
