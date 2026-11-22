using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Plans;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// People's names in what Klives reads. On 22 Nov 2026 the Agenda on the server read "meet with p9a05 to understand
    /// the project system", "play a game with p854e after bath", "group call and play Minecraft with p9a05 and p94f5":
    /// the model is given roster handles and wrote them into the text of its facts, which was stored as it came (29 of
    /// the newest 500 claims; p9a05 was Klives himself). Invented people and text only.
    /// </summary>
    public class HandleNamesTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L, Minute = 60_000L;
        private const string Me = "me", Dee = "dee", Bo = "bo";
        private const string Group = "discord:c-group";

        private static string Handle(string person) => HandleText.Of(person).Handle;

        [Fact]
        public void AHandleInText_IsReplacedByTheName_OnlyWhereItIsSomebodysHandle()
        {
            var names = new Dictionary<string, string> { ["p9a05"] = "Klives", ["p0ac2"] = "Bo Reed" };
            string? Name(string handle) => names.GetValueOrDefault(handle);

            Assert.Equal("meet with Klives to understand the project system", HandleText.Named("meet with p9a05 to understand the project system", Name));
            Assert.Equal("Bo Reed's server is bad; group call with Klives and p94f5", HandleText.Named("p0ac2's server is bad; group call with @p9a05 and p94f5", Name));
            Assert.Equal("Klives", HandleText.Named("p9a05", Name));
            Assert.Equal("Klives", HandleText.Named("P9A05", Name));
            // Words and numbers that have a handle's shape are asked about like any other, and nobody answers for them.
            foreach (string plain in new[] { "paced himself through the exam", "bought a p2000 card", "xp9a05", "p9a05x", "p9a056", "p9a0", "pdf of the cv" })
                Assert.Equal(plain, HandleText.Named(plain, Name));
            Assert.Equal(new[] { "p0ac2", "p9a05" }, HandleText.Handles("p0ac2 and @P9A05 and p9a05 again").OrderBy(h => h).ToArray());
            Assert.False(HandleText.MayHold("no one is named here"));
            // The handle a repair works a person out from is the handle a roster gives them.
            Assert.Equal(new Roster().HandleFor("some-person-id"), Handle("some-person-id"));
        }

        private static async Task<(string Plan, string Friend, string Joke)> SeedAsync(KnowledgeHarness h)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync(Me, "Klives", self: true);
            await corpus.PersonAsync(Dee, "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync(Bo, "Bo Reed");
            await corpus.ConversationAsync(Group, "group_dm", Me, Dee, Bo);
            long t = Now - 30 * Day;
            await corpus.MessageAsync(Me, Group, t, "morning both");
            await corpus.MessageAsync(Bo, Group, t + Minute, "morning, anyone about on friday?");
            string plan = await corpus.MessageAsync(Dee, Group, t + 2 * Minute, "I'll meet Klives on friday to plan the trip");
            string friend = await corpus.MessageAsync(Dee, Group, t + 3 * Minute, "Bo is my best friend, honestly");
            string joke = await corpus.MessageAsync(Dee, Group, t + 4 * Minute, "Bo's server is the worst thing ever made lol");
            return (plan, friend, joke);
        }

        /// <summary>A model that writes handles where it writes prose, as the real one does.</summary>
        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Question.Contains("sceptical reviewer", StringComparison.Ordinal))
                    return JsonSerializer.Serialize(new { verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "stated" }) });
                var roster = FakeLlm.Roster(call.Prompt);
                string dee = roster["Dee Marsh"], klives = roster["Klives"], bo = roster["Bo Reed"];
                var claims = new List<object>();
                foreach (var (reference, (handle, text)) in FakeLlm.EvidenceLines(call.Prompt))
                {
                    if (handle != dee) continue;
                    object Claim(string attribute, string value, string quote, string modality = "literal", string? other = null, object? plan = null) => new
                    {
                        id = "c" + (claims.Count + 1), subject = dee, attribute, value, @object = other, polarity = "affirmed", modality, source_kind = "self_disclosure",
                        speech_act = "statement", sincerity = "sincere", evidence = new[] { new { @ref = reference, quote } }, probability = 0.95, plan,
                    };
                    if (text.Contains("plan the trip")) claims.Add(Claim("plan", $"meet with {klives} on friday to plan the trip", "meet Klives on friday", plan: new { kind = "plan", firmness = "firm" }));
                    if (text.Contains("best friend")) claims.Add(Claim("friend", bo, "Bo is my best friend", other: bo));
                    if (text.Contains("worst thing")) claims.Add(Claim("opinion", $"{bo}'s server is the worst thing ever made", "Bo's server is the worst thing ever made", modality: "joke"));
                }
                return JsonSerializer.Serialize(new { claims });
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
        }

        private static async Task DriveAsync(KnowledgeHarness h, int rounds = 8)
        {
            for (int round = 0; round < rounds; round++)
            {
                while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
                await h.Runtime.Host.PlanOnceAsync();
                var failures = await InquiryTests.DriveAsync(h);
                Assert.True(failures.Count == 0, string.Join("\n", failures));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }
        }

        private static List<(string Attribute, string Verdict, string? Text, string? Norm)> Claims(KnowledgeHarness h)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT attribute, verdict, value_text, value_norm FROM kn_claims WHERE person_id=$p ORDER BY attribute", r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3)), ("$p", Dee));
        }

        private static List<(string Id, string Attribute, string Key, string? Text, string Json)> Facts(KnowledgeHarness h)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT assertion_id, attribute, natural_key, value_text, value_json FROM assertions WHERE subject_id=$p AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL ORDER BY attribute",
                r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4)), ("$p", Dee));
        }

        private static string PlanTitle(KnowledgeHarness h)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Scalar<string>(null, "SELECT title FROM kn_plans WHERE status <> 'cancelled'")!;
        }

        private static void AssertNamed(KnowledgeHarness h)
        {
            var claims = Claims(h);
            Assert.Equal(("verified", "Bo Reed"), (claims.Single(c => c.Attribute == "friend").Verdict, claims.Single(c => c.Attribute == "friend").Text));
            Assert.Equal(("verified", "meet with Klives on friday to plan the trip"), (claims.Single(c => c.Attribute == "plan").Verdict, claims.Single(c => c.Attribute == "plan").Text));
            // A claim that is turned away is kept for the audit, and reads the same way.
            Assert.Equal(("rejected", "Bo Reed's server is the worst thing ever made"), (claims.Single(c => c.Attribute == "opinion").Verdict, claims.Single(c => c.Attribute == "opinion").Text));
            var facts = Facts(h);
            Assert.Equal(new[] { "friend", "plan" }, facts.Select(f => f.Attribute).ToArray());
            Assert.Equal("Bo Reed", facts[0].Text);
            Assert.Equal("meet with Klives on friday to plan the trip", facts[1].Text);
            Assert.All(facts, f => Assert.Equal(f.Text, JsonDocument.Parse(f.Json).RootElement.GetProperty("text").GetString()));
            Assert.Equal("meet with Klives on friday to plan the trip", PlanTitle(h));
            // No handle of anyone here is left in what the pages are drawn from.
            using var c = h.Stores.Intel.OpenRead();
            foreach (string body in c.Query(null, "SELECT body FROM read_models WHERE model_id IN ($a,$b) AND key=$k", r => r.GetString(0),
                         ("$a", DeductionModule.ProfileReadModel), ("$b", PlansModule.PersonPlansModel), ("$k", Dee)))
                Assert.DoesNotContain(new[] { Handle(Me), Handle(Bo), Handle(Dee) }, handle => body.Contains(handle, StringComparison.Ordinal));
        }

        [Fact]
        public async Task ANewFact_NamesThePeopleItMentions_ThoughTheModelWroteTheirHandles()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h);
            await DriveAsync(h);
            AssertNamed(h);
            // The friend is still known as a person, not only as a name.
            using var c = h.Stores.Intel.OpenRead();
            Assert.Equal(Bo, c.Scalar<string>(null, "SELECT object_id FROM assertions WHERE subject_id=$p AND attribute='friend'", ("$p", Dee)));
        }

        /// <summary>What the server already holds: claims and facts stored with the handles in them, put right by the repair
        /// the migration asks for, with no model call and without a fact becoming another fact.</summary>
        [Fact]
        public async Task StoredHandles_AreReplacedByNames_AndEveryFactStaysTheSameFact()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h);
            await DriveAsync(h);

            // As the old code stored them: the text with the handles the model wrote, in the claims, in the facts, and in
            // everything projected from the facts.
            string me = Handle(Me), bo = Handle(Bo);
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (string table in new[] { "kn_claims", "assertions" })
                {
                    c.Exec(tx, $"UPDATE {table} SET value_text=replace(replace(value_text, 'Bo Reed', $bo), 'Klives', $me)", ("$bo", bo), ("$me", me));
                }
                c.Exec(tx, "UPDATE assertions SET value_json=json_set(value_json, '$.text', value_text)");
            });
            await h.Module<DeductionModule>().WriteProfileAsync(Dee, h.Kit.Corpus.MaxSeq(), CancellationToken.None);
            await h.Module<PlansModule>().ExecuteAsync(h.Job(KnowledgeSchema.PlansModule, PlansModule.ProjectKind, Dee));
            Assert.Equal($"meet with {me} on friday to plan the trip", PlanTitle(h));
            Assert.Equal(bo, Facts(h).Single(f => f.Attribute == "friend").Text);
            var before = Facts(h).ToDictionary(f => f.Attribute, f => (f.Id, f.Key));
            var normsBefore = Claims(h).Select(c => (c.Attribute, c.Norm)).ToList();
            string planBefore;
            using (var c = h.Stores.Intel.OpenRead()) planBefore = c.Scalar<string>(null, "SELECT plan_id FROM kn_plans")!;

            // The migration asks for the repair; the next plan queues it, and it runs beside the other work.
            await h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, KnowledgeSchema.Deduction.Single(m => m.Version == 4).Sql));
            int modelCalls = h.Llm.Calls.Count;
            await DriveAsync(h, rounds: 3);

            AssertNamed(h);
            // The same facts: their ids and keys, the keys of the claims under them, and the plan built on one of them.
            Assert.Equal(before, Facts(h).ToDictionary(f => f.Attribute, f => (f.Id, f.Key)));
            Assert.Equal(normsBefore, Claims(h).Select(c => (c.Attribute, c.Norm)).ToList());
            using (var c = h.Stores.Intel.OpenRead())
            {
                Assert.Equal(planBefore, c.Scalar<string>(null, "SELECT plan_id FROM kn_plans WHERE status <> 'cancelled'"));
                // (A new engine asks for the repair at its first start too, when there is nothing to repair: this is the one just asked for.)
                var job = c.Query(null, "SELECT lane, state, result_json FROM jobs WHERE module_id=$m AND kind=$k ORDER BY job_id DESC LIMIT 1", r => (r.GetString(0), r.GetString(1), r.GetString(2)),
                    ("$m", KnowledgeSchema.DeductionModule), ("$k", DeductionModule.HandlesKind)).Single();
                Assert.Equal(("Cpu", "succeeded"), (job.Item1, job.Item2));
                var result = JsonDocument.Parse(job.Item3).RootElement;
                Assert.True(result.GetProperty("done").GetBoolean());
                // The plan and the friend among the facts; those two and the opinion that was turned away among the claims.
                Assert.Equal((5, 0, 1), (result.GetProperty("rewritten").GetInt32(), result.GetProperty("leftAsHandles").GetInt32(), result.GetProperty("people").GetInt32()));
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM module_dirty WHERE subject_kind=$k", ("$k", KnowledgeSchema.HandlesDirtyKind)));
            }
            Assert.Equal(modelCalls, h.Llm.Calls.Count);

            // Asked for again, it finds nothing left to do.
            await h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, KnowledgeSchema.Deduction.Single(m => m.Version == 4).Sql));
            await DriveAsync(h, rounds: 2);
            using (var c = h.Stores.Intel.OpenRead())
                Assert.Equal(0, c.Query(null, "SELECT result_json FROM jobs WHERE module_id=$m AND kind=$k ORDER BY job_id DESC LIMIT 1", r => JsonDocument.Parse(r.GetString(0)).RootElement.GetProperty("rewritten").GetInt32(),
                    ("$m", KnowledgeSchema.DeductionModule), ("$k", DeductionModule.HandlesKind)).Single());
        }

        // ── the results of tactics ───────────────────────────────────────────────
        // A result is built from stored facts and from what the model wrote about them, so it carried the same handles:
        // an answer's reasoning, a list of promises, a report.

        [Fact]
        public async Task AResultAsJson_HasItsPeopleNamed_FromThePeopleAndPlacesItCarries()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (plan, _, _) = await SeedAsync(h);
            var namer = new HandleNamer(h.Kit.Corpus, h.Stores);
            string me = Handle(Me), dee = Handle(Dee), bo = Handle(Bo);

            // Bo is one of the people the result lists, Klives is always concerned, and Dee is in the conversation of the
            // message it cites. A property is never renamed, an id is never touched, and a number is written as it came.
            string json = $$"""{"summary":"{{bo}} lent {{me}} the tent","people":["{{Bo}}"],"items":[{"id":"{{plan}}","note":"ask @{{dee}} about it"}],"p2000":"a p2000 torch","count":3,"ratio":0.50,"none":null}""";
            var tally = new HandleNamer.Tally();
            string named = namer.Json(json, tally: tally);
            Assert.Equal($$"""{"summary":"Bo Reed lent Klives the tent","people":["{{Bo}}"],"items":[{"id":"{{plan}}","note":"ask Dee Marsh about it"}],"p2000":"a p2000 torch","count":3,"ratio":0.50,"none":null}""", named);
            Assert.Equal((3, 0), (tally.Named, tally.Left));

            // With nothing to say that Bo or Dee belongs to it, only Klives's handle is anybody's; the others stay.
            string bare = $$"""{"summary":"{{bo}} and {{dee}} lent {{me}} the tent"}""";
            tally = new HandleNamer.Tally();
            Assert.Equal($$"""{"summary":"{{bo}} and {{dee}} lent Klives the tent"}""", namer.Json(bare, tally: tally));
            Assert.Equal((1, 2), (tally.Named, tally.Left));
            // Told who it concerns, it names them.
            Assert.Equal("""{"summary":"Bo Reed and Dee Marsh lent Klives the tent"}""", namer.Json(bare, new[] { Bo, Dee }));

            // Nothing to do: the very same string back. Not JSON: left alone.
            string plain = """{"summary":"nobody is named here"}""";
            Assert.Same(plain, namer.Json(plain));
            string broken = $"{{\"summary\":\"{me} and";
            Assert.Same(broken, namer.Json(broken));
            Assert.Equal(new[] { Bo, Dee }, namer.PeopleIn($$"""{"selections":[{"kind":"Person","items":[{"id":"{{Bo}}"},{"id":"{{Dee}}"},{"id":"not-a-person"}]}]}""").OrderBy(p => p).ToArray());
        }

        private static Omnipotent.Services.OmniscienceV3.Kernel.Tactics.TacticInput Ask(string question) =>
            new(Array.Empty<Omnipotent.Services.OmniscienceV3.Kernel.Tactics.Selection>(), JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["question"] = question, ["passes"] = 3 }), null);

        private static (string? Outputs, string? Report, string? Restatement) Stored(KnowledgeHarness h, string runId)
        {
            using var c = h.Stores.Intel.OpenRead();
            string? outputs = c.Scalar<string>(null, "SELECT outputs_json FROM tactic_runs WHERE run_id=$r", ("$r", runId));
            var inquiry = c.Query(null, "SELECT report_json, restatement FROM kn_inquiries WHERE run_id=$r", r => (r.GetStringOrNull(0), r.GetStringOrNull(1)), ("$r", runId)).Single();
            return (outputs, inquiry.Item1, inquiry.Item2);
        }

        [Fact]
        public async Task ATacticsResult_NamesThePeople_ThoughTheModelWroteTheirHandles_AndStoredResultsArePutRight()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await Omnipotent.Services.OmniscienceV3.Knowledge.Quality.PlantedFactCorpus.WriteAsync(h.Stores, Now);
            const string Ava = "planted-ava", Klives = "planted-klives";
            string ava = Handle(Ava), klives = Handle(Klives);
            // A model that names people the way it met them in the facts it was shown: by handle.
            var model = new ScriptedInquirer
            {
                Devil = _ => new { argument = $"{ava} never says the cat is hers, and {klives} only asks about it once.", cites = Array.Empty<string>(), strength = "weak" },
            };
            var pass = model.Pass;
            model.Pass = (i, dossier) => JsonSerializer.SerializeToElement(new
            {
                answer = "yes", probability = 0.9, evidence_for = dossier.Where(kv => kv.Value.Contains("cat", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray(),
                evidence_against = Array.Empty<string>(), reasoning = $"{ava} says she has a cat.", would_change = new[] { $"A message from {ava} saying the cat was rehomed." },
                questions_to_ask = new[] { $"Ask {ava} how the cat is doing." },
            });
            model.Wire(h.Llm);
            // The engine's own background reading of these people finds nothing here: this is about the inquiry.
            h.Llm.On("deduction.extract", _ => """{"claims":[],"verdicts":[]}""");
            h.Llm.On("deduction.values", _ => """{"groups":[]}""");
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
            h.Llm.On("identity.judge", _ => """{"verdict":"unsure","probability":0.5,"reasons":[]}""");

            var (run, report, _) = await InquiryTests.RunAsync(h, "x01.ask_yes_no", Ask("Ava's obviously a crazy cat lady, right?"));

            // What Klives reads: the report, as the run's result and as the inquiry keeps it.
            string avaName = h.Kit.Corpus.DisplayName(Ava)!;
            Assert.Equal($"{avaName} never says the cat is hers, and Klives only asks about it once.", report!.DevilsAdvocate);
            Assert.Contains($"A message from {avaName} saying the cat was rehomed.", report.WouldChange);
            Assert.Contains($"Ask {avaName} how the cat is doing.", report.QuestionsToAsk);
            var stored = Stored(h, run.RunId);
            foreach (string? text in new[] { stored.Outputs, stored.Report })
            {
                Assert.DoesNotContain(ava, text);
                Assert.DoesNotContain(klives, text);
                Assert.Contains(avaName, text);
            }

            // As results were stored before: with the handles in them.
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE tactic_runs SET outputs_json=replace(replace(outputs_json, $name, $ava), 'Klives only', $k || ' only') WHERE run_id=$r", ("$name", avaName), ("$ava", ava), ("$k", klives), ("$r", run.RunId));
                c.Exec(tx, "UPDATE kn_inquiries SET report_json=replace(replace(report_json, $name, $ava), 'Klives only', $k || ' only'), restatement=$ava || ' has a cat?' WHERE run_id=$r",
                    ("$name", avaName), ("$ava", ava), ("$k", klives), ("$r", run.RunId));
            });
            Assert.Contains(ava, Stored(h, run.RunId).Outputs);
            Assert.Contains(klives, Stored(h, run.RunId).Report);

            // The repair is run as its job runs it, a stage at a time, and asks no model.
            int modelCalls = h.Llm.Calls.Count;
            var repair = new HandleRepair(h.Kit);
            var rewritten = new Dictionary<string, int>();
            foreach (string stage in HandleRepair.Stages)
            {
                var step = await repair.StepAsync(stage, "", 500, CancellationToken.None);
                Assert.Null(step.Next);
                Assert.Equal(0, step.LeftAsHandles);
                rewritten[stage] = step.Rewritten;
            }
            Assert.Equal((1, 1), (rewritten["runs"], rewritten["inquiries"]));

            stored = Stored(h, run.RunId);
            foreach (string? text in new[] { stored.Outputs, stored.Report })
            {
                Assert.DoesNotContain(ava, text);
                Assert.DoesNotContain(klives, text);
                Assert.Contains($"{avaName} never says the cat is hers, and Klives only asks about it once.", text);
            }
            Assert.Equal($"{avaName} has a cat?", stored.Restatement);
            // The report still reads as a report.
            var kept = JsonSerializer.Deserialize<Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry.InquiryReportDto>(stored.Report!, Omnipotent.Services.OmniscienceV3.Kernel.Jobs.KernelJson.Options)!;
            Assert.Equal((report.Verdict, report.Probability, report.EvidenceFor.Count), (kept.Verdict, kept.Probability, kept.EvidenceFor.Count));
            Assert.Equal(modelCalls, h.Llm.Calls.Count);
        }

        /// <summary>Two ids that are given the same handle (four hex digits are shared by several of an archive's people).</summary>
        private static (string First, string Second) Twins()
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; ; i++)
            {
                string id = "twin-" + i;
                if (seen.TryGetValue(Handle(id), out var other)) return (other, id);
                seen[Handle(id)] = id;
            }
        }

        [Fact]
        public async Task AHandleThatCouldBeSeveralPeople_IsOnlyReplaced_WhenOneOfThemBelongsToTheClaim()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h);
            await DriveAsync(h);
            var (first, second) = Twins();
            Assert.Equal(Handle(first), Handle(second));
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync(first, "Tam One");
            await corpus.PersonAsync(second, "Tam Two");
            string shared = Handle(first);

            async Task<string> RepairedAsync()
            {
                await h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE kn_claims SET value_text=$t WHERE person_id=$p AND attribute='plan'", ("$t", $"lend {shared} the tent and a p2000 torch"), ("$p", Dee)));
                var step = await new HandleRepair(h.Kit).ClaimsAsync("", 500, CancellationToken.None);
                Assert.Null(step.Next);
                return Claims(h).Single(c => c.Attribute == "plan").Text!;
            }

            // Neither of them has anything to do with the claim: the handle stays, and so does the word that only looks like one.
            Assert.Equal($"lend {shared} the tent and a p2000 torch", await RepairedAsync());

            // One of them is in the conversation the evidence comes from: it is theirs.
            await h.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',0)", ("$c", Group), ("$i", CorpusBuilder.Identity(second))));
            Assert.Equal("lend Tam Two the tent and a p2000 torch", await RepairedAsync());

            // Both are: it could be either, and a guess would put a wrong name in a fact.
            await h.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',0)", ("$c", Group), ("$i", CorpusBuilder.Identity(first))));
            Assert.Equal($"lend {shared} the tent and a p2000 torch", await RepairedAsync());
        }
    }
}
