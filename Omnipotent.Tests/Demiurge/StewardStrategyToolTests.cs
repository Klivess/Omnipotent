using Newtonsoft.Json;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// The Commander's strategy tools: submit/amend the Grand Plan (approval-gated) and the
    /// Planning-phase execution lockout. Exercises StewardCommanderTools.DispatchAsync directly
    /// against real stores, resolving gates the way the website/Discord surfaces do.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class StewardStrategyToolTests
    {
        private sealed class Setup
        {
            public StewardStore Store = new(_ => { });
            public StewardEventLogStore Log = new(_ => { });
            public StewardGateManager Gates = null!;
            public StewardGrandPlanStore GrandPlans = new(_ => { });
            public Steward Steward = null!;
            public StewardCommanderTools Tools = null!;
            public bool Activated;
        }

        private static Setup NewSetup(StewardStatus status = StewardStatus.Planning, string goal = "goal")
        {
            var s = new Setup();
            var p = s.Store.CreateSteward("t", goal, 100, 100, 10, 5);
            p.Status = status;
            s.Store.SaveSteward(p);
            s.Steward = p;
            s.Gates = new StewardGateManager(s.Log, _ => { });
            var digests = new StewardDigestStore(_ => { });
            var subAgents = new StewardSubAgentManager(s.Store, s.Log);
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new StewardBudgetLedger(s.Store, s.Log, fetcher, _ => { });
            var vault = new StewardVault(_ => { });
            s.Tools = new StewardCommanderTools(p, s.Log, digests, subAgents, s.Gates, budget, vault, s.Store, "commander", "w1")
            {
                GrandPlans = s.GrandPlans,
                ActivateStewardAsync = () => { s.Activated = true; p.Status = StewardStatus.Active; s.Store.SaveSteward(p); return Task.CompletedTask; },
                ConveneCouncilAsync = (topic, briefing, roles, urgency, purpose, ct) => Task.FromResult("STUB-VERDICT"),
            };
            return s;
        }

        private static async Task<StewardGate> WaitForGateAsync(StewardGateManager gates, string pid)
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
        public async Task SubmitGrandPlan_Approved_ActivatesSteward()
        {
            var s = NewSetup(StewardStatus.Planning);
            string args = JsonConvert.SerializeObject(new
            {
                mission = "Do it well.",
                milestones = new[] { new { title = "Stand up the pipeline" } },
                successCriteria = new[] { new { text = "Pipeline green" } },
                summary = "Do it well",
            });
            var task = s.Tools.DispatchAsync("submit_grand_plan", args, CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Steward.StewardID);
            Assert.Equal("plan", gate.Kind);
            s.Gates.ResolveGate(s.Steward.StewardID, gate.GateID, new GateResolution(GateDecision.Approve, "ship it", "klives"));

            var result = await task;
            Assert.Contains("approved", result.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.True(s.Activated);
            Assert.Equal(StewardStatus.Active, s.Store.GetSteward(s.Steward.StewardID)!.Status);
            Assert.True(s.GrandPlans.HasApprovedPlan(s.Steward.StewardID));
            Assert.Contains(s.Log.ReadSince(s.Steward.StewardID, 0), e => e.Type == StewardEventTypes.GrandPlanApproved);
        }

        [Fact]
        public async Task SubmitGrandPlan_Denied_NoActivation_VersionRejected()
        {
            var s = NewSetup(StewardStatus.Planning);
            string args = JsonConvert.SerializeObject(new
            {
                mission = "plan",
                milestones = new[] { new { title = "Deliver the result" } },
                successCriteria = new[] { new { text = "Result verified" } },
                summary = "sum"
            });
            var task = s.Tools.DispatchAsync("submit_grand_plan", args, CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Steward.StewardID);
            s.Gates.ResolveGate(s.Steward.StewardID, gate.GateID, new GateResolution(GateDecision.Deny, "rethink scope", "klives"));

            var result = await task;
            Assert.Contains("rethink scope", result.ResultText);
            Assert.False(s.Activated);
            Assert.Equal(StewardStatus.Planning, s.Store.GetSteward(s.Steward.StewardID)!.Status);
            Assert.False(s.GrandPlans.HasApprovedPlan(s.Steward.StewardID));
            Assert.Equal(GrandPlanVersionStatus.Rejected, s.GrandPlans.Get(s.Steward.StewardID).Versions.Single().Status);
        }

        [Fact]
        public async Task NonMaterialAmendment_NeedsNoGate()
        {
            var s = NewSetup(StewardStatus.Active);
            s.GrandPlans.SubmitVersion(s.Steward.StewardID, new GrandPlanContent { Mission = "v1" }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Steward.StewardID, 1, "g0", null);

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
            Assert.Empty(s.Gates.ListPending(s.Steward.StewardID));
            Assert.Equal(2, s.GrandPlans.GetCurrentApproved(s.Steward.StewardID)!.Version);
            Assert.Contains(s.Log.ReadSince(s.Steward.StewardID, 0), e => e.Type == StewardEventTypes.GrandPlanAmended);
        }

        [Fact]
        public async Task UpdatePlanProgress_TicksMilestoneAndCriterion_NoNewVersion()
        {
            var s = NewSetup(StewardStatus.Active);
            s.GrandPlans.SubmitVersion(s.Steward.StewardID, new GrandPlanContent
            {
                Mission = "Win",
                Milestones = { new PlanMilestone { Title = "Alpha" } },
                SuccessCriteria = { new PlanCriterion { Text = "PnL>0" } },
            }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Steward.StewardID, 1, "g0", null);

            var evidenceEvent = s.Log.Append(new StewardEvent
            {
                StewardID = s.Steward.StewardID,
                Type = StewardEventTypes.ToolResult,
                Author = "test",
                Text = "Verified milestone and criterion through the Steward test.",
            });
            var milestoneResult = await s.Tools.DispatchAsync("update_plan_progress",
                JsonConvert.SerializeObject(new
                {
                    milestoneId = "m1",
                    milestoneStatus = "done",
                    evidence = "Verified by Steward test result event",
                    evidenceEventSequence = evidenceEvent.Sequence,
                }),
                CancellationToken.None);
            var criterionResult = await s.Tools.DispatchAsync("update_plan_progress",
                JsonConvert.SerializeObject(new
                {
                    criterionId = "c1",
                    criterionMet = "true",
                    evidence = "Verified by Steward test result event",
                    evidenceEventSequence = evidenceEvent.Sequence,
                }),
                CancellationToken.None);

            Assert.Contains("Alpha", milestoneResult.ResultText);
            Assert.Contains("PnL>0", criterionResult.ResultText);
            var cur = s.GrandPlans.GetCurrentApproved(s.Steward.StewardID)!.Content!;
            Assert.Equal(MilestoneStatus.Done, cur.Milestones[0].Status);
            Assert.True(cur.SuccessCriteria[0].Met);
            Assert.Single(s.GrandPlans.Get(s.Steward.StewardID).Versions); // in place — no new version
            Assert.Contains(s.Log.ReadSince(s.Steward.StewardID, 0), e => e.Type == StewardEventTypes.GrandPlanProgress);
        }

        [Fact]
        public async Task PlanningPhase_AllowsExecutionTools_AlongsidePlanningTools()
        {
            var s = NewSetup(StewardStatus.Planning);

            var write = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.True(write.Succeeded, write.ResultText);

            var plan = await s.Tools.DispatchAsync("update_plan",
                JsonConvert.SerializeObject(new { plan = "tactical steps" }), CancellationToken.None);
            Assert.DoesNotContain("PLANNING", plan.ResultText, StringComparison.OrdinalIgnoreCase);

            var council = await s.Tools.DispatchAsync("convene_council",
                JsonConvert.SerializeObject(new { topic = "approach?", briefing = "everything" }), CancellationToken.None);
            Assert.Contains("STUB-VERDICT", council.ResultText);
        }

        [Fact]
        public async Task ExecutionToolsRemainAvailableAcrossPlanningAndActive()
        {
            var s = NewSetup(StewardStatus.Planning);
            var planning = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.True(planning.Succeeded, planning.ResultText);

            s.Steward.Status = StewardStatus.Active; // the in-wake snapshot flips on approval
            var ok = await s.Tools.DispatchAsync("write_file",
                JsonConvert.SerializeObject(new { path = "x.txt", content = "hi" }), CancellationToken.None);
            Assert.DoesNotContain("PLANNING", ok.ResultText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ConveneCouncil_RequiresBriefing()
        {
            var s = NewSetup(StewardStatus.Planning);
            var result = await s.Tools.DispatchAsync("convene_council",
                JsonConvert.SerializeObject(new { topic = "x" }), CancellationToken.None); // no briefing
            Assert.Contains("briefing", result.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task AgentCannotCreateOrClearStewardBlockersThroughCheckpointTool()
        {
            var s = NewSetup(StewardStatus.Active);
            var runtime = new StewardRuntimeStateStore(_ => { });
            s.Tools.RuntimeState = runtime;

            var set = await s.Tools.DispatchAsync("update_checkpoint",
                JsonConvert.SerializeObject(new { op = "set_blocker", blockerSummary = "Stop the Steward" }),
                CancellationToken.None);
            var clear = await s.Tools.DispatchAsync("update_checkpoint",
                JsonConvert.SerializeObject(new { op = "clear_blocker" }),
                CancellationToken.None);

            Assert.False(set.Succeeded);
            Assert.False(clear.Succeeded);
            Assert.Null(runtime.Get(s.Steward.StewardID).Blocker);
            Assert.Equal(StewardExecutionDisposition.Running, runtime.Get(s.Steward.StewardID).Disposition);
            Assert.Equal(StewardStatus.Active, s.Store.GetSteward(s.Steward.StewardID)!.Status);
        }

        [Fact]
        public async Task WorkerCannotOpenAnApprovalGateThroughDirectToolDispatch()
        {
            var s = NewSetup(StewardStatus.Active);
            var workerTools = new StewardCommanderTools(s.Steward, s.Log,
                new StewardDigestStore(_ => { }), new StewardSubAgentManager(s.Store, s.Log), s.Gates,
                new StewardBudgetLedger(s.Store, s.Log,
                    new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { }),
                new StewardVault(_ => { }), s.Store, "worker_1", "w1");

            var result = await workerTools.DispatchAsync("request_user_approval",
                JsonConvert.SerializeObject(new { title = "Stop work", description = "A worker cannot pause this Steward." }),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(s.Gates.ListPending(s.Steward.StewardID));
            Assert.Equal(StewardStatus.Active, s.Store.GetSteward(s.Steward.StewardID)!.Status);
        }

        [Fact]
        public void WorkerBlockedStatus_IsNormalizedToAssignedFollowup()
        {
            var s = NewSetup(StewardStatus.Active);
            var agents = new StewardSubAgentManager(s.Store, s.Log);
            agents.EnsureCommander(s.Steward.StewardID);
            var worker = agents.Spawn(s.Steward.StewardID, "commander", StewardAgentTier.Text, "researcher", "Research the next step.");

            Assert.True(agents.UpdateWorkState(s.Steward.StewardID, worker.AgentID,
                StewardAgentWorkStatus.Blocked, "Legacy worker obstacle."));

            Assert.Equal(StewardAgentWorkStatus.Assigned,
                agents.ListActive(s.Steward.StewardID).Single(a => a.AgentID == worker.AgentID).WorkStatus);
        }

        [Fact]
        public async Task OngoingExternalAccountPlan_RequiresLiveGateDurableCadenceAndFeedbackLoop()
        {
            var s = NewSetup(StewardStatus.Planning, "Run and grow a TikTok account on a posting schedule");
            var incomplete = await s.Tools.DispatchAsync("submit_grand_plan", JsonConvert.SerializeObject(new
            {
                mission = "Create the account and post once.",
                milestones = new[] { new { title = "Create TikTok account" } },
                successCriteria = new[] { new { text = "One post exists" } },
                summary = "Create it"
            }), CancellationToken.None);

            Assert.False(incomplete.Succeeded);
            Assert.Contains("EXTERNAL_OPERATION_PLAN_INCOMPLETE", incomplete.ResultText);
            Assert.Empty(s.Gates.ListPending(s.Steward.StewardID));

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
                risks = new[] { new { description = "Platform terms, account eligibility, and media rights", severity = "high", mitigation = "Review live policy and license every asset" } },
                successCriteria = new[] { new { text = "Publishing ledger, recurring cadence, and reach review remain operational" } },
                summary = "Operate through a verified, policy-aware, measured publishing loop"
            }), CancellationToken.None);

            var gate = await WaitForGateAsync(s.Gates, s.Steward.StewardID);
            s.Gates.ResolveGate(s.Steward.StewardID, gate.GateID,
                new GateResolution(GateDecision.Deny, "test complete", "klives"));
            var complete = await completeTask;
            Assert.Contains("test complete", complete.ResultText);
        }

        [Fact]
        public async Task IdenticalHumanRequest_IsNotRedeliveredUntilKlivesReplies()
        {
            var s = NewSetup(StewardStatus.Active);
            int deliveries = 0;
            s.Tools.RequestHumanAsync = _ => { deliveries++; return Task.CompletedTask; };
            string args = JsonConvert.SerializeObject(new { title = "Captcha", description = "Complete the visible challenge" });

            var first = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            var duplicate = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            Assert.True(first.Succeeded);
            Assert.False(duplicate.Succeeded);
            Assert.Contains("ALREADY_OPEN", duplicate.ResultText);
            Assert.Equal(1, deliveries);

            s.Log.Append(new StewardEvent
            {
                StewardID = s.Steward.StewardID,
                Type = StewardEventTypes.KlivesMessage,
                Author = "klives",
                Text = "I handled it.",
            });
            var followUp = await s.Tools.DispatchAsync("request_human", args, CancellationToken.None);
            Assert.True(followUp.Succeeded);
            Assert.Equal(2, deliveries);
        }

        [Fact]
        public async Task ProviderRateLimit_IsNotEscalatedAsHumanWork()
        {
            var s = NewSetup(StewardStatus.Active);
            int deliveries = 0;
            s.Tools.RequestHumanAsync = _ => { deliveries++; return Task.CompletedTask; };

            var result = await s.Tools.DispatchAsync("request_human", JsonConvert.SerializeObject(new
            {
                title = "LLM provider rate-limit",
                description = "Read work/github_intel.md manually because the provider is throttled."
            }), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("INFRASTRUCTURE_REQUEST_REJECTED", result.ResultText);
            Assert.Equal(0, deliveries);
            Assert.DoesNotContain(s.Log.ReadSince(s.Steward.StewardID, 0),
                e => e.Type == StewardEventTypes.HumanAssistanceRequested);
        }

        [Fact]
        public async Task FileDialogRescue_IsAnsweredWithTheUploadToolInsteadOfPingingKlives()
        {
            var s = NewSetup(StewardStatus.Active);
            int deliveries = 0;
            s.Tools.RequestHumanAsync = _ => { deliveries++; return Task.CompletedTask; };

            var result = await s.Tools.DispatchAsync("request_human", JsonConvert.SerializeObject(new
            {
                title = "GTK3 file dialog (permanent blocker)",
                description = "When you're in VNC, could you click Open at ~(1135, 730) for the Day 24 upload? " +
                              "7+ automated bypasses exhausted; manual VNC click still required for each upload.",
            }), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("UPLOAD_IS_NOT_HUMAN_ONLY", result.ResultText);
            Assert.Contains("computer_upload_file", result.ResultText);
            Assert.Equal(0, deliveries);
            Assert.DoesNotContain(s.Log.ReadSince(s.Steward.StewardID, 0),
                e => e.Type == StewardEventTypes.HumanAssistanceRequested);
        }

        [Fact]
        public async Task CaptchaRequest_StillReachesKlivesEvenWhenAnUploadIsMentioned()
        {
            var s = NewSetup(StewardStatus.Active);
            int deliveries = 0;
            s.Tools.RequestHumanAsync = _ => { deliveries++; return Task.CompletedTask; };

            var result = await s.Tools.DispatchAsync("request_human", JsonConvert.SerializeObject(new
            {
                title = "Captcha on the upload page",
                description = "A hCaptcha challenge is blocking the upload form; please solve it in VNC.",
            }), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, deliveries);
        }

        [Fact]
        public async Task IndefiniteAccountOperation_CannotBeCompletedAfterInitialSetup()
        {
            var s = NewSetup(StewardStatus.Active, "Run and grow a TikTok account continuously");

            var result = await s.Tools.DispatchAsync("complete_steward",
                JsonConvert.SerializeObject(new { summary = "The account was created and first post uploaded" }),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("ONGOING_OPERATION_REMAINS_ACTIVE", result.ResultText);
            Assert.Empty(s.Gates.ListPending(s.Steward.StewardID));
        }

        [Fact]
        public async Task NarrativeStatusEvent_CannotProveTerminalPlanProgress()
        {
            var s = NewSetup(StewardStatus.Active);
            s.GrandPlans.SubmitVersion(s.Steward.StewardID, new GrandPlanContent
            {
                Mission = "Verify reality",
                Milestones = { new PlanMilestone { Title = "External outcome" } },
                SuccessCriteria = { new PlanCriterion { Text = "Outcome verified" } },
            }, "s1", null, material: true, "w0");
            s.GrandPlans.MarkApproved(s.Steward.StewardID, 1, "g0", null);
            var narrative = s.Log.Append(new StewardEvent
            {
                StewardID = s.Steward.StewardID,
                Type = StewardEventTypes.Status,
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
                s.GrandPlans.GetCurrentApproved(s.Steward.StewardID)!.Content!.Milestones[0].Status);
        }
    }
}
