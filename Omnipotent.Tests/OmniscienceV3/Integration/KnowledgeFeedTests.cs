using Omnipotent.Services.KliveRAG;
using Omnipotent.Services.KliveRAG.Connectors;
using Omnipotent.Services.OmniscienceV3.Integration;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Tests.OmniscienceV3.Knowledge;

namespace Omnipotent.Tests.OmniscienceV3.Integration
{
    /// <summary>What KliveRAG may hold from v3 (KnowledgeFeed) and the connector that holds it. All people are synthetic.</summary>
    public class KnowledgeFeedTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        private const string Ana = "p-ana", Ben = "p-ben", Cam = "p-cam", Eve = "p-eve";

        private static KnowledgeFactDto Fact(string id, string attribute, string title, string category, string value, string status = AssertionStatus.Accepted,
            string sensitivity = "normal", string? other = null, long? endedAt = null) =>
            new(id, attribute, title, category, value, Polarity.Affirmed, other, status, 0.9, 0.9, true, null, null, endedAt, endedAt, null, null, Now - 1_000,
                "self_disclosure", sensitivity, "extract");

        private static Task PutProfileAsync(KnowledgeHarness h, string person, params KnowledgeCategoryDto[] categories) =>
            h.Context.ReadModels.PutAsync(DeductionModule.ProfileReadModel, person, 1, h.Clock.NowMs, 1,
                new KnowledgeProfileDto(person, h.Clock.NowMs, false, categories, Array.Empty<KnowledgeFactDto>(), 0, null, false, "test"));

