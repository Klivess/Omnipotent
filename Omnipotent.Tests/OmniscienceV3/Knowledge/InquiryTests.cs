using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// A scripted model for the four model stages of an inquiry (understand, plan, reason, verify). It reads the dossier it
    /// is given, exactly as the real model would, so the tests see what the engine really put in front of it.
    /// </summary>
    internal sealed class ScriptedInquirer
    {
        public Func<FakeLlm.FakeCall, object> Understand { get; set; } = _ => new { restatement = "Does Ava Lindqvist have a cat?", answer_type = "binary", subjects = new[] { new { person_id = "planted-ava" } }, topic = "cats" };
        public object Plan { get; set; } = new
        {
            facets = new[] { new { name = "pets", kind = "direct", queries = new[] { "cat", "pet" }, counter_queries = new[] { "dog", "allergic" } } },
            base_rate = new { probability = 0.3, rationale = "Roughly a third of households keep a cat." },
        };
        /// <summary>(pass index, dossier: id → line) → the pass's answer.</summary>
        public Func<int, IReadOnlyDictionary<string, string>, object> Pass { get; set; } = (_, dossier) => new
        {
            answer = "yes", probability = 0.9, evidence_for = dossier.Where(kv => kv.Value.Contains("cat", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray(),
            evidence_against = Array.Empty<string>(), reasoning = "She says she has a cat.", would_change = new[] { "A message saying the cat was rehomed." },
            questions_to_ask = new[] { "How is the cat doing?" },
        };
        public Func<IReadOnlyDictionary<string, string>, object> Devil { get; set; } = _ => new { argument = "Nothing in the dossier argues otherwise.", cites = Array.Empty<string>(), strength = "weak" };
        /// <summary>(evidence id, the point it was cited for) → whether the citation holds.</summary>
        public Func<string, string, bool> Supports { get; set; } = (_, _) => true;
        public List<IReadOnlyDictionary<string, string>> Dossiers { get; } = new();

        public void Wire(FakeLlm llm)
        {
            llm.On("inquiry.understand", c => JsonSerializer.Serialize(Understand(c)));
            llm.On("inquiry.plan", _ => JsonSerializer.Serialize(Plan));
            llm.On("inquiry.reason", c =>
            {
                var dossier = Dossier(c.Prompt);
                if (c.Seed == 999) return JsonSerializer.Serialize(Devil(dossier));
                lock (Dossiers) Dossiers.Add(dossier);
                return JsonSerializer.Serialize(Pass(c.Seed - 100, dossier));
            });
            llm.On("inquiry.verify", c => JsonSerializer.Serialize(new
            {
                checks = Regex.Matches(c.Question, @"^(E\d+) — (.+)$", RegexOptions.Multiline)
                    .Select(m => new { id = m.Groups[1].Value, point = m.Groups[2].Value.Trim(), supports = Supports(m.Groups[1].Value, m.Groups[2].Value.Trim()) }).ToArray(),
            }));
        }

        /// <summary>The numbered dossier lines of a reasoning prompt.</summary>
        public static Dictionary<string, string> Dossier(string prompt)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(prompt, @"^(E\d+) (?!—)(.*)$", RegexOptions.Multiline)) map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            return map;
        }
    }

    /// <summary>The inquiry engine (plan X-inquiry, "ask anything → a deduction job") through the tactics that start it.</summary>
    public class InquiryTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L;

        private static TacticInput Ask(string question, int passes = 3, string[]? people = null, long? asOf = null, object? extra = null)
        {
            var parameters = new Dictionary<string, object?> { ["question"] = question, ["passes"] = passes };
            if (extra != null)
                foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) parameters[p.Name] = p.Value;
            var selections = people == null ? Array.Empty<Selection>() : new[] { Selection.Of(SelectionKind.Person, people) };
            return new TacticInput(selections, JsonSerializer.SerializeToElement(parameters), asOf);
        }

        /// <summary>Runs every queued job once (a failed job is recorded as failed, as a worker would).</summary>
        internal static async Task<List<string>> DriveAsync(KnowledgeHarness h)
        {
            var failures = new List<string>();
            for (int round = 0; round < 20; round++)
            {
                bool any = false;
                foreach (var lane in Enum.GetValues<Lane>())
                {
                    while (await h.Runtime.Host.Jobs.LeaseNextAsync(lane, "test", TimeSpan.FromMinutes(10)) is { } job)
                    {
                        any = true;
                        var handler = h.Runtime.Host.Resolve(job.ModuleId, job.Kind) ?? throw new InvalidOperationException($"No handler for {job.ModuleId}/{job.Kind}.");
                        try
                        {
                            string? result = await handler(h.JobFor(job));
                            await h.Runtime.Host.Jobs.CompleteAsync(job.JobId, "test", result);
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{job.ModuleId}/{job.Kind}: {ex.GetType().Name}: {ex.Message}");
                            await h.Runtime.Host.Jobs.FailAsync(job.JobId, "test", ex is JobFailureException jf ? jf.Class : ErrorClass.Transient, ex.Message);
                        }
                    }
                }
                if (!any) break;
            }
            return failures;
        }

        internal static async Task<(TacticRunRecord Run, InquiryReportDto? Report, IReadOnlyList<TacticOutput> Outputs)> RunAsync(KnowledgeHarness h, string tactic, TacticInput input)
        {
            string runId = await h.Runtime.Tactics.StartAsync(tactic, input, "test");
            var failures = await DriveAsync(h);
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, $"run {run.State}: {run.Error} {string.Join(" | ", failures)}");
            var outputs = JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
            var answer = outputs.FirstOrDefault(o => o.Kind == TacticOutputKind.Answer)?.Data;
            return (run, answer is { } data ? data.Deserialize<InquiryReportDto>(KernelJson.Options) : null, outputs);
        }

        private static async Task DeduceAsync(KnowledgeHarness h, PlantedCorpus corpus, params string[] people)
        {
            var model = new PlantedExtractor(corpus);
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Seed is 1 or 2) model.SeePrompt(call.Prompt);
                return model.Answer(call);
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            var deduction = h.Module<DeductionModule>();
            foreach (var p in people) await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ExtractKind, p));
            foreach (var p in people) await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ConsolidateKind, p));
        }

        [Fact]
        public async Task YesNo_AnswersFromThePersonsOwnWords_CitesThem_AndStoresAnInferredAnswer()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new ScriptedInquirer();
            model.Wire(h.Llm);

            var (run, report, outputs) = await RunAsync(h, "x01.ask_yes_no", Ask("Ava's obviously a crazy cat lady, right?"));
            Assert.NotNull(report);
            Assert.Equal("Does Ava Lindqvist have a cat?", report!.Restatement); // the neutral restatement, not the loaded question
            Assert.Equal(InquiryVerdict.Yes, report.Verdict);
            Assert.InRange(report.Probability!.Value, 0.85, 0.99);
            Assert.True(report.BandLow < report.Probability && report.BandHigh >= report.Probability);
            Assert.False(report.Calibrated); // no gold yet: the raw probability, and it says so
            Assert.Equal(3, report.Passes);
            Assert.Equal(0, report.PassesDropped);
            var cited = Assert.Single(report.EvidenceFor);
            Assert.Contains("Miso", cited.Quote);
            Assert.NotNull(cited.MessageId);
            Assert.Equal("self", cited.Reliability);
            Assert.Equal("DM · discord", cited.Where);
            Assert.Empty(report.EvidenceAgainst);
            Assert.Contains("A message saying the cat was rehomed.", report.WouldChange);
            Assert.Contains("How is the cat doing?", report.QuestionsToAsk);
            Assert.Equal(2, report.EvidenceStrength); // one sincere statement by the person herself
            Assert.NotNull(report.PriorProbability);  // thin evidence: the base rate is shown beside the answer, labelled as a prior
            Assert.Contains("Ava Lindqvist", report.Coverage.Subjects);
            Assert.False(report.Coverage.VectorSearch);
            Assert.Contains(report.Coverage.Notes, n => n.Contains("Vector search was not available"));

            // The answer is an inferred assertion (never a fact) that can be confirmed or rejected, and the run offers it.
            Assert.NotNull(report.AssertionId);
            using (var c = h.Stores.Intel.OpenRead())
            {
                var stored = c.Query(null, "SELECT status, attribute, source_kind FROM assertions WHERE assertion_id=$a", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$a", report.AssertionId)).Single();
                Assert.Equal(("inferred", "inquiry_answer", "inferred"), stored);
                var row = c.Query(null, "SELECT state, verdict, tactic_key, primary_subject_id FROM kn_inquiries WHERE inquiry_id=$i",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$i", report.InquiryId)).Single();
                Assert.Equal(("answered", "Yes", "x01.ask_yes_no", "planted-ava"), row);
            }
            Assert.Contains(outputs, o => o.Kind == TacticOutputKind.MessageSet && o.Selection!.Items.Single().Id == cited.MessageId);
            Assert.Contains(outputs, o => o.Kind == TacticOutputKind.FactSet && o.Selection!.Items.Single().Id == report.AssertionId);

            // One understanding, one plan, three passes and the advocate on one dossier, one verification.
            Assert.Equal(1, h.Llm.CountPurpose("inquiry.understand"));
            Assert.Equal(1, h.Llm.CountPurpose("inquiry.plan"));
            Assert.Equal(4, h.Llm.CountPurpose("inquiry.reason"));
            Assert.Equal(1, h.Llm.CountPurpose("inquiry.verify"));
            // Every pass read the same dossier, in a different order after the first (position bias).
            var reason = h.Llm.Calls.Where(c => c.Purpose == "inquiry.reason" && c.Seed != 999).ToList();
            Assert.Single(reason.Select(c => c.Prompt[..c.Prompt.IndexOf("Answer the question.", StringComparison.Ordinal)]).Distinct());
            // Corpus text reaches the model fenced as data.
            Assert.Contains("<corpus-data>", reason[0].Prompt);
            Assert.Equal(TacticRunStates.Succeeded, run.State);
        }

        /// <summary>
        /// 18 Nov 2026 on the server: with the shared encoder installed, x01 failed in its gather stage ("Must add values for the
        /// following parameters: $model") after 23 minutes and three attempts. Every test above runs without an encoder.
        /// </summary>
        [Fact]
        public async Task YesNo_WithTheSharedEncoderInstalled_AlsoSearchesTheMessageVectors()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            List<(string Id, string Text)> messages;
            using (var c = h.Stores.Corpus.OpenRead())
                messages = c.Query(null, "SELECT message_id, content FROM messages WHERE content IS NOT NULL", r => (r.GetString(0), r.GetString(1)));
            // Every message embedded as the classifier files it (under the encoder's version). Ava's Portuguese message shares no
            // word with the plan's queries; filed with the vector of "cat", only the vector half can bring it into the dossier.
            var portuguese = messages.Single(m => m.Text.StartsWith("I speak Portuguese", StringComparison.Ordinal));
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion,
                messages.Select(m => new VectorItem(m.Id, m.Id == portuguese.Id ? FakeEncoder.Embed("cat") : FakeEncoder.Embed(m.Text), 1)).ToList());
            var model = new ScriptedInquirer();
            model.Wire(h.Llm);

            var (run, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Ava's obviously a crazy cat lady, right?"));

            Assert.Equal(TacticRunStates.Succeeded, run.State);
            Assert.True(report!.Coverage.VectorSearch);
            Assert.DoesNotContain(report.Coverage.Notes, n => n.Contains("Vector search was not available"));
            Assert.Contains(model.Dossiers[0].Values, line => line.Contains("I speak Portuguese"));
        }

        /// <summary>
        /// 22 Nov 2026 on the server: every inquiry was stopped in its gather stage, "made no progress for 60 min", three
        /// attempts each. The stage said "running" once and nothing more while it made up to thirty-two searches, and each
        /// search read the vectors of its scope again because the search before it had been of another scope.
        /// </summary>
        [Fact]
        public async Task TheGatherStage_SaysWhatItIsDoingAtEverySearch_AndReadsEachScopesVectorsOnce()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            List<(string Id, string Text)> messages;
            using (var c = h.Stores.Corpus.OpenRead())
                messages = c.Query(null, "SELECT message_id, content FROM messages WHERE content IS NOT NULL", r => (r.GetString(0), r.GetString(1)));
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, messages.Select(m => new VectorItem(m.Id, FakeEncoder.Embed(m.Text), 1)).ToList());
            // Five facets, each with counter-evidence to look for.
            var model = new ScriptedInquirer
            {
                Plan = new
                {
                    facets = new[] { "pets", "home", "habits", "purchases", "plans" }.Select(name => new { name, kind = "direct", queries = new[] { "cat", name }, counter_queries = new[] { "dog", "allergic" } }).ToArray(),
                    base_rate = new { probability = 0.3, rationale = "Roughly a third of households keep a cat." },
                },
            };
            model.Wire(h.Llm);

            var (run, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Ava's obviously a crazy cat lady, right?"));

            Assert.True(report!.Coverage.VectorSearch);
            List<JsonElement> said;
            using (var c = h.Stores.Intel.OpenRead())
                said = c.Query(null, "SELECT payload_json FROM stream_events WHERE topic='tactic.progress' ORDER BY event_id", r => JsonDocument.Parse(r.GetString(0)).RootElement)
                    .Where(e => e.GetProperty("runId").GetString() == run.RunId).Select(e => e.GetProperty("detail"))
                    .Where(d => d.TryGetProperty("stage", out var stage) && stage.GetString() == "gather" && d.TryGetProperty("doing", out _)).ToList();
            // Before the first search, twice a facet (her own words, her conversations and what she reacted to in one
            // search, then her own words for the opposite), and once more when the searching is done.
            var told = said.Where(d => d.GetProperty("facet").ValueKind == JsonValueKind.String).ToList();
            Assert.Equal(10, told.Count);
            Assert.Equal(new[] { "habits", "home", "pets", "plans", "purchases" }, told.Select(d => d.GetProperty("facet").GetString()).Distinct().OrderBy(f => f, StringComparer.Ordinal));
            Assert.Equal(5, told.Count(d => d.GetProperty("doing").GetString() == "searching their own messages for the opposite"));
            Assert.Equal(5, told.Count(d => d.GetProperty("doing").GetString()!.StartsWith("searching their own messages, the conversations they are in", StringComparison.Ordinal)));
            Assert.All(said, d => Assert.Equal(5, d.GetProperty("of").GetInt32()));
            var last = said[^1];
            // Every scope searched is counted: three or four a facet (her own, the opposite, her conversations, what she reacted to).
            Assert.Equal(5, last.GetProperty("facets").GetInt32());
            Assert.InRange(last.GetProperty("searches").GetInt32(), 15, 20);
            Assert.True(last.GetProperty("found").GetInt32() > 0);
            Assert.DoesNotContain(report.Coverage.Notes, n => n.Contains("time allowed", StringComparison.Ordinal) || n.Contains("not searched", StringComparison.Ordinal));
            // One read of each scope's vectors for all five facets (it was one for every search but the counter-evidence one),
            // one encoding of each facet's questions and of the opposite's (one for every search), and each word of the plan
            // read from the full-text index once: "cat", the five facet names, "dog" and "allergic", with their plain plurals.
            Assert.InRange(h.Kit.Retriever.CandidateLoads, 1, 3);
            Assert.InRange(h.Kit.Retriever.QueryEncodings, 1, 6);
            Assert.InRange(h.Kit.Retriever.TermReads, 8, 16);
        }

        /// <summary>
        /// 22 Nov 2026 on the server: the inquiries that did finish took four to five hours, nearly all of it looking for
        /// evidence on a drive where what is not in memory takes a tenth of a second a page. The stage has a budget now.
        /// With none at all it still searches its first facet, and the answer says what was not searched.
        /// </summary>
        [Fact]
        public async Task WithNoTimeToLookForEvidence_AnInquirySearchesItsFirstFacet_Answers_AndSaysWhatWasNotSearched()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            await h.Context.Settings.SetAsync(InquiryModule.GatherSecondsSetting, 0, "test");
            var model = new ScriptedInquirer
            {
                Plan = new
                {
                    facets = new[] { "pets", "home", "habits" }.Select(name => new { name, kind = "direct", queries = new[] { "cat", name }, counter_queries = new[] { "dog", "allergic" } }).ToArray(),
                    base_rate = new { probability = 0.3, rationale = "Roughly a third of households keep a cat." },
                },
            };
            model.Wire(h.Llm);

            var (run, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Ava's obviously a crazy cat lady, right?"));

            Assert.Equal(TacticRunStates.Succeeded, run.State);
            // Her own words about the cat were found by the first wording of the first facet, and are what the answer cites.
            Assert.NotEmpty(report!.EvidenceFor);
            Assert.True(report.Coverage.ItemsInDossier > 0);
            Assert.Contains(report.Coverage.Notes, n => n.Contains("given 0 s", StringComparison.Ordinal) && n.Contains("2 of the 3 facets planned were not searched", StringComparison.Ordinal));
            List<JsonElement> told;
            using (var c = h.Stores.Intel.OpenRead())
                told = c.Query(null, "SELECT payload_json FROM stream_events WHERE topic='tactic.progress' ORDER BY event_id", r => JsonDocument.Parse(r.GetString(0)).RootElement)
                    .Where(e => e.GetProperty("runId").GetString() == run.RunId).Select(e => e.GetProperty("detail"))
                    .Where(d => d.TryGetProperty("stage", out var stage) && stage.GetString() == "gather" && d.TryGetProperty("doing", out _) && d.GetProperty("facet").ValueKind == JsonValueKind.String).ToList();
            Assert.Equal(new[] { "pets" }, told.Select(d => d.GetProperty("facet").GetString()).Distinct().ToArray());
        }

        [Fact]
        public async Task Citations_ThatDoNotExistOrDoNotSupportThePoint_AreRemoved_AndPassesLeftWithNoneAreDropped()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new ScriptedInquirer();
            model.Pass = (i, dossier) =>
            {
                string real = dossier.First(kv => kv.Value.Contains("Miso")).Key;
                return i switch
                {
                    0 => new { answer = "yes", probability = 0.95, evidence_for = new[] { "E999", "[e404]" }, evidence_against = Array.Empty<string>(), reasoning = "Invented references." },
                    1 => new { answer = "yes", probability = 0.9, evidence_for = new[] { real }, evidence_against = Array.Empty<string>(), reasoning = "Cites the statement, wrongly judged." },
                    _ => new { answer = "yes", probability = 0.8, evidence_for = new[] { "[" + real.ToLowerInvariant() + "]" }, evidence_against = Array.Empty<string>(), reasoning = "Cites the statement." },
                };
            };
            // The verifier rejects every citation made for a probability-0.9 "yes"… which it cannot tell apart, so it rejects by point text:
            // all three real citations are for the same point, so it rejects none here; pass 0 has nothing real to check.
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Ava have a cat?"));
            Assert.Equal(2, report!.Passes);
            Assert.Equal(1, report.PassesDropped);
            Assert.Single(report.EvidenceFor);

            // Now the verifier says the cited message does not support the point: every pass loses its citation and is dropped.
            await using var h2 = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h2.Stores, Now);
            var strict = new ScriptedInquirer { Supports = (_, _) => false };
            strict.Wire(h2.Llm);
            var (_, none, outputs) = await RunAsync(h2, "x01.ask_yes_no", Ask("Does Ava have a cat?"));
            Assert.Equal(InquiryVerdict.InsufficientEvidence, none!.Verdict);
            Assert.Equal(0, none.Passes);
            Assert.Equal(3, none.PassesDropped);
            Assert.Null(none.Probability);          // no deduction: no probability
            Assert.Equal(0.3, none.PriorProbability); // only the labelled prior
            Assert.Null(none.AssertionId);
            Assert.DoesNotContain(outputs, o => o.Kind == TacticOutputKind.FactSet);
        }

        [Fact]
        public async Task PassesThatDisagree_PullTheAnswerTowardsUncertain_AndEvidenceIsFiledByWhatItSupports()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("nia", "Nia Brooks", tier: "tracked");
            await corpus.ConversationAsync("discord:c-nia", "dm", "me", "nia");
            await corpus.MessageAsync("nia", "discord:c-nia", Now - 400 * Day, "I love football, never miss a match");
            await corpus.MessageAsync("nia", "discord:c-nia", Now - 20 * Day, "honestly football bores me now, I stopped watching");
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Does Nia Brooks like football?", answer_type = "binary", subjects = new[] { new { person_id = "nia" } } },
                Plan = new { facets = new[] { new { name = "football", kind = "direct", queries = new[] { "football" }, counter_queries = new[] { "football bores" } } } },
                Pass = (i, dossier) =>
                {
                    string loves = dossier.First(kv => kv.Value.Contains("love football")).Key, bored = dossier.First(kv => kv.Value.Contains("bores me")).Key;
                    // Two passes say no (citing the recent message as their support), one says yes (citing the old one).
                    return i == 1
                        ? new { answer = "yes", probability = 0.8, evidence_for = new[] { loves }, evidence_against = new[] { bored }, reasoning = "She said she loves it." }
                        : new { answer = "no", probability = 0.2, evidence_for = new[] { bored }, evidence_against = new[] { loves }, reasoning = "More recently she lost interest." };
                },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Nia like football?"));
            Assert.Equal(3, report!.Passes);
            Assert.True(report.Disagreement > 0.2);
            // The median says no (0.2); the disagreement pulls it towards 0.5: a lean, not a verdict.
            Assert.InRange(report.Probability!.Value, 0.2, 0.5);
            Assert.Contains(report.Verdict, new[] { InquiryVerdict.LeansNo, InquiryVerdict.Uncertain });
            // "For" is for yes whatever each pass answered: the old message; "against" the recent one.
            Assert.Contains("love football", Assert.Single(report.EvidenceFor).Quote);
            Assert.Contains("bores me", Assert.Single(report.EvidenceAgainst).Quote);
        }

        [Fact]
        public async Task ANameThatMatchesSeveralPeople_StopsAndAsksWhich_AndASelectedPersonSkipsTheQuestion()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("sam-a", "Sam Carter");
            await corpus.PersonAsync("sam-b", "Sam Doyle");
            await corpus.ConversationAsync("discord:c-sam", "dm", "me", "sam-a");
            await corpus.MessageAsync("sam-a", "discord:c-sam", Now - 5 * Day, "football tonight, cannot wait");
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Does Sam like football?", answer_type = "binary", subjects = new[] { new { mention = "Sam" } } },
                Plan = new { facets = new[] { new { name = "football", kind = "direct", queries = new[] { "football" }, counter_queries = Array.Empty<string>() } } },
                Pass = (_, dossier) => new { answer = "yes", probability = 0.8, evidence_for = dossier.Keys.ToArray(), evidence_against = Array.Empty<string>(), reasoning = "He looks forward to it." },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Sam like football?"));
            Assert.Equal(InquiryVerdict.NeedsDisambiguation, report!.Verdict);
            var choice = Assert.Single(report.Disambiguation);
            Assert.Equal("Sam", choice.Mention);
            Assert.Equal(new[] { "sam-a", "sam-b" }, choice.Candidates.Select(c => c.PersonId).OrderBy(x => x));
            Assert.Equal(0, h.Llm.CountPurpose("inquiry.plan")); // nothing was searched or reasoned about the wrong person
            Assert.Equal(0, h.Llm.CountPurpose("inquiry.reason"));

            var (_, chosen, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Sam like football?", people: new[] { "sam-a" }));
            Assert.Equal(InquiryVerdict.LeansYes, chosen!.Verdict);
            Assert.Equal(new[] { "Sam Carter" }, chosen.Coverage.Subjects);
        }

        [Fact]
        public async Task SpecialCategoryQuestions_AreAnsweredLikeAnyOther()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            await DeduceAsync(h, corpus, "planted-ben");
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Is Ben Okafor religious?", answer_type = "binary", subjects = new[] { new { person_id = "planted-ben" } }, sensitive_category = "religion" },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Is Ben religious?"));
            Assert.NotEqual(InquiryVerdict.Refused, report!.Verdict);
            Assert.DoesNotContain("doesn't infer", report.PolicyNote ?? "");
            Assert.True(h.Llm.CountPurpose("inquiry.reason") > 0);
            using var c = h.Stores.Intel.OpenRead();
            Assert.NotEqual("refused", c.Scalar<string>(null, "SELECT state FROM kn_inquiries WHERE inquiry_id=$i", ("$i", report.InquiryId)));
        }

        [Fact]
        public async Task QuestionsAboutAMinorsFactsOrRoutine_AreAnswered_LikeAnyOther()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            await DeduceAsync(h, corpus, "planted-cam"); // "I'm 14 and in year 9": a minor from his own words
            var model = new ScriptedInquirer
            {
                Understand = c => c.Question.Contains("school", StringComparison.Ordinal) || c.Prompt.Contains("Which school", StringComparison.Ordinal)
                    ? new { restatement = "Which school does Cam attend?", answer_type = "open", subjects = new[] { new { person_id = "planted-cam" } }, restricted_attribute = (string?)"school", routine_inference = false }
                    : c.Prompt.Contains("usually online", StringComparison.Ordinal)
                        ? new { restatement = "When is Cam usually online?", answer_type = "open", subjects = new[] { new { person_id = "planted-cam" } }, restricted_attribute = (string?)null, routine_inference = true }
                        : new { restatement = "Does Cam like Minecraft?", answer_type = "binary", subjects = new[] { new { person_id = "planted-cam" } }, restricted_attribute = (string?)null, routine_inference = false },
                Plan = new { facets = new[] { new { name = "games", kind = "direct", queries = new[] { "Minecraft" }, counter_queries = Array.Empty<string>() } } },
                Pass = (_, dossier) => new { answer = "yes", probability = 0.9, evidence_for = dossier.Where(kv => kv.Value.Contains("Minecraft")).Select(kv => kv.Key).ToArray(), evidence_against = Array.Empty<string>(), reasoning = "He says so." },
            };
            model.Wire(h.Llm);

            var (_, school, _) = await RunAsync(h, "x02.ask_open", Ask("Which school does Cam go to?"));
            Assert.NotEqual(InquiryVerdict.Refused, school!.Verdict);
            var (_, routine, _) = await RunAsync(h, "x02.ask_open", Ask("When is Cam usually online?"));
            Assert.NotEqual(InquiryVerdict.Refused, routine!.Verdict);
            Assert.True(h.Llm.CountPurpose("inquiry.reason") > 0);

            var (_, likes, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Cam like Minecraft?"));
            Assert.Equal(InquiryVerdict.Yes, likes!.Verdict);
            // The dossier a minor's inquiry reads carries his school and city like anyone else's facts.
            Assert.Contains(model.Dossiers, d => d.Values.Any(v => v.Contains("[fact about") && (v.Contains("Hillcrest") || v.Contains("Rotherham"))));
        }

        [Fact]
        public async Task AsOf_ThePastSeesOnlyWhatHadBeenSaidByThen()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            long t0 = Now - 400 * Day;
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Does Dee Marsh live in Bristol?", answer_type = "binary", subjects = new[] { new { person_id = "planted-dee" } } },
                Plan = new { facets = new[] { new { name = "home", kind = "direct", queries = new[] { "live in", "moved to" }, counter_queries = new[] { "Glasgow" } } } },
                Pass = (_, dossier) =>
                {
                    var bristol = dossier.Where(kv => kv.Value.Contains("Bristol")).Select(kv => kv.Key).ToArray();
                    var glasgow = dossier.Where(kv => kv.Value.Contains("Glasgow")).Select(kv => kv.Key).ToArray();
                    return glasgow.Length > 0
                        ? new { answer = "no", probability = 0.1, evidence_for = glasgow, evidence_against = bristol, reasoning = "She moved.", temporal_note = (string?)"Lived in Bristol until she moved to Glasgow." }
                        : new { answer = "yes", probability = 0.9, evidence_for = bristol, evidence_against = glasgow, reasoning = "She says so.", temporal_note = (string?)null };
                },
            };
            model.Wire(h.Llm);

            long then = t0 + 100 * Day;
            var (_, past, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Dee live in Bristol?", asOf: then));
            Assert.Equal(InquiryVerdict.Yes, past!.Verdict);
            Assert.Equal(then, past.AsOfMs);
            Assert.Equal(then, past.Coverage.ToMs);
            Assert.All(model.Dossiers, d => Assert.DoesNotContain(d.Values, v => v.Contains("Glasgow"))); // nothing from after the as-of reached the model
            Assert.All(past.EvidenceFor, e => Assert.True(e.AtMs <= then));

            model.Dossiers.Clear();
            var (_, now, _) = await RunAsync(h, "x01.ask_yes_no", Ask("Does Dee live in Bristol?"));
            Assert.Equal(InquiryVerdict.No, now!.Verdict);
            Assert.Contains("Glasgow", Assert.Single(now.EvidenceAgainst).Quote);
            Assert.Contains("Bristol", Assert.Single(now.EvidenceFor).Quote);
            Assert.Equal("Lived in Bristol until she moved to Glasgow.", now.TemporalNote);
        }

        [Fact]
        public async Task KnownFacts_AndJokesSetAside_ShapeTheDossier()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            await DeduceAsync(h, corpus, "planted-ava");
            // The joke is labelled non-literal by the classification ensemble (WS-C writes this label; here it is written directly).
            string joke = corpus.Messages.Single(m => m.Kind == PlantKind.Joke).MessageId;
            await h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, abstained, signals_json, model, model_version, as_of_ms, source_seq)
                VALUES('message',$m,1,'modality','joke',0.93,0,'{}','test','test.v1',$t,0)
                """, ("$m", joke), ("$t", Now)));
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "What does Ava Lindqvist do for a living?", answer_type = "open", subjects = new[] { new { person_id = "planted-ava" } } },
                Plan = new { facets = new[] { new { name = "work", kind = "direct", queries = new[] { "work", "nurse", "queen", "pilot" }, counter_queries = Array.Empty<string>() } } },
                Pass = (_, dossier) => new { answer = "nurse", probability = 0.9, evidence_for = dossier.Where(kv => kv.Value.Contains("nurse")).Select(kv => kv.Key).ToArray(), evidence_against = Array.Empty<string>(), reasoning = "Her own statement and the known fact." },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x02.ask_open", Ask("What does Ava do for a living?"));
            Assert.Equal(InquiryVerdict.Answered, report!.Verdict);
            var dossier = model.Dossiers[0];
            Assert.Contains(dossier.Values, v => v.StartsWith("[fact about Ava Lindqvist]", StringComparison.Ordinal) && v.Contains("nurse"));
            Assert.DoesNotContain(dossier.Values, v => v.Contains("queen of England"));
            Assert.True(report.Coverage.ExcludedAsJokeOrFiction >= 1);
            Assert.True(report.Coverage.FactsConsidered >= 1);
            Assert.Contains(report.EvidenceFor, e => e.Kind == "fact" && e.AssertionId != null);
            // The fact was deduced from that same statement, so citing both is one unit of evidence, not two.
            Assert.Contains(report.EvidenceFor, e => e.Kind == "message" && e.Quote.Contains("nurse"));
            Assert.Equal(2, report.EvidenceStrength);
        }

        [Fact]
        public async Task AFailedModelCall_FailsTheJob_AndTheRetryResumesAfterTheStagesAlreadyDone()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new ScriptedInquirer();
            model.Wire(h.Llm);
            int reasonCalls = 0;
            h.Llm.On("inquiry.reason", c =>
            {
                if (Interlocked.Increment(ref reasonCalls) == 1) throw new FakeLlm.OutageException();
                return JsonSerializer.Serialize(c.Seed == 999 ? model.Devil(ScriptedInquirer.Dossier(c.Prompt)) : model.Pass(c.Seed - 100, ScriptedInquirer.Dossier(c.Prompt)));
            });
            string runId = await h.Runtime.Tactics.StartAsync("x01.ask_yes_no", Ask("Does Ava have a cat?"), "test");
            var failures = await DriveAsync(h);
            Assert.Single(failures); // the outage is a failed job, not an "insufficient evidence" answer
            Assert.NotEqual(TacticRunStates.Succeeded, h.Runtime.Tactics.Get(runId)!.State);

            h.Clock.Advance(TimeSpan.FromMinutes(30)); // past the retry backoff
            Assert.Empty(await DriveAsync(h));
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            // Understanding, planning and gathering were not repeated.
            Assert.Equal(1, h.Llm.CountPurpose("inquiry.understand"));
            Assert.Equal(1, h.Llm.CountPurpose("inquiry.plan"));
            var report = JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)![0].Data!.Value.Deserialize<InquiryReportDto>(KernelJson.Options)!;
            Assert.Equal(InquiryVerdict.Yes, report.Verdict);
        }

        [Fact]
        public async Task Catalogue_HoldsX01ToX16_AndEveryTacticIsWellFormed()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var catalogue = h.Runtime.Tactics.Catalog();
            Assert.Equal(catalogue.Count, catalogue.Select(d => d.Key).Distinct().Count());
            foreach (var d in catalogue)
            {
                Assert.True(d.Accepts != null, $"{d.Key} accepts null");
                Assert.NotEmpty(d.Outputs);
                Assert.False(string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Description), d.Key);
                Assert.Matches(@"^X\d\d$", d.Code);
                Assert.True(d.MinSelections <= d.MaxSelections, d.Key);
                using var schema = JsonDocument.Parse(d.ParameterSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, $"{d.Key} names the unknown permission '{d.RequiredPermission}'");
            }
            var reasoning = catalogue.Where(d => d.Category == TacticCategory.Inquiry).Select(d => d.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
            Assert.Equal(Enumerable.Range(1, 16).Select(i => "X" + i.ToString("D2")), reasoning);
            // A person can be handed to every tactic that asks about people (the shape check reads what each accepts).
            foreach (var d in catalogue.Where(d => d.Category == TacticCategory.Inquiry && d.Accepts.Contains(SelectionKind.Person) && d.MinSelections <= 1))
                Assert.Null(TacticRunner.CheckShape(d, new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p" }) })));
        }

        [Fact]
        public async Task Choice_GivesADistributionOverTheOptions()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Does Ava Lindqvist prefer cats, dogs or neither?", answer_type = "choice", options = new[] { "cats", "dogs", "neither" }, subjects = new[] { new { person_id = "planted-ava" } } },
                Pass = (i, dossier) => new
                {
                    answer = "cats", probability = 0.8, distribution = new Dictionary<string, double> { ["cats"] = 0.8 - 0.05 * i, ["dogs"] = 0.1 + 0.05 * i, ["neither"] = 0.1 },
                    evidence_for = dossier.Where(kv => kv.Value.Contains("cat")).Select(kv => kv.Key).ToArray(), evidence_against = Array.Empty<string>(), reasoning = "She has a cat.",
                },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await RunAsync(h, "x02.ask_open", Ask("Cats, dogs or neither for Ava?", extra: new { options = new[] { "cats", "dogs", "neither" } }));
            Assert.Equal(InquiryVerdict.Answered, report!.Verdict);
            Assert.Equal(new[] { "cats", "dogs", "neither" }, report.Distribution.Select(d => d.Option).OrderBy(x => x));
            Assert.Equal("cats", report.Distribution[0].Option);
            Assert.Equal(1.0, report.Distribution.Sum(d => d.Probability), 2);
        }
    }
}
