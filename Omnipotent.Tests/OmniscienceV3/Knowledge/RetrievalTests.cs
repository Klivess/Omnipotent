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
        public async Task FullText_ReadsNoMessageRow_ToFindAWordsMatchesOrToTellWhetherTheyAreInScope()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            using var c = h.Stores.Corpus.OpenRead();

            // A word's matches come from the full-text index alone: the messages table is not in the plan at all. (The
            // first statement joined every match to its row before sorting; on the server that was over a million row
            // reads for one query, and two tactic runs died in it, 20 Nov 2026.)
            var matches = c.Query(null, "EXPLAIN QUERY PLAN " + HybridRetriever.TermMatches, r => r.GetString(3), ("$q", "\"cat\""), ("$l", 10L));
            Assert.Contains(matches, p => p.Contains("messages_fts"));
            Assert.DoesNotContain(matches, p => p.Contains("ix_messages") || p.Contains(" messages ") || p.EndsWith(" messages") || p.Contains(" m "));
            // Nor are they asked for best first. To put its matches in order the index scores every one of them in the
            // archive, and a score needs the message's length: a lookup in a table of its own for each match, which was
            // where an inquiry's hour went (22 Nov 2026). The matches are ranked in memory, those in scope only.
            Assert.DoesNotContain("ORDER BY", HybridRetriever.TermMatches, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rank", HybridRetriever.TermMatches, StringComparison.OrdinalIgnoreCase);

            // Which rows a scope holds comes from an index that has everything asked of it, in the order asked: again no
            // row is read, and nothing is sorted.
            var scope = new RetrievalScope { FromMs = 0, ToMs = Now };
            foreach (string column in new[] { "author_identity_id", "conversation_id" })
            {
                var plan = c.Query(null, "EXPLAIN QUERY PLAN " + HybridRetriever.NewestOfOneStatement(column, scope), r => r.GetString(3),
                    ("$k", "a"), ("$f", 0L), ("$t", Now), ("$l", 5_000));
                Assert.All(plan, p => Assert.Contains("COVERING INDEX", p));
                Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
            }
        }

        /// <summary>
        /// 22 Nov 2026: the runs that did finish on the server took four to five hours, most of it in the gather stage. A
        /// facet's questions were asked of three scopes one after the other, and each time every question was run against
        /// the whole full-text index and its text encoded again.
        /// </summary>
        [Fact]
        public async Task ScopesAskedTheSameQuestionsTogether_ReadEachWordOnce_AndEncodeTheQuestionsOnce()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (cats, tabby, train) = await AnaAsync(h);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[]
            {
                new VectorItem(cats, FakeEncoder.Embed("my cat knocked the plant over again"), 1),
                new VectorItem(tabby, FakeEncoder.Embed("cat"), 1),
                new VectorItem(train, FakeEncoder.Embed("train was late again"), 1),
            });
            var own = AnasOwn();
            var theirs = new RetrievalScope { ConversationIds = new[] { "discord:c-ana" }, ToMs = Now, Limit = 5 };
            var reacted = new RetrievalScope { MessageIds = new[] { cats, train }, ToMs = Now, Limit = 5 };
            var retriever = h.Kit.Retriever;

            // Each scope asked on its own gives what the three give when asked together.
            var apart = new List<IReadOnlyList<RetrievedMessage>>();
            foreach (var scope in new[] { own, theirs, reacted })
                apart.Add((await new HybridRetriever(h.Stores, h.Kit.Corpus, () => encoder).SearchAsync(new[] { "cat", "the plant" }, scope, CancellationToken.None)).Results);

            // Eight facets, as an inquiry plans at most, each with two wordings.
            for (int facet = 0; facet < 8; facet++)
            {
                var together = await retriever.SearchScopesAsync(new[] { "cat", "the plant" }, new[] { own, theirs, reacted }, CancellationToken.None);
                Assert.Equal(3, together.Count);
                for (int s = 0; s < 3; s++)
                    Assert.Equal(apart[s].Select(r => (r.MessageId, r.FtsRank, r.VectorRank)), together[s].Results.Select(r => (r.MessageId, r.FtsRank, r.VectorRank)));
                Assert.Contains(together[0].Results, r => r.MessageId == tabby && r.VectorRank == 1 && r.FtsRank == null);
                Assert.Contains(together[1].Results, r => r.MessageId == cats && r.FtsRank == 1);
                Assert.DoesNotContain(together[2].Results, r => r.MessageId == tabby);
                Assert.Equal((3, 3, 2), (together[0].Coverage.Candidates, together[1].Coverage.Candidates, together[2].Coverage.Candidates));
            }

            // Twenty-four scope searches of two questions each. The words "cat", "cats", "the", "plant" (and their plain
            // plurals) were each read from the full-text index once, the two questions encoded in one go, and each
            // scope's vectors read once.
            Assert.InRange(retriever.TermReads, 4, 6);
            Assert.Equal(1, retriever.QueryEncodings);
            Assert.Equal(3, retriever.CandidateLoads);
        }

        [Fact]
        public async Task AMatchHoldingMoreOfTheQuestionsWords_StandsAboveOneHoldingFewer_AndTheRarerWordCountsForMore()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.ConversationAsync("discord:c-ana", "dm", "p-ana");
            string older = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 9_000_000, "chess tonight");
            string both = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 8_000_000, "I really dislike chess, far too slow for me");
            string newer = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 7_000_000, "chess club was fun");
            string rare = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 6_000_000, "I dislike mondays");
            await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 5_000_000, "train was late again");
            var retriever = new HybridRetriever(h.Stores, h.Kit.Corpus, () => null);

            var (hits, _) = await retriever.SearchAsync(new[] { "dislike chess" }, AnasOwn(10), CancellationToken.None);

            // Both words; then the word fewer messages hold ("dislike", two of them, against three for "chess"); then the
            // commoner word, the newer message first.
            Assert.Equal(new[] { both, rare, newer, older }, hits.Select(x => x.MessageId).ToArray());
            Assert.Equal(new int?[] { 1, 2, 3, 4 }, hits.Select(x => x.FtsRank).ToArray());
        }

        [Fact]
        public async Task AScopesRows_AreListedNewestFirstAFewAtATime_AndAllOfThemAreSearched()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.PersonAsync("p-ben", "Ben");
            await corpus.ConversationAsync("discord:c-one", "dm", "p-ana");
            await corpus.ConversationAsync("discord:c-two", "dm", "p-ben");
            var ids = new List<string>();
            for (int i = 0; i < 12; i++)
                ids.Add(await corpus.MessageAsync(i % 3 == 0 ? "p-ben" : "p-ana", i % 3 == 0 ? "discord:c-two" : "discord:c-one", Now - (20 - i) * 60_000L, $"garden note {i}"));
            // Two rows of each conversation at first, then the rest of each: eight and four in all.
            var retriever = new HybridRetriever(h.Stores, h.Kit.Corpus, () => null) { FirstPassRows = 2 };
            var scope = new RetrievalScope { ConversationIds = new[] { "discord:c-one", "discord:c-two" }, ToMs = Now, Limit = 20 };

            var (hits, coverage) = await retriever.SearchAsync(new[] { "garden" }, scope, CancellationToken.None);

            Assert.Equal(ids.OrderBy(x => x), hits.Select(x => x.MessageId).OrderBy(x => x));
            Assert.DoesNotContain("time allowed", coverage.Note ?? "");
            // One word in every message: equals, so the newest first.
            Assert.Equal(Enumerable.Reverse(ids), hits.Select(x => x.MessageId));
        }

        /// <summary>
        /// A word's matches are kept between searches, and a standing question is asked of the messages of the last
        /// minutes: what is kept has to hold the messages that came after it was read.
        /// </summary>
        [Fact]
        public async Task AWordsKeptMatches_HoldTheMessagesThatArrivedSinceTheyWereRead()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.ConversationAsync("discord:c-ana", "dm", "p-ana");
            string cats = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 3_600_000, "my cat knocked the plant over again");
            await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 2_000_000, "train was late again");
            var retriever = new HybridRetriever(h.Stores, h.Kit.Corpus, () => null);
            var (before, _) = await retriever.SearchAsync(new[] { "cat" }, AnasOwn() with { ToMs = null }, CancellationToken.None);
            Assert.Equal(new[] { cats }, before.Select(r => r.MessageId).ToArray());
            int reads = retriever.TermReads;

            string arrived = await corpus.MessageAsync("p-ana", "discord:c-ana", Now - 1_000, "the cat is on the keyboard");
            var (among, _) = await retriever.SearchAsync(new[] { "cat" }, new RetrievalScope { MessageIds = new[] { arrived }, Limit = 3 }, CancellationToken.None);

            Assert.Equal(new[] { arrived }, among.Select(r => r.MessageId).ToArray());
            // Only what arrived was looked up: the word was not read from the start again.
            Assert.Equal(reads, retriever.TermReads);
        }

        /// <summary>
        /// A search that has run out of time ends with what it has and says what it left out. With no time at all it still
        /// takes one step of everything: the first account or conversation of the scope, the first wording of the question.
        /// </summary>
        [Fact]
        public async Task WithNoTimeLeft_ASearchStillLooksAtTheFirstOfItsScope_AndSaysWhatItLeftOut()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.PersonAsync("p-ben", "Ben");
            await corpus.ConversationAsync("discord:c-one", "dm", "p-ana");
            await corpus.ConversationAsync("discord:c-two", "dm", "p-ben");
            string one = await corpus.MessageAsync("p-ana", "discord:c-one", Now - 3_000_000, "the cat is asleep");
            string two = await corpus.MessageAsync("p-ben", "discord:c-two", Now - 2_000_000, "my cat ignores me");
            string plant = await corpus.MessageAsync("p-ana", "discord:c-one", Now - 1_000_000, "watered the plant");
            var encoder = new FakeEncoder();
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[] { new VectorItem(one, FakeEncoder.Embed("cat"), 1), new VectorItem(two, FakeEncoder.Embed("cat"), 1) });
            var retriever = new HybridRetriever(h.Stores, h.Kit.Corpus, () => encoder);
            var scope = new RetrievalScope { ConversationIds = new[] { "discord:c-one", "discord:c-two" }, ToMs = Now, Limit = 10 };

            var spent = (await retriever.SearchScopesAsync(new[] { "cat", "plant" }, new[] { scope }, CancellationToken.None, new RetrievalBudget(TimeSpan.Zero)))[0];

            // The first conversation and the first wording, by full text.
            Assert.Equal(new[] { one }, spent.Results.Select(r => r.MessageId).ToArray());
            Assert.False(spent.Coverage.Vectors);
            Assert.Contains("only the newest of them were searched", spent.Coverage.Note);
            Assert.Contains("before every wording of the search had been tried", spent.Coverage.Note);
            Assert.Contains("message vectors were read", spent.Coverage.Note);

            // What a search could not finish is not kept as if it were the scope: the next one, given time, reads it all.
            var whole = (await retriever.SearchScopesAsync(new[] { "cat", "plant" }, new[] { scope }, CancellationToken.None, new RetrievalBudget(TimeSpan.FromMinutes(5))))[0];
            Assert.Equal(new[] { one, two, plant }.OrderBy(x => x), whole.Results.Select(r => r.MessageId).OrderBy(x => x));
            Assert.True(whole.Coverage.Vectors);
            Assert.Null(whole.Coverage.Note);
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

        /// <summary>
        /// 22 Nov 2026: every inquiry on the server was stopped in its gather stage after an hour, three attempts each. A
        /// gather searches three scopes for each facet in turn (the subject's own messages, their conversations, what they
        /// reacted to), and the retriever kept the vector candidates of the last scope only: each search read thousands of
        /// rows and vectors again, because the search before it had been of another scope.
        /// </summary>
        [Fact]
        public async Task ScopesSearchedInTurn_AreEachReadOnce_HoweverOftenTheSearchGoesRoundThem()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (cats, tabby, train) = await AnaAsync(h);
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, new[]
            {
                new VectorItem(cats, FakeEncoder.Embed("my cat knocked the plant over again"), 1),
                new VectorItem(tabby, FakeEncoder.Embed("cat"), 1),
                new VectorItem(train, FakeEncoder.Embed("train was late again"), 1),
            });
            var own = AnasOwn();
            var theirs = new RetrievalScope { ConversationIds = new[] { "discord:c-ana" }, ToMs = Now, Limit = 5 };
            var reacted = new RetrievalScope { MessageIds = new[] { cats, train }, ToMs = Now, Limit = 5 };

            // Eight facets, as an inquiry plans at most.
            for (int facet = 0; facet < 8; facet++)
            {
                var (mine, _) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, own, CancellationToken.None);
                // A counter-evidence search is the same scope with another limit.
                await h.Kit.Retriever.SearchAsync(new[] { "dog" }, own with { Limit = 8 }, CancellationToken.None);
                var (around, covTheirs) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, theirs, CancellationToken.None);
                var (liked, covReacted) = await h.Kit.Retriever.SearchAsync(new[] { "cat" }, reacted, CancellationToken.None);
                Assert.Contains(mine, r => r.MessageId == tabby && r.VectorRank == 1);
                Assert.Contains(around, r => r.MessageId == tabby && r.VectorRank == 1);
                Assert.Equal((3, 2), (covTheirs.Candidates, covReacted.Candidates));
                Assert.DoesNotContain(liked, r => r.MessageId == tabby);
            }

            // Thirty-two searches, three reads (twenty-four before).
            Assert.Equal(3, h.Kit.Retriever.CandidateLoads);
            // And the two questions were each encoded once, where every search encoded its own again.
            Assert.Equal(2, h.Kit.Retriever.QueryEncodings);
        }

        [Fact]
        public async Task TheNewestOfAScope_AreTakenFromEachAccountOrConversationInTurn_AndNoneIsLeftOut()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("p-ana", "Ana");
            await corpus.PersonAsync("p-ben", "Ben");
            await corpus.ConversationAsync("discord:c-one", "dm", "p-ana");
            await corpus.ConversationAsync("discord:c-two", "dm", "p-ben");
            var encoder = new FakeEncoder();
            h.Kit.Encoder = encoder;
            // Two conversations, written to in turn; the newest four are two of each.
            var ids = new List<(string Id, long At)>();
            for (int i = 0; i < 6; i++)
            {
                long at = Now - (6 - i) * 60_000L;
                ids.Add((await corpus.MessageAsync(i % 2 == 0 ? "p-ana" : "p-ben", i % 2 == 0 ? "discord:c-one" : "discord:c-two", at, $"note number {i} about the garden"), at));
            }
            await h.Context.Vectors!.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion, ids.Select(x => new VectorItem(x.Id, FakeEncoder.Embed("garden"), 1)).ToList());

            var scope = new RetrievalScope { ConversationIds = new[] { "discord:c-one", "discord:c-two" }, ToMs = Now, Limit = 10, VectorCandidates = 4 };
            var (results, coverage) = await h.Kit.Retriever.SearchAsync(new[] { "zebra" }, scope, CancellationToken.None);
            Assert.Equal(4, coverage.Candidates);
            Assert.Equal(ids.OrderByDescending(x => x.At).Take(4).Select(x => x.Id).OrderBy(x => x), results.Select(r => r.MessageId).OrderBy(x => x));
            // Nothing after the scope's period is a candidate.
            var (early, _) = await h.Kit.Retriever.SearchAsync(new[] { "zebra" }, scope with { ToMs = ids[2].At }, CancellationToken.None);
            Assert.Equal(ids.Take(3).Select(x => x.Id).OrderBy(x => x), early.Select(r => r.MessageId).OrderBy(x => x));

            // Each account's or conversation's newest rows come off the index in order: no row is read and nothing is sorted.
            using var c = h.Stores.Corpus.OpenRead();
            foreach (string column in new[] { "author_identity_id", "conversation_id" })
            {
                var plan = c.Query(null, "EXPLAIN QUERY PLAN " + HybridRetriever.NewestOfOneStatement(column, new RetrievalScope { FromMs = 0, ToMs = Now }), r => r.GetString(3),
                    ("$k", "a"), ("$f", 0L), ("$t", Now), ("$l", 10));
                Assert.All(plan, p => Assert.Contains("COVERING INDEX", p));
                Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
            }
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
