using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Infers who Klives would probably want Tracked: scores every non-tracked person on
    /// DM volume with Klives, shared-conversation interaction, and how often existing
    /// Tracked people mention them. Produces a suggestion queue with human-readable
    /// reasons. Never auto-promotes — promotion is a one-click human decision in the UI.
    /// </summary>
    public class TargetSuggestionEngine
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        public TargetSuggestionEngine(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<string> RunAsync(CancellationToken ct)
        {
            string? klivesPersonId = null;
            var dmCounts = new Dictionary<string, int>();
            var sharedCounts = new Dictionary<string, int>();
            var mentionCounts = new Dictionary<string, int>();
            var displayNames = new Dictionary<string, string>();

            using (var conn = db.Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT person_id FROM platform_identities WHERE platform='discord' AND platform_user_id=$u";
                    cmd.Parameters.AddWithValue("$u", OmniPaths.KlivesDiscordAccountID.ToString());
                    klivesPersonId = cmd.ExecuteScalar() as string;
                }
                if (klivesPersonId == null) return "Klives person not found in the identity table";

                // Candidates: non-tracked, non-merged persons with their display names.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT person_id, display_name FROM persons
                        WHERE merged_into_person_id IS NULL AND tier != 'tracked' AND person_id != $k";
                    cmd.Parameters.AddWithValue("$k", klivesPersonId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) displayNames[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }

                // Signal 1: messages exchanged in DM conversations shared with Klives.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT pi.person_id, COUNT(*)
                        FROM messages m
                        JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                        JOIN conversations c ON c.conversation_id = m.conversation_id
                        WHERE c.kind = 'dm'
                          AND m.conversation_id IN (
                              SELECT DISTINCT m2.conversation_id FROM messages m2
                              JOIN platform_identities pk ON pk.identity_id = m2.author_identity_id
                              WHERE pk.person_id = $k)
                          AND pi.person_id != $k
                        GROUP BY pi.person_id";
                    cmd.Parameters.AddWithValue("$k", klivesPersonId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) dmCounts[r.GetString(0)] = r.GetInt32(1);
                }

                // Signal 2: total messages in ANY conversation Klives speaks in.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT pi.person_id, COUNT(*)
                        FROM messages m
                        JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                        WHERE m.conversation_id IN (
                              SELECT DISTINCT m2.conversation_id FROM messages m2
                              JOIN platform_identities pk ON pk.identity_id = m2.author_identity_id
                              WHERE pk.person_id = $k)
                          AND pi.person_id != $k
                        GROUP BY pi.person_id";
                    cmd.Parameters.AddWithValue("$k", klivesPersonId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) sharedCounts[r.GetString(0)] = r.GetInt32(1);
                }

                // Signal 3: how often Tracked people mention them by name.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT pt.person_id, COUNT(*)
                        FROM name_usages u
                        JOIN platform_identities pt ON pt.identity_id = u.target_identity_id
                        JOIN platform_identities ps ON ps.identity_id = u.speaker_identity_id
                        JOIN persons sp ON sp.person_id = ps.person_id
                        WHERE sp.tier = 'tracked'
                        GROUP BY pt.person_id";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) mentionCounts[r.GetString(0)] = r.GetInt32(1);
                }
            }

            var scored = new List<(string PersonId, double Score, JArray Reasons)>();
            foreach (var (personId, name) in displayNames)
            {
                dmCounts.TryGetValue(personId, out int dm);
                sharedCounts.TryGetValue(personId, out int shared);
                mentionCounts.TryGetValue(personId, out int mentions);
                if (dm + shared + mentions < 25) continue;

                double score = 2.0 * Math.Log10(1 + dm) + Math.Log10(1 + shared) + 0.7 * Math.Log10(1 + mentions);
                var reasons = new JArray();
                if (dm > 0) reasons.Add($"{dm} messages in DMs with you");
                if (shared > 0) reasons.Add($"{shared} messages in conversations you're in");
                if (mentions > 0) reasons.Add($"mentioned {mentions}× by people you track");
                scored.Add((personId, score, reasons));
            }

            var top = scored.OrderByDescending(s => s.Score).Take(20).ToList();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var s in top)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    // Preserve a dismissal — don't resurface someone Klives said no to.
                    cmd.CommandText = @"INSERT INTO target_suggestions(person_id, score, reasons_json, computed_at)
                        VALUES($p,$s,$r,$t)
                        ON CONFLICT(person_id) DO UPDATE SET
                            score=excluded.score, reasons_json=excluded.reasons_json, computed_at=excluded.computed_at";
                    cmd.Parameters.AddWithValue("$p", s.PersonId);
                    cmd.Parameters.AddWithValue("$s", Math.Round(s.Score, 3));
                    cmd.Parameters.AddWithValue("$r", s.Reasons.ToString(Newtonsoft.Json.Formatting.None));
                    cmd.Parameters.AddWithValue("$t", now);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }

            string summary = $"{top.Count} target suggestions computed";
            await service.ServiceLog($"[Omniscience] Target suggestions: {summary}");
            return summary;
        }
    }
}
