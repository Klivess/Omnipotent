using System.Collections.Concurrent;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// The ambient dependency tracker underpins the never-stale guarantee: reads note a
/// version, writes bump it, and a fill is only cacheable if it wrote nothing tracked
/// and stayed uncacheable-free. These tests pin that contract, including the async
/// flow into Task.Run children and the seal-at-dispatch-end behaviour.
/// </summary>
public sealed class CacheDepsTests
{
    private static string K() => "test:" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Bump_IncrementsVersionMonotonically()
    {
        string k = K();
        Assert.Equal(0, CacheDeps.CurrentVersion(k));
        CacheDeps.Bump(k);
        Assert.Equal(1, CacheDeps.CurrentVersion(k));
        CacheDeps.Bump(k);
        Assert.Equal(2, CacheDeps.CurrentVersion(k));
    }

    [Fact]
    public void NoteRead_OutsideScope_IsNoOp()
    {
        // No scope open — must not throw and must record nothing.
        CacheDeps.NoteRead(K());
        Assert.Null(CacheDeps.CurrentScopeForTests);
    }

    [Fact]
    public void Scope_CapturesReadVersion_AndFirstNoteWins()
    {
        string k = K();
        CacheDeps.Bump(k);              // version 1
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead(k);          // records 1
        CacheDeps.Bump(k);             // version 2 (also marks wrote)
        CacheDeps.NoteRead(k);          // first-note-wins: still 1
        CacheDeps.Seal(scope);

        var reads = scope.SnapshotReads();
        Assert.Equal(1, reads[k]);
    }

    [Fact]
    public void Bump_DuringFill_LatchesWroteFlag()
    {
        string k = K();
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead(k);
        Assert.False(scope.WroteDuringFill);
        CacheDeps.Bump(k);
        Assert.True(scope.WroteDuringFill);
        CacheDeps.Seal(scope);
    }

    [Fact]
    public void MarkUncacheable_RecordsReason()
    {
        var scope = CacheDeps.OpenScope();
        CacheDeps.MarkUncacheable("uptime");
        CacheDeps.Seal(scope);
        Assert.Equal("uptime", scope.UncacheableReason);
    }

    [Fact]
    public void SealedScope_IgnoresLateReads_ButLateBumpsStillInvalidate()
    {
        string k = K();
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead(k);
        CacheDeps.Seal(scope);

        // A fire-and-forget child that still holds the sealed scope must not add reads.
        scope.NoteReadInternal("late", 5);
        Assert.False(scope.SnapshotReads().ContainsKey("late"));

        // But a late bump still moves the global version so any hit revalidation fails.
        long before = CacheDeps.CurrentVersion(k);
        CacheDeps.Bump(k);
        Assert.Equal(before + 1, CacheDeps.CurrentVersion(k));
    }

    [Fact]
    public async Task Scope_FlowsIntoTaskRunChildren()
    {
        // ExecutionContext carries the scope into parallel children (mirrors /batch and
        // handlers that fan out); every child's read must land on the same scope.
        var keys = Enumerable.Range(0, 16).Select(_ => K()).ToArray();
        var scope = CacheDeps.OpenScope();

        var tasks = keys.Select(key => Task.Run(() => CacheDeps.NoteRead(key))).ToArray();
        await Task.WhenAll(tasks);
        CacheDeps.Seal(scope);

        var reads = scope.SnapshotReads();
        foreach (string key in keys) Assert.True(reads.ContainsKey(key));
    }

    [Fact]
    public async Task ConcurrentBumps_AreLostlesslyCounted()
    {
        string k = K();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => { for (int i = 0; i < 100; i++) CacheDeps.Bump(k); }))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(800, CacheDeps.CurrentVersion(k));
    }
}
