using Newtonsoft.Json;
using Omnipotent.Services.Projects;
using ProjectsService = Omnipotent.Services.Projects.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// A halted fleet must hold wakes, not start them. 2026-10-23..28 the prompt-cache halt left two Active
    /// projects in a loop: each wake built its full brief (~11 s), deferred at the first turn, released its
    /// trigger (a two-week-old message from Klives, which bypasses every backoff) and was re-woken on it at
    /// once — 65,347 deferred wakes in seven days, none productive, each posting "Wake stopped" to Discord.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectFleetHaltWakeTests : IDisposable
    {
        private readonly string haltDirectory = Path.Combine(Path.GetTempPath(), "fleet-halt-wake-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(haltDirectory)) Directory.Delete(haltDirectory, recursive: true); } catch { }
        }

        private (ProjectsService Svc, string ProjectID) NewService(bool halted)
        {
            var svc = new ProjectsService();
            void Set(string name, object value) => typeof(ProjectsService).GetProperty(name)!.SetValue(svc, value);
            Set(nameof(ProjectsService.Store), new ProjectStore(_ => { }));
            Set(nameof(ProjectsService.EventLog), new ProjectEventLogStore(_ => { }));
            Set(nameof(ProjectsService.Digests), new ProjectDigestStore(_ => { }));
            Set(nameof(ProjectsService.RuntimeState), new ProjectRuntimeStateStore(_ => { }));
            Set(nameof(ProjectsService.Settings), new ProjectSettingsStore());
            Set(nameof(ProjectsService.Activity), new ProjectAgentActivityTracker());
            Set(nameof(ProjectsService.Gates), new ProjectGateManager(svc.EventLog, _ => { }));
            Set(nameof(ProjectsService.SubAgents), new ProjectSubAgentManager(svc.Store, svc.EventLog));
            Set(nameof(ProjectsService.WakeAdmission), new ProjectWakeAdmission(() => 1));
            Set(nameof(ProjectsService.CommanderRunner), new ProjectCommanderRunner(svc));
            Set(nameof(ProjectsService.SubAgentRunner), new ProjectSubAgentRunner(svc));

            // The latch as the live server held it: engaged and persisted, so it is restored at construction.
            Directory.CreateDirectory(haltDirectory);
            File.WriteAllText(Path.Combine(haltDirectory, "cache-halt.json"), JsonConvert.SerializeObject(new ProjectCacheHaltState
            {
                Engaged = halted,
                EngagedAt = new DateTime(2026, 10, 21, 19, 47, 6, DateTimeKind.Utc),
                ObservedHitRatePct = 57.3,
                ThresholdPct = 80,
                WindowMinutes = 60,
            }));
            Set(nameof(ProjectsService.CacheHealth), new ProjectCacheHealthMonitor(haltDirectory));

            var project = svc.Store.CreateProject("Run a business", "Win the first five customers", 1, 0, 0, 3);
            project.Status = ProjectStatus.Active;
            svc.Store.SaveProject(project);
            return (svc, project.ProjectID);
        }

        [Fact]
        public void HaltedFleet_HoldsTheCommander_InsteadOfStartingADoomedWake()
        {
            var (svc, pid) = NewService(halted: true);
            var project = svc.Store.GetProject(pid)!;
            const string trigger = "Message from Klives: [sent 2026-10-15 17:50:16 UTC] [directive:abc] hows it going?";

            // The replayed trigger arrives again and again (drain, keepalive, watchdog); none may start a wake.
            Assert.Null(svc.CommanderRunner.Wake(project, trigger));
            Assert.Null(svc.CommanderRunner.Wake(project, trigger));
            Assert.Null(svc.CommanderRunner.Wake(project, trigger));

            var events = svc.EventLog.ReadTail(pid, 50);
            Assert.DoesNotContain(events, e => e.Type == ProjectEventTypes.CommanderWake);
            Assert.DoesNotContain(events, e => e.Type == ProjectEventTypes.WakeDeferred);
            var note = Assert.Single(events, e => e.Type == ProjectEventTypes.WakeWaiting);
            Assert.Contains("prompt-cache health", note.Text, StringComparison.Ordinal);
            Assert.Contains("fleet-halt", note.PayloadJson ?? "", StringComparison.Ordinal);

            // Klives' message is kept exactly once for the wake that runs after the halt is cleared.
            Assert.Single(svc.RuntimeState.ListPendingTriggers(pid), t => t.Payload == trigger);
            Assert.Null(svc.RuntimeState.Get(pid).ActiveWakeLease);
            Assert.True(string.IsNullOrEmpty(svc.Digests.GetDigest(pid).ActiveWakeID));
        }

        [Fact]
        public void HaltedFleet_HoldsWorkers_AndRetainsTheirTrigger()
        {
            var (svc, pid) = NewService(halted: true);
            var project = svc.Store.GetProject(pid)!;
            svc.SubAgents.EnsureCommander(pid);
            var worker = svc.SubAgents.Spawn(pid, "commander", ProjectAgentTier.Text, "researcher", "Find five prospects.");

            Assert.Null(svc.SubAgentRunner.Wake(project, worker, "Heartbeat: report progress."));

            var events = svc.EventLog.ReadTail(pid, 50);
            Assert.DoesNotContain(events, e => e.Type == ProjectEventTypes.AgentWake);
            Assert.Contains(events, e => e.Type == ProjectEventTypes.WakeWaiting && e.AgentID == worker.AgentID);
            Assert.False(svc.SubAgentRunner.HasLiveWake(pid));
        }

        [Fact]
        public void ClearedHalt_NoLongerRefusesAdmission()
        {
            var (svc, _) = NewService(halted: true);
            Assert.NotNull(svc.FleetAdmissionRefusal());
            Assert.True(svc.CacheHealth.Clear("klives"));
            Assert.Null(svc.FleetAdmissionRefusal());
        }

        [Fact]
        public void RepeatedTroubleClosing_IsSentOncePerWindow()
        {
            var (svc, pid) = NewService(halted: false);
            var now = new DateTime(2026, 10, 28, 19, 39, 0, DateTimeKind.Utc);
            const string warning = "⚠️ Wake stopped before the next model call because the Projects fleet is halted.";

            Assert.False(svc.CommanderRunner.RepeatsRecentTroubleClosing(pid, warning, now));
            Assert.True(svc.CommanderRunner.RepeatsRecentTroubleClosing(pid, warning, now.AddSeconds(11)));
            Assert.True(svc.CommanderRunner.RepeatsRecentTroubleClosing(pid, warning, now.AddMinutes(29)));
            // A different problem is news straight away; the same one is repeated once the window has passed.
            Assert.False(svc.CommanderRunner.RepeatsRecentTroubleClosing(pid, "⚠️ Wake deferred by provider failure.", now.AddMinutes(1)));
            Assert.False(svc.CommanderRunner.RepeatsRecentTroubleClosing(pid, warning, now.AddMinutes(32)));
        }
    }
}
