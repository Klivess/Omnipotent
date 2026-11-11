using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// What OmniDefence does before its routes exist. On 10 Nov 2026 they were still missing 82 and
    /// 92 minutes after two starts: start-up awaited one query that linked IPs to profiles by
    /// grouping the whole request log, which SQLite ran as a walk of an IP index with a table lookup
    /// for every row. The links are now read from a watermark, after start-up, and start-up times
    /// each of its steps.
    /// </summary>
    public class StartupCostTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-start-" + Guid.NewGuid().ToString("N") + ".db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        private async Task<OmniDefenceStore> NewStoreAsync()
        {
            var store = new OmniDefenceStore(dbPath);
            await store.InitializeAsync();
            return store;
        }

        private static RequestRow Audit(string ip, long ts, string? profile = null, int? rank = null) => new()
        {
            UtcTimestamp = ts, Ip = ip, Method = "GET", Route = "/ping", StatusCode = 200, MatchedRoute = true,
            ProfileId = profile, ProfileName = profile == null ? null : "name-" + profile, ProfileRank = rank
        };

        private static async Task<IpThreatTracker> StartedTrackerAsync(OmniDefenceStore store)
        {
            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();
            return tracker;
        }

        [Fact]
        public async Task ProfileLinks_AreReadFromTheWatermark_NotTheWholeLogAgain()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[]
            {
                Audit("198.51.100.1", 1000, "p1", 5),
                Audit("198.51.100.2", 1100, "p2", 1),
                Audit("198.51.100.3", 1200),
            });

            var first = await (await StartedTrackerAsync(store)).CatchUpProfileLinksAsync();
            Assert.Equal(new IpThreatTracker.ProfileLinkPass(0, 3, 2), first);
            Assert.Equal("3", await store.GetSettingAsync(OmniDefenceStore.ProfileLinkWatermarkKey));

            // Rewrite a row the first pass has already read. A pass over the whole log, which is
            // what every start used to run, would now move 198.51.100.1 to "rewritten".
            await store.WithLockAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE requests SET profile_id = 'rewritten', utc_ts = 9000 WHERE id = 1";
                await cmd.ExecuteNonQueryAsync();
            });
            await store.InsertRequestsAsync(new[] { Audit("198.51.100.3", 1300, "p3", 2) });

            var restarted = await StartedTrackerAsync(store);
            var second = await restarted.CatchUpProfileLinksAsync();

            Assert.Equal(new IpThreatTracker.ProfileLinkPass(3, 4, 1), second);
            Assert.Equal("p1", restarted.Get("198.51.100.1")!.AssociatedProfileId);
            Assert.Equal(5, restarted.Get("198.51.100.1")!.AssociatedProfileRank);
            Assert.Equal("p2", restarted.Get("198.51.100.2")!.AssociatedProfileId);
            Assert.Equal("p3", restarted.Get("198.51.100.3")!.AssociatedProfileId);

            // Nothing new: the next start reads no rows at all.
            var third = await (await StartedTrackerAsync(store)).CatchUpProfileLinksAsync();
            Assert.Equal(new IpThreatTracker.ProfileLinkPass(4, 4, 0), third);
        }

        [Fact]
        public async Task ProfileLinks_TheLatestRequestWins_AcrossSpansAndSurvivesAReload()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[]
            {
                Audit("203.0.113.7", 100, "early", 1),
                Audit("203.0.113.7", 300, "latest", 5),
                Audit("203.0.113.7", 400),                 // no profile: links nothing
                Audit("203.0.113.7", 200, "out-of-order", 2),
                Audit("203.0.113.8", 250, "other", 3),
            });

            // Two ids per read, so the rows for one IP arrive in three separate reads.
            var pass = await (await StartedTrackerAsync(store)).CatchUpProfileLinksAsync(span: 2);
            Assert.Equal(new IpThreatTracker.ProfileLinkPass(0, 5, 4), pass);

            var reloaded = await StartedTrackerAsync(store);
            Assert.Equal("latest", reloaded.Get("203.0.113.7")!.AssociatedProfileId);
            Assert.Equal("name-latest", reloaded.Get("203.0.113.7")!.AssociatedProfileName);
            Assert.Equal(300, reloaded.Get("203.0.113.7")!.AssociatedProfileLastSeenUtc);
            Assert.True(reloaded.IsLinkedToKlives("203.0.113.7"));
            Assert.Equal("other", reloaded.Get("203.0.113.8")!.AssociatedProfileId);
        }

        [Fact]
        public async Task ProfileLinks_StartOver_WhenTheWatermarkIsPastTheEndOfTheLog()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[] { Audit("198.51.100.9", 500, "p9", 1) });
            await store.SetSettingAsync(OmniDefenceStore.ProfileLinkWatermarkKey, "999");

            var tracker = await StartedTrackerAsync(store);
            var pass = await tracker.CatchUpProfileLinksAsync();

            Assert.Equal(new IpThreatTracker.ProfileLinkPass(0, 1, 1), pass);
            Assert.Equal("p9", tracker.Get("198.51.100.9")!.AssociatedProfileId);
        }

        [Fact]
        public async Task ProfileLinkRead_WalksTheTableInStoredOrder_ThroughNoIndex()
        {
            await NewStoreAsync();
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + OmniDefenceStore.ProfileLinkRowsSql;
            cmd.Parameters.AddWithValue("$after", 0);
            cmd.Parameters.AddWithValue("$through", 20_000);
            var plan = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            }

            string step = Assert.Single(plan);
            Assert.Contains("USING INTEGER PRIMARY KEY (rowid>? AND rowid<?)", step);
        }

        [Fact]
        public async Task StartupTrace_NamesTheStepUnderWay_ThenKeepsEachStepsTime()
        {
            DateTimeOffset now = new(2026, 11, 10, 21, 10, 58, TimeSpan.Zero);
            var trace = new OmniDefenceStartupTrace(() => now);
            Assert.False(trace.Running);

            trace.Begin();
            var gate = new TaskCompletionSource();
            await trace.Step("opening the store", () => Task.CompletedTask);
            Task loading = trace.Step("IP records", () => gate.Task);

            // Mid-start: the step it is in, and how long the start has run so far.
            now = now.AddMinutes(25);
            Assert.True(trace.Running);
            Assert.Equal("IP records", trace.CurrentStep);
            var during = JObject.FromObject(trace.Status());
            Assert.False(during.Value<bool>("ready"));
            Assert.Equal("IP records", during.Value<string>("step"));
            Assert.Equal(1500, during.Value<double>("seconds"));
            Assert.Equal(new[] { "opening the store" }, ((JObject)during["steps"]!).Properties().Select(p => p.Name));

            gate.SetResult();
            await loading;
            now = now.AddSeconds(2);
            trace.Ready();

            Assert.False(trace.Running);
            var after = JObject.FromObject(trace.Status());
            Assert.True(after.Value<bool>("ready"));
            Assert.Null(after.Value<string>("step"));
            Assert.Equal(1502, after.Value<double>("seconds"));
            // Steps in the order they ran, each with its milliseconds.
            Assert.Equal(new[] { "opening the store", "IP records" }, ((JObject)after["steps"]!).Properties().Select(p => p.Name));
            Assert.StartsWith("1502.0 s (", trace.Summary());
        }

        [Fact]
        public async Task StartupTrace_AFailedStepIsStillTimed_AndTheStartReportsItsError()
        {
            var trace = new OmniDefenceStartupTrace();
            trace.Begin();
            var failure = new InvalidOperationException("database is locked");
            await Assert.ThrowsAsync<InvalidOperationException>(() => trace.Step("opening the store", () => throw failure));
            trace.Failed(failure);

            var status = JObject.FromObject(trace.Status());
            Assert.False(status.Value<bool>("ready"));
            Assert.Equal("database is locked", status.Value<string>("error"));
            Assert.Equal("opening the store", status.Value<string>("step"));
            Assert.NotNull(status["steps"]!["opening the store"]);
            Assert.False(trace.Running);
        }
    }
}
