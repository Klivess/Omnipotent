using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Monitors;
using Omnipotent.Services.OmniscienceV3.Modules.Signalling;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>The monitors that need no model (X42, X43, X46, X47, X51). Synthetic data only.</summary>
    public class MonitorsTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, Third = SignalsHarness.Third;
        private const string DmAna = SignalsHarness.DmAna, DmBen = SignalsHarness.DmBen;
        private const long Day = 86_400_000L, Hour = 3_600_000L;

        private static async Task RunAsync(SignalsHarness h, int minutes = 0)
        {
            if (minutes > 0) h.Clock.Advance(TimeSpan.FromMinutes(minutes));
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
        }

        private static async Task<List<TacticOutput>> TacticAsync(SignalsHarness h, string key, Selection[] selections, object? parameters)
        {
            string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
            await h.Driver.RunUntilIdleAsync();
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, run.Error);
            return JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
        }

        private static List<NoticeDto> Notices(SignalsHarness h, string kind) => h.Signals.Notices(null, kind, null, null, 100).Items.ToList();

        private static string Trigger(MonitorEventDto e) => e.Data.GetProperty("trigger").GetString()!;

        /// <summary>The ensemble's readings of messages, as the classifier stores them for an active label set.</summary>
        private static Task ReadingsAsync(SignalsHarness h, IEnumerable<string> messages, string codes) => h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
        {
            if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM classify_labelsets WHERE labelset=9") == 0)
                c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(9,'active','test',$h,$f,$n)",
                    ("$h", """{"single":{},"context":{}}"""), ("$f", """{"tasks":{},"llmReliability":{}}"""), ("$n", h.Clock.NowMs));
            foreach (string m in messages)
                c.Exec(tx, "INSERT OR REPLACE INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,9,1,$m,$c,NULL,$n,1)",
                    ("$m", m), ("$c", codes), ("$n", h.Clock.NowMs));
        });

        // ── X42 ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task PersonActivity_TellsWhenSomeoneComesBackOrPostsInAServer_OnlyLive_NeverAboutMinors()
        {
            await using var h = await SignalsHarness.StartAsync(monitors: true, datacontrols: true);
            long now = h.Clock.NowMs;
            await h.PostAsync(General, Ben, "last seen a while ago", source: SourceKinds.Backfill, sentAt: now - 20 * Day, arrivedAt: now - 20 * Day);
            await h.PostAsync(General, Ana, "ana was here yesterday", source: SourceKinds.Backfill, sentAt: now - Day, arrivedAt: now - Day);
            await RunAsync(h);

            var outputs = await TacticAsync(h, PersonActivityWatchTactic.Key, new[] { Selection.Of(SelectionKind.Person, new[] { "p-ben", "p-ana" }), Selection.Of(SelectionKind.Guild, new[] { "discord:g2" }) },
                new { absenceDays = 14 });
            var rule = outputs[0].Data!.Value;
            string watchId = rule.GetProperty("watchId").GetString()!;
            Assert.Equal(("Activity of Ben, Ana", MonitorsModule.PersonRuleKind, true), (rule.GetProperty("name").GetString(), rule.GetProperty("kind").GetString(), rule.GetProperty("enabled").GetBoolean()));
            Assert.Equal("Nothing found yet.", outputs[1].Summary);

            // Ben comes back after twenty days: told once, with the gap. Ana wrote yesterday: nothing.
            string b1 = await h.PostAsync(General, Ben, "hi all, I'm back");
            await h.PostAsync(General, Ben, "missed you");
            await h.PostAsync(General, Ana, "hello");
            await RunAsync(h);
            var back = Assert.Single(h.Monitors!.Events("person_activity", watchId).Items);
            Assert.Equal(("p-ben", "Ben", "returned", b1, 20.0, true), (back.PersonId, back.PersonName, Trigger(back), back.Data.GetProperty("messageId").GetString(), back.Data.GetProperty("gapDays").GetDouble(), back.Notified));
            var notice = Assert.Single(Notices(h, "person_activity"));
            Assert.Equal(("p-ben", MonitorsModule.ModuleId, "attention"), (notice.PersonId, notice.ModuleId, notice.Severity));

            // Ana posts in the watched server: told once that day, however much she writes there.
            string a1 = await h.PostAsync(Other, Ana, "first post in the second server");
            await h.PostAsync(Other, Ana, "and another");
            await RunAsync(h);
            var posted = h.Monitors.Events("person_activity", watchId).Items.Where(e => Trigger(e) == "posted").ToList();
            Assert.Equal(("p-ana", "discord:g2", a1), (Assert.Single(posted).PersonId, posted[0].GuildId, posted[0].Data.GetProperty("messageId").GetString()));

            // The next day, again.
            h.Clock.Advance(TimeSpan.FromDays(1));
            await h.PostAsync(Other, Ana, "next day");
            // What arrives by import, or live but long after it was written, is history: nothing.
            await h.PostAsync(Other, Ben, "imported", source: SourceKinds.Import);
            await h.PostAsync(Other, Ben, "arrived late", sentAt: h.Clock.NowMs - 2 * Day);
            await RunAsync(h);
            var all = h.Monitors.Events("person_activity", watchId).Items;
            Assert.Equal(2, all.Count(e => Trigger(e) == "posted" && e.PersonId == "p-ana"));
            Assert.DoesNotContain(all, e => Trigger(e) == "posted" && e.PersonId == "p-ben");
            string b3 = await h.PostAsync(Other, Ben, "now for real");
            await RunAsync(h);
            Assert.Equal(b3, h.Monitors.Events("person_activity", watchId).Items.Single(e => Trigger(e) == "posted" && e.PersonId == "p-ben").Data.GetProperty("messageId").GetString());
            Assert.Equal(4, h.Monitors.Rules(MonitorsModule.PersonRuleKind).Rules.Single().Fired30d);

            // Ben turns out to be under 18: nothing more about him, what was found is no longer shown, and no new watch can be made.
            await h.MarkMinorAsync("p-ben");
            h.Clock.Advance(TimeSpan.FromDays(1));
            await h.PostAsync(Other, Ben, "another day");
            await RunAsync(h);
            Assert.DoesNotContain(h.Monitors.Events("person_activity", watchId).Items, e => e.PersonId == "p-ben");
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM mon_events WHERE person_id='p-ben' AND at_ms > $t", ("$t", now + Day)));
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(PersonActivityWatchTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p-ben" }) }, JsonSerializer.SerializeToElement(new { absenceDays = 3 })), "tester"));
            Assert.Contains("under 18", refused.Message);
            // Someone whose analysis is off is not watched either; a watch must say what to watch for.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-cyd" }, h.Clock.NowMs));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Monitors.SavePersonRuleAsync(null, null, new[] { "p-cyd" }, 5, Array.Empty<string>(), "tester", default));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(PersonActivityWatchTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p-ana" }) }), "tester"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Monitors.SavePersonRuleAsync(null, null, new[] { "p-ana" }, null, new[] { "discord:nowhere" }, "tester", default));

            // Ana is forgotten: what was found about her goes, and the watch no longer names her.
            var data = h.Data!;
            await data.StartForgetAsync("p-ana", data.PreviewForget("p-ana").ConfirmToken, "tester", default);
            await RunAsync(h);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM mon_events WHERE person_id='p-ana'"));
            Assert.Equal("[\"p-ben\"]", h.Monitors.Rules().Rules.Single().Definition.GetProperty("persons").GetRawText());

            // And the watch can be removed.
            var removed = await TacticAsync(h, PersonActivityWatchTactic.Key, Array.Empty<Selection>(), new { delete = true, watchId });
            Assert.True(removed[0].Data!.Value.GetProperty("deleted").GetBoolean());
            Assert.Empty(h.Monitors.Rules().Rules);
        }

        // ── X46 ──────────────────────────────────────────────────────────────────

        private static Task CircleAsync(SignalsHarness h, string community, params string[] people) => h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "DELETE FROM ins_members");
            foreach (string p in people)
                c.Exec(tx, "INSERT INTO ins_members(person_id, community_id, strength, outside_share, bridge_to, updated_at_ms) VALUES($p,$c,5,0,NULL,$n)", ("$p", p), ("$c", community), ("$n", h.Clock.NowMs));
        });

        private static Task SpaceAsync(SignalsHarness h, string kind, string id, params (string Person, bool IsNew, bool Everywhere, int Recent)[] rising) => h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
        {
            string body = JsonSerializer.Serialize(new { rising = rising.Select(r => new { personId = r.Person, recent = r.Recent, earlier = 0, isNew = r.IsNew, everywhere = r.Everywhere, firstDay = "2025-05-30" }) });
            c.Exec(tx, """
                INSERT INTO ins_spaces(scope_kind, scope_id, title, platform, messages, previous_messages, people, about_klives, last_message_ms, window_days, body_json, factions_json, computed_at_ms)
                VALUES($k,$i,'A place','discord',100,50,5,0,NULL,90,$b,'{}',$n) ON CONFLICT(scope_kind, scope_id) DO UPDATE SET body_json=excluded.body_json
                """, ("$k", kind), ("$i", id), ("$b", body), ("$n", h.Clock.NowMs));
        });

        [Fact]
        public async Task NewPeople_AFirstDirectMessage_JoiningTheCircle_AndRisingNewcomers()
        {
            await using var h = await SignalsHarness.StartAsync(monitors: true, insights: true);
            long now = h.Clock.NowMs;
            await h.PostAsync(General, Ben, "hello server", source: SourceKinds.Backfill, sentAt: now - 3 * Day, arrivedAt: now - 3 * Day);
            await RunAsync(h);
            // What the nightly analytics found before the monitor first looked: learned, not told.
            await CircleAsync(h, "c1", "p-klives", "p-ana");
            await SpaceAsync(h, "guild", "discord:g1", ("p-ana", true, false, 30));

            // Ben writes to Klives directly for the first time (he was known from a server).
            string b1 = await h.PostAsync(DmBen, Ben, "hey, got a minute?");
            await h.PostAsync(DmBen, Ben, "it's about the trip");
            // Klives wrote to Ana first: her answer is not someone new reaching out. A chat without Klives is not his.
            await h.PostAsync(DmAna, Self, "are you coming?");
            await h.PostAsync(DmAna, Ana, "yes");
            await h.PostAsync(SignalsHarness.DmBotBen, Ben, "bot, help");
            await RunAsync(h, minutes: 61);
            var dm = Assert.Single(h.Monitors!.Events("new_person", null).Items);
            Assert.Equal(("p-ben", "first_direct_message", b1, true, DmBen), (dm.PersonId, Trigger(dm), dm.Data.GetProperty("messageId").GetString(), dm.Data.GetProperty("knownElsewhere").GetBoolean(), dm.ConversationId));
            Assert.Equal("attention", Assert.Single(Notices(h, "new_person")).Severity);
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM mon_circle"));
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM mon_seen"));

            // Cyd joins Klives's circle; in the first server Cyd is new and rising, Ben rises but was there before, and Dan is busier everywhere.
            await CircleAsync(h, "c1", "p-klives", "p-ana", "p-cyd");
            await SpaceAsync(h, "guild", "discord:g1", ("p-ana", true, false, 30), ("p-cyd", true, false, 25), ("p-ben", false, false, 40), ("p-bot", true, true, 50));
            await RunAsync(h, minutes: 61);
            var found = h.Monitors.Events("new_person", null).Items;
            var joined = found.Single(e => Trigger(e) == "joined_circle");
            Assert.Equal(("p-cyd", "c1", 2), (joined.PersonId, joined.Data.GetProperty("communityId").GetString(), joined.Data.GetProperty("circleSize").GetInt32()));
            var rising = found.Single(e => Trigger(e) == "rising_newcomer");
            Assert.Equal(("p-cyd", "discord:g1", 25), (rising.PersonId, rising.GuildId, rising.Data.GetProperty("recent").GetInt32()));
            Assert.Equal(new[] { "attention", "attention", "info" }, Notices(h, "new_person").Select(n => n.Severity).OrderBy(s => s, StringComparer.Ordinal));
            // Nothing is told twice.
            await RunAsync(h, minutes: 61);
            Assert.Equal(3, h.Monitors.Events("new_person", null).Items.Count);

            // Switched off: a first message from someone new is not told.
            var off = await TacticAsync(h, NewPersonRadarTactic.Key, Array.Empty<Selection>(), new { enabled = false });
            Assert.Equal(("new_person_radar", false), (off[0].Data!.Value.GetProperty("rule").GetString(), off[0].Data!.Value.GetProperty("enabled").GetBoolean()));
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, title, first_seen_ms) VALUES('discord:dm9','discord','dm','Cyd',$t)", ("$t", now));
                c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES('discord:dm9',$s,'member',$t),('discord:dm9',$c,'member',$t)", ("$s", Self), ("$c", Cyd), ("$t", now));
            });
            await h.PostAsync("discord:dm9", Cyd, "hi Klives");
            await RunAsync(h, minutes: 2);
            Assert.DoesNotContain(h.Monitors.Events("new_person", null).Items, e => Trigger(e) == "first_direct_message" && e.PersonId == "p-cyd");
        }

        // ── X43 ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TopicSurge_FiresWhenAPhraseOrAConceptSpikesAgainstItsUsualDay_OnceADay()
        {
            await using var h = await SignalsHarness.StartAsync(monitors: true, classification: true);
            long now = h.Clock.NowMs;
            // A usual day: one or two mentions in the first server, every day for four weeks (arrived by backfill, today).
            for (int d = 1; d <= 28; d++)
                for (int i = 0; i < 1 + d % 2; i++)
                    await h.PostAsync(General, d % 3 == 0 ? Ana : Ben, $"pizza night again {d}", source: SourceKinds.Backfill, sentAt: now - d * Day);
            // Today, twelve live mentions in the first server's two channels.
            var surge = new List<string>();
            for (int i = 0; i < 12; i++) surge.Add(await h.PostAsync(i % 2 == 0 ? General : Offtopic, i % 3 == 0 ? Ana : Cyd, $"pizza night tonight? {i}", sentAt: now - (12 - i) * 60_000L));
            // Another server, and an import: not counted.
            await h.PostAsync(Other, Ben, "pizza night in the other server");
            await h.PostAsync(General, Ben, "pizza night from an import", source: SourceKinds.Import);
            await RunAsync(h);

            var outputs = await TacticAsync(h, TopicSurgeTactic.Key, new[] { Selection.Of(SelectionKind.Guild, new[] { "discord:g1" }) }, new { phrase = "pizza night" });
            string watchId = outputs[0].Data!.Value.GetProperty("watchId").GetString()!;
            Assert.Equal("Surge of \"pizza night\"", outputs[0].Data!.Value.GetProperty("name").GetString());
            var status = outputs[1].Data!.Value;
            Assert.Equal((12, 1.5, true, 10, 28), (status.GetProperty("last24h").GetInt32(), status.GetProperty("usualDay").GetDouble(), status.GetProperty("surging").GetBoolean(),
                status.GetProperty("threshold").GetInt32(), status.GetProperty("days").GetArrayLength()));
            Assert.Equal(28, status.GetProperty("days").EnumerateArray().Sum(d => d.GetInt32()) - 14);
            Assert.EndsWith("It is surging now.", outputs[1].Summary);

            await RunAsync(h, minutes: 16);
            var fired = Assert.Single(h.Monitors!.Events("topic_surge", null).Items);
            Assert.Equal(("discord:g1", 12, watchId, 10), (fired.GuildId, fired.Data.GetProperty("count").GetInt32(), fired.RuleId, fired.Data.GetProperty("messageIds").GetArrayLength()));
            Assert.Equal(surge[^1], fired.Data.GetProperty("messageIds")[0].GetString());
            Assert.Null(Assert.Single(Notices(h, "topic_surge")).PersonId);
            // Still surging a quarter of an hour later: not told again the same day.
            await RunAsync(h, minutes: 16);
            Assert.Single(h.Monitors.Events("topic_surge", null).Items);

            // A taught concept works the same way; one that is not active yet cannot be watched.
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO classify_concepts(concept_id, name, examples_json, state, created_by, created_at_ms, updated_at_ms) VALUES('cpt1','Pizza plans','[]','active','test',$n,$n)", ("$n", now));
                c.Exec(tx, "INSERT INTO classify_concepts(concept_id, name, examples_json, state, created_by, created_at_ms, updated_at_ms) VALUES('cpt2','Half taught','[]','training','test',$n,$n)", ("$n", now));
                foreach (string m in surge.Take(11))
                    c.Exec(tx, "INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, model, model_version, as_of_ms, source_seq) VALUES('message',$m,1,'concept:cpt1','yes',0.9,'a2','concept.cpt1.v1',$n,1)",
                        ("$m", m), ("$n", now));
            });
            var concept = await h.Monitors.SaveTopicRuleAsync(null, null, "cpt1", null, Array.Empty<string>(), Array.Empty<string>(), null, null, "tester", default);
            Assert.Equal("Surge of Pizza plans", concept.Name);
            var conceptStatus = h.Monitors.TopicStatus(concept.WatchId);
            Assert.Equal((11, 0.0, true), (conceptStatus.Last24h, conceptStatus.UsualDay, conceptStatus.Surging));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Monitors.SaveTopicRuleAsync(null, null, "cpt2", null, Array.Empty<string>(), Array.Empty<string>(), null, null, "tester", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Monitors.SaveTopicRuleAsync(null, null, "nope", null, Array.Empty<string>(), Array.Empty<string>(), null, null, "tester", default));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Monitors.SaveTopicRuleAsync(null, null, "cpt1", "both", Array.Empty<string>(), Array.Empty<string>(), null, null, "tester", default));
            var bad = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(TopicSurgeTactic.Key, new TacticInput(Array.Empty<Selection>()), "tester"));
            Assert.Contains("phrase", bad.Message);

            // Someone whose analysis is off is not counted: without Cyd's eight messages there is no surge.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-cyd" }, h.Clock.NowMs));
            var after = h.Monitors.TopicStatus(watchId);
            Assert.Equal((4, false), (after.Last24h, after.Surging));
        }

        // ── X47 ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task KlivesTone_TellsOfABurstAboutHim_AgainstWhatTheCommunityUsuallySays()
        {
            await using var h = await SignalsHarness.StartAsync(monitors: true, rollups: true);
            await RunAsync(h);
            long now = h.Clock.NowMs;
            var negative = new List<string>();
            foreach (var (author, i) in new[] { Ana, Ben, Cyd, Ana, Ben, Cyd }.Select((a, i) => (a, i)))
                negative.Add(await h.PostAsync(i % 2 == 0 ? General : Offtopic, author, $"he broke it again {i}", sentAt: now - (6 - i) * 600_000L));
            var positive = new[] { await h.PostAsync(General, Ana, "he fixed it though"), await h.PostAsync(General, Ben, "thanks Klives") };
            string dm = await h.PostAsync(DmAna, Ana, "you broke it");
            string mine = await h.PostAsync(General, Self, "I did not");
            await ReadingsAsync(h, negative.Append(dm).Append(mine), "about_klives=mentioned:90|sentiment=negative:90");
            await ReadingsAsync(h, positive, "about_klives=addressed:90|sentiment=positive:90");
            await RunAsync(h, minutes: 16);

            var burst = Assert.Single(h.Monitors!.Events("klives_tone", null).Items);
            Assert.Equal(("discord:g1", "negative", 6, 3, 6), (burst.GuildId, burst.Data.GetProperty("tone").GetString(), burst.Data.GetProperty("count").GetInt32(),
                burst.Data.GetProperty("authors").GetInt32(), burst.Data.GetProperty("messageIds").GetArrayLength()));
            Assert.DoesNotContain(dm, burst.Data.GetProperty("messageIds").EnumerateArray().Select(m => m.GetString()));
            var notice = Assert.Single(Notices(h, "klives_tone"));
            Assert.Equal(("attention", (string?)null), (notice.Severity, notice.PersonId));
            await RunAsync(h, minutes: 16);
            Assert.Single(h.Monitors.Events("klives_tone", null).Items);

            // The second server talks about him like this every day (eight a day): five in a few hours is its usual, seven is not.
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                var today = DateOnly.FromDateTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(now));
                for (int d = 1; d <= 28; d++)
                    c.Exec(tx, """
                        INSERT OR REPLACE INTO rollup_scope_day(scope, key, day, msgs, authored, deleted, people, persons, labels, platforms, first_ms, last_ms, labelset, source_seq, computed_at_ms, klives)
                        VALUES('guild','discord:g2',$d,20,20,0,3,'{}','{}','{}',NULL,NULL,9,1,$n,$k)
                        """, ("$d", today.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$n", now),
                        ("$k", """{"N":8,"Kinds":{"mentioned":8},"Sentiment":{"negative":8},"Persons":{}}"""));
            });
            var usual = new List<string>();
            for (int i = 0; i < 5; i++) usual.Add(await h.PostAsync(Other, i % 2 == 0 ? Ana : Ben, $"klives again {i}"));
            await ReadingsAsync(h, usual, "about_klives=mentioned:90|sentiment=negative:90");
            await RunAsync(h, minutes: 16);
            Assert.DoesNotContain(h.Monitors.Events("klives_tone", null).Items, e => e.GuildId == "discord:g2");
            var more = new[] { await h.PostAsync(Other, Cyd, "klives again 5"), await h.PostAsync(Other, Cyd, "klives again 6") };
            await ReadingsAsync(h, more, "about_klives=mentioned:90|sentiment=negative:90");
            await RunAsync(h, minutes: 16);
            var second = h.Monitors.Events("klives_tone", null).Items.Single(e => e.GuildId == "discord:g2");
            Assert.Equal((7, 2.0), (second.Data.GetProperty("count").GetInt32(), second.Data.GetProperty("expected").GetDouble()));

            // Switched off: nothing more.
            var off = await TacticAsync(h, KlivesToneWatchTactic.Key, Array.Empty<Selection>(), new { enabled = false, minMessages = 4 });
            Assert.False(off[0].Data!.Value.GetProperty("enabled").GetBoolean());
            Assert.Equal(4, h.Settings.Get("monitors.klives_tone.min_messages", 5));
            var third = new List<string>();
            for (int i = 0; i < 6; i++) third.Add(await h.PostAsync(Third, i % 2 == 0 ? Ana : Cyd, $"about him {i}"));
            await ReadingsAsync(h, third, "about_klives=indirect:90|sentiment=negative:90");
            await RunAsync(h, minutes: 16);
            Assert.DoesNotContain(h.Monitors.Events("klives_tone", null).Items, e => e.GuildId == "discord:g3");
        }

        // ── X51 ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task DataHealth_OpensAndClosesProblems_AndTellsEachOnce()
        {
            await using var h = await SignalsHarness.StartAsync(monitors: true);
            // The first plan starts the schedules' clocks.
            await RunAsync(h);
            long now = h.Clock.NowMs;
            // The source brought thirty messages a day for a week, and nothing since yesterday's small hours.
            for (int d = 7; d >= 1; d--)
                for (int i = 0; i < 30; i++)
                {
                    long at = now - d * Day - 13 * Hour + i * 60_000L;
                    await h.PostAsync(General, Ana, $"steady {d} {i}", source: SourceKinds.Backfill, sentAt: at, arrivedAt: at);
                }
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO sources(source_id, platform, kind, label, status, status_detail, rank, created_at_ms, updated_at_ms) VALUES('wa','whatsapp','whatsapp-account','Phone','error','the session expired',0,$n,$n)", ("$n", now));
                c.Exec(tx, "INSERT INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES('old','discord','discord-account','Retired','disabled',0,$n,$n)", ("$n", now));
                c.Exec(tx, "INSERT INTO ingest_cursors(source_id, conversation_id, backward_complete, state, updated_at_ms) VALUES('test',$c,0,'backfill',$t)", ("$c", General), ("$t", now - 2 * Day));
                c.Exec(tx, "INSERT INTO ingest_cursors(source_id, conversation_id, backward_complete, state, updated_at_ms) VALUES('test',$c,0,'backfill',$t)", ("$c", Offtopic), ("$t", now - Hour));
            });
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Call(long at, string outcome, int prompt, int cached) => c.Exec(tx, """
                    INSERT INTO llm_calls(at_ms, module_id, purpose, model, work_class, prompt_tokens, cached_tokens, completion_tokens, outcome) VALUES($a,'links','links.categorise','m','Background',$p,$c,10,$o)
                    """, ("$a", at), ("$p", prompt), ("$c", cached), ("$o", outcome));
                for (int i = 0; i < 6; i++) Call(now - 10 * 60_000L, "ok", 1000, 900);
                for (int i = 0; i < 4; i++) Call(now - 10 * 60_000L, "RateLimited", 0, 0);
                for (int i = 0; i < 24; i++) Call(now - 2 * Hour, "ok", 1000, 100);
                c.Exec(tx, """
                    INSERT INTO jobs(module_id, kind, priority, lane, state, attempts, max_attempts, not_before_ms, error_class, error_message, created_at_ms, finished_at_ms)
                    VALUES('links','unfurl',1,'Io','dead',3,3,0,'Transient','HttpRequestException: 503',$n,$n)
                    """, ("$n", now));
            });
            await RunAsync(h, minutes: 11);

            var health = h.Monitors!.Health();
            Assert.True(health.Enabled);
            Assert.True(health.Open.Count > 0, string.Join(" | ", h.Driver.Ran.Where(r => r.Module == "monitors").Select(r => r.Kind + ":" + r.State + ":" + r.Result)));
            Assert.Equal(new[] { "backfill_stalled", "cache_floor", "jobs_dead", "llm_errors", "source_down", "source_stalled" }, health.Open.Select(i => i.Kind).OrderBy(k => k, StringComparer.Ordinal));
            var stalled = health.Open.Single(i => i.Kind == "source_stalled");
            Assert.Equal(("test", 30.0), (stalled.Subject, stalled.Detail.GetProperty("usualPerDay").GetDouble()));
            Assert.True(stalled.Detail.GetProperty("silentHours").GetDouble() > 36);
            Assert.Equal(("wa", "error"), (health.Open.Single(i => i.Kind == "source_down").Subject, health.Open.Single(i => i.Kind == "source_down").Detail.GetProperty("status").GetString()));
            Assert.Equal(1, health.Open.Single(i => i.Kind == "backfill_stalled").Detail.GetProperty("conversations").GetInt32());
            Assert.Equal((10, 4), (health.Open.Single(i => i.Kind == "llm_errors").Detail.GetProperty("calls").GetInt32(), health.Open.Single(i => i.Kind == "llm_errors").Detail.GetProperty("failed").GetInt32()));
            Assert.True(health.Open.Single(i => i.Kind == "cache_floor").Detail.GetProperty("efficiency").GetDouble() < 0.5);
            Assert.Equal("HttpRequestException: 503", health.Open.Single(i => i.Kind == "jobs_dead").Detail.GetProperty("lastError").GetString());
            Assert.Equal(6, Notices(h, "data_health").Count);
            Assert.All(Notices(h, "data_health"), n => Assert.Null(n.PersonId));

            // Checked again: still open, not told again.
            await RunAsync(h, minutes: 11);
            Assert.Equal(6, Notices(h, "data_health").Count);

            // The phone source is back and the server source brings messages again: both close.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE sources SET status='active' WHERE source_id='wa'"));
            await h.PostAsync(General, Ana, "back again", source: SourceKinds.Backfill);
            await RunAsync(h, minutes: 11);
            health = h.Monitors.Health();
            Assert.Equal(new[] { "source_down", "source_stalled" }, health.Resolved.Select(i => i.Kind).OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(4, health.Open.Count);

            // A problem that comes back is told again.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE sources SET status='error' WHERE source_id='wa'"));
            await RunAsync(h, minutes: 11);
            Assert.Equal(7, Notices(h, "data_health").Count);
            Assert.Single(h.Monitors.Health().Open, i => i.Kind == "source_down");

            // Switched off with X51: no checking at all.
            var off = await TacticAsync(h, DataHealthTactic.Key, Array.Empty<Selection>(), new { enabled = false });
            Assert.Equal(("data_health", false), (off[0].Data!.Value.GetProperty("rule").GetString(), off[0].Data!.Value.GetProperty("enabled").GetBoolean()));
            Assert.Equal(5, off[1].Data!.Value.GetProperty("extra").GetProperty("open").GetArrayLength());
            await RunAsync(h, minutes: 11);
            Assert.Equal("switched off", h.Driver.LastResult(MonitorsModule.HealthKind)!.Value.GetProperty("skipped").GetString());
        }
    }
}
