using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The Commander's strategy tools: submit/amend the Grand Plan (approval-gated) and the
    /// Planning-phase execution lockout. Exercises ProjectCommanderTools.DispatchAsync directly
    /// against real stores, resolving gates the way the website/Discord surfaces do.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectStrategyToolTests
    {
        private sealed class Setup
        {
            public ProjectStore Store = new(_ => { });
            public ProjectEventLogStore Log = new(_ => { });
            public ProjectGateManager Gates = null!;
            public ProjectGrandPlanStore GrandPlans = new(_ => { });
            public Project Project = null!;
            public ProjectCommanderTools Tools = null!;
            public bool Activated;
        }

        private static Setup NewSetup(ProjectStatus status = ProjectStatus.Planning)
        {
            var s = new Setup();
            var p = s.Store.CreateProject("t", "goal", 100, 100, 10, 5);
            p.Status = status;
            s.Store.SaveProject(p);
            s.Project = p;
            s.Gates = new ProjectGateManager(s.Log, _ => { });
            var digests = new ProjectDigestStore(_ => { });
            var subAgents = new ProjectSubAgentManager(s.Store, s.Log);
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(s.Store, s.Log, fetcher, _ => { });
            var vault = new ProjectVault(_ => { });
            s.Tools = new ProjectCommanderTools(p, s.Log, digests, subAgents, s.Gates, budget, vault, s.Store, "commander", "w1")
            {
                GrandPlans = s.GrandPlans,
                ActivateProjectAsync = () => { s.Activated = true; p.Status = ProjectStatus.Active; s.Store.SaveProject(p); return Task.CompletedTask; },
                ConveneCouncilAsync = (topic, briefing, roles, urgency, purpose, ct) => Task.FromResult("STUB-VERDICT"),
            };
            return s;
        }

        private static async Task<ProjectGate> WaitForGateAsync(ProjectGateManager gates, string pid)
        {
            for (int i = 0; i < 200; i++)
            {
                var g = gates.ListPending(pid).FirstOrDefault();
                if (g != null) return g;
                await Task.Delay(10);
            }
            throw new Xunit.Sdk.XunitException("gate never opened");
        }

        [Fact]
        public async Task SubmitGrandPlan_Approved_ActivatesProject()
        {
            var s = NewSetup(ProjectStatus.Planning);
            string args = JsonConvert.SerializeObject(new
            {
                mission = "Do it well.",
                milestones = new[] { new { title = "Stand up the pipeline" } },
                successCriteria = new[] { new { text = "Pipeline green" } },
                summary = "Do it well",
            });
            var task = s.Tools.DispatchAsync("submit_grand_plan", args, CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Project.ProjectID);
            Assert.Equal("plan", gate.Kind);
            s.Gates.ResolveGate(s.Project.ProjectID, gate.GateID, new GateResolution(GateDecision.Approve, "ship it", "klives"));

            var result = await task;
            Assert.Contains("approved", result.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.True(s.Activated);
            Assert.Equal(ProjectStatus.Active, s.Store.GetProject(s.Project.ProjectID)!.Status);
            Assert.True(s.GrandPlans.HasApprovedPlan(s.Project.ProjectID));
            Assert.Contains(s.Log.ReadSince(s.Project.ProjectID, 0), e => e.Type == ProjectEventTypes.GrandPlanApproved);
        }

        [Fact]
        public async Task SubmitGrandPlan_Denied_NoActivation_VersionRejected()
        {
            var s = NewSetup(ProjectStatus.Planning);
            string args = JsonConvert.SerializeObject(new { mission = "plan", summary = "sum" });
            var task = s.Tools.DispatchAsync("submit_grand_plan", args, CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Project.ProjectID);
            s.Gates.ResolveGate(s.Project.ProjectID, gate.GateID, new GateResolution(GateDecision.Deny, "rethink scope", "klives"));

            var result = await task;
            Assert.Contains("rethink scope", result.ResultText);
            Assert.False(s.Activated);
            Assert.Equal(ProjectStatus.Planning, s.Store.GetProject(s.Project.ProjectID)!.Status);
            Assert.False(s.GrandPlans.HasApprovedPlan(s.Project.ProjectID));
            Assert.Equal(GrandPlanVersionStatus.Rejected, s.GrandPlans.Get(s.Project.ProjectID).Versions.Single().Status);
        }

        [Fact]
        public async Task NonMaterialAmendment_NeedsNoGate()
        {
            var s = NewSetup(ProjectStatus.Active);
            s.GrandPlans.SubmitVersion(s.Project.ProjectID, new GrandPlanContent { Mission = "v1" }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Project.ProjectID, 1, "g0", null);

            string args = JsonConvert.SerializeObject(new { mission = "v2 tactical", summary = "s2", changeNote = "tweak", material = "false" });
            var result = await s.Tools.DispatchAsync("amend_grand_plan", args, CancellationToken.None);

            Assert.Contains("non-material", result.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(s.Gates.ListPending(s.Project.ProjectID));
            Assert.Equal(2, s.GrandPlans.GetCurrentApproved(s.Project.ProjectID)!.Version);
            Assert.Contains(s.Log.ReadSince(s.Project.ProjectID, 0), e => e.Type == ProjectEventTypes.GrandPlanAmended);
        }

        [Fact]
        public async Task UpdatePlanProgress_TicksMilestoneAndCriterion_NoNewVersion()
        {
            var s = NewSetup(ProjectStatus.Active);
            s.GrandPlans.SubmitVersion(s.Project.ProjectID, new GrandPlanContent
            {
                Mission = "Win",
                Milestones = { new PlanMilestone { Title = "Alpha" } },
                SuccessCriteria = { new PlanCriterion { Text = "PnL>0" } },
            }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Project.ProjectID, 1, "g0", null);

            var r = await s.Tools.DispatchAsync("update_plan_progress",
                JsonConvert.SerializeObject(new { milestoneId = "m1", milestoneStatus = "done", criterionId = "c1", criterionMet = "true" }),
                CancellationToken.None);

            Assert.Contains("Alpha", r.ResultText);
            var cur = s.GrandPlans.GetCurrentApproved(s.Project.ProjectID)!.Content!;
            Assert.Equal(MilestoneStatus.Done, cur.Milestones[0].Status);
            Assert.True(cur.SuccessCriteria[0].Met);
            Assert.Single(s.GrandPlans.Get(s.Project.ProjectID).Versions); // in place — no new version
            Assert.Contains(s.Log.ReadSince(s.Project.ProjectID, 0), e => e.Type == ProjectEventTypes.GrandPlanProgress);
        }

        [Fact]
        public async Task PlanningPhase_BlocksExecutionTools_AllowsPlanningTools()
        {
            var s = NewSetup(ProjectStatus.Planning);

            var blocked = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.Contains("PLANNING", blocked.ResultText, StringComparison.OrdinalIgnoreCase);

            var plan = await s.Tools.DispatchAsync("update_plan",
                JsonConvert.SerializeObject(new { plan = "tactical steps" }), CancellationToken.None);
            Assert.DoesNotContain("PLANNING", plan.ResultText, StringComparison.OrdinalIgnoreCase);

            var council = await s.Tools.DispatchAsync("convene_council",
                JsonConvert.SerializeObject(new { topic = "approach?", briefing = "everything" }), CancellationToken.None);
            Assert.Contains("STUB-VERDICT", council.ResultText);
        }

        [Fact]
        public async Task ExecutionTools_UnlockOnceActive()
        {
            var s = NewSetup(ProjectStatus.Planning);
            var blocked = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.Contains("PLANNING", blocked.ResultText, StringComparison.OrdinalIgnoreCase);

            s.Project.Status = ProjectStatus.Active; // the in-wake snapshot flips on approval
            var ok = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.DoesNotContain("PLANNING", ok.ResultText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ConveneCouncil_RequiresBriefing()
        {
            var s = NewSetup(ProjectStatus.Planning);
            var result = await s.Tools.DispatchAsync("convene_council",
                JsonConvert.SerializeObject(new { topic = "x" }), CancellationToken.None); // no briefing
            Assert.Contains("briefing", result.ResultText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
