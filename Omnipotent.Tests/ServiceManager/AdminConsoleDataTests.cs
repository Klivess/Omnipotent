using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Klives_Management.General_Analytics;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;

namespace Omnipotent.Tests.ServiceManager;

/// <summary>
/// The data the admin page is built on: the vitals history ring, per-service error counts, the build
/// identity, the uptime report and the API statistics store's write rate.
/// </summary>
public class AdminConsoleDataTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static VitalsHistory.Sample SampleAt(DateTime utc, double cpu = 10, long requests = 0) =>
        new(utc, cpu, 50, 1000, 500, 100, 7, 1_000, 2_000, 3, requests, 1);

    // ── vitals history ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void History_returns_samples_oldest_first()
    {
        var history = new VitalsHistory(capacity: 8, stepSeconds: 5);
        for (int i = 0; i < 5; i++) history.Append(SampleAt(Now.AddSeconds(-25 + i * 5), cpu: i));

        var series = history.Read(TimeSpan.FromMinutes(60), Now);

        Assert.Equal(5, series.Count);
        Assert.Equal(5, series.StepSeconds);
        Assert.Equal(new double[] { 0, 1, 2, 3, 4 }, series.Cpu);
        Assert.True(series.T.Zip(series.T.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Fact]
    public void History_overwrites_the_oldest_sample_once_full()
    {
        var history = new VitalsHistory(capacity: 4, stepSeconds: 5);
        for (int i = 0; i < 10; i++) history.Append(SampleAt(Now.AddSeconds(-50 + i * 5), cpu: i));

        var series = history.Read(TimeSpan.FromMinutes(60), Now);

        Assert.Equal(4, series.Count);
        Assert.Equal(new double[] { 6, 7, 8, 9 }, series.Cpu);
    }

    [Fact]
    public void History_window_leaves_out_older_samples()
    {
        var history = new VitalsHistory(capacity: 720, stepSeconds: 5);
        // The newest sample is 2 s old, so none sits exactly on a window edge.
        for (int i = 0; i < 720; i++) history.Append(SampleAt(Now.AddSeconds(-3597 + i * 5), cpu: i));

        var lastFiveMinutes = history.Read(TimeSpan.FromMinutes(5), Now);

        Assert.Equal(60, lastFiveMinutes.Count);
        Assert.Equal(719, lastFiveMinutes.Cpu[^1]);
        Assert.Equal(720, history.Read(TimeSpan.FromMinutes(60), Now).Count);
    }

    [Fact]
    public void History_serialises_as_columns_with_every_series_the_same_length()
    {
        var history = new VitalsHistory(capacity: 16, stepSeconds: 5);
        for (int i = 0; i < 3; i++) history.Append(SampleAt(Now.AddSeconds(-10 + i * 5), requests: 100 + i));

        var json = JObject.Parse(JsonConvert.SerializeObject(history.Read(TimeSpan.FromMinutes(60), Now)));

        foreach (string column in new[] { "T", "Cpu", "RamPct", "ProcessMB", "HeapMB", "Threads", "Gen2", "NetSent", "NetReceived", "Errors", "Requests", "ServerErrors" })
        {
            Assert.Equal(3, ((JArray)json[column]!).Count);
        }
        Assert.Equal(new long[] { 100, 101, 102 }, json["Requests"]!.ToObject<long[]>());
        Assert.Equal(new DateTimeOffset(Now).ToUnixTimeSeconds(), json["T"]!.ToObject<long[]>()![^1]);
    }

    [Fact]
    public void An_empty_history_reads_as_empty_columns()
    {
        var series = new VitalsHistory().Read(TimeSpan.FromMinutes(60), Now);

        Assert.Equal(0, series.Count);
        Assert.Empty(series.T);
        Assert.Empty(series.Cpu);
    }

    // ── per-service error counts ───────────────────────────────────────────────────────────

    [Fact]
    public void Error_tally_counts_per_service_and_separates_the_last_hour()
    {
        DateTime now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Local);
        var tally = new GeneralBotStatisticsService.ServiceErrorTally(now);

        tally.Add("CS2ArbitrageBot", now.AddMinutes(-5));
        tally.Add("CS2ArbitrageBot", now.AddMinutes(-50));
        tally.Add("cs2arbitragebot", now.AddHours(-9));
        tally.Add("OmniBlox", now.AddHours(-3));
        tally.Add(null, now.AddMinutes(-1));

        Assert.True(tally.TryGet("CS2ArbitrageBot", out var cs2));
        Assert.Equal(2, cs2.LastHour);
        Assert.Equal(3, cs2.Retained);
        Assert.Equal(now.AddMinutes(-5), cs2.LastAt);

        Assert.True(tally.TryGet("OmniBlox", out var blox));
        Assert.Equal(0, blox.LastHour);
        Assert.Equal(1, blox.Retained);

        Assert.False(tally.TryGet("KliveAPI", out var quiet));
        Assert.Equal(0, quiet.Retained);
        // The entry without a service still counts towards the total.
        Assert.Equal(3, tally.TotalLastHour);
    }

    // ── build identity ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0+59f29c2dac3dfb52620b39f4d18d99ee376abbf7", "1.0.0", "59f29c2dac3dfb52620b39f4d18d99ee376abbf7")]
    [InlineData("1.0.0+59F29C2", "1.0.0", "59f29c2")]
    [InlineData("2.1.0-beta+abcdef1234.dirty", "2.1.0-beta", "abcdef1234")]
    [InlineData("1.0.0", "1.0.0", "")]
    [InlineData("1.0.0+local", "1.0.0", "")]
    [InlineData("", "", "")]
    public void Informational_version_splits_into_version_and_commit(string informational, string version, string commit)
    {
        var parsed = BuildIdentity.SplitInformationalVersion(informational);

        Assert.Equal(version, parsed.Version);
        Assert.Equal(commit, parsed.Commit);
    }

    [Fact]
    public void This_build_embeds_the_commit_it_was_made_from()
    {
        // The admin page shows the running commit from this attribute, so a build that stops embedding
        // it (SourceLink switched off, a publish outside the git checkout) should be noticed here.
        Assert.Matches("^[0-9a-f]{7,40}$", BuildIdentity.Current.Commit);
        Assert.Equal(BuildIdentity.Current.Commit[..7], BuildIdentity.Current.CommitShort);
        Assert.True(BuildIdentity.Current.ProcessStartedUtc > DateTime.UtcNow.AddDays(-1));
    }

    [Theory]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Wi-Fi", "Intel(R) Wireless-AC 9560", false)]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", true)]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("Tailscale", "Tailscale Tunnel", true)]
    public void Virtual_adapters_are_recognised(string name, string description, bool isVirtual)
    {
        Assert.Equal(isVirtual, GeneralBotStatisticsService.IsVirtualAdapter(name, description));
    }

    [Theory]
    // The live server lists 52 interfaces for one physical card; these are the shapes among them.
    [InlineData("Ethernet", false)]
    [InlineData("vEthernet (WSL)", false)]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", true)]
    [InlineData("vEthernet (Default Switch)-WFP 802.3 MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Local Area Connection* 8-QoS Packet Scheduler-0000", true)]
    [InlineData("Wi-Fi", false)]
    [InlineData("Wi-Fi 2", false)]
    public void Filter_driver_instances_are_recognised_by_name(string name, bool isFilterLayer)
    {
        Assert.Equal(isFilterLayer, GeneralBotStatisticsService.IsFilterLayer(name));
    }

    // ── preflight answer ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Preflight_names_the_headers_that_were_asked_for()
    {
        // "*" never covers Authorization, so a browser re-sent the preflight before every request.
        string allowed = Omnipotent.Services.KliveAPI.KliveAPI.PreflightAllowedHeaders("authorization,x-klive-client,x-klive-page");

        Assert.Equal("authorization, x-klive-client, x-klive-page", allowed);
        Assert.DoesNotContain("*", allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("authorization\r\nSet-Cookie: x=1")]
    [InlineData("x-ok, bad header")]
    [InlineData("x-ok, <script>")]
    public void Preflight_falls_back_to_the_known_headers_for_anything_that_is_not_a_header_list(string? requested)
    {
        string allowed = Omnipotent.Services.KliveAPI.KliveAPI.PreflightAllowedHeaders(requested);

        Assert.Equal(Omnipotent.Services.KliveAPI.KliveAPI.DefaultPreflightHeaders, allowed);
        Assert.Contains("Authorization", allowed);
    }

    // ── diagnostic heartbeat ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_healthy_heartbeat_is_logged_once_a_quarter_hour_and_a_strained_one_every_minute()
    {
        var minute = TimeSpan.FromMinutes(1);

        // Healthy: quiet until the interval has passed. Logged every minute it filled a third of the
        // 4,000-entry log each day.
        Assert.False(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(inFlight: 3, pendingWork: 2, workersAvailable: 32_000, minute));
        Assert.False(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(3, 2, 32_000, TimeSpan.FromMinutes(14)));
        Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(3, 2, 32_000, TimeSpan.FromMinutes(15)));
        // The first one after start always goes out.
        Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(0, 0, 32_000, TimeSpan.MaxValue));

        // Strained: every minute, which is when the trajectory matters.
        Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(inFlight: 40, pendingWork: 2, workersAvailable: 32_000, minute));
        Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(3, pendingWork: 35_000, workersAvailable: 32_000, minute));
        Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.HeartbeatWorthLogging(3, 2, workersAvailable: 0, minute));
    }

    // ── uptime report ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Uptime_report_carries_the_commit_each_period_ran()
    {
        var periods = new List<OmniServiceMonitor.UptimePeriod>
        {
            new() { StartTime = Now.AddHours(-9), LastKnownUpTime = Now.AddHours(-6) },
            new() { StartTime = Now.AddHours(-5), LastKnownUpTime = Now.AddHours(-3), Commit = "59f29c2" },
            new() { StartTime = Now.AddHours(-2), LastKnownUpTime = Now.AddMinutes(-1), Commit = "a1b2c3d" },
        };

        var report = OmniServiceMonitor.BuildUptimeReport(periods, Now, days: null, includePeriods: true);
        var json = JArray.FromObject(report.Periods!);

        Assert.Equal(new string?[] { null, "59f29c2", "a1b2c3d" }, report.Periods!.Select(period => period.Commit));
        // Periods recorded before commits were kept do not grow a null field.
        Assert.False(((JObject)json[0]).ContainsKey("Commit"));
        Assert.Equal("a1b2c3d", json[2]["Commit"]!.Value<string>());
    }


    private static OmniServiceMonitor.UptimePeriod Period(DateTime start, DateTime end) =>
        new() { StartTime = start, LastKnownUpTime = end };

    [Fact]
    public void Uptime_report_runs_the_current_period_to_now()
    {
        // The stored end of the running period is refreshed once a minute. Read as written, the last
        // 45 seconds here would be an outage that never happened.
        var periods = new List<OmniServiceMonitor.UptimePeriod>
        {
            Period(Now.AddHours(-10), Now.AddHours(-6)),
            Period(Now.AddHours(-5), Now.AddSeconds(-45)),
        };

        var report = OmniServiceMonitor.BuildUptimeReport(periods, Now, days: null, includePeriods: true);

        Assert.Equal(Now, report.Periods![^1].LastKnownUpTime);
        Assert.Equal(5 * 3600, report.CurrentUptimeSeconds, 3);
        Assert.Equal(9 * 3600, report.TotalUptimeSeconds, 3);
        Assert.Equal(3600, report.TotalOutageSeconds, 3);
        Assert.Equal(2, report.TotalPeriods);
        // The stored list itself is left alone.
        Assert.Equal(Now.AddSeconds(-45), periods[1].LastKnownUpTime);
    }

    [Fact]
    public void Uptime_report_window_keeps_only_overlapping_periods_but_lifetime_totals()
    {
        var periods = new List<OmniServiceMonitor.UptimePeriod>
        {
            Period(Now.AddDays(-40), Now.AddDays(-30)),
            Period(Now.AddDays(-29), Now.AddDays(-6)),   // ends inside the 7-day window
            Period(Now.AddDays(-5), Now.AddDays(-1)),
            Period(Now.AddHours(-2), Now.AddMinutes(-1)),
        };

        var report = OmniServiceMonitor.BuildUptimeReport(periods, Now, days: 7, includePeriods: true);

        Assert.Equal(7, report.WindowDays);
        Assert.Equal(3, report.Periods!.Count);
        Assert.Equal(Now.AddDays(-29), report.Periods[0].StartTime);
        Assert.Equal(4, report.TotalPeriods);
        double lifetime = TimeSpan.FromDays(10).TotalSeconds + TimeSpan.FromDays(23).TotalSeconds
            + TimeSpan.FromDays(4).TotalSeconds + TimeSpan.FromHours(2).TotalSeconds;
        Assert.Equal(lifetime, report.TotalUptimeSeconds, 3);
    }

    [Fact]
    public void Uptime_report_without_periods_omits_the_list_from_the_json()
    {
        var periods = new List<OmniServiceMonitor.UptimePeriod> { Period(Now.AddHours(-3), Now.AddMinutes(-1)) };

        var report = OmniServiceMonitor.BuildUptimeReport(periods, Now, days: null, includePeriods: false);
        var json = JObject.Parse(JsonConvert.SerializeObject(report));

        Assert.Null(report.Periods);
        Assert.False(json.ContainsKey("Periods"));
        Assert.Equal(3 * 3600, json["CurrentUptimeSeconds"]!.Value<double>(), 3);
        Assert.Equal(1, json["TotalPeriods"]!.Value<int>());
    }

    [Fact]
    public void Uptime_report_of_no_history_is_all_zero()
    {
        var report = OmniServiceMonitor.BuildUptimeReport(new List<OmniServiceMonitor.UptimePeriod>(), Now, days: 7, includePeriods: true);

        Assert.Equal(0, report.TotalPeriods);
        Assert.Equal(0, report.TotalUptimeSeconds);
        Assert.Equal(0, report.TotalOutageSeconds);
        Assert.Empty(report.Periods!);
    }

    // ── API statistics store ───────────────────────────────────────────────────────────────

    private static string TempStatisticsPath() =>
        Path.Combine(Path.GetTempPath(), "omni-apistats-" + Guid.NewGuid().ToString("N"), "RequestStatistics.json");

    [Fact]
    public async Task Statistics_are_written_once_per_interval_however_many_requests_arrive()
    {
        string path = TempStatisticsPath();
        try
        {
            var store = new KliveApiStatisticsStore(path, saveInterval: TimeSpan.FromMilliseconds(400));

            // Steady traffic for about three intervals: the interval, not the request count, sets how
            // often the whole file is rewritten.
            var until = DateTime.UtcNow.AddMilliseconds(1250);
            int recorded = 0;
            while (DateTime.UtcNow < until)
            {
                store.RecordRequest("/ping", "GET", 200, TimeSpan.FromMilliseconds(2), matchedRoute: true);
                recorded++;
                await Task.Delay(5);
            }

            Assert.True(recorded > 50, $"only {recorded} requests were recorded");
            Assert.InRange(store.PersistCount, 1, 4);

            await store.FlushAsync();
            var reloaded = new KliveApiStatisticsStore(path);
            await reloaded.InitializeAsync();
            Assert.Equal(recorded, reloaded.TotalRequests);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Flush_saves_what_the_interval_is_still_holding()
    {
        string path = TempStatisticsPath();
        try
        {
            var store = new KliveApiStatisticsStore(path, saveInterval: TimeSpan.FromMinutes(10));
            store.RecordRequest("/a", "GET", 200, TimeSpan.FromMilliseconds(4), matchedRoute: true);
            store.RecordRequest("/a", "GET", 500, TimeSpan.FromMilliseconds(8), matchedRoute: true);

            Assert.Equal(0, store.PersistCount);
            await store.FlushAsync();
            Assert.Equal(1, store.PersistCount);

            // Compact on disk: the file is rewritten whole on every save.
            Assert.DoesNotContain("\n", await File.ReadAllTextAsync(path));

            var reloaded = new KliveApiStatisticsStore(path);
            await reloaded.InitializeAsync();
            Assert.Equal(2, reloaded.TotalRequests);
            Assert.Equal(1, reloaded.ServerErrorRequests);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Summary_can_limit_the_daily_history_to_the_most_recent_days()
    {
        string path = TempStatisticsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var days = Enumerable.Range(0, 200).Select(i => new
            {
                Date = new DateTime(2026, 4, 1).AddDays(i).ToString("yyyy-MM-dd"),
                Requests = 100 + i,
                Successes = 100 + i,
                TotalResponseTimeMs = 1000.0,
                MaxResponseTimeMs = 50.0,
            });
            await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(new { TotalRequests = 1, Days = days, Routes = Array.Empty<object>() }));
            var store = new KliveApiStatisticsStore(path);
            await store.InitializeAsync();

            var everything = JObject.FromObject(store.GetSummary());
            var recent = JObject.FromObject(store.GetSummary(30));

            Assert.Equal(200, ((JArray)everything["dailyHistory"]!).Count);
            var history = (JArray)recent["dailyHistory"]!;
            Assert.Equal(30, history.Count);
            Assert.Equal("2026-10-17", history[^1]!["date"]!.Value<string>());
            Assert.Equal("2026-09-18", history[0]!["date"]!.Value<string>());
            Assert.Equal(30, recent["historyWindow"]!["totalDays"]!.Value<int>());
            Assert.Equal(200, recent["historyWindow"]!["recordedDays"]!.Value<int>());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
