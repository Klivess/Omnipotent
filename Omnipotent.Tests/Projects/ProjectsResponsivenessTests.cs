using Omnipotent.Services.Projects;
using ProjectsService = Omnipotent.Services.Projects.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Responsiveness: Klives gets an answer within seconds, a resumed project restarts ahead of the
    /// fleet, a productive Commander keeps going between wakes, and every wait is written down so a quiet
    /// timeline always says why it is quiet.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectsResponsivenessTests
    {
        private static (ProjectsService Svc, string ProjectID) NewService()
        {
            var svc = new ProjectsService();
            void Set(string name, object value) => typeof(ProjectsService).GetProperty(name)!.SetValue(svc, value);
            Set(nameof(ProjectsService.Store), new ProjectStore(_ => { }));
            Set(nameof(ProjectsService.EventLog), new ProjectEventLogStore(_ => { }));
            Set(nameof(ProjectsService.Digests), new ProjectDigestStore(_ => { }));
            Set(nameof(ProjectsService.RuntimeState), new ProjectRuntimeStateStore(_ => { }));
            Set(nameof(ProjectsService.Settings), new ProjectSettingsStore());
            Set(nameof(ProjectsService.Activity), new ProjectAgentActivityTracker());
            Set(nameof(ProjectsService.Gates), new ProjectGateManager(svc.EventLog, _ => { }));
            Set(nameof(ProjectsService.SubAgents), new ProjectSubAgentManager(svc.Store, svc.EventLog));
            Set(nameof(ProjectsService.WakeAdmission), new ProjectWakeAdmission(() => 1));
            Set(nameof(ProjectsService.CommanderRunner), new ProjectCommanderRunner(svc));
            Set(nameof(ProjectsService.SubAgentRunner), new ProjectSubAgentRunner(svc));
            Set(nameof(ProjectsService.FrontDesk), new ProjectFrontDesk(svc));
            var project = svc.Store.CreateProject("Etsy storefront", "Run a profitable print-on-demand storefront", 100, 10, 5, 5);
            project.Status = ProjectStatus.Active;
            svc.Store.SaveProject(project);
            return (svc, project.ProjectID);
        }

        private static ProjectDirective Directive(string projectID, string text, ProjectDirectiveKind kind = ProjectDirectiveKind.Task) => new()
        {
            DirectiveID = Guid.NewGuid().ToString("N"),
            ProjectID = projectID,
            Text = text,
            Kind = kind,
        };

        // ── instant replies ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task InstantReply_IsPostedInTheCommandersVoice_AndHandedToTheCommander()
        {
            var (svc, pid) = NewService();
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, AgentID = "w1", Type = ProjectEventTypes.ToolCall, Author = "w1", Text = "browser op=navigate url=https://www.etsy.com/your/shops/me" });
            var message = svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.KlivesMessage, Author = "klives", Text = "How many listings are live?" });
            string? seenUser = null;
            svc.FrontDesk.CompleteOverride = (system, user, _) =>
            {
                seenUser = user;
                Assert.Contains("Etsy storefront", system);
                return Task.FromResult<string?>("Commander: Two listings are live; I'm uploading photos for the other eight next.");
            };

            string? reply = await svc.FrontDesk.RespondAsync(svc.Store.GetProject(pid)!, Directive(pid, "How many listings are live?"), message.Sequence);

            Assert.Equal("Two listings are live; I'm uploading photos for the other eight next.", reply);
            Assert.NotNull(seenUser);
            Assert.Contains("How many listings are live?", seenUser);
            Assert.Contains("w1 called browser op=navigate", seenUser);
            var posted = svc.EventLog.ReadTail(pid, 5).Last(e => e.Type == ProjectEventTypes.CommanderMessage);
            Assert.Equal("commander", posted.Author);
            Assert.Contains("\"instant\":true", posted.PayloadJson);
        }

        [Fact]
        public async Task InstantReply_StaysSilent_WhenTheCommanderAnsweredFirst_OrItIsSwitchedOff()
        {
            var (svc, pid) = NewService();
            var message = svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.KlivesMessage, Author = "klives", Text = "status?" });
            svc.FrontDesk.CompleteOverride = async (_, _, _) =>
            {
                // The Commander's own reply lands while the instant one is being written.
                svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderMessage, Author = "commander", Text = "All good." });
                await Task.Yield();
                return "Working on it.";
            };
            Assert.Null(await svc.FrontDesk.RespondAsync(svc.Store.GetProject(pid)!, Directive(pid, "status?"), message.Sequence));
            Assert.Single(svc.EventLog.ReadTail(pid, 10), e => e.Type == ProjectEventTypes.CommanderMessage);

            var settings = svc.Settings.Get(pid);
            Assert.True(settings.TrySet("instantRepliesEnabled", Newtonsoft.Json.Linq.JToken.FromObject(false)));
            svc.Settings.Save(settings);
            svc.FrontDesk.CompleteOverride = (_, _, _) => throw new InvalidOperationException("must not be called when disabled");
            Assert.Null(await svc.FrontDesk.RespondAsync(svc.Store.GetProject(pid)!, Directive(pid, "again?"), message.Sequence));
        }

        [Fact]
        public void InstantReplyPrompt_CarriesTheFactsAndForbidsInventedProgress()
        {
            var snapshot = new ProjectFrontDeskSnapshot
            {
                ProjectName = "Etsy storefront",
                Goal = "Run a profitable storefront",
                Status = "Active",
                CurrentFocus = "Get ten listings live",
                ActiveStep = "List the first ten products",
                ActiveStepNext = "Upload the mug photos",
                LiveActivity = { "scraper is running computer_navigate (etsy.com)" },
                RecentWork = { "Commander called step_ledger op=start" },
                RecentChat = { "Klives: hi", "You: Hello." },
                PendingApprovals = 1,
            };
            string user = ProjectFrontDesk.BuildUserPrompt(snapshot, "a task", "Switch to Gumroad.", new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));
            Assert.Contains("Active step: List the first ten products — next action: Upload the mug photos", user);
            Assert.Contains("Working right now: scraper is running computer_navigate (etsy.com)", user);
            Assert.Contains("1 approval(s) waiting on Klives", user);
            Assert.Contains("KLIVES' NEW MESSAGE (a task)", user);
            Assert.EndsWith("Write your reply to him now.", user);

            string system = ProjectFrontDesk.BuildSystemPrompt("Etsy storefront");
            Assert.Contains("Never say something was done", system);
            Assert.Contains("at most 70 words", system);
        }

        [Theory]
        [InlineData("  \"Done — on it.\"  ", "Done — on it.")]
        [InlineData("<think>plan the answer</think>On it now.", "On it now.")]
        [InlineData("# Reply\nReply: Two listings are live.", "Two listings are live.")]
        [InlineData("   ", null)]
        public void InstantReplyText_IsCleanedIntoOneChatMessage(string raw, string? expected)
            => Assert.Equal(expected, ProjectFrontDesk.Clean(raw));

        [Fact]
        public void InstantReplySnapshot_SkipsBookkeepingEvents()
        {
            Assert.Null(ProjectFrontDesk.DescribeWork(new ProjectEvent { Type = ProjectEventTypes.WakeDiagnostic, Text = "x" }));
            Assert.Null(ProjectFrontDesk.DescribeWork(new ProjectEvent { Type = ProjectEventTypes.WakeWaiting, Text = "x" }));
            Assert.Equal("w2 reported: found 3 suppliers",
                ProjectFrontDesk.DescribeWork(new ProjectEvent { Type = ProjectEventTypes.AgentMessage, AgentID = "w2", Text = "found 3 suppliers" }));
        }

        // ── waiting is visible ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void WaitingNotes_AreWrittenOncePerReason_AndAgainWhenTheReasonChanges()
        {
            var (svc, pid) = NewService();
            svc.NoteWakeWaiting(pid, "commander", "capacity", "waiting for capacity");
            svc.NoteWakeWaiting(pid, "commander", "capacity", "waiting for capacity");
            svc.NoteWakeWaiting(pid, "commander", "provider", "provider backoff");
            svc.NoteWakeWaiting(pid, "w1", "capacity", "worker waiting");
            Assert.Equal(3, svc.EventLog.ReadTail(pid, 20).Count(e => e.Type == ProjectEventTypes.WakeWaiting));

            svc.ClearWakeWaiting(pid, "commander");
            svc.NoteWakeWaiting(pid, "commander", "provider", "provider backoff again");
            Assert.Equal(4, svc.EventLog.ReadTail(pid, 20).Count(e => e.Type == ProjectEventTypes.WakeWaiting));
        }

        [Fact]
        public void WaitingNotes_NeverReachTheAgentsContext()
        {
            var evt = new ProjectEvent { Type = ProjectEventTypes.WakeWaiting, Author = "system", Text = "The Commander is waiting for LLM capacity." };
            Assert.False(ProjectPromptHygiene.IsAgentVisibleEvent(evt));
        }

        [Theory]
        [InlineData(10, "in a moment")]
        [InlineData(70, "in about a minute")]
        [InlineData(600, "in about 10 minutes")]
        [InlineData(4 * 3600, "in about 4 hours")]
        public void WaitingNotes_SayWhenInPlainWords(int seconds, string expected)
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected, ProjectWakeWaiting.Until(now.AddSeconds(seconds), now));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(2, 5)]
        [InlineData(3, 10)]
        [InlineData(4, 20)]
        [InlineData(7, 120)]
        [InlineData(12, 120)]
        public void FailureBackoff_StartsAtMinutesNotHalfAnHour(int failures, int minutes)
            => Assert.Equal(TimeSpan.FromMinutes(minutes), ProjectWakeRecovery.KeepaliveBackoff(failures));

        // ── resume goes first ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ResumedAgents_AreStartedAheadOfBackgroundWakes_WhenAPermitFrees()
        {
            var gate = new ProjectWakeAdmission(() => 1);
            Assert.True(gate.TryAdmit("busy", "commander", "working", out _));
            Assert.False(gate.TryAdmit("other", "w1", "heartbeat", out _));
            Assert.False(gate.TryAdmit("resumed", "w9", "resume", out _, priority: 50));
            // A later background re-deferral must not demote the resume.
            Assert.False(gate.TryAdmit("resumed", "w9", "heartbeat", out _));

            var promoted = gate.Release("busy", "commander");
            Assert.Equal("resumed", Assert.Single(promoted).ProjectID);
        }

        [Fact]
        public void AnUrgentWake_IsAdmittedPastAFullBudget()
        {
            var gate = new ProjectWakeAdmission(() => 1);
            Assert.True(gate.TryAdmit("busy", "commander", "working", out _));
            Assert.True(gate.TryAdmit("resumed", "commander", "Project resumed by Klives.", out _, mustAdmit: true));
        }

        // ── momentum ─────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true, ProjectStatus.Active, true, false, 0, false, true)]
        [InlineData(false, ProjectStatus.Active, true, false, 0, false, false)]   // switched off
        [InlineData(true, ProjectStatus.Planning, true, false, 0, false, false)]  // planning waits on approval
        [InlineData(true, ProjectStatus.Active, false, false, 0, false, false)]   // nothing open
        [InlineData(true, ProjectStatus.Active, true, true, 0, false, false)]     // a worker is on it
        [InlineData(true, ProjectStatus.Active, true, false, 1, false, false)]    // waiting on Klives
        [InlineData(true, ProjectStatus.Active, true, false, 0, true, false)]     // armed a hook to wait
        public void Momentum_FollowsUpOnlyWhenOpenWorkHasNobodyOnIt(bool enabled, ProjectStatus status, bool openSteps,
            bool workersAwake, int approvals, bool armedHook, bool expected)
            => Assert.Equal(expected, ProjectMomentum.ShouldContinue(enabled, status, openSteps, workersAwake, approvals, armedHook));

        [Fact]
        public void MomentumTrigger_LandsInsideThePrefixLifetime_AndCoalesces()
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            var trigger = ProjectMomentum.CreateTrigger(now);
            Assert.Equal(ProjectWakeTriggerKind.Continuation, trigger.Kind);
            Assert.True(trigger.NotBefore > now && trigger.NotBefore <= now.AddMinutes(1));
            Assert.Equal("momentum", trigger.CoalescingKey);
            Assert.StartsWith(ProjectMomentum.TriggerPrefix, trigger.Payload);

            var state = new ProjectRuntimeState();
            Assert.Equal(ProjectWakeTriggerApplicability.Deferred, ProjectRuntimeStateStore.EvaluateApplicability(trigger, state, now));
            Assert.Equal(ProjectWakeTriggerApplicability.Applicable, ProjectRuntimeStateStore.EvaluateApplicability(trigger, state, now.AddMinutes(1)));
            Assert.Equal(ProjectWakeTriggerApplicability.Stale, ProjectRuntimeStateStore.EvaluateApplicability(trigger, state, now.AddMinutes(11)));
        }

        [Fact]
        public void MomentumTriggers_ReplaceEachOther_InsteadOfPilingUp()
        {
            var (svc, pid) = NewService();
            var now = DateTime.UtcNow;
            svc.RuntimeState.EnqueueTrigger(pid, ProjectMomentum.CreateTrigger(now));
            svc.RuntimeState.EnqueueTrigger(pid, ProjectMomentum.CreateTrigger(now.AddSeconds(30)));
            Assert.Single(svc.RuntimeState.ListPendingTriggers(pid), t => t.CoalescingKey == "momentum");
        }

        // ── the schedule explains quiet projects ─────────────────────────────────────────────────────

        [Fact]
        public void Schedule_SaysWhetherTheCommanderIsWorkingWaitingOrAsleep_AndWhenItNextRuns()
        {
            var (svc, pid) = NewService();
            var now = DateTime.UtcNow;
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, AgentID = "commander", Type = ProjectEventTypes.CommanderWake, Author = "system", Text = "woke", Timestamp = now.AddMinutes(-4) });

            var asleep = ProjectSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Equal("sleeping", asleep.State);
            Assert.NotNull(asleep.NextWakeAt);
            Assert.InRange(asleep.NextWakeAt!.Value, now.AddMinutes(9), now.AddMinutes(11));

            svc.RuntimeState.EnqueueTrigger(pid, ProjectMomentum.CreateTrigger(now));
            var momentum = ProjectSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Contains("continues the open work", momentum.Detail);
            Assert.True(momentum.NextWakeAt <= now.AddMinutes(1));

            // Another project holds the only permit: this Commander is queued, and says so.
            Assert.True(svc.WakeAdmission.TryAdmit("someone-else", "commander", "busy", out _));
            Assert.False(svc.WakeAdmission.TryAdmit(pid, "commander", "keepalive", out string reason));
            svc.NoteWakeWaiting(pid, "commander", "capacity", $"The Commander is waiting for LLM capacity ({reason}).");
            var waiting = ProjectSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Equal("waiting", waiting.State);
            Assert.Contains("waiting for LLM capacity", waiting.Detail);

            Assert.True(svc.RuntimeState.TryAcquireWakeLease(pid, "live").Acquired);
            Assert.Equal("working", ProjectSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander").State);
        }

        // ── digest writes merge ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABackgroundDigestRebuild_NeverErasesTheNextWakesMarker()
        {
            var (svc, pid) = NewService();
            var project = svc.Store.GetProject(pid)!;
            svc.EventLog.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.ToolCall, Author = "commander", Text = "did work" });
            var modelStarted = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var rebuild = svc.Digests.RebuildDigestAsync(project, svc.EventLog, async _ =>
            {
                modelStarted.SetResult();
                await release.Task;
                return "{\"currentFocus\":\"Ship listings\",\"rollingSummary\":\"Did work.\"}";
            });
            await modelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The next wake starts while the utility model is still writing.
            svc.Digests.Update(pid, d => d.ActiveWakeID = "next-wake");
            release.SetResult();
            await rebuild.WaitAsync(TimeSpan.FromSeconds(5));

            var digest = svc.Digests.GetDigest(pid);
            Assert.Equal("next-wake", digest.ActiveWakeID);
            Assert.True(digest.LastDigestedSequence > 0);
        }

        [Fact]
        public void DigestUpdate_IsAReadModifyWriteOfTheLatestCopy()
        {
            var (svc, pid) = NewService();
            svc.Digests.Update(pid, d => d.CurrentFocus = "focus");
            svc.Digests.Update(pid, d => d.ActiveWakeID = "w");
            var digest = svc.Digests.GetDigest(pid);
            Assert.Equal("focus", digest.CurrentFocus);
            Assert.Equal("w", digest.ActiveWakeID);
        }
    }
}
