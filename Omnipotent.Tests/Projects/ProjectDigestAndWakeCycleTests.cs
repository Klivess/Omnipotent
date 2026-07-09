using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    public class ProjectDigestAndWakeCycleTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        private static Project NewProject(string pid) => new()
        {
            ProjectID = pid,
            Name = "Test project",
            Goal = "Run a successful dropshipping store",
            TokenBudgetUsd = 100,
            MoneyBudgetUsd = 500,
            MoneyAutonomousThresholdUsd = 25,
            SubAgentCap = 5,
        };

        [Fact]
        public void Digest_RoundTrips()
        {
            var store = new ProjectDigestStore(_ => { });
            string pid = NewProjectId();
            var d = store.GetDigest(pid);
            d.CurrentPlan = "find a niche";
            d.OrgChart = "commander only";
            d.ActiveWakeID = "wake1";
            d.LastDigestedSequence = 42;
            store.SaveDigest(d);

            var loaded = new ProjectDigestStore(_ => { }).GetDigest(pid);
            Assert.Equal("find a niche", loaded.CurrentPlan);
            Assert.Equal("commander only", loaded.OrgChart);
            Assert.Equal("wake1", loaded.ActiveWakeID);
            Assert.Equal(42, loaded.LastDigestedSequence);
        }

        [Fact]
        public void DigestsWithActiveWakes_AreFoundForCrashRecovery()
        {
            var store = new ProjectDigestStore(_ => { });
            string pid = NewProjectId();
            var d = store.GetDigest(pid);
            d.ActiveWakeID = "interrupted-wake";
            store.SaveDigest(d);

            var actives = new ProjectDigestStore(_ => { }).AllDigestsWithActiveWakes();
            Assert.Contains(actives, x => x.ProjectID == pid && x.ActiveWakeID == "interrupted-wake");
        }

        [Fact]
        public async Task RebuildDigest_ParsesSectionedResponse_AndAdvancesWatermark()
        {
            string pid = NewProjectId();
            var digests = new ProjectDigestStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var project = NewProject(pid);

            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "researched three niches" });
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderMessage, Author = "commander", Text = "picked pet supplies" });

            string modelResponse =
                "## PLAN\nvalidate pet-supplies suppliers\n" +
                "## ORG\ncommander + one text-tier researcher\n" +
                "## BUDGET\n$4 of $100 tokens spent\n" +
                "## OPEN\nawaiting supplier reply\n" +
                "## SUMMARY\nProject started; niche chosen: pet supplies.";
            var rebuilt = await digests.RebuildDigestAsync(project, log, _ => Task.FromResult<string?>(modelResponse));

            Assert.NotNull(rebuilt);
            Assert.Equal("validate pet-supplies suppliers", rebuilt!.CurrentPlan);
            Assert.Equal("commander + one text-tier researcher", rebuilt.OrgChart);
            Assert.Equal("$4 of $100 tokens spent", rebuilt.BudgetState);
            Assert.Equal("awaiting supplier reply", rebuilt.OpenThreads);
            Assert.Equal(2, rebuilt.LastDigestedSequence);

            // The rebuild itself logged a digest-rebuilt event.
            var events = log.ReadSince(pid, 2);
            Assert.Contains(events, e => e.Type == ProjectEventTypes.DigestRebuilt);
        }

        [Fact]
        public async Task RebuildDigest_UnstructuredResponse_DegradesToRollingSummary()
        {
            string pid = NewProjectId();
            var digests = new ProjectDigestStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var project = NewProject(pid);
            var seeded = digests.GetDigest(pid);
            seeded.CurrentPlan = "original plan";
            digests.SaveDigest(seeded);
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "something happened" });

            var rebuilt = await digests.RebuildDigestAsync(project, log, _ => Task.FromResult<string?>("just some prose with no headers"));

            Assert.NotNull(rebuilt);
            Assert.Equal("original plan", rebuilt!.CurrentPlan); // structured fields carried over
            Assert.Equal("just some prose with no headers", rebuilt.RollingSummary);
        }

        [Fact]
        public async Task WakeSeed_ContainsDigestRecentEventsAndTrigger()
        {
            string pid = NewProjectId();
            var log = new ProjectEventLogStore(_ => { });
            var digests = new ProjectDigestStore(_ => { });
            var retrieval = new ProjectRetrievalIndex(log);
            var wake = new ProjectWakeCycle(log, digests, retrieval);
            var project = NewProject(pid);

            var d = digests.GetDigest(pid);
            d.CurrentPlan = "validate suppliers";
            digests.SaveDigest(d);
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderMessage, Author = "commander", Text = "shortlisted supplier Acme" });

            string seed = await wake.BuildWakeSeed(project, "Email from Acme: quote attached");

            Assert.Contains("Run a successful dropshipping store", seed);
            Assert.Contains("validate suppliers", seed);
            Assert.Contains("shortlisted supplier Acme", seed);
            Assert.Contains("Email from Acme: quote attached", seed);
        }

        [Fact]
        public async Task WakeSeed_RetrievalReachesPastTheRecentWindow()
        {
            string pid = NewProjectId();
            var log = new ProjectEventLogStore(_ => { });
            var digests = new ProjectDigestStore(_ => { });
            var retrieval = new ProjectRetrievalIndex(log);
            var wake = new ProjectWakeCycle(log, digests, retrieval);
            var project = NewProject(pid);

            // Deep event that will fall outside the recent window once the digest watermark passes it.
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.ToolResult, Author = "agent", Text = "warehouse zebra credentials stored in vault" });
            for (int i = 0; i < 20; i++)
                log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"routine heartbeat {i}" });

            // Digest watermark past the deep event → it is no longer in the recent window.
            var d = digests.GetDigest(pid);
            d.LastDigestedSequence = 5;
            digests.SaveDigest(d);

            string seed = await wake.BuildWakeSeed(project, "need the warehouse zebra credentials");
            Assert.Contains("RETRIEVED FROM THE FULL LOG", seed);
            Assert.Contains("zebra", seed);
        }
    }

    public class ProjectRetrievalIndexTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Search_RanksTermMatchesAboveNoise()
        {
            var log = new ProjectEventLogStore(_ => { });
            var index = new ProjectRetrievalIndex(log);
            string pid = NewProjectId();

            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "checked shipping rates for europe" });
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "supplier acme sent the invoice for the pallet" });
            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "daily standup nothing new" });

            var hits = index.Search(pid, "acme invoice");
            Assert.NotEmpty(hits);
            Assert.Contains("acme", hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PushIngest_KeepsIndexFreshWithoutRescan()
        {
            var log = new ProjectEventLogStore(_ => { });
            var index = new ProjectRetrievalIndex(log);
            log.EventAppended += index.Ingest;
            string pid = NewProjectId();

            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "the quarterly kraken report is ready" });
            var hits = index.Search(pid, "kraken report");
            Assert.Single(hits);
        }

        [Fact]
        public void MixedPushAndPull_DoesNotDoubleIngest()
        {
            var log = new ProjectEventLogStore(_ => { });
            var index = new ProjectRetrievalIndex(log);
            string pid = NewProjectId();

            log.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "unique flamingo event" });
            index.EnsureFresh(pid); // pull first
            // now simulate the push arriving late (same event)
            index.Ingest(new ProjectEvent { ProjectID = pid, Sequence = 1, EventID = "dup", Type = ProjectEventTypes.Status, Text = "unique flamingo event" });

            var hits = index.Search(pid, "flamingo");
            Assert.Single(hits);
        }
    }
}
