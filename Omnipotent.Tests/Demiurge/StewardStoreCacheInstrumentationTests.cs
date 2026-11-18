using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// Every per-Steward store read by a /demiurge/* GET has to participate in the response cache's
    /// version model. These pin the same contract the settings store broke (see
    /// <see cref="StewardSettingsStoreCacheTests"/>) for its siblings: a read notes a version, a write
    /// bumps it, and one Steward's write never invalidates another's cached read.
    ///
    /// The failure mode is silent — the write succeeds, the API returns the new value, and only the
    /// next cached GET reveals the old one — so it is worth a test per store rather than trusting
    /// that the instrumentation is present.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class StewardStoreCacheInstrumentationTests
    {
        private static string NewStewardId() => "test_" + Guid.NewGuid().ToString("N");

        // ── runtime state (drives /demiurge/list + the detail page's status and disposition) ──

        [Fact]
        public void RuntimeState_Write_InvalidatesACachedRead()
        {
            var store = new StewardRuntimeStateStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.WouldBeCached());
            Assert.True(scope.StillValid());

            store.SetDisposition(pid, StewardExecutionDisposition.Paused);

            Assert.False(scope.StillValid());
        }

        [Fact]
        public void RuntimeState_Write_DoesNotInvalidate_AnotherStewardsCachedRead()
        {
            var store = new StewardRuntimeStateStore(_ => { });
            string mine = NewStewardId();
            string other = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.Get(mine));
            store.SetDisposition(other, StewardExecutionDisposition.Paused);

            Assert.True(scope.StillValid());
        }

        /// <summary>A no-op mutation changes nothing on disk, so it must not invalidate — otherwise
        /// the cache thrashes on every idle wake that re-asserts the state it already has.</summary>
        [Fact]
        public void RuntimeState_UnchangedMutation_LeavesCachedReadValid()
        {
            var store = new StewardRuntimeStateStore(_ => { });
            string pid = NewStewardId();
            store.SetDisposition(pid, StewardExecutionDisposition.Paused);

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            store.SetDisposition(pid, StewardExecutionDisposition.Paused); // already Paused — no commit

            Assert.True(scope.StillValid());
        }

        /// <summary>Fact freshness is a function of wall-clock time, not of any tracked version — a
        /// fact expires with no write to bump, so this read can never be cached.</summary>
        [Fact]
        public void RuntimeState_FreshVerifiedFacts_AreNeverCacheable()
        {
            var store = new StewardRuntimeStateStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.GetFreshVerifiedFacts(pid));

            Assert.False(scope.WouldBeCached());
            Assert.NotNull(scope.UncacheableReason);
        }

        /// <summary>The list renders the runtime projection rather than the whole state; it has to
        /// track the state it summarises, or a paused Steward keeps rendering as running.</summary>
        [Fact]
        public void RuntimeState_Summary_TracksTheState_AndInvalidatesOnWrite()
        {
            var store = new StewardRuntimeStateStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.GetSummary(pid));
            Assert.True(scope.WouldBeCached());
            Assert.Equal(StewardExecutionDisposition.Running, store.GetSummary(pid).Disposition);

            store.SetDisposition(pid, StewardExecutionDisposition.Paused);

            Assert.False(scope.StillValid());
            Assert.Equal(StewardExecutionDisposition.Paused, store.GetSummary(pid).Disposition);
            Assert.Equal(store.Get(pid).Revision, store.GetSummary(pid).Revision);
        }

        // ── budget ledger (the spend figures on every list row) ──

        [Fact]
        public async Task Budget_Spend_TracksTheLedger_AndInvalidatesOnWrite()
        {
            var stewards = new StewardStore(_ => { });
            var events = new StewardEventLogStore(_ => { });
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var ledger = new StewardBudgetLedger(stewards, events, fetcher, _ => { });
            var steward = stewards.CreateSteward("budget cache", "goal", tokenBudgetUsd: 100, 0, 0, 1);

            var scope = CacheFillProbe.Fill(() => ledger.GetSpend(steward.StewardID));
            Assert.True(scope.WouldBeCached());

            await ledger.RecordTokenSpendAsync(steward.StewardID, 1000, 1000, actualCostUsd: 0.25);

            Assert.False(scope.StillValid());
            Assert.Equal(0.25, ledger.GetSpend(steward.StewardID).TokenSpendUsd, 6);
            Assert.Equal(ledger.GetLedger(steward.StewardID).TokenSpendUsd, ledger.GetSpend(steward.StewardID).TokenSpendUsd, 6);
        }

        // ── approval gates (the pending-approvals badge) ──

        [Fact]
        public void Gates_PendingCount_TracksGates_AndInvalidatesOnWrite()
        {
            var events = new StewardEventLogStore(_ => { });
            var gates = new StewardGateManager(events, _ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => gates.CountPending(pid));
            Assert.True(scope.WouldBeCached());
            Assert.Equal(0, gates.CountPending(pid));

            using var cts = new CancellationTokenSource();
            var gate = new StewardGate { StewardID = pid, GateID = "g1", Title = "Spend money", Description = "$5" };
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
            var store = new StewardGrandPlanStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.WouldBeCached());

            store.SubmitVersion(pid, new GrandPlanContent { Mission = "Do the thing." }, "s1", null, material: true, "w1");

            Assert.False(scope.StillValid());
        }

        /// <summary>Approval is the gate Klives actually clicks: if it doesn't invalidate, the plan
        /// reads back as still-pending and the Steward looks stuck in Planning.</summary>
        [Fact]
        public void GrandPlan_Approve_InvalidatesACachedRead()
        {
            var store = new StewardGrandPlanStore(_ => { });
            string pid = NewStewardId();
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
            var store = new StewardCouncilStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.List(pid));
            Assert.True(scope.WouldBeCached());

            store.Create(new CouncilSession
            {
                StewardID = pid,
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
            var store = new StewardDigestStore(_ => { });
            string pid = NewStewardId();

            var scope = CacheFillProbe.Fill(() => store.GetDigest(pid));
            Assert.True(scope.WouldBeCached());

            store.SaveDigest(new StewardDigest { StewardID = pid, RollingSummary = "rebuilt" });

            Assert.False(scope.StillValid());
        }

        // ── artifacts ──

        [Fact]
        public void Artifact_Save_InvalidatesACachedList()
        {
            var store = new StewardArtifactStore(_ => { });
            string pid = NewStewardId();

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
            var store = new StewardArtifactStore(_ => { });
            string pid = NewStewardId();
            var saved = store.Save(pid, new byte[] { 1, 2, 3 }, "image/png", "a screenshot");

            var scope = CacheFillProbe.Fill(() => store.GetBytes(pid, saved.ArtifactID));
            Assert.True(scope.WouldBeCached());

            store.Validate(pid, saved.ArtifactID, valid: true, "checked");

            Assert.False(scope.StillValid());
        }
    }
}
