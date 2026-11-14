using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Knowledge.Tactics;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>X17, extract from a selection: the deduction pipeline on chosen messages only. All people are synthetic.</summary>
    public class ExtractFromSelectionTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        private const string Klives = "p-klives", Ana = "p-ana", Ben = "p-ben";

        /// <summary>
        /// A model that, in both readings, states the city of every line where the subject of the extraction says "I live in …",
        /// adjudicates nothing and supports every claim the critic is shown: the test is about what is read, not the model.
        /// </summary>
        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                switch (call.Seed)
                {
                    case 1 or 2:
                    {
                        string? subject = Regex.Match(call.Prompt, @"^(p[0-9a-f]+): [^\n]*\[subject of this extraction", RegexOptions.Multiline) is { Success: true } s ? s.Groups[1].Value : null;
                        var claims = FakeLlm.EvidenceLines(call.Prompt)
                            .Select(kv => (Ref: kv.Key, kv.Value.Handle, Said: Regex.Match(kv.Value.Text, @"I live in (\w+)")))
                            .Where(x => x.Said.Success && x.Handle == subject)
                            .Select((x, i) => new Dictionary<string, object?>
                            {
                                ["id"] = "c" + (i + 1).ToString(CultureInfo.InvariantCulture), ["subject"] = x.Handle, ["attribute"] = "city", ["value"] = x.Said.Groups[1].Value,
                                ["polarity"] = "affirmed", ["modality"] = "literal", ["source_kind"] = "self_disclosure", ["speech_act"] = "statement", ["sincerity"] = "sincere",
                                ["time"] = new { is_current = true },
                                ["evidence"] = new[] { new Dictionary<string, string> { ["ref"] = x.Ref, ["quote"] = x.Said.Value } }, ["probability"] = 0.9,
                            }).ToList();
                        return JsonSerializer.Serialize(new { claims });
                    }
                    case 3:
                        return JsonSerializer.Serialize(new { decisions = Array.Empty<object>() });
                    case 4:
                        return JsonSerializer.Serialize(new
                        {
                            verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "fake" }),
                        });
                    default:
                        return "{\"claims\":[]}";
                }
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
        }

        private static TacticInput Input(IEnumerable<string> messages, IEnumerable<string>? people = null)
        {
            var selections = new List<Selection> { Selection.Of(SelectionKind.Message, messages) };
            if (people != null) selections.Add(Selection.Of(SelectionKind.Person, people));
            return new TacticInput(selections);
        }

        private static async Task<(KnowledgeHarness H, Dictionary<string, string> M)> StartAsync()
        {
            var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            var b = new CorpusBuilder(h.Stores);
            await b.PersonAsync(Klives, "Klives", self: true);
            await b.PersonAsync(Ana, "Ana Testwell"); // archive tier: the continuous pipeline does not read her
            await b.PersonAsync(Ben, "Ben Testwell");
            string room = CorpusIds.Conversation("discord", "room"), dm = CorpusIds.Conversation("discord", "dm-ana");
            await b.ConversationAsync(room, "group_dm", Klives, Ana, Ben);
            await b.ConversationAsync(dm, "dm", Klives, Ana);
            long t = Now - 3_600_000;
            var m = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ana"] = await b.MessageAsync(Ana, room, t, "I live in Leeds these days"),
                ["ben"] = await b.MessageAsync(Ben, room, t + 60_000, "I live in York, still"),
                ["klives"] = await b.MessageAsync(Klives, room, t + 120_000, "I live in Bath myself"),
                ["ana-elsewhere"] = await b.MessageAsync(Ana, dm, t - 86_400_000, "I live in Paris, in my dreams"),
            };
            return (h, m);
        }

        private static string Summary(IReadOnlyList<TacticOutput> outputs) => outputs.Single(o => o.Kind == TacticOutputKind.Report).Summary!;

        private static List<string> Cities(KnowledgeHarness h, string person) =>
            h.Kit.Assertions.Query(new AssertionQuery { SubjectIds = new[] { person }, AtMs = h.Clock.NowMs, Attribute = "city" }).Select(a => a.Record.ValueText ?? a.Record.Value.Text).ToList();

        [Fact]
        public async Task X17_ReadsTheChosenMessagesForTheirAuthors_AndNeverTheSameMessageTwice()
        {
            var (h, m) = await StartAsync();
            await using var _h = h;

            var (run, _, outputs) = await InquiryTests.RunAsync(h, ExtractFromSelectionTactic.Key, Input(new[] { m["ana"], m["ben"], m["klives"] }));
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            // Ana and Ben were read, from the chosen messages only: her other chat is not part of the selection.
            Assert.Equal(new[] { "Leeds" }, Cities(h, Ana));
            Assert.Equal(new[] { "York" }, Cities(h, Ben));
            // Klives's own message is not read for facts about him unless he chooses himself.
            Assert.Empty(Cities(h, Klives));
            var facts = outputs.Single(o => o.Kind == TacticOutputKind.FactSet).Selection!.Items.Select(i => i.Id).ToList();
            Assert.Equal(2, facts.Count);
            Assert.Equal(new[] { m["ana"], m["ben"] }.OrderBy(x => x), outputs.Single(o => o.Kind == TacticOutputKind.MessageSet).Selection!.Items.Select(i => i.Id).OrderBy(x => x));
            Assert.StartsWith("Read 2 messages for ", Summary(outputs));
            // Klives asked and is waiting: the calls go out as interactive work, not background.
            Assert.All(h.Llm.Calls.Where(c => c.Purpose == "deduction.extract"), c => Assert.Equal(AIRouterWorkClass.Interactive, c.WorkClass));
            using (var c = h.Stores.Intel.OpenRead())
                Assert.Equal(0L, c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_coverage WHERE message_id=$m", ("$m", m["ana-elsewhere"])));

            // The same selection again: everything was read at this version under this prompt, so nothing is read.
            int calls = h.Llm.CountPurpose("deduction.extract");
            var (_, _, again) = await InquiryTests.RunAsync(h, ExtractFromSelectionTactic.Key, Input(new[] { m["ana"], m["ben"] }));
            Assert.Equal(calls, h.Llm.CountPurpose("deduction.extract"));
            Assert.StartsWith("Nothing new to read", Summary(again));
        }

        [Fact]
        public async Task X17_ReadsKlivesOwnMessagesOnlyWhenHeChoosesHimself_AndNeverSomeoneWhoseAnalysisIsOff()
        {
            var (h, m) = await StartAsync();
            await using var _h = h;

            // Only his own message chosen, nobody named: refused, saying how to include himself.
            string refusedRun = await h.Runtime.Tactics.StartAsync(ExtractFromSelectionTactic.Key, Input(new[] { m["klives"] }), "test");
            await InquiryTests.DriveAsync(h);
            var refused = h.Runtime.Tactics.Get(refusedRun)!;
            Assert.Equal(TacticRunStates.Failed, refused.State);
            Assert.Contains("Choose yourself", refused.Error);

            // Chosen as a person too: read.
            await InquiryTests.RunAsync(h, ExtractFromSelectionTactic.Key, Input(new[] { m["klives"] }, new[] { Klives }));
            Assert.Equal(new[] { "Bath" }, Cities(h, Klives));

            // Analysis switched off for Ben: his message is not read, and nothing about him is stored.
            await h.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { Ben }, h.Clock.NowMs));
            string offRun = await h.Runtime.Tactics.StartAsync(ExtractFromSelectionTactic.Key, Input(new[] { m["ben"] }), "test");
            await InquiryTests.DriveAsync(h);
            Assert.Equal(TacticRunStates.Failed, h.Runtime.Tactics.Get(offRun)!.State);
            Assert.Empty(Cities(h, Ben));
        }

        [Fact]
        public async Task KnowledgeRemovedFromUnderSomeone_RebuildsTheirProfile_AndWhatIsProjectedFromIt()
        {
            var (h, _) = await StartAsync();
            await using var _h = h;
            // A forget run removed rows from under Ana (a claim that cited the forgotten person, or a fact that named them).
            await h.Stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, h.Clock.NowMs, ChangeKinds.EvidenceRemoved, Ana, Ana, null, SourceKinds.Operator));
            await h.Runtime.Host.PumpChangesOnceAsync();
            await h.Runtime.Host.PlanOnceAsync();
            var job = Assert.Single(h.Runtime.Host.Jobs.List(JobStates.Queued, DeductionModule.Id, 20), j => j.Kind == DeductionModule.ReprojectKind);
            Assert.Equal(Ana, job.SubjectId);

            await h.Module<DeductionModule>().ExecuteAsync(h.JobFor(job));
            // Nothing changed in the consolidation itself (the rows were already gone), yet the profile is written again and
            // the timeline and plans are projected again: they may still show what was removed.
            using var c = h.Stores.Intel.OpenRead();
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM read_models WHERE model_id=$m AND key=$k", ("$m", DeductionModule.ProfileReadModel), ("$k", Ana)));
            foreach (string module in new[] { KnowledgeSchema.TimelineModule, KnowledgeSchema.PlansModule })
                Assert.Single(h.Runtime.Host.Jobs.List(JobStates.Queued, module, 20), j => j.Kind == "project" && j.SubjectId == Ana);
        }

        [Fact]
        public void X17_Validates_TheSelectionAndItsSize()
        {
            var tactic = new ExtractFromSelectionTactic(new Omnipotent.Services.OmniscienceV3.Knowledge.Deduction.DeductionModule());
            Assert.Equal("X17", tactic.Descriptor.Code);
            Assert.Equal("Choose the messages to read.", tactic.Validate(new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { Ana }) })));
            Assert.Contains("at most", tactic.Validate(Input(Enumerable.Range(0, ExtractFromSelectionTactic.MaxMessages + 1).Select(i => "discord:m" + i))));
            Assert.Null(tactic.Validate(Input(new[] { "discord:m1" })));
            var estimate = tactic.Estimate(Input(Enumerable.Range(0, 600).Select(i => "discord:m" + i), new[] { Ana }));
            Assert.Equal(8, estimate.LlmCalls); // one person, two slices, four calls each
        }
    }
}
