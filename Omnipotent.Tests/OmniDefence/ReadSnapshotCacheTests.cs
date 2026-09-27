using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence;

public sealed class ReadSnapshotCacheTests
{
    [Fact]
    public async Task RequestReadOnlyQueuesWork_AndPublishesCompletedJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), "defence-reads-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new OmniDefenceReadSnapshotCache(directory);
        cache.Start(stop.Token);

        try
        {
            Task<string> Build(CancellationToken ct) => CompleteAsync(ct);
            async Task<string> CompleteAsync(CancellationToken ct)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return "[{\"id\":1}]";
            }

            var first = cache.GetOrQueue("requests:unit", false, Build);
            Assert.Equal(ReadSnapshotState.Pending, first.State);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ReadSnapshotState.Pending,
                cache.GetOrQueue("requests:unit", false, Build).State);

            release.SetResult();
            Assert.True(SpinWait.SpinUntil(() =>
                cache.GetOrQueue("requests:unit", false, Build).State == ReadSnapshotState.Ready, 5_000));
            Assert.Equal("[{\"id\":1}]", cache.GetOrQueue("requests:unit", false, Build).Json);
        }
        finally
        {
            stop.Cancel();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InvalidationDiscardsAnInFlightOldResult()
    {
        string directory = Path.Combine(Path.GetTempPath(), "defence-reads-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new OmniDefenceReadSnapshotCache(directory);
        cache.Start(stop.Token);
        int attempts = 0;

        async Task<string> Build(CancellationToken ct)
        {
            int n = Interlocked.Increment(ref attempts);
            if (n == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return n == 1 ? "[{\"version\":1}]" : "[{\"version\":2}]";
        }

        try
        {
            Assert.Equal(ReadSnapshotState.Pending, cache.GetOrQueue("requests:race", false, Build).State);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Invalidate("requests:race");
            release.SetResult();
            Assert.True(SpinWait.SpinUntil(() =>
                cache.GetOrQueue("requests:race", false, Build).State == ReadSnapshotState.Ready, 5_000));
            Assert.Equal("[{\"version\":2}]", cache.GetOrQueue("requests:race", false, Build).Json);
            Assert.Equal(2, Volatile.Read(ref attempts));
        }
        finally
        {
            stop.Cancel();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
