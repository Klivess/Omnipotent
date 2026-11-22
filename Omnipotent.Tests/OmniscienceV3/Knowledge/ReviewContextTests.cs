using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// Review items say what they are about by name. On 22 Nov 2026 the Review page showed an alias item as its stored
    /// detail: { "aliasId": "al_6ff4…", "scope": "guild:discord:7565…", … }. Klives: "It should never be a lone ID shown.
    /// It should always be something clickable, something interactable and user friendly. Humans cannot recognise IDs."
    /// Each item now carries its context: the people by name, the server or chat by name, the messages it rests on with
    /// who said them, and the thing to decide. Invented people and text only.
    /// </summary>
    public class ReviewContextTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L, Minute = 60_000L;
        private const string Me = "me", Dee = "dee", Bo = "bo", DeeToo = "dee-2";
        private const string Guild = "discord:g-climb", General = "discord:c-general", Group = "discord:c-group";

        private static async Task<(string Booky, string Seventeen, string Thirty)> SeedAsync(KnowledgeHarness h)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync(Me, "Klives", self: true);
            await corpus.PersonAsync(Dee, "Dee Marsh");
            await corpus.PersonAsync(Bo, "Bo Reed");
            await corpus.PersonAsync(DeeToo, "Dee M");
            await corpus.ConversationAsync(Group, "group_dm", Me, Dee, Bo);
            await h.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,'discord','Climbing Club',0,$t)", ("$g", Guild), ("$t", Now));
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($c,'discord','text',$g,'general',0)", ("$c", General), ("$g", Guild));
                c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'username','deemarsh',0,'import')", ("$i", CorpusBuilder.Identity(Dee)));
                // A group chat has no title of its own (the builder gives every non-direct chat one).
                c.Exec(tx, "UPDATE conversations SET title=NULL WHERE conversation_id=$c", ("$c", Group));
            });
            long t = Now - 3 * Day;
            string booky = await corpus.MessageAsync(Bo, General, t, "Booky are you coming to the wall tonight?");
            await h.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE messages SET guild_id=$g WHERE message_id=$m", ("$g", Guild), ("$m", booky)));
            string seventeen = await corpus.MessageAsync(Dee, Group, t + Minute, "I'm 17 and already tired of school");
            string thirty = await corpus.MessageAsync(Dee, Group, t + 2 * Minute, "I'm 30 lol, kidding");
            return (booky, seventeen, thirty);
        }

        private static Task AliasAsync(KnowledgeHarness h, string id, string alias, string scopeKind, string scopeId, string speaker, string message) =>
            h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_aliases(alias_id, person_id, alias, alias_norm, name_kind, scope_kind, scope_id, valid_from_ms, valid_to_ms, first_seen_ms, last_seen_ms,
                    usages, speakers, self_claims, probability, status, evidence_json, common_word, updated_at_ms, source_seq)
                VALUES($id,$p,$a,$n,'nickname',$sk,$si,$t,NULL,$t,$t,1,1,0,0.8,'accepted',$ev,0,$t,1)
                """, ("$id", id), ("$p", Dee), ("$a", alias), ("$n", alias.ToLowerInvariant()), ("$sk", scopeKind), ("$si", scopeId), ("$t", Now - 3 * Day),
                ("$ev", JsonSerializer.Serialize(new[] { new { messageId = message, version = 1, quote = alias, authorPersonId = speaker, sentAtMs = Now - 3 * Day } }))));

        /// <summary>The open items as the review route serves them.</summary>
        private static IReadOnlyList<KnowledgeReviewItemDto> Page(KnowledgeHarness h)
        {
            var (items, _) = new ReviewQueue(h.Stores, h.Clock).Page("open", null, null, byAge: false, null, 100);
            return new ReviewContexts(h.Kit, h.Stores).Dtos(items);
        }

        [Fact]
        public async Task AnAlias_IsNamed_WithTheServerByName_WhoUsedIt_AndHowItStands()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (booky, _, _) = await SeedAsync(h);
            await AliasAsync(h, "al_server", "Booky", "guild", Guild, Bo, booky);
            await AliasAsync(h, "al_group", "Marshy", "conversation", Group, Bo, booky);
            var review = new ReviewQueue(h.Stores, h.Clock);
            await review.RaiseAsync(ReviewKinds.Alias, Dee, "k-server", "Confirm alias \"Booky\" (nickname) before it is used for alerts",
                new { aliasId = "al_server", alias = "Booky", scope = "guild:" + Guild, usages = 1, speakers = 1 }, 0.3, 1, CancellationToken.None);
            await review.RaiseAsync(ReviewKinds.Alias, Dee, "k-group", "Confirm alias \"Marshy\" (nickname) before it is used for alerts",
                new { aliasId = "al_group", alias = "Marshy", scope = "conversation:" + Group, usages = 1, speakers = 1 }, 0.2, 1, CancellationToken.None);

            var page = Page(h);
            var server = page.Single(i => i.Title.Contains("Booky")).Context!;
            Assert.NotNull(server.Alias);
            Assert.Equal("al_server", server.Alias!.AliasId);
            Assert.Equal("Booky", server.Alias.Alias);
            Assert.Equal("Climbing Club", server.Alias.PlaceName);
            Assert.Equal("server", server.Alias.PlaceKind);
            Assert.Equal("accepted", server.Alias.Status);
            Assert.Equal("Dee Marsh", server.People[Dee]);
            Assert.Equal("Bo Reed", server.People[Bo]);
            var said = Assert.Single(server.Messages);
            Assert.Equal("Bo Reed", said.AuthorName);
            Assert.Equal("Climbing Club", said.GuildName);
            Assert.Equal("general", said.ConversationTitle);
            Assert.Contains("Booky are you coming", said.Text);

            // A group chat has no name of its own: it is called by who is in it (Klives left out).
            var group = page.Single(i => i.Title.Contains("Marshy")).Context!;
            Assert.Equal("group", group.Alias!.PlaceKind);
            Assert.Equal("Bo Reed, Dee Marsh", group.Alias.PlaceName);
        }

        [Fact]
        public async Task TwoAccounts_AreNamedByPlatformNameAndHandle_EitherWayRound_AndAForgottenOneByItsPlatformOnly()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            await SeedAsync(h);
            string a = CorpusBuilder.Identity(Dee), b = CorpusBuilder.Identity(DeeToo), gone = "discord:u-gone";
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Link(string id, string ia, string ib, string pb) => c.Exec(tx, """
                    INSERT INTO kn_identity_links(link_id, identity_a, identity_b, person_a, person_b, signals_json, independent_signals, probability, verdict, created_at_ms, updated_at_ms, source_seq)
                    VALUES($id,$a,$b,$pa,$pb,$s,2,0.81,'suggested',$t,$t,1)
                    """, ("$id", id), ("$a", ia), ("$b", ib), ("$pa", Dee), ("$pb", pb), ("$t", Now),
                    ("$s", """[{"kind":"same_name","strength":0.7,"detail":"Both call themselves Dee."},{"kind":"shared_contacts","strength":0.5,"detail":"They talk to the same people."}]"""));
                Link("lk-1", a, b, DeeToo);
                Link("lk-2", a, gone, "someone-gone");
            });
            var review = new ReviewQueue(h.Stores, h.Clock);
            // The detail names the pair the other way round from the stored link: it is found all the same.
            await review.RaiseAsync(ReviewKinds.IdentityLink, Dee, "k-link", "These two accounts may be the same person",
                new { identityA = b, identityB = a, probability = 0.81, signals = new[] { "same_name", "shared_contacts" } }, 0.81, 1, CancellationToken.None);
            await review.RaiseAsync(ReviewKinds.IdentityLink, Dee, "k-gone", "These two accounts may be the same person",
                new { identityA = a, identityB = gone, probability = 0.6, signals = new[] { "same_name" } }, 0.6, 1, CancellationToken.None);

            var page = Page(h);
            var link = page.Single(i => i.Priority > 0.7).Context!.Link!;
            Assert.Equal("lk-1", link.LinkId);
            Assert.Equal("suggested", link.Verdict);
            Assert.Equal(("discord", "Dee Marsh", "deemarsh", Dee), (link.A.Platform, link.A.Name, link.A.Handle, link.A.PersonId));
            Assert.Equal(("discord", "Dee M", (string?)null, DeeToo), (link.B.Platform, link.B.Name, link.B.Handle, link.B.PersonId));
            Assert.Equal(new[] { "same_name", "shared_contacts" }, link.Signals.Select(s => s.Kind).ToArray());
            var named = page.Single(i => i.Priority > 0.7).Context!.People;
            Assert.Equal("Dee Marsh", named[Dee]);
            Assert.Equal("Dee M", named[DeeToo]);

            var forgotten = page.Single(i => i.Priority < 0.7).Context!.Link!;
            Assert.Equal("discord", forgotten.B.Platform);
            Assert.Null(forgotten.B.Name);

            // The "Same person?" list names its accounts the same way.
            var accounts = new ReviewContexts(h.Kit, h.Stores).Accounts(new[] { a, b, gone });
            Assert.Equal("Dee Marsh", accounts[a].Name);
            Assert.False(accounts.ContainsKey(gone));
        }

        [Fact]
        public async Task AgeStatements_AreQuoted_WithWhoSaidThemWhere_SoEachCanBeConfirmedOrRejected()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (_, seventeen, thirty) = await SeedAsync(h);
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Age(string id, string message, long at) => c.Exec(tx, """
                    INSERT INTO kn_age_evidence(evidence_id, person_id, kind, source_kind, asserted_at_ms, message_id, message_version, probability, status, natural_key, recorded_at_ms, source_seq)
                    VALUES($id,$p,'age_now','self_disclosure',$t,$m,1,0.9,'accepted',$id,$t,1)
                    """, ("$id", id), ("$p", Dee), ("$t", at), ("$m", message));
                Age("age-17", seventeen, Now - 3 * Day + Minute);
                Age("age-30", thirty, Now - 3 * Day + 2 * Minute);
            });
            await new ReviewQueue(h.Stores, h.Clock).RaiseAsync(ReviewKinds.AgeConflict, Dee, "k-age", "Age statements that cannot all be true",
                new { band = "Unknown", inconsistent = new[] { new { evidenceId = "age-17", kind = "age_now", messageId = seventeen, assertedAtMs = Now }, new { evidenceId = "age-30", kind = "age_now", messageId = thirty, assertedAtMs = Now } } },
                0.9, 1, CancellationToken.None);

            var ctx = Page(h).Single().Context!;
            Assert.Equal(new[] { "age-17", "age-30" }, ctx.Age!.Select(s => s.EvidenceId).ToArray());
            Assert.Contains("I'm 17", ctx.Age![0].Quote);
            Assert.Equal("accepted", ctx.Age[0].Status);
            Assert.Equal(2, ctx.Messages.Count);
            Assert.All(ctx.Messages, m => Assert.Equal("Dee Marsh", m.AuthorName));
            Assert.Equal("Dee Marsh", ctx.People[Dee]);
        }

        [Fact]
        public async Task AFactItem_OpensTheVersionThatStandsNow_AndAHypothesisQuotesWhatItCites()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var (booky, _, _) = await SeedAsync(h);
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string value, long? supersededAt) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, superseded_at_ms, status, modality,
                        source_kind, module_id, module_version, source_seq)
                    VALUES($i,'nk-city','person',$s,'city','state',$j,$v,$t,$sup,'accepted','literal','self_disclosure','test',1,1)
                    """, ("$i", id), ("$s", Dee), ("$j", JsonSerializer.Serialize(value)), ("$v", value), ("$t", Now - (supersededAt == null ? Day : 2 * Day)), ("$sup", supersededAt));
                Fact("as-city-1", "Bristol", Now - Day);
                Fact("as-city-2", "Leeds", null);
            });
            var review = new ReviewQueue(h.Stores, h.Clock);
            await review.RaiseAsync(ReviewKinds.Flapping, Dee, "nk-city", "City keeps changing: \"Leeds\"", new { attribute = "city", value = "Leeds" }, 0.5, 1, CancellationToken.None);
            await review.RaiseAsync(ReviewKinds.PossibleChange, Dee, "nk-gone", "Possible change since 2026-11-01: city \"York\" (you confirmed \"Leeds\")",
                new { attribute = "city", confirmed = "Leeds", newer = "York", sinceMs = Now - 20 * Day }, 0.7, 1, CancellationToken.None);
            await review.RaiseAsync(ReviewKinds.Hypothesis, Dee, "hy-1", "Hypothesis confirmed: climbs at the wall every week",
                new { hypothesisId = "hy-1", status = "confirmed", note = "Said twice in a month.", messages = new[] { booky } }, 0.4, 1, CancellationToken.None);

            var page = Page(h);
            var flapping = page.Single(i => i.Kind == ReviewKinds.Flapping).Context!.Fact!;
            Assert.Equal("as-city-2", flapping.AssertionId);
            Assert.Equal("Leeds", flapping.Value);
            Assert.Equal(Attributes.Get("city").Title, flapping.AttributeTitle);

            // A fact that is no longer stored still says what is in question, and has nothing to open.
            var change = page.Single(i => i.Kind == ReviewKinds.PossibleChange).Context!.Fact!;
            Assert.Null(change.AssertionId);
            Assert.Equal(new[] { "Leeds", "York" }, change.Values.ToArray());

            var cited = Assert.Single(page.Single(i => i.Kind == ReviewKinds.Hypothesis).Context!.Messages);
            Assert.Equal("Bo Reed", cited.AuthorName);
            Assert.Equal("Climbing Club", cited.GuildName);
        }
    }
}
