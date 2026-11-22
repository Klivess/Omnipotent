using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// What an extraction job does before its first model call. On the server five people's jobs were stopped for "no
    /// progress" after an hour, five attempts each, without one call made (20 Nov 2026): the job prepared slices for up
    /// to 4,000 anchors to read the first hundred or so, looked for the person's name once per 200 of their conversations
    /// (each time over the whole archive, reading the row of every match), and said nothing while it did.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class ExtractionPlanningTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
        private const long Minute = 60_000L;

        private static async Task<JobContext> JobAsync(KnowledgeHarness h, List<JsonElement> reports)
        {
            await h.Runtime.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = "knowledge.deduction", Kind = "extract-under-test", Lane = Lane.Coordinator, Priority = 999 });
            var job = await h.Runtime.Host.Jobs.LeaseNextAsync(Lane.Coordinator, "test", TimeSpan.FromMinutes(10));
            return new JobContext(job!, h.Clock, CancellationToken.None, p =>
            {
                lock (reports) reports.Add(JsonDocument.Parse(p).RootElement);
                return Task.FromResult(true);
            });
        }

        [Fact]
        public async Task AJobPreparesWhatItWillRead_NotEverythingThereIsToRead_AndSaysWhereItIs()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            h.Llm.On("deduction.extract", _ => """{"claims":[]}""");
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            const string filler = "the weather has been all over the place again this week and nobody on the street can agree what to wear in the morning";
            const int messages = 2_400;
            var hers = new List<(string Id, long At)>();
            for (int i = 0; i < messages; i++)
            {
                long at = Now - 400L * 86_400_000 + i * 20 * Minute;
                bool mine = i % 3 == 0;
                string id = await corpus.MessageAsync(mine ? "me" : "dee", "discord:c-dee", at, $"{filler} ({i})");
                if (!mine) hers.Add((id, at));
            }
            var pipeline = new ExtractionPipeline(h.Kit, new ClaimStore(h.Stores, h.Clock), new ExtractionOptions());
            var reports = new List<JsonElement>();

            var stats = await pipeline.RunAsync("dee", await JobAsync(h, reports));

            // Four slices read, more left for the next job, as before.
            Assert.Equal(4, stats.Slices);
            Assert.True(stats.Pending);
            // 1,600 of her messages wait to be read. The job looked at the oldest few hundred of them to find what to read
            // (two pages of her history), not at all of them, and prepared slices for fewer still.
            Assert.InRange(pipeline.LookedAt, stats.Messages, 2 * EvidenceQueue.PageSize);
            Assert.True(pipeline.LookedAt < hers.Count / 2, $"looked at {pipeline.LookedAt} of {hers.Count}");
            Assert.InRange(pipeline.PreparedAnchors, stats.Messages, ExtractionPipeline.QueueTarget);

            // What it read is the oldest of what was unread: nothing was skipped by looking at less.
            HashSet<string> covered;
            using (var intel = h.Stores.Intel.OpenRead())
                covered = intel.Query(null, "SELECT message_id FROM kn_coverage WHERE person_id='dee'", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(stats.Messages, covered.Count);
            long newestRead = hers.Where(m => covered.Contains(m.Id)).Max(m => m.At), oldestUnread = hers.Where(m => !covered.Contains(m.Id)).Min(m => m.At);
            Assert.True(newestRead < oldestUnread);

            // It says where it is from the start: finding the evidence (a report per page), the plan, the preparing, then each slice.
            string[] stages = reports.Where(r => r.TryGetProperty("stage", out _)).Select(r => r.GetProperty("stage").GetString()!).ToArray();
            Assert.Equal("evidence", stages[0]);
            Assert.Equal(new[] { "evidence", "planning", "preparing" }, stages.Distinct().ToArray());
            int firstSlice = reports.FindIndex(r => r.TryGetProperty("slice", out _));
            Assert.True(firstSlice >= 4, "the job reported four times or more before its first slice was read");
            // What waits in her queue is what was planned from: several jobs' worth, not her whole history.
            int waiting = reports.First(r => r.TryGetProperty("unread", out _)).GetProperty("unread").GetInt32();
            Assert.InRange(waiting, stats.Messages, 2 * EvidenceQueue.PageSize);

            // The next job carries on from there, and what the first one read has left the queue.
            var next = await pipeline.RunAsync("dee", await JobAsync(h, new List<JsonElement>()));
            Assert.Equal(stats.Messages, next.AlreadyRead);
            Assert.Equal(4, next.Slices);
            Assert.InRange(pipeline.LookedAt, 0, EvidenceQueue.PageSize); // one more page at most: most of what it needs was waiting
            using (var intel = h.Stores.Intel.OpenRead())
            {
                var readNow = intel.Query(null, "SELECT message_id FROM kn_coverage WHERE person_id='dee'", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                Assert.Equal(stats.Messages + next.Messages, readNow.Count);
                Assert.Equal(0L, intel.Scalar<long>(null, "SELECT COUNT(*) FROM kn_evidence_queue q JOIN kn_coverage c ON c.person_id=q.person_id AND c.message_id=q.message_id WHERE q.person_id='dee'"));
                Assert.True(hers.Where(m => readNow.Contains(m.Id)).Max(m => m.At) < hers.Where(m => !readNow.Contains(m.Id)).Min(m => m.At));
            }
        }

        /// <summary>
        /// The whole of someone's history is still read, a job at a time: every message they wrote, and every reply, mention
        /// and naming of them, exactly what the full scan found. Job after job until nothing is pending.
        /// </summary>
        [Fact]
        public async Task JobAfterJob_EverythingTheFullScanFound_IsRead_AndNothingTwice()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            h.Llm.On("deduction.extract", _ => """{"claims":[]}""");
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.PersonAsync("sam", "Sam Okoro");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            await corpus.ConversationAsync("discord:c-trio", "group_dm", "me", "dee", "sam");
            await corpus.ConversationAsync("discord:c-sam", "dm", "me", "sam");
            var rnd = new Random(7);
            var hers = new List<string>();
            for (int i = 0; i < 420; i++)
            {
                long at = Now - 300L * 86_400_000 + i * 37 * Minute;
                string conv = i % 4 == 0 ? "discord:c-trio" : "discord:c-dee";
                string who = i % 3 == 0 ? "dee" : i % 3 == 1 ? "me" : conv == "discord:c-trio" ? "sam" : "me";
                string? replyTo = who != "dee" && hers.Count > 0 && rnd.Next(4) == 0 ? hers[rnd.Next(hers.Count)] : null;
                string text = who == "sam" && i % 16 == 0 ? $"has anyone heard from Dee Marsh today ({i})" : $"a line about nothing in particular, number {i}";
                string id = await corpus.MessageAsync(who, conv, at, text, replyTo: replyTo);
                if (who == "dee") hers.Add(id);
            }
            // Her name in a conversation she is not in is somebody else.
            await corpus.MessageAsync("sam", "discord:c-sam", Now - 5 * Minute, "Dee Marsh is a common name where I come from");

            // What the full scan made of her history (the code this replaced, still used by the tests).
            var assembler = new EvidenceAssembler(h.Kit);
            var identities = new[] { CorpusBuilder.Identity("dee") };
            var full = assembler.Anchors(identities, Now).Select(a => a.MessageId).ToHashSet(StringComparer.Ordinal);
            foreach (var a in assembler.NameMentions(new[] { "Dee Marsh", "Dee" }, new[] { "discord:c-dee", "discord:c-trio" }, identities, Now)) full.Add(a.MessageId);
            Assert.True(full.Count > hers.Count, "replies and mentions are part of her evidence");

            var before = (EvidenceQueue.PageSize, ExtractionPipeline.QueueTarget);
            (EvidenceQueue.PageSize, ExtractionPipeline.QueueTarget) = (20, 30);
            try
            {
                var pipeline = new ExtractionPipeline(h.Kit, new ClaimStore(h.Stores, h.Clock), new ExtractionOptions());
                int jobs = 0, read = 0;
                ExtractionRunStats stats;
                do
                {
                    stats = await pipeline.RunAsync("dee", await JobAsync(h, new List<JsonElement>()));
                    read += stats.Messages;
                    Assert.True(++jobs < 60, "the reading never came to an end");
                    Assert.InRange(pipeline.LookedAt, 0, 4 * 20); // never more than a few pages of her history in one job
                } while (stats.Pending);

                using var intel = h.Stores.Intel.OpenRead();
                var covered = intel.Query(null, "SELECT message_id FROM kn_coverage WHERE person_id='dee'", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                Assert.Empty(full.Except(covered));     // nothing the full scan found was missed
                Assert.Empty(covered.Except(full));     // and nothing else was read as her evidence
                Assert.Equal(covered.Count, read);      // each of them once
                Assert.Equal(0L, intel.Scalar<long>(null, "SELECT COUNT(*) FROM kn_evidence_queue WHERE person_id='dee'"));
                Assert.Equal((long)covered.Count, intel.Scalar<long>(null, "SELECT covered_messages FROM kn_person_state WHERE person_id='dee'"));
                Assert.True(jobs > 3, "her history took several jobs, as it should");
            }
            finally
            {
                (EvidenceQueue.PageSize, ExtractionPipeline.QueueTarget) = before;
            }
        }

        [Fact]
        public async Task ANameIsLookedForOnce_HoweverManyConversationsThePersonIsIn()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh");
            await corpus.PersonAsync("sam", "Sam");
            // She is in 450 conversations (more than two of the old 200-conversation rounds); her name comes up in each.
            var conversations = new List<string>();
            var mentions = new List<string>();
            for (int i = 0; i < 450; i++)
            {
                string conv = "discord:c-" + i;
                conversations.Add(conv);
                await corpus.ConversationAsync(conv, "group_dm", "me", "dee");
                mentions.Add(await corpus.MessageAsync("me", conv, Now - (1_000 - i) * Minute, $"did Dee Marsh say when she is back ({i})"));
            }
            string own = await corpus.MessageAsync("dee", conversations[0], Now - 5 * Minute, "this is Dee Marsh speaking");
            await corpus.ConversationAsync("discord:c-elsewhere", "dm", "me", "sam");
            string elsewhere = await corpus.MessageAsync("sam", "discord:c-elsewhere", Now - 4 * Minute, "another Dee Marsh entirely");
            string later = await corpus.MessageAsync("me", conversations[1], Now + 60 * Minute, "Dee Marsh, written after the moment asked about");
            var assembler = new EvidenceAssembler(h.Kit);
            var identities = new[] { CorpusBuilder.Identity("dee") };

            var found = assembler.NameMentions(new[] { "Dee Marsh" }, conversations, identities, Now);

            // Up to the limit per name, from her conversations only, never her own words, nothing after the moment asked about.
            var ids = found.Select(a => a.MessageId).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(400, found.Count);
            Assert.All(found, a => Assert.Contains(a.MessageId, mentions));
            Assert.DoesNotContain(own, ids);
            Assert.DoesNotContain(elsewhere, ids);
            Assert.DoesNotContain(later, ids);
            Assert.All(found, a => Assert.Equal(EvidenceRelation.Mention, a.Relation));

            // The newest matches are the ones looked at, and how many is bounded: each is one row read.
            var few = assembler.NameMentions(new[] { "Dee Marsh" }, conversations, identities, Now, scanPerName: 60);
            // The sixty newest matches are her own line, the two that are out of scope, and the 57 latest mentions (393 to 449).
            Assert.Equal(57, few.Count);
            Assert.All(few, a => Assert.True(a.SentAtMs >= Now - (1_000 - 393) * Minute));

            // A name the full-text grammar cannot hold finds nothing by name and does not stop the others.
            var mixed = assembler.NameMentions(new[] { "\"\"\"", "Dee Marsh" }, conversations.Take(3).ToList(), identities, Now);
            Assert.Equal(3, mixed.Count);
        }
    }
}
