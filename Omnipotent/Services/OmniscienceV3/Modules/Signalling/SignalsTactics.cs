using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.Modules.Signalling
{
    /// <summary>
    /// X41 "Semantic watch": a rule in plain language becomes a standing alert (N11). Selected people or conversations
    /// limit where it looks. The rule is checked against the sensitive-trait policy before it is saved, so a refusal is the
    /// run's answer; the rule then calibrates in the background and starts checking live messages.
    /// </summary>
    public sealed class SemanticWatchTactic : TacticBase
    {
        public const string Key = "x41.semantic_watch";
        private readonly SignalsModule module;

        public SemanticWatchTactic(SignalsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X41",
            Title = "Semantic watch",
            Category = TacticCategory.Monitor,
            Description = "Describe what to watch for in plain language (\"anyone selling a GPU\", \"people talking about my game\"). New live messages that really say it raise an alert. " +
                          "Select people or conversations to limit where it looks.",
            Accepts = new[] { SelectionKind.Person, SelectionKind.Conversation },
            MinSelections = 0,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","required":["rule"],"properties":{
                  "rule":{"type":"string","minLength":3,"maxLength":2000,"description":"What to look for, in plain language."},
                  "name":{"type":"string","maxLength":80,"description":"A short name for the rule. Defaults to the start of the rule."},
                  "guilds":{"type":"array","items":{"type":"string"},"maxItems":50,"description":"Only look in these servers."},
                  "watchId":{"type":"string","description":"Change this rule instead of creating a new one."}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule },
            Lane = Kernel.Jobs.Lane.LlmInteractive,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 5_000,
            TypicalLlmCalls = 1,
        };

        public override string? Validate(TacticInput input)
        {
            string? rule = input.Param<string>("rule")?.Trim();
            if (rule is not { Length: >= 3 and <= 2000 }) return "Describe what to watch for (3 to 2000 characters).";
            if (input.Param<string>("name") is { Length: > 80 }) return "The rule's name can be at most 80 characters.";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of people and one of conversations.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var input = context.Input;
            string text = input.Param<string>("rule")!.Trim();
            string name = input.Param<string>("name")?.Trim() is { Length: > 0 } given ? given : text.Length <= 60 ? text : text[..60].TrimEnd() + "…";
            string? watchId = input.Param<string>("watchId") is { Length: > 0 } id ? id : null;
            var persons = input.Selections.Where(s => s.Kind == SelectionKind.Person).SelectMany(s => s.Items.Select(i => i.Id)).ToList();
            var conversations = input.Selections.Where(s => s.Kind == SelectionKind.Conversation).SelectMany(s => s.Items.Select(i => i.Id)).ToList();
            var guilds = input.Param<List<string>>("guilds") ?? new List<string>();
            if (persons.Count > 50 || conversations.Count > 50) throw new TacticRefusedException("A watch rule can be limited to at most 50 people and 50 conversations.");

            string? refusal = await context.StageAsync("policy", ct => module.SensitiveRuleAsync(name, text, null, ct)).ConfigureAwait(false);
            if (refusal != null) throw new TacticRefusedException(refusal);

            var rule = await context.StageAsync("save", async ct =>
            {
                try
                {
                    return await module.SaveWatchAsync(watchId, name, new WatchDefinition(text, persons, conversations, guilds), true, context.Actor, policyChecked: true, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
                {
                    throw new TacticRefusedException(ex.Message);
                }
            }).ConfigureAwait(false);

            string scope = persons.Count + conversations.Count + guilds.Count == 0 ? "everywhere"
                : string.Join(", ", new[] { persons.Count > 0 ? $"{persons.Count} people" : null, conversations.Count > 0 ? $"{conversations.Count} conversations" : null, guilds.Count > 0 ? $"{guilds.Count} servers" : null }.OfType<string>());
            return new[]
            {
                new TacticOutput(TacticOutputKind.AlertRule, Data: Json(new { rule.WatchId, rule.Name, rule.State, definition = rule.Definition, kind = SignalsModule.SemanticWatch }),
                    Summary: $"Watching for \"{name}\" ({scope}). It checks live messages only; older ones are not searched."),
            };
        }
    }

    /// <summary>
    /// X49 "Reply nudges": what has waited for Klives's answer longer than a threshold (N9), most pressing first. The same
    /// items are nudged once on their own when they pass <c>signals.inbox.nudge_hours</c>; this is the list on demand.
    /// </summary>
    public sealed class ReplyNudgesTactic : TacticBase
    {
        public const string Key = "x49.reply_nudges";
        private readonly SignalsModule module;

        public ReplyNudgesTactic(SignalsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X49",
            Title = "Reply nudges",
            Category = TacticCategory.Monitor,
            Description = "Messages to Klives that have waited for an answer longer than a threshold, ranked by how much he talks with the person and how long they have waited.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "olderThanHours":{"type":"number","minimum":0,"maximum":8760,"default":24,"description":"Only what has waited at least this long."},
                  "limit":{"type":"integer","minimum":1,"maximum":200,"default":20}}}
                """,
            Outputs = new[] { TacticOutputKind.Reminder, TacticOutputKind.MessageSet },
            Lane = Kernel.Jobs.Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            TypicalDurationMs = 500,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Param<double?>("olderThanHours") is { } h && (h < 0 || h > 8760)) return "'olderThanHours' must be between 0 and 8760.";
            if (input.Param<int?>("limit") is { } l && (l < 1 || l > 200)) return "'limit' must be between 1 and 200.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            double hours = context.Input.Param<double?>("olderThanHours") ?? 24;
            int limit = context.Input.Param<int?>("limit") ?? 20;
            long threshold = (long)(hours * 3_600_000);
            var waiting = module.Inbox("open", 500).Items.Where(i => i.WaitingMs >= threshold).Take(limit).ToList();
            string summary = waiting.Count == 0 ? $"Nothing has waited longer than {hours:0.#} hours."
                : $"{waiting.Count} waiting longer than {hours:0.#} hours; the oldest for {waiting.Max(w => w.WaitingMs) / 3_600_000.0:0.#} hours.";
            IReadOnlyList<TacticOutput> outputs = new[]
            {
                new TacticOutput(TacticOutputKind.Reminder, Data: Json(new { olderThanHours = hours, items = waiting }), Summary: summary),
                new TacticOutput(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, waiting.Select(w => w.MessageId), "inbox:waiting"), Summary: $"{waiting.Count} messages"),
            };
            return Task.FromResult(outputs);
        }
    }
}
