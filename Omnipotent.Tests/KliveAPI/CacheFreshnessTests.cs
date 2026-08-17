using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// Two ways a response can be stale that the version model alone cannot see, and the
/// mechanisms that close them:
///
///   1. Wall-clock questions ("last 30 days", "is the link expired yet") change answer
///      with no write to bump — <see cref="CacheDeps.NoteTimeBucket"/> anchors those
///      fills to a quantized clock so they self-invalidate.
///   2. A ranged GET asks for a different body than the same URL without one, and the
///      hit path has no partial-content support — so Range must key separately.
/// </summary>
public sealed class CacheFreshnessTests
{
    private static byte[] Body(string s = "payload") => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>Pins the clock so bucket boundaries are crossed deliberately, not by luck of timing.</summary>
    private static IDisposable FrozenClock(DateTime start, out Action<TimeSpan> advance)
    {
        DateTime now = start;
        CacheDeps.UtcNowProvider = () => now;
        advance = delta => now = now.Add(delta);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => CacheDeps.UtcNowProvider = () => DateTime.UtcNow;
    }

    private static (DependencyScope scope, ResponseRecording rec) TimeBucketedFill(TimeSpan bucket)
    {
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteTimeBucket(bucket);
        var rec = new ResponseRecording();
        rec.Record(200, "application/json", null, Body(), false);
        CacheDeps.Seal(scope);
        return (scope, rec);
    }

    [Fact]
    public void TimeBucketedFill_IsCacheable_AndServedWithinItsBucket()
    {
        var cache = new ResponseCache();
        using var _ = FrozenClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), out var advance);

        var (scope, rec) = TimeBucketedFill(TimeSpan.FromSeconds(30));
        Assert.True(cache.TryStoreFromRecording("GET|/firm||anon", rec, scope));

        // Same bucket — the window it was built for still describes now.
        advance(TimeSpan.FromSeconds(5));
        Assert.NotNull(cache.TryGetValid("GET|/firm||anon"));
    }

    [Fact]
    public void TimeBucketedFill_SelfInvalidates_WhenTheWindowAdvances()
    {
        var cache = new ResponseCache();
        using var _ = FrozenClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), out var advance);

        var (scope, rec) = TimeBucketedFill(TimeSpan.FromSeconds(30));
        Assert.True(cache.TryStoreFromRecording("GET|/firm||anon", rec, scope));

        // No write happened — under versions alone this entry would live forever.
        advance(TimeSpan.FromSeconds(31));
        Assert.Null(cache.TryGetValid("GET|/firm||anon"));
    }

    [Fact]
    public void TimeBucket_VersionIsStableWithinBucket_AndMonotonicAcross()
    {
        using var _ = FrozenClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), out var advance);
        string key = "clock:" + TimeSpan.FromMinutes(1).Ticks;

        long first = CacheDeps.CurrentVersion(key);
        advance(TimeSpan.FromSeconds(30));
        Assert.Equal(first, CacheDeps.CurrentVersion(key));

        advance(TimeSpan.FromSeconds(31));
        Assert.True(CacheDeps.CurrentVersion(key) > first);
    }

    [Fact]
    public void NonPositiveBucket_MarksFillUncacheable_RatherThanCachingForever()
    {
        var cache = new ResponseCache();
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead("dataset:x");
        CacheDeps.NoteTimeBucket(TimeSpan.Zero);
        var rec = new ResponseRecording();
        rec.Record(200, "application/json", null, Body(), false);
        CacheDeps.Seal(scope);

        Assert.False(cache.TryStoreFromRecording("GET|/x||anon", rec, scope));
    }

    [Fact]
    public void BuildKey_RangedRequest_DoesNotCollideWithFullBodyEntry()
    {
        string full = ResponseCache.BuildKey("/KliveCloud/StreamVideo", null, "user1", null);
        string ranged = ResponseCache.BuildKey("/KliveCloud/StreamVideo", null, "user1", "bytes=200-400");
        string otherRange = ResponseCache.BuildKey("/KliveCloud/StreamVideo", null, "user1", "bytes=500-900");

        Assert.NotEqual(full, ranged);
        Assert.NotEqual(ranged, otherRange);
    }

    [Fact]
    public void BuildKey_NoRangeHeader_KeepsTheOriginalKeyShape()
    {
        // Existing entries must not be orphaned by the added component.
        Assert.Equal(
            ResponseCache.BuildKey("/x", null, "u"),
            ResponseCache.BuildKey("/x", null, "u", null));
        Assert.Equal(
            ResponseCache.BuildKey("/x", null, "u"),
            ResponseCache.BuildKey("/x", null, "u", ""));
    }
}
