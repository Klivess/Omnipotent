using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Lowering the agent cap under a live roster. This used to be refused outright ("retire agents
    /// first"), which made the cap un-lowerable in exactly the situation it exists for: a task force
    /// that has grown too big or too expensive while everybody is mid-assignment.
    ///
    /// It now applies immediately, and the whole point of these tests is the other half of that —
    /// taking a slot away must never take the WORK away. Every piece the retirement path preserves is
    /// pinned here: which agents are chosen, that helpers are not orphaned, that the work snapshot is
    /// complete, that Klives' instructions survive the loss of their recipient, and that the
    /// milestones the retired agent held come back as unowned rather than looking staffed forever.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectAgentRetirementTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "khandover_" + Guid.NewGuid().ToString("N"));

        private static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

        private static ProjectAgentRecord Commander() => new()
        {
            AgentID = "commander", Role = "commander", WorkStatus = ProjectAgentWorkStatus.Running,
        };

        private static ProjectAgentRecord Worker(string id,
            ProjectAgentWorkStatus status = ProjectAgentWorkStatus.Running,
            ProjectAgentMissionKind mission = ProjectAgentMissionKind.Task,
            string? parent = "commander",
            string[]? milestones = null,
            DateTime? lastWakeAt = null,
            DateTime? lastReportAt = null,
            DateTime? createdAt = null) => new()
            {
                AgentID = id,
                Role = "role-" + id,
                ParentAgentID = parent,
                Objective = "do " + id,
                MissionKind = mission,
                WorkStatus = status,
                ActiveMilestoneIDs = (milestones ?? Array.Empty<string>()).ToList(),
                LastWakeAt = lastWakeAt ?? Now.AddMinutes(-5),
                LastReportAt = lastReportAt,
                CreatedAt = createdAt ?? Now.AddHours(-2),
            };

        // ── selection policy ──

        [Fact]
        public void SelectForCap_TakesFinishedWorkBeforeLiveWork()
        {
            var roster = new[]
            {
                Commander(),
                Worker("live", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }),
                // Completed a bounded deliverable and has been quiet past the reclaim grace period.
                Worker("done", ProjectAgentWorkStatus.Completed, lastReportAt: Now.AddHours(-1)),
            };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 2, nowUtc: Now);

            Assert.Single(picked);
            Assert.Equal("done", picked[0].AgentID);
        }

        [Fact]
        public void SelectForCap_TakesABoundedTaskBeforeAStandingBeat()
        {
            var roster = new[]
            {
                Commander(),
                Worker("beat", ProjectAgentWorkStatus.Running, ProjectAgentMissionKind.Standing),
                Worker("task", ProjectAgentWorkStatus.Running, ProjectAgentMissionKind.Task),
            };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 2, nowUtc: Now);

            // A task has a definition of done somebody else can finish; a standing beat simply stops
            // the moment nobody owns it, so it is the last slot to be taken.
            Assert.Equal("task", Assert.Single(picked).AgentID);
        }

        [Fact]
        public void SelectForCap_NeverRetiresTheCommander_AndACapOfOneLeavesItAlone()
        {
            var roster = new[] { Commander(), Worker("a"), Worker("b"), Worker("c"), Worker("d") };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 1, nowUtc: Now);

            Assert.Equal(4, picked.Count);
            Assert.DoesNotContain(picked, a => ProjectSubAgentManager.IsCommander(a));
        }

        [Fact]
        public void SelectForCap_PeelsHelpersFirstSoItNeverOvershootsTheCap()
        {
            // commander → parent → two helpers. Retiring `parent` directly would force its helpers out
            // with it and free three slots when one was asked for.
            var roster = new[]
            {
                Commander(),
                Worker("parent", ProjectAgentWorkStatus.Running, milestones: new[] { "m1" }),
                Worker("helper1", ProjectAgentWorkStatus.Running, parent: "parent"),
                Worker("helper2", ProjectAgentWorkStatus.Running, parent: "parent"),
            };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 3, nowUtc: Now);

            Assert.Single(picked);
            Assert.StartsWith("helper", picked[0].AgentID);
        }

        [Fact]
        public void SelectForCap_FreesExactlyTheSlotsAsked_EvenAcrossAnOrgTree()
        {
            var roster = new[]
            {
                Commander(),
                Worker("parent"),
                Worker("helper", parent: "parent"),
                Worker("solo"),
            };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 2, nowUtc: Now);
            var ids = picked.Select(a => a.AgentID).ToList();

            // Two slots asked for, two agents chosen — never three because a parent dragged its helper
            // along, and never an orphan left behind pointing at a retired parent.
            Assert.Equal(2, picked.Count);
            Assert.DoesNotContain("commander", ids);
            if (ids.Contains("parent"))
            {
                // The parent only becomes eligible once its helper has gone, so the guard against
                // retiring an agent that still has children is satisfied by the ORDER, not bypassed.
                Assert.Contains("helper", ids);
                Assert.True(ids.IndexOf("helper") < ids.IndexOf("parent"));
            }
        }

        [Fact]
        public void SelectForCap_PrefersTheNewestAgentAmongEquals()
        {
            var roster = new[]
            {
                Commander(),
                Worker("veteran", createdAt: Now.AddHours(-6)),
                Worker("fresh", createdAt: Now.AddMinutes(-2)),
            };

            var picked = ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 2, nowUtc: Now);

            // Unwinding a staffing decision made two minutes ago costs the project less than unwinding
            // one that has been running for six hours.
            Assert.Equal("fresh", Assert.Single(picked).AgentID);
        }

        [Fact]
        public void SelectForCap_IsAlreadySatisfiedWhenTheRosterFits()
        {
            var roster = new[] { Commander(), Worker("a") };
            Assert.Empty(ProjectAgentRetirementPolicy.SelectForCap(roster, cap: 5, nowUtc: Now));
        }

        [Fact]
        public void ExpandWithDescendants_DragsHelpersOutWithTheirParent_DeepestFirst()
        {
            var roster = new[]
            {
                Commander(),
                Worker("parent"),
                Worker("helper", parent: "parent"),
                Worker("bystander"),
            };

            var ordered = ProjectAgentRetirementPolicy.ExpandWithDescendants(roster, new[] { "parent" });

            // Deepest first: the helper must already be retired by the time its parent is, or the
            // "retire your children first" guard would reject the parent.
            Assert.Equal(new[] { "helper", "parent" }, ordered.Select(a => a.AgentID));
            Assert.DoesNotContain(ordered, a => a.AgentID == "bystander");
        }

        [Fact]
        public void ExpandWithDescendants_IgnoresTheCommanderAndUnknownIDs()
        {
            var roster = new[] { Commander(), Worker("a") };

            var ordered = ProjectAgentRetirementPolicy.ExpandWithDescendants(
                roster, new[] { "commander", "ghost", "a" });

            Assert.Equal("a", Assert.Single(ordered).AgentID);
        }

        // ── the handover record ──

        private ProjectAgentHandoverStore NewHandovers() => new(_ => { }, root);

        private static ProjectAgentHandover Handover(string agentID, string projectID = "p1",
            ProjectAgentRetirementReason reason = ProjectAgentRetirementReason.CapLowered) => new()
            {
                ProjectID = projectID,
                AgentID = agentID,
                Role = "scraper",
                MissionKind = ProjectAgentMissionKind.Task,
                WorkStatus = ProjectAgentWorkStatus.Running,
                Reason = reason,
                RetiredBy = "klives",
                Objective = "Scrape the supplier catalogue into a CSV",
                ActiveMilestoneIDs = { "m2" },
                DeliverablePaths = { "outputs/catalogue.csv" },
                ResumeAction = "Resume at supplier page 14 of 30; rows 1-13 are already written.",
                InterruptedMidWake = true,
            };

        [Fact]
        public void Handover_SurvivesAReload_WithTheWorkIntact()
        {
            var store = NewHandovers();
            var written = store.Record(Handover("w1"));

            var reloaded = new ProjectAgentHandoverStore(_ => { }, root).Get("p1", written.HandoverID);

            Assert.NotNull(reloaded);
            Assert.Equal("Scrape the supplier catalogue into a CSV", reloaded!.Objective);
            Assert.Equal("m2", Assert.Single(reloaded.ActiveMilestoneIDs));
            Assert.Equal("outputs/catalogue.csv", Assert.Single(reloaded.DeliverablePaths));
            Assert.Contains("page 14 of 30", reloaded.ResumeAction);
            Assert.True(reloaded.InterruptedMidWake);
            Assert.Equal(ProjectHandoverStatus.Open, reloaded.Status);
        }

        [Fact]
        public void HandoverBlock_NamesTheWorkAndTheExactNextAction()
        {
            var store = NewHandovers();
            store.Record(Handover("w1"));

            string block = store.DescribeForPrompt("p1");

            Assert.Contains("UNCLAIMED WORK FROM RETIRED AGENTS (1)", block);
            Assert.Contains("Klives lowered the agent cap and this slot was reclaimed", block);
            Assert.Contains("milestones now UNOWNED: m2", block);
            Assert.Contains("EXACT NEXT ACTION it had checkpointed", block);
            Assert.Contains("page 14 of 30", block);
            Assert.Contains("INTERRUPTED mid-wake", block);
            // The block must say the desktop is gone: the successor would otherwise assume the live
            // browser session it reads about in the log is still sitting there waiting.
            Assert.Contains("claim_handover", block);
        }

        [Fact]
        public void HandoverBlock_IsByteIdenticalAcrossRenders()
        {
            // Prefix-cache contract: this text rides the TASK FORCE block, so a relative age or a live
            // clock inside it would re-prefill everything behind the roster on every single wake.
            var store = NewHandovers();
            store.Record(Handover("w1"));

            string first = store.DescribeForPrompt("p1");
            Thread.Sleep(1_100);
            string second = store.DescribeForPrompt("p1");

            Assert.Equal(first, second);
        }

        [Fact]
        public void HandoverBlock_IsEmptyWhenNothingIsOutstanding()
        {
            var store = NewHandovers();
            var recorded = store.Record(Handover("w1"));
            store.Claim("p1", recorded.HandoverID, "commander", "Taking it onto my own step ledger.");

            Assert.Equal("", store.DescribeForPrompt("p1"));
        }

        [Fact]
        public void Claim_RecordsWhoTookTheWorkAndIsNotRepeatable()
        {
            var store = NewHandovers();
            var recorded = store.Record(Handover("w1"));

            var claimed = store.Claim("p1", recorded.HandoverID, "worker-9", "Reassigned to worker-9.");
            Assert.Equal(ProjectHandoverStatus.Claimed, claimed!.Status);
            Assert.Equal("worker-9", claimed.ClaimedBy);

            // A second claim must not overwrite the first — two agents both believing they own the
            // work is the failure mode a handover exists to prevent.
            var again = store.Claim("p1", recorded.HandoverID, "someone-else", "mine now", dropped: true);
            Assert.Equal(ProjectHandoverStatus.Claimed, again!.Status);
            Assert.Equal("worker-9", again.ClaimedBy);
        }

        [Fact]
        public void Drop_IsDistinguishableFromPickingTheWorkUp()
        {
            var store = NewHandovers();
            var recorded = store.Record(Handover("w1"));

            var dropped = store.Claim("p1", recorded.HandoverID, "commander",
                "Out of scope after the pivot; the catalogue is no longer needed.", dropped: true);

            Assert.Equal(ProjectHandoverStatus.Dropped, dropped!.Status);
            Assert.Empty(store.List("p1", openOnly: true));
        }

        [Fact]
        public void OpenHandovers_AreNeverEvictedByTheResolvedCap()
        {
            var store = NewHandovers();
            // Well past MaxResolvedPerProject, all resolved, plus one that nobody has dealt with.
            var stranded = store.Record(Handover("stranded"));
            for (int i = 0; i < ProjectAgentHandoverStore.MaxResolvedPerProject + 20; i++)
            {
                var h = store.Record(Handover("w" + i));
                store.Claim("p1", h.HandoverID, "commander", "handled");
            }

            var open = store.List("p1", openOnly: true);

            Assert.Equal(stranded.HandoverID, Assert.Single(open).HandoverID);
            Assert.True(store.List("p1").Count <= ProjectAgentHandoverStore.MaxResolvedPerProject + 1);
        }

        [Fact]
        public void Handovers_AreScopedToTheirOwnProject()
        {
            var store = NewHandovers();
            store.Record(Handover("w1", "p1"));
            store.Record(Handover("w2", "p2"));

            Assert.Equal("w1", Assert.Single(store.List("p1")).AgentID);
            Assert.Equal("w2", Assert.Single(store.List("p2")).AgentID);
        }

        // ── what the retired agent was carrying ──

        [Fact]
        public void RetireWithRecord_ReturnsTheWorkSnapshotTakenBeforeRetirement()
        {
            var projectStore = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = projectStore.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 4);
            var mgr = new ProjectSubAgentManager(projectStore, log);
            mgr.EnsureCommander(p.ProjectID);
            var worker = mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "scraper", "scrape the catalogue");
            mgr.SetActiveMilestones(p.ProjectID, worker.AgentID, new[] { "m2" });
            mgr.UpdateWorkState(p.ProjectID, worker.AgentID, ProjectAgentWorkStatus.Running,
                "halfway through page 14", new[] { "outputs/catalogue.csv" });

            var snapshot = mgr.RetireWithRecord(p.ProjectID, worker.AgentID, "klives", "Cap lowered.");

            Assert.NotNull(snapshot);
            Assert.False(snapshot!.Retired);                       // the state it was in, not the state it is in
            Assert.Equal("scrape the catalogue", snapshot.Objective);
            Assert.Equal("m2", Assert.Single(snapshot.ActiveMilestoneIDs));
            Assert.Equal("outputs/catalogue.csv", Assert.Single(snapshot.DeliverablePaths));
            Assert.Equal("halfway through page 14", snapshot.LastReport);
            // And it really is off the roster now.
            Assert.DoesNotContain(mgr.ListActive(p.ProjectID), a => a.AgentID == worker.AgentID);
            Assert.Null(mgr.Get(p.ProjectID, worker.AgentID));
        }

        [Fact]
        public void RetireWithRecord_RefusesAnAgentThatStillHasHelpers()
        {
            var projectStore = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = projectStore.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 6);
            var mgr = new ProjectSubAgentManager(projectStore, log);
            mgr.EnsureCommander(p.ProjectID);
            var parent = mgr.Spawn(p.ProjectID, "commander", ProjectAgentTier.Text, "lead");
            mgr.Spawn(p.ProjectID, parent.AgentID, ProjectAgentTier.Text, "helper");

            // The guard is what ExpandWithDescendants' deepest-first ordering exists to satisfy.
            Assert.Throws<InvalidOperationException>(
                () => mgr.RetireWithRecord(p.ProjectID, parent.AgentID, "klives"));
        }

        [Fact]
        public void RetireWithRecord_RefusesTheCommander()
        {
            var projectStore = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = projectStore.CreateProject("t", "goal", 100, 100, 10, subAgentCap: 2);
            var mgr = new ProjectSubAgentManager(projectStore, log);
            mgr.EnsureCommander(p.ProjectID);

            Assert.Throws<InvalidOperationException>(
                () => mgr.RetireWithRecord(p.ProjectID, "commander", "klives"));
        }

        [Fact]
        public void KlivesInstructionsSurviveTheLossOfTheAgentCarryingThem()
        {
            var directives = new ProjectDirectiveStore(_ => { }, root);
            string pid = "p_" + Guid.NewGuid().ToString("N")[..8];
            var task = directives.Create(pid, "Get the catalogue into a CSV", ProjectDirectiveKind.Task,
                ProjectDirectiveScope.SpecificAgents, new[] { "w1" }, new[] { "outputs/catalogue.csv" });
            directives.MarkDelivered(pid, task.DirectiveID, "w1", "wake-1");
            directives.Acknowledge(pid, task.DirectiveID, "w1", "on it");

            var forAgent = directives.ListAgentScoped(pid, "w1");
            Assert.Equal(task.DirectiveID, Assert.Single(forAgent).DirectiveID);

            var moved = directives.ReassignToCommander(pid, task.DirectiveID);

            // Same durable instruction — same ID, same required deliverable — now addressed to the
            // Commander, and reset to Active so it is actually re-delivered rather than being
            // filtered out of every future seed as "already acknowledged" by an agent that is gone.
            Assert.Equal(task.DirectiveID, moved!.DirectiveID);
            Assert.Equal(ProjectDirectiveScope.Commander, moved.Scope);
            Assert.Empty(moved.TargetAgentIDs);
            Assert.Equal(ProjectDirectiveStatus.Active, moved.Status);
            Assert.Null(moved.AcknowledgedBy);
            Assert.Equal("outputs/catalogue.csv", Assert.Single(moved.ExpectedArtifactPaths));
            Assert.True(ProjectDirectiveStore.AppliesTo(moved, "commander"));
            Assert.False(ProjectDirectiveStore.AppliesTo(moved, "w1"));
        }

        [Fact]
        public void ProjectWideRulesAreNotNarrowedToTheCommanderWhenAnAgentGoes()
        {
            var directives = new ProjectDirectiveStore(_ => { }, root);
            string pid = "p_" + Guid.NewGuid().ToString("N")[..8];
            directives.Create(pid, "Never spend money without asking.", ProjectDirectiveKind.Rule,
                ProjectDirectiveScope.AllAgents);

            // A standing rule belongs to every future agent too; only agent-addressed work moves.
            Assert.Empty(directives.ListAgentScoped(pid, "w1"));
        }

        [Fact]
        public void ARosterChangeNoticeWouldBeMisfiledAsAPermanentRule()
        {
            // This is why the Commander notification is sent with allowRulePromotion:false. The notice
            // is constraint-shaped English, and the auto-promoter reads constraint grammar as a
            // STANDING RULE — which is injected into every future wake forever and has to be revoked
            // by hand. A one-off "these agents are gone" must not become project doctrine.
            const string notice =
                "ROSTER CHANGE — 1 agent(s) were retired by klives and are gone from your task force.\n"
                + "· scraper (abc123) — Klives removed this agent directly. Nothing outstanding.\n"
                + "Your cap has not changed by accident — do not spawn replacements above it, "
                + "and do not ask Klives to undo it.";

            Assert.Equal(ProjectDirectiveKind.Rule,
                ProjectDirectiveClassifier.ClassifyStandingConstraint(notice));
        }

        [Fact]
        public void MilestonesHeldByARetiredAgentComeBackAsUnowned()
        {
            var plans = new ProjectGrandPlanStore(_ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            plans.SubmitVersion(pid, new GrandPlanContent
            {
                Mission = "Ship it",
                Milestones =
                {
                    new PlanMilestone { ID = "m1", Title = "Scrape", Status = MilestoneStatus.InProgress, OwnerAgentID = "w1" },
                    new PlanMilestone { ID = "m2", Title = "Write", Status = MilestoneStatus.Pending, OwnerAgentID = "w2" },
                },
            }, "plan", null, material: true, "wake1");
            plans.MarkApproved(pid, 1, "gate1", null);

            var released = plans.ReleaseOwnership(pid, "w1");

            Assert.Equal("m1", Assert.Single(released));
            var current = plans.GetCurrentApproved(pid)!.Content!;
            Assert.Null(current.Milestones.First(m => m.ID == "m1").OwnerAgentID);
            // Untouched: another agent's ownership is not collateral damage.
            Assert.Equal("w2", current.Milestones.First(m => m.ID == "m2").OwnerAgentID);
            // Status is deliberately left alone — the work genuinely is in progress, it just has
            // nobody on it, and rewinding it to Pending would throw that progress away.
            Assert.Equal(MilestoneStatus.InProgress, current.Milestones.First(m => m.ID == "m1").Status);
        }

        [Fact]
        public void AReleasedMilestoneIsSurfacedAsUnstaffedReadyWork()
        {
            var plan = new GrandPlanContent
            {
                Mission = "Ship it",
                Milestones = { new PlanMilestone { ID = "m1", Title = "Scrape" } },
            };
            var rosterAfterRetirement = new[] { Commander() };

            var unstaffed = ProjectStaffing.UnstaffedReady(plan.Milestones, rosterAfterRetirement);

            // This is the closing of the loop: the freed slot and the now-unowned milestone are what
            // drive the Commander's staffing checkpoint to re-staff the work on its very next wake.
            Assert.Equal("m1", Assert.Single(unstaffed).ID);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            GC.SuppressFinalize(this);
        }
    }
}
