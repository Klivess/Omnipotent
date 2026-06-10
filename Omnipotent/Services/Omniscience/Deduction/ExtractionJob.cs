using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Deduction Engine stage 1: reads every conversation involving Tracked persons in
    /// overlapping ~40-message windows and extracts facts, name usages, Q&amp;A pairs,
    /// entity mentions, relationship signals and temporal references via free-tier LLM
    /// calls. Fully incremental: a per-conversation watermark means only new messages
    /// are processed after the first historical pass. A local density pre-filter spends
    /// the LLM budget on information-rich windows; meme/link-only stretches are skipped.
    /// Raw window outputs persist in extraction_results so stage 2 (knowledge graph)
    /// can be replayed without re-calling the LLM. Stimulus→reply pairs are harvested
    /// deterministically for replica training.
    /// </summary>
    public class ExtractionJob
    {
        private const int WindowSize = 40;
        private const int WindowOverlap = 10;
        private const int MinPartialWindow = 15;
        private const int DensityThreshold = 6;
        private static readonly TimeSpan IdleFlushAge = TimeSpan.FromDays(3);
        private static readonly TimeSpan StimulusAdjacency = TimeSpan.FromMinutes(3);

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly SemaphoreSlim passLock = new(1, 1);

        public DateTime? LastPassAt { get; private set; }
        public string LastPassSummary { get; private set; } = "never run";

        public ExtractionJob(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        /// <summary>
        /// One extraction pass: walks tracked conversations, spends up to the configured
        /// LLM-call budget, returns a summary. Safe to call nightly + on demand.
        /// </summary>
        public async Task<string> RunPassAsync(CancellationToken ct)
        {
            if (!await passLock.WaitAsync(0, ct)) return "extraction already running";
            try
            {
                int budget = Math.Clamp(await service.GetIntOmniSetting("OmniscienceExtractionNightlyCalls", 150), 1, 5000);
                int callsUsed = 0, windowsExtracted = 0, windowsSkipped = 0, pairsHarvested = 0, convsTouched = 0;

                var conversations = LoadTrackedConversations();
                foreach (var convId in conversations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (callsUsed >= budget) break;

                    var fresh = LoadFreshMessages(convId);
                    if (fresh.Count == 0) continue;
                    convsTouched++;

                    pairsHarvested += await HarvestStimulusReplyPairsAsync(convId, fresh, ct);

                    var (label, participants) = BuildContext(convId, fresh);
                    bool idle = DateTime.UtcNow - fresh[^1].SentAt > IdleFlushAge;

                    int index = 0;
                    long watermark = 0;
                    bool advanced = false;
                    while (index < fresh.Count && callsUsed < budget)
                    {
                        ct.ThrowIfCancellationRequested();
                        int remaining = fresh.Count - index;
                        if (remaining < WindowSize && !(idle || remaining >= MinPartialWindow))
                            break; // wait for more messages before forming a runt window

                        var window = fresh.Skip(index).Take(WindowSize).ToList();
                        NumberWindow(window);

                        if (DensityScore(window) < DensityThreshold)
                        {
                            await RecordWindow(convId, window, "skipped_low_density", null, null, ct);
                            windowsSkipped++;
                        }
                        else
                        {
                            callsUsed++;
                            bool ok = await ExtractWindowAsync(convId, label, participants, window, ct);
                            if (ok) windowsExtracted++;
                        }

                        watermark = new DateTimeOffset(window[^1].SentAt).ToUnixTimeMilliseconds();
                        advanced = true;
                        index += window.Count >= WindowSize ? WindowSize - WindowOverlap : window.Count;
                    }

                    if (advanced) await SaveCursor(convId, watermark, ct);
                }

                LastPassAt = DateTime.UtcNow;
                LastPassSummary = $"{convsTouched} conversations, {windowsExtracted} windows extracted, {windowsSkipped} low-density skipped, {callsUsed}/{budget} LLM calls, {pairsHarvested} stimulus pairs";
                await service.ServiceLog($"[Omniscience] Extraction pass: {LastPassSummary}");
                return LastPassSummary;
            }
            finally { passLock.Release(); }
        }

        // ── Conversation + message loading ──

        private List<string> LoadTrackedConversations()
        {
            var list = new List<string>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            // Conversations where a Tracked person speaks. Self-disclosure-flagged
            // conversations jump the queue (priority from extraction_cursors), then
            // most recently active first.
            cmd.CommandText = @"SELECT m.conversation_id, MAX(m.sent_at) AS latest,
                       COALESCE((SELECT priority FROM extraction_cursors ec WHERE ec.conversation_id = m.conversation_id), 0) AS prio
                FROM messages m
                JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                JOIN persons p ON p.person_id = pi.person_id
                WHERE p.tier = 'tracked'
                GROUP BY m.conversation_id
                ORDER BY prio DESC, latest DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }

        /// <summary>
        /// Single-message self-disclosure check: high personal-info density in one
        /// message ("i'm 17 btw, my brother jake…") fast-tracks its conversation to the
        /// front of the next extraction pass.
        /// </summary>
        public static bool IsSelfDisclosure(string? content)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length < 15) return false;
            return DensitySignals.Matches(content).Count >= 3;
        }

        /// <summary>Flags a conversation for priority extraction (idempotent).</summary>
        public async Task MarkConversationPriorityAsync(string platform, string channelId)
        {
            // Conversations are keyed by (platform, channel_id); resolve the actual id.
            string? convId;
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id FROM conversations WHERE platform=$p AND channel_id=$c";
                cmd.Parameters.AddWithValue("$p", platform);
                cmd.Parameters.AddWithValue("$c", channelId);
                convId = cmd.ExecuteScalar() as string;
            }
            if (convId == null) return;

            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO extraction_cursors(conversation_id, last_extracted_sent_at, priority)
                    VALUES($c, 0, 1)
                    ON CONFLICT(conversation_id) DO UPDATE SET priority=1";
                cmd.Parameters.AddWithValue("$c", convId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private long LoadWatermark(string convId)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_extracted_sent_at FROM extraction_cursors WHERE conversation_id=$c";
            cmd.Parameters.AddWithValue("$c", convId);
            return cmd.ExecuteScalar() is long l ? l : 0;
        }

        private List<WindowMessage> LoadFreshMessages(string convId)
        {
            long watermark = LoadWatermark(convId);
            var list = new List<WindowMessage>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT m.message_id, m.author_identity_id, m.sent_at, m.content, m.reply_to_message_id,
                       COALESCE(p.display_name, pi.platform_username, m.author_identity_id)
                FROM messages m
                LEFT JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                LEFT JOIN persons p ON p.person_id = pi.person_id
                WHERE m.conversation_id=$c AND m.sent_at > $w AND m.content IS NOT NULL AND length(m.content) > 0
                ORDER BY m.sent_at ASC
                LIMIT 4000";
            cmd.Parameters.AddWithValue("$c", convId);
            cmd.Parameters.AddWithValue("$w", watermark);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new WindowMessage
                {
                    MessageId = r.GetString(0),
                    AuthorIdentityId = r.GetString(1),
                    SentAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime,
                    Content = r.IsDBNull(3) ? "" : r.GetString(3),
                    ReplyToMessageId = r.IsDBNull(4) ? null : r.GetString(4),
                    DisplayName = r.IsDBNull(5) ? "" : r.GetString(5),
                });
            }
            return list;
        }

        private (string Label, Dictionary<string, string> IdentityNames) BuildContext(string convId, List<WindowMessage> msgs)
        {
            string label = convId;
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT kind, guild_name, title FROM conversations WHERE conversation_id=$c";
                cmd.Parameters.AddWithValue("$c", convId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    string kind = r.IsDBNull(0) ? "" : r.GetString(0);
                    string? guild = r.IsDBNull(1) ? null : r.GetString(1);
                    string? title = r.IsDBNull(2) ? null : r.GetString(2);
                    label = kind switch
                    {
                        "dm" => "private DM (1-on-1 — disclosures here are high-trust)",
                        "group_dm" => $"group DM '{title ?? "untitled"}'",
                        _ => $"public server channel {guild ?? "?"}#{title ?? "?"} (public-facing persona)",
                    };
                }
            }
            var names = msgs.GroupBy(m => m.AuthorIdentityId)
                            .ToDictionary(g => g.Key, g => g.First().DisplayName);
            return (label, names);
        }

        // Assigns #numbers, participant letters, and in-window reply links.
        private static void NumberWindow(List<WindowMessage> window)
        {
            var letters = new Dictionary<string, string>();
            var numberByMessageId = new Dictionary<string, int>();
            for (int i = 0; i < window.Count; i++)
            {
                var m = window[i];
                m.Number = i + 1;
                numberByMessageId[m.MessageId] = m.Number;
                if (!letters.TryGetValue(m.AuthorIdentityId, out var letter))
                {
                    letter = LetterFor(letters.Count);
                    letters[m.AuthorIdentityId] = letter;
                }
                m.ParticipantLetter = letter;
            }
            foreach (var m in window)
            {
                m.ReplyToNumber = m.ReplyToMessageId != null && numberByMessageId.TryGetValue(m.ReplyToMessageId, out int n)
                    ? n : null;
            }
        }

        private static string LetterFor(int idx)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            return idx < alphabet.Length ? alphabet[idx].ToString() : "P" + idx;
        }

        // ── Density pre-filter: spend the LLM budget where the information is ──
        private static readonly Regex DensitySignals = new(
            @"\bmy\s+\w|\bi'?m\b|\bi\s+am\b|\bi\s+live\b|\bi\s+work\b|\bborn\b|\bbirthday\b|\bschool\b|\buni\b|\bcollege\b|\bjob\b|\bmum\b|\bmom\b|\bdad\b|\bbrother\b|\bsister\b|\bgirlfriend\b|\bboyfriend\b|\bgf\b|\bbf\b|\bage\b|\byears?\s+old\b|\bmoved?\b|\bexams?\b|\b(19|20)\d\d\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static int DensityScore(List<WindowMessage> window)
        {
            int score = 0;
            foreach (var m in window)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                score += DensitySignals.Matches(m.Content).Count;
                if (m.Content.Contains('?')) score++;
                if (m.ReplyToNumber.HasValue) score++; // threaded exchanges carry Q&A
            }
            return score;
        }

        // ── LLM extraction for one window ──

        private async Task<bool> ExtractWindowAsync(string convId, string label,
            Dictionary<string, string> identityNames, List<WindowMessage> window, CancellationToken ct)
        {
            // letter → identity map for this window
            var letterToIdentity = window
                .GroupBy(m => m.ParticipantLetter)
                .ToDictionary(g => g.Key, g => g.First().AuthorIdentityId);
            var participants = letterToIdentity
                .Select(kv => (kv.Key, identityNames.TryGetValue(kv.Value, out var n) ? n : kv.Value))
                .OrderBy(p => p.Key)
                .ToList();
            var numberToMessage = window.ToDictionary(m => m.Number);

            string prompt = ExtractionPromptBuilder.BuildUserPrompt(label, participants, window);

            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0) return false;
            var llm = (KliveLLM.KliveLLM)llms[0];

            KliveLLM.KliveLLM.KliveLLMResponse resp;
            try
            {
                resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 1800,
                    systemPrompt: ExtractionPromptBuilder.SystemPrompt, useFreeModel: true);
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Extraction LLM call threw");
                await RecordWindow(convId, window, "failed", null, null, ct);
                return false;
            }
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
            {
                await RecordWindow(convId, window, "failed", null, resp.ErrorMessage, ct);
                return false;
            }

            JObject payload;
            try
            {
                string raw = resp.Response;
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start < 0 || end <= start) throw new FormatException("no JSON object in response");
                payload = JObject.Parse(raw[start..(end + 1)]);
            }
            catch (Exception)
            {
                await RecordWindow(convId, window, "failed", resp.Response, "unparseable JSON", ct);
                return false;
            }

            // Persist the letter→identity and number→message maps alongside the raw output
            // so stage 2 (graph assembly) can replay windows without re-calling the LLM.
            var wrapped = new JObject(
                new JProperty("participants", new JObject(letterToIdentity.Select(kv => new JProperty(kv.Key, kv.Value)))),
                new JProperty("message_ids", new JObject(numberToMessage.Select(kv =>
                    new JProperty(kv.Key.ToString(), new JObject(
                        new JProperty("id", kv.Value.MessageId),
                        new JProperty("at", new DateTimeOffset(kv.Value.SentAt).ToUnixTimeMilliseconds())))))),
                new JProperty("extraction", payload));
            await RecordWindow(convId, window, "ok", wrapped.ToString(Newtonsoft.Json.Formatting.None), null, ct);
            await PersistStructuredRows(convId, payload, letterToIdentity, numberToMessage, ct);
            return true;
        }

        private async Task PersistStructuredRows(string convId, JObject payload,
            Dictionary<string, string> letterToIdentity, Dictionary<int, WindowMessage> numberToMessage, CancellationToken ct)
        {
            string? Identity(string? letter) =>
                letter != null && letterToIdentity.TryGetValue(letter.Trim(), out var id) ? id : null;
            WindowMessage? Msg(JToken? numTok)
            {
                int? n = numTok?.Type == JTokenType.Integer ? (int?)numTok.Value<int>() : null;
                return n.HasValue && numberToMessage.TryGetValue(n.Value, out var m) ? m : null;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();

                if (payload["qa_pairs"] is JArray qas)
                {
                    foreach (var qa in qas.OfType<JObject>())
                    {
                        string question = qa.Value<string>("question") ?? "";
                        string answer = qa.Value<string>("answer") ?? "";
                        if (question.Length == 0 || answer.Length == 0) continue;
                        var qMsg = Msg(qa["question_msg"]);
                        var aMsg = Msg(qa["answer_msg"]);
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO qa_pairs(asker_identity_id, answerer_identity_id, question, answer, category,
                                question_message_id, answer_message_id, conversation_id, occurred_at, extracted_at)
                            VALUES($ask,$ans,$q,$a,$cat,$qm,$am,$c,$t,$now)";
                        cmd.Parameters.AddWithValue("$ask", (object?)Identity(qa.Value<string>("asker")) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$ans", (object?)Identity(qa.Value<string>("answerer")) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$q", question);
                        cmd.Parameters.AddWithValue("$a", answer);
                        cmd.Parameters.AddWithValue("$cat", (object?)qa.Value<string>("category") ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$qm", (object?)qMsg?.MessageId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$am", (object?)aMsg?.MessageId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$c", convId);
                        cmd.Parameters.AddWithValue("$t", aMsg != null ? new DateTimeOffset(aMsg.SentAt).ToUnixTimeMilliseconds() : now);
                        cmd.Parameters.AddWithValue("$now", now);
                        cmd.ExecuteNonQuery();
                    }
                }

                if (payload["name_usages"] is JArray usages)
                {
                    foreach (var u in usages.OfType<JObject>())
                    {
                        string name = (u.Value<string>("name") ?? "").Trim();
                        if (name.Length is < 2 or > 32) continue;
                        string type = (u.Value<string>("type") ?? "third_person").Trim();
                        var evMsg = Msg((u["evidence"] as JArray)?.FirstOrDefault());
                        string? targetIdentity = Identity(u.Value<string>("target"));
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO name_usages(speaker_identity_id, name_used, usage_type, target_identity_id,
                                target_hint, evidence_message_id, conversation_id, occurred_at, extracted_at)
                            VALUES($sp,$n,$ty,$tg,$hint,$ev,$c,$t,$now)";
                        cmd.Parameters.AddWithValue("$sp", (object?)Identity(u.Value<string>("speaker")) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$n", name.ToLowerInvariant());
                        cmd.Parameters.AddWithValue("$ty", type);
                        cmd.Parameters.AddWithValue("$tg", (object?)targetIdentity ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$hint", targetIdentity == null ? (object)(u.Value<string>("target") ?? "") : DBNull.Value);
                        cmd.Parameters.AddWithValue("$ev", (object?)evMsg?.MessageId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$c", convId);
                        cmd.Parameters.AddWithValue("$t", evMsg != null ? new DateTimeOffset(evMsg.SentAt).ToUnixTimeMilliseconds() : now);
                        cmd.Parameters.AddWithValue("$now", now);
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task RecordWindow(string convId, List<WindowMessage> window, string status, string? payloadJson, string? error, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO extraction_results(window_id, conversation_id, window_start_sent_at, window_end_sent_at,
                        message_count, extracted_at, model_used, status, payload_json)
                    VALUES($id,$c,$s,$e,$n,$t,$m,$st,$p)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$c", convId);
                cmd.Parameters.AddWithValue("$s", new DateTimeOffset(window[0].SentAt).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$e", new DateTimeOffset(window[^1].SentAt).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$n", window.Count);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$m", "KliveLLM-free");
                cmd.Parameters.AddWithValue("$st", status + (error != null ? ":" + Truncate(error, 200) : ""));
                cmd.Parameters.AddWithValue("$p", (object?)payloadJson ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task SaveCursor(string convId, long watermark, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO extraction_cursors(conversation_id, last_extracted_sent_at, last_run_at)
                    VALUES($c,$w,$t)
                    ON CONFLICT(conversation_id) DO UPDATE SET
                        last_extracted_sent_at=MAX(extraction_cursors.last_extracted_sent_at, excluded.last_extracted_sent_at),
                        last_run_at=excluded.last_run_at,
                        priority=0"; // fast-track consumed
                cmd.Parameters.AddWithValue("$c", convId);
                cmd.Parameters.AddWithValue("$w", watermark);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        // ── Deterministic stimulus→reply harvest (no LLM): replica training fuel ──
        private async Task<int> HarvestStimulusReplyPairsAsync(string convId, List<WindowMessage> fresh, CancellationToken ct)
        {
            var trackedIdentities = LoadTrackedIdentities();
            var byId = fresh.ToDictionary(m => m.MessageId);
            var pairs = new List<(string Replier, string? StimId, string ReplyId, string Stim, string Reply, long At)>();

            for (int i = 0; i < fresh.Count; i++)
            {
                var m = fresh[i];
                if (!trackedIdentities.Contains(m.AuthorIdentityId)) continue;
                if (string.IsNullOrWhiteSpace(m.Content) || m.Content.Length < 2) continue;

                WindowMessage? stimulus = null;
                // Explicit reply beats adjacency.
                if (m.ReplyToMessageId != null && byId.TryGetValue(m.ReplyToMessageId, out var replied)
                    && replied.AuthorIdentityId != m.AuthorIdentityId)
                {
                    stimulus = replied;
                }
                if (stimulus == null && i > 0)
                {
                    var prev = fresh[i - 1];
                    if (prev.AuthorIdentityId != m.AuthorIdentityId && m.SentAt - prev.SentAt <= StimulusAdjacency)
                        stimulus = prev;
                }
                if (stimulus == null || string.IsNullOrWhiteSpace(stimulus.Content)) continue;

                pairs.Add((m.AuthorIdentityId, stimulus.MessageId, m.MessageId,
                    Truncate(stimulus.Content, 500), Truncate(m.Content, 500),
                    new DateTimeOffset(m.SentAt).ToUnixTimeMilliseconds()));
            }
            if (pairs.Count == 0) return 0;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR IGNORE INTO stimulus_reply_pairs
                    (replier_identity_id, stimulus_message_id, reply_message_id, stimulus_text, reply_text, conversation_id, occurred_at)
                    VALUES($r,$sm,$rm,$st,$rt,$c,$t)";
                var pr = cmd.Parameters.Add("$r", SqliteType.Text);
                var psm = cmd.Parameters.Add("$sm", SqliteType.Text);
                var prm = cmd.Parameters.Add("$rm", SqliteType.Text);
                var pst = cmd.Parameters.Add("$st", SqliteType.Text);
                var prt = cmd.Parameters.Add("$rt", SqliteType.Text);
                var pc = cmd.Parameters.Add("$c", SqliteType.Text);
                var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                foreach (var p in pairs)
                {
                    pr.Value = p.Replier;
                    psm.Value = (object?)p.StimId ?? DBNull.Value;
                    prm.Value = p.ReplyId;
                    pst.Value = p.Stim;
                    prt.Value = p.Reply;
                    pc.Value = convId;
                    pt.Value = p.At;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
            return pairs.Count;
        }

        private HashSet<string> LoadTrackedIdentities()
        {
            var set = new HashSet<string>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT pi.identity_id FROM platform_identities pi
                JOIN persons p ON p.person_id = pi.person_id WHERE p.tier='tracked'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
