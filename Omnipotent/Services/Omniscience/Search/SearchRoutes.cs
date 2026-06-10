using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience.Search
{
#pragma warning disable CS4014
    /// <summary>
    /// Semantic search + person Q&amp;A (RAG) over the corpus-wide embedding index.
    /// /omniscience/search/semantic  — top-K messages by meaning, optional person scope
    /// /omniscience/persons/ask      — natural-language question answered from retrieved
    ///                                 messages with message-id citations
    /// </summary>
    public class SearchRoutes
    {
        private readonly Omniscience service;

        public SearchRoutes(Omniscience service) { this.service = service; }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/omniscience/search/semantic", async req =>
            {
                try
                {
                    string q = req.userParameters?["q"] ?? "";
                    string? personId = req.userParameters?["personId"];
                    int limit = int.TryParse(req.userParameters?["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
                    if (string.IsNullOrWhiteSpace(q))
                    {
                        await req.ReturnResponse("q is required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var hits = await service.SearchIndex.SearchAsync(q, string.IsNullOrWhiteSpace(personId) ? null : personId, limit, CancellationToken.None);
                    var detailed = HydrateHits(hits);
                    await req.ReturnResponse(new JObject(
                        new JProperty("query", q),
                        new JProperty("results", detailed)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/ask", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    string question = body.Value<string>("question") ?? "";
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        await req.ReturnResponse("question is required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var answer = await AnswerQuestionAsync(string.IsNullOrWhiteSpace(personId) ? null : personId, question);
                    await req.ReturnResponse(answer.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            try { await req.ReturnResponse(new JObject(new JProperty("error", ex.Message)).ToString(Formatting.None), code: HttpStatusCode.InternalServerError); }
            catch { }
        }

        // Resolve hit message ids to content + author + conversation context.
        private JArray HydrateHits(List<(string MessageId, float Score)> hits)
        {
            var arr = new JArray();
            if (hits.Count == 0) return arr;
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            var names = hits.Select((_, i) => "$m" + i).ToList();
            cmd.CommandText = $@"SELECT m.message_id, m.content, m.sent_at, c.kind, c.guild_name, c.title,
                       pi.person_id, COALESCE(p.display_name, pi.platform_username, '')
                FROM messages m
                LEFT JOIN conversations c ON c.conversation_id = m.conversation_id
                LEFT JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                LEFT JOIN persons p ON p.person_id = pi.person_id
                WHERE m.message_id IN ({string.Join(",", names)})";
            for (int i = 0; i < hits.Count; i++) cmd.Parameters.AddWithValue(names[i], hits[i].MessageId);

            var byId = new Dictionary<string, JObject>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    byId[r.GetString(0)] = new JObject(
                        new JProperty("message_id", r.GetString(0)),
                        new JProperty("content", r.IsDBNull(1) ? "" : r.GetString(1)),
                        new JProperty("sent_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime.ToString("o")),
                        new JProperty("conversation_kind", r.IsDBNull(3) ? "" : r.GetString(3)),
                        new JProperty("guild_name", r.IsDBNull(4) ? null : r.GetString(4)),
                        new JProperty("channel_title", r.IsDBNull(5) ? null : r.GetString(5)),
                        new JProperty("author_person_id", r.IsDBNull(6) ? "" : r.GetString(6)),
                        new JProperty("author_display", r.IsDBNull(7) ? "" : r.GetString(7)));
                }
            }
            foreach (var (messageId, score) in hits)
            {
                if (!byId.TryGetValue(messageId, out var obj)) continue;
                obj["score"] = Math.Round(score, 4);
                arr.Add(obj);
            }
            return arr;
        }

        // ── Person Q&A: retrieve → answer with citations ──
        private async Task<JObject> AnswerQuestionAsync(string? personId, string question)
        {
            var hits = await service.SearchIndex.SearchAsync(question, personId, 14, CancellationToken.None);
            var evidence = HydrateHits(hits);
            if (evidence.Count == 0)
                return new JObject(
                    new JProperty("answer", "No relevant messages found in the corpus for this question."),
                    new JProperty("citations", new JArray()));

            var sb = new StringBuilder();
            sb.AppendLine("Question about a person, answered ONLY from their archived chat messages below.");
            sb.AppendLine($"Question: {question}");
            sb.AppendLine();
            sb.AppendLine("Evidence messages (numbered):");
            int n = 1;
            foreach (var e in evidence.OfType<JObject>())
            {
                string content = (e.Value<string>("content") ?? "").Replace("\r", " ").Replace("\n", " ");
                if (content.Length > 300) content = content[..300] + "…";
                sb.AppendLine($"[{n}] {e.Value<string>("sent_at")?[..10]} {e.Value<string>("author_display")}: {content}");
                n++;
            }
            sb.AppendLine();
            sb.AppendLine("Answer the question concisely. Cite evidence numbers like [3] for every claim. " +
                          "If the evidence does not answer the question, say so explicitly — do NOT guess.");

            var llms = await service.GetServicesByType<KliveLLM.KliveLLM>();
            if (llms == null || llms.Length == 0)
                return new JObject(new JProperty("answer", "KliveLLM unavailable."), new JProperty("citations", evidence));
            var llm = (KliveLLM.KliveLLM)llms[0];
            var resp = await llm.QueryLLM(sb.ToString(), sessionId: null, maxTokensOverride: 700,
                systemPrompt: "You are a precise analyst answering questions from archived chat evidence. Never invent facts.",
                useFreeModel: true);

            return new JObject(
                new JProperty("answer", resp.Success ? resp.Response : ("LLM error: " + resp.ErrorMessage)),
                new JProperty("citations", evidence));
        }
    }
}
