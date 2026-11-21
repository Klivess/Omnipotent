using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>The replica module's live parts, resolved when a tactic runs (they exist once the module has initialised).</summary>
    public sealed class ReplicaRuntime
    {
        public required ReplicaServices Services { get; init; }
        public required ReplicaTrainer Trainer { get; init; }
        public required ReplicaChatOrchestrator Chat { get; init; }
        public required GroupSimulation Groups { get; init; }
        public required BlindTest Blind { get; init; }
    }

    internal abstract class ReplicaTacticBase : TacticBase
    {
        private readonly Func<ReplicaRuntime> runtime;

        protected ReplicaTacticBase(Func<ReplicaRuntime> runtime) => this.runtime = runtime;

        protected ReplicaRuntime R => runtime();
        protected ReplicaServices S => R.Services;

        protected static string? Text(TacticInput input, string name, int max)
        {
            string? v = input.Param<string>(name)?.Trim();
            return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
        }

        /// <summary>The person's serving replica, or a refusal that shows the readiness numbers (F015).</summary>
        protected (ReplicaSnapshot Snapshot, PersonRecord Person) Serving(string personId)
        {
            string canonical;
            using (var corpus = S.Stores.Corpus.OpenRead()) canonical = People.Canonical(corpus, personId);
            var person = S.Versions.Person(canonical);
            if (person?.ServingVersionId == null)
            {
                var readiness = RJson.Read<ReplicaReadinessDto>(person?.ReadinessJson);
                throw new TacticRefusedException(readiness == null
                    ? "This person has no trained replica yet. Train one first (it needs at least " + S.Settings.MinRepliesExperimental + " replies)."
                    : "No trained replica yet: " + readiness.Reason);
            }
            return (S.Snapshots.Get(person.ServingVersionId), person);
        }
    }

    // ── X55 Rehearsal (N5) ───────────────────────────────────────────────────────

    /// <summary>
    /// X55 / N5 Rehearsal: paste a draft to X; X's replica, given the real recent conversation (as of now), samples several
    /// likely reactions and a summary says how it may land. Re-run with an edited draft and <c>compareToRunId</c> to
    /// compare. Every reaction is a labelled simulation; nothing is sent.
    /// </summary>
    internal sealed class RehearsalTactic : ReplicaTacticBase
    {
        public const string Key = "x55.rehearsal";

        public RehearsalTactic(Func<ReplicaRuntime> runtime) : base(runtime) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X55",
            Title = "Rehearse a message",
            Category = TacticCategory.Social,
            Description = "Paste a draft for someone; their replica samples likely reactions in the context of your real recent conversation, with a summary of how it may land. Simulations only; nothing is sent.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","required":["draft"],"properties":{
                  "draft":{"type":"string","minLength":1,"maxLength":4000},
                  "samples":{"type":"integer","minimum":2,"maximum":12,"default":6},
                  "conversationId":{"type":"string","description":"The real conversation to continue (default: your DM with them)."},
                  "compareToRunId":{"type":"string","description":"An earlier rehearsal run to compare with (edit the draft and compare)."}}}
                """,
            Outputs = new[] { TacticOutputKind.Report },
            Lane = Lane.LlmInteractive,
            RequiredPermission = "omniscience.replica.chat",
            TypicalDurationMs = 45_000,
            TypicalLlmCalls = 8,
        };

        public override string? Validate(TacticInput input)
        {
            if (Text(input, "draft", 4000) == null) return "A draft to rehearse is required.";
            if (input.Selections[0].Count != 1) return "Rehearse with exactly one person.";
            return null;
        }

        private sealed record Context(string VersionId, string PersonId, string Name, string? ConversationId, string? Addressee, string AddresseeName, string ContextKind,
            List<ChatTurn> History, long CoreAsOfMs, long NowMs);

        private sealed record Sample(int Index, string Text, IReadOnlyList<string> Lines, double? Score);

        private sealed record Samples(string GenerationId, List<Sample> Items, string? Model);

        private sealed record Summary(string? Text, Dictionary<string, double>? Sentiment, List<string>? Likely, List<string>? Risks, List<string>? Suggestions, string? Comparison);

        private sealed record SummaryAnswer(string? Summary, SentimentDto? Sentiment, List<string>? Likely, List<string>? Risks, List<string>? Suggestions, string? Comparison);

        private sealed record SentimentDto(double? Positive, double? Neutral, double? Negative);

        private static readonly LlmOutputSchema SummarySchema = ReplicaLlm.Schema("rehearsal_summary", "How the draft is likely to land.", """
            {"type":"object","required":["summary","sentiment","likely","risks"],"properties":{
              "summary":{"type":"string"},
              "sentiment":{"type":"object","properties":{"positive":{"type":"number"},"neutral":{"type":"number"},"negative":{"type":"number"}}},
              "likely":{"type":"array","items":{"type":"string"}},"risks":{"type":"array","items":{"type":"string"}},
              "suggestions":{"type":"array","items":{"type":"string"}},"comparison":{"type":"string"}}}
            """);

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext ctx)
        {
            string draft = Text(ctx.Input, "draft", 4000)!;
            int samples = Math.Clamp(ctx.Input.Param<int?>("samples") ?? 6, 2, 12);
            string personId = ctx.Input.Selections[0].Items[0].Id;

            var context = await ctx.StageAsync("context", ct =>
            {
                var (snapshot, person) = Serving(personId);
                string? conversation = Text(ctx.Input, "conversationId", 256);
                conversation ??= SocialKit.DirectConversation(S, snapshot.Person);
                string? addressee = ReplicaPrep.SelfPerson(S, snapshot);
                long now = ctx.AsOfMs;
                var messages = conversation == null ? new List<ConvMessage>() : SocialKit.Recent(S, conversation, now, 40).ToList();
                bool group = conversation != null && ReplicaPrep.ResolveContext(null, snapshot, addressee) != ContextKinds.Dm && messages.Select(m => m.AuthorPersonId).Distinct().Count() > 2;
                var history = Conversations.AsTurns(messages, snapshot.PersonId, group).ToList();
                string addresseeName = addressee == null ? "Klives" : ReplicaPrep.NameAsOf(S, addressee, snapshot.DataToMs);
                string kind = conversation == null ? ContextKinds.Dm : (Conv(conversation) ?? ContextKinds.Dm);
                return Task.FromResult(new Context(snapshot.VersionId, snapshot.PersonId, person.DisplayName ?? snapshot.Person.DisplayName, conversation, addressee, addresseeName,
                    kind, history, Math.Min(snapshot.DataToMs, now), now));
            }).ConfigureAwait(false);

            var sampled = await ctx.StageAsync("samples", async ct =>
            {
                var snapshot = S.Snapshots.Get(context.VersionId);
                var config = snapshot.Config with { Candidates = samples, LlmJudge = false };
                var request = await ReplicaPrep.RequestAsync(S, snapshot, context.CoreAsOfMs, context.NowMs, context.ContextKind, context.Addressee, context.AddresseeName,
                    context.History, new[] { draft }, config, null, null, null, ct).ConfigureAwait(false);
                var prompt = S.Composer.Compose(request);
                var result = await S.Generator!.GenerateAsync(new GenerateRequest
                {
                    Prompt = prompt, Recompose = shrink => S.Composer.Compose(request with { Shrink = shrink }), Snapshot = snapshot, Config = config,
                    Candidates = samples, LlmJudge = false, Scope = ctx.RunId, Interactive = true, Purpose = "replica.rehearsal",
                }, ct).ConfigureAwait(false);
                string generationId = "rg_" + TextFeatures.Hash(ctx.RunId, 16);
                await GenerationLog.WriteAsync(S, generationId, "rehearsal", null, null, ctx.RunId, context.PersonId, context.VersionId, context.CoreAsOfMs, context.ContextKind, prompt, result, ct).ConfigureAwait(false);
                var items = result.Candidates.Select((c, i) => (c, i)).Where(x => x.c.Lines.Count > 0)
                    .Select(x => new Sample(x.i, x.c.Text, x.c.Lines, x.c.Score?.Total)).ToList();
                if (items.Count == 0) throw new TacticRefusedException("The replica produced no usable reaction: " + (result.Problem ?? "empty output"));
                return new Samples(generationId, items, result.Model);
            }).ConfigureAwait(false);

            Summary? previous = null;
            if (Text(ctx.Input, "compareToRunId", 128) is { } compareTo) previous = PreviousSummary(compareTo);

            var summary = await ctx.StageAsync("summary", async ct =>
            {
                var recent = string.Join("\n", context.History.TakeLast(12).Select(t => (t.Role == "assistant" ? context.Name : t.Speaker ?? context.AddresseeName) + ": " + t.Text));
                var user = "Recent real conversation (context):\n" + (recent.Length == 0 ? "(none on record)" : recent)
                    + "\n\nDraft " + context.AddresseeName + " wants to send:\n" + draft
                    + "\n\nSimulated reactions of " + context.Name + " (" + sampled.Items.Count + " samples, each written by a replica that only saw " + context.Name + "'s archived messages):\n"
                    + string.Join("\n", sampled.Items.Select((x, i) => (i + 1) + ". " + x.Text.Replace("\n", " / ")))
                    + (previous?.Text is { } prev ? "\n\nA rehearsal of an earlier version of the draft concluded: " + prev + " Compare the two in 'comparison'." : "");
                var outcome = await SocialKit.AskAsync<SummaryAnswer>(S, ctx, "replica.rehearsal.summary",
                    "You summarise how a person is likely to react to a message, from simulated reactions written by a model of them. These are simulations, not certainties: say what the samples agree on, where they differ, what could go wrong, and how the draft could be improved.",
                    user, SummarySchema, a => string.IsNullOrWhiteSpace(a.Summary) ? "summary is empty" : null).ConfigureAwait(false);
                if (!outcome.Ok) return new Summary(null, null, null, null, null, null);
                var a = outcome.Value!;
                var sentiment = Normalize(a.Sentiment);
                return new Summary(a.Summary, sentiment, a.Likely, a.Risks, a.Suggestions, a.Comparison);
            }).ConfigureAwait(false);

            var data = new
            {
                simulated = true,
                label = SocialKit.Simulated,
                personId = context.PersonId,
                name = context.Name,
                draft,
                versionId = context.VersionId,
                generationId = sampled.GenerationId,
                conversationId = context.ConversationId,
                samples = sampled.Items,
                summary = summary.Text,
                sentiment = summary.Sentiment,
                likely = summary.Likely,
                risks = summary.Risks,
                suggestions = summary.Suggestions,
                comparison = summary.Comparison,
                previousSentiment = previous?.Sentiment,
            };
            string headline = summary.Text ?? $"{sampled.Items.Count} simulated reactions (no summary: the model did not answer).";
            return new[] { new TacticOutput(TacticOutputKind.Report, null, Json(data), "Simulation: " + headline) };
        }

        private string? Conv(string conversationId)
        {
            using var corpus = S.Stores.Corpus.OpenRead();
            return new Conversations(corpus).ConversationKind(conversationId);
        }

        private static Dictionary<string, double>? Normalize(SentimentDto? s)
        {
            if (s == null) return null;
            double p = Math.Max(0, s.Positive ?? 0), n = Math.Max(0, s.Neutral ?? 0), g = Math.Max(0, s.Negative ?? 0), total = p + n + g;
            if (total <= 0) return null;
            return new Dictionary<string, double> { ["positive"] = p / total, ["neutral"] = n / total, ["negative"] = g / total };
        }

        private Summary? PreviousSummary(string runId)
        {
            using var c = S.Stores.Intel.OpenRead();
            string? outputs = c.Scalar<string>(null, "SELECT outputs_json FROM tactic_runs WHERE run_id=$r AND tactic_key=$k", ("$r", runId), ("$k", Key));
            if (outputs == null) return null;
            var list = RJson.Read<List<TacticOutput>>(outputs);
            var dataElement = list?.FirstOrDefault()?.Data;
            if (dataElement is not { } d) return null;
            string? text = d.TryGetProperty("summary", out var sm) && sm.ValueKind == System.Text.Json.JsonValueKind.String ? sm.GetString() : null;
            Dictionary<string, double>? sentiment = d.TryGetProperty("sentiment", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.Object
                ? se.EnumerateObject().Where(x => x.Value.ValueKind == System.Text.Json.JsonValueKind.Number).ToDictionary(x => x.Name, x => x.Value.GetDouble())
                : null;
            return new Summary(text, sentiment, null, null, null, null);
        }
    }

    // ── X57 Draft in Klives's voice ──────────────────────────────────────────────

    /// <summary>
    /// X57: reply suggestions in Klives's own style, from his own replica (plan R5) and the real conversation. Suggestions
    /// only: copy them or open the conversation in Discord (a deep link, X79). Nothing is ever sent (Guardrails: outbound
    /// actions are Klives's own; replicas never post).
    /// </summary>
    internal sealed class DraftReplyTactic : ReplicaTacticBase
    {
        public const string Key = "x57.draft_reply";

        public DraftReplyTactic(Func<ReplicaRuntime> runtime) : base(runtime) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X57",
            Title = "Draft a reply in my voice",
            Category = TacticCategory.Social,
            Description = "Reply suggestions written the way you write, from your own replica and the real conversation. Copy one or open the chat in Discord; nothing is sent.",
            Accepts = new[] { SelectionKind.Conversation, SelectionKind.Message },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "count":{"type":"integer","minimum":1,"maximum":6,"default":3},
                  "intent":{"type":"string","maxLength":500,"description":"What you want the reply to do, e.g. 'say yes to Saturday but not before 8'."}}}
                """,
            Outputs = new[] { TacticOutputKind.ActionProposal },
            Lane = Lane.LlmInteractive,
            RequiredPermission = "omniscience.replica.chat",
            TypicalDurationMs = 25_000,
            TypicalLlmCalls = 5,
        };

        public override string? Validate(TacticInput input) => input.Selections[0].Count == 1 ? null : "Pick exactly one conversation or message.";

        private sealed record Context(string VersionId, string SelfPersonId, string ConversationId, string? ReplyToMessageId, string? Addressee, string AddresseeName,
            string ContextKind, List<ChatTurn> History, List<string> Latest, long NowMs, string? DeepLink);

        private sealed record Suggestion(string Text, IReadOnlyList<string> Lines, double? Score);

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext ctx)
        {
            int count = Math.Clamp(ctx.Input.Param<int?>("count") ?? 3, 1, 6);
            string? intent = Text(ctx.Input, "intent", 500);
            var selection = ctx.Input.Selections[0];

            var context = await ctx.StageAsync("context", ct =>
            {
                string selfPerson;
                using (var corpus = S.Stores.Corpus.OpenRead())
                    selfPerson = People.SelfPersons(corpus).OrderByDescending(p => S.Versions.Person(p)?.ServingVersionId != null).FirstOrDefault()
                                 ?? throw new TacticRefusedException("None of your own accounts is marked as yours (persons.is_self), so there is no replica of you to write with.");
                var (snapshot, _) = Serving(selfPerson);
                string conversationId;
                string? replyTo = null;
                long until = ctx.AsOfMs;
                using (var corpus = S.Stores.Corpus.OpenRead())
                {
                    if (selection.Kind == SelectionKind.Message)
                    {
                        replyTo = selection.Items[0].Id;
                        var row = corpus.Query(null, "SELECT conversation_id, sent_at_ms FROM messages WHERE message_id=$m", r => (r.GetString(0), r.GetInt64(1)), ("$m", replyTo)).FirstOrDefault();
                        if (row.Item1 == null) throw new TacticRefusedException("That message is not in the corpus.");
                        conversationId = row.Item1;
                        until = row.Item2;
                    }
                    else conversationId = selection.Items[0].Id;
                }
                var messages = SocialKit.Recent(S, conversationId, until, 60).ToList();
                if (messages.Count == 0) throw new TacticRefusedException("That conversation has no messages to reply to.");
                string kind;
                string? link;
                using (var corpus = S.Stores.Corpus.OpenRead())
                {
                    kind = new Conversations(corpus).ConversationKind(conversationId) ?? ContextKinds.Dm;
                    link = SocialKit.DeepLink(corpus, conversationId, replyTo);
                }
                bool group = kind != ContextKinds.Dm;
                int lastOwn = messages.FindLastIndex(m => m.AuthorPersonId == snapshot.PersonId);
                var latest = messages.Skip(lastOwn + 1).Where(m => !m.AuthorIsBot).Select(m => group ? m.Name + ": " + m.Text : m.Text).ToList();
                var history = Conversations.AsTurns(messages.Take(lastOwn + 1), snapshot.PersonId, group).ToList();
                var other = (replyTo != null ? messages.LastOrDefault(m => m.MessageId == replyTo) : null) ?? messages.LastOrDefault(m => m.AuthorPersonId != snapshot.PersonId && !m.AuthorIsBot);
                return Task.FromResult(new Context(snapshot.VersionId, snapshot.PersonId, conversationId, replyTo, other?.AuthorPersonId, other?.Name ?? "them", kind, history, latest,
                    Math.Max(until, ctx.AsOfMs), link));
            }).ConfigureAwait(false);

            var suggestions = await ctx.StageAsync("drafts", async ct =>
            {
                var snapshot = S.Snapshots.Get(context.VersionId);
                var config = snapshot.Config with { Candidates = Math.Min(8, count + 2), LlmJudge = S.Settings.LlmJudge };
                string instruction = "Klives is drafting this reply himself; write the message he would send" + (intent == null ? "." : ". He wants it to: " + intent + ".");
                var request = await ReplicaPrep.RequestAsync(S, snapshot, snapshot.DataToMs, context.NowMs, context.ContextKind, context.Addressee, context.AddresseeName,
                    context.History, context.Latest, config, null, null, instruction, ct).ConfigureAwait(false);
                var prompt = S.Composer.Compose(request);
                var result = await S.Generator!.GenerateAsync(new GenerateRequest
                {
                    Prompt = prompt, Recompose = shrink => S.Composer.Compose(request with { Shrink = shrink }), Snapshot = snapshot, Config = config,
                    Candidates = config.Candidates, LlmJudge = config.LlmJudge, Scope = ctx.RunId, Interactive = true, Purpose = "replica.draft",
                }, ct).ConfigureAwait(false);
                await GenerationLog.WriteAsync(S, "rg_" + TextFeatures.Hash(ctx.RunId, 16), "draft", null, null, ctx.RunId, context.SelfPersonId, context.VersionId, snapshot.DataToMs,
                    context.ContextKind, prompt, result, ct).ConfigureAwait(false);
                var ranked = result.Candidates.Where(c => c.Lines.Count > 0).OrderByDescending(c => c.Score?.Total ?? 0)
                    .DistinctBy(c => TextFeatures.Normalize(c.Text)).Take(count)
                    .Select(c => new Suggestion(c.Text, ReplicaGenerator.Transforms(c.Lines, prompt.Style).Lines, c.Score?.Total)).ToList();
                if (ranked.Count == 0) throw new TacticRefusedException("No usable suggestion: " + (result.Problem ?? "empty output"));
                return ranked;
            }).ConfigureAwait(false);

            var data = new
            {
                suggestions,
                conversationId = context.ConversationId,
                replyToMessageId = context.ReplyToMessageId,
                deepLink = context.DeepLink,
                // Generated text, marked as such in the data itself (not only in the wording), so no consumer can take a
                // suggestion for something Klives wrote, and it can never be used as evidence.
                generated = true,
                label = "Generated suggestion in your style, not a message",
                note = "Suggestions only: copy one or open the conversation. Nothing has been or will be sent.",
                versionId = context.VersionId,
            };
            return new[] { new TacticOutput(TacticOutputKind.ActionProposal, null, Json(data), $"{suggestions.Count} reply suggestion(s) in your voice (not sent).") };
        }
    }

    // ── X63 Group simulation (N6) ────────────────────────────────────────────────

    /// <summary>X63 / N6: a cohort of replicas in one simulated channel about a seed topic; continue it (and join in) from the chat routes.</summary>
    internal sealed class GroupSimulationTactic : ReplicaTacticBase
    {
        public const string Key = "x63.group_simulation";

        public GroupSimulationTactic(Func<ReplicaRuntime> runtime) : base(runtime) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X63",
            Title = "Simulate a group chat",
            Category = TacticCategory.Social,
            Description = "Several people's replicas in one simulated channel with a seed topic; turn-taking follows how the real group replies to each other. You can join in afterwards. A simulation; nothing is sent.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","required":["topic"],"properties":{
                  "topic":{"type":"string","minLength":1,"maxLength":500},
                  "turns":{"type":"integer","minimum":1,"maximum":40,"default":8},
                  "conversationId":{"type":"string","description":"A real group conversation they share, for context and turn-taking."}}}
                """,
            Outputs = new[] { TacticOutputKind.Report },
            Lane = Lane.LlmInteractive,
            RequiredPermission = "omniscience.replica.chat",
            TypicalDurationMs = 90_000,
            TypicalLlmCalls = 16,
        };

        public override string? Validate(TacticInput input)
        {
            int n = input.Selections[0].Count;
            if (n < 2 || n > GroupSimulation.MaxMembers) return $"Pick between 2 and {GroupSimulation.MaxMembers} people.";
            return Text(input, "topic", 500) == null ? "A seed topic is required." : null;
        }

        private sealed record Setup(string ChatId, List<string> Members, int SharedConversations, int ObservedTransitions);

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext ctx)
        {
            string topic = Text(ctx.Input, "topic", 500)!;
            int turns = Math.Clamp(ctx.Input.Param<int?>("turns") ?? 8, 1, 40);
            var setup = await ctx.StageAsync("setup", async ct =>
            {
                try
                {
                    var (chat, model) = await R.Groups.CreateAsync(ctx.Input.Selections[0].Items.Select(i => i.Id).ToList(), topic, Text(ctx.Input, "conversationId", 256), ctx.Actor, ctx.RunId, ct).ConfigureAwait(false);
                    return new Setup(chat.ChatId, chat.Members.ToList(), model.SharedConversations, model.ObservedTransitions);
                }
                catch (ReplicaRefusedException ex) { throw new TacticRefusedException(ex.Message); }
            }).ConfigureAwait(false);
            for (int k = 1; k <= turns; k++)
            {
                int target = k;
                await ctx.StageAsync("turn-" + target, async ct =>
                {
                    var chat = R.Chat.Chats.Get(setup.ChatId) ?? throw new TacticRefusedException("The simulation was deleted.");
                    int have = R.Chat.Chats.Lines(setup.ChatId).Where(l => l.Role == "assistant").Select(l => l.Meta.TurnId).Distinct().Count();
                    if (have < target) await R.Groups.RunTurnsAsync(chat, 1, ctx.RunId, null, ct).ConfigureAwait(false);
                    await ctx.ReportAsync(new { stage = "turn", turn = target, of = turns }).ConfigureAwait(false);
                    return target;
                }).ConfigureAwait(false);
            }
            var lines = R.Chat.Chats.Lines(setup.ChatId).Where(l => l.Role is "assistant" or "user")
                .Select(l => new { speaker = l.Meta.Speaker ?? (l.Role == "user" ? "Klives" : "someone"), personId = l.Meta.PersonId, text = l.Content, simulated = l.Role == "assistant" }).ToList();
            var data = new
            {
                simulated = true,
                label = SocialKit.Simulated,
                chatId = setup.ChatId,
                topic,
                members = setup.Members,
                turnTaking = new { setup.SharedConversations, setup.ObservedTransitions },
                lines,
            };
            return new[] { new TacticOutput(TacticOutputKind.Report, null, Json(data), $"Simulated group chat: {lines.Count} lines about \"{topic}\". Continue it in the replica chats.") };
        }
    }
}
