using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The live "who is generating right now" tracker behind the Conversation panel's indicator.
    /// Purely in-memory, so these tests touch no disk state. What matters is that it publishes the
    /// phase transitions the UI keys off, never grows without bound on a long turn, and always ends
    /// up empty when work stops — a stranded entry means a permanently "thinking" agent on screen.
    /// </summary>
    public class ProjectAgentActivityTests
    {
        [Fact]
        public void BeginThinking_PublishesThinkingPhase_WithNoPreview()
        {
            var tracker = new ProjectAgentActivityTracker();
            var published = new List<ProjectAgentActivity>();
            tracker.Changed += published.Add;

            tracker.BeginThinking("p1", "commander", "commander", "anthropic/claude-sonnet-4.5");

            var only = Assert.Single(published);
            Assert.Equal(ProjectActivityPhases.Thinking, only.Phase);
            Assert.Equal("commander", only.AgentID);
            Assert.Equal("anthropic/claude-sonnet-4.5", only.Model);
            Assert.Null(only.Preview);
            Assert.Null(only.ToolName);
        }

        [Fact]
        public void FirstToken_FlipsToWriting_AndPublishesImmediately()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            var published = new List<ProjectAgentActivity>();
            tracker.Changed += published.Add;

            tracker.AppendToken("p1", "commander", "Checking the ");

            var only = Assert.Single(published);
            Assert.Equal(ProjectActivityPhases.Writing, only.Phase);
            Assert.Equal("Checking the ", only.Preview);
            Assert.Equal(13, only.GeneratedChars);
        }

        [Fact]
        public void RapidTokens_AreThrottled_ButAlwaysReadableFromTheSnapshot()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            var published = new List<ProjectAgentActivity>();
            tracker.Changed += published.Add;

            for (int i = 0; i < 500; i++) tracker.AppendToken("p1", "commander", "x");

            // A fast stream must not put 500 frames on the socket; the first token always publishes.
            Assert.InRange(published.Count, 1, 5);
            var live = Assert.Single(tracker.ListForProject("p1"));
            Assert.Equal(500, live.GeneratedChars);
            Assert.Equal(ProjectActivityPhases.Writing, live.Phase);
        }

        [Fact]
        public void Preview_KeepsTheTail_Bounded()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.AppendToken("p1", "commander", new string('a', 5000));
            tracker.AppendToken("p1", "commander", "THE-NEWEST-TEXT");

            var live = Assert.Single(tracker.ListForProject("p1"));
            Assert.NotNull(live.Preview);
            // Bounded (plus the leading ellipsis marker) and showing the newest text, not the oldest.
            Assert.True(live.Preview!.Length <= ProjectAgentActivityTracker.PreviewChars + 1);
            Assert.EndsWith("THE-NEWEST-TEXT", live.Preview);
            Assert.Equal(5015, live.GeneratedChars);
        }

        [Fact]
        public void BeginTool_ReplacesTheWritingPreview_WithTheToolBeingRun()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.AppendToken("p1", "commander", "I will search now.");
            tracker.BeginTool("p1", "commander", "web_search", "web_search(query=openrouter status)");

            var live = Assert.Single(tracker.ListForProject("p1"));
            Assert.Equal(ProjectActivityPhases.Tool, live.Phase);
            Assert.Equal("web_search", live.ToolName);
            Assert.Equal("web_search(query=openrouter status)", live.Detail);
            // Stale prose from the finished model turn must not linger under a running tool.
            Assert.Null(live.Preview);
        }

        [Fact]
        public void End_ClearsTheAgent_AndSignalsTheUI()
        {
            var tracker = new ProjectAgentActivityTracker();
            var ended = new List<(string ProjectID, string AgentID)>();
            tracker.Ended += (p, a) => ended.Add((p, a));
            tracker.BeginThinking("p1", "commander", "commander", "m");

            tracker.End("p1", "commander");

            Assert.Empty(tracker.ListForProject("p1"));
            Assert.Equal(("p1", "commander"), Assert.Single(ended));
        }

        [Fact]
        public void TokensAfterEnd_AreIgnored_NotResurrected()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.End("p1", "commander");

            // A late SSE delta from a cancelled request must not put the agent back on the panel.
            tracker.AppendToken("p1", "commander", "orphaned");
            tracker.BeginTool("p1", "commander", "web_search", "web_search()");

            Assert.Empty(tracker.ListForProject("p1"));
        }

        [Fact]
        public void ListForProject_IsScopedToOneProject()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.BeginThinking("p1", "agent-7", "market-researcher", "m");
            tracker.BeginThinking("p2", "commander", "commander", "m");

            var live = tracker.ListForProject("p1");
            Assert.Equal(2, live.Count);
            Assert.All(live, a => Assert.Equal("p1", a.ProjectID));
            Assert.Contains(live, a => a.Role == "market-researcher");
        }

        [Fact]
        public void BeginThinking_AfterAToolRun_ResetsPhaseAndPreview()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.AppendToken("p1", "commander", "first turn prose");
            tracker.BeginTool("p1", "commander", "web_search", "web_search()");

            tracker.BeginThinking("p1", "commander", "commander", "m2");

            var live = Assert.Single(tracker.ListForProject("p1"));
            Assert.Equal(ProjectActivityPhases.Thinking, live.Phase);
            Assert.Null(live.ToolName);
            Assert.Null(live.Detail);
            Assert.Null(live.Preview);
            Assert.Equal(0, live.GeneratedChars);
            Assert.Equal("m2", live.Model);
        }

        [Fact]
        public void APublisherThatThrows_NeverBreaksTheWake()
        {
            var tracker = new ProjectAgentActivityTracker();
            tracker.Changed += _ => throw new InvalidOperationException("socket died");

            // A UI signal is best-effort: it must not propagate into the runner's tool loop.
            tracker.BeginThinking("p1", "commander", "commander", "m");
            tracker.AppendToken("p1", "commander", "hello");
            tracker.BeginTool("p1", "commander", "web_search", "web_search()");

            Assert.Single(tracker.ListForProject("p1"));
        }
    }
}
