using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    public enum TelemetryQueryKind
    {
        Overview,
        Routes,
        Route,
        Runtime,
        Rum,
        Weekly,
    }

    /// <summary>
    /// A parsed telemetry read. Preset ranges ("24h") resolve against the engine clock
    /// at build time; custom ranges carry explicit UTC bounds. Filters are series-level
    /// only — status/cache/origin are counters inside buckets, not series, so they
    /// narrow the trace list, not the aggregates.
    /// </summary>
    public sealed class TelemetryQuery
    {
        public static readonly string[] Presets = { "15m", "1h", "6h", "24h", "7d", "30d", "90d", "1y", "all" };

        public TelemetryQueryKind Kind;
        public string? RangeKey;          // preset, or null for custom
        public long FromMs, ToMs;         // custom range only
        public long? BucketMs;            // explicit bucket width, null = auto
        public string? Series;            // "GET /route" for Kind.Route
        public bool IncludeBatchItems;
        public bool IncludeDenied;
        public string? Method;            // routes table filter

        public bool IsPreset => RangeKey != null;
        public bool HasFilters => BucketMs.HasValue || IncludeBatchItems || IncludeDenied || !string.IsNullOrEmpty(Method);

        public static long PresetDurationMs(string preset) => preset switch
        {
            "15m" => 15 * 60_000L,
            "1h" => 3_600_000L,
            "6h" => 6 * 3_600_000L,
            "24h" => 24 * 3_600_000L,
            "7d" => 7 * 86_400_000L,
            "30d" => 30 * 86_400_000L,
            "90d" => 90 * 86_400_000L,
            "1y" => 365 * 86_400_000L,
            _ => long.MaxValue, // "all"
        };

        public static string KindKey(TelemetryQueryKind kind) => kind switch
        {
            TelemetryQueryKind.Overview => "overview",
            TelemetryQueryKind.Routes => "routes",
            TelemetryQueryKind.Route => "route",
            TelemetryQueryKind.Runtime => "runtime",
            TelemetryQueryKind.Rum => "rum",
            _ => "weekly",
        };

        /// <summary>Key of the materialized view that answers this query, if it is a plain preset.</summary>
        public string ViewKey => Kind == TelemetryQueryKind.Route
            ? $"route|{Series}|{RangeKey}"
            : Kind == TelemetryQueryKind.Weekly ? "weekly" : $"{KindKey(Kind)}|{RangeKey}";

        public string CacheKey
        {
            get
            {
                var sb = new StringBuilder(96);
                sb.Append(KindKey(Kind)).Append('|').Append(Series).Append('|');
                if (RangeKey != null) sb.Append(RangeKey); else sb.Append(FromMs).Append('-').Append(ToMs);
                sb.Append('|').Append(BucketMs).Append('|').Append(IncludeBatchItems ? 'b' : '-').Append(IncludeDenied ? 'd' : '-').Append('|').Append(Method);
                return sb.ToString();
            }
        }

        /// <summary>Whether the answer can change as time passes (its window reaches the live edge).</summary>
        public bool TouchesNow(long nowMs) => RangeKey != null || ToMs > nowMs - 10 * 60_000;

        /// <summary>
        /// Parses query-string parameters. Accepts <c>range</c> (preset) or <c>from</c>/<c>to</c>
        /// (ISO-8601 or epoch ms), <c>bucket</c> (auto|10s|1m|…), <c>route</c>+<c>method</c>,
        /// <c>batch=1</c>, <c>denied=1</c>.
        /// </summary>
        public static bool TryParse(NameValueCollection p, TelemetryQueryKind kind, long nowMs, out TelemetryQuery query, out string? error)
        {
            query = new TelemetryQuery { Kind = kind };
            error = null;

            string? from = p?["from"], to = p?["to"];
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!TryParseInstant(from, out long f)) { error = "Invalid 'from'."; return false; }
                long t = nowMs;
                if (!string.IsNullOrWhiteSpace(to) && !TryParseInstant(to, out t)) { error = "Invalid 'to'."; return false; }
                if (t <= f) { error = "'to' must be after 'from'."; return false; }
                query.FromMs = f;
                query.ToMs = Math.Min(t, nowMs + 60_000);
            }
            else
            {
                string range = (p?["range"] ?? "24h").Trim().ToLowerInvariant();
                if (Array.IndexOf(Presets, range) < 0) { error = $"Unknown range '{range}'. Use one of {string.Join(", ", Presets)} or from/to."; return false; }
                query.RangeKey = range;
            }

            string? bucket = p?["bucket"];
            if (!string.IsNullOrWhiteSpace(bucket) && !bucket.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!TelemetryTiers.TryParseWidth(bucket, out long ms) || ms < 10_000) { error = "Invalid 'bucket' (minimum 10s)."; return false; }
                query.BucketMs = ms;
            }

            query.IncludeBatchItems = IsTrue(p?["batch"]);
            query.IncludeDenied = IsTrue(p?["denied"]);

            string? method = p?["method"];
            string? route = p?["route"];
            if (kind == TelemetryQueryKind.Route)
            {
                if (string.IsNullOrWhiteSpace(route)) { error = "'route' is required."; return false; }
                if (route.StartsWith('(') || route.StartsWith('*'))
                {
                    query.Series = route; // pseudo-series by name
                }
                else
                {
                    string normalized = route.Trim();
                    if (!normalized.StartsWith('/')) normalized = "/" + normalized;
                    if (normalized.Length > 1) normalized = normalized.TrimEnd('/');
                    query.Series = ApiTelemetry.RouteSeriesKey(string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant(), normalized);
                }
            }
            else if (!string.IsNullOrWhiteSpace(method))
            {
                query.Method = method.Trim().ToUpperInvariant();
            }
            return true;
        }

        private static bool IsTrue(string? v) =>
            v != null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        public static bool TryParseInstant(string s, out long utcMs)
        {
            utcMs = 0;
            s = s.Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            {
                utcMs = n < 100_000_000_000 ? n * 1000 : n; // accept epoch seconds or ms
                return true;
            }
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
            {
                utcMs = dto.ToUnixTimeMilliseconds();
                return true;
            }
            return false;
        }
    }
}
