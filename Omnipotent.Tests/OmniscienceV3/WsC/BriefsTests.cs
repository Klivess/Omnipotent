using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Briefs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>Digests (N10), person briefs (N4) and the summarising tactics (X18, X30, X31, X52).</summary>
    public class BriefsTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd, Bot = SignalsHarness.Bot;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, Third = SignalsHarness.Third;
        private const string DmAna = SignalsHarness.DmAna, DmBen = SignalsHarness.DmBen;
        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), Hour = TimeSpan.FromHours(1);

        private static async Task<DigestDto> RunDigestAsync(SignalsHarness h, string scopeKind, string? scopeId = null, long? from = null, long? to = null)
        {
            var queued = await h.Briefs!.RequestDigestAsync(scopeKind, scopeId, from, to, "tester", "on_demand", null, interactive: true, CancellationToken.None);
            Assert.Equal("queued", queued.State);
            await h.Driver.RunUntilIdleAsync();
            h.Driver.AssertNoFailures();
            return h.Briefs.Digest(queued.DigestId)!;
        }

        private static IEnumerable<CitedPoint> AllPoints(DigestDto d) =>
            d.Overall.Concat(d.Sections.SelectMany(s => s.Points.Concat(s.Children.SelectMany(c => c.Points))));

        [Fact]
        public async Task Digest_SummarisesEachConversationThenEachServerThenTheWhole_AndKeepsOnlyVerifiedCitations()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            long start = h.Clock.NowMs;
            var ids = new List<string>
            {
                await h.PostAsync(General, Ana, "pizza on friday?"),
                await h.PostAsync(General, Ben, "pizza sounds good"),
                await h.PostAsync(General, Self, "deploy is done"),
                await h.PostAsync(Offtopic, Cyd, "football was great !!!"),
                await h.PostAsync(Offtopic, Ana, "football again tomorrow FABRICATE"),
                await h.PostAsync(Other, Ben, "release notes are up"),
                await h.PostAsync(DmAna, Ana, "book returned, thanks"),
            };
            string botMessage = await h.PostAsync(General, Bot, "pizza bot says hi");
            string deleted = await h.PostAsync(General, Cyd, "pizza never mind");
            await h.DeleteAsync(deleted);
            string blank = await h.PostAsync(General, Cyd, "   ");
            h.Clock.Advance(Hour);

            var digest = await RunDigestAsync(h, "all", null, start - 1, h.Clock.NowMs);
            Assert.Equal(("done", false, 7, 7, 4, 4), (digest.State, digest.NothingNew, digest.Messages, digest.MessagesRead, digest.Conversations, digest.People));
            // One call over the conversations, one over their points: channel → server → overall.
            Assert.Equal(2, digest.LlmCalls);
            Assert.Equal(new[] { "briefs.digest", "briefs.digest.merge" }, h.Llm.Requests.Select(r => r.Purpose));
            Assert.Equal(AIRouterWorkClass.Interactive, h.Llm.Requests[0].WorkClass);

            // Sections: the server with two active channels (its own points and both channels), the server with one, the chat.
            Assert.Equal(new[] { "guild:discord:g1", "guild:discord:g2", "conversation:" + DmAna }, digest.Sections.Select(s => s.Kind + ":" + s.Id));
            var g1 = digest.Sections[0];
            Assert.Equal(("Server 1", 5, 4), (g1.Title, g1.Messages, g1.People));
            Assert.Equal(new[] { General, Offtopic }, g1.Children.Select(c => c.Id));
            Assert.NotEmpty(g1.Points);
            Assert.Equal(new[] { "2 about pizza", "1 about deploy" }, g1.Children[0].Points.Select(p => p.Text));
            Assert.Equal(new[] { ids[0], ids[1] }, g1.Children[0].Points[0].Refs);
            // A server with a single active channel has that channel's points.
            Assert.Equal(digest.Sections[1].Children[0].Points.Select(p => p.Text + "|" + string.Join(",", p.Refs)), digest.Sections[1].Points.Select(p => p.Text + "|" + string.Join(",", p.Refs)));
            Assert.Equal("1 about release", digest.Sections[1].Points[0].Text);
            Assert.Equal(new[] { ids[5] }, Assert.Single(digest.Sections[1].Points).Refs);
            Assert.NotEmpty(digest.Overall);

            // Every citation at every level is a message of the period that was shown; nothing fabricated survived.
            var shown = ids.ToHashSet();
            Assert.All(AllPoints(digest), p => Assert.NotEmpty(p.Refs));
            Assert.All(AllPoints(digest).SelectMany(p => p.Refs), r => Assert.Contains(r, shown));
            Assert.DoesNotContain(AllPoints(digest), p => p.Text.Contains("fabricated"));
            Assert.DoesNotContain(AllPoints(digest).SelectMany(p => p.Refs), r => r == botMessage || r == deleted || r == blank);
            Assert.Equal(new[] { ids[3] }, digest.Notable);
            // The model saw Klives by name, a T3 stamp on every message, and neither the bot nor the deleted message.
            string prompt = h.Llm.Requests[0].Turns[^1].Content;
            Assert.Contains("by=\"Klives\"", prompt);
            Assert.Contains("author zone unknown", prompt);
            Assert.DoesNotContain("pizza bot", prompt);
            Assert.DoesNotContain("never mind", prompt);
            Assert.Contains("Never write \"today\", \"yesterday\"", h.Llm.Requests[0].SystemPrompt);

            // It is served as the scope's read model, and listed.
            var model = h.Runtime.Host.ReadModels.Get(BriefsModule.DigestModel, "all")!;
            Assert.Equal(digest.DigestId, JsonDocument.Parse(model.BodyJson).RootElement.GetProperty("digestId").GetString());
            Assert.Equal(digest.DigestId, Assert.Single(h.Briefs!.Digests(null, null, null, 10).Items).DigestId);
        }

        [Fact]
        public async Task Digest_OfAnEmptyPeriod_SaysNothingNew_WithoutAskingTheModel()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            await h.PostAsync(General, Ana, "from last week", sentAt: h.Clock.NowMs - 8 * 86_400_000L);
            var digest = await RunDigestAsync(h, "guild", "discord:g1");
            Assert.Equal(("done", true, 0, 0), (digest.State, digest.NothingNew, digest.Messages, digest.LlmCalls));
            Assert.Empty(digest.Overall);
            Assert.Empty(h.Llm.Requests);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Briefs!.RequestDigestAsync("guild", "discord:nope", null, null, "tester", "on_demand", null, true, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Briefs!.RequestDigestAsync("planet", null, null, null, "tester", "on_demand", null, true, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Briefs!.RequestDigestAsync("all", null, h.Clock.NowMs, h.Clock.NowMs - 1, "tester", "on_demand", null, true, CancellationToken.None));
        }

        [Fact]
        public async Task Digest_ThatCannotBeWritten_SaysSoInsteadOfStayingRunning()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            await h.PostAsync(General, Ana, "pizza on friday?");
            h.Clock.Advance(Minute);
            h.Llm.Override = _ => new LlmResult(false, null, null, LlmErrorKind.Other, "provider down", null, "fake-model", "fake", 0, 0, 0, 0, 5);
            var queued = await h.Briefs!.RequestDigestAsync("all", null, null, null, "tester", "on_demand", null, interactive: true, CancellationToken.None);
            // Asking again while it is being written returns the same digest, not a second one.
            Assert.Equal(queued.DigestId, (await h.Briefs.RequestDigestAsync("all", null, null, null, "tester", "on_demand", null, true, CancellationToken.None)).DigestId);
            await h.Driver.RunUntilIdleAsync();
            Assert.Equal("running", h.Briefs.Digest(queued.DigestId)!.State);          // first attempt failed; it will be retried
            for (int attempt = 0; attempt < 3; attempt++)
            {
                h.Clock.Advance(TimeSpan.FromMinutes(20));
                await h.Driver.RunUntilIdleAsync();
            }
            var failed = h.Briefs.Digest(queued.DigestId)!;
            Assert.Equal("failed", failed.State);
            Assert.Contains("LLM call failed", failed.Error);
            Assert.NotNull(failed.FinishedAtMs);
            Assert.Equal(3, h.Llm.Requests.Count(r => r.Purpose == "briefs.digest"));
            // The provider is back: a new request is written normally.
            h.Llm.Override = null;
            var again = await h.Briefs.RequestDigestAsync("all", null, queued.FromMs, null, "tester", "on_demand", null, true, CancellationToken.None);
            Assert.NotEqual(queued.DigestId, again.DigestId);
            await h.Driver.RunUntilIdleAsync();
            Assert.Equal("done", h.Briefs.Digest(again.DigestId)!.State);
        }

        [Fact]
        public async Task Digest_StartsWhereKlivesLastLooked_AndIsCappedInLengthAndSize()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            long now = h.Clock.NowMs;
            for (int i = 0; i < 10; i++) await h.PostAsync(General, Ana, "update " + i, sentAt: now - (50 - i * 5) * 3_600_000L);    // 50 h ago … 5 h ago
            // Never looked: the last 24 hours (updates 6–9 at 20, 15, 10 and 5 hours ago).
            var first = await RunDigestAsync(h, "guild", "discord:g1");
            Assert.Equal((now - 24 * 3_600_000L, now, 4), (first.FromMs, first.ToMs, first.Messages));

            // He looked 12 hours ago: the next digest starts there (updates 8 and 9).
            await h.Briefs!.MarkSeenAsync("guild", "discord:g1", now - 12 * 3_600_000L, "tester", CancellationToken.None);
            var second = await RunDigestAsync(h, "guild", "discord:g1");
            Assert.Equal((now - 12 * 3_600_000L, 2), (second.FromMs, second.Messages));
            // Looking never moves backwards.
            await h.Briefs.MarkSeenAsync("guild", "discord:g1", now - 40 * 3_600_000L, "tester", CancellationToken.None);
            Assert.Equal(now - 12 * 3_600_000L, (await RunDigestAsync(h, "guild", "discord:g1")).FromMs);

            // An explicit start further back than the longest period is pulled in.
            await h.Settings.SetAsync("briefs.digest.max_days", 1, "test");
            var capped = await RunDigestAsync(h, "guild", "discord:g1", now - 90 * 86_400_000L, now);
            Assert.Equal(now - 86_400_000L, capped.FromMs);

            // More messages than a digest may read: the newest are read and the digest says how many there were.
            await h.Settings.SetAsync("briefs.digest.max_days", 30, "test");
            await h.Settings.SetAsync("briefs.digest.max_messages", 100, "test");
            for (int i = 0; i < 130; i++) await h.PostAsync(Offtopic, Ben, "busy " + i, sentAt: now - 3_600_000L + i * 1000);
            var large = await RunDigestAsync(h, "conversation", Offtopic, now - 2 * 3_600_000L, now);
            Assert.Equal((130, 100), (large.Messages, large.MessagesRead));
            string prompt = h.Llm.Requests.Last(r => r.Purpose == "briefs.digest").Turns[^1].Content;
            Assert.Contains("busy 129", prompt);
            Assert.DoesNotContain("busy 29<", prompt);
        }

        [Fact]
        public async Task Summariser_SpreadsALargeInputOverSeveralCalls_AndWritesTheOverallOverTheParts()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            h.Llm.ContextWindow = 6_000;
            var summarizer = new CitedSummarizer(h.Runtime.Llm, BriefsModule.ModuleId);
            var focus = new SummaryFocus("briefs.test", "Summarise.");
            SummaryGroup Group(string key, int n) => new(key, "Group " + key, Enumerable.Range(0, n)
                .Select(i => new SummaryItem(key + ":" + i, CitedSummarizer.MessageLine("Ana", h.Clock.NowMs, null, (i % 2 == 0 ? "alpha " : "beta ") + new string('x', 180) + " " + i))).ToList());
            var groups = new[] { Group("a", 30), Group("b", 30), Group("c", 30) };

            var summary = await summarizer.RunAsync(focus, "omniscience-test", groups, overall: true, AIRouterWorkClass.Interactive, null, CancellationToken.None);
            int mapCalls = h.Llm.Requests.Count(r => r.Purpose == "briefs.test");
            Assert.True(mapCalls > 1, "the input should not have fitted one call");
            Assert.Equal(1, h.Llm.Requests.Count(r => r.Purpose == "briefs.test.merge"));
            Assert.Equal(mapCalls + 1, summary.Calls);
            Assert.Equal((90, 90, false), (summary.ItemsShown, summary.ItemsTotal, summary.Truncated));
            // Every item was shown exactly once across the calls, and every group has its points.
            Assert.Equal(new[] { "a", "b", "c" }, summary.Groups.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(90, h.Llm.Requests.Where(r => r.Purpose == "briefs.test").Sum(r => System.Text.RegularExpressions.Regex.Matches(r.Turns[^1].Content, "<m ref=").Count));
            Assert.All(summary.Groups.Values, points => Assert.NotEmpty(points));
            // The overall points were written over the parts' points and still cite items.
            Assert.NotEmpty(summary.Overall);
            Assert.All(summary.Overall.SelectMany(p => p.Refs), r => Assert.Matches("^[abc]:\\d+$", r));
            // No call was larger than the window allows.
            Assert.All(h.Llm.Requests, r => Assert.True(h.Runtime.Llm.Estimator.Estimate(r.SystemPrompt) + h.Runtime.Llm.Estimator.Estimate(r.Turns[^1].Content) < 6_000));

            // Far more than the allowed calls can hold: each group keeps its newest items, and the result says so.
            h.Llm.Requests.Clear();
            var huge = await summarizer.RunAsync(focus, "omniscience-test", new[] { Group("a", 400), Group("b", 40) }, overall: false, AIRouterWorkClass.Interactive, null, CancellationToken.None);
            Assert.True(huge.Truncated);
            Assert.True(huge.ItemsShown < 440 && huge.ItemsShown > 0);
            Assert.True(h.Llm.Requests.Count <= CitedSummarizer.MaxCallsPerRun);
            Assert.Contains(huge.Groups["a"].SelectMany(p => p.Refs), r => r == "a:399");
            Assert.DoesNotContain(huge.Groups["a"].SelectMany(p => p.Refs), r => r == "a:0");
            Assert.NotEmpty(huge.Groups["b"]);

            // Nothing to summarise: no call.
            h.Llm.Requests.Clear();
            var none = await summarizer.RunAsync(focus, "omniscience-test", new[] { new SummaryGroup("e", "Empty", Array.Empty<SummaryItem>()) }, true, AIRouterWorkClass.Interactive, null, CancellationToken.None);
            Assert.Equal((0, 0), (none.Calls, none.ItemsTotal));
            Assert.Empty(h.Llm.Requests);
        }

        [Fact]
        public async Task Digest_AboutOnePerson_IsRefusedForMinors_AndSubscriptionsRunOncePerOccurrence()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            await h.PostAsync(General, Ana, "pizza friday");
            await h.PostAsync(Other, Ana, "release on monday");
            await h.PostAsync(General, Ben, "pizza yes");
            h.Clock.Advance(Minute);
            var person = await RunDigestAsync(h, "person", "p-ana");
            Assert.Equal(("person", "p-ana", "Ana", 2, 2), (person.ScopeKind, person.ScopeId, person.ScopeTitle, person.Messages, person.Conversations));
            Assert.DoesNotContain("pizza yes", h.Llm.Requests[0].Turns[^1].Content);

            await h.MarkMinorAsync("p-ana");
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Briefs!.RequestDigestAsync("person", "p-ana", null, null, "tester", "on_demand", null, true, CancellationToken.None));
            Assert.Equal(BriefsModule.MinorRefusal, refused.Message);
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Briefs!.SaveSubscriptionAsync(null, "Ana daily", "person", "p-ana", "daily", "08:00", null, true, "tester", CancellationToken.None));

            // It is 13:00 in London. A daily subscription at 13:30 and a weekly one on Wednesdays at 09:00 (today is Monday).
            var daily = await h.Briefs!.SaveSubscriptionAsync(null, "Server 1 daily", "guild", "discord:g1", "daily", "13:30", null, true, "tester", CancellationToken.None);
            var weekly = await h.Briefs.SaveSubscriptionAsync(null, "Everything weekly", "all", null, "weekly", "09:00", 3, true, "tester", CancellationToken.None);
            Assert.Equal(("daily", "13:30", (int?)null), (daily.Cadence, daily.At, daily.Weekday));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Briefs.SaveSubscriptionAsync(null, "Bad", "all", null, "weekly", "09:00", null, true, "tester", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Briefs.SaveSubscriptionAsync(null, "Bad", "all", null, "hourly", "09:00", null, true, "tester", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Briefs.SaveSubscriptionAsync(null, "Bad", "all", null, "daily", "9am", null, true, "tester", CancellationToken.None));
            int before = h.Briefs.Digests(null, null, null, 100).Items.Count;

            async Task TickAsync()
            {
                await h.Driver.RunUntilIdleAsync();
                h.Driver.AssertNoFailures();
            }
            // 13:10: nothing is due yet (a new subscription does not run for an occurrence that passed before it existed).
            h.Clock.Advance(10 * Minute);
            await TickAsync();
            Assert.Equal(before, h.Briefs.Digests(null, null, null, 100).Items.Count);
            // 13:40: the daily one ran once.
            h.Clock.Advance(30 * Minute);
            await TickAsync();
            var scheduled = h.Briefs.Digests("guild", "discord:g1", null, 100).Items;
            Assert.Equal(("subscription", "done"), (Assert.Single(scheduled).Trigger, scheduled[0].State));
            h.Clock.Advance(10 * Minute);
            await TickAsync();
            Assert.Single(h.Briefs.Digests("guild", "discord:g1", null, 100).Items);
            // Three days without a tick (the service was down): one late run, not three. By then it is Thursday, so the
            // weekly one (Wednesday 09:00) and the built-in daily catch-up (07:30) ran once each as well.
            h.Clock.Advance(TimeSpan.FromDays(3));
            await TickAsync();
            Assert.Equal(2, h.Briefs.Digests("guild", "discord:g1", null, 100).Items.Count);
            var everything = h.Briefs.Digests("all", null, null, 100).Items;
            Assert.Equal(new[] { "daily", "subscription" }, everything.Select(d => d.Trigger).OrderBy(t => t, StringComparer.Ordinal));
            Assert.NotNull(h.Briefs.Subscriptions().Single(s => s.SubscriptionId == weekly.SubscriptionId).LastRunMs);

            // Deleted subscriptions stop.
            Assert.True(await h.Briefs.DeleteSubscriptionAsync(daily.SubscriptionId, "tester", CancellationToken.None));
            h.Clock.Advance(TimeSpan.FromDays(1));
            await TickAsync();
            Assert.Equal(2, h.Briefs.Digests("guild", "discord:g1", null, 100).Items.Count);
        }

        [Fact]
        public async Task PersonBrief_IsWrittenFromTheirMessages_WithNumbersFactsOpenThreadsAndAStalenessBanner()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true, rollups: true);
            long now = h.Clock.NowMs;
            var old = new List<string>();
            for (int i = 0; i < 6; i++) old.Add(await h.PostAsync(General, Ben, "climbing trip planning " + i, source: SourceKinds.Import, sentAt: now - (20 - i) * 86_400_000L, arrivedAt: now - 3_600_000L));
            string week1 = await h.PostAsync(General, Ben, "moving flat next month", source: SourceKinds.Import, sentAt: now - 3 * 86_400_000L, arrivedAt: now - 3_600_000L);
            string dm1 = await h.PostAsync(DmBen, Self, "can you lend me the drill", source: SourceKinds.Import, sentAt: now - 2 * 86_400_000L, arrivedAt: now - 3_600_000L);
            string dm2 = await h.PostAsync(DmBen, Ben, "sure, are you free on sunday?");
            await h.ReadAsync(dm2, act: "question", actP: 0.9);
            await h.PostAsync(General, Ana, "someone else entirely");
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string attribute, string value, string source) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, status, modality, source_kind, probability,
                        module_id, module_version, source_seq)
                    VALUES($i,$i,'person','p-ben',$a,'attribute','{}',$v,$n,'accepted','literal',$s,0.9,'test',1,1)
                    """, ("$i", id), ("$a", attribute), ("$v", value), ("$n", now), ("$s", source));
                Fact("a1", "city", "Leeds", "self_disclosure");
                Fact("a2", "religion", "inferred from a joke", "inference");      // a sensitive trait that he did not state himself: never shown
            });
            await h.ScanAsync();

            var queued = await h.Briefs!.RequestPersonBriefAsync("p-ben", "tester", CancellationToken.None);
            Assert.Equal("queued", queued.State);
            await h.Driver.RunUntilIdleAsync();
            h.Driver.AssertNoFailures();
            var brief = h.Briefs.PersonBrief("p-ben")!;
            Assert.Equal(("done", "Ben", false, now), (brief.State, brief.Name, brief.Stale, brief.NewestEvidenceMs));
            Assert.Equal(1, brief.LlmCalls);
            Assert.Equal(8, brief.MessagesInWindow);
            // Written sections cite his messages and the ones between the two of them, nobody else's.
            var keys = brief.Sections.Select(s => s.Key).ToList();
            Assert.Equal(new[] { "short", "week", "month", "with_klives" }, keys);
            var mine = old.Append(week1).Append(dm1).Append(dm2).ToHashSet();
            Assert.All(brief.Sections.SelectMany(s => s.Points).SelectMany(p => p.Refs), r => Assert.Contains(r, mine));
            Assert.Contains(brief.Sections.Single(s => s.Key == "week").Points.SelectMany(p => p.Refs), r => r == week1);
            Assert.Contains(brief.Sections.Single(s => s.Key == "month").Points.SelectMany(p => p.Refs), r => r == old[0]);
            Assert.Contains(brief.Sections.Single(s => s.Key == "with_klives").Points.SelectMany(p => p.Refs), r => r == dm1);
            string prompt = h.Llm.Requests.Single(r => r.Purpose == "briefs.person").Turns[^1].Content;
            Assert.DoesNotContain("someone else entirely", prompt);
            // What needs no model: activity and style from the rollups, the fact he stated himself, the question waiting for an answer.
            Assert.Equal(8, brief.Messages30d);
            Assert.True(brief.ActiveDays30d >= 7);
            Assert.NotNull(brief.Style);
            Assert.True(brief.Style!.AverageWords > 2);
            Assert.Equal(("city", "Leeds"), (Assert.Single(brief.Facts).Attribute, brief.Facts[0].Value));
            Assert.Equal((dm2, "question"), (Assert.Single(brief.OpenThreads).MessageId, brief.OpenThreads[0].Kind));
            Assert.NotNull(h.Runtime.Host.ReadModels.Get(BriefsModule.PersonModel, "p-ben"));

            // A digest about him lists what Omniscience concluded in its period (the same filters), and only then: the next one has nothing to repeat.
            h.Clock.Advance(Minute);
            var withFacts = await RunDigestAsync(h, "person", "p-ben", now - 86_400_000L);
            Assert.Equal(("a1", "city", "Leeds", "Ben"), (Assert.Single(withFacts.NewFacts).AssertionId, withFacts.NewFacts[0].Attribute, withFacts.NewFacts[0].Value, withFacts.NewFacts[0].PersonName));
            h.Clock.Advance(Hour);
            Assert.Empty((await RunDigestAsync(h, "person", "p-ben", withFacts.ToMs)).NewFacts);

            // Forty days on, with nothing new from him, the same brief is shown with the staleness banner.
            h.Clock.Advance(TimeSpan.FromDays(40));
            Assert.True(h.Briefs.PersonBrief("p-ben")!.Stale);

            // Someone with no messages gets a brief that says so in numbers, without a model call.
            int calls = h.Llm.Requests.Count(r => r.Purpose.StartsWith("briefs."));
            await h.Briefs.RequestPersonBriefAsync("p-cyd", "tester", CancellationToken.None);
            await h.Driver.RunUntilIdleAsync();
            var empty = h.Briefs.PersonBrief("p-cyd")!;
            Assert.Equal(("done", 0, true), (empty.State, empty.Sections.Count, empty.Stale));
            Assert.Equal(calls, h.Llm.Requests.Count(r => r.Purpose.StartsWith("briefs.")));

            // Minors: no brief is written, and one written earlier is no longer shown.
            await h.MarkMinorAsync("p-ben");
            Assert.Equal(BriefsModule.MinorRefusal, (await Assert.ThrowsAsync<TacticRefusedException>(() => h.Briefs.RequestPersonBriefAsync("p-ben", "tester", CancellationToken.None))).Message);
            Assert.Throws<TacticRefusedException>(() => h.Briefs.PersonBrief("p-ben"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Briefs.RequestPersonBriefAsync("p-nobody", "tester", CancellationToken.None));
        }

        [Fact]
        public async Task Tactics_SummariseASelection_SnapshotTopics_ListActionItems_AndSubscribe()
        {
            await using var h = await SignalsHarness.StartAsync(briefs: true);
            var mine = h.Runtime.Tactics.Catalog().Where(d => d.Code is "X18" or "X30" or "X31" or "X52").OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { SummariseSelectionTactic.Key, TopicSnapshotTactic.Key, ActionItemsTactic.Key, DigestSubscriptionTactic.Key }, mine.Select(d => d.Key));
            foreach (var d in mine)
            {
                using var schema = JsonDocument.Parse(d.ParameterSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, $"{d.Key} names the unknown permission '{d.RequiredPermission}'");
                Assert.False(d.Outbound);
            }

            long t0 = h.Clock.NowMs;
            string a = await h.PostAsync(General, Ana, "pizza on friday?");
            string b = await h.PostAsync(General, Ben, "pizza, I will book a table");
            string c = await h.PostAsync(General, Cyd, "let's meet at seven");
            string d2 = await h.PostAsync(Offtopic, Cyd, "football tonight");
            string e = await h.PostAsync(Other, Ana, "pizza in the other server");
            h.Clock.Advance(Minute);

            async Task<(TacticRunRecord Run, List<TacticOutput> Outputs)> RunAsync(string key, object? parameters, params Selection[] selections)
            {
                string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
                await h.Driver.RunUntilIdleAsync();
                h.Driver.AssertNoFailures();
                var run = h.Runtime.Tactics.Get(runId)!;
                Assert.True(run.State == TacticRunStates.Succeeded, $"{key}: {run.State} {run.Error}");
                return (run, JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!);
            }

            // X18 on chosen messages: only those are read and cited; Klives's wording of what to look for is quoted to the model.
            var (_, summary) = await RunAsync(SummariseSelectionTactic.Key, new { focus = "what was decided" }, Selection.Of(SelectionKind.Message, new[] { a, b, d2 }));
            Assert.Equal(new[] { TacticOutputKind.Report, TacticOutputKind.MessageSet }, summary.Select(o => o.Kind));
            var cited = summary[1].Selection!.Items.Select(i => i.Id).ToList();
            Assert.Equal(new[] { a, b, d2 }.OrderBy(x => x), cited.OrderBy(x => x));
            Assert.Equal(3, summary[0].Data!.Value.GetProperty("messages").GetInt32());
            var request = h.Llm.Requests.Last(r => r.Purpose == "briefs.summarise");
            Assert.Contains("\"what was decided\"", request.SystemPrompt);
            Assert.DoesNotContain("seven", request.Turns[^1].Content);

            // X18 on a conversation and a period.
            var (_, ranged) = await RunAsync(SummariseSelectionTactic.Key, null, Selection.Of(SelectionKind.Conversation, new[] { General }), Selection.Window(t0 - 1, h.Clock.NowMs));
            Assert.Equal(new[] { a, b, c }.OrderBy(x => x), ranged[1].Selection!.Items.Select(i => i.Id).OrderBy(x => x));
            // A period with nothing in it answers without the model.
            int calls = h.Llm.Requests.Count;
            var (_, nothing) = await RunAsync(SummariseSelectionTactic.Key, null, Selection.Of(SelectionKind.Conversation, new[] { General }), Selection.Window(t0 - 10_000, t0 - 5_000));
            Assert.True(nothing[0].Data!.Value.GetProperty("nothing").GetBoolean());
            Assert.Equal(calls, h.Llm.Requests.Count);

            // X30: topics across a server's channels.
            var (_, topics) = await RunAsync(TopicSnapshotTactic.Key, null, Selection.Of(SelectionKind.Guild, new[] { "discord:g1" }));
            var topicTexts = topics[0].Data!.Value.GetProperty("topics").EnumerateArray().Select(t => t.GetProperty("text").GetString()).ToList();
            Assert.Contains("2 about pizza", topicTexts);
            Assert.Contains("1 about football", topicTexts);
            Assert.DoesNotContain(e, topics[1].Selection!.Items.Select(i => i.Id));
            Assert.Equal(4, topics[0].Data!.Value.GetProperty("messages").GetInt32());

            // X31: what is still open, by kind.
            var (_, actions) = await RunAsync(ActionItemsTactic.Key, null, Selection.Of(SelectionKind.Conversation, new[] { General }));
            var items = actions[0].Data!.Value.GetProperty("items").EnumerateArray().Select(i => (Kind: i.GetProperty("kind").GetString(), Ref: i.GetProperty("refs")[0].GetString())).ToList();
            Assert.Equal(new[] { ("question", a), ("promise", b), ("plan", c) }, items);
            Assert.DoesNotContain("Promise:", actions[0].Data!.Value.GetProperty("items")[1].GetProperty("text").GetString());

            // X52: a weekly digest of a server.
            var (_, subscribed) = await RunAsync(DigestSubscriptionTactic.Key, new { cadence = "weekly", at = "18:00", weekday = 5 }, Selection.Of(SelectionKind.Guild, new[] { "discord:g1" }));
            Assert.Equal(TacticOutputKind.AlertRule, Assert.Single(subscribed).Kind);
            var subscription = Assert.Single(h.Briefs!.Subscriptions());
            Assert.Equal(("guild", "discord:g1", "weekly", "18:00", 5), (subscription.ScopeKind, subscription.ScopeId, subscription.Cadence, subscription.At, subscription.Weekday));

            // Refusals: a time range alone for topics, a malformed range, a person who may be a minor.
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(TopicSnapshotTactic.Key, new TacticInput(new[] { Selection.Window(t0, t0 + 1) }), "tester"));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(SummariseSelectionTactic.Key,
                new TacticInput(new[] { new Selection(SelectionKind.TimeRange, new[] { new SelectionItem("yesterday") }) }), "tester"));
            await h.MarkMinorAsync("p-ana");
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(DigestSubscriptionTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p-ana" }) }), "tester"));
            Assert.Contains("under 18", refused.Message);
        }
    }
}
