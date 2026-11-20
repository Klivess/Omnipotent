using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Knowledge.Retrieval;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// Hybrid retrieval over the corpus: the vector half with the shared encoder installed, the shape of the full-text
    /// statement, and which words the full-text half searches for. On 18 Nov 2026 every inquiry on the server failed in its
    /// gather stage: the vector read never bound its model, after minutes of full-text search per query. The knowledge
    /// tests had all run without an encoder, on corpora too small for the slow plan to show.
    /// </summary>
    public class RetrievalTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));

        private static async Task<(string Cats, string Tabby, string Train)> AnaAsync(KnowledgeHarness h)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.ConversationAsync("discord:c-ana", "dm", "p-ana");
            string cats = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 3_600_000, "my cat knocked the plant over again");
            string tabby = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 3_000_000, "adopted a tiny tabby this weekend");
            string train = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 2_000_000, "train was late again");
            return (cats, tabby, train);
        }

        private static RetrievalScope AnasOwn(int limit = 5) => new() { AuthorIdentityIds = new[] { CorpusBuilder.Identity("p-ana") }, ToMs = Now, Limit = limit };

        [Fact]
        public async Task WithAnEncoder_AScopedSearchReadsTheMessageVectors_FiledUnderTheEncodersVersion()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (cats, tabby, train) = await AnaAsync(h);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            // As the classifier files them: under the encoder's model version. The tabby message carries the vector of "cat",
            // and no word of it matches the query, so only the vector half can find it.
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[]
            {
                new VectorItem(cats, FakeEncoder.Embed("my cat knocked the plant over again"), 1),
                new VectorItem(tabby, FakeEncoder.Embed("cat"), 1),
                new VectorItem(train, FakeEncoder.Embed("train was late again"), 1),
            });

            var (results, coverage) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, AnasOwn(), CancellationToken.None);

            Assert.True(coverage.Vectors);
            Assert.Null(coverage.Note);
            Assert.Equal(3, coverage.Candidates);
            Assert.Contains(results, r => r.MessageId == tabby && r.VectorRank == 1 && r.FtsRank == null);
            Assert.Contains(results, r => r.MessageId == cats && r.FtsRank == 1);
        }

        [Fact]
        public async Task VectorsFiledUnderAnotherModel_AreNeverComparedWithTheQuery()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (cats, tabby, _) = await AnaAsync(h);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            // Filed under the model id rather than the version the encoder stamps: another model's vectors, as far as the store knows.
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelId, new[] { new VectorItem(tabby, FakeEncoder.Embed("cat"), 1) });

            var (results, coverage) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, AnasOwn(), CancellationToken.None);

            Assert.False(coverage.Vectors);
            Assert.Contains("No message vectors exist", coverage.Note);
            Assert.Equal(new[] { cats }, results.Select(r => r.MessageId).ToArray());
        }

        [Fact]
        public async Task FullText_ReadsNoMessageRow_ToRankAMatchOrToTellWhetherItIsInScope()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            using var c = h.Stores.Corpus.OpenRead();

            // The matches, best first, come from the full-text index alone: the messages table is not in the plan at all.
            // (The statement before this one joined every match to its row before sorting; on the server that was over a
            // million row reads for one query, and two tactic runs died in it, 20 Nov 2026.)
            var ranked = c.Query(null, "EXPLAIN QUERY PLAN " + HybridRetriever.RankedMatches, r => r.GetString(3), ("$q", "\"cat\" OR \"cats\""));
            Assert.Contains(ranked, p => p.Contains("messages_fts"));
            Assert.DoesNotContain(ranked, p => p.Contains("ix_messages") || p.Contains(" messages ") || p.EndsWith(" messages") || p.Contains(" m "));

            // Which rows a scope holds comes from an index that has everything asked of it: again no row is read.
            var scope = new RetrievalScope { FromMs = 0, ToMs = Now };
            foreach (string column in new[] { "author_identity_id", "conversation_id" })
            {
                var plan = c.Query(null, "EXPLAIN QUERY PLAN " + HybridRetriever.ScopeRowsStatement(column, "$k0,$k1", scope), r => r.GetString(3),
                    ("$k0", "a"), ("$k1", "b"), ("$f", 0L), ("$t", Now));
                Assert.All(plan, p => Assert.Contains("COVERING INDEX", p));
            }
        }

        [Theory]
        [InlineData("author")]
        [InlineData("conversations")]
        [InlineData("messages")]
        [InlineData("everywhere")]
        public async Task FullText_FindsWhatIsInScopeAndInThePeriod_AndNothingElse(string kind)
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.PersonAsync("p-ben", "Ben");
            await corpus.ConversationAsync("discord:c-ana", "dm", "p-ana");
            await corpus.ConversationAsync("discord:c-ben", "dm", "p-ben");
            string old = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 9_000_000, "the cat is asleep");
            string recent = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 1_000_000, "the cat woke up");
            string bens = await corpus.MessageAsync("p-ben", "discord:c-ben", Now - 2_000_000, "my cat ignores me");
            await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 500_000, "train was late again");
            RetrievalScope Scope(long? from = null, long? to = null) => kind switch
            {
                "author" => new RetrievalScope { AuthorIdentityIds = new[] { CorpusBuilder.Identity("p-ana") }, FromMs = from, ToMs = to ?? Now, Limit = 10 },
                "conversations" => new RetrievalScope { ConversationIds = new[] { "discord:c-ana" }, FromMs = from, ToMs = to ?? Now, Limit = 10 },
                "messages" => new RetrievalScope { MessageIds = new[] { old, recent }, FromMs = from, ToMs = to ?? Now, Limit = 10 },
                _ => new RetrievalScope { FromMs = from, ToMs = to ?? Now, Limit = 10 },
            };
            // A fresh retriever per search: the rows of a scope are kept for a few minutes, and each of these is another scope.
            HybridRetriever Retriever() => new(h.Stores, h.Kit.Corpus, () => null);

            var (all, _) = await Retriever().SearchAsync(new[] { "cat" }, Scope(), CancellationToken.None);
            var expected = kind == "everywhere" ? new[] { old, recent, bens } : new[] { old, recent };
            Assert.Equal(expected.OrderBy(x => x), all.Select(r => r.MessageId).OrderBy(x => x));

            var (later, _) = await Retriever().SearchAsync(new[] { "cat" }, Scope(from: Now - 3_000_000), CancellationToken.None);
            Assert.Equal((kind == "everywhere" ? new[] { recent, bens } : new[] { recent }).OrderBy(x => x), later.Select(r => r.MessageId).OrderBy(x => x));

            var (earlier, _) = await Retriever().SearchAsync(new[] { "cat" }, Scope(to: Now - 5_000_000), CancellationToken.None);
            Assert.Equal(new[] { old }, earlier.Select(r => r.MessageId));
        }

        [Fact]
        public async Task TheSameScope_IsReadOnce_ForAllTheQuestionsAskedOfIt()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (cats, tabby, train) = await AnaAsync(h);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[]
            {
                new VectorItem(cats, FakeEncoder.Embed("my cat knocked the plant over again"), 1),
                new VectorItem(tabby, FakeEncoder.Embed("cat"), 1),
            });
            var (first, coverage) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, AnasOwn(), CancellationToken.None);
            Assert.Equal(2, coverage.Candidates);

            // A vector filed after the scope was first read is not seen by the next question of the same scope (an inquiry's
            // many searches read its candidates once), and is by a search of another scope.
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[] { new VectorItem(train, FakeEncoder.Embed("train was late again"), 1) });
            var (_, again) = await h.Kit.Retriever.SearchAsync(new[] { "plant" }, AnasOwn(), CancellationToken.None);
            Assert.Equal(2, again.Candidates);
            var (_, other) = await h.Kit.Retriever.SearchAsync(new[] { "plant" }, AnasOwn() with { FromMs = 1 }, CancellationToken.None);
            Assert.Equal(3, other.Candidates);
            Assert.NotEmpty(first);
        }

        [Fact]
        public void FtsQuery_SplitsWordsWhereTheTokenizerDoes_AndLeavesOutWordsThatAreTooCommon()
        {
            // "don't" is the word "don" (the tokenizer splits at the apostrophe), never a phrase ending in the prefix t*.
            Assert.Equal("\"don\" OR \"dons\" OR \"care\" OR \"cares\" OR \"football\"*",
                HybridRetriever.FtsQuery("I don't care about football", t => t == "\"about\"*"));
            Assert.Equal("\"don\" OR \"dons\" OR \"stop\" OR \"stops\"", HybridRetriever.FtsQuery("don\u2019t stop"));
            // A common word goes with its plural.
            Assert.Equal("\"cats\"", HybridRetriever.FtsQuery("do you like cats", t => t is "\"do\"" or "\"you\"" or "\"like\""));
            // Nothing left: no full-text query at all. A quoted phrase is kept as it is.
            Assert.Null(HybridRetriever.FtsQuery("is it", _ => true));
            Assert.Equal("\"the end\"", HybridRetriever.FtsQuery("\"the end\" is near", _ => true));
        }

        [Fact]
        public async Task FullText_LeavesOutWordsTooManyMessagesContain()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.ConversationAsync("discord:c-ana", "dm", "p-ana");
            for (int i = 0; i < 6; i++) await corpus.MessageAsync("p-ana", "discord:c-ana", Now - (10 - i) * 60_000L, "lol the weather " + i);
            string football = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 60_000, "lol football tonight");
            // Seven messages, a floor of three: "lol" is in all seven, so it is too common to search for.
            var retriever = new HybridRetriever(h.Stores, h.Kit.Corpus, () => null) { CommonTermFloor = 3 };

            var (hits, _) = await retriever.SearchAsync(new[] { "lol football" }, AnasOwn(10), CancellationToken.None);
            Assert.Equal(new[] { football }, hits.Select(x => x.MessageId).ToArray());

            var (none, _) = await retriever.SearchAsync(new[] { "lol" }, AnasOwn(10), CancellationToken.None);
            Assert.Empty(none);
        }

        [Fact]
        public void QueryIn_BindsTheExtraParametersOnEveryChunk()
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            c.Exec(null, "CREATE TABLE t(k TEXT, g TEXT)");
            foreach (var (k, g) in new[] { ("a", "x"), ("b", "x"), ("c", "y"), ("d", "x") }) c.Exec(null, "INSERT INTO t VALUES($k,$g)", ("$k", k), ("$g", g));

            var rows = c.QueryIn(null, "SELECT k FROM t WHERE g=$g AND k IN ({keys}) ORDER BY k", new[] { "a", "b", "c", "d" }, r => r.GetString(0), 1, ("$g", "x"));

            Assert.Equal(new[] { "a", "b", "d" }, rows);
        }
    }
}
