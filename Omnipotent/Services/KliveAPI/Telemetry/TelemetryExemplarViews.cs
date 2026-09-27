using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Trace exemplars for route charts, read from SQLite by one bounded background
    /// worker. The telemetry aggregation thread never opens SQLite to build a view.
    /// </summary>
    internal sealed class TelemetryExemplarViews : IDisposable
    {
        private sealed class Snapshot
        {
            public required TelemetryDb.TraceRow[] Rows;
            public long BuiltAtMs;
            public long AccessMs;
        }

        private readonly TelemetryDb _db;
        private readonly Func<long> _nowMs;
        private readonly ConcurrentDictionary<string, Snapshot> _views = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
        private int _pendingCount;
        private readonly ConcurrentQueue<(string Key, string Route, string Method, long From, long To)> _jobs = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;
        private volatile bool _running = true;

        public TelemetryExemplarViews(TelemetryDb db, Func<long> nowMs)
        {
            _db = db;
            _nowMs = nowMs;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "KliveAPI_TelemetryExemplars", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        public TelemetryDb.TraceRow[]? TryGet(string series, TelemetryQuery query, long from, long to)
        {
            string key = series + '|' + (query.RangeKey ?? query.FromMs + "-" + query.ToMs);
            long now = _nowMs();
            bool recent = query.IsPreset || query.ToMs >= now - 120_000;
            if (_views.TryGetValue(key, out Snapshot? snapshot))
            {
                Interlocked.Exchange(ref snapshot.AccessMs, now);
                if (recent && now - snapshot.BuiltAtMs >= 30_000)
                    Queue(key, series, from, to);
                if (recent && now - snapshot.BuiltAtMs > 120_000)
                    return null;
                return snapshot.Rows;
            }
            Queue(key, series, from, to);
            return null;
        }

        private void Queue(string key, string series, long from, long to)
        {
            if (!_running || !_pending.TryAdd(key, 0)) return;
            if (Interlocked.Increment(ref _pendingCount) > 32)
            {
                Interlocked.Decrement(ref _pendingCount);
                _pending.TryRemove(key, out _);
                return;
            }
            int sp = series.IndexOf(' ');
            if (sp <= 0)
            {
                _pending.TryRemove(key, out _);
                Interlocked.Decrement(ref _pendingCount);
                return;
            }
            _jobs.Enqueue((key, series[(sp + 1)..], series[..sp], from, to));
            _wake.Set();
        }

        private void WorkLoop()
        {
            while (_running)
            {
                if (!_jobs.TryDequeue(out var job)) { _wake.WaitOne(250); continue; }
                try
                {
                    var rows = _db.QueryTraces(job.Route, job.Method, null, null,
                        job.From, job.To, "slowest", 12).ToArray();
                    long now = _nowMs();
                    _views[job.Key] = new Snapshot { Rows = rows, BuiltAtMs = now, AccessMs = now };
                    if (_views.Count > 128)
                    {
                        foreach (var old in _views.OrderBy(kv => Interlocked.Read(ref kv.Value.AccessMs)).Take(_views.Count - 128))
                            _views.TryRemove(old.Key, out _);
                    }
                }
                catch { /* A later lookup may retry; SQLite never blocks aggregation. */ }
                finally
                {
                    _pending.TryRemove(job.Key, out _);
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _wake.Set();
            try { if (_worker.Join(2000)) _wake.Dispose(); } catch { }
        }
    }
}
