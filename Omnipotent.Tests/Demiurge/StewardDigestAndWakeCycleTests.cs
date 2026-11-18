using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    public class StewardDigestAndWakeCycleTests
    {
        private static string NewStewardId() => "test_" + Guid.NewGuid().ToString("N");

        private static Steward NewSteward(string pid) => new()
        {
            StewardID = pid,
            Name = "Test Steward",
            Goal = "Run a successful dropshipping store",
            TokenBudgetUsd = 100,
            MoneyBudgetUsd = 500,
            MoneyAutonomousThresholdUsd = 25,
            SubAgentCap = 5,
        };

        [Fact]
        public void Digest_RoundTrips()
        {
            var store = new StewardDigestStore(_ => { });
            string pid = NewStewardId();
            var d = store.GetDigest(pid);
            d.CurrentPlan = "find a niche";
            d.OrgChart = "commander only";
            d.ActiveWakeID = "wake1";
            d.LastDigestedSequence = 42;
            store.SaveDigest(d);

            var loaded = new StewardDigestStore(_ => { }).GetDigest(pid);
            Assert.Equal("find a niche", loaded.CurrentPlan);
            Assert.Equal("commander only", loaded.OrgChart);
            Assert.Equal("wake1", loaded.ActiveWakeID);
            Assert.Equal(42, loaded.LastDigestedSequence);
        }

        [Fact]
        public void DigestsWithActiveWakes_AreFoundForCrashRecovery()
        {
            var store = new StewardDigestStore(_ => { });
            string pid = NewStewardId();
            var d = store.GetDigest(pid);
            d.ActiveWakeID = "interrupted-wake";
            store.SaveDigest(d);

            var actives = new StewardDigestStore(_ => { }).AllDigestsWithActiveWakes();
            Assert.Contains(actives, x => x.StewardID == pid && x.ActiveWakeID == "interrupted-wake");
        }

        [Fact]
        public async Task RebuildDigest_ParsesSectionedResponse_AndAdvancesWatermark()
        {
            string pid = NewStewardId();
            var digests = new StewardDigestStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var steward = NewSteward(pid);

            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "researched three niches" });
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.CommanderMessage, Author = "commander", Text = "picked pet supplies" });

            string modelResponse =
                "## PLAN\nvalidate pet-supplies suppliers\n" +
                "## ORG\ncommander + one text-tier researcher\n" +
                "## BUDGET\n$4 of $100 tokens spent\n" +
                "## OPEN\nawaiting supplier reply\n" +
                "## SUMMARY\nSteward started; niche chosen: pet supplies.";
            var rebuilt = await digests.RebuildDigestAsync(steward, log, _ => Task.FromResult<string?>(modelResponse));

            Assert.NotNull(rebuilt);
            Assert.Equal("validate pet-supplies suppliers", rebuilt!.CurrentPlan);
            Assert.Equal("commander + one text-tier researcher", rebuilt.OrgChart);
            Assert.Equal("$4 of $100 tokens spent", rebuilt.BudgetState);
            Assert.Equal("awaiting supplier reply", rebuilt.OpenThreads);
            Assert.Equal(2, rebuilt.LastDigestedSequence);

            // The rebuild itself logged a digest-rebuilt event.
            var events = log.ReadSince(pid, 2);
            Assert.Contains(events, e => e.Type == StewardEventTypes.DigestRebuilt);
        }

        [Fact]
        public async Task RebuildDigest_UnstructuredResponse_DegradesToRollingSummary()
        {
            string pid = NewStewardId();
            var digests = new StewardDigestStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var steward = NewSteward(pid);
            var seeded = digests.GetDigest(pid);
            seeded.CurrentPlan = "original plan";
            digests.SaveDigest(seeded);
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "something happened" });

            var rebuilt = await digests.RebuildDigestAsync(steward, log, _ => Task.FromResult<string?>("just some prose with no headers"));

            Assert.NotNull(rebuilt);
            Assert.Equal("original plan", rebuilt!.CurrentPlan); // structured fields carried over
            Assert.Equal("just some prose with no headers", rebuilt.RollingSummary);
        }

        [Fact]
        public async Task WakeSeed_ContainsDigestRecentEventsAndTrigger()
        {
            string pid = NewStewardId();
            var log = new StewardEventLogStore(_ => { });
            var digests = new StewardDigestStore(_ => { });
            var retrieval = new StewardRetrievalIndex(log);
            var wake = new StewardWakeCycle(log, digests, retrieval);
            var steward = NewSteward(pid);

            var d = digests.GetDigest(pid);
            d.CurrentPlan = "validate suppliers";
            digests.SaveDigest(d);
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.CommanderMessage, Author = "commander", Text = "shortlisted supplier Acme" });

            string seed = await wake.BuildWakeSeed(steward, "Email from Acme: quote attached");

            Assert.Contains("Run a successful dropshipping store", seed);
            Assert.Contains("validate suppliers", seed);
            Assert.Contains("shortlisted supplier Acme", seed);
            Assert.Contains("Email from Acme: quote attached", seed);
            Assert.Equal(0, log.FullIndexBuilds);
        }

        [Fact]
        public async Task WakeSeed_RetrievalReachesPastTheRecentWindow()
        {
            string pid = NewStewardId();
            var log = new StewardEventLogStore(_ => { });
            var digests = new StewardDigestStore(_ => { });
            var retrieval = new StewardRetrievalIndex(log);
            var wake = new StewardWakeCycle(log, digests, retrieval);
            var steward = NewSteward(pid);

            // Deep event that will fall outside the recent window once the digest watermark passes it.
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.ToolResult, Author = "agent", Text = "warehouse zebra credentials stored in vault" });
            for (int i = 0; i < 20; i++)
                log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = $"routine heartbeat {i}" });

            // Digest watermark past the deep event → it is no longer in the recent window.
            var d = digests.GetDigest(pid);
            d.LastDigestedSequence = 5;
            digests.SaveDigest(d);

            string seed = await wake.BuildWakeSeed(steward, "need the warehouse zebra credentials");
            Assert.Contains("RETRIEVED FROM THE FULL LOG", seed);
            Assert.Contains("zebra", seed);
        }
    }

    public class StewardRetrievalIndexTests
    {
        private static string NewStewardId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void RefreshWaitingForAnEventLogDoesNotBlockPushIngest()
        {
            var log = new StewardEventLogStore(_ => { });
            var index = new StewardRetrievalIndex(log);
            var evt = log.Append(new StewardEvent
            {
                StewardID = NewStewardId(), Type = StewardEventTypes.Status, Text = "concurrent retrieval regression",
            });
            string blockedSteward = NewStewardId();
            object eventGate = typeof(StewardEventLogStore)
                .GetMethod("LockFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(log, new object[] { blockedSteward })!;
            Exception? failure = null;
            var refresh = new Thread(() =>
            {
                try { index.EnsureFresh(blockedSteward); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            bool ingestedWhileBlocked = false;
            Task? ingest = null;
            try
            {
                lock (eventGate)
                {
                    refresh.Start();
                    Assert.True(SpinWait.SpinUntil(
                        () => (refresh.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)));
                    ingest = Task.Run(() => index.Ingest(evt));
                    ingestedWhileBlocked = ingest.Wait(TimeSpan.FromSeconds(2));
                }
            }
            finally
            {
                Assert.True(refresh.Join(TimeSpan.FromSeconds(5)));
                ingest?.GetAwaiter().GetResult();
            }
            Assert.Null(failure);
            Assert.True(ingestedWhileBlocked, "Refresh held the retrieval lock while waiting for the event log.");
            Assert.Single(index.Search(evt.StewardID, "regression"));
        }

        [Fact]
        public void Search_RanksTermMatchesAboveNoise()
        {
            var log = new StewardEventLogStore(_ => { });
            var index = new StewardRetrievalIndex(log);
            string pid = NewStewardId();

            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "checked shipping rates for europe" });
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "supplier acme sent the invoice for the pallet" });
            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "daily standup nothing new" });

            var hits = index.Search(pid, "acme invoice");
            Assert.NotEmpty(hits);
            Assert.Contains("acme", hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PushIngest_KeepsIndexFreshWithoutRescan()
        {
            var log = new StewardEventLogStore(_ => { });
            var index = new StewardRetrievalIndex(log);
            log.EventAppended += index.Ingest;
            string pid = NewStewardId();

            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "the quarterly kraken report is ready" });
            var hits = index.Search(pid, "kraken report");
            Assert.Single(hits);
        }

        [Fact]
        public void MixedPushAndPull_DoesNotDoubleIngest()
        {
            var log = new StewardEventLogStore(_ => { });
            var index = new StewardRetrievalIndex(log);
            string pid = NewStewardId();

            log.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Text = "unique flamingo event" });
            index.EnsureFresh(pid); // pull first
            // now simulate the push arriving late (same event)
            index.Ingest(new StewardEvent { StewardID = pid, Sequence = 1, EventID = "dup", Type = StewardEventTypes.Status, Text = "unique flamingo event" });

            var hits = index.Search(pid, "flamingo");
            Assert.Single(hits);
        }
    }
}
