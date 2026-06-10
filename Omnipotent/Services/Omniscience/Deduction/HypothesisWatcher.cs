using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Ingest-time evidence watcher: every live message is checked against open
    /// hypotheses' confirm-queries (cheap keyword match). Hits are appended to the
    /// hypothesis's evidence so the next detective pass can confirm/refute with the
    /// new material in front of it — answers get caught the moment they appear.
    /// </summary>
    public class HypothesisWatcher
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private volatile List<(string HypothesisId, string PersonId, string[] Keywords)> watchers = new();
        private DateTime lastRefresh = DateTime.MinValue;
        private readonly object refreshLock = new();

        public HypothesisWatcher(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        /// <summary>Hooked to IngestPipeline.OnMessagePersisted.</summary>
        public void InspectMessage(HarvestedMessage msg)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(msg.Content) || msg.Content.Length < 8) return;
                RefreshIfStale();
                var current = watchers;
                if (current.Count == 0) return;

                string low = msg.Content.ToLowerInvariant();
                foreach (var (hypothesisId, _, keywords) in current)
                {
                    int hits = keywords.Count(k => low.Contains(k));
                    if (hits < Math.Max(1, keywords.Length / 2)) continue;
                    _ = Task.Run(() => AppendEvidenceAsync(hypothesisId, msg));
                }
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Hypothesis watcher failed");
            }
        }

        private void RefreshIfStale()
        {
            if (DateTime.UtcNow - lastRefresh < TimeSpan.FromMinutes(10)) return;
            lock (refreshLock)
            {
                if (DateTime.UtcNow - lastRefresh < TimeSpan.FromMinutes(10)) return;
                var fresh = new List<(string, string, string[])>();
                try
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT hypothesis_id, person_id, confirm_query FROM hypotheses
                        WHERE status='open' AND confirm_query IS NOT NULL LIMIT 200";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var keywords = r.GetString(2).ToLowerInvariant()
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(k => k.Length >= 3)
                            .ToArray();
                        if (keywords.Length > 0) fresh.Add((r.GetString(0), r.GetString(1), keywords));
                    }
                }
                catch { }
                watchers = fresh;
                lastRefresh = DateTime.UtcNow;
            }
        }

        private async Task AppendEvidenceAsync(string hypothesisId, HarvestedMessage msg)
        {
            try
            {
                string compositeId = msg.Platform + ":" + msg.PlatformMessageId;
                await db.WriteLock.WaitAsync();
                try
                {
                    using var conn = db.Open();
                    string? existing;
                    using (var get = conn.CreateCommand())
                    {
                        get.CommandText = "SELECT evidence_json FROM hypotheses WHERE hypothesis_id=$id";
                        get.Parameters.AddWithValue("$id", hypothesisId);
                        existing = get.ExecuteScalar() as string;
                    }
                    JArray evidence;
                    try { evidence = existing != null ? JArray.Parse(existing) : new JArray(); }
                    catch { evidence = new JArray(); }
                    if (evidence.Any(e => e.ToString() == compositeId)) return;
                    evidence.Add(compositeId);

                    using var upd = conn.CreateCommand();
                    upd.CommandText = "UPDATE hypotheses SET evidence_json=$e WHERE hypothesis_id=$id AND status='open'";
                    upd.Parameters.AddWithValue("$e", new JArray(evidence.Take(20)).ToString(Newtonsoft.Json.Formatting.None));
                    upd.Parameters.AddWithValue("$id", hypothesisId);
                    upd.ExecuteNonQuery();
                }
                finally { db.WriteLock.Release(); }
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Hypothesis evidence append failed");
            }
        }
    }
}
