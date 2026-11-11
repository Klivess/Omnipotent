using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// What the overview build reads from the request log. Each read must cost the window it asks
    /// about (the last day, the last week), however long the log has grown.
    ///
    /// Cost is counted in database pages: SQLite's page-cache misses on a fresh connection, which
    /// is what turns into disk reads on the server. Timings on a development SSD hide the problem;
    /// page counts do not.
    /// </summary>
    public sealed class OverviewQueryCostTests : IDisposable
    {
        /// <summary>"Busiest routes today" as it was deployed through cb85839, with the index left to the planner.</summary>
        private const string TopRoutesLeftToThePlanner =
            "SELECT route, COUNT(*) AS hits FROM requests WHERE utc_ts >= $t GROUP BY route ORDER BY hits DESC LIMIT 10";

        private const long Now = 1_794_001_234; // deliberately not on an hour boundary
        private const long Day = Now - 86400;
        private const long Week = Now - 7 * 86400;

        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-cost-" + Guid.NewGuid().ToString("N") + ".db");
        private int inserted;

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

        private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());

        /// <summary>A row the size of a real audit row: a user agent and a block of headers.</summary>
        private static RequestRow Sized(long ts, int i) => new()
        {
            UtcTimestamp = ts,
            Ip = $"198.51.{i % 2000 / 250}.{i % 250}",
            Method = "GET",
            Route = "/r/" + (i * 7919 % 40),
            StatusCode = i % 9 == 0 ? 404 : 200,
            MatchedRoute = true,
            ProfileId = i % 3 == 0 ? "p" + (i % 5) : null,
            ProfileName = i % 3 == 0 ? "name" : null,
            UserAgent = new string('u', 110),
            HeadersJson = new string('h', 700),
        };

        /// <summary>Appends rows older than the week, in time order, the way the log grows.</summary>
        private async Task AddOldRowsAsync(OmniDefenceStore store, int count)
        {
            for (int done = 0; done < count; done += 5000)
            {
                var batch = new List<RequestRow>(5000);
                for (int k = 0; k < 5000 && done + k < count; k++, inserted++)
                    batch.Add(Sized(Week - 180 * 86400L + inserted * 30L, inserted));
                await store.InsertRequestsAsync(batch);
            }
        }

        /// <summary>600 rows this week, 200 of them today.</summary>
        private static async Task AddThisWeekAsync(OmniDefenceStore store)
        {
            var recent = new List<RequestRow>(600);
            for (int k = 0; k < 600; k++) recent.Add(Sized(k < 400 ? Week + 100 + k : Day + 100 + k, k));
            await store.InsertRequestsAsync(recent);
        }

        private async Task<(int Pages, List<string> Plan)> CostAsync(string sql, params (string Name, object Value)[] parameters)
        {
            using var conn = Open();
            await conn.OpenAsync();
            var plan = new List<string>();
            using (var explain = conn.CreateCommand())
            {
                explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
                foreach (var (name, value) in parameters) explain.Parameters.AddWithValue(name, value);
                using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            }
            SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0);
            return (pages, plan);
        }

        private async Task<long> ScalarAsync(string sql)
        {
            using var conn = Open();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private (string Name, string Sql, (string, object)[] Parameters)[] OverviewReads(long maxId) => new[]
        {
            ("requests in the last 7 days", OmniDefenceRoutes.OverviewWindowSql, new (string, object)[] { ("$week", Week) }),
            ("busiest routes today", OmniDefenceRoutes.OverviewTopRoutesSql, new (string, object)[] { ("$t", Day) }),
            ("request origins today", OmniDefenceRoutes.OverviewOriginsSql, new (string, object)[] { ("$t", Day) }),
            // Start-up's profile-link pass over the newest 600 rows.
            ("profile links", OmniDefenceStore.ProfileLinkRowsSql, new (string, object)[] { ("$after", maxId - 600), ("$through", maxId) }),
        };

        [Fact]
        public async Task OverviewReads_CostTheirWindow_HoweverLongTheLogHasGrown()
        {
            var store = await NewStoreAsync();
            await AddOldRowsAsync(store, 20_000);
            await AddThisWeekAsync(store);
            // The window sits at the end of the log, as it does in production; the log then doubles
            // in front of it (older ids cannot be appended, so the window is re-added after).
            var small = new Dictionary<string, int>();
            foreach (var (name, sql, parameters) in OverviewReads(await ScalarAsync("SELECT MAX(id) FROM requests")))
                small[name] = (await CostAsync(sql, parameters)).Pages;
            long smallDatabase = await ScalarAsync("PRAGMA page_count");

            await store.WithLockAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM requests WHERE utc_ts >= $week";
                cmd.Parameters.AddWithValue("$week", Week);
                await cmd.ExecuteNonQueryAsync();
            });
            await AddOldRowsAsync(store, 20_000);
            await AddThisWeekAsync(store);
            long bigDatabase = await ScalarAsync("PRAGMA page_count");
            Assert.True(bigDatabase > smallDatabase * 1.8, $"the log should have doubled: {smallDatabase} -> {bigDatabase} pages");

            foreach (var (name, sql, parameters) in OverviewReads(await ScalarAsync("SELECT MAX(id) FROM requests")))
            {
                var (pages, plan) = await CostAsync(sql, parameters);
                string detail = $"'{name}' read {small[name]} pages of {smallDatabase}, then {pages} of {bigDatabase}. Plan: {string.Join(" / ", plan)}";
                // A window of 600 rows is a few hundred pages at most; a pass over the log is thousands.
                Assert.True(pages < bigDatabase / 20, detail);
                // Twice the log, the same window: the same work, give or take a level of B-tree.
                Assert.True(pages <= small[name] + 16, detail);
            }
            await store.ShutdownAsync();
        }

        [Fact]
        public async Task LeftToThePlanner_BusiestRoutesFetchesEveryRowOfTheLog()
        {
            // The cause of the hours-long builds, reproduced. SQLite has no statistics for this
            // database, so it cannot tell that "utc_ts >= ?" keeps only the newest rows, and it
            // prefers to walk the route index (the rows come out already grouped). That walk visits
            // every row ever logged, in route order, and fetches each from the table to test utc_ts.
            var store = await NewStoreAsync();
            await AddOldRowsAsync(store, 20_000);
            await AddThisWeekAsync(store);
            long rows = await ScalarAsync("SELECT COUNT(*) FROM requests");
            var (wholeTable, _) = await CostAsync("SELECT COUNT(*), SUM(status_code) FROM requests NOT INDEXED");

            var (unpinned, plan) = await CostAsync(TopRoutesLeftToThePlanner, ("$t", Day));
            var (pinned, _) = await CostAsync(OmniDefenceRoutes.OverviewTopRoutesSql, ("$t", Day));

            string detail = $"{rows} rows; whole table in order {wholeTable} pages; left to the planner {unpinned}; pinned {pinned}. Plan: {string.Join(" / ", plan)}";
            // If this fails because the planner chose the time index by itself, the engine has
            // changed and INDEXED BY may no longer be needed. Keep it until that is measured.
            Assert.Contains(plan, step => step.Contains("ix_requests_route", StringComparison.Ordinal));
            // About one page per row of the log, and several times a plain pass over the table...
            Assert.True(unpinned > rows * 0.9, detail);
            Assert.True(unpinned > wholeTable * 3, detail);
            // ...against a few dozen for the same answer through the time index.
            Assert.True(pinned * 100 < unpinned, detail);
            await store.ShutdownAsync();
        }

        [Fact]
        public async Task OverviewReads_SearchTheTimeIndex_AndNeverScanTheLog()
        {
            await NewStoreAsync();
            foreach (var (name, sql, parameters) in OverviewReads(1000).Take(3))
            {
                var (_, plan) = await CostAsync(sql, parameters);
                string detail = $"'{name}': {string.Join(" / ", plan)}";
                Assert.True(plan.Any(step => step.Contains("SEARCH requests USING INDEX ix_requests_ts (utc_ts>?)", StringComparison.Ordinal)), detail);
                Assert.False(plan.Any(step => step.StartsWith("SCAN requests", StringComparison.Ordinal)), detail);
            }
        }

        private static RequestRow At(long ts, int status = 200, string? deny = null, string route = "/ping") => new()
        {
            UtcTimestamp = ts, Ip = "192.0.2.1", Method = "GET", Route = route, StatusCode = status, MatchedRoute = true, DenyReason = deny
        };

        [Fact]
        public async Task RequestWindow_GivesTheWeekByHour_AndTheLastDayToTheSecond()
        {
            var store = await NewStoreAsync();
            var rows = new[]
            {
                At(Week - 10),                         // just outside the week
                At(Week),                              // the week's first second
                At(Week + 5, 404),
                At(Day - 1),                           // same hour as the next two, but yesterday
                At(Day),                               // the day's first second
                At(Day + 1, 404),
                At(Now - 4000, 403, "IPBlocked"),
                At(Now - 20),
                At(Now - 10, 200, "Honeypot"),         // answered 200, still denied
            };
            await store.InsertRequestsAsync(rows);

            var window = await OmniDefenceRoutes.ReadRequestWindowAsync(store, Week, Day);

            static bool Denied(RequestRow r) => r.DenyReason != null || r.StatusCode >= 400;
            var expected = rows.Where(r => r.UtcTimestamp >= Week)
                .GroupBy(r => r.UtcTimestamp / 3600 * 3600).OrderBy(g => g.Key)
                .Select(g => new OmniDefenceRoutes.HourOfRequests(g.Key, g.Count(), g.Count(Denied))).ToList();
            Assert.Equal(expected, window.Hourly);
            // The hour that straddles the day boundary is one bucket, yet the 24-hour counts split it.
            Assert.Contains(window.Hourly, hour => hour.H == Day / 3600 * 3600 && hour.Requests == 3);
            Assert.Equal(8, window.Requests7d);
            Assert.Equal(5, window.Requests24h);
            Assert.Equal(3, window.Denied24h);
            Assert.Equal(window.Requests7d, window.Hourly.Sum(hour => hour.Requests));
        }

        [Fact]
        public async Task RequestWindow_IsEmpty_WhenNothingArrivedThisWeek()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[] { At(Week - 86400) });

            var window = await OmniDefenceRoutes.ReadRequestWindowAsync(store, Week, Day);

            Assert.Equal(new OmniDefenceRoutes.RequestWindow(0, 0, 0, window.Hourly), window);
            Assert.Empty(window.Hourly);
        }

        [Fact]
        public async Task TheWholeBuild_AgainstARealDatabase_CountsItsWindowsAndTimesEveryStep()
        {
            var store = await NewStoreAsync();
            var rows = new List<RequestRow>();
            for (int i = 0; i < 40; i++) rows.Add(At(Week - 86400 - i, route: "/long-ago"));
            rows.AddRange(new[]
            {
                At(Week + 60, route: "/b"), At(Week + 3600, 404, route: "/b"),                                // this week
                At(Day + 30, route: "/a"), At(Day + 40, route: "/a"), At(Day + 50, 403, "IPBlocked", "/b"),  // today
            });
            await store.InsertRequestsAsync(rows);
            foreach (var (ts, type) in new[] { (Day + 5, "UnauthRoute"), (Day + 6, "InvalidPassword"), (Day + 7, "Login"), (Week + 9, "UnauthRoute") })
                await store.InsertAuthEventAsync(new AuthEventRow { UtcTimestamp = ts, Ip = "192.0.2.1", Type = type });

            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();
            tracker.SetStatus("203.0.113.1", IpThreatTracker.IpStatus.Blocked, "test");
            tracker.SetStatus("203.0.113.2", IpThreatTracker.IpStatus.Watch, null);
            tracker.GetOrCreate("203.0.113.3");

            var reported = new List<string>();
            string json = await OmniDefenceRoutes.BuildOverviewJsonAsync(store, tracker, reported.Add,
                lastBuildMs: 1234, fingerprintBacklog: () => 7, nowUtc: Now);
            var overview = JObject.Parse(json);

            Assert.Equal(3, overview.Value<long>("requests24h"));
            Assert.Equal(5, overview.Value<long>("requests7d"));
            Assert.Equal(1, overview.Value<long>("denied24h"));
            Assert.Equal(45, overview.Value<long>("totalRequests"));
            Assert.Equal(2, overview.Value<long>("totalDenied"));
            Assert.Equal(2, overview.Value<long>("unauth24h"));
            Assert.Equal(4, overview.Value<long>("totalAuthEvents"));
            Assert.Equal(1, overview.Value<int>("blockedIps"));
            Assert.Equal(1, overview.Value<int>("watchedIps"));
            Assert.Equal(3, overview.Value<int>("totalIps"));
            Assert.Equal(new[] { "/a:2", "/b:1" },
                overview["topRoutes"]!.Select(r => $"{r.Value<string>("route")}:{r.Value<long>("hits")}"));
            Assert.Equal(new[] { "DirectApi:3" },
                overview["originBreakdown"]!.Select(o => $"{o.Value<string>("origin")}:{o.Value<long>("hits")}"));
            Assert.NotNull(overview["review"]!["hostileAnsweredCount"]);

            // The week by hour: every request once, oldest hour first, each hour on the hour.
            var hours = overview["hourly"]!.ToArray();
            Assert.Equal(5, hours.Sum(h => h.Value<long>("requests")));
            Assert.Equal(2, hours.Sum(h => h.Value<long>("denied")));
            Assert.All(hours, h => Assert.Equal(0, h.Value<long>("h") % 3600));
            Assert.Equal(hours.Select(h => h.Value<long>("h")).OrderBy(h => h), hours.Select(h => h.Value<long>("h")));

            // Every step named as it starts and timed in the health block, in build order.
            string[] steps =
            {
                "requests in the last 7 days", "all-time request totals", "auth events", "IP records",
                "busiest routes today", "request origins today", "class breakdown", "tag breakdown", "review lists"
            };
            Assert.Equal(steps, ((JObject)overview["health"]!["buildSteps"]!).Properties().Select(p => p.Name));
            Assert.Equal(steps.Append("writing the overview"), reported);
            Assert.Equal(1234, overview["health"]!.Value<long>("overviewBuildMs"));
            Assert.Equal(7, overview["health"]!.Value<int>("fingerprintBacklog"));
        }

        [Fact]
        public async Task TopRoutes_AndOrigins_CountOnlyRowsSinceTheCutoff()
        {
            var store = await NewStoreAsync();
            var rows = new List<RequestRow>();
            for (int i = 0; i < 50; i++) rows.Add(At(Day - 100 - i, route: "/yesterday"));
            rows.AddRange(new[]
            {
                At(Day + 10, route: "/a"), At(Day + 11, route: "/a"), At(Day + 12, route: "/a"),
                At(Day + 13, route: "/c"), At(Day + 14, route: "/c"),
                At(Day + 15, route: "/b"), At(Day + 16, route: "/b"),
            });
            rows[50].RequestOrigin = "WebsiteProfile";                      // recorded outright
            rows[51].ClientPage = "/home";                                  // a page, no profile
            rows[52].ClientPage = "/home"; rows[52].ProfileId = "p1";       // a page and a profile
            rows[53].ProfileId = "p1";                                      // a profile, no page
            await store.InsertRequestsAsync(rows);

            var top = await store.QueryAsync(OmniDefenceRoutes.OverviewTopRoutesSql, new() { ["$t"] = Day });
            Assert.Equal(new[] { ("/a", 3L), ("/b", 2L), ("/c", 2L) },
                top.Select(r => ((string)r["route"]!, Convert.ToInt64(r["hits"]))));

            var origins = await store.QueryAsync(OmniDefenceRoutes.OverviewOriginsSql, new() { ["$t"] = Day });
            Assert.Equal(new Dictionary<string, long>
            {
                ["DirectApi"] = 3, ["WebsiteProfile"] = 2, ["WebsiteNoProfile"] = 1, ["DirectApiProfile"] = 1
            }, origins.ToDictionary(r => (string)r["origin"]!, r => Convert.ToInt64(r["hits"])));
            Assert.Equal("DirectApi", origins[0]["origin"]);
        }
    }
}
