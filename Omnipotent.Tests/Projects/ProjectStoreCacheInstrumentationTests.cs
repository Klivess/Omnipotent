using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Every per-project store read by a /projects/* GET has to participate in the response cache's
    /// version model. These pin the same contract the settings store broke (see
    /// <see cref="ProjectSettingsStoreCacheTests"/>) for its siblings: a read notes a version, a write
    /// bumps it, and one project's write never invalidates another's cached read.
    ///
    /// The failure mode is silent — the write succeeds, the API returns the new value, and only the
    /// next cached GET reveals the old one — so it is worth a test per store rather than trusting
    /// that the instrumentation is present.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectStoreCacheInstrumentationTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        // ── runtime state (drives /projects/list + the detail page's status and disposition) ──

        [Fact]
        public void RuntimeState_Write_InvalidatesACachedRead()
        {
            var store = new ProjectRuntimeStateStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.WouldBeCached());
            Assert.True(scope.StillValid());

            store.SetDisposition(pid, ProjectExecutionDisposition.Paused);

            Assert.False(scope.StillValid());
        }

        [Fact]
        public void RuntimeState_Write_DoesNotInvalidate_AnotherProjectsCachedRead()
        {
            var store = new ProjectRuntimeStateStore(_ => { });
            string mine = NewProjectId();
            string other = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.Get(mine));
            store.SetDisposition(other, ProjectExecutionDisposition.Paused);

            Assert.True(scope.StillValid());
        }

        /// <summary>A no-op mutation changes nothing on disk, so it must not invalidate — otherwise
        /// the cache thrashes on every idle wake that re-asserts the state it already has.</summary>
        [Fact]
        public void RuntimeState_UnchangedMutation_LeavesCachedReadValid()
        {
            var store = new ProjectRuntimeStateStore(_ => { });
            string pid = NewProjectId();
            store.SetDisposition(pid, ProjectExecutionDisposition.Paused);

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            store.SetDisposition(pid, ProjectExecutionDisposition.Paused); // already Paused — no commit

            Assert.True(scope.StillValid());
        }

        /// <summary>Fact freshness is a function of wall-clock time, not of any tracked version — a
        /// fact expires with no write to bump, so this read can never be cached.</summary>
        [Fact]
        public void RuntimeState_FreshVerifiedFacts_AreNeverCacheable()
        {
            var store = new ProjectRuntimeStateStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.GetFreshVerifiedFacts(pid));

            Assert.False(scope.WouldBeCached());
            Assert.NotNull(scope.UncacheableReason);
        }

        /// <summary>The list renders the runtime projection rather than the whole state; it has to
        /// track the state it summarises, or a paused project keeps rendering as running.</summary>
        [Fact]
        public void RuntimeState_Summary_TracksTheState_AndInvalidatesOnWrite()
        {
            var store = new ProjectRuntimeStateStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.GetSummary(pid));
            Assert.True(scope.WouldBeCached());
            Assert.Equal(ProjectExecutionDisposition.Running, store.GetSummary(pid).Disposition);

            store.SetDisposition(pid, ProjectExecutionDisposition.Paused);

            Assert.False(scope.StillValid());
            Assert.Equal(ProjectExecutionDisposition.Paused, store.GetSummary(pid).Disposition);
            Assert.Equal(store.Get(pid).Revision, store.GetSummary(pid).Revision);
        }

        // ── budget ledger (the spend figures on every list row) ──

        [Fact]
        public async Task Budget_Spend_TracksTheLedger_AndInvalidatesOnWrite()
        {
            var projects = new ProjectStore(_ => { });
            var events = new ProjectEventLogStore(_ => { });
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var ledger = new ProjectBudgetLedger(projects, events, fetcher, _ => { });
            var project = projects.CreateProject("budget cache", "goal", tokenBudgetUsd: 100, 0, 0, 1);

            var scope = CacheFillProbe.Fill(() => ledger.GetSpend(project.ProjectID));
            Assert.True(scope.WouldBeCached());

            await ledger.RecordTokenSpendAsync(project.ProjectID, 1000, 1000, actualCostUsd: 0.25);

            Assert.False(scope.StillValid());
            Assert.Equal(0.25, ledger.GetSpend(project.ProjectID).TokenSpendUsd, 6);
            Assert.Equal(ledger.GetLedger(project.ProjectID).TokenSpendUsd, ledger.GetSpend(project.ProjectID).TokenSpendUsd, 6);
        }

        // ── approval gates (the pending-approvals badge) ──

        [Fact]
        public void Gates_PendingCount_TracksGates_AndInvalidatesOnWrite()
        {
            var events = new ProjectEventLogStore(_ => { });
            var gates = new ProjectGateManager(events, _ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => gates.CountPending(pid));
            Assert.True(scope.WouldBeCached());
            Assert.Equal(0, gates.CountPending(pid));

            using var cts = new CancellationTokenSource();
            var gate = new ProjectGate { ProjectID = pid, GateID = "g1", Title = "Spend money", Description = "$5" };
            _ = gates.OpenGateAndWaitAsync(gate, cts.Token);

            Assert.False(scope.StillValid());
            Assert.Equal(1, gates.CountPending(pid));

            gates.ResolveGate(pid, "g1", new GateResolution(GateDecision.Approve, "go", "klives"));
            Assert.Equal(0, gates.CountPending(pid));
        }

        // ── grand plan ──

        [Fact]
        public void GrandPlan_Submit_InvalidatesACachedRead()
        {
            var store = new ProjectGrandPlanStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.WouldBeCached());

            store.SubmitVersion(pid, new GrandPlanContent { Mission = "Do the thing." }, "s1", null, material: true, "w1");

            Assert.False(scope.StillValid());
        }

        /// <summary>Approval is the gate Klives actually clicks: if it doesn't invalidate, the plan
        /// reads back as still-pending and the project looks stuck in Planning.</summary>
        [Fact]
        public void GrandPlan_Approve_InvalidatesACachedRead()
        {
            var store = new ProjectGrandPlanStore(_ => { });
            string pid = NewProjectId();
            store.SubmitVersion(pid, new GrandPlanContent { Mission = "Do the thing." }, "s1", null, material: true, "w1");

            var scope = CacheFillProbe.Fill(() => store.GetCurrentApproved(pid));
            Assert.True(scope.WouldBeCached());

            store.MarkApproved(pid, 1, "gate1", "looks good");

            Assert.False(scope.StillValid());
        }

        // ── councils ──

        [Fact]
        public void Council_Create_InvalidatesACachedList()
        {
            var store = new ProjectCouncilStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.List(pid));
            Assert.True(scope.WouldBeCached());

            store.Create(new CouncilSession
            {
                ProjectID = pid,
                WakeID = "w1",
                Topic = "Should we pivot?",
                Briefing = "context",
                Roles = new List<string> { "Strategist", "Skeptic" },
                Model = "test/model",
            });

            Assert.False(scope.StillValid());
        }

        // ── digest ──

        [Fact]
        public void Digest_Save_InvalidatesACachedRead()
        {
            var store = new ProjectDigestStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.GetDigest(pid));
            Assert.True(scope.WouldBeCached());

            store.SaveDigest(new ProjectDigest { ProjectID = pid, RollingSummary = "rebuilt" });

            Assert.False(scope.StillValid());
        }

        // ── artifacts ──

        [Fact]
        public void Artifact_Save_InvalidatesACachedList()
        {
            var store = new ProjectArtifactStore(_ => { });
            string pid = NewProjectId();

            var scope = CacheFillProbe.Fill(() => store.List(pid));
            Assert.True(scope.WouldBeCached());

            store.Save(pid, new byte[] { 1, 2, 3 }, "image/png", "a screenshot");

            Assert.False(scope.StillValid());
        }

        /// <summary>GetBytes depends on the index rather than the blob: the retention sweep is the only
        /// thing that removes a blob, and it marks the record Degraded in the same locked write.</summary>
        [Fact]
        public void Artifact_Validate_InvalidatesACachedBytesRead()
        {
            var store = new ProjectArtifactStore(_ => { });
            string pid = NewProjectId();
            var saved = store.Save(pid, new byte[] { 1, 2, 3 }, "image/png", "a screenshot");

            var scope = CacheFillProbe.Fill(() => store.GetBytes(pid, saved.ArtifactID));
            Assert.True(scope.WouldBeCached());

            store.Validate(pid, saved.ArtifactID, valid: true, "checked");

            Assert.False(scope.StillValid());
        }
    }
}
