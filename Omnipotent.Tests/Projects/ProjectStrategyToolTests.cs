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

        private static Setup NewSetup(ProjectStatus status = ProjectStatus.Planning, string goal = "goal")
        {
            var s = new Setup();
            var p = s.Store.CreateProject("t", goal, 100, 100, 10, 5);
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
            string args = JsonConvert.SerializeObject(new
            {
                mission = "plan",
                milestones = new[] { new { title = "Deliver the result" } },
                successCriteria = new[] { new { text = "Result verified" } },
                summary = "sum"
            });
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

            string args = JsonConvert.SerializeObject(new
            {
                mission = "v2 tactical",
                milestones = new[] { new { title = "Tactical delivery" } },
                successCriteria = new[] { new { text = "Delivery verified" } },
                summary = "s2",
                changeNote = "tweak",
                material = "false"
            });
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

            var evidenceEvent = s.Log.Append(new ProjectEvent
            {
                ProjectID = s.Project.ProjectID,
                Type = ProjectEventTypes.ToolResult,
                Author = "test",
                Text = "Verified milestone and criterion through the project test.",
            });
            var milestoneResult = await s.Tools.DispatchAsync("update_plan_progress",
                JsonConvert.SerializeObject(new
                {
                    milestoneId = "m1",
                    milestoneStatus = "done",
                    evidence = "Verified by project test result event",
                    evidenceEventSequence = evidenceEvent.Sequence,
                }),
                CancellationToken.None);
            var criterionResult = await s.Tools.DispatchAsync("update_plan_progress",
                JsonConvert.SerializeObject(new
                {
                    criterionId = "c1",
                    criterionMet = "true",
                    evidence = "Verified by project test result event",
                    evidenceEventSequence = evidenceEvent.Sequence,
                }),
                CancellationToken.None);

            Assert.Contains("Alpha", milestoneResult.ResultText);
            Assert.Contains("PnL>0", criterionResult.ResultText);
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
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task OngoingExternalAccountPlan_RequiresLiveGateDurableCadenceAndFeedbackLoop()
        {
            var s = NewSetup(ProjectStatus.Planning, "Run and grow a TikTok account on a posting schedule");
            var incomplete = await s.Tools.DispatchAsync("submit_grand_plan", JsonConvert.SerializeObject(new
            {
                mission = "Create the account and post once.",
                milestones = new[] { new { title = "Create TikTok account" } },
                successCriteria = new[] { new { text = "One post exists" } },
                summary = "Create it"
            }), CancellationToken.None);

            Assert.False(incomplete.Succeeded);
            Assert.Contains("EXTERNAL_OPERATION_PLAN_INCOMPLETE", incomplete.ResultText);
            Assert.Empty(s.Gates.ListPending(s.Project.ProjectID));

            var completeTask = s.Tools.DispatchAsync("submit_grand_plan", JsonConvert.SerializeObject(new
            {
                mission = "Operate and improve the TikTok account continuously.",
                milestones = new[]
                {
                    new { title = "Verify signup and mailbox delivery" },
                    new { title = "Run a durable publishing queue and recurring timer schedule" },
                    new { title = "Review analytics metrics and feed the next growth experiment" },
                },
                preconditions = new[] { new { description = "TikTok signup and email verification are available", verification = "Use the visible browser and confirm a real KliveMail code arrives" } },
                risks = new[] { new { description = "Platform terms, account eligibility, and media rights", severity = "high", mitigation = "Review live policy and license every asset", blocksExecution = true } },
                successCriteria = new[] { new { text = "Publishing ledger, recurring cadence, and reach review remain operational" } },
                summary = "Operate through a verified, policy-aware, measured publishing loop"
            }), CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Project.ProjectID);
            s.Gates.ResolveGate(s.Project.ProjectID, gate.GateID,
                new GateResolution(GateDecision.Deny, "test complete", "klives"));
            var complete = await completeTask;
            Assert.Contains("test complete", complete.ResultText);
        }

        [Fact]
        public async Task IdenticalHumanRequest_IsNotRedeliveredUntilKlivesReplies()
        {
            var s = NewSetup(ProjectStatus.Active);
            int deliveries = 0;
            s.Tools.RequestHumanAsync = _ => { deliveries++; return Task.CompletedTask; };
            string args = JsonConvert.SerializeObject(new { title = "Captcha", description = "Complete the visible challenge" });

            var first = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            var duplicate = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            Assert.True(first.Succeeded);
            Assert.False(duplicate.Succeeded);
            Assert.Contains("ALREADY_OPEN", duplicate.ResultText);
            Assert.Equal(1, deliveries);

            s.Log.Append(new ProjectEvent
            {
                ProjectID = s.Project.ProjectID,
                Type = ProjectEventTypes.KlivesMessage,
                Author = "klives",
                Text = "I handled it.",
            });
            var followUp = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            Assert.True(followUp.Succeeded);
            Assert.Equal(2, deliveries);
        }

        [Fact]
        public async Task IndefiniteAccountOperation_CannotBeCompletedAfterInitialSetup()
        {
            var s = NewSetup(ProjectStatus.Active, "Run and grow a TikTok account continuously");

            var result = await s.Tools.DispatchAsync("complete_project",
                JsonConvert.SerializeObject(new { summary = "The account was created and first post uploaded" }),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("ONGOING_OPERATION_REMAINS_ACTIVE", result.ResultText);
            Assert.Empty(s.Gates.ListPending(s.Project.ProjectID));
        }

        [Fact]
        public async Task NarrativeStatusEvent_CannotProveTerminalPlanProgress()
        {
            var s = NewSetup(ProjectStatus.Active);
            s.GrandPlans.SubmitVersion(s.Project.ProjectID, new GrandPlanContent
            {
                Mission = "Verify reality",
                Milestones = { new PlanMilestone { Title = "External outcome" } },
                SuccessCriteria = { new PlanCriterion { Text = "Outcome verified" } },
            }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Project.ProjectID, 1, "g0", null);
            var narrative = s.Log.Append(new ProjectEvent
            {
                ProjectID = s.Project.ProjectID,
                Type = ProjectEventTypes.Status,
                Author = "commander",
                Text = "I believe the external outcome happened.",
            });

            var result = await s.Tools.DispatchAsync("update_plan_progress", JsonConvert.SerializeObject(new
            {
                milestoneId = "m1",
                milestoneStatus = "done",
                evidence = "Commander says it happened",
                evidenceEventSequence = narrative.Sequence,
            }), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("not outcome evidence", result.ResultText);
            Assert.Equal(MilestoneStatus.Pending,
                s.GrandPlans.GetCurrentApproved(s.Project.ProjectID)!.Content!.Milestones[0].Status);
        }
    }
}
