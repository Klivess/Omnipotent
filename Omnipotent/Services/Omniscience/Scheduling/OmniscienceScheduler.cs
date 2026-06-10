using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Scheduling
{
    /// <summary>
    /// Drives nightly + on-demand recompute/profile runs. Only explicitly enabled
    /// person_profile_targets receive LLM dossiers; analytics can still be recomputed
    /// for an opened person without enrolling them.
    /// </summary>
    public class OmniscienceScheduler
    {
        private readonly Omniscience service;
        private readonly Analytics.AnalyticsEngine analytics;
        private readonly Profiling.PersonalityProfiler profiler;
        private readonly SemaphoreSlim runLock = new(1, 1);
        private readonly object stateLock = new();
        private RunState? currentRun;

        public DateTime? LastRunStartedAt { get; private set; }
        public DateTime? LastRunFinishedAt { get; private set; }
        public string LastRunStatus { get; private set; } = "never run";
        public bool IsRunning => runLock.CurrentCount == 0;

        public OmniscienceScheduler(Omniscience service, Analytics.AnalyticsEngine analytics, Profiling.PersonalityProfiler profiler)
        {
            this.service = service;
            this.analytics = analytics;
            this.profiler = profiler;
        }

        public void HookSchedule()
        {
            var due = DateTime.Today.AddDays(1).AddHours(3).AddMinutes(30);
            _ = service.ServiceCreateScheduledTask(due, "OmniscienceNightly", "analytics", "Nightly recompute + targeted personality refresh", true);
            service.GetTimeManagerService().TaskDue += OnTaskDue;
        }

        private async void OnTaskDue(object? sender, TimeManager.ScheduledTask task)
        {
            if (task == null) return;
            if (!string.Equals(task.agentName, service.GetName(), StringComparison.Ordinal)) return;
            if (!string.Equals(task.taskName, "OmniscienceNightly", StringComparison.Ordinal)) return;
            try
            {
                await RunNowAsync(CancellationToken.None);
            }
            finally
            {
                // Re-arm for tomorrow.
                var due = DateTime.Today.AddDays(1).AddHours(3).AddMinutes(30);
                _ = service.ServiceCreateScheduledTask(due, "OmniscienceNightly", "analytics", "Nightly recompute + targeted personality refresh", true);
            }
        }

        public async Task<bool> RunNowAsync(CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct))
            {
                _ = service.ServiceLog("Omniscience nightly run skipped: already running.");
                return false;
            }
            try
            {
                var personIds = GetProfileTargetIds();
                StartRun("profile_targets", personIds);
                LastRunStartedAt = DateTime.UtcNow;
                LastRunStatus = "running targeted profile pass";
                _ = service.ServiceLog($"Omniscience: starting targeted profile pass for {personIds.Count} people.");

                int analyticsOk = 0, analyticsFail = 0, profilesOk = 0, profilesFail = 0;
                foreach (var pid in personIds)
                {
                    ct.ThrowIfCancellationRequested();
                    SetItem(pid, analytics: "running", profile: "waiting", error: null);
                    try
                    {
                        await analytics.RunForPersonAsync(pid, ct);
                        analyticsOk++;
                        SetItem(pid, analytics: "ok", profile: "running", error: null);
                    }
                    catch (Exception ex)
                    {
                        analyticsFail++;
                        SetItem(pid, analytics: "failed", profile: "skipped", error: ex.Message);
                        _ = service.ServiceLogError(ex, $"Analytics failed for {pid}");
                        continue;
                    }

                    try
                    {
                        bool ok = await profiler.GenerateForPersonAsync(pid, ct);
                        if (ok)
                        {
                            profilesOk++;
                            SetItem(pid, analytics: "ok", profile: "ok", error: null);
                        }
                        else
                        {
                            profilesFail++;
                            SetItem(pid, analytics: "ok", profile: "failed", error: "LLM returned no dossier");
                            MarkTargetStatus(pid, "failed: empty dossier");
                        }
                    }
                    catch (Exception ex)
                    {
                        profilesFail++;
                        SetItem(pid, analytics: "ok", profile: "failed", error: ex.Message);
                        MarkTargetStatus(pid, "failed: " + ex.Message);
                        _ = service.ServiceLogError(ex, $"Profiling failed for {pid}");
                    }
                }
                LastRunStatus = $"ok ({personIds.Count} targets, {analyticsOk} analytics ok, {analyticsFail} analytics failed, {profilesOk} dossiers, {profilesFail} dossier failed)";
                FinishRun("ok");
                _ = service.ServiceLog($"Omniscience targeted run complete. {LastRunStatus}");

                // Deduction pipeline: extraction (budgeted) → graph assembly → alias
                // resolution → detective synthesis per target → target suggestions.
                try
                {
                    string summary = await service.Extraction.RunPassAsync(ct);
                    LastRunStatus += $"; extraction: {summary}";
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Nightly extraction pass failed");
                }
                try
                {
                    string summary = await service.Graph.RunAsync(ct);
                    LastRunStatus += $"; graph: {summary}";
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Nightly graph assembly failed");
                }
                try
                {
                    await service.Aliases.RunAsync(ct);
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Nightly alias resolution failed");
                }
                int detectiveOk = 0;
                foreach (var pid in personIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (await service.Detective.RunForPersonAsync(pid, ct)) detectiveOk++; }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, $"Detective pass failed for {pid}"); }
                }
                LastRunStatus += $"; detective: {detectiveOk}/{personIds.Count}";
                try
                {
                    await service.TargetSuggestions.RunAsync(ct);
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Target suggestion engine failed");
                }
                try
                {
                    await service.IdentityLinks.RunAsync(ct);
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Identity-link engine failed");
                }
                try
                {
                    await service.Briefing.ComposeAndSendAsync(ct);
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "Daily briefing failed");
                }
                return true;
            }
            catch (Exception ex)
            {
                LastRunStatus = "error: " + ex.Message;
                FinishRun(LastRunStatus);
                _ = service.ServiceLogError(ex, "Omniscience nightly run failed");
                return false;
            }
            finally
            {
                LastRunFinishedAt = DateTime.UtcNow;
                runLock.Release();
            }
        }

        public async Task<(bool Accepted, string RunId, string Message)> StartPersonRecomputeAsync(string personId, CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct))
                return (false, currentRun?.RunId ?? "", "another recompute/profile run is already in progress");

            bool profileTargeted = IsProfileTarget(personId);
            string runId = StartRun("person_recompute", new List<string> { personId });
            _ = Task.Run(async () =>
            {
                try
                {
                    LastRunStartedAt = DateTime.UtcNow;
                    LastRunStatus = profileTargeted ? "running person recompute + dossier" : "running person analytics recompute";
                    SetItem(personId, analytics: "running", profile: profileTargeted ? "waiting" : "skipped_not_profile_target", error: null);

                    try
                    {
                        await analytics.RunForPersonAsync(personId, CancellationToken.None);
                        SetItem(personId, analytics: "ok", profile: profileTargeted ? "running" : "skipped_not_profile_target", error: null);
                    }
                    catch (Exception ex)
                    {
                        LastRunStatus = "analytics failed: " + ex.Message;
                        SetItem(personId, analytics: "failed", profile: profileTargeted ? "skipped" : "skipped_not_profile_target", error: ex.Message);
                        FinishRun(LastRunStatus);
                        _ = service.ServiceLogError(ex, $"Person analytics recompute failed for {personId}");
                        return;
                    }

                    if (profileTargeted)
                    {
                        try
                        {
                            bool ok = await profiler.GenerateForPersonAsync(personId, CancellationToken.None);
                            if (ok)
                            {
                                SetItem(personId, analytics: "ok", profile: "ok", error: null);
                                LastRunStatus = "ok (analytics ok, dossier ok)";
                            }
                            else
                            {
                                SetItem(personId, analytics: "ok", profile: "failed", error: "LLM returned no dossier");
                                MarkTargetStatus(personId, "failed: empty dossier");
                                LastRunStatus = "partial (analytics ok, dossier failed)";
                            }
                        }
                        catch (Exception ex)
                        {
                            SetItem(personId, analytics: "ok", profile: "failed", error: ex.Message);
                            MarkTargetStatus(personId, "failed: " + ex.Message);
                            LastRunStatus = "partial (analytics ok, dossier failed: " + ex.Message + ")";
                            _ = service.ServiceLogError(ex, $"Person profiling recompute failed for {personId}");
                        }
                    }
                    else
                    {
                        LastRunStatus = "ok (analytics ok, dossier skipped: not on profile list)";
                    }
                    FinishRun(LastRunStatus.StartsWith("ok", StringComparison.OrdinalIgnoreCase) ? "ok" : LastRunStatus);
                }
                finally
                {
                    LastRunFinishedAt = DateTime.UtcNow;
                    runLock.Release();
                }
            });

            return (true, runId, profileTargeted ? "queued analytics + dossier recompute" : "queued analytics recompute; dossier skipped because person is not on profile list");
        }

        public JObject BuildStatusJson()
        {
            JObject? job;
            lock (stateLock) job = currentRun?.ToJson();
            return new JObject(
                new JProperty("running", IsRunning),
                new JProperty("last_run_started_at", LastRunStartedAt?.ToString("o")),
                new JProperty("last_run_finished_at", LastRunFinishedAt?.ToString("o")),
                new JProperty("last_run_status", LastRunStatus),
                new JProperty("current_job", job)
            );
        }

        public List<string> GetProfileTargetIds()
        {
            var ids = new List<string>();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT person_id FROM person_profile_targets WHERE enabled=1 ORDER BY updated_at DESC";
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetString(0));
            }
            catch { }
            return ids;
        }

        public bool IsProfileTarget(string personId)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT enabled FROM person_profile_targets WHERE person_id=$p";
            cmd.Parameters.AddWithValue("$p", personId);
            try { return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) != 0; }
            catch { return false; }
        }

        public async Task SetProfileTargetAsync(string personId, bool enabled, CancellationToken ct)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await service.Db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO person_profile_targets(person_id, enabled, added_at, updated_at)
                    VALUES($p,$e,$ts,$ts)
                    ON CONFLICT(person_id) DO UPDATE SET enabled=excluded.enabled, updated_at=excluded.updated_at";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ts", ts);
                cmd.ExecuteNonQuery();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        public JArray GetProfileTargetsJson()
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT t.person_id, p.display_name, t.enabled, t.added_at, t.updated_at, t.last_profiled_at, t.last_profile_status,
                    (SELECT COUNT(*) FROM messages m JOIN platform_identities pi ON pi.identity_id=m.author_identity_id WHERE pi.person_id=t.person_id) AS msg_count
                FROM person_profile_targets t
                LEFT JOIN persons p ON p.person_id=t.person_id
                WHERE t.enabled=1 AND (p.merged_into_person_id IS NULL OR p.person_id IS NULL)
                ORDER BY t.updated_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject(
                    new JProperty("person_id", r.GetString(0)),
                    new JProperty("display_name", r.IsDBNull(1) ? "" : r.GetString(1)),
                    new JProperty("enabled", r.GetInt64(2) != 0),
                    new JProperty("added_at", r.GetInt64(3)),
                    new JProperty("updated_at", r.GetInt64(4)),
                    new JProperty("last_profiled_at", r.IsDBNull(5) ? null : r.GetInt64(5)),
                    new JProperty("last_profile_status", r.IsDBNull(6) ? null : r.GetString(6)),
                    new JProperty("message_count", r.IsDBNull(7) ? 0 : r.GetInt32(7))
                ));
            }
            return arr;
        }

        private string StartRun(string mode, List<string> personIds)
        {
            var run = new RunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                Mode = mode,
                StartedAt = DateTime.UtcNow,
                Status = "running",
                Items = personIds.Select(p => new RunItem { PersonId = p, AnalyticsStatus = "waiting", ProfileStatus = "waiting" }).ToList(),
            };
            lock (stateLock) currentRun = run;
            return run.RunId;
        }

        private void SetItem(string personId, string analytics, string profile, string? error)
        {
            lock (stateLock)
            {
                var item = currentRun?.Items.FirstOrDefault(i => i.PersonId == personId);
                if (item == null) return;
                item.AnalyticsStatus = analytics;
                item.ProfileStatus = profile;
                item.Error = error;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        private void FinishRun(string status)
        {
            lock (stateLock)
            {
                if (currentRun == null) return;
                currentRun.Status = status;
                currentRun.FinishedAt = DateTime.UtcNow;
            }
        }

        private void MarkTargetStatus(string personId, string status)
        {
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE person_profile_targets SET last_profile_status=$s, updated_at=$t WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private class RunState
        {
            public string RunId = "";
            public string Mode = "";
            public string Status = "running";
            public DateTime StartedAt;
            public DateTime? FinishedAt;
            public List<RunItem> Items = new();

            public JObject ToJson() => new(
                new JProperty("run_id", RunId),
                new JProperty("mode", Mode),
                new JProperty("status", Status),
                new JProperty("running", FinishedAt == null),
                new JProperty("started_at", StartedAt.ToString("o")),
                new JProperty("finished_at", FinishedAt?.ToString("o")),
                new JProperty("items", new JArray(Items.Select(i => i.ToJson()))),
                new JProperty("analytics_ok", Items.Count(i => i.AnalyticsStatus == "ok")),
                new JProperty("analytics_failed", Items.Count(i => i.AnalyticsStatus == "failed")),
                new JProperty("profiles_ok", Items.Count(i => i.ProfileStatus == "ok")),
                new JProperty("profiles_failed", Items.Count(i => i.ProfileStatus == "failed")),
                new JProperty("profiles_skipped", Items.Count(i => i.ProfileStatus.StartsWith("skipped", StringComparison.OrdinalIgnoreCase)))
            );
        }

        private class RunItem
        {
            public string PersonId = "";
            public string AnalyticsStatus = "waiting";
            public string ProfileStatus = "waiting";
            public string? Error;
            public DateTime UpdatedAt = DateTime.UtcNow;

            public JObject ToJson() => new(
                new JProperty("person_id", PersonId),
                new JProperty("analytics", AnalyticsStatus),
                new JProperty("profile", ProfileStatus),
                new JProperty("error", Error),
                new JProperty("updated_at", UpdatedAt.ToString("o"))
            );
        }
    }
}
