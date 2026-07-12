using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// query_events: the time-indexed read of a project's own history. An agent must be able to
    /// answer "what happened in <window>" with real filters, not by hoping the wake seed's recent
    /// window happens to cover it.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectQueryEventsTests
    {
        private static (ProjectCommanderTools tools, ProjectEventLogStore log, Project project) NewSetup()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, 5);
            var gates = new ProjectGateManager(log, _ => { });
            var digests = new ProjectDigestStore(_ => { });
            var subAgents = new ProjectSubAgentManager(store, log);
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            var vault = new ProjectVault(_ => { });
            var tools = new ProjectCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "w1");
            return (tools, log, p);
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
        public async Task QueryEvents_FiltersByTimeWindow_WithFullStamps()
        {
            var (tools, log, p) = NewSetup();
            Seed(log, p.ProjectID, "ancient work", hoursAgo: 100);
            Seed(log, p.ProjectID, "overnight deploy fixed", hoursAgo: 8);
            Seed(log, p.ProjectID, "fresh checkpoint", hoursAgo: 1);

            var result = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "24h" }), CancellationToken.None);

            Assert.Contains("overnight deploy fixed", result.ResultText);
            Assert.Contains("fresh checkpoint", result.ResultText);
            Assert.DoesNotContain("ancient work", result.ResultText);
            // Event lines carry full-date UTC stamps so the answer is temporally explicit.
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", result.ResultText);
        }

        [Fact]
        public async Task QueryEvents_FiltersByAuthorTypeAndText()
        {
            var (tools, log, p) = NewSetup();
            Seed(log, p.ProjectID, "worker found the bug", hoursAgo: 2, type: ProjectEventTypes.AgentMessage, author: "agent");
            Seed(log, p.ProjectID, "commander planned the fix", hoursAgo: 2);
            Seed(log, p.ProjectID, "worker deployed the fix", hoursAgo: 1, type: ProjectEventTypes.AgentMessage, author: "agent");

            var byAuthor = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "24h", author = "agent" }), CancellationToken.None);
            Assert.Contains("found the bug", byAuthor.ResultText);
            Assert.DoesNotContain("planned the fix", byAuthor.ResultText);

            var byText = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "24h", contains = "deployed" }), CancellationToken.None);
            Assert.Contains("deployed the fix", byText.ResultText);
            Assert.DoesNotContain("found the bug", byText.ResultText);
        }

        [Fact]
        public async Task QueryEvents_EmptyWindowAndBadInput_AreExplicit()
        {
            var (tools, log, p) = NewSetup();
            Seed(log, p.ProjectID, "only old news", hoursAgo: 72);

            var empty = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "1h" }), CancellationToken.None);
            Assert.Contains("No events matched", empty.ResultText);

            var bad = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "whenever" }), CancellationToken.None);
            Assert.Contains("Could not parse", bad.ResultText);
            Assert.Contains("Current time", bad.ResultText); // teaches the clock while rejecting
        }

        [Fact]
        public async Task QueryEvents_OverMax_KeepsTheNewest()
        {
            var (tools, log, p) = NewSetup();
            for (int i = 0; i < 10; i++)
                Seed(log, p.ProjectID, $"step {i}", hoursAgo: 10 - i);

            var result = await tools.DispatchAsync("query_events",
                JsonConvert.SerializeObject(new { from = "24h", max = 3 }), CancellationToken.None);

            Assert.Contains("10 event(s)", result.ResultText);
            Assert.Contains("most recent 3", result.ResultText);
            Assert.Contains("step 9", result.ResultText);
            Assert.DoesNotContain("step 0", result.ResultText);
        }
    }
}
