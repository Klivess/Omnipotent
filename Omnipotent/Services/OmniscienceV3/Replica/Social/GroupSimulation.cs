using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// Group Simulation (N6, tactic X63): several replicas in one simulated channel with a seed topic; Klives can join in.
    /// Turn-taking follows the real group's reply propensities (who answers whom, from conversations they really shared,
    /// with each member's own activity as the fallback), each replica composes from its own person's material only (N6:
    /// "each replica only knows its own person's knowledge view") as of now, and every line is a labelled simulation stored
    /// in operator.db beside the one-to-one chats. Turns are durable interactive jobs; the seeded speaker sequence makes a
    /// simulation reproducible from its history.
    /// </summary>
    public sealed class GroupSimulation
    {
        public const string KindAdvance = "group.advance";
        public const int MaxMembers = 8;

        private sealed record AdvancePayload(int Turns, string RunToken);

        /// <summary>Who speaks after whom: counts of real "j answered i" transitions plus each member's activity (smoothing).</summary>
        public sealed record TurnTaking(Dictionary<string, Dictionary<string, double>> Transitions, Dictionary<string, double> Activity, int SharedConversations, int ObservedTransitions);

        private readonly ReplicaServices s;
        private readonly ChatStore chats;

        public GroupSimulation(ReplicaServices services, ChatStore chats)
        {
            s = services;
            this.chats = chats;
        }

        public async Task<(ReplicaChatDto Chat, TurnTaking Model)> CreateAsync(IReadOnlyList<string> personIds, string topic, string? conversationId, string actor, string? runId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topic)) throw new ReplicaRefusedException("A group simulation needs a seed topic.");
            var members = new List<(string Id, string Name)>();
            using (var corpus = s.Stores.Corpus.OpenRead())
                foreach (var id in personIds)
                {
                    var p = People.Resolve(corpus, id) ?? throw new ReplicaRefusedException($"No person '{id}' in the corpus.", notFound: true);
                    if (members.All(m => m.Id != p.CanonicalId)) members.Add((p.CanonicalId, p.DisplayName));
                }
            if (members.Count < 2) throw new ReplicaRefusedException("A group simulation needs at least two different people.");
            if (members.Count > MaxMembers) throw new ReplicaRefusedException($"A group simulation takes at most {MaxMembers} people.");
            var notReady = new List<string>();
            foreach (var (id, name) in members)
            {
                var person = s.Versions.Person(id);
                if (person?.ServingVersionId == null) { notReady.Add(name); continue; }
                if (ReplicaPrep.MinorRefusal(s, id, name, null, null) is { } refusal) throw new ReplicaRefusedException(refusal);
            }
            if (notReady.Count > 0) throw new ReplicaRefusedException("No trained replica yet for: " + string.Join(", ", notReady) + ". Train them first.");

            var model = Learn(members.Select(m => m.Id).ToList(), conversationId);
            long now = s.Clock.NowMs;
            string chatId = "rs_" + TextFeatures.Hash(string.Join(",", members.Select(m => m.Id)) + "|" + now + "|" + Guid.NewGuid().ToString("N"), 16);
            var chat = new ChatRecord(chatId, ChatKinds.GroupPersonMarker, "Group: " + TextFeatures.HeadTail(topic.Trim(), 80), null, new ChatContext
            {
                Kind = ChatKinds.Group,
                Members = members.Select(m => m.Id).ToList(),
                Topic = topic.Trim(),
                ConversationId = conversationId,
                Seed = TextFeatures.Seed(chatId),
                ContextKind = conversationId == null ? ContextKinds.GroupDm : null,
                RunId = runId,
            }, false, now, now);
            await chats.CreateAsync(chat, ct).ConfigureAwait(false);
            await chats.AppendAsync(chatId, new[] { ("system", "Topic: " + topic.Trim(), (object)new { simulated = false, status = "topic" }) }, now, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.group.create", chatId, new { members = members.Select(m => m.Id), topic }, ct).ConfigureAwait(false);
            return (ChatStore.Dto(chat), model);
        }

        /// <summary>
        /// Real turn-taking among the members: in conversations where at least two of them wrote, which member's message
        /// followed which (bursts collapsed, last 2000 messages per conversation), plus each member's message volume.
        /// </summary>
        public TurnTaking Learn(IReadOnlyList<string> members, string? conversationId)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            var activity = new Dictionary<string, double>(StringComparer.Ordinal);
            var conversations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var m in members)
            {
                var scope = People.Resolve(corpus, m);
                if (scope == null) continue;
                foreach (var i in scope.IdentityIds) identities[i] = m;
                activity[m] = scope.IdentityIds.Sum(i => corpus.Scalar<long>(null, "SELECT COUNT(*) FROM messages WHERE author_identity_id=$i", ("$i", i)));
                foreach (var i in scope.IdentityIds)
                    foreach (var conv in corpus.Query(null, "SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id=$i ORDER BY sent_at_ms DESC LIMIT 300", r => r.GetString(0), ("$i", i)))
                    {
                        if (!conversations.TryGetValue(conv, out var set)) conversations[conv] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(m);
                    }
            }
            var shared = conversations.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key).ToList();
            if (conversationId != null && !shared.Contains(conversationId)) shared.Insert(0, conversationId);
            var transitions = members.ToDictionary(m => m, _ => new Dictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
            int observed = 0;
            foreach (var conv in shared.Take(25))
            {
                var authors = corpus.Query(null, "SELECT author_identity_id FROM messages WHERE conversation_id=$c ORDER BY sent_at_ms DESC, order_key DESC LIMIT 2000", r => r.GetString(0), ("$c", conv));
                authors.Reverse();
                string? previous = null;
                foreach (var a in authors)
                {
                    if (!identities.TryGetValue(a, out var member)) continue;
                    if (previous != null && previous != member) { transitions[previous][member] = transitions[previous].GetValueOrDefault(member) + 1; observed++; }
                    previous = member;
                }
            }
            return new TurnTaking(transitions, activity, shared.Count, observed);
        }

        /// <summary>The next speaker: the real transition row from the last speaker, smoothed by activity; never the same speaker twice.</summary>
        public static string NextSpeaker(TurnTaking model, IReadOnlyList<string> members, string? last, Random rng)
        {
            var candidates = members.Where(m => m != last).ToList();
            if (candidates.Count == 0) return members[0];
            double totalActivity = Math.Max(1, model.Activity.Values.Sum());
            var weights = candidates.Select(m =>
            {
                double prior = (model.Activity.GetValueOrDefault(m) + 1) / totalActivity;
                double observed = last != null && model.Transitions.TryGetValue(last, out var row) ? row.GetValueOrDefault(m) : 0;
                return observed + 2 * prior * Math.Max(1, model.ObservedTransitions / Math.Max(1, members.Count));
            }).ToList();
            double x = rng.NextDouble() * weights.Sum();
            for (int i = 0; i < candidates.Count; i++)
            {
                x -= weights[i];
                if (x <= 0) return candidates[i];
            }
            return candidates[^1];
        }

        public async Task<int> SendAsync(string chatId, string content, string actor, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("A message is required.");
            var chat = Require(chatId);
            string me = SelfName(chat);
            var seqs = await chats.AppendAsync(chatId, new[] { ("user", content.Trim(), (object)new { speaker = me, status = TurnStatuses.Answered }) }, s.Clock.NowMs, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.group.send", chatId, null, ct).ConfigureAwait(false);
            return seqs[0];
        }

        public async Task<string> AdvanceAsync(string chatId, int turns, CancellationToken ct)
        {
            Require(chatId);
            turns = Math.Clamp(turns, 1, 40);
            string token = "ra_" + TextFeatures.Hash(chatId + "|" + Guid.NewGuid().ToString("N"), 12);
            await s.Kernel.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ReplicaSchema.ModuleId, Kind = KindAdvance, SubjectKind = "replica_chat", SubjectId = chatId,
                DedupeKey = JobRequest.KeyFor(ReplicaSchema.ModuleId, KindAdvance, "replica_chat", chatId),
                Lane = Lane.LlmInteractive, Priority = 350, MaxAttempts = 2, PayloadJson = RJson.Write(new AdvancePayload(turns, token)), RunId = token,
            }, ct).ConfigureAwait(false);
            return token;
        }

        private ChatRecord Require(string chatId)
        {
            var chat = chats.Get(chatId) ?? throw new KeyNotFoundException("No such group simulation.");
            if (chat.Context.Kind != ChatKinds.Group) throw new ReplicaRefusedException("This is a one-to-one chat, not a group simulation.");
            if (chat.Archived) throw new ReplicaRefusedException("This simulation is archived.");
            return chat;
        }

        private string SelfName(ChatRecord chat)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            var self = People.SelfPersons(corpus).FirstOrDefault();
            return self == null ? "Klives" : People.Resolve(corpus, self)?.DisplayName ?? "Klives";
        }

        public async Task<string?> ExecuteAdvanceAsync(JobContext job)
        {
            var payload = job.Payload<AdvancePayload>() ?? new AdvancePayload(4, "-");
            string chatId = job.Job.SubjectId!;
            var chat = chats.Get(chatId);
            if (chat == null || chat.Context.Kind != ChatKinds.Group) return null;
            var produced = await RunTurnsAsync(chat, payload.Turns, payload.RunToken, job.Job.JobId, job.Cancellation).ConfigureAwait(false);
            return RJson.Write(new { chatId, turns = produced });
        }

        /// <summary>Generates up to <paramref name="turns"/> simulated turns (also used by the X63 tactic, stage by stage).</summary>
        public async Task<int> RunTurnsAsync(ChatRecord chat, int turns, string runToken, long? jobId, CancellationToken ct, Func<int, Task>? afterTurn = null)
        {
            var members = chat.Context.Members ?? Array.Empty<string>();
            var model = Learn(members, chat.Context.ConversationId);
            int produced = 0;
            for (int t = 0; t < turns; t++)
            {
                ct.ThrowIfCancellationRequested();
                var lines = chats.Lines(chat.ChatId);
                var simulated = lines.Where(l => l.Role is "assistant" or "user").ToList();
                string? last = simulated.LastOrDefault() is { } l0 ? (l0.Role == "user" ? null : l0.Meta.PersonId) : null;
                var rng = new Random((chat.Context.Seed ?? 1) + simulated.Count * 7919);
                string speaker = NextSpeaker(model, members, last, rng);
                await GenerateTurnAsync(chat, speaker, lines, runToken, jobId, ct).ConfigureAwait(false);
                produced++;
                if (afterTurn != null) await afterTurn(produced).ConfigureAwait(false);
            }
            return produced;
        }

        private async Task GenerateTurnAsync(ChatRecord chat, string speaker, IReadOnlyList<ChatLine> lines, string runToken, long? jobId, CancellationToken ct)
        {
            var person = s.Versions.Person(speaker);
            if (person?.ServingVersionId == null) throw ReplicaFailures.Permanent($"{speaker} no longer has a serving replica.");
            if (ReplicaPrep.MinorRefusal(s, speaker, person.DisplayName ?? speaker, null, null) is { } refusal) throw ReplicaFailures.Permanent(refusal);
            var snapshot = s.Snapshots.Get(person.ServingVersionId);
            long now = s.Clock.NowMs;
            string name = ReplicaPrep.NameAsOf(s, speaker, snapshot.DataToMs);
            string transcript = Transcript(chat, lines);
            string requested = ContextKinds.Normalize(chat.Context.ContextKind);
            string context = chat.Context.ConversationId != null ? ConversationKind(chat.Context.ConversationId)
                : requested != ContextKinds.Auto ? requested : ContextKinds.GroupDm;
            var config = snapshot.Config with { Candidates = 2, LlmJudge = false };
            var request = await ReplicaPrep.RequestAsync(s, snapshot, snapshot.DataToMs, now, context, null, "the group", Array.Empty<ChatTurn>(), Array.Empty<string>(),
                config, null, transcript, "This is a simulated group chat about: " + chat.Context.Topic + ".", ct).ConfigureAwait(false);
            request = request with { Name = name };
            var prompt = s.Composer.Compose(request);
            if (s.Generator == null) throw ReplicaFailures.Permanent("This engine has no LLM gateway.");
            string turnId = "rt_" + TextFeatures.Hash(chat.ChatId + "|" + lines.Count + "|" + runToken, 16);
            var result = await s.Generator.GenerateAsync(new GenerateRequest
            {
                Prompt = prompt, Recompose = shrink => s.Composer.Compose(request with { Shrink = shrink }), Snapshot = snapshot, Config = config,
                Candidates = config.Candidates, LlmJudge = false, Scope = turnId, Interactive = true, JobId = jobId,
            }, ct).ConfigureAwait(false);
            string generationId = "rg_" + TextFeatures.Hash(turnId, 16);
            await GenerationLog.WriteAsync(s, generationId, "group", chat.ChatId, turnId, chat.Context.RunId, speaker, snapshot.VersionId, snapshot.DataToMs, context, prompt, result, ct).ConfigureAwait(false);
            if (!result.Ok) throw ReplicaFailures.Transient($"No usable line from {name}: {result.Problem}");
            await chats.AppendAsync(chat.ChatId, result.Lines.Select((text, i) => ("assistant", text, (object)new
            {
                turnId, personId = speaker, speaker = name, versionId = snapshot.VersionId, generationId, burstIndex = i, burstSize = result.Lines.Count, simulated = true,
                status = TurnStatuses.Answered,
            })).ToList(), s.Clock.NowMs, ct).ConfigureAwait(false);
            await s.PublishAsync(ReplicaTopics.Group, new { chatId = chat.ChatId, turnId, speaker, lines = result.Lines.Count }, CancellationToken.None).ConfigureAwait(false);
        }

        private string ConversationKind(string conversationId)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            var kind = new Conversations(corpus).ConversationKind(conversationId);
            return kind is ContextKinds.Guild ? ContextKinds.Guild : ContextKinds.GroupDm;
        }

        /// <summary>The simulated channel as every member sees it (bounded: the newest lines that fit a few thousand tokens).</summary>
        private string Transcript(ChatRecord chat, IReadOnlyList<ChatLine> lines)
        {
            var sb = new StringBuilder();
            sb.Append("[Topic: ").Append(chat.Context.Topic).Append("]\n");
            var said = lines.Where(l => l.Role is "assistant" or "user").ToList();
            var kept = new List<string>();
            int used = 0;
            for (int i = said.Count - 1; i >= 0; i--)
            {
                string line = (said[i].Meta.Speaker ?? (said[i].Role == "user" ? "Klives" : "someone")) + ": " + said[i].Content;
                int cost = s.Estimator.Estimate(line);
                if (used + cost > 12_000 && kept.Count > 0) break;
                kept.Insert(0, line);
                used += cost;
            }
            if (kept.Count < said.Count) sb.Append("(").Append(said.Count - kept.Count).Append(" earlier lines not shown)\n");
            foreach (var line in kept) sb.Append(line).Append('\n');
            return sb.ToString();
        }
    }
}
