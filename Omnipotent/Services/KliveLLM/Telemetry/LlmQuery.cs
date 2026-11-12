using Omnipotent.Services.KliveAPI.Telemetry;
using System.Collections.Specialized;
using System.Globalization;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>A parsed read of the ledger: a window, a bucket width, a grouping, filters and metrics.</summary>
    public sealed class LlmQuerySpec
    {
        public static readonly string[] Presets = { "15m", "1h", "6h", "24h", "7d", "30d", "90d", "1y", "all" };
        public static readonly string[] Dimensions = { "service", "lane", "router", "provider", "model", "kind", "subject" };
        public static readonly string[] FilterNames = { "service", "lane", "router", "provider", "model", "kind" };

        private static readonly long[] Widths =
        {
            60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 10_800_000,
            21_600_000, 43_200_000, 86_400_000, 172_800_000, 604_800_000, 2_592_000_000,
        };

        public string? Range { get; set; }
        public long FromMs { get; set; }
        public long ToMs { get; set; }
        public long BucketMs { get; set; }
        public LlmTier Tier { get; set; }
        public string? GroupBy { get; set; }
        public Dictionary<string, string[]> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Metrics { get; } = new();
        public int Top { get; set; } = 8;
        public bool Compare { get; set; }
        public string? Rank { get; set; }
        public string? Subject { get; set; }

        public int Count => (int)Math.Max(1, (ToMs - FromMs) / BucketMs);

        public static long PresetMs(string preset) => preset switch
        {
            "15m" => 15 * 60_000L,
            "1h" => 3_600_000L,
            "6h" => 6 * 3_600_000L,
            "24h" => 86_400_000L,
            "7d" => 7 * 86_400_000L,
            "30d" => 30 * 86_400_000L,
            "90d" => 90 * 86_400_000L,
            "1y" => 365 * 86_400_000L,
            _ => long.MaxValue,
        };

        public static bool TryParse(NameValueCollection p, long nowMs, long earliestMs, LlmTelemetryOptions retention,
            out LlmQuerySpec spec, out string? error, string defaultRange = "24h", string[]? defaultMetrics = null)
        {
            spec = new LlmQuerySpec();
            error = null;
            string? from = p["from"], to = p["to"];
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!TryInstant(from, out long f)) { error = "Invalid 'from'."; return false; }
                long t = nowMs;
                if (!string.IsNullOrWhiteSpace(to) && !TryInstant(to, out t)) { error = "Invalid 'to'."; return false; }
                if (t <= f) { error = "'to' must be after 'from'."; return false; }
                spec.FromMs = f;
                spec.ToMs = Math.Min(t, nowMs + 60_000);
            }
            else
            {
                string range = (p["range"] ?? defaultRange).Trim().ToLowerInvariant();
                if (Array.IndexOf(Presets, range) < 0) { error = $"Unknown range '{range}'. Use one of {string.Join(", ", Presets)} or from/to."; return false; }
                spec.Range = range;
                spec.ToMs = nowMs + 1;
                spec.FromMs = range == "all"
                    ? Math.Min(nowMs - 3_600_000, earliestMs > 0 ? earliestMs : nowMs - 30 * 86_400_000L)
                    : nowMs - PresetMs(range);
            }

            long span = spec.ToMs - spec.FromMs;
            string? bucket = p["bucket"];
            long width;
            if (!string.IsNullOrWhiteSpace(bucket) && !bucket.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!TelemetryTiers.TryParseWidth(bucket, out width) || width < 60_000) { error = "Invalid 'bucket' (1m at the finest)."; return false; }
            }
            else
            {
                int target = int.TryParse(p["points"], out int pts) ? Math.Clamp(pts, 12, 400) : 160;
                width = Widths.FirstOrDefault(w => span / w <= target);
                if (width == 0) width = Widths[^1];
            }
            // The finest tier still kept for the start of the window decides how fine buckets can be.
            long minuteFloor = nowMs - retention.MinuteRetentionDays * 86_400_000L;
            long hourFloor = nowMs - retention.HourRetentionDays * 86_400_000L;
            if (width < 3_600_000 && spec.FromMs < minuteFloor) width = Math.Max(width, 3_600_000);
            if (width < 86_400_000 && spec.FromMs < hourFloor) width = Math.Max(width, 86_400_000);
            if (span / width > 2000) { error = "That bucket is too fine for the window (2000 points at most)."; return false; }

            spec.BucketMs = width;
            spec.Tier = width % 86_400_000 == 0 ? LlmTier.D1 : width % 3_600_000 == 0 ? LlmTier.H1 : LlmTier.M1;
            spec.FromMs = LlmTiers.Align(spec.FromMs, width);
            long end = LlmTiers.Align(spec.ToMs, width);
            spec.ToMs = end < spec.ToMs ? end + width : end;

            string? group = p["groupBy"]?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(group) && group != "none")
            {
                if (Array.IndexOf(Dimensions, group) < 0) { error = $"Unknown groupBy '{group}'."; return false; }
                spec.GroupBy = group;
            }
            foreach (string name in FilterNames)
            {
                string? raw = p[name];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                spec.Filters[name] = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToArray();
            }
            spec.Subject = string.IsNullOrWhiteSpace(p["subject"]) ? null : p["subject"]!.Trim();

            foreach (string metric in (p["metrics"] ?? p["metric"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!LlmAggregate.IsMetric(metric)) { error = $"Unknown metric '{metric}'."; return false; }
                if (!spec.Metrics.Contains(metric, StringComparer.OrdinalIgnoreCase)) spec.Metrics.Add(metric);
            }
            if (spec.Metrics.Count == 0) spec.Metrics.AddRange(defaultMetrics ?? new[] { "calls", "totalTokens", "estCostUsd", "p95ProviderMs", "errorPct" });
            if (spec.Metrics.Count > 12) { error = "12 metrics at most."; return false; }
            spec.Top = int.TryParse(p["top"], out int top) ? Math.Clamp(top, 1, 40) : 8;
            spec.Compare = p["compare"] is "1" or "true";
            spec.Rank = LlmAggregate.IsMetric(p["rank"]) ? p["rank"] : null;
            return true;
        }

        private static bool TryInstant(string text, out long ms)
        {
            if (long.TryParse(text, out ms)) return ms > 0;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                ms = dto.ToUnixTimeMilliseconds();
                return true;
            }
            return false;
        }

        public string BucketName => TelemetryTiers.FormatWidth(BucketMs);
    }

    /// <summary>Reads the ledger's aggregates into the shapes the KliveLLM page draws.</summary>
    public sealed class LlmQueryEngine
    {
        private readonly LlmTelemetryDb db;
        private readonly Func<LlmPriceBook?> prices;
        private readonly Func<string, LlmBilling> billingOf;
        private readonly Func<string, string> routerName;

        public LlmQueryEngine(LlmTelemetryDb db, Func<LlmPriceBook?> prices, Func<string, LlmBilling> billingOf, Func<string, string> routerName)
        {
            this.db = db;
            this.prices = prices;
            this.billingOf = billingOf;
            this.routerName = routerName;
        }

        /// <summary>Rows for a window, each costed with the price book as it stands now.</summary>
        private List<LlmAggRow> Rows(LlmTier tier, long from, long to, IReadOnlyDictionary<string, string[]> filters)
        {
            var rows = db.ReadAggregates(tier, from, to, filters);
            var book = prices();
            foreach (var row in rows) Cost(row.Agg, row.Router, row.Model, book);
            return rows;
        }

        private void Cost(LlmAggregate agg, string router, string model, LlmPriceBook? book)
        {
            var (estimated, list, unpriced) = LlmCosting.Price(agg, router, model, billingOf(router), book);
            agg.EstimatedCostUsd = estimated;
            agg.ListCostUsd = list;
            agg.UnpricedTokens = unpriced;
        }

        public string Label(string dimension, string key)
        {
            if (string.IsNullOrEmpty(key)) return dimension switch { "provider" => "(not reported)", "model" => "(none)", _ => "(none)" };
            switch (dimension)
            {
                case "lane":
                    var parts = key.Split('/');
                    var lane = parts.Length == 2 ? LlmConsumerCatalog.Find(parts[0], parts[1]) : null;
                    return parts.Length == 2 ? $"{parts[0]} › {lane?.Label ?? parts[1]}" : key;
                case "router": return routerName(key);
                case "kind":
                    return key switch { "tool" => "Tool session", "chat" => "Chat", "warm" => "Prefix warm-up", "stt" => "Speech to text", "tts" => "Text to speech", "local" => "Local model", _ => key };
                default: return key;
            }
        }

        private static Dictionary<string, double?> Values(LlmAggregate agg, IEnumerable<string> metrics)
            => metrics.ToDictionary(m => m, m => LlmAggregate.Round(agg.Metric(m)), StringComparer.Ordinal);

        // ── time series ──

        public object Series(LlmQuerySpec spec)
        {
            if (spec.GroupBy == "subject") return SubjectSeries(spec);
            var current = BuildSeries(spec, spec.FromMs, spec.ToMs);
            object? previous = null;
            if (spec.Compare)
            {
                long span = spec.ToMs - spec.FromMs;
                var prev = BuildSeries(spec, spec.FromMs - span, spec.FromMs, totalOnly: true);
                previous = new { from = spec.FromMs - span, to = spec.FromMs, total = prev.Total, series = prev.TotalSeries };
            }
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                step = spec.BucketMs,
                bucket = spec.BucketName,
                tier = LlmTiers.Name(spec.Tier),
                count = spec.Count,
                groupBy = spec.GroupBy,
                metrics = spec.Metrics,
                filters = spec.Filters,
                groups = current.Groups,
                total = current.Total,
                totalSeries = current.TotalSeries,
                previous,
            };
        }

        private sealed record SeriesResult(List<object> Groups, Dictionary<string, double?> Total, Dictionary<string, double?[]> TotalSeries);

        private SeriesResult BuildSeries(LlmQuerySpec spec, long from, long to, bool totalOnly = false)
        {
            int n = (int)Math.Max(1, (to - from) / spec.BucketMs);
            var rows = Rows(spec.Tier, from, to, spec.Filters);
            var total = new LlmAggregate();
            var totalBuckets = NewBuckets(n);
            var groups = new Dictionary<string, (LlmAggregate Total, LlmAggregate[] Buckets)>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                int i = (int)((row.Ts - from) / spec.BucketMs);
                if (i < 0 || i >= n) continue;
                total.Merge(row.Agg);
                totalBuckets[i].Merge(row.Agg);
                if (totalOnly || spec.GroupBy == null) continue;
                string key = row.Dimension(spec.GroupBy);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new LlmAggregate(), NewBuckets(n));
                g.Total.Merge(row.Agg);
                g.Buckets[i].Merge(row.Agg);
            }

            var output = new List<object>();
            if (!totalOnly && spec.GroupBy != null)
            {
                string rank = spec.Rank ?? spec.Metrics[0];
                var ordered = groups.OrderByDescending(kv => kv.Value.Total.Metric(rank) ?? double.MinValue).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                var kept = ordered.Take(spec.Top).ToList();
                var rest = ordered.Skip(spec.Top).ToList();
                foreach (var (key, g) in kept)
                    output.Add(new { key, label = Label(spec.GroupBy, key), total = Values(g.Total, spec.Metrics), series = SeriesOf(g.Buckets, spec.Metrics) });
                if (rest.Count > 0)
                {
                    var otherTotal = new LlmAggregate();
                    var otherBuckets = NewBuckets(n);
                    foreach (var (_, g) in rest)
                    {
                        otherTotal.Merge(g.Total);
                        for (int i = 0; i < n; i++) otherBuckets[i].Merge(g.Buckets[i]);
                    }
                    output.Add(new { key = "(other)", label = $"{rest.Count} more", other = true, total = Values(otherTotal, spec.Metrics), series = SeriesOf(otherBuckets, spec.Metrics) });
                }
            }
            return new SeriesResult(output, Values(total, spec.Metrics), SeriesOf(totalBuckets, spec.Metrics));
        }

        private static LlmAggregate[] NewBuckets(int n)
        {
            var buckets = new LlmAggregate[n];
            for (int i = 0; i < n; i++) buckets[i] = new LlmAggregate();
            return buckets;
        }

        private static Dictionary<string, double?[]> SeriesOf(LlmAggregate[] buckets, IEnumerable<string> metrics)
        {
            var result = new Dictionary<string, double?[]>(StringComparer.Ordinal);
            foreach (string metric in metrics)
            {
                var values = new double?[buckets.Length];
                for (int i = 0; i < buckets.Length; i++)
                {
                    var b = buckets[i];
                    // An empty bucket is zero for counts and a gap for rates and latencies.
                    values[i] = b.IsEmpty ? (IsCount(metric) ? 0 : null) : LlmAggregate.Round(b.Metric(metric));
                }
                result[metric] = values;
            }
            return result;
        }

        private static readonly HashSet<string> RateMetrics = new(StringComparer.OrdinalIgnoreCase)
        {
            "errorPct", "cacheHitPct", "prefixReusePct", "costPerMTok", "streamedPct", "attemptFailurePct", "tokensPerSec",
        };

        private static bool IsCount(string metric)
            => !RateMetrics.Contains(metric) && !metric.StartsWith("p", StringComparison.Ordinal)
               && !metric.StartsWith("mean", StringComparison.Ordinal) && !metric.StartsWith("max", StringComparison.Ordinal);

        /// <summary>Per project (or game) series come from the daily subject rows.</summary>
        private object SubjectSeries(LlmQuerySpec spec)
        {
            long width = Math.Max(spec.BucketMs, 86_400_000);
            long from = LlmTiers.Align(spec.FromMs, 86_400_000);
            int n = (int)Math.Max(1, (spec.ToMs - from + width - 1) / width);
            string? service = spec.Filters.TryGetValue("service", out var s) && s.Length == 1 ? s[0] : null;
            var groups = new Dictionary<string, (LlmAggregate Total, LlmAggregate[] Buckets, string Service)>(StringComparer.Ordinal);
            var total = new LlmAggregate();
            var book = prices();
            foreach (var (ts, svc, lane, subject, agg) in db.ReadSubjects(from, spec.ToMs, service))
            {
                if (spec.Filters.TryGetValue("lane", out var lanes) && !lanes.Contains(svc + "/" + lane, StringComparer.OrdinalIgnoreCase)) continue;
                // Subject rows are not split by router: cost what was reported.
                agg.EstimatedCostUsd = agg.CostUsd;
                int i = (int)((ts - from) / width);
                if (i < 0 || i >= n) continue;
                string key = svc + "#" + subject;
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new LlmAggregate(), NewBuckets(n), svc);
                g.Total.Merge(agg);
                g.Buckets[i].Merge(agg);
                total.Merge(agg);
            }
            string rank = spec.Rank ?? spec.Metrics[0];
            var ordered = groups.OrderByDescending(kv => kv.Value.Total.Metric(rank) ?? 0).Take(spec.Top).ToList();
            return new
            {
                from,
                to = from + n * width,
                step = width,
                bucket = TelemetryTiers.FormatWidth(width),
                tier = "1d",
                count = n,
                groupBy = "subject",
                metrics = spec.Metrics,
                groups = ordered.Select(kv => (object)new
                {
                    key = kv.Key,
                    label = kv.Key[(kv.Key.IndexOf('#') + 1)..],
                    service = kv.Value.Service,
                    total = Values(kv.Value.Total, spec.Metrics),
                    series = SeriesOf(kv.Value.Buckets, spec.Metrics),
                }).ToList(),
                total = Values(total, spec.Metrics),
                note = "Per-subject figures are kept by day and cost what the routers reported.",
            };
        }

        // ── tables ──

        public object Breakdown(LlmQuerySpec spec, string? sparkMetric = null)
        {
            string dimension = spec.GroupBy ?? "lane";
            if (dimension == "subject") return SubjectBreakdown(spec);
            var rows = Rows(spec.Tier, spec.FromMs, spec.ToMs, spec.Filters);
            int n = spec.Count;
            var groups = new Dictionary<string, (LlmAggregate Total, LlmAggregate[] Spark, long LastTs, HashSet<string> Routers, HashSet<string> Models)>(StringComparer.Ordinal);
            var total = new LlmAggregate();
            foreach (var row in rows)
            {
                string key = row.Dimension(dimension);
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = (new LlmAggregate(), sparkMetric != null ? NewBuckets(n) : Array.Empty<LlmAggregate>(), 0,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                g.Total.Merge(row.Agg);
                total.Merge(row.Agg);
                g.Routers.Add(row.Router);
                if (!string.IsNullOrEmpty(row.Model)) g.Models.Add(row.Model);
                if (row.Ts > g.LastTs) groups[key] = g with { LastTs = row.Ts };
                if (sparkMetric != null)
                {
                    int i = (int)((row.Ts - spec.FromMs) / spec.BucketMs);
                    if (i >= 0 && i < n) g.Spark[i].Merge(row.Agg);
                }
            }
            var metrics = spec.Metrics.Count > 0 && spec.Metrics.Count <= 12 && !spec.Metrics.SequenceEqual(new[] { "calls", "totalTokens", "estCostUsd", "p95ProviderMs", "errorPct" })
                ? spec.Metrics.Union(LlmAggregate.DefaultSummary).ToList()
                : LlmAggregate.DefaultSummary.ToList();
            double totalCalls = Math.Max(1, total.Calls), totalTokens = Math.Max(1, total.TotalTokens), totalCost = total.EstimatedCostUsd;
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                step = spec.BucketMs,
                groupBy = dimension,
                total = Values(total, metrics),
                groups = groups.OrderByDescending(kv => kv.Value.Total.Calls).Select(kv => (object)new
                {
                    key = kv.Key,
                    label = Label(dimension, kv.Key),
                    values = Values(kv.Value.Total, metrics),
                    share = new
                    {
                        calls = Math.Round(kv.Value.Total.Calls / totalCalls, 4),
                        tokens = Math.Round(kv.Value.Total.TotalTokens / totalTokens, 4),
                        cost = totalCost > 0 ? Math.Round(kv.Value.Total.EstimatedCostUsd / totalCost, 4) : 0,
                    },
                    lastBucket = kv.Value.LastTs,
                    routers = kv.Value.Routers.ToList(),
                    models = kv.Value.Models.Take(12).ToList(),
                    spark = sparkMetric != null ? SeriesOf(kv.Value.Spark, new[] { sparkMetric })[sparkMetric] : null,
                }).ToList(),
            };
        }

        private object SubjectBreakdown(LlmQuerySpec spec)
        {
            string? service = spec.Filters.TryGetValue("service", out var s) && s.Length == 1 ? s[0] : null;
            var groups = new Dictionary<string, (LlmAggregate Total, string Service, HashSet<string> Lanes)>(StringComparer.Ordinal);
            foreach (var (_, svc, lane, subject, agg) in db.ReadSubjects(LlmTiers.Align(spec.FromMs, 86_400_000), spec.ToMs, service))
            {
                agg.EstimatedCostUsd = agg.CostUsd;
                string key = svc + "#" + subject;
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new LlmAggregate(), svc, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                g.Total.Merge(agg);
                g.Lanes.Add(lane);
            }
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                groupBy = "subject",
                groups = groups.OrderByDescending(kv => kv.Value.Total.TotalTokens).Take(200).Select(kv => (object)new
                {
                    key = kv.Key,
                    subject = kv.Key[(kv.Key.IndexOf('#') + 1)..],
                    service = kv.Value.Service,
                    lanes = kv.Value.Lanes.ToList(),
                    values = Values(kv.Value.Total, LlmAggregate.DefaultSummary),
                }).ToList(),
            };
        }

        // ── distributions and patterns ──

        public object Distribution(LlmQuerySpec spec)
        {
            var merged = new LlmAggregate();
            foreach (var row in Rows(spec.Tier, spec.FromMs, spec.ToMs, spec.Filters)) merged.Merge(row.Agg);
            object Hist(LogHistogram h, string unit) => new
            {
                unit,
                count = h.Count,
                mean = h.Count > 0 ? Math.Round(h.Mean, 1) : (double?)null,
                max = h.Count > 0 ? h.Max : (long?)null,
                p50 = h.Count > 0 ? Math.Round(h.Percentile(0.50), 1) : (double?)null,
                p90 = h.Count > 0 ? Math.Round(h.Percentile(0.90), 1) : (double?)null,
                p95 = h.Count > 0 ? Math.Round(h.Percentile(0.95), 1) : (double?)null,
                p99 = h.Count > 0 ? Math.Round(h.Percentile(0.99), 1) : (double?)null,
                buckets = h.NonEmpty().Select(b => new { lo = Math.Round(LogHistogram.BucketLow(b.Index), 1), hi = Math.Round(LogHistogram.BucketHigh(b.Index), 1), count = b.Count }).ToList(),
            };
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                filters = spec.Filters,
                total = Hist(merged.TotalHist, "ms"),
                provider = Hist(merged.ProviderHist, "ms"),
                ttft = Hist(merged.TtftHist, "ms"),
                queue = Hist(merged.QueueHist, "ms"),
                prompt = Hist(merged.PromptHist, "tokens"),
                completion = Hist(merged.CompletionHist, "tokens"),
                outcomes = LlmOutcome.All.Select((name, i) => new { outcome = name, calls = merged.Outcomes[i], attempts = merged.AttemptOutcomes[i] })
                    .Where(o => o.calls > 0 || o.attempts > 0).ToList(),
            };
        }

        /// <summary>Weekday × hour of day, in the viewer's time zone (<paramref name="tzOffsetMinutes"/> east of UTC).</summary>
        public object Heatmap(LlmQuerySpec spec, string metric, int tzOffsetMinutes)
        {
            var cells = new LlmAggregate[7, 24];
            for (int d = 0; d < 7; d++) for (int h = 0; h < 24; h++) cells[d, h] = new LlmAggregate();
            var tier = spec.ToMs - spec.FromMs > 14L * 86_400_000 ? LlmTier.H1 : (spec.Tier == LlmTier.D1 ? LlmTier.H1 : spec.Tier);
            long offset = tzOffsetMinutes * 60_000L;
            foreach (var row in Rows(tier, spec.FromMs, spec.ToMs, spec.Filters))
            {
                var local = DateTimeOffset.FromUnixTimeMilliseconds(row.Ts + offset).UtcDateTime;
                cells[(int)local.DayOfWeek, local.Hour].Merge(row.Agg);
            }
            var values = new double?[7][];
            for (int d = 0; d < 7; d++)
            {
                values[d] = new double?[24];
                for (int h = 0; h < 24; h++) values[d][h] = cells[d, h].IsEmpty ? (IsCount(metric) ? 0 : null) : LlmAggregate.Round(cells[d, h].Metric(metric));
            }
            return new { from = spec.FromMs, to = spec.ToMs, metric, tzOffsetMinutes, days = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }, values };
        }

        // ── the simulator ──

        /// <summary>
        /// Token buckets split the way providers bill them, per lane and per day, plus what the window
        /// actually cost. Counts only: the page multiplies by whatever prices are being tried, so
        /// changing a price re-costs instantly. Cache writes are a subset of input, never an addition.
        /// </summary>
        public object Simulator(LlmQuerySpec spec)
        {
            string dimension = spec.GroupBy is "service" or "router" or "model" ? spec.GroupBy : "lane";
            var tier = spec.ToMs - spec.FromMs > 3L * 86_400_000 ? (spec.FromMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 400L * 86_400_000 ? LlmTier.D1 : LlmTier.H1) : spec.Tier;
            var rows = Rows(tier, spec.FromMs, spec.ToMs, spec.Filters);
            long dayWidth = 86_400_000;
            long dayFrom = LlmTiers.Align(spec.FromMs, dayWidth);
            int days = (int)Math.Max(1, (spec.ToMs - dayFrom + dayWidth - 1) / dayWidth);
            var groups = new Dictionary<string, Buckets>(StringComparer.Ordinal);
            var perDay = new Buckets[days];
            for (int i = 0; i < days; i++) perDay[i] = new Buckets();
            var totals = new Buckets();
            var models = new Dictionary<string, (long Tokens, long Calls)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string key = row.Dimension(dimension);
                if (!groups.TryGetValue(key, out var b)) groups[key] = b = new Buckets();
                b.Add(row.Agg);
                totals.Add(row.Agg);
                int d = (int)((row.Ts - dayFrom) / dayWidth);
                if (d >= 0 && d < days) perDay[d].Add(row.Agg);
                string m = string.IsNullOrEmpty(row.Model) ? "(none)" : row.Model;
                models[m] = models.TryGetValue(m, out var mt) ? (mt.Tokens + row.Agg.TotalTokens, mt.Calls + row.Agg.Calls) : (row.Agg.TotalTokens, row.Agg.Calls);
            }
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                groupBy = dimension,
                totals = totals.View(),
                groups = groups.OrderByDescending(kv => kv.Value.Total).Select(kv => (object)new { key = kv.Key, label = Label(dimension, kv.Key), buckets = kv.Value.View() }).ToList(),
                days = perDay.Select((b, i) => (object)new { day = dayFrom + i * dayWidth, buckets = b.View() }).ToList(),
                models = models.OrderByDescending(kv => kv.Value.Tokens).Select(kv => new { model = kv.Key, tokens = kv.Value.Tokens, calls = kv.Value.Calls }).Take(40).ToList(),
                note = "Input is the prompt tokens processed fresh (prompt minus cache reads); cache writes are part of it. "
                       + "Unmeasured tokens are prompts whose router reported no cache split: they are priced wholly as input.",
            };
        }

        private sealed class Buckets
        {
            public long Input, CacheRead, CacheWrite, Output, Reasoning, Calls, Unmeasured;
            public double Actual, List;
            public long Total => Input + CacheRead + Output;

            public void Add(LlmAggregate a)
            {
                long measured = Math.Min(a.PromptTokens, a.MeasuredPromptTokens);
                long cached = Math.Min(a.CachedTokens, measured);
                Input += Math.Max(0, a.PromptTokens - cached);
                CacheRead += cached;
                CacheWrite += Math.Min(a.CacheWriteTokens, Math.Max(0, a.PromptTokens - cached));
                Output += a.CompletionTokens;
                Reasoning += a.ReasoningTokens;
                Calls += a.Calls;
                Unmeasured += Math.Max(0, a.PromptTokens - measured);
                Actual += a.EstimatedCostUsd;
                List += a.ListCostUsd;
            }

            public object View() => new
            {
                inputTokens = Input,
                cacheReadTokens = CacheRead,
                cacheWriteTokens = CacheWrite,
                outputTokens = Output,
                reasoningTokens = Reasoning,
                totalTokens = Total,
                calls = Calls,
                unmeasuredPromptTokens = Unmeasured,
                actualCostUsd = Math.Round(Actual, 6),
                listCostUsd = Math.Round(List, 6),
            };
        }

        // ── the overview ──

        /// <summary>
        /// One read for the Overview tab: totals for the window and the one before, a series of the
        /// headline metrics, every lane and every router summarised, and the routing map's flows.
        /// </summary>
        public object Overview(LlmQuerySpec spec)
        {
            string[] seriesMetrics = { "calls", "totalTokens", "promptTokens", "completionTokens", "estCostUsd", "listCostUsd", "p95TotalMs", "p50TtftMs", "errorPct", "cacheHitPct", "failed", "fallbacks", "meanQueueMs" };
            var rows = Rows(spec.Tier, spec.FromMs, spec.ToMs, new Dictionary<string, string[]>());
            long span = spec.ToMs - spec.FromMs;
            var previousRows = Rows(spec.Tier, spec.FromMs - span, spec.FromMs, new Dictionary<string, string[]>());
            int n = spec.Count;
            var total = new LlmAggregate();
            var buckets = NewBuckets(n);
            var lanes = new Dictionary<string, (LlmAggregate Agg, LlmAggregate[] Spark)>(StringComparer.Ordinal);
            var routers = new Dictionary<string, (LlmAggregate Agg, LlmAggregate[] Spark)>(StringComparer.OrdinalIgnoreCase);
            var flows = new Dictionary<(string Lane, string Router, string Model, string Provider), LlmAggregate>();
            foreach (var row in rows)
            {
                total.Merge(row.Agg);
                int i = (int)((row.Ts - spec.FromMs) / spec.BucketMs);
                if (i >= 0 && i < n) buckets[i].Merge(row.Agg);
                string lane = row.Service + "/" + row.Lane;
                if (!lanes.TryGetValue(lane, out var l)) lanes[lane] = l = (new LlmAggregate(), NewBuckets(n));
                l.Agg.Merge(row.Agg);
                if (i >= 0 && i < n) l.Spark[i].Merge(row.Agg);
                if (!routers.TryGetValue(row.Router, out var r)) routers[row.Router] = r = (new LlmAggregate(), NewBuckets(n));
                r.Agg.Merge(row.Agg);
                if (i >= 0 && i < n) r.Spark[i].Merge(row.Agg);
                var flowKey = (lane, row.Router, row.Model, row.Provider);
                if (!flows.TryGetValue(flowKey, out var f)) flows[flowKey] = f = new LlmAggregate();
                f.Merge(row.Agg);
            }
            var previous = new LlmAggregate();
            var previousLanes = new Dictionary<string, LlmAggregate>(StringComparer.Ordinal);
            foreach (var row in previousRows)
            {
                previous.Merge(row.Agg);
                string lane = row.Service + "/" + row.Lane;
                if (!previousLanes.TryGetValue(lane, out var pl)) previousLanes[lane] = pl = new LlmAggregate();
                pl.Merge(row.Agg);
            }
            string[] laneMetrics = { "calls", "failed", "errorPct", "totalTokens", "promptTokens", "completionTokens", "cacheHitPct", "estCostUsd", "listCostUsd", "p50TotalMs", "p95TotalMs", "p95TtftMs", "retries", "fallbacks", "redirected", "truncated" };
            return new
            {
                from = spec.FromMs,
                to = spec.ToMs,
                step = spec.BucketMs,
                bucket = spec.BucketName,
                count = n,
                total = Values(total, LlmAggregate.DefaultSummary),
                previous = Values(previous, LlmAggregate.DefaultSummary),
                series = SeriesOf(buckets, seriesMetrics),
                lanes = lanes.OrderByDescending(kv => kv.Value.Agg.Calls).Select(kv => (object)new
                {
                    key = kv.Key,
                    label = Label("lane", kv.Key),
                    values = Values(kv.Value.Agg, laneMetrics),
                    previous = previousLanes.TryGetValue(kv.Key, out var p) ? Values(p, new[] { "calls", "totalTokens", "estCostUsd", "errorPct", "p95TotalMs" }) : null,
                    spark = SeriesOf(kv.Value.Spark, new[] { "calls" })["calls"],
                }).ToList(),
                routers = routers.OrderByDescending(kv => kv.Value.Agg.Calls).Select(kv => (object)new
                {
                    key = kv.Key,
                    label = Label("router", kv.Key),
                    values = Values(kv.Value.Agg, laneMetrics.Concat(new[] { "attemptFailures", "attemptFailurePct", "rateLimited", "tokensPerSec", "p50ProviderMs", "p95ProviderMs", "p99ProviderMs" }).ToArray()),
                    spark = SeriesOf(kv.Value.Spark, new[] { "calls" })["calls"],
                }).ToList(),
                flows = flows.Where(kv => kv.Value.Calls > 0).OrderByDescending(kv => kv.Value.TotalTokens).Take(150).Select(kv => (object)new
                {
                    lane = kv.Key.Lane,
                    laneLabel = Label("lane", kv.Key.Lane),
                    router = kv.Key.Router,
                    routerLabel = Label("router", kv.Key.Router),
                    model = string.IsNullOrEmpty(kv.Key.Model) ? "(none)" : kv.Key.Model,
                    provider = kv.Key.Provider,
                    calls = kv.Value.Calls,
                    tokens = kv.Value.TotalTokens,
                    costUsd = Math.Round(kv.Value.EstimatedCostUsd, 6),
                    listCostUsd = Math.Round(kv.Value.ListCostUsd, 6),
                    failed = kv.Value.Failed,
                }).ToList(),
            };
        }
    }
}
