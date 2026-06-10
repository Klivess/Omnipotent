using DSharpPlus.Entities;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveBot_Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Scheduling
{
    /// <summary>
    /// Daily intelligence briefing DM'd to Klives via KliveBot after the nightly run:
    /// new facts learned, hypotheses with fresh evidence, mood anomalies, friendship
    /// trends, radar activity and pipeline stats — the "what did Omniscience learn
    /// today" digest.
    /// </summary>
    public class DailyBriefing
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        public DailyBriefing(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<string> ComposeAndSendAsync(CancellationToken ct)
        {
            string markdown = Compose();
            var bots = await service.GetServicesByType<KliveBotDiscord>();
            var bot = bots is { Length: > 0 } ? bots[0] as KliveBotDiscord : null;
            if (bot != null && markdown.Length > 0)
            {
                // Discord embed description cap is 4096; chunk if the day was busy.
                foreach (var chunk in Chunk(markdown, 3900))
                {
                    try
                    {
                        await bot.SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed(
                            "🧠 Omniscience daily briefing", chunk, DiscordColor.Teal));
                    }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Briefing DM failed"); }
                }
            }
            return markdown;
        }

        public string Compose()
        {
            long since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
            var sb = new StringBuilder();
            using var conn = db.Open();

            // New facts learned (per person, best first).
            var factLines = new List<string>();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COALESCE(p.display_name,'?'), f.category, f.fact_text, f.confidence, f.extracted_by
                    FROM person_facts f
                    LEFT JOIN persons p ON p.person_id = f.person_id
                    WHERE f.created_at >= $since AND f.status='active'
                    ORDER BY f.confidence DESC LIMIT 14";
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    factLines.Add($"- **{r.GetString(0)}** [{r.GetString(1)}]: {Truncate(r.GetString(2), 140)}" +
                                  (r.GetString(4) == "detective" ? " 🔍" : ""));
            }
            catch { }
            if (factLines.Count > 0)
            {
                sb.AppendLine("**New facts learned** (🔍 = derived by the detective pass)");
                factLines.ForEach(l => sb.AppendLine(l));
                sb.AppendLine();
            }

            // Hypotheses that gathered fresh evidence.
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COALESCE(p.display_name,'?'), h.statement, h.evidence_json
                    FROM hypotheses h
                    LEFT JOIN persons p ON p.person_id = h.person_id
                    WHERE h.status='open' AND h.evidence_json IS NOT NULL AND length(h.evidence_json) > 4
                    ORDER BY h.created_at DESC LIMIT 6";
                using var r = cmd.ExecuteReader();
                bool any = false;
                var lines = new StringBuilder();
                while (r.Read())
                {
                    int evidenceCount = 0;
                    try { evidenceCount = JArray.Parse(r.GetString(2)).Count; } catch { }
                    if (evidenceCount == 0) continue;
                    lines.AppendLine($"- **{r.GetString(0)}**: \"{Truncate(r.GetString(1), 120)}\" ({evidenceCount} watcher hits)");
                    any = true;
                }
                if (any)
                {
                    sb.AppendLine("**Hypotheses gathering evidence**");
                    sb.Append(lines);
                    sb.AppendLine();
                }
            }
            catch { }

            // Mood anomalies for tracked persons.
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COALESCE(p.display_name,'?'), s.payload_json
                    FROM person_statistics s
                    JOIN persons p ON p.person_id = s.person_id
                    WHERE s.module_name='mood_trajectory' AND p.tier='tracked'";
                using var r = cmd.ExecuteReader();
                bool any = false;
                var lines = new StringBuilder();
                while (r.Read())
                {
                    try
                    {
                        var payload = JObject.Parse(r.GetString(1));
                        string flag = payload.Value<string>("current_mood_flag") ?? "normal";
                        if (flag == "normal") continue;
                        double z = payload.Value<double?>("current_mood_z_score") ?? 0;
                        lines.AppendLine($"- **{r.GetString(0)}** seems {(flag == "low" ? "down 📉" : "unusually upbeat 📈")} (z={z:0.0})");
                        any = true;
                    }
                    catch { }
                }
                if (any)
                {
                    sb.AppendLine("**Mood anomalies**");
                    sb.Append(lines);
                    sb.AppendLine();
                }
            }
            catch { }

            // Friendship trends (growing/fading) for tracked persons.
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COALESCE(p.display_name,'?'), s.payload_json
                    FROM person_statistics s
                    JOIN persons p ON p.person_id = s.person_id
                    WHERE s.module_name='friendship_strength' AND p.tier='tracked'";
                using var r = cmd.ExecuteReader();
                bool any = false;
                var lines = new StringBuilder();
                while (r.Read())
                {
                    try
                    {
                        var payload = JObject.Parse(r.GetString(1));
                        foreach (var rel in (payload["relationships"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            string trend = rel.Value<string>("trend") ?? "stable";
                            if (trend == "stable") continue;
                            lines.AppendLine($"- **{r.GetString(0)}** ↔ {rel.Value<string>("display_name")}: {(trend == "growing" ? "growing 🌱" : "fading 🍂")}");
                            any = true;
                        }
                    }
                    catch { }
                }
                if (any)
                {
                    sb.AppendLine("**Friendships changing**");
                    sb.Append(lines);
                    sb.AppendLine();
                }
            }
            catch { }

            // Radar + pipeline stats.
            try
            {
                long Count(string sql, bool sinceParam)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    if (sinceParam) cmd.Parameters.AddWithValue("$since", since);
                    return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                long radar = Count("SELECT COUNT(*) FROM radar_alerts WHERE occurred_at >= $since", true);
                long messages = Count("SELECT COUNT(*) FROM messages WHERE captured_at >= $since", true);
                long qa = Count("SELECT COUNT(*) FROM qa_pairs WHERE extracted_at >= $since", true);
                long suggestions = Count("SELECT COUNT(*) FROM target_suggestions WHERE dismissed=0", false);
                sb.AppendLine("**Pipeline (24h)**");
                sb.AppendLine($"- {messages} messages ingested · {qa} Q&A pairs mined · {radar} radar mentions of you · {suggestions} target suggestions pending");
            }
            catch { }

            return sb.ToString().Trim();
        }

        private static IEnumerable<string> Chunk(string text, int size)
        {
            for (int i = 0; i < text.Length; i += size)
                yield return text.Substring(i, Math.Min(size, text.Length - i));
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
