using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Why sub-agents used to work for one wake and then go dark: nothing periodic ever reached them
    /// (the service keepalive wakes only the Commander), and the only ways to end a wake were
    /// "finished forever" and "blocked" — so every report a worker made doubled as its resignation.
    /// These tests pin the heartbeat's eligibility and backoff, and the CONTINUING status that lets a
    /// worker report without ending its assignment.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectWorkerLivenessTests
    {
        private static ProjectAgentRecord Worker(
            ProjectAgentWorkStatus status = ProjectAgentWorkStatus.Assigned,
            ProjectAgentMissionKind mission = ProjectAgentMissionKind.Task,
            DateTime? lastWakeAt = null,
            string objective = "do the thing") => new()
            {
                AgentID = "w1", Role = "worker", ParentAgentID = "commander",
                Objective = objective, MissionKind = mission, WorkStatus = status,
                LastWakeAt = lastWakeAt, CreatedAt = DateTime.UtcNow.AddDays(-1),
            };

        private const int Base = 20, Max = 240;

        [Fact]
        public void Heartbeat_WakesAnOpenAssignmentThatHasGoneQuiet()
        {
            var now = DateTime.UtcNow;
            var agent = Worker(lastWakeAt: now.AddMinutes(-21));
            Assert.True(ProjectWorkerHeartbeat.ShouldWake(agent, isAwake: false, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_LeavesAnAgentAloneInsideTheQuietPeriod()
        {
            var now = DateTime.UtcNow;
            var agent = Worker(lastWakeAt: now.AddMinutes(-5));
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(agent, isAwake: false, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_SkipsAgentsThatAreAlreadyAwake()
        {
            var now = DateTime.UtcNow;
            var agent = Worker(lastWakeAt: now.AddHours(-4));
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(agent, isAwake: true, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_SkipsRetiredAgentsAndTheCommander()
        {
            var now = DateTime.UtcNow;
            var retired = Worker(lastWakeAt: now.AddHours(-4));
            retired.Retired = true;
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(retired, false, now, Base, Max, 0));

            var commander = new ProjectAgentRecord
            {
                AgentID = "commander", Role = "commander", Objective = "coordinate",
                WorkStatus = ProjectAgentWorkStatus.Running, LastWakeAt = now.AddHours(-4),
            };
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(commander, false, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_SkipsAFinishedBoundedWorker_ButNotAStandingOne()
        {
            var now = DateTime.UtcNow;
            var finishedTask = Worker(ProjectAgentWorkStatus.Completed, ProjectAgentMissionKind.Task, now.AddHours(-4));
            var standing = Worker(ProjectAgentWorkStatus.Completed, ProjectAgentMissionKind.Standing, now.AddHours(-4));

            // A delivered bounded task is genuinely done — its slot is the Commander's to reclaim.
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(finishedTask, false, now, Base, Max, 0));
            // A standing beat is never over just because one cycle reported complete.
            Assert.True(ProjectWorkerHeartbeat.ShouldWake(standing, false, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_SkipsAnAgentThatWasNeverGivenAnything()
        {
            var now = DateTime.UtcNow;
            var empty = Worker(ProjectAgentWorkStatus.Idle, objective: "");
            empty.LastWakeAt = now.AddHours(-4);
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(empty, false, now, Base, Max, 0));
        }

        [Fact]
        public void Heartbeat_UsesCreationTimeWhenAnAgentHasNeverWoken()
        {
            var now = DateTime.UtcNow;
            var agent = Worker(lastWakeAt: null);
            agent.CreatedAt = now.AddMinutes(-40);
            Assert.True(ProjectWorkerHeartbeat.ShouldWake(agent, false, now, Base, Max, 0));
        }

        [Fact]
        public void Backoff_DoublesPerUnproductiveWakeAndIsCapped()
        {
            Assert.Equal(TimeSpan.FromMinutes(20), ProjectWorkerHeartbeat.Interval(Base, Max, 0));
            Assert.Equal(TimeSpan.FromMinutes(40), ProjectWorkerHeartbeat.Interval(Base, Max, 1));
            Assert.Equal(TimeSpan.FromMinutes(80), ProjectWorkerHeartbeat.Interval(Base, Max, 2));
            Assert.Equal(TimeSpan.FromMinutes(160), ProjectWorkerHeartbeat.Interval(Base, Max, 3));
            // Capped, and a huge streak must not overflow the exponent into a negative interval.
            Assert.Equal(TimeSpan.FromMinutes(Max), ProjectWorkerHeartbeat.Interval(Base, Max, 4));
            Assert.Equal(TimeSpan.FromMinutes(Max), ProjectWorkerHeartbeat.Interval(Base, Max, 9999));
        }

        [Fact]
        public void Backoff_DelaysTheWakeOfAnIdlingAgent()
        {
            var now = DateTime.UtcNow;
            var agent = Worker(lastWakeAt: now.AddMinutes(-30));
            // Fresh streak: 30 minutes of quiet is past the 20-minute base.
            Assert.True(ProjectWorkerHeartbeat.ShouldWake(agent, false, now, Base, Max, 0));
            // After two unproductive wakes the bar is 80 minutes, so the same agent waits.
            Assert.False(ProjectWorkerHeartbeat.ShouldWake(agent, false, now, Base, Max, 2));
        }

        [Fact]
        public void HeartbeatTrigger_NamesTheMissionAndOffersTheCheapExit()
        {
            string trigger = ProjectWorkerHeartbeat.TriggerFor(Worker(objective: "run the posting queue"));
            Assert.Contains("run the posting queue", trigger);
            Assert.Contains("stimulus_hook", trigger);
            Assert.Contains("WORK_STATUS: CONTINUING", trigger);
            // The agent must not be pushed into manufacturing work to justify the wake.
            Assert.Contains("Do not invent filler work", trigger);
        }

        [Fact]
        public void ContinuingStatus_IsOfferedToWorkersAsTheNormalEnding()
        {
            var project = new Project { ProjectID = "p1", Name = "t", Goal = "g" };
            string standingPrompt = ProjectSubAgentRunner.BuildSystemPrompt(
                project, Worker(mission: ProjectAgentMissionKind.Standing), visionEnabled: false);
            string taskPrompt = ProjectSubAgentRunner.BuildSystemPrompt(
                project, Worker(mission: ProjectAgentMissionKind.Task), visionEnabled: false);

            Assert.Contains("WORK_STATUS: CONTINUING", standingPrompt);
            Assert.Contains("WORK_STATUS: CONTINUING", taskPrompt);
            Assert.Contains("MISSION IS STANDING", standingPrompt);
            Assert.Contains("MISSION IS A BOUNDED TASK", taskPrompt);
            // Reporting must read as a checkpoint, not as an exit.
            Assert.Contains("CHECKPOINT, not a resignation", standingPrompt);
        }

        [Fact]
        public void WorkerPrompt_TellsTheWorkerItHasPeersToTalkTo()
        {
            string prompt = ProjectSubAgentRunner.BuildSystemPrompt(
                new Project { ProjectID = "p1", Name = "t", Goal = "g" }, Worker(), visionEnabled: false);

            Assert.Contains("YOUR TEAM", prompt);
            Assert.Contains("peer who owns adjacent work", prompt);
            Assert.Contains("'team' reaches all", prompt);
        }

        [Fact]
        public void OpenAssignment_KeepsMomentumOnlyWhenTheSliceWasProductive()
        {
            // Reported a checkpoint, still owns the mission, did novel work → straight into a fresh
            // wake rather than sitting out the heartbeat's quiet period.
            Assert.True(ProjectWorkSliceBoundary.ShouldContinueOpenAssignment(true, true, 3));
            // Same, but the slice achieved nothing novel → fall through to the backing-off heartbeat.
            Assert.False(ProjectWorkSliceBoundary.ShouldContinueOpenAssignment(true, true, 0));
            // A closed assignment never self-renews, however productive it was.
            Assert.False(ProjectWorkSliceBoundary.ShouldContinueOpenAssignment(false, true, 9));
            // A wake that failed or was cancelled must not renew either.
            Assert.False(ProjectWorkSliceBoundary.ShouldContinueOpenAssignment(true, false, 9));
        }

        [Fact]
        public void HeartbeatDefaults_AreOnWithARevertSwitch()
        {
            var settings = new ProjectSettings();
            Assert.True(settings.WorkerHeartbeatEnabled);
            Assert.Equal(20, settings.WorkerHeartbeatMinutes);
            Assert.Equal(240, settings.WorkerHeartbeatMaxMinutes);
            Assert.Equal(6, settings.WorkerMessagesPerWake);

            // The kill switch is reachable through the same settings patch path as the rest.
            Assert.True(settings.TrySet("workerHeartbeatEnabled", "false"));
            Assert.False(settings.WorkerHeartbeatEnabled);
            Assert.True(settings.TrySet("workerHeartbeatMinutes", "45"));
            Assert.Equal(45, settings.WorkerHeartbeatMinutes);
        }
    }
}
