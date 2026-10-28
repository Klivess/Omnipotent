using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniBlox.Core
{
    /// <summary>
    /// A short-lived cache of expensive results (a compiled world's map report, a render) in which concurrent requests
    /// for one key share a single computation, and only a few computations run at once. The game page asks for the map
    /// report again after every build, and a builder rebuilds every minute or two: each request used to compute its own
    /// copy, so on Oct 24 2026 six to ten identical map reports ran side by side on the 4-core server during a build
    /// cycle and each took three to five minutes.
    /// </summary>
    public sealed class SingleFlightCache
    {
        private sealed class Entry
        {
            public required DateTime CreatedUtc { get; init; }
            public required Lazy<Task<object>> Value { get; init; }
        }

        private readonly TimeSpan keepFor;
        private readonly int maxEntries;
        private readonly SemaphoreSlim work;
        private readonly Func<DateTime> clock;
        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private int computing;

        public SingleFlightCache(TimeSpan keepFor, int maxEntries, int maxConcurrent, Func<DateTime>? clock = null)
        {
            this.keepFor = keepFor;
            this.maxEntries = maxEntries;
            work = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Computations running now (at most the concurrency given).</summary>
        public int Computing => Volatile.Read(ref computing);

        /// <summary>
        /// The cached value for the key, computing it once however many callers ask at the same time. A computation that
        /// throws is not kept: its callers get the exception and the next caller tries again.
        /// </summary>
        public async Task<T> GetAsync<T>(string key, Func<T> build) where T : class
        {
            while (true)
            {
                var now = clock();
                var entry = entries.GetOrAdd(key, _ => new Entry { CreatedUtc = now, Value = new Lazy<Task<object>>(() => ComputeAsync(build)) });
                if (now - entry.CreatedUtc >= keepFor && entry.Value.IsValueCreated && entry.Value.Value.IsCompleted)
                {
                    entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                    continue;
                }
                Trim();
                try
                {
                    return (T)await entry.Value.Value;
                }
                catch
                {
                    entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                    throw;
                }
            }
        }

        private async Task<object> ComputeAsync<T>(Func<T> build) where T : class
        {
            await work.WaitAsync();
            Interlocked.Increment(ref computing);
            try
            {
                // Off the caller's thread: the first caller would otherwise compute inside Lazy's lock.
                return await Task.Run(() => (object)build());
            }
            finally
            {
                Interlocked.Decrement(ref computing);
                work.Release();
            }
        }

        /// <summary>Drops the oldest finished entries past the limit (running ones stay: someone is waiting on them).</summary>
        private void Trim()
        {
            int over = entries.Count - maxEntries;
            if (over <= 0) return;
            foreach (var stale in entries.Where(kv => kv.Value.Value.IsValueCreated && kv.Value.Value.Value.IsCompleted)
                         .OrderBy(kv => kv.Value.CreatedUtc).Take(over).ToList())
                entries.TryRemove(stale);
        }
    }
}
