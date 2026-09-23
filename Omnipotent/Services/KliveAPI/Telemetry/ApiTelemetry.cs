using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>Live-tunable telemetry settings (refreshed from OmniSettings every 15 s).</summary>
    public sealed class TelemetryOptions
    {
        public volatile bool Disabled;
        public volatile bool RumDisabled;
        public long SlowTraceMs = 1000;
        public int SamplesPerRouteMinute = 2;
        public double ApdexTargetMs = 250;
        public int Retention1mDays = 14;
        public int Retention1hDays = 400;
        public int TraceRetentionDays = 7;
    }

    /// <summary>
    /// The KliveAPI telemetry engine.
    ///
    /// Hot path: <see cref="Record"/> is one non-blocking channel write — never a lock,
    /// never an await, never I/O. Everything else happens on ONE dedicated engine thread
    /// that exclusively owns all aggregation state (so none of it needs locks):
    ///   traces → per-series, per-tier buckets (10s/1m/1h/1d, UTC aligned)
    ///   bucket closes → persistence batches (handed to a separate DB writer thread)
    ///   bucket closes → rebuilt materialized views (pre-serialized + pre-compressed)
    ///   ad-hoc queries → computed from in-memory tiers only, bounded in points
    /// Readers only ever touch <see cref="_views"/> (immutable entries swapped atomically)
    /// or await a query the engine thread answers.
    /// </summary>
    internal sealed partial class ApiTelemetry : IDisposable
    {
        public const string GlobalSeries = "*";
        public const string BatchItemsSeries = "*batch";
        public const string DeniedSeries = "(denied)";
        public const string UnmatchedSeries = "(unmatched)";
        public const string OtherSeries = "(other)";
        public const string PreflightSeries = "(preflight)";
        private const int MaxRouteSeries = 2000;

        public static bool IsLongLived(string series) =>
            series.Length > 0 && (series[0] == '*' || series[0] == '(');

        public TelemetryOptions Options { get; }
        public Action<string>? Log { get; set; }
        /// <summary>Test hook: the engine's clock (UTC epoch ms).</summary>
        public Func<long> NowMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private readonly Channel<RequestTrace> _traceChannel;
        private readonly Channel<RumSample> _rumChannel;
        private readonly ConcurrentQueue<double[]> _runtimeSamples = new();
        private readonly ConcurrentQueue<QueryWork> _queries = new();
        private readonly ManualResetEventSlim _wake = new(false);

        private long _recorded, _dropped, _rumReceived, _rumDropped, _folded;
        private double[]? _lastRuntimeSample;

        // ── engine-thread-owned state ──
        private readonly TierStore<TelemetryBucket>[] _req = new TierStore<TelemetryBucket>[4];
        private readonly TierStore<RumBucket>[] _rum = new TierStore<RumBucket>[4];
        private readonly TierStore<RuntimeBucket>[] _rt = new TierStore<RuntimeBucket>[4];
        private readonly HashSet<string> _knownRouteSeries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _sampledThisMinute = new(StringComparer.Ordinal);
        private readonly Dictionary<long, long> _keptTraceIds = new(); // id → kept-at ms (for RUM join)
        private readonly List<TelemetryDb.TraceRow> _pendingTraces = new();
        private readonly Dictionary<long, TelemetryDb.TraceRow> _pendingById = new();

        // ── views (read lock-free from request threads) ──
        private readonly ConcurrentDictionary<string, CacheEntry> _views = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _routeViewLastAccess = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _viewBuildMs = new(StringComparer.Ordinal);
        private long _viewEpoch;
        private long _lastViewBuildUtcMs;
        private byte[]? _liveTick;
        private long _lastLiveUtcMs;

        /// <summary>Latest 1-second live tick (JSON), for the live stream / live route.</summary>
        public byte[]? LiveTick => Volatile.Read(ref _liveTick);

        // ── persistence ──
        private readonly TelemetryDb? _db;
        private readonly string? _legacyStatsPath;
        private readonly BlockingCollection<Action<TelemetryDb>> _dbWork = new(new ConcurrentQueue<Action<TelemetryDb>>(), 512);
        private Thread? _dbThread;
        private long _lastFlushUtcMs, _dbErrors;

        private Thread? _thread;
        private volatile bool _running;
        private long _startedUtcMs;

        public ApiTelemetry(TelemetryOptions? options = null, TelemetryDb? db = null, string? legacyStatsPath = null)
        {
            Options = options ?? new TelemetryOptions();
            _db = db;
            _legacyStatsPath = legacyStatsPath;

            _traceChannel = Channel.CreateBounded<RequestTrace>(
                new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
                _ => Interlocked.Increment(ref _dropped));
            _rumChannel = Channel.CreateBounded<RumSample>(
                new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
                _ => Interlocked.Increment(ref _rumDropped));

            foreach (TelemetryTier tier in TelemetryTiers.All)
            {
                int i = (int)tier;
                _req[i] = new TierStore<TelemetryBucket>(tier, () => new TelemetryBucket(), (a, b) => a.Merge(b), b => b.Freeze());
                _rum[i] = new TierStore<RumBucket>(tier, () => new RumBucket(), (a, b) => a.Merge(b), b => b.Freeze());
                _rt[i] = new TierStore<RuntimeBucket>(tier, () => new RuntimeBucket(), (a, b) => a.Merge(b), b => b);
            }
        }

        // ════════════════════════ hot path ════════════════════════

        /// <summary>Queues a completed trace. Never blocks, never throws.</summary>
        public void Record(RequestTrace trace)
        {
            try
            {
                if (Options.Disabled || trace == null) return;
                trace.Complete();
                Interlocked.Increment(ref _recorded);
                _traceChannel.Writer.TryWrite(trace);
            }
            catch { /* telemetry must never affect a request */ }
        }

        public bool RecordRum(RumSample sample)
        {
            if (Options.RumDisabled || sample == null) return false;
            Interlocked.Increment(ref _rumReceived);
            return _rumChannel.Writer.TryWrite(sample);
        }

        public void RecordRuntimeSample(double[] sample)
        {
            if (sample == null || sample.Length != RuntimeGauges.Count) return;
            Volatile.Write(ref _lastRuntimeSample, sample);
            if (_runtimeSamples.Count < 10_000) _runtimeSamples.Enqueue(sample);
        }

        public int Backlog => _traceChannel.Reader.CanCount ? _traceChannel.Reader.Count : 0;
        public long Dropped => Interlocked.Read(ref _dropped);
        public long Recorded => Interlocked.Read(ref _recorded);

        // ════════════════════════ lifecycle ════════════════════════

        public void Start()
        {
            if (_running) return;
            _running = true;
            _startedUtcMs = NowMs();
            if (_db != null)
            {
                _dbThread = new Thread(DbWriterLoop) { IsBackground = true, Name = "KliveAPI_TelemetryDb" };
                _dbThread.Start();
            }
            _thread = new Thread(EngineLoop) { IsBackground = true, Name = "KliveAPI_Telemetry", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _wake.Set();
            try { _thread?.Join(5000); } catch { }
            try { _dbWork.CompleteAdding(); _dbThread?.Join(10_000); } catch { }
        }

        public void Dispose() => Stop();

        private void EngineLoop()
        {
            try { LoadHistory(); }
            catch (Exception ex) { Log?.Invoke("Telemetry history load failed: " + ex.Message); }

            AdvanceClock(NowMs());
            RebuildViews(ViewGroup.All);

            while (_running)
            {
                try
                {
                    Pump();
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Telemetry engine error: " + ex);
                }
                _wake.Wait(50);
                _wake.Reset();
            }

            // Final drain + checkpoint of the open buckets so a restart loses nothing.
            try
            {
                DrainInputs(int.MaxValue);
                CheckpointOpen(final: true);
            }
            catch (Exception ex) { Log?.Invoke("Telemetry shutdown flush failed: " + ex.Message); }
        }

        /// <summary>One engine iteration. Public-internal so tests can drive the engine without its thread.</summary>
        internal void Pump()
        {
            DrainInputs(20_000);
            long now = NowMs();
            ViewGroup dirty = AdvanceClock(now);
            if (dirty != ViewGroup.None) RebuildViews(dirty);
            if (now - _lastLiveUtcMs >= 1000)
            {
                _lastLiveUtcMs = now;
                try { Volatile.Write(ref _liveTick, BuildLive(now)); } catch { }
            }
            ProcessQueries();
            EvictColdRouteViews();
        }

        private void DrainInputs(int maxTraces)
        {
            int n = 0;
            while (n < maxTraces && _traceChannel.Reader.TryRead(out RequestTrace? t))
            {
                Fold(t);
                n++;
                // Answer queries promptly even while a large backlog drains.
                if ((n & 4095) == 0 && !_queries.IsEmpty) ProcessQueries();
            }
            while (_rumChannel.Reader.TryRead(out RumSample? s)) FoldRum(s);
            while (_runtimeSamples.TryDequeue(out double[]? sample))
            {
                foreach (var store in _rt) store.GetOrCreateOpen(GlobalSeries).Fold(sample);
            }
        }

        // ════════════════════════ folding ════════════════════════

        public static string RouteSeriesKey(string method, string route) => method + " " + route;

        private string ResolveSeries(RequestTrace t)
        {
            if (t.SeriesOverride != null) return t.SeriesOverride;
            if (t.Denied) return DeniedSeries;
            if (!t.Matched) return UnmatchedSeries;
            string key = RouteSeriesKey(t.Method, t.Route);
            if (_knownRouteSeries.Contains(key)) return key;
            if (_knownRouteSeries.Count >= MaxRouteSeries) return OtherSeries;
            _knownRouteSeries.Add(key);
            return key;
        }

        internal void Fold(RequestTrace t)
        {
            string series = ResolveSeries(t);
            TelemetryTier[] routeTiers = { TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 };
            foreach (TelemetryTier tier in routeTiers) _req[(int)tier].GetOrCreateOpen(series).Fold(t);

            if (t.ViaBatch)
            {
                foreach (TelemetryTier tier in routeTiers) _req[(int)tier].GetOrCreateOpen(BatchItemsSeries).Fold(t);
            }
            else if (!t.Denied && t.SeriesOverride == null)
            {
                foreach (var store in _req) store.GetOrCreateOpen(GlobalSeries).Fold(t);
            }
            _folded++;

            MaybeKeepTrace(t, series);
        }

        private void MaybeKeepTrace(RequestTrace t, string series)
        {
            long totalMicros = RequestTrace.TicksToMicros(t.LatencyTicks);
            int flags = 0;
            if (totalMicros >= Options.SlowTraceMs * 1000) flags |= TelemetryDb.FlagSlow;
            if (t.StatusCode >= 500) flags |= TelemetryDb.FlagError;
            if (t.ClientDisconnected) flags |= TelemetryDb.FlagDisconnect;
            if (t.Exception) flags |= TelemetryDb.FlagException;
            if (flags == 0 && !t.Denied)
            {
                int already = _sampledThisMinute.GetValueOrDefault(series);
                if (already < Options.SamplesPerRouteMinute)
                {
                    _sampledThisMinute[series] = already + 1;
                    flags |= TelemetryDb.FlagSample;
                }
            }
            if (flags == 0) return;
            if (t.ViaBatch) flags |= TelemetryDb.FlagBatchItem;
            if (t.NotModified) flags |= TelemetryDb.FlagNotModified;

            long endMs = TraceEndUtcMs(t);
            _pendingTraces.Add(new TelemetryDb.TraceRow
            {
                Id = t.TraceId,
                Ts = endMs,
                Route = t.Route,
                Method = t.Method,
                Status = t.StatusCode,
                TotalMicros = totalMicros,
                Cache = (int)t.Cache,
                Origin = t.Origin,
                Profile = t.ProfileName ?? t.ProfileId,
                BytesIn = t.RequestBytes,
                BytesOut = t.ResponseBytes,
                BytesRaw = t.ResponseRawBytes,
                Encoding = t.Encoding,
                Flags = flags,
                Reason = t.DenyReason,
                Stages = EncodeStages(t),
                Spans = EncodeSpans(t),
            });
            _pendingById[t.TraceId] = _pendingTraces[^1];
            _keptTraceIds[t.TraceId] = endMs;
            if (_pendingTraces.Count >= 256) FlushPendingTraces();
        }

        public static long TraceEndUtcMs(RequestTrace t) =>
            (t.StartUtcTicks + RequestTrace.TicksToTimeSpanTicks(t.TotalTicks) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

        public static byte[] EncodeStages(RequestTrace t)
        {
            using var ms = new MemoryStream(48);
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)t.StageTicks.Length);
                foreach (long ticks in t.StageTicks) w.Write7BitEncodedInt64(RequestTrace.TicksToMicros(ticks));
                w.Write7BitEncodedInt(t.InFlightAtStart);
                w.Write7BitEncodedInt64(t.PendingWorkAtStart);
            }
            return ms.ToArray();
        }

        public static (long[] StageMicros, int InFlight, long Pending) DecodeStages(byte[] blob)
        {
            var result = new long[TelemetryStages.Count];
            int inFlight = 0; long pending = 0;
            try
            {
                using var r = new BinaryReader(new MemoryStream(blob));
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    long v = r.Read7BitEncodedInt64();
                    if (i < result.Length) result[i] = v;
                }
                if (r.BaseStream.Position < r.BaseStream.Length)
                {
                    inFlight = r.Read7BitEncodedInt();
                    pending = r.Read7BitEncodedInt64();
                }
            }
            catch { }
            return (result, inFlight, pending);
        }

        private static string? EncodeSpans(RequestTrace t)
        {
            if (t.SpanCount == 0) return null;
            var parts = new List<string>(t.SpanCount);
            for (int i = 0; i < t.SpanCount; i++)
            {
                parts.Add($"{t.SpanName(i).Replace('|', '/').Replace(';', ',')};{RequestTrace.TicksToMicros(t.SpanStartTicks(i))};{RequestTrace.TicksToMicros(t.SpanTicks(i))}");
            }
            return string.Join("|", parts);
        }

        private void FoldRum(RumSample s)
        {
            string series = RouteSeriesKey(s.Method, s.Route);
            if (!_knownRouteSeries.Contains(series)) series = series.EndsWith(" /batch", StringComparison.Ordinal) ? series : UnmatchedSeries;
            TelemetryTier[] tiers = { TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 };
            foreach (TelemetryTier tier in tiers)
            {
                _rum[(int)tier].GetOrCreateOpen(series).Fold(s);
                _rum[(int)tier].GetOrCreateOpen(GlobalSeries).Fold(s);
            }
            _rum[(int)TelemetryTier.S10].GetOrCreateOpen(GlobalSeries).Fold(s);

            if (s.TraceId != 0 && _pendingById.TryGetValue(s.TraceId, out TelemetryDb.TraceRow? pendingRow))
            {
                // Beacon beat the trace to disk: attach directly, no UPDATE needed.
                pendingRow.Client = EncodeClient(s);
            }
            else if (s.TraceId != 0 && _keptTraceIds.ContainsKey(s.TraceId) && _db != null)
            {
                byte[] client = EncodeClient(s);
                long id = s.TraceId;
                EnqueueDb(db => db.UpdateTraceClient(new[] { (id, client) }));
            }
        }

        public static byte[] EncodeClient(RumSample s)
        {
            using var ms = new MemoryStream(32);
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)s.PhaseMicros.Length);
                foreach (long v in s.PhaseMicros) w.Write7BitEncodedInt64(v);
                w.Write(s.Reused);
                w.Write7BitEncodedInt64(s.TransferBytes);
            }
            return ms.ToArray();
        }

        public static (long[] Phases, bool Reused, long Transfer) DecodeClient(byte[] blob)
        {
            var phases = new long[RumPhases.Count];
            bool reused = false; long transfer = 0;
            try
            {
                using var r = new BinaryReader(new MemoryStream(blob));
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    long v = r.Read7BitEncodedInt64();
                    if (i < phases.Length) phases[i] = v;
                }
                reused = r.ReadBoolean();
                transfer = r.Read7BitEncodedInt64();
            }
            catch { }
            return (phases, reused, transfer);
        }

        // ════════════════════════ clock / persistence ════════════════════════

        [Flags]
        internal enum ViewGroup
        {
            None = 0,
            Short = 1,   // 15m / 1h / 6h — every 10 s close
            Medium = 2,  // 24h / 7d + weekly — every minute close
            Long = 4,    // 30d / 90d / 1y / all — every hour close
            All = 7,
        }

        internal ViewGroup AdvanceClock(long nowMs)
        {
            ViewGroup dirty = ViewGroup.None;

            if (_req[0].Advance(nowMs) is not null | _rt[0].Advance(nowMs) is not null | _rum[0].Advance(nowMs) is not null)
            {
                dirty |= ViewGroup.Short;
                Interlocked.Increment(ref _viewEpoch);
                PruneMemory(TelemetryTier.S10, nowMs);
                FlushPendingTraces();
            }

            var m1 = _req[1].Advance(nowMs);
            var rumM1 = _rum[1].Advance(nowMs);
            var rtM1 = _rt[1].Advance(nowMs);
            if (m1 != null || rumM1 != null || rtM1 != null)
            {
                dirty |= ViewGroup.Medium | ViewGroup.Short;
                _sampledThisMinute.Clear();
                PersistClosed(TelemetryTier.M1, m1, rumM1, rtM1);
                CheckpointOpen(final: false);
                FlushPendingTraces();
                PruneMemory(TelemetryTier.M1, nowMs);
                PruneKeptTraceIds(nowMs);
            }

            var h1 = _req[2].Advance(nowMs);
            var rumH1 = _rum[2].Advance(nowMs);
            var rtH1 = _rt[2].Advance(nowMs);
            if (h1 != null || rumH1 != null || rtH1 != null)
            {
                dirty |= ViewGroup.All;
                PersistClosed(TelemetryTier.H1, h1, rumH1, rtH1);
                PruneMemory(TelemetryTier.H1, nowMs);
                PruneDisk(nowMs);
            }

            var d1 = _req[3].Advance(nowMs);
            var rumD1 = _rum[3].Advance(nowMs);
            var rtD1 = _rt[3].Advance(nowMs);
            if (d1 != null || rumD1 != null || rtD1 != null)
            {
                dirty |= ViewGroup.All;
                PersistClosed(TelemetryTier.D1, d1, rumD1, rtD1);
                PruneMemory(TelemetryTier.D1, nowMs);
            }

            // Short views also refresh on a 10 s cadence even when idle (honest AsOf + live tail).
            if (dirty == ViewGroup.None && nowMs - _lastViewBuildUtcMs >= 10_000) dirty |= ViewGroup.Short;
            return dirty;
        }

        /// <summary>In-memory retention per tier: (route series, long-lived pseudo-series).</summary>
        internal (long RouteMs, long LongMs) MemoryRetention(TelemetryTier tier) => tier switch
        {
            TelemetryTier.S10 => (6 * 3_600_000L, 6 * 3_600_000L),
            TelemetryTier.M1 => (24 * 3_600_000L, 48 * 3_600_000L),
            TelemetryTier.H1 => (8 * 86_400_000L, Math.Max(8, Options.Retention1hDays) * 86_400_000L),
            _ => (400 * 86_400_000L, long.MaxValue / 4),
        };

        private void PruneMemory(TelemetryTier tier, long nowMs)
        {
            var (routeMs, longMs) = MemoryRetention(tier);
            _req[(int)tier].Prune(nowMs, routeMs, longMs, IsLongLived);
            _rum[(int)tier].Prune(nowMs, routeMs, longMs, IsLongLived);
            _rt[(int)tier].Prune(nowMs, routeMs, longMs, IsLongLived);
        }

        private void PruneKeptTraceIds(long nowMs)
        {
            if (_keptTraceIds.Count < 20_000) return;
            long cutoff = nowMs - 3_600_000;
            foreach (long id in _keptTraceIds.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _keptTraceIds.Remove(id);
        }

        private void PersistClosed(TelemetryTier tier,
            (long Start, Dictionary<string, TelemetryBucket> Series)? req,
            (long Start, Dictionary<string, RumBucket> Series)? rum,
            (long Start, Dictionary<string, RuntimeBucket> Series)? rt)
        {
            if (_db == null) return;
            var rows = new List<TelemetryDb.BucketRow>();
            if (req is { } r) foreach (var (s, b) in r.Series) rows.Add(new(TelemetryDb.KindRequests, tier, r.Start, s, b.Serialize()));
            if (rum is { } u) foreach (var (s, b) in u.Series) rows.Add(new(TelemetryDb.KindRum, tier, u.Start, s, b.Serialize()));
            if (rt is { } g) foreach (var (s, b) in g.Series) rows.Add(new(TelemetryDb.KindRuntime, tier, g.Start, s, b.Serialize()));
            if (rows.Count == 0) return;
            EnqueueDb(db =>
            {
                db.UpsertBuckets(rows);
                Interlocked.Exchange(ref _lastFlushUtcMs, NowMs());
            });
        }

        /// <summary>
        /// Upserts the still-open 1h/1d buckets as partials so a restart resumes them
        /// instead of losing up to a day. Runs every minute close (and at shutdown).
        /// </summary>
        private void CheckpointOpen(bool final)
        {
            if (_db == null) return;
            var rows = new List<TelemetryDb.BucketRow>();
            TelemetryTier[] tiers = final
                ? new[] { TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 }
                : new[] { TelemetryTier.H1, TelemetryTier.D1 };
            foreach (TelemetryTier tier in tiers)
            {
                int i = (int)tier;
                if (_req[i].OpenStart != long.MinValue)
                    foreach (var (s, b) in _req[i].Open) rows.Add(new(TelemetryDb.KindRequests, tier, _req[i].OpenStart, s, b.Serialize()));
                if (_rum[i].OpenStart != long.MinValue)
                    foreach (var (s, b) in _rum[i].Open) rows.Add(new(TelemetryDb.KindRum, tier, _rum[i].OpenStart, s, b.Serialize()));
                if (_rt[i].OpenStart != long.MinValue)
                    foreach (var (s, b) in _rt[i].Open) rows.Add(new(TelemetryDb.KindRuntime, tier, _rt[i].OpenStart, s, b.Serialize()));
            }
            if (final) FlushPendingTraces();
            if (rows.Count == 0) return;
            if (final)
            {
                // Engine is stopping: write synchronously so the process can exit safely.
                try { _db.UpsertBuckets(rows); } catch (Exception ex) { Log?.Invoke("Telemetry final checkpoint failed: " + ex.Message); }
                return;
            }
            EnqueueDb(db => db.UpsertBuckets(rows));
        }

        private void FlushPendingTraces()
        {
            if (_pendingTraces.Count == 0) return;
            var batch = _pendingTraces.ToList();
            _pendingTraces.Clear();
            _pendingById.Clear();
            if (_db == null) return;
            if (!_running)
            {
                try { _db.InsertTraces(batch); } catch { }
                return;
            }
            EnqueueDb(db => db.InsertTraces(batch));
        }

        private void PruneDisk(long nowMs)
        {
            if (_db == null) return;
            long m1Cut = nowMs - Math.Max(1, Options.Retention1mDays) * 86_400_000L;
            long h1Cut = nowMs - Math.Max(8, Options.Retention1hDays) * 86_400_000L;
            long traceCut = nowMs - Math.Max(1, Options.TraceRetentionDays) * 86_400_000L;
            EnqueueDb(db =>
            {
                foreach (string kind in new[] { TelemetryDb.KindRequests, TelemetryDb.KindRum, TelemetryDb.KindRuntime })
                {
                    db.PruneBuckets(kind, TelemetryTier.M1, m1Cut);
                    db.PruneBuckets(kind, TelemetryTier.H1, h1Cut);
                }
                db.PruneTraces(traceCut);
            });
        }

        private void EnqueueDb(Action<TelemetryDb> work)
        {
            if (_db == null) return;
            if (_dbThread == null)
            {
                // No writer thread (tests / not started): run inline.
                try { work(_db); } catch (Exception ex) { Interlocked.Increment(ref _dbErrors); Log?.Invoke("Telemetry DB write failed: " + ex.Message); }
                return;
            }
            if (!_dbWork.TryAdd(work)) Interlocked.Increment(ref _dbErrors);
        }

        private void DbWriterLoop()
        {
            foreach (var work in _dbWork.GetConsumingEnumerable())
            {
                try { work(_db!); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _dbErrors);
                    Log?.Invoke("Telemetry DB write failed: " + ex.Message);
                }
            }
        }

        /// <summary>Loads persisted history into the in-memory tiers (engine thread, before the loop).</summary>
        internal void LoadHistory()
        {
            if (_db == null) return;
            _db.Migrate();
            long now = NowMs();
            foreach (var s in _req) s.Advance(now);
            foreach (var s in _rum) s.Advance(now);
            foreach (var s in _rt) s.Advance(now);

            SeedLegacyStatistics();

            foreach (TelemetryTier tier in new[] { TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 })
            {
                var (routeMs, longMs) = MemoryRetention(tier);
                long from = longMs >= long.MaxValue / 8 ? 0 : now - longMs;
                long routeFrom = now - routeMs;
                int i = (int)tier;
                long openStart = _req[i].OpenStart;

                foreach (var (ts, series, payload) in _db.ReadBuckets(TelemetryDb.KindRequests, tier, from, long.MaxValue))
                {
                    if (!IsLongLived(series) && ts < routeFrom) continue;
                    try
                    {
                        var b = TelemetryBucket.Deserialize(payload);
                        if (!IsLongLived(series)) _knownRouteSeries.Add(series);
                        if (ts == openStart) _req[i].SeedOpen(ts, series, b);
                        else if (ts < openStart) _req[i].LoadClosed(ts, series, b);
                    }
                    catch { /* skip a corrupt row rather than losing all history */ }
                }
                foreach (var (ts, series, payload) in _db.ReadBuckets(TelemetryDb.KindRum, tier, from, long.MaxValue))
                {
                    if (!IsLongLived(series) && ts < routeFrom) continue;
                    try
                    {
                        var b = RumBucket.Deserialize(payload);
                        if (ts == _rum[i].OpenStart) _rum[i].SeedOpen(ts, series, b);
                        else if (ts < _rum[i].OpenStart) _rum[i].LoadClosed(ts, series, b);
                    }
                    catch { }
                }
                foreach (var (ts, series, payload) in _db.ReadBuckets(TelemetryDb.KindRuntime, tier, from, long.MaxValue))
                {
                    try
                    {
                        var b = RuntimeBucket.Deserialize(payload);
                        if (ts == _rt[i].OpenStart) _rt[i].SeedOpen(ts, series, b);
                        else if (ts < _rt[i].OpenStart) _rt[i].LoadClosed(ts, series, b);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// One-time import of the old daily statistics (sum/max only, no histogram) into the
        /// 1d tier so long-range charts start with history. Each legacy day becomes a single
        /// latency bucket at its average, so percentiles for those days equal the average —
        /// flagged as <c>legacy</c> in payloads via the <c>legacyUntil</c> marker.
        /// </summary>
        private void SeedLegacyStatistics()
        {
            if (_db == null || string.IsNullOrEmpty(_legacyStatsPath) || !File.Exists(_legacyStatsPath)) return;
            if (_db.GetMeta("legacySeeded") != null) return;
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(_legacyStatsPath));
                var rows = new List<TelemetryDb.BucketRow>();
                long todayStart = TelemetryTiers.Align(NowMs(), TelemetryTier.D1);
                long legacyUntil = 0;
                foreach (var day in json["Days"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    if (!DateTime.TryParse((string?)day["Date"], System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime date)) continue;
                    long ts = new DateTimeOffset(date.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    if (ts >= todayStart) continue;
                    long requests = (long?)day["Requests"] ?? 0;
                    if (requests <= 0) continue;
                    double totalMs = (double?)day["TotalResponseTimeMs"] ?? 0;
                    double maxMs = (double?)day["MaxResponseTimeMs"] ?? 0;
                    var b = new TelemetryBucket { Count = requests };
                    b.Status[0] = (long?)day["Successes"] ?? 0;
                    b.Status[5] = (long?)day["NotFoundRequests"] ?? 0;
                    b.Status[3] = (long?)day["UnauthorizedRequests"] ?? 0;
                    b.Status[7] = Math.Max(0, ((long?)day["ClientErrors"] ?? 0) - b.Status[5] - b.Status[3]);
                    b.Status[8] = (long?)day["ServerErrors"] ?? 0;
                    long avgMicros = (long)(totalMs / requests * 1000);
                    b.Latency.AddToBucket(LogHistogram.IndexOf(avgMicros), requests, (long)(totalMs * 1000), (long)(maxMs * 1000));
                    rows.Add(new(TelemetryDb.KindRequests, TelemetryTier.D1, ts, GlobalSeries, b.Serialize()));
                    legacyUntil = Math.Max(legacyUntil, ts + 86_400_000);
                }
                // Never overwrite real telemetry rows: only fill days that have none.
                var existing = new HashSet<long>(_db.ReadBuckets(TelemetryDb.KindRequests, TelemetryTier.D1, 0, long.MaxValue, GlobalSeries).Select(r => r.Ts));
                rows = rows.Where(r => !existing.Contains(r.Ts)).ToList();
                _db.UpsertBuckets(rows);
                _db.SetMeta("legacySeeded", DateTime.UtcNow.ToString("O"));
                if (legacyUntil > 0) _db.SetMeta("legacyUntil", legacyUntil.ToString());
                Log?.Invoke($"Telemetry: imported {rows.Count} legacy daily statistics rows.");
            }
            catch (Exception ex)
            {
                Log?.Invoke("Telemetry legacy import failed: " + ex.Message);
            }
        }

        // ════════════════════════ view access ════════════════════════

        public CacheEntry? TryGetView(string key)
        {
            if (!_views.TryGetValue(key, out CacheEntry? entry)) return null;
            if (key.StartsWith("route|", StringComparison.Ordinal)) _routeViewLastAccess[key] = NowMs();
            return entry;
        }

        internal IEnumerable<string> ViewKeys => _views.Keys;

        private void PublishView(string key, byte[] json)
        {
            var entry = new CacheEntry(json, 200, "application/json", null, false,
                new Dictionary<string, long>(), HttpResponseHelpers.ContentEncoding.None, null);
            // Pre-compress both variants off the request path so a read is a pure memcpy.
            if (json.Length >= 1024)
            {
                entry.GetVariant(HttpResponseHelpers.ContentEncoding.Brotli);
                entry.GetVariant(HttpResponseHelpers.ContentEncoding.Gzip);
            }
            _views[key] = entry;
        }

        private void EvictColdRouteViews()
        {
            long now = NowMs();
            foreach (var (key, last) in _routeViewLastAccess)
            {
                if (now - last < 30 * 60_000) continue;
                _routeViewLastAccess.TryRemove(key, out _);
                _views.TryRemove(key, out _);
            }
        }

        // ════════════════════════ queries ════════════════════════

        private sealed class QueryWork
        {
            public required TelemetryQuery Query;
            public required TaskCompletionSource<CacheEntry> Completion;
        }

        private readonly Dictionary<string, (long Epoch, CacheEntry Entry)> _queryCache = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _queryCacheOrder = new();
        private const int QueryCacheCapacity = 96;

        /// <summary>Runs an ad-hoc query on the engine thread (in-memory tiers only; bounded).</summary>
        public Task<CacheEntry> QueryAsync(TelemetryQuery query)
        {
            var tcs = new TaskCompletionSource<CacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_running)
            {
                try { tcs.SetResult(ComputeQueryEntry(query)); }
                catch (Exception ex) { tcs.SetException(ex); }
                return tcs.Task;
            }
            _queries.Enqueue(new QueryWork { Query = query, Completion = tcs });
            _wake.Set();
            return tcs.Task;
        }

        private void ProcessQueries()
        {
            while (_queries.TryDequeue(out QueryWork? work))
            {
                try { work.Completion.TrySetResult(ComputeQueryEntry(work.Query)); }
                catch (Exception ex) { work.Completion.TrySetException(ex); }
            }
        }

        private CacheEntry ComputeQueryEntry(TelemetryQuery q)
        {
            string key = q.CacheKey;
            long epoch = q.TouchesNow(NowMs()) ? Interlocked.Read(ref _viewEpoch) : -1;
            if (_queryCache.TryGetValue(key, out var cached) && cached.Epoch == epoch) return cached.Entry;

            var sw = Stopwatch.StartNew();
            byte[] json = BuildQuery(q);
            var entry = new CacheEntry(json, 200, "application/json", null, false,
                new Dictionary<string, long>(), HttpResponseHelpers.ContentEncoding.None, null);
            RecordBuildTime("query:" + q.Kind, sw.Elapsed.TotalMilliseconds);

            if (_queryCache.Count >= QueryCacheCapacity && _queryCacheOrder.First != null)
            {
                _queryCache.Remove(_queryCacheOrder.First.Value);
                _queryCacheOrder.RemoveFirst();
            }
            if (!_queryCache.ContainsKey(key)) _queryCacheOrder.AddLast(key);
            _queryCache[key] = (epoch, entry);

            // A preset route view becomes a warm view that is kept rebuilt on its cadence.
            if (q.Kind == TelemetryQueryKind.Route && q.IsPreset && !q.HasFilters)
            {
                _views[q.ViewKey] = entry;
                _routeViewLastAccess[q.ViewKey] = NowMs();
            }
            return entry;
        }

        private void RecordBuildTime(string name, double ms)
        {
            lock (_viewBuildMs) _viewBuildMs[name] = Math.Round(ms, 2);
        }

        public double[]? LastRuntimeSample => Volatile.Read(ref _lastRuntimeSample);

        // ── test hooks (engine-thread state; only touch when the engine thread isn't running) ──
        internal TierStore<TelemetryBucket> RequestStore(TelemetryTier tier) => _req[(int)tier];
        internal TierStore<RuntimeBucket> RuntimeStore(TelemetryTier tier) => _rt[(int)tier];
        internal TierStore<RumBucket> RumStore(TelemetryTier tier) => _rum[(int)tier];
        internal void RebuildAllViewsForTest() => RebuildViews(ViewGroup.All);
        internal void DrainForTest() => DrainInputs(int.MaxValue);
        internal void FlushForTest() { CheckpointOpen(final: true); }
    }
}
