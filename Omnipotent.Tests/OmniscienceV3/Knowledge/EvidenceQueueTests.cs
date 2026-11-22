using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// What is waiting to be read about a tracked person is a queue that is pushed to, not a list worked out from their whole
    /// history by every job. On the server the three busiest people's jobs spent three to fifteen hours a run building that
    /// list (140,000 scattered row reads and more, before the first model call) and never finished one; they held the one
    /// worker of the model lane, and no extraction ran for a day (21 Nov 2026). Invented people and text only.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class EvidenceQueueTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        private const long Minute = 60_000L;
        private const long Day = 86_400_000L;

        private static Dictionary<string, EvidenceRelation> Queued(KnowledgeHarness h, string person)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT message_id, relation FROM kn_evidence_queue WHERE person_id=$p", r => (r.GetString(0), (EvidenceRelation)r.GetInt32(1)), ("$p", person))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        }

        /// <summary>Records everything waiting for a person as read (what the slices of a job do, without the model).</summary>
        private static async Task ReadAllAsync(KnowledgeHarness h, string person)
        {
            var queue = EvidenceQueue.For(h.Kit);
            var head = queue.Head(person, 10_000);
            await new ClaimStore(h.Stores, h.Clock).CommitAsync(new SliceCommit("xr_test", person, head.Select(a => (a.MessageId, a.Version)).ToList(), "ok",
                Array.Empty<VerifiedClaim>(), Array.Empty<RejectedClaim>(), Array.Empty<Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeEvidenceRow>(), 0), ExtractionPrompt.Version, "fake", CancellationToken.None);
        }

        private static Task MentionAsync(KnowledgeHarness h, string messageId, string person) => h.Stores.Corpus.WriteAsync((c, tx) =>
            c.Exec(tx, "INSERT INTO message_mentions(message_id, target_kind, target_id) VALUES($m,'user',$t)", ("$m", messageId), ("$t", CorpusBuilder.Identity(person))));

        private static Task EditAsync(KnowledgeHarness h, string messageId, string author, string conversation, string text) => h.Stores.Corpus.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "UPDATE messages SET current_version=current_version+1, content=$x, edited_at_ms=$t WHERE message_id=$m", ("$x", text), ("$t", Now), ("$m", messageId));
            c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, recorded_at_ms, source_id) SELECT message_id, current_version, content, $t, 'test' FROM messages WHERE message_id=$m",
                ("$t", Now), ("$m", messageId));
            ChangeLog.Append(c, tx, Now, ChangeKinds.MessageVersion, messageId, author, conversation, SourceKinds.Live);
        });

        /// <summary>
        /// The routing cursor cannot pass a window it cannot read, so the look for a name in a message must not be able to
        /// fail on the message: half an emoji, or a U+FFFE somebody pasted, would otherwise stop the routing of every later
        /// message for everyone (normalising a whole string throws on both).
        /// </summary>
        [Fact]
        public void ANameIsFoundInAnyText_AndNoTextCanMakeTheLookFail()
        {
            // Text is built from code units here, so this file holds no character an editor or a tool could change.
            static string S(params int[] units) => new(units.Select(u => (char)u).ToArray());
            string eAcute = S(0xE9), capitalEAcute = S(0xC9), acute = S(0x301), ellipsis = S(0x2026);
            string emoji = S(0xD83D, 0xDE00), doubleStruckZero = S(0xD835, 0xDFD8);

            string[] dee = EvidenceQueue.NameWords("Dee Marsh");
            Assert.Equal(new[] { "dee", "marsh" }, dee);

            // As the full-text index holds them: lower case, marks removed, anything else ends a word.
            Assert.Equal(new[] { "cafe", "ole", "dee", "marsh", "42" }, EvidenceQueue.NameWords("Caf" + eAcute + "  OL" + capitalEAcute + "! de" + acute + "e-Marsh" + ellipsis + "42"));
            Assert.True(EvidenceQueue.ContainsPhrase(EvidenceQueue.NameWords("so, D" + eAcute + "e   MARSH said no"), dee));
            Assert.False(EvidenceQueue.ContainsPhrase(EvidenceQueue.NameWords("Marsh, Dee said no"), dee)); // in order only
            Assert.False(EvidenceQueue.ContainsPhrase(EvidenceQueue.NameWords("deep marshes"), dee));       // whole words only

            // An emoji ends a word; a letter or digit outside the basic plane is part of one.
            Assert.Equal(new[] { "dee", "marsh" }, EvidenceQueue.NameWords("dee" + emoji + "marsh"));
            Assert.Single(EvidenceQueue.NameWords("a" + doubleStruckZero + "b"));

            // What is not a character at all: half a surrogate pair (text cut in the middle of an emoji), either half, alone or
            // doubled, and the noncharacters U+FFFE and U+FFFF. Each ends a word and nothing else; the name beside it is found.
            foreach (string broken in new[] { S(0xD83D), S(0xDE00), S(0xFFFE), S(0xFFFF), S(0xD83D, 0xD83D), S(0xDE00, 0xD83D) })
            {
                string which = string.Join(" ", broken.Select(c => ((int)c).ToString("X4")));
                Assert.Empty(EvidenceQueue.NameWords(broken));
                Assert.True(EvidenceQueue.ContainsPhrase(EvidenceQueue.NameWords(broken + "Dee Marsh" + broken), dee), "around " + which);
                Assert.Equal(new[] { "de", "e", "marsh" }, EvidenceQueue.NameWords("de" + broken + "e marsh"));
            }
            Assert.Empty(EvidenceQueue.NameWords(""));
        }

        [Fact]
        public async Task WhatArrivesLater_IsRoutedToEveryoneItIsAbout_AndToNobodyElse()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync("sam", "Sam Okoro", tier: "tracked");
            await corpus.PersonAsync("eve", "Eve Stone");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            await corpus.ConversationAsync("discord:c-trio", "group_dm", "me", "dee", "eve");
            await corpus.ConversationAsync("discord:c-eve", "dm", "me", "eve");
            await corpus.ConversationAsync("discord:c-server", "guild_channel");
            string hers = await corpus.MessageAsync("dee", "discord:c-dee", Now - 40 * Day, "an old line of hers");
            string evesOld = await corpus.MessageAsync("eve", "discord:c-trio", Now - 39 * Day, "an old line of Eve's");
            await corpus.MessageAsync("dee", "discord:c-server", Now - 38 * Day, "she has written in the server too");
            var tracked = new[] { "dee", "sam" };
            var queue = EvidenceQueue.For(h.Kit);

            // The first pass takes the log as read up to here: what was there before is each person's history.
            var first = await queue.RouteAsync(tracked, ExtractionPrompt.Version, 10_000, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal((0, 0), (first!.Added, first.Persons.Count));
            Assert.Empty(Queued(h, "dee"));

            // Her history is looked through and read.
            var topUp = await queue.TopUpAsync("dee", ExtractionPrompt.Version, 600, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(topUp.Complete);
            Assert.Equal(2, topUp.Queued);
            await ReadAllAsync(h, "dee");
            Assert.Empty(Queued(h, "dee"));

            // Then things happen.
            string newLine = await corpus.MessageAsync("dee", "discord:c-dee", Now - 9 * Minute, "a new line of hers", sourceKind: "live");
            string replyToHer = await corpus.MessageAsync("me", "discord:c-dee", Now - 8 * Minute, "an answer to her old line", sourceKind: "live", replyTo: hers);
            string herReply = await corpus.MessageAsync("dee", "discord:c-trio", Now - 7 * Minute, "she answers Eve's old line", sourceKind: "live", replyTo: evesOld);
            string atSam = await corpus.MessageAsync("eve", "discord:c-server", Now - 6 * Minute, "someone should ask him", sourceKind: "live");
            await MentionAsync(h, atSam, "sam");
            string namesHer = await corpus.MessageAsync("eve", "discord:c-trio", Now - 5 * Minute, "I spoke to Dee Marsh about it yesterday", sourceKind: "live");
            string namesHerInServer = await corpus.MessageAsync("eve", "discord:c-server", Now - 5 * Minute, "ask DEE, she knows", sourceKind: "live");
            string anotherDee = await corpus.MessageAsync("eve", "discord:c-eve", Now - 4 * Minute, "Dee Marsh is a common name", sourceKind: "live");
            string notHerName = await corpus.MessageAsync("eve", "discord:c-trio", Now - 4 * Minute, "indeed, the deed is done", sourceKind: "live");
            string sams = await corpus.MessageAsync("sam", "discord:c-server", Now - 3 * Minute, "Sam's own line", sourceKind: "live");
            string future = await corpus.MessageAsync("dee", "discord:c-dee", Now + 60 * Minute, "dated after now", sourceKind: "live");
            await EditAsync(h, hers, "dee", "discord:c-dee", "her old line, edited");

            var routed = await queue.RouteAsync(tracked, ExtractionPrompt.Version, 10_000, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(new[] { "dee", "sam" }, routed!.Persons.OrderBy(p => p, StringComparer.Ordinal).ToArray());
            Assert.Equal(routed.MaxSeq, routed.Cursor);
            var dee = Queued(h, "dee");
            Assert.Equal(EvidenceRelation.Authored, dee[newLine]);                 // what she wrote
            Assert.Equal(EvidenceRelation.ReplyToSubject, dee[replyToHer]);        // an answer to something of hers, however old
            Assert.Equal(EvidenceRelation.Authored, dee[herReply]);
            Assert.Equal(EvidenceRelation.RepliedBySubject, dee[evesOld]);         // what she answered
            Assert.Equal(EvidenceRelation.Mention, dee[namesHer]);                 // her name, where she is a member
            Assert.Equal(EvidenceRelation.Mention, dee[namesHerInServer]);         // her first name, where she has written
            Assert.Equal(EvidenceRelation.Authored, dee[hers]);                    // read before, edited since: read again
            Assert.Equal(7, dee.Count);
            Assert.DoesNotContain(anotherDee, dee.Keys);                           // her name where she is not: somebody else
            Assert.DoesNotContain(notHerName, dee.Keys);                           // "indeed", "deed": not her name
            Assert.DoesNotContain(future, dee.Keys);
            var sam = Queued(h, "sam");
            Assert.Equal(EvidenceRelation.Mention, sam[atSam]);
            Assert.Equal(EvidenceRelation.Authored, sam[sams]);
            Assert.Equal(2, sam.Count);
            Assert.Empty(Queued(h, "eve"));                                         // not tracked: nothing is kept for her

            // The same rows are not routed twice, and a second pass has nothing to do.
            var again = await queue.RouteAsync(tracked, ExtractionPrompt.Version, 10_000, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal((0, 0), (again!.Changes, again.Persons.Count));

            // Read once, an anchor leaves the queue, and her count of read messages moves with it.
            await ReadAllAsync(h, "dee");
            Assert.Empty(Queued(h, "dee"));
            using var intel = h.Stores.Intel.OpenRead();
            Assert.Equal(8L, intel.Scalar<long>(null, "SELECT covered_messages FROM kn_person_state WHERE person_id='dee'")); // 2 from her history, 6 new; the edit was one of the two
            Assert.Equal(intel.Scalar<long>(null, "SELECT COUNT(*) FROM kn_coverage WHERE person_id='dee'"), intel.Scalar<long>(null, "SELECT covered_messages FROM kn_person_state WHERE person_id='dee'"));
        }

        [Fact]
        public async Task HistoryIsPaged_AlongTheIndex_WithNoSort_AndAResetReadsNothingTwice()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            // Minute-precision history: many messages share an instant, which is where a page boundary can lose or repeat one.
            var hers = new List<string>();
            for (int i = 0; i < 130; i++) hers.Add(await corpus.MessageAsync("dee", "discord:c-dee", Now - 20 * Day + (i / 7) * Minute, $"line {i}"));
            var queue = EvidenceQueue.For(h.Kit);
            int before = EvidenceQueue.PageSize;
            EvidenceQueue.PageSize = 25;
            try
            {
                // The page is a walk along the (author, time) index from where the last page ended: no pass over the rest
                // of what she wrote, and no sort.
                using (var c = h.Stores.Corpus.OpenRead())
                {
                    var plan = c.Query(null, """
                        EXPLAIN QUERY PLAN SELECT message_id, rowid FROM messages INDEXED BY ix_messages_author_time
                        WHERE author_identity_id=$a AND (sent_at_ms, rowid) > ($t, $r) AND sent_at_ms <= $now ORDER BY sent_at_ms, rowid LIMIT $n
                        """, r => r.GetString(3), ("$a", CorpusBuilder.Identity("dee")), ("$t", 0L), ("$r", 0L), ("$now", Now), ("$n", 25));
                    Assert.Contains(plan, p => p.Contains("ix_messages_author_time") && p.Contains("sent_at_ms>"));
                    Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
                }

                var first = await queue.TopUpAsync("dee", ExtractionPrompt.Version, 40, TimeSpan.FromSeconds(30), CancellationToken.None);
                Assert.False(first.Complete);
                Assert.Equal(50, first.Looked);                 // two pages, to have forty waiting
                Assert.Equal(hers.Take(50), queue.Head("dee", 1_000).Select(a => a.MessageId));

                // Carried on to the end: every message once, in order.
                EvidenceTopUp last;
                do last = await queue.TopUpAsync("dee", ExtractionPrompt.Version, 10_000, TimeSpan.FromSeconds(30), CancellationToken.None);
                while (!last.Complete);
                Assert.Equal(hers, queue.Head("dee", 1_000).Select(a => a.MessageId));

                // Half of it is read; then her queue is reset (a merge): the history is looked through again, and what was
                // read is not queued again.
                var half = queue.Head("dee", 65);
                await new ClaimStore(h.Stores, h.Clock).CommitAsync(new SliceCommit("xr_half", "dee", half.Select(a => (a.MessageId, a.Version)).ToList(), "ok",
                    Array.Empty<VerifiedClaim>(), Array.Empty<RejectedClaim>(), Array.Empty<Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeEvidenceRow>(), 0), ExtractionPrompt.Version, "fake", CancellationToken.None);
                await queue.ResetAsync("dee", CancellationToken.None);
                Assert.Equal(0, queue.Count("dee"));
                do last = await queue.TopUpAsync("dee", ExtractionPrompt.Version, 10_000, TimeSpan.FromSeconds(30), CancellationToken.None);
                while (!last.Complete);
                Assert.Equal(hers.Skip(65), queue.Head("dee", 1_000).Select(a => a.MessageId));
            }
            finally
            {
                EvidenceQueue.PageSize = before;
            }
        }

        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Question.Contains("sceptical reviewer", StringComparison.Ordinal))
                    return JsonSerializer.Serialize(new { verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "stated" }) });
                var roster = FakeLlm.Roster(call.Prompt);
                var claims = new List<object>();
                if (!roster.TryGetValue("Dee Marsh", out var subject)) return JsonSerializer.Serialize(new { claims }); // a reading she is not part of
                foreach (var (reference, (handle, text)) in FakeLlm.EvidenceLines(call.Prompt))
                {
                    var m = Regex.Match(text, @"I live in (Town\d+)");
                    if (handle != subject || !m.Success) continue;
                    claims.Add(new
                    {
                        id = "c" + (claims.Count + 1), subject, attribute = "city", value = m.Groups[1].Value, polarity = "affirmed", modality = "literal",
                        source_kind = "self_disclosure", speech_act = "statement", sincerity = "sincere", evidence = new[] { new { @ref = reference, quote = m.Value } }, probability = 0.9,
                    });
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

        private static async Task<List<string>> TurnAsync(KnowledgeHarness h)
        {
            while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
            await h.Runtime.Host.PlanOnceAsync();
            return await InquiryTests.DriveAsync(h);
        }

        private static List<(long Id, string Kind, string Lane, string State, string? Subject)> Jobs(KnowledgeHarness h, string kind)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT job_id, kind, lane, state, subject_id FROM jobs WHERE module_id='knowledge.deduction' AND kind=$k ORDER BY job_id",
                r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4)), ("$k", kind));
        }

        /// <summary>
        /// The model lane is for model work. On the server 2,795 re-verifications of edited messages, nearly all with nothing
        /// to do, were queued in it ahead of fourteen people's readings; and whoever a new message was about, beyond its
        /// author, was found by giving every tracked member of the conversation a job that looked through their history.
        /// </summary>
        [Fact]
        public async Task AnEdit_IsReverifiedOutsideTheModelLane_AndANewMessage_IsReadByExactlyThePeopleItIsAbout()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync("sam", "Sam Okoro", tier: "tracked");
            await corpus.PersonAsync("eve", "Eve Stone");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            await corpus.ConversationAsync("discord:c-trio", "group_dm", "me", "dee", "sam", "eve");
            string home = await corpus.MessageAsync("dee", "discord:c-dee", Now - 30 * Day, "by the way I live in Town7 now, moved last month");
            string plain = await corpus.MessageAsync("dee", "discord:c-dee", Now - 29 * Day, "nothing much else to report");
            await corpus.MessageAsync("sam", "discord:c-trio", Now - 28 * Day, "Sam was here");
            for (int round = 0; round < 6; round++)
            {
                var failed = await TurnAsync(h);
                Assert.True(failed.Count == 0, string.Join("\n", failed));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }
            Assert.Contains(h.Kit.Assertions.Query(new AssertionQuery { SubjectIds = new[] { "dee" }, AtMs = h.Clock.NowMs, Limit = 50 }), v => v.Record.Attribute == "city");
            long lastJobBefore;
            using (var c = h.Stores.Intel.OpenRead()) lastJobBefore = c.Scalar<long?>(null, "SELECT MAX(job_id) FROM jobs") ?? 0;

            // Two edits: of the message a fact rests on, and of one nothing rests on. And Eve says something in the group
            // chat that names nobody.
            await EditAsync(h, home, "dee", "discord:c-dee", "by the way I live in Town9 now, moved again");
            await EditAsync(h, plain, "dee", "discord:c-dee", "nothing much else to report, really");
            await corpus.MessageAsync("eve", "discord:c-trio", h.Clock.NowMs - Minute, "good morning all", sourceKind: "live");
            while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
            await h.Runtime.Host.PlanOnceAsync();

            var reverify = Jobs(h, DeductionModule.ReverifyKind);
            Assert.Equal(2, reverify.Count);
            Assert.All(reverify, j => Assert.Equal(nameof(Lane.Cpu), j.Lane));
            Assert.Contains(Jobs(h, DeductionModule.RouteKind), j => j.Lane == nameof(Lane.Cpu) && j.State == JobStates.Queued);

            // The re-verification of the cited message makes no model call itself: it queues the consolidation.
            int callsBefore = h.Llm.Calls.Count;
            var module = h.Module<DeductionModule>();
            foreach (var job in reverify)
            {
                var leased = h.Runtime.Host.Jobs.Get(job.Id)!;
                await module.ExecuteAsync(h.JobFor(leased));
            }
            Assert.Equal(callsBefore, h.Llm.Calls.Count);
            Assert.Contains(Jobs(h, DeductionModule.ConsolidateKind), j => j.State == JobStates.Queued && j.Subject == "dee" && j.Lane == nameof(Lane.LlmBackground));

            // Everything runs: the edit is read again (the fact moves to Town9), and Eve's good morning gave nobody a reading
            // but its own routing (Sam and Dee are members of the chat; the message is about neither).
            for (int round = 0; round < 6; round++)
            {
                var failed = await TurnAsync(h);
                Assert.True(failed.Count == 0, string.Join("\n", failed));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }
            var facts = h.Kit.Assertions.Query(new AssertionQuery { SubjectIds = new[] { "dee" }, AtMs = h.Clock.NowMs, Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed }, Limit = 50 });
            Assert.Contains(facts, v => v.Record.Attribute == "city" && v.Record.ValueText == "Town9");
            Assert.DoesNotContain(Jobs(h, DeductionModule.ExtractKind), j => j.Subject == "sam" && j.Id > lastJobBefore);
            Assert.Contains(Jobs(h, DeductionModule.ExtractKind), j => j.Subject == "dee" && j.Id > lastJobBefore && j.State == JobStates.Succeeded);
            Assert.DoesNotContain(Jobs(h, DeductionModule.ReverifyKind), j => j.State == JobStates.Dead);
        }

        [Fact]
        public async Task TheSweep_ClosesReverificationsWithNothingToDo_AndQueuesAReadingForWhoeverHasAnchorsWaiting()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync("eve", "Eve Stone");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            await corpus.ConversationAsync("discord:c-trio", "group_dm", "me", "dee", "eve");
            string home = await corpus.MessageAsync("dee", "discord:c-dee", Now - 30 * Day, "by the way I live in Town7 now, moved last month");
            var plain = new List<string>();
            for (int i = 0; i < 5; i++) plain.Add(await corpus.MessageAsync("dee", "discord:c-dee", Now - 29 * Day + i * Minute, $"nothing much to report ({i})"));
            for (int round = 0; round < 6; round++)
            {
                var failed = await TurnAsync(h);
                Assert.True(failed.Count == 0, string.Join("\n", failed));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }

            // As the server stood: re-verifications queued in the model lane, one for a message a fact rests on, the rest
            // for messages nothing rests on.
            var jobs = h.Runtime.Host.Jobs;
            async Task<long> OldReverifyAsync(string message) => await jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = DeductionModule.Id, Kind = DeductionModule.ReverifyKind, SubjectKind = "message", SubjectId = message, Lane = Lane.LlmBackground, Priority = 150,
                DedupeKey = JobRequest.KeyFor(DeductionModule.Id, DeductionModule.ReverifyKind, "message", message),
            });
            long cited = await OldReverifyAsync(home);
            var idle = new List<long>();
            foreach (var m in plain) idle.Add(await OldReverifyAsync(m));
            // And a message that names her, written by someone who is not tracked: nothing plans a reading for it.
            await corpus.MessageAsync("eve", "discord:c-trio", h.Clock.NowMs - Minute, "has Dee Marsh said when she is back", sourceKind: "live");

            var module = h.Module<DeductionModule>();
            string result = (await module.ExecuteAsync(h.Job(DeductionModule.Id, DeductionModule.SweepKind)))!;

            using var doc = JsonDocument.Parse(result);
            Assert.Equal(5, doc.RootElement.GetProperty("closedReverifications").GetInt32());
            Assert.All(idle, id => Assert.Equal(JobStates.Cancelled, jobs.Get(id)!.State));
            Assert.Equal(JobStates.Queued, jobs.Get(cited)!.State);
            Assert.True(doc.RootElement.GetProperty("routed").GetInt32() >= 1);
            Assert.Contains(Jobs(h, DeductionModule.ExtractKind), j => j.Subject == "dee" && j.State == JobStates.Queued);
            // The counters the pages read stayed exact through the closing.
            using var c = h.Stores.Intel.OpenRead();
            var counted = c.Query(null, "SELECT lane || '|' || module_id || '|' || state, n FROM job_counts WHERE n <> 0", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
            var recounted = c.Query(null, "SELECT lane || '|' || module_id || '|' || state, COUNT(*) FROM jobs GROUP BY lane, module_id, state", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
            Assert.Equal(recounted, counted);
        }
    }
}
