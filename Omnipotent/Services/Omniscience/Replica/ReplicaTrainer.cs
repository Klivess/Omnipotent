using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Omniscience.Analytics;
using System.Net.Http;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Trains a Replica for a single person. The pipeline runs as a single async
    /// task (caller awaits it or fires-and-forgets); progress is persisted to
    /// <c>replica_training_jobs</c> and <c>OnProgress</c> fires for live UI updates.
    ///
    /// Stages:
    ///   1. voice         – LLM-derived voice rulebook from sampled messages
    ///   2. opinions      – LLM-derived opinion ledger from sampled messages
    ///   3. reflexes      – classify stimuli that triggered each reply, keep top exemplars per label
    ///   4. stylometric   – pick always-on top-30 (stimulus, reply) pairs that showcase voice
    ///   5. relational    – LLM-derived per-relation register shifts from social graph
    ///   6. forbidden     – static list of AI-isms; trainer just bakes the list into the dossier
    ///   7. embedding     – local MiniLM embed of every message into replica_message_embeddings
    /// </summary>
    public class ReplicaTrainer
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly HttpClient http;

        // Tunables — modest by default to keep first training under a few minutes on a typical corpus.
        private const int MaxMessagesForVoice = 250;
        private const int MaxMessagesForOpinions = 250;
        private const int MaxReflexExemplarsPerLabel = 4;
        private const int MaxReflexStimuliToClassify = 200;
        private const int MaxStylisticExemplars = 30;
        private const int MaxRelationsToProfile = 8;
        private const int MaxRelationSamples = 20;
        private const int EmbeddingBatchSize = 32;

        // Static "AI-ism" patterns the persona must never use. The trainer just stores
        // them so chat-time critique/system prompt can apply them uniformly.
        private static readonly string[] DefaultForbiddenPatterns =
        {
            "as an AI",
            "as a language model",
            "I'm just an AI",
            "I cannot",
            "I'm sorry, but",
            "It's important to note",
            "Remember to consult a professional",
            "I don't have personal opinions",
            "I'm here to help",
            "Is there anything else",
            "feel free to",
            "Certainly!",
        };

        public event Action<ReplicaJobProgress>? OnProgress;

        public ReplicaTrainer(Omniscience service, OmniscienceDb db, HttpClient http)
        {
            this.service = service;
            this.db = db;
            this.http = http;
        }

        /// <summary>
        /// Public entry-point. Creates a job row, runs all stages, and returns the
        /// jobId. On failure the job row is marked 'failed' and a notification row
        /// is enqueued; the exception is also re-thrown so the caller can decide.
        /// </summary>
        public async Task<long> TrainAsync(string personId, CancellationToken ct)
        {
            long jobId = await CreateJobAsync(personId, ct);
            await SetReplicaStatus(personId, ReplicaStatus.Training, null, ct);

            try
            {
                // Resolve display name & handles up front; needed by every stage.
                await UpdateStage(jobId, "loading_person", 1, "Loading person + identities", ct);
                var person = await LoadPersonAsync(personId, ct);
                if (person == null)
                    throw new InvalidOperationException($"Person {personId} does not exist.");

                // Pull every message authored by this person (across all merged identities).
                await UpdateStage(jobId, "loading_messages", 2, "Loading messages from corpus", ct);
                var allMessages = await LoadPersonMessagesAsync(personId, ct);
                if (allMessages.Count == 0)
                    throw new InvalidOperationException("This person has no captured messages to train on.");
                await UpdateStage(jobId, "loading_messages", 3, $"Loaded {allMessages.Count:N0} messages", ct);

                var dossier = new ReplicaDossier
                {
                    DisplayName = person.DisplayName,
                    Handles = person.Handles,
                    Stats = ComputeCorpusStats(allMessages),
                };

                var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
                if (llms == null || llms.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llms[0];

                // ── Stage 1: voice ──
                await UpdateStage(jobId, ReplicaStage.Voice, 5, "Building voice rulebook", ct);
                var voiceSamples = SampleEvenly(allMessages.Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Content!.Length >= 3).ToList(), MaxMessagesForVoice);
                dossier.VoiceRulebookMarkdown = await CallLLM(llm,
                    ReplicaPromptBuilder.BuildVoicePrompt(person.DisplayName, person.Handles, voiceSamples.Select(m => m.Content!).ToList()),
                    ReplicaPromptBuilder.VoiceSystemPrompt, 2048, ct);
                dossier.Stats.MessagesUsedForVoice = voiceSamples.Count;

                // ── Stage 2: opinions ──
                await UpdateStage(jobId, ReplicaStage.Opinions, 25, "Extracting opinion ledger", ct);
                var opinionSamples = SampleEvenly(allMessages.Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Content!.Length >= 30).ToList(), MaxMessagesForOpinions);
                if (opinionSamples.Count > 0)
                {
                    // We use one shot: feed everything as a single "general" cluster. Topical
                    // clustering is a future improvement; for now the LLM organises the ledger.
                    string opinionPrompt = ReplicaPromptBuilder.BuildOpinionPrompt(person.DisplayName, "all topics combined", opinionSamples.Select(m => m.Content!).ToList());
                    dossier.OpinionLedgerMarkdown = await CallLLM(llm, opinionPrompt, ReplicaPromptBuilder.OpinionSystemPrompt, 2048, ct);
                    dossier.Stats.MessagesUsedForOpinions = opinionSamples.Count;
                }

                // ── Stage 3: reflexes ──
                await UpdateStage(jobId, ReplicaStage.Reflexes, 45, "Capturing conversational reflexes", ct);
                dossier.ReflexExamples = await BuildReflexExemplarsAsync(personId, allMessages, llm, ct);

                // ── Stage 4: stylometric exemplars ──
                await UpdateStage(jobId, ReplicaStage.Stylometric, 60, "Selecting stylometric exemplars", ct);
                var stylisticPairs = await BuildStylisticExemplarsAsync(personId, allMessages, ct);

                // ── Stage 5: relational map ──
                await UpdateStage(jobId, ReplicaStage.Relational, 70, "Mapping relational register shifts", ct);
                var relationalEvidence = await BuildRelationalEvidenceAsync(personId, ct);
                if (relationalEvidence.Count > 0)
                {
                    dossier.RelationalMapMarkdown = await CallLLM(llm,
                        ReplicaPromptBuilder.BuildRelationalPrompt(person.DisplayName, relationalEvidence),
                        ReplicaPromptBuilder.RelationalSystemPrompt, 2048, ct);
                }

                // ── Stage 6: forbidden patterns ──
                await UpdateStage(jobId, ReplicaStage.Forbidden, 80, "Recording forbidden AI-isms", ct);
                dossier.ForbiddenPatterns = DefaultForbiddenPatterns.ToList();

                // ── Persist dossier + stylistic exemplars to the replicas row ──
                await PersistDossierAsync(personId, dossier, stylisticPairs, ct);

                // ── Stage 7: embed every message ──
                await UpdateStage(jobId, ReplicaStage.Embedding, 85, "Embedding messages", ct);
                int embedded = await EmbedAllMessagesAsync(personId, allMessages, jobId, ct);

                // ── Finalise ──
                await FinaliseJobAndReplicaAsync(personId, jobId, embedded, ct);
                await EnqueueNotificationAsync("replica_ready", personId, new JObject
                {
                    ["display_name"] = person.DisplayName,
                    ["messages_embedded"] = embedded,
                }, ct);
                _ = TryNotifyDiscordAsync(person.DisplayName, embedded, success: true, error: null);
                FireProgress(jobId, personId, ReplicaStage.Done, 100, "Replica is ready.");
                return jobId;
            }
            catch (Exception ex)
            {
                await MarkJobFailedAsync(jobId, ex.Message, ct);
                await SetReplicaStatus(personId, ReplicaStatus.Failed, ex.Message, ct);
                await EnqueueNotificationAsync("replica_failed", personId, new JObject { ["error"] = ex.Message }, ct);
                _ = TryNotifyDiscordAsync(personId, 0, success: false, error: ex.Message);
                _ = service.ServiceLogError(ex, $"[Replica] Training failed for person {personId}.");
                throw;
            }
        }

        private async Task TryNotifyDiscordAsync(string displayName, int messagesEmbedded, bool success, string? error)
        {
            try
            {
                var bots = await service.GetServicesByType<KliveBot_Discord.KliveBotDiscord>();
                if (bots == null || bots.Length == 0) return;
                var bot = (KliveBot_Discord.KliveBotDiscord)bots[0];
                string msg = success
                    ? $"🧠 **Replica ready for {displayName}** — embedded {messagesEmbedded} messages. Visit Omniscience → Dossier to chat."
                    : $"⚠️ **Replica training failed for {displayName}**: {error}";
                await bot.SendMessageToKlives(msg);
            }
            catch { /* discord notifications are best-effort */ }
        }

        // ── Stage helpers ──

        private async Task<Dictionary<string, List<ReplicaExemplar>>> BuildReflexExemplarsAsync(
            string personId, List<MessageRow> allMessages, KliveLLM.KliveLLM llm, CancellationToken ct)
        {
            // Find this person's messages that are direct replies (have reply_to_message_id),
            // then look up the stimulus content. We classify up to MaxReflexStimuliToClassify
            // of them and keep the top N replies per label.
            var pairs = await LoadReplyPairsAsync(personId, ct);
            if (pairs.Count == 0) return new();

            // Trim to a manageable batch — bias toward longer, more substantive stimuli first.
            var ranked = pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.StimulusContent) && !string.IsNullOrWhiteSpace(p.ReplyContent))
                .OrderByDescending(p => p.StimulusContent!.Length + p.ReplyContent!.Length)
                .Take(MaxReflexStimuliToClassify)
                .ToList();

            var byLabel = new Dictionary<string, List<ReplicaExemplar>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ranked)
            {
                ct.ThrowIfCancellationRequested();
                string label;
                try
                {
                    string raw = await CallLLM(llm,
                        ReplicaPromptBuilder.BuildReflexClassifierPrompt(pair.StimulusContent!),
                        ReplicaPromptBuilder.ReflexClassifierSystemPrompt, 32, ct, useFreeModel: true);
                    label = NormaliseReflexLabel(raw);
                }
                catch
                {
                    continue;
                }
                if (!byLabel.TryGetValue(label, out var list))
                    byLabel[label] = list = new List<ReplicaExemplar>();
                if (list.Count >= MaxReflexExemplarsPerLabel) continue;
                list.Add(new ReplicaExemplar
                {
                    Stimulus = pair.StimulusContent,
                    Reply = pair.ReplyContent!,
                    ConversationId = pair.ConversationId,
                    SentAt = pair.ReplySentAt,
                    Score = (float)Math.Min(1.0, pair.ReplyContent!.Length / 200.0),
                });
            }
            return byLabel;
        }

        private static string NormaliseReflexLabel(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();
            // Take first word.
            int sp = s.IndexOfAny(new[] { ' ', '\n', '\r', '\t', '.', ',' });
            if (sp > 0) s = s.Substring(0, sp);
            return s switch
            {
                "question" or "joke" or "insult" or "request" or "agreement" or "disagreement" or "correction" or "praise" or "casual" => s,
                _ => "casual",
            };
        }

        private async Task<List<ReplicaExemplar>> BuildStylisticExemplarsAsync(
            string personId, List<MessageRow> allMessages, CancellationToken ct)
        {
            // Score replies by a cheap heuristic favouring richer content (length-bounded,
            // contains punctuation, not pure URL). We also prefer ones that ARE replies so
            // the chat orchestrator can format them as user→assistant pairs.
            var pairs = await LoadReplyPairsAsync(personId, ct);
            var scored = pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.ReplyContent))
                .Select(p => (pair: p, score: ScoreStylistic(p.ReplyContent!)))
                .OrderByDescending(x => x.score)
                .Take(MaxStylisticExemplars)
                .Select(x => new ReplicaExemplar
                {
                    Stimulus = x.pair.StimulusContent,
                    Reply = x.pair.ReplyContent!,
                    ConversationId = x.pair.ConversationId,
                    SentAt = x.pair.ReplySentAt,
                    Score = x.score,
                })
                .ToList();
            // If the person rarely uses the reply mechanic, fall back to plain top messages.
            if (scored.Count < 5)
            {
                scored = allMessages
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => (msg: m, score: ScoreStylistic(m.Content!)))
                    .OrderByDescending(x => x.score)
                    .Take(MaxStylisticExemplars)
                    .Select(x => new ReplicaExemplar
                    {
                        Stimulus = null,
                        Reply = x.msg.Content!,
                        ConversationId = x.msg.ConversationId,
                        SentAt = x.msg.SentAt,
                        Score = x.score,
                    })
                    .ToList();
            }
            return scored;
        }

        private static float ScoreStylistic(string s)
        {
            int len = s.Length;
            // Prefer 30-300 chars; penalise tiny ("k") and giant pastes.
            float lengthScore = len < 30 ? len / 30f : (len > 300 ? Math.Max(0.2f, 300f / len) : 1.0f);
            // Penalise URL-only content.
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !s.Contains(' ')) lengthScore *= 0.2f;
            // Bonus if has punctuation variety (suggests natural prose).
            int punct = 0;
            foreach (var c in s) if (c == '.' || c == ',' || c == '!' || c == '?' || c == ';' || c == ':') punct++;
            float punctScore = Math.Min(1.0f, punct / 4f);
            return lengthScore * 0.7f + punctScore * 0.3f;
        }

        private async Task<List<ReplicaPromptBuilder.RelationalEvidence>> BuildRelationalEvidenceAsync(string personId, CancellationToken ct)
        {
            var result = new List<ReplicaPromptBuilder.RelationalEvidence>();
            await Task.Yield();
            using var conn = db.Open();

            // Find conversations this person participates in, count co-participants by message volume,
            // then pull recent message samples per top relation.
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return result;
            string inC = string.Join(",", idents.Select((_, i) => "$i" + i));

            // Co-author message counts in shared conversations.
            using var topCmd = conn.CreateCommand();
            topCmd.CommandText = $@"
                SELECT pi.platform, pi.platform_username, COALESCE(pi.display_name, pi.platform_username) AS dn,
                       p.person_id AS other_person_id, COUNT(*) AS msg_count
                FROM messages m
                JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                LEFT JOIN persons p ON p.person_id = pi.person_id
                WHERE m.conversation_id IN (
                    SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id IN ({inC})
                )
                AND m.author_identity_id NOT IN ({inC})
                GROUP BY pi.identity_id
                ORDER BY msg_count DESC
                LIMIT $lim";
            int idx = 0; foreach (var i in idents) topCmd.Parameters.AddWithValue("$i" + idx++, i);
            topCmd.Parameters.AddWithValue("$lim", MaxRelationsToProfile);

            var relations = new List<(string platform, string? puser, string dn, int count)>();
            using (var r = topCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    relations.Add((
                        r.GetString(0),
                        r.IsDBNull(1) ? null : r.GetString(1),
                        r.IsDBNull(2) ? "(unknown)" : r.GetString(2),
                        r.GetInt32(4)));
                }
            }

            foreach (var rel in relations)
            {
                using var sCmd = conn.CreateCommand();
                sCmd.CommandText = @"SELECT content FROM messages
                    WHERE author_identity_id IN (
                        SELECT identity_id FROM platform_identities WHERE platform=$pl AND platform_username=$pu
                    )
                    AND content IS NOT NULL AND length(content) >= 8
                    ORDER BY sent_at DESC LIMIT $n";
                sCmd.Parameters.AddWithValue("$pl", rel.platform);
                sCmd.Parameters.AddWithValue("$pu", (object?)rel.puser ?? DBNull.Value);
                sCmd.Parameters.AddWithValue("$n", MaxRelationSamples);
                var samples = new List<string>();
                using var sr = sCmd.ExecuteReader();
                while (sr.Read()) samples.Add(sr.GetString(0));
                if (samples.Count == 0) continue;
                result.Add(new ReplicaPromptBuilder.RelationalEvidence
                {
                    OtherDisplayName = rel.dn,
                    Platform = rel.platform,
                    PlatformUsername = rel.puser,
                    MessageCount = rel.count,
                    SampleMessages = samples,
                });
            }
            return result;
        }

        private async Task<int> EmbedAllMessagesAsync(string personId, List<MessageRow> allMessages, long jobId, CancellationToken ct)
        {
            var toEmbed = allMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Content!.Length >= 3)
                .ToList();
            if (toEmbed.Count == 0) return 0;

            using var embedder = new ReplicaEmbedder(http, msg => _ = service.ServiceLog(msg));
            await embedder.EnsureReadyAsync(ct);

            int totalEmbedded = 0;
            int total = toEmbed.Count;
            for (int start = 0; start < total; start += EmbeddingBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(start + EmbeddingBatchSize, total);
                var slice = toEmbed.GetRange(start, end - start);
                var vecs = await embedder.EmbedBatchAsync(slice.Select(m => m.Content!).ToList(), ct);

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR REPLACE INTO replica_message_embeddings(person_id, message_id, embedding, embedded_at)
                        VALUES($p, $m, $e, $t)";
                    var pp = cmd.Parameters.Add("$p", SqliteType.Text);
                    var pm = cmd.Parameters.Add("$m", SqliteType.Text);
                    var pe = cmd.Parameters.Add("$e", SqliteType.Blob);
                    var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    for (int i = 0; i < slice.Count; i++)
                    {
                        pp.Value = personId;
                        pm.Value = slice[i].MessageId;
                        pe.Value = ReplicaEmbedder.PackEmbedding(vecs[i]);
                        pt.Value = now;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                finally { db.WriteLock.Release(); }

                totalEmbedded += slice.Count;
                int pct = 85 + (int)(13.0 * totalEmbedded / total); // 85 → 98
                await UpdateStage(jobId, ReplicaStage.Embedding, pct, $"Embedded {totalEmbedded}/{total}", ct);
            }
            return totalEmbedded;
        }

        // ── DB helpers ──

        private async Task<long> CreateJobAsync(string personId, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_training_jobs(person_id, started_at, status, stage, progress_pct, log_json)
                    VALUES($p, $t, 'running', 'init', 0, '[]'); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var id = (long)(cmd.ExecuteScalar() ?? 0L);
                return id;
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task UpdateStage(long jobId, string stage, int pct, string message, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE replica_training_jobs
                    SET stage=$s, progress_pct=$p,
                        log_json = json_insert(COALESCE(log_json,'[]'), '$[#]', json_object('at', $t, 'stage', $s, 'msg', $m))
                    WHERE job_id=$j";
                cmd.Parameters.AddWithValue("$s", stage);
                cmd.Parameters.AddWithValue("$p", pct);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$m", message);
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }

            // Resolve personId for the event payload from the job row (avoids carrying it everywhere).
            string? personId = null;
            try
            {
                using var conn = db.Open();
                using var c = conn.CreateCommand();
                c.CommandText = "SELECT person_id FROM replica_training_jobs WHERE job_id=$j";
                c.Parameters.AddWithValue("$j", jobId);
                personId = c.ExecuteScalar() as string;
            }
            catch { }
            FireProgress(jobId, personId ?? string.Empty, stage, pct, message);
        }

        private void FireProgress(long jobId, string personId, string stage, int pct, string? message)
        {
            try { OnProgress?.Invoke(new ReplicaJobProgress { JobId = jobId, PersonId = personId, Stage = stage, ProgressPct = pct, Message = message }); }
            catch { }
        }

        private async Task MarkJobFailedAsync(long jobId, string error, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE replica_training_jobs
                    SET status='failed', finished_at=$t, error=$e WHERE job_id=$j";
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$e", error);
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task FinaliseJobAndReplicaAsync(string personId, long jobId, int embedded, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE replica_training_jobs
                        SET status='ok', finished_at=$t, stage='done', progress_pct=100 WHERE job_id=$j";
                    cmd.Parameters.AddWithValue("$t", now);
                    cmd.Parameters.AddWithValue("$j", jobId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE replicas
                        SET status='ready', built_at=$t, last_status_at=$t, version=version+1,
                            messages_embedded=$me, model_used='all-MiniLM-L6-v2 + KliveLLM', last_error=NULL
                        WHERE person_id=$p";
                    cmd.Parameters.AddWithValue("$t", now);
                    cmd.Parameters.AddWithValue("$me", embedded);
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.ExecuteNonQuery();
                }
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task SetReplicaStatus(string personId, string status, string? error, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replicas(person_id, status, last_status_at, last_error)
                    VALUES($p, $s, $t, $e)
                    ON CONFLICT(person_id) DO UPDATE SET status=excluded.status, last_status_at=excluded.last_status_at, last_error=excluded.last_error";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task PersistDossierAsync(string personId, ReplicaDossier dossier, List<ReplicaExemplar> stylistic, CancellationToken ct)
        {
            string dossierJson = JsonConvert.SerializeObject(dossier);
            string stylisticJson = JsonConvert.SerializeObject(stylistic);
            int promptTokenEstimate = Math.Max(0, (dossierJson.Length + stylisticJson.Length) / 4);
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE replicas SET dossier_json=$d, stylistic_exemplars_json=$s, prompt_token_estimate=$te
                    WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$d", dossierJson);
                cmd.Parameters.AddWithValue("$s", stylisticJson);
                cmd.Parameters.AddWithValue("$te", promptTokenEstimate);
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task EnqueueNotificationAsync(string kind, string personId, JObject payload, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_notifications(kind, person_id, payload_json, created_at)
                    VALUES($k, $p, $j, $t)";
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$j", payload.ToString(Formatting.None));
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        // ── Person/data loading ──

        private record PersonInfo(string DisplayName, List<string> Handles);

        private async Task<PersonInfo?> LoadPersonAsync(string personId, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT display_name FROM persons WHERE person_id=$p";
            cmd.Parameters.AddWithValue("$p", personId);
            var dn = cmd.ExecuteScalar();
            if (dn == null) return null;

            var handles = new List<string>();
            using var hCmd = conn.CreateCommand();
            hCmd.CommandText = @"SELECT platform, platform_username, display_name FROM platform_identities
                WHERE person_id IN (SELECT person_id FROM persons WHERE person_id=$p OR merged_into_person_id=$p)";
            hCmd.Parameters.AddWithValue("$p", personId);
            using var r = hCmd.ExecuteReader();
            while (r.Read())
            {
                handles.Add($"{r.GetString(0)}:{(r.IsDBNull(1) ? "" : r.GetString(1))}");
            }
            return new PersonInfo(dn?.ToString() ?? "(unknown)", handles);
        }

        private record MessageRow(string MessageId, string ConversationId, long SentAt, string? Content, string? ReplyToMessageId);

        private async Task<List<MessageRow>> LoadPersonMessagesAsync(string personId, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            var list = new List<MessageRow>();
            if (idents.Count == 0) return list;
            string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT message_id, conversation_id, sent_at, content, reply_to_message_id
                FROM messages WHERE author_identity_id IN ({inC}) ORDER BY sent_at ASC";
            int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new MessageRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
            return list;
        }

        private record ReplyPair(string ReplyMessageId, string ConversationId, long ReplySentAt, string? ReplyContent, string? StimulusContent);

        private async Task<List<ReplyPair>> LoadReplyPairsAsync(string personId, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            var list = new List<ReplyPair>();
            if (idents.Count == 0) return list;
            string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT m.message_id, m.conversation_id, m.sent_at, m.content, parent.content
                FROM messages m
                LEFT JOIN messages parent ON parent.message_id = m.reply_to_message_id
                WHERE m.author_identity_id IN ({inC})
                  AND m.reply_to_message_id IS NOT NULL
                  AND m.content IS NOT NULL
                ORDER BY m.sent_at DESC";
            int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ReplyPair(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
            return list;
        }

        // ── Misc helpers ──

        private static List<MessageRow> SampleEvenly(List<MessageRow> messages, int target)
        {
            if (messages.Count <= target) return new List<MessageRow>(messages);
            var picks = new List<MessageRow>(target);
            double stride = (double)messages.Count / target;
            for (int i = 0; i < target; i++)
            {
                int idx = (int)Math.Floor(i * stride);
                if (idx >= messages.Count) idx = messages.Count - 1;
                picks.Add(messages[idx]);
            }
            return picks;
        }

        private static ReplicaCorpusStats ComputeCorpusStats(List<MessageRow> messages)
        {
            var stats = new ReplicaCorpusStats { TotalMessages = messages.Count };
            if (messages.Count == 0) return stats;
            stats.FirstMessageAt = messages.Min(m => m.SentAt);
            stats.LastMessageAt = messages.Max(m => m.SentAt);
            var lens = messages.Where(m => m.Content != null).Select(m => m.Content!.Length).OrderBy(x => x).ToList();
            if (lens.Count > 0)
            {
                stats.AverageMessageLength = lens.Average();
                stats.MedianMessageLength = lens[lens.Count / 2];
            }
            return stats;
        }

        private static async Task<string> CallLLM(KliveLLM.KliveLLM llm, string prompt, string systemPrompt, int maxTokens, CancellationToken ct, bool useFreeModel = false)
        {
            // KliveLLM is not cancellable today; we honour the token at the boundary only.
            ct.ThrowIfCancellationRequested();
            var resp = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: maxTokens, systemPrompt: systemPrompt, useFreeModel: useFreeModel);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                throw new InvalidOperationException("KliveLLM returned empty/unsuccessful response.");
            return resp.Response.Trim();
        }
    }
}
