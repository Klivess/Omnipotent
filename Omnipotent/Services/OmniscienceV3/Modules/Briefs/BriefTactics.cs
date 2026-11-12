using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Briefs
{
    /// <summary>What the summarising tactics share: turning a selection into messages, and messages into cited points.</summary>
    public abstract class SummaryTacticBase : TacticBase
    {
        protected const int DefaultMaxMessages = 2_000;
        protected readonly BriefsModule Module;

        protected SummaryTacticBase(BriefsModule module) => Module = module;

        /// <summary>The stored form of what a run read (stage results round-trip through JSON).</summary>
        public sealed record Loaded(List<BriefsModule.SourceMessage> Messages, int Total);

        public sealed record Written(Dictionary<string, List<CitedPoint>> Groups, List<CitedPoint> Overall, List<string> Notable, int Shown, int Calls);

        /// <summary>
        /// The messages a selection stands for: the selected messages; or the selected conversations or servers, within the
        /// selected time range (or the default window); or everything in a time range. When there are more than
        /// <paramref name="cap"/>, the newest are kept and the total is reported.
        /// </summary>
        protected Loaded Load(TacticRunContext context, int cap, long defaultWindowMs)
        {
            var input = context.Input;
            var ctx = Module.Ctx;
            long to = context.AsOfMs, from = to - defaultWindowMs;
            foreach (var s in input.Selections.Where(s => s.Kind == SelectionKind.TimeRange))
                if (s.Items.Count > 0 && Selection.TryParseTimeRange(s.Items[0].Id, out long a, out long b)) (from, to) = (a, Math.Min(b, context.AsOfMs));
            var messageIds = input.Selections.Where(s => s.Kind == SelectionKind.Message).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();
            var conversations = input.Selections.Where(s => s.Kind == SelectionKind.Conversation).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();
            var guilds = input.Selections.Where(s => s.Kind == SelectionKind.Guild).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();
            using var corpus = ctx.Stores.Corpus.OpenRead();
            const string Columns = BriefsModule.MessageColumns, Readable = BriefsModule.Readable;
            static BriefsModule.SourceMessage Read(Microsoft.Data.Sqlite.SqliteDataReader r) => BriefsModule.ReadMessage(r);
            List<BriefsModule.SourceMessage> rows;
            if (messageIds.Count > 0)
                rows = corpus.QueryInWith("SELECT " + Columns + " WHERE m.message_id IN ({keys}) AND m.sent_at_ms <= $asof AND " + Readable, messageIds, Read, ("$asof", context.AsOfMs));
            else if (conversations.Count > 0)
                rows = corpus.QueryInWith("SELECT " + Columns + " WHERE m.conversation_id IN ({keys}) AND m.sent_at_ms >= $f AND m.sent_at_ms < $t AND " + Readable, conversations, Read, ("$f", from), ("$t", to));
            else if (guilds.Count > 0)
                rows = corpus.QueryInWith("SELECT " + Columns + " WHERE m.guild_id IN ({keys}) AND m.sent_at_ms >= $f AND m.sent_at_ms < $t AND " + Readable, guilds, Read, ("$f", from), ("$t", to));
            else
                rows = corpus.Query(null, "SELECT " + Columns + " WHERE m.sent_at_ms >= $f AND m.sent_at_ms < $t AND " + Readable + " ORDER BY m.sent_at_ms DESC LIMIT $l", Read, ("$f", from), ("$t", to), ("$l", cap + 1));
            // Analysis switched off for someone (N20): what they wrote is not summarised.
            var off = Storage.AnalysisOff.Load(ctx.Stores);
            if (off.Any) rows.RemoveAll(m => off.Author(m.AuthorIdentity));
            int total = rows.Count;
            var kept = rows.OrderByDescending(m => m.AtMs).ThenByDescending(m => m.Id, StringComparer.Ordinal).Take(cap).Reverse().ToList();
            return new Loaded(kept, total);
        }

        /// <summary>One group per conversation, in the order of their first message; the overall points span them.</summary>
        protected async Task<Written> WriteAsync(TacticRunContext context, SummaryFocus focus, Loaded loaded, bool perConversation, CancellationToken ct)
        {
            var ctx = Module.Ctx;
            var llm = ctx.Llm ?? throw new TacticRefusedException("Summaries need the model, which this runtime does not have.");
            BriefsModule.Naming names;
            using (var corpus = ctx.Stores.Corpus.OpenRead()) names = new BriefsModule.Naming(corpus, loaded.Messages);
            var zones = new PersonZones(ctx.Stores, ctx.Policy);
            SummaryItem Item(BriefsModule.SourceMessage m, bool where) => new(m.Id, CitedSummarizer.MessageLine(names.Author(m), m.AtMs, zones.At(m.Person, m.AtMs), m.Text,
                where ? names.ConversationTitle(m.Conversation) : null));
            List<SummaryGroup> groups = perConversation
                ? loaded.Messages.GroupBy(m => m.Conversation).OrderBy(g => g.Min(m => m.AtMs)).Select(g => new SummaryGroup(g.Key,
                    names.GuildOf(g.Key) is { } guild ? "#" + names.ConversationTitle(g.Key) + " in " + names.GuildName(guild) : names.ConversationTitle(g.Key), g.Select(m => Item(m, false)).ToList())).ToList()
                : new List<SummaryGroup> { new("all", "The selected messages", loaded.Messages.Select(m => Item(m, true)).ToList()) };
            var summary = await new CitedSummarizer(llm, BriefsModule.ModuleId).RunAsync(focus, "omniscience-ia-" + focus.Purpose.Replace('.', '-'), groups, overall: groups.Count > 1,
                AIRouterWorkClass.Interactive, null, ct).ConfigureAwait(false);
            return new Written(summary.Groups.ToDictionary(g => g.Key, g => g.Value.ToList(), StringComparer.Ordinal), summary.Overall.ToList(), summary.Notable.ToList(), summary.ItemsShown, summary.Calls);
        }

        protected static IEnumerable<string> Cited(Written w) =>
            w.Overall.Concat(w.Groups.Values.SelectMany(p => p)).SelectMany(p => p.Refs).Concat(w.Notable).Distinct(StringComparer.Ordinal);

        protected static string? CheckRange(TacticInput input)
        {
            foreach (var s in input.Selections.Where(s => s.Kind == SelectionKind.TimeRange))
                if (s.Items.Count != 1 || !Selection.TryParseTimeRange(s.Items[0].Id, out _, out _)) return "A time range selection holds exactly one range, as \"fromMs..toMs\".";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of each kind.";
            return null;
        }
    }

    /// <summary>X18 "Summarise selection": messages, conversations or a time range → a cited summary.</summary>
    public sealed class SummariseSelectionTactic : SummaryTacticBase
    {
        public const string Key = "x18.summarise";

        public SummariseSelectionTactic(BriefsModule module) : base(module) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X18",
            Title = "Summarise selection",
            Category = TacticCategory.Transform,
            Description = "A summary of the selected messages, conversations or period in which every point cites the messages it rests on. " +
                          "Conversations without a time range are read over the last 7 days.",
            Accepts = new[] { SelectionKind.Message, SelectionKind.Conversation, SelectionKind.TimeRange },
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "focus":{"type":"string","maxLength":400,"description":"What to look for (optional), e.g. \"what was decided about the trip\"."},
                  "maxMessages":{"type":"integer","minimum":10,"maximum":20000,"default":2000}}}
                """,
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.MessageSet },
            Lane = Kernel.Jobs.Lane.LlmInteractive,
            RequiredPermission = "omniscience.analysis.run",
            TypicalDurationMs = 20_000,
            TypicalLlmCalls = 1,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Param<string>("focus") is { Length: > 400 }) return "'focus' can be at most 400 characters.";
            if (input.Param<int?>("maxMessages") is { } n && (n < 10 || n > 20_000)) return "'maxMessages' must be between 10 and 20000.";
            return CheckRange(input);
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            int cap = context.Input.Param<int?>("maxMessages") ?? DefaultMaxMessages;
            string? focusText = context.Input.Param<string>("focus")?.Trim();
            var loaded = await context.StageAsync("messages", _ => Task.FromResult(Load(context, cap, 7 * 86_400_000L))).ConfigureAwait(false);
            if (loaded.Messages.Count == 0)
                return new[] { new TacticOutput(TacticOutputKind.Report, Data: Json(new { points = Array.Empty<object>(), messages = 0, nothing = true }), Summary: "There are no messages with text in this selection.") };
            var focus = new SummaryFocus("briefs.summarise",
                "Summarise the messages: what was said, decided, planned or left open, and by whom." +
                (string.IsNullOrEmpty(focusText) ? "" : " Klives asked to look for this in particular (it is his wording, quoted): \"" + Classification.TeacherPrompt.Clean(focusText, 400).Replace("\"", "'") + "\"."),
                MaxPointsPerGroup: 8, MaxOverallPoints: 8, PromptVersion: "summarise.v1");
            var written = await context.StageAsync("summary", ct => WriteAsync(context, focus, loaded, perConversation: loaded.Messages.Select(m => m.Conversation).Distinct().Count() > 1, ct)).ConfigureAwait(false);
            var points = written.Overall.Count > 0 ? written.Overall : written.Groups.Values.SelectMany(p => p).ToList();
            var cited = Cited(written).ToList();
            return new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(new
                {
                    points, groups = written.Groups.Select(g => new { conversationId = g.Key, points = g.Value }), notable = written.Notable, messagesRead = written.Shown,
                    messages = loaded.Total, truncated = written.Shown < loaded.Total,
                }), Summary: points.Count == 0 ? "The model found nothing worth summarising in these messages." : string.Join(" ", points.Take(3).Select(p => p.Text))),
                new TacticOutput(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, cited, "summary:cited"), Summary: $"{cited.Count} cited messages"),
            };
        }
    }

    /// <summary>X30 "Topic snapshot": a server (or conversations) and a period → what everyone was talking about, with examples.</summary>
    public sealed class TopicSnapshotTactic : SummaryTacticBase
    {
        public const string Key = "x30.topic_snapshot";
        private static readonly SummaryFocus Focus = new("briefs.topics",
            "List the topics people talked about. One point per topic: name the topic in a few words, then who took part and what was said about it. Cite two to five example messages per topic. " +
            "Order topics by how much was said about them.", MaxPointsPerGroup: 10, MaxOverallPoints: 10, PromptVersion: "topics.v1");

        public TopicSnapshotTactic(BriefsModule module) : base(module) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X30",
            Title = "Topic snapshot",
            Category = TacticCategory.Transform,
            Description = "What everyone in a server or conversation was talking about in a period (the last 7 days unless a time range is selected), topic by topic, with example messages.",
            Accepts = new[] { SelectionKind.Guild, SelectionKind.Conversation, SelectionKind.TimeRange },
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """{"type":"object","properties":{"maxMessages":{"type":"integer","minimum":10,"maximum":20000,"default":4000}}}""",
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.MessageSet },
            Lane = Kernel.Jobs.Lane.LlmInteractive,
            RequiredPermission = "omniscience.analysis.run",
            TypicalDurationMs = 30_000,
            TypicalLlmCalls = 1,
        };

        public override string? Validate(TacticInput input)
        {
            if (!input.Selections.Any(s => s.Kind is SelectionKind.Guild or SelectionKind.Conversation)) return "Select a server or a conversation (a time range alone is not enough).";
            if (input.Param<int?>("maxMessages") is { } n && (n < 10 || n > 20_000)) return "'maxMessages' must be between 10 and 20000.";
            return CheckRange(input);
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            int cap = context.Input.Param<int?>("maxMessages") ?? 4_000;
            var loaded = await context.StageAsync("messages", _ => Task.FromResult(Load(context, cap, 7 * 86_400_000L))).ConfigureAwait(false);
            if (loaded.Messages.Count == 0)
                return new[] { new TacticOutput(TacticOutputKind.Report, Data: Json(new { topics = Array.Empty<object>(), messages = 0, nothing = true }), Summary: "Nobody wrote anything there in this period.") };
            // Topics run across channels, so the whole selection is one group.
            var written = await context.StageAsync("topics", ct => WriteAsync(context, Focus, loaded, perConversation: false, ct)).ConfigureAwait(false);
            var topics = written.Groups.Values.SelectMany(p => p).ToList();
            var cited = Cited(written).ToList();
            return new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(new { topics, messagesRead = written.Shown, messages = loaded.Total, truncated = written.Shown < loaded.Total,
                    people = loaded.Messages.Select(m => m.Person ?? m.AuthorIdentity).Distinct().Count() }),
                    Summary: topics.Count == 0 ? "No topic stood out." : $"{topics.Count} topics across {loaded.Total} messages."),
                new TacticOutput(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, cited, "topics:examples"), Summary: $"{cited.Count} example messages"),
            };
        }
    }

    /// <summary>X31 "Action items": a conversation → plans, promises, questions and open loops.</summary>
    public sealed class ActionItemsTactic : SummaryTacticBase
    {
        public const string Key = "x31.action_items";
        private static readonly string[] Kinds = { "Plan", "Promise", "Question", "Open" };
        private static readonly SummaryFocus Focus = new("briefs.actions",
            "List what is still to be done or answered. Start every point with exactly one of these words and a colon: \"Plan:\" (something agreed to happen), " +
            "\"Promise:\" (someone said they would do something), \"Question:\" (asked and not answered), \"Open:\" (anything else left unresolved). Name who is to act. " +
            "Leave out anything that the later messages show was done, answered or cancelled.", MaxPointsPerGroup: 12, MaxOverallPoints: 12, PromptVersion: "actions.v1");

        public ActionItemsTactic(BriefsModule module) : base(module) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X31",
            Title = "Action items",
            Category = TacticCategory.Transform,
            Description = "Plans, promises, unanswered questions and open loops in the selected conversation or messages (the last 14 days unless a time range is selected), each with the messages it comes from.",
            Accepts = new[] { SelectionKind.Conversation, SelectionKind.Message, SelectionKind.TimeRange },
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """{"type":"object","properties":{"maxMessages":{"type":"integer","minimum":10,"maximum":20000,"default":3000}}}""",
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.MessageSet },
            Lane = Kernel.Jobs.Lane.LlmInteractive,
            RequiredPermission = "omniscience.analysis.run",
            TypicalDurationMs = 20_000,
            TypicalLlmCalls = 1,
        };

        public override string? Validate(TacticInput input)
        {
            if (!input.Selections.Any(s => s.Kind is SelectionKind.Conversation or SelectionKind.Message)) return "Select a conversation or messages (a time range alone is not enough).";
            if (input.Param<int?>("maxMessages") is { } n && (n < 10 || n > 20_000)) return "'maxMessages' must be between 10 and 20000.";
            return CheckRange(input);
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            int cap = context.Input.Param<int?>("maxMessages") ?? 3_000;
            var loaded = await context.StageAsync("messages", _ => Task.FromResult(Load(context, cap, 14 * 86_400_000L))).ConfigureAwait(false);
            if (loaded.Messages.Count == 0)
                return new[] { new TacticOutput(TacticOutputKind.Report, Data: Json(new { items = Array.Empty<object>(), messages = 0, nothing = true }), Summary: "There are no messages with text in this selection.") };
            var written = await context.StageAsync("items", ct => WriteAsync(context, Focus, loaded, perConversation: true, ct)).ConfigureAwait(false);
            var items = written.Groups.SelectMany(g => g.Value.Select(p =>
            {
                // The kind is the word the model was told to start with; anything else is filed as open.
                string kind = Kinds.FirstOrDefault(k => p.Text.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase)) ?? "Open";
                string text = p.Text.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase) ? p.Text[(kind.Length + 1)..].Trim() : p.Text;
                return new { kind = kind.ToLowerInvariant(), text, conversationId = g.Key, refs = p.Refs };
            })).ToList();
            var cited = items.SelectMany(i => i.refs).Distinct(StringComparer.Ordinal).ToList();
            return new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(new { items, messagesRead = written.Shown, messages = loaded.Total, truncated = written.Shown < loaded.Total }),
                    Summary: items.Count == 0 ? "Nothing is left open in these messages." : string.Join(", ", items.GroupBy(i => i.kind).Select(g => $"{g.Count()} {g.Key}")) + "."),
                new TacticOutput(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, cited, "actions:cited"), Summary: $"{cited.Count} cited messages"),
            };
        }
    }

    /// <summary>X52 "Digest subscriptions": a digest of a server, chat or person (or of everything) on a schedule.</summary>
    public sealed class DigestSubscriptionTactic : TacticBase
    {
        public const string Key = "x52.digest_subscription";
        private readonly BriefsModule module;

        public DigestSubscriptionTactic(BriefsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X52",
            Title = "Digest subscription",
            Category = TacticCategory.Monitor,
            Description = "A catch-up digest of the selected server, conversation or person, written every day or every week at a time of day in Klives's zone. With nothing selected it covers everything.",
            Accepts = new[] { SelectionKind.Guild, SelectionKind.Conversation, SelectionKind.Person },
            MinSelections = 0,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "name":{"type":"string","maxLength":80},
                  "cadence":{"type":"string","enum":["daily","weekly"],"default":"daily"},
                  "at":{"type":"string","pattern":"^[0-2][0-9]:[0-5][0-9]$","default":"08:00","description":"Time of day (HH:mm) in Klives's zone."},
                  "weekday":{"type":"integer","minimum":0,"maximum":6,"description":"For weekly: 0 is Sunday."}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule },
            Lane = Kernel.Jobs.Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            MinorRestriction = RestrictedCapability.PersonTargetedAlert,
            TypicalDurationMs = 300,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections.Count == 1 && input.Selections[0].Count != 1) return "Select exactly one server, conversation or person.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var input = context.Input;
            var selection = input.Selections.FirstOrDefault();
            string scopeKind = selection?.Kind switch { SelectionKind.Guild => "guild", SelectionKind.Conversation => "conversation", SelectionKind.Person => "person", _ => "all" };
            string? scopeId = selection?.Items[0].Id;
            string cadence = input.Param<string>("cadence") ?? "daily", at = input.Param<string>("at") ?? "08:00";
            string name = input.Param<string>("name")?.Trim() is { Length: > 0 } n ? n : (cadence == "weekly" ? "Weekly digest" : "Daily digest") + (selection?.Items[0].Label is { Length: > 0 } label ? ": " + label : "");
            try
            {
                var saved = await module.SaveSubscriptionAsync(null, name.Length > 80 ? name[..80] : name, scopeKind, scopeId, cadence, at, input.Param<int?>("weekday"), true, context.Actor, context.Cancellation)
                    .ConfigureAwait(false);
                return new[]
                {
                    new TacticOutput(TacticOutputKind.AlertRule, Data: Json(saved),
                        Summary: $"A {saved.Cadence} digest at {saved.At} ({(saved.ScopeKind == "all" ? "everything" : saved.ScopeKind)}); the first one is written at its next time."),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                throw new TacticRefusedException(ex.Message);
            }
        }
    }
}
