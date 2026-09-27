using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Serialized trace-list and trace-detail snapshots. A single background reader
    /// owns all SQLite reads and JSON generation; HTTP threads only look up entries.
    /// Distinct pending requests and retained results are bounded independently.
    /// </summary>
    internal sealed class TelemetryTraceViews : IDisposable
    {
        internal readonly record struct ListQuery(string Range, long? FromMs, long? ToMs,
            string? Route, string? Method, long? MinMicros, int? StatusMin, string Sort, int Limit)
        {
            public string Key => string.Join('|', "list", Range, FromMs, ToMs, Route, Method,
                MinMicros, StatusMin, Sort, Limit);
            public bool NeedsRefresh(long nowMs) => !FromMs.HasValue || !ToMs.HasValue
                || ToMs.Value >= nowMs - 120_000;
        }

        private sealed class Snapshot
        {
            public required CacheEntry Entry;
            public long BuiltAtMs;
            public long AccessMs;
        }

        private readonly TelemetryDb _db;
        private readonly Func<long> _nowMs;
        private readonly ConcurrentDictionary<string, Snapshot> _views = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, long> _detailTimestamps = new();
        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
        private int _pendingCount;
        private readonly ConcurrentQueue<(string Key, Func<CacheEntry> Build)> _priorityJobs = new();
        private readonly ConcurrentQueue<(string Key, Func<CacheEntry> Build)> _jobs = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;
        private readonly ListQuery _defaultList = new("24h", null, null, null, null, null, null, "slowest", 150);
        private long _lastDefaultRefreshMs;
        private volatile bool _running = true;

        public TelemetryTraceViews(TelemetryDb db, Func<long> nowMs)
        {
            _db = db;
            _nowMs = nowMs;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "KliveAPI_TelemetryTraceViews", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
            Queue(_defaultList);
        }

        public CacheEntry? TryGetList(ListQuery query)
        {
            string key = query.Key;
            long now = _nowMs();
            if (_views.TryGetValue(key, out Snapshot? snapshot))
            {
                Interlocked.Exchange(ref snapshot.AccessMs, now);
                if (query.NeedsRefresh(now) && now - snapshot.BuiltAtMs >= 5_000)
                {
                    Queue(query);
                    if (now - snapshot.BuiltAtMs > 120_000) return null;
                }
                return snapshot.Entry;
            }
            Queue(query);
            return null;
        }

        public CacheEntry? TryGetDetail(long id)
        {
            string key = "detail|" + id.ToString("x16", CultureInfo.InvariantCulture);
            long now = _nowMs();
            if (_views.TryGetValue(key, out Snapshot? snapshot))
            {
                Interlocked.Exchange(ref snapshot.AccessMs, now);
                bool recentTrace = _detailTimestamps.TryGetValue(id, out long ts) && now - ts <= 3_600_000;
                if ((snapshot.Entry.StatusCode == 404 && now - snapshot.BuiltAtMs >= 5_000)
                    || (recentTrace && now - snapshot.BuiltAtMs >= 10_000))
                    QueueDetail(id, key);
                if ((snapshot.Entry.StatusCode == 404 && now - snapshot.BuiltAtMs > 30_000)
                    || (recentTrace && now - snapshot.BuiltAtMs > 120_000))
                    return null;
                return snapshot.Entry;
            }
            QueueDetail(id, key);
            return null;
        }

        private void Queue(ListQuery query) => QueueWork(query.Key, () => BuildList(query),
            priority: query.Key == _defaultList.Key);
        private void QueueDetail(long id, string key) => QueueWork(key, () => BuildDetail(id), priority: false);

        private void QueueWork(string key, Func<CacheEntry> build, bool priority)
        {
            if (!_running || !_pending.TryAdd(key, 0)) return;
            if (Interlocked.Increment(ref _pendingCount) > (priority ? 32 : 31))
            {
                Interlocked.Decrement(ref _pendingCount);
                _pending.TryRemove(key, out _);
                return;
            }
            (priority ? _priorityJobs : _jobs).Enqueue((key, build));
            _wake.Set();
        }

        private void WorkLoop()
        {
            while (_running)
            {
                long now = _nowMs();
                if (now - _lastDefaultRefreshMs >= 5_000)
                {
                    _lastDefaultRefreshMs = now;
                    Queue(_defaultList);
                }
                if (!_priorityJobs.TryDequeue(out var job) && !_jobs.TryDequeue(out job))
                { _wake.WaitOne(250); continue; }
                try
                {
                    CacheEntry entry = job.Build();
                    long builtAt = _nowMs();
                    _views[job.Key] = new Snapshot { Entry = entry, BuiltAtMs = builtAt, AccessMs = builtAt };
                    if (_views.Count > 128)
                    {
                        foreach (var old in _views.OrderBy(kv => Interlocked.Read(ref kv.Value.AccessMs)).Take(_views.Count - 128))
                        {
                            if (_views.TryRemove(old.Key, out _) && old.Key.StartsWith("detail|", StringComparison.Ordinal)
                                && long.TryParse(old.Key.AsSpan(7), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id))
                                _detailTimestamps.TryRemove(id, out _);
                        }
                    }
                }
                catch { /* A later read retries; failed background I/O cannot stall HTTP. */ }
                finally
                {
                    _pending.TryRemove(job.Key, out _);
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }

        private CacheEntry BuildList(ListQuery query)
        {
            long now = _nowMs();
            long from = query.FromMs ?? (query.Range == "all" ? 0 : now - TelemetryQuery.PresetDurationMs(query.Range));
            long to = query.ToMs ?? now + 60_000;
            List<TelemetryDb.TraceRow> rows = _db.QueryTraces(query.Route, query.Method, query.MinMicros,
                query.StatusMin, from, to, query.Sort, query.Limit);
            return Entry(TelemetryRoutes.BuildTraceListJson(rows, now, from, to), 200);
        }

        private CacheEntry BuildDetail(long id)
        {
            TelemetryDb.TraceRow? row = _db.GetTrace(id);
            if (row != null) _detailTimestamps[id] = row.Ts;
            else _detailTimestamps.TryRemove(id, out _);
            return row == null
                ? Entry("{\"error\":\"Trace not found (it may not be flushed yet, or was pruned).\"}", 404)
                : Entry(TelemetryRoutes.BuildTraceJson(row), 200);
        }

        private static CacheEntry Entry(string json, int status)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var entry = new CacheEntry(bytes, status, "application/json", null, false,
                new Dictionary<string, long>(), HttpResponseHelpers.ContentEncoding.None, null);
            if (bytes.Length >= 1024)
            {
                entry.GetVariant(HttpResponseHelpers.ContentEncoding.Brotli);
                entry.GetVariant(HttpResponseHelpers.ContentEncoding.Gzip);
            }
            return entry;
        }

        public void Dispose()
        {
            _running = false;
            _wake.Set();
            try { if (_worker.Join(2000)) _wake.Dispose(); } catch { }
        }
    }
}
