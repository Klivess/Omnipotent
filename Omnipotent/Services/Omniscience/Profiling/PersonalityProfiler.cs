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
            int messageLimit = Math.Clamp(await service.GetIntOmniSetting("OmniscienceProfilerLLMMessageLimit", 200), 20, 500);

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

            // 3. Sampled messages: stratified recent-heavy (≈50% last 90 days, 30% last year,
            //    20% lifetime) so the dossier reflects the *current* persona while older
            //    eras still provide historical context. Chronological order preserved.
            var samples = new List<(string Content, DateTime SentAt)>();
            using (var conn = db.Open())
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return false;
                string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT content, sent_at FROM messages
                    WHERE author_identity_id IN ({inC}) AND content IS NOT NULL AND length(content) >= 10
                    ORDER BY sent_at ASC";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                using var r = cmd.ExecuteReader();
                while (r.Read()) samples.Add((r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).UtcDateTime));
            }
            var trimmed = StratifiedSample(samples, messageLimit);

            // 4. Social graph one-liner from social_graph module.
            var social = new List<string>();
            if (statsBundle["social_graph"] is JObject sg && sg["relationships"] is JArray rels)
                foreach (var rel in rels.Take(8))
                    social.Add($"{rel["display_name"]} ({rel["platform"]}:{rel["platform_username"]}) - {rel["interaction_messages"]} msgs in shared conversations");

            // 4b. Established facts from the deduction engine: the dossier composes from
            //     verified knowledge (citing [Fn]) instead of re-deriving from raw samples.
            var establishedFacts = new List<string>();
            var factEvidence = new JObject();
            using (var conn = db.Open())
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT fact_id, category, fact_text, confidence, source_context FROM person_facts
                        WHERE person_id=$p AND status='active'
                        ORDER BY confidence DESC, last_evidence_at DESC LIMIT 60";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    int fn = 0;
                    while (r.Read())
                    {
                        fn++;
                        establishedFacts.Add($"F{fn} [{r.GetString(1)}|{r.GetString(4) ?? "?"}|conf {r.GetDouble(3):0.00}] {r.GetString(2)}");
                        factEvidence["F" + fn] = r.GetString(0);
                    }
                }
                catch { /* pre-v8 schema */ }
            }

            string prompt = ProfilePromptBuilder.Build(displayName, handlesJoined, statsBundle, trimmed, social, establishedFacts);

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

            // 8. Fetch the previous dossier (for the diff changelog) before persisting.
            string? previousMarkdown = null;
            using (var conn = db.Open())
            {
                using var prev = conn.CreateCommand();
                prev.CommandText = @"SELECT profile_markdown FROM personality_profiles
                    WHERE person_id=$p ORDER BY generated_at DESC LIMIT 1";
                prev.Parameters.AddWithValue("$p", personId);
                previousMarkdown = prev.ExecuteScalar() as string;
            }

            // 9. Persist (history-preserving: one row per generation).
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO personality_profiles(profile_id, person_id, generated_at, model_used, prompt_hash, profile_markdown, traits_json, biographical_markdown, evidence_json)
                    VALUES($id,$p,$t,$m,$h,$n,$tj,$bio,$ev)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$m", "KliveLLM");
                cmd.Parameters.AddWithValue("$h", $"pt={resp.PromptTokens};ct={resp.CompletionTokens}");
                cmd.Parameters.AddWithValue("$n", narrative);
                cmd.Parameters.AddWithValue("$tj", traitsJson);
                cmd.Parameters.AddWithValue("$bio", (object?)biographical ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ev", factEvidence.Count > 0 ? factEvidence.ToString(Newtonsoft.Json.Formatting.None) : (object)DBNull.Value);
                cmd.ExecuteNonQuery();

                using var targetCmd = conn.CreateCommand();
                targetCmd.CommandText = @"UPDATE person_profile_targets
                    SET last_profiled_at=$t, last_profile_status='ok', updated_at=$t
                    WHERE person_id=$p";
                targetCmd.Parameters.AddWithValue("$p", personId);
                targetCmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                try { targetCmd.ExecuteNonQuery(); } catch { }
            }
            finally { db.WriteLock.Release(); }

            // 10. Profile diff changelog ("what changed about X") — non-fatal.
            if (!string.IsNullOrWhiteSpace(previousMarkdown))
            {
                try { await GenerateChangelogAsync(personId, llm, previousMarkdown!, narrative, ct); }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "Profile changelog generation failed (non-fatal)."); }
            }
            return true;
        }

        private async Task GenerateChangelogAsync(string personId, KliveLLM.KliveLLM llm,
            string previousMarkdown, string newMarkdown, CancellationToken ct)
        {
            string prompt = "Compare two versions of a personality dossier for the same person. " +
                "List ONLY meaningful changes (new traits, dropped traits, shifted assessments, new facts) as terse markdown bullets. " +
                "If nothing meaningfully changed, output exactly: no significant changes.\n\n" +
                "## PREVIOUS\n" + Truncate(previousMarkdown, 5000) + "\n\n## NEW\n" + Truncate(newMarkdown, 5000);
            var resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 600,
                systemPrompt: "You diff personality dossiers. Be terse and concrete.", useFreeModel: true);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response)) return;
            string changes = resp.Response.Trim();
            if (changes.Length < 4) return;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO profile_changelogs(changelog_id, person_id, generated_at, changes_markdown)
                    VALUES($id,$p,$t,$c)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$c", changes);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        /// <summary>
        /// Era profile: a mini-dossier built ONLY from one calendar year's messages —
        /// "talk to 2023-era X". Stored in personality_profiles with the era column set,
        /// so the personality timeline is browsable. Returns false when the era is too
        /// sparse (&lt;100 usable messages).
        /// </summary>
        public async Task<bool> GenerateEraProfileAsync(string personId, int year, CancellationToken ct)
        {
            string displayName;
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT display_name FROM persons WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                var dn = cmd.ExecuteScalar();
                if (dn == null) return false;
                displayName = dn.ToString() ?? "(unknown)";
            }

            long eraStart = new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            long eraEnd = new DateTimeOffset(new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var samples = new List<string>();
            using (var conn = db.Open())
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return false;
                string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT content FROM messages
                    WHERE author_identity_id IN ({inC}) AND sent_at >= $s AND sent_at < $e
                      AND content IS NOT NULL AND length(content) >= 10
                    ORDER BY sent_at ASC";
                int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                cmd.Parameters.AddWithValue("$s", eraStart);
                cmd.Parameters.AddWithValue("$e", eraEnd);
                using var r = cmd.ExecuteReader();
                while (r.Read()) samples.Add(r.GetString(0));
            }
            if (samples.Count < 100) return false;

            // Uniform stride within the era — recency weighting is meaningless inside one year.
            var trimmed = new List<string>();
            int stride = Math.Max(1, samples.Count / 200);
            for (int i = 0; i < samples.Count && trimmed.Count < 200; i += stride) trimmed.Add(samples[i]);

            string prompt = ProfilePromptBuilder.Build(
                $"{displayName} (as they were in {year} — describe their {year} persona, in past-aware present tense)",
                "", new JObject(), trimmed, new List<string>());

            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0) return false;
            var llm = (KliveLLM.KliveLLM)llms[0];
            var resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 1800,
                systemPrompt: ProfilePromptBuilder.SystemPrompt, useFreeModel: true);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response)) return false;

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
                    try { JObject.Parse(candidate); traitsJson = candidate; } catch { }
                }
            }

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO personality_profiles(profile_id, person_id, generated_at, model_used, prompt_hash, profile_markdown, traits_json, era)
                    VALUES($id,$p,$t,$m,$h,$n,$tj,$era)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$m", "KliveLLM");
                cmd.Parameters.AddWithValue("$h", $"era={year};pt={resp.PromptTokens};ct={resp.CompletionTokens}");
                cmd.Parameters.AddWithValue("$n", narrative);
                cmd.Parameters.AddWithValue("$tj", traitsJson);
                cmd.Parameters.AddWithValue("$era", year.ToString());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
            return true;
        }

        /// <summary>
        /// Recent-heavy stratified sampling: 50% from the last 90 days, 30% from the last
        /// year, 20% lifetime. Shortfalls in one stratum are redistributed to the others
        /// (a person inactive for 6 months still fills the budget from older eras).
        /// </summary>
        internal static List<string> StratifiedSample(List<(string Content, DateTime SentAt)> msgs, int limit)
        {
            if (msgs.Count == 0) return new List<string>();
            if (msgs.Count <= limit) return msgs.Select(m => m.Content).ToList();

            var now = DateTime.UtcNow;
            var recent = msgs.Where(m => (now - m.SentAt).TotalDays <= 90).ToList();
            var mid = msgs.Where(m => (now - m.SentAt).TotalDays is > 90 and <= 365).ToList();
            var old = msgs.Where(m => (now - m.SentAt).TotalDays > 365).ToList();

            int wantRecent = (int)(limit * 0.5), wantMid = (int)(limit * 0.3);
            int wantOld = limit - wantRecent - wantMid;
            int takeRecent = Math.Min(wantRecent, recent.Count);
            int takeMid = Math.Min(wantMid, mid.Count);
            int takeOld = Math.Min(wantOld, old.Count);
            int leftover = limit - takeRecent - takeMid - takeOld;
            // Redistribute spare budget, newest strata first.
            int spareRecent = Math.Min(leftover, recent.Count - takeRecent); takeRecent += spareRecent; leftover -= spareRecent;
            int spareMid = Math.Min(leftover, mid.Count - takeMid); takeMid += spareMid; leftover -= spareMid;
            takeOld += Math.Min(leftover, old.Count - takeOld);

            var picked = new List<(string Content, DateTime SentAt)>();
            void Stride(List<(string Content, DateTime SentAt)> source, int take)
            {
                if (take <= 0 || source.Count == 0) return;
                double step = (double)source.Count / take;
                for (int i = 0; i < take; i++) picked.Add(source[(int)(i * step)]);
            }
            Stride(old, takeOld);
            Stride(mid, takeMid);
            Stride(recent, takeRecent);
            return picked.OrderBy(p => p.SentAt).Select(p => p.Content).ToList();
        }
    }
}
