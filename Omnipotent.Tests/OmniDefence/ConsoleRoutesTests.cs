using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniDefence;
using System.Collections.Specialized;

namespace Omnipotent.Tests.OmniDefence;

/// <summary>
/// The two reads behind the OmniDefence console page. The IP map was sent as every record with
/// every field named, about 4 MB for ~6,000 addresses, and the old page fetched it 643 times in a
/// week (215 MB compressed); the compact form names the fields once. The events feed is the
/// server-wide history of what was done to addresses, which could only be read one address at a time.
/// Addresses are documentation ranges.
/// </summary>
public sealed class ConsoleRoutesTests : IDisposable
{
    private const long Now = 1_794_350_000;
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-console-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
        }
    }

    private static IpRecord Record(string ip, long lastSeen, string cls = "VulnScanner", long? classUpdated = null) => new()
    {
        Ip = ip,
        FirstSeen = lastSeen - 86_400,
        LastSeen = lastSeen,
        TotalRequests = 271,
        SuccessfulRequests = 12,
        UnauthAttempts = 3,
        DenyCount = 140,
        ThreatScore = 189.456,
        Status = "Watch",
        Country = "NL",
        City = "Amsterdam",
        Region = "North Holland",
        Asn = "AS64500 Example Hosting B.V.",
        AsName = "EXAMPLE-HOSTING",
        Isp = "Example Hosting",
        Org = "Example Hosting B.V.",
        Latitude = 52.37403,
        Longitude = 4.88969,
        Classification = cls,
        ClassConfidence = 0.9666,
        ClassTags = "Datacenter:Example,Probe",
        ClassUpdatedUtc = classUpdated ?? lastSeen - 600,
        IsHosting = true,
        IsProxy = false,
        IsMobile = false,
        Timezone = "Europe/Amsterdam",
        ReverseDns = "scanner.example.net",
    };

    [Fact]
    public void Compact_map_names_each_field_once_and_keeps_what_the_page_reads()
    {
        var records = new[] { Record("198.51.100.17", Now - 60), Record("203.0.113.40", Now - 7_200, "LikelyHuman") };
        var body = JObject.Parse(JsonConvert.SerializeObject(OmniDefenceRoutes.CompactIpMap(records, null, Now)));

        Assert.True(body.Value<bool>("full"));
        Assert.Equal(2, body.Value<int>("count"));
        var fields = body["fields"]!.Select(f => (string)f!).ToList();
        var rows = (JArray)body["rows"]!;
        Assert.All(rows, row => Assert.Equal(fields.Count, ((JArray)row).Count));

        // Most recently seen first, values in the column the field list says.
        JToken Cell(int row, string field) => rows[row][fields.IndexOf(field)]!;
        Assert.Equal("198.51.100.17", (string)Cell(0, "ip")!);
        Assert.Equal("203.0.113.40", (string)Cell(1, "ip")!);
        Assert.Equal("VulnScanner", (string)Cell(0, "cls")!);
        Assert.Equal(189.5, (double)Cell(0, "score"));
        Assert.Equal(0.97, (double)Cell(0, "conf"));
        Assert.Equal(52.374, (double)Cell(0, "lat"));
        Assert.Equal(4.89, (double)Cell(0, "lon"));
        Assert.Equal("Datacenter:Example,Probe", (string)Cell(0, "classTags")!);
        Assert.Equal(140, (long)Cell(0, "deny"));
        Assert.Equal("Watch", (string)Cell(0, "status")!);
    }

    [Fact]
    public void Compact_map_is_a_fraction_of_the_full_records()
    {
        var records = Enumerable.Range(0, 400).Select(i => Record($"198.51.{100 + i / 250}.{i % 250}", Now - i * 60)).ToList();
        int full = JsonConvert.SerializeObject(records).Length;
        int compact = JsonConvert.SerializeObject(OmniDefenceRoutes.CompactIpMap(records, null, Now)).Length;
        Assert.True(compact * 2 < full, $"compact {compact:N0} bytes vs full {full:N0}");
    }

    [Fact]
    public void Since_sends_only_addresses_seen_or_reclassified_after_it()
    {
        long since = Now - 300;
        var records = new[]
        {
            Record("198.51.100.1", Now - 10),                              // seen since
            Record("198.51.100.2", Now - 3_600, classUpdated: Now - 30),   // reclassified since, not seen
            Record("198.51.100.3", Now - 3_600, classUpdated: Now - 3_600), // neither
        };
        var body = JObject.Parse(JsonConvert.SerializeObject(OmniDefenceRoutes.CompactIpMap(records, since, Now)));
        Assert.False(body.Value<bool>("full"));
        var fields = body["fields"]!.Select(f => (string)f!).ToList();
        var ips = ((JArray)body["rows"]!).Select(row => (string)row[fields.IndexOf("ip")]!).ToList();
        Assert.Equal(new[] { "198.51.100.1", "198.51.100.2" }, ips);
    }

    [Fact]
    public void Events_query_leaves_out_geolocation_pages_back_and_caps_its_size()
    {
        var (sql, p) = OmniDefenceRoutes.BuildEventsQuery(new NameValueCollection { ["limit"] = "100000", ["before"] = "40" });
        Assert.Contains("kind <> 'GeoIP'", sql);
        Assert.Contains("id < $before", sql);
        Assert.EndsWith("ORDER BY id DESC LIMIT $lim", sql);
        Assert.Equal(OmniDefenceRoutes.MaxEvents, p["$lim"]);
        Assert.Equal(40L, p["$before"]);

        var (withGeo, _) = OmniDefenceRoutes.BuildEventsQuery(new NameValueCollection { ["geo"] = "1" });
        Assert.DoesNotContain("GeoIP", withGeo);
    }

    [Fact]
    public async Task Events_feed_reads_newest_first_with_a_day_of_counts()
    {
        var store = new OmniDefenceStore(dbPath);
        await store.InitializeAsync();
        try
        {
            async Task Add(long ts, string ip, string kind, string? actor = null, string? detail = null)
                => await store.InsertIpEventAsync(new IpEventRow { UtcTimestamp = ts, Ip = ip, Kind = kind, ActorProfileName = actor, Detail = detail });

            await Add(Now - 200_000, "198.51.100.9", "Block", "Klives", "Exploit probes"); // older than a day
            await Add(Now - 3_000, "198.51.100.17", "GeoIP", detail: "{\"Country\":\"Netherlands\"}");
            await Add(Now - 2_000, "198.51.100.17", "AutoWatch", detail: "Threat score 61 (was Normal)");
            await Add(Now - 1_000, "198.51.100.17", "AutoBlocked", detail: "Auto: threat score reached 204");
            await Add(Now - 500, "203.0.113.40", "Untrap", "Klives");
            await Add(Now - 100, "203.0.113.40", "StatusChange:Watch", "Klives");

            var body = JObject.Parse(JsonConvert.SerializeObject(await OmniDefenceRoutes.ReadEventsAsync(store, new NameValueCollection(), Now)));
            var kinds = ((JArray)body["events"]!).Select(e => (string)e["kind"]!).ToList();
            Assert.Equal(new[] { "StatusChange:Watch", "Untrap", "AutoBlocked", "AutoWatch", "Block" }, kinds);
            Assert.Equal("Klives", (string)body["events"]![0]!["actor_profile_name"]!);

            // The day's counts include the located address (it happened), not the week-old block.
            var counts = (JObject)body["counts24h"]!;
            Assert.Equal(1, counts.Value<long>("AutoBlocked"));
            Assert.Equal(1, counts.Value<long>("GeoIP"));
            Assert.Null(counts["Block"]);

            // A kind is a prefix, and one address can be asked for on its own.
            var status = JObject.Parse(JsonConvert.SerializeObject(await OmniDefenceRoutes.ReadEventsAsync(store, new NameValueCollection { ["kind"] = "StatusChange" }, Now)));
            Assert.Single((JArray)status["events"]!);
            var one = JObject.Parse(JsonConvert.SerializeObject(await OmniDefenceRoutes.ReadEventsAsync(store, new NameValueCollection { ["ip"] = "198.51.100.17" }, Now)));
            Assert.Equal(2, ((JArray)one["events"]!).Count);

            // Paging back from the oldest row shown returns what is before it.
            long lastId = (long)body["events"]![2]!["id"]!;
            var older = JObject.Parse(JsonConvert.SerializeObject(await OmniDefenceRoutes.ReadEventsAsync(store, new NameValueCollection { ["before"] = lastId.ToString() }, Now)));
            Assert.Equal(new[] { "AutoWatch", "Block" }, ((JArray)older["events"]!).Select(e => (string)e["kind"]!));
        }
        finally { await store.ShutdownAsync(); }
    }
}
