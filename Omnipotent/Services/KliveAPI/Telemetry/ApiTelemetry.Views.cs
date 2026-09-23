using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// View + query payload builders. All of this runs on the engine thread against the
    /// in-memory tiers; the output is compact columnar JSON (arrays per metric, not an
    /// object per point) with numbers rounded to 3 significant figures.
    /// </summary>
    internal sealed partial class ApiTelemetry
    {
        private const int AutoTargetPoints = 240;
        private const int MaxPoints = 300;
        private const int SparkPoints = 24;

        internal readonly record struct Window(long From, long To, long BucketMs, TelemetryTier Tier, int Points, bool Degraded, string? Note);

        // ════════════════════════ planning ════════════════════════

        private bool TierCovers(TelemetryTier tier, long fromMs, bool routeClass, long nowMs)
        {
            var (routeMs, longMs) = MemoryRetention(tier);
            long keep = routeClass ? routeMs : longMs;
            return keep >= long.MaxValue / 8 || fromMs >= nowMs - keep;
        }

        private long EarliestDataMs(long nowMs)
        {
            long earliest = nowMs - 86_400_000;
            var d1 = _req[(int)TelemetryTier.D1];
            if (d1.ClosedCount > 0) earliest = Math.Min(earliest, d1.OldestClosedStart);
            return earliest;
        }

        internal Window PlanWindow(TelemetryQuery q, long nowMs, bool routeClass, bool hasS10, int targetPoints = AutoTargetPoints)
        {
            long from, to;
            if (q.IsPreset)
            {
                to = nowMs;
                from = q.RangeKey == "all" ? EarliestDataMs(nowMs) : nowMs - TelemetryQuery.PresetDurationMs(q.RangeKey!);
            }
            else
            {
                from = q.FromMs;
                to = Math.Min(q.ToMs, nowMs);
                if (to <= from) to = from + 60_000;
            }
            long duration = Math.Max(10_000, to - from);

            bool degraded = false;
            string? note = null;
            long width;
            if (q.BucketMs is long requested)
            {
                width = requested;
                if (duration / width > MaxPoints)
                {
                    width = TelemetryTiers.BucketWidthsMs.FirstOrDefault(w => duration / w <= MaxPoints, TelemetryTiers.BucketWidthsMs[^1]);
                    degraded = true;
                    note = $"bucket widened from {TelemetryTiers.FormatWidth(requested)} to stay within {MaxPoints} points";
                }
            }
            else
            {
                width = TelemetryTiers.BucketWidthsMs.FirstOrDefault(w => (duration + w - 1) / w <= targetPoints, TelemetryTiers.BucketWidthsMs[^1]);
            }

            TelemetryTier tier = TelemetryTiers.TierFor(width);
            if (tier == TelemetryTier.S10 && !hasS10)
            {
                long widened = Math.Max(width, 60_000);
                if (widened != width && q.BucketMs.HasValue) { degraded = true; note = "per-route data starts at 1-minute resolution"; }
                width = TelemetryTiers.BucketWidthsMs.First(w => w >= widened && w % 60_000 == 0);
                tier = TelemetryTiers.TierFor(width);
            }
            while (tier < TelemetryTier.D1 && !TierCovers(tier, from, routeClass, nowMs))
            {
                tier++;
                long tierWidth = TelemetryTiers.WidthMs(tier);
                long widened = TelemetryTiers.BucketWidthsMs.First(w => w >= Math.Max(width, tierWidth) && w % tierWidth == 0);
                if (widened != width)
                {
                    degraded = true;
                    note = $"history before {DateTimeOffset.FromUnixTimeMilliseconds(nowMs - MemoryRetention(tier - 1).RouteMs):u} is kept at {TelemetryTiers.Name(tier)} resolution";
                }
                width = widened;
            }
            // For auto buckets a coarser tier is expected, not a degradation.
            if (!q.BucketMs.HasValue) { degraded = false; note = null; }

            long alignedFrom = TelemetryTiers.AlignTo(from, width);
            long end = TelemetryTiers.AlignTo(Math.Max(alignedFrom, to - 1), width) + width;
            int points = (int)Math.Clamp((end - alignedFrom) / width, 1, 4000);
            return new Window(alignedFrom, end, width, tier, points, degraded, note);
        }

        // ════════════════════════ binning ════════════════════════

        private static T?[] Bin<T>(TierStore<T> store, string series, Window w, Func<T> factory, Action<T, T> merge) where T : class
        {
            var arr = new T?[w.Points];
            long step = store.WidthMs;
            long end = w.From + w.Points * w.BucketMs;
            for (long s = w.From; s < end; s += step)
            {
                T? b = store.Get(s, series);
                if (b == null) continue;
                int i = (int)((s - w.From) / w.BucketMs);
                if (i < 0 || i >= arr.Length) continue;
                arr[i] ??= factory();
                merge(arr[i]!, b);
            }
            return arr;
        }

        private static Dictionary<string, TelemetryBucket?[]> BinAll(TierStore<TelemetryBucket> store, Window w, Func<string, bool> include)
        {
            var result = new Dictionary<string, TelemetryBucket?[]>(StringComparer.Ordinal);
            long step = store.WidthMs;
            long end = w.From + w.Points * w.BucketMs;
            for (long s = w.From; s < end; s += step)
            {
                var set = store.GetSet(s);
                if (set == null) continue;
                int i = (int)((s - w.From) / w.BucketMs);
                if (i < 0 || i >= w.Points) continue;
                foreach (var (series, b) in set)
                {
                    if (!include(series)) continue;
                    if (!result.TryGetValue(series, out var arr))
                    {
                        arr = new TelemetryBucket?[w.Points];
                        result[series] = arr;
                    }
                    arr[i] ??= new TelemetryBucket();
                    arr[i]!.Merge(b);
                }
            }
            return result;
        }

        private TelemetryBucket MergeRequests(IEnumerable<string> series, Window w)
        {
            var acc = new TelemetryBucket();
            foreach (string s in series) acc.Merge(_req[(int)w.Tier].MergeRange(s, w.From, w.To));
            return acc;
        }

        /// <summary>The window immediately before <paramref name="w"/>, planned on whatever tier still covers it.</summary>
        private Window PreviousWindow(Window w, long nowMs, bool routeClass)
        {
            long length = w.To - w.From;
            long from = w.From - length;
            TelemetryTier tier = w.Tier;
            while (tier < TelemetryTier.D1 && !TierCovers(tier, from, routeClass, nowMs)) tier++;
            long tw = TelemetryTiers.WidthMs(tier);
            long alignedFrom = TelemetryTiers.AlignTo(from, tw);
            long alignedTo = TelemetryTiers.AlignTo(w.From, tw);
            if (alignedTo <= alignedFrom) alignedTo = alignedFrom + tw;
            return new Window(alignedFrom, alignedTo, alignedTo - alignedFrom, tier, 1, false, null);
        }

        // ════════════════════════ builds ════════════════════════

        private void RebuildViews(ViewGroup group)
        {
            long now = NowMs();
            var presets = new List<string>();
            if (group.HasFlag(ViewGroup.Short)) presets.AddRange(new[] { "15m", "1h", "6h" });
            if (group.HasFlag(ViewGroup.Medium)) presets.AddRange(new[] { "24h", "7d" });
            if (group.HasFlag(ViewGroup.Long)) presets.AddRange(new[] { "30d", "90d", "1y", "all" });

            foreach (string preset in presets)
            {
                foreach (TelemetryQueryKind kind in new[] { TelemetryQueryKind.Overview, TelemetryQueryKind.Routes, TelemetryQueryKind.Runtime, TelemetryQueryKind.Rum })
                {
                    var q = new TelemetryQuery { Kind = kind, RangeKey = preset };
                    TimedPublish(q.ViewKey, () => BuildQuery(q));
                }
            }

            if (group.HasFlag(ViewGroup.Medium))
            {
                var weekly = new TelemetryQuery { Kind = TelemetryQueryKind.Weekly, RangeKey = "30d" };
                TimedPublish("weekly", () => BuildQuery(weekly));
                PrewarmTopRoutes(now);
            }

            // Warm per-route views follow the cadence of their preset.
            foreach (string key in _routeViewLastAccess.Keys.ToList())
            {
                string[] parts = key.Split('|');
                if (parts.Length != 3 || !presets.Contains(parts[2])) continue;
                var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route, Series = parts[1], RangeKey = parts[2] };
                TimedPublish(key, () => BuildQuery(q));
            }

            TimedPublish("health", BuildHealth);
            _lastViewBuildUtcMs = now;
        }

        private void PrewarmTopRoutes(long now)
        {
            var q = new TelemetryQuery { Kind = TelemetryQueryKind.Routes, RangeKey = "24h" };
            Window w = PlanWindow(q, now, routeClass: true, hasS10: false);
            var totals = _req[(int)w.Tier].MergeAllSeries(w.From, w.To, s => !IsLongLived(s));
            foreach (var (series, _) in totals.OrderByDescending(kv => kv.Value.Latency.Sum).Take(10))
            {
                string key = $"route|{series}|24h";
                _routeViewLastAccess.TryAdd(key, now);
            }
        }

        private void TimedPublish(string key, Func<byte[]> build)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                PublishView(key, build());
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Telemetry view '{key}' failed: {ex.Message}");
            }
            RecordBuildTime(key, sw.Elapsed.TotalMilliseconds);
            // A full rebuild can take a second at scale; never make a waiting query sit behind it.
            if (!_queries.IsEmpty) ProcessQueries();
        }

        internal byte[] BuildQuery(TelemetryQuery q) => q.Kind switch
        {
            TelemetryQueryKind.Overview => BuildSeriesDetail(q, GlobalSeries),
            TelemetryQueryKind.Route => BuildSeriesDetail(q, q.Series ?? GlobalSeries),
            TelemetryQueryKind.Routes => BuildRoutes(q),
            TelemetryQueryKind.Runtime => BuildRuntime(q),
            TelemetryQueryKind.Rum => BuildRum(q),
            _ => BuildWeekly(q),
        };

        // ─── overview / route detail ───

        private byte[] BuildSeriesDetail(TelemetryQuery q, string series)
        {
            long now = NowMs();
            bool isGlobal = series == GlobalSeries;
            bool routeClass = !IsLongLived(series);
            // Only the plain global series is kept at 10 s; pseudo-series start at 1 minute.
            Window w = PlanWindow(q, now, routeClass, hasS10: isGlobal && !q.IncludeBatchItems && !q.IncludeDenied);
            var store = _req[(int)w.Tier];

            var seriesSet = new List<string> { series };
            if (isGlobal && q.IncludeBatchItems) seriesSet.Add(BatchItemsSeries);
            if (isGlobal && q.IncludeDenied) seriesSet.Add(DeniedSeries);

            // Per-point buckets (merged across the included series).
            var points = new TelemetryBucket?[w.Points];
            foreach (string s in seriesSet)
            {
                var arr = Bin(store, s, w, () => new TelemetryBucket(), (a, b) => a.Merge(b));
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) continue;
                    points[i] ??= new TelemetryBucket();
                    points[i]!.Merge(arr[i]);
                }
            }
            var total = new TelemetryBucket();
            foreach (var p in points) total.Merge(p);

            Window pw = PreviousWindow(w, now, routeClass);
            TelemetryBucket? prev = q.RangeKey == "all" ? null : MergeRequests(seriesSet, pw);

            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                WriteHeader(j, isGlobal ? "overview" : "route", q, w, now);
                j.WriteString("series", series);
                if (!isGlobal)
                {
                    int sp = series.IndexOf(' ');
                    j.WriteString("method", sp > 0 ? series[..sp] : "");
                    j.WriteString("route", sp > 0 ? series[(sp + 1)..] : series);
                }
                j.WriteNumber("apdexTargetMs", Options.ApdexTargetMs);

                j.WritePropertyName("kpi");
                WriteKpi(j, total, Math.Max(1, Math.Min(w.To, now) - w.From) / 1000.0);
                j.WritePropertyName("prev");
                if (prev == null || prev.Count == 0) j.WriteNullValue();
                else WriteKpi(j, prev, Math.Max(1, pw.To - pw.From) / 1000.0);

                WriteTimeSeries(j, points, w, now);
                WriteStageSummary(j, total);
                WriteDistributions(j, total);
                WriteHeatmap(j, points, w);

                if (isGlobal)
                {
                    WriteTopCost(j, w, now);
                    j.WritePropertyName("pseudo");
                    j.WriteStartObject();
                    var pseudoStore = _req[(int)RouteTierFor(w, now)];
                    foreach (string ps in new[] { BatchItemsSeries, DeniedSeries, UnmatchedSeries, OtherSeries })
                    {
                        j.WriteNumber(ps, pseudoStore.MergeRange(ps, w.From, w.To).Count);
                    }
                    j.WriteEndObject();
                }
                else
                {
                    WriteExemplars(j, series, w);
                }
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        private void WriteHeader(Utf8JsonWriter j, string kind, TelemetryQuery q, Window w, long now)
        {
            j.WriteString("kind", kind);
            j.WriteNumber("asOfUtc", now);
            if (q.RangeKey != null) j.WriteString("range", q.RangeKey); else j.WriteNull("range");
            j.WriteNumber("from", w.From);
            j.WriteNumber("to", w.To);
            j.WriteNumber("bucketMs", w.BucketMs);
            j.WriteString("bucket", TelemetryTiers.FormatWidth(w.BucketMs));
            j.WriteString("tier", TelemetryTiers.Name(w.Tier));
            j.WriteNumber("points", w.Points);
            j.WriteBoolean("degraded", w.Degraded);
            if (w.Note != null) j.WriteString("note", w.Note);
        }

        private double Apdex(LogHistogram h)
        {
            if (h.Count == 0) return 1;
            double t = Options.ApdexTargetMs * 1000;
            double satisfied = h.FractionAtOrBelow(t);
            double tolerating = h.FractionAtOrBelow(4 * t) - satisfied;
            return satisfied + tolerating / 2;
        }

        private void WriteKpi(Utf8JsonWriter j, TelemetryBucket b, double seconds)
        {
            j.WriteStartObject();
            j.WriteNumber("count", b.Count);
            Num(j, "rps", b.Count / Math.Max(seconds, 0.001));
            Num(j, "p50", b.Latency.Percentile(0.50) / 1000);
            Num(j, "p90", b.Latency.Percentile(0.90) / 1000);
            Num(j, "p95", b.Latency.Percentile(0.95) / 1000);
            Num(j, "p99", b.Latency.Percentile(0.99) / 1000);
            Num(j, "max", b.Latency.Max / 1000.0);
            Num(j, "mean", b.Latency.Mean / 1000);
            Num(j, "errorPct", Pct(b.ServerErrors, b.Count));
            Num(j, "clientErrorPct", Pct(b.ClientErrors, b.Count));
            Num(j, "apdex", Apdex(b.Latency));
            NumOrNull(j, "cacheHitPct", b.CacheHit + b.CacheMiss == 0 ? null : Pct(b.CacheHit, b.CacheHit + b.CacheMiss));
            j.WriteNumber("cacheHit", b.CacheHit);
            j.WriteNumber("cacheMiss", b.CacheMiss);
            j.WriteNumber("cacheBypass", b.CacheBypass);
            j.WriteNumber("notModified", b.Status[1]);
            j.WriteNumber("bytesIn", b.BytesIn);
            j.WriteNumber("bytesOut", b.BytesOut);
            j.WriteNumber("bytesOutRaw", b.BytesOutRaw);
            Num(j, "avgBytesOut", b.Count == 0 ? 0 : (double)b.BytesOut / b.Count);
            Num(j, "compression", b.BytesOut == 0 ? 1 : (double)b.BytesOutRaw / b.BytesOut);
            j.WriteNumber("peakInFlight", b.InFlightMax);
            Num(j, "avgInFlight", b.Count == 0 ? 0 : (double)b.InFlightSum / b.Count);
            j.WriteNumber("disconnects", b.Disconnects);
            j.WriteNumber("exceptions", b.Exceptions);
            j.WriteNumber("viaBatch", b.ViaBatch);
            Num(j, "costMs", b.Latency.Sum / 1000.0);
            j.WriteEndObject();
        }

        private void WriteTimeSeries(Utf8JsonWriter j, TelemetryBucket?[] points, Window w, long now)
        {
            j.WritePropertyName("ts");
            j.WriteStartObject();
            j.WriteNumber("t0", w.From);
            j.WriteNumber("step", w.BucketMs);

            double Seconds(int i)
            {
                long start = w.From + i * w.BucketMs;
                long end = Math.Min(start + w.BucketMs, now);
                return Math.Max(1, end - start) / 1000.0;
            }
            double? L(TelemetryBucket? b, Func<TelemetryBucket, double> f) => b == null || b.Count == 0 ? null : f(b);

            Arr(j, "count", points.Select(b => (double?)(b?.Count ?? 0)));
            Arr(j, "rps", points.Select((b, i) => (double?)((b?.Count ?? 0) / Seconds(i))));
            Arr(j, "p50", points.Select(b => L(b, x => x.Latency.Percentile(0.50) / 1000)));
            Arr(j, "p90", points.Select(b => L(b, x => x.Latency.Percentile(0.90) / 1000)));
            Arr(j, "p95", points.Select(b => L(b, x => x.Latency.Percentile(0.95) / 1000)));
            Arr(j, "p99", points.Select(b => L(b, x => x.Latency.Percentile(0.99) / 1000)));
            Arr(j, "max", points.Select(b => L(b, x => x.Latency.Max / 1000.0)));
            Arr(j, "mean", points.Select(b => L(b, x => x.Latency.Mean / 1000)));
            Arr(j, "s2xx", points.Select(b => (double?)(b?.Status[0] ?? 0)));
            Arr(j, "s3xx", points.Select(b => (double?)((b?.Status[1] ?? 0) + (b?.Status[2] ?? 0))));
            Arr(j, "s4xx", points.Select(b => (double?)(b?.ClientErrors ?? 0)));
            Arr(j, "s5xx", points.Select(b => (double?)(b?.ServerErrors ?? 0)));
            Arr(j, "errPct", points.Select(b => L(b, x => Pct(x.ServerErrors, x.Count))));
            Arr(j, "cliErrPct", points.Select(b => L(b, x => Pct(x.ClientErrors, x.Count))));
            Arr(j, "hit", points.Select(b => (double?)(b?.CacheHit ?? 0)));
            Arr(j, "miss", points.Select(b => (double?)(b?.CacheMiss ?? 0)));
            Arr(j, "bypass", points.Select(b => (double?)(b?.CacheBypass ?? 0)));
            Arr(j, "hitPct", points.Select(b => b == null || b.CacheHit + b.CacheMiss == 0 ? (double?)null : Pct(b.CacheHit, b.CacheHit + b.CacheMiss)));
            Arr(j, "bytesIn", points.Select(b => (double?)(b?.BytesIn ?? 0)));
            Arr(j, "bytesOut", points.Select(b => (double?)(b?.BytesOut ?? 0)));
            Arr(j, "bytesOutRaw", points.Select(b => (double?)(b?.BytesOutRaw ?? 0)));
            Arr(j, "inFlightMax", points.Select(b => L(b, x => x.InFlightMax)));
            Arr(j, "inFlightAvg", points.Select(b => L(b, x => (double)x.InFlightSum / x.Count)));
            Arr(j, "apdex", points.Select(b => L(b, x => Apdex(x.Latency))));
            Arr(j, "disconnects", points.Select(b => (double?)(b?.Disconnects ?? 0)));

            j.WritePropertyName("stageMean");
            j.WriteStartObject();
            for (int s = 0; s < TelemetryStages.Count; s++)
            {
                int si = s;
                Arr(j, TelemetryStages.Keys[s], points.Select(b => L(b, x => x.Stages[si].Mean / 1000)));
            }
            j.WriteEndObject();

            j.WritePropertyName("stageP95");
            j.WriteStartObject();
            for (int s = 0; s < TelemetryStages.Count; s++)
            {
                int si = s;
                Arr(j, TelemetryStages.Keys[s], points.Select(b => L(b, x => x.Stages[si].Percentile(0.95) / 1000)));
            }
            j.WriteEndObject();
            j.WriteEndObject();
        }

        private static void WriteStageSummary(Utf8JsonWriter j, TelemetryBucket total)
        {
            double latencySum = Math.Max(1, total.Latency.Sum);
            j.WritePropertyName("stages");
            j.WriteStartArray();
            for (int s = 0; s < TelemetryStages.Count; s++)
            {
                LogHistogram h = total.Stages[s];
                j.WriteStartObject();
                j.WriteString("key", TelemetryStages.Keys[s]);
                j.WriteString("label", TelemetryStages.Labels[s]);
                j.WriteBoolean("latency", TelemetryStages.CountsTowardLatency(s));
                Num(j, "mean", h.Mean / 1000);
                Num(j, "p50", h.Percentile(0.50) / 1000);
                Num(j, "p95", h.Percentile(0.95) / 1000);
                Num(j, "p99", h.Percentile(0.99) / 1000);
                Num(j, "max", h.Max / 1000.0);
                Num(j, "share", TelemetryStages.CountsTowardLatency(s) ? h.Sum / latencySum : 0);
                j.WriteEndObject();
            }
            j.WriteEndArray();

            j.WritePropertyName("spans");
            j.WriteStartArray();
            if (total.Spans != null)
            {
                foreach (var (name, agg) in total.Spans.OrderByDescending(kv => kv.Value.SumMicros))
                {
                    j.WriteStartObject();
                    j.WriteString("name", name);
                    j.WriteNumber("count", agg.Count);
                    Num(j, "mean", agg.Count == 0 ? 0 : agg.SumMicros / 1000.0 / agg.Count);
                    Num(j, "max", agg.MaxMicros / 1000.0);
                    Num(j, "totalMs", agg.SumMicros / 1000.0);
                    j.WriteEndObject();
                }
            }
            j.WriteEndArray();
        }

        private static void WriteDistributions(Utf8JsonWriter j, TelemetryBucket total)
        {
            j.WritePropertyName("status");
            j.WriteStartObject();
            for (int i = 0; i < TelemetryBucket.StatusSlots; i++) j.WriteNumber(TelemetryBucket.StatusKeys[i], total.Status[i]);
            j.WriteEndObject();

            j.WritePropertyName("cache");
            j.WriteStartObject();
            j.WriteNumber("hit", total.CacheHit);
            j.WriteNumber("miss", total.CacheMiss);
            j.WriteNumber("bypass", total.CacheBypass);
            j.WriteNumber("none", Math.Max(0, total.Count - total.CacheHit - total.CacheMiss - total.CacheBypass));
            j.WriteEndObject();

            j.WritePropertyName("encodings");
            j.WriteStartObject();
            j.WriteNumber("br", total.EncBrotli);
            j.WriteNumber("gzip", total.EncGzip);
            j.WriteNumber("identity", total.EncNone);
            j.WriteEndObject();

            j.WritePropertyName("origins");
            j.WriteStartObject();
            if (total.Origins != null) foreach (var (k, v) in total.Origins.OrderByDescending(kv => kv.Value)) j.WriteNumber(k, v);
            j.WriteEndObject();

            j.WritePropertyName("users");
            j.WriteStartArray();
            if (total.Users != null)
            {
                foreach (var (k, v) in total.Users.OrderByDescending(kv => kv.Value).Take(10))
                {
                    j.WriteStartObject();
                    j.WriteString("name", k);
                    j.WriteNumber("count", v);
                    j.WriteEndObject();
                }
            }
            j.WriteEndArray();

            WriteHistogram(j, "latencyHist", total.Latency, 1000.0);
            WriteHistogram(j, "sizeHist", total.Size, 1.0);
        }

        private static void WriteHistogram(Utf8JsonWriter j, string name, LogHistogram h, double divisor)
        {
            j.WritePropertyName(name);
            j.WriteStartObject();
            var cells = h.NonEmpty().ToList();
            Arr(j, "lo", cells.Select(c => (double?)(LogHistogram.BucketLow(c.Index) / divisor)));
            Arr(j, "hi", cells.Select(c => (double?)(LogHistogram.BucketHigh(c.Index) / divisor)));
            Arr(j, "count", cells.Select(c => (double?)c.Count));
            Num(j, "p50", h.Percentile(0.50) / divisor);
            Num(j, "p95", h.Percentile(0.95) / divisor);
            Num(j, "p99", h.Percentile(0.99) / divisor);
            j.WriteEndObject();
        }

        private static void WriteHeatmap(Utf8JsonWriter j, TelemetryBucket?[] points, Window w)
        {
            int minIdx = LogHistogram.BucketCount, maxIdx = -1;
            foreach (var p in points)
            {
                if (p == null) continue;
                foreach (var (idx, _) in p.Latency.NonEmpty())
                {
                    if (idx < minIdx) minIdx = idx;
                    if (idx > maxIdx) maxIdx = idx;
                }
            }
            j.WritePropertyName("heatmap");
            j.WriteStartObject();
            j.WriteNumber("t0", w.From);
            j.WriteNumber("step", w.BucketMs);
            if (maxIdx < 0)
            {
                j.WriteStartArray("lo"); j.WriteEndArray();
                j.WriteStartArray("rows"); j.WriteEndArray();
                j.WriteEndObject();
                return;
            }
            Arr(j, "lo", Enumerable.Range(minIdx, maxIdx - minIdx + 1).Select(i => (double?)(LogHistogram.BucketLow(i) / 1000)));
            Arr(j, "hi", Enumerable.Range(minIdx, maxIdx - minIdx + 1).Select(i => (double?)(LogHistogram.BucketHigh(i) / 1000)));
            j.WriteStartArray("rows");
            foreach (var p in points)
            {
                j.WriteStartArray();
                for (int i = minIdx; i <= maxIdx; i++) j.WriteNumberValue(p?.Latency.CountAt(i) ?? 0);
                j.WriteEndArray();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }

        /// <summary>The finest tier holding per-route data that still covers the window's start.</summary>
        private TelemetryTier RouteTierFor(Window w, long now)
        {
            TelemetryTier tier = w.Tier == TelemetryTier.S10 ? TelemetryTier.M1 : w.Tier;
            while (tier < TelemetryTier.D1 && !TierCovers(tier, w.From, routeClass: true, now)) tier++;
            return tier;
        }

        private void WriteTopCost(Utf8JsonWriter j, Window w, long now)
        {
            TelemetryTier tier = RouteTierFor(w, now);
            var totals = _req[(int)tier].MergeAllSeries(w.From, w.To, s => !IsLongLived(s) || s == UnmatchedSeries);
            double all = Math.Max(1, totals.Values.Sum(b => (double)b.Latency.Sum));
            j.WritePropertyName("topCost");
            j.WriteStartArray();
            foreach (var (series, b) in totals.OrderByDescending(kv => kv.Value.Latency.Sum).Take(10))
            {
                j.WriteStartObject();
                j.WriteString("s", series);
                j.WriteNumber("count", b.Count);
                Num(j, "costMs", b.Latency.Sum / 1000.0);
                Num(j, "share", b.Latency.Sum / all);
                Num(j, "p95", b.Latency.Percentile(0.95) / 1000);
                j.WriteEndObject();
            }
            j.WriteEndArray();
        }

        private void WriteExemplars(Utf8JsonWriter j, string series, Window w)
        {
            j.WritePropertyName("exemplars");
            j.WriteStartArray();
            if (_db != null && !IsLongLived(series))
            {
                int sp = series.IndexOf(' ');
                string method = sp > 0 ? series[..sp] : "GET";
                string route = sp > 0 ? series[(sp + 1)..] : series;
                try
                {
                    var rows = _db.QueryTraces(route, method, null, null, w.From, w.To, "slowest", 12)
                        .Concat(_pendingTraces.Where(t => t.Route == route && t.Method == method && t.Ts >= w.From))
                        .GroupBy(t => t.Id).Select(g => g.First())
                        .OrderByDescending(t => t.TotalMicros).Take(12);
                    foreach (var t in rows) WriteTraceSummary(j, t);
                }
                catch (Exception ex) { Log?.Invoke("Telemetry exemplar read failed: " + ex.Message); }
            }
            j.WriteEndArray();
        }

        internal static void WriteTraceSummary(Utf8JsonWriter j, TelemetryDb.TraceRow t)
        {
            var (stages, inFlight, _) = DecodeStages(t.Stages);
            j.WriteStartObject();
            j.WriteString("id", t.Id.ToString("x16"));
            j.WriteNumber("ts", t.Ts);
            j.WriteString("route", t.Route);
            j.WriteString("method", t.Method);
            j.WriteNumber("status", t.Status);
            Num(j, "ms", t.TotalMicros / 1000.0);
            j.WriteString("cache", ((TelemetryCacheStatus)t.Cache).ToString().ToUpperInvariant());
            j.WriteNumber("bytesOut", t.BytesOut);
            j.WriteNumber("flags", t.Flags);
            j.WriteBoolean("client", t.Client != null);
            j.WriteNumber("inFlight", inFlight);
            j.WriteStartArray("stages");
            foreach (long us in stages) Value(j, us / 1000.0);
            j.WriteEndArray();
            j.WriteEndObject();
        }

        // ─── routes table ───

        private byte[] BuildRoutes(TelemetryQuery q)
        {
            long now = NowMs();
            Window w = PlanWindow(q, now, routeClass: true, hasS10: false);
            bool Include(string s) =>
                s != GlobalSeries && s != BatchItemsSeries
                && (q.IncludeDenied || s != DeniedSeries)
                && (q.Method == null || s.StartsWith(q.Method + " ", StringComparison.Ordinal));

            var store = _req[(int)w.Tier];
            var totals = store.MergeAllSeries(w.From, w.To, Include);

            Window pw = PreviousWindow(w, now, routeClass: true);
            var prevTotals = q.RangeKey == "all"
                ? new Dictionary<string, TelemetryBucket>()
                : _req[(int)pw.Tier].MergeAllSeries(pw.From, pw.To, Include);

            // Sparklines: ≤24 points over the same range.
            var sparkQuery = new TelemetryQuery { Kind = q.Kind, RangeKey = q.RangeKey, FromMs = q.FromMs, ToMs = q.ToMs };
            Window sw = PlanWindow(sparkQuery, now, routeClass: true, hasS10: false, targetPoints: SparkPoints);
            var sparks = BinAll(_req[(int)sw.Tier], sw, Include);

            double seconds = Math.Max(1, Math.Min(w.To, now) - w.From) / 1000.0;
            double allCost = Math.Max(1, totals.Values.Sum(b => (double)b.Latency.Sum));

            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                WriteHeader(j, "routes", q, w, now);
                j.WriteNumber("sparkT0", sw.From);
                j.WriteNumber("sparkStep", sw.BucketMs);
                Num(j, "totalCostMs", allCost / 1000);
                j.WriteNumber("totalCount", totals.Values.Sum(b => b.Count));
                j.WriteStartArray("stageKeys");
                foreach (string k in TelemetryStages.Keys) j.WriteStringValue(k);
                j.WriteEndArray();

                j.WriteStartArray("routes");
                foreach (var (series, b) in totals.OrderByDescending(kv => kv.Value.Latency.Sum))
                {
                    int sp = series.IndexOf(' ');
                    bool pseudo = IsLongLived(series);
                    prevTotals.TryGetValue(series, out TelemetryBucket? prev);
                    double p95 = b.Latency.Percentile(0.95) / 1000;
                    double? prevP95 = prev == null || prev.Count == 0 ? null : prev.Latency.Percentile(0.95) / 1000;

                    int dominant = 0;
                    double dominantMean = -1;
                    for (int s = 0; s < TelemetryStages.Count; s++)
                    {
                        if (!TelemetryStages.CountsTowardLatency(s)) continue;
                        if (b.Stages[s].Mean > dominantMean) { dominantMean = b.Stages[s].Mean; dominant = s; }
                    }

                    j.WriteStartObject();
                    j.WriteString("s", series);
                    j.WriteString("method", pseudo || sp < 0 ? "" : series[..sp]);
                    j.WriteString("route", pseudo || sp < 0 ? series : series[(sp + 1)..]);
                    j.WriteBoolean("pseudo", pseudo);
                    j.WriteNumber("count", b.Count);
                    Num(j, "rps", b.Count / seconds);
                    Num(j, "p50", b.Latency.Percentile(0.50) / 1000);
                    Num(j, "p95", p95);
                    Num(j, "p99", b.Latency.Percentile(0.99) / 1000);
                    Num(j, "max", b.Latency.Max / 1000.0);
                    Num(j, "mean", b.Latency.Mean / 1000);
                    Num(j, "errPct", Pct(b.ServerErrors, b.Count));
                    Num(j, "cliErrPct", Pct(b.ClientErrors, b.Count));
                    NumOrNull(j, "hitPct", b.CacheHit + b.CacheMiss == 0 ? null : Pct(b.CacheHit, b.CacheHit + b.CacheMiss));
                    Num(j, "avgOut", b.Count == 0 ? 0 : (double)b.BytesOut / b.Count);
                    Num(j, "comp", b.BytesOut == 0 ? 1 : (double)b.BytesOutRaw / b.BytesOut);
                    Num(j, "costMs", b.Latency.Sum / 1000.0);
                    Num(j, "share", b.Latency.Sum / allCost);
                    NumOrNull(j, "prevP95", prevP95);
                    j.WriteNumber("prevCount", prev?.Count ?? 0);
                    NumOrNull(j, "trend", prevP95 is > 0 ? p95 / prevP95.Value : null);
                    j.WriteNumber("batch", b.ViaBatch);
                    Num(j, "apdex", Apdex(b.Latency));
                    j.WriteString("dom", TelemetryStages.Keys[dominant]);
                    j.WriteStartArray("stages");
                    for (int s = 0; s < TelemetryStages.Count; s++) Value(j, b.Stages[s].Mean / 1000);
                    j.WriteEndArray();
                    j.WriteStartArray("stagesP95");
                    for (int s = 0; s < TelemetryStages.Count; s++) Value(j, b.Stages[s].Percentile(0.95) / 1000);
                    j.WriteEndArray();
                    sparks.TryGetValue(series, out var spark);
                    j.WritePropertyName("spark");
                    j.WriteStartObject();
                    Arr(j, "count", Enumerable.Range(0, sw.Points).Select(i => (double?)(spark?[i]?.Count ?? 0)));
                    Arr(j, "p95", Enumerable.Range(0, sw.Points).Select(i =>
                        spark?[i] is { Count: > 0 } sb ? sb.Latency.Percentile(0.95) / 1000 : (double?)null));
                    j.WriteEndObject();
                    j.WriteEndObject();
                }
                j.WriteEndArray();
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ─── runtime ───

        private byte[] BuildRuntime(TelemetryQuery q)
        {
            long now = NowMs();
            Window w = PlanWindow(q, now, routeClass: false, hasS10: true);
            var points = Bin(_rt[(int)w.Tier], GlobalSeries, w, () => new RuntimeBucket(), (a, b) => a.Merge(b));

            var buffer = new ArrayBufferWriter<byte>(32 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                WriteHeader(j, "runtime", q, w, now);
                j.WriteNumber("t0", w.From);
                j.WriteNumber("step", w.BucketMs);
                j.WritePropertyName("gauges");
                j.WriteStartObject();
                for (int g = 0; g < RuntimeGauges.Count; g++)
                {
                    int gi = g;
                    j.WritePropertyName(RuntimeGauges.Keys[g]);
                    j.WriteStartObject();
                    Arr(j, "min", points.Select(p => p == null || p.Samples == 0 ? (double?)null : p.Min[gi]));
                    Arr(j, "avg", points.Select(p => p == null || p.Samples == 0 ? (double?)null : p.Avg(gi)));
                    Arr(j, "max", points.Select(p => p == null || p.Samples == 0 ? (double?)null : p.Max[gi]));
                    j.WriteEndObject();
                }
                j.WriteEndObject();

                double[]? last = LastRuntimeSample;
                j.WritePropertyName("now");
                j.WriteStartObject();
                for (int g = 0; g < RuntimeGauges.Count; g++)
                {
                    if (last == null) j.WriteNull(RuntimeGauges.Keys[g]);
                    else Num(j, RuntimeGauges.Keys[g], last[g]);
                }
                j.WriteEndObject();
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ─── RUM ───

        private byte[] BuildRum(TelemetryQuery q)
        {
            long now = NowMs();
            Window w = PlanWindow(q, now, routeClass: false, hasS10: true);
            var store = _rum[(int)w.Tier];
            var points = Bin(store, GlobalSeries, w, () => new RumBucket(), (a, b) => a.Merge(b));
            var total = new RumBucket();
            foreach (var p in points) total.Merge(p);

            TelemetryTier routeTier = RouteTierFor(w, now);
            var perRoute = _rum[(int)routeTier].MergeAllSeries(w.From, w.To, s => s != GlobalSeries);

            var buffer = new ArrayBufferWriter<byte>(32 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                WriteHeader(j, "rum", q, w, now);
                j.WriteBoolean("enabled", !Options.RumDisabled);

                j.WritePropertyName("kpi");
                j.WriteStartObject();
                j.WriteNumber("count", total.Count);
                Num(j, "reusedPct", Pct(total.Reused, total.Count));
                Num(j, "avgTransfer", total.Count == 0 ? 0 : (double)total.TransferBytes / total.Count);
                for (int p = 0; p < RumPhases.Count; p++)
                {
                    j.WritePropertyName(RumPhases.Keys[p]);
                    j.WriteStartObject();
                    Num(j, "mean", total.Phases[p].Mean / 1000);
                    Num(j, "p50", total.Phases[p].Percentile(0.50) / 1000);
                    Num(j, "p95", total.Phases[p].Percentile(0.95) / 1000);
                    Num(j, "p99", total.Phases[p].Percentile(0.99) / 1000);
                    j.WriteEndObject();
                }
                j.WriteEndObject();

                j.WritePropertyName("phases");
                j.WriteStartArray();
                for (int p = 0; p < RumPhases.Count; p++)
                {
                    j.WriteStartObject();
                    j.WriteString("key", RumPhases.Keys[p]);
                    j.WriteString("label", RumPhases.Labels[p]);
                    Num(j, "mean", total.Phases[p].Mean / 1000);
                    Num(j, "p50", total.Phases[p].Percentile(0.50) / 1000);
                    Num(j, "p95", total.Phases[p].Percentile(0.95) / 1000);
                    j.WriteEndObject();
                }
                j.WriteEndArray();

                j.WritePropertyName("ts");
                j.WriteStartObject();
                j.WriteNumber("t0", w.From);
                j.WriteNumber("step", w.BucketMs);
                Arr(j, "count", points.Select(p => (double?)(p?.Count ?? 0)));
                Arr(j, "reusedPct", points.Select(p => p == null || p.Count == 0 ? (double?)null : Pct(p.Reused, p.Count)));
                for (int ph = 0; ph < RumPhases.Count; ph++)
                {
                    int pi = ph;
                    Arr(j, RumPhases.Keys[ph] + "Mean", points.Select(p => p == null || p.Count == 0 ? (double?)null : p.Phases[pi].Mean / 1000));
                    Arr(j, RumPhases.Keys[ph] + "P95", points.Select(p => p == null || p.Count == 0 ? (double?)null : p.Phases[pi].Percentile(0.95) / 1000));
                }
                j.WriteEndObject();

                j.WritePropertyName("routes");
                j.WriteStartArray();
                foreach (var (series, b) in perRoute.OrderByDescending(kv => kv.Value.Count).Take(40))
                {
                    j.WriteStartObject();
                    j.WriteString("s", series);
                    j.WriteNumber("count", b.Count);
                    Num(j, "reusedPct", Pct(b.Reused, b.Count));
                    Num(j, "totalP95", b.Phases[(int)RumPhase.Total].Percentile(0.95) / 1000);
                    Num(j, "networkP95", b.Phases[(int)RumPhase.Network].Percentile(0.95) / 1000);
                    Num(j, "serverP95", b.Phases[(int)RumPhase.Server].Percentile(0.95) / 1000);
                    Num(j, "downloadP95", b.Phases[(int)RumPhase.Download].Percentile(0.95) / 1000);
                    Num(j, "totalP50", b.Phases[(int)RumPhase.Total].Percentile(0.50) / 1000);
                    j.WriteEndObject();
                }
                j.WriteEndArray();
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ─── weekly (hour-of-week, UTC) ───

        private byte[] BuildWeekly(TelemetryQuery q)
        {
            long now = NowMs();
            long from = TelemetryTiers.Align(now - 28 * 86_400_000L, TelemetryTier.H1);
            var store = _req[(int)TelemetryTier.H1];
            var hists = new LogHistogram[168];
            var counts = new double[168];
            var errors = new double[168];
            var samples = new int[168];
            for (int i = 0; i < 168; i++) hists[i] = new LogHistogram();
            for (long s = from; s <= now; s += 3_600_000)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(s).UtcDateTime;
                int cell = (int)dt.DayOfWeek * 24 + dt.Hour;
                samples[cell]++;
                var b = store.Get(s, GlobalSeries);
                if (b == null) continue;
                counts[cell] += b.Count;
                errors[cell] += b.ServerErrors;
                hists[cell].Merge(b.Latency);
            }

            var buffer = new ArrayBufferWriter<byte>(8 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                j.WriteString("kind", "weekly");
                j.WriteNumber("asOfUtc", now);
                j.WriteNumber("from", from);
                j.WriteString("tz", "UTC");
                j.WriteString("layout", "cell = dayOfWeek(0=Sunday)*24 + hourUtc");
                Arr(j, "avgCount", Enumerable.Range(0, 168).Select(i => (double?)(samples[i] == 0 ? 0 : counts[i] / samples[i])));
                Arr(j, "p95", Enumerable.Range(0, 168).Select(i => hists[i].Count == 0 ? (double?)null : hists[i].Percentile(0.95) / 1000));
                Arr(j, "errPct", Enumerable.Range(0, 168).Select(i => counts[i] == 0 ? (double?)null : errors[i] / counts[i] * 100));
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ─── live tick ───

        /// <summary>
        /// A small rolling snapshot for live mode: the last ~60 s of global traffic from the
        /// 10 s tier (5 closed buckets + the open one) plus the latest runtime sample.
        /// </summary>
        internal byte[] BuildLive(long now)
        {
            var s10 = _req[(int)TelemetryTier.S10];
            var acc = new TelemetryBucket();
            long windowStart = s10.OpenStart == long.MinValue ? now : s10.OpenStart - 50_000;
            for (long start = windowStart; start <= s10.OpenStart; start += 10_000) acc.Merge(s10.Get(start, GlobalSeries));
            TelemetryBucket? lastClosed = s10.Get(s10.OpenStart - 10_000, GlobalSeries);
            double seconds = Math.Max(1, now - windowStart) / 1000.0;
            double[]? rt = LastRuntimeSample;

            var buffer = new ArrayBufferWriter<byte>(1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                j.WriteString("kind", "live");
                j.WriteNumber("t", now);
                j.WriteNumber("count60", acc.Count);
                Num(j, "rps", acc.Count / seconds);
                Num(j, "rps10", (lastClosed?.Count ?? 0) / 10.0);
                Num(j, "p50", acc.Latency.Percentile(0.50) / 1000);
                Num(j, "p95", acc.Latency.Percentile(0.95) / 1000);
                Num(j, "p99", acc.Latency.Percentile(0.99) / 1000);
                Num(j, "max", acc.Latency.Max / 1000.0);
                Num(j, "errorPct", Pct(acc.ServerErrors, acc.Count));
                Num(j, "queueP95", acc.Stages[(int)TelemetryStage.DispatchQueue].Percentile(0.95) / 1000);
                Num(j, "bytesOutPerSec", acc.BytesOut / seconds);
                j.WriteNumber("backlog", Backlog);
                j.WriteNumber("dropped", Dropped);
                if (rt != null)
                {
                    Num(j, "inFlight", rt[(int)RuntimeGauge.InFlight]);
                    Num(j, "workerBusy", rt[(int)RuntimeGauge.WorkerThreadsBusy]);
                    Num(j, "pendingWork", rt[(int)RuntimeGauge.PendingWorkItems]);
                    Num(j, "cpuPct", rt[(int)RuntimeGauge.CpuPct]);
                    Num(j, "heapMB", rt[(int)RuntimeGauge.HeapMB]);
                    Num(j, "gcPauseMsPerSec", rt[(int)RuntimeGauge.GcPauseMsPerSec]);
                }
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ─── health ───

        private byte[] BuildHealth()
        {
            long now = NowMs();
            var buffer = new ArrayBufferWriter<byte>(4 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                j.WriteString("kind", "health");
                j.WriteNumber("asOfUtc", now);
                j.WriteBoolean("enabled", !Options.Disabled);
                j.WriteBoolean("rumEnabled", !Options.RumDisabled);
                j.WriteNumber("startedUtc", _startedUtcMs);
                j.WriteNumber("recorded", Recorded);
                j.WriteNumber("folded", _folded);
                j.WriteNumber("dropped", Dropped);
                j.WriteNumber("backlog", Backlog);
                j.WriteNumber("rumReceived", Interlocked.Read(ref _rumReceived));
                j.WriteNumber("rumDropped", Interlocked.Read(ref _rumDropped));
                j.WriteNumber("routeSeries", _knownRouteSeries.Count);
                j.WriteNumber("views", _views.Count);
                j.WriteNumber("warmRouteViews", _routeViewLastAccess.Count);
                j.WriteNumber("dbBytes", _db?.FileBytes ?? 0);
                j.WriteNumber("dbErrors", Interlocked.Read(ref _dbErrors));
                j.WriteNumber("dbQueue", _dbWork.Count);
                long lastFlush = Interlocked.Read(ref _lastFlushUtcMs);
                if (lastFlush > 0) j.WriteNumber("lastFlushUtc", lastFlush); else j.WriteNull("lastFlushUtc");
                j.WritePropertyName("memoryBytes");
                j.WriteStartObject();
                foreach (TelemetryTier tier in TelemetryTiers.All)
                {
                    long bytes = _req[(int)tier].EstimateBytes(b => b.EstimatedBytes)
                        + _rum[(int)tier].EstimateBytes(b => 64 + b.Phases.Sum(h => h.EstimatedBytes))
                        + _rt[(int)tier].EstimateBytes(_ => RuntimeGauges.Count * 24 + 32);
                    j.WriteNumber(TelemetryTiers.Name(tier), bytes);
                }
                j.WriteEndObject();
                j.WritePropertyName("settings");
                j.WriteStartObject();
                j.WriteNumber("slowTraceMs", Options.SlowTraceMs);
                j.WriteNumber("samplesPerRouteMinute", Options.SamplesPerRouteMinute);
                j.WriteNumber("apdexTargetMs", Options.ApdexTargetMs);
                j.WriteNumber("retention1mDays", Options.Retention1mDays);
                j.WriteNumber("retention1hDays", Options.Retention1hDays);
                j.WriteNumber("traceRetentionDays", Options.TraceRetentionDays);
                j.WriteEndObject();
                j.WritePropertyName("buildMs");
                j.WriteStartObject();
                lock (_viewBuildMs)
                {
                    foreach (var (k, v) in _viewBuildMs.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Num(j, k, v);
                }
                j.WriteEndObject();
                j.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // ════════════════════════ JSON helpers ════════════════════════

        private static double Pct(long part, long whole) => whole <= 0 ? 0 : part * 100.0 / whole;

        internal static double Round3(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            if (v == 0) return 0;
            int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            int decimals = Math.Clamp(2 - magnitude, 0, 6);
            return Math.Round(v, decimals);
        }

        private static void Num(Utf8JsonWriter j, string name, double v) => j.WriteNumber(name, Round3(v));

        private static void NumOrNull(Utf8JsonWriter j, string name, double? v)
        {
            if (v == null) j.WriteNull(name); else j.WriteNumber(name, Round3(v.Value));
        }

        private static void Value(Utf8JsonWriter j, double v) => j.WriteNumberValue(Round3(v));

        private static void Arr(Utf8JsonWriter j, string name, IEnumerable<double?> values)
        {
            j.WriteStartArray(name);
            foreach (double? v in values)
            {
                if (v == null) j.WriteNullValue(); else j.WriteNumberValue(Round3(v.Value));
            }
            j.WriteEndArray();
        }
    }
}
