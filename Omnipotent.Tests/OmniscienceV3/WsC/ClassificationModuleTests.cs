using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The classification module end to end on a synthetic corpus: drain (embed + gate), the LLM teacher (a hidden oracle
    /// over the synthetic vocabulary), head training and the regression gate, the label-set build and activation, gold
    /// corrections, edits, bots, the teacher budget and refusals, and X19 taught concepts.
    /// </summary>
    public class ClassificationModuleTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_classify_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private readonly FakeLlm llm = new();
        private readonly ClassificationModule module = new();
        private V3Runtime rt = null!;
        private JobDriver driver = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root),
                Clock = clock,
                LlmClient = llm,
                Log = _ => { },
                LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { module },
                Encoder = new HashingTextEncoder(256),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 3, Messages = 2_500, People = 40, Guilds = 2, ChannelsPerGuild = 3, DirectChats = 10, WhatsAppGroups = 1, Span = TimeSpan.FromDays(120),
            });
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherTargetSetting, 700, "test");
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherBatchSetting, 150, "test");
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES('synth-p1','tracked',0)"));
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private T Scalar<T>(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private string Model => rt.Encoder!.ModelVersion;

        /// <summary>Runs the pipeline to an active label set: drain, several teacher rounds, training, relabel.</summary>
        private async Task ToActiveLabelsetAsync()
        {
            await driver.RunUntilIdleAsync();
            for (int i = 0; i < 6; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
                await driver.RunUntilIdleAsync();
            }
            clock.Advance(TimeSpan.FromMinutes(31));
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
        }

        [Fact]
        public async Task Pipeline_EmbedsTeachesTrainsAndPublishesALabelSetThatMatchesTheTeacher()
        {
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            long withText = Scalar<long>(rt.Stores.Corpus, "SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.is_bot=0 AND i.kind<>'bot' AND m.content IS NOT NULL");
            Assert.Equal(withText, rt.Vectors.Count(VectorSpaces.Message, Model));
            Assert.Equal(Scalar<long>(rt.Stores.Corpus, "SELECT MAX(seq) FROM change_log"), Scalar<long>(rt.Stores.Intel, "SELECT seq FROM classify_cursors WHERE name='drain'"));
            // Bots are never embedded or labelled.
            string botMessage = Scalar<string>(rt.Stores.Corpus, "SELECT m.message_id FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE i.kind='bot' LIMIT 1");
            Assert.Empty(rt.Vectors.Get(VectorSpaces.Message, Model, new[] { botMessage }));

            for (int i = 0; i < 6; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
                await driver.RunUntilIdleAsync();
            }
            long teacher = Scalar<long>(rt.Stores.Intel, "SELECT COUNT(DISTINCT item_id) FROM labels WHERE model_version=$v", ("$v", ClassificationModule.TeacherVersion));
            Assert.True(teacher >= 500, $"teacher labelled {teacher}");
            Assert.True(llm.Requests.Count(r => r.Purpose == "classify.teacher") >= 4);
            // Every teacher call shares one byte-identical system prompt (prefix cache) and runs in the background class.
            Assert.Single(llm.Requests.Where(r => r.Purpose == "classify.teacher").Select(r => r.SystemPrompt).Distinct());
            Assert.All(llm.Requests.Where(r => r.Purpose == "classify.teacher"), r => Assert.Equal(AIRouterWorkClass.Background, r.WorkClass));
            // The tracked person was sampled with priority.
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM classify_teacher_queue WHERE priority=300") > 0);

            clock.Advance(TimeSpan.FromMinutes(31));
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            int? active;
            using (var c = rt.Stores.Intel.OpenRead()) active = ClassificationReader.ActiveLabelset(c);
            Assert.NotNull(active);
            Assert.Equal(withText, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM classify_message WHERE labelset=$l", ("$l", active!.Value)));
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM classify_heads WHERE state='active'") >= ClassificationTasks.All.Count);

            // Messages the teacher never saw: the local heads must agree with it.
            List<(string Id, string Text)> unseen;
            using (var c = rt.Stores.Corpus.OpenRead())
                unseen = c.Query(null, "SELECT message_id, content FROM messages WHERE content IS NOT NULL AND is_bot=0", r => (r.GetString(0), r.GetString(1)));
            HashSet<string> taught;
            using (var c = rt.Stores.Intel.OpenRead())
                taught = c.Query(null, "SELECT DISTINCT item_id FROM labels WHERE model_version=$v", r => r.GetString(0), ("$v", ClassificationModule.TeacherVersion)).ToHashSet();
            unseen = unseen.Where(u => !taught.Contains(u.Id)).ToList();
            var labels = ClassificationReader.ForMessages(rt.Stores, unseen.Select(u => u.Id).ToList());
            var answered = unseen.Where(u => labels.TryGetValue(u.Id, out var l) && l.Answer("sentiment") != null).ToList();
            double coverage = (double)answered.Count / unseen.Count;
            double accuracy = answered.Count(u => labels[u.Id].Answer("sentiment") == FakeLlm.OracleSentiment(u.Text)) / (double)Math.Max(1, answered.Count);
            Assert.True(coverage > 0.5, $"sentiment coverage {coverage:0.00}");
            Assert.True(accuracy > 0.85, $"sentiment accuracy {accuracy:0.00} on {answered.Count} answered");
            var japanese = unseen.Where(u => FakeLlm.OracleLanguage(u.Text) == "ja").ToList();
            Assert.True(japanese.Count(u => labels.TryGetValue(u.Id, out var l) && l.Answer("language") == "ja") >= japanese.Count / 2);

            var health = rt.Host.ReadModels.Get(ClassificationModule.HealthModel, "global");
            Assert.NotNull(health);
            using var doc = JsonDocument.Parse(health!.BodyJson);
            Assert.Equal(active.Value, doc.RootElement.GetProperty("activeLabelset").GetInt32());
            Assert.Equal(ClassificationTasks.All.Count, doc.RootElement.GetProperty("tasks").GetArrayLength());
        }

        [Fact]
        public async Task GoldCorrections_WinAtOnceAndEditsReEmbedByVersion()
        {
            await ToActiveLabelsetAsync();
            string target;
            using (var c = rt.Stores.Corpus.OpenRead())
                target = c.Query(null, "SELECT message_id, content FROM messages WHERE content LIKE '%saffron%' AND content NOT LIKE '%tundra%' AND content NOT LIKE '%glacier%' AND is_bot=0 LIMIT 1",
                    r => r.GetString(0)).Single();
            await module.CorrectAsync(target, "sentiment", "negative", "test", "the teacher was wrong", CancellationToken.None);
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            var after = ClassificationReader.ForMessages(rt.Stores, new[] { target })[target];
            Assert.Equal("negative", after.Answer("sentiment"));
            Assert.Contains(ClassificationReader.LoadGold(rt.Stores), g => g.Key == (target, "sentiment") && g.Value == "negative");
            // The detailed record keeps the gold signal for explainability (A3).
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM labels WHERE item_id=$i AND task='sentiment' AND model='a2' AND signals_json LIKE '%gold%'", ("$i", target)) == 1);
            // Correcting the same key again supersedes, never deletes.
            await ClassificationReader.RecordGoldAsync(rt.Stores, clock, "test", target, "sentiment", "neutral", null);
            Assert.Equal(2, Scalar<long>(rt.Stores.Operator, "SELECT COUNT(*) FROM gold_labels WHERE kind='classification'"));
            Assert.Equal(1, Scalar<long>(rt.Stores.Operator, "SELECT COUNT(*) FROM gold_labels WHERE kind='classification' AND superseded_at_ms IS NULL"));
            await Assert.ThrowsAsync<ArgumentException>(() => ClassificationReader.RecordGoldAsync(rt.Stores, clock, "test", target, "sentiment", "furious", null));

            // An edit: a new version with a change-log row is re-embedded and relabelled at version 2.
            string edited;
            using (var c = rt.Stores.Corpus.OpenRead())
                edited = c.Query(null, "SELECT message_id FROM messages WHERE current_version=1 AND is_bot=0 AND content IS NOT NULL LIMIT 1", r => r.GetString(0)).Single();
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE messages SET content='totally glacier tundra now', current_version=2, edited_at_ms=$t WHERE message_id=$m", ("$t", clock.NowMs), ("$m", edited));
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,2,'totally glacier tundra now',$t,$t,'synthetic')",
                    ("$m", edited), ("$t", clock.NowMs));
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageVersion, edited, null, null, SourceKinds.Live);
            });
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            Assert.Equal(2, rt.Vectors.Get(VectorSpaces.Message, Model, new[] { edited })[edited].SourceVersion);
            var relabelled = ClassificationReader.ForMessages(rt.Stores, new[] { edited })[edited];
            Assert.Equal(2, relabelled.ItemVersion);
            Assert.Equal("negative", relabelled.Answer("sentiment"));
        }

        [Fact]
        public async Task Teacher_RespectsTheDailyBudgetAndRecordsRefusalsHonestly()
        {
            await driver.RunUntilIdleAsync();
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherCallsPerDaySetting, 1, "test");
            llm.Override = r => r.Purpose == "classify.teacher"
                ? new LlmResult(false, "I can't help with that.", null, LlmErrorKind.Refused, "refused", "stop", "fake-model", "fake", 10, 0, 5, 100, 5)
                : null;
            for (int i = 0; i < 3; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
                await driver.RunUntilIdleAsync();
            }
            Assert.Equal(1, llm.Requests.Count(r => r.Purpose == "classify.teacher")); // one call per Klives-day
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM classify_teacher_queue WHERE state='refused'") > 0);
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM labels WHERE model_version=$v", ("$v", ClassificationModule.TeacherVersion)));
            // The next Klives-day the budget resets.
            llm.Override = null;
            clock.Advance(TimeSpan.FromHours(24));
            await driver.RunUntilIdleAsync();
            Assert.Equal(2, llm.Requests.Count(r => r.Purpose == "classify.teacher"));
        }

        [Fact]
        public async Task PriorityPass_RunsFirstOnALargeBacklog()
        {
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 0, "test");
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            Assert.Equal(Model, module.State<string>("priority.done"));
            var progress = driver.Ran.Where(r => r.Kind == "drain").ToList();
            Assert.NotEmpty(progress);
            long withText = Scalar<long>(rt.Stores.Corpus, "SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.is_bot=0 AND i.kind<>'bot' AND m.content IS NOT NULL");
            Assert.Equal(withText, rt.Vectors.Count(VectorSpaces.Message, Model));
        }

        [Fact]
        public async Task TeachConcept_LearnsFromADescriptionAppliesCorpusWideAndRefusesSensitiveConcepts()
        {
            await ToActiveLabelsetAsync();
            var runner = rt.Tactics;
            string runId = await runner.StartAsync(TeachConceptTactic.Key, new TacticInput(Array.Empty<Selection>(),
                JsonSerializer.SerializeToElement(new { name = "spice talk", description = "messages about saffron" })), "test");
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            var run = runner.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, run.Error);
            var concept = Assert.Single(module.Concepts());
            Assert.Equal("active", concept.State);
            Assert.True(concept.Hits > 0);
            List<(string Id, string Text)> hits;
            using (var c = rt.Stores.Intel.OpenRead())
            {
                var ids = c.Query(null, "SELECT item_id FROM labels WHERE task=$t", r => r.GetString(0), ("$t", ClassificationModule.ConceptTask(concept.ConceptId)));
                using var corpus = rt.Stores.Corpus.OpenRead();
                hits = corpus.QueryIn(null, "SELECT message_id, content FROM messages WHERE message_id IN ({keys})", ids, r => (r.GetString(0), r.GetString(1)));
            }
            double precision = hits.Count(h => h.Text.Contains("saffron")) / (double)hits.Count;
            Assert.True(precision > 0.8, $"concept precision {precision:0.00} over {hits.Count} hits");

            // New messages are scored as they arrive.
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                string id = "discord:999000000000000001";
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at_ms, time_quality, order_key, current_version,
                        content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    SELECT $id, 'discord', '999000000000000001', conversation_id, author_identity_id, $t, 0, 999000000000000001, 1, 'saffron saffron saffron please', 'live', 'synthetic', 0, $t, 0
                    FROM messages WHERE is_bot=0 LIMIT 1
                    """, ("$id", id), ("$t", clock.NowMs));
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, id, null, null, SourceKinds.Live);
            });
            await driver.RunUntilIdleAsync();
            Assert.Equal(1, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM labels WHERE item_id='discord:999000000000000001' AND task=$t", ("$t", ClassificationModule.ConceptTask(concept.ConceptId))));

            string refused = await runner.StartAsync(TeachConceptTactic.Key, new TacticInput(Array.Empty<Selection>(),
                JsonSerializer.SerializeToElement(new { name = "religion talk", description = "people discussing their faith" })), "test");
            await driver.RunUntilIdleAsync();
            var r2 = runner.Get(refused)!;
            Assert.Equal(TacticRunStates.Failed, r2.State);
            Assert.Contains("doesn't infer", r2.Error);
            Assert.Single(module.Concepts());
        }

        [Fact]
        public async Task TeachConcept_ValidatesItsInput()
        {
            var tactic = new TeachConceptTactic(module);
            Assert.NotNull(tactic.Validate(new TacticInput(Array.Empty<Selection>(), JsonSerializer.SerializeToElement(new { name = "x" }))));
            Assert.NotNull(tactic.Validate(new TacticInput(new[] { Selection.Of(SelectionKind.Message, new[] { "a", "b" }) }, JsonSerializer.SerializeToElement(new { name = "x" }))));
            Assert.Null(tactic.Validate(new TacticInput(Array.Empty<Selection>(), JsonSerializer.SerializeToElement(new { name = "x", description = "about x" }))));
            await Task.CompletedTask;
        }
    }
}
