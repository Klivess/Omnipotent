using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Deduction stage 2: folds raw extraction window outputs into the knowledge graph —
    /// person_facts (with reconciliation + context trust), entities, typed relationship
    /// edges — and applies category-aware confidence decay. Pure replay over
    /// extraction_results, so the whole graph can be rebuilt without LLM calls.
    /// </summary>
    public class GraphAssembler
    {
        // Facts in these categories never decay: a 6-year-old DOB statement is still
        // perfect evidence. Mutable state (current city, school, job…) decays when
        // not re-evidenced.
        private static readonly HashSet<string> ImmutableCategories = new(StringComparer.OrdinalIgnoreCase)
        { "name", "age", "family", "misc" };
        private const int MutableDecayHorizonDays = 240;
        private const double DecayFactorPerRun = 0.93;
        private const double DecayedThreshold = 0.22;

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly SemaphoreSlim runLock = new(1, 1);

        public GraphAssembler(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<string> RunAsync(CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return "graph assembly already running";
            try
            {
                int windows = 0, facts = 0, entities = 0, rels = 0;
                while (!ct.IsCancellationRequested)
                {
                    var batch = LoadUnappliedWindows(100);
                    if (batch.Count == 0) break;
                    foreach (var w in batch)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var (f, e, r) = await ApplyWindowAsync(w, ct);
                            facts += f; entities += e; rels += r;
                        }
                        catch (Exception ex)
                        {
                            _ = service.ServiceLogError(ex, $"[Omniscience] Graph apply failed for window {w.WindowId}");
                        }
                        await MarkApplied(w.WindowId, ct);
                        windows++;
                    }
                }

                int decayed = await ApplyConfidenceDecayAsync(ct);
                int merges = await SuggestEntityMergesAsync(ct);
                string summary = $"{windows} windows applied, {facts} facts, {entities} entities, {rels} relationship signals, {decayed} facts decayed, {merges} merge suggestions";
                await service.ServiceLog($"[Omniscience] Graph assembly: {summary}");
                return summary;
            }
            finally { runLock.Release(); }
        }

        private class PendingWindow
        {
            public string WindowId = "";
            public string ConversationId = "";
            public JObject Payload = new();
        }

        private List<PendingWindow> LoadUnappliedWindows(int limit)
        {
            var list = new List<PendingWindow>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT window_id, conversation_id, payload_json FROM extraction_results
                WHERE graph_applied=0 AND status='ok' AND payload_json IS NOT NULL
                ORDER BY extracted_at ASC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    list.Add(new PendingWindow
                    {
                        WindowId = r.GetString(0),
                        ConversationId = r.GetString(1),
                        Payload = JObject.Parse(r.GetString(2)),
                    });
                }
                catch { /* unparseable payload: will be marked applied below via empty handling */ }
            }
            return list;
        }

        private async Task<(int Facts, int Entities, int Rels)> ApplyWindowAsync(PendingWindow w, CancellationToken ct)
        {
            var participants = w.Payload["participants"] as JObject;
            var extraction = w.Payload["extraction"] as JObject;
            if (participants == null || extraction == null) return (0, 0, 0);

            // letter → person_id (via identity), plus context trust from the conversation kind.
            var letterToPerson = new Dictionary<string, string>();
            string sourceContext = "server";
            using (var conn = db.Open())
            {
                foreach (var p in participants.Properties())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT person_id FROM platform_identities WHERE identity_id=$i";
                    cmd.Parameters.AddWithValue("$i", p.Value.ToString());
                    if (cmd.ExecuteScalar() is string pid) letterToPerson[p.Name] = pid;
                }
                using var kindCmd = conn.CreateCommand();
                kindCmd.CommandText = "SELECT kind FROM conversations WHERE conversation_id=$c";
                kindCmd.Parameters.AddWithValue("$c", w.ConversationId);
                sourceContext = kindCmd.ExecuteScalar() as string switch
                {
                    "dm" => "dm",
                    "group_dm" => "group_dm",
                    _ => "server",
                };
            }

            // Evidence numbers → message ids.
            var msgMap = new Dictionary<int, (string Id, long At)>();
            if (w.Payload["message_ids"] is JObject mids)
                foreach (var p in mids.Properties())
                    if (int.TryParse(p.Name, out int n) && p.Value is JObject o)
                        msgMap[n] = (o.Value<string>("id") ?? "", o.Value<long?>("at") ?? 0);

            (JArray ids, long at) ResolveEvidence(JToken? evidence)
            {
                var arr = new JArray();
                long newest = 0;
                if (evidence is JArray nums)
                    foreach (var t in nums)
                        if (t.Type == JTokenType.Integer && msgMap.TryGetValue(t.Value<int>(), out var m))
                        {
                            arr.Add(m.Id);
                            newest = Math.Max(newest, m.At);
                        }
                return (arr, newest == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : newest);
            }

            int factCount = 0, entityCount = 0, relCount = 0;
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();

                if (extraction["facts"] is JArray factArr)
                {
                    foreach (var f in factArr.OfType<JObject>())
                    {
                        string? subject = f.Value<string>("subject");
                        string text = (f.Value<string>("fact") ?? "").Trim();
                        if (subject == null || text.Length < 5 || !letterToPerson.TryGetValue(subject.Trim(), out var personId)) continue;
                        var (evidence, evidenceAt) = ResolveEvidence(f["evidence"]);
                        UpsertFact(conn, tx, personId, f.Value<string>("category") ?? "misc", text,
                            ConfidenceValue(f.Value<string>("confidence")), sourceContext, evidence, evidenceAt, "extraction", null);
                        factCount++;
                    }
                }

                if (extraction["temporal_refs"] is JArray temporal)
                {
                    foreach (var t in temporal.OfType<JObject>())
                    {
                        string? subject = t.Value<string>("subject");
                        string resolved = (t.Value<string>("resolved") ?? "").Trim();
                        if (subject == null || resolved.Length < 4 || !letterToPerson.TryGetValue(subject.Trim(), out var personId)) continue;
                        var (evidence, evidenceAt) = ResolveEvidence(t["evidence"]);
                        string category = resolved.Contains("birthday", StringComparison.OrdinalIgnoreCase) ||
                                          resolved.Contains("born", StringComparison.OrdinalIgnoreCase) ? "age" : "schedule";
                        UpsertFact(conn, tx, personId, category, resolved, 0.55, sourceContext, evidence, evidenceAt, "temporal",
                            t.Value<string>("statement"));
                        factCount++;
                    }
                }

                if (extraction["entity_mentions"] is JArray mentions)
                {
                    foreach (var e in mentions.OfType<JObject>())
                    {
                        string? by = e.Value<string>("by");
                        string name = (e.Value<string>("name") ?? "").Trim();
                        if (by == null || name.Length is < 2 or > 60 || !letterToPerson.TryGetValue(by.Trim(), out var ownerId)) continue;
                        var (_, evidenceAt) = ResolveEvidence(e["evidence"]);
                        UpsertEntity(conn, tx, ownerId, e.Value<string>("kind") ?? "person", name,
                            e.Value<string>("descriptor"), evidenceAt);
                        entityCount++;
                    }
                }

                if (extraction["relationship_signals"] is JArray signals)
                {
                    foreach (var s in signals.OfType<JObject>())
                    {
                        string? a = s.Value<string>("a"), b = s.Value<string>("b");
                        string signal = (s.Value<string>("signal") ?? "").Trim();
                        if (a == null || b == null || signal.Length == 0) continue;
                        if (!letterToPerson.TryGetValue(a.Trim(), out var personA) ||
                            !letterToPerson.TryGetValue(b.Trim(), out var personB) || personA == personB) continue;
                        var (evidence, evidenceAt) = ResolveEvidence(s["evidence"]);
                        UpsertRelationshipSignal(conn, tx, personA, personB, signal, evidence, evidenceAt);
                        relCount++;
                    }
                }

                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
            return (factCount, entityCount, relCount);
        }

        internal static double ConfidenceValue(string? label) => label?.ToLowerInvariant() switch
        {
            "high" => 0.85,
            "medium" => 0.6,
            _ => 0.35,
        };

        // Reconciliation: identical (normalised) fact text bumps evidence + confidence;
        // anything else inserts as a new active fact. Cross-fact contradiction detection
        // is the detective pass's job (it has the context to judge).
        internal static void UpsertFact(SqliteConnection conn, SqliteTransaction tx, string personId, string category,
            string text, double confidence, string sourceContext, JArray evidence, long evidenceAt, string extractedBy,
            string? derivedFrom)
        {
            string norm = NormaliseFact(text);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string? existingId = null;
            double existingConfidence = 0;
            string? existingEvidence = null;
            // Text comparison happens in C# (SQLite has no fuzzy match).
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = @"SELECT fact_id, fact_text, confidence, evidence_message_ids_json FROM person_facts
                    WHERE person_id=$p AND category=$c AND status='active'";
                get.Parameters.AddWithValue("$p", personId);
                get.Parameters.AddWithValue("$c", category);
                using var r = get.ExecuteReader();
                while (r.Read())
                {
                    if (NormaliseFact(r.GetString(1)) != norm) continue;
                    existingId = r.GetString(0);
                    existingConfidence = r.GetDouble(2);
                    existingEvidence = r.IsDBNull(3) ? null : r.GetString(3);
                    break;
                }
            }

            if (existingId != null)
            {
                // Re-evidenced: bump confidence toward 1, merge evidence ids.
                var merged = new HashSet<string>(evidence.Select(e => e.ToString()));
                if (existingEvidence != null)
                    try { foreach (var e in JArray.Parse(existingEvidence)) merged.Add(e.ToString()); } catch { }
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"UPDATE person_facts SET
                        confidence=$conf, last_evidence_at=MAX(COALESCE(last_evidence_at,0), $at),
                        evidence_message_ids_json=$ev, updated_at=$now
                    WHERE fact_id=$id";
                upd.Parameters.AddWithValue("$conf", Math.Min(0.98, Math.Max(existingConfidence, confidence) + 0.07));
                upd.Parameters.AddWithValue("$at", evidenceAt);
                upd.Parameters.AddWithValue("$ev", new JArray(merged.Take(40)).ToString(Newtonsoft.Json.Formatting.None));
                upd.Parameters.AddWithValue("$now", now);
                upd.Parameters.AddWithValue("$id", existingId);
                upd.ExecuteNonQuery();
                return;
            }

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO person_facts(fact_id, person_id, category, fact_text, confidence, status,
                    source_context, first_evidence_at, last_evidence_at, evidence_message_ids_json, derived_from_json,
                    extracted_by, created_at, updated_at)
                VALUES($id,$p,$c,$t,$conf,'active',$ctx,$at,$at,$ev,$df,$by,$now,$now)";
            ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            ins.Parameters.AddWithValue("$p", personId);
            ins.Parameters.AddWithValue("$c", category);
            ins.Parameters.AddWithValue("$t", text);
            ins.Parameters.AddWithValue("$conf", confidence);
            ins.Parameters.AddWithValue("$ctx", sourceContext);
            ins.Parameters.AddWithValue("$at", evidenceAt);
            ins.Parameters.AddWithValue("$ev", evidence.ToString(Newtonsoft.Json.Formatting.None));
            ins.Parameters.AddWithValue("$df", (object?)derivedFrom ?? DBNull.Value);
            ins.Parameters.AddWithValue("$by", extractedBy);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }

        private static string NormaliseFact(string s) =>
            new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

        private static void UpsertEntity(SqliteConnection conn, SqliteTransaction tx, string ownerPersonId,
            string kind, string name, string? descriptor, long seenAt)
        {
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = @"SELECT entity_id, descriptor FROM entities
                    WHERE owner_person_id=$o AND kind=$k AND canonical_name=$n COLLATE NOCASE";
                get.Parameters.AddWithValue("$o", ownerPersonId);
                get.Parameters.AddWithValue("$k", kind);
                get.Parameters.AddWithValue("$n", name);
                using var r = get.ExecuteReader();
                if (r.Read())
                {
                    string id = r.GetString(0);
                    string? existingDesc = r.IsDBNull(1) ? null : r.GetString(1);
                    string? newDesc = existingDesc;
                    if (!string.IsNullOrWhiteSpace(descriptor) &&
                        (existingDesc == null || !existingDesc.Contains(descriptor, StringComparison.OrdinalIgnoreCase)))
                        newDesc = existingDesc == null ? descriptor : Truncate(existingDesc + "; " + descriptor, 400);
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE entities SET mention_count=mention_count+1, last_seen=MAX(COALESCE(last_seen,0),$t), descriptor=$d
                        WHERE entity_id=$id";
                    upd.Parameters.AddWithValue("$t", seenAt);
                    upd.Parameters.AddWithValue("$d", (object?)newDesc ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$id", id);
                    upd.ExecuteNonQuery();
                    return;
                }
            }
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO entities(entity_id, owner_person_id, kind, canonical_name, descriptor, first_seen, last_seen)
                VALUES($id,$o,$k,$n,$d,$t,$t)";
            ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            ins.Parameters.AddWithValue("$o", ownerPersonId);
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$d", (object?)descriptor ?? DBNull.Value);
            ins.Parameters.AddWithValue("$t", seenAt);
            ins.ExecuteNonQuery();
        }

        private static void UpsertRelationshipSignal(SqliteConnection conn, SqliteTransaction tx,
            string ownerPersonId, string relatedPersonId, string relType, JArray evidence, long evidenceAt)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = @"SELECT rel_id, evidence_count, confidence FROM entity_relationships
                    WHERE owner_person_id=$o AND related_person_id=$r AND rel_type=$t AND status='active'";
                get.Parameters.AddWithValue("$o", ownerPersonId);
                get.Parameters.AddWithValue("$r", relatedPersonId);
                get.Parameters.AddWithValue("$t", relType);
                using var rr = get.ExecuteReader();
                if (rr.Read())
                {
                    long relId = rr.GetInt64(0);
                    int count = rr.GetInt32(1) + 1;
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE entity_relationships SET evidence_count=$n,
                            confidence=$conf, valid_to=NULL, updated_at=$now
                        WHERE rel_id=$id";
                    upd.Parameters.AddWithValue("$n", count);
                    upd.Parameters.AddWithValue("$conf", Math.Min(0.95, 0.3 + 0.08 * count));
                    upd.Parameters.AddWithValue("$now", now);
                    upd.Parameters.AddWithValue("$id", relId);
                    upd.ExecuteNonQuery();
                    return;
                }
            }
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO entity_relationships(owner_person_id, related_person_id, rel_type,
                    confidence, evidence_count, valid_from, evidence_json, created_at, updated_at)
                VALUES($o,$r,$t,0.3,1,$from,$ev,$now,$now)";
            ins.Parameters.AddWithValue("$o", ownerPersonId);
            ins.Parameters.AddWithValue("$r", relatedPersonId);
            ins.Parameters.AddWithValue("$t", relType);
            ins.Parameters.AddWithValue("$from", evidenceAt);
            ins.Parameters.AddWithValue("$ev", evidence.ToString(Newtonsoft.Json.Formatting.None));
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }

        private async Task MarkApplied(string windowId, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE extraction_results SET graph_applied=1 WHERE window_id=$id";
                cmd.Parameters.AddWithValue("$id", windowId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        // ── Category-aware confidence decay: mutable facts fade unless re-evidenced ──
        private async Task<int> ApplyConfidenceDecayAsync(CancellationToken ct)
        {
            long horizon = DateTimeOffset.UtcNow.AddDays(-MutableDecayHorizonDays).ToUnixTimeMilliseconds();
            string immutableList = string.Join(",", ImmutableCategories.Select(c => $"'{c}'"));
            int decayed = 0;
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using (var fade = conn.CreateCommand())
                {
                    fade.CommandText = $@"UPDATE person_facts SET confidence = confidence * {DecayFactorPerRun}
                        WHERE status='active' AND category NOT IN ({immutableList})
                          AND COALESCE(last_evidence_at, 0) < $h";
                    fade.Parameters.AddWithValue("$h", horizon);
                    fade.ExecuteNonQuery();
                }
                using (var expire = conn.CreateCommand())
                {
                    expire.CommandText = $@"UPDATE person_facts SET status='decayed'
                        WHERE status='active' AND category NOT IN ({immutableList}) AND confidence < {DecayedThreshold}";
                    decayed = expire.ExecuteNonQuery();
                }
            }
            finally { db.WriteLock.Release(); }
            return decayed;
        }

        // ── Entity coreference candidates: same kind + same/contained names → review queue ──
        private async Task<int> SuggestEntityMergesAsync(CancellationToken ct)
        {
            var suggestions = new List<(string Owner, string A, string B, double Score, string Reason)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT e1.owner_person_id, e1.entity_id, e2.entity_id, e1.canonical_name, e2.canonical_name
                    FROM entities e1
                    JOIN entities e2 ON e1.owner_person_id = e2.owner_person_id
                        AND e1.kind = e2.kind AND e1.entity_id < e2.entity_id
                    WHERE NOT EXISTS (SELECT 1 FROM entity_merge_suggestions s
                                      WHERE s.entity_id_a=e1.entity_id AND s.entity_id_b=e2.entity_id)";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string nameA = r.GetString(3), nameB = r.GetString(4);
                    string la = nameA.ToLowerInvariant(), lb = nameB.ToLowerInvariant();
                    if (la == lb) continue; // exact matches already merged by upsert
                    double score = 0;
                    string reason = "";
                    if (la.Contains(lb) || lb.Contains(la)) { score = 0.8; reason = $"'{nameA}' contains/extends '{nameB}'"; }
                    else if (la.Split(' ')[0] == lb.Split(' ')[0] && la.Split(' ')[0].Length >= 4)
                    { score = 0.6; reason = $"'{nameA}' and '{nameB}' share first token"; }
                    if (score > 0)
                        suggestions.Add((r.GetString(0), r.GetString(1), r.GetString(2), score, reason));
                }
            }
            if (suggestions.Count == 0) return 0;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var s in suggestions.Take(100))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO entity_merge_suggestions(suggestion_id, owner_person_id, entity_id_a, entity_id_b, score, reason, created_at)
                        VALUES($id,$o,$a,$b,$s,$r,$t)";
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                    cmd.Parameters.AddWithValue("$o", s.Owner);
                    cmd.Parameters.AddWithValue("$a", s.A);
                    cmd.Parameters.AddWithValue("$b", s.B);
                    cmd.Parameters.AddWithValue("$s", s.Score);
                    cmd.Parameters.AddWithValue("$r", s.Reason);
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
            return suggestions.Count;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
