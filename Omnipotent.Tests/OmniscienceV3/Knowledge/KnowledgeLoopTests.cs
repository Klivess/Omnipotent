using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Entities;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// The six knowledge modules run the way the service runs them: the kernel pumps the change log, each module plans its own
    /// jobs, the jobs run, schedules come due as the clock moves. Nothing here calls a module's internals to make it work.
    /// </summary>
    public class KnowledgeLoopTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L;

        private static void WireBackground(KnowledgeHarness h, PlantedCorpus corpus)
        {
            var extractor = new PlantedExtractor(corpus);
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Seed is 1 or 2) extractor.SeePrompt(call.Prompt);
                return extractor.Answer(call);
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
            h.Llm.On("identity.judge", _ => """{"verdict":"unsure","probability":0.5,"reasons":[]}""");
        }

        /// <summary>One turn of the kernel loop, driven by hand: pump, plan, run everything that is ready.</summary>
        private static async Task<List<string>> TurnAsync(KnowledgeHarness h)
        {
            while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
            await h.Runtime.Host.PlanOnceAsync();
            return await InquiryTests.DriveAsync(h);
        }

        [Fact]
        public async Task TheKernelLoop_DeducesAndProjectsThePlantedCorpus_WithNoFailedJob()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            WireBackground(h, corpus);
            var failures = new List<string>();
            for (int round = 0; round < 10; round++)
            {
                failures.AddRange(await TurnAsync(h));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }
            // The next day (daily schedules: calibration, the lab, the agenda) and a week on (the identity scan).
            foreach (var step in new[] { TimeSpan.FromHours(26), TimeSpan.FromDays(7) })
            {
                h.Clock.Advance(step);
                failures.AddRange(await TurnAsync(h));
                failures.AddRange(await TurnAsync(h));
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));

            using var c = h.Stores.Intel.OpenRead();
            var jobs = c.Query(null, "SELECT module_id, kind, state, COUNT(*) FROM jobs GROUP BY module_id, kind, state", r => (Module: r.GetString(0), Kind: r.GetString(1), State: r.GetString(2), N: r.GetInt64(3)));
            string summary = string.Join("; ", jobs.Select(j => $"{j.Module}/{j.Kind} {j.State} ×{j.N}"));
            Assert.DoesNotContain(jobs, j => j.State == JobStates.Dead);
            // Every module did its own work without being called directly.
            foreach (var (module, kind) in new[]
                     {
                         (KnowledgeSchema.DeductionModule, "extract"), (KnowledgeSchema.DeductionModule, "detective"), (KnowledgeSchema.TimelineModule, "project"),
                         (KnowledgeSchema.PlansModule, "project"), (KnowledgeSchema.QualityModule, "lab"), (KnowledgeSchema.IdentityModule, "scan"),
                     })
                Assert.True(jobs.Any(j => j.Module == module && j.Kind == kind && j.State == JobStates.Succeeded), $"no succeeded {module}/{kind} job. Jobs: {summary}");

            // Facts for the tracked people, including the minor's school and city; the relocation is a timeline entry.
            var ava = h.Kit.Assertions.Query(new AssertionQuery { SubjectIds = new[] { "planted-ava" }, AtMs = h.Clock.NowMs, Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed }, Limit = 100 });
            Assert.Contains(ava, v => v.Record.Attribute == "city" && v.Record.ValueText == "Leeds");
            Assert.Contains(ava, v => v.Record.Attribute == "job");
            Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions WHERE subject_id='planted-cam' AND attribute IN ('school','city')") > 0,
                "The minor's school and city are ordinary facts now.");
            Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_life_events WHERE person_id='planted-dee'") > 0, "Dee's move should be a life event. Jobs: " + summary);
            // Ava's promise to Klives and her birthday are on the agenda.
            var plans = c.Query(null, "SELECT kind, status FROM kn_plans WHERE owner_person_id='planted-ava'", r => (Kind: r.GetString(0), Status: r.GetString(1)));
            Assert.Contains(plans, p => p.Kind == "promise");
            Assert.Contains(plans, p => p.Kind is "birthday" or "date");
            // The promise was for the day after it was made, over a year ago, and nothing says it was kept: overdue.
            Assert.Contains(plans, p => p.Kind == "promise" && p.Status == "overdue");
        }

        [Fact]
        public async Task TwoAccountsOfOnePerson_AreSuggestedAndNeverMergedUnasked_AndAMergeCanBeUndone()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee-a", "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync("dee-b", "dee_marsh");
            await corpus.PersonAsync("eve", "Eve Stone");
            await corpus.ConversationAsync("discord:c-a", "dm", "me", "dee-a");
            await corpus.ConversationAsync("discord:c-b", "dm", "me", "dee-b");
            await corpus.ConversationAsync("discord:c-e", "dm", "me", "eve");
            await corpus.MessageAsync("dee-a", "discord:c-a", Now - 30 * Day, "this one is so good https://example.org/rare/recipe-41");
            await corpus.MessageAsync("dee-b", "discord:c-b", Now - 3 * Day, "new account, same me. still making https://example.org/rare/recipe-41");
            await corpus.MessageAsync("eve", "discord:c-e", Now - 2 * Day, "hello there");
            int judged = 0;
            h.Llm.On("identity.judge", _ =>
            {
                Interlocked.Increment(ref judged);
                return """{"verdict":"same","probability":0.9,"reasons":["the same name and the same rare link"]}""";
            });
            var module = h.Module<IdentityModule>();
            await module.ExecuteAsync(h.Job(IdentityModule.Id, IdentityModule.ScanKind));

            var link = Assert.Single(module.Page(null, null, null, 50).Items);
            Assert.Equal(new[] { "dee-a", "dee-b" }, new[] { link.PersonA, link.PersonB }.OrderBy(x => x, StringComparer.Ordinal));
            Assert.True(link.Signals.Select(s => s.Kind).Distinct().Count() >= IdentityLinkEngine.MinIndependentSignals, "a suggestion needs two independent signals");
            Assert.Equal(1, judged); // Eve shares nothing with anyone: never even put to the model
            // A suggestion is only a suggestion.
            Assert.Equal("dee-b", h.Kit.Corpus.Canonical("dee-b"));
            Assert.Equal("dee-a", h.Kit.Corpus.Canonical("dee-a"));

            var (decided, merge) = await module.DecideAsync(link.LinkId, accept: true, "test", "same person", CancellationToken.None);
            Assert.NotNull(merge);
            Assert.True(merge!.Changed);
            Assert.Equal(h.Kit.Corpus.Canonical("dee-a"), h.Kit.Corpus.Canonical("dee-b"));
            Assert.Equal(merge.Survivor, h.Kit.Corpus.Canonical(merge.Absorbed));
            Assert.Equal(decided.LinkId, link.LinkId);
            using (var c = h.Stores.Corpus.OpenRead())
                Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM change_log WHERE kind=$k", ("$k", ChangeKinds.PersonMerge)));

            Assert.True(await module.UndoAsync(link.LinkId, "test", CancellationToken.None));
            Assert.Equal("dee-b", h.Kit.Corpus.Canonical("dee-b"));
            Assert.Equal("dee-a", h.Kit.Corpus.Canonical("dee-a"));
            using (var c = h.Stores.Corpus.OpenRead())
                Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM change_log WHERE kind=$k", ("$k", ChangeKinds.PersonUnmerge)));

            // X67 on two selected people compares only accounts already in the archive and returns a suggestion, never a merge.
            var input = new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "dee-a", "eve" }) });
            var (run, _, outputs) = await InquiryTests.RunAsync(h, SamePersonTactic.Key, input);
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            Assert.NotEmpty(outputs);
            Assert.Equal("eve", h.Kit.Corpus.Canonical("eve"));

            // X67 can also check an anonymous account by platform and user id (the former "never unmask" ban is gone).
            var anonymous = new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "dee-a" }) },
                System.Text.Json.JsonSerializer.SerializeToElement(new { platform = "discord", platform_user_id = "u-dee-b" }));
            var (anonRun, _, anonOutputs) = await InquiryTests.RunAsync(h, SamePersonTactic.Key, anonymous);
            Assert.Equal(TacticRunStates.Succeeded, anonRun.State);
            Assert.True(anonOutputs[0].Data!.Value.GetProperty("anonymous").GetBoolean());
            Assert.NotEmpty(anonOutputs[0].Data!.Value.GetProperty("assessments").EnumerateArray());
        }
    }
}
