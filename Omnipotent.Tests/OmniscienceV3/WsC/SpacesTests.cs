using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Modules.Insights;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>Community pages of servers and group chats (N13). Synthetic data only.</summary>
    public class SpacesTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd;
        private const string Dan = "discord:204", Eve = "discord:205", Fay = "discord:206", Gus = "discord:207", Kid = "discord:208", Old = "discord:209";
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, Group = SignalsHarness.Group, DmAna = SignalsHarness.DmAna;
        private const string Server = "discord:g1";
        private const long Day = 86_400_000L;

        private sealed class World
        {
            public required SignalsHarness H { get; init; }
            /// <summary>Message id → the ensemble's reading, written as the classifier stores it.</summary>
            public Dictionary<string, string> Readings { get; } = new(StringComparer.Ordinal);
            public int Minute;

            /// <summary><paramref name="count"/> messages on the day <paramref name="daysAgo"/> days ago, at midday, a minute apart.</summary>
            public async Task<List<string>> PostAsync(string conversation, string author, int daysAgo, int count, string? reading = null, string? replyTo = null, string? mention = null)
            {
                var ids = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    long at = H.Clock.NowMs - daysAgo * Day + (Minute++ % 300) * 60_000L - 2 * 3_600_000L;
                    string id = await H.PostAsync(conversation, author, $"message {Minute}", sentAt: at, arrivedAt: at, replyTo: replyTo, mention: mention);
                    if (reading != null) Readings[id] = reading;
                    ids.Add(id);
                }
                return ids;
            }

            /// <summary>Makes the readings the active label set's, and announces them as the classifier's outbox would.</summary>
            public Task WriteReadingsAsync() => H.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM classify_labelsets WHERE labelset=9") == 0)
                    c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(9,'active','test',$h,$f,$n)",
                        ("$h", """{"single":{},"context":{}}"""), ("$f", """{"tasks":{},"llmReliability":{}}"""), ("$n", H.Clock.NowMs));
                foreach (var (id, codes) in Readings)
                {
                    c.Exec(tx, "INSERT OR REPLACE INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,9,1,$m,$c,NULL,$n,1)",
                        ("$m", id), ("$c", codes), ("$n", H.Clock.NowMs));
                    c.Exec(tx, "INSERT INTO classify_outbox(message_id, labelset, at_ms) VALUES($m,9,$n)", ("$m", id), ("$n", H.Clock.NowMs));
                }
                Readings.Clear();
            });
        }

        private static string DayText(SignalsHarness h, int daysAgo) =>
            DateOnly.FromDateTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(h.Clock.NowMs - daysAgo * Day)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static async Task RunAsync(SignalsHarness h)
        {
            await h.Driver.RunUntilIdleAsync(5_000);
            h.Driver.AssertNoFailures();
        }

        private static async Task RebuildAsync(SignalsHarness h)
        {
            await h.Insights!.RefreshAsync(CancellationToken.None);
            await RunAsync(h);
        }

        private static long Corpus(SignalsHarness h, string sql, params (string, object?)[] args)
        {
            using var c = h.Runtime.Stores.Corpus.OpenRead();
            return c.Scalar<long>(null, sql, args);
        }

        /// <summary>A server with regulars, a surge day, a tense day, two newcomers, a second circle, a minor, and talk about Klives.</summary>
        private static async Task<World> SeedAsync(SignalsHarness h)
        {
            var w = new World { H = h };
            await h.AddPersonAsync(Dan, "p-dan", "Dan");
            await h.AddPersonAsync(Eve, "p-eve", "Eve");
            await h.AddPersonAsync(Fay, "p-fay", "Fay");
            await h.AddPersonAsync(Gus, "p-gus", "Gus");
            await h.AddPersonAsync(Kid, "p-kid", "Kid");
            await h.AddPersonAsync(Old, "p-old", "Old");
            await h.MarkMinorAsync("p-kid");
            const string Calm = "sentiment=positive:90|conflict=none:90|about_klives=none:90";

            // The window before this one: a little, by two people; one message about Klives.
            await w.PostAsync(General, Ana, 120, 10);
            await w.PostAsync(General, Old, 130, 5);
            await w.PostAsync(General, Ben, 100, 1, "about_klives=mentioned:90|sentiment=negative:90|conflict=none:90");

            // Regulars: every fifth day, Ana three messages and Ben two; Ben answers her, and they talk about Klives now and then.
            string[] about =
            {
                "about_klives=mentioned:90|sentiment=negative:90", "about_klives=mentioned:90|sentiment=negative:90", "about_klives=mentioned:90|sentiment=positive:90",
                "about_klives=addressed:90|sentiment=neutral:90", "about_klives=addressed:90|sentiment=neutral:90", "about_klives=indirect:90|sentiment=negative:90",
            };
            int said = 0;
            for (int d = 85; d >= 25; d -= 5)
            {
                var hers = await w.PostAsync(General, Ana, d, 3, Calm);
                var his = await w.PostAsync(General, Ben, d, 2, Calm, replyTo: hers[0]);
                // Six of these messages are to or about Klives: four of hers, two of his.
                if (said < 4) w.Readings[hers[2]] = about[said++];
                else if (said < 6) w.Readings[his[1]] = about[said++];
            }
            // One day far busier than any other.
            await w.PostAsync(General, Ana, 40, 25);
            await w.PostAsync(General, Ben, 40, 20);
            // One tense day.
            var tenseA = await w.PostAsync(General, Ana, 10, 6, "sentiment=negative:90|conflict=tension:90|about_klives=none:90");
            await w.PostAsync(General, Ben, 10, 6, "sentiment=negative:90|conflict=conflict:90|about_klives=none:90", replyTo: tenseA[0]);
            // Cyd was barely there and now writes every day; she answers both regulars.
            await w.PostAsync(General, Cyd, 60, 2);
            for (int d = 9; d >= 5; d--)
            {
                await w.PostAsync(General, Cyd, d, 2, replyTo: tenseA[0]);
                await w.PostAsync(General, Cyd, d, 2, mention: Ben);
            }
            // Dan is new.
            for (int d = 4; d >= 1; d--) await w.PostAsync(General, Dan, d, 4);
            // A second circle in another channel: Eve, Fay and Gus answer each other. Eve answers Ana once.
            for (int d = 15; d >= 13; d--)
            {
                var e = await w.PostAsync(Offtopic, Eve, d, 2);
                var f = await w.PostAsync(Offtopic, Fay, d, 2, replyTo: e[0]);
                var g = await w.PostAsync(Offtopic, Gus, d, 2, replyTo: f[0]);
                await w.PostAsync(Offtopic, Eve, d, 1, replyTo: g[0]);
            }
            await w.PostAsync(General, Eve, 12, 1, replyTo: tenseA[1]);
            // A minor who writes a lot, and Klives himself.
            for (int d = 50; d >= 41; d--) await w.PostAsync(General, Kid, d, 4);
            for (int d = 34; d >= 30; d--) await w.PostAsync(General, Self, d, 2);

            // Elsewhere: a group chat, another server with almost nothing, and a direct chat.
            await w.PostAsync(Group, Ana, 3, 30);
            await w.PostAsync(Group, Ben, 3, 30);
            await w.PostAsync(Other, Ben, 2, 5);
            await w.PostAsync(DmAna, Ana, 2, 60);

            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                long start = h.Clock.NowMs - 400 * Day;
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES('discord:c9','discord','guild_channel',$g,'staff',$t)", ("$g", Server), ("$t", start));
                c.Exec(tx, "UPDATE conversations SET last_message_ms=$t WHERE conversation_id IN ($a,$b)", ("$t", h.Clock.NowMs), ("$a", General), ("$b", Offtopic));
                c.Exec(tx, "INSERT INTO ingest_cursors(source_id, conversation_id, backward_complete, state, updated_at_ms) VALUES('test',$c,1,'done',$t)", ("$c", General), ("$t", start));
                for (int i = 0; i < 6; i++)
                    c.Exec(tx, "INSERT INTO member_events(guild_id, identity_id, kind, at_ms, source_id, dedupe_key) VALUES($g,$i,'join',$t,'test',$d)",
                        ("$g", Server), ("$i", "discord:30" + i), ("$t", h.Clock.NowMs - 7 * Day), ("$d", "join" + i));
            });
            await RunAsync(h);
            await w.WriteReadingsAsync();
            // The rollups pick the readings up on their next scheduled pass.
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await RunAsync(h);
            return w;
        }

        [Fact]
        public async Task APage_ShowsActivityMembersToneEventsFactions_AndWhatIsSaidAboutKlives()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true);
            await h.Settings.SetAsync("insights.space.min_messages", 50, "test");
            await SeedAsync(h);
            long fromMs = h.Clock.NowMs - 89 * Day - 12 * 3_600_000L;
            long expected = Corpus(h, "SELECT COUNT(*) FROM messages WHERE guild_id=$g AND sent_at_ms >= $f", ("$g", Server), ("$f", fromMs));

            await RebuildAsync(h);

            // The busy server and the group chat have a page; the quiet server and the direct chat do not.
            var list = h.Insights!.Spaces();
            Assert.Equal(new[] { ("guild", Server), ("conversation", Group) }, list.Spaces.Select(s => (s.ScopeKind, s.ScopeId)));
            Assert.Equal((90, "Server 1", expected, 6), (list.WindowDays, list.Spaces[0].Title, list.Spaces[0].Messages, list.Spaces[0].AboutKlives));
            Assert.Null(h.Insights.Space("guild", "discord:g2"));
            Assert.Null(h.Insights.Space("conversation", DmAna));

            var page = h.Insights.Space("guild", Server)!;
            Assert.Equal(("guild", Server, "Server 1", "discord", 90, DayText(h, 89), DayText(h, 0)), (page.ScopeKind, page.ScopeId, page.Title, page.Platform, page.WindowDays, page.FromDay, page.ToDay));
            Assert.Equal((expected, 16L, 9, 3), (page.Messages, page.PreviousMessages, page.People, page.PreviousPeople));
            Assert.Equal(90, page.Days.Count);
            Assert.Equal(expected, page.Days.Sum(d => (long)d.Messages));
            Assert.Equal((50, 2), (page.Days.Single(d => d.Day == DayText(h, 40)).Messages, page.Days.Single(d => d.Day == DayText(h, 40)).People));

            // Members: the regulars lead; the minor is counted but never named.
            Assert.Equal(1, page.PeopleLeftOut);
            Assert.DoesNotContain(page.Core.Concat<object>(page.Rising).Select(m => m is SpaceMemberDto c ? c.PersonId : ((SpaceRisingDto)m).PersonId), p => p == "p-kid");
            Assert.Equal(new[] { "p-ana", "p-ben", "p-cyd" }, page.Core.Take(3).Select(m => m.PersonId));
            var ana = page.Core[0];
            Assert.Equal(("Ana", 70L, Math.Round(70.0 / expected, 4), 14), (ana.Name, ana.Messages, ana.Share, ana.ActiveDays));
            // She also wrote 30 messages in a group chat and 60 to Klives directly: under half of what she wrote was here.
            Assert.Equal(Math.Round(70.0 / 160, 4), ana.Devotion);
            Assert.Equal(1.0, page.Core.Single(m => m.PersonId == "p-cyd").Devotion);
            Assert.Contains(page.Core, m => m.PersonId == "p-klives");

            // Rising: Cyd came back and Dan is new. The regulars are not "rising" because of one busy day long ago.
            Assert.Equal(new[] { "p-cyd", "p-dan" }, page.Rising.Select(m => m.PersonId));
            Assert.Equal((20L, 2L, false, false, DayText(h, 60)), (page.Rising[0].Recent, page.Rising[0].Earlier, page.Rising[0].IsNew, page.Rising[0].Everywhere, page.Rising[0].FirstDay));
            Assert.Equal((16L, 0L, true, DayText(h, 4)), (page.Rising[1].Recent, page.Rising[1].Earlier, page.Rising[1].IsNew, page.Rising[1].FirstDay));

            // Tone: shares of what the ensemble answered; the tense share against the window before.
            Assert.Equal(77, page.Tone.RecentAnswered);
            Assert.Equal(Math.Round(15 / 77.0, 4), page.Tone.Recent["negative"]);
            Assert.Equal(Math.Round(12 / 71.0, 4), page.Tone.ConflictRecent);
            Assert.Equal((1, 0.0), (page.Tone.BaselineAnswered, page.Tone.ConflictBaseline));

            // What is said to or about Klives, and how exactly those messages read (not the mood of the server).
            var about = page.AboutKlives;
            Assert.Equal((6, 1, 6), (about.Messages, about.PreviousMessages, about.SentimentAnswered));
            Assert.Equal(new Dictionary<string, int> { ["addressed"] = 2, ["indirect"] = 1, ["mentioned"] = 3 }, about.Kinds);
            Assert.Equal((0.5, 0.3333, 0.1667), (about.Sentiment["negative"], about.Sentiment["neutral"], about.Sentiment["positive"]));
            Assert.Equal(new[] { ("p-ana", "Ana", 4), ("p-ben", "Ben", 2) }, about.People.Select(p => (p.PersonId, p.Name!, p.Messages)));

            // Notable days, newest first: the wave of new members, the tense day, the busy day.
            Assert.Equal(new[] { ("joined", DayText(h, 7)), ("conflict", DayText(h, 10)), ("surge", DayText(h, 40)) }, page.Events.Select(e => (e.Kind, e.Day)));
            Assert.Equal((50.0, "50 messages, about 50 times a usual day (0)."), (page.Events[2].Value, page.Events[2].Text));
            Assert.Equal((12.0, "12 messages read as tense or hostile, 100% of what was read that day."), (page.Events[1].Value, page.Events[1].Text));
            Assert.Equal("6 people joined.", page.Events[0].Text);

            // Factions: who answers and mentions whom. A single reply across is not a tie.
            Assert.Equal(2, page.Factions.Count);
            Assert.Equal(new[] { "p-ana,p-ben,p-cyd", "p-eve,p-fay,p-gus" },
                page.Factions.Select(f => string.Join(",", f.Members.Select(m => m.PersonId).OrderBy(p => p, StringComparer.Ordinal))).OrderBy(s => s, StringComparer.Ordinal));
            Assert.All(page.Factions, f => Assert.Equal(3, f.Size));
            Assert.True(page.FactionModularity > 0.3);

            // Channels and how much of the server is seen.
            Assert.Equal(new[] { General, Offtopic }, page.Channels.Select(c => c.ConversationId));
            Assert.Equal(("general", expected - 21, 7), (page.Channels[0].Title, page.Channels[0].Messages, page.Channels[0].People));
            Assert.Equal((3, 2, 2, 1), (page.Coverage.ChannelsKnown, page.Coverage.ChannelsWithMessages, page.Coverage.ChannelsActive, page.Coverage.ChannelsFullyBackfilled));
            Assert.StartsWith("Omniscience has messages from 2 of the 3 channels it knows of in this server; 1 of them are read back", page.Coverage.Note);
            Assert.Null(page.LatestDigestId);

            // The group chat: its own page, no channels, no factions between two people.
            var group = h.Insights.Space("conversation", Group)!;
            Assert.Equal(("The group", 60L, 2, 1), (group.Title, group.Messages, group.People, group.Coverage.ChannelsKnown));
            Assert.Empty(group.Channels);
            Assert.Empty(group.Factions);
            Assert.Null(group.FactionModularity);
        }

        [Fact]
        public async Task Pages_KeepTheirFactionIds_FollowTheRulesOfNow_AndForgetAPerson()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true, datacontrols: true);
            await h.Settings.SetAsync("insights.space.min_messages", 50, "test");
            var w = await SeedAsync(h);
            await RebuildAsync(h);
            var first = h.Insights!.Space("guild", Server)!;
            string IdOf(SpacePageDto page, string person) => page.Factions.Single(f => f.Members.Any(m => m.PersonId == person)).FactionId;
            string regulars = IdOf(first, "p-ana"), circle = IdOf(first, "p-eve");
            Assert.NotEqual(regulars, circle);

            // The next night, with a little more written: the same factions keep their ids.
            h.Clock.Advance(TimeSpan.FromDays(1));
            var more = await w.PostAsync(General, Ana, 0, 2);
            await w.PostAsync(General, Dan, 0, 3, replyTo: more[0]);
            await RunAsync(h);
            await RebuildAsync(h);
            var second = h.Insights.Space("guild", Server)!;
            Assert.Equal((regulars, circle), (IdOf(second, "p-ana"), IdOf(second, "p-eve")));
            Assert.Equal(regulars, IdOf(second, "p-dan"));
            Assert.True(second.AsOfMs > first.AsOfMs);

            // Ben turns out to be under 18: he is taken out of what is served at once, before any rebuild.
            await h.MarkMinorAsync("p-ben");
            var served = h.Insights.Space("guild", Server)!;
            Assert.DoesNotContain(served.Core, m => m.PersonId == "p-ben");
            Assert.DoesNotContain(served.AboutKlives.People, m => m.PersonId == "p-ben");
            Assert.DoesNotContain(served.Factions.SelectMany(f => f.Members), m => m.PersonId == "p-ben");
            Assert.Contains(served.Core, m => m.PersonId == "p-ana");

            // Eve is forgotten: the page is rebuilt without her, and what is left of her circle is still the same circle.
            var data = h.Data!;
            await data.StartForgetAsync("p-eve", data.PreviewForget("p-eve").ConfirmToken, "tester", default);
            await RunAsync(h);
            // The classifier and the rollups follow on their next scheduled pass.
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await RunAsync(h);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM ins_spaces WHERE body_json LIKE '%p-eve%' OR factions_json LIKE '%p-eve%'"));
            // The night after, the counts no longer include what she wrote.
            await RebuildAsync(h);
            var after = h.Insights.Space("guild", Server)!;
            Assert.Equal((8, second.Messages - 10), (after.People, after.Messages));
            Assert.DoesNotContain(after.Core, m => m.PersonId == "p-eve");
            Assert.Equal(new[] { "p-fay", "p-gus" }, after.Factions.Single(f => f.FactionId == circle).Members.Select(m => m.PersonId).OrderBy(p => p, StringComparer.Ordinal));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM ins_spaces WHERE body_json LIKE '%p-eve%' OR factions_json LIKE '%p-eve%'"));

            // A page for a place too small for the nightly run can be asked for; a direct chat or an unknown place cannot.
            await h.Insights.RequestSpaceAsync("guild", "discord:g2", default);
            await RunAsync(h);
            Assert.Equal((5L, 1), (h.Insights.Space("guild", "discord:g2")!.Messages, h.Insights.Space("guild", "discord:g2")!.People));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Insights.RequestSpaceAsync("conversation", DmAna, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Insights.RequestSpaceAsync("guild", "discord:nowhere", default));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Insights.RequestSpaceAsync("person", "p-ana", default));
            // The next nightly run drops the page of the place that is too small again.
            await RebuildAsync(h);
            Assert.Null(h.Insights.Space("guild", "discord:g2"));
        }
    }
}
