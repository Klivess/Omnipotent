using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Activity;
using Omnipotent.Profiles.Permissions;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Profiles;

/// <summary>
/// The profile console's reads after the 13 Nov 2026 rework. Live, a 30-day summary took up to
/// 48.7 s (p95 22.9 s), "refused only" 11.2 s and permission usage 2.8–3.9 s: every one read a page
/// per row of a profile whose rows sit among the owner's. These tests hold the reads to the rows
/// they need: summaries from hourly rollups (identical to the raw-row answer), refusals through a
/// partial index, usage through a covering index, and a backfill that resumes without counting
/// anything twice.
/// </summary>
public sealed class ActivityRollupTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-rollup-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(dir, "activity.db");

    public ActivityRollupTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    /// <summary>An hour boundary, so the rollups' hour-aligned window and the raw rows' window agree.</summary>
    private static readonly DateTimeOffset Now = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, TimeSpan.Zero);

    private static readonly (string Method, string Route, string Service, string Perm)[] Routes =
    {
        ("GET", "/omnitrader/status", "OmniTrader", "omnitrader.status.view"),
        ("POST", "/omnitrader/orders", "OmniTrader", "omnitrader.orders.place"),
        ("GET", "/omnitrader/orders", "OmniTrader", "omnitrader.orders.view"),
        ("GET", "/KliveCloud/Browse", "KliveCloud", "klivecloud.files.browse"),
        ("GET", "/KMProfiles/me", "Account", ""),
        ("GET", "/klivemail/messages", "KliveMail", "klivemail.messages.read"),
    };

    /// <summary>Requests the way the site makes them: a route decides service and permission; some are refused or fail.</summary>
    private static List<ActivityRequest> Traffic(string profile, int count, int seed, TimeSpan span, bool password = false)
    {
        var random = new Random(seed);
        string?[] ips = { "1.1.1.1", "2.2.2.2", "", null };
        return Enumerable.Range(0, count).Select(_ =>
        {
            var r = Routes[random.Next(Routes.Length)];
            return new ActivityRequest
            {
                ProfileId = profile,
                TsMs = Ms(Now) - 1 - (long)(random.NextDouble() * span.TotalMilliseconds),
                SessionId = password || random.Next(5) == 0 ? null : "s1",
                Method = r.Method,
                Route = r.Route,
                Service = r.Service,
                PermKey = r.Perm.Length == 0 ? null : r.Perm,
                Status = new[] { 200, 200, 200, 200, 403, 401, 500 }[random.Next(7)],
                Ip = ips[random.Next(ips.Length)],
            };
        }).ToList();
    }

    private ProfileActivityStore NewStore(bool autoUpgrade = true, int chunk = 5_000)
    {
        var store = new ProfileActivityStore(DbPath, _ => { }) { AutoUpgrade = autoUpgrade, BackfillChunk = chunk };
        store.Start();
        return store;
    }

    private static void AssertSameSummary(ProfileActivityStore.ActivitySummary expected, ProfileActivityStore.ActivitySummary actual)
    {
        Assert.Equal(expected.FromMs, actual.FromMs);
        Assert.Equal(expected.Requests, actual.Requests);
        Assert.Equal(expected.Denied, actual.Denied);
        Assert.Equal(expected.Errors, actual.Errors);
        Assert.Equal(expected.PasswordRequests, actual.PasswordRequests);
        Assert.Equal(expected.DistinctRoutes, actual.DistinctRoutes);
        Assert.Equal(expected.Services, actual.Services);
        Assert.Equal(expected.Buckets.Select(b => (b.T, b.Denied, string.Join(",", b.ByService.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")))),
            actual.Buckets.Select(b => (b.T, b.Denied, string.Join(",", b.ByService.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")))));
        Assert.Equal(expected.TopPermissions.Select(k => (k.Key, k.Count, k.Denied, k.LastMs)), actual.TopPermissions.Select(k => (k.Key, k.Count, k.Denied, k.LastMs)));
        Assert.Equal(expected.TopRoutes.Select(k => (k.Key, k.Count)), actual.TopRoutes.Select(k => (k.Key, k.Count)));
        Assert.Equal(expected.Ips.Select(i => (i.Ip, i.Count, i.FirstMs, i.LastMs)), actual.Ips.Select(i => (i.Ip, i.Count, i.FirstMs, i.LastMs)));
        Assert.Equal(expected.RecentDenials.Select(d => d.TsMs), actual.RecentDenials.Select(d => d.TsMs));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(24 * 7)]
    [InlineData(24 * 30)]
    public void RollupSummary_IsTheRawRowSummary(int hours)
    {
        using var store = NewStore();
        Assert.True(store.WaitUntilUpgraded(TimeSpan.FromSeconds(10)));
        Assert.True(store.RollupsReady); // an empty log has nothing to backfill
        store.WriteBatch(Traffic("p1", 1500, 3, TimeSpan.FromDays(35)).Cast<object>().ToList());
        store.WriteBatch(Traffic("p2", 300, 4, TimeSpan.FromDays(35)).Cast<object>().ToList());

        var range = TimeSpan.FromHours(hours);
        var fromRows = store.GetSummaryFromRows("p1", range, Now);
        var fromRollups = store.GetSummaryFromRollups("p1", range, Now);
        Assert.Equal("rows", fromRows.Source);
        Assert.Equal("rollup", fromRollups.Source);
        Assert.True(fromRows.Requests > 0);
        AssertSameSummary(fromRows, fromRollups);
        Assert.Equal("rollup", store.GetSummary("p1", range, Now).Source);
    }

    /// <summary>Rows written before the rollups existed: the first start only knows them as raw rows.</summary>
    private void SeedLegacyLog(IEnumerable<ActivityRequest> rows)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"CREATE TABLE requests (id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT NOT NULL, ts_ms INTEGER NOT NULL,
                session_id TEXT, method TEXT, route TEXT, perm_key TEXT, service TEXT, status INTEGER, duration_ms REAL, ip TEXT, page TEXT,
                deny_reason TEXT, via_batch INTEGER NOT NULL DEFAULT 0);";
            ddl.ExecuteNonQuery();
        }
        using var tx = conn.BeginTransaction();
        foreach (var r in rows.OrderBy(r => r.TsMs))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO requests (profile_id, ts_ms, session_id, method, route, perm_key, service, status, ip) VALUES ($p, $t, $s, $m, $r, $k, $svc, $st, $ip);";
            cmd.Parameters.AddWithValue("$p", r.ProfileId);
            cmd.Parameters.AddWithValue("$t", r.TsMs);
            cmd.Parameters.AddWithValue("$s", (object?)r.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", (object?)r.Method ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)r.Route ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", (object?)r.PermKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$svc", (object?)r.Service ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", r.Status);
            cmd.Parameters.AddWithValue("$ip", (object?)r.Ip ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Fact]
    public void Backfill_ResumesAfterARestart_AndCountsNothingTwice()
    {
        SeedLegacyLog(Traffic("p1", 95, 11, TimeSpan.FromDays(5)));

        // First start with rollups: 95 rows predate them. Roll up two chunks of 20, then "restart".
        var first = NewStore(autoUpgrade: false, chunk: 20);
        Assert.False(first.RollupsReady);
        Assert.Equal((0L, 95L), first.RollupProgress);
        Assert.Equal("rows", first.GetSummary("p1", TimeSpan.FromDays(7), Now).Source); // raw rows until done
        first.Backfill(maxChunks: 2);
        Assert.Equal((40L, 95L), first.RollupProgress);
        // Live rows written meanwhile go to the rollups as they are written.
        first.WriteBatch(Traffic("p1", 30, 12, TimeSpan.FromDays(2)).Cast<object>().ToList());
        first.Dispose();
        SqliteConnection.ClearAllPools();

        using var second = NewStore(autoUpgrade: true, chunk: 20);
        Assert.Equal(40L, second.RollupProgress.Through); // carries on, not from 0
        Assert.True(second.WaitUntilUpgraded(TimeSpan.FromSeconds(10)));
        Assert.True(second.RollupsReady);
        Assert.True(second.IndexesReady);

        var range = TimeSpan.FromDays(7);
        var fromRows = second.GetSummaryFromRows("p1", range, Now);
        Assert.Equal(125, fromRows.Requests);
        AssertSameSummary(fromRows, second.GetSummaryFromRollups("p1", range, Now));
    }

    // ───────────────────────────── what each read costs ─────────────────────────────

    /// <summary>The owner's busy traffic with a quiet profile's few requests spread through it, as on the server.</summary>
    private ProfileActivityStore BusyLog(out int quietRequests)
    {
        var store = NewStore();
        Assert.True(store.WaitUntilUpgraded(TimeSpan.FromSeconds(10)));
        var random = new Random(5);
        var batch = new List<object>();
        quietRequests = 0;
        for (int i = 0; i < 40_000; i++)
        {
            bool quiet = i % 20 == 0;
            if (quiet) quietRequests++;
            var r = Routes[random.Next(Routes.Length)];
            batch.Add(new ActivityRequest
            {
                ProfileId = quiet ? "quiet" : "owner",
                TsMs = Ms(Now) - (40_000 - i) * 60_000L, // a request a minute, about 28 days
                SessionId = "s",
                Method = r.Method, Route = r.Route, Service = r.Service, PermKey = r.Perm,
                Status = quiet && i % 4000 == 0 ? 403 : 200,
                Ip = "203.0.113.7",
                Page = new string('p', 40),
            });
            if (batch.Count == 2000) { store.WriteBatch(batch); batch = new List<object>(); }
        }
        if (batch.Count > 0) store.WriteBatch(batch);
        return store;
    }

    /// <summary>Pages read (cache misses on a fresh connection) and the plan, for one query.</summary>
    private (int Pages, string Plan) Cost(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        var plan = new List<string>();
        using (var explain = conn.CreateCommand())
        {
            explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var (name, value) in parameters) explain.Parameters.AddWithValue(name, value);
            using var reader = explain.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
        }
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }
        SQLitePCL.raw.sqlite3_db_status(conn.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0);
        return (pages, string.Join(" / ", plan));
    }

    [Fact]
    public void RefusedOnly_ReadsThePartialIndex_NotEveryRowOfTheProfile()
    {
        using var store = BusyLog(out _);
        var args = new (string, object)[] { ("$p", "quiet"), ("$b", long.MaxValue), ("$s", 0L), ("$l", 200) };

        var (pages, plan) = Cost(store.TimelineRequestSql(service: false, deniedOnly: true), args);
        // What ran live through 5a77498: the profile's index, then a table lookup per row to test the status.
        string unindexed = "SELECT ts_ms, status FROM requests INDEXED BY ix_req_profile_ts WHERE profile_id = $p AND ts_ms < $b AND ts_ms >= $s AND status IN (401, 403) ORDER BY ts_ms DESC LIMIT $l;";
        var (before, beforePlan) = Cost(unindexed, args);

        string detail = $"refused-only read {pages} pages ({plan}); the old query {before} ({beforePlan})";
        Assert.Contains("ix_req_denied", plan);
        Assert.True(pages * 20 < before, detail);
        Assert.True(pages < 30, detail);
        Assert.Equal(10, store.GetTimeline("quiet", null, null, 200, deniedOnly: true).Count);
        output.WriteLine(detail);
    }

    [Fact]
    public void ServiceFilter_ReadsItsIndex()
    {
        using var store = BusyLog(out _);
        var (_, plan) = Cost(store.TimelineRequestSql(service: true, deniedOnly: false),
            ("$p", "quiet"), ("$b", long.MaxValue), ("$s", 0L), ("$l", 60), ("$svc", "KliveMail"));
        Assert.Contains("ix_req_profile_service_ts", plan);
        Assert.All(store.GetTimeline("quiet", null, null, 60, service: "KliveMail"), i => Assert.Equal("KliveMail", i.Service));
    }

    [Fact]
    public void Usage_ReadsOnlyTheCoveringIndex()
    {
        using var store = BusyLog(out _);
        var (_, plan) = Cost(store.UsageSql, ("$p", "quiet"));
        Assert.Contains("COVERING INDEX ix_ru_profile_perm", plan);
        Assert.DoesNotContain("TEMP B-TREE", plan);
    }

    [Fact]
    public void ThirtyDaySummary_FromRollups_ReadsAFractionOfTheRawPass()
    {
        using var store = BusyLog(out int quiet);
        long from = Ms(Now.AddDays(-30)) / 3_600_000 * 3_600_000;
        var (rawPages, rawPlan) = Cost("SELECT ts_ms, service, status, route, perm_key, ip, session_id IS NULL, method FROM requests WHERE profile_id = $p AND ts_ms >= $f;",
            ("$p", "owner"), ("$f", from));
        var (rollupPages, rollupPlan) = Cost("SELECT hour_ms, route, password, service, perm_key, count, denied, errors, last_ts_ms, method FROM activity_hourly WHERE profile_id = $p AND hour_ms >= $f;",
            ("$p", "owner"), ("$f", from));
        string costs = $"rollups {rollupPages} pages ({rollupPlan}) vs raw {rawPages} ({rawPlan})";
        output.WriteLine(costs);
        Assert.True(rollupPages * 5 < rawPages, costs);

        var summary = store.GetSummary("quiet", TimeSpan.FromDays(30), Now);
        Assert.Equal("rollup", summary.Source);
        Assert.Equal(quiet, summary.Requests); // the log spans under 28 days: all of them
        AssertSameSummary(store.GetSummaryFromRows("quiet", TimeSpan.FromDays(30), Now), summary);
    }

    // ───────────────────────────── insight reads ─────────────────────────────

    [Fact]
    public void AuthMix_TellsPasswordRequestsFromSessionOnes()
    {
        using var store = NewStore();
        store.WriteBatch(Traffic("bot", 40, 21, TimeSpan.FromDays(3), password: true).Cast<object>().ToList());
        store.WriteBatch(new object[] { new ActivityRequest { ProfileId = "bot", TsMs = Ms(Now) - 5_000, SessionId = "s9", Route = "/x", Method = "GET", Status = 200 } });

        var mix = store.GetAuthMix("bot", Ms(Now.AddDays(-7)));
        Assert.Equal(40, mix.Password);
        Assert.Equal(1, mix.Session);
        Assert.NotNull(mix.LastPasswordMs);
        Assert.Equal(0, store.GetAuthMix("nobody", Ms(Now.AddDays(-7))).Password);
    }

    [Fact]
    public void Hourly_SplitsTheLastDayByHour()
    {
        using var store = NewStore();
        long now = Ms(DateTimeOffset.UtcNow);
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = "p", TsMs = now - 1_000, Route = "/a", Status = 200 },
            new ActivityRequest { ProfileId = "p", TsMs = now - 2_000, Route = "/a", Status = 200 },
            new ActivityRequest { ProfileId = "p", TsMs = now - 3 * 3_600_000L, Route = "/a", Status = 200 },
            new ActivityRequest { ProfileId = "p", TsMs = now - 30 * 3_600_000L, Route = "/a", Status = 200 }, // outside the day
        });
        long[] hourly = store.GetHourly("p", 24, now);
        Assert.Equal(24, hourly.Length);
        Assert.Equal(2, hourly[23]);
        Assert.Equal(1, hourly[20]);
        Assert.Equal(3, hourly.Sum());
        Assert.All(store.GetHourly("nobody", 24, now), n => Assert.Equal(0, n));
    }

    [Fact]
    public void KeysUsedSince_ListsWhatWasUsedInTheWindow()
    {
        using var store = NewStore();
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = "p", TsMs = Ms(Now.AddDays(-2)), Route = "/a", PermKey = "a.b.read", Status = 200 },
            new ActivityRequest { ProfileId = "p", TsMs = Ms(Now.AddDays(-50)), Route = "/b", PermKey = "a.b.write", Status = 200 },
        });
        var used = store.GetKeysUsedSince("p", Ms(Now.AddDays(-30)));
        Assert.Equal(new[] { "a.b.read" }, used.Keys);
    }
}

