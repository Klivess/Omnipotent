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

        [Theory]
        [InlineData("author")]
        [InlineData("conversations")]
        [InlineData("messages")]
        public async Task FullText_TheIndexDrivesTheStatement_WhateverTheScope(string kind)
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var scope = kind switch
            {
                "author" => new RetrievalScope { AuthorIdentityIds = new[] { CorpusBuilder.Identity("p-ana") }, ToMs = Now },
                "conversations" => new RetrievalScope { ConversationIds = new[] { "discord:c-ana" }, FromMs = 0, ToMs = Now },
                _ => new RetrievalScope { MessageIds = new[] { "discord:1", "discord:2" }, ToMs = Now },
            };
            var (sql, args) = HybridRetriever.FullTextStatement("\"cat\" OR \"cats\"", scope, 42);
            using var c = h.Stores.Corpus.OpenRead();

            var plan = c.Query(null, "EXPLAIN QUERY PLAN " + sql, r => r.GetString(3), args);

            // The outer loop reads the full-text index once; each match is then looked up in messages by rowid. Never a walk over
            // the scope's messages through one of their indexes with the match run again per message.
            Assert.Contains("messages_fts", plan[0]);
            Assert.Contains(plan, p => p.Contains("INTEGER PRIMARY KEY"));
            Assert.DoesNotContain(plan, p => p.Contains("ix_messages"));
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
