using System.Text;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// Trains the assistant-voice house model (background lane): real chat messages from many people as negatives (never
    /// from anyone MinorGuard restricts), and replies the LLM writes "as an AI assistant" and "as an AI chatbot imitating a
    /// casual person" to the same messages as positives (the LLM as teacher, plan A2). Retrained monthly or when the model
    /// version changes; the judge uses it from the moment it is ready.
    /// </summary>
    public sealed class AssistantVoiceTrainer
    {
        public const string Kind = "model.assistant_voice";
        public const int ModelVersion = 1;

        private sealed record Answer(List<Reply>? Replies);
        private sealed record Reply(string? I, string? Text);

        private static readonly LlmOutputSchema Schema = ReplicaLlm.Schema("assistant_replies", "One reply per numbered message.", """
            {"type":"object","required":["replies"],"properties":{"replies":{"type":"array","items":{"type":"object","required":["i","text"],
              "properties":{"i":{"type":"string"},"text":{"type":"string"}}}}}}
            """);

        private readonly ReplicaServices s;

        public AssistantVoiceTrainer(ReplicaServices services) => s = services;

        public bool IsFresh()
        {
            using var c = s.Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT version, state, trained_at_ms FROM replica_models WHERE model_id=$m", r => (V: r.GetInt32(0), State: r.GetString(1), At: r.GetInt64OrNull(2)),
                ("$m", AssistantVoiceModel.ModelId)).FirstOrDefault();
            return row.State == "ready" && row.V == ModelVersion && row.At is long at && s.Clock.NowMs - at < 30L * 86_400_000L;
        }

        public async Task<string?> ExecuteAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (IsFresh()) return null;
            if (s.Llm == null) return RJson.Write(new { skipped = "no LLM gateway" });
            if (s.Llm.Llm.CircuitOpen) throw ReplicaFailures.Transient("The LLM circuit is open; the assistant-voice model waits.");
            var real = SampleReal(600);
            if (real.Count < 60) return RJson.Write(new { skipped = $"only {real.Count} usable real messages" });
            await SetStateAsync("training", null, ct).ConfigureAwait(false);
            var positives = new List<string>();
            int calls = 0;
            var prompts = real.Take(320).ToList();
            foreach (var (batch, index) in prompts.Chunk(32).Select((b, i) => (b, i)))
            {
                ct.ThrowIfCancellationRequested();
                bool casual = index % 2 == 1;
                string system = casual
                    ? "You are an AI chatbot that has been asked to imitate a casual human friend in a chat. For each message, write the reply such a chatbot would give."
                    : "You are a helpful AI assistant. For each message, write the reply you would give.";
                var user = new StringBuilder("Messages:\n");
                for (int i = 0; i < batch.Length; i++) user.Append('m').Append(i + 1).Append(": ").Append(TextFeatures.HeadTail(batch[i].Replace('\n', ' '), 300)).Append('\n');
                var outcome = await s.Llm.StructuredAsync<Answer>("replica.assistant-teacher", ReplicaPrompts.AssistantTeacher, new LlmRequest
                {
                    Purpose = "replica.assistant-teacher",
                    SessionKey = ReplicaLlm.SessionKey(false, "assistant-teacher", system),
                    SystemPrompt = system,
                    Turns = new[] { new LlmTurn("user", user.ToString()) },
                    MaxOutputTokens = 4_000,
                    Sampling = new ModelSamplingParameters(Temperature: 0.8),
                    WorkClass = AIRouterWorkClass.Background,
                    OutputSchema = Schema,
                }, a => a.Replies is { Count: > 0 } ? null : "no replies", job.Job.JobId, ct).ConfigureAwait(false);
                calls++;
                foreach (var reply in outcome.Value?.Replies ?? new())
                    if (!string.IsNullOrWhiteSpace(reply.Text)) positives.Add(reply.Text.Trim());
                await job.ReportProgressAsync(new { stage = "teacher", batch = index + 1, positives = positives.Count }).ConfigureAwait(false);
            }
            if (positives.Count < 40)
            {
                await SetStateAsync("failed", $"the teacher produced only {positives.Count} examples", ct).ConfigureAwait(false);
                throw ReplicaFailures.Transient($"The assistant-voice teacher produced only {positives.Count} examples.");
            }
            var model = AssistantVoiceModel.Train(real, positives, ModelVersion);
            long now = s.Clock.NowMs;
            await s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO replica_models(model_id, kind, version, state, params_json, metrics_json, detail, trained_at_ms, updated_at_ms) VALUES($m,'logistic',$v,'ready',$p,$x,$d,$t,$t)
                ON CONFLICT(model_id) DO UPDATE SET version=excluded.version, state='ready', params_json=excluded.params_json, metrics_json=excluded.metrics_json,
                    detail=excluded.detail, trained_at_ms=excluded.trained_at_ms, updated_at_ms=excluded.updated_at_ms
                """, ("$m", AssistantVoiceModel.ModelId), ("$v", ModelVersion), ("$p", model.ToJson()), ("$x", RJson.Write(model.Metrics)),
                ("$d", $"{real.Count} real messages, {positives.Count} teacher replies, {calls} calls"), ("$t", now)), ct).ConfigureAwait(false);
            s.InvalidateAssistantVoice();
            return RJson.Write(model.Metrics);
        }

        private Task SetStateAsync(string state, string? detail, CancellationToken ct) =>
            s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO replica_models(model_id, kind, version, state, detail, updated_at_ms) VALUES($m,'logistic',$v,$s,$d,$t)
                ON CONFLICT(model_id) DO UPDATE SET state=CASE WHEN replica_models.state='ready' AND $s='training' THEN 'ready' ELSE $s END, detail=$d, updated_at_ms=$t
                """, ("$m", AssistantVoiceModel.ModelId), ("$v", ModelVersion), ("$s", state), ("$d", detail), ("$t", s.Clock.NowMs)), ct);

        /// <summary>Recent real human messages (no bots, system, forwards, deleted, code), never from a (likely) minor.</summary>
        private List<string> SampleReal(int count)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            var rows = corpus.Query(null, """
                SELECT m.content, COALESCE(c.canonical_id, i.person_id) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id
                LEFT JOIN person_canonical c ON c.person_id=i.person_id
                WHERE m.is_bot=0 AND m.is_system=0 AND m.is_webhook=0 AND m.deleted_at_ms IS NULL AND m.forwarded_from_json IS NULL AND m.content IS NOT NULL
                ORDER BY m.ingest_seq DESC LIMIT 8000
                """, r => (Text: r.GetString(0), Person: r.GetString(1)));
            var bands = new Dictionary<string, bool>(StringComparer.Ordinal);
            long now = s.Clock.NowMs;
            bool Allowed(string person)
            {
                if (!bands.TryGetValue(person, out bool ok)) bands[person] = ok = !MinorGuard.IsRestricted(s.Kernel.Policy.BandOf(person, now));
                return ok;
            }
            return rows.Where(r => r.Text.Length is >= 2 and <= 600 && !TextFeatures.HasCodeFence(r.Text) && TextFeatures.HasLetters(TextFeatures.StripMarkup(r.Text)) && Allowed(r.Person))
                .Select(r => r.Text.Trim()).Distinct(StringComparer.Ordinal).OrderBy(t => TextFeatures.Unit("avs|" + t)).Take(count).ToList();
        }
    }
}
