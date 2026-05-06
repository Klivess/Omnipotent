using Omnipotent.Service_Manager;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Scheduling
{
    /// <summary>
    /// Drives nightly + on-demand "recompute analytics → regenerate personality dossier" runs.
    /// Holds a single semaphore so manual + scheduled runs cannot overlap.
    /// </summary>
    public class OmniscienceScheduler
    {
        private readonly Omniscience service;
        private readonly Analytics.AnalyticsEngine analytics;
        private readonly Profiling.PersonalityProfiler profiler;
        private readonly SemaphoreSlim runLock = new(1, 1);

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
            // First run: tomorrow at 03:30 local. The TimeManager's TaskDue event fires
            // globally; we filter by agentName == this.service.name.
            var due = DateTime.Today.AddDays(1).AddHours(3).AddMinutes(30);
            _ = service.ServiceCreateScheduledTask(due, "OmniscienceNightly", "analytics", "Nightly recompute + personality refresh", true);
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
                _ = service.ServiceCreateScheduledTask(due, "OmniscienceNightly", "analytics", "Nightly recompute + personality refresh", true);
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
                LastRunStartedAt = DateTime.UtcNow;
                LastRunStatus = "running";
                _ = service.ServiceLog("Omniscience: starting analytics + profile pass.");

                var personIds = analytics.GetAllPersonIds();
                int profilesOk = 0, profilesFail = 0;
                foreach (var pid in personIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await analytics.RunForPersonAsync(pid, ct); }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, $"Analytics failed for {pid}"); }
                    try
                    {
                        bool ok = await profiler.GenerateForPersonAsync(pid, ct);
                        if (ok) profilesOk++; else profilesFail++;
                    }
                    catch (Exception ex)
                    {
                        profilesFail++;
                        _ = service.ServiceLogError(ex, $"Profiling failed for {pid}");
                    }
                }
                LastRunStatus = $"ok ({personIds.Count} persons, {profilesOk} profiles, {profilesFail} failed)";
                _ = service.ServiceLog($"Omniscience nightly complete. {LastRunStatus}");
                return true;
            }
            catch (Exception ex)
            {
                LastRunStatus = "error: " + ex.Message;
                _ = service.ServiceLogError(ex, "Omniscience nightly run failed");
                return false;
            }
            finally
            {
                LastRunFinishedAt = DateTime.UtcNow;
                runLock.Release();
            }
        }
    }
}
