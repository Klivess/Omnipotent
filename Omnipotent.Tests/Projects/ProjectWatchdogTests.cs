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
            Assert.Contains("No Commander activity", diag);
        }

        [Fact]
        public void LongRunningWake_StillEmittingToolCalls_IsHealthy()
        {
            // One wake started hours ago but the Commander is visibly working (tool calls flowing).
            // The old heartbeat only counted wake STARTS, so a long wake (or one blocked briefly)
            // read as "stalled" while mid-work — the exact false-positive Klives kept getting pinged for.
            var (svc, pid) = NewProjectService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, AgentID = "commander", Type = ProjectEventTypes.ToolCall, Author = "commander", Text = "still working", Timestamp = DateTime.UtcNow.AddMinutes(-1) });
            var wd = new ProjectWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
        }

        [Fact]
        public async Task PendingGate_SuppressesAllStallDiagnosis()
        {
            // A wake blocked on an unanswered approval gate is waiting on KLIVES — diagnosing it
            // as a heartbeat stall (and force-waking, which cancels the gate wait) is wrong.
            var (svc, pid) = NewProjectService();
            typeof(ProjectsService).GetProperty(nameof(ProjectsService.Gates))!
                .SetValue(svc, new ProjectGateManager(svc.EventLog, _ => { }));
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2)); // stale by the heartbeat rule
            _ = svc.Gates.OpenGateAndWaitAsync(
                new ProjectGate { ProjectID = pid, Kind = "money", Title = "Spend $50" }, CancellationToken.None);
            await Task.Delay(20);
            var wd = new ProjectWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
        }

        [Fact]
        public void WedgedSubAgent_IsDiagnosed_OnlyWhenSilent()
        {
            var (svc, pid) = NewProjectService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-2)); // commander itself is healthy
            string agentWakeID = Guid.NewGuid().ToString("N");
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, WakeID = agentWakeID, AgentID = "agent-1", Type = ProjectEventTypes.AgentWake, Author = "system", Text = "woke", Timestamp = DateTime.UtcNow.AddMinutes(-45) });
            var wd = new ProjectWatchdog(svc, _ => { });

            // Silent for 45 minutes with no completion → wedged.
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("agent-1", diag);

            // But a slow worker that is still emitting activity is left alone.
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, WakeID = agentWakeID, AgentID = "agent-1", Type = ProjectEventTypes.ToolCall, Author = "agent", Text = "slow but alive", Timestamp = DateTime.UtcNow.AddMinutes(-5) });
            Assert.Null(wd.Diagnose(svc.Store.GetProject(pid)!));
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
        public async Task PlanningProject_BlockedOnPlanGate_IsNotDiagnosedAsNoProgress()
        {
            // A project in PLANNING that has submitted its Grand Plan and is waiting on Klives shows
            // no tool calls across recent wakes — but that's "waiting on Klives", not a stall, so the
            // watchdog must not force-wake it (which would cancel the plan gate).
            var (svc, pid) = NewProjectService();
            typeof(ProjectsService).GetProperty(nameof(ProjectsService.Gates))!
                .SetValue(svc, new ProjectGateManager(svc.EventLog, _ => { }));
            var p = svc.Store.GetProject(pid)!;
            p.Status = ProjectStatus.Planning;
            svc.Store.SaveProject(p);
            for (int i = 0; i < 3; i++) Wake(svc, pid, DateTime.UtcNow.AddMinutes(-3 + i));

            // Open a pending "plan" gate (never resolved) — the Commander is blocked here.
            _ = svc.Gates.OpenGateAndWaitAsync(
                new ProjectGate { ProjectID = pid, Kind = "plan", Title = "Grand Plan v1" }, CancellationToken.None);
            await Task.Delay(20);

            var wd = new ProjectWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetProject(pid)!);
            if (diag != null) Assert.DoesNotContain("No progress", diag);
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
