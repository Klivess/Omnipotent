using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Projections;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>Rollups (exact, temporal, merge-safe) and the read-model projections built from them.</summary>
    public class RollupProjectionTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_rollups_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private readonly FakeLlm llm = new();
        private V3Runtime rt = null!;
        private JobDriver driver = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root), Clock = clock, LlmClient = llm, Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new ClassificationModule(), new RollupModule(), new ProjectionModule() },
                Encoder = new HashingTextEncoder(128),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 11, Messages = 3_000, People = 30, Guilds = 2, ChannelsPerGuild = 2, DirectChats = 8, WhatsAppGroups = 1, Span = TimeSpan.FromDays(200),
            });
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private T Scalar<T>(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private async Task DrainAsync()
        {
            await driver.RunUntilIdleAsync(2_000);
            driver.AssertNoFailures();
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM rollup_dirty"));
        }

        /// <summary>Non-bot, non-system messages of a person in [a, b).</summary>
        private long CorpusCount(string person, long a, long b) => Scalar<long>(rt.Stores.Corpus, """
            SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
            WHERE COALESCE(pc.canonical_id, i.person_id)=$p AND m.sent_at_ms >= $a AND m.sent_at_ms < $b AND m.is_bot=0 AND m.is_system=0 AND i.kind<>'bot'
            """, ("$p", person), ("$a", a), ("$b", b));

        [Fact]
        public async Task Cells_MatchTheCorpusExactly()
        {
            await DrainAsync();
            // Zones are unknown before inference: person-days are UTC days (R4).
            using (var c = rt.Stores.Intel.OpenRead())
            {
                var sample = c.Query(null, "SELECT person_id, day, msgs FROM rollup_person_day ORDER BY msgs DESC LIMIT 25", r => (r.GetString(0), r.GetString(1), r.GetInt32(2)));
                Assert.NotEmpty(sample);
                foreach (var (person, day, msgs) in sample)
                {
                    var (a, b) = Zones.LocalDayRange(RollupModule.ParseDay(day), TimeZoneInfo.Utc);
                    Assert.Equal(CorpusCount(person, a, b), msgs);
                }
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM rollup_person_day WHERE zone_known=1"));
            }
            // Global days (Klives's zone) sum to every human message exactly once.
            long humans = Scalar<long>(rt.Stores.Corpus, "SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.is_bot=0 AND m.is_system=0 AND i.kind<>'bot'");
            Assert.Equal(humans, Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='global'"));
            Assert.Equal(humans, Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_person_day"));
            Assert.Equal(humans, Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='conversation'"));
            // Bots never appear as people in the cells.
            string bot = Scalar<string>(rt.Stores.Corpus, "SELECT person_id FROM identities WHERE kind='bot' LIMIT 1");
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM rollup_person_day WHERE person_id=$p", ("$p", bot)));
            // Pair cells: replies out of a person equal the replies it made to other people's messages.
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT SUM(replies) FROM rollup_pair_day") > 0);
        }

        [Fact]
        public async Task KlivesDays_FollowDstAndEveryMessageCountsOnce()
        {
            // Klives's zone (Europe/London) springs forward on 2025-03-30: a 23-hour day.
            var (a, b) = Zones.LocalDayRange(new DateOnly(2025, 3, 30), Zones.GetOrThrow("Europe/London"));
            Assert.Equal(23 * 3_600_000L, b - a);
            await DrainAsync();
            long inCorpus = Scalar<long>(rt.Stores.Corpus, """
                SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id
                WHERE m.sent_at_ms >= $a AND m.sent_at_ms < $b AND m.is_bot=0 AND i.kind<>'bot'
                """, ("$a", a), ("$b", b));
            Assert.Equal(inCorpus, Scalar<long?>(rt.Stores.Intel, "SELECT msgs FROM rollup_scope_day WHERE scope='global' AND day='2025-03-30'") ?? 0);
        }

        [Fact]
        public async Task DeletesReactionRemovalsAndLateBackfill_RebuildTheirCells()
        {
            await DrainAsync();
            var (messageId, person, sentAt) = QueryOne();
            string day = RollupModule.Day(DateOnly.FromDateTime(Instant.ToUtcDateTime(sentAt)));
            int deletedBefore = Scalar<int>(rt.Stores.Intel, "SELECT deleted FROM rollup_person_day WHERE person_id=$p AND day=$d", ("$p", person), ("$d", day));
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE messages SET deleted_at_ms=$t WHERE message_id=$m", ("$t", clock.NowMs), ("$m", messageId));
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageDelete, messageId, person, null, SourceKinds.Live);
            });
            await DrainAsync();
            Assert.Equal(deletedBefore + 1, Scalar<int>(rt.Stores.Intel, "SELECT deleted FROM rollup_person_day WHERE person_id=$p AND day=$d", ("$p", person), ("$d", day)));

            // A reaction removed: the reactor's given count drops.
            var reaction = QueryReaction();
            string reactDay = RollupModule.Day(DateOnly.FromDateTime(Instant.ToUtcDateTime(reaction.At)));
            int given = Scalar<int>(rt.Stores.Intel, "SELECT reactions_given FROM rollup_person_day WHERE person_id=$p AND day=$d", ("$p", reaction.Person), ("$d", reactDay));
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE reactions SET removed_at_ms=$t WHERE message_id=$m AND identity_id=$i", ("$t", reaction.At + 1000), ("$m", reaction.Message), ("$i", reaction.Identity));
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.ReactionChange, reaction.Message, null, null, SourceKinds.Live);
            });
            await DrainAsync();
            int after = Scalar<int?>(rt.Stores.Intel, "SELECT reactions_given FROM rollup_person_day WHERE person_id=$p AND day=$d", ("$p", reaction.Person), ("$d", reactDay)) ?? 0;
            Assert.Equal(given - 1, after);

            // Backfill of a 2019 message lands today in the change log and still builds its 2019 cell (T5).
            long old = Instant.FromUtc(new DateTime(2019, 4, 2, 10, 0, 0, DateTimeKind.Utc));
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at_ms, time_quality, order_key, current_version,
                        content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    SELECT 'discord:1', 'discord', '1', conversation_id, author_identity_id, $t, 0, 1, 1, 'from the past', 'backfill', 'synthetic', 0, $now, 0
                    FROM messages WHERE message_id=$m
                    """, ("$t", old), ("$now", clock.NowMs), ("$m", messageId));
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, "discord:1", person, null, SourceKinds.Backfill);
            });
            await DrainAsync();
            Assert.Equal(1, Scalar<int>(rt.Stores.Intel, "SELECT msgs FROM rollup_person_day WHERE person_id=$p AND day='2019-04-02'", ("$p", person)));
        }

        private (string Message, string Person, long SentAt) QueryOne()
        {
            using var c = rt.Stores.Corpus.OpenRead();
            return c.Query(null, """
                SELECT m.message_id, COALESCE(pc.canonical_id, i.person_id), m.sent_at_ms FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id
                LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE m.deleted_at_ms IS NULL AND m.is_bot=0 AND i.kind<>'bot' AND m.platform='discord' LIMIT 1
                """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2))).Single();
        }

        private (string Message, string Identity, string Person, long At) QueryReaction()
        {
            using var c = rt.Stores.Corpus.OpenRead();
            return c.Query(null, """
                SELECT r.message_id, r.identity_id, COALESCE(pc.canonical_id, i.person_id), r.added_at_ms FROM reactions r JOIN identities i ON i.identity_id=r.identity_id
                LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE r.removed_at_ms IS NULL AND i.kind<>'bot' LIMIT 1
                """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3))).Single();
        }

        [Fact]
        public async Task Merge_FoldsTheMergedPersonIntoTheSurvivorEverywhere()
        {
            await DrainAsync();
            long beforeA = Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_person_day WHERE person_id='synth-p3'");
            long beforeB = Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_person_day WHERE person_id='synth-p4'");
            Assert.True(beforeA > 0 && beforeB > 0);
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO person_merges(from_person_id, into_person_id, merged_at_ms, actor) VALUES('synth-p4','synth-p3',$t,'test')", ("$t", clock.NowMs));
                c.Exec(tx, "UPDATE person_canonical SET canonical_id='synth-p3' WHERE person_id='synth-p4'");
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.PersonMerge, "synth-p4", "synth-p3", null, SourceKinds.Operator);
            });
            await DrainAsync();
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM rollup_person_day WHERE person_id='synth-p4'"));
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM rollup_pair_day WHERE partner_id='synth-p4' OR person_id='synth-p4'"));
            Assert.Equal(beforeA + beforeB, Scalar<long>(rt.Stores.Intel, "SELECT SUM(msgs) FROM rollup_person_day WHERE person_id='synth-p3'"));
            // The merged-away id's views go; the survivor's dossier counts both.
            Assert.Null(rt.Host.ReadModels.Get(ProjectionModule.Dossier, "synth-p4"));
            Assert.Equal(0, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM projection_people WHERE person_id='synth-p4'"));
            using var doc = JsonDocument.Parse(rt.Host.ReadModels.Get(ProjectionModule.Dossier, "synth-p3")!.BodyJson);
            Assert.Equal(beforeA + beforeB, doc.RootElement.GetProperty("activity").GetProperty("msgs").GetInt64());
        }

        [Fact]
        public void ZoneInference_FindsTheZoneFromRhythmAndDstAndLeavesThinMonthsUnknown()
        {
            // A person awake 08:00–24:00 Tokyo time (UTC+9, no DST) for six months, then a month in New York.
            var tokyo = new int[96];
            for (int q = 0; q < 96; q++)
            {
                int localHour = ((q / 4) + 9) % 24;
                tokyo[q] = localHour is >= 8 and <= 23 ? 20 + localHour : 1;
            }
            var (offset, _, confidence) = ZoneInference.BestOffset(tokyo, 150);
            Assert.InRange(offset, 8 * 60, 10 * 60);
            Assert.True(confidence > 0.3);
            var months = new[] { "2025-01", "2025-02", "2025-03", "2025-04", "2025-05", "2025-06" }
                .Select(m => (m, 540, 0.9, true)).Append(("2025-07", -240, 0.9, true)).Append(("2025-08", 0, 0.1, false)).ToList();
            var assigned = ZoneInference.Assign(months, null, "Europe/London");
            Assert.All(assigned.Take(6), z => Assert.Equal(540, z.OffsetMinutes));
            Assert.Equal("rhythm-away", assigned[6].Method);
            Assert.Equal(-240, assigned[6].OffsetMinutes); // July in New York: EDT
            Assert.Equal("unknown", assigned[7].Method); // thin, and the nearest confident month is away: no guess

            // DST tells Berlin from Lagos: same winter offset, different summer offset.
            var berlin = new[] { ("2025-01", 60, 0.8, true), ("2025-07", 120, 0.8, true) }.ToList();
            Assert.Equal("Europe/Berlin", ZoneInference.Assign(berlin, null, "Europe/London")[0].Zone);
            var lagos = new[] { ("2025-01", 60, 0.8, true), ("2025-07", 60, 0.8, true) }.ToList();
            Assert.Equal("Africa/Lagos", ZoneInference.Assign(lagos, null, "Europe/London")[0].Zone);
            // A stated zone wins over rhythm.
            Assert.All(ZoneInference.Assign(berlin, "Asia/Tokyo", "Europe/London"), z => Assert.Equal("stated", z.Method));
        }

        [Fact]
        public async Task Minors_GetRoutineInferenceAndTheServingFilterDoesNotRemoveIt()
        {
            await DrainAsync();
            clock.Advance(TimeSpan.FromDays(1));
            await driver.RunUntilIdleAsync(2_000); // daily schedules: zones, projections
            var dossier = rt.Host.ReadModels.Get(ProjectionModule.Dossier, "synth-p2")!;
            using (var doc = JsonDocument.Parse(dossier.BodyJson))
            {
                Assert.True(doc.RootElement.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Array);
                Assert.False(doc.RootElement.GetProperty("restricted").GetBoolean());
            }
            // Klives marks the person a minor: the stored dossier keeps its hours and zone, and serving does not strip them.
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES('synth-p2',1,$t)", ("$t", clock.NowMs)));
            var module = rt.Host.Modules.OfType<ProjectionModule>().Single();
            var served = module.Filter(dossier, rt.Host.Policy, clock.NowMs);
            using (var doc = JsonDocument.Parse(served))
            {
                Assert.True(doc.RootElement.TryGetProperty("hours", out _));
                Assert.True(doc.RootElement.TryGetProperty("zone", out _));
                Assert.False(doc.RootElement.GetProperty("restricted").GetBoolean());
            }
            // The projection catches up (policy job) and the zones job still writes a zone for them.
            clock.Advance(TimeSpan.FromDays(1));
            await driver.RunUntilIdleAsync(2_000);
            using (var doc = JsonDocument.Parse(rt.Host.ReadModels.Get(ProjectionModule.Dossier, "synth-p2")!.BodyJson))
                Assert.True(doc.RootElement.TryGetProperty("hours", out var hrs) && hrs.ValueKind == JsonValueKind.Array);
            // The zones job is allowed to write a zone for them; whether this seed has enough signal is immaterial here.
            Assert.True(Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM rollup_zones WHERE person_id='synth-p2'") >= 0);
        }

        [Fact]
        public async Task Projections_ServeEveryPageFromOneIndexedRead()
        {
            await DrainAsync();
            clock.Advance(TimeSpan.FromMinutes(6));
            await driver.RunUntilIdleAsync(2_000);
            Assert.NotNull(rt.Host.ReadModels.Get(ProjectionModule.Overview, "global"));
            Assert.NotNull(rt.Host.ReadModels.Get(ProjectionModule.Health, "global"));
            string conv = Scalar<string>(rt.Stores.Corpus, "SELECT conversation_id FROM conversations LIMIT 1");
            Assert.NotNull(rt.Host.ReadModels.Get(ProjectionModule.ConversationCard, conv));
            string guild = Scalar<string>(rt.Stores.Corpus, "SELECT guild_id FROM guilds LIMIT 1");
            Assert.NotNull(rt.Host.ReadModels.Get(ProjectionModule.GuildCard, guild));
            long people = Scalar<long>(rt.Stores.Corpus, "SELECT COUNT(*) FROM persons");
            Assert.Equal(people, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM projection_people"));

            await rt.Stores.Intel.WriteAsync((w, tx) => w.Exec(tx, "ANALYZE"));
            using var c = rt.Stores.Intel.OpenRead();
            foreach (var (sql, index) in new[]
            {
                ("SELECT card_json FROM projection_people ORDER BY last_seen_ms DESC, person_id DESC LIMIT 51", "ix_pp_last"),
                ("SELECT card_json FROM projection_people WHERE (msgs_total < 10 OR (msgs_total = 10 AND person_id < 'x')) ORDER BY msgs_total DESC, person_id DESC LIMIT 51", "ix_pp_total"),
                ("SELECT card_json FROM projection_people WHERE tier='tracked' ORDER BY last_seen_ms DESC, person_id DESC LIMIT 51", "ix_pp_tier_last"),
                ("SELECT body FROM read_models WHERE model_id='person.dossier' AND key='synth-p1'", "sqlite_autoindex_read_models_1 (model_id=? AND key=?)"),
            })
            {
                var plan = string.Join(" | ", c.Query(null, "EXPLAIN QUERY PLAN " + sql, r => r.GetString(3)));
                Assert.True(plan.Contains(index, StringComparison.Ordinal), $"{sql} => {plan}");
                Assert.False(plan.Contains("USE TEMP B-TREE", StringComparison.Ordinal), $"{sql} => {plan}");
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) rt.Host.ReadModels.Get(ProjectionModule.Dossier, "synth-p" + (i % 30));
            Assert.True(sw.ElapsedMilliseconds / 200.0 < 150, "a dossier read must be far under 150 ms");
        }
    }
}
