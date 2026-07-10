using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Grand Plan store: versioned strategic plan Klives approves before work begins. Versions are
    /// append-only and monotonic; approving one supersedes the prior approved version.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectGrandPlanStoreTests
    {
        private static ProjectGrandPlanStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Submit_MaterialVersion_IsPendingApproval_NotYetCurrent()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var v = store.SubmitVersion(pid, "# Mission\nDo the thing.", "Do the thing", null, material: true, "wake1");
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
            store.SubmitVersion(pid, "plan v1", "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "gate1", "looks good");
            Assert.True(store.HasApprovedPlan(pid));
            Assert.Equal(1, store.GetCurrentApproved(pid)!.Version);

            store.SubmitVersion(pid, "plan v2", "s2", "sharper", material: true, "w2");
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
            store.SubmitVersion(pid, "v1", "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);
            store.SubmitVersion(pid, "v2", "s2", "revise", material: true, "w2");
            store.MarkRejected(pid, 2, "g2", "not yet");

            Assert.Equal(1, store.GetCurrentApproved(pid)!.Version);
            Assert.Equal(GrandPlanVersionStatus.Rejected, store.Get(pid).Versions.First(v => v.Version == 2).Status);
        }

        [Fact]
        public void NonMaterialAmendment_ImmediatelyApproved_AndSupersedes()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.SubmitVersion(pid, "v1", "s1", null, material: true, "w1");
            store.MarkApproved(pid, 1, "g1", null);
            var v2 = store.SubmitVersion(pid, "v2 tactical", "s2", "tweak", material: false, "w2");

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
                Assert.Equal(i, store.SubmitVersion(pid, $"v{i}", $"s{i}", null, material: true, "w").Version);
        }

        [Fact]
        public void DescribeForSeed_ReflectsApprovedSummary_EmptyWhenNone()
        {
            var store = NewStore();
            string pid = NewProjectId();
            Assert.Equal("", store.DescribeForSeed(pid));

            store.SubmitVersion(pid, "v1", "Ship the MVP by Q3", null, material: true, "w1");
            Assert.Equal("", store.DescribeForSeed(pid)); // still pending
            store.MarkApproved(pid, 1, "g1", null);
            var seed = store.DescribeForSeed(pid);
            Assert.Contains("GRAND PLAN v1", seed);
            Assert.Contains("Ship the MVP by Q3", seed);
        }

        [Fact]
        public void SurvivesNewStoreInstance()
        {
            string pid = NewProjectId();
            NewStore().SubmitVersion(pid, "persisted", "sum", null, material: true, "w1");
            NewStore().MarkApproved(pid, 1, "g1", "ok");
            Assert.True(NewStore().HasApprovedPlan(pid));
            Assert.Equal("persisted", NewStore().GetCurrentApproved(pid)!.Markdown);
        }

        [Fact]
        public void EmptyMarkdown_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                NewStore().SubmitVersion(NewProjectId(), "  ", "s", null, material: true, "w"));
        }
    }
}
