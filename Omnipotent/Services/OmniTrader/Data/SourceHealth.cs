using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.OmniTrader.Data
{
    /// <summary>Live health of one external data source.</summary>
    public sealed class SourceHealthRecord
    {
        public required string Source { get; init; }
        public string Category { get; set; } = "";
        public long Calls { get; set; }
        public long Failures { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastError { get; set; }
        public double LastLatencyMs { get; set; }
        public double AvgLatencyMs { get; set; }

        public string State => LastSuccessUtc == null && LastFailureUtc == null ? "Unknown"
            : ConsecutiveFailures >= 3 ? "Down"
            : ConsecutiveFailures > 0 ? "Degraded"
            : "Up";
    }

    /// <summary>
    /// Process-wide record of every free data source OmniTrader calls: success/failure counts,
    /// latency and the last error. Feeds the Systems page and lets routers skip a source that is
    /// plainly down instead of paying its timeout on every request.
    /// </summary>
    public static class SourceHealth
    {
        private static readonly ConcurrentDictionary<string, SourceHealthRecord> records = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<SourceHealthRecord> Snapshot()
            => records.Values.OrderBy(r => r.Category).ThenBy(r => r.Source).ToList();

        public static SourceHealthRecord Get(string source, string category = "")
            => records.GetOrAdd(source, s => new SourceHealthRecord { Source = s, Category = category });

        public static void Success(string source, double latencyMs, string category = "")
        {
            var r = Get(source, category);
            lock (r)
            {
                if (!string.IsNullOrEmpty(category)) r.Category = category;
                r.Calls++;
                r.ConsecutiveFailures = 0;
                r.LastSuccessUtc = DateTime.UtcNow;
                r.LastLatencyMs = latencyMs;
                r.AvgLatencyMs = r.AvgLatencyMs <= 0 ? latencyMs : r.AvgLatencyMs * 0.8 + latencyMs * 0.2;
            }
        }

        public static void Failure(string source, string error, string category = "")
        {
            var r = Get(source, category);
            lock (r)
            {
                if (!string.IsNullOrEmpty(category)) r.Category = category;
                r.Calls++;
                r.Failures++;
                r.ConsecutiveFailures++;
                r.LastFailureUtc = DateTime.UtcNow;
                r.LastError = error.Length > 300 ? error[..300] : error;
            }
        }

        /// <summary>True when the source has failed repeatedly and recently — callers should go
        /// straight to the next source. Retried after a cool-down so a recovered source is found.</summary>
        public static bool IsBackingOff(string source)
        {
            if (!records.TryGetValue(source, out var r)) return false;
            lock (r)
            {
                if (r.ConsecutiveFailures < 3 || r.LastFailureUtc == null) return false;
                var cooldown = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(6, r.ConsecutiveFailures - 3))));
                return DateTime.UtcNow - r.LastFailureUtc.Value < cooldown;
            }
        }

        /// <summary>Times a call and records its outcome.</summary>
        public static async Task<T> TrackAsync<T>(string source, string category, Func<Task<T>> call)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await call();
                Success(source, sw.Elapsed.TotalMilliseconds, category);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Failure(source, ex.Message, category);
                throw;
            }
        }
    }

    /// <summary>Collapses concurrent identical requests onto one in-flight task.</summary>
    public sealed class SingleFlight<TResult>
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<TResult>>> inflight = new(StringComparer.Ordinal);

        public async Task<TResult> RunAsync(string key, Func<Task<TResult>> work)
        {
            var lazy = inflight.GetOrAdd(key, _ => new Lazy<Task<TResult>>(work, LazyThreadSafetyMode.ExecutionAndPublication));
            try { return await lazy.Value; }
            finally { inflight.TryRemove(new KeyValuePair<string, Lazy<Task<TResult>>>(key, lazy)); }
        }
    }

    /// <summary>Small in-memory TTL cache for data-hub responses.</summary>
    public sealed class TtlCache
    {
        private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, object Value)> entries = new(StringComparer.Ordinal);
        private readonly SingleFlight<object?> flights = new();
        private readonly int maxEntries;

        public TtlCache(int maxEntries = 5000) { this.maxEntries = maxEntries; }

        public async Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory) where T : class
        {
            if (entries.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow) return (T)hit.Value;
            var value = await flights.RunAsync(key, async () => (object?)await factory());
            if (value != null)
            {
                if (entries.Count >= maxEntries) Evict();
                entries[key] = (DateTime.UtcNow + ttl, value);
            }
            else if (hit.Value is T stale)
            {
                // A failed refresh keeps serving the last good value rather than nothing.
                return stale;
            }
            return (T?)value;
        }

        public bool TryGetStale<T>(string key, out T? value) where T : class
        {
            if (entries.TryGetValue(key, out var hit) && hit.Value is T t) { value = t; return true; }
            value = null;
            return false;
        }

        public void Invalidate(string prefix)
        {
            foreach (var k in entries.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) entries.TryRemove(k, out _);
        }

        private void Evict()
        {
            var now = DateTime.UtcNow;
            foreach (var (k, v) in entries)
                if (v.ExpiresUtc <= now) entries.TryRemove(k, out _);
            if (entries.Count < maxEntries) return;
            foreach (var k in entries.OrderBy(e => e.Value.ExpiresUtc).Take(maxEntries / 5).Select(e => e.Key).ToList())
                entries.TryRemove(k, out _);
        }
    }
}
