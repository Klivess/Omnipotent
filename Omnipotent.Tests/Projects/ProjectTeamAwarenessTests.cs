using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Why no agent ever messaged another: send_agent_message has always been available to every
    /// tier, but a worker's wake seed contained no roster at all and its event window was filtered to
    /// its own AgentID. With no peer IDs and no sight of anyone else acting, lateral coordination was
    /// mechanically impossible regardless of what the prompt encouraged. These tests pin the roster
    /// and team-activity views a worker now receives.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectTeamAwarenessTests
    {
        private static (ProjectSubAgentManager mgr, ProjectStore store, string pid) NewSetup(int cap = 12)
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, cap);
            var mgr = new ProjectSubAgentManager(store, log);
            mgr.EnsureCommander(p.ProjectID);
            return (mgr, store, p.ProjectID);
        }

        [Fact]
        public void WorkerView_ListsEveryPeerWithARoutableId()
        {
            var (mgr, _, pid) = NewSetup();
            var me = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "writer", "draft the posts");
            var peer = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "researcher", "gather the sources");
            mgr.UpdateWorkState(pid, peer.AgentID, ProjectAgentWorkStatus.Running, "found 12 sources in shared/sources.md");

            string view = mgr.DescribeTaskForce(pid, 12, viewerAgentID: me.AgentID);

            // The peer is addressable: both its ID and its role appear, and so does what it just did.
            Assert.Contains(peer.AgentID, view);
            Assert.Contains("researcher", view);
            Assert.Contains("shared/sources.md", view);
            // The commander is on the roster too — upward reporting uses the same primitive.
            Assert.Contains("commander", view);
        }

        [Fact]
        public void WorkerView_MarksTheViewerSoItDoesNotMessageItself()
        {
            var (mgr, _, pid) = NewSetup();
            var me = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "writer");
            mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "researcher");

            string view = mgr.DescribeTaskForce(pid, 12, viewerAgentID: me.AgentID);
            var mine = view.Split('\n').First(l => l.Contains(me.AgentID));

            Assert.Contains("(you)", mine);
            Assert.Equal(1, view.Split("(you)").Length - 1);
        }

        [Fact]
        public void WorkerView_OmitsRetiredAgents()
        {
            var (mgr, _, pid) = NewSetup();
            var gone = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "temp");
            mgr.Retire(pid, gone.AgentID);

            string view = mgr.DescribeTaskForce(pid, 12);

            // Messaging a retired agent is rejected, so advertising one would only waste a tool call.
            Assert.DoesNotContain(gone.AgentID, view);
        }

        [Fact]
        public void IdleWorkerIsFlaggedForTheCommanderToActOn()
        {
            var (mgr, _, pid) = NewSetup();
            var idle = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "spare");
            mgr.UpdateWorkState(pid, idle.AgentID, ProjectAgentWorkStatus.Idle);

            string view = mgr.DescribeTaskForce(pid, 12);

            Assert.Contains("IDLE", view);
            Assert.Contains("task it or retire it", view);
        }

        [Fact]
        public void SilentWorkerIsFlaggedWithItsAge()
        {
            var (mgr, _, pid) = NewSetup();
            var quiet = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "scraper", "scrape the feed");
            mgr.MarkWakeStarted(pid, quiet.AgentID, DateTime.UtcNow.AddHours(-2));

            string view = mgr.DescribeTaskForce(pid, 12);

            Assert.Contains("SILENT", view);
            Assert.Contains(quiet.AgentID, view);
        }

        [Fact]
        public void NeverWokenWorkerIsDistinguishedFromASilentOne()
        {
            var (mgr, _, pid) = NewSetup();
            var never = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "unused", "something");

            string view = mgr.DescribeTaskForce(pid, 12);

            // "spawned but never given work" is a different Commander action from "chase it".
            Assert.Contains("NEVER WOKEN", view);
            Assert.Contains(never.AgentID, view);
        }

        [Fact]
        public void MissionKindSurvivesAReassignment()
        {
            var (mgr, _, pid) = NewSetup();
            var w = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "poster", "post daily",
                ProjectAgentMissionKind.Standing);

            // Reassigning without naming a mission must not silently demote a standing beat to a task,
            // which would make it retirable and let the beat stop.
            mgr.AssignObjective(pid, w.AgentID, "post twice daily", new[] { "m1" });
            Assert.Equal(ProjectAgentMissionKind.Standing,
                mgr.ListActive(pid).First(a => a.AgentID == w.AgentID).MissionKind);

            mgr.AssignObjective(pid, w.AgentID, "one-off cleanup", new[] { "m2" }, null, ProjectAgentMissionKind.Task);
            Assert.Equal(ProjectAgentMissionKind.Task,
                mgr.ListActive(pid).First(a => a.AgentID == w.AgentID).MissionKind);
        }

        [Fact]
        public void MessagingToolIsDirectionNeutralAndAdvertisesTheBroadcast()
        {
            var tools = ProjectCommanderAgent.BuildCoreToolDefinitions();
            var send = tools.First(t => t.function.name == "send_agent_message");

            // The old description said "Send a message to a sub-agent", which reads as downward-only
            // to a worker that needs the same tool to report upward and sideways.
            Assert.Contains("commander", send.function.description);
            Assert.Contains("peer", send.function.description);
            Assert.Contains("team", send.function.description);
        }

        [Fact]
        public void SpawnToolOffersTheMissionKind()
        {
            var tools = ProjectCommanderAgent.BuildCoreToolDefinitions();
            var spawn = tools.First(t => t.function.name == "spawn_sub_agent");
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(spawn.function.parameters);

            Assert.Contains("mission", json);
            Assert.Contains("standing", json);
        }

        [Fact]
        public void FoldedManageAgentsCarriesMission_ScopedToBothOpsThatAcceptIt()
        {
            // spawn and assign_work both declare 'mission', and they fold into one tool. The facade
            // must surface it once with op-scoped descriptions rather than letting one op's wording
            // silently stand in for the other's.
            var offered = ProjectToolFacade.Fold(ProjectCommanderAgent.BuildCoreToolDefinitions());
            var manage = offered.First(t => t.function.name == "manage_agents");
            var schema = Newtonsoft.Json.Linq.JObject.FromObject(manage.function.parameters);
            string mission = (string?)schema["properties"]?["mission"]?["description"] ?? "";

            Assert.Contains("spawn", mission);
            Assert.Contains("assign_work", mission);
            Assert.Contains("standing", mission);

            // And it still resolves back to the right canonical tool with the argument intact.
            var unfolded = ProjectToolFacade.Unfold("manage_agents",
                """{"op":"spawn","role":"poster","tier":"Text","objective":"post daily","mission":"standing"}""");
            Assert.True(unfolded.IsValid);
            Assert.Equal("spawn_sub_agent", unfolded.ToolName);
            Assert.Contains("standing", unfolded.ArgumentsJson);
        }

        [Fact]
        public void CommanderPromptCarriesTheMusterDuty()
        {
            string prompt = ProjectCommanderAgent.BuildSystemPrompt(
                new Project { ProjectID = "p1", Name = "t", Goal = "g" }, visionEnabled: true);

            Assert.Contains("MUSTER YOUR TASK FORCE EVERY WAKE", prompt);
            Assert.Contains("An idle slot is wasted throughput", prompt);
            Assert.Contains("standing", prompt);
            // Reports must be answered — silence is what made the hierarchy feel absent.
            Assert.Contains("never silence", prompt);
        }
    }
}
