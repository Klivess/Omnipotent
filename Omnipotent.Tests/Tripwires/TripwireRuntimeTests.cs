using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Tripwires;

namespace Omnipotent.Tests.Tripwires;

/// <summary>
/// The redirect's decisions (what a visit gets and what it counts as), the in-memory link index the redirect
/// reads, the visitor-facing pages, and the store reads behind the Tripwires console. Fixtures are invented;
/// the scenarios replay what the live server did on 8 and 21 Nov 2026.
/// </summary>
public sealed class TripwireRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "omnipotent-tripwire-runtime-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CappedLink_CountsEveryVisitAfterTheLimit_AndNeverSaysTripwire()
    {
        // 8 Nov: a link capped at 10 trips was posted on a forum; 64 people opened it. 10 were recorded and the
        // other 54 were answered "This tripwire has reached its trip limit" and not counted anywhere.
        var tripwire = Tripwire(new TripwireSettings { MaxTrips = 10 });
        var index = new TripwireLinkIndex();
        index.Load(new[] { tripwire });
        var link = index.Find(tripwire.Targets[0].Token);
        Assert.NotNull(link);

        int trips = 0, closed = 0;
        for (int visit = 0; visit < 64; visit++)
        {
            var verdict = TripwireGate.Decide(link, isBot: false, DateTimeOffset.UtcNow, index.Trips(tripwire.Id));
            if (verdict.Count == TripwireCount.Trip && index.TryReserve(tripwire)) trips++;
            else
            {
                Assert.Equal("limit", verdict.ClosedReason ?? "limit");
                closed++;
            }
        }

        Assert.Equal(10, trips);
        Assert.Equal(54, closed);
        Assert.Equal(10, index.Trips(tripwire.Id));
        Assert.Equal(TripwireSettings.ClosedRefuse, tripwire.Settings.ClosedBehaviour);
        foreach (string page in new[] { TripwirePages.Unavailable(), TripwirePages.NotFound() })
            Assert.DoesNotContain("tripwire", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_ClosedReasons_AndPreviewsOnClosedLinks()
    {
        var now = DateTimeOffset.UtcNow;
        var open = Tripwire(new TripwireSettings());
        Assert.Equal(TripwireCount.Trip, TripwireGate.Decide(Link(open), false, now, 0).Count);
        Assert.Equal(TripwireCount.Preview, TripwireGate.Decide(Link(open), true, now, 0).Count);
        Assert.False(TripwireGate.Decide(null, false, now, 0).Found);

        Assert.Equal("disabled", TripwireGate.Decide(Link(Tripwire(new TripwireSettings { Enabled = false })), false, now, 0).ClosedReason);
        Assert.Equal("expired", TripwireGate.Decide(Link(Tripwire(new TripwireSettings { ExpiresUtc = now.AddMinutes(-1) })), false, now, 0).ClosedReason);
        var off = Tripwire(new TripwireSettings());
        off.Targets[0].Enabled = false;
        Assert.Equal("link-off", TripwireGate.Decide(Link(off), false, now, 0).ClosedReason);

        // A link preview of a closed link is counted as a preview, not as a person turned away.
        var capped = TripwireGate.Decide(Link(Tripwire(new TripwireSettings { MaxTrips = 1 })), true, now, 1);
        Assert.True(capped.Closed);
        Assert.Equal(TripwireCount.Preview, capped.Count);

        // Bots are recorded as trips when the tripwire does not ignore them.
        var keepBots = TripwireGate.Decide(Link(Tripwire(new TripwireSettings { IgnoreBots = false })), true, now, 0);
        Assert.Equal(TripwireCount.Trip, keepBots.Count);
    }

    [Fact]
    public void LinkIndex_NeverLowersAQueuedCount_ReleasesAndResets()
    {
        var tripwire = Tripwire(new TripwireSettings { MaxTrips = 2 });
        var index = new TripwireLinkIndex();
        Assert.False(index.Loaded);
        index.Load(new[] { tripwire });
        Assert.True(index.Loaded);

        Assert.True(index.TryReserve(tripwire));
        // A reload from the store before the queued trip is written must not hand the slot out again.
        index.Load(new[] { tripwire });
        Assert.Equal(1, index.Trips(tripwire.Id));
        Assert.True(index.TryReserve(tripwire));
        Assert.False(index.TryReserve(tripwire));

        index.Release(tripwire.Id);
        Assert.True(index.TryReserve(tripwire));

        index.Reset(tripwire.Id);
        Assert.Equal(0, index.Trips(tripwire.Id));

        // A deleted tripwire's tokens and counts go with it.
        index.Load(Array.Empty<TripwireRecord>());
        Assert.Null(index.Find(tripwire.Targets[0].Token));
        Assert.Equal(0, index.Trips(tripwire.Id));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", "Discord")]
    [InlineData("TelegramBot (like TwitterBot)", "Telegram")]
    [InlineData("WhatsApp/2.23.20.0", "WhatsApp")]
    [InlineData("Mozilla/5.0 (Macintosh) AppleWebKit/601.2.4 (KHTML, like Gecko) Version/9.0.1 Safari/601.2.4 facebookexternalhit/1.1 Facebot Twitterbot/1.0", "iMessage")]
    [InlineData("", "No user agent")]
    [InlineData("SomeCrawler/1.0 (+https://example.com)", "SomeCrawler")]
    public void PreviewAgent_NamesTheAppThatFetchedTheLink(string userAgent, string expected)
        => Assert.Equal(expected, TripwireGate.PreviewAgent(userAgent));

    [Fact]
    public void Settings_ClosedBehaviourNormalises_AndOldSettingsKeepRefusing()
    {
        var old = JsonConvert.DeserializeObject<TripwireSettings>("{\"enabled\":true,\"maxTrips\":10}")!;
        old.Normalize();
        Assert.Equal(TripwireSettings.ClosedRefuse, old.ClosedBehaviour);
        Assert.False(old.DiscordSkipKnownProfiles);

        var parsed = new TripwireSettings { ClosedBehaviour = " Redirect " };
        parsed.Normalize();
        Assert.Equal(TripwireSettings.ClosedRedirect, parsed.ClosedBehaviour);
        var junk = new TripwireSettings { ClosedBehaviour = "explode" };
        junk.Normalize();
        Assert.Equal(TripwireSettings.ClosedRefuse, junk.ClosedBehaviour);
    }

    [Fact]
    public void DiscordMessage_SaysWhenTheAddressIsAKmAccountOrAVpnExit()
    {
        var tripwire = Tripwire(new TripwireSettings { DiscordNotifications = true });
        var item = new TripwireEvent
        {
            Id = "e1", TripwireId = tripwire.Id, TargetId = tripwire.Targets[0].Id, TrippedUtc = DateTimeOffset.UtcNow,
            IpAddress = "203.0.113.20", City = "London", Country = "United Kingdom", IsUnique = true,
        };
        var own = TripwireService.BuildDiscordMessage(tripwire, tripwire.Targets[0], item,
            new TripwireNetwork { Profile = "Klives", ProfileRank = 5, Isp = "Example Broadband", Hosting = false, Proxy = false });
        string ownText = own.Embeds[0].Description;
        Assert.Contains("signed in to KM as Klives", ownText);
        Assert.Contains("Example Broadband · home or office", ownText);

        var vpn = TripwireService.BuildDiscordMessage(tripwire, tripwire.Targets[0], item,
            new TripwireNetwork { Isp = "Example VPN", Proxy = true });
        string vpnText = vpn.Embeds[0].Description;
        Assert.Contains("VPN or proxy exit, not the person", vpnText);
        Assert.DoesNotContain("signed in to KM", vpnText);

        Assert.Null(TripwireService.NetworkKind(null));
        Assert.Equal("mobile network", TripwireService.NetworkKind(new TripwireNetwork { Mobile = true, Hosting = false }));
        Assert.Equal("datacenter", TripwireService.NetworkKind(new TripwireNetwork { Hosting = true }));
    }

    [Fact]
    public async Task Store_CountsClosedVisitsAndPreviews_AndClearResetsThem()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Counters", "Klives", new TripwireSettings(), new[]
        {
            new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
        });
        var at = DateTimeOffset.UtcNow;
        await store.RecordClosedVisitAsync(tripwire.Id, at);
        await store.RecordClosedVisitAsync(tripwire.Id, at.AddSeconds(5));
        await store.RecordPreviewAsync(tripwire.Id, at, "Discord");

        var read = await store.GetAsync(tripwire.Id);
        Assert.Equal(2, read!.ClosedVisits);
        Assert.Equal(1, read.PreviewVisits);
        Assert.Equal("Discord", read.LastPreviewAgent);
        Assert.NotNull(read.LastClosedUtc);
        var resolved = await store.ResolveTokenAsync(tripwire.Targets[0].Token);
        Assert.Equal(2, resolved!.Value.Tripwire.ClosedVisits);
        Assert.Equal(tripwire.Targets[0].Id, resolved.Value.Target.Id);
        Assert.Equal(2, (await store.ListAsync()).Single().ClosedVisits);

        Assert.True(await store.ClearEventsAsync(tripwire.Id));
        var cleared = await store.GetAsync(tripwire.Id);
        Assert.Equal(0, cleared!.ClosedVisits);
        Assert.Equal(0, cleared.PreviewVisits);
        Assert.Null(cleared.LastPreviewAgent);
    }

    [Fact]
    public async Task Summary_HourlyBucketsVisitorsAndPrivacySignals()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Summary", "Klives", new TripwireSettings(), new[]
        {
            new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
        });
        var target = tripwire.Targets[0];
        var hour = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day,
            DateTimeOffset.UtcNow.Hour, 0, 0, TimeSpan.Zero);
        await store.RecordEventAsync(tripwire, target, Event(tripwire, target, "a", hour.AddMinutes(1)), 60);
        await store.RecordEventAsync(tripwire, target, Event(tripwire, target, "a", hour.AddMinutes(2)), 60);
        var dnt = Event(tripwire, target, null, hour.AddHours(-3));
        dnt.DoNotTrack = true;
        dnt.IpAddress = null;
        await store.RecordEventAsync(tripwire, target, dnt, 60);

        var summary = await store.GetSummaryAsync(tripwire.Id);
        Assert.Equal(3, summary.RetainedTrips);
        Assert.Equal(1, summary.Visitors);
        Assert.Equal(1, summary.PrivacySignalTrips);
        Assert.Equal(2, summary.Hourly.Count);
        Assert.Equal(hour.ToString("yyyy-MM-ddTHH"), summary.Hourly[^1].H);
        Assert.Equal(2, summary.Hourly[^1].N);
        // The JSON keeps the short keys the page bins with.
        string json = JsonConvert.SerializeObject(summary, new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        });
        Assert.Contains("\"hourly\":[{\"h\":", json);
    }

    [Fact]
    public async Task Visitors_GroupTrips_AndFindTheSamePersonOnOtherTripwires()
    {
        using var store = await CreateStoreAsync();
        var first = await store.CreateAsync("First", "Klives", new TripwireSettings(), new[] { new TripwireTarget { Label = "A", DestinationUrl = "https://example.com/a" } });
        var second = await store.CreateAsync("Second", "Klives", new TripwireSettings(), new[] { new TripwireTarget { Label = "B", DestinationUrl = "https://example.com/b" } });
        var third = await store.CreateAsync("Third", "Klives", new TripwireSettings(), new[] { new TripwireTarget { Label = "C", DestinationUrl = "https://example.com/c" } });
        var now = DateTimeOffset.UtcNow;

        await store.RecordEventAsync(first, first.Targets[0], Event(first, first.Targets[0], "visitor-1", now.AddMinutes(-30), "198.51.100.7"), 60);
        await store.RecordEventAsync(first, first.Targets[0], Event(first, first.Targets[0], "visitor-1", now.AddMinutes(-1), "198.51.100.7"), 60);
        await store.RecordEventAsync(first, first.Targets[0], Event(first, first.Targets[0], "visitor-2", now.AddMinutes(-5), "198.51.100.9"), 60);
        // The same browser on another tripwire, and the same address in another browser on a third.
        await store.RecordEventAsync(second, second.Targets[0], Event(second, second.Targets[0], "visitor-1", now.AddDays(-2), "198.51.100.7"), 60);
        await store.RecordEventAsync(third, third.Targets[0], Event(third, third.Targets[0], "visitor-9", now.AddDays(-1), "198.51.100.9"), 60);

        var visitors = await store.GetVisitorsAsync(first.Id, 50);
        Assert.Equal(new[] { "visitor-1", "visitor-2" }, visitors.Select(v => v.VisitorHash));
        var one = visitors[0];
        Assert.Equal(2, one.Trips);
        Assert.True(one.FirstUtc < one.LastUtc);
        Assert.Equal("198.51.100.7", one.Latest.IpAddress);
        var also = Assert.Single(one.Also);
        Assert.Equal(("Second", "visitor"), (also.Name, also.Match));
        var two = Assert.Single(visitors[1].Also);
        Assert.Equal(("Third", "address"), (two.Name, two.Match));

        var page = await store.GetEventsAsync(first.Id, null, 100, 0, default, "visitor-1");
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, item => Assert.Equal("visitor-1", item.VisitorHash));

        var recent = await store.GetRecentEventsAsync(3);
        Assert.Equal(new[] { "First", "First", "First" }, recent.Select(r => r.TripwireName));
        Assert.True(recent[0].TrippedUtc >= recent[1].TrippedUtc);
        var counts = await store.GetIpCountsAsync();
        Assert.Equal(2, counts.Single(c => c.TripwireId == first.Id && c.Ip == "198.51.100.7").Count);
    }

    [Fact]
    public async Task Initialise_AddsTheCounterColumnsToAnOlderDatabase()
    {
        string dbPath = Path.Combine(directory, "tripwires.db");
        string id;
        using (var store = await CreateStoreAsync())
        {
            id = (await store.CreateAsync("Older", "Klives", new TripwireSettings(), new[]
            {
                new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
            })).Id;
        }
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DROP INDEX idx_tripwire_events_time;
                ALTER TABLE tripwires DROP COLUMN closed_visits; ALTER TABLE tripwires DROP COLUMN last_closed_utc;
                ALTER TABLE tripwires DROP COLUMN preview_visits; ALTER TABLE tripwires DROP COLUMN last_preview_utc;
                ALTER TABLE tripwires DROP COLUMN last_preview_agent;";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new TripwireStore(dbPath);
        await migrated.InitialiseAsync();
        var read = await migrated.GetAsync(id);
        Assert.Equal(0, read!.ClosedVisits);
        await migrated.RecordPreviewAsync(id, DateTimeOffset.UtcNow, "Slack");
        Assert.Equal(1, (await migrated.GetAsync(id))!.PreviewVisits);
        Assert.Empty(await migrated.GetRecentEventsAsync(10));
    }

    private static TripwireRecord Tripwire(TripwireSettings settings)
    {
        settings.Normalize();
        string id = Guid.NewGuid().ToString("N");
        return new TripwireRecord
        {
            Id = id, Name = "Fixture", Settings = settings,
            Targets = { new TripwireTarget { Id = id + "t", TripwireId = id, Token = "token-" + id, Label = "Main", DestinationUrl = "https://example.com", Enabled = true } },
        };
    }

    private static TripwireLink Link(TripwireRecord tripwire) => new(tripwire, tripwire.Targets[0]);

    private async Task<TripwireStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new TripwireStore(Path.Combine(directory, "tripwires.db"));
        await store.InitialiseAsync();
        return store;
    }

    private static TripwireEvent Event(TripwireRecord tripwire, TripwireTarget target, string? visitor, DateTimeOffset at, string? ip = "203.0.113.10") => new()
    {
        Id = Guid.NewGuid().ToString("N"), TripwireId = tripwire.Id, TargetId = target.Id,
        TargetLabel = target.Label, DestinationUrl = target.DestinationUrl, TrippedUtc = at,
        VisitorHash = visitor, IpAddress = ip, DeviceType = "Desktop",
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch { }
    }
}
