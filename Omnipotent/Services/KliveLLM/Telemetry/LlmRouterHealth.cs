using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>A minute of one router, as health is judged: what its attempts did, what its probes saw.</summary>
    public sealed class LlmRouterMinute
    {
        public long Ts { get; set; }
        public long[] Attempts { get; set; } = new long[LlmOutcome.All.Length];
        public long ProbeOk { get; set; }
        public long ProbeFail { get; set; }
        public double ProbeMsSum { get; set; }
        public double ProbeMsMax { get; set; }
        /// <summary>Base64 of a <see cref="LogHistogram"/> of successful attempts' provider time.</summary>
        public string? LatencyHist { get; set; }
        public Dictionary<string, double> Gauges { get; set; } = new(StringComparer.Ordinal);
        public string State { get; set; } = LlmRouterHealth.NoData;

        [JsonIgnore] internal LogHistogram Latency = new();
    }

    /// <summary>An hour of one router: how many of its minutes were up, degraded, down or silent.</summary>
    public sealed class LlmRouterHour
    {
        public long Ts { get; set; }
        public int Up { get; set; }
        public int Degraded { get; set; }
        public int Down { get; set; }
        public int NoData { get; set; }
        public long[] Attempts { get; set; } = new long[LlmOutcome.All.Length];
        public long ProbeOk { get; set; }
        public long ProbeFail { get; set; }
        public double ProbeMsSum { get; set; }
        public string? LatencyHist { get; set; }
        public Dictionary<string, double> Gauges { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>What the page shows about a router right now.</summary>
    public sealed class LlmRouterLive
    {
        public string Router { get; set; } = "";
        public string State { get; set; } = LlmRouterHealth.NoData;
        public DateTime? LastProbeUtc { get; set; }
        public bool? LastProbeOk { get; set; }
        public double? LastProbeMs { get; set; }
        public string? LastProbeError { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastFailure { get; set; }
        public int ModelCount { get; set; }
        public DateTime? ModelsFetchedUtc { get; set; }
        public Dictionary<string, double> Gauges { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Oldest first: the last hour of minute states.</summary>
        public List<string> RecentStates { get; set; } = new();
        [JsonIgnore] internal HashSet<string>? Models;
    }

    /// <summary>
    /// Router availability from two sources: every attempt every service makes (passive, free) and a
    /// light probe per router each minute (so a router nobody is using still has a status). A minute is
    /// down when half or more of its attempts failed on the router's side, degraded when any did or the
    /// router refused (rate limits, a bad key, no credit), up when everything worked, and no data when
    /// nothing was tried and nothing was probed. Our own bad requests never count against a router.
    /// </summary>
    public sealed class LlmRouterHealth
    {
        public const string Up = "up", Degraded = "degraded", Down = "down", NoData = "nodata";

        private readonly object sync = new();
        private readonly Dictionary<string, LlmRouterMinute> open = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LlmRouterHour> hours = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LlmRouterLive> live = new(StringComparer.OrdinalIgnoreCase);
        private long openTs = -1;

        private LlmRouterMinute MinuteFor(string router, long nowMs)
        {
            long ts = LlmTiers.Align(nowMs, LlmTier.M1);
            if (openTs < 0) openTs = ts;
            if (!open.TryGetValue(router, out var minute))
            {
                minute = new LlmRouterMinute { Ts = openTs };
                open[router] = minute;
            }
            return minute;
        }

        private LlmRouterLive LiveFor(string router)
        {
            if (!live.TryGetValue(router, out var state)) live[router] = state = new LlmRouterLive { Router = router };
            return state;
        }

        public void NoteAttempts(LlmCallRecord record)
        {
            long now = record.EndedMs;
            lock (sync)
            {
                if (record.AttemptLog is { Count: > 0 } attempts)
                {
                    foreach (var attempt in attempts)
                    {
                        if (string.IsNullOrEmpty(attempt.Router)) continue;
                        var minute = MinuteFor(attempt.Router, now);
                        minute.Attempts[LlmOutcome.IndexOf(attempt.Outcome)]++;
                        var state = LiveFor(attempt.Router);
                        if (attempt.Outcome == LlmOutcome.Ok)
                        {
                            if (attempt.ProviderMs > 0) minute.Latency.Add(attempt.ProviderMs);
                            state.LastSuccessUtc = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
                        }
                        else if (LlmOutcome.IsRouterFault(attempt.Outcome) || LlmOutcome.IsRouterRefusal(attempt.Outcome))
                        {
                            state.LastFailureUtc = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
                            state.LastFailure = attempt.Outcome + (attempt.Status is { } s ? $" ({s})" : "")
                                + (record.Error != null && attempt == attempts[^1] ? ": " + record.Error : "");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(record.Router) && record.Outcome != LlmOutcome.Blocked)
                {
                    var minute = MinuteFor(record.Router, now);
                    minute.Attempts[LlmOutcome.IndexOf(record.Outcome)]++;
                    if (record.Outcome == LlmOutcome.Ok)
                    {
                        if (record.ProviderMs > 0) minute.Latency.Add(record.ProviderMs);
                        LiveFor(record.Router).LastSuccessUtc = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
                    }
                }
            }
        }

        public void NoteProbe(string router, bool ok, double ms, string? error, IReadOnlyCollection<string>? models = null)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (sync)
            {
                var minute = MinuteFor(router, now);
                if (ok) minute.ProbeOk++; else minute.ProbeFail++;
                minute.ProbeMsSum += ms;
                if (ms > minute.ProbeMsMax) minute.ProbeMsMax = ms;
                var state = LiveFor(router);
                state.LastProbeUtc = DateTime.UtcNow;
                state.LastProbeOk = ok;
                state.LastProbeMs = Math.Round(ms, 1);
                state.LastProbeError = ok ? null : Clip(error);
                if (models is { Count: > 0 })
                {
                    state.Models = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
                    state.ModelCount = models.Count;
                    state.ModelsFetchedUtc = DateTime.UtcNow;
                }
            }
        }

        public void NoteGauge(string router, string name, double value)
        {
            if (!double.IsFinite(value)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (sync)
            {
                var minute = MinuteFor(router, now);
                minute.Gauges[name] = minute.Gauges.TryGetValue(name, out double max) ? Math.Max(max, value) : value;
                LiveFor(router).Gauges[name] = Math.Round(value, 3);
            }
        }

        /// <summary>The models a router's last probe listed, or null when it has not been listed.</summary>
        public IReadOnlySet<string>? Models(string router)
        {
            lock (sync) return live.TryGetValue(router, out var state) ? state.Models : null;
        }

        public static string Judge(long[] attempts, long probeOk, long probeFail)
        {
            long total = 0, faults = 0, refusals = 0;
            for (int i = 0; i < attempts.Length; i++)
            {
                string outcome = LlmOutcome.All[i];
                if (outcome is LlmOutcome.Cancelled or LlmOutcome.Blocked or LlmOutcome.Invalid) continue;
                total += attempts[i];
                if (LlmOutcome.IsRouterFault(outcome)) faults += attempts[i];
                else if (LlmOutcome.IsRouterRefusal(outcome)) refusals += attempts[i];
            }
            if (total > 0)
            {
                if (faults * 2 >= total) return Down;
                return faults + refusals > 0 ? Degraded : Up;
            }
            if (probeFail > 0 && probeOk == 0) return Down;
            return probeOk > 0 ? Up : NoData;
        }

        /// <summary>
        /// Closes the minute once the clock has left it. Returns the rows to persist: each router's
        /// minute, and its hour with that minute folded in (<paramref name="loadHour"/> reads an hour
        /// already on disk, so a restart mid-hour continues the same row).
        /// </summary>
        public List<(string Table, long Ts, string Router, string Json)> Roll(long nowMs, Func<long, string, string?> loadHour)
        {
            var rows = new List<(string, long, string, string)>();
            lock (sync)
            {
                long current = LlmTiers.Align(nowMs, LlmTier.M1);
                if (openTs < 0) { openTs = current; return rows; }
                if (current <= openTs) return rows;

                foreach (var (router, minute) in open)
                {
                    minute.State = Judge(minute.Attempts, minute.ProbeOk, minute.ProbeFail);
                    minute.LatencyHist = minute.Latency.Count > 0 ? Encode(minute.Latency) : null;
                    rows.Add(("router_1m", minute.Ts, router, JsonConvert.SerializeObject(minute)));

                    var state = LiveFor(router);
                    state.State = minute.State;
                    state.RecentStates.Add(minute.State);
                    if (state.RecentStates.Count > 60) state.RecentStates.RemoveAt(0);

                    long hourTs = LlmTiers.Align(minute.Ts, LlmTier.H1);
                    string hourKey = router + "|" + hourTs;
                    if (!hours.TryGetValue(hourKey, out var hour))
                    {
                        string? stored = loadHour(hourTs, router);
                        hour = stored != null ? JsonConvert.DeserializeObject<LlmRouterHour>(stored) ?? new() : new();
                        hour.Ts = hourTs;
                        hours[hourKey] = hour;
                    }
                    switch (minute.State)
                    {
                        case Up: hour.Up++; break;
                        case Degraded: hour.Degraded++; break;
                        case Down: hour.Down++; break;
                        default: hour.NoData++; break;
                    }
                    for (int i = 0; i < minute.Attempts.Length && i < hour.Attempts.Length; i++) hour.Attempts[i] += minute.Attempts[i];
                    hour.ProbeOk += minute.ProbeOk;
                    hour.ProbeFail += minute.ProbeFail;
                    hour.ProbeMsSum += minute.ProbeMsSum;
                    if (minute.Latency.Count > 0)
                    {
                        var merged = Decode(hour.LatencyHist);
                        merged.Merge(minute.Latency);
                        hour.LatencyHist = Encode(merged);
                    }
                    foreach (var (name, value) in minute.Gauges)
                        hour.Gauges[name] = hour.Gauges.TryGetValue(name, out double max) ? Math.Max(max, value) : value;
                    rows.Add(("router_1h", hourTs, router, JsonConvert.SerializeObject(hour)));
                }

                // Routers nobody touched this minute still get a "no data" minute in their live strip.
                foreach (var state in live.Values)
                    if (!open.ContainsKey(state.Router))
                    {
                        state.State = NoData;
                        state.RecentStates.Add(NoData);
                        if (state.RecentStates.Count > 60) state.RecentStates.RemoveAt(0);
                    }

                open.Clear();
                openTs = current;
                foreach (string key in hours.Keys.ToList())
                    if (hours[key].Ts < LlmTiers.Align(nowMs, LlmTier.H1) - 3_600_000) hours.Remove(key);
            }
            return rows;
        }

        public List<LlmRouterLive> Live()
        {
            lock (sync)
                return live.Values.Select(s => new LlmRouterLive
                {
                    Router = s.Router,
                    State = s.State,
                    LastProbeUtc = s.LastProbeUtc,
                    LastProbeOk = s.LastProbeOk,
                    LastProbeMs = s.LastProbeMs,
                    LastProbeError = s.LastProbeError,
                    LastSuccessUtc = s.LastSuccessUtc,
                    LastFailureUtc = s.LastFailureUtc,
                    LastFailure = s.LastFailure,
                    ModelCount = s.ModelCount,
                    ModelsFetchedUtc = s.ModelsFetchedUtc,
                    Gauges = new Dictionary<string, double>(s.Gauges, StringComparer.Ordinal),
                    RecentStates = s.RecentStates.ToList(),
                }).ToList();
        }

        internal static string Encode(LogHistogram h)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) h.Write(w);
            return Convert.ToBase64String(ms.ToArray());
        }

        internal static LogHistogram Decode(string? base64)
        {
            if (string.IsNullOrEmpty(base64)) return new LogHistogram();
            try
            {
                using var ms = new MemoryStream(Convert.FromBase64String(base64));
                using var r = new BinaryReader(ms);
                return LogHistogram.Read(r);
            }
            catch { return new LogHistogram(); }
        }

        private static string? Clip(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 240 ? value : value[..240];
        }
    }
}
