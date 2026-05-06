using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Omniscience.Analytics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Profiling
{
    /// <summary>
    /// Runs the LLM-driven personality dossier for a single person, using already-computed
    /// person_statistics rows + a sampled set of representative messages.
    /// </summary>
    public class PersonalityProfiler
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        public PersonalityProfiler(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<bool> GenerateForPersonAsync(string personId, CancellationToken ct)
        {
            // 1. Pull display name + handles.
            string displayName, handlesJoined;
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT display_name FROM persons WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                var dn = cmd.ExecuteScalar();
                if (dn == null) return false;
                displayName = dn?.ToString() ?? "(unknown)";

                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = @"SELECT platform, platform_username, display_name FROM platform_identities
                    WHERE person_id IN (SELECT person_id FROM persons WHERE person_id=$p OR merged_into_person_id=$p)";
                cmd2.Parameters.AddWithValue("$p", personId);
                using var r = cmd2.ExecuteReader();
                var handles = new List<string>();
                while (r.Read())
                    handles.Add($"{r.GetString(0)}:{(r.IsDBNull(1) ? "" : r.GetString(1))}{(r.IsDBNull(2) ? "" : $" ({r.GetString(2)})")}");
                handlesJoined = string.Join(", ", handles);
            }

            // 2. Bundle analytics.
            JObject statsBundle;
            using (var conn = db.Open())
            {
                statsBundle = new JObject();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT module_name, payload_json FROM person_statistics WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try { statsBundle[r.GetString(0)] = JObject.Parse(r.GetString(1)); }
                    catch { statsBundle[r.GetString(0)] = r.GetString(1); }
                }
            }

            // 3. Sampled messages: spread across timeline, prefer non-empty + non-trivial content.
            var samples = new List<string>();
            using (var conn = db.Open())
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return false;
                string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT content FROM messages
                    WHERE author_identity_id IN ({inC}) AND content IS NOT NULL AND length(content) >= 10
                    ORDER BY sent_at ASC";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                using var r = cmd.ExecuteReader();
                while (r.Read()) samples.Add(r.GetString(0));
            }
            // Stride to ~80 messages.
            var trimmed = new List<string>();
            if (samples.Count > 0)
            {
                int stride = Math.Max(1, samples.Count / 200);
                for (int i = 0; i < samples.Count && trimmed.Count < 200; i += stride)
                    trimmed.Add(samples[i]);
            }

            // 4. Social graph one-liner from social_graph module.
            var social = new List<string>();
            if (statsBundle["social_graph"] is JObject sg && sg["relationships"] is JArray rels)
                foreach (var rel in rels.Take(8))
                    social.Add($"{rel["display_name"]} ({rel["platform"]}:{rel["platform_username"]}) - {rel["interaction_messages"]} msgs in shared conversations");

            string prompt = ProfilePromptBuilder.Build(displayName, handlesJoined, statsBundle, trimmed, social);

            // 5. Call KliveLLM.
            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0)
            {
                _ = service.ServiceLogError("KliveLLM service not available; cannot generate personality profile.");
                return false;
            }
            var llm = (KliveLLM.KliveLLM)llms[0];
            KliveLLM.KliveLLM.KliveLLMResponse resp;
            try
            {
                resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 2048, systemPrompt: ProfilePromptBuilder.SystemPrompt, useFreeModel: true);
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "KliveLLM threw while generating personality profile");
                return false;
            }
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response)) return false;

            // 6. Split narrative + traits JSON.
            string narrative = resp.Response;
            string traitsJson = "{}";
            int idxS = resp.Response.IndexOf(ProfilePromptBuilder.TraitsSentinel, StringComparison.Ordinal);
            if (idxS >= 0)
            {
                narrative = resp.Response[..idxS].Trim();
                string tail = resp.Response[(idxS + ProfilePromptBuilder.TraitsSentinel.Length)..].Trim();
                int braceStart = tail.IndexOf('{');
                int braceEnd = tail.LastIndexOf('}');
                if (braceStart >= 0 && braceEnd > braceStart)
                {
                    string candidate = tail.Substring(braceStart, braceEnd - braceStart + 1);
                    try { JObject.Parse(candidate); traitsJson = candidate; }
                    catch { }
                }
            }

            // 7. Second LLM pass: biographical inferences (location, school, employment\u2026).
            //    Failure here must not block the personality dossier from being persisted.
            string? biographical = null;
            try
            {
                string bioPrompt = ProfilePromptBuilder.BuildBiographical(displayName, handlesJoined, statsBundle, trimmed);
                var bioResp = await llm.QueryLLM(bioPrompt, sessionId: null, maxTokensOverride: 1536,
                    systemPrompt: ProfilePromptBuilder.BiographicalSystemPrompt, useFreeModel: true);
                if (bioResp.Success && !string.IsNullOrWhiteSpace(bioResp.Response))
                    biographical = bioResp.Response.Trim();
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "Biographical dossier generation failed (non-fatal).");
            }

            // 8. Persist (history-preserving: one row per generation).
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO personality_profiles(profile_id, person_id, generated_at, model_used, prompt_hash, profile_markdown, traits_json, biographical_markdown)
                    VALUES($id,$p,$t,$m,$h,$n,$tj,$bio)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$m", "KliveLLM");
                cmd.Parameters.AddWithValue("$h", $"pt={resp.PromptTokens};ct={resp.CompletionTokens}");
                cmd.Parameters.AddWithValue("$n", narrative);
                cmd.Parameters.AddWithValue("$tj", traitsJson);
                cmd.Parameters.AddWithValue("$bio", (object?)biographical ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
            return true;
        }
    }
}
