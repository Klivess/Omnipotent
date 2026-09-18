// Test file for feature #5: cross-project retrieval (SearchAll + cross_project_search tool).
using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    [Collection("ProjectsSerial")]
    public class ProjectCrossProjectSearchTests
    {
        private static (ProjectCommanderTools tools, ProjectEventLogStore log, ProjectStore store, Project project) NewSetup()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var retrieval = new ProjectRetrievalIndex(log);
            var p = store.CreateProject("t", "goal", 100, 100, 10, 5);
            var gates = new ProjectGateManager(log, _ => { });
            var digests = new ProjectDigestStore(_ => { });
            var subAgents = new ProjectSubAgentManager(store, log);
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            var vault = new ProjectVault(_ => { });
            var tools = new ProjectCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "w1")
            {
                Retrieval = retrieval,
            };
            return (tools, log, store, p);
        }

        private static void Seed(ProjectEventLogStore log, string pid, string text, double hoursAgo,
            string type = ProjectEventTypes.Status, string author = "commander")
            => log.Append(new ProjectEvent
            {
                ProjectID = pid,
                Type = type,
                Author = author,
                Text = text,
                Timestamp = DateTime.UtcNow.AddHours(-hoursAgo),
            });

        [Fact]
        public void SearchAll_FindsHitsAcrossMultipleProjects_WithAnnotation()
        {
            var (_, log, _, _) = NewSetup();
            var index = new ProjectRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            string pidB = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} deployed the bridge", 1);
            Seed(log, pidB, $"{marker} bridge rolled back", 2);
            Seed(log, pidA, "daily standup nothing new", 3);
            index.EnsureAllFresh();

            var hits = index.SearchAll(marker, 20);
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.ProjectID == pidA);
            Assert.Contains(hits, h => h.ProjectID == pidB);
            Assert.All(hits, h => Assert.False(string.IsNullOrEmpty(h.ProjectID)));
            Assert.Contains("deployed", hits.First(h => h.ProjectID == pidA).Snippet, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rolled back", hits.First(h => h.ProjectID == pidB).Snippet, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SearchAll_RanksByBm25AcrossProjects()
        {
            var (_, log, _, _) = NewSetup();
            var index = new ProjectRetrievalIndex(log);
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            string pidA = "test_" + Guid.NewGuid().ToString("N");
            string pidB = "test_" + Guid.NewGuid().ToString("N");
            Seed(log, pidA, $"{marker} kraken the kraken kraken the kraken kraken", 1);
            Seed(log, pidB, $"{marker} kraken appeared once in this otherwise ordinary line", 1);
            index.EnsureAllFresh();

            var hits = index.SearchAll(marker, 20);
            Assert.Equal(2, hits.Count);
            Assert.Equal(pidA, hits[0].ProjectID);
            Assert.Equal(pidB, hits[1].ProjectID);
            Assert.True(hits[0].Score >= hits[1].Score);
        }

        [Fact]
        public void SearchAll_SinceExcludesOlderEvents()
        {
            var (_, log, _, _) = NewSetup();
            var index = new ProjectRetrievalIndex(log);
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
        public void PerProjectSearch_Unchanged_NoLeakOfCrossProjectHits()
        {
            var (_, log, _, _) = NewSetup();
            var index = new ProjectRetrievalIndex(log);
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
        public void CrossProjectSearch_IsAnAllowedTextTierTool()
        {
            var router = new ProjectTierRouter(new ProjectSettingsStore());
            Assert.True(router.IsToolAllowed(ProjectAgentTier.Text, "cross_project_search"));
            var names = ProjectCommanderAgent.BuildCoreToolDefinitions().Select(t => t.function.name).ToList();
            Assert.Contains("cross_project_search", names);
        }

        [Fact]
        public async Task CrossProjectSearchTool_ReturnsAnnotatedHits()
        {
            var (tools, log, store, p) = NewSetup();
            string marker = "q" + Guid.NewGuid().ToString("N")[..13];
            var other = store.CreateProject("other", "other goal", 100, 100, 10, 5);
            Seed(log, p.ProjectID, $"{marker} commander found the leak", 1, type: ProjectEventTypes.CommanderMessage);
            Seed(log, other.ProjectID, $"{marker} worker patched the leak", 2, type: ProjectEventTypes.AgentMessage, author: "agent");

            var result = await tools.DispatchAsync("cross_project_search",
                JsonConvert.SerializeObject(new { query = marker, max = 10 }), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains(marker, result.ResultText);
            Assert.Contains(p.ProjectID, result.ResultText);
            Assert.Contains(other.ProjectID, result.ResultText);
            Assert.Contains("found the leak", result.ResultText);
            Assert.Contains("patched the leak", result.ResultText);
            Assert.Contains("other", result.ResultText); // project name annotation

            var bad = await tools.DispatchAsync("cross_project_search", "{}", CancellationToken.None);
            Assert.Contains("Provide a 'query'", bad.ResultText);
        }

        [Fact]
        public async Task CrossProjectSearchTool_WithoutRetrievalIsExplicit()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, 5);
            var tools = new ProjectCommanderTools(p, log, new ProjectDigestStore(_ => { }),
                new ProjectSubAgentManager(store, log), new ProjectGateManager(log, _ => { }),
                new ProjectBudgetLedger(store, log, new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { }), _ => { }),
                new ProjectVault(_ => { }), store, "commander", "w1");

            var result = await tools.DispatchAsync("cross_project_search",
                JsonConvert.SerializeObject(new { query = "anything" }), CancellationToken.None);
            Assert.Contains("unavailable", result.ResultText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
