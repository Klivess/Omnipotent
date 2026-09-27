using Microsoft.Data.Sqlite;
using Omnipotent.Services.Tripwires;

namespace Omnipotent.Tests.Tripwires;

public sealed class TripwireStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "omnipotent-tripwire-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateResolveAndRecord_PersistsTargetsAndDeduplicatesVisitors()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Launch link", "Klives", new TripwireSettings(), new[]
        {
            new TripwireTarget { Label = "Website", DestinationUrl = "https://example.com/launch" },
            new TripwireTarget { Label = "Docs", DestinationUrl = "https://example.com/docs" },
        });

        Assert.Equal(2, tripwire.Targets.Count);
        Assert.All(tripwire.Targets, target => Assert.True(target.Token.Length >= 16));
        Assert.NotEqual(tripwire.Targets[0].Token, tripwire.Targets[1].Token);

        var resolved = await store.ResolveTokenAsync(tripwire.Targets[0].Token);
        Assert.True(resolved.HasValue);
        Assert.Equal("https://example.com/launch", resolved.Value.Target.DestinationUrl);

        var first = NewEvent(tripwire, tripwire.Targets[0], "visitor-a", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = NewEvent(tripwire, tripwire.Targets[0], "visitor-a", DateTimeOffset.UtcNow);
        Assert.True((await store.RecordEventAsync(tripwire, tripwire.Targets[0], first, 60)).Event!.IsUnique);
        Assert.False((await store.RecordEventAsync(tripwire, tripwire.Targets[0], second, 60)).Event!.IsUnique);

        var page = await store.GetEventsAsync(tripwire.Id, null, 100, 0);
        var summary = await store.GetSummaryAsync(tripwire.Id);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, summary.TotalTrips);
        Assert.Equal(1, summary.UniqueTrips);
        Assert.Equal(second.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task RecordEvent_EnforcesMaximumTripCountAtomically()
    {
        using var store = await CreateStoreAsync();
        var settings = new TripwireSettings { MaxTrips = 1 };
        var tripwire = await store.CreateAsync("One shot", "Klives", settings, new[]
        {
            new TripwireTarget { Label = "Only link", DestinationUrl = "https://example.com" },
        });
        var target = tripwire.Targets[0];

        var first = await store.RecordEventAsync(tripwire, target, NewEvent(tripwire, target, "a", DateTimeOffset.UtcNow), 60);
        var second = await store.RecordEventAsync(tripwire, target, NewEvent(tripwire, target, "b", DateTimeOffset.UtcNow), 60);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.True(second.LimitReached);
        Assert.Equal(1, (await store.GetSummaryAsync(tripwire.Id)).TotalTrips);
    }

    [Fact]
    public async Task Update_KeepsExistingTrackingTokenAndDisablesRemovedTargets()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Before", "Klives", new TripwireSettings(), new[]
        {
            new TripwireTarget { Label = "Keep", DestinationUrl = "https://example.com/old" },
            new TripwireTarget { Label = "Remove", DestinationUrl = "https://example.com/remove" },
        });
        string originalToken = tripwire.Targets[0].Token;
        string removedToken = tripwire.Targets[1].Token;

        var updated = await store.UpdateAsync(tripwire.Id, "After", new TripwireSettings { DiscordNotifications = true }, new[]
        {
            new TripwireTarget { Id = tripwire.Targets[0].Id, Label = "Kept", DestinationUrl = "https://example.com/new", Enabled = true },
            new TripwireTarget { Label = "Added", DestinationUrl = "https://example.org", Enabled = true },
        });

        Assert.NotNull(updated);
        Assert.Equal("After", updated!.Name);
        Assert.True(updated.Settings.DiscordNotifications);
        Assert.Equal(originalToken, updated.Targets.Single(target => target.Id == tripwire.Targets[0].Id).Token);
        Assert.Null(await store.ResolveTokenAsync(removedToken));
        Assert.Equal(2, updated.Targets.Count);
    }

    [Fact]
    public async Task SummarySnapshot_IsPrecomputedAndSurvivesReopeningTheStore()
    {
        string dbPath = Path.Combine(directory, "tripwires.db");
        using (var store = await CreateStoreAsync())
        {
            var tripwire = await store.CreateAsync("Summary", "Klives", new TripwireSettings(), new[]
            {
                new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
            });
            Assert.Equal(0, (await store.GetSummarySnapshotAsync(tripwire.Id))!.TotalTrips);

            await store.RecordEventAsync(tripwire, tripwire.Targets[0],
                NewEvent(tripwire, tripwire.Targets[0], "visitor", DateTimeOffset.UtcNow), 60);
            Assert.Equal(0, (await store.GetSummarySnapshotAsync(tripwire.Id))!.TotalTrips);

            await store.RefreshSummarySnapshotAsync(tripwire.Id);
            var snapshot = await store.GetSummarySnapshotAsync(tripwire.Id);
            Assert.Equal(1, snapshot!.TotalTrips);
            Assert.Equal(1, snapshot.UniqueTrips);
            Assert.Equal(1, snapshot.TripsLast24Hours);
            Assert.Contains("\"totalTrips\":1", await store.GetSummarySnapshotJsonAsync(tripwire.Id));
        }

        using var reopened = new TripwireStore(dbPath);
        await reopened.InitialiseAsync();
        string id = (await reopened.ListTripwireIdsAsync()).Single();
        Assert.Equal(1, (await reopened.GetSummarySnapshotAsync(id))!.TotalTrips);
        Assert.True(await reopened.DeleteAsync(id));
        Assert.Null(await reopened.GetSummarySnapshotAsync(id));
    }

    [Fact]
    public async Task ExpiredSummarySnapshot_IsNotServed()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Summary freshness", "Klives", new TripwireSettings(), new[]
        {
            new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
        });
        await using (var conn = new SqliteConnection($"Data Source={store.DbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tripwire_summary_snapshots SET generated_utc=$old WHERE tripwire_id=$id";
            cmd.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddMinutes(-4).ToString("O"));
            cmd.Parameters.AddWithValue("$id", tripwire.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.GetSummarySnapshotJsonAsync(tripwire.Id));
        await store.RefreshSummarySnapshotAsync(tripwire.Id);
        Assert.NotNull(await store.GetSummarySnapshotJsonAsync(tripwire.Id));
    }

    [Fact]
    public async Task EventPageTotals_TrackRetainedRowsAfterCleanupAndClear()
    {
        using var store = await CreateStoreAsync();
        var tripwire = await store.CreateAsync("Retention", "Klives", new TripwireSettings { RetentionDays = 1 }, new[]
        {
            new TripwireTarget { Label = "Old", DestinationUrl = "https://example.com/old" },
            new TripwireTarget { Label = "Recent", DestinationUrl = "https://example.com/recent" },
        });
        await store.RecordEventAsync(tripwire, tripwire.Targets[0],
            NewEvent(tripwire, tripwire.Targets[0], "old", DateTimeOffset.UtcNow.AddDays(-2)), 60);
        await store.RecordEventAsync(tripwire, tripwire.Targets[1],
            NewEvent(tripwire, tripwire.Targets[1], "recent", DateTimeOffset.UtcNow), 60);
        Assert.Equal(2, (await store.GetEventsAsync(tripwire.Id, null, 100, 0)).Total);

        await store.CleanupExpiredEventsAsync();
        Assert.Equal(1, (await store.GetEventsAsync(tripwire.Id, null, 100, 0)).Total);
        Assert.Equal(0, (await store.GetEventsAsync(tripwire.Id, tripwire.Targets[0].Id, 100, 0)).Total);
        Assert.Equal(1, (await store.GetEventsAsync(tripwire.Id, tripwire.Targets[1].Id, 100, 0)).Total);
        Assert.Empty((await store.GetEventsAsync(tripwire.Id, null, 100, int.MaxValue)).Items);

        Assert.True(await store.ClearEventsAsync(tripwire.Id));
        Assert.Equal(0, (await store.GetEventsAsync(tripwire.Id, null, 100, 0)).Total);
        Assert.Equal(0, (await store.GetEventsAsync(tripwire.Id, tripwire.Targets[1].Id, 100, 0)).Total);
    }

    [Fact]
    public async Task Initialise_MigratesExistingEventCounts()
    {
        string dbPath = Path.Combine(directory, "tripwires.db");
        string tripwireId;
        string targetId;
        using (var store = await CreateStoreAsync())
        {
            var tripwire = await store.CreateAsync("Legacy", "Klives", new TripwireSettings(), new[]
            {
                new TripwireTarget { Label = "Main", DestinationUrl = "https://example.com" },
            });
            tripwireId = tripwire.Id;
            targetId = tripwire.Targets[0].Id;
            await store.RecordEventAsync(tripwire, tripwire.Targets[0],
                NewEvent(tripwire, tripwire.Targets[0], "visitor", DateTimeOffset.UtcNow), 60);
        }

        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DROP TABLE tripwire_summary_snapshots;
                ALTER TABLE tripwires DROP COLUMN retained_event_count;
                ALTER TABLE tripwire_targets DROP COLUMN retained_event_count;";
            await cmd.ExecuteNonQueryAsync();
        }

        using var migrated = new TripwireStore(dbPath);
        await migrated.InitialiseAsync();
        Assert.Equal(1, (await migrated.GetEventsAsync(tripwireId, null, 100, 0)).Total);
        Assert.Equal(1, (await migrated.GetEventsAsync(tripwireId, targetId, 100, 0)).Total);
        await migrated.RefreshSummarySnapshotAsync(tripwireId);
        Assert.Equal(1, (await migrated.GetSummarySnapshotAsync(tripwireId))!.TotalTrips);
    }

    [Theory]
    [InlineData("https://example.com/path?campaign=launch", true)]
    [InlineData("http://127.0.0.1:8080/test", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("https://user:password@example.com/private", false)]
    [InlineData("not a url", false)]
    public void DestinationValidation_AllowsOnlySafeHttpLinks(string value, bool expected)
    {
        Assert.Equal(expected, TripwireService.TryNormalizeDestination(value, out _));
    }

    private async Task<TripwireStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new TripwireStore(Path.Combine(directory, "tripwires.db"));
        await store.InitialiseAsync();
        return store;
    }

    private static TripwireEvent NewEvent(TripwireRecord tripwire, TripwireTarget target, string visitor, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid().ToString("N"), TripwireId = tripwire.Id, TargetId = target.Id,
        TargetLabel = target.Label, DestinationUrl = target.DestinationUrl, TrippedUtc = at,
        VisitorHash = visitor, IpAddress = "203.0.113.10", DeviceType = "Desktop",
    };

    public void Dispose()
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}
