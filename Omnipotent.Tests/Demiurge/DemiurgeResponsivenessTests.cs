using Omnipotent.Services.Demiurge;
using DemiurgeService = Omnipotent.Services.Demiurge.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// Responsiveness: Klives gets an answer within seconds, a resumed Steward restarts ahead of the
    /// fleet, a productive Commander keeps going between wakes, and every wait is written down so a quiet
    /// timeline always says why it is quiet.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class DemiurgeResponsivenessTests
    {
        private static (DemiurgeService Svc, string StewardID) NewService()
        {
            var svc = new DemiurgeService();
            void Set(string name, object value) => typeof(DemiurgeService).GetProperty(name)!.SetValue(svc, value);
            Set(nameof(DemiurgeService.Store), new StewardStore(_ => { }));
            Set(nameof(DemiurgeService.EventLog), new StewardEventLogStore(_ => { }));
            Set(nameof(DemiurgeService.Digests), new StewardDigestStore(_ => { }));
            Set(nameof(DemiurgeService.RuntimeState), new StewardRuntimeStateStore(_ => { }));
            Set(nameof(DemiurgeService.Settings), new StewardSettingsStore());
            Set(nameof(DemiurgeService.Activity), new StewardAgentActivityTracker());
            Set(nameof(DemiurgeService.Gates), new StewardGateManager(svc.EventLog, _ => { }));
            Set(nameof(DemiurgeService.SubAgents), new StewardSubAgentManager(svc.Store, svc.EventLog));
            Set(nameof(DemiurgeService.WakeAdmission), new StewardWakeAdmission(() => 1));
            Set(nameof(DemiurgeService.CommanderRunner), new StewardCommanderRunner(svc));
            Set(nameof(DemiurgeService.SubAgentRunner), new StewardSubAgentRunner(svc));
            Set(nameof(DemiurgeService.FrontDesk), new StewardFrontDesk(svc));
            var steward = svc.Store.CreateSteward("Etsy storefront", "Run a profitable print-on-demand storefront", 100, 10, 5, 5);
            steward.Status = StewardStatus.Active;
            svc.Store.SaveSteward(steward);
            return (svc, steward.StewardID);
        }

        private static StewardDirective Directive(string stewardID, string text, StewardDirectiveKind kind = StewardDirectiveKind.Task) => new()
        {
            DirectiveID = Guid.NewGuid().ToString("N"),
            StewardID = stewardID,
            Text = text,
            Kind = kind,
        };

        // ── instant replies ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task InstantReply_IsPostedInTheCommandersVoice_AndHandedToTheCommander()
        {
            var (svc, pid) = NewService();
            svc.EventLog.Append(new StewardEvent { StewardID = pid, AgentID = "w1", Type = StewardEventTypes.ToolCall, Author = "w1", Text = "browser op=navigate url=https://www.etsy.com/your/shops/me" });
            var message = svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.KlivesMessage, Author = "klives", Text = "How many listings are live?" });
            string? seenUser = null;
            svc.FrontDesk.CompleteOverride = (system, user, _) =>
            {
                seenUser = user;
                Assert.Contains("Etsy storefront", system);
                return Task.FromResult<string?>("Commander: Two listings are live; I'm uploading photos for the other eight next.");
            };

            string? reply = await svc.FrontDesk.RespondAsync(svc.Store.GetSteward(pid)!, Directive(pid, "How many listings are live?"), message.Sequence);

            Assert.Equal("Two listings are live; I'm uploading photos for the other eight next.", reply);
            Assert.NotNull(seenUser);
            Assert.Contains("How many listings are live?", seenUser);
            Assert.Contains("w1 called browser op=navigate", seenUser);
            var posted = svc.EventLog.ReadTail(pid, 5).Last(e => e.Type == StewardEventTypes.CommanderMessage);
            Assert.Equal("commander", posted.Author);
            Assert.Contains("\"instant\":true", posted.PayloadJson);
        }

        [Fact]
        public async Task InstantReply_StaysSilent_WhenTheCommanderAnsweredFirst_OrItIsSwitchedOff()
        {
            var (svc, pid) = NewService();
            var message = svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.KlivesMessage, Author = "klives", Text = "status?" });
            svc.FrontDesk.CompleteOverride = async (_, _, _) =>
            {
                // The Commander's own reply lands while the instant one is being written.
                svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.CommanderMessage, Author = "commander", Text = "All good." });
                await Task.Yield();
                return "Working on it.";
            };
            Assert.Null(await svc.FrontDesk.RespondAsync(svc.Store.GetSteward(pid)!, Directive(pid, "status?"), message.Sequence));
            Assert.Single(svc.EventLog.ReadTail(pid, 10), e => e.Type == StewardEventTypes.CommanderMessage);

            var settings = svc.Settings.Get(pid);
            Assert.True(settings.TrySet("instantRepliesEnabled", Newtonsoft.Json.Linq.JToken.FromObject(false)));
            svc.Settings.Save(settings);
            svc.FrontDesk.CompleteOverride = (_, _, _) => throw new InvalidOperationException("must not be called when disabled");
            Assert.Null(await svc.FrontDesk.RespondAsync(svc.Store.GetSteward(pid)!, Directive(pid, "again?"), message.Sequence));
        }

        [Fact]
        public void InstantReplyPrompt_CarriesTheFactsAndForbidsInventedProgress()
        {
            var snapshot = new StewardFrontDeskSnapshot
            {
                StewardName = "Etsy storefront",
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
            string user = StewardFrontDesk.BuildUserPrompt(snapshot, "a task", "Switch to Gumroad.", new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));
            Assert.Contains("Active step: List the first ten products — next action: Upload the mug photos", user);
            Assert.Contains("Working right now: scraper is running computer_navigate (etsy.com)", user);
            Assert.Contains("1 approval(s) waiting on Klives", user);
            Assert.Contains("KLIVES' NEW MESSAGE (a task)", user);
            Assert.EndsWith("Write your reply to him now.", user);

            string system = StewardFrontDesk.BuildSystemPrompt("Etsy storefront");
            Assert.Contains("Never say something was done", system);
            Assert.Contains("at most 70 words", system);
        }

        [Theory]
        [InlineData("  \"Done — on it.\"  ", "Done — on it.")]
        [InlineData("<think>plan the answer</think>On it now.", "On it now.")]
        [InlineData("# Reply\nReply: Two listings are live.", "Two listings are live.")]
        [InlineData("   ", null)]
        public void InstantReplyText_IsCleanedIntoOneChatMessage(string raw, string? expected)
            => Assert.Equal(expected, StewardFrontDesk.Clean(raw));

        [Fact]
        public void InstantReplySnapshot_SkipsBookkeepingEvents()
        {
            Assert.Null(StewardFrontDesk.DescribeWork(new StewardEvent { Type = StewardEventTypes.WakeDiagnostic, Text = "x" }));
            Assert.Null(StewardFrontDesk.DescribeWork(new StewardEvent { Type = StewardEventTypes.WakeWaiting, Text = "x" }));
            Assert.Equal("w2 reported: found 3 suppliers",
                StewardFrontDesk.DescribeWork(new StewardEvent { Type = StewardEventTypes.AgentMessage, AgentID = "w2", Text = "found 3 suppliers" }));
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
            Assert.Equal(3, svc.EventLog.ReadTail(pid, 20).Count(e => e.Type == StewardEventTypes.WakeWaiting));

            svc.ClearWakeWaiting(pid, "commander");
            svc.NoteWakeWaiting(pid, "commander", "provider", "provider backoff again");
            Assert.Equal(4, svc.EventLog.ReadTail(pid, 20).Count(e => e.Type == StewardEventTypes.WakeWaiting));
        }

        [Fact]
        public void WaitingNotes_NeverReachTheAgentsContext()
        {
            var evt = new StewardEvent { Type = StewardEventTypes.WakeWaiting, Author = "system", Text = "The Commander is waiting for LLM capacity." };
            Assert.False(StewardPromptHygiene.IsAgentVisibleEvent(evt));
        }

        [Theory]
        [InlineData(10, "in a moment")]
        [InlineData(70, "in about a minute")]
        [InlineData(600, "in about 10 minutes")]
        [InlineData(4 * 3600, "in about 4 hours")]
        public void WaitingNotes_SayWhenInPlainWords(int seconds, string expected)
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected, StewardWakeWaiting.Until(now.AddSeconds(seconds), now));
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
            => Assert.Equal(TimeSpan.FromMinutes(minutes), StewardWakeRecovery.KeepaliveBackoff(failures));

        // ── resume goes first ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ResumedAgents_AreStartedAheadOfBackgroundWakes_WhenAPermitFrees()
        {
            var gate = new StewardWakeAdmission(() => 1);
            Assert.True(gate.TryAdmit("busy", "commander", "working", out _));
            Assert.False(gate.TryAdmit("other", "w1", "heartbeat", out _));
            Assert.False(gate.TryAdmit("resumed", "w9", "resume", out _, priority: 50));
            // A later background re-deferral must not demote the resume.
            Assert.False(gate.TryAdmit("resumed", "w9", "heartbeat", out _));

            var promoted = gate.Release("busy", "commander");
            Assert.Equal("resumed", Assert.Single(promoted).StewardID);
        }

        [Fact]
        public void AnUrgentWake_IsAdmittedPastAFullBudget()
        {
            var gate = new StewardWakeAdmission(() => 1);
            Assert.True(gate.TryAdmit("busy", "commander", "working", out _));
            Assert.True(gate.TryAdmit("resumed", "commander", "Steward resumed by Klives.", out _, mustAdmit: true));
        }

        // ── momentum ─────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true, StewardStatus.Active, true, false, 0, false, true)]
        [InlineData(false, StewardStatus.Active, true, false, 0, false, false)]   // switched off
        [InlineData(true, StewardStatus.Planning, true, false, 0, false, false)]  // planning waits on approval
        [InlineData(true, StewardStatus.Active, false, false, 0, false, false)]   // nothing open
        [InlineData(true, StewardStatus.Active, true, true, 0, false, false)]     // a worker is on it
        [InlineData(true, StewardStatus.Active, true, false, 1, false, false)]    // waiting on Klives
        [InlineData(true, StewardStatus.Active, true, false, 0, true, false)]     // armed a hook to wait
        public void Momentum_FollowsUpOnlyWhenOpenWorkHasNobodyOnIt(bool enabled, StewardStatus status, bool openSteps,
            bool workersAwake, int approvals, bool armedHook, bool expected)
            => Assert.Equal(expected, StewardMomentum.ShouldContinue(enabled, status, openSteps, workersAwake, approvals, armedHook));

        [Fact]
        public void MomentumTrigger_LandsInsideThePrefixLifetime_AndCoalesces()
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            var trigger = StewardMomentum.CreateTrigger(now);
            Assert.Equal(StewardWakeTriggerKind.Continuation, trigger.Kind);
            Assert.True(trigger.NotBefore > now && trigger.NotBefore <= now.AddMinutes(1));
            Assert.Equal("momentum", trigger.CoalescingKey);
            Assert.StartsWith(StewardMomentum.TriggerPrefix, trigger.Payload);

            var state = new StewardRuntimeState();
            Assert.Equal(StewardWakeTriggerApplicability.Deferred, StewardRuntimeStateStore.EvaluateApplicability(trigger, state, now));
            Assert.Equal(StewardWakeTriggerApplicability.Applicable, StewardRuntimeStateStore.EvaluateApplicability(trigger, state, now.AddMinutes(1)));
            Assert.Equal(StewardWakeTriggerApplicability.Stale, StewardRuntimeStateStore.EvaluateApplicability(trigger, state, now.AddMinutes(11)));
        }

        [Fact]
        public void MomentumTriggers_ReplaceEachOther_InsteadOfPilingUp()
        {
            var (svc, pid) = NewService();
            var now = DateTime.UtcNow;
            svc.RuntimeState.EnqueueTrigger(pid, StewardMomentum.CreateTrigger(now));
            svc.RuntimeState.EnqueueTrigger(pid, StewardMomentum.CreateTrigger(now.AddSeconds(30)));
            Assert.Single(svc.RuntimeState.ListPendingTriggers(pid), t => t.CoalescingKey == "momentum");
        }

        // ── the schedule explains quiet Stewards ─────────────────────────────────────────────────────

        [Fact]
        public void Schedule_SaysWhetherTheCommanderIsWorkingWaitingOrAsleep_AndWhenItNextRuns()
        {
            var (svc, pid) = NewService();
            var now = DateTime.UtcNow;
            svc.EventLog.Append(new StewardEvent { StewardID = pid, AgentID = "commander", Type = StewardEventTypes.CommanderWake, Author = "system", Text = "woke", Timestamp = now.AddMinutes(-4) });

            var asleep = StewardSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Equal("sleeping", asleep.State);
            Assert.NotNull(asleep.NextWakeAt);
            Assert.InRange(asleep.NextWakeAt!.Value, now.AddMinutes(9), now.AddMinutes(11));

            svc.RuntimeState.EnqueueTrigger(pid, StewardMomentum.CreateTrigger(now));
            var momentum = StewardSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Contains("continues the open work", momentum.Detail);
            Assert.True(momentum.NextWakeAt <= now.AddMinutes(1));

            // Another Steward holds the only permit: this Commander is queued, and says so.
            Assert.True(svc.WakeAdmission.TryAdmit("someone-else", "commander", "busy", out _));
            Assert.False(svc.WakeAdmission.TryAdmit(pid, "commander", "keepalive", out string reason));
            svc.NoteWakeWaiting(pid, "commander", "capacity", $"The Commander is waiting for LLM capacity ({reason}).");
            var waiting = StewardSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander");
            Assert.Equal("waiting", waiting.State);
            Assert.Contains("waiting for LLM capacity", waiting.Detail);

            Assert.True(svc.RuntimeState.TryAcquireWakeLease(pid, "live").Acquired);
            Assert.Equal("working", StewardSchedule.Describe(svc, pid, now).Agents.Single(a => a.AgentID == "commander").State);
        }

        // ── digest writes merge ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABackgroundDigestRebuild_NeverErasesTheNextWakesMarker()
        {
            var (svc, pid) = NewService();
            var steward = svc.Store.GetSteward(pid)!;
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.ToolCall, Author = "commander", Text = "did work" });
            var modelStarted = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var rebuild = svc.Digests.RebuildDigestAsync(steward, svc.EventLog, async _ =>
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
