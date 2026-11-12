using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    public sealed class LlmTelemetryOptions
    {
        public int CallRetentionDays { get; set; } = 90;
        public int MinuteRetentionDays { get; set; } = 14;
        public int HourRetentionDays { get; set; } = 400;
        /// <summary>0 keeps daily rows forever.</summary>
        public int DayRetentionDays { get; set; } = 0;
        public int RouterMinuteRetentionDays { get; set; } = 14;
        public int RouterHourRetentionDays { get; set; } = 400;
        public int SubjectRetentionDays { get; set; } = 400;
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
        public int Capacity { get; set; } = 50_000;
        public int RecentCalls { get; set; } = 500;
    }

    /// <summary>
    /// The KliveLLM ledger: every model call, kept as a numbers-only row plus minute, hour and day
    /// aggregates, per-subject day aggregates and router health.
    ///
    /// The request path pays one bounded-channel write per call and never waits: a single writer task
    /// turns records into rows, updates the open aggregates in memory and writes everything it changed
    /// in one transaction every couple of seconds. A full channel drops the record and counts the drop
    /// rather than block a model call.
    /// </summary>
    public sealed class LlmTelemetry : IAsyncDisposable
    {
        private readonly LlmTelemetryOptions options;
        private readonly Func<LlmPriceBook?> prices;
        private readonly Action<string> log;
        private readonly Channel<LlmCallRecord> channel;
        private readonly ConcurrentDictionary<long, LlmCallTrace> inFlight = new();
        private readonly ConcurrentQueue<(long Ts, string Kind, string Title, string? Detail)> pendingMarks = new();
        private readonly ConcurrentDictionary<string, UsageCounter> usage = new(StringComparer.OrdinalIgnoreCase);
        private readonly object recentLock = new();
        private readonly LinkedList<LlmCallRecord> recent = new();
        private readonly CancellationTokenSource stop = new();

        // Owned by the writer task.
        private readonly Dictionary<LlmAggKey, LlmAggregate> buckets = new();
        private readonly HashSet<LlmAggKey> dirty = new();
        private readonly Dictionary<LlmSubjectKey, LlmAggregate> subjects = new();
        private readonly HashSet<LlmSubjectKey> dirtySubjects = new();
        private readonly List<LlmCallRecord> retry = new();

        private Task? writer;
        private long nextId;
        private long enqueued;
        private long processed;
        private long dropped;
        private long written;
        private long writeFailures;
        private DateTime lastErrorLogUtc = DateTime.MinValue;
        private DateTime lastMaintenanceUtc = DateTime.MinValue;
        private DateTime lastOptimizeUtc = DateTime.MinValue;

        public LlmTelemetry(LlmTelemetryDb db, LlmTelemetryOptions? options = null, Func<LlmPriceBook?>? prices = null,
            LlmContentStore? content = null, Action<string>? log = null)
        {
            Db = db;
            this.options = options ?? new LlmTelemetryOptions();
            this.prices = prices ?? (() => null);
            Content = content;
            this.log = log ?? (_ => { });
            channel = Channel.CreateBounded<LlmCallRecord>(new BoundedChannelOptions(Math.Max(100, this.options.Capacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
        }

        public LlmTelemetryDb Db { get; }
        /// <summary>A router's billing as configured now; set by KliveLLM.</summary>
        public Func<string, LlmBilling?>? BillingOf { get; set; }
        public LlmRouterHealth Health { get; } = new();
        public LlmContentStore? Content { get; }
        public LlmTelemetryOptions Options => options;
        public DateTime StartedUtc { get; private set; }
        public long FirstLiveMs { get; private set; }
        public bool Running => writer is { IsCompleted: false };

        public void Start()
        {
            Db.Migrate();
            Interlocked.Exchange(ref nextId, Math.Max(Db.MaxCallId(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10));
            StartedUtc = DateTime.UtcNow;
            string? first = Db.GetMeta("first_live_ms");
            if (first == null || !long.TryParse(first, out long firstMs))
            {
                firstMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Db.SetMeta("first_live_ms", firstMs.ToString());
            }
            FirstLiveMs = firstMs;
            try { LoadUsageTotals(); } catch (Exception ex) { log($"KliveLLM ledger: budget totals could not be loaded ({ex.Message}); they start from zero."); }
            AddMark("restart", "Omnipotent started", null);
            writer = Task.Run(WriteLoopAsync);
        }

        // ── the request path ──

        public long NewId() => Interlocked.Increment(ref nextId);

        public LlmCallTrace Begin(LlmCaller caller, string kind)
        {
            var trace = new LlmCallTrace(NewId(), caller, kind);
            inFlight[trace.Id] = trace;
            return trace;
        }

        public void End(LlmCallTrace trace, LlmCallRecord record)
        {
            inFlight.TryRemove(trace.Id, out _);
            Enqueue(record);
        }

        public void Abandon(LlmCallTrace trace) => inFlight.TryRemove(trace.Id, out _);

        public void Enqueue(LlmCallRecord record)
        {
            if (record.Id == 0) record.Id = NewId();
            if (!record.Has(LlmCallFlags.Imported)) AddRecent(record);
            if (channel.Writer.TryWrite(record)) Interlocked.Increment(ref enqueued);
            else Interlocked.Increment(ref dropped);
        }

        public void AddMark(string kind, string title, string? detail)
            => pendingMarks.Enqueue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, title, detail));

        public IReadOnlyList<LlmCallTrace> InFlight() => inFlight.Values.OrderBy(t => t.StartedUtc).ToList();

        public IReadOnlyList<LlmCallRecord> Recent(int limit = 100)
        {
            lock (recentLock) return recent.Take(Math.Clamp(limit, 1, options.RecentCalls)).ToList();
        }

        private void AddRecent(LlmCallRecord record)
        {
            lock (recentLock)
            {
                recent.AddFirst(record);
                while (recent.Count > options.RecentCalls) recent.RemoveLast();
            }
        }

        /// <summary>What a service ("Projects") or lane ("Projects/commander") has spent today and this month.</summary>
        public LlmUsageSnapshot Usage(string key)
            => usage.TryGetValue(key, out var counter) ? counter.Snapshot(DateTime.UtcNow) : LlmUsageSnapshot.Zero;

        public object Stats() => new
        {
            running = Running,
            queued = channel.Reader.Count,
            inFlight = inFlight.Count,
            enqueued = Interlocked.Read(ref enqueued),
            written = Interlocked.Read(ref written),
            dropped = Interlocked.Read(ref dropped),
            writeFailures = Interlocked.Read(ref writeFailures),
            dbBytes = Db.FileBytes,
            contentBytes = Content?.FileBytes ?? 0,
            startedUtc = StartedUtc,
            firstLiveMs = FirstLiveMs,
        };

        /// <summary>Waits until everything enqueued before the call has been written (tests, shutdown).</summary>
        public async Task FlushAsync(TimeSpan? timeout = null)
        {
            long target = Interlocked.Read(ref enqueued);
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (Interlocked.Read(ref processed) < target && DateTime.UtcNow < deadline && Running)
                await Task.Delay(20).ConfigureAwait(false);
            // One more pass so aggregates of the last batch are on disk too.
            await Task.Delay(options.FlushInterval < TimeSpan.FromMilliseconds(50) ? options.FlushInterval : TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        // ── the writer ──

        private async Task WriteLoopAsync()
        {
            var reader = channel.Reader;
            var batch = new List<LlmCallRecord>(1024);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    {
                        wait.CancelAfter(options.FlushInterval);
                        try { await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                    }
                    batch.Clear();
                    while (batch.Count < 2000 && reader.TryRead(out var record)) batch.Add(record);
                    Process(batch);
                    Interlocked.Add(ref processed, batch.Count);
                    Maintain();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Interlocked.Add(ref processed, batch.Count);
                    LogRateLimited($"KliveLLM ledger writer error: {ex.Message}");
                    try { await Task.Delay(1000, stop.Token).ConfigureAwait(false); } catch { break; }
                }
            }
            // Drain what is left so a clean shutdown loses nothing.
            try
            {
                batch.Clear();
                while (reader.TryRead(out var record)) batch.Add(record);
                Process(batch);
            }
            catch { }
        }

        private void Process(List<LlmCallRecord> records)
        {
            var book = prices();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var write = new LlmTelemetryDb.Batch();
            if (retry.Count > 0) { write.Calls.AddRange(retry); retry.Clear(); }

            foreach (var record in records)
            {
                record.EstimatedCostUsd ??= LlmCosting.CallCost(record, book);
                Apply(record, now);
                if (!record.Has(LlmCallFlags.Imported)) Health.NoteAttempts(record);
                write.Calls.Add(record);
                if (record.Has(LlmCallFlags.ContentKept) && Content != null)
                {
                    try { Content.Write(record); }
                    catch (Exception ex) { LogRateLimited($"KliveLLM kept text could not be written: {ex.Message}"); }
                }
            }

            foreach (var row in Health.Roll(now, (ts, router) => Db.ReadRouterRow("router_1h", ts, router)))
                write.Routers.Add(row);
            while (pendingMarks.TryDequeue(out var mark)) write.Marks.Add(mark);
            foreach (var key in dirty) write.Aggregates.Add((key, buckets[key]));
            foreach (var key in dirtySubjects) write.Subjects.Add((key, subjects[key]));
            if (write.IsEmpty) return;

            try
            {
                Db.Write(write);
                Interlocked.Add(ref written, write.Calls.Count);
                dirty.Clear();
                dirtySubjects.Clear();
                Evict(now);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref writeFailures);
                // Keep the calls for the next pass (bounded), and the aggregates stay dirty.
                if (retry.Count < 20_000) retry.AddRange(write.Calls.Take(20_000 - retry.Count));
                foreach (var mark in write.Marks) pendingMarks.Enqueue(mark);
                LogRateLimited($"KliveLLM ledger write failed ({ex.Message}); {write.Calls.Count} calls kept for the next attempt.");
            }
        }

        private void Apply(LlmCallRecord record, long now)
        {
            long ended = record.EndedMs > 0 ? record.EndedMs : now;
            string provider = record.Provider ?? "";
            string model = record.Model;
            foreach (var tier in LlmTiers.All)
            {
                if (tier == LlmTier.M1 && ended < now - options.MinuteRetentionDays * 86_400_000L) continue;
                if (tier == LlmTier.H1 && ended < now - options.HourRetentionDays * 86_400_000L) continue;
                var key = new LlmAggKey(tier, LlmTiers.Align(ended, tier), record.Service, record.Lane, record.Router ?? "", provider, model, record.Kind);
                Bucket(key).Add(record);
                dirty.Add(key);
            }
            if (!string.IsNullOrWhiteSpace(record.Subject))
            {
                var key = new LlmSubjectKey(LlmTiers.Align(ended, LlmTier.D1), record.Service, record.Lane, record.Subject!);
                if (!subjects.TryGetValue(key, out var agg))
                {
                    agg = Db.ReadSubject(key) ?? new LlmAggregate();
                    subjects[key] = agg;
                }
                agg.Add(record);
                dirtySubjects.Add(key);
            }

            double cost = record.EstimatedCostUsd ?? record.CostUsd ?? 0;
            long tokens = record.PromptTokens + record.CompletionTokens;
            var at = DateTimeOffset.FromUnixTimeMilliseconds(ended).UtcDateTime;
            usage.GetOrAdd(record.Service, _ => new UsageCounter()).Add(at, cost, tokens);
            usage.GetOrAdd(record.Service + "/" + record.Lane, _ => new UsageCounter()).Add(at, cost, tokens);
        }

        private LlmAggregate Bucket(LlmAggKey key)
        {
            if (buckets.TryGetValue(key, out var agg)) return agg;
            agg = Db.ReadAggregate(key) ?? new LlmAggregate();
            buckets[key] = agg;
            return agg;
        }

        private void Evict(long now)
        {
            if (buckets.Count < 2000 && subjects.Count < 2000) return;
            long minuteKeep = now - 3 * 3_600_000L, hourKeep = now - 50 * 3_600_000L, dayKeep = now - 3 * 86_400_000L;
            foreach (var key in buckets.Keys.ToList())
            {
                long keep = key.Tier switch { LlmTier.M1 => minuteKeep, LlmTier.H1 => hourKeep, _ => dayKeep };
                if (key.Ts < keep && !dirty.Contains(key)) buckets.Remove(key);
            }
            foreach (var key in subjects.Keys.ToList())
                if (key.Ts < dayKeep && !dirtySubjects.Contains(key)) subjects.Remove(key);
        }

        private void LoadUsageTotals()
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            long from = new DateTimeOffset(monthStart).ToUnixTimeMilliseconds();
            long today = LlmTiers.Align(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LlmTier.D1);
            var book = prices();
            foreach (var row in Db.ReadAggregates(LlmTier.D1, from, today + 86_400_000))
            {
                // Billing is not a dimension; the router's current billing decides what its rows cost.
                var billing = BillingOf?.Invoke(row.Router)
                    ?? (row.Router is LlmRouterIds.AIRouter or LlmRouterIds.OpenRouterFree or LlmRouterIds.Local ? LlmBilling.Flat : LlmBilling.PerToken);
                double cost = LlmCosting.Price(row.Agg, row.Router, row.Model, billing, book).Estimated;
                long tokens = row.Agg.TotalTokens;
                bool isToday = row.Ts == today;
                foreach (string key in new[] { row.Service, row.Service + "/" + row.Lane })
                    usage.GetOrAdd(key, _ => new UsageCounter()).Seed(now, isToday, cost, tokens);
            }
        }

        // ── retention ──

        private void Maintain()
        {
            var now = DateTime.UtcNow;
            if (now - lastMaintenanceUtc < TimeSpan.FromHours(1)) return;
            lastMaintenanceUtc = now;
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long Days(int d) => ms - d * 86_400_000L;
            try
            {
                int removed = 0;
                if (options.CallRetentionDays > 0) removed += Db.Prune("calls", "ended_ms", Days(options.CallRetentionDays));
                if (options.MinuteRetentionDays > 0) removed += Db.Prune("agg_1m", "ts", Days(options.MinuteRetentionDays));
                if (options.HourRetentionDays > 0) removed += Db.Prune("agg_1h", "ts", Days(options.HourRetentionDays));
                if (options.DayRetentionDays > 0) removed += Db.Prune("agg_1d", "ts", Days(options.DayRetentionDays));
                if (options.RouterMinuteRetentionDays > 0) removed += Db.Prune("router_1m", "ts", Days(options.RouterMinuteRetentionDays));
                if (options.RouterHourRetentionDays > 0) removed += Db.Prune("router_1h", "ts", Days(options.RouterHourRetentionDays));
                if (options.SubjectRetentionDays > 0) removed += Db.Prune("subject_1d", "ts", Days(options.SubjectRetentionDays));
                removed += Db.Prune("marks", "ts", Days(400));
                Content?.Prune();
                if (now - lastOptimizeUtc > TimeSpan.FromHours(24) || removed > 50_000)
                {
                    lastOptimizeUtc = now;
                    Db.Optimize();
                }
            }
            catch (Exception ex) { LogRateLimited($"KliveLLM ledger maintenance failed: {ex.Message}"); }
        }

        private void LogRateLimited(string message)
        {
            if (DateTime.UtcNow - lastErrorLogUtc < TimeSpan.FromMinutes(5)) return;
            lastErrorLogUtc = DateTime.UtcNow;
            try { log(message); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            stop.Cancel();
            if (writer != null)
            {
                try { await writer.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>Today's and this month's spend and tokens for one key, rolling over at UTC midnight.</summary>
        private sealed class UsageCounter
        {
            private readonly object sync = new();
            private DateTime day;
            private DateTime month;
            private double dayCost, monthCost;
            private long dayTokens, monthTokens;

            private void Roll(DateTime nowUtc)
            {
                var d = nowUtc.Date;
                var m = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                if (d != day) { day = d; dayCost = 0; dayTokens = 0; }
                if (m != month) { month = m; monthCost = 0; monthTokens = 0; }
            }

            public void Add(DateTime atUtc, double cost, long tokens)
            {
                lock (sync)
                {
                    var now = DateTime.UtcNow;
                    Roll(now);
                    if (atUtc.Date == day) { dayCost += cost; dayTokens += tokens; }
                    if (atUtc.Year == month.Year && atUtc.Month == month.Month) { monthCost += cost; monthTokens += tokens; }
                }
            }

            public void Seed(DateTime nowUtc, bool today, double cost, long tokens)
            {
                lock (sync)
                {
                    Roll(nowUtc);
                    monthCost += cost; monthTokens += tokens;
                    if (today) { dayCost += cost; dayTokens += tokens; }
                }
            }

            public LlmUsageSnapshot Snapshot(DateTime nowUtc)
            {
                lock (sync)
                {
                    Roll(nowUtc);
                    return new LlmUsageSnapshot(dayCost, monthCost, dayTokens, monthTokens);
                }
            }
        }
    }
}
