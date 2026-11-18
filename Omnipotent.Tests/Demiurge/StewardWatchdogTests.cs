using Omnipotent.Services.Demiurge;
using DemiurgeService = Omnipotent.Services.Demiurge.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// The watchdog's Diagnose() is pure over the event log + digest, so it's tested directly by
    /// seeding a Steward's log. Escalation/force-wake I/O (Discord, LLM) is covered by the
    /// build + the ForceWake unit below; the timing loop itself isn't unit-tested.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class StewardWatchdogTests
    {
        private static (DemiurgeService svc, string pid) NewStewardService()
        {
            // A Demiurge service far enough constructed for the watchdog's read-only diagnosis:
            // the watchdog only touches Store, EventLog and Digests.
            var svc = new DemiurgeService();
            // ServiceMain isn't run in tests (it needs the whole service manager); build the
            // subsystems the watchdog reads, mirroring ServiceMain's first lines.
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Store))!.SetValue(svc, new StewardStore(_ => { }));
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.EventLog))!.SetValue(svc, new StewardEventLogStore(_ => { }));
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Digests))!.SetValue(svc, new StewardDigestStore(_ => { }));
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.RuntimeState))!.SetValue(svc, new StewardRuntimeStateStore(_ => { }));
            var p = svc.Store.CreateSteward("t", "goal", 100, 100, 10, 5);
            return (svc, p.StewardID);
        }

        private static void Wake(DemiurgeService svc, string pid, DateTime when)
        {
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.CommanderWake, Author = "system", Text = "wake", Timestamp = when });
        }

        [Fact]
        public void FreshSteward_WithNoHistory_IsNotStalled()
        {
            var (svc, pid) = NewStewardService();
            var wd = new StewardWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void FreshSteward_WithInitEventButNoWakeYet_IsNotStalled()
        {
            // The create route logs a "Steward initialised" event immediately, so the tail is
            // non-empty before the first wake — that must not read as "stalled (last: never)".
            var (svc, pid) = NewStewardService();
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Author = "klives", Text = "Steward initialised." });
            var wd = new StewardWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void NeverWokenSteward_OlderThanWakeGap_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewStewardService();
            var p = svc.Store.GetSteward(pid)!;
            p.CreatedAt = DateTime.UtcNow.AddHours(-1);
            svc.Store.SaveSteward(p);
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.Status, Author = "klives", Text = "Steward initialised." });
            var wd = new StewardWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("never", diag);
        }

        [Fact]
        public void RecentWakeWithProgress_IsHealthy()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-2));
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.ToolCall, Author = "commander", Text = "did work" });
            var wd = new StewardWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void StaleHeartbeat_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2)); // last wake long ago
            var wd = new StewardWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("No Commander activity", diag);
        }

        [Fact]
        public void FutureResumeNotBefore_SuppressesWatchdogStallDiagnosis()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.RuntimeState.SetResumeAction(pid, new StewardResumeAction
            {
                Kind = "resume",
                Summary = "Check the external result after the agreed waiting period.",
                NotBefore = DateTime.UtcNow.AddHours(4),
                RecordedBy = "commander"
            });

            var wd = new StewardWatchdog(svc, _ => { });

            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void LegacyLoopRecoveryGetsAnImplicitCooldownInsteadOfAWatchdogRestart()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-1));
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.ToolCall, Author = "commander", Text = "x" });
            svc.RuntimeState.SetResumeAction(pid, new StewardResumeAction
            {
                Kind = "loop-recovery",
                Summary = "Change strategy after the convergence guard stopped the wake.",
                RecordedAt = DateTime.UtcNow,
                RecordedBy = "commander",
            });
            var digest = svc.Digests.GetDigest(pid);
            digest.RecentStuckLoopTrips = 5;
            svc.Digests.SaveDigest(digest);

            var wd = new StewardWatchdog(svc, _ => { });

            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void ExpiredResumeNotBefore_DoesNotSuppressRealStall()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.RuntimeState.SetResumeAction(pid, new StewardResumeAction
            {
                Kind = "resume",
                Summary = "Check the result.",
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                RecordedBy = "commander"
            });

            var wd = new StewardWatchdog(svc, _ => { });

            Assert.NotNull(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void LongRunningWake_StillEmittingToolCalls_IsHealthy()
        {
            // One wake started hours ago but the Commander is visibly working (tool calls flowing).
            // The old heartbeat only counted wake STARTS, so a long wake (or one blocked briefly)
            // read as "stalled" while mid-work — the exact false-positive Klives kept getting pinged for.
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.EventLog.Append(new StewardEvent { StewardID = pid, AgentID = "commander", Type = StewardEventTypes.ToolCall, Author = "commander", Text = "still working", Timestamp = DateTime.UtcNow.AddMinutes(-1) });
            var wd = new StewardWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public async Task PendingGate_SuppressesAllStallDiagnosis()
        {
            // A wake blocked on an unanswered approval gate is waiting on KLIVES — diagnosing it
            // as a heartbeat stall (and force-waking, which cancels the gate wait) is wrong.
            var (svc, pid) = NewStewardService();
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Gates))!
                .SetValue(svc, new StewardGateManager(svc.EventLog, _ => { }));
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2)); // stale by the heartbeat rule
            _ = svc.Gates.OpenGateAndWaitAsync(
                new StewardGate { StewardID = pid, Kind = "money", Title = "Spend $50" }, CancellationToken.None);
            await Task.Delay(20);
            var wd = new StewardWatchdog(svc, _ => { });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void WedgedSubAgent_IsDiagnosed_OnlyWhenSilent()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-2)); // commander itself is healthy
            string agentWakeID = Guid.NewGuid().ToString("N");
            svc.EventLog.Append(new StewardEvent { StewardID = pid, WakeID = agentWakeID, AgentID = "agent-1", Type = StewardEventTypes.AgentWake, Author = "system", Text = "woke", Timestamp = DateTime.UtcNow.AddMinutes(-45) });
            var wd = new StewardWatchdog(svc, _ => { });

            // Silent for 45 minutes with no completion → wedged.
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("agent-1", diag);

            // But a slow worker that is still emitting activity is left alone.
            svc.EventLog.Append(new StewardEvent { StewardID = pid, WakeID = agentWakeID, AgentID = "agent-1", Type = StewardEventTypes.ToolCall, Author = "agent", Text = "slow but alive", Timestamp = DateTime.UtcNow.AddMinutes(-5) });
            Assert.Null(wd.Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void ThreeWakesNoProgress_IsDiagnosedAsStall()
        {
            var (svc, pid) = NewStewardService();
            // Three recent wakes, no tool-call/artifact/spawn between them.
            for (int i = 0; i < 3; i++)
                Wake(svc, pid, DateTime.UtcNow.AddMinutes(-3 + i));
            var wd = new StewardWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("No verified progress", diag);
        }

        [Fact]
        public async Task PlanningSteward_BlockedOnPlanGate_IsNotDiagnosedAsNoProgress()
        {
            // A Steward in PLANNING that has submitted its Grand Plan and is waiting on Klives shows
            // no tool calls across recent wakes — but that's "waiting on Klives", not a stall, so the
            // watchdog must not force-wake it (which would cancel the plan gate).
            var (svc, pid) = NewStewardService();
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Gates))!
                .SetValue(svc, new StewardGateManager(svc.EventLog, _ => { }));
            var p = svc.Store.GetSteward(pid)!;
            p.Status = StewardStatus.Planning;
            svc.Store.SaveSteward(p);
            for (int i = 0; i < 3; i++) Wake(svc, pid, DateTime.UtcNow.AddMinutes(-3 + i));

            // Open a pending "plan" gate (never resolved) — the Commander is blocked here.
            _ = svc.Gates.OpenGateAndWaitAsync(
                new StewardGate { StewardID = pid, Kind = "plan", Title = "Grand Plan v1" }, CancellationToken.None);
            await Task.Delay(20);

            var wd = new StewardWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            if (diag != null) Assert.DoesNotContain("No verified progress", diag);
        }

        [Fact]
        public async Task ResolvedApprovalRestartsTheSilenceWindowForItsWaitingWorkerAndCommander()
        {
            var (svc, pid) = NewStewardService();
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Gates))!
                .SetValue(svc, new StewardGateManager(svc.EventLog, _ => { }));
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.RuntimeState.TryAcquireAgentWakeLease(pid, "worker", "worker-wake", nowUtc: DateTime.UtcNow.AddHours(-2));
            var gate = new StewardGate { StewardID = pid, AgentID = "worker", WakeID = "worker-wake", Title = "Approve work" };
            var waiting = svc.Gates.OpenGateAndWaitAsync(gate, CancellationToken.None);
            Assert.True(svc.Gates.ResolveGate(pid, gate.GateID, new(GateDecision.Approve, "", "test")));
            await waiting;
            Assert.Empty(svc.Gates.ListPending(pid));
            Assert.Null(new StewardWatchdog(svc, _ => { }).Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void OldWorkerWakeDoesNotOverrideAHealthyReplacementLease()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.EventLog.Append(new StewardEvent { StewardID = pid, AgentID = "worker", WakeID = "old",
                Type = StewardEventTypes.AgentWake, Timestamp = DateTime.UtcNow.AddHours(-2) });
            svc.RuntimeState.TryAcquireAgentWakeLease(pid, "worker", "replacement");
            Assert.Null(new StewardWatchdog(svc, _ => { }).Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public void StreamingWorkerIsAliveBeforeItsModelResponseIsCommitted()
        {
            var (svc, pid) = NewStewardService();
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.Activity))!
                .SetValue(svc, new StewardAgentActivityTracker());
            Wake(svc, pid, DateTime.UtcNow.AddHours(-2));
            svc.RuntimeState.TryAcquireAgentWakeLease(pid, "worker", "w", nowUtc: DateTime.UtcNow.AddHours(-2));
            svc.Activity.BeginThinking(pid, "worker", "worker", "model");
            svc.Activity.AppendToken(pid, "worker", "still generating");
            Assert.Null(new StewardWatchdog(svc, _ => { }).Diagnose(svc.Store.GetSteward(pid)!));
        }

        [Fact]
        public async Task WorkerRecoveryDoesNotRequestCommanderCancellation()
        {
            var (svc, pid) = NewStewardService();
            var steward = svc.Store.GetSteward(pid)!;
            steward.Status = StewardStatus.Active;
            svc.Store.SaveSteward(steward);
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.CommanderRunner))!
                .SetValue(svc, new StewardCommanderRunner(svc));
            typeof(DemiurgeService).GetProperty(nameof(DemiurgeService.SubAgentRunner))!
                .SetValue(svc, new StewardSubAgentRunner(svc));
            svc.RuntimeState.TryAcquireWakeLease(pid, "commander-live");
            var digest = svc.Digests.GetDigest(pid);
            digest.ActiveWakeID = "commander-live";
            svc.Digests.SaveDigest(digest);
            svc.RuntimeState.TryAcquireAgentWakeLease(pid, "worker", "stale", nowUtc: DateTime.UtcNow.AddHours(-2));
            var watchdog = new StewardWatchdog(svc, _ => { });
            await watchdog.SelfHealAsync(steward, "stale worker", "worker");
            var lease = svc.RuntimeState.Get(pid).ActiveWakeLease!;
            Assert.Equal("commander-live", lease.WakeID);
            Assert.Null(lease.CancellationRequestedAt);
            Assert.Empty(svc.RuntimeState.ListPendingTriggers(pid));
            Assert.Single(svc.EventLog.ReadTail(pid, 10), e => e.Type == StewardEventTypes.WatchdogRecovery);
        }

        [Fact]
        public void RestartRecoveryPreservesTheReplacementWakeMarker()
        {
            var (svc, pid) = NewStewardService();
            svc.RuntimeState.TryAcquireWakeLease(pid, "interrupted");
            var digest = svc.Digests.GetDigest(pid);
            digest.ActiveWakeID = "interrupted";
            svc.Digests.SaveDigest(digest);
            int resumed = 0;
            new StewardCommanderRunner(svc).RecoverInterruptedWakes((steward, _) =>
            {
                if (steward.StewardID != pid) return;
                resumed++;
                Assert.True(svc.RuntimeState.TryAcquireWakeLease(pid, "replacement").Acquired);
                var replacement = svc.Digests.GetDigest(pid);
                replacement.ActiveWakeID = "replacement";
                svc.Digests.SaveDigest(replacement);
            });
            Assert.Equal(1, resumed);
            Assert.Equal("replacement", svc.Digests.GetDigest(pid).ActiveWakeID);
            Assert.Equal("replacement", svc.RuntimeState.Get(pid).ActiveWakeLease!.WakeID);
        }

        [Fact]
        public void StuckLoopTrips_AreDiagnosedAsStall()
        {
            var (svc, pid) = NewStewardService();
            Wake(svc, pid, DateTime.UtcNow.AddMinutes(-1));
            svc.EventLog.Append(new StewardEvent { StewardID = pid, Type = StewardEventTypes.ToolCall, Author = "commander", Text = "x" });
            var digest = svc.Digests.GetDigest(pid);
            digest.RecentStuckLoopTrips = 4;
            svc.Digests.SaveDigest(digest);
            var wd = new StewardWatchdog(svc, _ => { });
            var diag = wd.Diagnose(svc.Store.GetSteward(pid)!);
            Assert.NotNull(diag);
            Assert.Contains("stuck-loop", diag);
        }
    }
}
