using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>Filters for the LLM call audit; all optional, combined with AND. <see cref="ToMs"/> is exclusive.</summary>
    public sealed record LlmCallQuery
    {
        public string? ModuleId { get; init; }
        public string? Purpose { get; init; }
        public string? WorkClass { get; init; }
        public string? Model { get; init; }
        public string? Outcome { get; init; }
        public long? JobId { get; init; }
        public long? FromMs { get; init; }
        public long? ToMs { get; init; }
        /// <summary>Keyset cursor: the previous page's <see cref="LlmCallPage.NextCursor"/>.</summary>
        public long? BeforeCallId { get; init; }
        public int Limit { get; init; } = 100;
    }

    /// <summary>
    /// The engine read side shared by the HTTP routes and the agent tools. Everything here is an indexed read of
    /// state the kernel maintains; nothing computes on request (the plan's route rule). The status snapshot is
    /// additionally throttled, so a dashboard polling it costs at most one assembly every two seconds.
    /// </summary>
    public sealed class EngineQueries
    {
        private static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(2);

        private readonly V3Host host;
        private readonly IClock clock;
        private readonly Func<IReadOnlyCollection<string>> routes;
        private readonly object statusGate = new();
        private volatile StatusSnapshot? lastStatus;

        private sealed record StatusSnapshot(V3StatusDto Dto, long AtMs);

        public EngineQueries(V3Host host, IClock clock, Func<IReadOnlyCollection<string>> routes)
        {
            this.host = host;
            this.clock = clock;
            this.routes = routes;
        }

        // ── status ───────────────────────────────────────────────────────────────

        public V3StatusDto Status()
        {
            var rt = host.Runtime;
            long now = clock.NowMs;
            if (rt == null || host.Phase != V3Phase.Ready)
            {
                return new V3StatusDto(host.Phase, host.Stage, host.Failure, now, host.StartedAtMs, host.ReadyAtMs, null,
                    Array.Empty<StoreHealthDto>(), Array.Empty<LaneJobsDto>(), Array.Empty<ModuleHealth>(), null, null, routes().ToList());
            }
            // Nobody waits behind a build that is already running: while one request assembles the snapshot the others
            // are answered with the last one. Queued behind a single lock, the dashboard's ten-second poll once piled up
            // for twenty minutes at a time.
            if (lastStatus is { } fresh && now - fresh.AtMs < (long)StatusMaxAge.TotalMilliseconds) return fresh.Dto with { NowMs = now };
            bool taken = false;
            try
            {
                Monitor.TryEnter(statusGate, ref taken);
                if (!taken)
                {
                    if (lastStatus is { } stale) return stale.Dto with { NowMs = now };
                    Monitor.Enter(statusGate, ref taken); // the very first snapshot: there is nothing older to answer with
                }
                if (lastStatus is { } cached && now - cached.AtMs < (long)StatusMaxAge.TotalMilliseconds) return cached.Dto with { NowMs = now };
                var dto = BuildStatus(rt, now);
                lastStatus = new StatusSnapshot(dto, now);
                return dto;
            }
            finally
            {
                if (taken) Monitor.Exit(statusGate);
            }
        }

        private V3StatusDto BuildStatus(V3Runtime rt, long now)
        {
            var stores = rt.Stores.All.Select(s =>
            {
                var h = s.GetHealth();
                return new StoreHealthDto(h.Name, h.FileBytes, h.WalBytes, h.PendingWrites, h.CommittedBatches, h.LastCommit.TotalMilliseconds);
            }).ToList();

            var counts = rt.Host.Jobs.Counts();
            var lanes = Enum.GetValues<Lane>().Select(l =>
            {
                long C(string state) => counts.Where(c => c.Lane == l && c.State == state).Sum(c => c.Count);
                return new LaneJobsDto(l, C(JobStates.Queued), C(JobStates.Leased), C(JobStates.Succeeded), C(JobStates.Dead), C(JobStates.Cancelled));
            }).ToList();

            var llm = new LlmStatusDto(rt.Llm.CircuitOpen, rt.BackgroundGate.ShouldPause(),
                LlmSummary(new LlmSummaryFilter(now - (long)TimeSpan.FromHours(1).TotalMilliseconds, null, LlmGroupBy.WorkClass, null, null)).Rows);
            var bounds = rt.Host.Events.Bounds();
            var stream = new StreamStatusDto(rt.Stream.Connections, bounds.Oldest, bounds.Newest);

            return new V3StatusDto(V3Phase.Ready, host.Stage, null, now, host.StartedAtMs, host.ReadyAtMs, rt.Host.Settings.KlivesZone.Id,
                stores, lanes, rt.Host.Health(), llm, stream, routes().ToList());
        }

        // ── modules ──────────────────────────────────────────────────────────────

        public IReadOnlyList<ModuleDto> Modules()
        {
            var rt = host.Require();
            var health = rt.Host.Health().ToDictionary(h => h.ModuleId, StringComparer.Ordinal);
            return rt.Host.Modules.Select(m =>
            {
                var mf = m.Manifest;
                return new ModuleDto(mf.Id, mf.Version, mf.Title, mf.Category, mf.Description, mf.DependsOn, mf.SubscribedChangeKinds,
                    mf.Schedules.Select(s => new ScheduleDto(s.Name, s.DailyAtLocal?.ToString("HH:mm"), s.Every is { } e ? (long)e.TotalMilliseconds : null)).ToList(),
                    mf.DefaultLane, mf.MinorRestricted, mf.Datasets, mf.Settings, health.GetValueOrDefault(mf.Id));
            }).ToList();
        }

        // ── jobs ─────────────────────────────────────────────────────────────────

        public JobPage Jobs(JobQuery query) => host.Require().Host.Jobs.Query(query);

        public JobRecord Job(long jobId) =>
            host.Require().Host.Jobs.Get(jobId) ?? throw ApiException.NotFound($"Job {jobId} does not exist (finished jobs are kept 14 days, dead ones 90).");

        // ── LLM audit ────────────────────────────────────────────────────────────

        private const string CallColumns =
            "call_id, at_ms, module_id, purpose, job_id, model, provider, work_class, prompt_tokens, cached_tokens, completion_tokens, " +
            "request_bytes, latency_ms, outcome, finish_reason, prompt_version, error";

        public LlmCallPage LlmCalls(LlmCallQuery q)
        {
            var rt = host.Require();
            int limit = Math.Clamp(q.Limit, 1, 1000);
            var (where, p) = LlmWhere(q.FromMs, q.ToMs, q.ModuleId, q.WorkClass);
            void Eq(string column, string name, object? value)
            {
                if (value == null) return;
                where.Add($"{column}={name}");
                p.Add((name, value));
            }
            Eq("purpose", "$purpose", q.Purpose);
            Eq("model", "$model", q.Model);
            Eq("outcome", "$outcome", q.Outcome);
            Eq("job_id", "$job", q.JobId);
            if (q.BeforeCallId is long before) { where.Add("call_id < $before"); p.Add(("$before", before)); }
            p.Add(("$lim", limit + 1));

            using var c = rt.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"SELECT {CallColumns} FROM llm_calls {Where(where)} ORDER BY call_id DESC LIMIT $lim", r => new LlmCallDto(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64OrNull(4), r.GetStringOrNull(5), r.GetStringOrNull(6),
                r.GetStringOrNull(7), r.GetInt64OrNull(8), r.GetInt64OrNull(9), r.GetInt64OrNull(10), r.GetInt64OrNull(11), r.GetInt64OrNull(12),
                r.GetString(13), r.GetStringOrNull(14), r.GetStringOrNull(15), r.GetStringOrNull(16)), p.ToArray());
            if (rows.Count <= limit) return new LlmCallPage(rows, null);
            var page = rows.Take(limit).ToList();
            return new LlmCallPage(page, page[^1].CallId);
        }

        public sealed record LlmSummaryFilter(long? FromMs, long? ToMs, LlmGroupBy GroupBy, string? ModuleId, string? WorkClass);

        /// <summary>Usage grouped by work class, module, purpose, model or outcome: calls, tokens, cache hit rate and
        /// latency percentiles (F358: Omniscience's own cache efficiency, metered per class).</summary>
        public LlmSummaryDto LlmSummary(LlmSummaryFilter f)
        {
            var rt = host.Require();
            string key = f.GroupBy switch
            {
                LlmGroupBy.WorkClass => "COALESCE(work_class,'unknown')",
                LlmGroupBy.Module => "module_id",
                LlmGroupBy.Purpose => "module_id || '/' || purpose",
                LlmGroupBy.Model => "COALESCE(model,'unknown')",
                LlmGroupBy.Outcome => "outcome",
                _ => throw ApiException.BadRequest("Unknown grouping."),
            };
            var (where, p) = LlmWhere(f.FromMs, f.ToMs, f.ModuleId, f.WorkClass);
            using var c = rt.Stores.Intel.OpenRead();
            var totals = c.Query(null, $"""
                SELECT {key} AS k, COUNT(*), SUM(CASE WHEN outcome='ok' THEN 1 ELSE 0 END),
                       COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(cached_tokens),0), COALESCE(SUM(completion_tokens),0), MIN(at_ms), MAX(at_ms)
                FROM llm_calls {Where(where)} GROUP BY k ORDER BY COUNT(*) DESC
                """,
                r => (Key: r.GetString(0), Calls: r.GetInt64(1), Ok: r.GetInt64(2), Prompt: r.GetInt64(3), Cached: r.GetInt64(4),
                      Completion: r.GetInt64(5), First: r.GetInt64OrNull(6), Last: r.GetInt64OrNull(7)), p.ToArray());

            var latWhere = new List<string>(where) { "latency_ms IS NOT NULL" };
            var latencies = c.Query(null, $"SELECT {key} AS k, latency_ms FROM llm_calls {Where(latWhere)} ORDER BY k, latency_ms",
                    r => (Key: r.GetString(0), Ms: r.GetInt64(1)), p.ToArray())
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Ms).ToList(), StringComparer.Ordinal);

            var rows = totals.Select(t =>
            {
                latencies.TryGetValue(t.Key, out var lat);
                return new LlmSummaryRowDto(t.Key, t.Calls, t.Ok, t.Calls - t.Ok, t.Prompt, t.Cached, t.Completion,
                    t.Prompt > 0 ? Math.Round((double)t.Cached / t.Prompt, 4) : 0, Percentile(lat, 0.50), Percentile(lat, 0.95), t.First, t.Last);
            }).ToList();
            return new LlmSummaryDto(f.FromMs, f.ToMs, f.GroupBy, rows);
        }

        /// <summary>Nearest-rank percentile of an ascending list.</summary>
        internal static long? Percentile(IReadOnlyList<long>? sorted, double q)
        {
            if (sorted == null || sorted.Count == 0) return null;
            int rank = (int)Math.Ceiling(q * sorted.Count);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
        }

        private static (List<string> Where, List<(string, object?)> Params) LlmWhere(long? from, long? to, string? moduleId, string? workClass)
        {
            var where = new List<string>();
            var p = new List<(string, object?)>();
            if (from is long f) { where.Add("at_ms >= $from"); p.Add(("$from", f)); }
            if (to is long t) { where.Add("at_ms < $to"); p.Add(("$to", t)); }
            if (moduleId != null) { where.Add("module_id = $module"); p.Add(("$module", moduleId)); }
            if (workClass != null) { where.Add("work_class = $wc"); p.Add(("$wc", workClass)); }
            return (where, p);
        }

        private static string Where(List<string> clauses) => clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);

        // ── events (polling twin of the live stream) ─────────────────────────────

        public StreamEventPage Events(long since, IReadOnlyList<string> prefixes, Func<string, bool> maySee, int limit)
        {
            var events = host.Require().Host.Events;
            limit = Math.Clamp(limit, 1, 1000);
            var (cursor, reset) = LiveStreamHub.PlanResume(since, events.Bounds());
            var items = new List<StreamEventDto>();
            var permitted = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool more = false;
            for (int pass = 0; pass < 20 && !more; pass++)
            {
                var page = events.Replay(cursor, 1000);
                foreach (var ev in page)
                {
                    if (!permitted.TryGetValue(ev.Topic, out bool allowed)) permitted[ev.Topic] = allowed = maySee(ev.Topic);
                    if (allowed && LiveStreamHub.MatchesPrefixes(ev.Topic, prefixes))
                    {
                        if (items.Count == limit) { more = true; break; }
                        items.Add(new StreamEventDto(ev.EventId, ev.AtMs, ev.Topic, ParsePayload(ev.PayloadJson)));
                    }
                    cursor = ev.EventId;
                }
                if (page.Count < 1000) break;
                if (pass == 19) more = true; // scanned 20k events without filling the page: let the client continue
            }
            return new StreamEventPage(items, cursor, reset, more);
        }

        private static JsonElement ParsePayload(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        // ── settings ─────────────────────────────────────────────────────────────

        public IReadOnlyList<SettingDto> Settings()
        {
            var rt = host.Require();
            var stored = rt.Host.Settings.Stored().ToDictionary(s => s.Key, StringComparer.Ordinal);
            var result = new List<SettingDto>();
            foreach (var (scope, d) in Descriptors(rt))
            {
                stored.Remove(d.Key, out var s);
                result.Add(new SettingDto(d.Key, scope, d.Type, d.DefaultJson, s.ValueJson, d.Description, d.Min, d.Max, s.ValueJson == null ? null : s.UpdatedAtMs));
            }
            // Values no descriptor claims any more (a module was removed or renamed a key): shown so they can be spotted.
            foreach (var s in stored.Values)
                result.Add(new SettingDto(s.Key, "orphaned", SettingTypes.Json, "null", s.ValueJson, "No module declares this setting any more; it has no effect.", null, null, s.UpdatedAtMs));
            return result;
        }

        public async Task<SettingDto> SetSettingAsync(SetSettingRequest request, string actor, CancellationToken ct)
        {
            var rt = host.Require();
            if (string.IsNullOrWhiteSpace(request.Key)) throw ApiException.BadRequest("'key' is required.");
            if (request.Value.ValueKind == JsonValueKind.Undefined) throw ApiException.BadRequest("'value' is required (send null to store an explicit null).");
            var match = Descriptors(rt).FirstOrDefault(x => x.Descriptor.Key == request.Key);
            if (match.Descriptor == null) throw ApiException.NotFound($"No setting is named '{request.Key}'.");
            string? problem = KernelSettings.Validate(match.Descriptor, request.Value);
            if (problem != null) throw ApiException.BadRequest($"'{request.Key}' {problem}.");
            await rt.Host.Settings.SetAsync(request.Key, request.Value, actor, ct).ConfigureAwait(false);
            lock (statusGate) lastStatus = null;
            return Settings().First(s => s.Key == request.Key);
        }

        private static IEnumerable<(string Scope, SettingDescriptor Descriptor)> Descriptors(V3Runtime rt) =>
            KernelSettings.KernelDescriptors.Select(d => ("kernel", d))
                .Concat(rt.Host.Modules.SelectMany(m => m.Manifest.Settings.Select(d => (m.Manifest.Id, d))));

        // ── commands ─────────────────────────────────────────────────────────────

        public async Task<JobActionResultDto> CancelJobAsync(long jobId, string actor, CancellationToken ct)
        {
            var rt = host.Require();
            if (jobId <= 0) throw ApiException.BadRequest("'jobId' is required.");
            bool changed = await rt.Host.Jobs.CancelAsync(jobId, ct).ConfigureAwait(false);
            var job = rt.Host.Jobs.Get(jobId) ?? throw ApiException.NotFound($"Job {jobId} does not exist.");
            if (changed) await OperatorAudit.WriteAsync(rt.Stores.Operator, clock, actor, "job.cancel", jobId.ToString(), new { job.ModuleId, job.Kind }, ct).ConfigureAwait(false);
            return new JobActionResultDto(jobId, changed, job.State);
        }

        /// <summary>Retries every dead job (of one module, or of all) in one go; see <see cref="JobQueue.RetryAllDeadAsync"/>.</summary>
        public async Task<DeadJobsRetriedDto> RetryDeadJobsAsync(string? moduleId, string actor, CancellationToken ct)
        {
            var rt = host.Require();
            string? module = string.IsNullOrWhiteSpace(moduleId) ? null : moduleId.Trim();
            var (retried, closed) = await rt.Host.Jobs.RetryAllDeadAsync(module, ct).ConfigureAwait(false);
            if (retried + closed > 0)
            {
                await OperatorAudit.WriteAsync(rt.Stores.Operator, clock, actor, "job.retry_dead", module ?? "all", new { retried, closed }, ct).ConfigureAwait(false);
                rt.Host.Kick();
            }
            return new DeadJobsRetriedDto(retried, closed);
        }

        public async Task<JobActionResultDto> RetryJobAsync(long jobId, string actor, CancellationToken ct)
        {
            var rt = host.Require();
            if (jobId <= 0) throw ApiException.BadRequest("'jobId' is required.");
            bool changed = await rt.Host.Jobs.RetryDeadAsync(jobId, ct).ConfigureAwait(false);
            var job = rt.Host.Jobs.Get(jobId) ?? throw ApiException.NotFound($"Job {jobId} does not exist.");
            // "changed" and cancelled: the same work was already queued again, so this one was closed rather than retried.
            if (!changed && job.State != JobStates.Dead && job.State != JobStates.Queued)
                throw ApiException.Conflict($"Job {jobId} is {job.State}; only dead jobs can be retried.");
            if (changed)
            {
                await OperatorAudit.WriteAsync(rt.Stores.Operator, clock, actor, "job.retry", jobId.ToString(), new { job.ModuleId, job.Kind }, ct).ConfigureAwait(false);
                rt.Host.Kick();
            }
            return new JobActionResultDto(jobId, changed, job.State);
        }

        public async Task<ModuleDto> SetModuleEnabledAsync(ModuleEnabledRequest request, string actor, CancellationToken ct)
        {
            var rt = host.Require();
            if (string.IsNullOrWhiteSpace(request.ModuleId)) throw ApiException.BadRequest("'moduleId' is required.");
            if (!rt.Host.Modules.Any(m => m.Manifest.Id == request.ModuleId)) throw ApiException.NotFound($"No module is named '{request.ModuleId}'.");
            await rt.Host.SetEnabledAsync(request.ModuleId, request.Enabled, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(rt.Stores.Operator, clock, actor, request.Enabled ? "module.enable" : "module.disable", request.ModuleId, null, ct).ConfigureAwait(false);
            lock (statusGate) lastStatus = null;
            if (request.Enabled) rt.Host.Kick();
            return Modules().First(m => m.Id == request.ModuleId);
        }
    }
}
