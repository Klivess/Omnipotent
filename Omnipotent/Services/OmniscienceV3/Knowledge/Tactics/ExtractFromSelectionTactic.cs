using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Tactics
{
    /// <summary>
    /// X17, extract from a selection (plan X-catalogue "Transforms": messages → verified claims/facts). The deduction
    /// pipeline reads only the chosen messages, with the conversation around them, for the people they are evidence about:
    /// the people Klives chose, or else whoever wrote them. Coverage still applies, so nothing is read twice; the claims go
    /// through the same two readings, verification, critic and MinorGuard as the continuous pipeline, and are consolidated
    /// into facts before the run ends.
    /// </summary>
    public sealed class ExtractFromSelectionTactic : TacticBase
    {
        public const string Key = "x17.extract_from_selection";
        public const int MaxMessages = 2_000;
        public const int DefaultMaxPeople = 6;

        private readonly DeductionModule module;

        public ExtractFromSelectionTactic(DeductionModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X17",
            Title = "Extract facts from messages",
            Category = TacticCategory.Transform,
            Description = "Reads the chosen messages, with the conversation around them, for facts about the people who wrote them (or the people you choose), " +
                          "checks them the way the continuous pipeline does, and adds them to those people's knowledge. Messages already read are not read again; " +
                          "your own messages are read for facts about you only when you choose yourself.",
            Accepts = new[] { SelectionKind.Message, SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "maxPeople":{"type":"integer","minimum":1,"maximum":20,"default":6,"description":"Read for at most this many people (those with the most chosen messages first)."}}}
                """,
            Outputs = new[] { TacticOutputKind.FactSet, TacticOutputKind.MessageSet, TacticOutputKind.Report },
            Lane = Lane.LlmInteractive,
            RequiredPermission = ReasoningTactics.AskPermission,
            TypicalDurationMs = 120_000,
            TypicalLlmCalls = 8,
        };

        private static int MessagesIn(TacticInput input) => input.Selections.Where(s => s.Kind == SelectionKind.Message).Sum(s => s.Count);

        public override string? Validate(TacticInput input)
        {
            int messages = MessagesIn(input);
            if (messages == 0) return "Choose the messages to read.";
            if (messages > MaxMessages) return $"Choose at most {MaxMessages:N0} messages (tracked people's messages are already read continuously).";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of each kind.";
            if (input.Param<int?>("maxPeople") is { } k && (k < 1 || k > 20)) return "'maxPeople' must be between 1 and 20.";
            return null;
        }

        /// <summary>About four model calls per slice of evidence (two readings, adjudication, the critic), per person read.</summary>
        public override TacticEstimate Estimate(TacticInput input)
        {
            int messages = MessagesIn(input);
            int chosenPeople = input.Selections.Where(s => s.Kind == SelectionKind.Person).Sum(s => s.Count);
            int maxPeople = Math.Clamp(input.Param<int?>("maxPeople") ?? DefaultMaxPeople, 1, 20);
            int people = Math.Min(maxPeople, chosenPeople > 0 ? chosenPeople : Math.Max(1, messages / 20));
            int slices = Math.Min(DeductionModule.SelectionSlicesPerPerson, Math.Max(1, (int)Math.Ceiling(messages / 300.0)));
            int calls = people * slices * 4;
            return new TacticEstimate(calls * 15_000L, calls, messages, WillSample: false,
                $"Up to {people} {(people == 1 ? "person" : "people")}, about {slices} slice{(slices == 1 ? "" : "s")} each. Messages already read are skipped.");
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context) => module.ExtractSelectedAsync(context);
    }

    /// <summary>What X17 did for one person (kept with the run, so a resumed run does not read them again).</summary>
    public sealed record SelectionOutcome(string PersonId, string? Name, string RunId, int Messages, int AlreadyRead, int Verified, int Rejected, int Refusals, bool Pending,
        string Outcome, Dictionary<string, int> Rejections);
}
