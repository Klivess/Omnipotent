using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Explore;
using Omnipotent.Services.OmniscienceV3.Modules.Projections;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Tests.OmniscienceV3.Storage;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>The Explore engine (plan D4) on its DuckDB mirror.</summary>
    public class ExploreTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_explore_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private V3Runtime rt = null!;
        private JobDriver driver = null!;
        private ExploreModule explore = null!;

        public ExploreTests(ITestOutputHelper output) => this.output = output;

        private static bool All(string _) => true;

        /// <summary>A stand-in for any other module: it owns a table, declares datasets over it and offers them to Explore
        /// (one followed by a change column, one copied whole).</summary>
        private sealed class NotesModule : IntelModuleBase, IExploreSourceProvider
        {
            private static readonly DimensionBinding[] Bindings =
            {
                new("time.occurred", "at_ms"), new("person.subject", "person_id"), new("content.attribute", "attribute"), new("prov.confidence", "score"),
                new("content.is_edited", "flagged"),
            };
            private static readonly MeasureDescriptor[] Measures = { new("count", "Notes", "count", null), new("avg_score", "Average score", "avg", "score") };

            public override ModuleManifest Manifest { get; } = new()
            {
                Id = "test_notes", Version = 1, Title = "Test notes",
                Datasets = new[]
                {
                    new DatasetDescriptor("test.notes", "Notes", Bindings, Measures, "note", "omniscience.persons.read", MinorFiltered: true),
                    new DatasetDescriptor("test.notes_whole", "Notes (copied whole)", Bindings, Measures, "note", "omniscience.persons.read", MinorFiltered: true),
                    new DatasetDescriptor("test.unsafe", "Badly declared", Bindings, Measures, "note", "omniscience.persons.read", MinorFiltered: true),
                },
            };

            public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
            {
                new Migration("test_notes", 1, "notes", "CREATE TABLE test_notes(note_id TEXT PRIMARY KEY, at_ms INTEGER, person_id TEXT, attribute TEXT, score REAL, flagged INTEGER, seq INTEGER);"),
            };

            public IReadOnlyList<ExploreSource> ExploreSources { get; } = new[]
            {
                new ExploreSource("test.notes", "test_notes", "note_id", "at_ms", "seq"),
                new ExploreSource("test.notes_whole", "test_notes", "note_id", "at_ms"),
                new ExploreSource("test.unsafe", "test_notes; DROP TABLE messages", "note_id", "at_ms"),
            };

            public override Task<string?> ExecuteAsync(Omnipotent.Services.OmniscienceV3.Kernel.Jobs.JobContext job) => Task.FromResult<string?>(null);
        }

        private readonly List<string> errors = new();

        public async Task InitializeAsync()
        {
            var modules = new List<IIntelModule> { new ClassificationModule(), new RollupModule(), new ProjectionModule(), new NotesModule() };
            explore = new ExploreModule(() => modules);
            modules.Add(explore);
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root), Clock = clock, LlmClient = new FakeLlm(), Log = _ => { }, LogError = (m, _) => { lock (errors) errors.Add(m); },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = modules,
                Encoder = new HashingTextEncoder(64),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 21, Messages = 2_500, People = 30, Guilds = 2, ChannelsPerGuild = 2, DirectChats = 8, WhatsAppGroups = 1, Span = TimeSpan.FromDays(150),
            });
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
            await driver.RunUntilIdleAsync(3_000);
            driver.AssertNoFailures();
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private long Corpus(string sql, params (string, object?)[] args)
        {
            using var c = rt.Stores.Corpus.OpenRead();
            return c.Scalar<long>(null, sql, args);
        }

        private const string Humans = "FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.is_bot=0 AND m.is_system=0 AND i.kind<>'bot'";

        private static ExploreFilter F(string dim, FilterOp op, params object[] values) =>
            new(dim, op, values.Select(v => JsonSerializer.SerializeToElement(v)).ToList());

        [Fact]
        public async Task Mirror_HoldsEveryHumanMessageOnceAndCountsMatch()
        {
            var r = await explore.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), new[] { "where.kind" }, new[] { "count" }), All, CancellationToken.None);
            long total = r.Rows.Sum(row => row[1].GetInt64());
            Assert.Equal(Corpus($"SELECT COUNT(*) {Humans}"), total);
            Assert.Equal(total, r.Coverage.Records);
            Assert.Contains("discord", r.Coverage.Sources);
            Assert.NotNull(r.DrillToken);
            Assert.Contains("SQL:", r.Explain);
            // Same query, unchanged mirror: served from the result cache.
            var again = await explore.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), new[] { "where.kind" }, new[] { "count" }), All, CancellationToken.None);
            Assert.Same(r, again);
            // Every direct message can be split by who it was written to, and server messages by server id (never by name).
            var partners = await explore.QueryAsync(new ExploreQuery("messages", new[] { F("where.kind", FilterOp.Eq, "dm") }, new[] { "person.partner" }, new[] { "count" }), All, CancellationToken.None);
            Assert.All(partners.Rows, row => Assert.StartsWith("synth-p", row[0].GetString()));
            Assert.Equal(Corpus($"SELECT COUNT(*) {Humans} AND m.conversation_id IN (SELECT conversation_id FROM conversations WHERE kind='dm')"), partners.Rows.Sum(row => row[1].GetInt64()));
            var guilds = await explore.QueryAsync(new ExploreQuery("messages", new[] { F("where.guild", FilterOp.IsNotNull) }, new[] { "where.guild" }, new[] { "count", "people" }), All, CancellationToken.None);
            Assert.Equal(2, guilds.Rows.Count);
            Assert.All(guilds.Rows, row => Assert.StartsWith("discord:", row[0].GetString()));
        }

        [Fact]
        public async Task KlivesLens_DaysFollowDstAndUtcLensDiffers()
        {
            var london = Zones.GetOrThrow("Europe/London");
            var (a, b) = Zones.LocalDayRange(new DateOnly(2025, 3, 30), london); // 23 hours
            var q = new ExploreQuery("messages", new[] { F("time.occurred", FilterOp.Between, a - 86_400_000L, b + 86_400_000L) }, Array.Empty<string>(), new[] { "count" },
                TimeBucket.Day, Lens: TimeLens.Klives);
            var r = await explore.QueryAsync(q, All, CancellationToken.None);
            var day = r.Rows.Single(row => row[0].GetString() == "2025-03-30");
            Assert.Equal(Corpus($"SELECT COUNT(*) {Humans} AND m.sent_at_ms >= $a AND m.sent_at_ms < $b", ("$a", a), ("$b", b)), day[1].GetInt64());
            // Hour of day on the subject's clock (UTC until their zone is inferred) is a different, complete partition.
            var hours = await explore.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), Array.Empty<string>(), new[] { "count" }, TimeBucket.HourOfDay, Lens: TimeLens.Subject),
                All, CancellationToken.None);
            Assert.InRange(hours.Rows.Count, 20, 24);
            Assert.Equal(Corpus($"SELECT COUNT(*) {Humans} AND m.time_quality NOT IN (3,5)"), hours.Rows.Sum(row => row[1].GetInt64()));
            // An explicit IANA zone works too, and a bad one is refused by validation.
            var tokyo = await explore.QueryAsync(q with { Lens = TimeLens.Utc, Zone = "Asia/Tokyo" }, All, CancellationToken.None);
            Assert.NotEmpty(tokyo.Rows);
            await Assert.ThrowsAsync<ApiException>(() => explore.QueryAsync(q with { Lens = TimeLens.Utc, Zone = "Mars/Olympus" }, All, CancellationToken.None));
        }

        [Fact]
        public async Task People_ResolveThroughMergesAtQueryTime()
        {
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE person_canonical SET canonical_id='synth-p3' WHERE person_id='synth-p4'");
                ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.PersonMerge, "synth-p4", "synth-p3", null, SourceKinds.Operator);
            });
            await driver.RunUntilIdleAsync(3_000);
            var r = await explore.QueryAsync(new ExploreQuery("messages", new[] { F("person.author", FilterOp.In, "synth-p3", "synth-p4") }, new[] { "person.author" }, new[] { "count" }),
                All, CancellationToken.None);
            var row = Assert.Single(r.Rows);
            Assert.Equal("synth-p3", row[0].GetString());
            Assert.Equal(Corpus($"SELECT COUNT(*) {Humans} AND i.person_id IN ('synth-p3','synth-p4')"), row[1].GetInt64());
        }

        [Fact]
        public async Task Records_DrillToTheExactMessagesWithTextAndNeedTheMessagesPermission()
        {
            var r = await explore.QueryAsync(new ExploreQuery("messages", new[] { F("where.kind", FilterOp.Eq, "dm") }, Array.Empty<string>(), new[] { "count" }, TimeBucket.Month),
                All, CancellationToken.None);
            string month = r.Rows[0][0].GetString()!;
            long inMonth = r.Rows[0][1].GetInt64();
            var page = await explore.RecordsAsync(new ExploreRecordsRequest(r.DrillToken!, new[] { F("@bucket", FilterOp.Eq, month) }, Limit: 7), All, CancellationToken.None);
            Assert.Equal(inMonth, page.Total);
            Assert.Equal(Math.Min(7, inMonth), page.Records.Count);
            Assert.All(page.Records, rec => Assert.Equal("dm", rec.GetProperty("kind").GetString()));
            Assert.All(page.Records, rec => Assert.True(rec.TryGetProperty("content", out _)));
            // Keyset paging walks every record exactly once.
            var seen = new HashSet<string>(page.Records.Select(x => x.GetProperty("message_id").GetString()!));
            var cursor = page.NextCursor;
            while (cursor != null)
            {
                var next = await explore.RecordsAsync(new ExploreRecordsRequest(r.DrillToken!, new[] { F("@bucket", FilterOp.Eq, month) }, Limit: 7, Cursor: cursor), All, CancellationToken.None);
                foreach (var x in next.Records) Assert.True(seen.Add(x.GetProperty("message_id").GetString()!));
                cursor = next.NextCursor;
            }
            Assert.Equal(inMonth, seen.Count);
            await Assert.ThrowsAsync<ApiException>(() => explore.RecordsAsync(new ExploreRecordsRequest(r.DrillToken!), k => k != "omniscience.messages.read", CancellationToken.None));
            await Assert.ThrowsAsync<ApiException>(() => explore.RecordsAsync(new ExploreRecordsRequest("not-a-token"), All, CancellationToken.None));
        }

        [Fact]
        public async Task Minors_AreLeftOutOfPerPersonTimePatterns()
        {
            // The busiest person other than Klives himself (which people write at all depends on the generator's seed).
            string person;
            using (var c = rt.Stores.Corpus.OpenRead())
                person = c.Scalar<string>(null, $"SELECT i.person_id {Humans} AND i.person_id <> 'synth-p0' AND m.time_quality = 0 GROUP BY i.person_id ORDER BY COUNT(*) DESC, i.person_id LIMIT 1")!;
            var q = new ExploreQuery("messages", new[] { F("person.author", FilterOp.Eq, person) }, Array.Empty<string>(), new[] { "count" }, TimeBucket.HourOfDay);
            Assert.NotEmpty((await explore.QueryAsync(q, All, CancellationToken.None)).Rows);
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t)", ("$p", person), ("$t", clock.NowMs)));
            clock.Advance(TimeSpan.FromMinutes(1));
            await driver.RunUntilIdleAsync(3_000);
            var after = await explore.QueryAsync(q, All, CancellationToken.None);
            Assert.Empty(after.Rows);
            Assert.Contains("MinorGuard", after.Explain);
            // Their messages stay countable without a time-of-day pattern (their history stays browsable).
            Assert.NotEmpty((await explore.QueryAsync(q with { Bucket = TimeBucket.None }, All, CancellationToken.None)).Rows);
        }

        [Fact]
        public async Task FacetsExportsAndValidation()
        {
            var facets = await explore.FacetsAsync(new ExploreFacetsRequest("messages", Array.Empty<ExploreFilter>(), new[] { "where.platform", "where.kind" }), All, CancellationToken.None);
            Assert.Equal(2, facets.Facets.Count);
            Assert.Contains(facets.Facets[0].Values, v => v.Value.GetString() == "discord");

            var r = await explore.QueryAsync(new ExploreQuery("messages", new[] { F("where.platform", FilterOp.Eq, "whatsapp") }, Array.Empty<string>(), new[] { "count" }), All, CancellationToken.None);
            string csv = Path.Combine(root, "export.csv");
            long n = await explore.ExportAsync(new ExploreExportRequest(ExportFormat.Csv, r.DrillToken), All, csv, CancellationToken.None);
            Assert.Equal(r.Rows[0][0].GetInt64(), n);
            Assert.Equal(n + 1, File.ReadAllLines(csv).Length); // header + every record: never capped
            string parquet = Path.Combine(root, "export.parquet");
            await explore.ExportAsync(new ExploreExportRequest(ExportFormat.Parquet, r.DrillToken), All, parquet, CancellationToken.None);
            Assert.True(new FileInfo(parquet).Length > 0);

            await Assert.ThrowsAsync<ApiException>(() => explore.QueryAsync(new ExploreQuery("nope", Array.Empty<ExploreFilter>(), Array.Empty<string>(), new[] { "count" }), All, CancellationToken.None));
            await Assert.ThrowsAsync<ApiException>(() => explore.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), new[] { "person.in_image" }, new[] { "count" }), All, CancellationToken.None));
            await Assert.ThrowsAsync<ApiException>(() => explore.QueryAsync(new ExploreQuery("events", Array.Empty<ExploreFilter>(), Array.Empty<string>(), new[] { "count" }), k => k != "omniscience.persons.read", CancellationToken.None));
            // Engine datasets are mirrored too.
            clock.Advance(TimeSpan.FromMinutes(6));
            await driver.RunUntilIdleAsync(3_000);
            var jobs = await explore.QueryAsync(new ExploreQuery("jobs", Array.Empty<ExploreFilter>(), new[] { "prov.module" }, new[] { "count" }), All, CancellationToken.None);
            Assert.Contains(jobs.Rows, row => row[0].GetString() == "classify");
        }

        [Fact]
        public async Task ModuleDatasets_AreCopiedFromTheirTablesFollowChangesAndPolicy()
        {
            Task Notes(params (string Id, long At, string Person, string Attribute, double Score, int Flag, long Seq)[] rows) =>
                rt.Stores.Intel.WriteAsync((c, tx) =>
                {
                    foreach (var n in rows)
                        c.Exec(tx, """
                            INSERT INTO test_notes(note_id, at_ms, person_id, attribute, score, flagged, seq) VALUES($i,$a,$p,$k,$s,$f,$q)
                            ON CONFLICT(note_id) DO UPDATE SET attribute=excluded.attribute, score=excluded.score, flagged=excluded.flagged, seq=excluded.seq
                            """, ("$i", n.Id), ("$a", n.At), ("$p", n.Person), ("$k", n.Attribute), ("$s", n.Score), ("$f", n.Flag), ("$q", n.Seq));
                });
            async Task PassAsync()
            {
                clock.Advance(TimeSpan.FromMinutes(6));
                await driver.RunUntilIdleAsync(3_000);
                driver.AssertNoFailures();
            }
            async Task<Dictionary<string, long>> ByAttributeAsync(string dataset)
            {
                var r = await explore.QueryAsync(new ExploreQuery(dataset, Array.Empty<ExploreFilter>(), new[] { "content.attribute" }, new[] { "count", "avg_score" }), All, CancellationToken.None);
                return r.Rows.ToDictionary(row => row[0].GetString()!, row => row[1].GetInt64());
            }

            // The badly declared dataset (a table name that is not an identifier) was refused at start; the others are served.
            Assert.Contains(errors, e => e.Contains("test.unsafe"));
            Assert.DoesNotContain(explore.ServedDatasets, d => d.Id == "test.unsafe");
            await Assert.ThrowsAsync<ApiException>(() => explore.QueryAsync(new ExploreQuery("test.unsafe", Array.Empty<ExploreFilter>(), Array.Empty<string>(), new[] { "count" }), All, CancellationToken.None));

            long t0 = clock.NowMs - 86_400_000L;
            await Notes(("n1", t0, "synth-p3", "city", 0.9, 1, 1), ("n2", t0 + 1_000, "synth-p3", "religion", 0.8, 0, 2),
                ("n3", t0 + 2_000, "synth-p5", "school", 0.7, 0, 3), ("n4", t0 + 3_000, "synth-p5", "favourite_game", 0.6, 0, 4));
            await PassAsync();
            foreach (var dataset in new[] { "test.notes", "test.notes_whole" })
            {
                // A special-category attribute is never offered as something to slice people by.
                Assert.Equal(new[] { "city", "favourite_game", "school" }, (await ByAttributeAsync(dataset)).Keys.OrderBy(k => k, StringComparer.Ordinal));
            }

            // A person marked as a minor: their restricted identity attributes leave every dataset that carries attributes.
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES('synth-p5',1,$t)", ("$t", clock.NowMs)));
            await PassAsync();
            foreach (var dataset in new[] { "test.notes", "test.notes_whole" })
                Assert.Equal(new[] { "city", "favourite_game" }, (await ByAttributeAsync(dataset)).Keys.OrderBy(k => k, StringComparer.Ordinal));

            // A changed row and a new one arrive through the change column (and through the whole copy).
            await Notes(("n1", t0, "synth-p3", "hometown", 0.95, 1, 5), ("n5", t0 + 4_000, "synth-p3", "pet", 0.5, 0, 6));
            await PassAsync();
            foreach (var dataset in new[] { "test.notes", "test.notes_whole" })
                Assert.Equal(new[] { "favourite_game", "hometown", "pet" }, (await ByAttributeAsync(dataset)).Keys.OrderBy(k => k, StringComparer.Ordinal));

            // A row deleted at the source leaves the mirror too (the incremental copy notices it holds more than the source).
            await rt.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM test_notes WHERE note_id='n4'"));
            await PassAsync();
            foreach (var dataset in new[] { "test.notes", "test.notes_whole" })
                Assert.Equal(new[] { "hometown", "pet" }, (await ByAttributeAsync(dataset)).Keys.OrderBy(k => k, StringComparer.Ordinal));

            // Records: typed values, the module's own columns, no bookkeeping columns.
            var result = await explore.QueryAsync(new ExploreQuery("test.notes", new[] { F("content.attribute", FilterOp.Eq, "hometown") }, Array.Empty<string>(), new[] { "count" }), All, CancellationToken.None);
            var page = await explore.RecordsAsync(new ExploreRecordsRequest(result.DrillToken!), All, CancellationToken.None);
            Assert.Equal("note", page.RecordKind);
            var record = Assert.Single(page.Records);
            Assert.Equal("n1", record.GetProperty("note_id").GetString());
            Assert.Equal("synth-p3", record.GetProperty("person_id").GetString());
            Assert.True(record.GetProperty("flagged").GetBoolean());
            Assert.Equal(0.95, record.GetProperty("score").GetDouble(), 6);
            Assert.False(record.TryGetProperty("_change", out _));
        }

        /// <summary>Plan D4 target: p95 &lt; 500 ms for 3 filters + 2 group-bys on 5M messages (opt-in: OMNISCIENCE_PERF=1).</summary>
        [PerfFact]
        public async Task FiveMillionRows_ThreeFiltersTwoGroupBysUnderBudget()
        {
            const int rows = 5_000_000;
            var rng = new Random(5);
            string[] kinds = { "dm", "group_dm", "guild_channel" }, sentiments = { "negative", "neutral", "positive", null! }, platforms = { "discord", "whatsapp" };
            var sw = Stopwatch.StartNew();
            using (var appender = explore.Db.CreateAppender("messages"))
            {
                long start = clock.NowMs - 730L * 86_400_000;
                for (int i = 0; i < rows; i++)
                {
                    var row = appender.CreateRow().AppendValue("perf:" + i).AppendValue(start + (long)(rng.NextDouble() * 730 * 86_400_000L)).AppendValue(clock.NowMs)
                        .AppendValue((long?)null).AppendValue((long?)null).AppendValue((sbyte)0).AppendValue("synth-p" + rng.Next(3000)).AppendValue((string?)null)
                        .AppendValue("conv" + rng.Next(800)).AppendValue("guild" + rng.Next(25)).AppendValue(kinds[rng.Next(3)]).AppendValue(platforms[rng.Next(2)])
                        .AppendValue("synthetic").AppendValue(rng.Next(400)).AppendValue((string?)null).AppendValue((string?)null).AppendValue(rng.Next(7) == 0)
                        .AppendValue(false).AppendValue(false).AppendValue(false).AppendValue(rng.Next(50) == 0).AppendValue("en").AppendValue(sentiments[rng.Next(4)]);
                    for (int k = 0; k < 12; k++) row = row.AppendValue((string?)null);
                    row.AppendValue(false).AppendValue(1).AppendValue("Europe/London").AppendValue(0).EndRow();
                }
            }
            output.WriteLine($"appended {rows:N0} rows in {sw.Elapsed}");
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                var q = new ExploreQuery("messages", new[]
                {
                    F("where.kind", FilterOp.In, "dm", "guild_channel"), F("content.sentiment", FilterOp.IsNotNull), F("time.occurred", FilterOp.Gte, clock.NowMs - (30 + i * 10) * 86_400_000L),
                }, new[] { "where.platform", "content.sentiment" }, new[] { "count", "people" }, TimeBucket.Month, Lens: TimeLens.Klives);
                sw.Restart();
                await explore.QueryAsync(q, All, CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
            output.WriteLine($"p50 {times[times.Count / 2]:0} ms, p95 {p95:0} ms");
            Assert.True(p95 < 500, $"p95 {p95:0} ms");
        }
    }
}
