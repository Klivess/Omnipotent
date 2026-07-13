using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Grand Plan store: versioned strategic plan Klives approves before work begins. Versions are
    /// append-only and monotonic; approving one supersedes the prior approved version. Content is
    /// structured (mission/milestones/criteria/…); milestone status and criterion ticks are living
    /// (mutated in place on the current approved version, no new version).
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectGrandPlanStoreTests
    {
        private static ProjectGrandPlanStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");
        private static GrandPlanContent Plan(string mission = "Do the thing.") => new() { Mission = mission };

        [Fact]
        public void Submit_MaterialVersion_IsPendingApproval_NotYetCurrent()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var v = store.SubmitVersion(pid, Plan(), "Do the thing", null, material: true, "wake1");
            Assert.Equal(1, v.Version);
            Assert.Equal(GrandPlanVersionStatus.PendingApproval, v.Status);
            Assert.False(store.HasApprovedPlan(pid));
            Assert.Null(store.GetCurrentApproved(pid));
        }

        [Fact]
        public void Approve_MakesCurrent_AndSupersedesPrevious()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, Plan("v1"), "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "gate1", "looks good");
            Assert.True(store.HasApprovedPlan(pid));
            Assert.Equal(1, store.GetCurrentApproved(pid)!.Version);

            store.SubmitVersion(pid, Plan("v2"), "s2", "sharper", material: true, "w2");
            store.MarkApproved(pid, 2, "gate2", null);
            var current = store.GetCurrentApproved(pid)!;
            Assert.Equal(2, current.Version);

            var doc = store.Get(pid);
            Assert.Equal(GrandPlanVersionStatus.Superseded, doc.Versions.First(v => v.Version == 1).Status);
            Assert.Equal(GrandPlanVersionStatus.Approved, doc.Versions.First(v => v.Version == 2).Status);
        }

        [Fact]
        public void Reject_LeavesPriorApprovedCurrent()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, Plan("v1"), "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);
            store.SubmitVersion(pid, Plan("v2"), "s2", "revise", material: true, "w2");
            store.MarkRejected(pid, 2, "g2", "not yet");

            Assert.Equal(1, store.GetCurrentApproved(pid)!.Version);
            Assert.Equal(GrandPlanVersionStatus.Rejected, store.Get(pid).Versions.First(v => v.Version == 2).Status);
        }

        [Fact]
        public void NonMaterialAmendment_ImmediatelyApproved_AndSupersedes()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, Plan("v1"), "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);
            var v2 = store.SubmitVersion(pid, Plan("v2 tactical"), "s2", "tweak", material: false, "w2");

            Assert.Equal(GrandPlanVersionStatus.Approved, v2.Status);
            Assert.Equal(2, store.GetCurrentApproved(pid)!.Version);
            Assert.Equal(GrandPlanVersionStatus.Superseded, store.Get(pid).Versions.First(v => v.Version == 1).Status);
        }

        [Fact]
        public void VersionNumbers_AreMonotonic()
        {
            var store = NewStore();
            string pid = NewProjectId();
            for (int i = 1; i <= 5; i++)
                Assert.Equal(i, store.SubmitVersion(pid, Plan($"v{i}"), $"s{i}", null, material: true, "w").Version);
        }

        [Fact]
        public void DescribeForSeed_ReflectsApprovedSummary_EmptyWhenNone()
        {
            var store = NewStore();
            string pid = NewProjectId();
            Assert.Equal("", store.DescribeForSeed(pid));

            store.SubmitVersion(pid, Plan("v1"), "Ship the MVP by Q3", null, material: true, "w1");
            Assert.Equal("", store.DescribeForSeed(pid)); // still pending
            store.MarkApproved(pid, 1, "g1", null);
            var seed = store.DescribeForSeed(pid);
            Assert.Contains("GRAND PLAN v1", seed);
            Assert.Contains("Ship the MVP by Q3", seed);
        }

        [Fact]
        public void Submit_AssignsStableIds_AndRendersMarkdownMirror()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var content = new GrandPlanContent
            {
                Mission = "Win",
                Milestones = { new PlanMilestone { Title = "Alpha" }, new PlanMilestone { Title = "Beta" } },
                SuccessCriteria = { new PlanCriterion { Text = "PnL > 0" } },
                Risks = { new PlanRisk { Description = "Rate limits", Severity = RiskSeverity.High, Mitigation = "cache" } },
            };
            var v = store.SubmitVersion(pid, content, "sum", null, material: true, "w1");

            Assert.Equal("m1", v.Content!.Milestones[0].ID);
            Assert.Equal("m2", v.Content.Milestones[1].ID);
            Assert.Equal(0, v.Content.Milestones[0].Order);
            Assert.Equal("c1", v.Content.SuccessCriteria[0].ID);
            Assert.Equal("r1", v.Content.Risks[0].ID);
            Assert.Contains("## Mission", v.Markdown);
            Assert.Contains("Alpha", v.Markdown);
        }

        [Fact]
        public void UpdateMilestoneStatus_And_SetCriterionMet_MutateCurrentApprovedInPlace()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var content = new GrandPlanContent
            {
                Mission = "Win",
                Milestones = { new PlanMilestone { Title = "Alpha" }, new PlanMilestone { Title = "Beta" } },
                SuccessCriteria = { new PlanCriterion { Text = "PnL > 0" } },
            };
            store.SubmitVersion(pid, content, "sum", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);

            // Match by id, and by exact title.
            var evidence = new PlanEvidence { Summary = "verified by event", EventSequence = 1, RecordedBy = "test" };
            Assert.NotNull(store.UpdateMilestoneStatus(pid, "m1", MilestoneStatus.Done, evidence));
            Assert.NotNull(store.UpdateMilestoneStatus(pid, "Beta", MilestoneStatus.InProgress));
            Assert.Null(store.UpdateMilestoneStatus(pid, "nope", MilestoneStatus.Done));
            Assert.NotNull(store.SetCriterionMet(pid, "c1", true, evidence));

            var cur = store.GetCurrentApproved(pid)!.Content!;
            Assert.Equal(MilestoneStatus.Done, cur.Milestones[0].Status);
            Assert.Equal(MilestoneStatus.InProgress, cur.Milestones[1].Status);
            Assert.True(cur.SuccessCriteria[0].Met);
            Assert.NotNull(cur.Milestones[0].UpdatedAt);

            // No new version was created — still v1.
            Assert.Single(store.Get(pid).Versions);
            // Progress is reflected in the seed.
            Assert.Contains("1/2 milestones", store.DescribeForSeed(pid));
            Assert.Contains("1/1 criteria", store.DescribeForSeed(pid));
        }

        [Fact]
        public void LegacyMarkdownOnlyVersion_LoadsWithNullContent()
        {
            // A version persisted before the structured model had Markdown but no Content.
            var legacy = new GrandPlanDocument
            {
                ProjectID = "p",
                Versions = { new GrandPlanVersion { Version = 1, Markdown = "old plan", Content = null, Status = GrandPlanVersionStatus.Approved } },
            };
            var roundTripped = JsonConvert.DeserializeObject<GrandPlanDocument>(JsonConvert.SerializeObject(legacy))!;
            Assert.Null(roundTripped.Versions[0].Content);
            Assert.Equal("old plan", roundTripped.Versions[0].Markdown);
            Assert.Equal("", ProjectGrandPlanStore.DescribeProgress(roundTripped.Versions[0].Content));
        }

        [Fact]
        public void SurvivesNewStoreInstance()
        {
            string pid = NewProjectId();
            NewStore().SubmitVersion(pid, Plan("persisted"), "sum", null, material: true, "w1");
            NewStore().MarkApproved(pid, 1, "g1", "ok");
            Assert.True(NewStore().HasApprovedPlan(pid));
            Assert.Equal("persisted", NewStore().GetCurrentApproved(pid)!.Content!.Mission);
        }

        [Fact]
        public void EmptyMission_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                NewStore().SubmitVersion(NewProjectId(), new GrandPlanContent { Mission = "  " }, "s", null, material: true, "w"));
        }

        [Fact]
        public void ReadyMilestones_RespectDependencies()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, new GrandPlanContent
            {
                Mission = "Ship",
                Milestones =
                {
                    new PlanMilestone { Title = "Foundation" },
                    new PlanMilestone { Title = "Integration", DependsOn = { "Foundation" } },
                },
            }, "dependency plan", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);

            Assert.Equal("Foundation", Assert.Single(store.GetReadyMilestones(pid)).Title);
            Assert.Throws<InvalidOperationException>(() =>
                store.UpdateMilestoneStatus(pid, "Integration", MilestoneStatus.InProgress));
            store.UpdateMilestoneStatus(pid, "Foundation", MilestoneStatus.Done,
                new PlanEvidence { Summary = "verified", EventSequence = 1, RecordedBy = "test" });
            Assert.Equal("Integration", Assert.Single(store.GetReadyMilestones(pid)).Title);
            Assert.Contains("Ready now", store.DescribeForSeed(pid));
        }

        [Fact]
        public void Submit_RejectsCyclicOrUnknownDependencies()
        {
            var store = NewStore();
            var cyclic = new GrandPlanContent
            {
                Mission = "Ship",
                Milestones =
                {
                    new PlanMilestone { Title = "A", DependsOn = { "B" } },
                    new PlanMilestone { Title = "B", DependsOn = { "A" } },
                },
            };
            Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() =>
                store.SubmitVersion(NewProjectId(), cyclic, "cyclic", null, true, "w")).Message,
                StringComparison.OrdinalIgnoreCase);

            var unknown = new GrandPlanContent
            {
                Mission = "Ship",
                Milestones = { new PlanMilestone { Title = "A", DependsOn = { "missing" } } },
            };
            Assert.Contains("unknown", Assert.Throws<InvalidOperationException>(() =>
                store.SubmitVersion(NewProjectId(), unknown, "bad", null, true, "w")).Message,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EvidenceBackedPreconditions_GateMilestoneExecutionAndCompletion()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, new GrandPlanContent
            {
                Mission = "Operate account",
                Preconditions =
                {
                    new PlanPrecondition
                    {
                        Description = "Verification email is deliverable",
                        Verification = "Send a live message and observe it in the canonical mailbox",
                    },
                },
                Milestones = { new PlanMilestone { Title = "Create account" } },
                SuccessCriteria = { new PlanCriterion { Text = "Account is verified" } },
            }, "preconditioned plan", null, true, "w1");
            store.MarkApproved(pid, 1, "g1", null);

            Assert.Empty(store.GetReadyMilestones(pid));
            Assert.Throws<InvalidOperationException>(() =>
                store.UpdateMilestoneStatus(pid, "m1", MilestoneStatus.InProgress));
            Assert.Contains(store.GetCompletionReadinessIssues(pid), x => x.Contains("precondition p1"));

            var verified = store.SetPreconditionStatus(pid, "p1", PlanPreconditionStatus.Verified,
                new PlanEvidence { Summary = "message id 123 observed", EventSequence = 123, RecordedBy = "test" });

            Assert.Equal(PlanPreconditionStatus.Verified, verified!.Status);
            Assert.Equal("Create account", Assert.Single(store.GetReadyMilestones(pid)).Title);
            Assert.Contains("1/1 preconditions", store.DescribeForSeed(pid));
        }

        [Fact]
        public void BlockingAndHighRisks_MustBeExplicitlyResolvedWithEvidence()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, new GrandPlanContent
            {
                Mission = "Operate responsibly",
                Risks =
                {
                    new PlanRisk
                    {
                        Description = "Platform policy permits the planned operation",
                        Severity = RiskSeverity.High,
                        BlocksExecution = true,
                        Mitigation = "Review the current policy and constrain the workflow",
                    },
                },
                Milestones = { new PlanMilestone { Title = "Operate account" } },
                SuccessCriteria = { new PlanCriterion { Text = "Operation is compliant" } },
            }, "risk-aware plan", null, true, "w1");
            store.MarkApproved(pid, 1, "g1", null);

            Assert.Empty(store.GetReadyMilestones(pid));
            Assert.Throws<InvalidOperationException>(() =>
                store.UpdateMilestoneStatus(pid, "m1", MilestoneStatus.InProgress));
            Assert.Contains(store.GetCompletionReadinessIssues(pid), issue => issue.Contains("risk r1"));

            var resolved = store.SetRiskStatus(pid, "r1", PlanRiskStatus.Mitigated,
                new PlanEvidence { Summary = "policy review event #44", EventSequence = 44, RecordedBy = "test" });

            Assert.Equal(PlanRiskStatus.Mitigated, resolved!.Status);
            Assert.Equal("Operate account", Assert.Single(store.GetReadyMilestones(pid)).Title);
            Assert.DoesNotContain(store.GetCompletionReadinessIssues(pid), issue => issue.Contains("risk r1"));
        }
    }
}
