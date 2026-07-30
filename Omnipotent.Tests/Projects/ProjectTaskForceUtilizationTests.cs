using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The staffing half of the parallelism work: a Commander must be told, every wake, that it has
    /// free slots and unowned ready work. The predecessor logic fired only while the roster held one
    /// agent — and the roster counts the Commander — so a project got exactly one delegation nudge in
    /// its life and then ran serially forever. These tests pin the "keeps firing" property.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectTaskForceUtilizationTests
    {
        private static Project ActiveProject(int cap) =>
            new() { ProjectID = "p1", Name = "t", Goal = "g", Status = ProjectStatus.Active, SubAgentCap = cap };

        private static GrandPlanContent SeparablePlan() => new()
        {
            Mission = "m",
            Milestones =
            {
                new PlanMilestone { ID = "m1", Title = "Build the thing" },
                new PlanMilestone { ID = "m2", Title = "Ship the thing" },
            },
        };

        private static ProjectAgentRecord Commander() => new()
        {
            AgentID = "commander", Role = "commander", WorkStatus = ProjectAgentWorkStatus.Running,
        };

        private static ProjectAgentRecord Worker(string id, ProjectAgentWorkStatus status,
            ProjectAgentMissionKind mission = ProjectAgentMissionKind.Task,
            string[]? milestones = null, DateTime? lastReportAt = null) => new()
            {
                AgentID = id, Role = "worker-" + id, ParentAgentID = "commander",
                Objective = "do the thing", MissionKind = mission, WorkStatus = status,
                ActiveMilestoneIDs = (milestones ?? Array.Empty<string>()).ToList(),
                LastReportAt = lastReportAt,
                CreatedAt = DateTime.UtcNow.AddHours(-3),
            };

        [Fact]
        public void Checkpoint_KeepsFiring_AfterTheFirstWorkerIsSpawned()
        {
            var now = DateTime.UtcNow;
            var plan = SeparablePlan();
            // One worker already owns m1 — the old `roster <= 1` gate went permanently silent here.
            var roster = new[] { Commander(), Worker("w1", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }) };

            string? msg = ProjectStaffing.ComposeCheckpoint(
                ActiveProject(cap: 12), plan, plan.Milestones, roster, now);

            Assert.NotNull(msg);
            Assert.Contains("2 of 12 agent slots in use, 10 free", msg);
            Assert.Contains("m2", msg);          // the unowned milestone is named
            Assert.DoesNotContain("m1 Build", msg); // the owned one is not
        }

        [Fact]
        public void Checkpoint_IsSilent_WhenEveryReadyMilestoneHasAnOwner()
        {
            var now = DateTime.UtcNow;
            var plan = SeparablePlan();
            var roster = new[]
            {
                Commander(),
                Worker("w1", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }),
                Worker("w2", ProjectAgentWorkStatus.Running, milestones: new[] { "m2" }),
            };

            Assert.Null(ProjectStaffing.ComposeCheckpoint(
                ActiveProject(cap: 12), plan, plan.Milestones, roster, now));
        }

        [Fact]
        public void Checkpoint_IsSilent_WhenTheWorkIsNotSeparable()
        {
            var now = DateTime.UtcNow;
            var plan = new GrandPlanContent
            {
                Mission = "m",
                Milestones = { new PlanMilestone { ID = "m1", Title = "One indivisible step" } },
            };

            Assert.Null(ProjectStaffing.ComposeCheckpoint(
                ActiveProject(cap: 12), plan, plan.Milestones, new[] { Commander() }, now));
        }

        [Fact]
        public void Checkpoint_TellsCommanderToRetireFirst_WhenTheRosterIsFull()
        {
            var now = DateTime.UtcNow;
            var plan = SeparablePlan();
            // Cap 3: commander + a live worker + a finished one, quiet long enough to be reclaimable.
            var roster = new[]
            {
                Commander(),
                Worker("w1", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }),
                Worker("done1", ProjectAgentWorkStatus.Completed, lastReportAt: now.AddHours(-1)),
            };

            string? msg = ProjectStaffing.ComposeCheckpoint(
                ActiveProject(cap: 3), plan, plan.Milestones, roster, now);

            Assert.NotNull(msg);
            Assert.Contains("0 free", msg);
            Assert.Contains("retire done1", msg);
        }

        [Fact]
        public void Checkpoint_EscalatesOnlyAfterRepeatedUnderStaffedWakes()
        {
            var now = DateTime.UtcNow;
            var plan = SeparablePlan();
            var roster = new[] { Commander() };
            var project = ActiveProject(cap: 12);

            string? first = ProjectStaffing.ComposeCheckpoint(project, plan, plan.Milestones, roster, now, 0);
            string? third = ProjectStaffing.ComposeCheckpoint(project, plan, plan.Milestones, roster, now, 2);

            Assert.NotNull(first);
            Assert.NotNull(third);
            Assert.DoesNotContain("consecutive wake", first);
            Assert.Contains("3rd consecutive wake", third);
        }

        [Fact]
        public void UnderStaffed_RequiresBothFreeCapacityAndUnownedWork()
        {
            var plan = SeparablePlan();
            var full = new[]
            {
                Commander(),
                Worker("w1", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }),
                Worker("w2", ProjectAgentWorkStatus.Running, milestones: new[] { "m2" }),
            };
            // Free slots but everything owned → not under-staffed.
            Assert.False(ProjectStaffing.IsUnderStaffed(ActiveProject(cap: 12), plan.Milestones, full));
            // No free slots but work unowned → not under-staffed either (retirement, not spawning, is the fix).
            Assert.False(ProjectStaffing.IsUnderStaffed(ActiveProject(cap: 2), plan.Milestones, new[] { Commander(), Worker("w1", ProjectAgentWorkStatus.Idle) }));
            // Free slots AND unowned work → under-staffed.
            Assert.True(ProjectStaffing.IsUnderStaffed(ActiveProject(cap: 12), plan.Milestones, new[] { Commander() }));
        }

        [Fact]
        public void UnstaffedReady_IgnoresOwnersWhoAreNoLongerOnTheRoster()
        {
            var plan = SeparablePlan();
            plan.Milestones[0].OwnerAgentID = "retired-worker";
            var roster = new[] { Commander() };

            var unstaffed = ProjectStaffing.UnstaffedReady(plan.Milestones, roster);

            // A milestone owned by an agent that no longer exists is unowned work, not staffed work.
            Assert.Equal(2, unstaffed.Count);
        }

        [Fact]
        public void TaskForceBlock_ReportsSlotArithmeticAndNamesReclaimableAgents()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 12);
            var mgr = new ProjectSubAgentManager(store, log);
            mgr.EnsureCommander(p.ProjectID);
            var finished = mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "scraper");
            mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "writer");
            mgr.UpdateWorkState(p.ProjectID, finished.AgentID, ProjectAgentWorkStatus.Completed, "delivered the CSV");

            // Ten minutes on, the finished bounded worker is advertised as reclaimable.
            string block = mgr.DescribeTaskForce(p.ProjectID, 12, nowUtc: DateTime.UtcNow.AddMinutes(11));

            Assert.Contains("SLOTS: 3 of 12 used · 9 free", block);
            Assert.Contains("1 reclaimable", block);
            Assert.Contains(finished.AgentID, block);
            Assert.Contains("delivered the CSV", block);
            Assert.Contains("mission=task", block);
        }

        [Fact]
        public void TaskForceBlock_DoesNotAdvertiseAStandingAgentAsReclaimable()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 12);
            var mgr = new ProjectSubAgentManager(store, log);
            mgr.EnsureCommander(p.ProjectID);
            var beat = mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "poster",
                "run the posting queue", ProjectAgentMissionKind.Standing);
            mgr.UpdateWorkState(p.ProjectID, beat.AgentID, ProjectAgentWorkStatus.Completed, "cycle done");

            string block = mgr.DescribeTaskForce(p.ProjectID, 12, nowUtc: DateTime.UtcNow.AddHours(4));

            // A standing mission is only ever closed by the Commander, so it must never be offered up
            // for reclamation just because one cycle reported complete.
            Assert.DoesNotContain("reclaimable", block);
            Assert.Contains("mission=standing", block);
        }

        [Fact]
        public void CapError_IsActionableRatherThanADeadEnd()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 2);
            var mgr = new ProjectSubAgentManager(store, log);
            mgr.EnsureCommander(p.ProjectID);
            var busy = mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "scraper");
            mgr.UpdateWorkState(p.ProjectID, busy.AgentID, ProjectAgentWorkStatus.Running, "working");

            var ex = Assert.Throws<InvalidOperationException>(
                () => mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "another"));

            // Nothing is reclaimable here, so the message must still point at the way out rather than
            // leaving the Commander to conclude the roster simply cannot grow.
            Assert.Contains("cap reached", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2 of 2", ex.Message);
            Assert.Contains("holding live work", ex.Message);
            Assert.Contains("request_budget_increase", ex.Message);
        }

        [Fact]
        public void Reclaimable_RequiresABoundedMission_ThatIsDone_AndPastTheQuietPeriod()
        {
            var now = DateTime.UtcNow;

            // Delivered a bounded task and gone quiet → reclaimable.
            Assert.True(ProjectSubAgentManager.IsReclaimable(
                Worker("a", ProjectAgentWorkStatus.Completed, lastReportAt: now.AddMinutes(-30)), now));

            // Reported seconds ago → the Commander gets a wake to use the result before being told to
            // retire its author.
            Assert.False(ProjectSubAgentManager.IsReclaimable(
                Worker("b", ProjectAgentWorkStatus.Completed, lastReportAt: now.AddMinutes(-1)), now));

            // Still working → never reclaimable.
            Assert.False(ProjectSubAgentManager.IsReclaimable(
                Worker("c", ProjectAgentWorkStatus.Running, lastReportAt: now.AddHours(-4)), now));

            // A standing beat is the Commander's to close, however long it has been quiet.
            Assert.False(ProjectSubAgentManager.IsReclaimable(
                Worker("d", ProjectAgentWorkStatus.Completed, ProjectAgentMissionKind.Standing, lastReportAt: now.AddHours(-4)), now));

            // Still owns a milestone → not free, whatever its status says.
            Assert.False(ProjectSubAgentManager.IsReclaimable(
                Worker("e", ProjectAgentWorkStatus.Completed, milestones: new[] { "m1" }, lastReportAt: now.AddHours(-4)), now));

            // The Commander is never reclaimable.
            Assert.False(ProjectSubAgentManager.IsReclaimable(Commander(), now));
        }

        [Fact]
        public void NewProjectsGetARosterWorthParallelising()
        {
            // The cap counts the Commander, so 12 is eleven workers. A default of 5 (four workers)
            // was itself a throughput ceiling regardless of how well the Commander delegated.
            Assert.Equal(12, new Project().SubAgentCap);
        }
    }
}