        private static async Task<KnowledgeHarness> StartAsync()
        {
            var h = await KnowledgeHarness.StartAsync(Now);
            var b = new CorpusBuilder(h.Stores);
            await b.PersonAsync(Ana, "Ana Testwell");
            await b.PersonAsync(Ben, "Ben Testwell");
            await b.PersonAsync(Cam, "Cam Testwell");
            await b.PersonAsync(Eve, "Eve Testwell");
            // Cam is marked a minor; Ben's analysis is switched off.
            await h.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1", ("$p", Cam), ("$t", Now));
                AnalysisOff.Set(c, tx, new[] { Ben }, Now);
            });
            ReadModelStore.PolicyChanged();
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            await PutProfileAsync(h, Ana,
                new KnowledgeCategoryDto("location", new[]
                {
                    Fact("a1", "city", "City", "location", "Leeds"),
                    Fact("a2", "city", "City", "location", "Bristol", endedAt: Now - 86_400_000), // moved away: not current
                    Fact("a3", "city", "City", "location", "York", status: AssertionStatus.Proposed),  // not settled
                }),
                new KnowledgeCategoryDto("work", new[] { Fact("a4", "employer", "Employer", "work", "the library", status: AssertionStatus.Confirmed) }),
                new KnowledgeCategoryDto("family", new[]
                {
                    Fact("a5", "sibling", "Sibling", "family", "Eve", other: Eve),
                    Fact("a6", "sibling", "Sibling", "family", "Cam", other: Cam), // names a minor
                }),
                new KnowledgeCategoryDto(Attributes.Sensitive, new[] { Fact("a7", "religion", "Religion", Attributes.Sensitive, "synthetic", sensitivity: "sensitive_self") }));
            await PutProfileAsync(h, Ben, new KnowledgeCategoryDto("location", new[] { Fact("b1", "city", "City", "location", "Hull") }));
            await PutProfileAsync(h, Cam, new KnowledgeCategoryDto("work", new[] { Fact("c1", "employer", "Employer", "work", "a shop") }));
            return h;
        }

        [Fact]
        public async Task Feed_HoldsCurrentStatedFacts_AndNothingSensitiveUnsettledOrAboutPeopleItMayNotHold()
        {
            await using var h = await StartAsync();
            var feed = new KnowledgeFeed(h.Kit);

            Assert.True(feed.MayHold(Ana, h.Clock.NowMs));
            Assert.False(feed.MayHold(Ben, h.Clock.NowMs));          // analysis off
            Assert.False(feed.MayHold(Cam, h.Clock.NowMs));          // a minor
            Assert.False(feed.MayHold("p-nobody", h.Clock.NowMs));   // forgotten, or never known

            var page = feed.ChangedSince(0);
            Assert.Equal(new[] { Ana, Ben, Cam }.OrderBy(x => x), page.People.OrderBy(x => x));
            Assert.All(page.Documents, d => Assert.Equal(Ana, d.PersonId));
            var byId = page.Documents.ToDictionary(d => d.DocId);
            Assert.Equal(new[] { KnowledgeFeed.DocId(Ana, "family"), KnowledgeFeed.DocId(Ana, "location"), KnowledgeFeed.DocId(Ana, "work") }, byId.Keys.OrderBy(x => x));
            Assert.Equal("Ana Testwell — location:\n• City: Leeds", byId[KnowledgeFeed.DocId(Ana, "location")].Text);
            Assert.Contains("the library (confirmed by Klives)", byId[KnowledgeFeed.DocId(Ana, "work")].Text);
            Assert.Contains("Eve", byId[KnowledgeFeed.DocId(Ana, "family")].Text);
            Assert.DoesNotContain("Cam", byId[KnowledgeFeed.DocId(Ana, "family")].Text);
            Assert.Equal(Ana, KnowledgeFeed.PersonOf(KnowledgeFeed.DocId(Ana, "family")));
            Assert.Null(KnowledgeFeed.PersonOf("omnifact:x:y"));
            // A document says whom else it names, so its holder can tell when it names someone it may no longer hold.
            Assert.Equal(new[] { Eve }, byId[KnowledgeFeed.DocId(Ana, "family")].Names);
            Assert.Empty(byId[KnowledgeFeed.DocId(Ana, "location")].Names);

            // Nothing new after the watermark; a rebuilt profile is picked up again.
            Assert.Empty(feed.ChangedSince(page.Watermark).People);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            await PutProfileAsync(h, Eve, new KnowledgeCategoryDto("location", new[] { Fact("e1", "city", "City", "location", "Derby") }));
            Assert.Equal(new[] { Eve }, feed.ChangedSince(page.Watermark).People);
        }

        [Fact]
        public async Task Connector_FeedsWhenSwitchedOn_RemovesWhatV3NoLongerAllows_AndLetsGoOfEverythingWhenSwitchedOff()
        {
            await using var h = await StartAsync();
            string root = Path.Combine(Path.GetTempPath(), "omniv3_rag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var db = new KliveRAGDb(Path.Combine(root, "rag.db"));
                db.Migrate();
                var writer = new RagIndexWriter(db);
                bool on = true;
                var connector = new OmniscienceV3Connector(writer, _ => { }, () => Task.FromResult<Omnipotent.Services.OmniscienceV3.V3Runtime?>(h.Runtime), () => Task.FromResult(on));
                // What v2 had indexed: v3 cannot forget anyone in it, so it goes once v3 feeds the source.
                await writer.UpsertAsync(new RagDocument { DocId = "omnifact:v2-person:work", Source = RagSource.Omniscience, Title = "v2", Content = "synthetic v2 fact", ContentHash = "h", CreatedAtUnixMs = Now, SingleChunk = true });
                await writer.SetCursorAsync("omniscience:facts", "123");

                await connector.RunIncrementalAsync(CancellationToken.None);
                var held = writer.GetDocIdsForSource(RagSource.Omniscience).OrderBy(x => x).ToList();
                Assert.Equal(new[] { KnowledgeFeed.DocId(Ana, "family"), KnowledgeFeed.DocId(Ana, "location"), KnowledgeFeed.DocId(Ana, "work") }, held);
                Assert.Equal("0", writer.GetCursor("omniscience:facts")); // v2 re-reads from scratch if the switch goes back

                // Analysis switched off for Ana: everything held about her goes on the next pass, rebuilt profile or not.
                await h.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { Ana }, h.Clock.NowMs));
                await connector.RunIncrementalAsync(CancellationToken.None);
                Assert.Empty(writer.GetDocIdsForSource(RagSource.Omniscience));

                // Back on, and her profile rebuilt: held again. A category she no longer has facts in is removed.
                await h.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Clear(c, tx, new[] { Ana }));
                h.Clock.Advance(TimeSpan.FromMinutes(1));
                await PutProfileAsync(h, Ana, new KnowledgeCategoryDto("location", new[] { Fact("a1", "city", "City", "location", "Leeds") }));
                await connector.RunIncrementalAsync(CancellationToken.None);
                Assert.Equal(new[] { KnowledgeFeed.DocId(Ana, "location") }, writer.GetDocIdsForSource(RagSource.Omniscience));

                // Switched off (shadow mode): v3 lets go of what it held and starts again from the beginning next time.
                on = false;
                await connector.RunIncrementalAsync(CancellationToken.None);
                Assert.Empty(writer.GetDocIdsForSource(RagSource.Omniscience));
                Assert.Equal("0", writer.GetCursor(OmniscienceV3Connector.Cursor));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* a handle may linger on Windows; the temp dir is unique */ }
            }
        }

        private static async Task<(KliveRAGDb Db, RagIndexWriter Writer, string Root)> IndexAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "omniv3_rag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var db = new KliveRAGDb(Path.Combine(root, "rag.db"));
            db.Migrate();
            return (db, new RagIndexWriter(db), root);
        }

        private static void Remove(string root)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a handle may linger on Windows; the temp dir is unique */ }
        }

        [Fact]
        public async Task AForgetCountsFromItsConfirmation_AndADocumentNamingThemIsRebuiltWithoutThem()
        {
            await using var h = await StartAsync();
            var (_, writer, root) = await IndexAsync();
            try
            {
                var connector = new OmniscienceV3Connector(writer, _ => { }, () => Task.FromResult<Omnipotent.Services.OmniscienceV3.V3Runtime?>(h.Runtime), () => Task.FromResult(true));
                await connector.RunIncrementalAsync(CancellationToken.None);
                string family = KnowledgeFeed.DocId(Ana, "family"), location = KnowledgeFeed.DocId(Ana, "location"), work = KnowledgeFeed.DocId(Ana, "work");
                Assert.Equal(new[] { family, location, work }, writer.GetDocIdsForSource(RagSource.Omniscience).OrderBy(x => x));
                var feed = new KnowledgeFeed(h.Kit);
                Assert.Empty(connector.Withheld(new[] { family, location, work }, v3Feeds: true, h.Runtime));

                // Klives confirms forgetting Eve: the tombstone is written with the confirmation, and the run that removes her
                // data has not started. From this moment nothing about her, and nothing naming her, may be shown.
                await h.Stores.Operator.WriteAsync((c, tx) => Forgotten.Add(c, tx, Array.Empty<string>(), new[] { Eve }, Array.Empty<string>(), h.Clock.NowMs));
                Assert.False(feed.MayHold(Eve, h.Clock.NowMs));
                Assert.Equal(new[] { family }, connector.Withheld(new[] { family, location, work }, v3Feeds: true, h.Runtime));

                // The pass asked for by the forget: Ana's family document named only Eve and a minor, so nothing is left of
                // it; her other documents are untouched. Her profile has not been rebuilt, so this cannot wait for that.
                await connector.RunIncrementalAsync(CancellationToken.None);
                Assert.Equal(new[] { location, work }, writer.GetDocIdsForSource(RagSource.Omniscience).OrderBy(x => x));

                // Who is not there to be asked, or does not feed, shows nothing of v3's; a document v3's index does not know is not shown either.
                Assert.Equal(new[] { location }, connector.Withheld(new[] { location }, v3Feeds: true, runtime: null));
                Assert.Equal(new[] { location }, connector.Withheld(new[] { location, "omnifact:v2:work" }, v3Feeds: false, h.Runtime));
                Assert.Equal(new[] { "omnifact:v2:work" }, connector.Withheld(new[] { location, "omnifact:v2:work" }, v3Feeds: true, h.Runtime));
                Assert.Equal(new[] { KnowledgeFeed.DocId(Ana, "hobbies") }, connector.Withheld(new[] { KnowledgeFeed.DocId(Ana, "hobbies") }, v3Feeds: true, h.Runtime));
                // Documents of other sources are nobody's business here.
                Assert.Empty(connector.Withheld(new[] { "repo:README.md", "omni3msg:discord:1" }, v3Feeds: true, h.Runtime));
            }
            finally
            {
                Remove(root);
            }
        }

        [Fact]
        public async Task ADocumentStoredBeforeItSaidWhomItNames_IsRebuiltOnce_AndNotShownUntilThen()
        {
            await using var h = await StartAsync();
            var (_, writer, root) = await IndexAsync();
            try
            {
                string family = KnowledgeFeed.DocId(Ana, "family");
                await writer.UpsertAsync(new RagDocument { DocId = family, Source = RagSource.Omniscience, Title = "old", Content = "Ana Testwell — family:\n• Sibling: Eve", ContentHash = "old", CreatedAtUnixMs = Now, SingleChunk = true });
                var connector = new OmniscienceV3Connector(writer, _ => { }, () => Task.FromResult<Omnipotent.Services.OmniscienceV3.V3Runtime?>(h.Runtime), () => Task.FromResult(true));
                Assert.Equal(new[] { family }, connector.Withheld(new[] { family }, v3Feeds: true, h.Runtime)); // it does not say whom it names

                await connector.RunIncrementalAsync(CancellationToken.None);
                Assert.Empty(connector.Withheld(new[] { family }, v3Feeds: true, h.Runtime));
                Assert.Contains("\"Names\":[\"" + Eve + "\"]", writer.GetDocMeta(new[] { family })[family]);
            }
            finally
            {
                Remove(root);
            }
        }

        [Fact]
        public async Task MessageSearch_LeavesOutDeletedMessages_AndPeopleItMayNotHold()
        {
            await using var h = await StartAsync();
            var b = new CorpusBuilder(h.Stores);
            string room = CorpusIds.Conversation("discord", "room");
            await b.ConversationAsync(room, "group_dm", Ana, Ben, Cam, Eve);
            long t = h.Clock.NowMs - 3_600_000;
            string ana = await b.MessageAsync(Ana, room, t, "the lighthouse tour was great");
            await b.MessageAsync(Ben, room, t + 1_000, "lighthouse again tomorrow");
            await b.MessageAsync(Cam, room, t + 2_000, "lighthouse photos are up");
            string gone = await b.MessageAsync(Eve, room, t + 3_000, "lighthouse? I deleted this");
            await h.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE messages SET deleted_at_ms=$t WHERE message_id=$m", ("$t", h.Clock.NowMs), ("$m", gone)));

            var hits = await new KnowledgeFeed(h.Kit).SearchMessagesAsync("lighthouse", 4, CancellationToken.None);
            Assert.Equal(new[] { ana }, hits.Select(x => x.MessageId));
            Assert.Equal("Ana Testwell", hits[0].Author);
        }
    }
}
