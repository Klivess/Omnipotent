using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Analytics.Modules;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics
{
    /// <summary>
    /// Runs every registered <see cref="IPersonAnalyticModule"/> for a given person and
    /// persists results into person_statistics. Designed so the metric set is never
    /// considered "complete" \u2014 add new modules to extend the dossier.
    /// Explicit registration (no reflection) per repo policy.
    /// </summary>
    public class AnalyticsEngine
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        public IReadOnlyList<IPersonAnalyticModule> Modules { get; }

        public AnalyticsEngine(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
            Modules = new List<IPersonAnalyticModule>
            {
                new ActivityPatternModule(),
                new VocabularyModule(),
                new SentimentModule(),
                new EmojiUsageModule(),
                new TopicModule(),
                new ResponseBehaviourModule(),
                new SocialGraphModule(),
                new ConflictModule(),
                new HumorModule(),
                new LanguageDetectionModule(),
                new InterestInferenceModule(),
                new MentionAffinityModule(),
                new TimezoneInferenceModule(),
            };
        }

        public async Task RunForPersonAsync(string personId, CancellationToken ct)
        {
            foreach (var m in Modules)
            {
                ct.ThrowIfCancellationRequested();
                JObject payload;
                try { payload = await m.ComputeAsync(personId, db, ct); }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, $"Analytic '{m.Name}' failed for person {personId}");
                    continue;
                }

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO person_statistics(person_id, module_name, module_version, computed_at, payload_json)
                        VALUES($p,$n,$v,$t,$j)
                        ON CONFLICT(person_id, module_name) DO UPDATE SET
                            module_version=excluded.module_version,
                            computed_at=excluded.computed_at,
                            payload_json=excluded.payload_json";
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.Parameters.AddWithValue("$n", m.Name);
                    cmd.Parameters.AddWithValue("$v", m.Version);
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$j", payload.ToString(Newtonsoft.Json.Formatting.None));
                    cmd.ExecuteNonQuery();
                }
                finally { db.WriteLock.Release(); }
            }
        }

        public List<string> GetAllPersonIds()
        {
            var ids = new List<string>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT person_id FROM persons WHERE merged_into_person_id IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
    }
}
