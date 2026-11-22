using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Modules.Explore;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The jobs that follow the whole archive (the Explore copy, the rollups) work for a few minutes, save where they
    /// got to and end, so the lane's other work runs between them. On the server one step of the copy took 22 minutes
    /// and then more than 55, one batch of rollup cells 23 to 46: the time limit was only looked at between steps, so
    /// those jobs held both workers of the lane, said nothing for hours, were stopped for "no progress" and began the same
    /// step again, while the health check waited six hours behind them (21 Nov 2026).
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class BackfillPacingTests
    {
        private const string Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Self = SignalsHarness.Self;
        private const string General = SignalsHarness.General, DmAna = SignalsHarness.DmAna, Group = SignalsHarness.Group;
        private const long Day = 86_400_000L;

        private static bool All(string _) => true;

        /// <summary>The same small archive every time: three people over eight days, in a server channel, a direct chat and a group.</summary>
        private static async Task PostArchiveAsync(SignalsHarness h, int messages)
        {
            long t0 = h.Clock.NowMs - 9 * Day;
            var lastIn = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < messages; i++)
            {
                string conversation = (i % 5) switch { 0 => DmAna, 1 => Group, _ => General };
                string author = conversation == DmAna ? (i % 2 == 0 ? Ana : Self) : (i % 3) switch { 0 => Ana, 1 => Ben, _ => Self };
                lastIn[conversation] = await h.PostAsync(conversation, author, "line " + i.ToString(CultureInfo.InvariantCulture) + (i % 7 == 0 ? " see https://example.test/a" : ""),
                    sentAt: t0 + i * (8 * Day / messages), replyTo: i % 4 == 3 ? lastIn.GetValueOrDefault(conversation) : null, mention: i % 11 == 0 && author != Self ? Self : null);
            }
        }

        private static long Scalar(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<long?>(null, sql, args) ?? 0;
        }

        private static async Task<long> MirroredAsync(SignalsHarness h)
        {
            var r = await h.Explore!.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), new[] { "where.kind" }, new[] { "count" }), All, CancellationToken.None);
            return r.Rows.Sum(row => row[1].GetInt64());
        }

        private static List<JsonElement> Results(SignalsHarness h, string module, string kind) =>
            h.Driver.Ran.Where(r => r.Module == module && r.Kind == kind && r.Result != null).Select(r => JsonDocument.Parse(r.Result!).RootElement).ToList();

        // ── the Explore copy ─────────────────────────────────────────────────────

        [Fact]
        public async Task TheCopy_EndsWhenItsTimeIsUp_WithItsPlaceSaved_AndTheJobsAfterItFinishIt()
        {
            var before = (ExploreModule.ChangeBatch, ExploreModule.Budget);
            (ExploreModule.ChangeBatch, ExploreModule.Budget) = (100, TimeSpan.Zero); // a job's time is up after its first step
            try
            {
                await using var h = await SignalsHarness.StartAsync(explore: true);
                await PostArchiveAsync(h, 350);
                var corpus = h.Runtime.Stores.Corpus;
                long head = Scalar(corpus, "SELECT MAX(seq) FROM change_log"), firstStep = Scalar(corpus, "SELECT seq FROM change_log ORDER BY seq LIMIT 1 OFFSET 99");
                long humans = Scalar(corpus, "SELECT COUNT(*) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.is_bot=0 AND m.is_system=0 AND i.kind<>'bot'");
                Assert.Equal(350, humans);

                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();

                // The first job took one step of a hundred changes, said where it stopped and how far there is to go, and
                // what it copied can already be asked about.
                var first = Results(h, ExploreModule.ModuleId, "mirror")[0];
                Assert.Equal((firstStep, head), (first.GetProperty("cursor").GetInt64(), first.GetProperty("maxSeq").GetInt64()));
                Assert.InRange(await MirroredAsync(h), 100, 349);

                // Its schedule brings the next one within half a minute, and each carries on from where the last one ended.
                for (int round = 0; round < 12 && Results(h, ExploreModule.ModuleId, "mirror")[^1].GetProperty("cursor").GetInt64() < head; round++)
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(31));
                    await h.Driver.RunUntilIdleAsync(2_000);
                    h.Driver.AssertNoFailures();
                }
                var cursors = Results(h, ExploreModule.ModuleId, "mirror").Select(r => r.GetProperty("cursor").GetInt64()).ToList();
                Assert.Equal(head, cursors[^1]);
                Assert.True(cursors.Count >= 4, $"{cursors.Count} jobs"); // 350 changes, a hundred a job
                Assert.Equal(cursors.OrderBy(c => c), cursors);
                Assert.Equal(humans, await MirroredAsync(h));
            }
            finally
            {
                (ExploreModule.ChangeBatch, ExploreModule.Budget) = before;
            }
        }

        [Fact]
        public async Task WithABacklogOfJobs_TheJobsDatasetIsCopiedWholeOnlyNowAndThen()
        {
            long before = ExploreModule.LargeJobs;
            ExploreModule.LargeJobs = 0; // any number of jobs is "a backlog" here; on the server it was a million rows, read every five minutes
            try
            {
                await using var h = await SignalsHarness.StartAsync(explore: true);
                await PostArchiveAsync(h, 20);
                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();
                // The first copy is always made: the dataset is never left empty.
                Assert.True(Assert.Single(Results(h, ExploreModule.ModuleId, "engine")).GetProperty("jobs").GetInt32() > 0);

                h.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();
                var engine = Results(h, ExploreModule.ModuleId, "engine");
                Assert.Equal(2, engine.Count);
                Assert.Equal(0, engine[1].GetProperty("jobs").GetInt32()); // five minutes on: not due

                h.Clock.Advance(ExploreModule.LargeJobsEvery);
                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();
                Assert.True(Results(h, ExploreModule.ModuleId, "engine")[^1].GetProperty("jobs").GetInt32() > 0); // six hours on: copied again
            }
            finally
            {
                ExploreModule.LargeJobs = before;
            }
        }

        // ── the rollups ──────────────────────────────────────────────────────────

        /// <summary>Every rollup cell as text, without the columns that say when or from which change it was built, and
        /// without the classifier's labels (how far the classifier has got by then is not what these tests are about).</summary>
        private static List<string> Cells(SignalsHarness h)
        {
            var rows = new List<string>();
            using var c = h.Runtime.Stores.Intel.OpenRead();
            foreach (string table in new[] { "rollup_person_day", "rollup_pair_day", "rollup_scope_day", "rollup_slots" })
                rows.AddRange(c.Query(null, $"SELECT * FROM {table}", r =>
                {
                    var sb = new StringBuilder(table);
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        if (r.GetName(i) is "computed_at_ms" or "source_seq" or "labels" or "labelset" or "klives") continue;
                        sb.Append('|').Append(r.GetName(i)).Append('=').Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture));
                    }
                    return sb.ToString();
                }));
            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        [Fact]
        public async Task Drains_LookAtTheTimeAfterEveryCell_AndTogetherBuildWhatOneLongDrainBuilds()
        {
            // What one drain builds with all the time it wants.
            List<string> whole;
            await using (var h = await SignalsHarness.StartAsync(rollups: true))
            {
                await PostArchiveAsync(h, 120);
                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();
                whole = Cells(h);
                Assert.True(whole.Count > 30, $"{whole.Count} cells");
                Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_dirty"));
            }

            var before = RollupModule.Budget;
            RollupModule.Budget = TimeSpan.Zero; // a drain's time is up after its first cell
            try
            {
                await using var h = await SignalsHarness.StartAsync(rollups: true);
                await PostArchiveAsync(h, 120);
                await h.Driver.RunUntilIdleAsync(2_000);
                h.Driver.AssertNoFailures();

                // The first drain marked what the changes touch and built a cell or two (one each time it turns to
                // building), not the whole batch of them; the rest are still marked, so nothing is lost by ending.
                var first = Results(h, RollupModule.ModuleId, "drain")[0];
                Assert.InRange(first.GetProperty("built").GetInt32(), 1, 2);
                Assert.True(first.GetProperty("marked").GetInt32() > 30);
                Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_dirty") > 20);

                // Its schedule brings the next within half a minute; each builds a little more, until none is left.
                for (int round = 0; round < 400 && h.Intel("SELECT COUNT(*) FROM rollup_dirty") > 0; round++)
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(31));
                    await h.Driver.RunUntilIdleAsync(2_000);
                    h.Driver.AssertNoFailures();
                }
                Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_dirty"));
                var drains = Results(h, RollupModule.ModuleId, "drain");
                Assert.All(drains, d => Assert.InRange(d.GetProperty("built").GetInt32(), 0, 2));
                Assert.True(drains.Count > 15, $"{drains.Count} drains");
                var cells = Cells(h);
                var different = whole.Except(cells, StringComparer.Ordinal).Select(c => (Row: c, From: "one drain:   "))
                    .Concat(cells.Except(whole, StringComparer.Ordinal).Select(c => (Row: c, From: "many drains: ")))
                    .OrderBy(x => x.Row, StringComparer.Ordinal).Take(6).Select(x => x.From + x.Row).ToList();
                Assert.True(different.Count == 0, string.Join("\n", different));
                Assert.Equal(whole.Count, cells.Count);
            }
            finally
            {
                RollupModule.Budget = before;
            }
        }
    }
}
