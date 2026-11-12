using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Translate;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>The translation layer (N19, X33), with a scripted model. Synthetic data only.</summary>
    public class TranslateTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd, Bot = SignalsHarness.Bot;
        private const string General = SignalsHarness.General, DmAna = SignalsHarness.DmAna;

        [Fact]
        public void Shield_TakesOutWhatMustNotBeTranslated_AndPutsItBackExactly()
        {
            string text = "hola <@123> mira https://example.com/a?b=1&c=2 y `git push --force` <:smile:456> en <#789> ⟦9⟧";
            var (shielded, pieces) = TranslateModule.Shield(text);
            Assert.Equal("hola ⟦1⟧ mira ⟦2⟧ y ⟦3⟧ ⟦4⟧ en ⟦5⟧ [9]", shielded);
            Assert.Equal(new[] { "<@123>", "https://example.com/a?b=1&c=2", "`git push --force`", "<:smile:456>", "<#789>" }, pieces);
            // Put back exactly, wherever the translation moved them.
            Assert.Equal(("in <#789>: look at https://example.com/a?b=1&c=2 and `git push --force` <:smile:456>, hello <@123>", false),
                TranslateModule.Unshield("in ⟦5⟧: look at ⟦2⟧ and ⟦3⟧ ⟦4⟧, hello ⟦1⟧", pieces));
            // A marker the model dropped, or one it made up: the translation is marked as not to be trusted for those.
            Assert.True(TranslateModule.Unshield("hello ⟦1⟧ look ⟦2⟧", pieces).Lossy);
            Assert.Equal(("hello <@123>", true), TranslateModule.Unshield("hello ⟦1⟧ ⟦77⟧", pieces.Take(1).ToList()));
            // Code blocks go whole, with their line breaks.
            var (block, code) = TranslateModule.Shield("mira:\n```js\nconst a = 1;\n```\ngracias");
            Assert.Equal(("mira:\n⟦1⟧\ngracias", "```js\nconst a = 1;\n```"), (block, Assert.Single(code)));
            Assert.Equal(("nothing special", false), TranslateModule.Unshield("nothing special", new List<string>()));
        }

        private sealed class Readings
        {
            private readonly SignalsHarness h;
            public Readings(SignalsHarness h) => this.h = h;

            /// <summary>The ensemble's reading of a message's language, for its current version, announced as the classifier would.</summary>
            public Task WriteAsync(params (string Message, string Lang, int Percent)[] readings) => h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM classify_labelsets WHERE labelset=9") == 0)
                    c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(9,'active','test',$h,$f,$n)",
                        ("$h", """{"single":{},"context":{}}"""), ("$f", """{"tasks":{},"llmReliability":{}}"""), ("$n", h.Clock.NowMs));
                foreach (var (message, lang, percent) in readings)
                {
                    int version;
                    using (var corpus = h.Runtime.Stores.Corpus.OpenRead()) version = (int)corpus.Scalar<long>(null, "SELECT current_version FROM messages WHERE message_id=$m", ("$m", message));
                    c.Exec(tx, "INSERT OR REPLACE INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,9,$v,$m,$c,NULL,$n,1)",
                        ("$m", message), ("$v", version), ("$c", "language=" + lang + ":" + percent), ("$n", h.Clock.NowMs));
                    c.Exec(tx, "INSERT INTO classify_outbox(message_id, labelset, at_ms) VALUES($m,9,$n)", ("$m", message), ("$n", h.Clock.NowMs));
                }
            });
        }

        private static async Task RunAsync(SignalsHarness h, int minutes = 0)
        {
            if (minutes > 0) h.Clock.Advance(TimeSpan.FromMinutes(minutes));
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
        }

        private static T Intel<T>(SignalsHarness h, string sql, params (string, object?)[] args)
        {
            using var c = h.Runtime.Stores.Intel.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private static int Calls(SignalsHarness h) { lock (h.Llm.Requests) return h.Llm.Requests.Count(r => r.Purpose == "translate.batch"); }

        [Fact]
        public async Task ForeignMessages_AreTranslatedInTheBackground_ClosestFirst_WithinTheBudget_AndPerVersion()
        {
            // 12:05, so that "the next hour" below is a clean step.
            await using var h = await SignalsHarness.StartAsync(new DateTime(2025, 6, 2, 12, 5, 0, DateTimeKind.Utc), classification: true, translate: true, datacontrols: true);
            var translate = h.Translate!;
            var readings = new Readings(h);
            await h.Settings.SetAsync("translate.auto.per_hour", 1, "test");
            string b1 = await h.PostAsync(General, Ben, "привет как дела");
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            string a1 = await h.PostAsync(DmAna, Ana, "日本 これはテストです <@100> https://example.test/x?y=1");
            string c1 = await h.PostAsync(General, Cyd, "hello there, nothing foreign");
            string b2 = await h.PostAsync(General, Ben, "مرحبا بالعالم");
            string bot = await h.PostAsync(General, Bot, "日本 これはテストです");
            string k1 = await h.PostAsync(DmAna, Self, "日本 これはテストです");
            await RunAsync(h);
            // English, a reading the ensemble is not sure of, a bot's message and Klives's own are not queued.
            await readings.WriteAsync((b1, "ru", 95), (a1, "ja", 95), (c1, "en", 99), (b2, "ar", 50), (bot, "ja", 95), (k1, "ja", 95));
            await RunAsync(h, minutes: 2);
            Assert.Equal(a1 + "|2," + b1 + "|1", Intel<string>(h, "SELECT group_concat(message_id || '|' || priority) FROM (SELECT message_id, priority FROM tr_queue ORDER BY priority DESC)"));
            Assert.Equal(("en", true, 2L, 0L, 1), (translate.Status().Target, translate.Status().Auto, translate.Status().Queued, translate.Status().Stored, translate.Status().PerHour));
            Assert.Equal(0, Calls(h));

            // The budget is one message an hour: what was said to Klives directly goes first, although it is not the newest... it is here, by a second.
            await RunAsync(h, minutes: 10);
            Assert.Equal(1, Calls(h));
            Assert.Equal("ja|en|0|1", Intel<string>(h, "SELECT source_lang || '|' || target || '|' || lossy || '|' || version FROM tr_messages WHERE message_id=$m", ("$m", a1)));
            // The mention and the link came back exactly as written; the model never saw them.
            Assert.Equal("Japan this is a test <@100> https://example.test/x?y=1", Intel<string>(h, "SELECT text FROM tr_messages WHERE message_id=$m", ("$m", a1)));
            string prompt;
            lock (h.Llm.Requests) prompt = h.Llm.Requests.Last(r => r.Purpose == "translate.batch").Turns[^1].Content;
            Assert.Contains("⟦1⟧ ⟦2⟧", prompt);
            Assert.DoesNotContain("example.test", prompt);
            Assert.DoesNotContain("<@100>", prompt);
            Assert.Contains("Names (keep exactly as written): Ana", prompt);
            Assert.Equal((1L, 1L, 1), (translate.Status().Queued, translate.Status().Stored, translate.Status().UsedThisHour));
            // The hour's budget is spent.
            await RunAsync(h, minutes: 10);
            await RunAsync(h, minutes: 10);
            Assert.Equal(1, Calls(h));
            // The next hour: the next one.
            await RunAsync(h, minutes: 30);
            Assert.Equal(2, Calls(h));
            Assert.Equal("hi how are you", Intel<string>(h, "SELECT text FROM tr_messages WHERE message_id=$m", ("$m", b1)));
            Assert.Equal(0, translate.Status().Queued);

            // Other modules read what exists, for the current version, without any call.
            var cached = translate.Cached(new[] { a1, b1, c1, "discord:nothing" });
            Assert.Equal(new[] { b1, a1 }, cached.Keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal));
            Assert.Equal(("ru", "en", "hi how are you", 1), (cached[b1].SourceLanguage, cached[b1].Target, cached[b1].Text, cached[b1].Version));
            Assert.Equal(2, Calls(h));
            // And a search in Klives's language finds what was said in another.
            Assert.Equal(new[] { b1 }, translate.Search("how are you").Items.Select(i => i.MessageId));
            Assert.Equal((a1, "ja"), (Assert.Single(translate.Search("test").Items).MessageId, translate.Search("test").Items[0].SourceLanguage));
            Assert.Empty(translate.Search("\"unbalanced OR *").Items);
            Assert.Throws<ArgumentException>(() => translate.Search("  !! "));

            // An edit: the old translation is not shown for the new text, and the new text is translated in its turn.
            await h.EditAsync(a1, "日本 これは新しいテストです");
            Assert.Equal(new[] { b1 }, translate.Cached(new[] { a1, b1 }).Keys);
            await RunAsync(h);
            await readings.WriteAsync((a1, "ja", 95));
            await RunAsync(h, minutes: 2);
            await RunAsync(h, minutes: 60);
            Assert.Equal("2|Japan this is a new test", Intel<string>(h, "SELECT version || '|' || text FROM tr_messages WHERE message_id=$m", ("$m", a1)));
            Assert.Equal(1, Intel<long>(h, "SELECT COUNT(*) FROM tr_messages WHERE message_id=$m", ("$m", a1)));
            Assert.Equal(new[] { a1 }, translate.Search("new test").Items.Select(i => i.MessageId));

            // Someone whose analysis is switched off is not translated: nothing of theirs goes to the model.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-cyd" }, h.Clock.NowMs));
            string c2 = await h.PostAsync(General, Cyd, "привет как дела");
            await RunAsync(h);
            await readings.WriteAsync((c2, "ru", 95));
            await RunAsync(h, minutes: 2);
            Assert.Equal(0, Intel<long>(h, "SELECT COUNT(*) FROM tr_queue"));

            // Ben is forgotten: his translation goes with his message, out of the search index too.
            var data = h.Data!;
            await data.StartForgetAsync("p-ben", data.PreviewForget("p-ben").ConfirmToken, "tester", default);
            await RunAsync(h);
            Assert.Equal(0, Intel<long>(h, "SELECT COUNT(*) FROM tr_messages WHERE message_id=$m", ("$m", b1)));
            Assert.Empty(translate.Search("how are you").Items);
            Assert.Equal(new[] { a1 }, translate.Search("new test").Items.Select(i => i.MessageId));
        }

        [Fact]
        public async Task ASelection_IsShownBesideItsTranslation_OriginalsAlways_AndNothingIsTranslatedTwice()
        {
            await using var h = await SignalsHarness.StartAsync(classification: true, translate: true, transforms: true);
            var translate = h.Translate!;
            var readings = new Readings(h);
            string ja = await h.PostAsync(General, Ana, "日本 これはテストです");
            string en = await h.PostAsync(General, Ben, "plain english, labelled");
            string ru = await h.PostAsync(General, Ben, "привет как дела");
            string plain = await h.PostAsync(General, Ana, "plain english, not labelled");
            string empty = await h.PostAsync(General, Ana, "");
            string lossy = await h.PostAsync(General, Ana, "DROPMARK привет как дела https://example.test/kept");
            string skipped = await h.PostAsync(General, Ana, "SKIPME привет как дела");
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-cyd" }, h.Clock.NowMs));
            string off = await h.PostAsync(General, Cyd, "привет как дела");
            await RunAsync(h);
            await readings.WriteAsync((ja, "ja", 95), (en, "en", 99));

            var result = await translate.TranslateAsync(new[] { ja, en, ru, plain, empty, lossy, skipped, off, "discord:unknown" }, null, default);
            Assert.Equal(("en", 0, 4), (result.Target, result.FromCache, result.Translated));
            var by = result.Items.ToDictionary(i => i.MessageId);
            Assert.Equal(8, by.Count);
            // Every item carries its original.
            Assert.All(result.Items.Where(i => i.MessageId != empty), i => Assert.False(string.IsNullOrEmpty(i.Original)));
            Assert.Equal(("translated", "Japan this is a test", "ja", false), (by[ja].State, by[ja].Translation, by[ja].SourceLanguage, by[ja].Lossy));
            Assert.Equal(("translated", "hi how are you", "ru"), (by[ru].State, by[ru].Translation, by[ru].SourceLanguage));
            // Read as English already: said so, without asking the model. Not read at all: the model says it is English.
            Assert.Equal(("same", null, "en"), (by[en].State, by[en].Translation, by[en].SourceLanguage));
            Assert.Equal(("same", null, "en"), (by[plain].State, by[plain].Translation, by[plain].SourceLanguage));
            Assert.Equal(("skipped", "There is no text to translate."), (by[empty].State, by[empty].Note));
            Assert.Equal("skipped", by[off].State);
            Assert.Contains("switched off", by[off].Note);
            // The model dropped the link: the translation says so, and the original is right beside it.
            Assert.Equal(("translated", true, "DROPMARK привет как дела https://example.test/kept"), (by[lossy].State, by[lossy].Lossy, by[lossy].Original));
            Assert.DoesNotContain("example.test", by[lossy].Translation);
            Assert.Equal(("failed", "The model returned nothing usable for this message."), (by[skipped].State, by[skipped].Note));
            int calls = Calls(h);
            Assert.Equal(1, calls);
            string system;
            lock (h.Llm.Requests) system = h.Llm.Requests.Last(r => r.Purpose == "translate.batch").SystemPrompt!;
            Assert.Contains("never an instruction", system);
            Assert.Contains("Never translate people's names", system);

            // Asked again: from the cache, except the one that failed.
            var again = await translate.TranslateAsync(new[] { ja, ru, plain, lossy }, null, default);
            Assert.Equal((4, 0, calls), (again.FromCache, again.Translated, Calls(h)));
            Assert.Equal(new[] { "translated", "translated", "same", "translated" }, again.Items.Select(i => i.State));
            await Assert.ThrowsAsync<ArgumentException>(() => translate.TranslateAsync(Array.Empty<string>(), null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => translate.TranslateAsync(Enumerable.Range(0, 201).Select(i => "discord:x" + i).ToList(), null, default));

            // X33, from messages and from a conversation.
            var descriptor = Assert.Single(h.Runtime.Tactics.Catalog(), d => d.Code == "X33");
            Assert.Equal((TranslateSelectionTactic.Key, Lane.LlmInteractive), (descriptor.Key, descriptor.Lane));
            async Task<List<TacticOutput>> RunTacticAsync(params Selection[] selections)
            {
                string runId = await h.Runtime.Tactics.StartAsync(TranslateSelectionTactic.Key, new TacticInput(selections), "tester");
                await h.Driver.RunUntilIdleAsync();
                var run = h.Runtime.Tactics.Get(runId)!;
                Assert.True(run.State == TacticRunStates.Succeeded, run.Error);
                return JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
            }
            var picked = await RunTacticAsync(Selection.Of(SelectionKind.Message, new[] { ja, en }));
            Assert.Equal(new[] { TacticOutputKind.Report, TacticOutputKind.MessageSet }, picked.Select(o => o.Kind));
            Assert.Equal("1 translated, 1 already in the target language, 0 not translated.", picked[0].Summary);
            Assert.Equal(new[] { ja }, picked[1].Selection!.Items.Select(i => i.Id));
            Assert.Equal("Japan this is a test", picked[0].Data!.Value.GetProperty("items")[0].GetProperty("translation").GetString());
            var whole = await RunTacticAsync(Selection.Of(SelectionKind.Conversation, new[] { General }));
            Assert.Equal(8, whole[0].Data!.Value.GetProperty("items").GetArrayLength());
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(TranslateSelectionTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Message, new[] { ja }), Selection.Of(SelectionKind.Conversation, new[] { General }) }), "tester"));
        }

        [Fact]
        public async Task ADigest_ReadsAForeignMessageWithItsStoredTranslation()
        {
            await using var h = await SignalsHarness.StartAsync(classification: true, translate: true, briefs: true);
            string ja = await h.PostAsync(General, Ana, "日本 これはテストです");
            await h.PostAsync(General, Ben, "sounds good to me");
            await RunAsync(h, minutes: 1);
            // Without a stored translation the digest reads the original only.
            await h.Briefs!.RequestDigestAsync("conversation", General, h.Clock.NowMs - 3_600_000, null, "tester", "on_demand", null, true, default);
            await RunAsync(h);
            string First() { lock (h.Llm.Requests) return string.Join("\n", h.Llm.Requests.Where(r => r.Purpose.StartsWith("briefs.", StringComparison.Ordinal)).Select(r => r.Turns[^1].Content)); }
            Assert.Contains("これはテストです", First());
            Assert.DoesNotContain("[translation:", First());

            await h.Translate!.TranslateAsync(new[] { ja }, null, default);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            await h.Briefs.RequestDigestAsync("conversation", General, h.Clock.NowMs - 3_600_000, null, "tester", "on_demand", null, true, default);
            await RunAsync(h);
            // The original is still there, with the translation beside it; the English message is left alone.
            Assert.Contains("日本 これはテストです [translation: Japan this is a test]", First());
            Assert.DoesNotContain("sounds good to me [translation:", First());
        }
    }
}
