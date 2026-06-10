using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Deduction stage 3: the nightly synthesis pass per Tracked person. Reads the
    /// person's knowledge graph (facts, relationships, aliases, Q&amp;A pairs, module
    /// stats) and asks the LLM to: derive second-order facts WITH derivation chains
    /// ("born 2007±1: year-11 in Sept 2023 [F4] + 'just turned 17' Jan 2024 [F9]"),
    /// flag contradictions (DM-sourced facts outrank public claims; a public/private
    /// mismatch is itself recorded as a fact), emit hypotheses with confirm-queries
    /// for the ingest watchers, and maintain the open-questions/completeness ledger —
    /// the engine knows what it still doesn't know.
    /// </summary>
    public class DetectivePass
    {
        public static readonly string[] CompletenessSlots =
        {
            "real_name", "age_dob", "location", "school_or_employer", "family",
            "partner", "pets", "interests", "schedule", "languages",
        };

        private const string SystemPrompt =
@"You are a careful detective synthesising a dossier from structured evidence about one person.
Evidence items are numbered (F1, F2…). Source context matters: facts from 'dm' are private
disclosures (high trust); 'server' facts are public-facing and may be performative. A conflict
between a private and a public claim usually means the public one is presentation — and that
mismatch is itself worth recording as a fact.
Output a SINGLE valid JSON object, nothing else:

{""derived_facts"":[{""category"":""age|location|family|relationships|education|employment|name|schedule|misc"",""fact"":""concrete second-order conclusion"",""confidence"":""low|medium|high"",""derived_from"":[""F3"",""F9""],""reasoning"":""one-line chain""}],
 ""contradictions"":[{""a"":""F2"",""b"":""F7"",""winner"":""a|b|unclear"",""reason"":""why""}],
 ""hypotheses"":[{""statement"":""testable theory about the person"",""rationale"":""why plausible"",""confirm_query"":""short search phrase that would find confirming messages""}],
 ""open_questions"":[{""slot"":""real_name|age_dob|location|school_or_employer|family|partner|pets|interests|schedule|languages"",""question"":""what to look for""}],
 ""completeness"":{""real_name"":""known|partial|unknown"",""age_dob"":""..."",""location"":""..."",""school_or_employer"":""..."",""family"":""..."",""partner"":""..."",""pets"":""..."",""interests"":""..."",""schedule"":""..."",""languages"":""...""}}

Rules: derive ONLY what the evidence supports (arithmetic on dates/ages is encouraged — show it
in reasoning). Do not restate single facts as 'derived'. Max 12 derived facts, 8 hypotheses.";

        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        public DetectivePass(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<bool> RunForPersonAsync(string personId, CancellationToken ct)
        {
            var (prompt, factIdByRef) = BuildPrompt(personId);
            if (prompt == null) return false;

            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0) return false;
            var llm = (KliveLLM.KliveLLM)llms[0];
            KliveLLM.KliveLLM.KliveLLMResponse resp;
            try
            {
                resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 2200,
                    systemPrompt: SystemPrompt, useFreeModel: true);
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, $"[Omniscience] Detective pass LLM failed for {personId}");
                return false;
            }
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response)) return false;

            JObject result;
            try
            {
                string raw = resp.Response;
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start < 0 || end <= start) return false;
                result = JObject.Parse(raw[start..(end + 1)]);
            }
            catch { return false; }

            await ApplyResultAsync(personId, result, factIdByRef, ct);
            return true;
        }

        // ── Prompt: the person's whole graph, numbered for citation ──
        private (string? Prompt, Dictionary<string, string> FactIdByRef) BuildPrompt(string personId)
        {
            var sb = new StringBuilder();
            var factIdByRef = new Dictionary<string, string>();
            using var conn = db.Open();

            string displayName = "(unknown)";
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT display_name FROM persons WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                if (cmd.ExecuteScalar() is string dn) displayName = dn;
            }
            sb.AppendLine($"# Person: {displayName}");
            sb.AppendLine($"# Today's date: {DateTime.UtcNow:yyyy-MM-dd}");
            sb.AppendLine();

            // Active facts, numbered F1..Fn (the citation universe).
            sb.AppendLine("## Established facts");
            int n = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT fact_id, category, fact_text, confidence, source_context, first_evidence_at, extracted_by
                    FROM person_facts WHERE person_id=$p AND status='active'
                    ORDER BY category, confidence DESC LIMIT 120";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    n++;
                    string fref = "F" + n;
                    factIdByRef[fref] = r.GetString(0);
                    string when = r.IsDBNull(5) ? "?" : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).UtcDateTime.ToString("yyyy-MM");
                    sb.AppendLine($"{fref} [{r.GetString(1)}|{r.GetString(4) ?? "?"}|{when}|conf {r.GetDouble(3):0.00}] {r.GetString(2)}");
                }
            }
            if (n == 0) return (null, factIdByRef); // nothing to synthesise yet

            // Q&A pairs: the highest-grade raw evidence.
            sb.AppendLine();
            sb.AppendLine("## Direct Q&A answers given by this person");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT q.question, q.answer, q.occurred_at FROM qa_pairs q
                    JOIN platform_identities pi ON pi.identity_id = q.answerer_identity_id
                    WHERE pi.person_id=$p ORDER BY q.occurred_at DESC LIMIT 30";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string when = r.IsDBNull(2) ? "?" : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime.ToString("yyyy-MM");
                    sb.AppendLine($"- [{when}] Q: {Truncate(r.GetString(0), 120)} → A: {Truncate(r.GetString(1), 160)}");
                }
            }

            // Relationships.
            sb.AppendLine();
            sb.AppendLine("## Relationship signals");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(p.display_name, er.entity_id, '?'), er.rel_type, er.evidence_count, er.confidence
                    FROM entity_relationships er
                    LEFT JOIN persons p ON p.person_id = er.related_person_id
                    WHERE er.owner_person_id=$p AND er.status='active'
                    ORDER BY er.evidence_count DESC LIMIT 25";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    sb.AppendLine($"- {r.GetString(0)}: {r.GetString(1)} (signals {r.GetInt32(2)}, conf {r.GetDouble(3):0.00})");
            }

            // Entities (people/places/orgs in their world).
            sb.AppendLine();
            sb.AppendLine("## Entities in their world");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT kind, canonical_name, descriptor, mention_count FROM entities
                    WHERE owner_person_id=$p ORDER BY mention_count DESC LIMIT 30";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    sb.AppendLine($"- [{r.GetString(0)}] {r.GetString(1)}{(r.IsDBNull(2) ? "" : " — " + Truncate(r.GetString(2), 120))} ({r.GetInt32(3)}×)");
            }

            // Behavioural stats (compact).
            sb.AppendLine();
            sb.AppendLine("## Behavioural analytics (selected)");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT module_name, payload_json FROM person_statistics
                    WHERE person_id=$p AND module_name IN ('timezone_inference','chronotype','sleep_schedule','language','facet_divergence')";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try
                    {
                        var compact = Analytics.AnalyticSplits.Compact(JObject.Parse(r.GetString(1)));
                        sb.AppendLine($"- {r.GetString(0)}: {compact.ToString(Newtonsoft.Json.Formatting.None)}");
                    }
                    catch { }
                }
            }

            // Open hypotheses so the model can confirm/refute rather than re-invent.
            sb.AppendLine();
            sb.AppendLine("## Currently open hypotheses");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT statement FROM hypotheses WHERE person_id=$p AND status='open' LIMIT 10";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) sb.AppendLine("- " + r.GetString(0));
            }

            sb.AppendLine();
            sb.AppendLine("Synthesise now. Output the JSON object only.");
            return (sb.ToString(), factIdByRef);
        }

        private async Task ApplyResultAsync(string personId, JObject result, Dictionary<string, string> factIdByRef, CancellationToken ct)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();

                // Derived facts with derivation chains.
                if (result["derived_facts"] is JArray derived)
                {
                    foreach (var d in derived.OfType<JObject>().Take(12))
                    {
                        string text = (d.Value<string>("fact") ?? "").Trim();
                        if (text.Length < 5) continue;
                        var chain = new JObject(
                            new JProperty("from", new JArray((d["derived_from"] as JArray ?? new JArray())
                                .Select(f => factIdByRef.TryGetValue(f.ToString(), out var id) ? id : f.ToString()))),
                            new JProperty("reasoning", d.Value<string>("reasoning") ?? ""));
                        GraphAssembler.UpsertFact(conn, tx, personId, d.Value<string>("category") ?? "misc", text,
                            GraphAssembler.ConfidenceValue(d.Value<string>("confidence")), "derived",
                            new JArray(), now, "detective", chain.ToString(Newtonsoft.Json.Formatting.None));
                    }
                }

                // Contradictions: loser marked superseded (kept for the audit trail).
                if (result["contradictions"] is JArray contradictions)
                {
                    foreach (var c in contradictions.OfType<JObject>())
                    {
                        string? winner = c.Value<string>("winner");
                        if (winner is not ("a" or "b")) continue;
                        string loserRef = winner == "a" ? c.Value<string>("b") ?? "" : c.Value<string>("a") ?? "";
                        if (!factIdByRef.TryGetValue(loserRef, out var loserId)) continue;
                        using var upd = conn.CreateCommand();
                        upd.Transaction = tx;
                        upd.CommandText = @"UPDATE person_facts SET status='superseded', updated_at=$now,
                                derived_from_json=COALESCE(derived_from_json,'') || $note
                            WHERE fact_id=$id AND status='active'";
                        upd.Parameters.AddWithValue("$now", now);
                        upd.Parameters.AddWithValue("$note", $"\n[superseded: {Truncate(c.Value<string>("reason") ?? "", 200)}]");
                        upd.Parameters.AddWithValue("$id", loserId);
                        upd.ExecuteNonQuery();
                    }
                }

                // Hypotheses: insert new (dedupe on statement), keep ledger small.
                if (result["hypotheses"] is JArray hyps)
                {
                    foreach (var h in hyps.OfType<JObject>().Take(8))
                    {
                        string statement = (h.Value<string>("statement") ?? "").Trim();
                        if (statement.Length < 8) continue;
                        using (var check = conn.CreateCommand())
                        {
                            check.Transaction = tx;
                            check.CommandText = "SELECT 1 FROM hypotheses WHERE person_id=$p AND statement=$s LIMIT 1";
                            check.Parameters.AddWithValue("$p", personId);
                            check.Parameters.AddWithValue("$s", statement);
                            if (check.ExecuteScalar() != null) continue;
                        }
                        using var ins = conn.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText = @"INSERT INTO hypotheses(hypothesis_id, person_id, statement, rationale, confirm_query, created_at)
                            VALUES($id,$p,$s,$r,$q,$now)";
                        ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                        ins.Parameters.AddWithValue("$p", personId);
                        ins.Parameters.AddWithValue("$s", statement);
                        ins.Parameters.AddWithValue("$r", (object?)h.Value<string>("rationale") ?? DBNull.Value);
                        ins.Parameters.AddWithValue("$q", (object?)h.Value<string>("confirm_query") ?? DBNull.Value);
                        ins.Parameters.AddWithValue("$now", now);
                        ins.ExecuteNonQuery();
                    }
                }

                // Open questions: replace the open set with the model's current view.
                if (result["open_questions"] is JArray oqs)
                {
                    using (var clear = conn.CreateCommand())
                    {
                        clear.Transaction = tx;
                        clear.CommandText = "UPDATE open_questions SET status='answered', answered_at=$now WHERE person_id=$p AND status='open'";
                        clear.Parameters.AddWithValue("$p", personId);
                        clear.Parameters.AddWithValue("$now", now);
                        clear.ExecuteNonQuery();
                    }
                    foreach (var q in oqs.OfType<JObject>().Take(12))
                    {
                        string slot = (q.Value<string>("slot") ?? "misc").Trim();
                        string question = (q.Value<string>("question") ?? "").Trim();
                        if (question.Length < 5) continue;
                        using var ins = conn.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText = @"INSERT INTO open_questions(question_id, person_id, slot, question, created_at)
                            VALUES($id,$p,$s,$q,$now)";
                        ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                        ins.Parameters.AddWithValue("$p", personId);
                        ins.Parameters.AddWithValue("$s", slot);
                        ins.Parameters.AddWithValue("$q", question);
                        ins.Parameters.AddWithValue("$now", now);
                        ins.ExecuteNonQuery();
                    }
                }

                // Completeness → person_statistics row so it rides existing payload paths.
                if (result["completeness"] is JObject comp)
                {
                    int known = 0, partial = 0;
                    foreach (var slot in CompletenessSlots)
                    {
                        string v = comp.Value<string>(slot) ?? "unknown";
                        if (v == "known") known++;
                        else if (v == "partial") partial++;
                    }
                    double score = (known + 0.5 * partial) / CompletenessSlots.Length;
                    var payload = new JObject(
                        new JProperty("completeness_score", Math.Round(score, 3)),
                        new JProperty("slots", comp),
                        new JProperty("computed_at", DateTime.UtcNow.ToString("o")));
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"INSERT INTO person_statistics(person_id, module_name, module_version, computed_at, payload_json)
                        VALUES($p,'deduction_summary',1,$t,$j)
                        ON CONFLICT(person_id, module_name) DO UPDATE SET
                            computed_at=excluded.computed_at, payload_json=excluded.payload_json";
                    ins.Parameters.AddWithValue("$p", personId);
                    ins.Parameters.AddWithValue("$t", now);
                    ins.Parameters.AddWithValue("$j", payload.ToString(Newtonsoft.Json.Formatting.None));
                    ins.ExecuteNonQuery();
                }

                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
