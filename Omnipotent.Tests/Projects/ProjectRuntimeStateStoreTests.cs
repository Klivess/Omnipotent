using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    public sealed class ProjectRuntimeStateStoreTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-runtime-tests", Guid.NewGuid().ToString("N"));

        private ProjectRuntimeStateStore NewStore() => new(_ => { }, root);

        [Fact]
        public void WakeLease_IsSingleFlightAndGenerationFenced()
        {
            var store = NewStore();
            string pid = "p1";
            var first = store.TryAcquireWakeLease(pid, "wake-a");
            var overlapping = store.TryAcquireWakeLease(pid, "wake-b");

            Assert.True(first.Acquired);
            Assert.False(overlapping.Acquired);
            Assert.False(store.ReleaseWakeLease(pid, "wake-a", first.Lease!.Generation + 1).Applied);
            Assert.NotNull(store.Get(pid).ActiveWakeLease);
            Assert.True(store.ReleaseWakeLease(pid, "wake-a", first.Lease.Generation).Applied);

            var replacement = store.TryAcquireWakeLease(pid, "wake-b");
            Assert.True(replacement.Acquired);
            Assert.True(replacement.Lease!.Generation > first.Lease.Generation);
        }

        [Fact]
        public void CheckpointCasAndFactFreshness_AreMachineEnforced()
        {
            var store = NewStore();
            string pid = "p2";
            DateTime now = DateTime.UtcNow;
            long revision = store.Get(pid).Revision;
            var applied = store.UpsertVerifiedFact(pid, new ProjectVerifiedFact
            {
                Key = "dependency.version",
                Value = "2.0",
                VerifiedAt = now,
                ValidUntil = now.AddMinutes(5),
                Evidence = { new ProjectEvidenceReference { Kind = ProjectEvidenceKind.ToolResult, Reference = "tool-call-7" } },
            }, expectedRevision: revision, nowUtc: now);

            Assert.True(applied.Applied);
            Assert.False(store.SetActiveMilestones(pid, 1, new[] { "m1" }, expectedRevision: revision).Applied);
            Assert.Single(store.GetFreshVerifiedFacts(pid, now.AddMinutes(1)));
            Assert.Empty(store.GetFreshVerifiedFacts(pid, now.AddMinutes(6)));
            Assert.Contains("tool-call-7", store.DescribeForWake(pid, now.AddMinutes(1)));
        }

        [Fact]
        public void TriggerClaim_RequiresOwningWakeLease()
        {
            var store = NewStore();
            string pid = "p3";
            store.EnqueueTrigger(pid, new ProjectWakeTrigger
            {
                Kind = ProjectWakeTriggerKind.Resume,
                Payload = "resume the verified next action",
                Priority = 10,
            });
            var lease = store.TryAcquireWakeLease(pid, "wake-a").Lease!;

            Assert.False(store.TryClaimNextTrigger(pid, "wake-b", lease.Generation).Claimed);
            var claim = store.TryClaimNextTrigger(pid, "wake-a", lease.Generation);
            Assert.True(claim.Claimed);
            Assert.True(store.AcknowledgeTrigger(pid, claim.Trigger!.TriggerID, "wake-a", lease.Generation, succeeded: true).Applied);
            Assert.Empty(store.ListPendingTriggers(pid));
        }

        [Fact]
        public void FailedTriggerAcknowledgement_ReleasesTheExactPayloadForReplayAfterRestart()
        {
            var store = NewStore();
            const string pid = "p3-retry";
            store.EnqueueTrigger(pid, new ProjectWakeTrigger
            {
                Kind = ProjectWakeTriggerKind.HumanMessage,
                Payload = "durable human instruction",
                Priority = 100,
            });
            var firstLease = store.TryAcquireWakeLease(pid, "wake-a").Lease!;
            var firstClaim = store.TryClaimNextTrigger(pid, "wake-a", firstLease.Generation);

            Assert.True(firstClaim.Claimed);
            Assert.True(store.AcknowledgeTrigger(pid, firstClaim.Trigger!.TriggerID, "wake-a", firstLease.Generation,
                succeeded: false).Applied);
            Assert.True(store.ReleaseWakeLease(pid, "wake-a", firstLease.Generation).Applied);

            var reloaded = NewStore();
            var pending = Assert.Single(reloaded.ListPendingTriggers(pid));
            Assert.Equal("durable human instruction", pending.Payload);
            Assert.Equal(1, pending.AttemptCount);
            Assert.Null(pending.ClaimedByWakeID);

            var secondLease = reloaded.TryAcquireWakeLease(pid, "wake-b").Lease!;
            var replay = reloaded.TryClaimNextTrigger(pid, "wake-b", secondLease.Generation);
            Assert.True(replay.Claimed);
            Assert.Equal(firstClaim.Trigger.TriggerID, replay.Trigger!.TriggerID);
        }

        [Fact]
        public void AgentWakeLeases_AreIndependentDurableAndGenerationFenced()
        {
            var store = NewStore();
            string pid = "p4";
            var a = store.TryAcquireAgentWakeLease(pid, "agent-a", "wake-a");
            var b = store.TryAcquireAgentWakeLease(pid, "agent-b", "wake-b");

            Assert.True(a.Acquired);
            Assert.True(b.Acquired);
            Assert.False(store.TryAcquireAgentWakeLease(pid, "agent-a", "overlap").Acquired);
            Assert.False(store.ReleaseAgentWakeLease(pid, "agent-a", "wake-a", a.Lease!.Generation + 1).Applied);
            Assert.True(store.MarkAgentWakeRunning(pid, "agent-a", "wake-a", a.Lease.Generation).Applied);
            Assert.Equal(2, NewStore().Get(pid).ActiveAgentWakeLeases.Count);
            Assert.True(store.ReleaseAgentWakeLease(pid, "agent-a", "wake-a", a.Lease.Generation).Applied);
            Assert.Single(store.Get(pid).ActiveAgentWakeLeases);
        }

        [Fact]
        public void AgentResumeAndTypedApplicability_SurviveRestart()
        {
            var store = NewStore();
            string pid = "p5";
            store.SetAgentResumeAction(pid, "agent-a", new ProjectResumeAction
            {
                Kind = "work-slice", Summary = "continue at record 401", RecordedBy = "agent-a"
            });
            store.SetActiveMilestones(pid, 2, new[] { "m2" });
            var state = NewStore().Get(pid);

            Assert.Equal("continue at record 401", state.Checkpoint.AgentResumeActions["agent-a"].Summary);
            Assert.Equal(ProjectWakeTriggerApplicability.Applicable,
                ProjectRuntimeStateStore.EvaluateApplicability(new ProjectWakeTrigger
                {
                    ExpectedGrandPlanVersion = 2,
                    RequiredActiveMilestoneIDs = { "m2" },
                }, state, DateTime.UtcNow));
            Assert.Equal(ProjectWakeTriggerApplicability.Stale,
                ProjectRuntimeStateStore.EvaluateApplicability(new ProjectWakeTrigger
                {
                    ExpectedGrandPlanVersion = 1,
                }, state, DateTime.UtcNow));
        }

        [Fact]
        public void RenewableWork_RequiresNovelSuccessfulResults()
        {
            var store = NewStore();
            string pid = "p6";
            var failed = new CommanderToolResult("Tool failed: connection unavailable");
            var success = new CommanderToolResult("Fetched 14 distinct records.");

            Assert.False(ProjectWorkProgress.RecordIfNovel(store, pid, "commander", "web_fetch", "{\"url\":\"a\"}", failed));
            Assert.True(ProjectWorkProgress.RecordIfNovel(store, pid, "commander", "web_fetch", "{\"url\":\"a\"}", success));
            Assert.False(ProjectWorkProgress.RecordIfNovel(store, pid, "commander", "web_fetch", "{\"url\":\"a\"}", success));
            Assert.True(ProjectWorkProgress.RecordIfNovel(store, pid, "agent-a", "web_fetch", "{\"url\":\"a\"}", success));
            Assert.NotNull(store.Get(pid).Checkpoint.AgentLastSuccessfulActions["agent-a"].Fingerprint);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
