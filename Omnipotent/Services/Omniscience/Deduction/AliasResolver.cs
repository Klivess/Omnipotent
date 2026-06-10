using Microsoft.Data.Sqlite;
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
    /// Folds name_usages into per-person alias conclusions: real-name deduction from
    /// vocatives ("3 different people called B 'james' in replies → B's name is James"),
    /// per-community nickname maps, and self-identifications (strongest evidence).
    /// Conclusions land in person_facts (category 'name'). The Klives person's learned
    /// aliases auto-feed the radar so nicknames people invent are caught without config.
    /// </summary>
    public class AliasResolver
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        public AliasResolver(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<string> RunAsync(CancellationToken ct)
        {
            var conclusions = ComputeAliasConclusions();
            int factsWritten = 0;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var c in conclusions)
                {
                    ct.ThrowIfCancellationRequested();
                    GraphAssembler.UpsertFact(conn, tx, c.PersonId, "name", c.FactText, c.Confidence,
                        "server", new JArray(c.EvidenceMessageIds.Take(10)), c.LastSeenAt, "alias", null);
                    factsWritten++;
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }

            await FeedKlivesRadarAsync(conclusions);
            string summary = $"{conclusions.Count} alias conclusions, {factsWritten} name facts";
            await service.ServiceLog($"[Omniscience] Alias resolver: {summary}");
            return summary;
        }

        public class AliasConclusion
        {
            public string PersonId = "";
            public string Name = "";
            public string Kind = "";           // real_name | nickname | self_identified
            public string FactText = "";
            public double Confidence;
            public int UsageCount;
            public int DistinctSpeakers;
            public long LastSeenAt;
            public List<string> EvidenceMessageIds = new();
        }

        /// <summary>Aggregates name_usages → conclusions. Pure read; reusable by routes.</summary>
        public List<AliasConclusion> ComputeAliasConclusions(string? onlyPersonId = null)
        {
            var rows = new List<(string Person, string Name, string Type, int Count, int Speakers, long LastAt, string EvidenceCsv, string? Username)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT pi.person_id, u.name_used, u.usage_type, COUNT(*),
                           COUNT(DISTINCT u.speaker_identity_id), MAX(COALESCE(u.occurred_at,0)),
                           GROUP_CONCAT(COALESCE(u.evidence_message_id,''), '|'),
                           MIN(pi.platform_username)
                    FROM name_usages u
                    JOIN platform_identities pi ON pi.identity_id = u.target_identity_id
                    WHERE u.target_identity_id IS NOT NULL"
                    + (onlyPersonId != null ? " AND pi.person_id=$p" : "") + @"
                    GROUP BY pi.person_id, u.name_used, u.usage_type";
                if (onlyPersonId != null) cmd.Parameters.AddWithValue("$p", onlyPersonId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                        r.GetInt64(5), r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
                }
            }

            var conclusions = new List<AliasConclusion>();
            foreach (var group in rows.GroupBy(x => (x.Person, x.Name)))
            {
                string person = group.Key.Person;
                string name = group.Key.Name;
                int total = group.Sum(g => g.Count);
                int speakers = group.Max(g => g.Speakers);
                long lastAt = group.Max(g => g.LastAt);
                bool selfIdentified = group.Any(g => g.Type == "self_identification");
                bool vocative = group.Any(g => g.Type is "vocative" or "greeting");
                string? username = group.First().Username;

                // Filter junk: too generic, or just their handle echoed back.
                if (!selfIdentified && total < 2) continue;
                if (username != null && string.Equals(name, username, StringComparison.OrdinalIgnoreCase)) continue;

                double confidence;
                string kind;
                if (selfIdentified) { confidence = 0.9; kind = "self_identified"; }
                else if (vocative && speakers >= 3) { confidence = 0.85; kind = "real_name"; }
                else if (vocative && speakers == 2) { confidence = 0.65; kind = "real_name"; }
                else if (vocative) { confidence = 0.45; kind = "nickname"; }
                else { confidence = 0.35; kind = "nickname"; }

                var evidence = group.SelectMany(g => g.EvidenceCsv.Split('|'))
                                    .Where(e => e.Length > 0).Distinct().Take(10).ToList();
                conclusions.Add(new AliasConclusion
                {
                    PersonId = person,
                    Name = name,
                    Kind = kind,
                    Confidence = confidence,
                    UsageCount = total,
                    DistinctSpeakers = speakers,
                    LastSeenAt = lastAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastAt,
                    EvidenceMessageIds = evidence,
                    FactText = kind switch
                    {
                        "self_identified" => $"Identified themselves as '{name}'",
                        "real_name" => $"Called '{name}' by {speakers} different people ({total}× — likely real name)",
                        _ => $"Goes by '{name}' ({total} uses)",
                    },
                });
            }
            return conclusions;
        }

        // Radar v2: learned aliases for the Klives person merge into the live alias set,
        // so nicknames people invent for Klives are caught without any configuration.
        private async Task FeedKlivesRadarAsync(List<AliasConclusion> conclusions)
        {
            try
            {
                string? klivesPersonId = null;
                using (var conn = db.Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT person_id FROM platform_identities WHERE platform='discord' AND platform_user_id=$u";
                    cmd.Parameters.AddWithValue("$u", OmniPaths.KlivesDiscordAccountID.ToString());
                    klivesPersonId = cmd.ExecuteScalar() as string;
                }
                if (klivesPersonId == null) return;

                var learned = conclusions
                    .Where(c => c.PersonId == klivesPersonId && c.Confidence >= 0.45)
                    .Select(c => c.Name)
                    .ToList();
                if (learned.Count == 0) return;
                service.Radar.AddLearnedAliases(learned);
                await service.ServiceLog($"[Omniscience] Radar learned aliases for Klives: {string.Join(", ", learned)}");
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Radar alias feed failed");
            }
        }
    }
}
