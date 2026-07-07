using Omnipotent.Services.Projects;
using ProjectsService = Omnipotent.Services.Projects.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The watchdog's Diagnose() is pure over the event log + digest, so it's tested directly by
    /// seeding a project's log. Escalation/force-wake I/O (Discord, LLM) is covered by the
    /// build + the ForceWake unit below; the timing loop itself isn't unit-tested.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectWatchdogTests
    {
        private static (ProjectsService svc, string pid) NewProjectService()
        {
            // A Projects service far enough constructed for the watchdog's read-only diagnosis:
            // the watchdog only touches Store, EventLog and Digests.
            var svc = new ProjectsService();
            // ServiceMain isn't run in tests (it needs the whole service manager); build the
            // subsystems the watchdog reads, mirroring ServiceMain's first lines.
            typeof(ProjectsService).GetProperty(nameof(ProjectsService.Store))!.SetValue(svc, new ProjectStore(_ => { }));
            typeof(ProjectsService).GetProperty(nameof(ProjectsService.EventLog))!.SetValue(svc, new ProjectEventLogStore(_ => { }));
            typeof(ProjectsService).GetProperty(nameof(ProjectsService.Digests))!.SetValue(svc, new ProjectDigestStore(_ => { }));
            var p = svc.Store.CreateProject("t", "goal", 100, 100, 10, 5);
            return (svc, p.ProjectID);
        }

        private static void Wake(ProjectsService svc, string pid, DateTime when)
        {
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderWake, Author = "system", Text = "wake", Timestamp = when });
        }

        [Fact]
        public void FreshProject_WithNoHistory_IsNotStalled()
        {
            var (svc, pid) = NewProjectService();
            var wd = new ProjectWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
        }

        [Fact]
        public void FreshProject_WithInitEventButNoWakeYet_IsNotStalled()
        {
            // The create route logs a "Project initialised" event immediately, so the tail is
            // non-empty before the first wake — that must not read as "stalled (last: never)".
            var (svc, pid) = NewProjectService();
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Author = "klives", Text = "Project initialised." });
            var wd = new ProjectWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
        }

        [Fact]
        public void NeverWokenProject_OlderThanWakeGap_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewProjectService();
            var p = svc.Store.GetProject(pid)!;
            p.CreatedAt = DateTime.UtcNow.AddHours(-1);
            svc.Store.SaveProject(p);
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Author = "klives", Text = "Project initialised." });
            var wd = new ProjectWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("never", diag);
        }

        [Fact]
        public void RecentWakeWithProgress_IsHealthy()
        {
            var (svc, pid) = NewProjectService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-2));
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.ToolCall, Author = "commander", Text = "did work" });
            var wd = new ProjectWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
        }

        [Fact]
        public void StaleHeartbeat_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewProjectService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2)); // last wake long ago
            var wd = new ProjectWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("No Commander wake", diag);
        }

        [Fact]
        public void ThreeWakesNoProgress_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewProjectService();
            // Three recent wakes, no tool-call/artifact/spawn between them.
            for (int i = 0; i < 3; i++)
                Wake(svc, pid, DateTime.UtcNow.AddMinutes(-3 + i));
            var wd = new ProjectWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("No progress", diag);
        }

        [Fact]
        public void StuckLoopTrips_AreDiagnosedAsStall()
        {
            var (svc, pid) = NewProjectService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-1));
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.ToolCall, Author = "commander", Text = "x" });
            var digest = svc.Digests.GetDigest(pid);
            digest.RecentStuckLoopTrips = 4;
            svc.Digests.SaveDigest(digest);
            var wd = new ProjectWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("stuck-loop", diag);
        }
    }
}
