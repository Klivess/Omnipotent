using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>The reasoning tactics X03–X16 (X01 and X02 are in <see cref="InquiryTests"/>), each run as a job on synthetic data.</summary>
    public class ReasoningTacticsTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L;
        private const string Ava = "planted-ava", Ben = "planted-ben", Cam = "planted-cam", Dee = "planted-dee", Klives = "planted-klives";

        private static TacticInput Input(object? parameters, string[]? people = null, long? asOf = null, Selection? also = null)
        {
            var selections = new List<Selection>();
            if (people != null) selections.Add(Selection.Of(SelectionKind.Person, people));
            if (also != null) selections.Add(also);
            return new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters), asOf);
        }

        private static async Task<(KnowledgeHarness H, PlantedCorpus Corpus, ScriptedInquirer Model)> StartAsync()
        {
            var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new ScriptedInquirer();
            model.Wire(h.Llm);
            return (h, corpus, model);
        }

        private static async Task DeduceAsync(KnowledgeHarness h, PlantedCorpus corpus, params string[] people)
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
            var deduction = h.Module<DeductionModule>();
            foreach (var p in people) await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ExtractKind, p));
            foreach (var p in people) await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ConsolidateKind, p));
        }

        /// <summary>The dossier items that contain one of the phrases as whole words.</summary>
        private static string[] Mentioning(IReadOnlyDictionary<string, string> dossier, params string[] words) =>
            dossier.Where(kv => words.Any(w => Regex.IsMatch(kv.Value, @"\b" + Regex.Escape(w) + @"\b", RegexOptions.IgnoreCase))).Select(kv => kv.Key).ToArray();

        [Fact]
        public async Task X03_X04_CompareAndRank_ScoreEachPersonOnTheirOwnEvidence_AndReturnARankedCohort()
        {
            var (h, _, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "Who is more likely to keep a pet?", answer_type = "ranking" };
            model.Pass = (i, dossier) => new
            {
                answer = "Ava Lindqvist", probability = 0.8, scores = new Dictionary<string, double> { ["Ava Lindqvist"] = 0.9 - 0.02 * i, ["Ben Okafor"] = 0.15, ["Dee Marsh"] = 0.3 },
                evidence_for = Mentioning(dossier, "cat"), evidence_against = Array.Empty<string>(), reasoning = "Ava has a cat; nothing says the others keep pets.",
            };
            var (_, compared, outputs) = await InquiryTests.RunAsync(h, "x03.compare_people", Input(new { question = "Who is more likely to keep a pet?" }, new[] { Ava, Ben }));
            Assert.Equal(InquiryVerdict.Answered, compared!.Verdict);
            Assert.Equal(new[] { Ava, Ben }, compared.Ranking.Select(r => r.PersonId));
            Assert.True(compared.Ranking[0].Score > compared.Ranking[1].Score);
            Assert.NotEmpty(compared.Ranking[0].EvidenceIds); // Ava's place rests on evidence about Ava
            Assert.Empty(compared.Ranking[1].EvidenceIds);
            var cohort = outputs.Single(o => o.Kind == TacticOutputKind.Cohort).Selection!;
            Assert.Equal(new[] { Ava, Ben }, cohort.Items.Select(x => x.Id));
            Assert.Equal(compared.Ranking[0].Score, cohort.Items[0].Score);

            var (_, ranked, _) = await InquiryTests.RunAsync(h, "x04.rank_cohort", Input(new { question = "Rank them by how likely they are to keep a pet." }, new[] { Ava, Ben, Dee }));
            Assert.Equal(new[] { Ava, Dee, Ben }, ranked!.Ranking.Select(r => r.PersonId));

            // One person cannot be compared or ranked: refused before anything runs.
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync("x03.compare_people", Input(new { question = "?" }, new[] { Ava }), "test"));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync("x04.rank_cohort", Input(new { question = "?" }, new[] { Ava }), "test"));
        }

        [Fact]
        public async Task X05_ExpertiseFinder_FindsWhoTalksAboutItAcrossTheArchive()
        {
            var (h, _, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "Who speaks Portuguese?", answer_type = "binary" };
            model.Plan = new { facets = new[] { new { name = "language", kind = "direct", queries = new[] { "Portuguese", "speak" }, counter_queries = Array.Empty<string>() } } };
            model.Pass = (_, dossier) => new { answer = "yes", probability = 0.9, evidence_for = Mentioning(dossier, "Portuguese"), evidence_against = Array.Empty<string>(), reasoning = "She grew up speaking it." };
            var (_, report, outputs) = await InquiryTests.RunAsync(h, "x05.expertise_finder", Input(new { question = "speaks Portuguese" }));
            Assert.Equal(InquiryVerdict.Yes, report!.Verdict);
            Assert.Equal(new[] { "Ava Lindqvist" }, report.Coverage.Subjects); // found by searching, not named in the question
            Assert.Contains("Portuguese", report.EvidenceFor[0].Quote);

            Assert.Contains(outputs, o => o.Kind == TacticOutputKind.MessageSet);
            // Nobody talks about it: no inquiry is run, and the tactic says so.
            int understood = h.Llm.CountPurpose("inquiry.understand");
            var (_, _, none) = await InquiryTests.RunAsync(h, "x05.expertise_finder", Input(new { question = "underwater welding" }));
            Assert.Contains("Nobody", Assert.Single(none).Summary);
            Assert.Equal(understood, h.Llm.CountPurpose("inquiry.understand"));
        }

        [Fact]
        public async Task X06_X12_HypothesisAndFactCheck_TellTheModelHowToReadTheQuestion()
        {
            var (h, _, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "Ben Okafor has never been to Spain.", answer_type = "binary", subjects = new[] { new { person_id = Ben } } };
            model.Plan = new { facets = new[] { new { name = "travel", kind = "direct", queries = new[] { "Spain" }, counter_queries = new[] { "been to" } } } };
            model.Pass = (_, dossier) => new { answer = "yes", probability = 0.88, evidence_for = Mentioning(dossier, "never been to Spain"), evidence_against = Array.Empty<string>(), reasoning = "He says so himself." };
            var (_, check, _) = await InquiryTests.RunAsync(h, "x12.fact_check", Input(new { question = "Ben said he has never been to Spain" }, new[] { Ben }));
            Assert.Equal(InquiryVerdict.Yes, check!.Verdict);
            Assert.Contains(h.Llm.Calls, c => c.Purpose == "inquiry.reason" && c.Question.Contains("claim to fact-check"));

            var (_, hypothesis, _) = await InquiryTests.RunAsync(h, "x06.hypothesis_test", Input(new { question = "Ben has never left the country" }, new[] { Ben }));
            Assert.NotNull(hypothesis);
            Assert.Contains(h.Llm.Calls, c => c.Purpose == "inquiry.reason" && c.Question.Contains("hypothesis"));
        }

        [Fact]
        public async Task X07_OpinionPredictor_GivesAStanceDistribution_AndIsRefusedForAMinor()
        {
            var (h, corpus, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "What would Ava Lindqvist think of adopting a second cat?", answer_type = "choice" };
            model.Pass = (_, dossier) => new
            {
                answer = "positive", probability = 0.7, distribution = new Dictionary<string, double> { ["positive"] = 0.7, ["negative"] = 0.1, ["mixed"] = 0.15, ["indifferent"] = 0.05 },
                evidence_for = Mentioning(dossier, "cat"), evidence_against = Array.Empty<string>(), reasoning = "She is fond of her cat.",
            };
            var (_, report, _) = await InquiryTests.RunAsync(h, "x07.opinion_predictor", Input(new { question = "What would she think of adopting a second cat?" }, new[] { Ava }));
            Assert.Equal(InquiryAnswerType.Choice, report!.AnswerType);
            Assert.Equal("positive", report.Distribution[0].Option);
            Assert.Equal(4, report.Distribution.Count);

            // A person who is a minor by their own words: the tactic is refused with the reason, before anything runs.
            await DeduceAsync(h, corpus, Cam);
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() =>
                h.Runtime.Tactics.StartAsync("x07.opinion_predictor", Input(new { question = "What would he think of the new update?" }, new[] { Cam }), "test"));
            Assert.Contains("18", refused.Message);
        }

        [Fact]
        public async Task X08_BehaviourPrediction_RestsOnExactStatisticsOfPastBehaviour_AndIsLabelledAPrediction()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("nia", "Nia Brooks", tier: "tracked");
            await corpus.ConversationAsync("discord:c-nia", "dm", "me", "nia");
            // Six messages from Klives over six days; Nia answers five of them within the hour.
            for (int d = 0; d < 6; d++)
            {
                await corpus.MessageAsync("me", "discord:c-nia", Now - (20 - d) * Day, $"are you around on day {d}?");
                if (d != 3) await corpus.MessageAsync("nia", "discord:c-nia", Now - (20 - d) * Day + 30 * 60_000, $"yes, day {d} works");
            }
            var model = new ScriptedInquirer
            {
                Understand = _ => new { restatement = "Will Nia Brooks reply this weekend?", answer_type = "prediction" },
                Plan = new { facets = new[] { new { name = "availability", kind = "behaviour", queries = new[] { "weekend" }, counter_queries = Array.Empty<string>() } } },
                Pass = (_, dossier) => new { answer = "yes", probability = 0.8, evidence_for = Mentioning(dossier, "behaviour statistics"), evidence_against = Array.Empty<string>(), reasoning = "She usually replies: a prediction from past behaviour." },
            };
            model.Wire(h.Llm);
            var (_, report, _) = await InquiryTests.RunAsync(h, "x08.behaviour_prediction", Input(new { question = "Will Nia reply this weekend?" }, new[] { "nia" }));
            Assert.True(report!.IsPrediction);
            Assert.Equal(InquiryVerdict.LeansYes, report.Verdict); // a counted statistic is evidence enough to answer on
            var statistic = Assert.Single(report.EvidenceFor);
            Assert.Contains("replied within a day to 5 of 6", statistic.Quote);
            Assert.Contains("median reply time 30 minutes", statistic.Quote);
            Assert.Equal(InquiryAggregation.Statistic, statistic.Reliability);
            Assert.Null(statistic.MessageId);
            Assert.Contains(h.Llm.Calls, c => c.Purpose == "inquiry.reason" && c.Question.Contains("past behaviour first"));
        }

        [Fact]
        public async Task X09_ChangeDetector_ReturnsTheIntervalOfTheChange()
        {
            var (h, _, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "When did Dee Marsh move to Glasgow?", answer_type = "change_point" };
            model.Plan = new { facets = new[] { new { name = "home", kind = "direct", queries = new[] { "live in", "moved to" }, counter_queries = Array.Empty<string>() } } };
            model.Pass = (_, dossier) => new
            {
                answer = "around April 2026", probability = 0.7, change_from = "2026-04", change_to = "2026-05",
                evidence_for = Mentioning(dossier, "Glasgow"), evidence_against = Mentioning(dossier, "Bristol"), reasoning = "She says she moved the month before.",
            };
            var (_, report, outputs) = await InquiryTests.RunAsync(h, "x09.change_detector", Input(new { question = "When did Dee move to Glasgow?" }, new[] { Dee }));
            Assert.Equal(InquiryVerdict.Answered, report!.Verdict);
            var london = Zones.GetOrThrow("Europe/London");
            Assert.Equal(Zones.DayStartUtcMs(new DateOnly(2026, 4, 1), london), report.ChangeFromMs);
            Assert.Equal(Zones.DayStartUtcMs(new DateOnly(2026, 6, 1), london) - 1, report.ChangeToMs);
            var window = outputs.Single(o => o.Kind == TacticOutputKind.Timeline).Selection!;
            Assert.True(Selection.TryParseTimeRange(window.Items[0].Id, out long from, out long to) && from == report.ChangeFromMs && to == report.ChangeToMs);
        }

        [Fact]
        public async Task X10_X16_WhatChangedAndCoverage_AreReadFromTheKnowledgeStore_WithoutTheModel()
        {
            var (h, corpus, _) = await StartAsync();
            await using var _h = h;
            await DeduceAsync(h, corpus, Dee, Cam);
            int llmBefore = h.Llm.Calls.Count;
            long since = Now - 250 * Day;

            var (run, _, _) = await InquiryTests.RunAsync(h, "x10.what_changed", Input(new { sinceMs = since }, new[] { Dee }));
            var changed = JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!.Single(o => o.Kind == TacticOutputKind.Report).Data!.Value;
            Assert.Contains(changed.GetProperty("learned").EnumerateArray(), f => f.GetProperty("attribute").GetString() == "city" && f.GetProperty("value").GetString() == "Glasgow");
            Assert.Equal(3, changed.GetProperty("activity").GetProperty("messagesSince").GetInt32());
            Assert.Equal(1, changed.GetProperty("activity").GetProperty("messagesInPreviousEqualPeriod").GetInt32());

            var (coverageRun, _, _) = await InquiryTests.RunAsync(h, "x16.coverage_check", Input(null, new[] { Dee }));
            var coverage = JsonSerializer.Deserialize<List<TacticOutput>>(coverageRun.OutputsJson!, KernelJson.Options)!.Single().Data!.Value;
            Assert.Equal(4, coverage.GetProperty("messages").GetInt64());
            Assert.False(coverage.GetProperty("minorRestricted").GetBoolean());
            Assert.Contains(coverage.GetProperty("categories").EnumerateArray(), c => c.GetProperty("known").GetBoolean());
            Assert.Equal(2, coverage.GetProperty("messagesByConversationKind").GetProperty("dm").GetInt64());

            // A minor's coverage says he is restricted and counts none of his restricted facts.
            var (camRun, _, _) = await InquiryTests.RunAsync(h, "x16.coverage_check", Input(null, new[] { Cam }));
            var cam = JsonSerializer.Deserialize<List<TacticOutput>>(camRun.OutputsJson!, KernelJson.Options)!.Single().Data!.Value;
            Assert.True(cam.GetProperty("minorRestricted").GetBoolean());
            Assert.DoesNotContain("Hillcrest", camRun.OutputsJson);
            Assert.DoesNotContain("Rotherham", camRun.OutputsJson);
            Assert.Equal(llmBefore, h.Llm.Calls.Count); // neither tactic asked the model anything
        }

        [Fact]
        public async Task X13_IncidentReconstruction_BuildsATimelineWhoseEveryEntryCitesARealMessage()
        {
            var (h, corpus, _) = await StartAsync();
            await using var _h = h;
            h.Llm.On("inquiry.incident", c =>
            {
                var lines = Regex.Matches(c.Prompt, @"^\[(m:\d+) · [^\]]*\] ([AB?]): (.*)$", RegexOptions.Multiline).Select(m => (Ref: m.Groups[1].Value, Side: m.Groups[2].Value, Text: m.Groups[3].Value)).ToList();
                string leeds = lines.First(l => l.Text.Contains("Leeds")).Ref;
                return JsonSerializer.Serialize(new
                {
                    events = new object[]
                    {
                        new { at = "day 1", summary = "Ava says she has moved to Leeds.", cites = new[] { leeds }, side = "A" },
                        new { at = "day 2", summary = "An argument nobody had.", cites = new[] { "m:999" }, side = "B" },
                        new { at = "day 3", summary = "", cites = new[] { leeds }, side = "A" },
                    },
                    positions = new Dictionary<string, string> { ["A"] = "Happy with the move.", ["B"] = "Curious about it." },
                    outcome = "Friendly catch-up.",
                });
            });
            long t0 = Now - 400 * Day;
            var (run, _, outputs) = await InquiryTests.RunAsync(h, "x13.incident_reconstruction", Input(new { fromMs = t0, toMs = t0 + 10 * Day }, new[] { Ava, Klives }));
            var timeline = outputs.Single(o => o.Kind == TacticOutputKind.Timeline);
            var events = timeline.Data!.Value.GetProperty("events").EnumerateArray().ToList();
            var only = Assert.Single(events); // the entry citing nothing real and the empty one are dropped
            string cited = only.GetProperty("messageIds")[0].GetString()!;
            Assert.Equal(corpus.Messages.Single(m => m.Text.Contains("Leeds")).MessageId, cited);
            Assert.Equal(new[] { cited }, outputs.Single(o => o.Kind == TacticOutputKind.MessageSet).Selection!.Items.Select(i => i.Id));
            // Only what the two of them wrote to each other was read: nothing from the group or from other people.
            var prompt = h.Llm.Calls.Single(c => c.Purpose == "inquiry.incident").Prompt;
            Assert.DoesNotContain("bakery", prompt);
            Assert.Contains("A = Ava Lindqvist", prompt);
            Assert.Equal(TacticRunStates.Succeeded, run.State);

            var (_, _, quiet) = await InquiryTests.RunAsync(h, "x13.incident_reconstruction", Input(new { fromMs = Now - 5 * Day, toMs = Now }, new[] { Ava, Klives }));
            Assert.Contains("no messages", Assert.Single(quiet).Summary);
        }

        [Fact]
        public async Task X14_StandingQuestion_IsReEvaluatedOnNewLiveEvidence_AlertsOnAFlip_AndNeverWatchesAMinor()
        {
            var (h, corpus, model) = await StartAsync();
            await using var _h = h;
            model.Understand = _ => new { restatement = "Does Dee Marsh live in Glasgow?", answer_type = "binary" };
            model.Plan = new { facets = new[] { new { name = "home", kind = "direct", queries = new[] { "live in", "moved to", "moving" }, counter_queries = Array.Empty<string>() } } };
            model.Pass = (_, dossier) =>
            {
                var left = Mentioning(dossier, "left Glasgow");
                return left.Length > 0
                    ? new { answer = "no", probability = 0.1, evidence_for = left, evidence_against = Mentioning(dossier, "moved to Glasgow"), reasoning = "She has left." }
                    : new { answer = "yes", probability = 0.9, evidence_for = Mentioning(dossier, "moved to Glasgow"), evidence_against = Array.Empty<string>(), reasoning = "She moved there." };
            };
            var (_, first, outputs) = await InquiryTests.RunAsync(h, "x14.standing_question", Input(new { question = "Does Dee live in Glasgow?", minIntervalHours = 1 }, new[] { Dee }));
            Assert.Equal(InquiryVerdict.Yes, first!.Verdict);
            Assert.NotNull(first.StandingId);
            Assert.Contains(outputs, o => o.Kind == TacticOutputKind.AlertRule);
            var module = h.Module<InquiryModule>();
            Assert.True(module.StandingQuestions().ContainsKey(first.StandingId!));

            // A backfilled message is not live evidence: nothing is re-evaluated, nobody is alerted (T5).
            h.Clock.Advance(TimeSpan.FromHours(2));
            var builder = new CorpusBuilder(h.Stores);
            string dmDee = corpus.Messages.First(m => m.AuthorPersonId == Dee && m.Text.Contains("Bristol")).ConversationId;
            await WriteAsync(h, Dee, dmDee, h.Clock.NowMs - 60_000, "I left Glasgow years ago in another life", SourceKinds.Backfill);
            Assert.Equal("no_new_live_evidence", Outcome(await module.EvaluateStandingAsync(first.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false)));

            // A live message that does not bear on the question is seen and set aside without a model call.
            int reasonCalls = h.Llm.CountPurpose("inquiry.reason");
            await WriteAsync(h, Dee, dmDee, h.Clock.NowMs - 30_000, "what a week, the kettle broke again", SourceKinds.Live);
            Assert.Equal("new_evidence_unrelated", Outcome(await module.EvaluateStandingAsync(first.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false)));
            Assert.Equal(reasonCalls, h.Llm.CountPurpose("inquiry.reason"));

            // A live message that does: re-evaluated, the verdict flips, an alert is published.
            await WriteAsync(h, Dee, dmDee, h.Clock.NowMs - 10_000, "big news, I left Glasgow and moved to Cardiff", SourceKinds.Live);
            long eventsBefore = h.Runtime.Host.Events.Bounds().LatestEver;
            var result = await module.EvaluateStandingAsync(first.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false);
            Assert.Equal("evaluated", Outcome(result));
            var state = module.State(first.StandingId!)!;
            Assert.Equal("No", state.LastVerdict);
            var alerts = h.Runtime.Host.Events.Replay(eventsBefore, 100).Where(e => e.Topic == Topics.Alert).ToList();
            var alert = Assert.Single(alerts);
            Assert.Contains("standing_question.flip", alert.PayloadJson);
            Assert.Contains("\"from\":\"Yes\"", alert.PayloadJson);

            // More evidence inside the minimum interval is deferred, not run again at once.
            await WriteAsync(h, Dee, dmDee, h.Clock.NowMs - 5_000, "Cardiff is great, glad I left Glasgow", SourceKinds.Live);
            Assert.Equal("deferred", Outcome(await module.EvaluateStandingAsync(first.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false)));
            Assert.True(module.State(first.StandingId!)!.Pending);

            // MinorGuard: a question cannot be kept open on a minor, and one that exists is switched off when they turn out to be one.
            await h.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1", ("$p", Dee), ("$t", h.Clock.NowMs)));
            Omnipotent.Services.OmniscienceV3.ReadModels.ReadModelStore.PolicyChanged();
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync("x14.standing_question", Input(new { question = "Does Dee live in Cardiff?" }, new[] { Dee }), "test"));
            h.Clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal("disabled_minor", Outcome(await module.EvaluateStandingAsync(first.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false)));
            Assert.False(module.StandingQuestions().ContainsKey(first.StandingId!));
        }

        [Fact]
        public async Task X50_AFlip_IsHandedToTheNotices_AboutThePersonAsked_AndNotAlertedASecondTime()
        {
            var (h, corpus, model) = await StartAsync();
            await using var _h = h;
            var notices = new Omnipotent.Tests.OmniscienceV3.Integration.RecordingNotices();
            h.Kit.Notices = notices;
            model.Understand = _ => new { restatement = "Does Dee Marsh live in Glasgow?", answer_type = "binary" };
            model.Plan = new { facets = new[] { new { name = "home", kind = "direct", queries = new[] { "live in", "moved to", "moving" }, counter_queries = Array.Empty<string>() } } };
            model.Pass = (_, dossier) =>
            {
                var left = Mentioning(dossier, "left Glasgow");
                return left.Length > 0
                    ? new { answer = "no", probability = 0.1, evidence_for = left, evidence_against = Mentioning(dossier, "moved to Glasgow"), reasoning = "She has left." }
                    : new { answer = "yes", probability = 0.9, evidence_for = Mentioning(dossier, "moved to Glasgow"), evidence_against = Array.Empty<string>(), reasoning = "She moved there." };
            };
            var (_, first, _) = await InquiryTests.RunAsync(h, "x14.standing_question", Input(new { question = "Does Dee live in Glasgow?", minIntervalHours = 1 }, new[] { Dee }));
            Assert.Empty(notices.Received); // the first answer is not a flip

            h.Clock.Advance(TimeSpan.FromHours(2));
            string dmDee = corpus.Messages.First(m => m.AuthorPersonId == Dee && m.Text.Contains("Bristol")).ConversationId;
            await WriteAsync(h, Dee, dmDee, h.Clock.NowMs - 10_000, "big news, I left Glasgow and moved to Cardiff", SourceKinds.Live);
            long eventsBefore = h.Runtime.Host.Events.Bounds().LatestEver;
            var module = h.Module<InquiryModule>();
            Assert.Equal("evaluated", Outcome(await module.EvaluateStandingAsync(first!.StandingId!, h.Job("knowledge.inquiry", InquiryModule.StandingKind, first.StandingId), force: false)));

            var notice = Assert.Single(notices.Received);
            Assert.Equal(("standing_question", InquiryModule.Id, Dee, true), (notice.Kind, notice.ModuleId, notice.PersonId, notice.PersonTargeted));
            Assert.StartsWith("standing|" + first.StandingId + "|", notice.NoticeId);
            string data = JsonSerializer.Serialize(notice.Data, KernelJson.Options);
            Assert.Contains("\"from\":\"Yes\"", data);
            Assert.Contains("\"to\":\"No\"", data);
            // The notices decide whether to push it; the module does not push its own alert beside them.
            Assert.DoesNotContain(h.Runtime.Host.Events.Replay(eventsBefore, 100), e => e.Topic == Topics.Alert);
        }

        private static string Outcome(object result) => JsonSerializer.SerializeToElement(result, KernelJson.Options).GetProperty("outcome").GetString()!;

        /// <summary>Writes a message of a planted person with a source kind (the planted identities use their own id scheme).</summary>
        private static async Task<string> WriteAsync(KnowledgeHarness h, string person, string conversation, long atMs, string text, string sourceKind)
        {
            string id = CorpusIds.Message("discord", "live-" + Guid.NewGuid().ToString("N")[..12]);
            await h.Stores.Corpus.WriteAsync((c, tx) =>
            {
                long seq = ChangeLog.Append(c, tx, atMs, ChangeKinds.MessageInsert, id, person, conversation, sourceKind);
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at_ms, time_quality, order_key, current_version, content,
                        first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    VALUES($id,'discord',$pm,$c,$a,$t,0,$t,1,$x,$sk,'planted',0,$t,$seq)
                    """, ("$id", id), ("$pm", CorpusIds.Native(id)), ("$c", conversation), ("$a", PlantedFactCorpus.Identity(person)), ("$t", atMs), ("$x", text), ("$sk", sourceKind), ("$seq", seq));
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, recorded_at_ms, source_id) VALUES($m,1,$x,$t,'planted')", ("$m", id), ("$x", text), ("$t", atMs));
            });
            return id;
        }

        [Fact]
        public async Task X15_AskTheCorpus_SearchesStepByStep_AndKeepsOnlyCitationsThatExistAndHold()
        {
            var (h, corpus, _) = await StartAsync();
            await using var _h = h;
            await DeduceAsync(h, corpus, Ava);
            var steps = new List<string>();
            h.Llm.On("inquiry.research", c =>
            {
                if (c.Question.StartsWith("For each citation", StringComparison.Ordinal))
                    return JsonSerializer.Serialize(new { checks = Regex.Matches(c.Question, @"^(E\d+) — (.+)$", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, supports = true }) });
                lock (steps) steps.Add(c.Question);
                if (steps.Count == 1) return JsonSerializer.Serialize(new { action = "search", query = "nurse hospital" });
                if (steps.Count == 2) return JsonSerializer.Serialize(new { action = "facts", person = Ava });
                if (steps.Count == 3) return JsonSerializer.Serialize(new { action = "read", id = "E77" }); // an id it was never shown
                string shown = Regex.Match(c.Prompt, @"^(E\d+) .*nurse", RegexOptions.Multiline).Groups[1].Value;
                return JsonSerializer.Serialize(new { action = "answer", answer = "Ava works as a nurse at a children's hospital.", cites = new[] { shown, "E55" }, confidence = 0.9 });
            });
            var (run, _, outputs) = await InquiryTests.RunAsync(h, "x15.ask_corpus", Input(new { question = "What does Ava do for work, and where?" }));
            var answer = outputs.Single(o => o.Kind == TacticOutputKind.Answer).Data!.Value;
            Assert.Contains("nurse", answer.GetProperty("answer").GetString());
            var citation = Assert.Single(answer.GetProperty("citations").EnumerateArray()); // the invented E55 is gone
            Assert.Equal(corpus.Messages.Single(m => m.Text.Contains("nurse")).MessageId, citation.GetProperty("messageId").GetString());
            Assert.Equal(0.9, answer.GetProperty("confidence").GetDouble(), 3);
            Assert.Contains(outputs, o => o.Kind == TacticOutputKind.MessageSet);
            // The tools answered: search results fenced as data, the person's facts, and an honest "could not" for the unknown id.
            Assert.Contains(steps, s => s.Contains("SEARCH RESULTS") && s.Contains("<corpus-data>"));
            Assert.Contains(steps, s => s.StartsWith("FACTS about Ava Lindqvist", StringComparison.Ordinal) && s.Contains("nurse"));
            Assert.Contains(steps, s => s.Contains("could not be carried out"));
            Assert.Equal(TacticRunStates.Succeeded, run.State);

            // An answer whose citations are all invented keeps a low confidence, whatever the model claimed.
            steps.Clear();
            h.Llm.On("inquiry.research", c => JsonSerializer.Serialize(new { action = "answer", answer = "Ava is a pilot.", cites = new[] { "E9" }, confidence = 0.95 }));
            var (_, _, unsupported) = await InquiryTests.RunAsync(h, "x15.ask_corpus", Input(new { question = "What does Ava do for work?" }));
            var weak = unsupported.Single(o => o.Kind == TacticOutputKind.Answer).Data!.Value;
            Assert.Empty(weak.GetProperty("citations").EnumerateArray());
            Assert.True(weak.GetProperty("confidence").GetDouble() <= 0.3);
            Assert.DoesNotContain(unsupported, o => o.Kind == TacticOutputKind.MessageSet);
        }
    }
}
