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
