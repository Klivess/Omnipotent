using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    /// <summary>
    /// A problem with Omniscience's own data or machinery (X51). <c>kind</c>: source_down, source_stalled, backfill_stalled,
    /// llm_errors, llm_circuit, cache_floor, jobs_dead, tail_stalled (v2's messages have stopped arriving) or module_stalled
    /// (a module has work queued and has finished none for hours). Open while <c>resolvedMs</c> is null.
    /// </summary>
    [Api.ApiContract]
    public sealed record HealthIssueDto(string IssueId, string Kind, string? Subject, string Severity, JsonElement Detail, long FirstMs, long LastMs, long? ResolvedMs);

    [Api.ApiContract]
    public sealed record HealthListDto(IReadOnlyList<HealthIssueDto> Open, IReadOnlyList<HealthIssueDto> Resolved, bool Enabled, long? CheckedAtMs);

    public sealed partial class MonitorsModule
    {
        /// <summary>Statuses a source can be in on purpose (nothing to tell) or while working.</summary>
        private static readonly HashSet<string> Deliberate = new(StringComparer.OrdinalIgnoreCase) { "disabled", "paused", "stopped_by_user", "archived" };
        private static readonly HashSet<string> Working = new(StringComparer.OrdinalIgnoreCase) { "active", "ok", "connected", "idle", "running", "live", "syncing" };

        private sealed record Issue(string Id, string Kind, string? Subject, object Detail, string Severity);

        /// <summary>When this engine first checked its health (the stall checks count from here after a start).</summary>
        private long healthSinceMs;

        private async Task<string?> HealthAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!Context.Settings.Get("monitors.health.enabled", true)) return Json(new { skipped = "switched off" });
            long now = Context.Clock.NowMs;
            var issues = new List<Issue>();
            var zone = Context.Settings.KlivesZone;

            // 1. Sources: down, or busy and suddenly silent. What arrived is counted per source and day as it arrives (by
            //    ingest sequence, so a pass reads only the new messages).
            await TrackIngestAsync(ct).ConfigureAwait(false);
            List<(string Id, string Platform, string Kind, string? Label, string Status, string? Detail)> sources;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                sources = corpus.Query(null, "SELECT source_id, platform, kind, label, status, status_detail FROM sources ORDER BY source_id",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4), r.GetStringOrNull(5)));
            Dictionary<string, (long Last, Dictionary<string, int> Days)> ingest;
            using (var c = Context.Stores.Intel.OpenRead())
                ingest = c.Query(null, "SELECT source_id, last_ingest_ms, days_json FROM mon_sources", r => (r.GetString(0), r.GetInt64(1), r.GetString(2)))
                    .ToDictionary(x => x.Item1, x => (x.Item2, JsonSerializer.Deserialize<Dictionary<string, int>>(x.Item3) ?? new()), StringComparer.Ordinal);
            double stallHours = Context.Settings.Get("monitors.health.source_stall_hours", 12.0);
            int minDaily = Context.Settings.Get("monitors.health.source_min_daily", 20);
            var today = Zones.LocalDate(now, zone);
            foreach (var s in sources)
            {
                if (Deliberate.Contains(s.Status)) continue;
                if (!Working.Contains(s.Status))
                {
                    issues.Add(new Issue("source_down|" + s.Id, "source_down", s.Id, new { sourceId = s.Id, label = s.Label, platform = s.Platform, status = s.Status, detail = s.Detail }, NoticeSeverity.Attention));
                    continue;
                }
                if (!ingest.TryGetValue(s.Id, out var seen)) continue;
                double week = Enumerable.Range(1, 7).Sum(i => seen.Days.GetValueOrDefault(today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) / 7.0;
                double silentHours = (now - seen.Last) / (double)Hour;
                if (week >= minDaily && silentHours >= stallHours)
                    issues.Add(new Issue("source_stalled|" + s.Id, "source_stalled", s.Id,
                        new { sourceId = s.Id, label = s.Label, platform = s.Platform, lastMessageMs = seen.Last, silentHours = Math.Round(silentHours, 1), usualPerDay = Math.Round(week, 1) }, NoticeSeverity.Attention));
            }

            // 2. Backfills that stopped moving.
            double backfillHours = Context.Settings.Get("monitors.health.backfill_stall_hours", 24.0);
            using (var corpus = Context.Stores.Corpus.OpenRead())
                foreach (var (source, n, oldest) in corpus.Query(null, """
                             SELECT source_id, COUNT(*), MIN(updated_at_ms) FROM ingest_cursors
                             WHERE backward_complete=0 AND COALESCE(state,'') NOT IN ('legacy','done','paused','disabled','complete') AND updated_at_ms < $cut GROUP BY source_id
                             """, r => (r.GetString(0), r.GetInt32(1), r.GetInt64(2)), ("$cut", now - (long)(backfillHours * Hour))))
                    issues.Add(new Issue("backfill_stalled|" + source, "backfill_stalled", source, new { sourceId = source, conversations = n, sinceMs = oldest }, NoticeSeverity.Attention));

            // 3. The model: failures in the last hour, the circuit breaker, and the cache efficiency of background work.
            List<(string Outcome, int N)> outcomes;
            using (var c = Context.Stores.Intel.OpenRead())
                outcomes = c.Query(null, "SELECT outcome, COUNT(*) FROM llm_calls WHERE at_ms >= $h GROUP BY outcome", r => (r.GetString(0), r.GetInt32(1)), ("$h", now - Hour));
            int calls = outcomes.Sum(o => o.N), failed = outcomes.Where(o => o.Outcome != "ok").Sum(o => o.N);
            if (calls >= 10 && failed / (double)calls >= Context.Settings.Get("monitors.health.llm_error_share", 0.3))
                issues.Add(new Issue("llm_errors", "llm_errors", null, new { calls, failed, byOutcome = outcomes.Where(o => o.Outcome != "ok").OrderByDescending(o => o.N).ToDictionary(o => o.Outcome, o => o.N) },
                    NoticeSeverity.Attention));
            if (Context.Llm?.CircuitOpen == true) issues.Add(new Issue("llm_circuit", "llm_circuit", null, new { reason = "The model refused Omniscience's credentials; background model work is paused." }, NoticeSeverity.Urgent));
            if (Context.Llm != null)
            {
                // Of what background work sent a second time, how much the provider served from cache: the part prompt
                // assembly decides. The plain hit rate also counts evidence read for the first time, which nothing can
                // serve from a cache, so it stood under this floor whenever extraction was simply busy.
                var (efficiency, samples) = Context.Llm.ResentEfficiency(TimeSpan.FromHours(6), "Background");
                double floor = Context.Settings.Get("monitors.health.cache_floor", 0.5);
                if (samples >= Context.Settings.Get("monitors.health.cache_min_calls", 20) && efficiency < floor)
                    issues.Add(new Issue("cache_floor", "cache_floor", null, new
                    {
                        efficiency = Math.Round(efficiency, 3), floor, calls = samples, hours = 6,
                        hitRate = Math.Round(Context.Llm.CacheEfficiency(TimeSpan.FromHours(6), "Background").Efficiency, 3),
                    }, NoticeSeverity.Attention));
            }

            // 4. Jobs that failed for good in the last day, per module.
            using (var c = Context.Stores.Intel.OpenRead())
                foreach (var (module, n, last) in c.Query(null, "SELECT module_id, COUNT(*), MAX(finished_at_ms) FROM jobs WHERE state='dead' AND finished_at_ms >= $d GROUP BY module_id",
                             r => (r.GetString(0), r.GetInt32(1), r.GetInt64(2)), ("$d", now - Day)))
                {
                    string? error = c.Scalar<string>(null, "SELECT error_message FROM jobs WHERE state='dead' AND module_id=$m ORDER BY finished_at_ms DESC LIMIT 1", ("$m", module));
                    issues.Add(new Issue("jobs_dead|" + module, "jobs_dead", module, new { moduleId = module, jobs = n, lastMs = last, lastError = error is { Length: > 300 } e ? e[..300] : error }, NoticeSeverity.Attention));
                }

            // 5. Work that is queued and going nowhere, from the job counters (a few dozen rows). Two days after launch the
            //    tail of v2's messages had not completed for nine hours and the deduction sweep not at all, each waiting
            //    behind another module's backlog, and nothing said so: every job in sight was merely "queued".
            double moduleStallHours = Context.Settings.Get("monitors.health.module_stall_hours", 6.0);
            double tailStallMinutes = Context.Settings.Get("monitors.health.tail_stall_minutes", 30.0);
            // Measured from the last success, or from this engine's first check when that is later: a module is given the
            // full time after every start before it is called stalled. A job that is running is not stalled (one that hangs
            // is stopped by the lane's own watch).
            if (healthSinceMs == 0) healthSinceMs = now;
            // Since when each module's next job has been ready to run. A job waiting for its start time (a schedule, a
            // retry backing off) is not waiting on anyone.
            var readySince = Context.Jobs.WaitingHeads().GroupBy(h => h.Module, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(h => h.ReadySinceMs), StringComparer.Ordinal);
            foreach (var (module, stats) in Context.Jobs.ModuleStats())
            {
                if (stats.Leased > 0) continue;
                long since = Math.Max(stats.LastSucceededMs ?? 0, healthSinceMs);
                if (module == Storage.Legacy.LegacyImportModule.ModuleId)
                {
                    // The only way messages reach this engine until Discord capture is switched on. It is due every two
                    // minutes, so with none completed for this long it has stopped, queued or not.
                    if (Context.Settings.Get(Storage.Legacy.LegacyImportModule.EnabledSetting, true) && now - since >= (long)(tailStallMinutes * 60_000))
                        issues.Add(new Issue("tail_stalled", "tail_stalled", module,
                            new { moduleId = module, lastSucceededMs = stats.LastSucceededMs, minutes = Math.Round((now - since) / 60_000.0), queued = stats.Queued }, NoticeSeverity.Urgent));
                    continue;
                }
                // Stalled: a job has been ready all that time, and the module finished nothing in it. (A backlog that is
                // being worked through has old jobs waiting too, but it keeps finishing others.)
                if (!readySince.TryGetValue(module, out long ready)) continue;
                long waiting = now - Math.Max(ready, since);
                if (waiting >= (long)(moduleStallHours * Hour))
                    issues.Add(new Issue("module_stalled|" + module, "module_stalled", module,
                        new { moduleId = module, queued = stats.Queued, lastSucceededMs = stats.LastSucceededMs, hours = Math.Round(waiting / (double)Hour, 1) }, NoticeSeverity.Attention));
            }

            // Open what is new, keep what is still there, close what went away. A problem that comes back is told again.
            var open = new Dictionary<string, (long First, long? Resolved)>(StringComparer.Ordinal);
            using (var c = Context.Stores.Intel.OpenRead())
                foreach (var (id, first, resolved) in c.Query(null, "SELECT issue_id, first_ms, resolved_ms FROM mon_health", r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2))))
                    open[id] = (first, resolved);
            var told = new List<(Issue Issue, long First)>();
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var current = issues.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var issue in issues)
                {
                    string detail = Json(issue.Detail);
                    if (open.TryGetValue(issue.Id, out var row) && row.Resolved == null)
                        c.Exec(tx, "UPDATE mon_health SET detail_json=$d, severity=$s, last_ms=$n WHERE issue_id=$i", ("$d", detail), ("$s", issue.Severity), ("$n", now), ("$i", issue.Id));
                    else
                    {
                        c.Exec(tx, """
                            INSERT INTO mon_health(issue_id, kind, subject, detail_json, severity, first_ms, last_ms, resolved_ms) VALUES($i,$k,$sub,$d,$s,$n,$n,NULL)
                            ON CONFLICT(issue_id) DO UPDATE SET detail_json=excluded.detail_json, severity=excluded.severity, first_ms=excluded.first_ms, last_ms=excluded.last_ms, resolved_ms=NULL
                            """, ("$i", issue.Id), ("$k", issue.Kind), ("$sub", issue.Subject), ("$d", detail), ("$s", issue.Severity), ("$n", now));
                        told.Add((issue, now));
                    }
                }
                foreach (var (id, row) in open.Where(o => o.Value.Resolved == null && !current.Contains(o.Key)))
                    c.Exec(tx, "UPDATE mon_health SET resolved_ms=$n WHERE issue_id=$i", ("$n", now), ("$i", id));
                SetState(c, tx, "health_checked_ms", now);
            }, ct).ConfigureAwait(false);
            int notified = 0;
            foreach (var (issue, first) in told)
                if (await FireAsync($"x51|{issue.Id}|{first.ToString(CultureInfo.InvariantCulture)}", "data_health", null, null, null, null, now,
                        new { issueId = issue.Id, issue = issue.Kind, subject = issue.Subject, detail = issue.Detail }, issue.Severity, false, ct).ConfigureAwait(false))
                    notified++;
            return Json(new { open = issues.Count, notified });
        }

        /// <summary>Counts what each source brought per day (Klives's days, last eight kept), reading only messages that are new since the last pass.</summary>
        private async Task TrackIngestAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            var zone = Context.Settings.KlivesZone;
            long cursor = State("ingest_seq");
            if (cursor == 0)
                using (var corpus = Context.Stores.Corpus.OpenRead())
                    cursor = Math.Max(0, FirstChangeAtOrAfter(corpus, now - 8 * Day, ChangeLog.MaxSeq(corpus)) - 1);
            var tally = new Dictionary<string, (long Last, Dictionary<string, int> Days)>(StringComparer.Ordinal);
            using (var c = Context.Stores.Intel.OpenRead())
                foreach (var (id, last, days) in c.Query(null, "SELECT source_id, last_ingest_ms, days_json FROM mon_sources", r => (r.GetString(0), r.GetInt64(1), r.GetString(2))))
                    tally[id] = (last, JsonSerializer.Deserialize<Dictionary<string, int>>(days) ?? new());
            bool changed = false;
            for (int pass = 0; pass < 20; pass++)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Source, long Seq, long At)> rows;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                    rows = corpus.Query(null, "SELECT best_source_id, ingest_seq, ingested_at_ms FROM messages WHERE ingest_seq > $c ORDER BY ingest_seq LIMIT 20000",
                        r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$c", cursor));
                if (rows.Count == 0) break;
                foreach (var (source, _, at) in rows)
                {
                    var t = tally.TryGetValue(source, out var known) ? known : (Last: 0L, Days: new Dictionary<string, int>(StringComparer.Ordinal));
                    string day = Zones.LocalDate(at, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    t.Days[day] = t.Days.GetValueOrDefault(day) + 1;
                    tally[source] = (Math.Max(t.Last, at), t.Days);
                }
                cursor = rows[^1].Seq;
                changed = true;
                if (rows.Count < 20000) break;
            }
            if (!changed && State("ingest_seq") != 0) return;
            string oldest = Zones.LocalDate(now, zone).AddDays(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            long final = cursor;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var (source, (last, days)) in tally)
                {
                    var kept = days.Where(d => string.CompareOrdinal(d.Key, oldest) >= 0).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                    c.Exec(tx, "INSERT INTO mon_sources(source_id, last_ingest_ms, days_json) VALUES($s,$l,$d) ON CONFLICT(source_id) DO UPDATE SET last_ingest_ms=excluded.last_ingest_ms, days_json=excluded.days_json",
                        ("$s", source), ("$l", last), ("$d", JsonSerializer.Serialize(kept)));
                }
                SetState(c, tx, "ingest_seq", Math.Max(final, 1));
            }, ct).ConfigureAwait(false);
        }

        public HealthListDto Health()
        {
            long now = Context.Clock.NowMs;
            using var c = Context.Stores.Intel.OpenRead();
            var rows = c.Query(null, "SELECT issue_id, kind, subject, severity, detail_json, first_ms, last_ms, resolved_ms FROM mon_health WHERE resolved_ms IS NULL OR resolved_ms >= $w ORDER BY first_ms DESC, issue_id",
                r =>
                {
                    using var doc = JsonDocument.Parse(r.GetString(4));
                    return new HealthIssueDto(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), doc.RootElement.Clone(), r.GetInt64(5), r.GetInt64(6), r.GetInt64OrNull(7));
                }, ("$w", now - 7 * Day));
            long? checkedAt = c.Scalar<long?>(null, "SELECT value FROM mon_state WHERE name='health_checked_ms'");
            return new HealthListDto(rows.Where(r => r.ResolvedMs == null).ToList(), rows.Where(r => r.ResolvedMs != null).ToList(), Context.Settings.Get("monitors.health.enabled", true), checkedAt);
        }
    }
}
