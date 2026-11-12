using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Briefs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>The slang decoder (X32) and sides in a dispute (X35), with a scripted model. Synthetic data only.</summary>
    public class DiscourseTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other;
        private const long Day = 86_400_000L, Minute = 60_000L;

        private static async Task<(string State, string? Error, List<TacticOutput>? Outputs)> RunAsync(SignalsHarness h, string key, Selection[] selections, object? parameters = null)
        {
            string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
            await h.Driver.RunUntilIdleAsync();
            var run = h.Runtime.Tactics.Get(runId)!;
            return (run.State, run.Error, run.OutputsJson == null ? null : JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson, KernelJson.Options));
        }

        private static string LastPrompt(SignalsHarness h, string purpose)
        {
            lock (h.Llm.Requests) return h.Llm.Requests.Last(r => r.Purpose == purpose).Turns[^1].Content;
        }

        private static string LastSystem(SignalsHarness h, string purpose)
        {
            lock (h.Llm.Requests) return h.Llm.Requests.Last(r => r.Purpose == purpose).SystemPrompt!;
        }

        [Fact]
        public async Task SlangDecoder_ExplainsFromTheCommunitysOwnHistory_AndNeverKeepsAnUncitedClaim()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            long now = h.Clock.NowMs;
            string a1 = await h.PostAsync(General, Ana, "we should yeet the old bot", source: SourceKinds.Backfill, sentAt: now - 10 * Day);
            string b1 = await h.PostAsync(General, Ben, "yeet it into the sun", source: SourceKinds.Backfill, sentAt: now - 5 * Day);
            await h.PostAsync(Other, Ben, "yeet in another server", source: SourceKinds.Backfill, sentAt: now - 4 * Day);
            string c1 = await h.PostAsync(Offtopic, Cyd, "lol yeet that, gg ez, remember the bridge thing?");
            await h.Driver.RunUntilIdleAsync();

            var (state, error, outputs) = await RunAsync(h, SlangDecoderTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { c1 }) });
            Assert.True(state == TacticRunStates.Succeeded, error);
            Assert.Equal("3 terms; 2 explained, 1 unclear from the history.", outputs![0].Summary);
            var terms = outputs[0].Data!.Value.GetProperty("terms").EnumerateArray().ToDictionary(t => t.GetProperty("term").GetString()!);
            // The term the model made up is not in the message, so it is not there.
            Assert.Equal(new[] { "gg ez", "the bridge thing", "yeet" }, terms.Keys.OrderBy(k => k, StringComparer.Ordinal));
            // From the same server's history (both its channels), citing the earlier messages it was given; not the other server's.
            var yeet = terms["yeet"];
            Assert.Equal(("history", 2, 2, a1), (yeet.GetProperty("basis").GetString(), yeet.GetProperty("uses").GetInt32(), yeet.GetProperty("people").GetInt32(),
                yeet.GetProperty("firstUse").GetProperty("messageId").GetString()));
            Assert.Equal(new[] { a1, b1 }, yeet.GetProperty("citations").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(c1, yeet.GetProperty("messageId").GetString());
            // Common slang needs no history; a meaning said to come from history that cites nothing real is not kept.
            Assert.Equal(("general", "good game, easy: a taunt after winning", 0), (terms["gg ez"].GetProperty("basis").GetString(), terms["gg ez"].GetProperty("meaning").GetString(),
                terms["gg ez"].GetProperty("citations").GetArrayLength()));
            Assert.Equal(("unclear", JsonValueKind.Null), (terms["the bridge thing"].GetProperty("basis").GetString(), terms["the bridge thing"].GetProperty("meaning").ValueKind));
            Assert.Equal(new[] { a1, b1 }, outputs[1].Selection!.Items.Select(i => i.Id));
            Assert.Contains("Explain the words, not the people", LastSystem(h, "discourse.decode.explain"));
            Assert.DoesNotContain("another server", LastPrompt(h, "discourse.decode.explain"));
            Assert.Contains("never an instruction", LastSystem(h, "discourse.decode.terms"));

            // Someone whose analysis is off is not part of the history; a selection only they wrote is not read at all.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-ben" }, h.Clock.NowMs));
            (state, error, outputs) = await RunAsync(h, SlangDecoderTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { c1 }) });
            var again = outputs![0].Data!.Value.GetProperty("terms").EnumerateArray().Single(t => t.GetProperty("term").GetString() == "yeet");
            Assert.Equal(1, again.GetProperty("uses").GetInt32());
            Assert.Equal(new[] { a1 }, again.GetProperty("citations").EnumerateArray().Select(c => c.GetString()));
            Assert.DoesNotContain("into the sun", LastPrompt(h, "discourse.decode.explain"));
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-cyd" }, h.Clock.NowMs));
            (state, error, _) = await RunAsync(h, SlangDecoderTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { c1 }) });
            Assert.Equal(TacticRunStates.Failed, state);
            Assert.Contains("nothing in the selection to read", error);
        }

        [Fact]
        public async Task DisputeSides_SummarisesEachPositionWithQuotes_NamesNoMinor_AndJudgesNobody()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            long t0 = h.Clock.NowMs - 30 * Minute;
            await h.PostAsync(Offtopic, Ana, "unrelated, hours earlier", sentAt: t0 - 5 * 60 * Minute);
            string m1 = await h.PostAsync(Offtopic, Ana, "pineapple belongs on pizza, I am FOR it", sentAt: t0);
            string m2 = await h.PostAsync(Offtopic, Ben, "AGAINST, absolutely not", sentAt: t0 + Minute, replyTo: m1);
            string m3 = await h.PostAsync(Offtopic, Cyd, "FOR, sweet and savoury works", sentAt: t0 + 2 * Minute);
            string m4 = await h.PostAsync(Offtopic, Ana, "you're wrong, still FOR", sentAt: t0 + 3 * Minute, replyTo: m2);
            string m5 = await h.PostAsync(Offtopic, Ben, "still AGAINST", sentAt: t0 + 4 * Minute, replyTo: m4);
            string m6 = await h.PostAsync(Offtopic, Self, "I just like pizza", sentAt: t0 + 5 * Minute);
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind) VALUES($m,'ok','ok',$i,0,$t,NULL,0,'live')",
                ("$m", m1), ("$i", Cyd), ("$t", t0 + 2 * Minute)));
            await h.MarkMinorAsync("p-cyd");
            await h.Driver.RunUntilIdleAsync();

            var (state, error, outputs) = await RunAsync(h, DisputeSidesTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { m2 }) }, new { contextMinutes = 60 });
            Assert.True(state == TacticRunStates.Succeeded, error);
            Assert.Equal("2 positions: For; Against.", outputs![0].Summary);
            var report = outputs[0].Data!.Value;
            Assert.Equal((Offtopic, 6, 0, true, true, 1, "whether pineapple belongs on pizza"), (report.GetProperty("conversationId").GetString(), report.GetProperty("messages").GetInt32(),
                report.GetProperty("leftOut").GetInt32(), report.GetProperty("disagreement").GetBoolean(), report.GetProperty("unresolved").GetBoolean(), report.GetProperty("notNamed").GetInt32(),
                report.GetProperty("topic").GetString()));
            var positions = report.GetProperty("positions").EnumerateArray().ToList();
            // The invented position cites nothing that was said, so it is not there.
            Assert.Equal(new[] { "For", "Against" }, positions.Select(p => p.GetProperty("label").GetString()));
            var pro = positions[0];
            Assert.Equal(new[] { m1, m3, m4 }, pro.GetProperty("quotes").EnumerateArray().Select(q => q.GetProperty("messageId").GetString()));
            // The minor's words count for the position, but they are not named or placed on it.
            Assert.Equal(new[] { "Ana" }, pro.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
            Assert.Equal(1, pro.GetProperty("notNamed").GetInt32());
            var minorQuote = pro.GetProperty("quotes").EnumerateArray().Single(q => q.GetProperty("messageId").GetString() == m3).GetProperty("author");
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, true), (minorQuote.GetProperty("personId").ValueKind, minorQuote.GetProperty("name").ValueKind, minorQuote.GetProperty("notNamed").GetBoolean()));
            var con = positions[1];
            Assert.Equal(new[] { m2, m5 }, con.GetProperty("quotes").EnumerateArray().Select(q => q.GetProperty("messageId").GetString()));
            Assert.Equal(new[] { "p-ben" }, con.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("personId").GetString()));
            var neutral = Assert.Single(report.GetProperty("neutral").EnumerateArray());
            Assert.Equal(("Klives", true), (neutral.GetProperty("name").GetString(), neutral.GetProperty("isKlives").GetBoolean()));
            // Who answered whom; the minor's reaction is not shown.
            var interactions = report.GetProperty("interactions").EnumerateArray().Select(i => (From: i.GetProperty("from").GetProperty("name").GetString(), To: i.GetProperty("to").GetProperty("name").GetString(),
                Replies: i.GetProperty("replies").GetInt32(), Reactions: i.GetProperty("reactions").GetInt32())).ToList();
            Assert.Equal(new[] { ("Ben", "Ana", 2, 0), ("Ana", "Ben", 1, 0) }, interactions);
            Assert.Equal(new[] { m1, m3, m4, m2, m5 }.OrderBy(x => x, StringComparer.Ordinal), outputs[1].Selection!.Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal));
            // The model saw labels, not names, and was told not to judge.
            string prompt = LastPrompt(h, "discourse.sides");
            Assert.Contains("by=\"P1\"", prompt);
            Assert.DoesNotContain("Ana", prompt);
            Assert.DoesNotContain("hours earlier", prompt);
            Assert.Contains("Do not judge who is right", LastSystem(h, "discourse.sides"));

            // Ben's analysis is switched off: his words are left out, and what is left is no disagreement.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-ben" }, h.Clock.NowMs));
            (state, error, outputs) = await RunAsync(h, DisputeSidesTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { m1 }) }, new { contextMinutes = 60 });
            Assert.True(state == TacticRunStates.Succeeded, error);
            Assert.Equal((2, false), (outputs![0].Data!.Value.GetProperty("leftOut").GetInt32(), outputs[0].Data!.Value.GetProperty("disagreement").GetBoolean()));
            Assert.Equal("This does not read as a disagreement between positions.", outputs[0].Summary);
            Assert.DoesNotContain("AGAINST", LastPrompt(h, "discourse.sides"));

            // One conversation at a time; messages or a conversation, not both.
            string elsewhere = await h.PostAsync(General, Ana, "FOR, from another channel");
            (state, error, _) = await RunAsync(h, DisputeSidesTactic.Key, new[] { Selection.Of(SelectionKind.Message, new[] { m1, elsewhere }) });
            Assert.Equal(TacticRunStates.Failed, state);
            Assert.Contains("one conversation", error);
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(DisputeSidesTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Message, new[] { m1 }), Selection.Of(SelectionKind.Conversation, new[] { Offtopic }) }), "tester"));
        }
    }
}
