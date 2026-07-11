using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// The store's contract: serve only still-valid entries, apply every cacheability
/// gate, and reject a fill that raced a write. ETag and compressed variants must be
/// byte-identical to the live response path so a HIT is indistinguishable from a MISS.
/// </summary>
public sealed class ResponseCacheTests
{
    private static string K() => "test:" + Guid.NewGuid().ToString("N");
    private static byte[] Body(string s = "hello world") => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>Simulates a dispatcher fill: open scope, note a read, record a response, seal.</summary>
    private static (DependencyScope scope, ResponseRecording rec) Fill(
        string? depKey, byte[] body, Action? during = null,
        int status = 200, string contentType = "application/json", bool binary = false, bool streaming = false)
    {
        var scope = CacheDeps.OpenScope();
        if (depKey != null) CacheDeps.NoteRead(depKey);
        during?.Invoke();
        var rec = new ResponseRecording();
        if (streaming) rec.MarkStreaming();
        else rec.Record(status, contentType, null, body, binary);
        CacheDeps.Seal(scope);
        return (scope, rec);
    }

    [Fact]
    public void Store_Then_Hit_ReturnsSameBody()
    {
        var cache = new ResponseCache();
        string dep = K();
        byte[] body = Body();
        var (scope, rec) = Fill(dep, body);
        string key = "GET|/x||anon:" + dep;

        Assert.True(cache.TryStoreFromRecording(key, rec, scope));
        CacheEntry? hit = cache.TryGetValid(key);
        Assert.NotNull(hit);
        Assert.Equal(body, hit!.RawBody);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void Bump_InvalidatesAndPurgesEntry()
    {
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body());
        string key = "GET|/x||anon:" + dep;
        Assert.True(cache.TryStoreFromRecording(key, rec, scope));

        CacheDeps.Bump(dep);

        Assert.Null(cache.TryGetValid(key));
        Assert.Equal(0, cache.EntryCount);   // invalid entry purged on access
    }

    [Fact]
    public void RaceBumpBetweenFillAndStore_IsRejected()
    {
        // The classic hazard: a write lands after the handler read but before we store.
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body());     // noted dep at version 0
        string key = "GET|/x||anon:" + dep;

        CacheDeps.Bump(dep);                        // version 1 — read is now stale

        Assert.False(cache.TryStoreFromRecording(key, rec, scope));
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void Gate_NoTrackedReads_NotStored()
    {
        var cache = new ResponseCache();
        var (scope, rec) = Fill(depKey: null, Body());   // touched nothing tracked
        Assert.False(cache.TryStoreFromRecording("GET|/x||anon", rec, scope));
    }

    [Fact]
    public void Gate_WroteDuringFill_NotStored()
    {
        var cache = new ResponseCache();
        string dep = K();
        // Writes an unrelated key during the fill: read key stays valid, but the
        // write flag alone must veto caching (a GET with a hidden side effect).
        var (scope, rec) = Fill(dep, Body(), during: () => CacheDeps.Bump(K()));
        Assert.True(scope.WroteDuringFill);
        Assert.False(cache.TryStoreFromRecording("GET|/x||anon:" + dep, rec, scope));
    }

    [Fact]
    public void Gate_NonOkStatus_NotStored()
    {
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body(), status: 500);
        Assert.False(cache.TryStoreFromRecording("GET|/x||anon:" + dep, rec, scope));
    }

    [Fact]
    public void Gate_Streaming_NotStored()
    {
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body(), streaming: true);
        Assert.False(cache.TryStoreFromRecording("GET|/x||anon:" + dep, rec, scope));
    }

    [Fact]
    public void Gate_OverPerEntryCap_NotStored()
    {
        var cache = new ResponseCache();
        string dep = K();
        byte[] tooBig = new byte[ResponseCache.PerEntryCapBytes + 1];
        var (scope, rec) = Fill(dep, tooBig, contentType: "application/octet-stream", binary: true);
        Assert.False(cache.TryStoreFromRecording("GET|/x||anon:" + dep, rec, scope));
    }

    [Fact]
    public void Entry_ETag_MatchesLivePathWeakTag()
    {
        var cache = new ResponseCache();
        string dep = K();
        byte[] body = Body("some cached json payload");
        var (scope, rec) = Fill(dep, body);
        string key = "GET|/x||anon:" + dep;
        cache.TryStoreFromRecording(key, rec, scope);

        CacheEntry hit = cache.TryGetValid(key)!;
        Assert.Equal(HttpResponseHelpers.ComputeWeakETag(body), hit.ETag);
    }

    [Fact]
    public void Entry_Binary_HasNoETag()
    {
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body(), contentType: "image/png", binary: true);
        string key = "GET|/img||anon:" + dep;
        cache.TryStoreFromRecording(key, rec, scope);
        Assert.Null(cache.TryGetValid(key)!.ETag);
    }

    [Fact]
    public void Entry_Variant_MatchesLivePathCompression()
    {
        var cache = new ResponseCache();
        string dep = K();
        byte[] body = System.Text.Encoding.UTF8.GetBytes(new string('a', 4096) + "-varied-" + new string('b', 4096));
        var (scope, rec) = Fill(dep, body);
        string key = "GET|/x||anon:" + dep;
        cache.TryStoreFromRecording(key, rec, scope);

        CacheEntry hit = cache.TryGetValid(key)!;
        byte[] expected = HttpResponseHelpers.Compress(body, HttpResponseHelpers.ContentEncoding.Brotli);
        Assert.Equal(expected, hit.GetVariant(HttpResponseHelpers.ContentEncoding.Brotli));
    }

    [Fact]
    public void Eviction_EnforcesMaxEntries()
    {
        var cache = new ResponseCache();
        cache.Configure(maxBytes: 256L * 1024 * 1024, maxEntries: 2);

        for (int i = 0; i < 5; i++)
        {
            string dep = K();
            var (scope, rec) = Fill(dep, Body("entry" + i));
            cache.TryStoreFromRecording("GET|/x/" + i + "||anon", rec, scope);
        }

        Assert.True(cache.EntryCount <= 2, $"expected <= 2 entries, got {cache.EntryCount}");
    }

    [Fact]
    public void StoreFromParts_BatchPath_ProducesEquivalentEntry()
    {
        // The /batch path captures into a buffer instead of teeing the socket, then
        // stores via TryStoreFromParts. A hit from either path must be identical.
        var cache = new ResponseCache();
        string dep = K();
        byte[] body = Body("shared payload");

        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead(dep);
        CacheDeps.Seal(scope);

        string key = "GET|/x||anon:" + dep;
        Assert.True(cache.TryStoreFromParts(key, 200, "application/json", null, body, isBinary: false, scope));

        CacheEntry hit = cache.TryGetValid(key)!;
        Assert.Equal(body, hit.RawBody);
        Assert.Equal(HttpResponseHelpers.ComputeWeakETag(body), hit.ETag);
    }

    [Fact]
    public void StoreFromParts_RaceOrWrite_IsRejected()
    {
        var cache = new ResponseCache();
        string dep = K();
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead(dep);
        CacheDeps.Seal(scope);
        CacheDeps.Bump(dep);                        // raced after read

        Assert.False(cache.TryStoreFromParts("GET|/x||anon:" + dep, 200, "application/json", null, Body(), false, scope));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var cache = new ResponseCache();
        string dep = K();
        var (scope, rec) = Fill(dep, Body());
        cache.TryStoreFromRecording("GET|/x||anon:" + dep, rec, scope);
        Assert.Equal(1, cache.EntryCount);

        cache.Clear();
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(0, cache.TotalBytes);
    }
}
