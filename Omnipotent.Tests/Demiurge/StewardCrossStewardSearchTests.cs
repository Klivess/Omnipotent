// Test file for feature #5: cross-Steward retrieval (SearchAll + cross_steward_search tool).
using Newtonsoft.Json;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    [Collection("DemiurgeSerial")]
    public class StewardCrossStewardSearchTests
    {
        private static (StewardCommanderTools tools, StewardEventLogStore log, StewardStore store, Steward steward) NewSetup()
        {
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var retrieval = new StewardRetrievalIndex(log);
            var p = store.CreateSteward("t", "goal", 100, 100, 10, 5);
            var gates = new StewardGateManager(log, _ => { });
            var digests = new StewardDigestStore(_ => { });
            var subAgents = new StewardSubAgentManager(store, log);
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new StewardBudgetLedger(store, log, fetcher, _ => { });
            var vault = new StewardVault(_ => { });
            var tools = new StewardCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "w1")
            {
                Retrieval = retrieval,
            };
            return (tools, log, store, p);
        }

        private static void Seed(StewardEventLogStore log, string pid, string text, double hoursAgo,
            string type = StewardEventTypes.Status, string author = "commander")
            => log.Append(new StewardEvent
            {
                StewardID = pid,
                Type = type,
                Author = author,
                Text = text,
                Timestamp = DateTime.UtcNow.AddHours(-hoursAgo),
            });

        [Fact]
        public void SearchAll_FindsHitsAcrossMultipleStewards_WithAnnotation()
        {
            var (_, log, _, _) = NewSetup();
            var index = new StewardRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            string pidB = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} deployed the bridge", 1);
            Seed(log, pidB, $"{marker} bridge rolled back", 2);
            Seed(log, pidA, "daily standup nothing new", 3);
            index.EnsureAllFresh();

            var hits = index.SearchAll(marker, 20);
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.StewardID == pidA);
            Assert.Contains(hits, h => h.StewardID == pidB);
            Assert.All(hits, h => Assert.False(string.IsNullOrEmpty(h.StewardID)));
            Assert.Contains("deployed", hits.First(h => h.StewardID == pidA).Snippet, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rolled back", hits.First(h => h.StewardID == pidB).Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SearchAll_RanksByBm25AcrossStewards()
        {
            var (_, log, _, _) = NewSetup();
            var index = new StewardRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            string pidB = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} kraken the kraken kraken the kraken kraken", 1);
            Seed(log, pidB, $"{marker} kraken appeared once in this otherwise ordinary line", 1);
            index.EnsureAllFresh();

            var hits = index.SearchAll(marker, 20);
            Assert.Equal(2, hits.Count);
            Assert.Equal(pidA, hits[0].StewardID);
            Assert.Equal(pidB, hits[1].StewardID);
            Assert.True(hits[0].Score >= hits[1].Score);
        }

        [Fact]
        public void SearchAll_SinceExcludesOlderEvents()
        {
            var (_, log, _, _) = NewSetup();
            var index = new StewardRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} old event from long ago", 2);
            Seed(log, pidA, $"{marker} recent event", 0.25);
            index.EnsureAllFresh();

            var all = index.SearchAll(marker, 20);
            Assert.Equal(2, all.Count);

            var recentOnly = index.SearchAll(marker, 20, sinceUtc: DateTime.UtcNow.AddMinutes(-60));
            Assert.Single(recentOnly);
            Assert.Contains("recent event", recentOnly[0].Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PerStewardSearch_Unchanged_NoLeakOfCrossStewardHits()
        {
            var (_, log, _, _) = NewSetup();
            var index = new StewardRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            string pidB = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} alpha only lives here", 1);
            Seed(log, pidB, "zebra noise that shares no marker", 1);

            var hits = index.Search(pidA, marker, 12);
            Assert.Single(hits);
            Assert.Contains("alpha only lives here", hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CrossStewardSearch_IsAnAllowedTextTierTool()
        {
            var router = new StewardTierRouter(new StewardSettingsStore());
            Assert.True(router.IsToolAllowed(StewardAgentTier.Text, "cross_steward_search"));
            var names = StewardCommanderAgent.BuildCoreToolDefinitions().Select(t => t.function.name).ToList();
            Assert.Contains("cross_steward_search", names);
        }

        [Fact]
        public async Task CrossStewardSearchTool_ReturnsAnnotatedHits()
        {
            var (tools, log, store, p) = NewSetup();
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            var other = store.CreateSteward("other", "other goal", 100, 100, 10, 5);
            Seed(log, p.StewardID, $"{marker} commander found the leak", 1, type: StewardEventTypes.CommanderMessage);
            Seed(log, other.StewardID, $"{marker} worker patched the leak", 2, type: StewardEventTypes.AgentMessage, author: "agent");

            var result = await tools.DispatchAsync("cross_steward_search",
                JsonConvert.SerializeObject(new { query = marker, max = 10 }), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains(marker, result.ResultText);
            Assert.Contains(p.StewardID, result.ResultText);
            Assert.Contains(other.StewardID, result.ResultText);
            Assert.Contains("found the leak", result.ResultText);
            Assert.Contains("patched the leak", result.ResultText);
            Assert.Contains("other", result.ResultText); // Steward name annotation

            var bad = await tools.DispatchAsync("cross_steward_search", "{}", CancellationToken.None);
            Assert.Contains("Provide a 'query'", bad.ResultText);
        }

        [Fact]
        public async Task CrossStewardSearchTool_WithoutRetrievalIsExplicit()
        {
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var p = store.CreateSteward("t", "goal", 100, 100, 10, 5);
            var tools = new StewardCommanderTools(p, log, new StewardDigestStore(_ => { }),
                new StewardSubAgentManager(store, log), new StewardGateManager(log, _ => { }),
                new StewardBudgetLedger(store, log, new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { }),
                new StewardVault(_ => { }), store, "commander", "w1");

            var result = await tools.DispatchAsync("cross_steward_search",
                JsonConvert.SerializeObject(new { query = "anything" }), CancellationToken.None);
            Assert.Contains("unavailable", result.ResultText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
