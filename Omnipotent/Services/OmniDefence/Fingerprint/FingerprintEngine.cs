using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>A class transition, reported once per change.</summary>
    public sealed record ClassChange(string Ip, IpClass Previous, IpClass Current, Classification Result, bool FromBackfill);

    /// <summary>
    /// Folds per-request signals into per-IP fingerprints and classifies them, entirely off
    /// the request path. Same shape as <c>ApiTelemetry</c>: request threads do a non-blocking
    /// bounded-channel write; one owner thread drains, folds, classifies and persists.
    ///
    /// Thread model: fingerprints are mutated only on the engine thread, always under
    /// <c>lock(fp)</c>, so route handlers can snapshot one by taking the same lock.
    /// </summary>
    public sealed class FingerprintEngine : IDisposable
    {
        public const int SignalCapacity = 50_000;
        public static readonly TimeSpan ReclassifyThrottle = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);
        public const int MaxClassifyPerPump = 2_000;
        public const int MaxSignalsPerPump = 5_000;

        private readonly Channel<RequestSignals> _signals = Channel.CreateBounded<RequestSignals>(
            new BoundedChannelOptions(SignalCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private readonly Channel<Action> _commands = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<string, IpFingerprint> _fps = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly OmniDefenceStore? _store;
        private Thread? _thread;
        private volatile bool _stopping;
        private long _lastPersistMs;
        private Task _persistInFlight = Task.CompletedTask;
        private long _signalsFolded;
        private long _classifications;

        public FingerprintEngine(OmniDefenceStore? store) { _store = store; }

        // ── wiring (set by OmniDefence before Start) ──
        public Func<long> NowMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public Func<string, bool> IsHoneypotRoute { get; set; } = _ => false;
        public string? RobotsTrapRoute { get; set; }
        public Func<string, IpRecord?> GetRecord { get; set; } = _ => null;
        public Action<string>? MarkRecordDirty { get; set; }
        /// <summary>Returns the merged intel for an IP (record fields + intel-service cache).</summary>
        public Func<string, IpRecord?, IpFingerprint, IpIntel?> GetIntel { get; set; } = (_, _, _) => null;
        /// <summary>Called on the engine thread after each classification so enrichment can be scheduled.</summary>
        public Action<IpFingerprint, IpRecord?>? AfterClassify { get; set; }
        public Action<ClassChange>? ClassChanged { get; set; }
        public Action<Exception, string>? LogError { get; set; }

        public bool BackfillRunning { get; private set; }
        public long BackfillRowsFolded { get; private set; }
        public long SignalsFolded => Interlocked.Read(ref _signalsFolded);
        public long Classifications => Interlocked.Read(ref _classifications);
        public int Backlog => _signals.Reader.CanCount ? _signals.Reader.Count : 0;
        public int Count => _fps.Count;

        // ── producers (any thread, never block) ──

        public void Offer(RequestSignals? signals)
        {
            if (signals == null || string.IsNullOrEmpty(signals.Ip)) return;
            _signals.Writer.TryWrite(signals);
        }

        /// <summary>Runs <paramref name="mutate"/> against the IP's fingerprint on the engine thread.</summary>
        public void Post(string ip, Action<IpFingerprint> mutate, bool urgent = true)
        {
            if (string.IsNullOrEmpty(ip)) return;
            _commands.Writer.TryWrite(() =>
            {
                var fp = GetOrCreate(ip);
                lock (fp)
                {
                    mutate(fp);
                    fp.Dirty = true;
                    fp.PendingClassify = true;
                    if (urgent) fp.Urgent = true;
                }
                _pending.Add(ip);
            });
        }

        /// <summary>Queues a reclassification without touching the aggregate (e.g. intel arrived).</summary>
        public void Reclassify(string ip, bool urgent = true)
        {
            if (string.IsNullOrEmpty(ip)) return;
            _commands.Writer.TryWrite(() =>
            {
                if (!_fps.TryGetValue(ip, out var fp)) return;
                lock (fp) { fp.PendingClassify = true; if (urgent) fp.Urgent = true; }
                _pending.Add(ip);
            });
        }

        /// <summary>Reclassifies everything (weights or feeds changed). Spread over pumps.</summary>
        public void ReclassifyAll()
        {
            _commands.Writer.TryWrite(() =>
            {
                foreach (var fp in _fps.Values)
                {
                    lock (fp) fp.PendingClassify = true;
                    _pending.Add(fp.Ip);
                }
            });
        }

        // ── readers ──

        public IpFingerprint? Get(string ip) => _fps.TryGetValue(ip, out var fp) ? fp : null;

        public IEnumerable<IpFingerprint> All() => _fps.Values;

        /// <summary>A consistent JSON snapshot of one fingerprint (for routes).</summary>
        public string? SnapshotJson(string ip)
        {
            if (!_fps.TryGetValue(ip, out var fp)) return null;
            lock (fp) return JsonConvert.SerializeObject(fp);
        }

        public T? Read<T>(string ip, Func<IpFingerprint, T> read)
        {
            if (!_fps.TryGetValue(ip, out var fp)) return default;
            lock (fp) return read(fp);
        }

        // ── lifecycle ──

        public async Task LoadAsync()
        {
            if (_store == null) return;
            var rows = await _store.LoadFingerprintsAsync();
            foreach (var row in rows)
            {
                try
                {
                    var fp = JsonConvert.DeserializeObject<IpFingerprint>(row.Json);
                    if (fp == null) continue;
                    fp.Ip = row.Ip;
                    if (fp.HourHistogram == null || fp.HourHistogram.Length != 24) fp.HourHistogram = new int[24];
                    _fps[row.Ip] = fp;
                }
                catch (Exception ex) { LogError?.Invoke(ex, $"Fingerprint for {row.Ip} failed to load; it will rebuild."); }
            }
        }

        public void Start()
        {
            if (_thread != null) return;
            _lastPersistMs = NowMs();
            _thread = new Thread(Loop) { IsBackground = true, Name = "OmniDefence_Fingerprint", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            try { _thread?.Join(5_000); } catch { }
            try { PersistDirty(force: true); _persistInFlight.Wait(10_000); } catch { }
        }

        private void Loop()
        {
            while (!_stopping)
            {
                int work = 0;
                try { work = Pump(); }
                catch (Exception ex) { LogError?.Invoke(ex, "Fingerprint engine pump failed."); }
                if (work == 0) Thread.Sleep(100);
            }
        }

        /// <summary>One engine iteration. Internal so tests can drive the engine without its thread.</summary>
        internal int Pump()
        {
            int work = 0;
            while (_commands.Reader.TryRead(out var cmd))
            {
                try { cmd(); } catch (Exception ex) { LogError?.Invoke(ex, "Fingerprint command failed."); }
                work++;
            }

            int folded = 0;
            while (folded < MaxSignalsPerPump && _signals.Reader.TryRead(out var s))
            {
                FoldOne(s);
                folded++;
            }
            work += folded;
            if (folded > 0) Interlocked.Add(ref _signalsFolded, folded);

            work += ClassifyPending(fromBackfill: false);

            long now = NowMs();
            if (now - _lastPersistMs >= PersistInterval.TotalMilliseconds)
            {
                _lastPersistMs = now;
                PersistDirty(force: false);
            }
            return work;
        }

        private IpFingerprint GetOrCreate(string ip) => _fps.GetOrAdd(ip, key => new IpFingerprint { Ip = key });

        private void FoldOne(RequestSignals s)
        {
            var fp = GetOrCreate(s.Ip);
            lock (fp) FingerprintFolder.Fold(fp, s, IsHoneypotRoute, RobotsTrapRoute);
            _pending.Add(s.Ip);
        }

        private int ClassifyPending(bool fromBackfill)
        {
            if (_pending.Count == 0) return 0;
            long now = NowMs();
            long throttle = (long)ReclassifyThrottle.TotalMilliseconds;
            var ready = new List<IpFingerprint>();
            var drop = new List<string>();
            foreach (var ip in _pending)
            {
                if (!_fps.TryGetValue(ip, out var fp)) { drop.Add(ip); continue; }
                bool due;
                lock (fp) due = fromBackfill || fp.Urgent || now - fp.ClassifiedMs >= throttle;
                if (due) ready.Add(fp);
                if (ready.Count >= MaxClassifyPerPump) break;
            }
            foreach (var ip in drop) _pending.Remove(ip);
            foreach (var fp in ready)
            {
                _pending.Remove(fp.Ip);
                ClassifyNow(fp, fromBackfill);
            }
            return ready.Count;
        }

        internal void ClassifyNow(IpFingerprint fp, bool fromBackfill)
        {
            IpRecord? rec = GetRecord(fp.Ip);
            Classification result;
            IpClass previous;
            IpIntel? intel;
            lock (fp) intel = GetIntel(fp.Ip, rec, fp);

            int? associatedRank = null;
            if (rec != null) lock (rec) associatedRank = rec.AssociatedProfileRank;

            lock (fp)
            {
                result = IpClassifier.Classify(fp, intel, associatedRank);
                IpClassInfo.TryParse(fp.Class, out previous);
                long now = NowMs();
                fp.Class = result.Class.ToString();
                fp.Confidence = result.Confidence;
                fp.Evidence = result.Evidence.Take(12).ToList();
                fp.Tags = result.Tags;
                fp.Scores = result.Scores;
                fp.ClassifiedMs = now;
                if (previous != result.Class) fp.ClassChangedMs = now;
                fp.PendingClassify = false;
                fp.Urgent = false;
                fp.Dirty = true;
            }
            Interlocked.Increment(ref _classifications);

            if (rec != null)
            {
                bool changed = false;
                string cls = result.Class.ToString();
                string tags = result.TagString();
                lock (rec)
                {
                    if (rec.Classification != cls || rec.ClassConfidence != result.Confidence || rec.ClassTags != tags)
                    {
                        rec.Classification = cls;
                        rec.ClassConfidence = result.Confidence;
                        rec.ClassTags = tags;
                        rec.ClassUpdatedUtc = NowMs() / 1000;
                        changed = true;
                    }
                }
                if (changed) MarkRecordDirty?.Invoke(fp.Ip);
            }

            try { AfterClassify?.Invoke(fp, rec); } catch (Exception ex) { LogError?.Invoke(ex, "Fingerprint AfterClassify hook failed."); }

            if (previous != result.Class)
            {
                try { ClassChanged?.Invoke(new ClassChange(fp.Ip, previous, result.Class, result, fromBackfill)); }
                catch (Exception ex) { LogError?.Invoke(ex, "Fingerprint ClassChanged hook failed."); }
            }
        }

        private void PersistDirty(bool force)
        {
            if (_store == null) return;
            if (!force && !_persistInFlight.IsCompleted) return; // one writer at a time; the rest stays dirty
            var batch = new List<OmniDefenceStore.FingerprintRow>();
            var batchFps = new List<IpFingerprint>();
            foreach (var fp in _fps.Values)
            {
                lock (fp)
                {
                    if (!fp.Dirty) continue;
                    fp.Dirty = false;
                    batch.Add(new OmniDefenceStore.FingerprintRow
                    {
                        Ip = fp.Ip,
                        UpdatedUtc = NowMs() / 1000,
                        Class = fp.Class,
                        Confidence = fp.Confidence,
                        Json = JsonConvert.SerializeObject(fp)
                    });
                }
                batchFps.Add(fp);
            }
            if (batch.Count == 0) return;

            var write = _store.UpsertFingerprintsAsync(batch);
            _persistInFlight = write.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                // Re-flag so the next flush retries instead of losing the aggregate.
                foreach (var fp in batchFps) lock (fp) fp.Dirty = true;
                LogError?.Invoke(t.Exception!.GetBaseException(), $"Persisting {batch.Count} fingerprints failed.");
            }, TaskScheduler.Default);
        }

        /// <summary>Forces pending classifications and a flush (tests, shutdown).</summary>
        internal async Task FlushForTestAsync()
        {
            while (Pump() > 0) { }
            PersistDirty(force: true);
            await _persistInFlight;
        }

        /// <summary>
        /// Rebuilds fingerprints from the stored audit log so existing IPs are classified at
        /// once instead of waiting for fresh traffic. Runs before the live loop starts
        /// draining (live signals buffer in the channel meanwhile). Reads in id-ordered chunks,
        /// each its own short read, so it never pins a read connection for long.
        /// </summary>
        public async Task BackfillAsync(int maxRows, long minUtcTs, Action<string>? log = null)
        {
            if (_store == null) return;
            BackfillRunning = true;
            try
            {
                long maxId = await _store.MaxRequestIdAsync();
                long after = Math.Max(0, maxId - maxRows);
                const int chunk = 10_000;
                while (!_stopping)
                {
                    var rows = await _store.ReadRequestChunkAsync(after, chunk);
                    if (rows.Count == 0) break;
                    foreach (var r in rows)
                    {
                        after = r.Id;
                        if (r.UtcTs < minUtcTs) continue;
                        var s = RequestSignals.FromAuditRow(r.UtcTs, r.Ip, r.Method, r.Route, r.Query, r.Status, r.UserAgent,
                            r.Origin, r.ProfileId, r.ProfileRank, r.Matched, r.DenyReason, r.HeadersJson);
                        var fp = GetOrCreate(s.Ip);
                        lock (fp) FingerprintFolder.Fold(fp, s, IsHoneypotRoute, RobotsTrapRoute);
                        _pending.Add(s.Ip);
                        BackfillRowsFolded++;
                    }
                    if (rows.Count < chunk) break;
                }
                while (ClassifyPending(fromBackfill: true) > 0) { }
                log?.Invoke($"Fingerprint backfill folded {BackfillRowsFolded} audit rows into {_fps.Count} IPs.");
            }
            finally
            {
                BackfillRunning = false;
            }
        }
    }
}
