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
            // 1,600 of her messages wait to be read; slices were prepared for a few hundred of them, not for all (every one
            // of which means reading the conversation around it).
            Assert.InRange(pipeline.PreparedAnchors, stats.Messages, 800);
            Assert.True(pipeline.PreparedAnchors < hers.Count / 2, $"prepared {pipeline.PreparedAnchors} of {hers.Count}");

            // What it read is the oldest of what was unread: nothing was skipped by preparing less.
            HashSet<string> covered;
            using (var intel = h.Stores.Intel.OpenRead())
                covered = intel.Query(null, "SELECT message_id FROM kn_coverage WHERE person_id='dee'", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(stats.Messages, covered.Count);
            long newestRead = hers.Where(m => covered.Contains(m.Id)).Max(m => m.At), oldestUnread = hers.Where(m => !covered.Contains(m.Id)).Min(m => m.At);
            Assert.True(newestRead < oldestUnread);

            // It says where it is from the start: finding the evidence, the mentions, the plan, the preparing, then each slice.
            string[] stages = reports.Where(r => r.TryGetProperty("stage", out _)).Select(r => r.GetProperty("stage").GetString()!).ToArray();
            Assert.Equal(new[] { "evidence", "mentions", "planning" }, stages.Take(3));
            Assert.Contains("preparing", stages);
            int firstSlice = reports.FindIndex(r => r.TryGetProperty("slice", out _));
            Assert.True(firstSlice >= 4, "the job reported four times or more before its first slice was read");
            Assert.Equal(hers.Count, reports.First(r => r.TryGetProperty("unread", out _)).GetProperty("unread").GetInt32());

            // The next job carries on from there.
            var next = await pipeline.RunAsync("dee", await JobAsync(h, new List<JsonElement>()));
            Assert.Equal(stats.Messages, next.AlreadyRead);
            Assert.Equal(4, next.Slices);
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