/// <summary>The directory's review (GET /KMProfiles/insights) and signing out idle devices.</summary>
public sealed class ProfileInsightsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-insights-" + Guid.NewGuid().ToString("N"));
    private readonly KMProfileManager manager = new();
    private readonly ProfileActivityStore store;
    private readonly KMProfile owner, bot, person, dormant;

    public ProfileInsightsTests()
    {
        owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        owner.Name = "Owner";
        bot = Profile(ProfileRank.Admin, ProfilesPerms.DirectoryView.Key, OmniTraderPerms.StatusView.Key, OmniTraderPerms.BacktestsRun.Key);
        bot.Name = "Bot";
        bot.AllowPasswordApiAccess = true;
        person = Profile(ProfileRank.Manager, OmniTraderPerms.StatusView.Key, OmniTraderPerms.BacktestsRun.Key);
        person.Name = "Person";
        dormant = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        dormant.Name = "Dormant";
        manager.InitializeForTests(new[] { owner, bot, person, dormant }, dir);
        store = new ProfileActivityStore(Path.Combine(dir, "activity.db"), _ => { });
        store.Start();
        manager.UseActivityForTests(store, acceptPasswordAsBearer: true);
    }

    public void Dispose()
    {
        store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private JObject View(KMProfile viewer)
    {
        var data = manager.BuildInsights(DateTime.UtcNow);
        return JObject.FromObject(manager.InsightsView(data, viewer, DateTime.UtcNow), Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver { NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy() },
        }));
    }

    private JObject Row(JObject view, KMProfile p) => (JObject)view["profiles"]!.First(r => (string?)r["userId"] == p.UserID);

    [Fact]
    public void PasswordBearer_IsSafeToSwitchOff_WhenOnlyProfilesWithPasswordAccessUseAPassword()
    {
        long now = Ms(DateTime.UtcNow);
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = bot.UserID, TsMs = now - 60_000, Route = "/omnitrader/status", Method = "GET", PermKey = OmniTraderPerms.StatusView.Key, Status = 200 },
            new ActivityRequest { ProfileId = person.UserID, TsMs = now - 60_000, SessionId = "s1", Route = "/omnitrader/status", Method = "GET", PermKey = OmniTraderPerms.StatusView.Key, Status = 200 },
        });
        var view = View(owner);
        Assert.True((bool)view["passwordBearer"]!["safeToSwitchOff"]!);
        Assert.Equal(1, (long)Row(view, bot)["auth"]!["password7d"]!);
        Assert.Equal(1, (long)Row(view, person)["auth"]!["session7d"]!);

        // The person's old device still sends the password: switching off would lock it out.
        store.WriteBatch(new object[] { new ActivityRequest { ProfileId = person.UserID, TsMs = now - 30_000, Route = "/omnitrader/status", Method = "GET", Status = 200 } });
        view = View(owner);
        Assert.False((bool)view["passwordBearer"]!["safeToSwitchOff"]!);
        Assert.Equal(person.UserID, (string?)view["passwordBearer"]!["blockers"]![0]!["userId"]);
    }

    [Fact]
    public void Usage_ListsDirectGrantsUnusedFor30Days()
    {
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = person.UserID, TsMs = Ms(DateTime.UtcNow.AddDays(-3)), SessionId = "s", Route = "/omnitrader/status", Method = "GET", PermKey = OmniTraderPerms.StatusView.Key, Status = 200 },
        });
        var usage = Row(View(owner), person)["usage"]!;
        Assert.Equal(new[] { OmniTraderPerms.BacktestsRun.Key }, usage["unused"]!.Select(t => (string)t!));
        Assert.Equal(1, (int)usage["used"]!);
        Assert.Empty(Row(View(owner), owner)["usage"]!["unused"]!); // the owner is never "over-granted"
        Assert.Equal(1, Row(View(owner), dormant)["usage"]!["unused"]!.Count());
    }

    [Fact]
    public void AViewerSeesOnlyWhatTheirPermissionsAllow()
    {
        // The bot holds the directory only: activity shape, nothing about credentials, sessions or grants.
        var row = Row(View(bot), person);
        Assert.NotNull(row["hourly24"]);
        Assert.Null(row["auth"]);
        Assert.Null(row["usage"]);
        Assert.Null(row["sessions"]);
        Assert.Null(row["effective"]);
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, View(bot)["passwordBearer"]!.Type);
    }

    [Fact]
    public void IdleSessions_AreCounted_AndOnlyThoseAreSignedOut()
    {
        var (_, fresh) = manager.Sessions.Issue(person.UserID, "203.0.113.1", "Mozilla/5.0 (Windows NT 10.0) Chrome/120");
        var (_, stale) = manager.Sessions.Issue(person.UserID, "203.0.113.2", "curl/8.0", nowUtc: DateTime.UtcNow.AddDays(-12));
        var (_, staleToo) = manager.Sessions.Issue(person.UserID, "203.0.113.3", "Mozilla/5.0 (iPhone) Safari/604.1", nowUtc: DateTime.UtcNow.AddDays(-20));

        var sessions = Row(View(owner), person)["sessions"]!;
        Assert.Equal(3, (int)sessions["active"]!);
        Assert.Equal(2, (int)sessions["idle"]!);

        int revoked = manager.Sessions.RevokeIdle(person.UserID, DateTime.UtcNow.AddDays(-7), owner.UserID, "unused");
        Assert.Equal(2, revoked);
        Assert.True(manager.Sessions.Get(fresh.SessionId)!.IsActive(DateTime.UtcNow));
        Assert.False(manager.Sessions.Get(stale.SessionId)!.IsActive(DateTime.UtcNow));
        Assert.False(manager.Sessions.Get(staleToo.SessionId)!.IsActive(DateTime.UtcNow));

        // Keeping the caller's own session even when it is the idle one.
        var (_, mine) = manager.Sessions.Issue(person.UserID, null, null, nowUtc: DateTime.UtcNow.AddDays(-9));
        Assert.Equal(0, manager.Sessions.RevokeIdle(person.UserID, DateTime.UtcNow.AddDays(-7), person.UserID, "unused", exceptSessionId: mine.SessionId));
    }
}
