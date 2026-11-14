using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Plans;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Tests.OmniscienceV3.Knowledge;
using Omnipotent.Tests.OmniscienceV3.Replica;
using Omnipotent.Tests.OmniscienceV3.WsC;

namespace Omnipotent.Tests.OmniscienceV3.Integration
{
    /// <summary>
    /// What the workstreams left for the merge to connect (plan Phase 3), each tested where they meet: ingest honours the
    /// forgotten (WS-A ← WS-C), the knowledge layer and replicas honour analysis-off (WS-B, WS-D ← WS-C), forgetting someone
    /// takes the knowledge that rested on their words (WS-C → WS-B), the knowledge layer reads the classifier (WS-B ← WS-C),
    /// and its reminders and standing-question flips are notices (WS-B → WS-C). All people and messages are synthetic.
    /// </summary>
    public class MergeWiringTests
    {
        private static long Count(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<long>(null, sql, args);
        }

        private static List<string> Rows(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Query(null, sql, r => r.GetString(0), args);
        }

        // ── WS-A ← WS-C: the corpus writer stores nothing of a forgotten person ──────────────────────────────────────

        [Fact]
        public async Task CorpusWriter_StoresNothingOfAForgottenPerson_AndCountsWhatItLeftOut()
        {
            string root = Path.Combine(Path.GetTempPath(), "omniv3_wiring_" + Guid.NewGuid().ToString("N"));
            var clock = new FixedClock(Instant.FromUtc(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
            var stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            try
            {
                var source = new IngestSource("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
                var gone = new ForgottenSet(new(StringComparer.Ordinal) { "testnet:u-gone" }, new(StringComparer.Ordinal), new(StringComparer.Ordinal) { "testnet:dm-gone" });
                var them = new IngestIdentity("testnet", "u-gone");
                var bo = new IngestIdentity("testnet", "u-bo");
                var room = new IngestConversation("testnet", "room", ConversationKinds.GroupDm) { Members = new[] { them, bo } };
                long t = clock.NowMs - 3_600_000;
                IngestMessage Message(string id, IngestIdentity author, IngestConversation conversation) => new()
                {
                    PlatformMessageId = id, Conversation = conversation, Author = author, SentAtMs = t, OrderKey = t, Content = "synthetic " + id,
                };

                var stats = await stores.Corpus.WriteAsync((c, tx) =>
                {
                    using var w = new CorpusWriter(c, tx, source, SourceKinds.Live, clock.NowMs, gone);
                    Assert.False(w.Message(Message("m1", them, room)));                                                                 // by them
                    Assert.False(w.Message(Message("m2", new IngestIdentity("testnet", "u-ada"), new IngestConversation("testnet", "dm-gone", ConversationKinds.Dm)))); // in their direct chat
                    Assert.True(w.Message(Message("m3", new IngestIdentity("testnet", "u-ada"), room) with
                    {
                        Mentions = new[] { new RawMention("user", "testnet:u-gone"), new RawMention("user", "testnet:u-bo") },
                        Reactions = new[] { new IngestReaction("thumbsup", Actor: them), new IngestReaction("thumbsup", Actor: bo) },
                    }));
                    Assert.False(w.ReactionAdded("testnet", "m3", "room", them, "heart", null, t, TimeQuality.Exact));
                    Assert.False(w.Typing(them, "testnet", "room", t));
                    Assert.Equal("testnet:u-gone", w.IdentitySeen(them, t));
                    return w.Complete();
                });

                Assert.Equal(1, stats.Inserted);
                Assert.Equal(5, stats.Forgotten); // two messages, the reaction inside m3, the reaction event, the typing
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM identities WHERE identity_id='testnet:u-gone'"));
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM conversations WHERE conversation_id='testnet:dm-gone'"));
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM conversation_members WHERE identity_id='testnet:u-gone'"));
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM message_mentions WHERE target_id='testnet:u-gone'"));
                Assert.Equal(1, Count(stores.Corpus, "SELECT COUNT(*) FROM message_mentions WHERE target_id='testnet:u-bo'"));
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM reactions WHERE identity_id='testnet:u-gone'"));
                Assert.Equal(1, Count(stores.Corpus, "SELECT COUNT(*) FROM reactions WHERE identity_id='testnet:u-bo'"));
                Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM quarantine")); // not set aside either: that would keep their id
            }
            finally
            {
                await stores.DisposeAsync();
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
            }
        }

        // ── WS-B ← WS-C: what analysis-off people wrote never reaches the knowledge layer ─────────────────────────────

        [Fact]
        public async Task KnowledgeReader_LeavesOutWhatAnalysisOffPeopleWrote()
        {
            await using var h = await SignalsHarness.StartAsync();
            string a1 = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ana, "synthetic words of one person");
            string b1 = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ben, "synthetic words of another");
            Assert.Equal(2, new CorpusReader(h.Runtime.Stores, h.Runtime.Host.Policy).Messages(new[] { a1, b1 }).Count);

            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-ana" }, h.Clock.NowMs));
            var reader = new CorpusReader(h.Runtime.Stores, h.Runtime.Host.Policy);
            Assert.Equal(new[] { b1 }, reader.Messages(new[] { a1, b1 }).Select(m => m.MessageId));
            Assert.Empty(reader.AuthoredBy(new[] { SignalsHarness.Ana }));
            Assert.Equal(new[] { b1 }, reader.ConversationRange(SignalsHarness.General, 0, long.MaxValue).Select(m => m.MessageId));
            Assert.DoesNotContain(reader.Window(SignalsHarness.General, h.Clock.NowMs, 10, 10), m => m.MessageId == a1);
        }

        // ── WS-B ← WS-C: the knowledge layer reads the classifier's modality labels ──────────────────────────────────

        [Fact]
        public async Task ModalityOracle_ReadsTheClassifiersLabels_ForThatVersionOnly()
        {
            await using var h = await SignalsHarness.StartAsync(classification: true);
            string joke = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ana, "oh sure, that went perfectly");
            string ask = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ben, "is it raining over there?");
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(99,'active','test','{}','{}',$t)", ("$t", h.Clock.NowMs));
                c.Exec(tx, "INSERT INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,99,1,'h1',$c,NULL,$t,1)",
                    ("$m", joke), ("$c", "dialogue_act=statement:70|modality=sarcasm:90"), ("$t", h.Clock.NowMs));
                c.Exec(tx, "INSERT INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,99,1,'h2',$c,NULL,$t,1)",
                    ("$m", ask), ("$c", "dialogue_act=question:80|modality=question:85"), ("$t", h.Clock.NowMs));
            });
            var oracle = new ClassifierModalityOracle(h.Runtime.Stores, new LabelTableModalityOracle(h.Runtime.Stores.Intel));

            var sarcasm = oracle.Get(joke, 1)!;
            Assert.Equal(Modality.Sarcasm, sarcasm.Label);
            Assert.Equal(0.9, sarcasm.NonLiteralProbability, 3);
            Assert.Equal(0.3, sarcasm.QuestionProbability, 3); // a statement at 70%
            Assert.Equal(0.8, oracle.Get(ask, 1)!.QuestionProbability, 3);
            // Labels of version 1 say nothing about an edited version 2 (and the labels table has nothing either).
            Assert.Null(oracle.Get(joke, 2));
        }

        // ── WS-C → WS-B: forgetting someone takes the knowledge that rested on their words ───────────────────────────

        [Fact]
        public async Task Forget_TakesTheKnowledgeThatRestedOnTheirMessages_AndHasTheOthersConsolidatedAgain()
        {
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var stores = h.Runtime.Stores;
            await stores.Intel.ApplyMigrationsAsync(KnowledgeSchema.Deduction.Concat(KnowledgeSchema.Identity).Concat(KnowledgeSchema.Timeline).Concat(KnowledgeSchema.Plans).ToList());
            string a1 = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ana, "my synthetic friend moved to synthetic town");
            string b1 = await h.PostAsync(SignalsHarness.General, SignalsHarness.Ben, "yes, I moved to synthetic town");
            long t = h.Clock.NowMs;
            await stores.Intel.WriteAsync((c, tx) =>
            {
                void Claim(string id, string person, params string[] messages)
                {
                    string evidence = "[" + string.Join(",", messages.Select(m => $"{{\"messageId\":\"{m}\",\"quote\":\"synthetic\"}}")) + "]";
                    c.Exec(tx, """
                        INSERT INTO kn_claims(claim_id, run_id, person_id, slice_index, attribute, category, evidence_json, agreement, verdict, asserted_at_ms, recorded_at_ms, source_seq, prompt_version)
                        VALUES($id,'run-1',$p,0,'home_city','location',$e,'single','verified',$t,$t,1,'test/1')
                        """, ("$id", id), ("$p", person), ("$e", evidence), ("$t", t));
                    foreach (string m in messages) c.Exec(tx, "INSERT INTO kn_claim_messages(claim_id, message_id) VALUES($c,$m)", ("$c", id), ("$m", m));
                }
                Claim("c-ben-from-her", "p-ben", a1, b1); // rests partly on her words: goes
                Claim("c-ben-own", "p-ben", b1);         // his own words only: stays
                void Entity(string id, string message) => c.Exec(tx, """
                    INSERT INTO kn_entities(entity_id, owner_person_id, kind, name, name_norm, attributes_json, first_evidence_ms, last_evidence_ms, mentions, evidence_json, status, updated_at_ms, source_seq)
                    VALUES($id,'p-ben','place','Synthetic Town','synthetic town','{}',$t,$t,1,$e,'active',$t,1)
                    """, ("$id", id), ("$e", $"[{{\"messageId\":\"{message}\",\"quote\":\"synthetic\"}}]"), ("$t", t));
                Entity("en-from-her", a1);
                Entity("en-own", b1);
            });

            var data = h.Data!;
            var preview = data.PreviewForget("p-ana");
            await data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default);
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();

            Assert.Equal(1, Count(stores.Intel, "SELECT COUNT(*) FROM kn_claims"));
            Assert.Equal(1, Count(stores.Intel, "SELECT COUNT(*) FROM kn_claims WHERE claim_id='c-ben-own'"));
            Assert.Equal(1, Count(stores.Intel, "SELECT COUNT(*) FROM kn_claim_messages"));
            Assert.Equal(1, Count(stores.Intel, "SELECT COUNT(*) FROM kn_entities WHERE entity_id='en-own'"));
            Assert.Equal(0, Count(stores.Intel, "SELECT COUNT(*) FROM kn_entities WHERE entity_id='en-from-her'"));
            // The person whose facts lost evidence is told to consolidate them again (the knowledge module listens).
            Assert.Equal(1, Count(stores.Corpus, "SELECT COUNT(*) FROM change_log WHERE kind=$k AND entity_id='p-ben'", ("$k", ChangeKinds.EvidenceRemoved)));
            Assert.Equal(0, Count(stores.Corpus, "SELECT COUNT(*) FROM change_log WHERE kind=$k AND entity_id='p-ana'", ("$k", ChangeKinds.EvidenceRemoved)));
        }

        [Fact]
        public async Task Forget_TellsItsHoldersAtConfirmation_AndRebuildsWhatNamedThem()
        {
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var stores = h.Runtime.Stores;
            await stores.Intel.ApplyMigrationsAsync(KnowledgeSchema.Deduction.Concat(KnowledgeSchema.Identity).Concat(KnowledgeSchema.Timeline).Concat(KnowledgeSchema.Plans).ToList());
            await h.PostAsync(SignalsHarness.General, SignalsHarness.Ana, "synthetic words");
            await h.PostAsync(SignalsHarness.General, SignalsHarness.Ben, "more synthetic words");
            await h.PostAsync(SignalsHarness.General, SignalsHarness.Cyd, "and some more");
            long t = h.Clock.NowMs;
            await stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string subject, string? other) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, object_kind, object_id, recorded_at_ms, status,
                        module_id, module_version, source_seq)
                    VALUES($id,$id,'person',$s,'friend','relationship','{}','synthetic',$ok,$o,$t,'accepted','knowledge.deduction',1,1)
                    """, ("$id", id), ("$s", subject), ("$ok", other == null ? null : "person"), ("$o", other), ("$t", t));
                Fact("f-ben-names-her", "p-ben", "p-ana"); // Ben's fact names her: it goes, and Ben's projections are rebuilt
                Fact("f-ben-own", "p-ben", null);
                Fact("f-cyd-own", "p-cyd", null);          // Cyd has nothing to do with her: left alone
                c.Exec(tx, """
                    INSERT INTO kn_plans(plan_id, kind, owner_person_id, counterparty_person_id, involves_klives, klives_promised, title, all_day, firmness, status,
                        natural_key, evidence_json, created_at_ms, updated_at_ms, source_seq)
                    VALUES('plan-cyd-with-her','plan','p-cyd','p-ana',0,0,'synthetic lunch',0,'firm','scheduled','k','[]',$t,$t,1)
                    """, ("$t", t));
            });

            var data = h.Data!;
            var preview = data.PreviewForget("p-ana");
            long before = h.Runtime.Host.Events.Bounds().LatestEver;
            await data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default);
            // At confirmation, before the run: holders outside the engine are told (never who), and she counts as forgotten.
            var started = h.Runtime.Host.Events.Replay(before, 100).Where(e => e.Topic == Omnipotent.Services.OmniscienceV3.Modules.DataControls.DataControlsModule.ForgettingTopic).ToList();
            Assert.Single(started);
            Assert.DoesNotContain("p-ana", started[0].PayloadJson);
            Assert.True(Forgotten.IsPerson(stores.Operator, "p-ana"));
            Assert.False(Forgotten.IsPerson(stores.Operator, "p-ben"));

            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
            Assert.Equal(0, Count(stores.Intel, "SELECT COUNT(*) FROM assertions WHERE assertion_id='f-ben-names-her'"));
            Assert.Equal(2, Count(stores.Intel, "SELECT COUNT(*) FROM assertions"));
            Assert.Equal(0, Count(stores.Intel, "SELECT COUNT(*) FROM kn_plans"));
            // Ben (a fact named her) and Cyd (a plan with her) have what is projected from their knowledge rebuilt; nobody else.
            Assert.Equal(new[] { "p-ben", "p-cyd" }, Rows(stores.Corpus, "SELECT entity_id FROM change_log WHERE kind=$k ORDER BY entity_id", ("$k", ChangeKinds.EvidenceRemoved)));
            Assert.Single(h.Runtime.Host.Events.Replay(before, 500), e => e.Topic == Omnipotent.Services.OmniscienceV3.Modules.DataControls.DataControlsModule.ForgottenTopic);
        }

        [Fact]
        public async Task PeopleNames_AnswerForKnownPeople_AndForTheMergedUnderWhoTheyAreNow()
        {
            await using var h = await SignalsHarness.StartAsync();
            await h.PostAsync(SignalsHarness.General, SignalsHarness.Ana, "synthetic words");
            await h.PostAsync(SignalsHarness.General, SignalsHarness.Ben, "more synthetic words");
            var stores = h.Runtime.Stores;
            string ana, ben;
            using (var c = stores.Corpus.OpenRead())
            {
                ana = c.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id='p-ana'")!;
                ben = c.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id='p-ben'")!;
            }
            var names = Omnipotent.Services.OmniscienceV3.Api.WsCRoutes.NamesOf(stores, new[] { "p-ana", "p-ben", "p-nobody", "p-ana" });
            Assert.Equal(new[] { "p-ana", "p-ben" }, names.Keys.OrderBy(k => k));
            Assert.Equal(ana, names["p-ana"]);

            // Ben merged into Ana: an old link to Ben shows who that is now.
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE person_canonical SET canonical_id='p-ana' WHERE person_id='p-ben'"));
            Assert.Equal(ana, Omnipotent.Services.OmniscienceV3.Api.WsCRoutes.NamesOf(stores, new[] { "p-ben" })["p-ben"]);
            Assert.NotEqual(ana, ben);
        }

        // ── WS-D ← WS-C: no replica of someone whose analysis is off ──────────────────────────────────────────────────

        [Fact]
        public async Task Replica_AnalysisOff_IsRefused_AnExistingOneBlocked_AndTheBlockLiftedWhenSwitchedBackOn()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            Assert.Equal(VersionStates.Ready, v.State);

            async Task Announce(string kind)
            {
                await h.Runtime.Stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, h.Clock.NowMs, kind, Personas.Lena, Personas.Lena, null, SourceKinds.Operator));
                await h.Runtime.Host.PumpChangesOnceAsync();
                await h.Runtime.Host.PlanOnceAsync();
                await h.DrainAsync();
            }

            // Switched off (the forget run announces it with person.erase once it has removed what was derived).
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { Personas.Lena }, h.Clock.NowMs));
            await Announce(ChangeKinds.PersonErase);
            var person = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.Equal(PersonStatus.Blocked, person.Status);
            Assert.Null(person.ServingVersionId);
            Assert.Equal(0, Count(h.Runtime.Stores.Intel, "SELECT COUNT(*) FROM replica_pairs WHERE person_id=$p", ("$p", Personas.Lena)));
            var refused = await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Trainer.RequestAsync(Personas.Lena, "t", "tester", CancellationToken.None));
            Assert.Contains("analysis is switched off", refused.Message);

            // Switched back on: the block set for that reason is lifted, and the replica can be trained again on request.
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Clear(c, tx, new[] { Personas.Lena }));
            await Announce(ChangeKinds.PersonReanalyse);
            Assert.Equal(PersonStatus.None, h.R.Services.Versions.Person(Personas.Lena)!.Status);
        }

        // ── WS-B → WS-C: a due plan reminder is a notice (X48) ───────────────────────────────────────────────────────

        [Fact]
        public async Task PlanReminder_IsANotice_AndOneWhosePlanIsGoneOrHeldBackIsCancelled()
        {
            long now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
            await using var h = await KnowledgeHarness.StartAsync(now);
            var notices = new RecordingNotices();
            h.Kit.Notices = notices;
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var (plan, restricted) in new[] { ("plan-dinner", 0), ("plan-held", 1) })
                    c.Exec(tx, """
                        INSERT INTO kn_plans(plan_id, kind, owner_person_id, counterparty_person_id, involves_klives, klives_promised, title, all_day, firmness, status,
                            natural_key, evidence_json, minor_restricted, created_at_ms, updated_at_ms, source_seq)
                        VALUES($p,'plan','p-owner','p-other',1,0,'synthetic dinner',0,'firm','scheduled',$p,'[]',$r,$t,$t,1)
                        """, ("$p", plan), ("$r", restricted), ("$t", now));
                void Reminder(string id, string plan, long occurrence) =>
                    c.Exec(tx, "INSERT INTO kn_reminders(reminder_id, plan_id, occurrence_ms, fire_at_ms, kind, state, created_at_ms) VALUES($i,$p,$o,$f,'before','pending',$t)",
                        ("$i", id), ("$p", plan), ("$o", occurrence), ("$f", now - 60_000), ("$t", now));
                Reminder("rm-due", "plan-dinner", now + 2 * 3_600_000L);
                Reminder("rm-long-past", "plan-dinner", now - 3 * 86_400_000L); // the service was down past the occurrence itself
                Reminder("rm-held", "plan-held", now + 2 * 3_600_000L);         // marked a minor after the reminder was set
                Reminder("rm-gone", "plan-forgotten", now + 2 * 3_600_000L);    // the plan was removed by a forget run
            });

            var result = System.Text.Json.JsonSerializer.SerializeToElement(await h.Module<PlansModule>().FireRemindersAsync(CancellationToken.None));
            Assert.Equal((1, 1, 2), (result.GetProperty("fired").GetInt32(), result.GetProperty("missed").GetInt32(), result.GetProperty("withheld").GetInt32()));

            var notice = Assert.Single(notices.Received);
            Assert.Equal(("plan|rm-due", "plan_reminder", PlansModule.Id), (notice.NoticeId, notice.Kind, notice.ModuleId));
            Assert.Equal(("p-owner", "p-other"), (notice.PersonId, notice.OtherPersonId));
            Assert.Contains("synthetic dinner", System.Text.Json.JsonSerializer.Serialize(notice.Data));
            string State(string id) { using var c = h.Stores.Intel.OpenRead(); return c.Scalar<string>(null, "SELECT state FROM kn_reminders WHERE reminder_id=$i", ("$i", id))!; }
            Assert.Equal(new[] { "fired", "missed", "cancelled", "cancelled" }, new[] { "rm-due", "rm-long-past", "rm-held", "rm-gone" }.Select(State));
        }
    }
}
