using System.Diagnostics;
using Omnipotent.Services.Omniscience;

namespace Omnipotent.Tests.Omniscience
{
    /// <summary>
    /// The Omniscience TTL response cache is what stands between the dashboard and
    /// full-table SQLite scans, so its three load-bearing behaviors get pinned here:
    /// fresh hits skip the builder, expired entries serve stale instantly while a
    /// background refresh lands (no request ever waits on a warm key), and mutation
    /// routes can invalidate by prefix so writes appear immediately.
    /// </summary>
    public class OmniscienceResponseCacheTests
    {
        [Fact]
        public async Task FreshEntry_ServedWithoutRebuilding()
        {
            string key = "test-fresh-" + Guid.NewGuid().ToString("N");
            int builds = 0;

            string first = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5),
                () => { Interlocked.Increment(ref builds); return "v1"; });
            string second = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5),
                () => { Interlocked.Increment(ref builds); return "v2"; });

            Assert.Equal("v1", first);
            Assert.Equal("v1", second);
            Assert.Equal(1, builds);
        }

        [Fact]
        public async Task ExpiredEntry_ServesStaleInstantly_ThenRefreshesInBackground()
        {
            string key = "test-swr-" + Guid.NewGuid().ToString("N");

            // Fill with an already-expired TTL so the next read sees a stale payload.
            string first = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.Zero, () => "v1");
            Assert.Equal("v1", first);

            // The refresh builder blocks; the read must NOT block with it.
            using var releaseBuilder = new ManualResetEventSlim(false);
            var stopwatch = Stopwatch.StartNew();
            string stale = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5),
                () => { releaseBuilder.Wait(TimeSpan.FromSeconds(10)); return "v2"; });
            stopwatch.Stop();

            Assert.Equal("v1", stale);
            Assert.True(stopwatch.ElapsedMilliseconds < 2000,
                $"stale read took {stopwatch.ElapsedMilliseconds}ms — it waited on the background rebuild");

            // Once the background refresh lands, reads serve the new payload.
            releaseBuilder.Set();
            string fresh = stale;
            for (int i = 0; i < 100 && fresh != "v2"; i++)
            {
                await Task.Delay(50);
                fresh = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5), () => "v3");
            }
            Assert.Equal("v2", fresh);
        }

        [Fact]
        public async Task InvalidateCachePrefix_ForcesRebuildOnNextRead()
        {
            string prefix = "test-inv-" + Guid.NewGuid().ToString("N");
            string key = prefix + "|l=60";

            string first = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5), () => "before");
            Assert.Equal("before", first);

            OmniscienceRoutes.InvalidateCachePrefix(prefix);

            string second = await OmniscienceRoutes.GetOrComputeAsync(key, TimeSpan.FromMinutes(5), () => "after");
            Assert.Equal("after", second);
        }
    }
}
