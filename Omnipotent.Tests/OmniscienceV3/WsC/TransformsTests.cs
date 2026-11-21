using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Signalling;
using Omnipotent.Services.OmniscienceV3.Modules.Transforms;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>The transform tactics that need no model (X20–X22, X28, X29, X34, X37–X40) and the statistics under them.</summary>
    public class TransformsTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd, Bot = SignalsHarness.Bot;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, DmAna = SignalsHarness.DmAna, DmBen = SignalsHarness.DmBen;
        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), Hour = TimeSpan.FromHours(1);
        private const long DayMs = 86_400_000L;

        private static async Task<List<TacticOutput>> RunAsync(SignalsHarness h, string key, object? parameters, params Selection[] selections)
        {
            string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
            await h.Driver.RunUntilIdleAsync();
            h.Driver.AssertNoFailures();
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, $"{key}: {run.State} {run.Error}");
            return JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
        }

        private static async Task<string> RefusedAsync(SignalsHarness h, string key, object? parameters, params Selection[] selections)
        {
            try
            {
                string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
                await h.Driver.RunUntilIdleAsync();
                var run = h.Runtime.Tactics.Get(runId)!;
                Assert.Equal(TacticRunStates.Failed, run.State);
                return run.Error!;
            }
            catch (TacticRefusedException ex)
            {
                return ex.Message;
            }
        }

        [Fact]
        public void Stats_AgreeWithKnownValues()
        {
            // Chi-square tail: the textbook 5% points.
            Assert.Equal(0.05, Stats.ChiSquareSurvival(3.841, 1), 3);
            Assert.Equal(0.05, Stats.ChiSquareSurvival(5.991, 2), 3);
            Assert.Equal(0.01, Stats.ChiSquareSurvival(13.277, 4), 3);
            Assert.Equal(1.0, Stats.ChiSquareSurvival(0, 3));
            Assert.Equal(0.975, Stats.NormalCdf(1.96), 3);

            // A 2×2 table with a clear difference, and one with none.
            var (chi, df, p, v) = Stats.ChiSquare(new long[] { 90, 10 }, new long[] { 50, 50 });
            Assert.Equal(1, df);
            Assert.Equal(38.095, chi, 2);
            Assert.True(p < 1e-8);
            Assert.Equal(0.436, v, 2);
            Assert.Equal(1.0, Stats.ChiSquare(new long[] { 30, 70 }, new long[] { 30, 70 }).P, 6);
            // Categories nobody used, and an empty side, are not evidence of anything.
            Assert.Equal(1.0, Stats.ChiSquare(new long[] { 5, 0, 0 }, new long[] { 9, 0, 0 }).P);
            Assert.Equal(1.0, Stats.ChiSquare(new long[] { 0, 0 }, new long[] { 9, 3 }).P);

            // Mann–Whitney: fully separated groups, identical groups, and ties.
            var separated = Stats.MannWhitney(new double[] { 10, 11, 12, 13, 14, 15 }, new double[] { 1, 2, 3, 4, 5, 6 });
            Assert.Equal((36.0, 1.0), (separated.U, separated.Effect));
            Assert.True(separated.P < 0.01 && separated.Z > 0);
            var same = Stats.MannWhitney(new double[] { 1, 2, 3, 4 }, new double[] { 1, 2, 3, 4 });
            Assert.Equal(0.0, same.Effect, 6);
            Assert.True(same.P > 0.9);
            Assert.Equal(1.0, Stats.MannWhitney(new double[] { 5, 5, 5 }, new double[] { 5, 5, 5 }).P);
            Assert.Equal(1.0, Stats.MannWhitney(Array.Empty<double>(), new double[] { 1 }).P);
            Assert.True(Stats.MannWhitney(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }).Effect < 0);

            // Benjamini–Hochberg keeps the small ones together and nothing when nothing is small.
            Assert.Equal(new[] { true, true, false, false }, Stats.BenjaminiHochberg(new[] { 0.001, 0.02, 0.04, 0.5 }));
            Assert.Equal(new[] { false, false }, Stats.BenjaminiHochberg(new[] { 0.2, 0.9 }));
            Assert.Equal(2.5, Stats.Median(new double[] { 4, 1, 3, 2 }));
        }

        [Fact]
        public async Task FindSimilar_RanksByMeaning_InsideTheSelectedScope_AndNeverReturnsTheSeedsOrDeletedMessages()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true);
            string seed = await h.PostAsync(General, Ana, "pizza night on friday at mine", embed: true);
            string close = await h.PostAsync(General, Ben, "friday pizza night sounds great", embed: true);
            string closeElsewhere = await h.PostAsync(Other, Cyd, "pizza night this friday?", embed: true);
            string far = await h.PostAsync(General, Cyd, "the compiler crashed again", embed: true);
            string gone = await h.PostAsync(General, Ben, "pizza night friday for sure", embed: true);
            await h.DeleteAsync(gone);
            string notEmbedded = await h.PostAsync(General, Ben, "pizza night friday maybe");
            h.Clock.Advance(Minute);

            var everywhere = await RunAsync(h, FindSimilarTactic.Key, new { k = 10 }, Selection.Of(SelectionKind.Message, new[] { seed }));
            var ids = everywhere[0].Selection!.Items.Select(i => i.Id).ToList();
            Assert.Equal(new[] { close, closeElsewhere }.OrderBy(x => x), ids.Take(2).OrderBy(x => x));
            Assert.Equal(far, ids[^1]);
            Assert.DoesNotContain(seed, ids);
            Assert.DoesNotContain(gone, ids);
            Assert.DoesNotContain(notEmbedded, ids);
            Assert.True(everywhere[0].Selection!.Items[0].Score > everywhere[0].Selection!.Items[^1].Score);
            Assert.Equal(1, everywhere[1].Data!.Value.GetProperty("seeds").GetInt32());

            // In one conversation, with a floor on the score: only the close one from there.
            var scoped = await RunAsync(h, FindSimilarTactic.Key, new { minScore = 0.3 }, Selection.Of(SelectionKind.Message, new[] { seed }), Selection.Of(SelectionKind.Conversation, new[] { General }));
            Assert.Equal(new[] { close }, scoped[0].Selection!.Items.Select(i => i.Id));
            Assert.True(scoped[1].Data!.Value.GetProperty("exactInScope").GetBoolean());
            // By person, and by a description instead of a message.
            var byPerson = await RunAsync(h, FindSimilarTactic.Key, new { text = "pizza on friday night" }, Selection.Of(SelectionKind.Person, new[] { "p-cyd" }));
            Assert.Equal(closeElsewhere, byPerson[0].Selection!.Items[0].Id);
            Assert.All(byPerson[0].Selection!.Items, i => Assert.Contains(i.Id, new[] { closeElsewhere, far }));

            Assert.Contains("Select at least one message", await RefusedAsync(h, FindSimilarTactic.Key, null));
            Assert.Contains("embedded yet", await RefusedAsync(h, FindSimilarTactic.Key, null, Selection.Of(SelectionKind.Message, new[] { notEmbedded })));
        }

        [Fact]
        public async Task Context_ShowsWhatSurroundedAMessage_ItsParentRepliesAndReactions_AsOfTheChosenMoment()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true);
            var line = new List<string>();
            for (int i = 0; i < 5; i++) { line.Add(await h.PostAsync(General, i % 2 == 0 ? Ana : Ben, "before " + i)); h.Clock.Advance(Minute); }
            string question = await h.PostAsync(General, Self, "who is bringing the projector?");
            h.Clock.Advance(Minute);
            string target = await h.PostAsync(General, Ana, "I can bring it", replyTo: question);
            h.Clock.Advance(Minute);
            string reply1 = await h.PostAsync(General, Ben, "thanks Ana", replyTo: target);
            h.Clock.Advance(Minute);
            string after = await h.PostAsync(General, Cyd, "see you there");
            long cut = h.Clock.NowMs;
            h.Clock.Advance(Hour);
            string lateReply = await h.PostAsync(General, Self, "perfect", replyTo: target);
            await h.PostAsync(Offtopic, Ben, "another channel entirely");
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, added_at_ms, time_quality, source_kind) VALUES($m,'👍','thumbsup',$i,$t,0,'live')", ("$m", target), ("$i", Ben), ("$t", cut - 1000));
                c.Exec(tx, "INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, added_at_ms, time_quality, source_kind) VALUES($m,'👍','thumbsup',$i,$t,0,'live')", ("$m", target), ("$i", Self), ("$t", cut + 1000));
                c.Exec(tx, "INSERT INTO reaction_counts(message_id, emoji_key, count, observed_at_ms) VALUES($m,'🎉',3,$t)", ("$m", target), ("$t", cut));
            });
            h.Clock.Advance(Minute);

            var now = await RunAsync(h, ContextExpanderTactic.Key, new { before = 3, after = 5 }, Selection.Of(SelectionKind.Message, new[] { target }));
            Assert.Equal(new[] { line[3], line[4], question, target, reply1, after, lateReply }, now[0].Selection!.Items.Select(i => i.Id));
            var report = now[1].Data!.Value;
            Assert.Equal((question, "Klives", true), (report.GetProperty("parent").GetProperty("messageId").GetString(), report.GetProperty("parent").GetProperty("author").GetString(),
                report.GetProperty("parent").GetProperty("isKlives").GetBoolean()));
            Assert.Equal(new[] { reply1, lateReply }, report.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("messageId").GetString()));
            var reactions = report.GetProperty("reactions").EnumerateArray().ToDictionary(r => r.GetProperty("emoji").GetString()!, r => (Count: r.GetProperty("count").GetInt32(), People: r.GetProperty("people").GetArrayLength()));
            Assert.Equal((3, 0), reactions["🎉"]);
            Assert.Equal((2, 2), reactions["👍"]);
            Assert.Equal("I can bring it", report.GetProperty("window")[3].GetProperty("text").GetString());

            // As of an earlier moment: what was said and reacted later is not there yet.
            string runId = await h.Runtime.Tactics.StartAsync(ContextExpanderTactic.Key, new TacticInput(new[] { Selection.Of(SelectionKind.Message, new[] { target }) },
                JsonSerializer.SerializeToElement(new { before = 1, after = 10 }), AsOfMs: cut), "tester");
            await h.Driver.RunUntilIdleAsync();
            var then = JsonSerializer.Deserialize<List<TacticOutput>>(h.Runtime.Tactics.Get(runId)!.OutputsJson!, KernelJson.Options)!;
            Assert.Equal(new[] { question, target, reply1, after }, then[0].Selection!.Items.Select(i => i.Id));
            Assert.Equal(1, then[1].Data!.Value.GetProperty("replies").GetArrayLength());
            Assert.Equal(1, then[1].Data!.Value.GetProperty("reactions").EnumerateArray().Single(r => r.GetProperty("emoji").GetString() == "👍").GetProperty("people").GetArrayLength());

            Assert.Contains("no message", await RefusedAsync(h, ContextExpanderTactic.Key, null, Selection.Of(SelectionKind.Message, new[] { "discord:nope" })));
            Assert.Contains("exactly one", await RefusedAsync(h, ContextExpanderTactic.Key, null, Selection.Of(SelectionKind.Message, new[] { target, question })));
        }

        [Fact]
        public async Task PhraseTracker_FindsWhoSaidItFirst_WhoPickedItUpFromWhom_AndHowItGrew()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true);
            long now = h.Clock.NowMs;
            string first = await h.PostAsync(General, Ana, "that was a big yikes moment", sentAt: now - 70 * DayMs);
            await h.PostAsync(General, Ana, "Big Yikes again", sentAt: now - 69 * DayMs);
            string ben = await h.PostAsync(General, Ben, "big yikes indeed", sentAt: now - 60 * DayMs);
            string cyd = await h.PostAsync(Other, Cyd, "honestly big yikes", sentAt: now - 20 * DayMs);                // another server: nobody to have picked it up from there
            await h.PostAsync(General, Cyd, "yikes, that is big", sentAt: now - 10 * DayMs);                           // the words, not the phrase
            await h.PostAsync(General, Bot, "big yikes detected", sentAt: now - 5 * DayMs);                            // a bot
            string quoted = await h.PostAsync(Offtopic, Ben, "she said \"big yikes\" twice", sentAt: now - 2 * DayMs);

            var all = await RunAsync(h, PhraseTrackerTactic.Key, new { phrase = "big yikes" });
            var data = all[0].Data!.Value;
            Assert.Equal((5, 3), (data.GetProperty("uses").GetInt32(), data.GetProperty("people").GetInt32()));
            Assert.Equal((first, "p-ana", "Ana"), (data.GetProperty("first").GetProperty("messageId").GetString(), data.GetProperty("first").GetProperty("personId").GetString(),
                data.GetProperty("first").GetProperty("name").GetString()));
            Assert.Equal(new[] { "p-ana", "p-ben", "p-cyd" }, data.GetProperty("adopters").EnumerateArray().Select(a => a.GetProperty("personId").GetString()));
            Assert.Equal(new[] { 2, 2, 1 }, data.GetProperty("adopters").EnumerateArray().Select(a => a.GetProperty("uses").GetInt32()));
            var pickup = Assert.Single(data.GetProperty("pickups").EnumerateArray());
            Assert.Equal(("p-ana", "p-ben"), (pickup.GetProperty("from").GetString(), pickup.GetProperty("to").GetString()));
            Assert.Equal(new[] { first, ben, cyd }, all[1].Selection!.Items.Select(i => i.Id));
            Assert.True(data.GetProperty("months").GetArrayLength() >= 2);
            Assert.Equal(4, data.GetProperty("guilds").EnumerateArray().Single(g => g.GetProperty("guildId").GetString() == "discord:g1").GetProperty("uses").GetInt32());
            _ = quoted;

            // In one server only; and a phrase with quotes and operators in it is data, not query syntax.
            var scoped = await RunAsync(h, PhraseTrackerTactic.Key, new { phrase = "big yikes" }, Selection.Of(SelectionKind.Guild, new[] { "discord:g2" }));
            Assert.Equal(1, scoped[0].Data!.Value.GetProperty("uses").GetInt32());
            var odd = await RunAsync(h, PhraseTrackerTactic.Key, new { phrase = "\"big\" OR yikes*" });
            Assert.Equal(0, odd[0].Data!.Value.GetProperty("uses").GetInt32());
            Assert.Contains("Give the phrase", await RefusedAsync(h, PhraseTrackerTactic.Key, new { phrase = "?" }));
        }

        /// <summary>More people than the harness has, each with one identity, so comparisons have something to compare.</summary>
        private static async Task<List<(string Identity, string Person)>> AddPeopleAsync(SignalsHarness h, string prefix, int count)
        {
            var added = Enumerable.Range(0, count).Select(i => (Identity: $"discord:{prefix}{i}", Person: $"p-{prefix}{i}")).ToList();
            long start = h.Clock.NowMs - 400 * DayMs;
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                foreach (var (identity, person) in added)
                {
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,0)", ("$p", person), ("$t", start), ("$n", person[2..]));
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
                    c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,'user',$t,$t)",
                        ("$i", identity), ("$u", identity[8..]), ("$p", person), ("$t", start));
                }
            });
            return added;
        }

        private static Task PostRawAsync(SignalsHarness h, string conversation, string identity, string person, string text, long sentAt, string? replyTo = null) =>
            h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                string id = "discord:x" + Guid.NewGuid().ToString("N")[..12];
                string? guild = c.Scalar<string>(tx, "SELECT guild_id FROM conversations WHERE conversation_id=$c", ("$c", conversation));
                long seq = ChangeLog.Append(c, tx, h.Clock.NowMs, ChangeKinds.MessageInsert, id, person, conversation, SourceKinds.Import);
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key, is_bot,
                        reply_to_message_id, current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    VALUES($id,'discord',$pm,$c,$g,$a,$t,0,$t,0,$r,1,$content,'import','test',0,$n,$seq)
                    """, ("$id", id), ("$pm", id[8..]), ("$c", conversation), ("$g", guild), ("$a", identity), ("$t", sentAt), ("$r", replyTo), ("$content", text), ("$n", h.Clock.NowMs), ("$seq", seq));
            });

        [Fact]
        public async Task ClosestContacts_AndCommonGround_ComeFromWhoReallyInteracts()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true, rollups: true);
            long now = h.Clock.NowMs;
            // Ben and Klives write to each other daily; Ana replies to Ben now and then; Cyd is replied to by both.
            for (int i = 0; i < 20; i++)
            {
                long at = now - (40 - i) * DayMs;
                await h.PostAsync(DmBen, Ben, "morning " + i, source: SourceKinds.Import, sentAt: at, arrivedAt: now);
                await h.PostAsync(DmBen, Self, "morning back " + i, source: SourceKinds.Import, sentAt: at + 60_000, arrivedAt: now);
            }
            string fromCyd = await h.PostAsync(General, Cyd, "anyone up for a hike?", source: SourceKinds.Import, sentAt: now - 30 * DayMs, arrivedAt: now);
            for (int i = 0; i < 5; i++)
            {
                string b = await h.PostAsync(General, Ben, "hike yes " + i, source: SourceKinds.Import, sentAt: now - (29 - i) * DayMs, arrivedAt: now, replyTo: fromCyd);
                await h.PostAsync(General, Ana, "hike me too " + i, source: SourceKinds.Import, sentAt: now - (29 - i) * DayMs + 60_000, arrivedAt: now, replyTo: fromCyd);
                await h.PostAsync(General, Ana, "agreed " + i, source: SourceKinds.Import, sentAt: now - (29 - i) * DayMs + 120_000, arrivedAt: now, replyTo: b);
            }
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string person, string attribute, string value) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, status, modality, source_kind, probability,
                        module_id, module_version, source_seq)
                    VALUES($i,$i,'person',$p,$a,'attribute','{}',$v,$n,'accepted','literal','self_disclosure',0.9,'test',1,1)
                    """, ("$i", id), ("$p", person), ("$a", attribute), ("$v", value), ("$n", now));
                Fact("f1", "p-ben", "hobby", "Hiking");
                Fact("f2", "p-ana", "hobby", "hiking");
                Fact("f3", "p-ben", "city", "Leeds");
                Fact("f4", "p-ana", "city", "York");
            });
            await h.Driver.RunUntilIdleAsync(5_000);
            h.Driver.AssertNoFailures();

            var closest = await RunAsync(h, ClosestContactsTactic.Key, new { days = 90, top = 5 }, Selection.Of(SelectionKind.Person, new[] { "p-ben" }));
            var contacts = closest[1].Data!.Value.GetProperty("contacts").EnumerateArray().ToList();
            Assert.Equal("p-klives", contacts[0].GetProperty("personId").GetString());
            Assert.Equal(new[] { "p-klives", "p-ana", "p-cyd" }.OrderBy(x => x), contacts.Select(c => c.GetProperty("personId").GetString()).OrderBy(x => x));
            Assert.True(contacts[0].GetProperty("share").GetDouble() > 0.5);
            Assert.True(contacts[0].GetProperty("reciprocity").GetDouble() > 0.8);
            Assert.Equal(40, contacts[0].GetProperty("directMessages").GetInt32());
            Assert.Equal("p-klives", closest[0].Selection!.Items[0].Id);
            // A minor among the contacts is included like anyone else.
            await h.MarkMinorAsync("p-cyd");
            var withMinor = await RunAsync(h, ClosestContactsTactic.Key, new { days = 90 }, Selection.Of(SelectionKind.Person, new[] { "p-ben" }));
            Assert.Contains(withMinor[1].Data!.Value.GetProperty("contacts").EnumerateArray(), c => c.GetProperty("personId").GetString() == "p-cyd");
            Assert.Equal(0, withMinor[1].Data!.Value.GetProperty("leftOutAsMinors").GetInt32());
            Assert.False(string.IsNullOrEmpty(await h.Runtime.Tactics.StartAsync(ClosestContactsTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p-cyd" }) }), "tester")));

            var common = await RunAsync(h, CommonGroundTactic.Key, new { days = 90 }, Selection.Of(SelectionKind.Person, new[] { "p-ben", "p-ana" }));
            var ground = common[0].Data!.Value;
            Assert.Equal("discord:g1", Assert.Single(ground.GetProperty("guilds").EnumerateArray()).GetProperty("guildId").GetString());
            Assert.Equal(General, Assert.Single(ground.GetProperty("conversations").EnumerateArray()).GetProperty("conversationId").GetString());
            var fact = Assert.Single(ground.GetProperty("sharedFacts").EnumerateArray());
            Assert.Equal("hobby", fact.GetProperty("attribute").GetString());
            Assert.Equal(5, Assert.Single(ground.GetProperty("between").EnumerateArray()).GetProperty("replies").GetInt32());
            // Cyd is a mutual contact and is listed, minor or not.
            Assert.Equal(1, ground.GetProperty("mutualContacts").GetArrayLength());
            Assert.Contains("two to five", await RefusedAsync(h, CommonGroundTactic.Key, null, Selection.Of(SelectionKind.Person, new[] { "p-ben" })));
        }

        [Fact]
        public async Task CohortComparison_AndPeriodComparison_ReportDifferencesWithTheirEvidence()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true, rollups: true);
            long now = h.Clock.NowMs;
            var wordy = await AddPeopleAsync(h, "w", 6);
            var terse = await AddPeopleAsync(h, "t", 6);
            string longText = string.Join(' ', Enumerable.Repeat("considerable", 30)), shortText = "ok";
            for (int d = 0; d < 25; d++)
            {
                long at = now - (30 - d) * DayMs;
                for (int i = 0; i < 6; i++)
                {
                    await PostRawAsync(h, General, wordy[i].Identity, wordy[i].Person, longText + " " + d, at + i * 1000);
                    await PostRawAsync(h, General, terse[i].Identity, terse[i].Person, shortText + " " + d, at + 60_000 + i * 1000);
                }
            }
            // Ana wrote only in the earlier period, Cyd only in the later one, Ben in both.
            for (int i = 0; i < 12; i++) await h.PostAsync(Other, Ana, "early " + i, source: SourceKinds.Import, sentAt: now - 50 * DayMs + i * 1000, arrivedAt: now);
            for (int i = 0; i < 6; i++) await h.PostAsync(Other, Ben, "early ben " + i, source: SourceKinds.Import, sentAt: now - 50 * DayMs + 100_000 + i * 1000, arrivedAt: now);
            for (int i = 0; i < 30; i++) await h.PostAsync(Other, i % 2 == 0 ? Cyd : Ben, "late " + i, source: SourceKinds.Import, sentAt: now - 5 * DayMs + i * 1000, arrivedAt: now);
            await h.Driver.RunUntilIdleAsync(10_000);
            h.Driver.AssertNoFailures();

            var comparison = await RunAsync(h, CohortComparisonTactic.Key, new { days = 60, minMessages = 20 }, Selection.Of(SelectionKind.Person, wordy.Select(w => w.Person)));
            var report = comparison[0].Data!.Value;
            // The rest: the six terse people and Ben, who also wrote more than twenty messages in the period.
            Assert.Equal((6, 7), (report.GetProperty("cohortPeople").GetInt32(), report.GetProperty("restPeople").GetInt32()));
            var words = report.GetProperty("measures").EnumerateArray().Single(m => m.GetProperty("measure").GetString() == "words");
            Assert.True(words.GetProperty("significant").GetBoolean());
            Assert.Equal("higher", words.GetProperty("direction").GetString());
            Assert.True(words.GetProperty("cohortMedian").GetDouble() > 25 && words.GetProperty("restMedian").GetDouble() < 5);
            // Both sides wrote one message a day: that measure shows no difference and is not called significant.
            var rate = report.GetProperty("measures").EnumerateArray().Single(m => m.GetProperty("measure").GetString() == "per_active_day");
            Assert.False(rate.GetProperty("significant").GetBoolean());
            Assert.Contains("Words per message", comparison[0].Summary);
            Assert.Contains("at least three", await RefusedAsync(h, CohortComparisonTactic.Key, null, Selection.Of(SelectionKind.Person, new[] { "p-w0", "p-w1" })));
            Assert.Contains("Too few people", await RefusedAsync(h, CohortComparisonTactic.Key, new { minMessages = 500 }, Selection.Of(SelectionKind.Person, wordy.Select(w => w.Person))));

            long earlyFrom = now - 55 * DayMs, earlyTo = now - 45 * DayMs, lateFrom = now - 10 * DayMs;
            var ranges = new Selection(SelectionKind.TimeRange, new[] { new SelectionItem(Selection.TimeRangeId(lateFrom, now)), new SelectionItem(Selection.TimeRangeId(earlyFrom, earlyTo)) });
            var periods = await RunAsync(h, PeriodComparisonTactic.Key, null, ranges, Selection.Of(SelectionKind.Guild, new[] { "discord:g2" }));
            var change = periods[0].Data!.Value;
            Assert.Equal((18, 30), (change.GetProperty("earlier").GetProperty("messages").GetInt32(), change.GetProperty("later").GetProperty("messages").GetInt32()));
            Assert.Equal(earlyFrom, change.GetProperty("earlier").GetProperty("fromMs").GetInt64());
            Assert.True(change.GetProperty("activityChange").GetDouble() > 0.5);
            Assert.Equal("p-ana", Assert.Single(change.GetProperty("wentQuiet").GetProperty("people").EnumerateArray()).GetProperty("personId").GetString());
            Assert.Equal("p-cyd", Assert.Single(change.GetProperty("appeared").GetProperty("people").EnumerateArray()).GetProperty("personId").GetString());
            // A single person: no member lists, only their own activity.
            var onePerson = await RunAsync(h, PeriodComparisonTactic.Key, null, ranges, Selection.Of(SelectionKind.Person, new[] { "p-ben" }));
            Assert.Equal((6, 15), (onePerson[0].Data!.Value.GetProperty("earlier").GetProperty("messages").GetInt32(), onePerson[0].Data!.Value.GetProperty("later").GetProperty("messages").GetInt32()));
            Assert.Equal(JsonValueKind.Null, onePerson[0].Data!.Value.GetProperty("appeared").ValueKind);
            Assert.Contains("exactly two ranges", await RefusedAsync(h, PeriodComparisonTactic.Key, null, Selection.Window(earlyFrom, earlyTo)));
        }

        [Fact]
        public async Task EvidenceBundle_PinsASelection_AddsToIt_AndExportsWhatMayBeShown()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true);
            string m1 = await h.PostAsync(General, Ana, "I left the keys under the mat");
            h.Clock.Advance(Minute);
            string m2 = await h.PostAsync(General, Self, "got them, thanks");
            string m3 = await h.PostAsync(DmAna, Ana, "line one\nline two");
            await h.DeleteAsync(m3);
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string attribute, string value, string source) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, status, modality, source_kind, probability,
                        module_id, module_version, source_seq)
                    VALUES($i,$i,'person','p-ana',$a,'attribute','{}',$v,$n,'accepted','literal',$s,0.9,'test',1,1)
                    """, ("$i", id), ("$a", attribute), ("$v", value), ("$n", h.Clock.NowMs), ("$s", source));
                Fact("a1", "city", "York", "self_disclosure");
                Fact("a2", "religion", "guessed", "inference");
            });

            var made = await RunAsync(h, EvidenceBundleTactic.Key, new { title = "The keys", note = "Who had the keys and when." },
                Selection.Of(SelectionKind.Message, new[] { m1, m2 }), Selection.Of(SelectionKind.Person, new[] { "p-ana" }));
            string bundleId = made[0].Data!.Value.GetProperty("bundleId").GetString()!;
            Assert.Equal(3, made[0].Data!.Value.GetProperty("items").GetInt32());
            // Adding to it: an item already there is not added twice.
            var added = await RunAsync(h, EvidenceBundleTactic.Key, new { bundleId }, Selection.Of(SelectionKind.Message, new[] { m2, m3 }), Selection.Of(SelectionKind.Assertion, new[] { "a1", "a2" }),
                Selection.Of(SelectionKind.Conversation, new[] { General }));
            Assert.Equal(7, added[0].Data!.Value.GetProperty("items").GetInt32());
            var bundle = Assert.Single(h.Transforms!.Bundles());
            Assert.Equal(("The keys", "Who had the keys and when.", 7), (bundle.Title, bundle.Note, bundle.Items.Count));

            string markdown = h.Transforms.ExportBundle(bundleId, "markdown");
            Assert.StartsWith("# The keys\n", markdown);
            Assert.Contains("**Ana**: I left the keys under the mat", markdown);
            Assert.Contains("**Klives**: got them, thanks", markdown);
            Assert.Contains("(deleted by its author): line one\n  line two", markdown);
            Assert.Contains("Ana: city = York (`a1`)", markdown);
            Assert.Contains("Ana: religion = guessed (`a2`)", markdown);
            Assert.Contains("guessed", markdown);
            Assert.Contains("author zone unknown", markdown);
            Assert.True(markdown.IndexOf("under the mat", StringComparison.Ordinal) < markdown.IndexOf("got them", StringComparison.Ordinal));

            using var json = JsonDocument.Parse(h.Transforms.ExportBundle(bundleId, "json"));
            Assert.Equal(3, json.RootElement.GetProperty("messages").GetArrayLength());
            var religion = json.RootElement.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("assertionId").GetString() == "a2");
            Assert.False(religion.TryGetProperty("withheld", out var withheld) && withheld.GetBoolean());
            Assert.Contains("guessed", json.RootElement.GetRawText());
            Assert.Throws<ArgumentException>(() => h.Transforms.ExportBundle(bundleId, "pdf"));
            Assert.Throws<KeyNotFoundException>(() => h.Transforms.ExportBundle("nope", "markdown"));

            Assert.Contains("needs a title", await RefusedAsync(h, EvidenceBundleTactic.Key, null, Selection.Of(SelectionKind.Message, new[] { m1 })));
            Assert.Contains("No bundle", await RefusedAsync(h, EvidenceBundleTactic.Key, new { bundleId = "nope" }, Selection.Of(SelectionKind.Message, new[] { m1 })));
            Assert.True(await h.Transforms.DeleteBundleAsync(bundleId, "tester", CancellationToken.None));
            Assert.Empty(h.Transforms.Bundles());
            Assert.False(await h.Transforms.DeleteBundleAsync(bundleId, "tester", CancellationToken.None));
        }

        [Fact]
        public async Task ExportAndCustomChart_HandOverWhatTheRoutesNeed_AndTheCatalogueIsWellFormed()
        {
            await using var h = await SignalsHarness.StartAsync(transforms: true);
            var mine = h.Runtime.Tactics.Catalog().Where(d => d.Code is "X20" or "X21" or "X22" or "X28" or "X29" or "X34" or "X37" or "X38" or "X39" or "X40").OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
            Assert.Equal(10, mine.Count);
            foreach (var d in mine)
            {
                using var schema = JsonDocument.Parse(d.ParameterSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, $"{d.Key} names the unknown permission '{d.RequiredPermission}'");
                Assert.False(d.Outbound);
                Assert.Equal(0, d.TypicalLlmCalls);
                Assert.Equal(Lane.Cpu, d.Lane);
            }

            var export = await RunAsync(h, DatasetExportTactic.Key, new { format = "parquet" }, new Selection(SelectionKind.ExploreDrill, new[] { new SelectionItem("drill-token-1") }));
            var body = export[0].Data!.Value;
            Assert.Equal(("explore/export", "Parquet", "drill-token-1"), (body.GetProperty("route").GetString(), body.GetProperty("body").GetProperty("format").GetString(),
                body.GetProperty("body").GetProperty("drillToken").GetString()));
            Assert.Contains("'format' must be", await RefusedAsync(h, DatasetExportTactic.Key, new { format = "xlsx" }, new Selection(SelectionKind.ExploreDrill, new[] { new SelectionItem("t") })));

            // A chart of a dataset that a module of this build declares (the signals module's alerts).
            var dataset = new Selection(SelectionKind.Dataset, new[] { new SelectionItem(SignalsModule.AlertsDataset) });
            var chart = await RunAsync(h, CustomChartTactic.Key, new { name = "Alerts by kind", groupBy = new[] { "content.event" }, measures = new[] { "count", "people" }, chart = "bar" }, dataset);
            string viewId = chart[0].Data!.Value.GetProperty("viewId").GetString()!;
            using (var op = h.Runtime.Stores.Operator.OpenRead())
            {
                var saved = op.Query(null, "SELECT page, name, state_json FROM saved_views WHERE view_id=$v", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$v", viewId)).Single();
                Assert.Equal((TransformsModule.DashboardPage, "Alerts by kind"), (saved.Item1, saved.Item2));
                using var state = JsonDocument.Parse(saved.Item3);
                Assert.Equal((SignalsModule.AlertsDataset, "omniscience.persons.read"), (state.RootElement.GetProperty("datasetId").GetString(), state.RootElement.GetProperty("requiredPermission").GetString()));
            }
            Assert.Contains("has no dimension", await RefusedAsync(h, CustomChartTactic.Key, new { name = "Bad", groupBy = new[] { "content.language" }, measures = new[] { "count" } }, dataset));
            Assert.Contains("has no measure", await RefusedAsync(h, CustomChartTactic.Key, new { name = "Bad", measures = new[] { "revenue" } }, dataset));
            Assert.Contains("no dataset", await RefusedAsync(h, CustomChartTactic.Key, new { name = "Bad", measures = new[] { "count" } }, new Selection(SelectionKind.Dataset, new[] { new SelectionItem("nope") })));
        }
    }
}
