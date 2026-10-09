using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI.Caching;
using static Omnipotent.Services.KliveAPI.KliveAPI;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// HTTP surface of the telemetry engine. Every aggregate read is answered from a
    /// materialized, pre-compressed view. Missing custom views are queued for the
    /// background engine and answered with 202; a handler never computes analytics.
    /// None of these routes are stored in the general response cache.
    /// </summary>
    internal static class TelemetryRoutes
    {
        private static readonly ConcurrentDictionary<string, (long Minute, int Count)> RumRate = new(StringComparer.Ordinal);
        private const int RumSamplesPerProfileMinute = 1200;
        private const long MaxPhaseMs = 600_000;

        /// <summary>
        /// Registers the routes. Handlers resolve the engine through <paramref name="engine"/>
        /// on every call: the route table outlives a KliveAPI service restart, while the
        /// engine is recreated, so capturing an instance here would pin a stopped one.
        /// </summary>
        public static async Task RegisterAsync(KliveAPI api, Func<ApiTelemetry?> engine,
            Func<TelemetryDb?> database, Func<TelemetryTraceViews?> traceViews, Func<string, string?> routeMethod)
        {
            var klives = SystemPerms.ApiTelemetryRead;

            Func<UserRequest, Task> With(Func<UserRequest, ApiTelemetry, Task> handler) => async req =>
            {
                ApiTelemetry? tel = engine();
                if (tel == null)
                {
                    CacheDeps.MarkUncacheable("telemetry offline");
                    await req.ReturnResponse("{\"error\":\"Telemetry is not running.\"}", "application/json", null, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await handler(req, tel);
            };

            await api.CreateRoute("/KliveAPI/telemetry/overview", With((req, tel) => ServeQuery(req, tel, TelemetryQueryKind.Overview)), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/routes", With((req, tel) => ServeQuery(req, tel, TelemetryQueryKind.Routes)), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/route", With((req, tel) => ServeQuery(req, tel, TelemetryQueryKind.Route)), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/runtime", With((req, tel) => ServeQuery(req, tel, TelemetryQueryKind.Runtime)), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/rum", With((req, tel) => ServeQuery(req, tel, TelemetryQueryKind.Rum)), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/weekly", With((req, tel) => ServeView(req, tel, "weekly")), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/health", With((req, tel) => ServeView(req, tel, "health")), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/live", With(ServeLive), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/traces", With((req, tel) => ServeTraces(req, tel, database(), traceViews())), HttpMethod.Get, klives);
            await api.CreateRoute("/KliveAPI/telemetry/trace", req => ServeTrace(req, database(), traceViews()), HttpMethod.Get, klives);
            // Any signed-in website user reports their own client timings.
            await api.CreateBufferedRoute("/KliveAPI/telemetry/rum", With((req, tel) => IngestRum(req, tel, routeMethod)), HttpMethod.Post,
                Perms.SignedIn, 32 * 1024);
        }

        // ─── aggregates ───

        private static async Task ServeQuery(UserRequest req, ApiTelemetry tel, TelemetryQueryKind kind)
        {
            CacheDeps.MarkUncacheable("telemetry has its own materialized views");
            if (!TelemetryQuery.TryParse(req.userParameters, kind, tel.NowMs(), out TelemetryQuery q, out string? error))
            {
                await req.ReturnResponse(JsonSerializer.Serialize(new { error }), "application/json", null, HttpStatusCode.BadRequest);
                return;
            }

            CacheEntry? entry = tel.TryGetQueryView(q);
            if (entry == null)
            {
                await Pending(req);
                return;
            }
            await WriteEntry(req, entry, "VIEW");
        }

        /// <summary>
        /// Emits a pre-built entry. Inside <c>/batch</c> the handler must never touch the real
        /// socket (the batch owns it), so it goes through the capture-aware ReturnResponse.
        /// </summary>
        private static Task WriteEntry(UserRequest req, CacheEntry entry, string source)
        {
            if (req.capture != null)
            {
                return req.ReturnResponse(Encoding.UTF8.GetString(entry.RawBody), "application/json", null, (HttpStatusCode)entry.StatusCode);
            }
            return CachedResponseWriter.WriteCachedResponseAsync(req.context, req.req, entry, req.requestTimer, req.Trace, source);
        }

        private static async Task ServeView(UserRequest req, ApiTelemetry tel, string key)
        {
            CacheDeps.MarkUncacheable("telemetry view");
            CacheEntry? entry = tel.TryGetView(key);
            if (entry == null)
            {
                await req.ReturnResponse("{\"pending\":true}", "application/json", null, HttpStatusCode.ServiceUnavailable);
                return;
            }
            await WriteEntry(req, entry, "VIEW");
        }

        private static async Task ServeLive(UserRequest req, ApiTelemetry tel)
        {
            CacheDeps.MarkUncacheable("live telemetry tick");
            CacheEntry? entry = tel.TryGetView("live");
            if (entry == null) { await Pending(req); return; }
            await WriteEntry(req, entry, "VIEW");
        }

        private static Task Pending(UserRequest req) => req.ReturnResponse(
            "{\"pending\":true}", "application/json",
            new NameValueCollection { ["Retry-After"] = "1", ["Cache-Control"] = "no-store" }, HttpStatusCode.Accepted);

        // ─── traces ───

        private static async Task ServeTraces(UserRequest req, ApiTelemetry tel, TelemetryDb? db, TelemetryTraceViews? views)
        {
            CacheDeps.MarkUncacheable("trace list");
            if (db == null || views == null)
            {
                await req.ReturnResponse("{\"traces\":[],\"persisted\":false}", "application/json");
                return;
            }
            NameValueCollection p = req.userParameters;
            string range = p["range"] ?? "24h";
            if (!TelemetryQuery.Presets.Contains(range)) range = "24h";
            long? from = p["from"] is string f && TelemetryQuery.TryParseInstant(f, out long fm) ? fm : null;
            long? to = p["to"] is string t && TelemetryQuery.TryParseInstant(t, out long tm) ? tm : null;
            string? route = string.IsNullOrWhiteSpace(p["route"]) ? null : p["route"]!.Trim();
            string? method = string.IsNullOrWhiteSpace(p["method"]) ? null : p["method"]!.Trim().ToUpperInvariant();
            if (route?.Length > 512 || method?.Length > 16)
            {
                await req.ReturnResponse("{\"error\":\"Trace filter too long.\"}", "application/json", null, HttpStatusCode.BadRequest);
                return;
            }
            long? minMicros = double.TryParse(p["minMs"], NumberStyles.Float, CultureInfo.InvariantCulture, out double minMs)
                && double.IsFinite(minMs) && minMs >= 0 && minMs <= 600_000 ? (long)(minMs * 1000) : null;
            int? statusMin = int.TryParse(p["status"], out int st) ? st : null;
            string sort = p["sort"] == "slowest" ? "slowest" : "recent";
            int limit = Math.Clamp(int.TryParse(p["limit"], out int l) ? l : 100, 1, 500);

            var query = new TelemetryTraceViews.ListQuery(range, from, to, route, method, minMicros, statusMin, sort, limit);
            CacheEntry? entry = views.TryGetList(query);
            if (entry == null) { await Pending(req); return; }
            await WriteEntry(req, entry, "VIEW");
        }

        internal static string BuildTraceListJson(System.Collections.Generic.IEnumerable<TelemetryDb.TraceRow> rows, long now, long from, long to)
        {
            var buffer = new ArrayBufferWriter<byte>(16 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                j.WriteNumber("asOfUtc", now);
                j.WriteNumber("from", from);
                j.WriteNumber("to", to);
                j.WriteBoolean("persisted", true);
                j.WriteStartArray("stageKeys");
                foreach (string k in TelemetryStages.Keys) j.WriteStringValue(k);
                j.WriteEndArray();
                j.WriteStartArray("traces");
                foreach (var row in rows) ApiTelemetry.WriteTraceSummary(j, row);
                j.WriteEndArray();
                j.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);

        }

        private static async Task ServeTrace(UserRequest req, TelemetryDb? db, TelemetryTraceViews? views)
        {
            CacheDeps.MarkUncacheable("trace detail");
            string? idText = req.userParameters["id"];
            if (db == null || views == null || string.IsNullOrWhiteSpace(idText)
                || !long.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id))
            {
                await req.ReturnResponse("{\"error\":\"Unknown trace.\"}", "application/json", null, HttpStatusCode.NotFound);
                return;
            }
            CacheEntry? entry = views.TryGetDetail(id);
            if (entry == null) { await Pending(req); return; }
            await WriteEntry(req, entry, "VIEW");
        }

        internal static string BuildTraceJson(TelemetryDb.TraceRow row)
        {
            var (stages, inFlight, pending) = ApiTelemetry.DecodeStages(row.Stages);
            var buffer = new ArrayBufferWriter<byte>(4 * 1024);
            using (var j = new Utf8JsonWriter(buffer))
            {
                j.WriteStartObject();
                j.WriteString("id", row.Id.ToString("x16"));
                j.WriteNumber("ts", row.Ts);
                j.WriteString("route", row.Route);
                j.WriteString("method", row.Method);
                j.WriteNumber("status", row.Status);
                j.WriteNumber("ms", ApiTelemetry.Round3(row.TotalMicros / 1000.0));
                j.WriteString("cache", ((TelemetryCacheStatus)row.Cache).ToString().ToUpperInvariant());
                j.WriteString("origin", row.Origin);
                j.WriteString("profile", row.Profile);
                j.WriteString("reason", row.Reason);
                j.WriteString("encoding", row.Encoding);
                j.WriteNumber("bytesIn", row.BytesIn);
                j.WriteNumber("bytesOut", row.BytesOut);
                j.WriteNumber("bytesRaw", row.BytesRaw);
                j.WriteNumber("flags", row.Flags);
                j.WriteNumber("inFlight", inFlight);
                j.WriteNumber("pendingWork", pending);

                j.WriteStartArray("stages");
                double offset = 0;
                for (int i = 0; i < stages.Length; i++)
                {
                    double ms = stages[i] / 1000.0;
                    j.WriteStartObject();
                    j.WriteString("key", TelemetryStages.Keys[i]);
                    j.WriteString("label", TelemetryStages.Labels[i]);
                    j.WriteNumber("startMs", ApiTelemetry.Round3(offset));
                    j.WriteNumber("ms", ApiTelemetry.Round3(ms));
                    j.WriteBoolean("latency", TelemetryStages.CountsTowardLatency(i));
                    j.WriteEndObject();
                    offset += ms;
                }
                j.WriteEndArray();

                j.WriteStartArray("spans");
                if (!string.IsNullOrEmpty(row.Spans))
                {
                    foreach (string part in row.Spans.Split('|'))
                    {
                        string[] f = part.Split(';');
                        if (f.Length != 3) continue;
                        j.WriteStartObject();
                        j.WriteString("name", f[0]);
                        j.WriteNumber("startMs", ApiTelemetry.Round3(long.TryParse(f[1], out long s0) ? s0 / 1000.0 : 0));
                        j.WriteNumber("ms", ApiTelemetry.Round3(long.TryParse(f[2], out long d0) ? d0 / 1000.0 : 0));
                        j.WriteEndObject();
                    }
                }
                j.WriteEndArray();

                if (row.Client != null)
                {
                    var (phases, reused, transfer) = ApiTelemetry.DecodeClient(row.Client);
                    j.WritePropertyName("client");
                    j.WriteStartObject();
                    j.WriteBoolean("reused", reused);
                    j.WriteNumber("transferBytes", transfer);
                    for (int i = 0; i < phases.Length; i++) j.WriteNumber(RumPhases.Keys[i], ApiTelemetry.Round3(phases[i] / 1000.0));
                    j.WriteEndObject();
                }
                else
                {
                    j.WriteNull("client");
                }
                j.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);

        }

        // ─── RUM ingest ───

        private static async Task IngestRum(UserRequest req, ApiTelemetry tel, Func<string, string?> routeMethod)
        {
            CacheDeps.MarkUncacheable("rum ingest");
            if (tel.Options.RumDisabled)
            {
                await req.ReturnResponse("{\"accepted\":0,\"enabled\":false}", "application/json");
                return;
            }

            string who = req.user?.UserID ?? req.context?.Request?.RemoteEndPoint?.Address?.ToString() ?? "anon";
            long minute = tel.NowMs() / 60_000;
            int accepted = 0, rejected = 0;
            try
            {
                using var doc = JsonDocument.Parse(req.userMessageContent ?? "");
                JsonElement entries = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement
                    : doc.RootElement.TryGetProperty("e", out var e) ? e : default;
                if (entries.ValueKind != JsonValueKind.Array)
                {
                    await req.ReturnResponse("{\"error\":\"Expected {\\\"e\\\":[...]}.\"}", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }
                foreach (JsonElement item in entries.EnumerateArray())
                {
                    if (!TakeRumBudget(who, minute)) { rejected++; continue; }
                    RumSample? sample = ParseRum(item, tel.NowMs(), routeMethod);
                    if (sample == null || !tel.RecordRum(sample)) { rejected++; continue; }
                    accepted++;
                }
            }
            catch (JsonException)
            {
                await req.ReturnResponse("{\"error\":\"Invalid JSON.\"}", "application/json", null, HttpStatusCode.BadRequest);
                return;
            }
            await req.ReturnResponse($"{{\"accepted\":{accepted},\"rejected\":{rejected}}}", "application/json");
        }

        private static bool TakeRumBudget(string who, long minute)
        {
            var updated = RumRate.AddOrUpdate(who, _ => (minute, 1), (_, cur) => cur.Minute == minute ? (minute, cur.Count + 1) : (minute, 1));
            if (RumRate.Count > 5000) RumRate.Clear(); // bounded; worst case resets budgets
            return updated.Count <= RumSamplesPerProfileMinute;
        }

        /// <summary>
        /// Parses one beacon entry: <c>{r, t, ts, b, dns, tcp, tls, net, srv, dl, tot, reuse, xfer}</c>
        /// (all durations in ms). Every value is clamped; the method comes from the route
        /// table (Resource Timing can't see it), and unknown routes collapse to
        /// "(unmatched)" so beacons can never create unbounded series.
        /// </summary>
        internal static RumSample? ParseRum(JsonElement item, long nowMs, Func<string, string?> routeMethod)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            string raw = item.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "/" : "/";
            string route = NormalizeBeaconRoute(raw);
            string? method = routeMethod(route);
            if (method == null)
            {
                route = ApiTelemetry.UnmatchedSeries;
                method = "GET";
            }

            var s = new RumSample { Route = route, Method = method };
            if (item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
                && long.TryParse(t.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id)) s.TraceId = id;
            s.TimestampUtcMs = item.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out long tsv) ? Math.Clamp(tsv, nowMs - 3_600_000, nowMs + 60_000) : nowMs;
            s.Reused = item.TryGetProperty("reuse", out var reuse) && (reuse.ValueKind == JsonValueKind.True || (reuse.ValueKind == JsonValueKind.Number && reuse.GetDouble() != 0));
            s.TransferBytes = item.TryGetProperty("xfer", out var x) && x.TryGetInt64(out long xv) ? Math.Clamp(xv, 0, 1L << 32) : 0;

            string[] keys = { "b", "dns", "tcp", "tls", "net", "srv", "dl", "tot" };
            for (int i = 0; i < keys.Length; i++)
            {
                double ms = item.TryGetProperty(keys[i], out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                if (double.IsNaN(ms) || ms < 0) ms = 0;
                s.PhaseMicros[i] = (long)(Math.Min(ms, MaxPhaseMs) * 1000);
            }
            if (s.PhaseMicros[(int)RumPhase.Total] == 0) return null; // no timing = no information
            return s;
        }

        private static string NormalizeBeaconRoute(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "/";
            string path = raw.Trim();
            int scheme = path.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = path.IndexOf('/', scheme + 3);
                path = slash >= 0 ? path[slash..] : "/";
            }
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path[..q];
            if (!path.StartsWith('/')) path = "/" + path;
            if (path.Length > 1) path = path.TrimEnd('/');
            return path.Length > 200 ? path[..200] : path;
        }
    }
}
