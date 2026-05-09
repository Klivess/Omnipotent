using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.KliveLLM;
using System.Net.Http;
using System.Text;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Runs one chat turn for a Replica:
    ///   1. Embeds the user's prompt with MiniLM
    ///   2. Retrieves top-K topic-matched message exemplars from this person's corpus
    ///   3. Builds a layered system prompt (persona dossier + always-on stylistic exemplars + retrieved exemplars + reflexes)
    ///   4. Calls KliveLLM for the draft reply
    ///   5. Optionally calls KliveLLM a second time as an in-character editor (self-critique pass)
    ///   6. Persists user + assistant messages to <c>replica_chat_messages</c>
    ///
    /// Returns a <see cref="ReplicaChatResult"/> with both draft and (optional) polished outputs so the
    /// HTTP layer can stream them as separate SSE events without re-running anything.
    /// </summary>
    public class ReplicaChatOrchestrator
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly HttpClient http;

        // Tunables.
        private const int RetrievedExemplarCount = 8;
        private const int RecentChatHistoryTurns = 12;
        private const int DraftMaxTokens = 600;
        private const int PolishMaxTokens = 600;

        public ReplicaChatOrchestrator(Omniscience service, OmniscienceDb db, HttpClient http)
        {
            this.service = service;
            this.db = db;
            this.http = http;
        }

        public class ReplicaChatResult
        {
            public string Draft { get; set; } = string.Empty;
            public string? Polished { get; set; }
            public bool UsedSelfCritique { get; set; }
            public int DraftPromptTokens { get; set; }
            public int DraftCompletionTokens { get; set; }
            public int PolishPromptTokens { get; set; }
            public int PolishCompletionTokens { get; set; }
            public long DraftLatencyMs { get; set; }
            public long PolishLatencyMs { get; set; }
            public string ModelUsed { get; set; } = "KliveLLM";
        }

        /// <summary>
        /// Runs one turn. <paramref name="useSelfCritique"/> is the per-chat toggle from the UI.
        /// </summary>
        public async Task<ReplicaChatResult> SendAsync(long chatId, string personId, string userMessage, bool useSelfCritique, CancellationToken ct)
        {
            // Fetch replica row (must exist + ready).
            var (dossierJson, stylisticJson) = await LoadReplicaAsync(personId, ct);
            if (dossierJson == null)
                throw new InvalidOperationException("Replica is not ready for this person.");

            var dossier = JsonConvert.DeserializeObject<ReplicaDossier>(dossierJson)
                          ?? throw new InvalidOperationException("Replica dossier is corrupt.");
            var stylisticExemplars = JsonConvert.DeserializeObject<List<ReplicaExemplar>>(stylisticJson ?? "[]")
                                     ?? new List<ReplicaExemplar>();

            // Persist user turn first so the chat log is durable even if the LLM call fails mid-turn.
            await PersistChatMessageAsync(chatId, "user", userMessage, null, ct);

            // Retrieve topic-matched exemplars via local embedding cosine search.
            var retrieved = await RetrieveTopicalExemplarsAsync(personId, userMessage, RetrievedExemplarCount, ct);

            // Build the system prompt.
            string systemPrompt = BuildSystemPrompt(dossier, stylisticExemplars, retrieved);

            // Pull recent chat history (oldest-first) so the LLM has continuity.
            var history = await LoadRecentHistoryAsync(chatId, RecentChatHistoryTurns, ct);

            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llms[0];

            // We don't reuse KliveLLM sessions across turns because we need full control over the
            // system prompt every turn (it embeds freshly-retrieved exemplars). We rebuild the
            // conversation prompt manually.
            string composed = ComposePromptWithHistory(history, userMessage);

            var result = new ReplicaChatResult { UsedSelfCritique = useSelfCritique };

            var draftSw = System.Diagnostics.Stopwatch.StartNew();
            var draftResp = await llm.QueryLLM(composed, sessionId: null, maxTokensOverride: DraftMaxTokens,
                systemPrompt: systemPrompt, useFreeModel: false);
            draftSw.Stop();
            if (!draftResp.Success || string.IsNullOrWhiteSpace(draftResp.Response))
                throw new InvalidOperationException("KliveLLM draft call failed: " + draftResp.ErrorMessage);

            result.Draft = StripQuotedBlock(draftResp.Response.Trim());
            result.DraftPromptTokens = draftResp.PromptTokens;
            result.DraftCompletionTokens = draftResp.CompletionTokens;
            result.DraftLatencyMs = draftSw.ElapsedMilliseconds;

            // Optional self-critique pass.
            if (useSelfCritique)
            {
                string critiquePrompt = BuildCritiquePrompt(dossier, stylisticExemplars, retrieved, userMessage, result.Draft);
                var polishSw = System.Diagnostics.Stopwatch.StartNew();
                var polishResp = await llm.QueryLLM(critiquePrompt, sessionId: null, maxTokensOverride: PolishMaxTokens,
                    systemPrompt: ReplicaPromptBuilder.CritiqueSystemPrompt, useFreeModel: false);
                polishSw.Stop();
                if (polishResp.Success && !string.IsNullOrWhiteSpace(polishResp.Response))
                {
                    result.Polished = StripQuotedBlock(polishResp.Response.Trim());
                    result.PolishPromptTokens = polishResp.PromptTokens;
                    result.PolishCompletionTokens = polishResp.CompletionTokens;
                    result.PolishLatencyMs = polishSw.ElapsedMilliseconds;
                }
            }

            string finalReply = result.Polished ?? result.Draft;
            await PersistChatMessageAsync(chatId, "assistant", finalReply, result, ct);
            await TouchChatAsync(chatId, ct);
            return result;
        }

        // ── Prompt construction ──

        private static string BuildSystemPrompt(ReplicaDossier dossier, List<ReplicaExemplar> stylistic, List<ReplicaExemplar> retrieved)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ReplicaPromptBuilder.ChatSystemPromptTemplate.Replace("{DISPLAY_NAME}", dossier.DisplayName));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(dossier.VoiceRulebookMarkdown))
            {
                sb.AppendLine("# Your voice rulebook (follow it):");
                sb.AppendLine(dossier.VoiceRulebookMarkdown);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(dossier.OpinionLedgerMarkdown))
            {
                sb.AppendLine("# Your opinions / stances:");
                sb.AppendLine(dossier.OpinionLedgerMarkdown);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(dossier.RelationalMapMarkdown))
            {
                sb.AppendLine("# How you talk to specific people:");
                sb.AppendLine(dossier.RelationalMapMarkdown);
                sb.AppendLine();
            }
            if (dossier.ReflexExamples.Count > 0)
            {
                sb.AppendLine("# Conversational reflexes (when prompted by X you tend to say things like Y):");
                foreach (var kv in dossier.ReflexExamples)
                {
                    sb.AppendLine($"## When the message is a {kv.Key}");
                    foreach (var ex in kv.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(ex.Stimulus))
                            sb.AppendLine($"- They said: \"{Truncate(ex.Stimulus, 160)}\" → You replied: \"{Truncate(ex.Reply, 240)}\"");
                        else
                            sb.AppendLine($"- You replied: \"{Truncate(ex.Reply, 240)}\"");
                    }
                }
                sb.AppendLine();
            }
            if (stylistic.Count > 0)
            {
                sb.AppendLine("# Always-on stylometric exemplars (showcase your voice):");
                foreach (var ex in stylistic.Take(15))
                    sb.AppendLine($"- \"{Truncate(ex.Reply, 240)}\"");
                sb.AppendLine();
            }
            if (retrieved.Count > 0)
            {
                sb.AppendLine("# Topic-matched things you've actually said before (USE THESE for voice and content):");
                foreach (var ex in retrieved)
                {
                    if (!string.IsNullOrWhiteSpace(ex.Stimulus))
                        sb.AppendLine($"- They said: \"{Truncate(ex.Stimulus, 200)}\" → You replied: \"{Truncate(ex.Reply, 280)}\"");
                    else
                        sb.AppendLine($"- \"{Truncate(ex.Reply, 280)}\"");
                }
                sb.AppendLine();
            }
            if (dossier.ForbiddenPatterns.Count > 0)
            {
                sb.AppendLine("# NEVER use these phrases or anything resembling them — they break character:");
                foreach (var p in dossier.ForbiddenPatterns) sb.AppendLine($"- \"{p}\"");
                sb.AppendLine();
            }
            sb.AppendLine("Reply in their voice. Match their length, casing, punctuation and emoji habits. " +
                          "Do not add a name prefix, do not wrap the reply in quotes — output the reply text only.");
            return sb.ToString();
        }

        private static string ComposePromptWithHistory(List<(string role, string content)> history, string newUserMessage)
        {
            // We build a flat transcript and let the system prompt do the heavy lifting.
            // KliveLLM already wraps a single 'prompt' as the user turn; injecting history here
            // gives the model continuity without us needing to manage a KliveLLMSession ourselves.
            var sb = new StringBuilder();
            if (history.Count > 0)
            {
                sb.AppendLine("Recent conversation so far (oldest first):");
                foreach (var (role, content) in history)
                {
                    string label = role == "assistant" ? "You" : "Them";
                    sb.AppendLine($"{label}: {content}");
                }
                sb.AppendLine();
                sb.AppendLine("Now they just said:");
            }
            sb.AppendLine(newUserMessage);
            sb.AppendLine();
            sb.AppendLine("Reply (in your voice, no quotes, no name prefix):");
            return sb.ToString();
        }

        private static string BuildCritiquePrompt(ReplicaDossier dossier, List<ReplicaExemplar> stylistic, List<ReplicaExemplar> retrieved, string userMessage, string draft)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Person: **{dossier.DisplayName}**");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(dossier.VoiceRulebookMarkdown))
            {
                sb.AppendLine("# Their voice rulebook:");
                sb.AppendLine(dossier.VoiceRulebookMarkdown);
                sb.AppendLine();
            }
            if (stylistic.Count > 0)
            {
                sb.AppendLine("# Reference exemplars of how they actually talk:");
                foreach (var ex in stylistic.Take(10)) sb.AppendLine($"- \"{Truncate(ex.Reply, 240)}\"");
                sb.AppendLine();
            }
            if (retrieved.Count > 0)
            {
                sb.AppendLine("# Topic-matched things they've actually said:");
                foreach (var ex in retrieved.Take(6)) sb.AppendLine($"- \"{Truncate(ex.Reply, 240)}\"");
                sb.AppendLine();
            }
            if (dossier.ForbiddenPatterns.Count > 0)
            {
                sb.AppendLine("# Forbidden phrases (rewrite the reply if any of these slipped in):");
                foreach (var p in dossier.ForbiddenPatterns) sb.AppendLine($"- \"{p}\"");
                sb.AppendLine();
            }
            sb.AppendLine($"Stimulus they were responding to:\n{userMessage}");
            sb.AppendLine();
            sb.AppendLine("Draft reply to polish:");
            sb.AppendLine(draft);
            sb.AppendLine();
            sb.AppendLine("Rewrite the reply so it sounds EXACTLY like them. Output ONLY the rewritten reply text.");
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string StripQuotedBlock(string s)
        {
            // Models occasionally wrap the whole reply in surrounding quotes; strip them.
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\u201C') && (s[^1] == '"' || s[^1] == '\u201D'))
                return s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        // ── Retrieval ──

        private async Task<List<ReplicaExemplar>> RetrieveTopicalExemplarsAsync(string personId, string query, int k, CancellationToken ct)
        {
            using var embedder = new ReplicaEmbedder(http, msg => _ = service.ServiceLog(msg));
            await embedder.EnsureReadyAsync(ct);
            var queryVec = await embedder.EmbedAsync(query, ct);

            // Brute-force cosine over the person's embedded messages.
            var candidates = new List<(string messageId, float score)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT message_id, embedding FROM replica_message_embeddings WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string mid = r.GetString(0);
                    var blob = (byte[])r.GetValue(1);
                    var v = ReplicaEmbedder.UnpackEmbedding(blob);
                    if (v.Length != queryVec.Length) continue;
                    float score = ReplicaEmbedder.CosineSimilarity(queryVec, v);
                    candidates.Add((mid, score));
                }
            }
            if (candidates.Count == 0) return new();

            // Top-K by cosine.
            var topIds = candidates
                .OrderByDescending(c => c.score)
                .Take(Math.Max(k * 2, k)) // pull extras so we can drop ones with no usable content
                .ToList();

            // Fetch the actual content + parent (stimulus) content.
            var result = new List<ReplicaExemplar>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                var ids = string.Join(",", topIds.Select((_, i) => "$m" + i));
                cmd.CommandText = $@"
                    SELECT m.message_id, m.conversation_id, m.sent_at, m.content, parent.content
                    FROM messages m
                    LEFT JOIN messages parent ON parent.message_id = m.reply_to_message_id
                    WHERE m.message_id IN ({ids})";
                for (int i = 0; i < topIds.Count; i++)
                    cmd.Parameters.AddWithValue("$m" + i, topIds[i].messageId);
                var byId = new Dictionary<string, (string convId, long sentAt, string? content, string? stim)>();
                using var rr = cmd.ExecuteReader();
                while (rr.Read())
                {
                    byId[rr.GetString(0)] = (
                        rr.GetString(1),
                        rr.GetInt64(2),
                        rr.IsDBNull(3) ? null : rr.GetString(3),
                        rr.IsDBNull(4) ? null : rr.GetString(4));
                }
                foreach (var top in topIds)
                {
                    if (!byId.TryGetValue(top.messageId, out var row)) continue;
                    if (string.IsNullOrWhiteSpace(row.content)) continue;
                    result.Add(new ReplicaExemplar
                    {
                        Stimulus = row.stim,
                        Reply = row.content!,
                        ConversationId = row.convId,
                        SentAt = row.sentAt,
                        Score = top.score,
                    });
                    if (result.Count >= k) break;
                }
            }
            return result;
        }

        // ── DB helpers ──

        private async Task<(string? dossier, string? stylistic)> LoadReplicaAsync(string personId, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, dossier_json, stylistic_exemplars_json FROM replicas WHERE person_id=$p";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, null);
            string status = r.GetString(0);
            if (status != ReplicaStatus.Ready && status != ReplicaStatus.Stale) return (null, null);
            string? d = r.IsDBNull(1) ? null : r.GetString(1);
            string? s = r.IsDBNull(2) ? null : r.GetString(2);
            return (d, s);
        }

        private async Task<List<(string role, string content)>> LoadRecentHistoryAsync(long chatId, int turns, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT role, content FROM replica_chat_messages
                WHERE chat_id=$c AND role IN ('user','assistant')
                ORDER BY sent_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$c", chatId);
            cmd.Parameters.AddWithValue("$n", turns);
            var list = new List<(string role, string content)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1)));
            list.Reverse(); // oldest first
            // Drop the most recent user row if it matches — we just inserted it.
            if (list.Count > 0 && list[^1].role == "user") list.RemoveAt(list.Count - 1);
            return list;
        }

        private async Task PersistChatMessageAsync(long chatId, string role, string content, ReplicaChatResult? meta, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_chat_messages
                    (chat_id, role, content, sent_at, prompt_token_count, completion_token_count, latency_ms, used_self_critique, model_used)
                    VALUES($c, $r, $t, $s, $pt, $ct, $lat, $crit, $mu)";
                cmd.Parameters.AddWithValue("$c", chatId);
                cmd.Parameters.AddWithValue("$r", role);
                cmd.Parameters.AddWithValue("$t", content);
                cmd.Parameters.AddWithValue("$s", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$pt", meta == null ? (object)DBNull.Value : meta.DraftPromptTokens + meta.PolishPromptTokens);
                cmd.Parameters.AddWithValue("$ct", meta == null ? (object)DBNull.Value : meta.DraftCompletionTokens + meta.PolishCompletionTokens);
                cmd.Parameters.AddWithValue("$lat", meta == null ? (object)DBNull.Value : meta.DraftLatencyMs + meta.PolishLatencyMs);
                cmd.Parameters.AddWithValue("$crit", meta != null && meta.UsedSelfCritique ? 1 : 0);
                cmd.Parameters.AddWithValue("$mu", (object?)meta?.ModelUsed ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task TouchChatAsync(long chatId, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE replica_chats SET updated_at=$t WHERE chat_id=$c";
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$c", chatId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
