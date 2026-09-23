using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Tests.KliveAPI.Telemetry;

/// <summary>
/// Dev tool, not a check: when TELEMETRY_DUMP_DIR is set, simulates 8 days of realistic
/// traffic through the real engine and writes every endpoint's JSON to that directory,
/// so the website can be developed against genuine payload shapes. No-op otherwise.
/// </summary>
public sealed class TelemetryPayloadDump
{
    private sealed record RouteProfile(string Method, string Route, double MedianMs, double Spread, int Weight, long Bytes, double HitRate, double ErrorRate, bool Batch);

    [Fact]
    public void DumpRealisticPayloads()
    {
        string? dir = Environment.GetEnvironmentVariable("TELEMETRY_DUMP_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(Path.GetTempPath(), $"telemetry-dump-{Guid.NewGuid():N}.db");

        var profiles = new[]
        {
            new RouteProfile("GET", "/projects/list", 4, 0.6, 30, 18_000, 0.85, 0, true),
            new RouteProfile("GET", "/projects/get", 1.2, 0.5, 40, 42_000, 0.9, 0, true),
            new RouteProfile("GET", "/KliveAPI/Statistics", 0.8, 0.4, 12, 6_000, 0, 0, true),
            new RouteProfile("GET", "/GeneralBotStatistics/GetFrontpageStats", 2, 0.5, 12, 3_500, 0.6, 0, true),
            new RouteProfile("GET", "/api/logs", 6, 0.7, 10, 38_000, 0.2, 0, true),
            new RouteProfile("GET", "/api/logs/summary", 3, 0.5, 8, 2_400, 0.4, 0, true),
            new RouteProfile("GET", "/omniscience/persons", 80, 0.9, 6, 120_000, 0.5, 0.002, false),
            new RouteProfile("GET", "/omniscience/deduction/status", 140, 0.8, 4, 9_000, 0.3, 0.004, false),
            new RouteProfile("GET", "/omniscience/conversations", 60, 0.9, 5, 90_000, 0.5, 0, false),
            new RouteProfile("GET", "/omnidefence/overview", 25, 0.7, 5, 14_000, 0.2, 0, true),
            new RouteProfile("GET", "/omnidefence/requests", 45, 0.8, 3, 160_000, 0, 0, false),
            new RouteProfile("GET", "/kliveagent/stats/prompt-cache", 12, 0.6, 3, 22_000, 0.7, 0, false),
            new RouteProfile("POST", "/kliveagent/chat", 900, 0.7, 2, 4_000, 0, 0.01, false),
            new RouteProfile("GET", "/omnitrader/positions", 9, 0.5, 6, 30_000, 0.4, 0, true),
            new RouteProfile("GET", "/omnitrader/quotes", 3, 0.4, 9, 12_000, 0, 0, true),
            new RouteProfile("POST", "/omnitrader/orders", 35, 0.5, 1, 1_200, 0, 0.02, false),
            new RouteProfile("GET", "/KliveCloud/Download", 220, 1.1, 2, 2_400_000, 0.3, 0, false),
            new RouteProfile("PUT", "/KliveCloud/Upload", 1500, 0.9, 1, 300, 0, 0.01, false),
            new RouteProfile("GET", "/KMProfiles/LoginStatus", 0.4, 0.3, 14, 40, 0, 0, false),
            new RouteProfile("GET", "/ping", 0.15, 0.3, 4, 4, 0, 0, false),
            new RouteProfile("GET", "/projects/cache-health", 18, 0.6, 2, 8_000, 0.5, 0, true),
            new RouteProfile("GET", "/stratum/projects", 7, 0.5, 2, 16_000, 0.6, 0, false),
            new RouteProfile("POST", "/projects/steer", 55, 0.6, 1, 900, 0, 0.005, false),
            new RouteProfile("GET", "/klivemail/inbox", 30, 0.8, 3, 70_000, 0.4, 0, false),
        };
        int totalWeight = profiles.Sum(p => p.Weight);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long simNow = now - 8 * 86_400_000L;
        var db = new TelemetryDb(dbPath);
        var tel = new ApiTelemetry(new TelemetryOptions { SlowTraceMs = 800 }, db) { NowMs = () => simNow };
        tel.LoadHistory();
        tel.AdvanceClock(simNow);
        var rng = new Random(20260923);

        double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

        for (; simNow < now; simNow += 10_000)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(simNow).UtcDateTime;
            double hour = dt.Hour + dt.Minute / 60.0;
            // Diurnal load, quieter weekends, plus the dashboard's constant polling floor.
            double diurnal = 0.35 + 0.65 * Math.Max(0, Math.Sin((hour - 6) / 24 * 2 * Math.PI));
            if (dt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) diurnal *= 0.6;
            // A latency incident 30 hours ago (thread-pool starvation for 40 minutes).
            bool incident = simNow > now - 30 * 3_600_000L && simNow < now - 30 * 3_600_000L + 40 * 60_000L;
            // Denser recent history so the short ranges look alive.
            double density = simNow > now - 6 * 3_600_000L ? 1.0 : 0.35;
            int requests = (int)(18 * diurnal * density + rng.NextDouble() * 3);

            int inFlightBase = incident ? 40 : 2;
            for (int i = 0; i < requests; i++)
            {
                int pick = rng.Next(totalWeight);
                var p = profiles.First(x => (pick -= x.Weight) < 0);
                double handler = Math.Max(0.02, p.MedianMs * Math.Exp(p.Spread * Gaussian()));
                double queue = incident ? 20 + rng.NextDouble() * 400 : 0.02 + rng.NextDouble() * 0.2;
                bool hit = p.Method == "GET" && rng.NextDouble() < p.HitRate;
                if (hit) handler = 0.01;
                bool viaBatch = p.Batch && rng.NextDouble() < 0.7;
                int status = rng.NextDouble() < p.ErrorRate ? 500 : (p.Method == "GET" && rng.NextDouble() < 0.12 ? 304 : 200);
                long bytes = status == 304 ? 0 : (long)(p.Bytes * (0.6 + rng.NextDouble() * 0.8));
                bool compressed = bytes > 1024 && !p.Route.Contains("Download");
                long wire = compressed ? bytes / 5 : bytes;

                var t = TraceHelpers.Make(p.Route, method: p.Method, status: status, handlerMs: handler,
                    bytes: wire, cache: p.Method != "GET" ? TelemetryCacheStatus.None : hit ? TelemetryCacheStatus.Hit : TelemetryCacheStatus.Miss,
                    origin: viaBatch || rng.NextDouble() < 0.8 ? "WebsiteProfile" : "DirectApiProfile",
                    user: rng.NextDouble() < 0.9 ? "Klives" : "Associate-1", viaBatch: viaBatch,
                    queueMs: queue, writeMs: 0.05 + wire / 2_000_000.0);
                t.StageTicks[(int)TelemetryStage.Auth] = TraceHelpers.ToTicks(0.01 + rng.NextDouble() * 0.03);
                t.StageTicks[(int)TelemetryStage.Prologue] = TraceHelpers.ToTicks(0.03 + rng.NextDouble() * 0.05);
                t.StageTicks[(int)TelemetryStage.CacheLookup] = p.Method == "GET" ? TraceHelpers.ToTicks(0.005 + rng.NextDouble() * 0.02) : 0;
                t.StageTicks[(int)TelemetryStage.Encode] = TraceHelpers.ToTicks(bytes / 4_000_000.0);
                t.StageTicks[(int)TelemetryStage.ETag] = p.Method == "GET" ? TraceHelpers.ToTicks(bytes / 8_000_000.0) : 0;
                t.StageTicks[(int)TelemetryStage.Compress] = compressed ? TraceHelpers.ToTicks(bytes / 1_500_000.0) : 0;
                t.StageTicks[(int)TelemetryStage.Teardown] = TraceHelpers.ToTicks(0.02);
                if (p.Method != "GET") t.StageTicks[(int)TelemetryStage.RequestBodyRead] = TraceHelpers.ToTicks(p.Route.Contains("Upload") ? 400 + rng.NextDouble() * 900 : 0.1);
                t.ResponseRawBytes = bytes;
                t.RequestBytes = p.Method == "GET" ? 0 : p.Route.Contains("Upload") ? 8_000_000 : 600;
                t.Encoding = compressed ? "br" : null;
                t.InFlightAtStart = inFlightBase + rng.Next(0, 4);
                t.NotModified = status == 304;
                if (p.Route.StartsWith("/omniscience")) t.AddSpan("gate-wait", t.StartTimestamp, TraceHelpers.ToTicks(incident ? 200 : rng.NextDouble() * 3));
                tel.Fold(t);
            }
            // The outer /batch requests (one global request per dashboard poll).
            if (rng.NextDouble() < 0.9 * density)
            {
                tel.Fold(TraceHelpers.Make("/batch", method: "POST", handlerMs: 20 + rng.NextDouble() * 60, bytes: 30_000, origin: "WebsiteProfile", user: "Klives"));
            }
            if (rng.NextDouble() < 0.05) tel.Fold(TraceHelpers.Make("/wp-login.php", matched: false, status: 404, handlerMs: 0.05, origin: "DirectApi"));
            if (rng.NextDouble() < 0.02) tel.Fold(TraceHelpers.Make("/.env", denied: true, status: 403, handlerMs: 0.02, origin: "DirectApi"));
            tel.Fold(TraceHelpers.Make("/projects/list", method: "OPTIONS", seriesOverride: ApiTelemetry.PreflightSeries, handlerMs: 0.05, bytes: 0));

            // Browser timings for a subset.
            for (int k = 0; k < (int)(3 * density) + 1; k++)
            {
                var s = new RumSample { Route = "/batch", Method = "POST", Reused = rng.NextDouble() < 0.93 };
                double srv = 20 + rng.NextDouble() * 60 + (incident ? 250 : 0);
                double net = 18 + Math.Abs(Gaussian()) * 12;
                double dl = 1 + rng.NextDouble() * 6;
                double dns = s.Reused ? 0 : 8 + rng.NextDouble() * 20, tcp = s.Reused ? 0 : 15 + rng.NextDouble() * 10, tls = s.Reused ? 0 : 25 + rng.NextDouble() * 20;
                double blocked = rng.NextDouble() < 0.1 ? 30 + rng.NextDouble() * 40 : rng.NextDouble() * 2;
                double[] ms = { blocked, dns, tcp, tls, net, srv, dl, blocked + dns + tcp + tls + net + srv + dl };
                for (int ph = 0; ph < ms.Length; ph++) s.PhaseMicros[ph] = (long)(ms[ph] * 1000);
                s.TransferBytes = 6_000 + rng.Next(0, 20_000);
                tel.RecordRum(s);
            }

            // Runtime gauges.
            var g = new double[RuntimeGauges.Count];
            g[(int)RuntimeGauge.InFlight] = inFlightBase + rng.Next(0, 3);
            g[(int)RuntimeGauge.WorkerThreadsBusy] = incident ? 120 + rng.Next(0, 8) : 3 + rng.Next(0, 5);
            g[(int)RuntimeGauge.PoolThreads] = incident ? 132 : 24;
            g[(int)RuntimeGauge.PendingWorkItems] = incident ? 300 + rng.Next(0, 200) : rng.Next(0, 2);
            g[(int)RuntimeGauge.HeapMB] = 900 + 200 * diurnal + rng.NextDouble() * 60;
            g[(int)RuntimeGauge.WorkingSetMB] = 2100 + 300 * diurnal;
            g[(int)RuntimeGauge.GcPauseMsPerSec] = 2 + rng.NextDouble() * 6;
            g[(int)RuntimeGauge.AllocMBPerSec] = 30 + 60 * diurnal + rng.NextDouble() * 10;
            g[(int)RuntimeGauge.Gen0PerMin] = 20 + rng.NextDouble() * 20;
            g[(int)RuntimeGauge.Gen2PerMin] = rng.NextDouble() < 0.1 ? 1 : 0;
            g[(int)RuntimeGauge.LockContentionPerSec] = incident ? 40 : rng.NextDouble() * 3;
            g[(int)RuntimeGauge.CpuPct] = 8 + 20 * diurnal + rng.NextDouble() * 5;
            g[(int)RuntimeGauge.AcceptsPerSec] = requests / 10.0;
            g[(int)RuntimeGauge.CacheEntries] = 3000 + rng.Next(0, 400);
            g[(int)RuntimeGauge.CacheMB] = 60 + rng.NextDouble() * 10;
            g[(int)RuntimeGauge.TelemetryBacklog] = rng.Next(0, 3);
            tel.RecordRuntimeSample(g);

            tel.DrainForTest();
            tel.AdvanceClock(simNow + 10_000);
        }
        simNow = now;
        tel.AdvanceClock(now);
        tel.FlushForTest();
        tel.RebuildAllViewsForTest();
        _ = tel.BuildLive(now);

        void Save(string name, byte[] body) => File.WriteAllBytes(Path.Combine(dir, name + ".json"), body);
        foreach (string key in tel.ViewKeys.ToList())
        {
            var v = tel.TryGetView(key)!;
            Save(key.Replace('|', '_').Replace('/', '~').Replace(' ', '+'), v.RawBody);
        }
        foreach (var p in profiles.Take(8))
        {
            foreach (string preset in new[] { "1h", "24h", "7d" })
            {
                var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route, Series = $"{p.Method} {p.Route}", RangeKey = preset };
                Save($"route_{p.Method}+{p.Route.Replace('/', '~')}_{preset}", tel.QueryAsync(q).Result.RawBody);
            }
        }
        Save("live", tel.BuildLive(now));
        var rows = db.QueryTraces(null, null, null, null, 0, long.MaxValue, "slowest", 150);
        File.WriteAllText(Path.Combine(dir, "traces.json"), TelemetryRoutes.BuildTraceListJson(rows, now, now - 86_400_000, now));
        foreach (var row in rows.Take(40)) File.WriteAllText(Path.Combine(dir, $"trace_{row.Id:x16}.json"), TelemetryRoutes.BuildTraceJson(row));

        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch { } }
    }
}
